using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Тип товара на весах Dahua TM (поле mweigh_mode): 0 — «Взвешивание», 1 — «поштучный»,
/// 2 — «определить вес» (фиксированный вес). Названия — как в их программе «Русский масштаб».</summary>
public enum DahuaTmWeighMode
{
    Weighed = 0,
    Piece = 1,
    FixedWeight = 2,
}

/// <summary>Одна запись PLU для весов Dahua TM-30F / TM-A / TM-F (команда «!0V»).</summary>
public sealed record DahuaTmPlu
{
    /// <summary>Номер PLU (ячейки) на весах, 1…4000 (mplu_no, в их базе data_lmt_max = 4000).
    /// Горячие клавиши весов (!0L) ссылаются именно на этот номер.</summary>
    public int PluNumber { get; init; }

    /// <summary>«Код товара» (mcode, ровно 7 цифр). Именно он печатается в весовом штрих-коде
    /// (буквы W в формате ШК весов), а не номер PLU.</summary>
    public long ProductCode { get; init; }

    /// <summary>Цена за кг (или за штуку у штучного товара) в сомах.</summary>
    public decimal Price { get; init; }

    public DahuaTmWeighMode WeighMode { get; init; } = DahuaTmWeighMode.Weighed;

    /// <summary>Срок годности в днях (mshelflife, 3 цифры, 0…999).</summary>
    public int ShelfLifeDays { get; init; }

    /// <summary>«Префикс штрихкода» товара (madv5, 2 цифры) — это F/FF в формате ШК весов.</summary>
    public int BarcodePrefix { get; init; } = 20;

    /// <summary>Тара в кг (mtare, в пакете — граммы, 5 цифр).</summary>
    public decimal TareKg { get; init; }

    /// <summary>Номер этикетки (mlab_no, 0…15).</summary>
    public int LabelNumber { get; init; }

    public string Name { get; init; } = "";

    /// <summary>«Примечание A» / «Примечание B» (mname2 / mname3).</summary>
    public string NoteA { get; init; } = "";
    public string NoteB { get; init; } = "";

    /// <summary>«Специальная информация 1…3» (madv1…madv3, номера текстов 0…22). Касса шлёт 0.</summary>
    public int SpecialInfo1 { get; init; }
    public int SpecialInfo2 { get; init; }
    public int SpecialInfo3 { get; init; }

    /// <summary>madv4 и madv6…madv18 (по 2 цифры) — смысл неизвестен, их программа шлёт нули.
    /// Касса их не заполняет; поле нужно только для сверки с эталонными строками EXE.
    /// Порядок: madv4, madv6, madv7, …, madv18 (14 значений).</summary>
    public IReadOnlyList<int>? ReservedAdv { get; init; }
}

/// <summary>2026-09-30: как записывать имя товара в «!0V». Dahua — по 3 цифры на байт CP1251
/// (TM-30F); Rongta — парами по 4 цифры (<see cref="RongtaNameCodec"/>, подобрано на живых весах).</summary>
public enum DahuaTmNameCodec
{
    Dahua = 0,
    Rongta = 1,
}

/// <summary>Почему запись PLU не может уйти на весы (текст на 5 языках — в окне кассы).</summary>
public enum DahuaTmPluProblem
{
    None,
    BadPluNumber,
    BadProductCode,
    PriceDoesNotFit,
    BadShelfLife,
    BadBarcodePrefix,
    TareDoesNotFit,
    BadLabelNumber,
    BadSpecialInfo,
}

