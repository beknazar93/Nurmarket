using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Ключевые цвета своей темы для одного варианта (светлого или тёмного). null — цвет
/// берётся из базовой темы. Остальные оттенки (рамки, мягкие фоны, наведение) выводятся из
/// этих девяти — см. AccentThemeService.BuildCustomPalette.</summary>
public sealed class CustomThemeColors
{
    public string? Accent { get; set; }
    public string? AccentText { get; set; }
    public string? Background { get; set; }
    public string? Panel { get; set; }
    public string? Text { get; set; }
    public string? TextSoft { get; set; }
    public string? Success { get; set; }
    public string? Warning { get; set; }
    public string? Danger { get; set; }

    public CustomThemeColors Clone() => (CustomThemeColors)MemberwiseClone();

    public bool IsEmpty =>
        Accent is null && AccentText is null && Background is null && Panel is null && Text is null &&
        TextSoft is null && Success is null && Warning is null && Danger is null;
}

/// <summary>Своя тема кассы (Маркетплейс → Темы → «Редактор тем», 2026-09-28): базовая встроенная
/// тема + свои ключевые цвета для светлого и тёмного варианта, скругление углов и размер шрифта.</summary>
public sealed class CustomThemeDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseThemeId { get; set; } = "classic";
    public CustomThemeColors Light { get; set; } = new();
    public CustomThemeColors Dark { get; set; } = new();

    /// <summary>Скругление кнопок, px (карточки — на треть больше). null — как у базовой темы.</summary>
    public double? CornerRadius { get; set; }

    /// <summary>Базовый размер шрифта, px. null — как у базовой темы.</summary>
    public double? FontSize { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public CustomThemeColors ColorsFor(bool dark) => dark ? Dark : Light;

    public CustomThemeDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        BaseThemeId = BaseThemeId,
        Light = Light.Clone(),
        Dark = Dark.Clone(),
        CornerRadius = CornerRadius,
        FontSize = FontSize,
        UpdatedUtc = UpdatedUtc,
    };
}

/// <summary>Хранилище своих тем: %AppData%\NurMarketKassa\custom-themes.json (у программы
/// владельца — своя папка, как и у остальных настроек; при первом запуске она читает темы кассы).
/// Экспорт/импорт одной темы — маленький JSON-файл с пометкой формата, чтобы тему можно было
/// перенести на другую кассу.</summary>
public static class CustomThemeStore
{
    public const string IdPrefix = "custom:";
    private const string FileName = "custom-themes.json";
    private const string ExportFormat = "nurmarket-theme";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static List<CustomThemeDefinition>? _themes;

    /// <summary>2026-10-27 (аудит редакторов, В-6): папка для файла тем вместо %AppData% — выставляет только стенд проверок
    /// отражением (GetFolderPath обходит подмену переменной APPDATA). В программе всегда null.</summary>
#pragma warning disable CS0649
    private static string? _rootOverride;
#pragma warning restore CS0649

    /// <summary>2026-10-27 (аудит редакторов, В-6): что случилось с файлом тем при чтении — null, если всё в порядке. Не пусто, когда
    /// файл оказался испорченным: его копия сохранена рядом (<c>.bad</c>), а темы восстановлены из последней хорошей копии (<c>.bak</c>), если она есть.
    /// Редактор тем показывает это сообщение; список «Мои темы» молча пустым не остаётся.</summary>
    public static string? LoadProblem { get; private set; }

    /// <summary>Список изменился (сохранили, удалили, импортировали) — галерея тем перестраивается.</summary>
    public static event Action? Changed;

    public static string DefaultName => Tr.T("Моя тема", "Менин темам", "My theme", "Temam", "Mening mavzuim");

    public static bool IsCustomId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<CustomThemeDefinition> All => Themes;

    public static CustomThemeDefinition? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Themes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Новая тема на основе встроенной (или копия своей). Не сохраняется, пока не вызван Save.</summary>
    public static CustomThemeDefinition Create(string baseThemeId, string name, CustomThemeDefinition? copyFrom = null)
    {
        var def = copyFrom?.Clone() ?? new CustomThemeDefinition { BaseThemeId = baseThemeId };
        def.Id = IdPrefix + Guid.NewGuid().ToString("N")[..10];
        def.Name = UniqueName(string.IsNullOrWhiteSpace(name) ? DefaultName : name.Trim(), null);
        def.UpdatedUtc = DateTime.UtcNow;
        return def;
    }

