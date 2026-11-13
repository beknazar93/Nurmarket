using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ZXing;
using ZXing.Common;

namespace NurMarketKassa.Services;

/// <summary>2026-11-05 (аудит №4, А4-09): QR-код в PNG без System.Drawing — для Android и Linux, где его нет. Матрицу строит ZXing,
/// PNG (серый, 8 бит) пишется вручную: сжатие ZLibStream, контрольная сумма CRC-32. Новых пакетов не нужно.</summary>
internal static class QrPngWriter
{
    /// <summary>2026-11-12 (жалоба тестера «не генерируется QR для кассовой стороны, сделать QR побольше»): тихая зона — 4 модуля (как требует стандарт QR;
    /// было 1 — на тёмном фоне экрана покупателя камера телефона хуже находила код), коррекция ошибок — M (было L по умолчанию ZXing).</summary>
    internal const int QuietZoneModules = 4;

    public static void Write(string payload, int sizePx, string targetPath) =>
        File.WriteAllBytes(targetPath, ToPng(Encode(payload, sizePx)));

    /// <summary>2026-11-12: матрица QR отдельно от записи PNG — для проверки тестом (тихая зона, уровень коррекции, обратное чтение).</summary>
    internal static BitMatrix Encode(string payload, int sizePx)
    {
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = QuietZoneModules,
            [EncodeHintType.ERROR_CORRECTION] = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
        };
        // 2026-11-05 (ревью волн 0–3, А-12): как на Windows (BarcodeLabelService) — UTF-8 только для не-ASCII строк. Подсказка кодировки
        // добавляет в QR метку ECI, которую часть банковских приложений читает плохо; ASCII-платёжные строки идут без неё.
        for (var i = 0; i < payload.Length; i++)
            if (payload[i] > 127)
            {
                hints[EncodeHintType.CHARACTER_SET] = "UTF-8";
                break;
            }
        return new ZXing.QrCode.QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, sizePx, sizePx, hints);
    }

    private static byte[] ToPng(BitMatrix matrix)
    {
        var width = matrix.Width;
        var height = matrix.Height;

        // Строки изображения: байт фильтра 0 + по байту на точку (чёрная — 0, белая — 255).
        var raw = new byte[(width + 1) * height];
        var offset = 0;
        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0;
            for (var x = 0; x < width; x++)
                raw[offset++] = matrix[x, y] ? (byte)0 : (byte)255;
        }

        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            compressed = buffer.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        WriteInt(header, 0, width);
        WriteInt(header, 4, height);
        header[8] = 8; // глубина — 8 бит
        header[9] = 0; // серое изображение
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", compressed);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteInt(byte[] target, int index, int value)
    {
        target[index] = (byte)(value >> 24);
        target[index + 1] = (byte)(value >> 16);
        target[index + 2] = (byte)(value >> 8);
        target[index + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        var body = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
            body[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, body, 4, data.Length);
        stream.Write(body);
        var crc = new byte[4];
        WriteInt(crc, 0, unchecked((int)Crc32(body)));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