/// <summary>
/// 2026-09-28: кодек протокола весов Dahua (Shanghai Dahua Scale, серия TM-A / TM-F; у владельца
/// «TM-30F Series BARCODE PRINTING SCALE»). Просьба владельца — «чтобы наша программа могла
/// напрямую отправлять на весы TM-30F». Документации нет; всё ниже восстановлено по их
/// программе «Русский масштаб V1.3» (mscale.exe + datatransters.dll), только чтением файлов:
///
/// • СВЯЗЬ. TCP-клиент к IP весов, порт 4001 (зашит в datatransters.dll; в весах сетевой модуль
///   ZLG ZNE-100T — мост TCP→COM). По COM: «COM%d:%d,n,8,1».
/// • КОМАНДЫ — строки ASCII. mscale сам дописывает «\r\n» (строка 0x4a3378 после «!0V»+поля),
///   DLL шлёт строку как есть. !0V — PLU, !0L — горячие клавиши, !0O — системные параметры,
///   !0T/!0R — этикетка, !0X — спец. информация, !0Z — тексты, чтение: «!0J%04dA\r\n» (PLU по
///   номеру, «0000%d» с отрезанием до 4 цифр) и «!0HA\r\n» (продажи).
/// • ОТВЕТ (datatransters.dll, 0x1000c630): ответ считается полным, когда в принятом есть
///   «\r\n\x03» или «\r\r\n»; хвостовые байты &lt; 0x20 отрезаются; короче 5 знаков — не ответ
///   (ждём дальше). Второй символ 'e'/'j' и «ac» с 7-го символа (индекс 6) — «конец данных»;
///   k n q s u w y — записи чтения; ЛЮБОЙ другой полный ответ — «принято, следующая строка».
/// • ОЖИДАНИЕ (0x1000ddc0/0x1000dc50): одна строка → ждать ответ 2500 мс (0x9c4) → при тишине
///   повторить ту же строку, после 4 повторов — «Коммуникация не удалось».
/// • ПОЛЯ PLU — таблица t_base_merchandise в mscommpro.mdb (item_index, item_len,
///   item_max_len, item_tail, item_transform) и сборщик mscale.exe 0x431020…0x431570:
///   числа дополняются нулями слева до item_len (длиннее — берутся последние item_len цифр);
///   поля FLOAT умножаются на 10^data_lmt_other (mscaledbset: у цены 0 — «Price point:
///   Integer [123]», у тары 3 — кг→г) и округляются (+0,5); имена (item_transform = 1) — каждый
///   байт в 3 десятичные цифры («000%d», правые 3) и в конце «000»; затем хвост (A, B, C, D, E).
///
/// Строка, байт в байт как у mscale (3 эталонные строки «PLU Demo» из EXE совпадают при
/// <c>nameTerminator:false</c> — в демо-строках «000» нет, реальный сборщик его добавляет):
/// <code>!0V pppp A ccccccc $$$$$$ t i1 i2 i3 sss ff dd 0000000000000 ttttt ll (13×"00") B имя1 000 C прим.A 000 D прим.B 000 E \r\n</code>
/// НА ЖИВЫХ ВЕСАХ НЕ ПРОВЕРЕНО.
/// </summary>
public static class DahuaTmProtocol
{
    /// <summary>TCP-порт весов (datatransters.dll, 0xfa1).</summary>
    public const int DefaultPort = 4001;

    /// <summary>Ожидание ответа на строку — как у их DLL (0x9c4 мс).</summary>
    public const int DefaultReplyTimeoutMs = 2500;

    /// <summary>Сколько раз повторить строку без ответа — как у их DLL (счётчик &gt; 4 → ошибка).</summary>
    public const int DefaultRetries = 4;

    public const int MaxPluNumber = 4000;

    /// <summary>item_max_len имени = 132 цифры кода; «000» в конце тоже в счёт → 43 знака.</summary>
    public const int MaxNameChars = 43;

    public const string LineEnd = "\r\n";

    /// <summary>Эталоны «PLU Demo» из mscale.exe (0x4a3bc4, 0x4a3b5c, 0x4a3ae0) — для самопроверки.</summary>
    public static readonly IReadOnlyList<string> ExeDemoLines = new[]
    {
        "!0V0001A0228100000011000000000803000000000000000000000000000000000000000000000000B116101115116049CDE",
        "!0V0002A0228010000022000000008020000000000000000000000100000000000000000000000000B116101115116050CDE",
        "!0V0003A0229002000033001020310030000000000000000000000000000610071240131820192330B116101115116051C107107107D108108108E",
    };

    /// <summary>Поля между «A» и «B» в порядке item_index (mscommpro.mdb → t_base_merchandise).</summary>
    private static readonly (string Column, int Length)[] FixedFields =
    {
        ("mcode", 7),
        ("mprice", 6),
        ("mweigh_mode", 1),
        ("madv1", 2), ("madv2", 2), ("madv3", 2),
        ("mshelflife", 3),
        ("madv5", 2), // «Префикс штрихкода»
        ("madv4", 2),
        ("m13code", 13),
        ("mtare", 5),
        ("mlab_no", 2),
        ("madv6", 2), ("madv7", 2), ("madv8", 2), ("madv9", 2), ("madv10", 2), ("madv11", 2), ("madv12", 2),
        ("madv13", 2), ("madv14", 2), ("madv15", 2), ("madv16", 2), ("madv17", 2), ("madv18", 2),
    };

    /// <summary>Длина цифровой части между «A» и «B» (73).</summary>
    public static int FixedPartLength => FixedFields.Sum(f => f.Length);

    private static readonly Encoding Cp1251 = ResolveCp1251();

