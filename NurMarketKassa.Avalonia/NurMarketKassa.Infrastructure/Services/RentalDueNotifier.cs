using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-02, владелец: «в админке добавь уведомление об окончании и приближении срока аренды
/// проката, и в боте тоже».
///
/// Раз в час (первый раз — через 20 с после запуска) берёт с сервера прокаты на руках и делит их:
/// просрочен / вернуть сегодня / вернуть завтра. Итог — событием <see cref="Changed"/> (значок и
/// всплывающее уведомление в программе владельца) и сообщением владельцу в Телеграм.
///
/// Масштаб (15 000 магазинов × 2 программы): опрос только в сферах «Одежда» и «Услуги», раз в час,
/// не во время паузы 429; на 403/404 — до перезапуска не спрашиваем. Это ≤ 2 запроса в час на программу.
///
/// Телеграм — не чаще одного раза на событие: «завтра» и «сегодня» — по разу, «просрочен» — раз в день,
/// только с 8:00 до 21:00. Что уже отправлено, помнит общий файл в %LOCALAPPDATA%\NurMarketKassa —
/// касса и программа владельца на одном компьютере не дублируют друг друга (замок — именованный мьютекс).
/// Бот на сервере NurCRM таких напоминаний пока не шлёт (ТЗ часть 7, п. 4.5) — шлёт программа.</summary>
public static class RentalDueNotifier
{
    public enum DueKind { None, Tomorrow, Today, Overdue }

    public sealed record Summary(IReadOnlyList<RentalDto> Overdue, IReadOnlyList<RentalDto> Today, IReadOnlyList<RentalDto> Tomorrow)
    {
        public int Count => Overdue.Count + Today.Count + Tomorrow.Count;
    }

    /// <summary>Новый итог проверки (с фонового потока).</summary>
    public static event Action<Summary>? Changed;

    public static Summary? Last { get; private set; }

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly string SentFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurMarketKassa", "rental-alerts.json");
    private static readonly object Gate = new();
    private static Func<string?, CancellationToken, Task<IReadOnlyList<RentalDto>>>? _loader;
    private static CancellationTokenSource? _cts;
    private static bool _unavailable;

    /// <summary>Срок проката относительно сегодняшнего дня.</summary>
    public static DueKind Kind(RentalDto r, DateTime? today = null)
    {
        if (!r.IsActive)
            return DueKind.None;
        if (r.IsOverdue)
            return DueKind.Overdue;
        if (r.DateTo is not { } to)
            return DueKind.None;
        var d = today ?? DateTime.Today;
        if (to.Date < d)
            return DueKind.Overdue;
        if (to.Date == d)
            return DueKind.Today;
        return to.Date == d.AddDays(1) ? DueKind.Tomorrow : DueKind.None;
    }

    public static Summary Classify(IEnumerable<RentalDto> rentals)
    {
        var list = rentals.Where(r => r.IsActive).GroupBy(r => r.Id).Select(g => g.First()).ToList();
        return new Summary(
            list.Where(r => Kind(r) == DueKind.Overdue).OrderBy(r => r.DateTo).ToList(),
            list.Where(r => Kind(r) == DueKind.Today).OrderBy(r => r.Number).ToList(),
            list.Where(r => Kind(r) == DueKind.Tomorrow).OrderBy(r => r.Number).ToList());
    }

