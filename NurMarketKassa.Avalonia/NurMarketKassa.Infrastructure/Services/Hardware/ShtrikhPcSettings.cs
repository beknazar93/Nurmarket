using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;

namespace NurMarketKassa.Services.Hardware;

/// <summary>2026-11-06: весы «ШТРИХ РС-200С2» (ЗАО «Штрих-М», сенсорные весы на встроенном Windows-ПК, программа pcscale.exe). Обмен — ТОЛЬКО файлами в общей
/// папке весов («Конфигурация → База данных»: папка импорта-экспорта, файл импорта, флаг-файл), сетевого протокола нет. Здесь настройки обмена и
/// раскладки штрихкода одной записью; хранятся строкой JSON в <see cref="UserPreferences.ShtrikhPcConfig"/> (по умолчанию — значения из руководства).</summary>
public sealed class ShtrikhPcSettings
{
    public const string DefaultImportFile = @"Import\PCScale.txt";
    public const string DefaultFlagFile = @"Import\PCScale.flz";
    public const string DefaultExportFile = @"Export\Export.txt";

    /// <summary>Формат начала строки данных: «&lt;D 1;…» (как в примере руководства, п. 8.1.3), «&lt;D1;…» (как строка конфигурации «&lt;N»+номер+«;»)
    /// и «&lt;D;1;…». Точно из руководства не следует — переключается, если весы не принимают строки (нужна проверка на весах).</summary>
    public const string LineSpace = "space";
    public const string LineGlued = "glued";
    public const string LineSemicolon = "semicolon";

    public const string FormatCodeWeight = "code-weight";
    public const string FormatCodeAmount = "code-amount";
    public const string FormatAmountCode = "amount-code";
    public const string FormatWeightCode = "weight-code";

    /// <summary>2026-11-06: пример заполнения по весам магазина владельца (Умар, проверено на месте 06.11): IP 192.168.0.200 постоянный, папка Shared под пользователем Windows «scale»,
    /// на весах префикс 27, «Код-Вес», 5 цифр кода. Только подсказка «как заполнить», не навязанные значения.</summary>
    public const string ExampleFolder = @"\\192.168.0.200\Shared";
    public const string ExampleLogin = @"web-scale1\kassa";

    /// <summary>Источник файла импорта: касса собирает сама (сейчас) либо скачивает готовый PCScale.txt с сервера NurCRM. Точка расширения: серверный формат
    /// (GET /api/main/products/scale-export/?format=shtrih_pc200) Умар добавит позже — скачивание не реализовано, пока нет его документа.</summary>
    public const string SourceKassa = "kassa";
    public const string SourceServer = "server";

    /// <summary>Общая папка обмена весов: \\192.168.0.x\Shared (сетевая), локальная или флешка. Пусто — не задана.</summary>
    public string Folder { get; set; } = "";

    /// <summary>Логин Windows весов для сетевой папки (например «scale» или «web-scale1\scale»). Пусто — под текущим пользователем кассы.</summary>
    public string Login { get; set; } = "";

    /// <summary>Пароль Windows весов — НЕ в JSON настроек: живёт в <see cref="UserPreferences.ShtrikhPcPassword"/> (в файле — только зашифрованный).</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    public string FileSource { get; set; } = SourceKassa;

    /// <summary>Отправлять картинки товаров (на главном экране весов видны только товары с картинкой). Выключено — товары уйдут без картинок.</summary>
    public bool SendPictures { get; set; } = true;

    /// <summary>Код ресурса «картинка» = смещение + код товара (по умолчанию 100000 + код): не пересекается с кодами прежней системы весов (6000+код) и кодами товаров.</summary>
    public int PictureOffset { get; set; } = 100_000;

    /// <summary>Картинка-заглушка для товара без фото: плитка с названием (и кодом), рисуется кассой. Выключено — товар без фото остаётся без картинки.</summary>
    public bool PlaceholderPictures { get; set; } = true;

    /// <summary>Показывать цену на плитке-заглушке (внизу, рядом с кодом).</summary>
    public bool PlaceholderShowPrice { get; set; }

    /// <summary>Обновлять ГЛАВНЫЙ ЭКРАН весов: пересобирать сценарий самообслуживания (<c>SelfScenario\&lt;файл&gt;.ssc</c>) из выгруженных товаров. Главный экран РС-200 строится из сценария,
    /// а не из всей базы: без этого после выгрузки с другими кодами экран пуст. Включено по умолчанию.</summary>
    public bool UpdateScenario { get; set; } = true;

