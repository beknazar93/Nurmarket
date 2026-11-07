using System.Text.Json;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-А, функция 5): оповещения «Сообщать в Telegram, если…» — недостача при закрытии смены, крупный возврат, выручка к 18:00 ниже обычного,
// товар A-группы закончился, новый крупный долг, просрочка; вечерний итог и недельный отчёт по расписанию. Идея — Frappe Notification / Auto Email Report;
// код свой. Здесь — чистые правила, тихие часы, отсрочка до утра и учёт «уже отправлено сегодня»; сбор данных и таймер — Services/ShopAlertsRunner (программа).
// Тихие часы — как у напоминаний должникам (DebtReminderPlanner): сообщения, попавшие в тихие часы, копятся и уходят одним письмом утром.

public static class ShopAlerts
{
    private sealed class State
    {
        /// <summary>ключ → день (yyyy-MM-dd), когда уже отправляли.</summary>
        public Dictionary<string, string> Sent { get; set; } = new();
        public List<PendingAlert> Pending { get; set; } = new();
        /// <summary>2026-11-06: В3 Н9 — товары группы A, о которых уже сообщили «закончился» и которые с тех пор не появлялись на складе (пока остаток не вернётся, повтора нет).</summary>
        public List<string> OutOfStock { get; set; } = new();
    }

    public sealed class PendingAlert
    {
        public string Title { get; set; } = "";
        public string Details { get; set; } = "";
        public DateTime At { get; set; }
    }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static State? _state;

    /// <summary>Тесты: другая папка вместо данных программы.</summary>
    public static string? DataDirOverride { get; set; }

    // 2026-11-05 (ревью волн 0–3, В1/В2): состояние (очередь до утра и «уже отправляли») — ОБЩИЙ файл кассы и владельца в %LOCALAPPDATA%\NurMarketKassa,
    // как настройки. Тогда ночное оповещение, созданное кассой, уйдёт утром от любой запущенной программы, а не потеряется в её личной папке.
    private static string Folder => DataDirOverride ?? AppPaths.KassaLocal;

    private static string FilePath => Path.Combine(Folder, "shop-alerts-state.json");

    private static DateTime _stateStamp;

    // 2026-11-06: В3 Л5-22 — отметки «уже отправляли» и очередь до утра — общий файл кассы и владельца; замок внутри процесса не мешал двум программам одновременно
    // прочитать «не отправляли». Правка состояния идёт под межпроцессным замком (файл рядом) с повторным чтением файла.
    private static string LockPath => Path.Combine(Folder, "shop-alerts.lock");

    private static T Locked<T>(Func<T> action)
    {
        lock (Gate)
        {
            using var cross = CrossProcessLock.TryAcquire(LockPath, TimeSpan.FromSeconds(5)); // не дождались — работаем как раньше (редкий повтор лучше потери оповещения)
            _state = null; // другая программа могла изменить файл — читаем заново, а не из кеша
            _stateStamp = default;
            return action();
        }
    }

    // ───────────── чистые правила ─────────────

    /// <summary>Тихие часы: с QuietFromHour до QuietToHour (через полночь тоже). Равные часы — тихих часов нет.</summary>
    public static bool InQuietHours(ShopAlertRules r, DateTime now)
    {
        var h = now.Hour;
        if (r.QuietFromHour == r.QuietToHour)
            return false;
        return r.QuietFromHour > r.QuietToHour ? h >= r.QuietFromHour || h < r.QuietToHour : h >= r.QuietFromHour && h < r.QuietToHour;
    }

    /// <summary>Разница в кассе при закрытии смены: факт − ожидалось (минус — недостача). Формула та же, что у сводки смены в Telegram
    /// (TelegramBotService.BuildShiftSummary). null — нет начальной суммы или фактического пересчёта.</summary>
    public static decimal? ShiftDifference(ShiftModel shift, decimal deposits, decimal withdrawals)
    {
        if (shift.OpeningCash is not { } opening || shift.ClosingCash is not { } actual)
            return null;
        var expected = ShiftExpectedCash.WithFix(shift.ExpectedCash, shift.Id)
            ?? (shift.ExpenseTotal is not null
                ? opening + (shift.CashSales ?? 0m) - withdrawals
                : opening + (shift.CashSales ?? 0m) + deposits - withdrawals);
        return actual - expected;
    }

