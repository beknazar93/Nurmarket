using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Одна запись товара для весов Масса-К серий R и SL (файл товаров, FileType 1).</summary>
public sealed record MassaKGoods
{
    /// <summary>ID товара на весах, 1…99999999 (уникальный). Касса берёт номер PLU: повторная отправка того же товара ИЗМЕНЯЕТ запись, а не дублирует.</summary>
    public int Id { get; init; }

    /// <summary>Код товара (текст до 15 знаков). У кассы = PLU цифрами.</summary>
    public string Code { get; init; } = "";

    /// <summary>Цена за кг (или за штуку) в сомах; на весах — в «копейках» (тыйынах), 0…99999999.</summary>
    public decimal Price { get; init; }

    /// <summary>true — штучный (GoodsTypeID = 1), false — весовой (0).</summary>
    public bool Piece { get; init; }

    public string Name { get; init; } = "";

    /// <summary>Состав (до 1500 знаков), пусто — не печатается.</summary>
    public string Ingredients { get; init; } = "";

    /// <summary>Единица измерения, до 5 знаков (у штучных, «шт»); пусто — не записывается.</summary>
    public string Unit { get; init; } = "";

    /// <summary>Тара, г (0 — не записывается).</summary>
    public int TareGrams { get; init; }

    /// <summary>Префикс штрихкода 20…29 (BarcodePrefix), 0 — не записывается (тогда его даёт шаблон этикетки весов).</summary>
    public int BarcodePrefix { get; init; }

    /// <summary>Срок годности в МИНУТАХ (ShelfLife), 0 — не записывается.</summary>
    public int ShelfLifeMinutes { get; init; }
}

public enum MassaKGoodsProblem
{
    None,
    BadId,
    BadCode,
    BadPrice,
    EmptyName,
    BadPrefix,
}

public enum MassaKParseState
{
    Ok,
    NeedMore,
    BadHeader,
    BadCrc,
}

/// <summary>Один кадр обмена: команда и данные (без заголовка, длины и CRC).</summary>
public sealed record MassaKFrame(byte Command, byte[] Data);

/// <summary>
/// 2026-11-13: кодек обмена с весами АО «МАССА-К» (Санкт-Петербург): терминалы серии R (RP, R2P, RL, R2L; в линейке — MK-RP10 / MK-RL10 и др.)
/// и серия SL. Протокол ОТКРЫТЫЙ, описан производителем (massa.ru → «Программное обеспечение» → «Протоколы обмена»):
/// • «Описание форматов файлов и протокола обмена данными с терминалами серии R», ред. 6.44 (2025) и «Протокол обмена весов серии SL», ред. 1.0 (2023) —
///   файлы товаров/PLU/шаблонов, команды 0x80…0x91, CRC; SL «полностью совместим с протоколом R в части обмена файлами»;
/// • «Весы Масса-К. Подключение к компьютеру» (Тв4.177.004 РД, ред. 2 от 22.07.2019) — примеры кадров в hex, порт TCP по умолчанию 5001.
/// Кадр: F8 55 CE | длина тела (2 байта, младший первым) | команда | данные | CRC16 (2 байта, младший первым), CRC считается по «команда + данные»
/// алгоритмом из приложения к протоколу (<see cref="Crc16"/>; все десять примерных кадров из документов совпадают — см. тесты).
///
/// Подтверждено документами: кадр, CRC, команды 0x80/0x40 (состояние файлов), 0x90/0x50 (серийный номер), 0x91/0x51 (режим обмена), 0xA0/0x10 (масса),
/// 0x82/0x42/0x43/0x44 (загрузка записи файла), номера файлов (1 — товары, 5 — PLU/штрихкоды), правило «FileType + 100 = дозагрузить/изменить одну запись».
/// НЕ подтверждено документами (догадка по таблицам; проверить на весах — первая запись только с подтверждением владельца):
/// порядок байт чисел внутри записей файлов (принят младший первым, как в кадре), биты BitMask для кода товара (0–3 — ставим все), нужен ли заголовок «xxPC…»
/// у одиночной записи (не кладём), значения Nums/CurNum у одиночной записи (1 и 1), дополнение строк пробелами, кодировка названия (Windows-1251).
/// Касса НИКОГДА не шлёт CMD_TCP_RESET_FILES (0x81) и не загружает файлы целиком: только одиночные записи (+100), чтобы не стереть справочник весов.
/// </summary>
public static class MassaKProtocol
{
    /// <summary>TCP-порт по умолчанию («Подключение к компьютеру», п. 3.3: «если опции номера порта нет, то используется порт 5001»).</summary>
    public const int DefaultPort = 5001;

