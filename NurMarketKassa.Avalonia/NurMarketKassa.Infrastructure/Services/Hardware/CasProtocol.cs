using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Что не так в записи PLU весов CAS.</summary>
public enum CasPluProblem
{
    None,
    BadPluNumber,
    BadItemCode,
    BadDepartment,
    PriceDoesNotFit,
    BadBarcodeNumber,
}

/// <summary>Одна карточка PLU для весов CAS CL5000J/CL3000 (структура 147 байт по «CL5000J Communication Protocol», 05.10.2007, Rev 1.0).</summary>
public sealed class CasPlu
{
    /// <summary>Отдел 1…99. В штрихкоде формата «DD…» это двузначный префикс — для кассы 20…29.</summary>
    public int Department { get; init; } = 1;
    /// <summary>Номер PLU 1…999999.</summary>
    public int PluNumber { get; init; }
    /// <summary>Код товара в штрихкоде («I» в формате штрихкода весов), 0…999999.</summary>
    public int ItemCode { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Весовой (по весу) или штучный (по количеству).</summary>
    public bool Weighed { get; init; } = true;
    /// <summary>Цена за кг/штуку в наименьших единицах весов (при двух знаках 125,50 → 12550).</summary>
    public long UnitPriceUnits { get; init; }
    /// <summary>Номер формата штрихкода из таблицы весов (0 — «как настроено на весах», глобальный формат).</summary>
    public int BarcodeNumber { get; init; }
    /// <summary>Номер формата этикетки на весах (0 — формат по умолчанию).</summary>
    public int LabelNumber { get; init; }
}

/// <summary>
/// 2026-11-13: протокол обмена с весами CAS CL5000J / CL3000 по TCP (порт 20304 по умолчанию, как у CL-Works) — документ «CL5000J Communication Protocol» (CAS, 05.10.2007, Rev 1.0);
/// независимая реализация на C# сверена с открытым клиентом alexesDev/cas (MIT, Go). Кадр: Opcode(2) + Address(4, LE) + ',' + Length(2, LE) + ':' + Data + ':' + Checksum(1) + CR.
/// Контрольная сумма — сумма байтов от Address до второго ':' включительно по модулю 256 (так в живых примерах документа; формула «ABCD» из его таблицы с ними расходится).
/// Используются только: «записать PLU» (WL), «прочитать состояние» (RN, только чтение). Стирание PLU/памяти и запись остальных таблиц не реализованы намеренно.
/// </summary>
public static class CasProtocol
{
    public const int DefaultPort = 20304;
    public const int PluStructSize = 147;
    public const int MaxPluNumber = 999_999;
    /// <summary>Цена в меню весов: «от 0 до 999999» (руководство оператора CL5000) — в наименьших единицах (при двух знаках — 9999,99).</summary>
    public const long MaxPriceUnits = 999_999;
    public const int NameLength = 40;
    public const byte Cr = 0x0D;

    /// <summary>Кадр: opcode — две буквы («WL», «RN»), address — «Scale ID» (для команд от ПК — 0).</summary>
    public static byte[] BuildPacket(string opcode, uint address, ReadOnlySpan<byte> data)
    {
        if (opcode.Length != 2)
            throw new ArgumentException("opcode — две буквы", nameof(opcode));
        if (data.Length > 512)
            throw new ArgumentException("данные кадра — не более 512 байт", nameof(data));
        var buf = new List<byte>(data.Length + 14) { (byte)opcode[0], (byte)opcode[1] };
        buf.Add((byte)address); buf.Add((byte)(address >> 8)); buf.Add((byte)(address >> 16)); buf.Add((byte)(address >> 24));
        buf.Add((byte)',');
        buf.Add((byte)data.Length); buf.Add((byte)(data.Length >> 8));
        buf.Add((byte)':');
        foreach (var b in data)
            buf.Add(b);
        buf.Add((byte)':');
        buf.Add(Checksum(buf.ToArray().AsSpan(2)));
        buf.Add(Cr);
        return buf.ToArray();
    }

    /// <summary>Сумма байтов по модулю 256 (от Address до второго ':').</summary>
    public static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (var b in bytes)
            sum = unchecked((byte)(sum + b));
        return sum;
    }

    public static byte[] BuildDownloadPlu(CasPlu plu) => BuildPacket("WL", 0, BuildPluStruct(plu));

    /// <summary>Чтение состояния весов («RN»): без данных, ничего не меняет — для «Проверить связь».</summary>
    public static byte[] BuildReadStatus() => BuildPacket("RN", 0, ReadOnlySpan<byte>.Empty);

    public static CasPluProblem Validate(CasPlu plu)
    {
        if (plu.PluNumber is < 1 or > MaxPluNumber) return CasPluProblem.BadPluNumber;
        if (plu.ItemCode is < 0 or > 999_999) return CasPluProblem.BadItemCode;
        if (plu.Department is < 1 or > 99) return CasPluProblem.BadDepartment;
        if (plu.BarcodeNumber is < 0 or > 99) return CasPluProblem.BadBarcodeNumber;
        if (plu.UnitPriceUnits is < 0 or > MaxPriceUnits) return CasPluProblem.PriceDoesNotFit;
        return CasPluProblem.None;
    }