    /// <summary>Запускает проверку (повторный вызов ничего не делает). loader — RentalsApi.ListAsync.</summary>
    public static void Start(Func<string?, CancellationToken, Task<IReadOnlyList<RentalDto>>> loader)
    {
        lock (Gate)
        {
            if (_cts != null)
                return;
            _loader = loader;
            _cts = new CancellationTokenSource();
        }
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                while (!ct.IsCancellationRequested)
                {
                    await CheckAsync(ct).ConfigureAwait(false);
                    await Task.Delay(Interval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>Свежий список уже загружен (окно «Прокат» после выдачи или возврата) — обновить значок
    /// и карточку сразу, без лишнего запроса и без Телеграма.</summary>
    public static void Publish(IEnumerable<RentalDto> active)
    {
        var summary = Classify(active);
        Last = summary;
        try
        {
            Changed?.Invoke(summary);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат: уведомление не показано ({ex.Message}).", "WARNING");
        }
    }

    private static async Task CheckAsync(CancellationToken ct)
    {
        if (_loader is null || _unavailable || !(MarketSpheres.IsClothing || MarketSpheres.IsServices))
            return;
        if (ApiThrottle.RemainingBlock > TimeSpan.Zero)
            return;
        Summary summary;
        try
        {
            var active = await _loader("active", ct).ConfigureAwait(false);
            var overdue = await _loader("overdue", ct).ConfigureAwait(false);
            summary = Classify(active.Concat(overdue));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ApiException ex) when (ex.StatusCode is 403 or 404)
        {
            _unavailable = true;
            PosLogger.Log($"Прокат: сроки не проверяются ({ex.StatusCode}) — до перезапуска.", "RENTAL");
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат: сроки не проверены ({ex.Message}).", "DEBUG");
            return;
        }

        Last = summary;
        try
        {
            Changed?.Invoke(summary);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат: уведомление не показано ({ex.Message}).", "WARNING");
        }
        await SendTelegramAsync(summary, ct).ConfigureAwait(false);
    }

    /// <summary>Ключ события: «просрочен» повторяется раз в день, остальное — один раз.</summary>
    private static string Key(RentalDto r, DueKind kind) =>
        kind == DueKind.Overdue ? $"{r.Id}:overdue:{DateTime.Today:yyyy-MM-dd}" : $"{r.Id}:{kind}:{r.DateTo:yyyy-MM-dd}";

    private static async Task SendTelegramAsync(Summary summary, CancellationToken ct)
    {
        if (summary.Count == 0 || !TelegramBotService.IsConfigured || ServerTelegramBotApi.LastKnownRentalReminders)
            return;
        var hour = DateTime.Now.Hour;
        if (hour < 8 || hour >= 21)
            return;

        // Касса и программа владельца на одном ПК: отправляет та, что первой взяла замок.
        // 2026-10-03, журнал: «Object synchronization method was called from an unsynchronized block of code» —
        // Mutex привязан к потоку, а после await отправки в Телеграм ReleaseMutex шёл с другого потока.
        // Именованный семафор между процессами к потоку не привязан.
        using var gate = new Semaphore(1, 1, @"Local\NurMarketRentalAlerts");
        var owned = false;
        try
        {
            owned = gate.WaitOne(TimeSpan.FromSeconds(30));
            if (!owned)
                return;

            var sent = ReadSent();
            var fresh = new List<(RentalDto R, DueKind K)>();
            foreach (var (list, kind) in new[] { (summary.Overdue, DueKind.Overdue), (summary.Today, DueKind.Today), (summary.Tomorrow, DueKind.Tomorrow) })
                fresh.AddRange(list.Where(r => !sent.ContainsKey(Key(r, kind))).Select(r => (r, kind)));
            if (fresh.Count == 0)
                return;

            var error = await TelegramBotService.SendAsync(BuildTelegram(fresh), ct).ConfigureAwait(false);
            if (error != null)
            {
                PosLogger.Log($"Прокат: напоминание в Телеграм не ушло ({error}).", "WARNING");
                return;
            }
            foreach (var (r, k) in fresh)
                sent[Key(r, k)] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            // Старше двух недель — не нужно.
            var cutoff = DateTime.Today.AddDays(-14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (var old in sent.Where(p => string.CompareOrdinal(p.Value, cutoff) < 0).Select(p => p.Key).ToList())
                sent.Remove(old);
            WriteSent(sent);
            PosLogger.Log($"Прокат: напоминание в Телеграм отправлено ({fresh.Count}).", "RENTAL");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат: напоминание в Телеграм — ошибка ({ex.Message}).", "WARNING");
        }
        finally
        {
            if (owned)
                gate.Release();
        }
    }

    /// <summary>Текст для владельца: сначала просрочки, затем «сегодня», затем «завтра».</summary>
    public static string BuildTelegram(IReadOnlyList<(RentalDto R, DueKind K)> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("⏰ <b>" + Esc(Tr.T("Прокат: сроки возврата", "Прокат: кайтаруу мөөнөтү", "Rentals: return dates", "Kiralama: iade tarihleri", "Prokat: qaytarish muddati")) + "</b>");
        void Block(DueKind kind, string title)
        {
            var part = items.Where(x => x.K == kind).Select(x => x.R).ToList();
            if (part.Count == 0)
                return;
            sb.AppendLine();
            sb.AppendLine("<b>" + Esc(title) + "</b>");
            foreach (var r in part.Take(15))
                sb.AppendLine(Line(r, kind));
            if (part.Count > 15)
                sb.AppendLine(Esc(Tr.T($"…и ещё {part.Count - 15}", $"…жана дагы {part.Count - 15}", $"…and {part.Count - 15} more", $"…ve {part.Count - 15} tane daha", $"…va yana {part.Count - 15}")));
        }
        Block(DueKind.Overdue, Tr.T("❗ Просрочены", "❗ Мөөнөтү өттү", "❗ Overdue", "❗ Gecikmiş", "❗ Muddati o'tgan"));
        Block(DueKind.Today, Tr.T("Вернуть сегодня", "Бүгүн кайтаруу керек", "Due today", "Bugün iade", "Bugun qaytarish"));
        Block(DueKind.Tomorrow, Tr.T("Вернуть завтра", "Эртең кайтаруу керек", "Due tomorrow", "Yarın iade", "Ertaga qaytarish"));
        return sb.ToString().TrimEnd();
    }

    private static string Line(RentalDto r, DueKind kind)
    {
        var items = string.Join(", ", r.Items.Select(i => i.Label));
        var days = r.DateTo is { } to ? Math.Max(1, (DateTime.Today - to.Date).Days) : 1;
        var when = kind == DueKind.Overdue
            ? Tr.T($"просрочен на {days} дн.", $"{days} күн кечикти", $"{days} d overdue", $"{days} gün gecikti", $"{days} kun kechikdi")
            : Tr.T($"до {r.DateTo:dd.MM}", $"{r.DateTo:dd.MM} чейин", $"by {r.DateTo:dd.MM}", $"{r.DateTo:dd.MM} tarihine kadar", $"{r.DateTo:dd.MM} gacha");
        var deposit = r.IsDocumentDeposit
            ? Tr.T("залог: документ", "күрөө: документ", "deposit: document", "depozito: belge", "garov: hujjat")
            : r.DepositAmount > 0
                ? Tr.T($"залог {r.DepositAmount.ToString("N0", Ru)} сом", $"күрөө {r.DepositAmount.ToString("N0", Ru)} сом", $"deposit {r.DepositAmount.ToString("N0", Ru)} som", $"depozito {r.DepositAmount.ToString("N0", Ru)} som", $"garov {r.DepositAmount.ToString("N0", Ru)} so'm")
                : "";
        return Esc($"• №{r.Number} {r.ClientName} — {items}, {when}" + (deposit.Length > 0 ? " · " + deposit : ""));
    }

    private static string Esc(string? text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static Dictionary<string, string> ReadSent()
    {
        try
        {
            if (File.Exists(SentFile))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SentFile)) ?? new();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Прокат: файл отправленных напоминаний не прочитан ({ex.Message}).", "WARNING");
        }
        return new();
    }

    private static void WriteSent(Dictionary<string, string> sent)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SentFile)!);
        var tmp = SentFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(sent));
        File.Move(tmp, SentFile, true);
    }
}
