using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-29: строка проданного чека для возврата без связи. Key — ключ строки (индекс в снимке), LineTotal — сумма строки после скидки на
/// позицию, но до скидки на весь чек (как SaleLedgerLine.Total).</summary>
public sealed class OfflineSaleLine
{
    public string Key { get; set; } = "";
    public string? ProductId { get; set; }
    public string Name { get; set; } = "";
    public double Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    /// <summary>Сколько единиц склада в одной единице строки (пачка/штуки) — для остатка при возврате.</summary>
    public double StockPerQty { get; set; } = 1.0;
}

/// <summary>2026-10-29: чек, который касса знает без сервера: из ещё не отправленной очереди (SaleRefIsLocal) или проведённый раньше (журнал возвратов
/// SaleReturnLedger + локальная история SoldLineItems). Чека нет локально (старый) — возврат без связи запрещён.</summary>
public sealed class OfflineSaleSnapshot
{
    public string SaleRef { get; set; } = "";
    public bool IsLocal { get; set; }
    public string? ReceiptNumber { get; set; }
    public string Method { get; set; } = "";
    public decimal Total { get; set; }
    public decimal LinesTotal { get; set; }
    public decimal PaidCash { get; set; }
    public decimal PaidNonCash { get; set; }
    public string? ClientId { get; set; }
    public string? ClientName { get; set; }
    public string? ShiftId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<OfflineSaleLine> Lines { get; set; } = new();

    /// <summary>Доля скидки на весь чек: стоимость строки × Factor = сколько за неё заплатили.</summary>
    public decimal Factor => LinesTotal > 0.005m && Total > 0m ? Math.Min(1m, Total / LinesTotal) : 1m;
    public decimal Debt => Math.Max(0m, Total - PaidCash - PaidNonCash);
}

/// <summary>Возврат, уже оформленный по чеку (офлайн-операция или запись журнала): сколько вернули и каких строк.</summary>
public sealed class OfflinePriorRefund
{
    public string Id { get; set; } = "";
    public decimal Amount { get; set; }
    public decimal Cash { get; set; }
    public decimal NonCash { get; set; }
    public decimal DebtReduced { get; set; }
    public List<(string? Key, string Name, double Quantity)> Lines { get; set; } = new();
}

/// <summary>Результат расчёта возврата без связи.</summary>
public sealed class OfflineRefundPlan
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public List<OfflineRefundLine> Lines { get; set; } = new();
    public decimal Amount { get; set; }
    public decimal Cash { get; set; }
    public decimal NonCash { get; set; }
    public decimal DebtReduced { get; set; }
    public bool SplitKnown { get; set; } = true;
    public bool DebtUnknown { get; set; }
    /// <summary>2026-10-29 (ревью, В-6): часть возврата записана только в уменьшение долга, но кассе неизвестно точно, не погасил ли клиент долг сам —
    /// кассиру показывается предупреждение «если клиент уже погасил долг, верните разницу, когда появится связь».</summary>
    public bool DebtNeedsCheck { get; set; }
    /// <summary>Возвращаются все остатки чека: сумма — ровно остаток оплаченного, без копеек округления.</summary>
    public bool Last { get; set; }
    public bool Whole { get; set; }
    /// <summary>Сумма строк была больше остатка чека — урезана до остатка.</summary>
    public bool Clamped { get; set; }
}

