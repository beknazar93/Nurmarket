using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28, владелец: «Схема зарплат от чистой прибыли, от чистой маржи». Где хранятся схемы «от прибыли».
/// Схемы сервера (market-sale-employee-pay-profiles) — оклад / процент / за товар — расширить с кассы нельзя (сервер не знает новых
/// pay_scheme, схему базы кассы не меняем), поэтому схемы «от прибыли» — в общих настройках компании NurCRM (тот же документ
/// api/main/app-settings/nurmarket-kassa/, что общие настройки кассы и программы владельца), отдельным ключом «profitPay»:
/// их видят все ПК владельца и администраторов, читают все, меняют только владелец и администратор (кассир — 403 — работает с копией).
/// Копия — файл %APPDATA%\{NurMarketOwner|NurMarketKassa}\profit-pay\{компания}.json: нет связи — работа идёт, при следующем
/// сохранении изменения уходят. Слияние по меткам времени: схема сотрудника и общие настройки — побеждает более новая запись.
/// Остальные ключи общего документа не читаются и не меняются (PATCH только «profitPay»).</summary>
public static class ProfitPayStore
{
    public const string CloudPath = "api/main/app-settings/nurmarket-kassa/";
    public const string CloudKey = "profitPay";

    public static string? DataDirOverride { get; set; }
    public static bool CloudDisabled { get; set; }
    /// <summary>Стенд проверки: свой «сервер» вместо NurCRM (GET → ответ документа, PATCH → тело).</summary>
    public static Func<HttpMethod, JsonObject?, Task<JsonElement>>? TransportOverride { get; set; }

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public enum SyncState { Unknown, Saved, LocalOnly, Forbidden }
    public static SyncState State { get; private set; } = SyncState.Unknown;

    private static string CompanyKey => CompanyInfoService.LastCompany?.Id is { Length: > 0 } id ? id : "default";

    private static string FilePath
    {
        get
        {
            var dir = DataDirOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "profit-pay");
            var safe = string.Concat(CompanyKey.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
            return Path.Combine(dir, (safe.Length == 0 ? "default" : safe) + ".json");
        }
    }

