using System.Text.Json;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-10-26: настройки окна «Печать штрихкодов и ценников», которых нет в шаблоне этикетки: копии по умолчанию, лист A4
/// и принтер для A4. Размер этикетки, что показывать, высота и толщина штриха хранятся в самом шаблоне этикетки (LabelTemplateStore).</summary>
public sealed class BarcodePrintSettings
{
    /// <summary>«Копий (по умолчанию)» — для каждой этикетки, у которой число копий не задано отдельно.</summary>
    public int DefaultCopies { get; set; } = 1;

    /// <summary>Раскрыты ли «Настройки этикетки» в окне. null — не выбирал: раскрыты только на высоком экране (карточкам товаров нужно место).</summary>
    public bool? SettingsOpen { get; set; }

    public A4SheetOptions A4 { get; set; } = new();

    /// <summary>«Копий каждого» на листе A4.</summary>
    public int A4Copies { get; set; } = 1;

    /// <summary>Принтер для листов A4 (имя очереди Windows); пусто — первый подходящий.</summary>
    public string? A4PrinterName { get; set; }

    // 2026-11-01 (пакеты из веба, П-4/П-5/П-9). Всё необязательно: нет поля в файле — прежнее поведение.
    /// <summary>«Копии»: "default" — «по умолчанию», "stock" — «= остаток» (целое 1..99; дробное и весовое = 1).</summary>
    public string CopiesMode { get; set; } = "default";
    /// <summary>Режим сканера «скан → в печать».</summary>
    public bool ScanToQueue { get; set; }
    /// <summary>Что было в окне при закрытии (копии и выбор товаров не запоминаются).</summary>
    public string? LastSearch { get; set; }
    public int LastFilter { get; set; }
    public string? LastCategory { get; set; }
    public int LastPage { get; set; }
}

/// <summary>Файл barcode-print-settings.json рядом с шаблоном этикетки. Ошибки чтения/записи не бросаются — окно работает с умолчаниями.</summary>
public static class BarcodePrintSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        NurMarketKassa.Services.AppMode.DataFolderName, "barcode-print-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static BarcodePrintSettings Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<BarcodePrintSettings>(File.ReadAllText(FilePath), JsonOptions) is { } s)
            {
                s.A4 ??= new A4SheetOptions();
                s.A4.Name ??= new A4TextStyle();
                s.A4.Price ??= new A4TextStyle { SizePt = 16 };
                s.A4.OldPrice ??= new A4TextStyle { SizePt = 12, Bold = false }; // 2026-11-01 (П-8)
                s.A4.Plu ??= new A4TextStyle { SizePt = 12, Bold = true };
                s.A4.Description ??= new A4TextStyle { SizePt = 10, Bold = false };
                s.A4.Preset ??= "";
                s.A4.StartCell = Math.Clamp(s.A4.StartCell, 1, 400);
                s.A4.OffsetXmm = Math.Clamp(s.A4.OffsetXmm, -10, 10);
                s.A4.OffsetYmm = Math.Clamp(s.A4.OffsetYmm, -10, 10);
                if (s.CopiesMode != "stock")
                    s.CopiesMode = "default";
                if (s.LastSearch is { Length: > 200 })
                    s.LastSearch = s.LastSearch[..200];
                s.LastPage = Math.Max(s.LastPage, 0);
                s.DefaultCopies = Math.Clamp(s.DefaultCopies, 1, 99);
                s.A4Copies = Math.Clamp(s.A4Copies, 1, 99);
                return s;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print settings load failed: {ex.GetType().Name}", "WARNING");
        }
        return new BarcodePrintSettings();
    }

    /// <summary>Стенд проверки (без экрана) выставляет true отражением ДО создания окон — Save ничего не пишет на диск
    /// (как у LabelTemplateStore). В программе всегда false.</summary>
#pragma warning disable CS0649 // выставляется стендом отражением
    private static bool _persistenceDisabled;
#pragma warning restore CS0649

    public static void Save(BarcodePrintSettings settings)
    {
        if (_persistenceDisabled)
            return;
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions)); // 2026-11-01 (аудит 10 из 10, Н-07): без риска полфайла
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Barcode print settings save failed: {ex.GetType().Name}", "ERROR");
        }
    }
}
