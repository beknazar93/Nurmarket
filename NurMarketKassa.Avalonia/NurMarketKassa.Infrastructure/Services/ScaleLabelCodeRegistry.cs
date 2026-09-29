using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-09-29 (закреплённые PLU весов): «Код в ШК», который касса записала в ПЛУ весов,
/// → id товара. Касса ищет весовую этикетку по полям карточки (PLU, артикул, код товара —
/// <see cref="LocalCartService.FindByEmbeddedCode"/>), но у товара без PLU и кода в карточке в
/// этикетку уходит номер ячейки весов или код, вписанный владельцем в колонку «Код в ШК», — по
/// карточке такую этикетку не найти. Этот список — последний запасной шаг поиска: этикетка,
/// напечатанная вчера, находит тот же товар и сегодня.
///
/// Заполняет окно «Весы» после каждой успешной отправки (LabelScaleStore); хранится отдельным
/// файлом рядом с user-settings.json (не в папке программы — её заменяет каждое обновление) и
/// читается лениво при первом скане, поэтому работает и без открытия окна «Весы».
/// Код, записанный на разные товары (разные весы), считается неоднозначным и не используется —
/// лучше «товар не найден», чем чужой товар в чеке.</summary>
public static class ScaleLabelCodeRegistry
{
    private const string FileName = "scale-label-codes.json";

    private sealed class Entry
    {
        public string Code { get; set; } = "";
        public string ProductId { get; set; } = "";
    }

    private static readonly object Gate = new();
    private static Dictionary<string, string>? _map;