    /// <summary>Добавляет или заменяет тему (по Id) и пишет файл.</summary>
    public static void Save(CustomThemeDefinition def)
    {
        var copy = def.Clone();
        copy.Name = UniqueName(string.IsNullOrWhiteSpace(copy.Name) ? DefaultName : copy.Name.Trim(), copy.Id);
        copy.UpdatedUtc = DateTime.UtcNow;
        var list = Themes;
        var index = list.FindIndex(t => string.Equals(t.Id, copy.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            list[index] = copy;
        else
            list.Add(copy);
        Persist();
    }

    public static void Delete(string id)
    {
        if (Themes.RemoveAll(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) > 0)
            Persist();
    }

    /// <summary>Одна тема в переносимый JSON (для файла «Экспорт»).</summary>
    public static string Export(CustomThemeDefinition def) =>
        JsonSerializer.Serialize(new ExportEnvelope { Format = ExportFormat, Version = 1, Theme = def.Clone() }, Json);

    /// <summary>Тема из файла «Импорт»: проверяет формат и цвета, выдаёт новый Id (чтобы импорт не
    /// затёр уже существующую тему) и сохраняет. Бросает InvalidDataException с понятным текстом.</summary>
    public static CustomThemeDefinition Import(string json)
    {
        ExportEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<ExportEnvelope>(json, Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        if (envelope?.Theme is null || !string.Equals(envelope.Format, ExportFormat, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("not a NurCRM theme file");

        var theme = envelope.Theme;
        Sanitize(theme.Light);
        Sanitize(theme.Dark);
        theme.CornerRadius = theme.CornerRadius is { } r ? Math.Clamp(r, 0, 24) : null;
        theme.FontSize = theme.FontSize is { } f ? Math.Clamp(f, 12, 18) : null;
        if (string.IsNullOrWhiteSpace(theme.BaseThemeId) || IsCustomId(theme.BaseThemeId))
            theme.BaseThemeId = "classic";

        var imported = Create(theme.BaseThemeId, theme.Name, theme);
        Save(imported);
        return Find(imported.Id)!;
    }

    public static string SuggestFileName(CustomThemeDefinition def)
    {
        var safe = new string(def.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        return (safe.Length == 0 ? "theme" : safe) + ".nmtheme.json";
    }

    /// <summary>Нормализует HEX (#RGB / #RRGGBB / #AARRGGBB → #RRGGBB); неверный — null.</summary>
    public static string? NormalizeHex(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (!text.StartsWith('#'))
            text = "#" + text;
        if (!Color.TryParse(text, out var color))
            return null;
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    // ------------------------------------------------------------------ файл

    private static List<CustomThemeDefinition> Themes => _themes ??= Load();

    private static string FilePath(string folder) =>
        Path.Combine(_rootOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folder, FileName);

    /// <summary>Читает файл тем; бросает исключение, если файл испорчен.</summary>
    private static List<CustomThemeDefinition> ReadFile(string path)
    {
        var list = JsonSerializer.Deserialize<List<CustomThemeDefinition>>(File.ReadAllText(path), Json) ?? new();
        foreach (var theme in list)
        {
            theme.Light ??= new CustomThemeColors();
            theme.Dark ??= new CustomThemeColors();
            Sanitize(theme.Light);
            Sanitize(theme.Dark);
        }
        return list.Where(t => IsCustomId(t.Id)).ToList();
    }

    private static List<CustomThemeDefinition> Load()
    {
        // Своя папка, а у программы владельца при первом запуске — темы кассы (как и настройки).
        var folders = NurMarketKassa.Services.AppMode.IsOwner
            ? new[] { NurMarketKassa.Services.AppMode.DataFolderName, "NurMarketKassa" }
            : new[] { NurMarketKassa.Services.AppMode.DataFolderName };
        foreach (var folder in folders)
        {
            try
            {
                var path = FilePath(folder);
                if (!File.Exists(path))
                    continue;
                return ReadFile(path);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Свои темы не прочитались: {ex.Message}", "WARNING");
                // 2026-10-27 (аудит редакторов, В-6): раньше пустой список молча записывался поверх файла первым же «Сохранить» — все темы
                // пропадали. Теперь испорченный файл откладывается рядом (.bad), а темы берутся из последней хорошей копии (.bak).
                var recovered = RecoverFromCorrupt(FilePath(folder));
                if (recovered is not null)
                    return recovered;
            }
        }
        return new List<CustomThemeDefinition>();
    }

    private static List<CustomThemeDefinition>? RecoverFromCorrupt(string path)
    {
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Копия испорченного файла тем не сохранилась: {ex.Message}", "WARNING");
        }

        List<CustomThemeDefinition>? restored = null;
        try
        {
            if (File.Exists(path + ".bak"))
                restored = ReadFile(path + ".bak");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Копия файла тем (.bak) тоже не читается: {ex.Message}", "WARNING");
        }

        LoadProblem = restored is not null
            ? Tr.T($"Файл своих тем был повреждён. Темы восстановлены из последней копии ({restored.Count}); испорченный файл сохранён рядом как custom-themes.json.bad.",
                $"Өз темалар файлы бузулган. Темалар акыркы көчүрмөдөн калыбына келтирилди ({restored.Count}); бузулган файл custom-themes.json.bad деп жанына сакталды.",
                $"The custom themes file was damaged. Themes were restored from the last backup ({restored.Count}); the damaged file is kept next to it as custom-themes.json.bad.",
                $"Özel temalar dosyası bozulmuş. Temalar son yedekten geri yüklendi ({restored.Count}); bozuk dosya yanına custom-themes.json.bad olarak kaydedildi.",
                $"O'z mavzulari fayli buzilgan. Mavzular oxirgi nusxadan tiklandi ({restored.Count}); buzilgan fayl yoniga custom-themes.json.bad nomi bilan saqlandi.")
            : Tr.T("Файл своих тем был повреждён и не читается. Его копия сохранена рядом как custom-themes.json.bad — пришлите её в поддержку, темы можно вернуть.",
                "Өз темалар файлы бузулган жана окулбайт. Анын көчүрмөсү custom-themes.json.bad деп жанына сакталды — колдоого жөнөтүңүз, темаларды кайтарууга болот.",
                "The custom themes file was damaged and cannot be read. A copy is kept next to it as custom-themes.json.bad — send it to support, the themes can be recovered.",
                "Özel temalar dosyası bozulmuş ve okunamıyor. Bir kopyası yanına custom-themes.json.bad olarak kaydedildi — destek ekibine gönderin, temalar kurtarılabilir.",
                "O'z mavzulari fayli buzilgan va o'qilmaydi. Nusxasi yoniga custom-themes.json.bad nomi bilan saqlandi — yordam xizmatiga yuboring, mavzularni qaytarish mumkin.");
        return restored;
    }

    private static void Persist()
    {
        WriteFile(JsonSerializer.Serialize(Themes, Json));
        Changed?.Invoke();
    }

    private static void WriteFile(string json)
    {
        try
        {
            var path = FilePath(NurMarketKassa.Services.AppMode.DataFolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // 2026-10-27 (В-6): прежний хороший файл — в .bak (испорченный в копию не попадает).
            if (File.Exists(path))
            {
                try
                {
                    ReadFile(path);
                    File.Copy(path, path + ".bak", overwrite: true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Копия файла тем (.bak) не обновлена: {ex.Message}", "WARNING");
                }
            }
            // Сначала во временный файл, затем подмена — чтобы сбой питания не оставил полфайла.
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Свои темы не сохранились: {ex.Message}", "WARNING");
        }
    }

    private static void Sanitize(CustomThemeColors c)
    {
        c.Accent = NormalizeHex(c.Accent);
        c.AccentText = NormalizeHex(c.AccentText);
        c.Background = NormalizeHex(c.Background);
        c.Panel = NormalizeHex(c.Panel);
        c.Text = NormalizeHex(c.Text);
        c.TextSoft = NormalizeHex(c.TextSoft);
        c.Success = NormalizeHex(c.Success);
        c.Warning = NormalizeHex(c.Warning);
        c.Danger = NormalizeHex(c.Danger);
    }

    private static string UniqueName(string name, string? ownId)
    {
        var candidate = name;
        for (var i = 2; Themes.Any(t => !string.Equals(t.Id, ownId, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(t.Name, candidate, StringComparison.CurrentCultureIgnoreCase)); i++)
            candidate = $"{name} ({i})";
        return candidate;
    }

    private sealed class ExportEnvelope
    {
        public string? Format { get; set; }
        public int Version { get; set; }
        public CustomThemeDefinition? Theme { get; set; }
    }
}