    private static Encoding ResolveCp1251()
    {
        // Как в ShtrikhPrintProtocol: провайдер регистрируется на старте приложения, но кодек
        // может вызываться из утилиты/стенда — регистрируем сами.
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch (Exception)
        {
            // уже зарегистрирован или недоступен — ниже GetEncoding скажет
        }
        return Encoding.GetEncoding(1251, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }

    /// <summary>Проверка записи до отправки. Цена переводится в целое число с учётом
    /// «Price point» весов (<paramref name="priceDecimals"/>).</summary>
    public static DahuaTmPluProblem Validate(DahuaTmPlu plu, int priceDecimals)
    {
        if (plu.PluNumber is < 1 or > MaxPluNumber)
            return DahuaTmPluProblem.BadPluNumber;
        if (plu.ProductCode is < 0 or > 9_999_999)
            return DahuaTmPluProblem.BadProductCode;
        if (plu.Price < 0 || ScalePrice(plu.Price, priceDecimals) > 999_999)
            return DahuaTmPluProblem.PriceDoesNotFit;
        if (plu.ShelfLifeDays is < 0 or > 999)
            return DahuaTmPluProblem.BadShelfLife;
        if (plu.BarcodePrefix is < 0 or > 99)
            return DahuaTmPluProblem.BadBarcodePrefix;
        if (plu.TareKg < 0 || ScaleDecimal(plu.TareKg, 3) > 99_999)
            return DahuaTmPluProblem.TareDoesNotFit;
        if (plu.LabelNumber is < 0 or > 15)
            return DahuaTmPluProblem.BadLabelNumber;
        if (plu.SpecialInfo1 is < 0 or > 99 || plu.SpecialInfo2 is < 0 or > 99 || plu.SpecialInfo3 is < 0 or > 99)
            return DahuaTmPluProblem.BadSpecialInfo;
        return DahuaTmPluProblem.None;
    }

    /// <summary>Цена в целых единицах весов: сом × 10^priceDecimals, округление как у mscale
    /// (+0,5 и отбросить дробь). priceDecimals = «Price point» системных параметров весов:
    /// 0 — «Integer [123]» (так у владельца), 1 — [12.3], 2 — [1.23], 3 — [0.123].</summary>
    public static long ScalePrice(decimal price, int priceDecimals) => ScaleDecimal(price, Math.Clamp(priceDecimals, 0, 3));

    private static long ScaleDecimal(decimal value, int decimals)
    {
        var scaled = value;
        for (var i = 0; i < decimals; i++)
            scaled *= 10m;
        return (long)Math.Floor(scaled + 0.5m);
    }

    /// <summary>Строка «!0V…» с «\r\n» на конце. Бросает ArgumentException, если запись не
    /// проходит <see cref="Validate"/> — вызывающий должен проверить заранее.</summary>
    /// <param name="nameTerminator">«000» после каждого имени, как у настоящего сборщика mscale.
    /// false — как в демо-строках EXE (используется только самопроверкой).</param>
    /// <param name="codec">2026-09-30: Rongta — имена парами (<see cref="RongtaNameCodec"/>).</param>
    public static string BuildPluCommand(DahuaTmPlu plu, int priceDecimals, bool nameTerminator = true,
        DahuaTmNameCodec codec = DahuaTmNameCodec.Dahua)
    {
        var problem = Validate(plu, priceDecimals);
        if (problem != DahuaTmPluProblem.None)
            throw new ArgumentException($"PLU {plu.PluNumber}: {problem}", nameof(plu));

        var values = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["mcode"] = plu.ProductCode,
            ["mprice"] = ScalePrice(plu.Price, priceDecimals),
            ["mweigh_mode"] = (int)plu.WeighMode,
            ["mshelflife"] = plu.ShelfLifeDays,
            ["madv5"] = plu.BarcodePrefix,
            ["mtare"] = ScaleDecimal(plu.TareKg, 3),
            ["mlab_no"] = plu.LabelNumber,
            ["madv1"] = plu.SpecialInfo1,
            ["madv2"] = plu.SpecialInfo2,
            ["madv3"] = plu.SpecialInfo3,
        };
        if (plu.ReservedAdv is { } reserved)
        {
            string[] names = { "madv4", "madv6", "madv7", "madv8", "madv9", "madv10", "madv11", "madv12", "madv13", "madv14", "madv15", "madv16", "madv17", "madv18" };
            for (var i = 0; i < names.Length && i < reserved.Count; i++)
                values[names[i]] = Math.Clamp(reserved[i], 0, 99);
        }

        var sb = new StringBuilder(160);
        sb.Append("!0V");
        sb.Append(Digits(plu.PluNumber, 4)).Append('A');
        foreach (var (column, length) in FixedFields)
            sb.Append(Digits(values.TryGetValue(column, out var v) ? v : 0, length));
        sb.Append('B');
        if (codec == DahuaTmNameCodec.Rongta)
        {
            sb.Append(RongtaNameCodec.Encode(plu.Name)).Append('C');
            sb.Append(RongtaNameCodec.Encode(plu.NoteA)).Append('D');
            sb.Append(RongtaNameCodec.Encode(plu.NoteB)).Append('E');
        }
        else
        {
            sb.Append(EncodeName(plu.Name, nameTerminator)).Append('C');
            sb.Append(EncodeName(plu.NoteA, nameTerminator)).Append('D');
            sb.Append(EncodeName(plu.NoteB, nameTerminator)).Append('E');
        }
        sb.Append(LineEnd);
        return sb.ToString();
    }

