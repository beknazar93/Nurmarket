using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

// 2026-11-05, жалоба владельца: «в десктопе возврат поставщику — нет возможности распечатки накладной для возврата поставщику!».
// Накладная на возврат поставщику строится ТОЛЬКО из ответа сервера (список и карточка возврата suppliers/returns/): номер сервера («ВП-…»),
// дата, поставщик, приход-основание, причина, состав с закупочными ценами. Локального снимка нет — старые возвраты печатаются так же, а
// повторная печать даёт тот же документ. Касса ничего не пишет на сервер: возврат, остаток и долг поставщику документ не меняет.
// Рисование — SupplierReturnDocumentRenderer (PDF, печать), Word и Excel — SupplierReturnDocumentOffice; страница, подписи и шрифты
// общие с документами перемещения (TransferDocumentRenderer).

/// <summary>Строка накладной на возврат. Цена — закупочная; <paramref name="LineTotal"/> — сумма строки по серверу (line_total), 0 — считаем сами.</summary>
public sealed record SupplierReturnDocLine(string Name, string Article, string Barcode, string Unit, double Qty, double Price, double LineTotal = 0)
{
    public double Sum => Math.Round(LineTotal > 0 ? LineTotal : Qty * Price, 2, MidpointRounding.AwayFromZero);
}

/// <summary>Всё, что нужно для рисования накладной на возврат поставщику. Язык (ru/ky/en/tr/uz) — язык интерфейса на момент печати.</summary>
public sealed class SupplierReturnDocData
{
    /// <summary>id возврата на сервере (ключ памяти последнего просмотра).</summary>
    public string ReturnId { get; set; } = "";
    public string Number { get; set; } = "";
    /// <summary>Дата возврата (по серверу).</summary>
    public DateTime Date { get; set; }
    /// <summary>Когда документ напечатан/сохранён (с поясом «UTC+06» в подвале).</summary>
    public DateTime CreatedAt { get; set; }
    public string Lang { get; set; } = "ru";
    public string OrgName { get; set; } = "";
    public string OrgInn { get; set; } = "";
    public string OrgAddress { get; set; } = "";
    public string OrgPhone { get; set; } = "";
    public string Warehouse { get; set; } = "";
    public string SupplierName { get; set; } = "";
    public string SupplierInn { get; set; } = "";
    public string SupplierAddress { get; set; } = "";
    public string SupplierPhone { get; set; } = "";
    /// <summary>Номер прихода-основания («ПР-2026-000073»); пусто — возврат со склада без привязки к приходу.</summary>
    public string ReceiptNumber { get; set; } = "";
    public bool HasReceipt { get; set; }
    /// <summary>defect / surplus / wrong_item / expired / other.</summary>
    public string Reason { get; set; } = "";
    /// <summary>cash / debt_offset / none.</summary>
    public string Compensation { get; set; } = "";
    public string Comment { get; set; } = "";
    public string IssuedBy { get; set; } = "";
    /// <summary>Итог по ответу сервера — используется, только если у возврата нет строк.</summary>
    public double ServerTotal { get; set; }
    public List<SupplierReturnDocLine> Lines { get; set; } = new();

    public double TotalQty => Lines.Sum(l => l.Qty);
    /// <summary>Итог: по серверу (total_amount), если он сходится с суммой строк до копеек округления; иначе — сумма строк (таблица всегда сходится сама с собой).</summary>
    public double TotalSum
    {
        get
        {
            var server = Math.Round(ServerTotal, 2, MidpointRounding.AwayFromZero);
            if (Lines.Count == 0)
                return server;
            var sum = Math.Round(Lines.Sum(l => l.Sum), 2, MidpointRounding.AwayFromZero);
            return server > 0 && Math.Abs(server - sum) <= 0.05 ? server : sum;
        }
    }
}

public static class SupplierReturnDocuments
{
    // Память последнего просмотра: реквизиты поставщика, номер прихода и склад, найденные при прошлой печати этого возврата, — если
    // сейчас связи нет, документ берёт их оттуда (иначе в бланке остались бы пустые строки).
    private static readonly Dictionary<string, SupplierReturnDocData> Last = new();
    private static readonly object LastLock = new();

    private static string Pick(string lang, string ru, string ky, string en, string tr, string uz) => TransferDocuments.Pick(lang, ru, ky, en, tr, uz);

    public static string Title(string lang) => Pick(lang, "Накладная на возврат товара поставщику", "Жеткирүүчүгө товарды кайтаруу накладнойу",
        "Return-to-supplier waybill", "Tedarikçiye iade irsaliyesi", "Yetkazib beruvchiga tovar qaytarish yuk xati");