/// <summary>2026-10-29: расчёт возврата без связи — чистая арифметика (без файлов и сети), её гонит мини-прогон. Правила как у сервера и окна возврата
/// (Д-4, ComputeSelectedTotal): возврат строки = её цена со скидкой × доля скидки на чек; не больше проданного количества и оплаченной суммы с учётом
/// уже сделанных возвратов; последний возврат — ровно остаток; чем вернуть — как ReturnSaleDialog.ComputeRefundSplit (долг уменьшается, выдаётся
/// только внесённое).</summary>
public static class OfflineRefundPlanner
{
    public static OfflineRefundPlan Plan(OfflineSaleSnapshot sale, IReadOnlyList<OfflinePriorRefund> priors,
        IReadOnlyList<(string Key, double Quantity)> requested, decimal? clientDebtKnown)
    {
        static OfflineRefundPlan Fail(string text) => new() { Ok = false, Error = text };

        if (sale.Total <= 0.004m || sale.Lines.Count == 0)
            return Fail(Tr.T("Состав или сумма чека неизвестны кассе — возврат без связи невозможен. Найдите чек на сервере, когда появится связь.",
                "Чектин курамы же суммасы кассага белгисиз — байланышсыз кайтаруу мүмкүн эмес. Байланыш пайда болгондо чекти серверден табыңыз.",
                "The receipt's contents or total are unknown to the till — a return without a connection is impossible. Find the receipt on the server once the connection is back.",
                "Fişin içeriği veya tutarı kasaya bilinmiyor — bağlantısız iade yapılamaz. Bağlantı gelince fişi sunucuda bulun.",
                "Chekning tarkibi yoki summasi kassaga noma'lum — aloqasiz qaytarib bo'lmaydi. Aloqa tiklanganda chekni serverdan toping."));
        if (requested.Count == 0 || requested.All(r => r.Quantity <= 0))
            return Fail(Tr.T("Отметьте хотя бы одну позицию для возврата.", "Кайтаруу үчүн жок дегенде бир позицияны белгилеңиз.",
                "Select at least one item to return.", "İade için en az bir kalem seçin.", "Qaytarish uchun kamida bitta pozitsiyani belgilang."));

        // Сколько уже возвращено по каждой строке: по ключу строки, иначе по названию (возвраты с сервера/сайта ключа строки кассы не знают).
        var returned = sale.Lines.ToDictionary(l => l.Key, _ => 0.0, StringComparer.Ordinal);
        foreach (var prior in priors)
        {
            foreach (var (key, name, qty) in prior.Lines)
            {
                if (qty <= 0)
                    continue;
                if (!string.IsNullOrEmpty(key) && returned.ContainsKey(key!))
                {
                    returned[key!] += qty;
                    continue;
                }
                var left = qty;
                foreach (var line in sale.Lines.Where(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    var room = Math.Max(0, line.Quantity - returned[line.Key]);
                    var take = Math.Min(room, left);
                    returned[line.Key] += take;
                    left -= take;
                    if (left <= 1e-9)
                        break;
                }
            }
        }

        var priorAmount = priors.Sum(p => p.Amount);
        var leftMoney = Math.Max(0m, sale.Total - priorAmount);
        if (leftMoney <= 0.004m)
            return Fail(Tr.T("По этому чеку уже возвращено всё, что оплачено.", "Бул чек боюнча төлөнгөндүн баары мурунтан кайтарылган.",
                "Everything paid on this receipt has already been returned.", "Bu fişte ödenen her şey zaten iade edildi.", "Bu chek bo'yicha to'langan hamma narsa allaqachon qaytarilgan."));

        var plan = new OfflineRefundPlan { Ok = true };
        decimal linesSum = 0m;
        var picked = new List<(OfflineSaleLine Line, double Qty, decimal Value)>();
        foreach (var (key, qtyRaw) in requested)
        {
            if (qtyRaw <= 0)
                continue;
            var line = sale.Lines.FirstOrDefault(l => string.Equals(l.Key, key, StringComparison.Ordinal));
            if (line is null)
                return Fail(Tr.T("Позиция не найдена в чеке.", "Позиция чектен табылган жок.", "The item was not found in the receipt.", "Kalem fişte bulunamadı.", "Pozitsiya chekda topilmadi."));
            if (picked.Any(p => p.Line.Key == line.Key))
                return Fail(Tr.T("Позиция указана дважды.", "Позиция эки жолу көрсөтүлдү.", "The item is listed twice.", "Kalem iki kez belirtilmiş.", "Pozitsiya ikki marta ko'rsatilgan."));
            var qty = Math.Round(qtyRaw, 3);
            // 2026-10-29 (ревью, Б-1): после округления до тысячных количество должно остаться положительным — 0,0004 даёт 0, а возврат «ноль» уходил на сервер как весь чек.
            if (qty < 0.001)
                return Fail(Tr.T($"«{line.Name}»: количество к возврату слишком мало — не меньше 0,001.", $"«{line.Name}»: кайтарыла турган сан өтө аз — 0,001ден кем эмес.",
                    $"“{line.Name}”: the quantity to return is too small — at least 0.001.", $"«{line.Name}»: iade miktarı çok küçük — en az 0,001.",
                    $"«{line.Name}»: qaytariladigan miqdor juda kam — kamida 0,001."));
            var room = Math.Max(0, line.Quantity - returned[line.Key]);
            if (qty > room + 1e-6)
                return Fail(Tr.T($"«{line.Name}»: продано {Q(line.Quantity)}, уже возвращено {Q(returned[line.Key])}, можно вернуть не больше {Q(room)}.",
                    $"«{line.Name}»: сатылды {Q(line.Quantity)}, мурун кайтарылды {Q(returned[line.Key])}, {Q(room)} ашык кайтарууга болбойт.",
                    $"“{line.Name}”: sold {Q(line.Quantity)}, already returned {Q(returned[line.Key])}, at most {Q(room)} can be returned.",
                    $"«{line.Name}»: satılan {Q(line.Quantity)}, daha önce iade {Q(returned[line.Key])}, en fazla {Q(room)} iade edilebilir.",
                    $"«{line.Name}»: sotilgan {Q(line.Quantity)}, avval qaytarilgan {Q(returned[line.Key])}, {Q(room)} dan ko'p qaytarib bo'lmaydi."));
            var value = line.Quantity > 0
                ? Math.Round(line.LineTotal * (decimal)(qty / line.Quantity) * sale.Factor, 2, MidpointRounding.AwayFromZero)
                : 0m;
            if (value <= 0m)
                return Fail(Tr.T($"«{line.Name}»: сумма возврата по позиции нулевая или отрицательная — вернуть её без связи нельзя.", $"«{line.Name}»: позиция боюнча кайтаруу суммасы нөл же терс — байланышсыз кайтарууга болбойт.",
                    $"“{line.Name}”: the return amount for this item is zero or negative — it cannot be returned without a connection.", $"«{line.Name}»: kalem iade tutarı sıfır veya negatif — bağlantısız iade edilemez.",
                    $"«{line.Name}»: pozitsiya qaytarish summasi nol yoki manfiy — aloqasiz qaytarib bo'lmaydi."));
            picked.Add((line, qty, value));
            linesSum += value;
        }

        if (picked.Count == 0)
            return Fail(Tr.T("Отметьте хотя бы одну позицию для возврата.", "Кайтаруу үчүн жок дегенде бир позицияны белгилеңиз.",
                "Select at least one item to return.", "İade için en az bir kalemi işaretleyin.", "Qaytarish uchun kamida bitta pozitsiyani belgilang."));

        // «Последний возврат»: возвращается всё, что ещё не возвращено, — ровно остаток оплаченного (копейки округления — сюда).
        var openLines = sale.Lines.Where(l => l.Quantity - returned[l.Key] > 1e-6).ToList();
        var last = openLines.Count > 0
            && openLines.All(l => picked.Any(p => p.Line.Key == l.Key && p.Qty >= l.Quantity - returned[l.Key] - 1e-6));
        decimal amount;
        if (last && leftMoney <= linesSum + 0.01m * picked.Count)
            amount = leftMoney;
        else
        {
            amount = Math.Min(linesSum, leftMoney);
            plan.Clamped = linesSum > leftMoney + 0.004m;
        }
        // 2026-10-29 (ревью, Б-1): сумма возврата должна быть положительной — иначе операция не создаётся (раньше 0 уходил на сервер).
        if (amount <= 0.004m)
            return Fail(Tr.T("Сумма возврата нулевая — возвращать нечего.", "Кайтаруу суммасы нөл — кайтарыла турган нерсе жок.", "The return amount is zero — there is nothing to return.",
                "İade tutarı sıfır — iade edilecek bir şey yok.", "Qaytarish summasi nol — qaytariladigan narsa yo'q."));
        plan.Last = last;
        // «Весь чек» одним запросом без строк — только когда возвращается ВСЁ, ничего ещё не возвращали и сумма совпала с итогом чека.
        plan.Whole = last && priors.Count == 0 && Math.Abs(amount - sale.Total) <= 0.01m;
        plan.Amount = amount;

        // Суммы по строкам сходятся с общей: урезание — пропорционально строкам, копейки округления — в последнюю.
        var lines = picked.Select(p => new OfflineRefundLine
        {
            Key = p.Line.Key, ProductId = p.Line.ProductId, Name = p.Line.Name, Quantity = p.Qty, OriginalQuantity = p.Line.Quantity,
            UnitPrice = p.Line.UnitPrice, Amount = p.Value,
        }).ToList();
        var diff = amount - lines.Sum(l => l.Amount);
        if (diff != 0m && linesSum > 0m)
        {
            decimal assigned = 0m;
            for (var i = 0; i < lines.Count; i++)
            {
                lines[i].Amount = i == lines.Count - 1
                    ? Math.Max(0m, amount - assigned)
                    : Math.Round(amount * picked[i].Value / linesSum, 2, MidpointRounding.AwayFromZero);
                assigned += lines[i].Amount;
            }
        }
        plan.Lines = lines;

        // Чем вернуть — как ReturnSaleDialog.ComputeRefundSplit.
        var priorCash = priors.Sum(p => p.Cash);
        var priorNonCash = priors.Sum(p => p.NonCash);
        var priorDebt = priors.Sum(p => p.DebtReduced);
        var cashLeft = Math.Max(0m, sale.PaidCash - priorCash);
        var nonCashLeft = Math.Max(0m, sale.PaidNonCash - priorNonCash);
        var debtLeftByChecks = Math.Max(0m, sale.Debt - priorDebt);
        var method = (sale.Method ?? "").Trim().ToLowerInvariant();
        if (sale.Debt > 0.005m || method == "debt")
        {
            // Долг уменьшается не больше текущего долга клиента (кассе известен по кэшу): клиент мог уже погасить — тогда возврат выдаётся деньгами.
            decimal debtLeft;
            if (clientDebtKnown is { } known)
                debtLeft = Math.Min(Math.Max(0m, known), debtLeftByChecks);
            else
            {
                debtLeft = debtLeftByChecks;
                plan.DebtUnknown = true;
            }
            var money = Math.Max(0m, amount - Math.Min(amount, debtLeft));
            // 2026-10-29 (ревью, В-6): без связи деньгами выдаётся НЕ больше внесённой при продаже предоплаты. Остальное — только уменьшение долга:
            // кэш долга мог ошибаться (клиент погасил долг онлайн, сводка неполная, чужой клиент) — разницу вернут при появлении связи.
            var prepaidCap = cashLeft + nonCashLeft;
            if (money > prepaidCap + 0.005m)
            {
                money = prepaidCap;
                plan.DebtNeedsCheck = true;
            }
            if (clientDebtKnown is null || clientDebtKnown.Value < debtLeftByChecks - 0.005m)
                plan.DebtNeedsCheck = true;
            plan.DebtReduced = amount - money;
            var prepaid = money;
            var cash = prepaidCap > 0m
                ? Math.Round(prepaid * cashLeft / prepaidCap, 2, MidpointRounding.AwayFromZero)
                : prepaid;
            plan.Cash = cash;
            plan.NonCash = money - cash;
        }
        else
        {
            switch (method)
            {
                case "cash":
                    plan.Cash = amount;
                    break;
                case "mixed" or "split" when cashLeft + nonCashLeft > 0m:
                    plan.Cash = Math.Min(amount, Math.Round(amount * cashLeft / (cashLeft + nonCashLeft), 2, MidpointRounding.AwayFromZero));
                    plan.NonCash = amount - plan.Cash;
                    break;
                case "mixed" or "split" or "":
                    plan.Cash = amount;
                    plan.SplitKnown = false;
                    break;
                default:
                    plan.NonCash = amount;
                    break;
            }
        }

        return plan;
    }

    private static string Q(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ── снимок чека ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Снимок чека из записи очереди (чек ещё не ушёл на сервер или отклонён). Строки, сумма и скидка — из снимка корзины.</summary>
    public static OfflineSaleSnapshot? FromQueueEntry(OfflineSaleEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(entry.CartJson) ? "{}" : entry.CartJson);
            var root = doc.RootElement;
            var totals = CartTotalsCalculator.Calculate(root);
            var snap = new OfflineSaleSnapshot
            {
                SaleRef = entry.Id,
                IsLocal = true,
                ReceiptNumber = null,
                Method = (entry.PaymentMethod ?? "").Trim().ToLowerInvariant(),
                Total = Math.Round((decimal)totals.TotalDue, 2),
                LinesTotal = Math.Round((decimal)Math.Max(0, totals.Subtotal - totals.LineDiscounts), 2),
                ClientId = entry.ClientId,
                ClientName = entry.DebtClientName,
                ShiftId = entry.ShiftId,
                CreatedAt = entry.CreatedAt,
            };
            switch (snap.Method)
            {
                case "cash":
                    snap.PaidCash = snap.Total;
                    break;
                case "debt":
                    decimal.TryParse(entry.CashReceived, NumberStyles.Any, CultureInfo.InvariantCulture, out var prepaid);
                    snap.PaidCash = Math.Min(Math.Max(0m, prepaid), snap.Total);
                    break;
                case "mixed" or "split":
                    return null; // в очередь не ставится
                default:
                    snap.PaidNonCash = snap.Total;
                    break;
            }

            var index = 0;
            foreach (var item in CartDisplayHelper.EnumerateItems(root))
            {
                var qty = CartDisplayHelper.LineQuantity(item);
                decimal.TryParse(CartDisplayHelper.LineTotal(item), NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal);
                // 2026-10-29 (ревью, Б-1): строка с нулевым количеством или отрицательной суммой («Расход») — чек для возврата без связи не подходит целиком.
                if (!(qty > 0) || lineTotal < 0m || CartDisplayHelper.UnitPrice(item) < 0)
                    return null;
                snap.Lines.Add(new OfflineSaleLine
                {
                    Key = index.ToString(CultureInfo.InvariantCulture),
                    ProductId = CartDisplayHelper.TryProductId(item),
                    Name = CartDisplayHelper.ItemName(item),
                    Quantity = qty,
                    UnitPrice = (decimal)CartDisplayHelper.UnitPrice(item),
                    LineTotal = lineTotal,
                    StockPerQty = StockFactor(item, qty),
                });
                index++;
            }

            return snap.Lines.Count == 0 ? null : snap;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            PosLogger.Log($"OFFLINE: снимок чека {entry.Id} для возврата не прочитан: {ex.Message}", "WARNING");
            return null;
        }
    }

