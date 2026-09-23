using System.Globalization;

using System.IO;

using System.Runtime.Versioning;

using QuestPDF.Fluent;

using QuestPDF.Helpers;

using QuestPDF.Infrastructure;



namespace NurMarketKassa.Services;



[SupportedOSPlatform("windows")]

public static class ReceiptPdfPreviewService

{

    private const float ReceiptWidthMm = 80f;



    static ReceiptPdfPreviewService()

    {

        QuestPDF.Settings.License = LicenseType.Community;

    }



    public static void GenerateTextReceiptPdf(

        string outputPath,

        string receiptText,

        string userEncoding,

        int? escTableByte)

    {

        var width = TestReceiptLineBuilder.CharWidth;

        var encodingName = ReceiptEncodingHelper.ResolveDotNetEncodingName(userEncoding);

        var previewText = ReceiptEncodingHelper.ApplyEncodingPreview(receiptText, userEncoding);

        var formatted = ReceiptTextFormatter.FormatForPrinter(previewText, width).TrimEnd().ToUpperInvariant();

        var tableLabel = escTableByte?.ToString(CultureInfo.InvariantCulture) ?? "авто";



        var pageHeightMm = Math.Clamp(40f + formatted.Split('\n').Length * 4.2f, 120f, 400f);



        Document.Create(container =>

        {

            container.Page(page =>

            {

                page.Size(ReceiptWidthMm, pageHeightMm, Unit.Millimetre);

                page.MarginHorizontal(4, Unit.Millimetre);

                page.MarginVertical(6, Unit.Millimetre);

                page.DefaultTextStyle(x => x.FontFamily(TestReceiptLineBuilder.FontFamily).FontSize(TestReceiptLineBuilder.FontSizePt));



                page.Content().Column(col =>

                {

                    col.Spacing(4);

                    col.Item().Text("Предпросмотр текстового чека (ESC/POS)")

                        .Bold().FontSize(10).FontColor(Colors.Grey.Darken2);

                    col.Item().Text($"Кодировка: {userEncoding} → {encodingName}  |  ESC t: {tableLabel}")

                        .FontSize(8).FontColor(Colors.Grey.Medium);

                    col.Item().Text("Символы «?» — не поддерживаются выбранной кодировкой.")

                        .FontSize(7).FontColor(Colors.Red.Medium);

                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

                    col.Item().Text(formatted)

                        .FontFamily(TestReceiptLineBuilder.FontFamily)

                        .FontSize(TestReceiptLineBuilder.FontSizePt)

                        .LineHeight(1f);

                });

            });

        }).GeneratePdf(outputPath);

    }



    public static void GenerateGraphicReceiptPdf(
        string outputPath,
        GraphicReceiptSettings settings,
        string storeName)
    {
        var lines = TestReceiptLineBuilder.GetTestTextReceiptLines(settings, storeName);
        var fontSize = TestReceiptLineBuilder.ResolveFontSize(settings.FontSize);
        var body = string.Join("\n", lines);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.ContinuousSize(ReceiptWidthMm, Unit.Millimetre);
                page.MarginHorizontal(1.5f, Unit.Millimetre);
                page.MarginVertical(2, Unit.Millimetre);

                page.Content().Text(body)
                    .FontFamily(TestReceiptLineBuilder.FontFamily)
                    .FontSize(fontSize)
                    .LineHeight(0.88f);
            });
        }).GeneratePdf(outputPath);
    }



    public static string BuildTextTestReceipt(GraphicReceiptSettings settings, string storeName)

    {

        var lines = TestReceiptLineBuilder.GetTestTextReceiptLines(settings, storeName);

        return string.Join("\n", lines);

    }

}