    /// <summary>Файл сценария (на весах выбран параметром 952, у владельца — Semya.ssc). Другое имя — тогда его надо выбрать на весах: Конфигурация → Самообслуживание → «Файл сценария».</summary>
    public string ScenarioFile { get; set; } = ShtrikhPcScenarioWriter.DefaultFileName;

    /// <summary>Папка сценариев внутри папки обмена (параметр 951, по умолчанию SelfScenario).</summary>
    public string ScenarioFolder { get; set; } = ShtrikhPcScenarioWriter.DefaultFolder;

    /// <summary>На главный экран — только товары, у которых есть картинка (фото или плитка-заглушка). Выключено — все выгруженные.</summary>
    public bool ScenarioOnlyWithPicture { get; set; }

    /// <summary>Предел товаров в сценарии (по 29–30 на экран): защита от сотен экранов. По умолчанию 300 (около 11 экранов).</summary>
    public int ScenarioMaxProducts { get; set; } = 300;

    /// <summary>Назначить товары на кнопки быстрого доступа весов (параметры конфигурации 2001…2360, строки «&lt;N» в файле импорта). Главный экран весов — это кнопки быстрого
    /// доступа (по 18 на экран): товар, не назначенный ни на одну кнопку, находится только поиском. Касса пишет эти строки всегда, но весы применяют их, только если в
    /// «Конфигурация → База данных → Импорт» стоит галочка «Конфигурация» (параметр 814 = 1).</summary>
    public bool QuickKeys { get; set; }

    /// <summary>Сколько кнопок быстрого доступа заполнять (по умолчанию 54 = 3 виртуальных экрана по 18; на весах «Число экранов» — параметр 402, по умолчанию 3). Лишние кнопки очищаются (0).</summary>
    public int QuickKeyCount { get; set; } = 54;

    /// <summary>Номер параметра конфигурации кнопки быстрого доступа №1 (кнопка N = 2000 + N, до 2360).</summary>
    public const int QuickKeyFirstParam = 2001;

    /// <summary>2026-11-06 (Паспорт версий 2.1.3.7, параметры 953/954): после загрузки товаров весы перечитывают сценарий главного экрана, если рядом лежит флаг-файл с именем из параметра 954
    /// (у владельца 953=1, 954=flag). Касса создаёт пустой флаг после записи файла импорта, картинок и сценария и ПЕРЕД основным флагом импорта (весы удаляют использованный).</summary>
    public bool ScenarioReloadFlagEnabled { get; set; } = true;

    /// <summary>Имя флаг-файла перезагрузки сценария (параметр весов 954; по умолчанию flag).</summary>
    public string ScenarioReloadFlag { get; set; } = DefaultReloadFlag;

    public const string DefaultReloadFlag = "flag";

    /// <summary>2026-11-06: адрес весов для «Проверить протокол обмена» (только чтение). Пусто — взять из папки обмена, если она вида \\IP\Shared.</summary>
    public string ProtocolHost { get; set; } = "";

    /// <summary>Порт сетевого протокола весов (Паспорт версий: PCScale.exe слушает 22222).</summary>
    public int ProtocolPort { get; set; } = DefaultProtocolPort;

    public const int DefaultProtocolPort = 22222;

    public string ImportFile { get; set; } = DefaultImportFile;
    public string FlagFile { get; set; } = DefaultFlagFile;
    public string ExportFile { get; set; } = DefaultExportFile;

    /// <summary>«Номер весов» (общие параметры) — касса его не пишет в файл импорта, только показывает; нужен для подписи.</summary>
    public int ScaleNumber { get; set; } = 1;

    /// <summary>«Число десятичных знаков цены и стоимости» 0–3 (по умолчанию 2) — как на весах.</summary>
    public int PriceDecimals { get; set; } = 2;

    /// <summary>Перед загрузкой очистить базу товаров весов (команда «$$$CLR» в конце файла). По умолчанию выключено.</summary>
    public bool ClearBase { get; set; }

    public string LineStyle { get; set; } = LineSpace;

    /// <summary>Десятичный разделитель цены в файле: «,» (русская Windows весов) или «.». Из руководства не следует — переключается.</summary>
    public string DecimalSeparator { get; set; } = "."; // 2026-11-06: как в файле Умара (650.00)

    // ---- штрихкод (раскладка «Штрих-код» на весах, п. 7.6) ----

    /// <summary>Касса читает этикетки по раскладке РС-200 (иначе — как у остальных весов, по настройке компании).</summary>
    public bool BarcodeEnabled { get; set; }

