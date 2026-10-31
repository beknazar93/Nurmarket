using System.Globalization;
using System.Text;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28: бот на новых адресах NurCRM (docs/BACKEND_API.md). Отдельной частью класса, чтобы
/// старый путь в TelegramBotPollingService.cs остался как был и работал запасным:
///
/// • /dolgi, /dolg и рассылка напоминаний — сводка должников одним запросом
///   (GET clients/debtors/, BE-03) вместо сбора из pos/sales/?status=debt по страницам;
/// • «Старт» по персональной ссылке — chat_id сохраняется и в карточке клиента на сервере
///   (PATCH clients/{id}/ telegram_chat_id, BE-04), а напоминание берёт chat_id с сервера: клиент,
///   подписавшийся через одну кассу, получает напоминания и от другой;
/// • /ostatki — товары с малым остатком фильтром сервера (quantity_lte, is_service=false, BE-05)
///   вместо локального каталога этой кассы.
///
/// Если сервер эти адреса не знает или не ответил — бот молча берёт старый путь.
/// </summary>
public sealed partial class TelegramBotPollingService
{
    /// <summary>Порог «заканчивается» для /ostatki. 2026-10-03: был 3, а склад и аналитика считают
    /// «мало» ниже 10 — отчёты не сходились («22 в одном, 11 в другом»). Теперь один порог на всё.</summary>
    private const double LowStockThreshold = NurMarketKassa.Models.Pos.CatalogProductTileVm.LowStockQuantityThreshold;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>2026-09-29 (стресс-тест бота): последняя сводка должников. /dolg может прислать любой,
    /// кто знает имя бота (оно в ссылке на чеке), и каждый такой запрос тянул с NurCRM весь список
    /// должников. Сотня сообщений — сотни запросов тем же пользователем, что и касса: сервер отвечал
    /// 429 уже на оплату. Теперь покупателям — копия не старше минуты, владельцу — не старше 5 с.</summary>
    private readonly SemaphoreSlim _debtorsGate = new(1, 1);
    private List<DebtorInfo>? _debtorsCache;
    private DateTime _debtorsCachedAtUtc;

    private static readonly TimeSpan DebtorsCacheForClients = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DebtorsCacheForOwner = TimeSpan.FromSeconds(5);

    /// <summary>2026-10-21, «Нур Советник до 10» (О2): точный список должников программы владельца (тот же расчёт, что «Сводка» и
    /// «Долги клиентов»: DebtsView/OwnerShopFacts) — подставляет приложение. null — не задан или не успел (тогда как раньше).</summary>
    public static Func<CancellationToken, Task<List<DebtorInfo>?>>? ExactDebtorsProvider { get; set; }