    /// <summary>Стенд проверки (без экрана) выставляет true отражением: список живёт только в
    /// памяти — файл кассы на этом компьютере не читается и не пишется (как у LabelScaleStore).</summary>
#pragma warning disable CS0649 // выставляется стендом отражением
    private static bool _persistenceDisabled;
#pragma warning restore CS0649

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        FileName);

    /// <summary>id товара для кода из весовой этикетки; null — кода нет или он неоднозначен.</summary>
    public static string? ProductIdFor(string? embeddedCode)
    {
        var key = Normalize(embeddedCode);
        if (key.Length == 0)
            return null;
        lock (Gate)
        {
            // 2026-09-30 (живой случай владельца): свой файл перечитывается, если его переписали, а если
            // кода нет — смотрим файл соседней программы на этом же компьютере (касса ↔ программа
            // владельца): товары на весы отправили из программы владельца, а сканирует касса —
            // раньше касса про эти коды не знала («Товар с кодом 00193 не найден»).
            RefreshIfChanged(ref _map, ref _mapStamp, FilePath);
            if (_map.TryGetValue(key, out var id))
                return id;
            RefreshIfChanged(ref _siblingMap, ref _siblingStamp, SiblingFilePath);
            return _siblingMap.TryGetValue(key, out var siblingId) ? siblingId : null;
        }
    }

    private static Dictionary<string, string>? _siblingMap;
    private static DateTime _mapStamp;
    private static DateTime _siblingStamp;

    /// <summary>Папка данных второй программы на этом компьютере (касса ↔ программа владельца).</summary>
    private static string SiblingFolder => AppMode.DataFolderName == "NurMarketOwner" ? "NurMarketKassa" : "NurMarketOwner";

    private static string SiblingFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), SiblingFolder, FileName);

    private static void RefreshIfChanged(ref Dictionary<string, string>? map, ref DateTime stamp, string path)
    {
        if (_persistenceDisabled)
        {
            map ??= new Dictionary<string, string>(StringComparer.Ordinal);
            return;
        }
        var time = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (map is not null && time == stamp)
            return;
        stamp = time;
        map = LoadFrom(path);
    }

    /// <summary>Заменяет список целиком и сохраняет его. Неоднозначные коды отбрасываются.</summary>
    public static void Replace(IEnumerable<(string Code, string ProductId)> entries)
    {
        var map = Build(entries);
        lock (Gate)
        {
            _map = map;
            if (_persistenceDisabled)
                return;
            try
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                var list = map.Select(kv => new Entry { Code = kv.Key, ProductId = kv.Value }).ToList();
                File.WriteAllText(temp, JsonSerializer.Serialize(list));
                File.Move(temp, path, overwrite: true);
                _mapStamp = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Коды этикеток весов не сохранились: {ex.Message}", "WARNING");
            }
        }
    }

    private static Dictionary<string, string> LoadFrom(string path)
    {
        if (_persistenceDisabled)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? new List<Entry>();
                return Build(list.Select(e => (e.Code, e.ProductId)));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Коды этикеток весов не прочитались: {ex.Message}", "WARNING");
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Build(IEnumerable<(string Code, string ProductId)> entries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (code, productId) in entries)
        {
            var key = Normalize(code);
            if (key.Length == 0 || string.IsNullOrWhiteSpace(productId) || ambiguous.Contains(key))
                continue;
            if (map.TryGetValue(key, out var existing) && !string.Equals(existing, productId, StringComparison.Ordinal))
            {
                map.Remove(key);
                ambiguous.Add(key);
                continue;
            }
            map[key] = productId;
        }
        return map;
    }

    // ------------------------------------------------------------------ префиксы весов «сумма»

    private const string AmountPrefixesFile = "scale-amount-prefixes.json";
    private static DateTime _ownPrefixStamp = DateTime.MinValue, _siblingPrefixStamp = DateTime.MinValue;
    private static HashSet<string> _sharedAmount = new(StringComparer.Ordinal);

    /// <summary>2026-09-30: весы, на которые касса пишет товары сама (Rongta напрямую: тип ШК 02 —
    /// отдел + код + СУММА), печатают в этикетке сумму. Префикс таких весов записывается сюда, и обе
    /// программы на компьютере читают его этикетки как сумму — даже если у кассы для этого префикса
    /// стояло «вес» (было у владельца: префикс 21 = «вес», весы печатали сумму).</summary>
    public static void PublishAmountPrefix(string prefix)
    {
        if (_persistenceDisabled || prefix.Length != 2 || !prefix.All(char.IsDigit))
            return;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, AmountPrefixesFile);
            var set = ReadPrefixes(path);
            if (!set.Add(prefix))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(set.OrderBy(p => p).ToList()));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Префикс весов «сумма» не сохранился: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Добавляет общие префиксы «сумма» (свои и соседней программы) к правилам разбора
    /// этикеток. Дёшево: файлы перечитываются, только если изменились. Вызывать перед разбором скана.</summary>
    public static void ApplySharedAmountPrefixes()
    {
        if (_persistenceDisabled)
            return;
        try
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var own = Path.Combine(root, AppMode.DataFolderName, AmountPrefixesFile);
            var sibling = Path.Combine(root, SiblingFolder, AmountPrefixesFile);
            var ownTime = File.Exists(own) ? File.GetLastWriteTimeUtc(own) : DateTime.MinValue;
            var siblingTime = File.Exists(sibling) ? File.GetLastWriteTimeUtc(sibling) : DateTime.MinValue;
            if (ownTime != _ownPrefixStamp || siblingTime != _siblingPrefixStamp)
            {
                _ownPrefixStamp = ownTime;
                _siblingPrefixStamp = siblingTime;
                _sharedAmount = ReadPrefixes(own);
                _sharedAmount.UnionWith(ReadPrefixes(sibling));
            }
            if (_sharedAmount.Count == 0 || _sharedAmount.IsSubsetOf(NurMarketKassa.Core.Application.WeightBarcodeParser.AmountPrefixes))
                return;
            var merged = new HashSet<string>(NurMarketKassa.Core.Application.WeightBarcodeParser.AmountPrefixes, StringComparer.Ordinal);
            merged.UnionWith(_sharedAmount);
            NurMarketKassa.Core.Application.WeightBarcodeParser.AmountPrefixes = merged;
            PosLogger.Log("Весы: префиксы «сумма» от весов с прямой отправкой: " + string.Join(", ", _sharedAmount.OrderBy(p => p)), "SCALES");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Префиксы весов «сумма» не прочитаны: {ex.Message}", "WARNING");
        }
    }

    private static HashSet<string> ReadPrefixes(string path)
    {
        try
        {
            if (File.Exists(path))
                return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>(), StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // битый файл — как пустой
        }
        return new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Как в поиске по этикетке: ведущие нули не важны («00005» = «5»).</summary>
    private static string Normalize(string? code)
    {
        var digits = (code ?? "").Trim().TrimStart('0');
        return digits.Length > 0 && digits.All(char.IsDigit) ? digits : "";
    }
}