    public static ProfitPayDocument LoadLocal()
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(FilePath))
                    return Normalize(JsonSerializer.Deserialize<ProfitPayDocument>(File.ReadAllText(FilePath), Json));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                PosLogger.Log($"Зарплата от прибыли: копия настроек не прочитана ({ex.Message}).", "WARNING");
                try { File.Move(FilePath, FilePath + $".bad-{DateTime.Now:yyyyMMdd-HHmmss}", true); } catch { }
            }
            return new ProfitPayDocument();
        }
    }

    private static ProfitPayDocument Normalize(ProfitPayDocument? d)
    {
        d ??= new ProfitPayDocument();
        d.Shop ??= new ProfitPayShopSettings();
        d.Shop.Pool ??= new ProfitPayRule();
        d.Schemes = new Dictionary<string, ProfitPayScheme>(d.Schemes ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var s in d.Schemes.Values)
            s.Rule ??= new ProfitPayRule();
        return d;
    }

    private static void WriteLocal(ProfitPayDocument doc)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Зарплата от прибыли: копия настроек не записана ({ex.Message}).", "WARNING");
        }
    }

    private static bool Newer(string? a, string? b)
    {
        var x = ParseAt(a);
        var y = ParseAt(b);
        return Nullable.Compare(x, y) > 0;
    }

    private static DateTime? ParseAt(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static string Stamp(DateTime? after = null)
    {
        var now = DateTime.UtcNow;
        if (after is { } a && a >= now)
            now = a.AddMilliseconds(1);
        return now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Слияние двух документов: по каждому сотруднику и по общим настройкам побеждает более новая метка времени
    /// (равные или отсутствующие — берётся «наш» документ). Ничего не удаляется.</summary>
    public static ProfitPayDocument Merge(ProfitPayDocument ours, ProfitPayDocument theirs)
    {
        var res = Normalize(new ProfitPayDocument { Shop = (ours.Shop ?? new()).Clone() });
        if (Newer(theirs.Shop?.UpdatedAt, ours.Shop?.UpdatedAt) || (ours.Shop?.UpdatedAt is null && theirs.Shop?.UpdatedAt is not null))
            res.Shop = theirs.Shop!.Clone();
        foreach (var (id, s) in ours.Schemes)
            res.Schemes[id] = s.Clone();
        foreach (var (id, s) in theirs.Schemes)
            if (!res.Schemes.TryGetValue(id, out var mine) || Newer(s.UpdatedAt, mine.UpdatedAt))
                res.Schemes[id] = s.Clone();
        return res;
    }

    private static Task<JsonElement> Send(HttpMethod method, JsonObject? body, CancellationToken ct)
    {
        if (TransportOverride is { } t)
            return t(method, body);
        var api = SettingsCloudSync.ApiProvider?.Invoke() is { } a && !string.IsNullOrEmpty(a.AccessToken) ? a : throw new InvalidOperationException("нет входа в NurCRM");
        return api.RequestAsync(method, CloudPath, body, null, ct, TimeSpan.FromSeconds(20));
    }

    private static ProfitPayDocument? ParseRemote(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("settings", out var s))
            throw new InvalidDataException("ответ без поля settings");
        if (s.ValueKind != JsonValueKind.Object || !s.TryGetProperty(CloudKey, out var v) || v.ValueKind != JsonValueKind.Object)
            return null; // в облаке ещё нет схем
        return Normalize(JsonSerializer.Deserialize<ProfitPayDocument>(v.GetRawText(), Json));
    }

    /// <summary>Схемы: копия этого ПК, слитая с облаком (если оно ответило). Состояние — <see cref="State"/>.</summary>
    public static async Task<ProfitPayDocument> LoadAsync(CancellationToken ct = default)
    {
        var local = LoadLocal();
        if (CloudDisabled && TransportOverride is null)
            return local;
        try
        {
            var remote = ParseRemote(await Send(HttpMethod.Get, null, ct).ConfigureAwait(false));
            State = SyncState.Saved;
            if (remote is null)
                return local;
            var merged = Merge(local, remote);
            WriteLocal(merged);
            return merged;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            State = SyncState.LocalOnly;
            PosLogger.Log($"Зарплата от прибыли: схемы с сервера не получены ({ex.Message}) — работаю с копией этого ПК.", "WARNING");
            return local;
        }
    }

    /// <summary>Сохранить изменение: сначала копия, затем — свежий документ облака, слияние и PATCH только ключа «profitPay».
    /// Возвращает итоговый документ. 403 (кассир) — изменения остаются на этом ПК.</summary>
    public static async Task<ProfitPayDocument> SaveAsync(Action<ProfitPayDocument> change, CancellationToken ct = default)
    {
        ProfitPayDocument doc;
        lock (Gate)
        {
            doc = LoadLocal();
            change(doc);
            WriteLocal(doc);
        }
        if (CloudDisabled && TransportOverride is null)
            return doc;
        try
        {
            for (var round = 0; round < 3; round++)
            {
                var remote = ParseRemote(await Send(HttpMethod.Get, null, ct).ConfigureAwait(false));
                var merged = remote is null ? doc : Merge(doc, remote);
                var body = new JsonObject { [CloudKey] = JsonNode.Parse(JsonSerializer.Serialize(merged, Json)) };
                await Send(new HttpMethod("PATCH"), body, ct).ConfigureAwait(false);
                var check = ParseRemote(await Send(HttpMethod.Get, null, ct).ConfigureAwait(false));
                lock (Gate)
                {
                    WriteLocal(merged);
                    doc = merged;
                }
                // чужая запись, пришедшая между нашим PATCH и проверкой, затёрла бы нашу — слияние ещё раз
                if (check is null || JsonSerializer.Serialize(Merge(check, merged), Json) == JsonSerializer.Serialize(check, Json))
                {
                    State = SyncState.Saved;
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            State = SyncState.Forbidden;
            PosLogger.Log("Зарплата от прибыли: нет прав менять общие настройки — схемы остаются на этом ПК.", "INFO");
        }
        catch (Exception ex)
        {
            State = SyncState.LocalOnly;
            PosLogger.Log($"Зарплата от прибыли: схемы не отправлены ({ex.Message}) — отправятся при следующем сохранении.", "WARNING");
        }
        return doc;
    }

    public static Task<ProfitPayDocument> SaveSchemeAsync(ProfitPayScheme scheme, CancellationToken ct = default) =>
        SaveAsync(d =>
        {
            var s = scheme.Clone();
            s.UpdatedAt = Stamp(ParseAt(d.Schemes.TryGetValue(s.UserId, out var old) ? old.UpdatedAt : null));
            d.Schemes[s.UserId] = s;
        }, ct);

    public static Task<ProfitPayDocument> SaveShopAsync(ProfitPayShopSettings shop, CancellationToken ct = default) =>
        SaveAsync(d =>
        {
            var s = shop.Clone();
            s.UpdatedAt = Stamp(ParseAt(d.Shop.UpdatedAt));
            d.Shop = s;
        }, ct);
}