    private static async Task<List<DebtorInfo>?> TryExactDebtorsAsync(CancellationToken ct)
    {
        if (ExactDebtorsProvider is not { } provider)
            return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            return await provider(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"Телеграм-бот: точный список долгов не получен ({ex.GetType().Name}) — по сводке сервера.", "WARNING");
            return null;
        }
    }

    /// <summary>Суммы — из точного расчёта, телефон и чат Telegram — из сводки сервера (по id клиента).</summary>
    private static List<DebtorInfo> MergeExactDebtors(IReadOnlyList<DebtorInfo> exact, IReadOnlyList<DebtorInfo> server)
    {
        var byId = server.Where(s => !string.IsNullOrWhiteSpace(s.ClientId)).GroupBy(s => s.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return exact.Select(e => byId.TryGetValue(e.ClientId, out var s)
            ? e with { Phone = string.IsNullOrWhiteSpace(e.Phone) ? s.Phone : e.Phone, TelegramChatId = e.TelegramChatId ?? s.TelegramChatId }
            : e).ToList();
    }

    /// <summary>Сводка должников с сервера или null, если её получить не удалось (тогда — старый путь).</summary>
    private async Task<List<DebtorInfo>?> TryLoadServerDebtorsAsync(CancellationToken ct, TimeSpan? maxAge = null)
    {
        if (_debtsApi == null)
            return null;
        var age = maxAge ?? DebtorsCacheForOwner;
        if (_debtorsCache is { } fresh && DateTime.UtcNow - _debtorsCachedAtUtc <= age)
            return fresh;
        try
        {
            // Один запрос на всех: пока сводка грузится, остальные ждут её же, а не шлют свои.
            await _debtorsGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_debtorsCache is { } loaded && DateTime.UtcNow - _debtorsCachedAtUtc <= age)
                    return loaded;
                var list = await _debtsApi.GetDebtorsAsync(0, ct).ConfigureAwait(false);
                _debtorsCache = list;
                _debtorsCachedAtUtc = DateTime.UtcNow;
                return list;
            }
            finally
            {
                _debtorsGate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: сводка должников с сервера не получена, беру старый путь ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Отчёт /dolgi по сводке сервера. Тот же вид, что у старого отчёта: сумма, число
    /// чеков и готовая ссылка WhatsApp; подписанность на бота — по серверу ИЛИ по этой кассе.</summary>
    internal static string BuildDebtorsReportFromServer(IReadOnlyList<DebtorInfo> debtors, Func<string, string?> localChatLookup)
    {
        var owing = debtors.Where(d => d.DebtTotal > 0.005).OrderByDescending(d => d.DebtTotal).ToList();

        // 2026-10-21 (бот, идея 10): на языке владельца; в тексте для WhatsApp — название магазина (раньше без него).
        var sb = new StringBuilder();
        sb.AppendLine(TelegramBotText.T("<b>Должники</b>", "<b>Карызкорлор</b>", "<b>Debtors</b>", "<b>Borçlular</b>", "<b>Qarzdorlar</b>"));

        if (owing.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine(TelegramBotText.T("Непогашенных долгов нет.", "Төлөнбөгөн карыз жок.", "No unpaid debts.", "Ödenmemiş borç yok.", "To'lanmagan qarz yo'q."));
            return sb.ToString();
        }

        var totalText = owing.Sum(d => d.DebtTotal).ToString("N2", Ru);
        sb.AppendLine(TelegramBotText.T($"Всего: {totalText} сом у {owing.Count} чел.", $"Бардыгы: {totalText} сом, {owing.Count} киши.",
            $"Total: {totalText} som, {owing.Count} people.", $"Toplam: {totalText} som, {owing.Count} kişi.", $"Jami: {totalText} so'm, {owing.Count} kishi."));
        sb.AppendLine();
        var shop = UserPreferences.Instance.StoreName;

        foreach (var debtor in owing.Take(25))
        {
            var name = string.IsNullOrWhiteSpace(debtor.Name)
                ? TelegramBotText.T("Без имени", "Аты жок", "No name", "İsimsiz", "Ismsiz")
                : debtor.Name;
            var amountText = debtor.DebtTotal.ToString("N2", Ru);
            sb.AppendLine($"<b>{Escape(name)}</b> — {amountText} " + TelegramBotText.T($"сом ({debtor.SalesCount} чек.)", $"сом ({debtor.SalesCount} чек)",
                $"som ({debtor.SalesCount} receipts)", $"som ({debtor.SalesCount} fiş)", $"so'm ({debtor.SalesCount} chek)"));

            var chatId = !string.IsNullOrWhiteSpace(debtor.TelegramChatId)
                ? debtor.TelegramChatId
                : localChatLookup(debtor.ClientId);
            if (!string.IsNullOrWhiteSpace(chatId))
            {
                sb.AppendLine(TelegramBotText.T("  подписан на бота — напоминание уйдёт автоматически", "  ботко жазылган — эскертме өзү жөнөтүлөт",
                    "  subscribed to the bot — the reminder goes automatically", "  bota abone — hatırlatma otomatik gider", "  botga obuna — eslatma o'zi yuboriladi"));
            }
            else if (!string.IsNullOrWhiteSpace(debtor.Phone) && debtor.Phone.Any(char.IsDigit))
            {
                var digits = new string(debtor.Phone.Where(char.IsDigit).ToArray());
                var reminder = Uri.EscapeDataString(string.IsNullOrWhiteSpace(shop)
                    ? $"Здравствуйте, {name}! Напоминаем о задолженности {amountText} сом. Спасибо!"
                    : $"Здравствуйте, {name}! Магазин «{shop}» напоминает о задолженности {amountText} сом. Спасибо!");
                sb.AppendLine($"  <a href=\"https://wa.me/{digits}?text={reminder}\">" + TelegramBotText.T("написать в WhatsApp", "WhatsApp'ка жазуу",
                    "message on WhatsApp", "WhatsApp'tan yaz", "WhatsApp'da yozish") + "</a>");
            }
            else
            {
                sb.AppendLine(TelegramBotText.T("  <i>нет телефона в карточке клиента</i>", "  <i>кардардын картасында телефон жок</i>",
                    "  <i>no phone in the customer card</i>", "  <i>müşteri kartında telefon yok</i>", "  <i>mijoz kartasida telefon yo'q</i>"));
            }
        }

        if (owing.Count > 25)
            sb.AppendLine(TelegramBotText.T($"\n<i>…и ещё {owing.Count - 25} чел. Весь список — «Долги клиентов» в программе.</i>",
                $"\n<i>…жана дагы {owing.Count - 25} киши. Толук тизме — программадагы «Кардарлардын карыздары».</i>",
                $"\n<i>…and {owing.Count - 25} more. Full list — “Customer debts” in the program.</i>",
                $"\n<i>…ve {owing.Count - 25} kişi daha. Tam liste — programda «Müşteri borçları».</i>",
                $"\n<i>…va yana {owing.Count - 25} kishi. To'liq ro'yxat — dasturdagi «Mijozlar qarzlari».</i>"));

        return sb.ToString();
    }

    /// <summary>«/start &lt;id клиента&gt;»: chat_id уходит в карточку клиента на сервере, чтобы
    /// напоминание могла отправить любая касса компании, а не только та, где клиент нажал «Старт».
    /// Ошибка сервера подписку не отменяет — локальная связка уже сохранена.</summary>
    private void PushSubscriberToServer(string chatId, string clientId)
    {
        if (_debtsApi == null || !Guid.TryParse(clientId, out _))
            return;

        var api = _debtsApi;
        _ = Task.Run(async () =>
        {
            try
            {
                // 2026-09-29 (стресс-тест): очередью массовых запросов (не больше трёх сразу, с паузой,
                // и не во время «подождите» от сервера). Сотня «/start» подряд занимала все
                // соединения кассы к NurCRM, и оплата ждала в очереди за ними.
                await ApiThrottle.RunBulkAsync(() => api.SetClientTelegramChatIdAsync(clientId, chatId)).ConfigureAwait(false);
                PosLogger.Log($"Телеграм-бот: chat_id клиента {clientId} сохранён на сервере.", "TELEGRAM");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Телеграм-бот: chat_id клиента {clientId} на сервер не сохранён ({ex.Message}).", "WARNING");
            }
        });
    }

    /// <summary>Клиент, подписавшийся через ДРУГУЮ кассу: в местной базе его нет, но chat_id
    /// лежит в карточке на сервере и приходит в сводке должников.</summary>
    private async Task<string?> FindClientByChatOnServerAsync(string chatId, CancellationToken ct)
    {
        var debtors = await TryLoadServerDebtorsAsync(ct, DebtorsCacheForClients).ConfigureAwait(false);
        return debtors?.FirstOrDefault(d => string.Equals(d.TelegramChatId, chatId, StringComparison.Ordinal))?.ClientId;
    }

    /// <summary>/ostatki по серверу. null — сервер не ответил, тогда отчёт строится по каталогу кассы.</summary>
    private async Task<string?> TryBuildLowStockFromServerAsync(CancellationToken ct)
    {
        if (_debtsApi == null)
            return null;
        try
        {
            var (items, count) = await _debtsApi.GetLowStockProductsAsync(LowStockThreshold, ct: ct).ConfigureAwait(false);
            return BuildLowStockFromServer(items, count, LowStockThreshold);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: остатки с сервера не получены, беру каталог кассы ({ex.Message}).", "WARNING");
            return null;
        }
    }

    /// <summary>Тот же вид, что у TelegramReportBuilder.BuildLowStock: меньший остаток — выше.</summary>
    internal static string BuildLowStockFromServer(IReadOnlyList<JsonElement> items, int? count, double threshold, int take = 20)
    {
        var rows = items
            .Select(p => (
                Name: ClientDebtsApiService.ReadString(p, "name") ?? TelegramBotText.T("Без названия", "Аталышы жок", "No name", "İsimsiz", "Nomsiz"),
                Quantity: ClientDebtsApiService.ReadDouble(p, "quantity") ?? 0,
                Unit: ClientDebtsApiService.ReadString(p, "unit")))
            .OrderBy(r => r.Quantity)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(TelegramReportBuilder.LowStockTitle());
        sb.AppendLine();

        if (rows.Count == 0)
        {
            sb.AppendLine(TelegramReportBuilder.NoLowStockText(threshold));
            return sb.ToString();
        }

        foreach (var row in rows.Take(take))
        {
            var unit = string.IsNullOrWhiteSpace(row.Unit) ? "" : " " + Escape(row.Unit);
            sb.AppendLine($"• {Escape(row.Name)} — {row.Quantity.ToString("0.###", CultureInfo.InvariantCulture)}{unit}");
        }

        var total = Math.Max(count ?? rows.Count, rows.Count);
        if (total > take)
            sb.AppendLine(TelegramBotText.T($"\n<i>…и ещё {total - take}. Всего с остатком до {threshold:0.##}: {total}.</i>",
                $"\n<i>…жана дагы {total - take}. Калдыгы {threshold:0.##} чейин: {total}.</i>",
                $"\n<i>…and {total - take} more. Total with stock up to {threshold:0.##}: {total}.</i>",
                $"\n<i>…ve {total - take} tane daha. Stoğu {threshold:0.##} altında toplam: {total}.</i>",
                $"\n<i>…va yana {total - take}. Qoldig'i {threshold:0.##} gacha jami: {total}.</i>"));

        return sb.ToString();
    }
}
