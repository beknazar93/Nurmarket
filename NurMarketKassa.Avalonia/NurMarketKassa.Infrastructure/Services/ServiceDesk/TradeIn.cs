using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13: статус выкупа «Trade-in / скупки». «Оценён» — устройство у нас, расчёт с клиентом ещё не закрыт; «Выкуплен» — вся сумма выплачена
/// из кассы и/или зачтена в оплаченный чек; «Оприходован» — устройство принято на склад как б/у; «Отменён» — клиент передумал (пока деньги не двигались).</summary>
public enum TradeInStatus
{
    Appraised,
    Bought,
    Stocked,
    Cancelled,
}

public sealed class TradeInLogEntry
{
    public DateTime At { get; set; }
    public string By { get; set; } = "";
    public string Action { get; set; } = "";
    public decimal Amount { get; set; }
}

/// <summary>Один выкуп. Деньги — decimal. Сумма выкупа делится на три части: выплачено из кассы (<see cref="PaidOut"/>), зачёт в чеке, ждущий оплаты
/// (<see cref="CreditAdded"/>), зачёт в оплаченном чеке (<see cref="CreditConfirmed"/>); остаток — <see cref="TradeInRules.Outstanding"/>.</summary>
public sealed class TradeInRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public int Number { get; set; }
    public DateTime CreatedAt { get; set; }
    public string ClientName { get; set; } = "";
    public string ClientPhone { get; set; } = "";
    /// <summary>Паспорт / ID клиента: серия, номер, кем и когда выдан (скупка у физлица).</summary>
    public string ClientDocument { get; set; } = "";
    public string Device { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Condition { get; set; } = "";
    public string Equipment { get; set; } = "";
    /// <summary>Фото устройства (пути к файлам на этом компьютере), по желанию.</summary>
    public List<string> Photos { get; set; } = new();
    /// <summary>Оценка мастера (справочно), сумма выкупа — что платим клиенту.</summary>
    public decimal Appraisal { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidOut { get; set; }
    public decimal CreditAdded { get; set; }
    public decimal CreditConfirmed { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TradeInStatus Status { get; set; }
    /// <summary>Цена продажи б/у товара при оприходовании.</summary>
    public decimal ResalePrice { get; set; }
    public DateTime? StockedAt { get; set; }
    public string CreatedBy { get; set; } = "";
    public List<TradeInLogEntry> Log { get; set; } = new();
}

public sealed class TradeInData
{
    public int Version { get; set; } = 1;
    public int LastNumber { get; set; }
    public List<TradeInRecord> Records { get; set; } = new();
}

/// <summary>Чистая логика «Trade-in / скупки»: расчёт остатка, зачёт в чек, проверки, тексты и печатные формы.</summary>
public static class TradeInRules
{
    public static string Code(int number) => "Т-" + number.ToString("0000", CultureInfo.InvariantCulture);

    public static string Code(TradeInRecord r) => Code(r.Number);

    public static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Сколько ещё не рассчитано с клиентом: сумма выкупа минус выплаченное и зачтённое (в чеке и подтверждённое).</summary>
    public static decimal Outstanding(TradeInRecord r) =>
        Math.Max(0m, Money(r.Amount) - Money(r.PaidOut) - Money(r.CreditAdded) - Money(r.CreditConfirmed));

    /// <summary>Расчёт закрыт полностью: ничего не осталось и нет зачёта, ждущего оплаты чека.</summary>
    public static bool IsSettled(TradeInRecord r) => Outstanding(r) == 0m && Money(r.CreditAdded) == 0m;

    /// <summary>Сколько можно зачесть в чек с итогом <paramref name="cartTotal"/>: не больше остатка и не больше итога чека (итог не уходит в минус).</summary>
    public static decimal CreditLimit(TradeInRecord r, decimal cartTotal) =>
        r.Status != TradeInStatus.Appraised || r.CreditAdded > 0 ? 0m : Math.Max(0m, Math.Min(Outstanding(r), Money(cartTotal)));

    /// <summary>Выплатить из кассы можно остаток (без зачёта, ждущего оплаты чека).</summary>
    public static decimal PayoutLimit(TradeInRecord r) =>
        r.Status != TradeInStatus.Appraised || r.CreditAdded > 0 ? 0m : Outstanding(r);

    /// <summary>Статус по деньгам: «Оценён», пока расчёт не закрыт; «Выкуплен», когда закрыт (оприходованный и отменённый не меняются).</summary>
    public static TradeInStatus StatusFor(TradeInRecord r) =>
        r.Status is TradeInStatus.Stocked or TradeInStatus.Cancelled ? r.Status
        : IsSettled(r) ? TradeInStatus.Bought : TradeInStatus.Appraised;

    /// <summary>Отменить можно, пока деньги не двигались.</summary>
    public static bool CanCancel(TradeInRecord r) =>
        r.Status == TradeInStatus.Appraised && r.PaidOut == 0m && r.CreditAdded == 0m && r.CreditConfirmed == 0m;

    public static bool CanStock(TradeInRecord r) => r.Status == TradeInStatus.Bought;

    public static string StatusName(TradeInStatus s) => s switch
    {
        TradeInStatus.Appraised => Tr.T("Оценён", "Бааланды", "Appraised", "Değerlendirildi", "Baholandi"),
        TradeInStatus.Bought => Tr.T("Выкуплен", "Сатып алынды", "Bought", "Satın alındı", "Sotib olindi"),
        TradeInStatus.Stocked => Tr.T("На складе", "Кампада", "In stock", "Stokta", "Omborda"),
        _ => Tr.T("Отменён", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"),
    };

    public static List<string> ValidateNew(TradeInRecord r)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(r.ClientName))
            problems.Add(Tr.T("Впишите имя клиента.", "Кардардын атын жазыңыз.", "Enter the customer's name.", "Müşterinin adını girin.", "Mijoz ismini kiriting."));
        if (string.IsNullOrWhiteSpace(r.ClientDocument))
            problems.Add(Tr.T("Впишите данные паспорта / ID клиента — скупка идёт у физического лица.", "Кардардын паспорт / ID маалыматтарын жазыңыз — жеке адамдан сатып алабыз.", "Enter the customer's passport / ID — we buy from an individual.", "Müşterinin pasaport / kimlik bilgilerini girin — bireyden alım yapılıyor.", "Mijozning pasport / ID ma'lumotlarini kiriting — jismoniy shaxsdan sotib olinadi."));
        if (!ServerAddons.PhoneLooksValid(r.ClientPhone))
            problems.Add(Tr.T("Впишите телефон клиента (не меньше 9 цифр).", "Кардардын телефонун жазыңыз (кеминде 9 сан).", "Enter the customer's phone (at least 9 digits).", "Müşterinin telefonunu girin (en az 9 hane).", "Mijoz telefonini kiriting (kamida 9 raqam)."));
        if (string.IsNullOrWhiteSpace(r.Device))
            problems.Add(Tr.T("Впишите устройство.", "Түзмөктү жазыңыз.", "Enter the device.", "Cihazı girin.", "Qurilmani kiriting."));
        if (string.IsNullOrWhiteSpace(r.Serial))
            problems.Add(Tr.T("Впишите серийный номер / IMEI — без него б/у не принимаем.", "Сериялык номер / IMEI жазыңыз — аны жок болсо б/у кабыл албайбыз.", "Enter the serial number / IMEI — we do not accept used items without it.", "Seri numarası / IMEI girin — o olmadan ikinci el almıyoruz.", "Seriya raqami / IMEI ni kiriting — usiz b/u qabul qilmaymiz."));
        else if (ImeiValidator.Check(r.Serial, out _) is ImeiValidator.Verdict.BadFormat or ImeiValidator.Verdict.BadImei)
            problems.Add(Tr.T("Серийный номер / IMEI не прошёл проверку — проверьте цифры.", "Сериялык номер / IMEI текшерүүдөн өткөн жок — сандарды текшериңиз.", "The serial number / IMEI failed the check — check the digits.", "Seri numarası / IMEI kontrolü geçemedi — rakamları kontrol edin.", "Seriya raqami / IMEI tekshiruvdan o'tmadi — raqamlarni tekshiring."));
        if (r.Amount < 0 || r.Appraisal < 0)
            problems.Add(Tr.T("Суммы не могут быть отрицательными.", "Суммалар терс болушу мүмкүн эмес.", "Amounts cannot be negative.", "Tutarlar negatif olamaz.", "Summalar manfiy bo'lishi mumkin emas."));
        else if (r.Amount <= 0)
            problems.Add(Tr.T("Впишите сумму выкупа.", "Сатып алуу суммасын жазыңыз.", "Enter the buy-back amount.", "Alım tutarını girin.", "Sotib olish summasini kiriting."));
        return problems;
    }

    /// <summary>Строка приёмки для склада: б/у как новый товар, закупка = сумма выкупа, штрихкод = серийный номер (по нему устройство находят в продаже), 1 шт.</summary>
    public static ReceivingLineVm StockLine(TradeInRecord r, decimal resalePrice) => new()
    {
        ProductName = Tr.T("Б/у ", "Б/у ", "Used ", "İkinci el ", "B/u ") + r.Device.Trim(),
        Barcode = ImeiValidator.Normalize(r.Serial),
        Unit = "шт",
        Source = ReceivingSource.Unknown,
        Quantity = 1,
        PurchasePrice = (double)Money(r.Amount),
        SalePrice = (double)Money(resalePrice),
    };

    // ───────────────────────── печатные формы ─────────────────────────

    public static string BuildAct(TradeInRecord r) => BuildAct(r, ReceiptLayout.CharWidth);

    private static string Day(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Sum(decimal v) => ReceiptLineLayout.WithSom(Money(v).ToString("N2", CultureInfo.CurrentCulture));

    private static void Field(StringBuilder sb, string label, string? value, int width)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        foreach (var p in ReceiptLineLayout.WrapLeft(label + ": " + value.Trim(), width))
            sb.Append(p).Append('\n');
    }

    /// <summary>Акт приёма б/у у физического лица: данные продавца, устройство, сумма и способ расчёта, подписи двух сторон.</summary>
    public static string BuildAct(TradeInRecord r, int width)
    {
        var prefs = UserPreferences.Instance;
        var sb = new StringBuilder(700);
        sb.Append(ReceiptLineLayout.Center(Tr.T("АКТ ПРИЁМА Б/У ТОВАРА", "Б/У ТОВАР КАБЫЛ АЛУУ АКТЫСЫ", "USED ITEM ACCEPTANCE ACT", "İKİNCİ EL ÜRÜN KABUL TUTANAĞI", "B/U TOVAR QABUL QILISH DALOLATNOMASI"), width)).Append('\n').Append('\n');
        if (!string.IsNullOrWhiteSpace(prefs.StoreName))
            foreach (var p in ReceiptLineLayout.WrapLeft(prefs.StoreName.Trim(), width)) sb.Append(p).Append('\n');
        if (!string.IsNullOrWhiteSpace(prefs.StoreInn))
            sb.Append($"{Tr.T("ИНН", "ИНН", "TIN", "Vergi no", "STIR")}: {prefs.StoreInn.Trim()}").Append('\n');
        sb.Append(new string('-', width)).Append('\n');
        sb.Append($"{Tr.T("Акт №", "Акт №", "Act No.", "Tutanak No", "Dalolatnoma №")} {Code(r)} / {Day(r.CreatedAt)}").Append('\n');
        Field(sb, Tr.T("Продавец (клиент)", "Сатуучу (кардар)", "Seller (customer)", "Satıcı (müşteri)", "Sotuvchi (mijoz)"), r.ClientName, width);
        Field(sb, Tr.T("Паспорт / ID", "Паспорт / ID", "Passport / ID", "Pasaport / Kimlik", "Pasport / ID"), r.ClientDocument, width);
        Field(sb, Tr.T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), r.ClientPhone, width);
        sb.Append(new string('-', width)).Append('\n');
        Field(sb, Tr.T("Устройство", "Түзмөк", "Device", "Cihaz", "Qurilma"), r.Device, width);
        Field(sb, ImeiValidator.LabelFor(r.Serial), r.Serial, width);
        Field(sb, Tr.T("Состояние", "Абалы", "Condition", "Durum", "Holati"), r.Condition, width);
        Field(sb, Tr.T("Комплектация", "Комплектация", "Included items", "Teslim edilenler", "Komplektatsiya"), r.Equipment, width);
        sb.Append(new string('-', width)).Append('\n');
        sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Сумма выкупа", "Сатып алуу суммасы", "Buy-back amount", "Alım tutarı", "Sotib olish summasi"), Sum(r.Amount), width)).Append('\n');
        if (r.PaidOut > 0)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Выплачено из кассы", "Кассадан төлөндү", "Paid out from the till", "Kasadan ödendi", "Kassadan to'landi"), Sum(r.PaidOut), width)).Append('\n');
        var credit = Money(r.CreditAdded + r.CreditConfirmed);
        if (credit > 0)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Зачтено в покупку", "Сатып алууга эсептелди", "Credited to the purchase", "Alışverişe mahsup edildi", "Xaridga hisobga olindi"), Sum(credit), width)).Append('\n');
        sb.Append('\n');
        foreach (var p in ReceiptLineLayout.WrapLeft(Tr.T(
            "Продавец подтверждает, что устройство принадлежит ему, не заложено, не находится в розыске и передано покупателю без претензий.",
            "Сатуучу түзмөк өзүнүкү экенин, күрөөгө коюлбаганын, издөөдө эместигин жана доомаатсыз өткөрүлгөнүн ырастайт.",
            "The seller confirms the device is theirs, not pledged, not reported stolen and is handed over without claims.",
            "Satıcı, cihazın kendisine ait olduğunu, rehinli veya aranan olmadığını ve itirazsız devredildiğini onaylar.",
            "Sotuvchi qurilma o'zinikiligini, garovga qo'yilmaganini, qidiruvda emasligini va e'tirozsiz topshirilganini tasdiqlaydi."), width))
            sb.Append(p).Append('\n');
        sb.Append('\n');
        sb.Append(Tr.T("Подпись продавца: ____________", "Сатуучунун колу: ____________", "Seller's signature: ____________", "Satıcı imzası: ____________", "Sotuvchi imzosi: ____________")).Append('\n');
        sb.Append(Tr.T("Принял: ____________", "Кабыл алды: ____________", "Accepted by: ____________", "Teslim alan: ____________", "Qabul qildi: ____________")).Append('\n');
        return sb.ToString();
    }
}

