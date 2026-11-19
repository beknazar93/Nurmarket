using System.Collections.Concurrent;
using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ4; ответ Умара 19.11 вечер): данные запчасти товара (<see cref="AutoPartInfo"/>) приходят в ответе сервера
/// (<c>attributes</c>, <c>storage_location</c>, <c>related[]</c>), а колонок под них в pos_local.db нет (схему не меняем). Касса запоминает их по id товара
/// в файле <c>autopart-meta.json</c> — как серийники электроники (<see cref="ProductSerialRules"/>): <see cref="Remember"/> зовёт разбор карточки
/// (<c>ProductCatalogMapper.TryTile</c>), файл пишется после загрузки каталога (<see cref="SaveIfDirty"/>). Источник правды — сервер: файл лишь кэш,
/// чтобы плитки и поиск работали после перезапуска и без связи. Смена сферы данные не теряет и не удаляет.
/// </summary>
public static class PartMetaStore
{
    private static readonly ConcurrentDictionary<string, AutoPartInfo> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileGate = new();
    private static volatile bool _loaded;
    private static volatile bool _dirty;

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "autopart-meta.json");

    /// <summary>Запомнить данные запчасти из ответа API. У товара нет ни атрибутов, ни ячейки, ни связанных — а раньше были — запись удаляется (сервер главный).</summary>
    public static void Remember(string? productId, JsonElement product)
    {
        if (string.IsNullOrWhiteSpace(productId) || product.ValueKind != JsonValueKind.Object)
            return;
        // Ответ без этих полей вовсе (список без attributes) — не стираем то, что знаем.
        if (!product.TryGetProperty("attributes", out _) && !product.TryGetProperty("storage_location", out _) && !product.TryGetProperty("related", out _))
            return;
        EnsureLoaded();
        var id = productId.Trim();
        var info = AutoPartInfo.Parse(product);
        if (info is null)
        {
            if (Known.TryRemove(id, out _))
                _dirty = true;
            return;
        }
        if (Known.TryGetValue(id, out var old) && Same(old, info))
            return;
        Known[id] = info;
        _dirty = true;
    }

    /// <summary>Запомнить то, что владелец только что записал в карточку (до следующей загрузки каталога).</summary>
    public static void Set(string? productId, AutoPartInfo? info)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return;
        EnsureLoaded();
        if (info is null || (info.IsEmpty && info.Other.Count == 0))
            Known.TryRemove(productId.Trim(), out _);
        else
            Known[productId.Trim()] = info;
        _dirty = true;
    }

    public static AutoPartInfo? Get(string? productId)
    {
        if (string.IsNullOrWhiteSpace(productId))
            return null;
        EnsureLoaded();
        return Known.TryGetValue(productId.Trim(), out var info) ? info : null;
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
            PosLogger.Log($"Данные запчастей: не удалось сохранить на диск: {ex.Message}", "WARNING");
        }
    }

    private static bool Same(AutoPartInfo a, AutoPartInfo b) =>
        JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

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
                    && JsonSerializer.Deserialize<Dictionary<string, AutoPartInfo>>(text) is { } stored)
                    foreach (var (id, info) in stored)
                        if (info is not null)
                            Known.TryAdd(id, info);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Повреждённый файл не роняет кассу: данные придут с сервера при ближайшей загрузке каталога.
                PosLogger.Log($"Данные запчастей: файл не прочитан ({ex.Message}) — ждём каталог с сервера.", "WARNING");
            }
            _loaded = true;
        }
    }
}