    /// <summary>Текст про недостачу; null — правило выключено или недостача меньше порога.</summary>
    public static string? ShiftShortageText(decimal? difference, ShopAlertRules r, string? cashier)
    {
        if (!r.Enabled || !r.ShiftShortage || difference is not { } d || d >= 0 || (double)(-d) <= r.ShiftShortageAmount)
            return null;
        return Tr.T($"При закрытии смены{(string.IsNullOrWhiteSpace(cashier) ? "" : " (" + cashier + ")")} недостача {MoneyText.Som((double)(-d))} (порог {MoneyText.Som(r.ShiftShortageAmount)}).",
            $"Смена жабылганда{(string.IsNullOrWhiteSpace(cashier) ? "" : " (" + cashier + ")")} жетишпестик {MoneyText.Som((double)(-d))} (чек {MoneyText.Som(r.ShiftShortageAmount)}).",
            $"At shift close{(string.IsNullOrWhiteSpace(cashier) ? "" : " (" + cashier + ")")} the drawer is short by {MoneyText.Som((double)(-d))} (threshold {MoneyText.Som(r.ShiftShortageAmount)}).",
            $"Vardiya kapanırken{(string.IsNullOrWhiteSpace(cashier) ? "" : " (" + cashier + ")")} kasada {MoneyText.Som((double)(-d))} eksik (eşik {MoneyText.Som(r.ShiftShortageAmount)}).",
            $"Smena yopilganda{(string.IsNullOrWhiteSpace(cashier) ? "" : " (" + cashier + ")")} kassada {MoneyText.Som((double)(-d))} kamomad (chegara {MoneyText.Som(r.ShiftShortageAmount)}).");
    }

    /// <summary>Выручка «обычного» дня недели: среднее по тем же дням недели за последние 4 недели (дни без выручки не считаются; нужно не меньше 2 таких дней). 0 — не из чего считать.</summary>
    public static double TypicalDayRevenue(IEnumerable<(DateTime Day, double Revenue)> byDay, DateTime today)
    {
        var from = today.Date.AddDays(-28);
        var same = byDay.Where(d => d.Day.Date >= from && d.Day.Date < today.Date && d.Day.DayOfWeek == today.DayOfWeek && d.Revenue > 0).Select(d => d.Revenue).ToList();
        return same.Count >= 2 ? same.Average() : 0;
    }

    /// <summary>Выручка сегодня к часу проверки заметно ниже обычной: ниже (обычная × доля к этому часу) на заданный процент.</summary>
    public static bool RevenueBehind(double revenueToday, double typicalDay, ShopAlertRules r) =>
        r.Enabled && r.RevenueBehindPlan && typicalDay > 0
        && revenueToday < typicalDay * (r.RevenueShareByCheck / 100.0) * (1 - r.RevenueBehindPercent / 100.0);

    // ───────────── отправка, тихие часы, «уже отправляли» ─────────────

    /// <summary>Отправляли ли уже сегодня по этому ключу (ничего не отмечает).</summary>
    public static bool WasSentToday(string key, DateTime now)
    {
        lock (Gate)
            return Read().Sent.TryGetValue(key, out var day) && day == now.ToString("yyyy-MM-dd");
    }

    /// <summary>true — этот ключ сегодня ещё не отмечен (и отмечает его). Старые отметки (старше 3 суток) вычищаются.</summary>
    public static bool TryMarkSentToday(string key, DateTime now) => Locked(() =>
    {
        var s = Read();
        var today = now.ToString("yyyy-MM-dd");
        if (s.Sent.TryGetValue(key, out var day) && day == today)
            return false;
        s.Sent[key] = today;
        var limit = now.Date.AddDays(-3).ToString("yyyy-MM-dd");
        foreach (var old in s.Sent.Where(kv => string.CompareOrdinal(kv.Value, limit) < 0).Select(kv => kv.Key).ToList())
            s.Sent.Remove(old);
        Write(s);
        return true;
    });