    private static double StockFactor(JsonElement item, double qty)
    {
        try
        {
            var productId = CartDisplayHelper.TryProductId(item);
            if (string.IsNullOrEmpty(productId) || qty <= 0)
                return 1.0;
            var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
            if (tile == null)
                return 1.0;
            var inStock = CartDisplayHelper.LineQuantityInStockUnits(item, tile);
            return inStock > 0 ? inStock / qty : 1.0;
        }
        catch
        {
            return 1.0;
        }
    }

    /// <summary>Снимок чека, уже проведённого на сервере: итог и оплата — из журнала возвратов (касса записывает их при оплате), состав — из локальной
    /// истории продаж. Состав не сходится с итогом (строки без товара, «Доп. услуга») — null: чек надо искать на сервере.</summary>
    public static OfflineSaleSnapshot? FromHistory(string saleId, SaleLedgerEntry? ledger,
        IReadOnlyList<(string ProductName, double Quantity, double UnitPrice)>? soldLines)
    {
        // Состав чека: сперва строки из журнала возвратов (их пишет окно возврата и отправка чека очереди), иначе локальная история продаж.
        // 2026-10-29 (ревью, мелочь 1): ключ строки — LineId из журнала (его пишет отправка чека очереди = ключ снимка очереди), иначе номер по порядку.
        IReadOnlyList<(string? Key, string ProductName, double Quantity, double UnitPrice)>? source =
            soldLines?.Select(l => ((string?)null, l.ProductName, l.Quantity, l.UnitPrice)).ToList();
        if (ledger is { Lines.Count: > 0 })
            source = ledger.Lines.Select(l => (string.IsNullOrWhiteSpace(l.LineId) ? null : l.LineId, l.Name, l.Quantity,
                l.Quantity > 0 ? (double)(l.Total / (decimal)l.Quantity) : (double)l.UnitPrice)).ToList();
        if (ledger is not { Total: > 0m, LinesTotal: > 0m } || string.IsNullOrWhiteSpace(ledger.Method) || source is not { Count: > 0 })
            return null;
        var snap = new OfflineSaleSnapshot
        {
            SaleRef = saleId.Trim(),
            IsLocal = false,
            ReceiptNumber = ledger.Number,
            Method = ledger.Method.Trim().ToLowerInvariant(),
            Total = ledger.Total,
            LinesTotal = ledger.LinesTotal,
            PaidCash = ledger.PaidCash,
            PaidNonCash = ledger.PaidNonCash,
            CreatedAt = ledger.SoldAt ?? ledger.RecordedAt,
            ClientName = ledger.ClientName,
        };
        var index = 0;
        foreach (var (lineKey, name, qty, price) in source)
        {
            if (qty > 0)
                snap.Lines.Add(new OfflineSaleLine
                {
                    Key = lineKey ?? index.ToString(CultureInfo.InvariantCulture), Name = name, Quantity = qty, UnitPrice = (decimal)price,
                    LineTotal = Math.Round((decimal)qty * (decimal)price, 2, MidpointRounding.AwayFromZero),
                });
            index++;
        }

        // Состав должен сходиться с суммой строк чека (до скидки на весь чек) — иначе в нём есть строки, которых касса не помнит.
        var sum = snap.Lines.Sum(l => l.LineTotal);
        if (snap.Lines.Count == 0 || Math.Abs(sum - snap.LinesTotal) > Math.Max(0.5m, snap.LinesTotal * 0.005m))
            return null;
        return snap;
    }

