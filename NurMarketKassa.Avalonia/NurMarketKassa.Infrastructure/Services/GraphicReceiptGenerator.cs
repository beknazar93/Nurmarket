using System;

using System.Collections.Generic;

using System.Drawing;

using System.Drawing.Imaging;

using System.IO;

using System.Linq;

using System.Runtime.Versioning;



namespace NurMarketKassa.Services

{

    public class GraphicReceiptSettings

    {

        public int PaperWidthPixels { get; set; } = TestReceiptLineBuilder.PaperWidthPixels;

        public bool PrintQrCode { get; set; } = false;

        public string QrCodePath { get; set; } = "";

        public string FontFamily { get; set; } = TestReceiptLineBuilder.FontFamily;

        public string DevicePath { get; set; } = "LPT1";

        public int RetryCount { get; set; } = 3;

        public float FontSize { get; set; } = TestReceiptLineBuilder.FontSizePt;



        public bool ShowStoreName { get; set; } = true;

        public bool ShowAddress { get; set; } = true;

        public bool ShowInn { get; set; } = true;

        public bool ShowReceiptNumber { get; set; } = true;

        public bool ShowDate { get; set; } = true;

        public bool ShowItems { get; set; } = true;

        public bool ShowTotal { get; set; } = true;

        public bool ShowQrCode { get; set; } = false;

        public string StoreAddress { get; set; } = "";

        public string? StoreInn { get; set; }

        /// <summary>true — графический режим печати; false — текстовый ESC/POS.</summary>
        public bool GraphicPrintMode { get; set; }
    }



    [SupportedOSPlatform("windows")]

    public static class GraphicReceiptGenerator

    {

        public static byte[] GenerateReceiptImage(string receiptText, GraphicReceiptSettings settings) =>

            ConvertToEscPosRaster(GenerateReceiptBitmap(receiptText, settings));



        public static byte[] GenerateTestReceiptImage(GraphicReceiptSettings settings, string storeName) =>

            ConvertToEscPosRaster(GenerateTestReceiptBitmap(settings, storeName));



        public static Bitmap GenerateTestReceiptBitmap(GraphicReceiptSettings settings, string storeName)

        {

            var lines = TestReceiptLineBuilder.GetTestTextReceiptLines(settings, storeName);

            return MonospaceReceiptRenderer.RenderLines(lines, settings);

        }



        public static Bitmap GenerateReceiptBitmap(string receiptText, GraphicReceiptSettings settings)

        {

            var width = ReceiptPaperProfile.GetCharWidth(
                settings.PaperWidthPixels >= ReceiptPaperProfile.GetRasterWidthPixels(ReceiptPaperProfile.Paper80mm)
                    ? ReceiptPaperProfile.Paper80mm
                    : ReceiptPaperProfile.Paper58mm);

            var formatted = ReceiptTextFormatter.FormatForPrinter(
                receiptText ?? string.Empty,
                width,
                rightMarginChars: 2).TrimEnd();

            var lines = formatted.Split('\n').ToList();

            if (lines.Count == 0)

                lines.Add("Чек");



            return MonospaceReceiptRenderer.RenderLines(lines, settings);

        }



        /// <summary>2026-09-08: реальный баг, второй раунд — разбиение на полосы для GS v 0
        /// (растровая команда) не помогло, владелец прислал фото с той же "кашей" из символов
        /// даже после фикса. Значит дело не в размере одной полосы, а в том, что сам принтер
        /// (дешёвый WinUSB-клон, VID_0483&amp;PID_5743) вообще не понимает команду GS v 0 —
        /// она появилась в спецификации ESC/POS позже и поддерживается не всеми клонами;
        /// непонятые байты команды/растра принтер просто пропускает через таблицу символов и
        /// печатает как текст. Переключились на ESC * (0x1B 0x2A) — гораздо более старую и
        /// универсально поддерживаемую команду печати битового изображения (была ещё в первых
        /// принтерах Epson TM, её понимают буквально все ESC/POS-совместимые устройства).
        /// Печатаем построчно полосами по 24 точки высотой (режим m=33, "24-точечная двойная
        /// плотность") — это стандартный, повсеместно задокументированный алгоритм печати
        /// растра через ESC/POS.</summary>
        private const int BitImageBandHeightPx = 24;

        private static byte[] ConvertToEscPosRaster(Bitmap bitmap)
        {
            int width = bitmap.Width;
            int height = bitmap.Height;

            using var ms = new MemoryStream();
            EscPosCommands.WriteInitialize(ms);

            // 2026-09-13, живой баг ("двоение"/линии на графическом чеке): без этой команды LF
            // между полосами растра продвигает бумагу на межстрочный интервал по умолчанию
            // принтера (обычно 1/6", не 24 точки), полосы съезжают и накладываются друг на
            // друга. Выставляем интервал ровно в высоту полосы — см. WriteLineSpacing.
            EscPosCommands.WriteLineSpacing(ms, BitImageBandHeightPx);

            for (var bandStart = 0; bandStart < height; bandStart += BitImageBandHeightPx)
            {
                ms.WriteByte(0x1B); // ESC
                ms.WriteByte(0x2A); // *
                ms.WriteByte(33);   // m = 24-точечная двойная плотность (3 байта на столбец)
                ms.WriteByte((byte)(width % 256));
                ms.WriteByte((byte)(width / 256));

                for (var x = 0; x < width; x++)
                {
                    for (var chunk = 0; chunk < 3; chunk++)
                    {
                        byte col = 0;
                        for (var bit = 0; bit < 8; bit++)
                        {
                            var y = bandStart + chunk * 8 + bit;
                            if (y >= height)
                                continue;

                            var pixel = bitmap.GetPixel(x, y);
                            if (pixel.R + pixel.G + pixel.B < 384)
                                col |= (byte)(1 << (7 - bit));
                        }

                        ms.WriteByte(col);
                    }
                }

                ms.WriteByte(0x0A); // LF — переход на следующую полосу той же ширины экрана
            }

            EscPosCommands.WriteDefaultLineSpacing(ms);
            EscPosCommands.WriteFeedLines(ms, 2);
            EscPosCommands.WriteFeedAndCut(ms);

            return ms.ToArray();
        }

    }

}