/// <summary>2026-11-13: локальное хранилище выкупов — файл trade-in-records.json (схема pos_local.db не меняется), общий для кассы и программы владельца на одном ПК.
/// Деньги стор не двигает: выплата — изъятие из кассы, зачёт — отрицательная строка чека, приход товара — обычная приёмка склада; здесь только записываются суммы.</summary>
public sealed class TradeInStore
{
    public const string FileName = "trade-in-records.json";
    private const int MaxLog = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTime> _now;

    public TradeInStore(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
    }

    public static TradeInStore Default => new(Path.Combine(AppPaths.KassaLocal, FileName));

    public TradeInData Load()
    {
        var text = AtomicFile.ReadAllText(_path);
        if (string.IsNullOrWhiteSpace(text))
            return new TradeInData();
        return Parse(text);
    }

    private static TradeInData Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TradeInData();
        try { return JsonSerializer.Deserialize<TradeInData>(text, Json) ?? new TradeInData(); }
        catch (JsonException) { return new TradeInData(); }
    }

    private void Mutate(Func<TradeInData, bool> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Update(_path, text =>
        {
            var data = Parse(text);
            return change(data) ? JsonSerializer.Serialize(data, Json) : null;
        });
    }

    public List<TradeInRecord> All() => Load().Records.OrderByDescending(r => r.Number).ToList();

    public TradeInRecord? Find(string id) => Load().Records.FirstOrDefault(r => r.Id == id);

    public TradeInRecord Create(TradeInRecord draft, string by)
    {
        TradeInRecord? saved = null;
        Mutate(d =>
        {
            d.LastNumber = Math.Max(d.LastNumber, d.Records.Count == 0 ? 0 : d.Records.Max(r => r.Number)) + 1;
            draft.Number = d.LastNumber;
            draft.CreatedAt = _now();
            draft.CreatedBy = by ?? "";
            draft.Status = TradeInStatus.Appraised;
            draft.Serial = ImeiValidator.Normalize(draft.Serial);
            draft.Amount = TradeInRules.Money(draft.Amount);
            draft.Appraisal = TradeInRules.Money(draft.Appraisal);
            draft.Log.Add(new TradeInLogEntry { At = draft.CreatedAt, By = by ?? "", Action = "create", Amount = draft.Amount });
            d.Records.Add(draft);
            saved = draft;
            return true;
        });
        return saved!;
    }

    public bool Update(string id, Func<TradeInRecord, bool> change, string by, string action, decimal amount = 0m)
    {
        var done = false;
        Mutate(d =>
        {
            var r = d.Records.FirstOrDefault(x => x.Id == id);
            if (r is null || !change(r))
                return false;
            r.Status = TradeInRules.StatusFor(r);
            r.Log.Add(new TradeInLogEntry { At = _now(), By = by ?? "", Action = action, Amount = amount });
            if (r.Log.Count > MaxLog)
                r.Log.RemoveRange(0, r.Log.Count - MaxLog);
            done = true;
            return true;
        });
        return done;
    }

    /// <summary>Выплата из кассы проведена (изъятие записано кассой) — записываем. Не больше остатка.</summary>
    public bool RecordPayout(string id, decimal amount, string by) => Update(id, r =>
    {
        amount = TradeInRules.Money(amount);
        if (amount <= 0 || amount > TradeInRules.PayoutLimit(r))
            return false;
        r.PaidOut = TradeInRules.Money(r.PaidOut + amount);
        return true;
    }, by, "payout", amount);

    /// <summary>Зачёт добавлен в чек как отрицательная строка — ждёт оплаты чека. Не больше остатка.</summary>
    public bool RecordCreditAdded(string id, decimal amount, string by) => Update(id, r =>
    {
        amount = TradeInRules.Money(amount);
        if (amount <= 0 || r.Status != TradeInStatus.Appraised || r.CreditAdded > 0 || amount > TradeInRules.Outstanding(r))
            return false;
        r.CreditAdded = amount;
        return true;
    }, by, "credit-added", amount);

    /// <summary>Чек с зачётом оплачен — зачёт подтверждён.</summary>
    public bool ConfirmCredit(string id, string by) => Update(id, r =>
    {
        if (r.CreditAdded <= 0)
            return false;
        r.CreditConfirmed = TradeInRules.Money(r.CreditConfirmed + r.CreditAdded);
        r.CreditAdded = 0;
        return true;
    }, by, "credit-confirmed");

    /// <summary>Чек не оплачен / строку убрали — зачёт снимается, сумма снова в остатке.</summary>
    public bool CancelCredit(string id, string by) => Update(id, r =>
    {
        if (r.CreditAdded <= 0)
            return false;
        r.CreditAdded = 0;
        return true;
    }, by, "credit-cancelled");

    public bool Cancel(string id, string by) => Update(id, r =>
    {
        if (!TradeInRules.CanCancel(r))
            return false;
        r.Status = TradeInStatus.Cancelled;
        return true;
    }, by, "cancel");

    /// <summary>Устройство принято на склад (приёмка прошла) — запись цены продажи.</summary>
    public bool MarkStocked(string id, decimal resalePrice, string by) => Update(id, r =>
    {
        if (!TradeInRules.CanStock(r))
            return false;
        r.ResalePrice = TradeInRules.Money(resalePrice);
        r.StockedAt = _now();
        r.Status = TradeInStatus.Stocked;
        return true;
    }, by, "stocked", resalePrice);

    public static IEnumerable<TradeInRecord> Filter(IEnumerable<TradeInRecord> all, string? query, TradeInStatus? status)
    {
        var q = (query ?? "").Trim();
        var qs = ImeiValidator.Normalize(q);
        var digits = q.TrimStart('Т', 'т', 'T', 't', '-').TrimStart('0');
        return all.Where(r => status is null || r.Status == status).Where(r =>
            q.Length == 0
            || TradeInRules.Code(r).Contains(q, StringComparison.OrdinalIgnoreCase)
            || (digits.Length > 0 && r.Number.ToString(CultureInfo.InvariantCulture) == digits)
            || r.ClientName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.ClientPhone.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Device.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (qs.Length >= 4 && r.Serial.Contains(qs, StringComparison.OrdinalIgnoreCase)));
    }
}
