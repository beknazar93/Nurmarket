using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services;

// 2026-11-20 (автоподтяжка базы из папки программы): чтение таблицы dBase/FoxPro (.dbf) без пакетов — только заголовок, описания полей и записи.
// Memo-поля (текст в .fpt/.dbt) не читаются (остаются пустыми): для товаров они не нужны. Кодировка: байт языка в заголовке (0x65/0x26 — 866, 0xC9 — 1251, 0x03 — 1252),
// «неизвестно» — выбирается 866 или 1251 по числу кириллических букв в образце. Файл только читается.

/// <summary>Прочитанная DBF: имена полей и строки текстом.</summary>
internal sealed class DbfTable
{
    public required string[] Fields { get; init; }
    public required List<string[]> Rows { get; init; }
    public int Deleted { get; init; }
}

internal static class DbfTableReader
{
    private static readonly byte[] Versions = [0x02, 0x03, 0x04, 0x05, 0x30, 0x31, 0x32, 0x43, 0x63, 0x83, 0x8B, 0x8E, 0xCB, 0xF5, 0xFB];

    private sealed record Field(string Name, char Type, int Length, int Offset);

    /// <summary>Правдоподобный заголовок DBF (версия, длины, размер файла) — чтобы случайный файл с расширением .dbf не читался как таблица.</summary>
    internal static bool LooksLikeDbf(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var h = new byte[32];
            if (fs.Read(h, 0, 32) < 32)
                return false;
            return Plausible(h, fs.Length);
        }
        catch
        {
            return false;
        }
    }

    private static bool Plausible(byte[] h, long fileLength)
    {
        if (!Versions.Contains(h[0]))
            return false;
        var records = BitConverter.ToUInt32(h, 4);
        var headerLen = BitConverter.ToUInt16(h, 8);
        var recLen = BitConverter.ToUInt16(h, 10);
        if (headerLen < 33 || recLen < 2)
            return false;
        // размер файла не меньше заголовка + записей (допуск на недописанный хвост/маркер конца)
        return (long)headerLen + (long)records * recLen <= fileLength + recLen;
    }

    /// <summary>Прочитать таблицу. Исключения наружу не бросает: null и текст причины.</summary>
    internal static DbfTable? Read(string path, int maxRows, out string? error)
    {
        error = null;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var h = new byte[32];
            if (fs.Read(h, 0, 32) < 32 || !Plausible(h, fs.Length))
            {
                error = "not-dbf";
                return null;
            }
            var records = (int)Math.Min(BitConverter.ToUInt32(h, 4), int.MaxValue);
            var headerLen = BitConverter.ToUInt16(h, 8);
            var recLen = BitConverter.ToUInt16(h, 10);
            var lang = h[29];

            // описания полей: по 32 байта до байта 0x0D
            var desc = new byte[headerLen - 32];
            var got = fs.Read(desc, 0, desc.Length);
            var fields = new List<Field>();
            var offset = 1; // первый байт записи — признак удаления
            for (var i = 0; i + 32 <= got && desc[i] != 0x0D; i += 32)
            {
                var nameBytes = desc.AsSpan(i, 11);
                var end = nameBytes.IndexOf((byte)0);
                var name = Encoding.ASCII.GetString(end >= 0 ? nameBytes[..end] : nameBytes).Trim();
                var type = (char)desc[i + 11];
                var len = desc[i + 16];
                fields.Add(new Field(name, type, len, offset));
                offset += len;
            }
            if (fields.Count == 0 || offset > recLen)
            {
                error = "bad-header";
                return null;
            }

            fs.Position = headerLen;
            var encoding = PickEncoding(lang, fs, headerLen, recLen, records, fields);
            fs.Position = headerLen;
            var rows = new List<string[]>();
            var deleted = 0;
            var buf = new byte[recLen];
            for (var r = 0; r < records; r++)
            {
                if (fs.Read(buf, 0, recLen) < recLen || buf[0] == 0x1A)
                    break;
                if (buf[0] == (byte)'*')
                {
                    deleted++;
                    continue;
                }
                if (rows.Count >= maxRows + 1)
                    break;
                var cells = new string[fields.Count];
                for (var f = 0; f < fields.Count; f++)
                    cells[f] = Cell(buf, fields[f], encoding);
                rows.Add(cells);
            }
            return new DbfTable { Fields = fields.Select(f => f.Name).ToArray(), Rows = rows, Deleted = deleted };
        }
        catch (Exception ex)
        {
            error = ex is IOException ? "busy" : "error";
            return null;
        }
    }

    private static Encoding PickEncoding(byte lang, FileStream fs, int headerLen, int recLen, int records, List<Field> fields)
    {
        switch (lang)
        {
            case 0x65:
            case 0x26:
                return Encoding.GetEncoding(866);
            case 0xC9:
                return Encoding.GetEncoding(1251);
            case 0x03:
                return Encoding.GetEncoding(1252);
        }
        // образец текстовых полей первых записей: что даёт больше кириллицы
        var sample = new List<byte>();
        var buf = new byte[recLen];
        for (var r = 0; r < Math.Min(records, 50); r++)
        {
            if (fs.Read(buf, 0, recLen) < recLen)
                break;
            foreach (var f in fields.Where(f => f.Type == 'C'))
                sample.AddRange(buf.AsSpan(f.Offset, f.Length).ToArray());
        }
        var arr = sample.ToArray();
        var e866 = Encoding.GetEncoding(866);
        var e1251 = Encoding.GetEncoding(1251);
        return Cyr(e1251.GetString(arr)) > Cyr(e866.GetString(arr)) ? e1251 : e866;

        static int Cyr(string s) => s.Count(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё');
    }

    private static string Cell(byte[] rec, Field f, Encoding enc)
    {
        var span = rec.AsSpan(f.Offset, f.Length);
        switch (f.Type)
        {
            case 'C':
                return enc.GetString(span).TrimEnd('\0', ' ').Trim();
            case 'N':
            case 'F':
                return Encoding.ASCII.GetString(span).Trim().Trim('\0');
            case 'I' when f.Length >= 4:
                return BitConverter.ToInt32(rec, f.Offset).ToString(CultureInfo.InvariantCulture);
            case 'B' when f.Length >= 8:
                return BitConverter.ToDouble(rec, f.Offset).ToString("0.########", CultureInfo.InvariantCulture);
            case 'Y' when f.Length >= 8:
                return (BitConverter.ToInt64(rec, f.Offset) / 10000m).ToString(CultureInfo.InvariantCulture);
            case 'D':
                return Encoding.ASCII.GetString(span).Trim();
            case 'L':
                return span[0] is (byte)'T' or (byte)'t' or (byte)'Y' or (byte)'y' ? "1" : span[0] is (byte)'F' or (byte)'f' or (byte)'N' or (byte)'n' ? "0" : "";
            default:
                return ""; // M, G, P, 0 и прочее (memo, двоичные) — не читаем
        }
    }
}
