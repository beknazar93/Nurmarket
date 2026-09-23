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



        private static byte[] ConvertToEscPosRaster(Bitmap bitmap)

        {

            int width = bitmap.Width;

            int height = bitmap.Height;

            int widthBytes = (width + 7) / 8;



            var rasterBytes = new byte[widthBytes * height];



            for (int y = 0; y < height; y++)

            {

                for (int x = 0; x < width; x++)

                {

                    var pixel = bitmap.GetPixel(x, y);

                    if (pixel.R + pixel.G + pixel.B < 384)

                    {

                        int byteIndex = y * widthBytes + (x / 8);

                        int bitIndex = 7 - (x % 8);

                        rasterBytes[byteIndex] |= (byte)(1 << bitIndex);

                    }

                }

            }



            var command = new List<byte>

            {

                0x1D, 0x76, 0x30, 0x00,

                (byte)(widthBytes % 256), (byte)(widthBytes / 256),

                (byte)(height % 256), (byte)(height / 256)

            };

            command.AddRange(rasterBytes);



            using var ms = new MemoryStream();

            EscPosCommands.WriteInitialize(ms);
            ms.Write(command.ToArray(), 0, command.Count);
            EscPosCommands.WriteFeedLines(ms, 2);
            EscPosCommands.WriteFeedAndCut(ms);

            return ms.ToArray();

        }

    }

}


