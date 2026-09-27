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
            throw new InvalidDataException("not a NurMarket theme file");

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
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folder, FileName);

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
            catch (Exception ex)
            {
                PosLogger.Log($"Свои темы не прочитались: {ex.Message}", "WARNING");
            }
        }
        return new List<CustomThemeDefinition>();
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
