using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21, владелец: «КП и товарную накладную, выставление счёта — … во вкладке Клиенты». Вид документа клиента.</summary>
public enum ClientDocKind { Quote, Invoice, Waybill }

/// <summary>2026-10-21: строка документа. Имена полей — как у телефона (NurMarketMobile/lib/features/admin/quotes.dart, QuoteLine:
/// p, n, u, q, pr), чтобы КП/счёт с телефона открывался здесь и наоборот. <see cref="Price"/> — цена ПОСЛЕ скидки строки:
/// телефон считает сумму как q × pr. Цена до скидки и процент — свои поля (телефон их не знает и не теряет смысла).</summary>
public sealed class ClientDocLine
{
    [JsonPropertyName("p")] public string ProductId { get; set; } = "";
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("u")] public string Unit { get; set; } = "шт";
    [JsonPropertyName("q")] public double Qty { get; set; } = 1;
    /// <summary>Цена за единицу после скидки строки (как у телефона).</summary>
    [JsonPropertyName("pr")] public double Price { get; set; }
    /// <summary>Цена до скидки (есть только при скидке строки).</summary>
    [JsonPropertyName("pr0")] public double? ListPrice { get; set; }
    /// <summary>Скидка строки, %.</summary>
    [JsonPropertyName("dp")] public double? DiscountPercent { get; set; }
    /// <summary>Свободная строка (услуга, доставка) — не из каталога.</summary>
    [JsonPropertyName("f")] public bool? Free { get; set; }
    /// <summary>2026-10-21 (ревью документов, A4): незнакомые поля строки (телефон, будущие версии) — сохраняются как есть.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public double BasePrice => ListPrice ?? Price;
    [JsonIgnore] public double Sum => ClientDoc.Round2(Qty * Price);

    /// <summary>Цена до скидки и скидка % → цена строки (pr) и поля скидки.</summary>
    public void SetPrice(double basePrice, double discountPercent)
    {
        basePrice = Math.Max(0, basePrice);
        discountPercent = Math.Clamp(discountPercent, 0, 100);
        if (discountPercent > 0.0001)
        {
            ListPrice = ClientDoc.Round2(basePrice);
            DiscountPercent = Math.Round(discountPercent, 2);
            Price = ClientDoc.Round2(basePrice * (1 - discountPercent / 100.0));
        }
        else
        {
            ListPrice = null;
            DiscountPercent = null;
            Price = ClientDoc.Round2(basePrice);
        }
    }

    public ClientDocLine Clone() => (ClientDocLine)MemberwiseClone();
}