    /// <summary>Клавиш быстрого вызова на одной странице «!0L».</summary>
    public const int HotkeysPerPage = 35;

    /// <summary>2026-09-30: клавиши быстрого вызова — «!0L» + страница (2 цифры: 00 — клавиши 1–35,
    /// 01 — 36–70 …) + «A» + 35 номеров PLU по 4 цифры + «\r\n». Формат — статья CSDN «大华条码秤开发之-
    /// 快捷键传输»; на весах Rongta владельца строка с клавишей 1 = PLU 66 принята («0l00a\r\n\x03»).
    /// По умолчанию на весах клавиша N вызывает PLU N — касса шлёт так же для клавиш без товара.</summary>
    public static string BuildHotkeyPage(int page, IReadOnlyDictionary<int, int> pluByKey)
    {
        var sb = new StringBuilder(8 + HotkeysPerPage * 4);
        sb.Append("!0L").Append(Digits(page, 2)).Append('A');
        for (var i = 1; i <= HotkeysPerPage; i++)
        {
            var key = page * HotkeysPerPage + i;
            sb.Append(Digits(pluByKey.TryGetValue(key, out var plu) ? plu : key, 4));
        }
        sb.Append(LineEnd);
        return sb.ToString();
    }

    /// <summary>Чтение PLU по номеру — «!0U%04dA\r\n». 2026-09-28: было «!0J» (по разбору DLL), но
    /// живая запись обмена «Русского масштаба» с TM-30F владельца (pktmon, кнопка «Обратный вызов»)
    /// показала «!0U0001A» → ответ «0u0001A…B…CDE\r\n\x03». Только чтение: весы ничего не меняют.</summary>
    public static string BuildReadPluCommand(int pluNumber) => "!0U" + Digits(pluNumber, 4) + "A" + LineEnd;

    /// <summary>Число → ровно <paramref name="length"/> цифр: слева нули, лишние старшие цифры
    /// отбрасываются (как Right() у mscale; Validate такие значения до сюда не пускает).</summary>
    public static string Digits(long value, int length)
    {
        var text = Math.Max(0, value).ToString(CultureInfo.InvariantCulture);
        return text.Length >= length ? text[^length..] : text.PadLeft(length, '0');
    }

