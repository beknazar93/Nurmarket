namespace NurMarketKassa.Services;

public static class GraphicReceiptPrinter
{
    public static void Print(string text, GraphicReceiptSettings settings)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Текст чека пуст.");

        if (string.IsNullOrWhiteSpace(settings.DevicePath))
            throw new InvalidOperationException("Не указан порт принтера.");

        // 2026-11-06 (В18, АЛ-3): Windows — GDI+ как раньше; Linux и Android — растр Skia (System.Drawing там нет).
        var imageBytes = OperatingSystem.IsWindows() ? RenderGdi(text, settings) : GraphicReceiptSkia.GenerateReceiptImage(text, settings);
        PrinterPortService.SendRawBytes(settings.DevicePath, imageBytes, settings.RetryCount);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static byte[] RenderGdi(string text, GraphicReceiptSettings settings) => GraphicReceiptGenerator.GenerateReceiptImage(text, settings);
}
