using System.IO;
using NurMarketKassa.Configuration;

namespace NurMarketKassa.Services;

public static class ReceiptPrintService
{
    public static void PrintText(ReceiptPrinterSettings cfg, string text, int? charWidth = null)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        EscPosTextReceiptPrinter.ValidateSettings(cfg);
        EscPosTextReceiptPrinter.Print(cfg, text, charWidth);
    }

    public static void PrintGraphic(GraphicReceiptSettings settings, string text)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Graphic receipt printing is supported only on Windows.");
        ValidateGraphicSettings(settings);
        GraphicReceiptPrinter.Print(text, settings);
    }

    public static void PrintGraphicTest(GraphicReceiptSettings settings, string storeName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Graphic receipt printing is supported only on Windows.");
        ValidateGraphicSettings(settings);
        var bytes = GraphicReceiptGenerator.GenerateTestReceiptImage(settings, storeName);
        PrinterPortService.SendRawBytes(settings.DevicePath, bytes, settings.RetryCount);
    }

    public static void SendRawBytes(string port, byte[] bytes, int retries = 3) =>
        PrinterPortService.SendRawBytes(port, bytes, retries);

    public static void PrintReceipt(
        string cartJson,
        string? offlineNote = null,
        string? paymentMethodKey = null,
        string? cashReceived = null,
        string? receiptText = null)
    {
        var prefs = UserPreferences.Instance;
        if (!prefs.ReceiptEnabled)
        {
            PosLogger.Log("ReceiptPrintService: печать пропущена — выключена в настройках кассы.", "PRINTER");
            return;
        }

        var text = !string.IsNullOrWhiteSpace(receiptText)
            ? receiptText
            : CartReceiptTextBuilder.BuildSimpleReceipt(cartJson, offlineNote, paymentMethodKey, cashReceived);

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Текст чека пуст.");

        if (prefs.SelectedPrintMode == PrintMode.Graphic)
        {
            if (!prefs.GraphicReceiptEnabled)
                throw new InvalidOperationException("Графический чек выключен в настройках кассы.");

            PrintGraphic(prefs.ToGraphicReceiptSettings(), text);
        }
        else
        {
            PrintText(prefs.ToReceiptPrinterSettings(), text);
        }
    }

    private static void ValidateGraphicSettings(GraphicReceiptSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.DevicePath))
            throw new InvalidOperationException("Не указан порт принтера (LPT/COM).");
    }
}
