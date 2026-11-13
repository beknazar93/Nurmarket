using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-11-13: одна карточка товара в «родном» формате весов Rongta RLS (64 байта, как её пишет программа RLS1000).</summary>
public sealed record RongtaUdpPlu
{
    /// <summary>Байты названия (до 36). Касса берёт те же байты, что и для «!0V» (<see cref="RongtaNameCodec.EncodeBytes"/>).</summary>
    public byte[] NameBytes { get; init; } = Array.Empty<byte>();

    /// <summary>LF-код (6 цифр) — по нему весы находят карточку («single item edit», «Update PLU» RLS1000).</summary>
    public int LfCode { get; init; }

    /// <summary>Код товара Code / Art. No. (до 10 цифр) — его весы печатают в штрихкоде (буквы I в таблице типов).</summary>
    public long Code { get; init; }

    /// <summary>Отдел (DD) — первые цифры штрихкода у типов 00–09.</summary>
    public int Department { get; init; }

    /// <summary>Тип штрихкода 0–99 (Appendix II руководства RLS1000).</summary>
    public int BarcodeType { get; init; }

    /// <summary>Цена в целых единицах весов (сом × 10^знаков), до 8 цифр.</summary>
    public long PriceUnits { get; init; }

    /// <summary>Единица: 4 — кг (весовой), 9 — шт. (SYSTEM.CFG RLS1000, [User Weight Unit]).</summary>
    public int WeightUnit { get; init; } = RongtaUdpProtocol.UnitKg;

    /// <summary>Старшие 4 бита байта единицы (0–15). В TXP — 19-я колонка; касса пишет 0, как RLS1000 по умолчанию.</summary>
    public int UnitHighNibble { get; init; }

    public int ShelfLifeDays { get; init; }
    public int Message1 { get; init; }
    public int Message2 { get; init; }
    public int TareGrams { get; init; }
    /// <summary>Тип упаковки 0–3: 0 — обычный, 1 — фиксированный вес, 2 — фиксированная цена, 3 — оба.</summary>
    public int PackType { get; init; }
    public int PackErrorPercent { get; init; }
    public int PackWeightGrams { get; init; }
    public int Rebate { get; init; }
    public int Account { get; init; }
}

/// <summary>
/// 2026-11-13: «родной» сетевой протокол весов Rongta RLS — тот, которым программа RLS1000 V1.129 (с диска весов) пишет товары.
/// Восстановлен на ЛОКАЛЬНОЙ ловушке: копия RLS1000 с адресом весов 127.0.0.1, ответы ловушки — по разбору RLS1000.exe
/// (функции 0x6d4afc «подключиться», 0x6d50fc «пакет → ответ», 0x6d44d8 «разбор ответа»). На реальные весы при разборе ничего не отправлялось.
/// Отчёт и эталоны — docs/Отчеты/Rongta-RLS1000-разбор.md, NurMarketKassa.Tests/Hardware/RongtaUdpFixtures.
///
/// • Связь — UDP, порт весов 5001 (зашит в RLS1000; режим «TCP/IP» её настроек — это UDP).
/// • Пакет всегда 261 байт: команда (1) + параметр (2, младший байт первым) + данные (256) + сумма (2) — сумма всех 259 байт до неё, младший байт первым.
/// • Ответ весов: первый байт 0x02 — принято, 0x55 — отказ; ответ с данными (чтение) — 301 байт и больше.
/// • 0E/0001 — «подключиться»: номер весов (4 байта) + дата и время ПК (BCD гг мм дд чч мм сс). 0E/0002 — «отключиться» (номер весов).
///   Номер весов = IPv4-адрес как число, последние 8 десятичных цифр в BCD, младшая пара первой (127.0.0.1 → 33 64 70 30).
/// • 0B/0000 — «обновить PLU» (меню RLS1000 «обновить plu», F8): до 4 карточек по 64 байта; весы ищут карточку по LF-коду.
/// Карточка: 0–35 имя · 36–38 LF (BCD) · 39 таблица скидок · 40 счёт · 41 отдел (BCD) · 42 ТИП ШТРИХКОДА (двоичный, 17 → 0x11) ·
/// 43–46 цена (BCD 8) · 47 единица (младшие 4 бита) · 48–49 срок годности (дни, младший байт первым) · 50 сообщение 1 · 51 сообщение 2 ·
/// 52–54 тара (BCD 6, граммы) · 55 тип упаковки (биты 6–7) + допуск % (биты 0–5) · 56–58 вес упаковки (BCD 6, граммы) · 59–63 код (BCD 10).
/// В протоколе «!0V» (порт 4001, им касса пишет товары) поля типа штрихкода НЕТ — поэтому тип пишется этим протоколом.
/// НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО (байты совпадают с RLS1000 байт в байт — см. тесты).
/// </summary>
public static class RongtaUdpProtocol
{
    public const int DefaultPort = 5001;
    public const int PacketLength = 261;
    public const int DataLength = 256;
    public const int RecordLength = 64;
    public const int RecordsPerPacket = DataLength / RecordLength;
    public const int NameLength = 36;

