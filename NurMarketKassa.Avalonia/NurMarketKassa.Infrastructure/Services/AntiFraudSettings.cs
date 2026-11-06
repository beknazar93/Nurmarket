using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-Б, функции 2, 12 и 8 из docs/Отчеты/Поиск решений на GitHub и GitLab — функции магазина): настройки «Подозрительных операций»
// (антифрод), «Права кассира по действиям + PIN старшего» и «Электронного чека через Telegram-бот». Идеи: exception-based reporting, Odoo/OCA pos_user_restriction;
// код свой. Файл ОДИН на компьютер и общий для кассы и программы владельца (%LOCALAPPDATA%\NurMarketKassa\antifraud-settings.json) — владелец задаёт пороги в
// своей программе, касса применяет их сразу. По умолчанию всё выключено: касса ведёт себя как раньше. Денег файл не содержит.

/// <summary>Правила «Подозрительных операций»: что считать подозрительным и какие пороги.</summary>
public sealed class FraudRules
{
    /// <summary>Вести журнал подозрительных операций и считать баллы кассиров.</summary>
    public bool Enabled { get; set; }

    /// <summary>Сообщать владельцу в Telegram о каждом срабатывании (тихие часы — ниже, накопленное уходит утром).</summary>
    public bool Telegram { get; set; } = true;

    public bool Removal { get; set; } = true;
    /// <summary>Строка убрана из чека на сумму не меньше N сом.</summary>
    public double RemovalAmount { get; set; } = 500;

    public bool RemovalMany { get; set; } = true;
    /// <summary>За одну смену убрано из чека строк больше K.</summary>
    public int RemovalPerShift { get; set; } = 10;

    public bool Return { get; set; } = true;
    /// <summary>Возврат на сумму не меньше N сом.</summary>
    public double ReturnAmount { get; set; } = 2000;

    /// <summary>Возврат без чека (по номеру чека не найден) — всегда подозрительный.</summary>
    public bool ReturnNoReceipt { get; set; } = true;

    public bool Discount { get; set; } = true;
    /// <summary>Скидка не меньше X % (на строку или на весь чек).</summary>
    public double DiscountPercent { get; set; } = 20;

    /// <summary>Цена ниже закупки (продажа в убыток).</summary>
    public bool BelowCost { get; set; } = true;

    public bool Shortage { get; set; } = true;
    /// <summary>Недостача при закрытии смены не меньше N сом.</summary>
    public double ShortageAmount { get; set; } = 300;

    /// <summary>Тихие часы для сообщений в Telegram: с этого часа до другого (через полночь тоже).</summary>
    public int QuietFromHour { get; set; } = 22;
    public int QuietToHour { get; set; } = 8;
}

/// <summary>Действия кассира, которые можно закрыть PIN-кодом владельца/старшего.</summary>
public enum GuardAction
{
    Discount,
    Return,
    ManualPrice,
    RemovalAfterRing,
    OpenDrawer,
    ShiftShortage,
}

/// <summary>Правила «спросить PIN старшего». Выключено = как раньше (никаких вопросов).</summary>
public sealed class ActionGuardRules
{
    public bool Enabled { get; set; }

    public bool Discount { get; set; } = true;
    /// <summary>Скидка выше X % — спросить PIN.</summary>
    public double DiscountPercent { get; set; } = 10;

    public bool Return { get; set; } = true;
    /// <summary>Возврат на сумму от N сом — спросить PIN (0 — любой возврат).</summary>
    public double ReturnAmount { get; set; }

    public bool ManualPrice { get; set; }

    public bool RemovalAfterRing { get; set; }
    /// <summary>Удаление строки после пробития на сумму от N сом — спросить PIN.</summary>
    public double RemovalAmount { get; set; } = 500;

    public bool OpenDrawer { get; set; }

    public bool ShiftShortage { get; set; } = true;
    /// <summary>Закрытие смены с недостачей от N сом — спросить PIN.</summary>
    public double ShortageAmount { get; set; } = 300;
}

/// <summary>Электронный чек через Telegram-бот магазина.</summary>
public sealed class TelegramReceiptRules
{
    /// <summary>Касса сама шлёт чек клиенту, который привязал телефон к боту (кнопка Start + «Подтвердить номер»).</summary>
    public bool Enabled { get; set; }
    /// <summary>Шлёт ли касса баланс бонусов вместе с чеком.</summary>
    public bool Bonus { get; set; } = true;
    /// <summary>QR «Чеки и бонусы в Telegram» на экране покупателя.</summary>
    public bool QrOnCustomerDisplay { get; set; } = true;
    /// <summary>Две строки со ссылкой на бота в конце печатного чека (по умолчанию выключено: бумага как раньше).</summary>
    public bool LinkOnPaper { get; set; }
}

public sealed class AntiFraudData
{
    public FraudRules Fraud { get; set; } = new();
    public ActionGuardRules Guard { get; set; } = new();
    public TelegramReceiptRules Receipts { get; set; } = new();

