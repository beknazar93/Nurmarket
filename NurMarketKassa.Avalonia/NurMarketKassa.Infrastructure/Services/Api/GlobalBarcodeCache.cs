using System.Collections.Concurrent;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-10-03, владелец: «очень медленно» — поиск названия в общей базе NurCRM ждал сервер по 40–60 с.
/// Найденные товары общей базы (название, категория, бренд) храним на компьютере 30 дней
/// (%LOCALAPPDATA%\NurMarketKassa\global-barcodes.json, общий для кассы и программы владельца): повторный скан
/// того же штрихкода подставляется без сервера. Не найденные не запоминаем — их могут добавить в базу.</summary>
internal static class GlobalBarcodeCache
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "global-barcodes.json");
    private static readonly TimeSpan Ttl = TimeSpan.FromDays(30);
    private static readonly object Gate = new();
    private static ConcurrentDictionary<string, Entry>? _items;

    private sealed class Entry
    {
        public DateTime At { get; set; }
        public string Json { get; set; } = "";
    }

    public static JsonElement? TryGet(string barcode)
    {
        var items = Load();
        if (!items.TryGetValue(barcode, out var e) || DateTime.UtcNow - e.At > Ttl)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(e.Json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    public static void Put(string barcode, JsonElement product)
    {
        var items = Load();
        items[barcode] = new Entry { At = DateTime.UtcNow, Json = product.GetRawText() };
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(items.Where(p => DateTime.UtcNow - p.Value.At <= Ttl)
                    .ToDictionary(p => p.Key, p => p.Value)));
                File.Move(tmp, FilePath, true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Общая база: кэш штрихкодов не сохранён ({ex.Message}).", "WARNING");
        }
    }

    private static ConcurrentDictionary<string, Entry> Load()
    {
        if (_items != null)
            return _items;
        lock (Gate)
        {
            if (_items != null)
                return _items;
            var loaded = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(FilePath)
                    && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) is { } fromFile)
                    foreach (var (k, v) in fromFile)
                        loaded[k] = v;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Общая база: кэш штрихкодов не прочитан ({ex.Message}).", "WARNING");
            }
            _items = loaded;
            return loaded;
        }
    }
}