    /// <summary>«Префикс весового товара» (по умолчанию на весах 20; рекомендуется 20..29). Штучный 21 и корзина 22 кассой не читаются.</summary>
    public int BarcodePrefix { get; set; } = 27; // 2026-11-06: на весах владельца (параметр 303); на заводе 20

    /// <summary>«Число знаков кода товара» (по умолчанию 4; на код и значение остаётся 10 знаков). 1…6.</summary>
    public int CodeLength { get; set; } = 5; // 2026-11-06: на весах владельца (параметр 307); на заводе 4

    /// <summary>«Формат весового товара»: Код-Вес / Код-Стоимость / Вес-Код / Стоимость-Код.</summary>
    public string BarcodeFormat { get; set; } = FormatCodeWeight;

    /// <summary>«КС кода товара» — последняя цифра поля кода = младший разряд суммы остальных цифр.</summary>
    public bool CodeChecksum { get; set; }

    /// <summary>2026-11-06 (фото этикетки «Алма» с весов владельца: 2000002007432 = префикс 20, код 00002, стоимость 7,43): на одних весах могут печататься этикетки двух видов —
    /// весовые (префикс 27, «Код-Вес») и «штучные/стоимостные» (параметры весов 304 и 306: префикс 20, «Код-Стоимость»). Касса читает обе раскладки сразу.
    /// Число знаков кода и КС у них общие (параметры 307 и 308).</summary>
    public bool PieceBarcodeEnabled { get; set; } = true;

    /// <summary>«Префикс штучного товара» (параметр 304; по умолчанию на весах 21, у владельца 20). Не должен совпадать с префиксом весового.</summary>
    public int PiecePrefix { get; set; } = 20;

    /// <summary>«Формат штучного товара» (параметр 306): здесь только форматы со стоимостью — «Код-Стоимость» (по умолчанию) или «Стоимость-Код».</summary>
    public string PieceFormat { get; set; } = FormatCodeAmount;

    public static readonly string[] PieceFormats = { FormatCodeAmount, FormatAmountCode };

    [JsonIgnore]
    public bool CodeFirst => BarcodeFormat is FormatCodeWeight or FormatCodeAmount;
    [JsonIgnore]
    public bool ByAmount => BarcodeFormat is FormatCodeAmount or FormatAmountCode;

    public static readonly string[] Formats = { FormatCodeWeight, FormatCodeAmount, FormatWeightCode, FormatAmountCode };

    /// <summary>Подпись формата, как на весах.</summary>
    public static string FormatTitle(string format) => format switch
    {
        FormatCodeAmount => Tr.T("Код-Стоимость", "Код-Баа", "Code-Cost", "Kod-Tutar", "Kod-Narx"),
        FormatWeightCode => Tr.T("Вес-Код", "Салмак-Код", "Weight-Code", "Ağırlık-Kod", "Vazn-Kod"),
        FormatAmountCode => Tr.T("Стоимость-Код", "Баа-Код", "Cost-Code", "Tutar-Kod", "Narx-Kod"),
        _ => Tr.T("Код-Вес", "Код-Салмак", "Code-Weight", "Kod-Ağırlık", "Kod-Vazn"),
    };

