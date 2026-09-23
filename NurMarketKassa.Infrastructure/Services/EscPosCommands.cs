using System.IO;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>Общие ESC/POS-команды для текстовой и графической печати.</summary>
internal static class EscPosCommands
{
    public static void WriteInitialize(Stream s)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x40);
    }

    public static void WriteCodePage(Stream s, int tableByte, int? escRByte, bool noEscPct)
    {
        if (!noEscPct)
        {
            s.WriteByte(0x1B);
            s.WriteByte(0x25);
            s.WriteByte(0x00);
        }

        if (escRByte is >= 0 and <= 255)
        {
            s.WriteByte(0x1B);
            s.WriteByte(0x52);
            s.WriteByte((byte)(escRByte.Value & 0xFF));
        }

        s.WriteByte(0x1B);
        s.WriteByte(0x74);
        s.WriteByte((byte)(tableByte & 0xFF));
    }

    public static void WriteDefaultLineSpacing(Stream s)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x32);
    }

    /// <summary>LF — перевод строки для ESC/POS текстового режима.</summary>
    public static void WriteLineFeed(Stream s) => s.WriteByte(0x0A);

    /// <summary>ESC d n — прокрутка на n строк.</summary>
    public static void WriteFeedLines(Stream s, byte lines)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x64);
        s.WriteByte(lines);
    }

    /// <summary>Прокрутка на 3 строки и отрез (GS V B 0).</summary>
    public static void WriteFeedAndCut(Stream s, byte feedLines = 3)
    {
        s.WriteByte(0x1B);
        s.WriteByte(0x64);
        s.WriteByte(feedLines);

        s.WriteByte(0x1D);
        s.WriteByte(0x56);
        s.WriteByte(0x42);
        s.WriteByte(0x00);
    }
}