/// <summary>2026-10-21: документ клиента — КП, счёт на оплату или накладная. Формат JSON совместим с телефоном (QuoteDoc в
/// quotes.dart: id, number, quote, client, phone, inn, lines, discount, until, status, paid, created, basedOn, history); свои
/// поля (kind, clientId, address, note, vat, template, lang, updated, by) телефон пропускает.
/// Статусы: КП — draft | sent | accepted | declined; счёт — draft | issued | sent | partial | paid; накладная — draft | sent |
/// accepted. «Просрочен» не хранится: срок (until) прошёл, а КП не принято / счёт не оплачен.</summary>
public sealed class ClientDoc
{
    [JsonPropertyName("id")] public string Id { get; set; } = NewId();
    [JsonPropertyName("number")] public string Number { get; set; } = "";
    /// <summary>Телефон: true — КП, false — счёт. Накладная — false и <see cref="KindCode"/> = "waybill".</summary>
    [JsonPropertyName("quote")] public bool IsQuote { get; set; } = true;
    [JsonPropertyName("kind")] public string? KindCode { get; set; }
    [JsonPropertyName("client")] public string Client { get; set; } = "";
    [JsonPropertyName("clientId")] public string? ClientId { get; set; }
    [JsonPropertyName("phone")] public string Phone { get; set; } = "";
    [JsonPropertyName("inn")] public string Inn { get; set; } = "";
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("lines")] public List<ClientDocLine> Lines { get; set; } = new();
    /// <summary>Скидка на весь документ, сом (как у телефона — сумма, не процент).</summary>
    [JsonPropertyName("discount")] public double Discount { get; set; }
    [JsonPropertyName("until")] public string UntilRaw { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "draft";
    [JsonPropertyName("paid")] public double Paid { get; set; }
    /// <summary>Дата документа (у телефона — дата создания, по ней печатается «от …»).</summary>
    [JsonPropertyName("created")] public string CreatedRaw { get; set; } = "";
    [JsonPropertyName("basedOn")] public string BasedOn { get; set; } = "";
    [JsonPropertyName("history")] public List<string> History { get; set; } = new();
    [JsonPropertyName("note")] public string? Note { get; set; }
    /// <summary>НДС, % «в том числе» (null — без НДС).</summary>
    [JsonPropertyName("vat")] public double? VatPercent { get; set; }
    /// <summary>Шаблон печати: classic | modern | minimal (null — шаблон по умолчанию для вида).</summary>
    [JsonPropertyName("template")] public string? Template { get; set; }
    /// <summary>Язык бланка: ru | ky.</summary>
    [JsonPropertyName("lang")] public string? Lang { get; set; }
    [JsonPropertyName("updated")] public string? UpdatedRaw { get; set; }
    /// <summary>2026-10-21 (ревью документов, A4): общий с телефоном признак версии — ISO 8601 UTC («2026-10-21T08:15:30.123Z»),
    /// ставится при каждом изменении. При слиянии по id побеждает больший; без поля — старше любого с полем.</summary>
    [JsonPropertyName("updatedAt")] public string? UpdatedAtRaw { get; set; }
    /// <summary>2026-10-21 (ревью документов, A4): «надгробие» удалённого документа {"id", "deleted": true, "updatedAt"} — хранится
    /// 90 дней, чтобы удалённое не «воскресало» из копии другого ПК/телефона; в списках не показывается.</summary>
    [JsonPropertyName("deleted")] public bool? IsDeleted { get; set; }
    [JsonPropertyName("by")] public string? Author { get; set; }
    /// <summary>2026-10-21 (ревью документов, A4): незнакомые поля документа — сохраняются как есть при записи.</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static string NewId() => (DateTime.UtcNow.Ticks / 10).ToString(CultureInfo.InvariantCulture) + Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);

    [JsonIgnore]
    public ClientDocKind Kind
    {
        get => string.Equals(KindCode, "waybill", StringComparison.OrdinalIgnoreCase) ? ClientDocKind.Waybill
            : IsQuote ? ClientDocKind.Quote : ClientDocKind.Invoice;
        set
        {
            IsQuote = value == ClientDocKind.Quote;
            KindCode = value switch { ClientDocKind.Waybill => "waybill", ClientDocKind.Invoice => "invoice", _ => "quote" };
        }
    }

    [JsonIgnore]
    public DateTime Date
    {
        get => ParseDate(CreatedRaw) ?? DateTime.Today;
        set => CreatedRaw = FormatDate(value);
    }

    [JsonIgnore]
    public DateTime Until
    {
        get => ParseDate(UntilRaw) ?? Date.Date.AddDays(3);
        set => UntilRaw = FormatDate(value.Date);
    }

    [JsonIgnore] public DateTime UpdatedAt => ParseUtc(UpdatedAtRaw)?.ToLocalTime() ?? ParseDate(UpdatedRaw) ?? ParseDate(CreatedRaw) ?? DateTime.MinValue;

    // 2026-10-21 (ревью документов, A4): + updatedAt (UTC) — по нему слияние с другими ПК и телефоном.
    public void Touch()
    {
        UpdatedRaw = FormatDate(DateTime.Now);
        UpdatedAtRaw = FormatUtc(DateTime.UtcNow);
    }

    /// <summary>2026-10-21 (ревью документов, A4): «2026-10-21T08:15:30.123Z».</summary>
    public static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>ISO 8601 → UTC; без зоны считается UTC. Не дата — null.</summary>
    public static DateTime? ParseUtc(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    [JsonIgnore] public double Subtotal => Round2(Lines.Sum(l => l.Sum));
    /// <summary>Скидки строк (по цене до скидки) — для строки «в т.ч. скидка».</summary>
    [JsonIgnore] public double LineDiscounts => Round2(Lines.Sum(l => l.ListPrice is { } lp ? Math.Max(0, lp - l.Price) * l.Qty : 0));
    [JsonIgnore] public double Total => Round2(Math.Max(0, Subtotal - Math.Max(0, Discount)));
    [JsonIgnore] public double Left => Round2(Math.Max(0, Total - Paid));
    /// <summary>НДС в том числе: итог × ставка / (100 + ставка).</summary>
    [JsonIgnore] public double VatAmount => VatPercent is > 0 and var v ? Round2(Total * v / (100 + v)) : 0;

    /// <summary>Срок прошёл: КП не принято и не отклонено, счёт не оплачен. Накладная не просрочивается.</summary>
    [JsonIgnore]
    public bool Overdue => Kind switch
    {
        ClientDocKind.Quote => Status is "draft" or "sent" && Until.Date < DateTime.Today,
        ClientDocKind.Invoice => Status != "paid" && Until.Date < DateTime.Today,
        _ => false,
    };

    public static double Round2(double v) => Math.Round(v + (v >= 0 ? 1e-9 : -1e-9), 2, MidpointRounding.AwayFromZero);

    /// <summary>Как телефон: «2026-10-24T00:00:00.000» (местное время, без зоны).</summary>
    public static string FormatDate(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static DateTime? ParseDate(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;

    public ClientDoc Clone()
    {
        var c = (ClientDoc)MemberwiseClone();
        c.Lines = Lines.Select(l => l.Clone()).ToList();
        c.History = History.ToList();
        c.Extra = Extra == null ? null : new(Extra);
        return c;
    }

    /// <summary>2026-10-21 (ревью документов, A3): заменить содержимое этого документа другим (открытый редактор держит ссылку).</summary>
    public void CopyFrom(ClientDoc other)
    {
        var c = other.Clone();
        foreach (var p in typeof(ClientDoc).GetProperties())
            if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !Attribute.IsDefined(p, typeof(JsonIgnoreAttribute)))
                p.SetValue(this, p.GetValue(c));
    }

    /// <summary>Префикс номера по виду: КП-0001 / СЧ-0001 (как телефон), НК-0001 — накладная.</summary>
    public static string Prefix(ClientDocKind kind) => kind switch { ClientDocKind.Quote => "КП", ClientDocKind.Invoice => "СЧ", _ => "НК" };

    private static readonly Regex NumberRx = new(@"^\s*(КП|СЧ|НК)\s*-\s*0*(\d{1,9})\s*$", RegexOptions.Compiled);

    /// <summary>Порядковый номер «СЧ-0012» → 12 (для своего вида), иначе null.</summary>
    public static int? NumberIndex(string? number, ClientDocKind kind)
    {
        var m = NumberRx.Match(number ?? "");
        return m.Success && m.Groups[1].Value == Prefix(kind) && int.TryParse(m.Groups[2].Value, out var n) ? n : null;
    }

    public void AddHistory(string text) => History.Add(DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " · " + text);
}

/// <summary>2026-10-21: реквизиты продавца и настройки бланков — запоминаются (на этом ПК и в общих настройках компании, кроме
/// логотипа: путь к картинке — только этого ПК).</summary>
public sealed class ClientDocProfile
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("address")] public string? Address { get; set; }
    [JsonPropertyName("inn")] public string? Inn { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("bank")] public string? Bank { get; set; }
    [JsonPropertyName("bik")] public string? Bik { get; set; }
    [JsonPropertyName("account")] public string? Account { get; set; }
    [JsonPropertyName("director")] public string? Director { get; set; }
    [JsonPropertyName("accountant")] public string? Accountant { get; set; }
    /// <summary>НДС по умолчанию для новых документов, % (null — без НДС).</summary>
    [JsonPropertyName("vat")] public double? VatPercent { get; set; }
    /// <summary>Шаблон по виду: quote/invoice/waybill → classic|modern|minimal.</summary>
    [JsonPropertyName("templates")] public Dictionary<string, string> Templates { get; set; } = new();
    /// <summary>Счётчики номеров по виду (kp/inv/wb) — номер не повторяется и после удаления документа.</summary>
    [JsonPropertyName("counters")] public Dictionary<string, int> Counters { get; set; } = new();
    [JsonPropertyName("terms")] public Dictionary<string, string> Terms { get; set; } = new();
    /// <summary>Только этот ПК: путь к логотипу (в общие настройки не уходит).</summary>
    [JsonPropertyName("logo")] public string? LogoPath { get; set; }
    [JsonPropertyName("stamp")] public bool ShowStamp { get; set; } = true;

    public ClientDocProfile Clone()
    {
        var c = (ClientDocProfile)MemberwiseClone();
        c.Templates = new(Templates);
        c.Counters = new(Counters);
        c.Terms = new(Terms);
        return c;
    }

    public static string KindKey(ClientDocKind k) => k switch { ClientDocKind.Quote => "quote", ClientDocKind.Invoice => "invoice", _ => "waybill" };
    public static string CounterKey(ClientDocKind k) => k switch { ClientDocKind.Quote => "kp", ClientDocKind.Invoice => "inv", _ => "wb" };
}
