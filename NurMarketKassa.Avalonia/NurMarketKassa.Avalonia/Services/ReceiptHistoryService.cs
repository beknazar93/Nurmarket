using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Период «Истории чеков».</summary>
public enum ReceiptHistoryPeriod
{
    CurrentShift,
    Today,
    Yesterday,
}

/// <summary>Один чек в «Истории чеков»: либо продажа с сервера, либо запись офлайн-очереди
/// этой кассы (<see cref="OfflinePendingSalesStore"/>), которая ещё не ушла на сервер.</summary>
public sealed class ReceiptHistoryEntry
{
    /// <summary>id продажи на сервере или id записи офлайн-очереди.</summary>
    public string Id { get; init; } = "";

    /// <summary>Запись из офлайн-очереди этой кассы, а не продажа с сервера.</summary>
    public OfflineSaleEntry? Local { get; init; }

    public bool IsLocal => Local != null;

    public DateTime CreatedAt { get; init; }

    /// <summary>Номер чека так, как его показывает список: настоящий номер с сервера или,
    /// если его нет, порядковый «№N» за период (как в «Продажах»).
    /// 2026-09-28: настоящий номер — постоянный sale.number сервера (BE-08), шесть цифр, как на
    /// печатном чеке (SalesWindow.TryReceiptNumber: 1115 → «001115»).</summary>
    public string ReceiptNumber { get; set; } = "";

    public decimal Total { get; init; }

    public string PaymentMethod { get; init; } = "";

    /// <summary>Статус продажи на сервере: paid, debt, partially_returned, canceled. У записей
    /// офлайн-очереди пусто.</summary>
    public string Status { get; init; } = "";

    public string? Cashier { get; init; }

    /// <summary>Первый товар чека — для строки списка.</summary>
    public string FirstItemName { get; init; } = "";

    /// <summary>Номер, товары — всё, по чему ищет строка поиска. Дополняется, когда кассир
    /// открывает состав чека.</summary>
    public string SearchText { get; set; } = "";

    /// <summary>Чек ждёт отправки на сервер (или отправляется прямо сейчас).</summary>
    public bool IsPendingUpload => Local is { IsAutonomous: false } local
        && !string.Equals(local.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase);

