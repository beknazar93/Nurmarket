using System;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06: В4 А5-04 — умеет ли эта платформа создавать PDF. QuestPDF берёт нативную библиотеку libQuestPdfSkia.so,
/// собранную под glibc (Linux/Windows); в Android (bionic) она не загружается, для armeabi-v7a её нет совсем. Поэтому на Android
/// кнопки «PDF» скрыты, а действие заменяется Word / Excel / текстом. Когда В18 переведёт PDF на SkiaSharp (SKDocument.CreatePdf),
/// здесь достаточно вернуть true.</summary>
internal static class PdfSupport
{
    /// <summary>false — Android (QuestPDF не работает); true — Windows и Linux.</summary>
    public static bool IsAvailable => !OperatingSystem.IsAndroid();
}
