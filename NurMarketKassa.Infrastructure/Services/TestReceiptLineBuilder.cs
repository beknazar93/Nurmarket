using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>Единый источник строк тестового чека (текст ESC/POS и графический PDF).</summary>
public static class TestReceiptLineBuilder
{
    public static int CharWidth => ReceiptLayout.CharWidth;
    public static int PaperWidthPixels => ReceiptLayout.RasterWidthPixels;
    public const string FontFamily = "Courier New";
    public const float DefaultFontSizePt = 10f;
    public const float FontSizePt = DefaultFontSizePt;
    public const float LineStepPx = 11f;
    public const float CompactLineStepPx = 6f;
    public const float EmptyLineStepPx = 3f;
    public const float StartOffsetPx = 6f;
    public const float BottomPaddingPx = 8f;
    public const float LeftMarginPx = 8f;

    public static List<string> GetTestTextReceiptLines(GraphicReceiptSettings settings, string storeName)
    {
        var w = CharWidth;
        var sep = new string('=', w);
        var lines = new List<string>();
        var displayName = string.IsNullOrWhiteSpace(storeName) ? "MARKET PLUS" : storeName.Trim().ToUpperInvariant();
        var now = DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);

        lines.Add(sep);

        if (settings.ShowStoreName)
        {
            lines.Add(ReceiptLineLayout.Center(displayName, w));
            lines.Add(ReceiptLineLayout.Center("NUR MARKET KASSA", w));
        }

        if (settings.ShowInn && !string.IsNullOrWhiteSpace(settings.StoreInn))
            lines.Add(ReceiptLineLayout.Center($"ИНН: {settings.StoreInn.Trim()}", w));

        if (settings.ShowAddress && !string.IsNullOrWhiteSpace(settings.StoreAddress))
        {
            foreach (var addressLine in ReceiptLineLayout.WrapCenter(settings.StoreAddress.Trim(), w))
                lines.Add(addressLine);
        }

        lines.Add(sep);

        if (settings.ShowReceiptNumber)
            lines.Add(ReceiptLineLayout.PadLine("Чек № 00042", w));

        if (settings.ShowDate)
            lines.Add(ReceiptLineLayout.PadLine($"Дата: {now}", w));

        if (settings.ShowReceiptNumber || settings.ShowDate)
            lines.Add(sep);

        if (settings.ShowItems)
        {
            lines.Add(ReceiptLineLayout.PadLine("Молоко 1л", w));
            foreach (var part in ReceiptLineLayout.FormatItemBlock("2 x 89.00", "178.00", w))
                lines.Add(part);

            lines.Add(ReceiptLineLayout.PadLine("Хлеб белый", w));
            foreach (var part in ReceiptLineLayout.FormatItemBlock("1 x 45.00", "45.00", w))
                lines.Add(part);

            lines.Add(ReceiptLineLayout.PadLine("Султан чай", w));
            foreach (var part in ReceiptLineLayout.FormatItemBlock("1 x 120.00", "120.00", w))
                lines.Add(part);
        }

        if (settings.ShowTotal)
        {
            var dash = new string('-', w);
            lines.Add(dash);
            AppendStackedAmountLines(lines, "ПОДИТОГ:", "370.00", w);
            AppendStackedAmountLines(lines, "СКИДКА:", "27.00", w);
            lines.Add(dash);
            AppendStackedAmountLines(lines, "ИТОГО К ОПЛАТЕ:", "343.00", w);
            AppendStackedAmountLines(lines, "ВНЕСЕНО:", "500.00", w);
            AppendStackedAmountLines(lines, "СДАЧА:", "157.00", w);
            lines.Add(sep);
        }
        else
        {
            lines.Add(sep);
        }

        lines.Add(ReceiptLineLayout.PadLine("Кириллица: АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ", w));
        lines.Add(ReceiptLineLayout.PadLine("Кыргызча: Салам дүйнө!", w));
        lines.Add(sep);

        if (settings.ShowQrCode)
            lines.Add(ReceiptLineLayout.Center("[ QR-код оплаты ]", w));

        var printMode = settings.GraphicPrintMode ? "Графический" : "Текстовый";
        AppendStackedAmountLines(lines, "Режим печати:", printMode, w, appendSom: false);

        lines.Add(string.Empty);
        return lines;
    }

    public static float ResolveFontSize(float fontSize) =>
        fontSize > 0 ? fontSize : DefaultFontSizePt;

    public static GraphicReceiptSettings CreateDefaultTestSettings() =>
        new()
        {
            PaperWidthPixels = PaperWidthPixels,
            FontFamily = FontFamily,
            FontSize = FontSizePt,
            ShowStoreName = true,
            ShowAddress = true,
            ShowReceiptNumber = true,
            ShowDate = true,
            ShowItems = true,
            ShowTotal = true,
            ShowQrCode = false,
        };

    private static void AppendStackedAmountLines(List<string> lines, string label, string amount, int width, bool appendSom = true)
    {
        foreach (var part in ReceiptLineLayout.FormatStackedLabelAmount(label, amount, width, appendSom))
            lines.Add(part);
    }
}