    /// <summary>Сервер отклонил чек — повторить можно в «Некорректных чеках».</summary>
    public bool IsFailedUpload => Local is { IsAutonomous: false } local
        && string.Equals(local.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Результат загрузки «Истории чеков».</summary>
public sealed class ReceiptHistoryResult
{
    public List<ReceiptHistoryEntry> Entries { get; } = new();

    /// <summary>Сервер не ответил — в списке только чеки офлайн-очереди этой кассы.</summary>
    public bool ServerUnavailable { get; set; }

    public string? ServerError { get; set; }

    /// <summary>Выбрана «Эта смена», но смена не открыта — показаны чеки этой кассы за сегодня.</summary>
    public bool NoOpenShift { get; set; }
}

/// <summary>«История чеков» и повторная печать для кассира (2026-09-27, просьба владельца:
/// «как кассир может повторный чек пробить вдруг!!! это добавь и ещё история чеков»).
///
/// После разделения программ повторная печать осталась только в разделе владельца «Продажи»,
/// который на тарифе «Стандарт» и выше в кассе скрыт — кассир остался без неё совсем.
///
/// Данные — те же, что у «Продаж» и отчёта смены: список продаж за даты с сервера, отобранный
/// по смене и кассе на стороне кассы (сервер фильтр по смене молча игнорирует, см.
/// <see cref="ShiftReportData"/>), плюс чеки офлайн-очереди, которые ещё не ушли на сервер.
/// Печать — тем же построителем, что и «Продажи» (<see cref="SaleReceiptTextBuilder"/>), а для
/// чека из офлайн-очереди — тем, которым его напечатала сама оплата
/// (<see cref="CartReceiptTextBuilder"/>); на бумаге всегда отметка «(повторная печать)».</summary>
public static class ReceiptHistoryService
{
    private const int PageSize = 80;
    private const int MaxPages = 25;

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Подпись валюты на экране.</summary>
    public static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    public static string Money(decimal value) => value.ToString("N2", Ru) + " " + Som;

    /// <summary>Загружает чеки этой кассы за период: с сервера и из офлайн-очереди.</summary>
    public static async Task<ReceiptHistoryResult> LoadAsync(ReceiptHistoryPeriod period, CancellationToken ct = default)
    {
        var result = new ReceiptHistoryResult();
        var today = DateTime.Today;
        var shiftId = CurrentShiftId();

        if (period == ReceiptHistoryPeriod.CurrentShift && string.IsNullOrWhiteSpace(shiftId))
        {
            // Смены нет — «эта смена» пуста по определению. Показываем чеки кассы за сегодня,
            // а не пустой экран: кассир чаще всего ищет чек, пробитый только что.
            result.NoOpenShift = true;
            period = ReceiptHistoryPeriod.Today;
        }

        // Смена может начаться вчера (сервер закрывает смены сам только в 01:00), поэтому для
        // «этой смены» берём два дня и отбираем по номеру смены.
        var (from, toExclusive) = period switch
        {
            ReceiptHistoryPeriod.Yesterday => (today.AddDays(-1), today),
            ReceiptHistoryPeriod.Today => (today, today.AddDays(1)),
            _ => (today.AddDays(-1), today.AddDays(1)),
        };

        bool InPeriod(DateTime at) => period switch
        {
            ReceiptHistoryPeriod.Yesterday => at.Date == today.AddDays(-1),
            ReceiptHistoryPeriod.Today => at.Date == today,
            _ => at >= from && at < toExclusive,
        };

        var autonomous = IsAutonomousSession();
        var serverEntries = new List<ReceiptHistoryEntry>();

        // В автономном режиме сервера нет вообще — все чеки лежат в офлайн-очереди.
        // 2026-09-29: в аварии сервера — тоже только локальные чеки, без ожидания таймаута.
        if (!autonomous && !OfflineModeHelper.SellLocally)
        {
            try
            {
                serverEntries = await LoadServerAsync(period, shiftId, from, toExclusive, InPeriod, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Любая ошибка сервера (нет сети, тайм-аут, 5xx, 429) — не повод прятать чеки,
                // которые касса знает сама: показываем офлайн-очередь и пишем, что истории нет.
                PosLogger.Log($"История чеков: сервер недоступен ({ex.Message}) — показываю офлайн-очередь.", "SALES");
                result.ServerUnavailable = true;
                result.ServerError = ex.Message;
            }
        }

        var syncedIds = new HashSet<string>(serverEntries.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var localEntries = LoadLocal(period, shiftId, autonomous, InPeriod)
            .Where(e => string.IsNullOrWhiteSpace(e.Local!.SyncedSaleId) || !syncedIds.Contains(e.Local.SyncedSaleId!))
            .ToList();

        result.Entries.AddRange(serverEntries);
        result.Entries.AddRange(localEntries);

        // 2026-09-28: чеки с сервера уже с номером, как на сайте (см. LoadServerAsync). У чека из
        // офлайн-очереди, которого сервер ещё не видел, номера на сайте нет — «—». Без сервера
        // (нет связи, автономный режим) — как раньше: порядковые за период, по времени.
        var position = 0;
        foreach (var entry in result.Entries.OrderBy(e => e.CreatedAt))
        {
            position++;
            if (string.IsNullOrWhiteSpace(entry.ReceiptNumber))
                entry.ReceiptNumber = serverEntries.Count > 0
                    ? "—"
                    : "№" + position.ToString(CultureInfo.InvariantCulture);
            entry.SearchText = entry.ReceiptNumber + " " + entry.SearchText;
        }

        result.Entries.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return result;
    }

    private static async Task<List<ReceiptHistoryEntry>> LoadServerAsync(
        ReceiptHistoryPeriod period,
        string? shiftId,
        DateTime from,
        DateTime toExclusive,
        Func<DateTime, bool> inPeriod,
        CancellationToken ct)
    {
        var rows = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var items = await App.SalesApi
                .PosSalesListAsync(page, PageSize, null, ct, dateFrom: from, dateToExclusive: toExclusive)
                .ConfigureAwait(false);
            if (items.Count == 0)
                break;

            // Защита от сервера, который проигнорировал бы «page»: одна и та же страница по кругу.
            var added = 0;
            foreach (var row in items)
            {
                var id = PosSaleRowFormatter.TrySaleId(row);
                if (string.IsNullOrEmpty(id) || !seen.Add(id))
                    continue;
                rows.Add(row.Clone());
                added++;
            }

            if (added == 0 || items.Count < PageSize)
                break;
        }

        // 2026-09-28, сверка с сайтом: номер чека — как в «Продажах» сайта и в окне «Возврат»:
        // место продажи в общем списке компании, новые первыми («№ 1» — последняя продажа, все
        // кассы и все статусы, нумерация сквозная по страницам). Своего номера чека у сервера нет.
        // Раньше здесь был порядковый номер за период по времени (первый чек дня — №1), и одна и та
        // же продажа была №9 в «Истории чеков» и №5 в «Возврате». Сервер отдаёт список новыми
        // первыми; продажи новее конца окна (для «Вчера» — всё, что продано сегодня) считаем
        // отдельно и прибавляем.
        //
        // 2026-09-28, доработка NurCRM (BE-08): у продажи появился постоянный номер «number» — он
        // присваивается при оплате и больше не меняется (по нему же ищет «Возврат» и сайт:
        // ?number=). Его берёт SalesWindow.TryReceiptNumber ниже. Место в списке — только запас для
        // старого сервера без номера; если номер есть у всех чеков, лишние запросы (подсчёт
        // продаж после периода для «Вчера») не делаем.
        var allNumbered = rows.All(r => SalesWindow.TryReceiptNumber(r) is { Length: > 0 });
        var newerCount = !allNumbered && toExclusive <= DateTime.Today
            ? await CountServerSalesAsync(toExclusive, DateTime.Today.AddDays(1), ct).ConfigureAwait(false)
            : 0;
        var webPositions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            if (PosSaleRowFormatter.TrySaleId(rows[i]) is { Length: > 0 } rowId)
                webPositions[rowId] = newerCount + i + 1;
        }

        var cashboxId = (PosApp.PosCashboxId ?? "").Trim();
        var result = new List<ReceiptHistoryEntry>(rows.Count);
        foreach (var row in rows)
        {
            var status = (Str(row, "status") ?? "").ToLowerInvariant();

            // «new» — продажа ещё не оплачена (открытая корзина), чека по ней не было.
            if (status == "new")
                continue;

            var createdAt = SaleTime(row);
            if (createdAt is null || !inPeriod(createdAt.Value))
                continue;

            // Чеки только этой кассы. Для «этой смены» достаточно номера смены: смена открыта на
            // этой кассе, а касса у кассира может смениться посреди смены (сервер отверг прежнюю,
            // см. PosApp.RejectedCashboxIds) — по кассе такие чеки потерялись бы.
            var rowCashbox = RowCashboxId(row);
            var sameCashbox = cashboxId.Length == 0 || rowCashbox == null
                || string.Equals(rowCashbox, cashboxId, StringComparison.OrdinalIgnoreCase);
            var rowShift = RowShiftId(row);
            if (period == ReceiptHistoryPeriod.CurrentShift && rowShift != null)
            {
                if (!string.Equals(rowShift, shiftId, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else if (period == ReceiptHistoryPeriod.CurrentShift)
            {
                // Сервер не прислал смену — берём только сегодняшние чеки, где касса точно эта.
                if (createdAt.Value.Date != DateTime.Today || rowCashbox == null || !sameCashbox)
                    continue;
            }
            else if (!sameCashbox)
            {
                continue;
            }

            var id = PosSaleRowFormatter.TrySaleId(row) ?? "";
            var firstItem = Str(row, "first_item_name") ?? "";
            result.Add(new ReceiptHistoryEntry
            {
                Id = id,
                CreatedAt = createdAt.Value,
                ReceiptNumber = SalesWindow.TryReceiptNumber(row) is { Length: > 0 } serverNumber
                    ? serverNumber
                    : webPositions.TryGetValue(id, out var webPosition)
                        ? "№" + webPosition.ToString(CultureInfo.InvariantCulture)
                        : "",
                Total = RowTotal(row),
                PaymentMethod = Str(row, "payment_method") ?? "",
                Status = status,
                Cashier = CartDisplayHelper.TryCashierName(row),
                FirstItemName = firstItem,
                SearchText = firstItem,
            });
        }

        // Полный состав чеков, пробитых на этой кассе, есть в локальной истории продаж — по нему
        // поиск находит чек по любому товару, а не только по первому.
        try
        {
            var local = SoldLineItemsStore.LinesBySale(result.Select(e => e.Id).Where(id => id.Length > 0).ToList());
            foreach (var entry in result)
            {
                if (!local.TryGetValue(entry.Id, out var lines))
                    continue;
                entry.SearchText += " " + string.Join(" ", lines.Select(l => l.ProductName));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: локальный состав чеков не прочитан: {ex.Message}", "DEBUG");
        }

        return result;
    }

    /// <summary>Сколько продаж компании (все кассы, все статусы) за [from; toExclusive) — для
    /// номера чека «как на сайте» у чеков вчерашнего дня. Ошибка — 0: номер съедет, но список
    /// чеков из-за этого не пропадёт.</summary>
    private static async Task<int> CountServerSalesAsync(DateTime from, DateTime toExclusive, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var items = await App.SalesApi
                    .PosSalesListAsync(page, PageSize, null, ct, dateFrom: from, dateToExclusive: toExclusive)
                    .ConfigureAwait(false);
                var added = items.Count(row => PosSaleRowFormatter.TrySaleId(row) is { Length: > 0 } id && seen.Add(id));
                if (added == 0 || items.Count < PageSize)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"История чеков: продажи после периода не посчитаны: {ex.Message}", "SALES");
        }

        return seen.Count;
    }

    private static List<ReceiptHistoryEntry> LoadLocal(
        ReceiptHistoryPeriod period,
        string? shiftId,
        bool autonomousSession,
        Func<DateTime, bool> inPeriod)
    {
        var result = new List<ReceiptHistoryEntry>();
        List<OfflineSaleEntry> all;
        try
        {
            all = OfflinePendingSalesStore.LoadAll();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: офлайн-очередь не прочитана: {ex.Message}", "WARNING");
            return result;
        }

        foreach (var sale in all)
        {
            // Выгруженные записи удаляются из очереди (SyncService) — если какая-то ещё не
            // удалена, её чек уже есть в списке с сервера.
            if (string.Equals(sale.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase))
                continue;

            // Продажи автономного режима и обычного аккаунта не смешиваем.
            if (sale.IsAutonomous != autonomousSession)
                continue;

            var at = sale.CreatedAt.LocalDateTime;
            if (period == ReceiptHistoryPeriod.CurrentShift)
            {
                var sameShift = !string.IsNullOrWhiteSpace(sale.ShiftId)
                    ? string.Equals(sale.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase)
                    : at.Date == DateTime.Today;
                if (!sameShift)
                    continue;
            }
            else if (!inPeriod(at))
            {
                continue;
            }

            string? number = null;
            string? cashier = null;
            decimal total = 0;
            var names = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(sale.CartJson) ? "{}" : sale.CartJson);
                var root = doc.RootElement;
                number = SalesWindow.TryReceiptNumber(root);
                cashier = CartDisplayHelper.TryCashierName(root);
                total = (decimal)CartTotalsCalculator.Calculate(root).TotalDue;
                names.AddRange(CartDisplayHelper.EnumerateSaleLineItems(root).Select(CartDisplayHelper.ItemName));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"История чеков: офлайн-чек {sale.Id} не разобран: {ex.GetType().Name}", "WARNING");
            }

            // Чек этой смены пробил тот, кто сейчас за кассой.
            if (string.IsNullOrWhiteSpace(cashier)
                && !string.IsNullOrWhiteSpace(sale.ShiftId)
                && string.Equals(sale.ShiftId, shiftId, StringComparison.OrdinalIgnoreCase))
                cashier = PosApp.CurrentUserDisplayName;

            result.Add(new ReceiptHistoryEntry
            {
                Id = sale.Id,
                Local = sale,
                CreatedAt = at,
                ReceiptNumber = number ?? "",
                Total = total,
                PaymentMethod = sale.PaymentMethod ?? "",
                Cashier = cashier,
                FirstItemName = names.FirstOrDefault() ?? "",
                SearchText = string.Join(" ", names),
            });
        }

        return result;
    }

    /// <summary>Полный чек для состава и печати: с сервера — свежий (после возврата статус и
    /// строки меняются, поэтому не из кэша отчётов), из офлайн-очереди — его снимок корзины.</summary>
    /// <summary>2026-10-07, владелец (снимок: «по чекам за вчера в сводке только общая выручка… предлагаю зайти в NurMarket»):
    /// «дай ИИ полный доступ — пусть сам собирает информацию и выводит таблицей». Чеки ВСЕХ касс компании за [from; toExclusive)
    /// с сервера — для Нур Советника (OwnerSalesData). Не больше <paramref name="maxPages"/> страниц по 80; новые первыми.</summary>
    public static async Task<List<ReceiptHistoryEntry>> LoadRangeAsync(DateTime from, DateTime toExclusive, int maxPages, CancellationToken ct)
    {
        var result = new List<ReceiptHistoryEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var items = await App.SalesApi.PosSalesListAsync(page, PageSize, null, ct, dateFrom: from, dateToExclusive: toExclusive).ConfigureAwait(false);
            if (items.Count == 0)
                break;
            var added = 0;
            foreach (var row in items)
            {
                var id = PosSaleRowFormatter.TrySaleId(row) ?? "";
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                added++;
                var status = (Str(row, "status") ?? "").ToLowerInvariant();
                if (status == "new" || SaleTime(row) is not { } createdAt || createdAt < from || createdAt >= toExclusive)
                    continue;
                result.Add(new ReceiptHistoryEntry
                {
                    Id = id,
                    CreatedAt = createdAt,
                    ReceiptNumber = SalesWindow.TryReceiptNumber(row) ?? "",
                    Total = RowTotal(row),
                    PaymentMethod = Str(row, "payment_method") ?? "",
                    Status = status,
                    Cashier = CartDisplayHelper.TryCashierName(row),
                    FirstItemName = Str(row, "first_item_name") ?? "",
                });
            }
            if (added == 0 || items.Count < PageSize)
                break;
        }
        result.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
        return result;
    }

