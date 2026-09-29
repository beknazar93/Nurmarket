using System.Text.Json;
using System.Text.Json.Serialization;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>Одни весы с печатью этикеток (2026-09-28, просьба владельца: «каждые весы настраиваются
/// отдельно, со своим IP; разные категории на разные весы; товары на клавиши»).</summary>
public sealed class LabelScaleProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>shtrikh / rongta / tm / ai — как UserPreferences.ScaleBrand.</summary>
    public string Brand { get; set; } = "shtrikh";

    public string Ip { get; set; } = "";
    public int Port { get; set; }

    /// <summary>Пароль администратора (Штрих-ПРИНТ).</summary>
    public string? Password { get; set; }

    /// <summary>Штрих-ПРИНТ: true — напрямую по сети, false — через сервер NurCRM.</summary>
    public bool ShtrikhDirect { get; set; }

    /// <summary>Rongta: «site» (через сайт и RLS1000) или «server» (свой сервер кассы).</summary>
    public string RongtaSource { get; set; } = "site";
    public int RongtaServerPort { get; set; } = 5001;

    /// <summary>Категории товаров этих весов. Пусто — все весовые товары (или отмеченные вручную).</summary>
    public List<string> Categories { get; set; } = new();

    /// <summary>Товары, отмеченные вручную при последней отправке (когда категории не заданы).</summary>
    public List<string> ProductIds { get; set; } = new();

    /// <summary>Клавиша быстрого доступа на весах для товара: id товара → номер клавиши (1–120).
    /// Пишется на весы командой B1h только при прямой отправке на Штрих-ПРИНТ.</summary>
    public Dictionary<string, int> Hotkeys { get; set; } = new();

    /// <summary>2026-09-29 (клиент «Алтымыш ата»: «при каждой отправке ПЛУ меняются»): закреплённый
    /// номер ячейки ПЛУ на этих весах: id товара → номер. Выдаётся товару один раз и больше не
    /// зависит от порядка, поиска и выбора строк (правила — ScalePluPlanner).</summary>
    public Dictionary<string, int> PluNumbers { get; set; } = new();

    /// <summary>2026-09-29: «Код в ШК», который владелец вписал сам (только если он отличается от
    /// кода по умолчанию — PLU/код из карточки). Раньше правка терялась при следующем открытии окна.</summary>
    public Dictionary<string, string> BarcodeCodes { get; set; } = new();

    /// <summary>2026-09-29: что реально записано на весы при последних отправках: id товара →
    /// номер, код и название. По нему при смене номера находятся старая ячейка и клавиши весов.</summary>
    public Dictionary<string, ScaleSentPlu> SentPlus { get; set; } = new();

    /// <summary>2026-09-29: номер ПЛУ брать из карточки товара (галочка «Постоянный PLU» снята).</summary>
    public bool PluFromCatalog { get; set; }

    /// <summary>2026-09-29: откуда закреплены номера при первом запуске после обновления («журнал
    /// 28.09 18:12» / «порядок списка»). null — ещё не закреплялись (тогда окно «Весы» один раз
    /// закрепит их и покажет владельцу, какой PLU у какого товара).</summary>
    public string? PluPinnedFrom { get; set; }

    public LabelScaleProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Brand = Brand,
        Ip = Ip,
        Port = Port,
        Password = Password,
        ShtrikhDirect = ShtrikhDirect,
        RongtaSource = RongtaSource,
        RongtaServerPort = RongtaServerPort,
        Categories = new List<string>(Categories),
        ProductIds = new List<string>(ProductIds),
        Hotkeys = new Dictionary<string, int>(Hotkeys),
        // 2026-09-29: без этих полей «Отмена» в окне настроек весов (Restore) стирала бы номера PLU.
        PluNumbers = new Dictionary<string, int>(PluNumbers),
        BarcodeCodes = new Dictionary<string, string>(BarcodeCodes),
        SentPlus = SentPlus.ToDictionary(kv => kv.Key, kv => new ScaleSentPlu { Plu = kv.Value.Plu, Code = kv.Value.Code, Name = kv.Value.Name }),
        PluFromCatalog = PluFromCatalog,
        PluPinnedFrom = PluPinnedFrom,
    };
}

