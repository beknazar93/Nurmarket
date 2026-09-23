using System;
using System.Collections.Generic;
using System.Globalization;

namespace NurMarketKassa.Services.Hardware;

/// <summary>Разобранный ответ весов: код команды (эхо), код ошибки и полезные данные.
/// При ненулевом коде ошибки <see cref="Payload"/> пуст — так устроен протокол
/// («передается только код команды и код ошибки — 2 байта»).</summary>
public readonly record struct ShtrikhResponse(byte Command, byte ErrorCode, byte[] Payload)
{
    public bool Ok => ErrorCode == 0;

    /// <summary>Команда 11h возвращает данные не только при нулевом коде: примечание 1 к
    /// ней разрешает коды 165 («сбой часов») и 168 («ошибка структуры базы») — весы при этом
    /// отвечают полным пакетом состояния, и его надо читать, а не отбрасывать.</summary>
    public bool HasStatusPayload => ErrorCode is 0 or 165 or 168;

    public string ErrorText => ShtrikhPrintProtocol.DescribeError(ErrorCode);
}

/// <summary>Запись товара (ПЛУ) в расширенном формате — то, что уходит командами 55h/57h.
/// Названия полей соответствуют спецификации; значения по умолчанию подобраны так, чтобы
/// заполнять надо было только реально нужное кассе: номер ПЛУ, код, название, цену и тип.</summary>
public sealed class ShtrikhPluRecord
{
    /// <summary>Номер ПЛУ, 1..[размер таблицы товаров] (его отдаёт команда D0h).</summary>
    public int PluNumber { get; init; }

    /// <summary>Код товара, 1..999999. Именно по нему весы ищут товар, если параметр
    /// «Доступ к ПЛУ» = 1 (команда 2Fh); по умолчанию поиск идёт по номеру ПЛУ.</summary>
    public int ProductCode { get; init; }

    /// <summary>Наименование товара, до 28 байт в кодировке устройства. Сколько строк из
    /// него реально печатается, задаёт параметр «Название товара, стр.» (D3h/D4h).</summary>
    public string Name1 { get; init; } = "";

    /// <summary>Вторая строка наименования, до 28 байт.</summary>
    public string Name2 { get; init; } = "";

    /// <summary>Цена в МДЕ, 0..999999. Пересчёт из сомов — <see cref="ShtrikhPrintProtocol.PriceToMde"/>.</summary>
    public int PriceMde { get; init; }

    /// <summary>Срок годности в днях, 0..9999. 0 — не задан.</summary>
    public int ShelfLifeDays { get; init; }

    /// <summary>Масса тары в граммах.</summary>
    public int TareGrams { get; init; }

    /// <summary>Групповой код (0..9999) либо упакованная дата изготовления — что именно,
    /// зависит от параметра весов «Назначение гр. кода» (8Ch/8Dh). Для упаковки даты есть
    /// <see cref="ShtrikhPrintProtocol.PackManufactureDate"/>.</summary>
    public int GroupCodeOrPackedDate { get; init; }

    /// <summary>Номер сообщения (состав, условия хранения), 0 — без сообщения.
    /// Сами сообщения пишутся командой 52h.</summary>
    public int MessageNumber { get; init; }

    /// <summary>false — весовой товар (обычный случай для кассы), true — штучный.</summary>
    public bool IsPiece { get; init; }

    /// <summary>Код РОСТЕСТ, 4 ASCII-символа; печатается подписью под изображением 1.</summary>
    public string RostestCode { get; init; } = "";

    /// <summary>Фиксированная дата реализации. null — не задана (00.00.00), тогда весы
    /// считают её от даты упаковки по сроку годности.</summary>
    public DateTime? SaleDate { get; init; }

    /// <summary>Приоритетный формат этикетки, 0 — не задан (берётся общая настройка весов).</summary>
    public int PreferredLabelFormat { get; init; }

    /// <summary>Приоритетная структура ШК, 0 — не задана (берётся общая настройка весов).</summary>
    public int PreferredBarcodeStructure { get; init; }

    /// <summary>Приоритетный тип префикса ШК: 0 — нет, 1 — номер весов, 2 — групповой код,
    /// 3 — весовой/штучный префикс, 4 — префикс GS1.</summary>
    public int PreferredBarcodePrefixType { get; init; }

