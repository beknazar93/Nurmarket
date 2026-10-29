using System.Text.Json;
using System.Text.Json.Serialization;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-10-28: как округлять цену продажи, посчитанную от закупки и наценки (ручная приёмка, Склад → Приёмка и Закупки → Приём товара).
/// «До целого сома вверх» — как было всегда (наценка не станет ниже заданной); остальные режимы — к ближайшему шагу.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SaleRounding
{
    /// <summary>До целого сома вверх (по умолчанию, прежнее поведение).</summary>
    WholeUp,

    /// <summary>До 0,5 сома (к ближайшему).</summary>
    Half,

    /// <summary>До 0,1 сома (к ближайшему).</summary>
    Tenth,

    /// <summary>До 0,01 — без округления (копейки).</summary>
    None,
}

/// <summary>2026-10-28, просьба владельца «автоматическая наценка в ручном режиме приёмки»: правила наценки магазина.
/// Приоритет при расчёте цены продажи по закупке: наценка товара (поле markup_percent или прежние цены) → наценка категории → наценка по умолчанию.
/// Хранится в своём файле (<see cref="ReceivingMarkupSettingsStore"/>), менять могут владелец и администратор.</summary>
public sealed class ReceivingMarkupSettings
{
    /// <summary>Считать цену продажи автоматически при ручном вводе закупки (по умолчанию включено).</summary>
    public bool AutoEnabled { get; set; } = true;

    /// <summary>«Наценка по умолчанию, %» — для товара без своей наценки и без наценки категории.</summary>
    public double DefaultPercent { get; set; } = 30;

    /// <summary>Наценка по категориям: название категории → %. Регистр не важен.</summary>
    public Dictionary<string, double> CategoryPercents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Округление цены продажи.</summary>
    public SaleRounding Rounding { get; set; } = SaleRounding.WholeUp;

    public ReceivingMarkupSettings Clone() => new()
    {
        AutoEnabled = AutoEnabled,
        DefaultPercent = DefaultPercent,
        CategoryPercents = new Dictionary<string, double>(CategoryPercents, StringComparer.OrdinalIgnoreCase),
        Rounding = Rounding,
    };

    /// <summary>Приводит значения к допустимым (после чтения файла и перед записью).</summary>
    public void Normalize()
    {
        if (double.IsNaN(DefaultPercent) || double.IsInfinity(DefaultPercent) || DefaultPercent < 0 || DefaultPercent > 1000)
            DefaultPercent = 30;
        var fixedMap = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, percent) in CategoryPercents ?? new())
            if (!string.IsNullOrWhiteSpace(name) && !double.IsNaN(percent) && !double.IsInfinity(percent) && percent >= 0 && percent <= 1000)
                fixedMap[name.Trim()] = percent;
        CategoryPercents = fixedMap;
        if (!Enum.IsDefined(Rounding))
            Rounding = SaleRounding.WholeUp;
    }
}

/// <summary>Файл receiving-markup.json рядом с остальными настройками кассы (%APPDATA%\NurMarketKassa). Ошибки чтения/записи не бросаются —
/// приёмка работает с умолчаниями. Испорченный файл откладывается рядом (receiving-markup.bad-…json).</summary>
public static class ReceivingMarkupSettingsStore
{
    private static readonly object Gate = new();
    private static ReceivingMarkupSettings? _current;

    /// <summary>Стенд проверки: своя папка вместо %APPDATA% (до первого обращения).</summary>
    public static string? DataDirOverride { get; set; }

    /// <summary>Стенд проверки: ничего не писать на диск.</summary>
    public static bool PersistenceDisabled { get; set; }

    /// <summary>Настройки изменились (владелец сохранил окно «Наценка»).</summary>
    public static event Action? Changed;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName),
        "receiving-markup.json");

    /// <summary>Текущие настройки (читаются один раз; копию править и отдавать в <see cref="Save"/>).</summary>
    public static ReceivingMarkupSettings Current
    {
        get
        {
            lock (Gate)
                return _current ??= Load();
        }
    }

    /// <summary>Стенд: подставить настройки без файла.</summary>
    public static void Override(ReceivingMarkupSettings settings)
    {
        settings.Normalize();
        lock (Gate)
            _current = settings;
    }

    private static ReceivingMarkupSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                try
                {
                    if (JsonSerializer.Deserialize<ReceivingMarkupSettings>(text, JsonOptions) is { } s)
                    {
                        s.Normalize();
                        return s;
                    }
                }
                catch (JsonException ex)
                {
                    var bad = Path.Combine(Path.GetDirectoryName(FilePath)!, $"receiving-markup.bad-{DateTime.Now:yyyyMMdd-HHmmss}.json");
                    File.Copy(FilePath, bad, true);
                    PosLogger.Log($"Наценка приёмки: файл настроек повреждён ({ex.Message}) — копия {Path.GetFileName(bad)}, взяты значения по умолчанию.", "WARNING");
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Наценка приёмки: настройки не прочитаны ({ex.GetType().Name}) — значения по умолчанию.", "WARNING");
        }
        return new ReceivingMarkupSettings();
    }

    /// <summary>2026-10-28: поле «Наценка для всех, %» над списком приёмки. persist = false — пока вводят (строки пересчитываются на лету, файл не пишется),
    /// true — поле закончили править (значение запоминается между приёмками). Событие <see cref="Changed"/> приходит в обоих случаях.</summary>
    public static void SetDefaultPercent(double percent, bool persist)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0 || percent > 1000)
            return;
        var next = Current.Clone();
        next.DefaultPercent = percent;
        if (persist)
            Save(next);
        else
        {
            lock (Gate)
                _current = next;
            Changed?.Invoke();
        }
    }

    /// <summary>Сохранить настройки (только после проверки прав в окне). Возвращает false, если записать не удалось.</summary>
    public static bool Save(ReceivingMarkupSettings settings)
    {
        settings.Normalize();
        lock (Gate)
            _current = settings;
        var ok = true;
        if (!PersistenceDisabled)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex)
            {
                ok = false;
                PosLogger.Log($"Наценка приёмки: настройки не сохранены ({ex.GetType().Name}).", "ERROR");
            }
        }
        Changed?.Invoke();
        return ok;
    }
}
