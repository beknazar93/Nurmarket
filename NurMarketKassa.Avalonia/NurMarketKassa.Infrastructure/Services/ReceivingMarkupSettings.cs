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
    /// <summary>Считать цену продажи автоматически при ручном вводе закупки. 2026-11-06 (жалоба «Орук маркет», приход №48: программа молча поставила «закупка + 20 %»):
    /// по умолчанию ВЫКЛЮЧЕНО — общая наценка включается только явно (число в поле «Наценка для всех, %» или галочка в окне «Наценка…»).</summary>
    public bool AutoEnabled { get; set; }

    /// <summary>«Наценка для всех, %» — для товара без наценки категории. 2026-11-06: по умолчанию 0 (не задана); 0 = общей наценки нет.</summary>
    public double DefaultPercent { get; set; }

    /// <summary>2026-11-06: сброс настроек 1.17.78 выполнен (после обновления общая наценка один раз выключается: у части магазинов в файле стояло 20 или 30 «по умолчанию»,
    /// которые владелец не вводил). В новом файле true; у файла без признака — миграция при чтении (<see cref="ReceivingMarkupSettingsStore"/>).</summary>
    public bool MarkupResetDone { get; set; }

    /// <summary>2026-11-06 (живой тест, Б-10): общая наценка, которая стояла до сброса (только если была включена); для подсказки владельцу, автоматически не включается.</summary>
    public double ResetPreviousPercent { get; set; }

    /// <summary>2026-11-06 (Б-10): сброс выключил включённую общую наценку, а владельцу об этом ещё не сказали на экране. Файл общий для компании, поэтому показ запоминается по компании.</summary>
    public bool ResetNoticePending { get; set; }

    /// <summary>Наценка по категориям: название категории → %. Регистр не важен.</summary>
    public Dictionary<string, double> CategoryPercents { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Округление цены продажи.</summary>
    public SaleRounding Rounding { get; set; } = SaleRounding.WholeUp;

    public ReceivingMarkupSettings Clone() => new()
    {
        AutoEnabled = AutoEnabled,
        DefaultPercent = DefaultPercent,
        MarkupResetDone = MarkupResetDone,
        ResetPreviousPercent = ResetPreviousPercent,
        ResetNoticePending = ResetNoticePending,
        CategoryPercents = new Dictionary<string, double>(CategoryPercents, StringComparer.OrdinalIgnoreCase),
        Rounding = Rounding,
    };

    /// <summary>Текст для поля «Наценка для всех, %»: пусто, если общей наценки нет (0), иначе число.</summary>
    public static string PercentBoxText(double percent) =>
        percent > 0 ? percent.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : "";

    /// <summary>Приводит значения к допустимым (после чтения файла и перед записью).</summary>
    public void Normalize()
    {
        if (double.IsNaN(DefaultPercent) || double.IsInfinity(DefaultPercent) || DefaultPercent < 0 || DefaultPercent > 1000)
            DefaultPercent = 0; // 2026-11-06: было 30 «по умолчанию» — теперь общей наценки нет, пока её не ввели
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
        DataDirOverride ?? Path.Combine(NurMarketKassa.Services.AppPaths.Roaming, AppMode.DataFolderName),
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
                        MigrateReset(s);
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
        return new ReceivingMarkupSettings { MarkupResetDone = true };
    }

    /// <summary>2026-11-06 (жалоба «Орук маркет», приход №48): один раз при обновлении общая наценка выключается и обнуляется — раньше в файле стояла включённая «30 %» (или 20 %),
    /// из-за чего цена продажи ставилась сама. Категории и округление остаются. Запись в журнал; владелец включит наценку сам. Возвращает true, если сброс выполнен сейчас.</summary>
    public static bool MigrateReset(ReceivingMarkupSettings s)
    {
        if (s.MarkupResetDone)
            return false;
        var was = s.AutoEnabled ? $"включена, {s.DefaultPercent:0.##} %" : $"выключена, {s.DefaultPercent:0.##} %";
        // 2026-11-06 (живой тест, Б-10): включённую наценку запоминаем и просим показать владельцу один раз на экране (раньше сообщение было только в журнале).
        if (s.AutoEnabled && s.DefaultPercent > 0)
        {
            s.ResetPreviousPercent = s.DefaultPercent;
            s.ResetNoticePending = true;
        }
        s.AutoEnabled = false;
        s.DefaultPercent = 0;
        s.MarkupResetDone = true;
        PosLogger.Log($"Наценка приёмки: общая наценка сброшена один раз при обновлении (была {was}) — теперь выключена, цену продажи программа сама не ставит; включить можно в поле «Наценка для всех, %» или в окне «Наценка…».", "WARNING");
        try
        {
            if (!PersistenceDisabled)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Наценка приёмки: сброшенные настройки не записаны ({ex.GetType().Name}) — сброс повторится при следующем запуске.", "WARNING");
        }
        return true;
    }

    /// <summary>2026-11-06 (живой тест, Б-10): забрать «непоказанное» уведомление о сбросе общей наценки. Возвращает прежний процент, если показать нужно, и сразу запоминает показ
    /// в файле (по компании) — второй раз null. Нет уведомления (сброса не было или уже показывали) — null.</summary>
    public static double? TakeResetNotice()
    {
        ReceivingMarkupSettings next;
        lock (Gate)
        {
            var cur = _current ??= Load();
            if (!cur.ResetNoticePending)
                return null;
            next = cur.Clone();
            next.ResetNoticePending = false;
            _current = next;
        }
        try
        {
            if (!PersistenceDisabled)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(next, JsonOptions));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Наценка приёмки: показ уведомления о сбросе не записан ({ex.GetType().Name}) — уведомление может показаться ещё раз.", "WARNING");
        }
        return next.ResetPreviousPercent;
    }

    /// <summary>2026-10-28: поле «Наценка для всех, %» над списком приёмки. persist = false — пока вводят (строки пересчитываются на лету, файл не пишется),
    /// true — поле закончили править (значение запоминается между приёмками). Событие <see cref="Changed"/> приходит в обоих случаях.</summary>
    public static void SetDefaultPercent(double percent, bool persist)
    {
        if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0 || percent > 1000)
            return;
        var next = Current.Clone();
        next.DefaultPercent = percent;
        // 2026-11-06: число в поле «Наценка для всех, %» — явное включение общей наценки; пусто/0 — общей наценки нет (автоцена по категориям от поля не зависит).
        if (percent > 0)
            next.AutoEnabled = true;
        else if (next.CategoryPercents.Count == 0)
            next.AutoEnabled = false;
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
        settings.MarkupResetDone = true; // 2026-11-06: сохранённое владельцем — уже после сброса, не затирать повторно
        if (settings.AutoEnabled)
            settings.ResetNoticePending = false; // 2026-11-06 (Б-10): общую наценку уже включили — напоминать о сбросе не нужно (пустое поле кассира уведомление не гасит)
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
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions)); // 2026-11-01 (Н2-01/Н-07): сброс на диск + копия .bak
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