    /// <summary>Файл передаётся частями не более 1024 байт.</summary>
    public const int MaxPartBytes = 1024;

    public const byte Header0 = 0xF8;
    public const byte Header1 = 0x55;
    public const byte Header2 = 0xCE;

    // Команды ПК → весы и ответы (R: п. 3.x; SL: п. 4.x).
    public const byte CmdUdpPoll = 0x00;
    public const byte CmdUdpResId = 0x01;
    public const byte CmdGetStatus = 0x80;
    public const byte CmdFileStatus = 0x40;
    public const byte CmdResetFiles = 0x81; // касса не использует никогда
    public const byte CmdDfile = 0x82;
    public const byte CmdAckDfile = 0x42;
    public const byte CmdBadDfile = 0x43;
    public const byte CmdBadDfileSize = 0x44;
    public const byte CmdGetDeviceId = 0x90;
    public const byte CmdAckDeviceId = 0x50;
    public const byte CmdSetWorkMode = 0x91;
    public const byte CmdAckWorkMode = 0x51;
    public const byte CmdNackWorkMode = 0x54;
    public const byte CmdGetWeight = 0xA0;
    public const byte CmdAckWeight = 0x10;
    public const byte CmdUnableToSet = 0x15;
    public const byte CmdNack = 0xF0;

    /// <summary>Режим работы в CMD_TCP_SET_WORK_MODE — «константа» 0x04.</summary>
    public const byte WorkModeExchange = 0x04;

    public const byte FileGoods = 1;
    public const byte FilePlu = 5;

    /// <summary>Примечание п. 4.1: «для дозагрузки одной записи к ранее загруженному файлу или изменения одной записи … указывается число FileType+100».</summary>
    public const int SingleRecordOffset = 100;

    public const int MaxGoodsId = 99_999_999;
    public const int MaxPriceUnits = 99_999_999;
    public const int MaxCodeLength = 15;
    public const int MaxNameLength = 250;
    public const int MaxIngredientsLength = 1500;

    // ------------------------------------------------------------------ кадр и CRC

