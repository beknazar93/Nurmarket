using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Электроника», ЭЛ3): печать серийных номеров и гарантии. Две вещи: строки под товаром на чеке
/// («IMEI: …», «Гарантия до ДД.ММ.ГГГГ») и отдельный гарантийный талон (58/80 мм по ширине чека). Всё работает только в сфере
/// «Электроника»: у других сфер <see cref="ItemLines"/> и <see cref="ItemLinesFromRecords"/> возвращают пусто, и чек остаётся
/// байт в байт прежним.
/// </summary>
public static class WarrantyCard
{
    /// <summary>Дата окончания гарантии: день продажи + N месяцев (как в сроке гарантии — «до» включительно).</summary>
    public static DateTime WarrantyUntil(DateTime soldAt, int months) => soldAt.Date.AddMonths(Math.Max(0, months));

    private static string Day(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>Строки под товаром на чеке по строке корзины: номера (каждый на своей строке) и «Гарантия до …».
    /// Нет номеров и нет гарантии, либо сфера не «Электроника» — пусто.</summary>
    public static List<string> ItemLines(JsonElement item, DateTime saleDate, int width)
    {
        if (!ElectronicsProfile.IsActive)
            return new List<string>();
        return Compose(SerialJournal.ReadSerials(item), SerialJournal.ReadMonths(item), saleDate, width);
    }

    /// <summary>То же из записей журнала одной продажи (повторная печать чека с сервера): строки продажи берутся по товару
    /// (<paramref name="productId"/>); у товара без записей — пусто.</summary>
    public static List<string> ItemLinesFromRecords(IEnumerable<SerialRecord> saleRecords, string? productId, string? title, int width)
    {
        if (!ElectronicsProfile.IsActive)
            return new List<string>();
        var mine = saleRecords
            .Where(r => (!string.IsNullOrEmpty(productId) && string.Equals(r.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                        || (string.IsNullOrEmpty(productId) && string.Equals(r.Title, title?.Trim(), StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (mine.Count == 0)
            return new List<string>();
        var first = mine[0];
        return Compose(mine.Select(r => r.Serial).ToList(), first.WarrantyMonths, first.SoldAt, width);
    }

    /// <summary>2026-11-13 (сервер sn1311): то же по строке проданного чека С СЕРВЕРА — номера <c>serial_numbers</c> и дата <c>warranty_until</c> печатаются как прислал сервер
    /// (а не пересчитываются кассой). Пусто — сфера не «Электроника» или сервер номеров у строки не вернул (тогда вызывающий берёт местный журнал).</summary>
    public static List<string> ItemLinesFromServer(JsonElement line, int width)
    {
        if (!ElectronicsProfile.IsActive)
            return new List<string>();
        var serials = ServerSerials.LineSerials(line);
        if (serials.Count == 0)
            return new List<string>();
        var lines = new List<string>();
        foreach (var serial in serials)
            foreach (var part in ReceiptLineLayout.WrapLeft($"{ImeiValidator.LabelFor(serial)}: {serial}", width))
                lines.Add(part);
        if (ServerSerials.LineWarrantyUntil(line) is { } until)
            lines.Add(WarrantyUntilLine(until));
        return lines;
    }

    /// <summary>«Гарантия до 13.11.2027» — по дате, которую прислал сервер (без срока в месяцах).</summary>
    public static string WarrantyUntilLine(DateTime until) =>
        Tr.T($"Гарантия до {Day(until)}", $"Кепилдик {Day(until)} чейин", $"Warranty until {Day(until)}", $"Garanti bitişi {Day(until)}", $"Kafolat {Day(until)} gacha");

    private static List<string> Compose(IReadOnlyList<string> serials, int months, DateTime soldAt, int width)
    {
        var lines = new List<string>();
        foreach (var serial in serials)
            foreach (var part in ReceiptLineLayout.WrapLeft($"{ImeiValidator.LabelFor(serial)}: {serial}", width))
                lines.Add(part);
        if (months > 0 && serials.Count > 0)
            lines.Add(WarrantyLine(soldAt, months));
        return lines;
    }

    /// <summary>«Гарантия до 12.11.2027 (12 мес.)».</summary>
    public static string WarrantyLine(DateTime soldAt, int months) =>
        Tr.T($"Гарантия до {Day(WarrantyUntil(soldAt, months))} ({months} мес.)",
            $"Кепилдик {Day(WarrantyUntil(soldAt, months))} чейин ({months} ай)",
            $"Warranty until {Day(WarrantyUntil(soldAt, months))} ({months} mo.)",
            $"Garanti bitişi {Day(WarrantyUntil(soldAt, months))} ({months} ay)",
            $"Kafolat {Day(WarrantyUntil(soldAt, months))} gacha ({months} oy)");

    /// <summary>Текст гарантийного талона одной единицы: магазин, чек, товар, номер, дата продажи, срок, «до», условия и подпись.
    /// Ширина — символов в строке чековой ленты (<see cref="ReceiptLayout.CharWidth"/> — 58/80 мм).</summary>
    public static string BuildCard(SerialRecord rec, int width)
    {
        var prefs = UserPreferences.Instance;
        var sb = new StringBuilder(600);
        void Line(string s = "") => sb.Append(s).Append('\n');
        void Dash() => Line(new string('-', width));
        void Wrapped(string s) { foreach (var p in ReceiptLineLayout.WrapLeft(s, width)) Line(p); }

        Line(ReceiptLineLayout.Center(Tr.T("ГАРАНТИЙНЫЙ ТАЛОН", "КЕПИЛДИК ТАЛОНУ", "WARRANTY CARD", "GARANTİ BELGESİ", "KAFOLAT TALONI"), width));
        Line();
        if (!string.IsNullOrWhiteSpace(prefs.StoreName))
            Wrapped(Tr.T("Магазин: ", "Дүкөн: ", "Store: ", "Mağaza: ", "Do'kon: ") + prefs.StoreName.Trim());
        if (!string.IsNullOrWhiteSpace(prefs.StoreInn))
            Line($"{Tr.T("ИНН", "ИНН", "TIN", "Vergi no", "STIR")}: {prefs.StoreInn.Trim()}");
        if (!string.IsNullOrWhiteSpace(prefs.StoreAddress))
            Wrapped(prefs.StoreAddress.Trim());
        Dash();
        if (!string.IsNullOrWhiteSpace(rec.ReceiptNo))
            Line($"{Tr.T("Чек №", "Чек №", "Receipt No.", "Fiş No", "Chek №")}: {rec.ReceiptNo.Trim()}");
        Line($"{Tr.T("Дата продажи", "Сатылган күнү", "Sale date", "Satış tarihi", "Sotilgan sana")}: {Day(rec.SoldAt)}");
        Wrapped(Tr.T("Товар: ", "Товар: ", "Item: ", "Ürün: ", "Mahsulot: ") + rec.Title);
        Wrapped($"{ImeiValidator.LabelFor(rec.Serial)}: {rec.Serial}");
        // 2026-11-13 (сервер sn1311): «гарантия до» может прийти от сервера без срока в месяцах — тогда срок не печатаем, дату печатаем.
        if (rec.WarrantyMonths > 0 || rec.WarrantyUntil != null)
        {
            if (rec.WarrantyMonths > 0)
                Line($"{Tr.T("Срок гарантии", "Кепилдик мөөнөтү", "Warranty period", "Garanti süresi", "Kafolat muddati")}: {rec.WarrantyMonths} {Tr.T("мес.", "ай", "mo.", "ay", "oy")}");
            Line($"{Tr.T("Гарантия до", "Кепилдик чейин", "Warranty until", "Garanti bitişi", "Kafolat gacha")}: {Day(rec.WarrantyUntil ?? WarrantyUntil(rec.SoldAt, rec.WarrantyMonths))}");
        }
        else
            Wrapped(Tr.T("Гарантия на этот товар не установлена.", "Бул товарга кепилдик коюлган эмес.", "No warranty is set for this item.", "Bu ürün için garanti belirlenmemiş.", "Bu mahsulotga kafolat belgilanmagan."));
        Dash();
        Wrapped(Tr.T(
            "Гарантия действует при предъявлении талона и чека. Не распространяется на механические повреждения и следы попадания влаги.",
            "Кепилдик талонду жана чекти көрсөткөндө колдонулат. Механикалык бузулууларга жана суу тийген издерге таркатылбайт.",
            "The warranty applies when this card and the receipt are presented. It does not cover mechanical damage or moisture.",
            "Garanti, bu belge ve fiş ibraz edildiğinde geçerlidir. Mekanik hasarı ve sıvı temasını kapsamaz.",
            "Kafolat talon va chek ko'rsatilganda amal qiladi. Mexanik shikastlanish va namlik izlariga tatbiq etilmaydi."));
        Line();
        Line(Tr.T("Подпись продавца: ____________", "Сатуучунун колу: ____________", "Seller's signature: ____________", "Satıcı imzası: ____________", "Sotuvchi imzosi: ____________"));
        Line();
        return sb.ToString();
    }

    /// <summary>Талоны всех записей продажи (каждая единица — свой талон), через пустую строку.</summary>
    public static string BuildCards(IEnumerable<SerialRecord> records, int width) =>
        string.Join("\n", records.Select(r => BuildCard(r, width)));

    /// <summary>Талоны на ширину ленты, выбранную в настройках чека (58/80 мм).</summary>
    public static string BuildCards(IEnumerable<SerialRecord> records) => BuildCards(records, ReceiptLayout.CharWidth);
}