    public void Normalize()
    {
        Fraud ??= new();
        Guard ??= new();
        Receipts ??= new();
        var f = Fraud;
        f.RemovalAmount = Clean(f.RemovalAmount, 500);
        f.ReturnAmount = Clean(f.ReturnAmount, 2000);
        f.ShortageAmount = Clean(f.ShortageAmount, 300);
        f.DiscountPercent = Math.Clamp(double.IsFinite(f.DiscountPercent) ? f.DiscountPercent : 20, 0, 100);
        f.RemovalPerShift = Math.Clamp(f.RemovalPerShift, 1, 1000);
        f.QuietFromHour = Math.Clamp(f.QuietFromHour, 0, 23);
        f.QuietToHour = Math.Clamp(f.QuietToHour, 0, 23);
        var g = Guard;
        g.DiscountPercent = Math.Clamp(double.IsFinite(g.DiscountPercent) ? g.DiscountPercent : 10, 0, 100);
        g.ReturnAmount = Clean(g.ReturnAmount, 0);
        g.RemovalAmount = Clean(g.RemovalAmount, 500);
        g.ShortageAmount = Clean(g.ShortageAmount, 300);
    }

    private static double Clean(double value, double fallback) => double.IsFinite(value) ? Math.Max(0, value) : fallback;
}

/// <summary>Хранилище настроек: один файл на компьютер, общий для кассы и владельца. Перечитывается, если другая программа его изменила.</summary>
public static class AntiFraudSettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static AntiFraudData? _current;
    private static DateTime _readStamp;
    // 2026-11-06 (аудит №5, Л5-10): последнее чтение файла не удалось (занят антивирусом, нет доступа). Прежние настройки НЕ сбрасываются на «всё выключено»,
    // файл не переименовывается и поверх него ничего не пишется, пока он не прочитается.
    private static bool _readFailed;

    /// <summary>Только для тестов: другая папка вместо %LOCALAPPDATA%\NurMarketKassa.</summary>
    public static string? DataDirOverride { get; set; }

    public static event Action? Changed;

    internal static string Folder => DataDirOverride ?? Path.Combine(AppPaths.Local, "NurMarketKassa");

    private static string FilePath => Path.Combine(Folder, "antifraud-settings.json");

    public static AntiFraudData Current
    {
        get
        {
            lock (Gate)
            {
                var stamp = StampOf(FilePath);
                if (_current is null || stamp != _readStamp || _readFailed)
                {
                    var fresh = Read();
                    if (!_readFailed || _current is null)
                        _current = fresh; // сбой чтения и есть прежние настройки — остаются они (Л5-10)
                    if (!_readFailed)
                        _readStamp = stamp;
                }
                return _current;
            }
        }
    }

    public static void Update(Action<AntiFraudData> change)
    {
        var switchedOff = new List<string>(); // 2026-11-06: В3 — какие защиты этим изменением выключены (пишется в журнал антифрода после записи файла)
        lock (Gate)
        {
            var data = Current;
            if (_readFailed)
            {
                // 2026-11-06 (Л5-10): файл не прочитан — записать «изменённые умолчания» поверх настоящих правил защиты нельзя.
                PosLogger.Log("Антифрод: файл настроек сейчас не читается — изменение не записано, повторите позже.", "WARNING");
                return;
            }
            var guardWas = data.Guard.Enabled;
            var fraudWas = data.Fraud.Enabled;
            change(data);
            data.Normalize();
            if (guardWas && !data.Guard.Enabled)
                switchedOff.Add(FraudMonitor.ProtectionGuard);
            if (fraudWas && !data.Fraud.Enabled)
                switchedOff.Add(FraudMonitor.ProtectionFraud);
            try
            {
                Directory.CreateDirectory(Folder);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(data, Json));
                _readStamp = StampOf(FilePath);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Антифрод: настройки не записаны ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            }
        }
        foreach (var protection in switchedOff)
            FraudMonitor.OnProtectionOff(protection);
        try { Changed?.Invoke(); } catch { /* подписчик окна — не повод ломать запись */ }
    }

    /// <summary>Тесты: сбросить кеш.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _current = null;
            _readStamp = default;
        }
    }

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception) { return default; }
    }

    private static AntiFraudData Read()
    {
        _readFailed = false;
        try
        {
            if (File.Exists(FilePath))
            {
                var r = AtomicFile.TryRead(FilePath, attempts: 2, pauseMs: 50);
                if (r.Status == AtomicFile.ReadStatus.Unreadable)
                {
                    // 2026-11-06 (аудит №5, Л5-10): файл занят/недоступен — это не «правил нет». Берём прежние настройки; если их нет — копию .bak (прошлая запись);
                    // и только если нет ничего — безопасный режим (PIN старшего включён), а не «всё выключено».
                    _readFailed = true;
                    if (_current is not null)
                        return _current;
                    PosLogger.Log("Антифрод: файл настроек занят и не прочитан — взяты прежние правила (копия .bak) или безопасный режим.", "WARNING");
                    if (r.Text is { } bakText && JsonSerializer.Deserialize<AntiFraudData>(bakText, Json) is { } fromBak)
                    {
                        fromBak.Normalize();
                        return fromBak;
                    }
                    return SafeDefaults();
                }
                var data = JsonSerializer.Deserialize<AntiFraudData>(r.Text ?? "{}", Json) ?? new AntiFraudData();
                data.Normalize();
                return data;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Антифрод: файл настроек не прочитан ({ex.GetType().Name}) — взяты умолчания (всё выключено).", "WARNING");
        }
        var fresh = new AntiFraudData();
        fresh.Normalize();
        return fresh;
    }

    /// <summary>2026-11-06 (аудит №5, Л5-10): настройки при сбое чтения, когда прежних нет вовсе: PIN старшего включён (скидка, возврат, недостача по умолчанию).</summary>
    internal static AntiFraudData SafeDefaults()
    {
        var safe = new AntiFraudData();
        safe.Guard.Enabled = true;
        safe.Normalize();
        return safe;
    }
}