    /// <summary>2026-11-06: В3 Н9 — какие из закончившихся сейчас товаров группы A ещё НЕ сообщали. Раньше ключ «class-a|имя» жил сутки, и тот же товар приходил в оповещениях каждый день,
    /// пока его не завезут. Теперь помним «сообщали, пока не появился»: возвращаются только товары, которых нет в памяти; из памяти уходят те, что больше не в списке «закончились»
    /// (остаток вернулся) — при следующем окончании сообщим снова. Новые отмечаются сразу.</summary>
    /// <param name="currentlyOut">Названия закончившихся сейчас товаров группы A.</param>
    /// <param name="listIsComplete">false — список остатков не получен целиком (память не трогаем, чтобы сбой не «забыл» товары).</param>
    public static List<string> NewlyOutOfStock(IReadOnlyCollection<string> currentlyOut, bool listIsComplete = true) => Locked(() =>
    {
        var s = Read();
        var known = new HashSet<string>(s.OutOfStock, StringComparer.CurrentCultureIgnoreCase);
        var fresh = currentlyOut.Where(n => !known.Contains(n)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (listIsComplete)
        {
            var stillOut = new HashSet<string>(currentlyOut, StringComparer.CurrentCultureIgnoreCase);
            s.OutOfStock = s.OutOfStock.Where(stillOut.Contains).Concat(fresh).Take(500).ToList();
            Write(s);
        }
        else if (fresh.Count > 0)
        {
            s.OutOfStock = s.OutOfStock.Concat(fresh).Take(500).ToList();
            Write(s);
        }
        return fresh;
    });

    /// <summary>Отметка «уже отправляли» без проверки времени — для событий с уникальным ключом (чек, смена).</summary>
    public static bool TryMarkEvent(string key, DateTime now) => TryMarkSentToday(key, now);

    /// <summary>Отправить оповещение владельцу: вне тихих часов — сразу, в тихие — в очередь до утра. Бот не подключён или оповещения выключены — ничего.
    /// 2026-11-06 (аудит №5, Л5-07): если Telegram недоступен (ошибка отправки или сбой связи) — сообщение ставится в ту же очередь Pending и уходит позже
    /// (<see cref="FlushPendingAsync"/> по таймеру), а не теряется.</summary>
    public static Task NotifyAsync(string title, string details, DateTime? now = null, bool ignoreQuiet = false) =>
        NotifyCoreAsync(ShopAutomationSettings.Current.Alerts, TelegramBotService.IsConfigured, title, details, now ?? DateTime.Now, ignoreQuiet,
            text => TelegramBotService.SendAsync(text));

    /// <summary>Ядро <see cref="NotifyAsync"/> с подставляемой отправкой (тесты проверяют очередь без настоящего Telegram). send возвращает текст ошибки или null.</summary>
    public static async Task NotifyCoreAsync(ShopAlertRules rules, bool botConfigured, string title, string details, DateTime at, bool ignoreQuiet, Func<string, Task<string?>> send)
    {
        if (!rules.Enabled || !botConfigured)
            return;
        if (!ignoreQuiet && InQuietHours(rules, at))
        {
            EnqueuePending(title, details, at);
            return;
        }
        string? error;
        try
        {
            error = await send(TelegramBotService.BuildAlert(title, details)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.GetType().Name;
        }
        if (error != null)
        {
            EnqueuePending(title, details, at);
            PosLogger.Log($"Оповещение «{title}» не ушло в Telegram ({error}) — поставлено в очередь, будет отправлено позже.", "WARNING");
        }
    }

    private static void EnqueuePending(string title, string details, DateTime at) => Locked(() =>
    {
        var s = Read();
        s.Pending.Add(new PendingAlert { Title = title, Details = details, At = at });
        if (s.Pending.Count > 50)
            s.Pending.RemoveRange(0, s.Pending.Count - 50);
        Write(s);
        return true;
    });

    /// <summary>Отправить накопленные за тихие часы оповещения одним сообщением (когда тихие часы кончились).</summary>
    public static async Task FlushPendingAsync(DateTime? now = null)
    {
        var rules = ShopAutomationSettings.Current.Alerts;
        var at = now ?? DateTime.Now;
        if (!rules.Enabled || !TelegramBotService.IsConfigured || InQuietHours(rules, at))
            return;
        // 2026-11-06: В3 Л5-22 — очередь забирается под межпроцессным замком: вторая программа, пришедшая за ней же, увидит пустую очередь и не отправит те же сообщения второй раз.
        var list = Locked(() =>
        {
            var s = Read();
            if (s.Pending.Count == 0)
                return new List<PendingAlert>();
            var taken = s.Pending.ToList();
            s.Pending.Clear();
            Write(s);
            return taken;
        });
        if (list.Count == 0)
            return;
        // 2026-11-05 (С10): если Telegram недоступен — ночные сообщения возвращаются в очередь, а не пропадают.
        void PutBack() => Locked(() =>
        {
            var s = Read();
            s.Pending.InsertRange(0, list);
            if (s.Pending.Count > 50)
                s.Pending.RemoveRange(0, s.Pending.Count - 50);
            Write(s);
            return true;
        });
        var title = Tr.T($"Оповещения за ночь: {list.Count}", $"Түнкү эскертүүлөр: {list.Count}", $"Overnight alerts: {list.Count}", $"Gece uyarıları: {list.Count}", $"Tungi ogohlantirishlar: {list.Count}");
        var body = string.Join("\n\n", list.Select(p => $"{p.At:dd.MM HH:mm} · {p.Title}\n{p.Details}"));
        string? error;
        try
        {
            error = await TelegramBotService.SendAsync(TelegramBotService.BuildAlert(title, body)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.GetType().Name;
        }
        if (error != null)
        {
            PosLogger.Log($"Накопленные оповещения не ушли в Telegram ({error}) — остались в очереди.", "WARNING");
            PutBack();
        }
    }

    /// <summary>Сколько оповещений ждёт утра.</summary>
    public static int PendingCount
    {
        get
        {
            lock (Gate)
                return Read().Pending.Count;
        }
    }

    /// <summary>Смена закрыта (вызывается кассой): недостача больше порога → оповещение. Ничего не бросает.</summary>
    public static async Task OnShiftClosedAsync(ShiftModel shift, decimal deposits, decimal withdrawals)
    {
        try
        {
            var rules = ShopAutomationSettings.Current.Alerts;
            if (!rules.Enabled || !rules.ShiftShortage)
                return;
            var text = ShiftShortageText(ShiftDifference(shift, deposits, withdrawals), rules, shift.Cashier);
            if (text is null || !TryMarkEvent("shift-short|" + shift.Id, DateTime.Now))
                return;
            await NotifyAsync(Tr.T("Недостача при закрытии смены", "Смена жабылганда жетишпестик", "Shortage at shift close", "Vardiya kapanışında eksik", "Smena yopilishida kamomad"), text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оповещение о недостаче не отправлено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    // ───────────── файл состояния ─────────────

    private static DateTime StampOf(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default; }
        catch (Exception) { return default; }
    }

    private static State Read()
    {
        // Файл общий для двух программ: перечитываем, если его изменила другая (по отметке времени).
        var stamp = StampOf(FilePath);
        if (_state is not null && stamp == _stateStamp)
            return _state;
        _stateStamp = stamp;
        try
        {
            if (File.Exists(FilePath))
                return _state = JsonSerializer.Deserialize<State>(File.ReadAllText(FilePath), Json) ?? new State();
            if (DataDirOverride is null && MigrateLegacy() is { } migrated)
                return _state = migrated;
        }
        catch (Exception)
        {
            // испорченный файл — начинаем с пустого (в нём только «уже отправляли» и очередь до утра)
        }
        return _state = new State();
    }

    /// <summary>2026-11-05: старые личные файлы кассы и владельца (%APPDATA%\NurMarketKassa|NurMarketOwner) → общий: очередь и отметки сливаются, ничего не теряется.</summary>
    private static State? MigrateLegacy()
    {
        State? merged = null;
        foreach (var folder in new[] { "NurMarketKassa", "NurMarketOwner" })
        {
            try
            {
                var old = Path.Combine(AppPaths.Roaming, folder, "shop-alerts-state.json");
                if (!File.Exists(old) || JsonSerializer.Deserialize<State>(File.ReadAllText(old), Json) is not { } st)
                    continue;
                merged ??= new State();
                foreach (var kv in st.Sent)
                    if (!merged.Sent.TryGetValue(kv.Key, out var d) || string.CompareOrdinal(kv.Value, d) > 0)
                        merged.Sent[kv.Key] = kv.Value;
                merged.Pending.AddRange(st.Pending);
            }
            catch (Exception)
            {
                // испорченный старый файл пропускаем
            }
        }
        if (merged is null)
            return null;
        merged.Pending = merged.Pending.OrderBy(p => p.At).TakeLast(50).ToList();
        try
        {
            Directory.CreateDirectory(Folder);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(merged, Json));
        }
        catch (Exception)
        {
            // не записали сейчас — запишется при первой правке состояния
        }
        return merged;
    }

    private static void Write(State s)
    {
        _state = s;
        try
        {
            Directory.CreateDirectory(Folder);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json));
            _stateStamp = StampOf(FilePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Состояние оповещений не записано ({ex.GetType().Name}).", "WARNING");
        }
    }

    /// <summary>Тесты: сбросить кеш.</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            _state = null;
            _stateStamp = default;
        }
    }
}
