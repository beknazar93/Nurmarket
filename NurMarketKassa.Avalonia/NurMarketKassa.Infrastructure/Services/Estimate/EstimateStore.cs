using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 («Смета», ТЗ 19.11.2026: «только локально, без сервера»): локальное хранилище смет — файл estimates.json в %LOCALAPPDATA%\NurMarketKassa (AppPaths.KassaLocal, как
/// repair-orders.json и trade-in-records.json; схема pos_local.db не меняется), общий для кассы и программы владельца на одном ПК; запись — под межпроцессным замком
/// AtomicFile.Update (с копией .bak). Файл — данные компании (CompanyStoreRegistry). Между компьютерами НЕ синхронизируется. Повреждённый файл не роняет окно: AtomicFile читает копию .bak,
/// нет и её — пустой список, а испорченный файл откладывается рядом как estimates.json.bad.</summary>
public sealed class EstimateStore
{
    public const string FileName = "estimates.json";
    private const int MaxEstimates = 2000;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTime> _now;

    public EstimateStore(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
    }

    public static EstimateStore Default => new(Path.Combine(AppPaths.KassaLocal, FileName));

    public DateTime Now => _now();

    public EstimateData Load()
    {
        string? text;
        try
        {
            text = AtomicFile.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new EstimateData();
        }
        return Parse(text);
    }

    private static EstimateData Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new EstimateData();
        try
        {
            var data = JsonSerializer.Deserialize<EstimateData>(text, Json) ?? new EstimateData();
            data.Estimates ??= new List<EstimateDoc>();
            data.Links ??= new Dictionary<string, string>();
            foreach (var e in data.Estimates)
            {
                e.Sections ??= new List<EstimateSection>();
                e.Lines ??= new List<EstimateLine>();
                e.Title ??= "";
                e.ClientName ??= "";
                e.Phone ??= "";
                e.Address ??= "";
                e.Note ??= "";
            }
            return data;
        }
        catch (JsonException)
        {
            return new EstimateData();
        }
    }

    private void Mutate(Func<EstimateData, bool> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Update(_path, text =>
        {
            var data = Parse(text);
            return change(data) ? JsonSerializer.Serialize(data, Json) : null;
        });
    }

    /// <summary>Все сметы: новые сверху.</summary>
    public List<EstimateDoc> All() => Load().Estimates.OrderByDescending(e => e.UpdatedAt).ThenByDescending(e => e.Number).ToList();

    public EstimateDoc? Find(string id) => Load().Estimates.FirstOrDefault(e => e.Id == id);

    /// <summary>Новая смета (номер присваивается здесь, под замком).</summary>
    public EstimateDoc Create(Action<EstimateDoc>? init = null)
    {
        EstimateDoc? created = null;
        Mutate(d =>
        {
            d.LastNumber = Math.Max(d.LastNumber, d.Estimates.Count == 0 ? 0 : d.Estimates.Max(e => e.Number)) + 1;
            var doc = EstimateRules.NewDoc(d.LastNumber, _now());
            init?.Invoke(doc);
            d.Estimates.Add(doc);
            if (d.Estimates.Count > MaxEstimates)
                d.Estimates.Remove(d.Estimates.Where(e => e.Id != doc.Id).OrderBy(e => e.UpdatedAt).First()); // 2026-11-19: предел размера файла — самая старая смета уходит
            created = doc;
            return true;
        });
        return created!;
    }

    /// <summary>Сохранить смету целиком (новая — добавляется, существующая — заменяется). Время изменения ставится здесь.</summary>
    public void Save(EstimateDoc doc)
    {
        Mutate(d =>
        {
            doc.UpdatedAt = _now();
            var i = d.Estimates.FindIndex(e => e.Id == doc.Id);
            if (i >= 0)
                d.Estimates[i] = doc;
            else
            {
                if (doc.Number <= 0)
                    doc.Number = Math.Max(d.LastNumber, d.Estimates.Count == 0 ? 0 : d.Estimates.Max(e => e.Number)) + 1;
                d.LastNumber = Math.Max(d.LastNumber, doc.Number);
                d.Estimates.Add(doc);
            }
            return true;
        });
    }

    /// <summary>Копия сметы (статус «Черновик», новый номер). null — исходной нет.</summary>
    public EstimateDoc? Duplicate(string id, string titleSuffix)
    {
        EstimateDoc? copy = null;
        Mutate(d =>
        {
            var source = d.Estimates.FirstOrDefault(e => e.Id == id);
            if (source is null)
                return false;
            d.LastNumber = Math.Max(d.LastNumber, d.Estimates.Max(e => e.Number)) + 1;
            copy = EstimateRules.Duplicate(source, d.LastNumber, _now(), titleSuffix);
            d.Estimates.Add(copy);
            return true;
        });
        return copy;
    }

    public bool Delete(string id)
    {
        var done = false;
        Mutate(d => done = d.Estimates.RemoveAll(e => e.Id == id) > 0);
        return done;
    }

    public bool SetStatus(string id, EstimateStatus status)
    {
        var done = false;
        Mutate(d =>
        {
            var e = d.Estimates.FirstOrDefault(x => x.Id == id);
            if (e is null || e.Status == status)
                return false;
            e.Status = status;
            e.UpdatedAt = _now();
            return done = true;
        });
        return done;
    }

    /// <summary>Запомненная привязка «материал → товар» (ключ — код материала калькулятора).</summary>
    public string? GetLink(string materialKey) => Load().Links.TryGetValue(materialKey, out var id) ? id : null;

    public void SetLink(string materialKey, string productId) => Mutate(d =>
    {
        if (d.Links.TryGetValue(materialKey, out var old) && old == productId)
            return false;
        d.Links[materialKey] = productId;
        return true;
    });
}