    public static string ReasonName(string reason, string lang) => reason switch
    {
        "defect" => Pick(lang, "Брак", "Бузук", "Defect", "Kusurlu", "Brak"),
        "surplus" => Pick(lang, "Излишек", "Ашыкча", "Surplus", "Fazla", "Ortiqcha"),
        "wrong_item" => Pick(lang, "Ошибка поставки", "Жеткирүүдөгү ката", "Wrong item delivered", "Yanlış teslimat", "Yetkazishdagi xato"),
        "expired" => Pick(lang, "Просрочка", "Мөөнөтү өткөн", "Expired", "Süresi geçmiş", "Muddati o'tgan"),
        _ => Pick(lang, "Другое", "Башка", "Other", "Diğer", "Boshqa"),
    };

    /// <summary>Как рассчитаны с поставщиком. Про «в счёт долга» сказано как о способе расчёта, а не о результате на сервере.</summary>
    public static string SettlementText(SupplierReturnDocData d)
    {
        var l = d.Lang;
        return d.Compensation switch
        {
            "cash" => Pick(l, "Поставщик возвращает деньги в кассу", "Жеткирүүчү акчаны кассага кайтарат", "The supplier refunds the money to the till", "Tedarikçi parayı kasaya iade eder", "Yetkazib beruvchi pulni kassaga qaytaradi"),
            "debt_offset" => Pick(l, "Зачёт в счёт долга перед поставщиком", "Жеткирүүчүгө болгон карыздын эсебине эсептелет", "Offset against the debt to the supplier", "Tedarikçiye olan borca mahsup edilir", "Yetkazib beruvchiga qarz hisobiga o'tkaziladi"),
            _ => Pick(l, "Без движения денег (товар списан со склада; замена — по договорённости)", "Акча кыймылысыз (товар кампадан чыгарылды; алмаштыруу — макулдашуу боюнча)",
                "No money movement (written off the stock; replacement as agreed)", "Para hareketi yok (stoktan düşüldü; değişim anlaşmaya göre)", "Pul harakatisiz (ombordan chiqarildi; almashtirish — kelishuv bo'yicha)"),
        };
    }

    /// <summary>Собрать документ из возврата сервера. <paramref name="supplier"/> — карточка поставщика (ИНН, адрес, телефон), если есть;
    /// <paramref name="receiptNumber"/> — номер прихода-основания, если его уже узнали. Нет данных — поля остаются пустыми и печатаются как «____».
    /// Реквизиты организации — те же, что в документах перемещения. Ничего не пишет на сервер.</summary>
    public static SupplierReturnDocData Build(SupplierReturnInfo r, SupplierInfo? supplier, string? issuedBy, string? receiptNumber = null, DateTime? now = null)
    {
        var profile = ClientDocsStore.Profile;
        var company = CompanyInfoService.LastCompany;
        var prefs = UserPreferences.Instance;
        string First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

        var lang = TransferDocuments.CurrentLang();
        var number = First(r.NumberDisplay);
        if (number.Length == 0)
        {
            try
            {
                number = DocumentNumbering.Text(DocSeries.Return, r.Id, DocumentNumbering.ShortCode(r.Id));
            }
            catch (Exception)
            {
                number = DocumentNumbering.ShortCode(r.Id);
            }
        }
        var supplierName = First(r.SupplierName, supplier?.Name);
        if (supplier is { Llc.Length: > 0 } && !string.Equals(supplier.Llc, supplierName, StringComparison.OrdinalIgnoreCase))
            supplierName = supplierName.Length > 0 ? supplierName + " (" + supplier.Llc.Trim() + ")" : supplier.Llc.Trim();

        var data = new SupplierReturnDocData
        {
            ReturnId = r.Id,
            Number = number,
            Date = (r.CreatedAt ?? now ?? DateTime.Now).Date,
            CreatedAt = now ?? DateTime.Now,
            Lang = lang,
            OrgName = First(profile.Name, company?.Name, prefs.StoreName),
            OrgInn = First(profile.Inn, company?.Inn, prefs.StoreInn),
            OrgAddress = First(profile.Address, company?.Address, prefs.StoreAddress),
            OrgPhone = First(profile.Phone),
            Warehouse = First(r.Warehouse),
            SupplierName = supplierName,
            SupplierInn = First(supplier?.Inn),
            SupplierAddress = First(supplier?.Address),
            SupplierPhone = First(supplier?.Phone),
            HasReceipt = !string.IsNullOrWhiteSpace(r.ReceiptId),
            ReceiptNumber = First(receiptNumber, r.ReceiptNumber),
            Reason = r.Reason,
            Compensation = r.Compensation,
            Comment = First(r.Comment),
            IssuedBy = First(r.CreatedBy, issuedBy),
            ServerTotal = r.Total,
        };
        if (data.HasReceipt && data.ReceiptNumber.Length == 0)
        {
            try
            {
                data.ReceiptNumber = DocumentNumbering.ReceiptText(r.ReceiptId);
            }
            catch (Exception)
            {
                data.ReceiptNumber = DocumentNumbering.ShortCode(r.ReceiptId);
            }
        }
        foreach (var line in r.Lines)
            data.Lines.Add(new SupplierReturnDocLine(line.ProductName, line.Article, line.Barcode,
                string.IsNullOrWhiteSpace(line.Unit) ? Pick(lang, "шт", "даана", "pcs", "adet", "dona") : line.Unit.Trim(), line.Qty, Math.Max(0, line.PurchasePrice), line.Total));
        FillFromLast(data);
        Remember(data);
        return data;
    }

