using System;
using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Кодек протокола обмена весов ШТРИХ-ПРИНТ v1.6 (спецификация «Протокол обмена
/// весов ШТРИХ-ПРИНТ», компания «ШТРИХ-М», 2017) — только кадрирование и упаковка полей,
/// без сети. Транспорт — <see cref="ShtrikhPrintLanScaleService"/>.
///
/// Кадр для Ethernet (раздел «Описание протокола для интерфейса Ethernet»):
///   байт 0 — STX (0x02) либо STE (0x03) для режима синхронизации;
///   байт 1 — длина N (двоичная): код команды + параметры, БЕЗ байта 0 и без самого себя;
///   байт 2 — код команды;
///   байты 3..N+1 — параметры.
/// Байта LRC в Ethernet-кадре НЕТ (в отличие от RS-232C) — это прямо оговорено.
/// Все числовые поля — little-endian («стиль остроконечников»).
///
/// Проверено по документу: сумма ширин полей каждой реализованной команды сходится с
/// указанной в ней «Длиной сообщения» (например 57h: 1+4+2+4+28+28+3+1+2+2+2+2+1+4+3 = 87).
///
/// ЧЕСТНО о том, что НЕ проверено на живых весах, и почему:
/// (1) Номер UDP-порта в спецификации не указан вообще — он берётся из системного меню
///     весов. <see cref="ShtrikhPrintLanScaleService.DefaultPort"/> — лишь частое значение
///     по умолчанию, его нужно сверить с меню конкретных весов.
/// (2) В командах 55h/57h поля «Приоритетный формат этикетки» и «Приоритетная структура ШК»
///     занимают по 4 бита одного байта, но спецификация НЕ указывает, какое из них в
///     старшем, а какое в младшем полубайте. Здесь формат этикетки — младший полубайт,
///     структура ШК — старший. Значение по умолчанию у обоих 0 («нет», берётся общая
///     настройка весов), при нём байт равен 0x00 при любом порядке — поэтому на обычную
///     выгрузку неоднозначность не влияет.
/// (3) Команды, требующие СПЕЦИАЛЬНОГО пароля (08h эмуляция клавиатуры, 30h ноль, 31h/32h
///     тара, 37h выбор товара, 41h печать этикетки), намеренно НЕ реализованы: по разделу
///     «Особенности» этот пароль выдаёт компания «ШТРИХ-М» отдельно под каждый заводской
///     номер весов, поэтому без него они всё равно вернут ошибку 122.</summary>
public static class ShtrikhPrintProtocol
{
    // Служебные символы (раздел «Служебные символы»).
    public const byte Enq = 0x05;
    public const byte Stx = 0x02;
    public const byte Ste = 0x03;
    public const byte Ack = 0x06;
    public const byte Nak = 0x15;
    public const byte Busy = 0x0B;

    // Коды команд (раздел «Поддерживаемые команды»). Реализован набор, нужный кассе:
    // опознание весов, состояние, выгрузка и чтение ПЛУ, сообщения, итоги, настройки ШК.
    public const byte CmdGetStatus = 0x11;
    public const byte CmdGetMode = 0x12;
    public const byte CmdBeep = 0x13;
    public const byte CmdClearDatabase = 0x18;
    public const byte CmdResetTotals = 0x19;
    public const byte CmdGetSerial = 0x1A;
    public const byte CmdGetPluAccess = 0x2C;
    public const byte CmdSetPluAccess = 0x2F;
    public const byte CmdGetPrinterStatus = 0x4A;
    public const byte CmdWriteMessageLine = 0x52;
    public const byte CmdClearPlu = 0x54;
    public const byte CmdWritePluBlockExtended = 0x55;
    public const byte CmdFastLoad = 0x56;
    public const byte CmdWritePluExtended = 0x57;
    public const byte CmdReadPluExtended = 0x58;
    public const byte CmdGetPluByProductCode = 0x5A;
    public const byte CmdGetEmptyPlu = 0x5B;
    public const byte CmdGetPluTotals = 0x60;
    public const byte CmdGetOverallTotals = 0x61;
    public const byte CmdGetBarcodeStructure = 0x74;
    public const byte CmdSetBarcodeStructure = 0x75;
    public const byte CmdGetBarcodePrefixes = 0x76;
    public const byte CmdSetBarcodePrefix = 0x77;
    public const byte CmdGetMaxPlu = 0xD0;
    public const byte CmdGetMaxMessages = 0xD1;
    public const byte CmdGetMessageLineCount = 0xD2;
    public const byte CmdGetDeviceType = 0xFC;

    /// <summary>Длина одной записи ПЛУ расширенного формата без пароля — общая часть команд
    /// 55h (блок) и 57h (одиночная запись).</summary>
    public const int PluRecordLength = 82;

    /// <summary>Максимум записей в одном блоке 55h («Количество ПЛУ (1 байт): 1..6»).</summary>
    public const int MaxPlusPerBlock = 6;

    /// <summary>Ширина каждого из двух полей наименования товара.</summary>
    public const int NameFieldLength = 28;

    /// <summary>Ширина строки сообщения в команде 52h.</summary>
    public const int MessageLineLength = 50;

    /// <summary>Приложение 1: кодовая таблица устройства «основана на WIN1251 с
    /// незначительными отличиями». Провайдер кодовых страниц регистрируется на старте
    /// приложения (AvaloniaHostServiceRegistration), но драйвер может быть вызван из теста
    /// или утилиты — поэтому пробуем зарегистрировать его сами и мягко падаем в Latin1,
    /// чтобы кодек не бросал исключение на ровном месте.</summary>
    private static readonly Encoding DeviceEncoding = ResolveDeviceEncoding();

    private static Encoding ResolveDeviceEncoding()
    {
        try
        {
            return Encoding.GetEncoding(1251);
        }
        catch (Exception)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1251);
            }
            catch (Exception)
            {
                return Encoding.Latin1;
            }
        }
    }

    /// <summary>Собирает Ethernet-кадр: STX/STE + длина + код команды + параметры.
    /// <paramref name="dynamicLength"/> — для команд 55h и 59h, у которых спецификация
    /// предписывает ставить в байт длины число FFh («длина этой команды динамическая и
    /// может превышать 255 байт»).</summary>
    public static byte[] BuildFrame(byte command, ReadOnlySpan<byte> parameters, bool sync = false, bool dynamicLength = false)
    {
        var declared = 1 + parameters.Length; // код команды + параметры
        if (!dynamicLength && declared > 0xFE)
            throw new ArgumentException(
                $"Команда {command:X2}h: {declared} байт не помещается в однобайтовое поле длины. " +
                "Для команд с динамической длиной (55h, 59h) нужен dynamicLength: true.",
                nameof(parameters));

        var frame = new byte[2 + declared];
        frame[0] = sync ? Ste : Stx;
        frame[1] = dynamicLength ? (byte)0xFF : (byte)declared;
        frame[2] = command;
        parameters.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>Разбирает ответный кадр. Возвращает false, если это не кадр (например,
    /// одиночный ACK/NAK/BUSY) или он пришёл обрезанным.
    /// Спецификация: «Если код ошибки не 0, передается только код команды и код ошибки —
    /// 2 байта», поэтому у ошибочного ответа пустой payload — это норма, а не обрезка.</summary>
    public static bool TryParseFrame(ReadOnlySpan<byte> datagram, out ShtrikhResponse response)
    {
        response = default;
        if (datagram.Length < 4)
            return false; // минимум: STX + длина + код команды + код ошибки
        if (datagram[0] != Stx && datagram[0] != Ste)
            return false;

        var declared = datagram[1];
        var command = datagram[2];
        var errorCode = datagram[3];

        // Реально пришедшая длина тела (код команды + код ошибки + данные).
        var available = datagram.Length - 2;
        // FFh — маркер динамической длины, как числу ему доверять нельзя.
        var bodyLength = declared == 0xFF ? available : Math.Min(declared, available);
        if (bodyLength < 2)
            return false;

        var payloadLength = bodyLength - 2; // минус код команды и код ошибки
        var payload = payloadLength > 0 ? datagram.Slice(4, payloadLength).ToArray() : Array.Empty<byte>();
        response = new ShtrikhResponse(command, errorCode, payload);
        return true;
    }

    /// <summary>Пароль администратора — ровно 4 байта ASCII-цифр «0».."9" (спецификация
    /// повторяет это в описании каждой команды). Заводское значение — "0000".</summary>
    public static byte[] EncodePassword(string? password)
    {
        var text = (password ?? "").Trim();
        if (text.Length == 0)
            text = "0000";

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var ch = i < text.Length ? text[i] : '0';
            if (ch is < '0' or > '9')
                throw new ArgumentException(
                    "Пароль весов ШТРИХ-ПРИНТ — ровно 4 цифры (0-9); другие символы протокол не принимает.",
                    nameof(password));
            bytes[i] = (byte)ch;
        }
        return bytes;
    }

    /// <summary>Текст в кодовую таблицу устройства с обрезкой/добивкой пробелами до
    /// фиксированной ширины поля. Обрезаем по БАЙТАМ, а не по символам — в CP1251 кириллица
    /// однобайтовая, но подстраховка нужна на случай непредставимых символов.</summary>
    public static byte[] EncodeFixedText(string? text, int width)
    {
        var buffer = new byte[width];
        buffer.AsSpan().Fill((byte)' ');
        if (string.IsNullOrEmpty(text))
            return buffer;

        var encoded = DeviceEncoding.GetBytes(text);
        var count = Math.Min(encoded.Length, width);
        encoded.AsSpan(0, count).CopyTo(buffer);
        return buffer;
    }

    /// <summary>Обратное преобразование для ответов весов (наименование, название устройства).</summary>
    public static string DecodeText(ReadOnlySpan<byte> bytes) =>
        DeviceEncoding.GetString(bytes).TrimEnd('\0', ' ');

    /// <summary>ASCII-поле фиксированной ширины (код РОСТЕСТ — 4 байта «символы ASCII»).</summary>
    public static byte[] EncodeAscii(string? text, int width)
    {
        var buffer = new byte[width];
        buffer.AsSpan().Fill((byte)' ');
        if (string.IsNullOrEmpty(text))
            return buffer;

        for (var i = 0; i < width && i < text.Length; i++)
        {
            var ch = text[i];
            buffer[i] = ch is >= (char)0x20 and <= (char)0x7E ? (byte)ch : (byte)' ';
        }
        return buffer;
    }

    /// <summary>Little-endian запись целого в <paramref name="width"/> байт.</summary>
    public static void WriteLittleEndian(Span<byte> destination, long value, int width)
    {
        if (value < 0)
            value = 0;
        for (var i = 0; i < width; i++)
            destination[i] = (byte)((value >> (8 * i)) & 0xFF);
    }

    /// <summary>Little-endian чтение беззнакового целого.</summary>
    public static long ReadLittleEndian(ReadOnlySpan<byte> source, int offset, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++)
            value |= (long)source[offset + i] << (8 * i);
        return value;
    }

    /// <summary>Little-endian чтение знакового 16-битного (масса и тара — «2 байта со знаком»).</summary>
    public static short ReadInt16(ReadOnlySpan<byte> source, int offset) =>
        unchecked((short)(source[offset] | (source[offset + 1] << 8)));

    /// <summary>Дата в формате «ДД ММ ГГ» (3 байта). Незаданная дата — 00.00.00
    /// (примечание 3 к команде 55h).</summary>
    public static void WriteDate(Span<byte> destination, DateTime? date)
    {
        if (date is not { } value)
        {
            destination[0] = 0;
            destination[1] = 0;
            destination[2] = 0;
            return;
        }
        destination[0] = (byte)value.Day;
        destination[1] = (byte)value.Month;
        destination[2] = (byte)(value.Year % 100);
    }

    /// <summary>Упакованная дата изготовления для поля «Групповой код / Дата изготовления»
    /// (примечание 5 к команде 55h): биты 0..6 — год (0..99), биты 7..10 — месяц (1..12),
    /// биты 11..15 — день (1..31). Применимо, только если параметр весов «Назначение гр.
    /// кода» = 1 (команды 8Ch/8Dh); иначе то же поле читается как групповой код.</summary>
    public static ushort PackManufactureDate(DateTime date) =>
        (ushort)((date.Year % 100) | (date.Month << 7) | (date.Day << 11));

    /// <summary>Пересчёт цены в МДЕ (минимальную денежную единицу) — «Все суммы в данном
    /// разделе — целые величины, указанные в МДЕ».
    /// <paramref name="decimalPointDigits"/> берётся из ответа 11h (байт «Положение десятичной
    /// точки», «отделяет 0 или 2 знака»): при 2 знаках МДЕ = тыйын (сом × 100), при 0 — сом.
    /// Если настройка весов не совпадёт с переданной, цены уедут ровно в 100 раз — поэтому
    /// <see cref="ShtrikhPrintLanScaleService"/> читает её с весов, а не угадывает.</summary>
    public static int PriceToMde(decimal priceSom, int decimalPointDigits)
    {
        var factor = decimalPointDigits >= 2 ? 100m : 1m;
        var mde = Math.Round(priceSom * factor, MidpointRounding.AwayFromZero);
        if (mde < 0)
            return 0;
        return mde > 999999m ? 999999 : (int)mde;
    }

    /// <summary>Упаковывает запись ПЛУ расширенного формата — 82 байта, общая часть команд
    /// 55h и 57h (у 57h перед ней идёт пароль, у 55h — пароль и количество записей).
    /// Ширины полей и их порядок — по описанию команды 57h.</summary>
    public static byte[] EncodePluRecord(ShtrikhPluRecord plu)
    {
        ArgumentNullException.ThrowIfNull(plu);

        var buffer = new byte[PluRecordLength];
        var offset = 0;

        WriteLittleEndian(buffer.AsSpan(offset), plu.PluNumber, 2); offset += 2;   // Номер ПЛУ
        WriteLittleEndian(buffer.AsSpan(offset), plu.ProductCode, 4); offset += 4; // Код товара
        EncodeFixedText(plu.Name1, NameFieldLength).CopyTo(buffer, offset); offset += NameFieldLength;
        EncodeFixedText(plu.Name2, NameFieldLength).CopyTo(buffer, offset); offset += NameFieldLength;
        WriteLittleEndian(buffer.AsSpan(offset), plu.PriceMde, 3); offset += 3;    // Цена, МДЕ

        // Полубайты «Приоритетный формат этикетки» и «Приоритетная структура ШК».
        // Порядок полубайтов спецификацией не задан — см. оговорку (2) в шапке класса.
        // При значениях по умолчанию (0/0) байт нулевой при любом порядке.
        buffer[offset++] = (byte)((plu.PreferredLabelFormat & 0x0F) | ((plu.PreferredBarcodeStructure & 0x0F) << 4));

        WriteLittleEndian(buffer.AsSpan(offset), plu.ShelfLifeDays, 2); offset += 2; // Срок годности, дней
        WriteLittleEndian(buffer.AsSpan(offset), plu.TareGrams, 2); offset += 2;     // Тара, г
        WriteLittleEndian(buffer.AsSpan(offset), plu.GroupCodeOrPackedDate, 2); offset += 2;
        WriteLittleEndian(buffer.AsSpan(offset), plu.MessageNumber, 2); offset += 2; // Номер сообщения

        // «Номер граф. изображения и тип товара»: бит 7 — тип товара (0 весовой, 1 штучный),
        // биты 4..6 — приоритетный тип префикса ШК, биты 0..3 — маска изображений 1..4.
        var flags = (byte)(plu.ImageMask & 0x0F);
        flags |= (byte)((plu.PreferredBarcodePrefixType & 0x07) << 4);
        if (plu.IsPiece)
            flags |= 0x80;
        buffer[offset++] = flags;

        EncodeAscii(plu.RostestCode, 4).CopyTo(buffer, offset); offset += 4; // Код РОСТЕСТ
        WriteDate(buffer.AsSpan(offset), plu.SaleDate); offset += 3;         // Дата реализации

        if (offset != PluRecordLength)
            throw new InvalidOperationException($"Запись ПЛУ получилась {offset} байт вместо {PluRecordLength}.");

        return buffer;
    }

    /// <summary>Разбирает ответ команды 58h «Получить ПЛУ расширенного формата» (80 байт
    /// полезных данных: те же поля, что в записи, но без номера ПЛУ).</summary>
    public static ShtrikhPluRecord? DecodePluRecord(int pluNumber, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < PluRecordLength - 2)
            return null;

        var offset = 0;
        var productCode = (int)ReadLittleEndian(payload, offset, 4); offset += 4;
        var name1 = DecodeText(payload.Slice(offset, NameFieldLength)); offset += NameFieldLength;
        var name2 = DecodeText(payload.Slice(offset, NameFieldLength)); offset += NameFieldLength;
        var priceMde = (int)ReadLittleEndian(payload, offset, 3); offset += 3;
        var nibbles = payload[offset++];
        var shelfLife = (int)ReadLittleEndian(payload, offset, 2); offset += 2;
        var tare = (int)ReadLittleEndian(payload, offset, 2); offset += 2;
        var groupCode = (int)ReadLittleEndian(payload, offset, 2); offset += 2;
        var messageNumber = (int)ReadLittleEndian(payload, offset, 2); offset += 2;
        var flags = payload[offset++];
        var rostest = DecodeText(payload.Slice(offset, 4)); offset += 4;

        var day = payload[offset];
        var month = payload[offset + 1];
        var year = payload[offset + 2];
        DateTime? saleDate = null;
        if (day is > 0 and <= 31 && month is > 0 and <= 12)
        {
            try
            {
                saleDate = new DateTime(2000 + year, month, day);
            }
            catch (ArgumentOutOfRangeException)
            {
                saleDate = null; // 31 февраля и прочий мусор из незаполненной записи
            }
        }

        return new ShtrikhPluRecord
        {
            PluNumber = pluNumber,
            ProductCode = productCode,
            Name1 = name1,
            Name2 = name2,
            PriceMde = priceMde,
            PreferredLabelFormat = nibbles & 0x0F,
            PreferredBarcodeStructure = (nibbles >> 4) & 0x0F,
            ShelfLifeDays = shelfLife,
            TareGrams = tare,
            GroupCodeOrPackedDate = groupCode,
            MessageNumber = messageNumber,
            ImageMask = flags & 0x0F,
            PreferredBarcodePrefixType = (flags >> 4) & 0x07,
            IsPiece = (flags & 0x80) != 0,
            RostestCode = rostest,
            SaleDate = saleDate,
        };
    }

    /// <summary>Приложение 5 «Коды ошибок». Возвращает человекочитаемое описание для показа
    /// кассиру; для неизвестного кода — его номер, чтобы можно было найти в спецификации.</summary>
    public static string DescribeError(byte code) => code switch
    {
        0 => "Ошибок нет",
        1 => "Нет бумаги",
        2 => "Этикетка не спозиционирована",
        3 => "Открыта печатающая головка",
        4 => "Не снята отпечатанная этикетка",
        5 => "Перегрев печатной головки",
        6 => "Перегрев печатной головки во время печати",
        7 => "Пустой формат этикетки",
        9 => "Печать прервана / неполная печать",
        10 => "Ошибка при чтении часов",
        11 => "Ошибка при паковке / распаковке даты",
        12 => "Ошибка при чтении сообщений",
        13 => "Ошибка при чтении накоплений",
        14 => "Ошибка при формировании штрих-кода",
        15 => "Ошибка в значении количества",
        16 => "Ошибка в значении веса",
        17 => "Ошибка в значении тары",
        18 => "Ошибка в значении цены",
        19 => "Ошибка в значении стоимости",
        20 => "Нулевая стоимость",
        100 => "Совпадение весового и штучного префиксов",
        101 => "Неверный префикс итоговой этикетки",
        102 => "Совпадение номера весов и префикса итоговой этикетки",
        103 => "Совпадение группового кода товара и префикса итоговой этикетки",
        104 => "Совпадение префикса весового товара и префикса итоговой этикетки",
        105 => "Совпадение префикса штучного товара и префикса итоговой этикетки",
        106 => "Неверный тип префикса штрих-кода",
        107 => "Неверный номер весов",
        108 => "Неверный номер группового кода товара",
        109 => "Неверное количество строк в наименовании товара",
        110 => "Неверное количество строк в наименовании магазина",
        111 => "Неверный весовой префикс",
        112 => "Неверный штучный префикс",
        113 => "Неверный номер формата этикетки",
        114 => "Неверный номер формата штрих-кода",
        115 => "Печать опционально запрещена",
        116 => "Неверный GS1-префикс",
        120 => "Неизвестная команда",
        121 => "Неверная длина данных команды",
        122 => "Неверный пароль",
        123 => "Команда не выполняется в текущем режиме весов",
        124 => "Неверное значение параметра",
        125 => "Порт не поддерживается",
        126 => "Поддерживается только чтение",
        127 => "Невозможна печать копии",
        128 => "Неверный номер ПЛУ",
        129 => "Неверный номер строки сообщения",
        130 => "Неверный код товара",
        131 => "Неверная цена товара",
        132 => "Неверный срок годности товара",
        133 => "Неверная тара товара",
        134 => "Неверный групповой код товара",
        135 => "Неверный номер сообщения",
        136 => "Неверный номер изображения",
        139 => "Таблица товаров пуста",
        140 => "Пустое ПЛУ",
        141 => "Товар выбран",
        142 => "Неверная дата реализации",
        143 => "Неверная дата изготовления",
        144 => "Неверный предпочитаемый тип префикса ШК",
        145 => "Сумматор не пуст",
        146 => "Сумматор пуст",
        147 => "Добавление в сумматор невозможно",
        148 => "Отмена последнего добавления в сумматор невозможна",
        149 => "Печать итоговой этикетки запрещена",
        150 => "Ошибка при попытке установки нуля",
        151 => "Ошибка при установке тары",
        152 => "Вес не фиксирован",
        153 => "Переполнение стоимости",
        154 => "Неверный номер предпочитаемого формата ШК",
        155 => "Таблица товаров не пуста",
        156 => "ПЛУ не найдено",
        161 => "Размер изображения превышает лимит",
        162 => "Неверный номер символа",
        163 => "Неверный размер символа",
        164 => "Неверный номер блока",
        165 => "Сбой часов",
        167 => "Не реализуется данным интерфейсом",
        168 => "Ошибка структуры базы",
        169 => "Не инициализирована или неисправна SRAM",
        170 => "Исчерпан лимит попыток обращения с неверным паролем",
        _ => $"Ошибка весов, код {code.ToString(CultureInfo.InvariantCulture)}",
    };
}
