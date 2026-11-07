using System;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06: В4 А5-04 — умеет ли эта платформа создавать PDF. QuestPDF берёт нативную библиотеку libQuestPdfSkia.so,
/// собранную под glibc (Linux/Windows); в Android (bionic) она не загружается, для armeabi-v7a её нет совсем. Поэтому на Android
/// кнопки «PDF» скрыты, а действие заменяется Word / Excel / текстом. Когда В18 переведёт PDF на SkiaSharp (SKDocument.CreatePdf),
/// здесь достаточно вернуть true.</summary>
internal static class PdfSupport
{
    /// <summary>false — Android (QuestPDF не работает); true — Windows и Linux. Относится к документам A4 на QuestPDF (возврат поставщику, накладные и т. п.).</summary>
    public static bool IsAvailable => !OperatingSystem.IsAndroid();

    /// <summary>2026-11-06 (В18, А5-04): PDF чека есть везде — на Android его пишет SkiaSharp (ReceiptSkiaPdf), на Windows и Linux — QuestPDF.</summary>
    public static bool ReceiptPdfAvailable => true;
}