    public const byte CmdControl = 0x0E;
    public const byte CmdUpdatePlu = 0x0B;
    public const ushort ControlConnect = 0x0001;
    public const ushort ControlDisconnect = 0x0002;

    public const byte ReplyAck = 0x02;
    public const byte ReplyNak = 0x55;

    public const int UnitKg = 4;
    public const int UnitPiece = 9;

    public const long MaxPriceUnits = 99_999_999;

    /// <summary>Пакет 261 байт: команда, параметр (LE), данные (дополняются нулями), сумма (LE).</summary>
    public static byte[] BuildPacket(byte command, ushort parameter, ReadOnlySpan<byte> data)
    {
        if (data.Length > DataLength)
            throw new ArgumentException("data > 256", nameof(data));
        var packet = new byte[PacketLength];
        packet[0] = command;
        packet[1] = (byte)(parameter & 0xFF);
        packet[2] = (byte)(parameter >> 8);
        data.CopyTo(packet.AsSpan(3));
        var sum = 0;
        for (var i = 0; i < PacketLength - 2; i++)
            sum += packet[i];
        packet[PacketLength - 2] = (byte)(sum & 0xFF);
        packet[PacketLength - 1] = (byte)((sum >> 8) & 0xFF);
        return packet;
    }

    /// <summary>Сумма пакета сходится (для ловушки/стенда).</summary>
    public static bool ChecksumOk(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketLength)
            return false;
        var sum = 0;
        for (var i = 0; i < PacketLength - 2; i++)
            sum += packet[i];
        return packet[PacketLength - 2] == (byte)(sum & 0xFF) && packet[PacketLength - 1] == (byte)((sum >> 8) & 0xFF);
    }

    /// <summary>Номер весов из IP (как RLS1000: число адреса, последние 8 цифр, BCD, младшая пара первой).</summary>
    public static byte[] ScaleId(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("IPv4 only", nameof(address));
        var b = address.GetAddressBytes();
        var number = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        var digits = (number % 100_000_000u).ToString("00000000", CultureInfo.InvariantCulture);
        return new[]
        {
            Bcd(digits.Substring(6, 2)), Bcd(digits.Substring(4, 2)), Bcd(digits.Substring(2, 2)), Bcd(digits.Substring(0, 2)),
        };
    }

    /// <summary>0E/0001 «подключиться» — с датой и временем ПК (так делает RLS1000 при каждом подключении).</summary>
    public static byte[] BuildConnect(IPAddress scale, DateTime now)
    {
        var data = new byte[10];
        ScaleId(scale).CopyTo(data, 0);
        data[4] = Bcd(now.Year % 100);
        data[5] = Bcd(now.Month);
        data[6] = Bcd(now.Day);
        data[7] = Bcd(now.Hour);
        data[8] = Bcd(now.Minute);
        data[9] = Bcd(now.Second);
        return BuildPacket(CmdControl, ControlConnect, data);
    }

    /// <summary>0E/0002 «отключиться».</summary>
    public static byte[] BuildDisconnect(IPAddress scale) => BuildPacket(CmdControl, ControlDisconnect, ScaleId(scale));

    /// <summary>Карточка 64 байта (раскладка — в описании класса).</summary>
    public static byte[] BuildRecord(RongtaUdpPlu plu)
    {
        if (plu.LfCode is < 0 or > 999_999)
            throw new ArgumentOutOfRangeException(nameof(plu), "LF 0..999999");
        if (plu.Code is < 0 or > 9_999_999_999)
            throw new ArgumentOutOfRangeException(nameof(plu), "Code 0..9999999999");
        if (plu.PriceUnits is < 0 or > MaxPriceUnits)
            throw new ArgumentOutOfRangeException(nameof(plu), "Price 0..99999999");
        var r = new byte[RecordLength];
        var name = plu.NameBytes.Length > NameLength ? plu.NameBytes.AsSpan(0, NameLength) : plu.NameBytes.AsSpan();
        r.AsSpan(0, NameLength).Fill(0x20);
        name.CopyTo(r);
        PutBcd(r, 36, plu.LfCode, 6);
        r[39] = (byte)Math.Clamp(plu.Rebate, 0, 255);
        r[40] = (byte)Math.Clamp(plu.Account, 0, 255);
        PutBcd(r, 41, Math.Clamp(plu.Department, 0, 99), 2);
        r[42] = (byte)Math.Clamp(plu.BarcodeType, 0, 99);
        PutBcd(r, 43, plu.PriceUnits, 8);
        r[47] = (byte)((Math.Clamp(plu.UnitHighNibble, 0, 15) << 4) | Math.Clamp(plu.WeightUnit, 0, 15));
        var shelf = Math.Clamp(plu.ShelfLifeDays, 0, 65535);
        r[48] = (byte)(shelf & 0xFF);
        r[49] = (byte)(shelf >> 8);
        r[50] = (byte)Math.Clamp(plu.Message1, 0, 255);
        r[51] = (byte)Math.Clamp(plu.Message2, 0, 255);
        PutBcd(r, 52, Math.Clamp(plu.TareGrams, 0, 999_999), 6);
        r[55] = (byte)((Math.Clamp(plu.PackType, 0, 3) << 6) | Math.Clamp(plu.PackErrorPercent, 0, 63));
        PutBcd(r, 56, Math.Clamp(plu.PackWeightGrams, 0, 999_999), 6);
        PutBcd(r, 59, plu.Code, 10);
        return r;
    }

    /// <summary>0B/0000 «обновить PLU»: по 4 карточки в пакете (пустые места — нули, как у RLS1000).</summary>
    public static List<byte[]> BuildUpdatePackets(IEnumerable<RongtaUdpPlu> plus)
    {
        var packets = new List<byte[]>();
        var data = new byte[DataLength];
        var slot = 0;
        foreach (var plu in plus)
        {
            BuildRecord(plu).CopyTo(data, slot * RecordLength);
            if (++slot == RecordsPerPacket)
            {
                packets.Add(BuildPacket(CmdUpdatePlu, 0, data));
                data = new byte[DataLength];
                slot = 0;
            }
        }
        if (slot > 0)
            packets.Add(BuildPacket(CmdUpdatePlu, 0, data));
        return packets;
    }

    /// <summary>Карточка из записи «!0V» кассы: LF = номер PLU (ячейка), Code = «код в ШК», отдел = префикс, цена — по «Price point» весов.</summary>
    public static RongtaUdpPlu FromDahua(DahuaTmPlu plu, int priceDecimals, int barcodeType) => new()
    {
        // 2026-11-13: в карточке RLS1000 имя — обычный Windows-1251 (пары «!0V» печатались мусором: пробел → «ЎЎ», скобки → «JЁ»/«J®»).
        NameBytes = RongtaNameCodec.EncodeCardBytes(plu.Name, NameLength),
        LfCode = plu.PluNumber,
        Code = plu.ProductCode,
        Department = plu.BarcodePrefix,
        BarcodeType = barcodeType,
        PriceUnits = DahuaTmProtocol.ScalePrice(plu.Price, priceDecimals),
        WeightUnit = plu.WeighMode == DahuaTmWeighMode.Piece ? UnitPiece : UnitKg,
        ShelfLifeDays = plu.ShelfLifeDays,
        TareGrams = (int)Math.Round(plu.TareKg * 1000m, MidpointRounding.AwayFromZero),
    };

    public enum ReplyKind
    {
        None,
        Ack,
        Nak,
        Data,
    }

    public static ReplyKind Classify(ReadOnlySpan<byte> reply) =>
        reply.Length == 0 ? ReplyKind.None
        : reply[0] == ReplyAck ? ReplyKind.Ack
        : reply[0] == ReplyNak ? ReplyKind.Nak
        : ReplyKind.Data;

    /// <summary>Для журнала: «0e 01 00 33 … [00×246] f2 01» — длинные серии нулей сжаты.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var parts = new List<string>(bytes.Length);
        for (var i = 0; i < bytes.Length;)
        {
            var j = i;
            while (j < bytes.Length && bytes[j] == 0)
                j++;
            if (j - i >= 8)
            {
                parts.Add($"[00×{j - i}]");
                i = j;
                continue;
            }
            parts.Add(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            i++;
        }
        return string.Join(' ', parts);
    }

    private static byte Bcd(int value) => (byte)(((value / 10) % 10) << 4 | (value % 10));

    private static byte Bcd(string twoDigits) => (byte)(((twoDigits[0] - '0') << 4) | (twoDigits[1] - '0'));

    private static void PutBcd(byte[] target, int offset, long value, int digits)
    {
        var text = Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
        text = text.Length > digits ? text[^digits..] : text.PadLeft(digits, '0');
        for (var i = 0; i < digits / 2; i++)
            target[offset + i] = Bcd(text.Substring(i * 2, 2));
    }
}