    /// <summary>147 байт структуры PLU (little-endian, смещения по документу). Остальные поля — 0.</summary>
    public static byte[] BuildPluStruct(CasPlu plu)
    {
        var d = new byte[PluStructSize];
        PutU16(d, 0, plu.Department);
        PutU32(d, 2, (uint)plu.PluNumber);
        d[6] = (byte)(plu.Weighed ? 1 : 3); // 1 — по весу, 3 — по количеству (порядок пунктов меню весов; в документе значения не расписаны — НЕ ПРОВЕРЕНО)
        var (n1, n2) = SplitName(plu.Name);
        PutText(d, 7, NameLength, n1);
        PutText(d, 47, NameLength, n2);
        PutU16(d, 94, plu.LabelNumber);
        PutU32(d, 105, (uint)plu.ItemCode);
        PutU32(d, 113, (uint)plu.UnitPriceUnits);
        PutU16(d, 126, plu.BarcodeNumber);
        return d;
    }

    // ------------------------------------------------------------------ ответы весов

    public enum ReplyKind { Unknown, Good, NoGood, Status }

    public sealed record Reply(ReplyKind Kind, string Opcode, uint ScaleId, byte[] Data, bool ChecksumOk)
    {
        /// <summary>Для «N?»: последний байт данных — номер ошибки (89 — память PLU заполнена, 98 — PLU нет, 97 — неверная структура, 84 — ошибка кадра/суммы и др.).</summary>
        public int ErrorNumber => Kind == ReplyKind.NoGood && Data.Length > 0 ? Data[^1] : 0;
    }

    /// <summary>Разбирает один кадр (до CR включительно). null — это не кадр CAS.</summary>
    public static Reply? ParseReply(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 13 || frame[^1] != Cr || frame[6] != (byte)',' || frame[9] != (byte)':')
            return null;
        var length = frame[7] | (frame[8] << 8);
        if (frame.Length != 10 + length + 3 || frame[10 + length] != (byte)':')
            return null;
        var sumOk = Checksum(frame.Slice(2, 8 + length + 1)) == frame[10 + length + 1];
        var kind = frame[0] switch
        {
            (byte)'G' => ReplyKind.Good,
            (byte)'N' => ReplyKind.NoGood,
            (byte)'W' when frame[1] == (byte)'N' => ReplyKind.Status,
            _ => ReplyKind.Unknown,
        };
        var scaleId = (uint)(frame[2] | (frame[3] << 8) | (frame[4] << 16) | (frame[5] << 24));
        return new Reply(kind, ((char)frame[0]).ToString() + (char)frame[1], scaleId, frame.Slice(10, length).ToArray(), sumOk);
    }

    /// <summary>Состояние весов (30 байт ответа «WN»).</summary>
    public sealed record ScaleStatus(int LoadFlag, bool Stable, int WeightDecimalPoint, int PriceDecimalPoint, long UnitPrice, int PluNumber, int Department);

    public static ScaleStatus? ParseStatus(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30)
            return null;
        return new ScaleStatus(data[0], data[1] == 1, data[5], data[6],
            BitConverter.ToUInt32(data.Slice(16, 4)), (int)BitConverter.ToUInt32(data.Slice(24, 4)), BitConverter.ToUInt16(data.Slice(28, 2)));
    }

    // ------------------------------------------------------------------ название (кодировка весов — ожидается cp1251, НЕ ПРОВЕРЕНО на весах)

    /// <summary>Делит название на две строки по 39 байт (слова не рвём, если есть пробел).</summary>
    public static (string Name1, string Name2) SplitName(string? name)
    {
        var text = Normalize(name);
        if (text.Length <= NameLength - 1)
            return (text, "");
        var cut = text.LastIndexOf(' ', NameLength - 1, NameLength);
        if (cut < NameLength / 2)
            cut = NameLength - 1;
        var first = text[..cut].TrimEnd();
        var second = text[cut..].TrimStart();
        if (second.Length > NameLength - 1)
            second = second[..(NameLength - 1)].TrimEnd();
        return (first, second);
    }

    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
        {
            sb.Append(ch switch
            {
                'ү' => 'у', 'Ү' => 'У', 'ө' => 'о', 'Ө' => 'О', 'ң' => 'н', 'Ң' => 'Н',
                'ў' => 'у', 'Ў' => 'У', 'қ' => 'к', 'Қ' => 'К', 'ғ' => 'г', 'Ғ' => 'Г', 'ҳ' => 'х', 'Ҳ' => 'Х',
                'ұ' => 'у', 'Ұ' => 'У', 'һ' => 'х', 'Һ' => 'Х', 'і' => 'и', 'І' => 'И', 'ә' => 'а', 'Ә' => 'А',
                ' ' => ' ',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>Символ → байт cp1251 (ASCII как есть, А–я, Ё/ё и несколько знаков; остальное — «?»).</summary>
    public static byte Cp1251(char ch)
    {
        if (ch < 0x80) return ch < 0x20 ? (byte)' ' : (byte)ch;
        if (ch is >= 'А' and <= 'я') return (byte)(ch - 'А' + 0xC0);
        return ch switch { 'Ё' => 0xA8, 'ё' => 0xB8, '№' => 0xB9, '«' => 0xAB, '»' => 0xBB, '–' => 0x96, '—' => 0x97, '°' => 0xB0, _ => (byte)'?' };
    }

    private static void PutText(byte[] d, int offset, int size, string text)
    {
        var n = Math.Min(text.Length, size - 1); // последний байт — 0 (C-строка)
        for (var i = 0; i < n; i++)
            d[offset + i] = Cp1251(text[i]);
    }

    private static void PutU16(byte[] d, int offset, int v) { d[offset] = (byte)v; d[offset + 1] = (byte)(v >> 8); }
    private static void PutU32(byte[] d, int offset, uint v) { d[offset] = (byte)v; d[offset + 1] = (byte)(v >> 8); d[offset + 2] = (byte)(v >> 16); d[offset + 3] = (byte)(v >> 24); }

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
            sb.Append(b.ToString("X2")).Append(' ');
        return sb.ToString().TrimEnd();
    }
}
