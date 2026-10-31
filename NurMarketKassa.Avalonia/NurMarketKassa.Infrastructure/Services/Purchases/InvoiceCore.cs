using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

// 2026-10-30, форма «Накладная прихода» и редактор («Б-лайт», docs/ТЗ Редактор накладных закупок 2.1–2.3, 3.4): чистая логика без сети, файлов и
// окон — право и окно правки, расчёт наценка ↔ цена реализации, итоговые плитки, план правки количества (возврат поставщику / доприход).
// Всё, что считается здесь, проверяется мини-прогоном без интерфейса (invoice-check).

/// <summary>Окно правки проведённой накладной закупки (настройка компании «Редактирование накладных закупок»).</summary>
public enum ReceiptEditWindow { Hours24, Hours48, Always }

public enum ReceiptEditDenied { None, NotOwner, Expired, InQueue, FullyReturned, NoDate }

/// <summary>Remaining/Until — только при ограниченном окне (24/48 ч); у «Всегда» пусто.</summary>
public readonly record struct ReceiptEditAccess(bool Allowed, ReceiptEditDenied Reason, TimeSpan? Remaining, DateTime? Until);

public static class ReceiptEditPolicy
{
    public const string SettingsKey = "PurchaseEditWindow";

    /// <summary>Значение настройки компании: "24" (по умолчанию) / "48" / "always".</summary>
    public static ReceiptEditWindow Parse(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "48" => ReceiptEditWindow.Hours48,
        "always" or "всегда" => ReceiptEditWindow.Always,
        _ => ReceiptEditWindow.Hours24,
    };

    public static string ToKey(ReceiptEditWindow w) => w switch { ReceiptEditWindow.Hours48 => "48", ReceiptEditWindow.Always => "always", _ => "24" };

    public static TimeSpan? Length(ReceiptEditWindow w) => w switch
    {
        ReceiptEditWindow.Hours24 => TimeSpan.FromHours(24),
        ReceiptEditWindow.Hours48 => TimeSpan.FromHours(48),
        _ => null,
    };

    /// <summary>Откуда читается настройка компании. Приложение ставит чтение UserPreferences, мини-прогон подменяет.</summary>
    public static Func<string?> SettingProvider { get; set; } = () => UserPreferences.Instance.PurchaseEditWindow;

    public static ReceiptEditWindow Current => Parse(SettingProvider());

    /// <summary>Разница «часы сервера − часы ПК»: окно считается по серверному времени, перевод часов ПК его не продлевает (если сдвиг известен).</summary>
    public static TimeSpan ClockSkew { get; set; } = TimeSpan.Zero;

    public static DateTime ServerNow => DateTime.Now + ClockSkew;