    /// <summary>Приводит значения к допустимым (после чтения из файла настроек или правки на экране).</summary>
    public ShtrikhPcSettings Normalize()
    {
        Folder = (Folder ?? "").Trim();
        Login = (Login ?? "").Trim();
        Password ??= "";
        if (FileSource != SourceServer)
            FileSource = SourceKassa;
        ImportFile = string.IsNullOrWhiteSpace(ImportFile) ? DefaultImportFile : ImportFile.Trim();
        FlagFile = string.IsNullOrWhiteSpace(FlagFile) ? DefaultFlagFile : FlagFile.Trim();
        ExportFile = string.IsNullOrWhiteSpace(ExportFile) ? DefaultExportFile : ExportFile.Trim();
        ScaleNumber = Math.Clamp(ScaleNumber, 1, 999);
        PictureOffset = Math.Clamp(PictureOffset, 0, 900_000);
        QuickKeyCount = Math.Clamp(QuickKeyCount, 1, 360);
        ScenarioMaxProducts = Math.Clamp(ScenarioMaxProducts, 1, 3000);
        ScenarioReloadFlag = ShtrikhPcScenarioWriter.SafeFlagName(ScenarioReloadFlag);
        ProtocolHost = (ProtocolHost ?? "").Trim();
        ProtocolPort = ProtocolPort is > 0 and <= 65535 ? ProtocolPort : DefaultProtocolPort;
        ScenarioFile = ShtrikhPcScenarioWriter.SafeFileName(ScenarioFile);
        ScenarioFolder = string.IsNullOrWhiteSpace(ScenarioFolder) || ScenarioFolder.IndexOfAny(new[] { '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 ? ShtrikhPcScenarioWriter.DefaultFolder : ScenarioFolder.Trim().Trim('\\');
        PriceDecimals = Math.Clamp(PriceDecimals, 0, 3);
        if (LineStyle is not (LineSpace or LineGlued or LineSemicolon))
            LineStyle = LineSpace;
        DecimalSeparator = DecimalSeparator == "," ? "," : ".";
        BarcodePrefix = Math.Clamp(BarcodePrefix, 20, 29);
        CodeLength = Math.Clamp(CodeLength, 1, 6);
        if (CodeChecksum && CodeLength < 2)
            CodeChecksum = false;
        if (!Formats.Contains(BarcodeFormat))
            BarcodeFormat = FormatCodeWeight;
        PiecePrefix = Math.Clamp(PiecePrefix, 20, 29);
        if (!PieceFormats.Contains(PieceFormat))
            PieceFormat = FormatCodeAmount;
        return this;
    }

    /// <summary>Раскладка штрихкода для парсера кассы (<see cref="WeightBarcodeParser.CustomLayouts"/>).</summary>
    public WeightBarcodeCustomLayout ToLayout() => new(
        BarcodePrefix.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
        CodeLength,
        CodeFirst,
        ByAmount ? WeightBarcodeValueKind.Amount : WeightBarcodeValueKind.Weight,
        CodeChecksum,
        PriceDecimals);

    /// <summary>Раскладка «штучных/стоимостных» этикеток (префикс 20, «Код-Стоимость»): те же число знаков кода, КС и знаков цены, что у весовой.</summary>
    public WeightBarcodeCustomLayout ToPieceLayout() => new(
        PiecePrefix.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
        CodeLength,
        PieceFormat == FormatCodeAmount,
        WeightBarcodeValueKind.Amount,
        CodeChecksum,
        PriceDecimals);

    /// <summary>Все раскладки, которые касса читает для весов РС-200: основная и (если включена и префикс другой) штучная.</summary>
    public IReadOnlyList<WeightBarcodeCustomLayout> ToLayouts()
    {
        var list = new List<WeightBarcodeCustomLayout> { ToLayout() };
        if (PieceBarcodeEnabled && PiecePrefix != BarcodePrefix)
            list.Add(ToPieceLayout());
        return list;
    }

    /// <summary>Наибольший код товара, помещающийся в этикетку по текущей раскладке (не больше 999999 — предел поля «Код» БД весов).</summary>
    [JsonIgnore]
    public long MaxProductCode => Math.Min(999_999, ToLayout().MaxCode);

    // ------------------------------------------------------------------ хранение

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static ShtrikhPcSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ShtrikhPcSettings();
        try
        {
            return (JsonSerializer.Deserialize<ShtrikhPcSettings>(json, Json) ?? new ShtrikhPcSettings()).Normalize();
        }
        catch (JsonException)
        {
            return new ShtrikhPcSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Normalize(), Json);

    /// <summary>Текущие настройки этой кассы.</summary>
    public static ShtrikhPcSettings Current
    {
        get
        {
            var s = Parse(UserPreferences.Instance.ShtrikhPcConfig);
            s.Password = UserPreferences.Instance.ShtrikhPcPassword ?? "";
            return s;
        }
    }

    /// <summary>Записывает настройки в память (на диск — <c>UserPreferences.Instance.SaveToDisk()</c> вызывающим) и сразу применяет раскладку к парсеру.</summary>
    public static void Save(ShtrikhPcSettings settings)
    {
        UserPreferences.Instance.ShtrikhPcConfig = settings.ToJson();
        UserPreferences.Instance.ShtrikhPcPassword = string.IsNullOrEmpty(settings.Password) ? null : settings.Password;
        ApplyToParser();
    }

    /// <summary>Включает/выключает раскладку РС-200 в парсере по текущим настройкам. Вызывается при запуске (после настроек компании) и после правок.</summary>
    public static void ApplyToParser()
    {
        var settings = Current;
        WeightBarcodeParser.CustomLayouts = settings.BarcodeEnabled
            ? settings.ToLayouts().ToArray()
            : Array.Empty<WeightBarcodeCustomLayout>();
    }
}
