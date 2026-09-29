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
            _map ??= Load();
            return _map.TryGetValue(key, out var id) ? id : null;
        }
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
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Коды этикеток весов не сохранились: {ex.Message}", "WARNING");
            }
        }
    }

    private static Dictionary<string, string> Load()
    {
        if (_persistenceDisabled)
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var path = FilePath;
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

    /// <summary>Как в поиске по этикетке: ведущие нули не важны («00005» = «5»).</summary>
    private static string Normalize(string? code)
    {
        var digits = (code ?? "").Trim().TrimStart('0');
        return digits.Length > 0 && digits.All(char.IsDigit) ? digits : "";
    }
}