    private static void FillFromLast(SupplierReturnDocData d)
    {
        if (d.ReturnId.Length == 0)
            return;
        SupplierReturnDocData? old;
        lock (LastLock)
            Last.TryGetValue(d.ReturnId, out old);
        if (old is null)
            return;
        if (d.SupplierInn.Length == 0)
            d.SupplierInn = old.SupplierInn;
        if (d.SupplierAddress.Length == 0)
            d.SupplierAddress = old.SupplierAddress;
        if (d.SupplierPhone.Length == 0)
            d.SupplierPhone = old.SupplierPhone;
        if (d.Warehouse.Length == 0)
            d.Warehouse = old.Warehouse;
        // Номер прихода: настоящий («ПР-…») содержит дефис; короткий код без дефиса (связи не было) заменяем прежним настоящим.
        if (old.ReceiptNumber.Contains('-') && !d.ReceiptNumber.Contains('-'))
            d.ReceiptNumber = old.ReceiptNumber;
    }

    private static void Remember(SupplierReturnDocData d)
    {
        if (d.ReturnId.Length == 0)
            return;
        lock (LastLock)
        {
            if (Last.Count > 200)
                Last.Clear();
            Last[d.ReturnId] = d;
        }
    }

    /// <summary>Имя файла: «Накладная на возврат товара поставщику ВП-2026-000010.pdf».</summary>
    public static string FileName(SupplierReturnDocData data, string extension) =>
        ExportFileName.Build(Title(data.Lang), data.Number, null, extension);

    /// <summary>Имя документа для очереди печати.</summary>
    public static string PrintName(SupplierReturnDocData data) => Title(data.Lang) + " " + data.Number;

    public static string SupplierLine(SupplierReturnDocData d, string tin, string tel) => string.Join(", ", new[]
    {
        d.SupplierName,
        string.IsNullOrWhiteSpace(d.SupplierInn) ? null : tin + d.SupplierInn,
        d.SupplierAddress,
        string.IsNullOrWhiteSpace(d.SupplierPhone) ? null : tel + d.SupplierPhone,
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    /// <summary>Основание: приход, по которому возвращают, либо «со склада».</summary>
    public static string BasisText(SupplierReturnDocData d)
    {
        var l = d.Lang;
        if (!d.HasReceipt)
            return Pick(l, "Возврат со склада (без привязки к приходу)", "Кампадан кайтаруу (кирүүгө байланышсыз)", "Return from stock (not tied to a delivery)", "Stoktan iade (girişe bağlı değil)", "Ombordan qaytarish (kirimga bog'lanmagan)");
        return Pick(l, "Приход товара № ", "Товардын кириши № ", "Delivery No. ", "Mal girişi No: ", "Tovar kirimi № ") + (d.ReceiptNumber.Length > 0 ? d.ReceiptNumber : "____");
    }
}

/// <summary>2026-11-06 (В12, ЖП Н-11): сумма строки возврата поставщику. Окно возврата округляло «банковским» способом (0,125 → 0,12), а приход сервер записал как 0,13
/// (от нуля) — на поставщике оставался долг 0,01. Теперь сумма строки считается так же, как у прихода: до копейки от нуля, в decimal (без шума double).
/// Что запишет сервер в сам возврат — решает сервер (мы шлём количество и цену); при доле копейки окно предупреждает.</summary>
public static class SupplierReturnMath
{
    public static double LineTotal(double qty, double price) =>
        (double)Math.Round((decimal)Math.Max(0, qty) * (decimal)price, 2, MidpointRounding.AwayFromZero);

    public static double Total(IEnumerable<(double Qty, double Price)> lines) =>
        (double)lines.Sum(l => (decimal)LineTotal(l.Qty, l.Price));

    /// <summary>Сумма строки лежит ровно на половине копейки (0,125): здесь округление сервера и кассы может разойтись.</summary>
    public static bool IsHalfKopeck(double qty, double price)
    {
        var raw = (decimal)Math.Max(0, qty) * (decimal)price * 100m;
        return raw != decimal.Truncate(raw) && raw - decimal.Truncate(raw) == 0.5m;
    }
}