    /// <summary>Ранее оформленные возвраты по чеку: операции без связи (по обоим id чека) + возвраты из журнала кассы (онлайн), без дубля офлайн-операций.</summary>
    public static List<OfflinePriorRefund> PriorRefunds(string saleRef, SaleLedgerEntry? ledger, bool includeLedgerOnline = true)
    {
        var result = new List<OfflinePriorRefund>();
        foreach (var op in OfflineOperationsStore.RefundsForSale(saleRef))
            result.Add(new OfflinePriorRefund
            {
                Id = op.Id, Amount = op.Amount, Cash = op.Cash, NonCash = op.NonCash, DebtReduced = op.DebtReduced,
                Lines = op.Lines.Select(l => ((string?)l.Key, l.Name, l.Quantity)).ToList(),
            });
        if (includeLedgerOnline && ledger is not null)
        {
            foreach (var r in ledger.Refunds)
            {
                var offlineDoc = r.ServerDocId != null && r.ServerDocId.StartsWith(OfflineLedgerPrefix, StringComparison.Ordinal);
                // Офлайн-возврат учтён выше, пока его операция лежит в файле очереди. Через 45 дней операция из файла уходит — тогда о нём помнит только журнал
                // (мелочь 2 ревью): берём запись журнала, ключи строк у неё — LineId.
                if (offlineDoc && OfflineOperationsStore.Get(r.ServerDocId![OfflineLedgerPrefix.Length..]) is not null)
                    continue;
                result.Add(new OfflinePriorRefund
                {
                    Id = r.ServerDocId ?? "", Amount = r.Amount, Cash = r.Cash, NonCash = r.NonCash, DebtReduced = r.DebtReduced,
                    Lines = r.Lines.Select(l => (offlineDoc && !string.IsNullOrWhiteSpace(l.LineId) ? l.LineId : null, l.Name, l.Quantity)).ToList(),
                });
            }
        }
        return result;
    }

    /// <summary>Пометка записи журнала возвратов: это возврат без связи (ServerDocId = "offline:" + ключ операции).</summary>
    public const string OfflineLedgerPrefix = "offline:";
}
