using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-13 (сервер apl1311+sn1311, «Сервер 13.11 — серийные номера, гарантия, Электроника»): у карточки товара появились поля
/// <c>track_serials</c> (bool — «продаётся с серийными номерами») и <c>warranty_months</c> (число или null — гарантия в месяцах).
/// Колонок в pos_local.db для них нет (схему не меняем), поэтому касса запоминает их по id товара в файле <c>product-serials.json</c>
/// (как «Акции» — <see cref="PromotionRules"/>): <see cref="Remember"/> вызывается разбором карточки (<see cref="ProductCatalogMapper.TryTile(System.Text.Json.JsonElement, string)"/>),
/// файл пишется после загрузки каталога (<see cref="SaveIfDirty"/>). Старый сервер полей не присылает — тогда ничего не запоминается,
/// и касса решает по категории (<see cref="ElectronicsProfile.NeedsSerial"/>), как раньше.
/// </summary>
public static class ProductSerialRules
{
    /// <summary>Что сервер сказал про товар: <c>Track</c> — track_serials; <c>Months</c> — warranty_months (0 — у товара гарантии нет, null — не сказано).</summary>
    public sealed class Info
    {
        public bool? Track { get; set; }
        public int? Months { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Info> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileGate = new();
    private static volatile bool _loaded;
    private static volatile bool _dirty;

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "product-serials.json");

    /// <summary>Запомнить поля товара из ответа API (products/list, products/changes, карточка). Нет ни одного поля — ничего не меняется.</summary>
    public static void Remember(string? productId, JsonElement product)
    {
        if (string.IsNullOrWhiteSpace(productId) || product.ValueKind != JsonValueKind.Object)
            return;
        var hasTrack = product.TryGetProperty("track_serials", out var trackEl);
        var hasMonths = product.TryGetProperty("warranty_months", out var monthsEl);
        if (!hasTrack && !hasMonths)
            return;

        EnsureLoaded();
        var id = productId.Trim();
        var info = Known.TryGetValue(id, out var old) ? new Info { Track = old.Track, Months = old.Months } : new Info();
        if (hasTrack && ReadBool(trackEl) is { } track)
            info.Track = track;
        if (hasMonths)
            info.Months = ReadMonths(monthsEl) ?? info.Months;
        if (old is not null && old.Track == info.Track && old.Months == info.Months)
            return;
        Known[id] = info;
        _dirty = true;
    }

    /// <summary>Запомнить то, что кассир/владелец только что записал в карточку (до следующей загрузки каталога).</summary>
    public static void Set(string? productId, bool track, int? months)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        EnsureLoaded();
        Known[productId.Trim()] = new Info { Track = track, Months = months is { } m ? Math.Clamp(m, 0, 120) : null };
        _dirty = true;
    }

    /// <summary>track_serials товара: true/false — сервер сказал; null — не знаем (старый сервер или карточка без поля).</summary>
    public static bool? TrackSerials(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        EnsureLoaded();
        return Known.TryGetValue(productId.Trim(), out var i) ? i.Track : null;
    }

    /// <summary>warranty_months товара: число (0 — гарантии нет); null — не знаем.</summary>
    public static int? WarrantyMonths(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        EnsureLoaded();
        return Known.TryGetValue(productId.Trim(), out var i) ? i.Months : null;
    }

    /// <summary>Только для проверок: забыть всё (память; файл не трогает).</summary>
    public static void ResetForTests()
    {
        Known.Clear();
        _loaded = false;
        _dirty = false;
    }

    public static void SaveIfDirty()
    {
        if (!_dirty)
            return;
        try
        {
            lock (FileGate)
            {
                _dirty = false;
                var snapshot = Known.ToDictionary(kv => kv.Key, kv => kv.Value);
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(snapshot));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Серийники товаров: не удалось сохранить на диск: {ex.Message}", "WARNING");
        }
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (FileGate)
        {
            if (_loaded)
                return;
            try
            {
                var path = FilePath;
                if (File.Exists(path)
                    && AtomicFile.ReadAllText(path) is { Length: > 0 } text
                    && JsonSerializer.Deserialize<Dictionary<string, Info>>(text) is { } stored)
                    foreach (var (id, info) in stored)
                        if (info is not null)
                            Known.TryAdd(id, info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                PosLogger.Log($"Серийники товаров: файл не прочитан ({ex.Message}) — ждём каталог с сервера.", "WARNING");
            }
            _loaded = true;
        }
    }

    private static bool? ReadBool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => e.TryGetInt32(out var n) ? n != 0 : null,
        JsonValueKind.String => e.GetString()?.Trim().ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => null },
        _ => null,
    };

    /// <summary>Месяцы гарантии: число или строка-число; null в ответе = «гарантии нет» (0); другое — не разобрано (null).</summary>
    private static int? ReadMonths(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
                return 0;
            case JsonValueKind.Number when e.TryGetDecimal(out var d):
                return (int)Math.Clamp(Math.Round(d), 0, 120);
            case JsonValueKind.String when decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s):
                return (int)Math.Clamp(Math.Round(s), 0, 120);
            default:
                return null;
        }
    }
}