    /// <summary>Кыргызские/казахские буквы, которых нет в CP1251 (весы печатают вместо них «?»),
    /// заменяем ближайшими русскими. «І/і» в CP1251 есть (178/179) — остаются.</summary>
    public static string ReplaceUnsupportedLetters(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(ch switch
            {
                'Ң' => 'Н', 'ң' => 'н',
                'Ө' => 'О', 'ө' => 'о',
                'Ү' => 'У', 'ү' => 'у',
                'Ә' => 'А', 'ә' => 'а',
                'Ғ' => 'Г', 'ғ' => 'г',
                'Қ' => 'К', 'қ' => 'к',
                'Ұ' => 'У', 'ұ' => 'у',
                'Һ' => 'Х', 'һ' => 'х',
                '\r' or '\n' or '\t' => ' ',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>Имя для весов: замена букв, обрезка до <see cref="MaxNameChars"/> знаков, CP1251.</summary>
    public static byte[] NameBytes(string? name)
    {
        var text = ReplaceUnsupportedLetters((name ?? "").Trim());
        var bytes = Cp1251.GetBytes(text);
        return bytes.Length > MaxNameChars ? bytes[..MaxNameChars] : bytes;
    }

    /// <summary>Каждый байт CP1251 — три десятичные цифры (item_transform = 1, mscale 0x405180);
    /// «test1» → «116101115116049». В конце «000», если <paramref name="terminator"/>.</summary>
    public static string EncodeName(string? name, bool terminator = true)
    {
        var bytes = NameBytes(name);
        var sb = new StringBuilder(bytes.Length * 3 + 3);
        foreach (var b in bytes)
            sb.Append(b.ToString("000", CultureInfo.InvariantCulture));
        if (terminator)
            sb.Append("000");
        return sb.ToString();
    }

    /// <summary>Обратное преобразование (для стенда и журнала): «116101115116049» → «test1».</summary>
    public static string DecodeName(string digits)
    {
        var bytes = new List<byte>();
        for (var i = 0; i + 3 <= digits.Length; i += 3)
        {
            if (!int.TryParse(digits.AsSpan(i, 3), NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code is < 0 or > 255)
                break;
            if (code == 0)
                break;
            bytes.Add((byte)code);
        }
        return Cp1251.GetString(bytes.ToArray());
    }

    // ------------------------------------------------------------------ ответы весов

    public enum ReplyKind
    {
        /// <summary>Ещё не полный ответ (нет «\r\n\x03» / «\r\r\n»).</summary>
        Incomplete,
        /// <summary>Полный, но короче 5 знаков — их DLL такое пропускает и ждёт дальше.</summary>
        TooShort,
        /// <summary>«Принято» — любой полный ответ, кроме конца данных.</summary>
        Ack,
        /// <summary>Второй символ 'e'/'j' и «ac» с индекса 6 — «конец данных».</summary>
        EndOfData,
    }

    /// <summary>Ищет в принятом конец ответа. Возвращает длину ответа вместе с маркером или -1.</summary>
    public static int FindReplyEnd(ReadOnlySpan<byte> received)
    {
        for (var i = 0; i + 2 < received.Length; i++)
        {
            if (received[i] == 0x0D && received[i + 1] == 0x0A && received[i + 2] == 0x03)
                return i + 3;
            if (received[i] == 0x0D && received[i + 1] == 0x0D && received[i + 2] == 0x0A)
                return i + 3;
        }
        return -1;
    }

    /// <summary>Разбор полного ответа так же, как datatransters.dll (0x1000c630).</summary>
    public static ReplyKind ClassifyReply(string reply)
    {
        if (reply.IndexOf("\r\n\u0003", StringComparison.Ordinal) < 0 && reply.IndexOf("\r\r\n", StringComparison.Ordinal) < 0)
            return ReplyKind.Incomplete;
        var trimmed = reply.TrimEnd(Enumerable.Range(0, 0x20).Select(c => (char)c).ToArray());
        if (trimmed.Length < 5)
            return ReplyKind.TooShort;
        var second = trimmed[1];
        if ((second == 'e' || second == 'j') && trimmed.IndexOf("ac", StringComparison.Ordinal) == 6)
            return ReplyKind.EndOfData;
        return ReplyKind.Ack;
    }

    /// <summary>Для журнала: управляющие байты видно как \r \n \x03.</summary>
    public static string Visible(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            if (ch == '\r') sb.Append("\\r");
            else if (ch == '\n') sb.Append("\\n");
            else if (ch < 0x20 || ch == 0x7F) sb.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ файл для их программы

    /// <summary>Строка файла импорта формата «DIGI_TOP2000» программы «Русский масштаб»
    /// (msimplu.mdb, таблица DIGI_TOP2000: 0 полей фиксированной длины, минимум 6 полей,
    /// разделитель «,»): PLU, имя, цена (множитель 1 — в сомах), пропуск, код товара, срок годности.
    /// Последнее поле тоже закрывается запятой (mifSpaceMark у поля 6 = «,»).
    /// Запятые внутри имени заменяются пробелом — кавычек их разбор не знает.</summary>
    public static string BuildDigiTop2000Line(DahuaTmPlu plu)
    {
        var name = ReplaceUnsupportedLetters((plu.Name ?? "").Trim()).Replace(',', ' ').Replace(';', ' ');
        if (name.Length > 120)
            name = name[..120];
        return string.Join(",",
                   plu.PluNumber.ToString(CultureInfo.InvariantCulture),
                   name,
                   plu.Price.ToString("0.##", CultureInfo.InvariantCulture),
                   "",
                   Digits(plu.ProductCode, 7),
                   plu.ShelfLifeDays.ToString(CultureInfo.InvariantCulture))
               + ",";
    }

    /// <summary>Весь файл DIGI_TOP2000 в кодировке CP1251 (программа не Unicode, ANSI).</summary>
    public static byte[] BuildDigiTop2000File(IEnumerable<DahuaTmPlu> plus)
    {
        var sb = new StringBuilder();
        foreach (var plu in plus)
            sb.Append(BuildDigiTop2000Line(plu)).Append("\r\n");
        return Cp1251.GetBytes(sb.ToString());
    }
}
