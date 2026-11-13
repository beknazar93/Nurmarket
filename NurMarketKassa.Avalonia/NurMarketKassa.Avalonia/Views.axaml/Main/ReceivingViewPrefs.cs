using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-11: локальные выборы вида приёмки — файл receiving-view.json рядом с настройками кассы (как размеры упаковок): «Подробнее ▸» таблицы «Склад → Приёмка»
/// и (2026-11-11, «упаковку тоже сделай СВЕРХУ») «штук в упаковке по умолчанию» обеих приёмок. Все ключи пишутся одним файлом (запись одного не стирает другие).
/// 2026-11-12 (аудит Л-9, решение владельца): СПОСОБ приёмки («Упаковками» и т. п.) в файл больше не пишется — он живёт одно открытие приёмки и до проведения прихода
/// (раньше «Упаковками» со вчерашнего дня молча превращало каждый скан в упаковку). Старый ключ pack_mode в файле игнорируется и при следующей записи исчезает.
/// В черновик приёмки эти выборы не попадают — в него идёт только состояние самих строк.</summary>
public static class ReceivingViewPrefs
{
    private static bool? _details;
    private static double _packSize;
    private static bool _loaded;

    /// <summary>Стенд проверки: свой файл вместо %APPDATA%\…\receiving-view.json (до первого обращения).</summary>
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "receiving-view.json");

    private static void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement is not { ValueKind: JsonValueKind.Object } root)
                return;
            if (root.TryGetProperty("details", out var d) && d.ValueKind is JsonValueKind.True or JsonValueKind.False)
                _details = d.GetBoolean();
            if (root.TryGetProperty("pack_size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var size) && ReceivingPackDefaults.IsValidSize(size))
                _packSize = size;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: файл вида таблицы не прочитан ({ex.GetType().Name}) — по умолчанию.", "WARNING");
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var map = new Dictionary<string, object>();
            if (_details is { } details)
                map["details"] = details;
            if (_packSize > 0)
                map["pack_size"] = _packSize;
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(map));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: вид таблицы не записан ({ex.GetType().Name}).", "WARNING");
        }
    }

    /// <summary>«Подробнее ▸»: null — не выбирали.</summary>
    public static bool? Details
    {
        get
        {
            EnsureLoaded();
            return _details;
        }
    }

    public static void SetDetails(bool shown)
    {
        EnsureLoaded();
        _details = shown;
        Save();
    }

    /// <summary>«Штук в упаковке по умолчанию»; 0 — не задано.</summary>
    public static double PackSize
    {
        get
        {
            EnsureLoaded();
            return _packSize;
        }
    }

    public static void SetPackSize(double size)
    {
        EnsureLoaded();
        var value = ReceivingPackDefaults.IsValidSize(size) ? size : 0;
        if (Math.Abs(_packSize - value) < 1e-12)
            return;
        _packSize = value;
        Save();
    }

    /// <summary>Стенд: забыть выбор (как первый запуск).</summary>
    public static void ResetForTests()
    {
        _loaded = true;
        _details = null;
        _packSize = 0;
    }

    /// <summary>Стенд: как после перезапуска кассы — прочитать файл заново.</summary>
    public static void ReloadForTests()
    {
        _loaded = false;
        _details = null;
        _packSize = 0;
    }
}