    /// <summary>Роль «владелец» из профиля. Админ, право «Настройки» и сама программа владельца (она бывает у сотрудника) права не дают.</summary>
    public static bool IsOwnerRole(JsonElement payload)
    {
        var user = OfflineAuthSessionStore.ResolveUserObject(payload);
        if (user.ValueKind != JsonValueKind.Object)
            user = payload;
        return user.ValueKind == JsonValueKind.Object
               && user.TryGetProperty("role", out var role)
               && role.ValueKind == JsonValueKind.String
               && string.Equals(role.GetString(), "owner", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Можно ли сейчас править накладную: владелец, окно не истекло (отсчёт от даты прихода), приход не в очереди и не возвращён целиком.</summary>
    public static ReceiptEditAccess Evaluate(bool isOwner, ReceiptEditWindow window, DateTime? receiptAt, DateTime now, bool inQueue = false, bool fullyReturned = false)
    {
        if (!isOwner)
            return new ReceiptEditAccess(false, ReceiptEditDenied.NotOwner, null, null);
        if (inQueue)
            return new ReceiptEditAccess(false, ReceiptEditDenied.InQueue, null, null);
        if (fullyReturned)
            return new ReceiptEditAccess(false, ReceiptEditDenied.FullyReturned, null, null);
        if (Length(window) is not { } len)
            return new ReceiptEditAccess(true, ReceiptEditDenied.None, null, null);
        if (receiptAt is not { } at)
            return new ReceiptEditAccess(false, ReceiptEditDenied.NoDate, null, null);
        var until = at + len;
        var left = until - now;
        return left > TimeSpan.Zero
            ? new ReceiptEditAccess(true, ReceiptEditDenied.None, left, until)
            : new ReceiptEditAccess(false, ReceiptEditDenied.Expired, null, until);
    }

    /// <summary>«13 ч 20 мин» / «45 мин» / «меньше минуты».</summary>
    public static string RemainingText(TimeSpan left)
    {
        var minutes = (int)Math.Floor(left.TotalMinutes);
        if (minutes < 1)
            return Tr.T("меньше минуты", "бир мүнөттөн аз", "under a minute", "bir dakikadan az", "bir daqiqadan kam");
        var h = minutes / 60;
        var m = minutes % 60;
        return h > 0
            ? Tr.T($"{h} ч {m} мин", $"{h} саат {m} мүн", $"{h} h {m} min", $"{h} sa {m} dk", $"{h} soat {m} daq")
            : Tr.T($"{m} мин", $"{m} мүн", $"{m} min", $"{m} dk", $"{m} daq");
    }

    public static string DeniedText(ReceiptEditAccess a) => a.Reason switch
    {
        ReceiptEditDenied.Expired => Tr.T($"Время правки истекло {a.Until:dd.MM в HH:mm}", $"Оңдоо убактысы өттү {a.Until:dd.MM HH:mm}", $"The edit window ended {a.Until:dd.MM at HH:mm}",
            $"Düzenleme süresi doldu {a.Until:dd.MM HH:mm}", $"Tahrirlash vaqti tugadi {a.Until:dd.MM HH:mm}"),
        ReceiptEditDenied.InQueue => Tr.T("Приход ещё отправляется — правьте его в очереди", "Кирүү дагы жөнөтүлүүдө — кезекте оңдоңуз", "The receipt is still being sent — edit it in the queue",
            "Giriş hâlâ gönderiliyor — kuyrukta düzenleyin", "Kirim hali yuborilmoqda — navbatda tahrirlang"),
        ReceiptEditDenied.FullyReturned => Tr.T("Приход полностью возвращён поставщику", "Кирүү толугу менен жеткирүүчүгө кайтарылды", "The receipt was fully returned to the supplier",
            "Giriş tamamen tedarikçiye iade edildi", "Kirim to'liq yetkazib beruvchiga qaytarilgan"),
        ReceiptEditDenied.NoDate => Tr.T("У прихода нет даты — окно правки не определить", "Кирүүнүн күнү жок — оңдоо мөөнөтүн аныктоого болбойт", "The receipt has no date — the edit window can't be determined",
            "Girişin tarihi yok — düzenleme süresi belirlenemiyor", "Kirimning sanasi yo'q — tahrirlash muddatini aniqlab bo'lmaydi"),
        ReceiptEditDenied.NotOwner => Tr.T("Правка накладных — только у владельца", "Накладнойлорду оңдоо — ээсинде гана", "Only the owner can edit invoices", "Faturaları yalnızca sahip düzenleyebilir", "Yuk xatlarini faqat egasi tahrirlay oladi"),
        _ => "",
    };

    /// <summary>Подпись окна для настроек и подсказок.</summary>
    public static string WindowName(ReceiptEditWindow w) => w switch
    {
        ReceiptEditWindow.Hours48 => Tr.T("48 часов", "48 саат", "48 hours", "48 saat", "48 soat"),
        ReceiptEditWindow.Always => Tr.T("Всегда", "Дайыма", "Always", "Her zaman", "Doim"),
        _ => Tr.T("24 часа", "24 саат", "24 hours", "24 saat", "24 soat"),
    };
}

/// <summary>Показатели строки накладной (для цвета, чипов и плиток).</summary>
public readonly record struct InvoiceLineFacts(double Qty, double Purchase, double Sale, double? PreviousPurchase, bool IsNew);

/// <summary>Итоговая панель накладной: 7 плиток.</summary>
public readonly record struct InvoiceTotals(int Positions, double Units, double PurchaseSum, double? AvgMarkup, double Revenue, double Profit, int Attention);

public static class InvoiceMath
{
    /// <summary>Цена реализации от закупки и наценки: P × (1 + M/100), округление по настройке магазина.</summary>
    public static double SaleFromMarkup(double purchase, double markupPercent, SaleRounding rounding) =>
        purchase > 0 ? ReceivingPricing.SaleFromMarkup(purchase, markupPercent, rounding) : 0;

    /// <summary>Наценка от закупки и цены реализации; null — считать нечем.</summary>
    public static double? MarkupFromSale(double purchase, double sale) => ReceivingPricing.MarkupPercent(purchase, sale);

    /// <summary>Прибыль с единицы.</summary>
    public static double ProfitPerUnit(double purchase, double sale) => sale > 0 && purchase > 0 ? Math.Round(sale - purchase, 2) : 0;

    public static bool IsBelowCost(in InvoiceLineFacts l) => l.Purchase > 0 && l.Sale > 0 && l.Sale < l.Purchase;

    /// <summary>Маржа низкая (не «ниже закупки»): наценка меньше порога <see cref="ReceivingPricing.LowMarginPercent"/>.</summary>
    public static bool IsLowMargin(in InvoiceLineFacts l) =>
        !IsBelowCost(l) && ReceivingPricing.MarkupPercent(l.Purchase, l.Sale) is { } m && m < ReceivingPricing.LowMarginPercent;

    /// <summary>Закупка выросла против прошлой больше чем на порог (0,1 % — копеечные колебания не считаем).</summary>
    public static double RaisePercent(in InvoiceLineFacts l) =>
        l.PreviousPurchase is > 0 and { } prev && l.Purchase > prev ? (l.Purchase / prev - 1) * 100 : 0;

    public static bool IsRaised(in InvoiceLineFacts l) => RaisePercent(l) > 0.1;

    /// <summary>Нужно внимание: ниже закупки, маржа &lt; 10 % или нет цены закупки (количество 0 не считается).</summary>
    public static bool NeedsAttention(in InvoiceLineFacts l) => l.Qty > 0 && (l.Purchase <= 0 || IsBelowCost(l) || IsLowMargin(l));

    public static InvoiceTotals Totals(IEnumerable<InvoiceLineFacts> lines)
    {
        int positions = 0, attention = 0;
        double units = 0, purchase = 0, bothPurchase = 0, bothSale = 0;
        foreach (var l in lines)
        {
            if (l.Qty <= 0)
                continue; // строка с нулём в итог не входит
            positions++;
            units += l.Qty;
            purchase += ReceivingPricing.LineTotal(l.Qty, l.Purchase);
            if (l.Purchase > 0 && l.Sale > 0)
            {
                bothPurchase += ReceivingPricing.LineTotal(l.Qty, l.Purchase);
                bothSale += ReceivingPricing.LineTotal(l.Qty, l.Sale);
            }
            if (NeedsAttention(l))
                attention++;
        }
        double? avg = bothPurchase > 0 ? Math.Round((bothSale / bothPurchase - 1) * 100, 2) : null;
        return new InvoiceTotals(positions, Math.Round(units, 3), Math.Round(purchase, 2), avg, Math.Round(bothSale, 2), Math.Round(bothSale - bothPurchase, 2), attention);
    }

    /// <summary>Отпечаток состава прихода на сервере: если он изменился между открытием правки и отправкой — приход правили с другого ПК.</summary>
    public static string Fingerprint(Services.Api.SupplierReceipt r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Id).Append('|').Append(r.Total.ToString("0.###", CultureInfo.InvariantCulture)).Append('|').Append(r.ReturnedAmount.ToString("0.###", CultureInfo.InvariantCulture));
        foreach (var l in r.Lines.OrderBy(x => x.Id, StringComparer.Ordinal))
            sb.Append(';').Append(l.Id).Append(':').Append(l.Qty.ToString("0.###", CultureInfo.InvariantCulture)).Append(':').Append(l.ReturnedQty.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(':').Append(l.PurchasePrice.ToString("0.###", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }
}

// ---------------------------------------------------------------------- контрольная сверка итога накладной (2026-10-31)

/// <summary>2026-10-31, жалоба владельца: «в бумаге одна сумма, а в закупке другая». Числа, которые ИИ ПРОЧИТАЛ в накладной (не посчитал): итог
/// к оплате, НДС, скидка, число позиций («ИТОГ НАКЛАДНОЙ: {…}»), и то, что ИИ сам насчитал по своей таблице (AiLinesTotal / AiPositions —
/// блок «Общая сумма закупа» / «Всего наименований»). Всё необязательно: накладная может не содержать итога.</summary>
public sealed record InvoiceReference(double? Total, double? Vat, double? Discount, int? Positions, double? AiLinesTotal = null, int? AiPositions = null)
{
    /// <summary>Итог, напечатанный в документе (то, что владелец увидит на бумаге).</summary>
    public bool HasDocumentTotal => Total is > 0;
}

public enum ReconcileState { NoReference, Match, Mismatch }

/// <summary>Итог сверки: сумма строк закупки против итога накладной. FromDocument = эталон прочитан из документа (иначе — лишь таблица ИИ).</summary>
public sealed record InvoiceReconcileResult(ReconcileState State, bool FromDocument, double LinesSum, double Reference, double Expected, double Difference,
    bool VatKnown, bool DiscountKnown, bool PositionsDiffer, int? DocumentPositions);

/// <summary>Причина подозрения на строку: итоговая строка, принятая за товар; нет количества; нет цены закупки.</summary>
public enum InvoiceRowFlag { None, LooksLikeTotal, NoQty, NoPurchase }

public static class InvoiceReconcile
{
    /// <summary>Допуск расхождения, сом: больше — без подтверждения владельца закупку не проводим.</summary>
    public const double Tolerance = 1.0;

    /// <summary>Сумма строк (количество × закупка; строка без числа в сумму не входит) против итога накладной. Накладная могла быть с НДС
    /// или со скидкой — сходящимися считаются: строки, строки + НДС, строки − скидка, строки + НДС − скидка.</summary>
    public static InvoiceReconcileResult Check(double linesSum, int positions, InvoiceReference? r)
    {
        var fromDoc = r?.HasDocumentTotal == true;
        double? reference = fromDoc ? r!.Total : r?.AiLinesTotal is > 0 ? r.AiLinesTotal : null;
        if (r is null || reference is not { } total)
            return new InvoiceReconcileResult(ReconcileState.NoReference, false, Math.Round(linesSum, 2), 0, 0, 0, false, false, false, r?.Positions);
        var vat = fromDoc && r.Vat is > 0 ? r.Vat.Value : 0;
        var discount = fromDoc && r.Discount is > 0 ? r.Discount.Value : 0;
        var candidates = new List<double> { linesSum };
        if (vat > 0) candidates.Add(linesSum + vat);
        if (discount > 0) candidates.Add(linesSum - discount);
        if (vat > 0 && discount > 0) candidates.Add(linesSum + vat - discount);
        // Ближайший к эталону вариант — то, «что должно было быть»; при совпадении разница нулевая.
        var expected = candidates.OrderBy(c => Math.Abs(c - total)).First();
        var diff = Math.Round(expected - total, 2);
        var match = Math.Abs(diff) <= Tolerance + 1e-9;
        // Число позиций: у документа, а при его отсутствии — у таблицы ИИ.
        var docPositions = fromDoc ? r.Positions : r.AiPositions;
        var differ = docPositions is > 0 and { } dp && dp != positions;
        return new InvoiceReconcileResult(match ? ReconcileState.Match : ReconcileState.Mismatch, fromDoc, Math.Round(linesSum, 2), Math.Round(total, 2),
            Math.Round(expected, 2), diff, vat > 0, discount > 0, differ, docPositions);
    }

    /// <summary>Повтор одного товара в накладной объединяется в одну строку: количество складывается, а цена закупки — СРЕДНЕВЗВЕШЕННАЯ,
    /// чтобы сумма (количество × цена) осталась той же, что на бумаге (раньше бралась цена первой строки). Нет цены у одной из частей —
    /// берётся цена другой.</summary>
    public static double MergePurchase(double qty0, double price0, double qty1, double price1)
    {
        if (price0 <= 0) return price1 > 0 ? price1 : 0;
        if (price1 <= 0 || qty0 + qty1 <= 0) return price0;
        return Math.Round((qty0 * price0 + qty1 * price1) / (qty0 + qty1), 4);
    }

    private static readonly System.Text.RegularExpressions.Regex TotalWords = new(
        @"^\s*((итого|итог|всего|подитог|промежуточн\w*|в\s*т\.?\s*ч|ндс|нсп|скидк\w*|к\s+оплате|сумма\s+(налог\w*|ндс|прописью))\b|(grand\s+)?(sub)?total\s*[:\-]?\s*$|vat\s*[:\-]?\s*$)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Строка закупки, похожая на не-товар или неполная. Строка, чья сумма равна итогу накладной при нескольких строках, — итог, принятый за товар.</summary>
    public static InvoiceRowFlag RowFlag(string? name, double? qty, double? purchase, double? documentTotal, int rowCount)
    {
        if (!string.IsNullOrWhiteSpace(name) && TotalWords.IsMatch(name))
            return InvoiceRowFlag.LooksLikeTotal;
        if (documentTotal is > 0 && rowCount > 1 && qty is > 0 && purchase is > 0 && Math.Abs(qty.Value * purchase.Value - documentTotal.Value) <= Tolerance)
            return InvoiceRowFlag.LooksLikeTotal;
        if (qty is not > 0)
            return InvoiceRowFlag.NoQty;
        if (purchase is not > 0)
            return InvoiceRowFlag.NoPurchase;
        return InvoiceRowFlag.None;
    }
}

// ---------------------------------------------------------------------- план правки количества («Б-лайт»)

/// <summary>Как приход был оплачен: от этого зависят возврат (чем компенсируется) и доприход (разрешён только наличными и в долг).</summary>
public enum ReceiptPayKind { Unknown, Cash, Debt, Partial, Drawer }

/// <summary>Откуда можно вернуть товар поставщику: строка самого прихода или строка более раннего доприхода по нему.</summary>
public sealed record ReturnSource(string ReceiptId, string LineId, double Remaining, bool IsAddendum);

/// <summary>Вход для плана по одной строке: что на сервере и сколько хочет владелец.</summary>
public sealed record EditLineInput(string LineId, string ProductId, string Name, string Unit, double OriginalQty, double ReturnedQty, double Purchase, double? Stock,
    IReadOnlyList<ReturnSource> Addenda);

public enum EditAction { None, Return, Addendum }

public sealed record EditLinePlan(EditLineInput Input, double Effective, double NewQty, double Delta, double MinQty, string? Error, EditAction Action);

/// <summary>Одна операция к серверу: возврат по строке прихода (или доприхода) либо позиция доприхода.</summary>
public sealed record PlannedReturn(string ReceiptId, string LineId, string ProductId, string Name, double Qty, double Price);

public sealed record PlannedAddendum(string ProductId, string Name, string Unit, double Qty, double Price);

public sealed class ReceiptEditPlan
{
    public List<EditLinePlan> Lines { get; } = new();
    public List<string> Errors { get; } = new();
    public List<PlannedReturn> Returns { get; } = new();
    public List<PlannedAddendum> Addenda { get; } = new();
    public double TotalBefore { get; set; }
    public double TotalAfter { get; set; }
    public double Diff => Math.Round(TotalAfter - TotalBefore, 2);
    public bool HasChanges => Returns.Count + Addenda.Count > 0;
    public bool IsValid => Errors.Count == 0;
}

public static class ReceiptEditPlanner
{
    private static readonly HashSet<string> FractionalUnits = new(StringComparer.OrdinalIgnoreCase) { "кг", "kg", "г", "гр", "g", "л", "l", "м", "m", "пог.м", "м2", "м²", "m2" };

    public static bool AllowsFraction(EditLineInput l) =>
        FractionalUnits.Contains((l.Unit ?? "").Trim()) || Math.Abs(l.OriginalQty - Math.Round(l.OriginalQty)) > 0.0005;

    /// <summary>Сколько сейчас «живёт» в приходе: проведено − возвращено + доприходы (минус возвращённое из них).</summary>
    public static double Effective(EditLineInput l) =>
        Math.Round(l.OriginalQty - l.ReturnedQty + l.Addenda.Sum(a => a.Remaining), 3);

    /// <summary>Сколько можно убрать: не больше остатка товара на складе и не больше того, что ещё не возвращено (строка + доприходы).
    /// Остаток не получен — убрать нельзя вообще (не знаем, не уйдёт ли склад в минус).</summary>
    public static double Reducible(EditLineInput l)
    {
        if (l.Stock is not { } stock)
            return 0;
        var returnable = Math.Max(0, l.OriginalQty - l.ReturnedQty) + l.Addenda.Sum(a => a.Remaining);
        return Math.Round(Math.Max(0, Math.Min(Math.Max(0, stock), returnable)), 3);
    }

    /// <summary>Нижняя граница количества: «не меньше проданного» (эффективное − сколько можно убрать).</summary>
    public static double MinQty(EditLineInput l) => Math.Round(Math.Max(0, Effective(l) - Reducible(l)), 3);

    public static ReceiptPayKind ParsePayKind(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "cash" => ReceiptPayKind.Cash,
        "debt" => ReceiptPayKind.Debt,
        "partial" => ReceiptPayKind.Partial,
        "drawer" => ReceiptPayKind.Drawer,
        _ => ReceiptPayKind.Unknown,
    };

    /// <summary>Доприход (увеличение количества) — только тем же способом оплаты, который умеет касса без смены: наличные из основной кассы и в долг.</summary>
    public static bool CanAddendum(ReceiptPayKind k) => k is ReceiptPayKind.Cash or ReceiptPayKind.Debt;

    /// <summary>Чем компенсировать возврат по умолчанию: долг/предоплата — зачесть в долг; оплачено — деньги в кассу; неизвестно — без возмещения.</summary>
    public static string DefaultCompensation(ReceiptPayKind k) => k switch
    {
        ReceiptPayKind.Debt or ReceiptPayKind.Partial => "debt_offset",
        ReceiptPayKind.Cash or ReceiptPayKind.Drawer => "cash",
        _ => "none",
    };

    /// <summary>План: для каждой строки с новым количеством — возврат (меньше), доприход (больше) или ничего; проверки минимума, дроби и способа оплаты.
    /// <paramref name="newQty"/> — id строки → новое количество; строки без записи не меняются.</summary>
    public static ReceiptEditPlan Build(IReadOnlyList<EditLineInput> inputs, IReadOnlyDictionary<string, double> newQty, ReceiptPayKind pay)
    {
        var plan = new ReceiptEditPlan();
        // 2026-10-30 (приёмка 1.17.66, 3г): две строки одного товара делят один остаток склада — вторая сверяется с тем, что осталось после первой.
        var reservedStock = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in inputs)
        {
            var l = original.Stock is { } stockNow ? original with { Stock = stockNow - reservedStock.GetValueOrDefault(original.ProductId) } : original;
            var eff = Effective(l);
            var target = newQty.TryGetValue(l.LineId, out var q) ? Math.Round(q, 3) : eff;
            var min = MinQty(l);
            var delta = Math.Round(target - eff, 3);
            string? error = null;
            var action = EditAction.None;
            if (double.IsNaN(target) || double.IsInfinity(target) || target < 0)
                error = Tr.T("количество не может быть отрицательным", "саны терс болбойт", "the quantity can't be negative", "miktar negatif olamaz", "miqdor manfiy bo'lolmaydi");
            else if (Math.Abs(delta) > 0.0005 && !AllowsFraction(l) && Math.Abs(target - Math.Round(target)) > 0.0005)
                error = Tr.T("для штучного товара — целое число", "даана товар үчүн — бүтүн сан", "a whole number for piece goods", "adetli ürün için tam sayı", "donali mahsulot uchun — butun son");
            else if (delta < -0.0005)
            {
                if (l.Stock is null)
                    error = Tr.T("остаток товара не получен — уменьшить нельзя", "товардын калдыгы алынган жок — азайтууга болбойт", "stock not loaded — can't reduce", "stok alınamadı — azaltılamaz", "qoldiq olinmadi — kamaytirib bo'lmaydi");
                else if (target < min - 0.0005)
                    error = Tr.T($"не меньше {Fmt(min)}: на складе {Fmt(Math.Max(0, l.Stock.Value))}, остальное продано", $"{Fmt(min)} дан аз эмес: кампада {Fmt(Math.Max(0, l.Stock.Value))}, калганы сатылган",
                        $"not below {Fmt(min)}: {Fmt(Math.Max(0, l.Stock.Value))} in stock, the rest is sold", $"{Fmt(min)} altına inemez: stokta {Fmt(Math.Max(0, l.Stock.Value))}, kalanı satıldı", $"{Fmt(min)} dan kam emas: omborda {Fmt(Math.Max(0, l.Stock.Value))}, qolgani sotilgan");
                else
                    action = EditAction.Return;
                if (action == EditAction.Return)
                    reservedStock[original.ProductId] = reservedStock.GetValueOrDefault(original.ProductId) - delta;
            }
            else if (delta > 0.0005)
            {
                if (!CanAddendum(pay))
                    error = Tr.T("доприход для этого способа оплаты — после обновления сервера", "бул төлөм ыкмасы үчүн кошумча кирүү — сервер жаңырганда", "supplementary receipt for this payment type — after the server update",
                        "bu ödeme türü için ek giriş — sunucu güncellemesinden sonra", "bu to'lov turi uchun qo'shimcha kirim — server yangilangach");
                else if (l.Purchase <= 0)
                    error = Tr.T("нет цены закупки", "сатып алуу баасы жок", "no purchase price", "alış fiyatı yok", "xarid narxi yo'q");
                else
                    action = EditAction.Addendum;
            }
            plan.Lines.Add(new EditLinePlan(original, eff, target, delta, min, error, action));
            if (error is not null)
                plan.Errors.Add($"{l.Name}: {error}");
            plan.TotalBefore += ReceivingPricing.LineTotal(eff, l.Purchase);
            plan.TotalAfter += ReceivingPricing.LineTotal(error is null ? target : eff, l.Purchase);

            if (action == EditAction.Return)
            {
                // Сначала возвращаем из доприходов (последнее добавленное — первым), затем из самой строки прихода.
                var left = -delta;
                foreach (var a in l.Addenda.Where(a => a.Remaining > 0.0005).Reverse())
                {
                    if (left <= 0.0005) break;
                    var take = Math.Min(left, a.Remaining);
                    plan.Returns.Add(new PlannedReturn(a.ReceiptId, a.LineId, l.ProductId, l.Name, Math.Round(take, 3), l.Purchase));
                    left = Math.Round(left - take, 3);
                }
                if (left > 0.0005)
                    plan.Returns.Add(new PlannedReturn("", l.LineId, l.ProductId, l.Name, Math.Round(left, 3), l.Purchase));
            }
            else if (action == EditAction.Addendum)
                plan.Addenda.Add(new PlannedAddendum(l.ProductId, l.Name, l.Unit, delta, l.Purchase));
        }
        plan.TotalBefore = Math.Round(plan.TotalBefore, 2);
        plan.TotalAfter = Math.Round(plan.TotalAfter, 2);
        return plan;
    }

    public static string Fmt(double v) => v.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
}