    /// <summary>Битовая маска изображений 1..4 (бит 0 — изображение 1 и так далее).</summary>
    public int ImageMask { get; init; }
}

/// <summary>Ответ команды FCh «Получить тип устройства» — по ней проверяют, что по адресу
/// действительно весы ШТРИХ-ПРИНТ, а не другое устройство. Пароля не требует.</summary>
public sealed record ShtrikhDeviceInfo(
    byte DeviceType,
    byte DeviceSubType,
    byte ProtocolVersion,
    byte ProtocolSubVersion,
    byte Model,
    byte Language,
    string Name)
{
    /// <summary>Тип 1 — «Весы» (0 — ККМ), подтип 1 — «Комплексы этикетирования».</summary>
    public bool IsScale => DeviceType == 1;

    /// <summary>Модель по таблице из описания FCh: 0 — ШТРИХ-ПРИНТ, 1 — Штрих-ПАК110.</summary>
    public string ModelName => Model switch
    {
        0 => "ШТРИХ-ПРИНТ",
        1 => "Штрих-ПАК110",
        _ => $"неизвестная модель ({Model})",
    };

    public string ProtocolText =>
        $"{ProtocolVersion.ToString(CultureInfo.InvariantCulture)}.{ProtocolSubVersion.ToString(CultureInfo.InvariantCulture)}";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name)
            ? $"{ModelName}, протокол {ProtocolText}"
            : $"{Name} ({ModelName}, протокол {ProtocolText})";
}

/// <summary>Состояние весов — ответ команды 11h (74 байта). Разобраны поля, нужные кассе;
/// остальные (курс валюты, сумматор, коллизии Ethernet) намеренно опущены, чтобы не
/// тащить в драйвер то, чем он не пользуется.</summary>
public sealed class ShtrikhScaleStatus
{
    public string SoftwareVersion { get; init; } = "";
    public int HardwareVariant { get; init; }

    /// <summary>Размер таблицы товаров — максимальный номер ПЛУ. Это же значение отдаёт D0h.</summary>
    public int ProductTableSize { get; init; }

    public int MessageTableSize { get; init; }
    public int MessageLineCount { get; init; }

    /// <summary>Наибольший предел взвешивания, кг.</summary>
    public int MaxWeightKg { get; init; }

    public int ScaleNumber { get; init; }

    /// <summary>Режим весов (битовая маска). 0 — весы свободны. Ненулевое значение означает,
    /// что оператор внутри меню/режима ввода, и часть команд вернёт ошибку 123.</summary>
    public int Mode { get; init; }

    public int SubMode { get; init; }

    /// <summary>Положение десятичной точки: 0 или 2 знака. От него зависит, в чём считать
    /// цену — в сомах или в тыйынах (см. <see cref="ShtrikhPrintProtocol.PriceToMde"/>).</summary>
    public int DecimalPointDigits { get; init; }

    public int PrintMode { get; init; }
    public byte PrinterState { get; init; }
    public byte WeightDeviceState { get; init; }
    public int WeightGrams { get; init; }
    public int TareGrams { get; init; }
    public long PriceMde { get; init; }
    public long TotalMde { get; init; }
    public int SelectedPlu { get; init; }
    public bool SelectedIsPiece { get; init; }

    // Разбор байта «Состояние печатающего устройства».
    public bool HasPaper => (PrinterState & 0x01) != 0;
    public bool LabelPrinted => (PrinterState & 0x02) != 0;
    public bool LabelPositioned => (PrinterState & 0x04) != 0;
    public bool PrintHeadOpen => (PrinterState & 0x08) != 0;

    // Разбор байта «Состояние весового устройства».
    public bool WeightFixed => (WeightDeviceState & 0x01) != 0;
    public bool TareSet => (WeightDeviceState & 0x08) != 0;
    public bool WeightSettled => (WeightDeviceState & 0x10) != 0;
    public bool Overloaded => (WeightDeviceState & 0x40) != 0;
    public bool WeightMeasurementError => (WeightDeviceState & 0x80) != 0;

    /// <summary>Весы свободны и готовы принимать команды записи ПЛУ. Если оператор сейчас
    /// в системном меню или вводит пароль, массовая выгрузка посыплется ошибкой 123 —
    /// лучше сказать об этом заранее, чем на двухсотой записи.</summary>
    public bool IsIdle => Mode == 0 && SubMode == 0;