/// <summary>2026-09-28: список весов с печатью этикеток — файл label-scales.json рядом с
/// user-settings.json (как CustomThemeStore). Прежние одиночные настройки (ScaleBrand,
/// ScaleNetworkIp/TmScaleIp/RongtaScaleIp, порты, пароль, способ отправки) НЕ заменены: они —
/// «выбранные сейчас» весы. Выбор весов (Activate) переписывает в них адрес и способ этих весов,
/// поэтому окна марок, проверка связи и отправка работают как раньше; после правки полей
/// CaptureActive записывает их обратно в профиль. Первый профиль собирается из прежних настроек.</summary>
public static class LabelScaleStore
{
    private const string FileName = "label-scales.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class FileModel
    {
        public string? ActiveId { get; set; }
        public List<LabelScaleProfile> Scales { get; set; } = new();
    }

    private static FileModel? _model;

    /// <summary>Список или выбранные весы изменились.</summary>
    public static event Action? Changed;

    private static FileModel Model => _model ??= Load();

    public static IReadOnlyList<LabelScaleProfile> All => Model.Scales;

    public static LabelScaleProfile Active =>
        Model.Scales.FirstOrDefault(s => s.Id == Model.ActiveId) ?? Model.Scales[0];

    public static LabelScaleProfile? Find(string? id) => Model.Scales.FirstOrDefault(s => s.Id == id);

    public static string DefaultName(int number) =>
        Tr.T($"Весы {number}", $"Тараза {number}", $"Scale {number}", $"Tartı {number}", $"Tarozi {number}");

    /// <summary>Новые весы (марка — как у выбранных сейчас, порт — по умолчанию марки).</summary>
    public static LabelScaleProfile Add()
    {
        var number = Model.Scales.Count + 1;
        while (Model.Scales.Any(s => s.Name == DefaultName(number)))
            number++;
        var brand = Active.Brand;
        var profile = new LabelScaleProfile
        {
            Id = Guid.NewGuid().ToString("N")[..10],
            Name = DefaultName(number),
            Brand = brand,
            Port = DefaultPort(brand),
            Password = brand == "shtrikh" ? "0030" : null,
            ShtrikhDirect = Active.ShtrikhDirect,
        };
        Model.Scales.Add(profile);
        Persist();
        return profile;
    }

    public static int DefaultPort(string brand) => brand switch
    {
        "tm" => 4001,
        "rongta" => 5001,
        "ai" => 0,
        _ => 1111,
    };

    /// <summary>Удаляет весы (последние не удаляются). Если удалили выбранные — выбираются первые.</summary>
    public static void Delete(string id)
    {
        if (Model.Scales.Count <= 1)
            return;
        Model.Scales.RemoveAll(s => s.Id == id);
        if (Model.ActiveId == id)
            Activate(Model.Scales[0]);
        else
            Persist();
    }

    /// <summary>Делает весы выбранными: их адрес, порт, пароль и способ отправки — в прежние
    /// настройки марки (UserPreferences), марка — в ScaleBrand. Сохраняет оба файла.</summary>
    public static void Activate(LabelScaleProfile profile)
    {
        var prefs = UserPreferences.Instance;
        prefs.ScaleBrand = profile.Brand;
        switch (profile.Brand)
        {
            case "tm":
                prefs.TmScaleIp = profile.Ip;
                if (profile.Port is > 0 and <= 65535)
                    prefs.TmScalePort = profile.Port;
                break;
            case "rongta":
                prefs.RongtaScaleIp = profile.Ip;
                if (profile.Port is > 0 and <= 65535)
                    prefs.RongtaScalePort = profile.Port;
                // 2026-09-30: «lan» — касса сама пишет товары на весы (протокол Dahua, порт 4001).
                prefs.RongtaDataSource = profile.RongtaSource is "server" or "lan" ? profile.RongtaSource : "site";
                if (profile.RongtaServerPort is > 0 and <= 65535)
                    prefs.RongtaServerPort = profile.RongtaServerPort;
                break;
            case "shtrikh":
                prefs.ScaleNetworkIp = profile.Ip;
                if (profile.Port is > 0 and <= 65535)
                    prefs.ScaleLanPort = profile.Port;
                if (!string.IsNullOrWhiteSpace(profile.Password))
                    prefs.ScaleLanPassword = profile.Password;
                prefs.ShtrikhDirectLan = profile.ShtrikhDirect;
                break;
        }
        if (!_persistenceDisabled)
            prefs.SaveToDisk();
        Model.ActiveId = profile.Id;
        Persist();
    }