    public static async Task<JsonElement> LoadDetailAsync(ReceiptHistoryEntry entry, CancellationToken ct = default)
    {
        if (entry.Local is { } local)
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(local.CartJson) ? "{}" : local.CartJson);
            return doc.RootElement.Clone();
        }

        return await App.SalesApi.PosSaleGetAsync(entry.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Текст копии чека с отметкой «(повторная печать)».</summary>
    public static string BuildReprintText(ReceiptHistoryEntry entry, JsonElement detail)
    {
        if (entry.Local is { } local)
        {
            // Тот же построитель и та же пометка, что у чека, который напечатала сама оплата
            // (PosCheckoutService.CompleteOfflineCheckoutAsync), но с временем продажи.
            var note = local.IsAutonomous ? "АВТОНОМНЫЙ РЕЖИМ" : "ОФФЛАЙН (ожидает выгрузку)";
            return CartReceiptTextBuilder.BuildSimpleReceipt(
                local.CartJson,
                note,
                local.PaymentMethod,
                local.CashReceived,
                receiptTime: entry.CreatedAt,
                isReprint: true);
        }

        // Как повторная печать в «Продажах» (SalesWindow.PrintReceiptAgain_Click).
        return SaleReceiptTextBuilder.Build(
            detail,
            entry.ReceiptNumber.TrimStart('№').Trim(),
            SalesWindow.ReadSaleDiscount(detail),
            SalesWindow.ReadSaleTotal(detail),
            isReprint: true);
    }

    /// <summary>Печатает готовый текст чека тем же принтером, что и оплата. null — напечатано,
    /// иначе — понятная кассиру причина.</summary>
    public static async Task<string?> PrintAsync(string receiptText)
    {
        try
        {
            var printer = App.GetRequiredService<IReceiptPrinterService>();

            // Настоящий принтер молча пропускает печать, если она выключена или порт не выбран —
            // говорим кассиру, почему бумаги нет, вместо «напечатано».
            if (printer is LptReceiptPrinterService)
            {
                var prefs = UserPreferences.Instance;
                if (!prefs.ReceiptEnabled)
                    return Tr.T("Печать чеков выключена в настройках кассы.",
                        "Чек басып чыгаруу кассанын жөндөөлөрүндө өчүрүлгөн.",
                        "Receipt printing is turned off in the till settings.",
                        "Fiş yazdırma kasa ayarlarında kapalı.",
                        "Chek chop etish kassa sozlamalarida o'chirilgan.");
                if (HardwareModeHelper.IsNonePort(prefs.ReceiptDevicePath))
                    return Tr.T("Принтер чеков не выбран в настройках кассы.",
                        "Чек принтери кассанын жөндөөлөрүндө тандалган эмес.",
                        "No receipt printer is selected in the till settings.",
                        "Kasa ayarlarında fiş yazıcısı seçilmemiş.",
                        "Kassa sozlamalarida chek printeri tanlanmagan.");
            }

            // Тот же путь, что у чека при оплате: жёсткий тайм-аут, зависший принтер окно не держит.
            var printed = await printer.PrintReceiptAsync(new CartSnapshot
            {
                CartJson = "{}",
                ReceiptText = receiptText,
            }).ConfigureAwait(false);

            return printed
                ? null
                : Tr.T("Принтер не ответил — проверьте, что он включён и подключён.",
                    "Принтер жооп берген жок — күйгүзүлгөнүн жана туташканын текшериңиз.",
                    "The printer did not respond — check that it is on and connected.",
                    "Yazıcı yanıt vermedi — açık ve bağlı olduğunu kontrol edin.",
                    "Printer javob bermadi — yoqilgan va ulanganini tekshiring.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: повторная печать не удалась: {ex}", "PRINTER");
            return Tr.T("Не удалось напечатать чек: ", "Чекти басып чыгаруу мүмкүн болгон жок: ", "Could not print the receipt: ", "Fiş yazdırılamadı: ", "Chekni chop etib bo'lmadi: ") + ex.Message;
        }
    }

    private static int _printingLast;

    /// <summary>«Печать последнего чека» из меню «Ещё» корзины: последний чек этой смены (или,
    /// если смена не открыта, этой кассы за сегодня) — с сервера или из офлайн-очереди.</summary>
    /// <returns>Ok и сообщение для кассира.</returns>
    public static async Task<(bool Ok, string Message)> PrintLastReceiptAsync(CancellationToken ct = default)
    {
        // Двойное нажатие не печатает две копии.
        if (Interlocked.Exchange(ref _printingLast, 1) == 1)
            return (false, Tr.T("Последний чек уже печатается.", "Акыркы чек басылып жатат.", "The last receipt is already being printed.", "Son fiş zaten yazdırılıyor.", "Oxirgi chek allaqachon chop etilmoqda."));

        try
        {
            var history = await LoadAsync(ReceiptHistoryPeriod.CurrentShift, ct).ConfigureAwait(false);
            var last = history.Entries.OrderByDescending(e => e.CreatedAt).FirstOrDefault();
            if (last is null)
            {
                return (false, history.ServerUnavailable
                    ? Tr.T("Нет связи с сервером — последний чек не найден.",
                        "Сервер менен байланыш жок — акыркы чек табылган жок.",
                        "No connection to the server — the last receipt was not found.",
                        "Sunucuyla bağlantı yok — son fiş bulunamadı.",
                        "Server bilan aloqa yo'q — oxirgi chek topilmadi.")
                    : Tr.T("Чеков пока нет — печатать нечего.",
                        "Азырынча чек жок — басып чыгара турган эч нерсе жок.",
                        "No receipts yet — nothing to print.",
                        "Henüz fiş yok — yazdırılacak bir şey yok.",
                        "Hali chek yo'q — chop etiladigan hech narsa yo'q."));
            }

            var detail = await LoadDetailAsync(last, ct).ConfigureAwait(false);
            var error = await PrintAsync(BuildReprintText(last, detail)).ConfigureAwait(false);
            if (error != null)
                return (false, error);

            var number = last.ReceiptNumber;
            var time = last.CreatedAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            return (true, Tr.T(
                $"Копия чека {number} ({time}, {Money(last.Total)}) напечатана.",
                $"{number} чектин көчүрмөсү ({time}, {Money(last.Total)}) басылып чыкты.",
                $"Copy of receipt {number} ({time}, {Money(last.Total)}) printed.",
                $"{number} fişinin kopyası ({time}, {Money(last.Total)}) yazdırıldı.",
                $"{number} chek nusxasi ({time}, {Money(last.Total)}) chop etildi."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Печать последнего чека не удалась: {ex}", "PRINTER");
            return (false, Tr.T("Не удалось напечатать последний чек: ", "Акыркы чекти басып чыгаруу мүмкүн болгон жок: ", "Could not print the last receipt: ", "Son fiş yazdırılamadı: ", "Oxirgi chekni chop etib bo'lmadi: ") + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _printingLast, 0);
        }
    }

    /// <summary>2026-10-18, ревью этапа 6: «Печать копии» на плашке «Продажа прошла» — копия ИМЕННО этой продажи.
    /// «Последний чек смены» (PrintLastReceiptAsync) мог напечатать предыдущий: история сортируется по времени создания
    /// корзины (оплатили «Чек 2», потом «Чек 1»), у офлайн-чека — по часам ПК, а сервер мог ещё не отдать новую продажу
    /// в списке. Здесь: продажа на сервере — по её id (как повторная печать в «Продажах»); офлайн, без id или сервер не
    /// ответил — из корзины этой продажи (как копия офлайн-чека в «Истории чеков»). Та же защита от двойного нажатия.</summary>
    public static async Task<(bool Ok, string Message)> PrintSaleCopyAsync(
        NurMarketKassa.ViewModels.Main.LastSaleInfo sale,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _printingLast, 1) == 1)
            return (false, Tr.T("Чек уже печатается.", "Чек басылып жатат.", "The receipt is already being printed.", "Fiş zaten yazdırılıyor.", "Chek allaqachon chop etilmoqda."));

        try
        {
            string? text = null;
            string? number = null;
            if (!sale.SavedOffline && !string.IsNullOrWhiteSpace(sale.SaleId))
            {
                try
                {
                    var detail = await App.SalesApi.PosSaleGetAsync(sale.SaleId, ct).ConfigureAwait(false);
                    number = SalesWindow.TryReceiptNumber(detail);
                    text = SaleReceiptTextBuilder.Build(
                        detail,
                        number,
                        SalesWindow.ReadSaleDiscount(detail),
                        SalesWindow.ReadSaleTotal(detail),
                        isReprint: true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Продажа на сервере есть, но сейчас он не ответил — копия из корзины этой продажи (без пометки «ОФФЛАЙН»).
                    PosLogger.Log($"Копия чека {sale.SaleId}: сервер не отдал продажу ({ex.Message}) — печатаю из корзины.", "PRINTER");
                    number = null;
                }
            }

            text ??= CartReceiptTextBuilder.BuildSimpleReceipt(
                sale.CartJson ?? "{}",
                sale.SavedOffline ? "ОФФЛАЙН (ожидает выгрузку)" : null,
                sale.PaymentMethod,
                sale.CashReceivedText,
                receiptTime: sale.SoldAt,
                isReprint: true);

            var error = await PrintAsync(text).ConfigureAwait(false);
            if (error != null)
                return (false, error);

            var label = string.IsNullOrWhiteSpace(number) ? "" : "№" + number.TrimStart('№') + " ";
            var time = sale.SoldAt.ToString("HH:mm", CultureInfo.InvariantCulture);
            var sum = Money((decimal)sale.Total);
            return (true, Tr.T(
                $"Копия чека {label}({time}, {sum}) напечатана.",
                $"Чектин {label}көчүрмөсү ({time}, {sum}) басылып чыкты.",
                $"Copy of receipt {label}({time}, {sum}) printed.",
                $"Fiş {label}kopyası ({time}, {sum}) yazdırıldı.",
                $"Chek {label}nusxasi ({time}, {sum}) chop etildi."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Копия чека с плашки не напечатана: {ex}", "PRINTER");
            return (false, Tr.T("Не удалось напечатать копию чека: ", "Чектин көчүрмөсүн басып чыгаруу мүмкүн болгон жок: ", "Could not print the receipt copy: ", "Fiş kopyası yazdırılamadı: ", "Chek nusxasini chop etib bo'lmadi: ") + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _printingLast, 0);
        }
    }

    // ── 2026-10-20, владелец: «кнопка отправить на сервер — у чека с „Не отправлен на сервер“ должна быть» ──

    /// <summary>Идёт ли сейчас досылка офлайн-очереди (<see cref="SyncService"/>). Службы нет — false.</summary>
    public static bool IsQueueSending()
    {
        try
        {
            return App.GetRequiredService<SyncService>().IsSyncInProgress;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>«Отправить на сервер»: тот же проход досылки офлайн-очереди, что <see cref="SyncService"/> делает сам
    /// раз в 45 с (проверка связи → TriggerSyncNowAsync). Своей отправки здесь нет — логика досылки не меняется.
    /// Sent — записи чека в очереди больше нет (проведён на сервере).</summary>
    public static async Task<(bool Sent, string Message)> SendPendingNowAsync(string localId, CancellationToken ct = default)
    {
        if (IsAutonomousSession())
            return (false, Tr.T("Касса работает без сервера (автономный режим) — отправлять чек некуда.",
                "Касса серверсиз иштеп жатат (автономдук режим) — чекти жөнөтө турган жер жок.",
                "The till works without a server (autonomous mode) — there is nowhere to send the receipt.",
                "Kasa sunucusuz çalışıyor (otonom mod) — fişin gönderileceği bir yer yok.",
                "Kassa serversiz ishlayapti (avtonom rejim) — chekni yuboradigan joy yo'q."));

        SyncService sync;
        try
        {
            sync = App.GetRequiredService<SyncService>();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: служба досылки недоступна: {ex.Message}", "OFFLINE");
            return (false, Tr.T("Не удалось отправить: служба отправки недоступна.", "Жөнөтүү мүмкүн болгон жок: жөнөтүү кызматы жеткиликсиз.",
                "Could not send: the sending service is unavailable.", "Gönderilemedi: gönderme hizmeti kullanılamıyor.", "Yuborib bo'lmadi: yuborish xizmati mavjud emas."));
        }

        if (sync.IsSyncInProgress)
            return (false, SendingNowText);

        try
        {
            PosLogger.Log($"История чеков: кассир нажал «Отправить на сервер» (чек {localId}) — досылка очереди сейчас.", "OFFLINE");
            await Task.Run(async () =>
            {
                await sync.ProbeNowAsync(ct).ConfigureAwait(false);
                if (sync.IsOnline)
                    await sync.TriggerSyncNowAsync(ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            var left = await Task.Run(() => OfflinePendingSalesStore.TryGetById(localId), ct).ConfigureAwait(false);
            if (left == null || string.Equals(left.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase))
                return (true, Tr.T("Чек отправлен на сервер.", "Чек серверге жөнөтүлдү.", "The receipt has been sent to the server.",
                    "Fiş sunucuya gönderildi.", "Chek serverga yuborildi."));

            if (!sync.IsOnline)
                return (false, Tr.T("Нет связи с сервером — чек уйдёт сам, когда связь появится.",
                    "Сервер менен байланыш жок — байланыш пайда болгондо чек өзү жөнөтүлөт.",
                    "No connection to the server — the receipt will be sent automatically when the connection is back.",
                    "Sunucuyla bağlantı yok — bağlantı geldiğinde fiş kendiliğinden gönderilecek.",
                    "Server bilan aloqa yo'q — aloqa paydo bo'lganda chek o'zi yuboriladi."));

            if (string.Equals(left.Status, OfflineSaleEntry.Syncing, StringComparison.OrdinalIgnoreCase))
                return (false, SendingNowText);

            var reason = DescribeUploadError(left.LastError).Text;
            if (string.Equals(left.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase))
                return (false, Tr.T("Сервер не принял чек", "Сервер чекти кабыл алган жок", "The server did not accept the receipt",
                                   "Sunucu fişi kabul etmedi", "Server chekni qabul qilmadi")
                               + (reason == null ? "" : ": " + reason) + ". "
                               + Tr.T("Повторить можно в разделе «Некорректные чеки».", "«Туура эмес чектер» бөлүмүнөн кайталаса болот.",
                                   "You can retry in “Irregular receipts”.", "«Sorunlu fişler» bölümünden yeniden deneyebilirsiniz.",
                                   "«Noto'g'ri cheklar» bo'limidan qayta urinish mumkin."));

            return (false, Tr.T("Чек пока не отправлен", "Чек азырынча жөнөтүлгөн жок", "The receipt has not been sent yet",
                               "Fiş henüz gönderilmedi", "Chek hali yuborilmadi")
                           + (reason == null ? "" : ": " + reason) + ". "
                           + Tr.T("Касса повторит отправку сама.", "Касса жөнөтүүнү өзү кайталайт.", "The till will retry by itself.",
                               "Kasa göndermeyi kendisi tekrarlayacak.", "Kassa yuborishni o'zi takrorlaydi."));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"История чеков: «Отправить на сервер» не удалось: {ex}", "OFFLINE");
            return (false, Tr.T("Не удалось отправить чек: ", "Чекти жөнөтүү мүмкүн болгон жок: ", "Could not send the receipt: ",
                "Fiş gönderilemedi: ", "Chekni yuborib bo'lmadi: ") + ex.Message);
        }
    }

    /// <summary>«Отправляется…» — подпись выключенной кнопки, пока идёт досылка.</summary>
    public static string SendingNowText => Tr.T("Отправляется…", "Жөнөтүлүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");

    /// <summary>Причина неудачной отправки простыми словами — без кода ответа и JSON сервера. FromServer — это ответ
    /// сервера по существу (а не обрыв связи). Пример из журнала 20.10.2026:
    /// «отказ 400: items: {"6":"Товар «Nike» имеет размеры/цвета. Выберите размер/цвет."}» → «у товара «Nike» не выбран размер/цвет».</summary>
    public static (string? Text, bool FromServer) DescribeUploadError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return (null, false);

        var text = error.Trim();
        var fromServer = false;
        var status = System.Text.RegularExpressions.Regex.Match(text, @"^(отказ\s+)?\d{3}\s*[:\-—]\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (status.Success)
        {
            fromServer = true;
            text = text[status.Length..];
        }

        var brace = text.IndexOfAny(new[] { '{', '[' });
        if (brace >= 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(text[brace..]);
                var parts = new List<string>();
                CollectStrings(doc.RootElement, parts);
                if (parts.Count > 0)
                {
                    text = string.Join(" ", parts.Distinct());
                    fromServer = true;
                }
            }
            catch (JsonException)
            {
                // Не JSON — оставляем текст как есть.
            }
        }

        // Имя поля сервера в начале («items: …»).
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^[A-Za-z_]+\s*:\s*", "").Trim();

        var variant = System.Text.RegularExpressions.Regex.Match(text, @"Товар\s+«(?<name>[^»]+)»\s+имеет\s+размеры", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (variant.Success)
        {
            var name = variant.Groups["name"].Value;
            return (Tr.T($"у товара «{name}» не выбран размер/цвет", $"«{name}» товарынын өлчөмү/түсү тандалган эмес",
                $"no size/colour was selected for «{name}»", $"«{name}» ürünü için beden/renk seçilmedi",
                $"«{name}» mahsuloti uchun o'lcham/rang tanlanmagan"), true);
        }

        if (text.Length > 240)
            text = text[..240] + "…";
        return (text.Length == 0 ? null : text, fromServer);
    }

    private static void CollectStrings(JsonElement element, List<string> into)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when !string.IsNullOrWhiteSpace(element.GetString()):
                into.Add(element.GetString()!.Trim());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                    CollectStrings(property.Value, into);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectStrings(item, into);
                break;
        }
    }

    /// <summary>Способ оплаты словами, на языке интерфейса.</summary>
    public static string PaymentLabel(string? method) => (method ?? "").Trim().ToLowerInvariant() switch
    {
        "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
        "transfer" or "card" or "noncash" or "cashless" => Tr.T("Безналичные", "Накталай эмес", "Cashless", "Nakitsiz", "Naqdsiz"),
        "mbank" => "MBank",
        "mixed" or "split" => Tr.T("Смешанная", "Аралаш", "Mixed", "Karışık", "Aralash"),
        "debt" => Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
        // 2026-10-06: зачёт при обмене и выдаче заказа (сервер: «Зачёт (предоплата/обмен)») — показывалось слово «offset».
        "offset" => Tr.T("Зачёт (обмен, предоплата)", "Эсепке алуу (алмаштыруу, алдын ала төлөм)", "Offset (exchange, prepayment)", "Mahsup (değişim, ön ödeme)", "Hisobga olish (almashtirish, oldindan to'lov)"),
        "" => "—",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };

    /// <summary>Чек возвращён целиком или отменён. Полностью возвращённый чек сервер помечает
    /// «canceled» (см. SalesWindow.FetchSalesPageAsync).</summary>
    public static bool IsReturnedOrCanceled(ReceiptHistoryEntry entry) =>
        !entry.IsLocal && entry.Status is "canceled" or "cancelled" or "returned" or "refunded";

    /// <summary>Статус чека словами, на языке интерфейса.</summary>
    public static string StatusLabel(ReceiptHistoryEntry entry)
    {
        if (entry.IsFailedUpload)
            return Tr.T("Ошибка отправки", "Жөнөтүү катасы", "Send error", "Gönderme hatası", "Yuborishda xato");
        if (entry.IsPendingUpload)
            return Tr.T("Не отправлен на сервер", "Серверге жөнөтүлгөн жок", "Not sent to the server", "Sunucuya gönderilmedi", "Serverga yuborilmagan");
        if (entry.IsLocal)
            return Tr.T("Оплачен", "Төлөндү", "Paid", "Ödendi", "To'langan");
        if (IsReturnedOrCanceled(entry))
            return Tr.T("Возврат / отменён", "Кайтарылды / жокко чыгарылды", "Returned / canceled", "İade / iptal", "Qaytarilgan / bekor qilingan");

        return entry.Status switch
        {
            "debt" => Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
            "partially_returned" => Tr.T("Частичный возврат", "Жарым-жартылай кайтарылды", "Partially returned", "Kısmi iade", "Qisman qaytarilgan"),
            "" or "paid" or "completed" or "closed" or "checked_out" => Tr.T("Оплачен", "Төлөндү", "Paid", "Ödendi", "To'langan"),
            // Незнакомый статус показываем как есть, а не выдаём за «оплачен».
            var other => other,
        };
    }

    // 2026-10-20: internal — окно «История чеков» выключает «Эта смена», когда смены нет.
    internal static string? CurrentShiftId()
    {
        if (!string.IsNullOrWhiteSpace(PosApp.ActiveShiftId))
            return PosApp.ActiveShiftId!.Trim();
        try
        {
            var session = App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>();
            return string.IsNullOrWhiteSpace(session.ActiveShiftId) ? null : session.ActiveShiftId!.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsAutonomousSession()
    {
        try
        {
            return App.GetRequiredService<IAutonomousAuthService>().IsCurrentSessionAutonomous;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Время продажи: момент оплаты, если сервер его отдаёт, иначе создание.</summary>
    private static DateTime? SaleTime(JsonElement row)
    {
        foreach (var key in new[] { "paid_at", "closed_at", "created_at", "date" })
        {
            if (Str(row, key) is { } text
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                return at.LocalDateTime;
        }

        return null;
    }

    private static decimal RowTotal(JsonElement row)
    {
        foreach (var key in new[] { "total", "grand_total", "total_amount", "amount", "total_sum", "final_total", "order_total" })
        {
            if (row.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d))
                return (decimal)d;
        }

        return 0m;
    }

    /// <summary>Касса продажи: «cashbox» (id или объект) либо «cashbox_id», как у смен
    /// (ShiftHelper.ReadCashboxId). null — сервер кассу не прислал.</summary>
    private static string? RowCashboxId(JsonElement row) => IdOf(row, "cashbox") ?? IdOf(row, "cashbox_id");

    private static string? RowShiftId(JsonElement row) => IdOf(row, "shift") ?? IdOf(row, "shift_id");

    private static string? IdOf(JsonElement row, string key)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Object)
            return v.TryGetProperty("id", out var id) ? Scalar(id) : null;
        return Scalar(v);
    }

    private static string? Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
            && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;
}