    /// <summary>Человекочитаемая причина занятости — по описанию битов поля «Режим весов»
    /// и «Подрежим весов» в команде 11h.</summary>
    public string DescribeBusyReason()
    {
        if (IsIdle)
            return "весы свободны";

        var reasons = new List<string>();
        if ((SubMode & 0x01) != 0) reasons.Add("идёт очистка базы товаров");
        if ((SubMode & 0x02) != 0) reasons.Add("идёт очистка итогов учёта");
        if ((SubMode & 0x04) != 0) reasons.Add("показывается срочное сообщение");
        if ((Mode & 0x0800) != 0) reasons.Add("идёт ввод пароля");
        if ((Mode & 0x1000) != 0) reasons.Add("открыто системное меню");
        if ((Mode & 0x4000) != 0) reasons.Add("включён режим быстрой загрузки");
        if ((Mode & 0x0002) != 0) reasons.Add("идёт добавление в сумматор");
        if ((Mode & 0x0020) != 0) reasons.Add("редактируется дата или время");
        if ((Mode & 0x2000) != 0) reasons.Add("идёт запись цены ПЛУ");
        if ((Mode & 0x8000) != 0) reasons.Add("идёт ввод номера ПЛУ или кода товара");

        return reasons.Count > 0
            ? string.Join(", ", reasons)
            : $"весы заняты (режим 0x{Mode:X4}, подрежим 0x{SubMode:X2})";
    }

    /// <summary>Разбирает 72 байта полезных данных ответа 11h. Смещения — по порядку полей
    /// в спецификации; суммарная длина сходится с заявленными 74 байтами кадра
    /// (1 код команды + 1 код ошибки + 72).</summary>
    public static ShtrikhScaleStatus? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 72)
            return null;

        // «Версия ПО весов (2 байта), формат: два символа ASCII, между которыми ставится точка».
        var version = $"{(char)payload[0]}.{(char)payload[1]}";

        return new ShtrikhScaleStatus
        {
            SoftwareVersion = version,
            HardwareVariant = (int)ShtrikhPrintProtocol.ReadLittleEndian(payload, 2, 2),
            ProductTableSize = (int)ShtrikhPrintProtocol.ReadLittleEndian(payload, 7, 2),
            MessageTableSize = (int)ShtrikhPrintProtocol.ReadLittleEndian(payload, 9, 2),
            MessageLineCount = payload[11],
            MaxWeightKg = payload[12],
            ScaleNumber = payload[14],
            Mode = (int)ShtrikhPrintProtocol.ReadLittleEndian(payload, 17, 2),
            SubMode = payload[19],
            DecimalPointDigits = payload[30],
            PrintMode = payload[33],
            PrinterState = payload[36],
            WeightDeviceState = payload[37],
            WeightGrams = ShtrikhPrintProtocol.ReadInt16(payload, 38),
            TareGrams = ShtrikhPrintProtocol.ReadInt16(payload, 40),
            PriceMde = ShtrikhPrintProtocol.ReadLittleEndian(payload, 42, 4),
            TotalMde = ShtrikhPrintProtocol.ReadLittleEndian(payload, 46, 4),
            SelectedPlu = (int)ShtrikhPrintProtocol.ReadLittleEndian(payload, 50, 2),
            SelectedIsPiece = payload[52] == 1,
        };
    }

    /// <summary>Название конструктивного исполнения по Приложению 8.</summary>
    public string HardwareVariantName => HardwareVariant switch
    {
        0 => "ШТРИХ-ПРИНТ",
        1 => "ШТРИХ-ПРИНТ С",
        2 => "ШТРИХ-ПРИНТ М",
        3 => "ШТРИХ-ПРИНТ Ф",
        4 => "ШТРИХ-ПРИНТ ПВ",
        _ => $"исполнение {HardwareVariant}",
    };
}

/// <summary>Итог выгрузки товаров на весы.</summary>
public sealed record ShtrikhUploadResult(int Sent, int Failed, IReadOnlyList<string> Errors)
{
    public bool Ok => Failed == 0 && Errors.Count == 0;
}

/// <summary>Ход выгрузки — для прогресса в окне «Весы».</summary>
public readonly record struct ShtrikhUploadProgress(int Done, int Total, string Stage);
