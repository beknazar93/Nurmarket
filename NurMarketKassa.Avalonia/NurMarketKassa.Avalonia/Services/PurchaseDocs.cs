using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.AvaloniaHost.Views;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-22 (закупки как в вебе): документы закупки — «Скачать накладную» сайта (приходная накладная на A4/PDF) и заказ
/// поставщику (PDF, печать, текст в WhatsApp). Бланк — тот же, что у накладных «Клиентов» (ClientDocRenderer, без новых зависимостей):
/// отправитель — поставщик, получатель — наш магазин, заголовок свой (Extra["title"]).</summary>
public static class PurchaseDocs
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public sealed record DocLine(string Name, string Unit, double Qty, double Price);

    public sealed record Party(string Name, string Phone, string Inn, string Address);

    /// <summary>receipt — приходная накладная, order — заказ поставщику.</summary>
    public static ClientDocRenderer.Input Build(Control host, string kind, Party supplier, string number, DateTime date, IReadOnlyList<DocLine> lines, string? note)
    {
        var profile = ClientDocsStore.Profile;
        var order = kind == "order";
        var doc = new ClientDoc
        {
            Kind = ClientDocKind.Waybill,
            Number = number,
            Client = profile.Name is { Length: > 0 } own ? own : CompanyInfoService.LastCompany?.Name ?? "",
            Inn = profile.Inn ?? "",
            Address = profile.Address,
            Phone = profile.Phone ?? "",
            Note = note,
            Lang = Tr.T("ru", "ky", "ru", "ru", "ru"),
            Template = ClientDocRenderer.Classic,
        };
        doc.Date = date;
        doc.Extra = new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement(order ? "Заказ поставщику" : "Приходная накладная"),
            ["title_ky"] = JsonSerializer.SerializeToElement(order ? "Жеткирүүчүгө буйрутма" : "Кириш накладнойу"),
        };
        foreach (var l in lines.Where(x => x.Qty > 0))
        {
            var line = new ClientDocLine { Name = l.Name, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "шт" : l.Unit, Qty = l.Qty };
            line.SetPrice(Math.Round(l.Price, 2), 0);
            doc.Lines.Add(line);
        }
        var seller = new ClientDocProfile { Name = supplier.Name, Phone = supplier.Phone, Inn = supplier.Inn, Address = supplier.Address, ShowStamp = false };
        return new ClientDocRenderer.Input(doc, seller, ClientDocRenderer.Classic, ClientDocsUi.AccentHex(host), null);
    }

    private static string Folder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NurMarket", "Закупки");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string FileName(string kind, string supplier, string number, DateTime date)
    {
        var name = $"{(kind == "order" ? "Заказ" : "Накладная")} {number} {supplier} {date:dd.MM.yyyy}".Trim();
        var safe = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
        return (safe.Length > 90 ? safe[..90] : safe) + ".pdf";
    }

    /// <summary>PDF в «Документы\NurMarket\Закупки» и открыть его. Возвращает путь или null (ошибка — в журнале и тексте).</summary>
    public static async Task<(string? Path, string? Error)> SavePdfAsync(ClientDocRenderer.Input input, string fileName, bool open = true)
    {
        try
        {
            var path = Path.Combine(Folder(), fileName);
            await Task.Run(() => ClientDocRenderer.SavePdf(input, path)).ConfigureAwait(true);
            PosLogger.Log($"Закупки: PDF сохранён ({path}).", "INFO");
            if (open)
                OpenFile(path);
            return (path, null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: PDF не сохранён ({ex}).", "WARNING");
            return (null, ex.Message);
        }
    }

    /// <summary>Печать на принтер по умолчанию (A4). null — напечатано, иначе текст ошибки.</summary>
    public static async Task<string?> PrintAsync(ClientDocRenderer.Input input, string name)
    {
        try
        {
            var printer = ClientDocPrint.DefaultPrinter();
            if (string.IsNullOrWhiteSpace(printer))
                return T("Принтер по умолчанию не найден.", "Демейки принтер табылган жок.", "No default printer found.", "Varsayılan yazıcı bulunamadı.", "Standart printer topilmadi.");
            return await Task.Run(() => ClientDocPrint.Print(printer, name, ClientDocRenderer.PageImages(input, 200))).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: печать не удалась ({ex}).", "WARNING");
            return ex.Message;
        }
    }

    /// <summary>Текст заказа поставщику — в WhatsApp/Telegram: магазин, дата, строки «товар — количество», итог по закупочным ценам.</summary>
    public static string OrderText(string supplier, string number, DateTime date, IReadOnlyList<DocLine> lines, string? note)
    {
        var sb = new StringBuilder();
        var shop = ClientDocsStore.Profile.Name is { Length: > 0 } own ? own : CompanyInfoService.LastCompany?.Name;
        sb.Append(T("Заказ", "Буйрутма", "Order", "Sipariş", "Buyurtma")).Append(' ').Append(number).Append(T(" от ", " · ", " of ", " · ", " · ")).Append(date.ToString("dd.MM.yyyy", Ru));
        if (!string.IsNullOrWhiteSpace(shop))
            sb.Append(" — ").Append(shop.Trim());
        sb.AppendLine();
        sb.Append(T("Поставщику: ", "Жеткирүүчүгө: ", "To supplier: ", "Tedarikçiye: ", "Yetkazib beruvchiga: ")).AppendLine(supplier);
        sb.AppendLine();
        var i = 0;
        foreach (var l in lines.Where(x => x.Qty > 0))
            sb.Append(++i).Append(". ").Append(l.Name).Append(" — ").Append(l.Qty.ToString("0.###", Ru)).Append(' ').AppendLine(string.IsNullOrWhiteSpace(l.Unit) ? "шт" : l.Unit);
        var total = lines.Sum(l => l.Qty * l.Price);
        if (total > 0)
            sb.AppendLine().Append(T("Сумма по закупочным ценам: ", "Сатып алуу баасы боюнча сумма: ", "Total at purchase prices: ", "Alış fiyatlarıyla toplam: ", "Xarid narxlarida jami: ")).AppendLine(MoneyText.Som(total));
        if (!string.IsNullOrWhiteSpace(note))
            sb.AppendLine().AppendLine(note.Trim());
        return sb.ToString().TrimEnd();
    }

    /// <summary>Открыть чат WhatsApp с поставщиком и готовым текстом (wa.me). Телефона нет — только текст (выбрать чат самому).</summary>
    public static void OpenWhatsApp(string? phone, string text)
    {
        var digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length == 9)
            digits = "996" + digits;
        else if (digits.Length == 10 && digits.StartsWith('0'))
            digits = "996" + digits[1..];
        var url = (digits.Length >= 9 ? "https://wa.me/" + digits : "https://wa.me/") + "?text=" + Uri.EscapeDataString(text);
        SiteOrdersWindow.OpenUrl(url);
        PosLogger.Log($"Закупки: заказ поставщику открыт в WhatsApp ({(digits.Length >= 9 ? "чат поставщика" : "без номера")}).", "INFO");
    }

    public static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: файл не открылся ({ex.Message}).", "WARNING");
        }
    }
}