    /// <summary>Записывает текущие прежние настройки (их правят панель и окна марок) в выбранные весы.</summary>
    public static void CaptureActive()
    {
        CaptureInto(Active);
        Persist();
    }

    private static void CaptureInto(LabelScaleProfile profile)
    {
        var prefs = UserPreferences.Instance;
        profile.Brand = NormalizeBrand(prefs.ScaleBrand);
        switch (profile.Brand)
        {
            case "tm":
                profile.Ip = prefs.TmScaleIp ?? "";
                profile.Port = prefs.TmScalePort;
                break;
            case "rongta":
                profile.Ip = !string.IsNullOrWhiteSpace(prefs.RongtaScaleIp) ? prefs.RongtaScaleIp! : "";
                profile.Port = prefs.RongtaScalePort;
                profile.RongtaSource = prefs.RongtaDataSource is "server" or "lan" ? prefs.RongtaDataSource : "site";
                profile.RongtaServerPort = prefs.RongtaServerPort;
                break;
            case "shtrikh":
                profile.Ip = prefs.ScaleNetworkIp ?? "";
                profile.Port = prefs.ScaleLanPort;
                profile.Password = prefs.ScaleLanPassword;
                profile.ShtrikhDirect = prefs.ShtrikhDirectLan;
                break;
            default:
                profile.Ip = "";
                profile.Port = 0;
                break;
        }
    }

    /// <summary>«Отмена» в окне настроек весов: вернуть сохранённую копию и её адрес в настройки.</summary>
    public static void Restore(LabelScaleProfile snapshot)
    {
        var index = Model.Scales.FindIndex(s => s.Id == snapshot.Id);
        if (index < 0)
            return;
        Model.Scales[index] = snapshot.Clone();
        Activate(Model.Scales[index]);
    }

    /// <summary>Имя, категории, отмеченные товары, клавиши — сохранить файл.</summary>
    public static void Save() => Persist();

    /// <summary>2026-09-29: коды этикеток, записанные на все весы, — в запасной поиск кассы по
    /// весовому штрих-коду (ScaleLabelCodeRegistry). Вызывается после отправки товаров.</summary>
    public static void PublishLabelCodes()
    {
        ScaleLabelCodeRegistry.Replace(Model.Scales
            .SelectMany(s => s.SentPlus)
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Code))
            .Select(kv => (kv.Value.Code!, kv.Key)));
    }

    private static string NormalizeBrand(string? brand) => brand is "rongta" or "tm" or "ai" ? brand : "shtrikh";

    // ------------------------------------------------------------------ файл

    private static string FilePath(string folder) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), folder, FileName);

    private static FileModel Load()
    {
        try
        {
            var path = FilePath(AppMode.DataFolderName);
            if (File.Exists(path))
            {
                var model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Json);
                if (model is not null && model.Scales.Count > 0)
                {
                    foreach (var s in model.Scales)
                    {
                        s.Brand = NormalizeBrand(s.Brand);
                        s.Categories ??= new();
                        s.ProductIds ??= new();
                        s.Hotkeys ??= new();
                        // 2026-09-29: файл от прежней версии — полей закреплённых PLU в нём нет.
                        s.PluNumbers ??= new();
                        s.BarcodeCodes ??= new();
                        s.SentPlus ??= new();
                        if (string.IsNullOrWhiteSpace(s.Id))
                            s.Id = Guid.NewGuid().ToString("N")[..10];
                    }
                    return model;
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Список весов не прочитался: {ex.Message}", "WARNING");
        }

        // Файла ещё нет — первые весы из прежних настроек (ничего не теряется).
        var first = new LabelScaleProfile { Id = Guid.NewGuid().ToString("N")[..10], Name = DefaultName(1) };
        CaptureInto(first);
        return new FileModel { ActiveId = first.Id, Scales = { first } };
    }

    /// <summary>Стенд снимков (без экрана) выставляет true отражением: ничего не пишется на диск.</summary>
#pragma warning disable CS0649 // выставляется стендом отражением
    private static bool _persistenceDisabled;
#pragma warning restore CS0649

    private static void Persist()
    {
        if (_persistenceDisabled)
        {
            Changed?.Invoke();
            return;
        }
        try
        {
            var path = FilePath(AppMode.DataFolderName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Сначала во временный файл, затем подмена — чтобы сбой питания не оставил полфайла.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Model, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Список весов не сохранился: {ex.Message}", "WARNING");
        }
        Changed?.Invoke();
    }
}