    /// <summary>CRC из приложения к протоколу (начальное значение 0, по байтам «команда + данные»).</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var b in data)
        {
            ushort accumulator = 0;
            var temp = (ushort)((crc >> 8) << 8);
            for (var bits = 0; bits < 8; bits++)
            {
                accumulator = ((temp ^ accumulator) & 0x8000) != 0 ? (ushort)((accumulator << 1) ^ 0x1021) : (ushort)(accumulator << 1);
                temp <<= 1;
            }
            crc = (ushort)(accumulator ^ (crc << 8) ^ b);
        }
        return crc;
    }

    public static byte[] BuildFrame(byte command, ReadOnlySpan<byte> data = default)
    {
        var body = new byte[1 + data.Length];
        body[0] = command;
        data.CopyTo(body.AsSpan(1));
        var frame = new byte[3 + 2 + body.Length + 2];
        frame[0] = Header0;
        frame[1] = Header1;
        frame[2] = Header2;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), (ushort)body.Length);
        body.CopyTo(frame.AsSpan(5));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5 + body.Length), Crc16(body));
        return frame;
    }

    /// <summary>Разбирает первый кадр в буфере. NeedMore — кадр ещё не пришёл целиком; BadHeader — в начале не заголовок (consumed — сколько байт мусора отбросить);
    /// BadCrc — кадр целый, но CRC не сошёлся (consumed — его длина).</summary>
    public static MassaKParseState TryParse(ReadOnlySpan<byte> buffer, out MassaKFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;
        if (buffer.Length < 3)
            return MassaKParseState.NeedMore;
        if (buffer[0] != Header0 || buffer[1] != Header1 || buffer[2] != Header2)
        {
            // ищем следующий заголовок, мусор до него отбрасываем
            var next = buffer[1..].IndexOf(Header0);
            consumed = next < 0 ? buffer.Length : next + 1;
            return MassaKParseState.BadHeader;
        }
        if (buffer.Length < 5)
            return MassaKParseState.NeedMore;
        var len = BinaryPrimitives.ReadUInt16LittleEndian(buffer[3..]);
        if (len < 1 || len > 4096)
        {
            consumed = 3;
            return MassaKParseState.BadHeader;
        }
        var total = 5 + len + 2;
        if (buffer.Length < total)
            return MassaKParseState.NeedMore;
        consumed = total;
        var body = buffer.Slice(5, len);
        var crc = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(5 + len)..]);
        if (crc != Crc16(body))
            return MassaKParseState.BadCrc;
        frame = new MassaKFrame(body[0], body[1..].ToArray());
        return MassaKParseState.Ok;
    }

    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            if (sb.Length > 0)
                sb.Append(' ');
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ команды «только чтение» и режим обмена

    /// <summary>CMD_TCP_GET_DEVICE_ID — запрос серийного номера (только чтение).</summary>
    public static byte[] BuildGetDeviceId() => BuildFrame(CmdGetDeviceId);

    /// <summary>Серийный номер из ответа 0x50 (4 байта, младший первым); null — это не ответ на запрос номера.</summary>
    public static uint? ParseDeviceId(MassaKFrame frame) =>
        frame.Command == CmdAckDeviceId && frame.Data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(frame.Data) : null;

    /// <summary>CMD_TCP_GET_STATUS — состояние файлов (только чтение).</summary>
    public static byte[] BuildGetStatus() => BuildFrame(CmdGetStatus);

    /// <summary>Маска файлов из ответа 0x40: бит = 1 — файла нет или он с ошибкой (бит 0 — товары, бит 4 — PLU/штрихкоды, бит 5 — шаблоны печати).</summary>
    public static uint? ParseFileStatus(MassaKFrame frame) =>
        frame.Command == CmdFileStatus && frame.Data.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(frame.Data) : null;

    /// <summary>CMD_TCP_GET_WEIGHT — текущая масса (только чтение).</summary>
    public static byte[] BuildGetWeight() => BuildFrame(CmdGetWeight);

    /// <summary>Масса из ответа 0x10: граммы со знаком (цена деления учитывается), признак стабильности.</summary>
    public static (double Kg, bool Stable)? ParseWeight(MassaKFrame frame)
    {
        if (frame.Command != CmdAckWeight || frame.Data.Length != 6)
            return null;
        var raw = BinaryPrimitives.ReadInt32LittleEndian(frame.Data);
        var division = frame.Data[4];
        // Weight — в единицах цены деления: 0 — 100 мг, 1 — 1 г, 2 — 10 г, 3 — 100 г, 4 — 1 кг (пример документа: 00 00 00 00 01 01 → 0 г, стабильно).
        var grams = division switch { 0 => raw * 0.1, 1 => raw, 2 => raw * 10.0, 3 => raw * 100.0, 4 => raw * 1000.0, _ => raw };
        return (grams / 1000.0, frame.Data[5] == 1);
    }

    /// <summary>CMD_TCP_SET_WORK_MODE (0x91, режим 0x04) — по протоколу R им начинается обмен файлами; в протоколе «1С» тот же кадр — «проверка связи».</summary>
    public static byte[] BuildSetWorkMode() => BuildFrame(CmdSetWorkMode, new[] { WorkModeExchange });

    // ------------------------------------------------------------------ записи файлов

    public static MassaKGoodsProblem Validate(MassaKGoods g)
    {
        if (g.Id is < 1 or > MaxGoodsId)
            return MassaKGoodsProblem.BadId;
        if (string.IsNullOrWhiteSpace(g.Code) || g.Code.Length > MaxCodeLength || g.Code.Any(ch => ch < 0x20 || ch > 0x7E))
            return MassaKGoodsProblem.BadCode;
        if (g.Price < 0 || PriceUnits(g.Price) > MaxPriceUnits)
            return MassaKGoodsProblem.BadPrice;
        if (string.IsNullOrWhiteSpace(g.Name))
            return MassaKGoodsProblem.EmptyName;
        if (g.BarcodePrefix is < 0 or > 99)
            return MassaKGoodsProblem.BadPrefix;
        return MassaKGoodsProblem.None;
    }

    /// <summary>Цена в «копейках» (тыйынах) с округлением.</summary>
    public static long PriceUnits(decimal price) => (long)Math.Round(price * 100m, MidpointRounding.AwayFromZero);

    private static byte[] FixedAscii(string text, int length)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        var src = Encoding.ASCII.GetBytes(text ?? "");
        Array.Copy(src, bytes, Math.Min(src.Length, length));
        return bytes;
    }

    /// <summary>Текстовое поле записи: 2 байта длины строки + строка. Пустое — 00 00 (п. 2.1, примечание 4).</summary>
    private static void WriteText(List<byte> into, byte[] text)
    {
        into.Add((byte)(text.Length & 0xFF));
        into.Add((byte)(text.Length >> 8));
        into.AddRange(text);
    }

    private static void WriteU32(List<byte> into, long value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, (uint)value);
        into.AddRange(tmp.ToArray());
    }

    /// <summary>Название на весах: «|» — перенос строки, «^NN» в начале строки — номер шрифта (драйвер R, п. 4.4): из названий товаров их убираем.</summary>
    public static string CleanText(string? text, int maxLength)
    {
        var t = (text ?? "").Replace('|', '/').Replace('^', ' ').Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > maxLength ? t[..maxLength].TrimEnd() : t;
    }

    private static readonly Encoding Cp1251 = ResolveCp1251();

    private static Encoding ResolveCp1251()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // уже зарегистрирован
        }
        return Encoding.GetEncoding(1251, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }

    /// <summary>Кодировка текста записи: Windows-1251 (кыргызские буквы, которых в ней нет, заменяются похожими) либо латиница (<paramref name="latin"/>).</summary>
    public static byte[] EncodeText(string text, bool latin)
    {
        if (latin)
            return Encoding.ASCII.GetBytes(RongtaTransliterator.Transliterate(text).Where(ch => ch is >= ' ' and <= '~').ToArray());
        var mapped = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            mapped.Append(ch switch
            {
                'ү' => 'у', 'Ү' => 'У', 'ө' => 'о', 'Ө' => 'О', 'ң' => 'н', 'Ң' => 'Н',
                'ғ' => 'г', 'Ғ' => 'Г', 'қ' => 'к', 'Қ' => 'К', 'ұ' => 'у', 'Ұ' => 'У', 'ҳ' => 'х', 'Ҳ' => 'Х',
                _ => ch,
            });
        }
        return Cp1251.GetBytes(mapped.ToString());
    }

    /// <summary>Запись файла товаров (п. 1.2): ID(4) Length(2) DigLength(1) BitMask(4) + заполненные числовые поля по порядку + Name + Ingredients.
    /// Length — с DigLength до конца состава; DigLength — с BitMask до BarcodePrefix. Числа — младший байт первым (догадка).</summary>
    public static byte[] BuildGoodsRecord(MassaKGoods g, bool latinText = false)
    {
        uint mask = 0;
        var code = FixedAscii(g.Code, MaxCodeLength);
        // №5 Code, биты 0–3 (в таблице протокола для кода отведено четыре бита маски)
        mask |= 0x0F;
        var numbers = new List<byte>();
        numbers.AddRange(code);
        if (!string.IsNullOrEmpty(g.Unit))
        {
            mask |= 1u << 4;
            numbers.AddRange(FixedAscii(g.Unit, 5));
        }
        var price = PriceUnits(g.Price);
        if (price > 0)
        {
            mask |= 1u << 5;
            WriteU32(numbers, price);
        }
        if (g.TareGrams > 0)
        {
            mask |= 1u << 6;
            WriteU32(numbers, g.TareGrams);
        }
        // бит 7 (UnitWeight) — масса одной штуки в мг: касса не знает, не пишем (иначе весы уйдут в счётный режим)
        if (g.Piece)
        {
            mask |= 1u << 8;
            numbers.Add(1);
        }
        // бит 8 при весовом не ставим: 0 = «параметр равен нулю, не записывается», значение по умолчанию — весовой
        if (g.ShelfLifeMinutes > 0)
        {
            mask |= 1u << 13;
            WriteU32(numbers, g.ShelfLifeMinutes);
        }
        if (g.BarcodePrefix > 0)
        {
            mask |= 1u << 15;
            numbers.Add((byte)g.BarcodePrefix);
        }
        // порядок полей по таблице: Code, BasicUnit, Price, TareWeight, UnitWeight, GoodsTypeID, GoodsGroupCode, AdditionPercent, NameTextAlignmentID,
        // BestBefore, ShelfLife, CertificationCode, BarcodePrefix — ShelfLife (13) идёт после GoodsTypeID (8), префикс (15) — последний: порядок сохранён выше
        var digBytes = new List<byte>();
        WriteU32(digBytes, mask);
        digBytes.AddRange(numbers);

        var name = EncodeText(CleanText(g.Name, MaxNameLength), latinText);
        var ingredients = EncodeText(CleanText(g.Ingredients, MaxIngredientsLength), latinText);
        var tail = new List<byte>();
        WriteText(tail, name);
        WriteText(tail, ingredients);

        var length = 1 + digBytes.Count + tail.Count;
        var record = new List<byte>(4 + 2 + length);
        WriteU32(record, g.Id);
        record.Add((byte)(length & 0xFF));
        record.Add((byte)(length >> 8));
        record.Add((byte)digBytes.Count);
        record.AddRange(digBytes);
        record.AddRange(tail);
        return record.ToArray();
    }

    /// <summary>Запись файла PLU/штрихкодов (п. 1.6): ID(4) Length(2) Code(6: PLU весового товара или штрихкод) GoodsID(4) Unit(5) ConversionFactor(4, 1000 = «1:1»).</summary>
    public static byte[] BuildPluRecord(int id, long pluOrBarcode, int goodsId, string unit = "", int conversionFactor = 1000)
    {
        var record = new List<byte>(25);
        WriteU32(record, id);
        const int length = 6 + 4 + 5 + 4;
        record.Add(length);
        record.Add(0);
        Span<byte> code = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(code, (ulong)pluOrBarcode);
        record.AddRange(code[..6].ToArray());
        WriteU32(record, goodsId);
        record.AddRange(FixedAscii(unit, 5));
        WriteU32(record, conversionFactor);
        return record.ToArray();
    }

    /// <summary>CMD_TCP_DFILE (0x82): FileType(1) Nums(2) CurNum(2) DataLen(2) Data. Для одиночной записи FileType уже с +100 (<see cref="SingleRecordOffset"/>).</summary>
    public static byte[] BuildDfile(int fileType, int nums, int curNum, ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxPartBytes)
            throw new ArgumentException("Часть файла — не более 1024 байт.", nameof(data));
        var payload = new byte[1 + 2 + 2 + 2 + data.Length];
        payload[0] = (byte)fileType;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(1), (ushort)nums);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3), (ushort)curNum);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(5), (ushort)data.Length);
        data.CopyTo(payload.AsSpan(7));
        return BuildFrame(CmdDfile, payload);
    }

    /// <summary>Два кадра на один товар: запись в файл товаров (101) и в файл PLU/штрихкодов (105). Без стирания, только одиночные записи.</summary>
    public static (byte[] Goods, byte[] Plu) BuildUpsertFrames(MassaKGoods g, long plu, bool latinText = false)
    {
        var goods = BuildDfile(FileGoods + SingleRecordOffset, 1, 1, BuildGoodsRecord(g, latinText));
        var pluRecord = BuildDfile(FilePlu + SingleRecordOffset, 1, 1, BuildPluRecord(g.Id, plu, g.Id, g.Piece ? g.Unit : ""));
        return (goods, pluRecord);
    }

    /// <summary>Ответ на DFILE: принят (0x42) / ошибка. FileType в ответе должен совпасть с отправленным.</summary>
    public static bool IsDfileAck(MassaKFrame frame, int fileType) =>
        frame.Command == CmdAckDfile && frame.Data.Length == 5 && frame.Data[0] == fileType;
}
