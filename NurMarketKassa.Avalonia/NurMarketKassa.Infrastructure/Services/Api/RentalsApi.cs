using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>Вещь в прокате (вариант одежды или обычный товар).</summary>
public sealed record RentalItem(string? ProductId, string? VariantId, string Name, string Size, string Color, double Qty)
{
    public string Label => string.Join(", ", new[] { Size, Color }.Where(x => !string.IsNullOrWhiteSpace(x))) is { Length: > 0 } sc
        ? $"{Name} ({sc})"
        : Name;
}

/// <summary>Прокат NurCRM (GET /api/rentals/).</summary>
public sealed record RentalDto(
    string Id,
    int Number,
    string ClientId,
    string ClientName,
    string Status,
    bool Overdue,
    DateTime? DateFrom,
    DateTime? DateTo,
    string Tariff,
    string DepositType,
    double DepositAmount,
    string DepositMethod,
    string DepositDocument,
    IReadOnlyList<RentalItem> Items,
    string? Condition,
    double Penalty,
    string Note,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? ReturnedAt,
    double DepositRefunded,
    double DepositWithheld)
{
    /// <summary>На руках — всё, что не возвращено (2026-10-02, проверка: сервер может отдать статус
    /// «overdue» — раньше у такого проката не было кнопки «Принять возврат»).</summary>
    public bool IsActive => !string.Equals(Status, "returned", StringComparison.OrdinalIgnoreCase);
    public bool IsOverdue => IsActive && (Overdue || string.Equals(Status, "overdue", StringComparison.OrdinalIgnoreCase));
    public bool IsDocumentDeposit => string.Equals(DepositType, "document", StringComparison.OrdinalIgnoreCase);
}

/// <summary>2026-10-02, владелец: «реализуй аренду для услуг и для магазина одежды — прокат!».
/// Сервер NurCRM уже ведёт прокат по нашему ТЗ (ТЗ-BE раздел 7.3): документ проката с клиентом,
/// вещами (вариант или товар), сроком и залогом. Проверено на тестовом аккаунте 02.10:
/// создание списывает вещь со склада и приходует денежный залог в кассу смены («Залог по прокату №N»),
/// возврат возвращает вещь на склад и проводит расход «Возврат залога…», штраф удерживается из залога.
/// Стоимости проката в документе НЕТ — касса берёт её обычной продажей (строка «Прокат №N…»).</summary>
public sealed class RentalsApi
{
    private const string Root = "api/rentals/";
    private readonly NurMarketApiClient _api;

    public RentalsApi(NurMarketApiClient api) => _api = api;

    /// <summary>status: active | overdue | returned | null (все).</summary>
    public async Task<IReadOnlyList<RentalDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(status))
            query["status"] = status;
        var data = await _api.RequestAsync(HttpMethod.Get, Root, null, query, ct).ConfigureAwait(false);
        return Rows(data).Select(Parse).ToList();
    }

    /// <summary>Новый прокат. depositType: money | document; для документа — его описание
    /// («Паспорт ID 1234567»), сумма 0.</summary>
    public async Task<RentalDto> CreateAsync(
        string clientId, IReadOnlyList<RentalItem> items, DateTime dateFrom, DateTime dateTo, string tariff,
        string depositType, double depositAmount, string? depositMethod, string? depositDocument, string? note,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["client"] = clientId,
            ["items"] = items.Select(i =>
            {
                var item = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(i.VariantId))
                    item["variant"] = i.VariantId;
                else
                    item["product"] = i.ProductId;
                if (Math.Abs(i.Qty - 1) > 1e-9)
                    item["qty"] = i.Qty.ToString("0.###", CultureInfo.InvariantCulture);
                return item;
            }).ToList(),
            ["date_from"] = dateFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["date_to"] = dateTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["tariff"] = tariff,
            ["deposit_type"] = depositType,
            ["deposit_amount"] = depositAmount.ToString("0.00", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(depositMethod))
            body["deposit_method"] = depositMethod;
        if (!string.IsNullOrWhiteSpace(depositDocument))
            body["deposit_document"] = depositDocument;
        if (!string.IsNullOrWhiteSpace(note))
            body["note"] = note;
        var data = await _api.RequestAsync(HttpMethod.Post, Root, body, null, ct).ConfigureAwait(false);
        return Parse(data);
    }

    /// <summary>Возврат: condition ok | damaged, штраф удерживается из залога.</summary>
    public async Task<RentalDto> ReturnAsync(string id, string condition, double penalty, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["condition"] = condition,
            ["penalty"] = penalty.ToString("0.00", CultureInfo.InvariantCulture),
        };
        var data = await _api.RequestAsync(HttpMethod.Post, Root + id + "/return/", body, null, ct).ConfigureAwait(false);
        return Parse(data);
    }

    /// <summary>Понятный текст ошибки сервера (первое сообщение валидации).</summary>
    public static string Describe(Exception ex)
    {
        if (ex is ApiException { Payload: { ValueKind: JsonValueKind.Object } payload })
        {
            foreach (var p in payload.EnumerateObject())
            {
                var v = p.Value;
                if (v.ValueKind == JsonValueKind.String)
                    return v.GetString() ?? ex.Message;
                if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0)
                    return v[0].ValueKind == JsonValueKind.String ? $"{p.Name}: {v[0].GetString()}" : $"{p.Name}: {v[0]}";
            }
        }
        return SensitiveDataRedactor.Redact(ex.Message);
    }

    private static RentalDto Parse(JsonElement d)
    {
        var items = new List<RentalItem>();
        if (d.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in arr.EnumerateArray())
                items.Add(new RentalItem(Str(i, "product"), Str(i, "variant"), Str(i, "name") ?? "", Str(i, "size") ?? "", Str(i, "color") ?? "",
                    Num(i, "qty") is var q && q > 0 ? q : 1));
        }

        var (refunded, withheld) = DepositSettlement(d);
        return new RentalDto(
            Str(d, "id") ?? "",
            (int)Num(d, "number"),
            Str(d, "client") ?? "",
            Str(d, "client_name") ?? "",
            Str(d, "status") ?? "",
            d.TryGetProperty("overdue", out var o) && o.ValueKind == JsonValueKind.True,
            Day(d, "date_from"),
            Day(d, "date_to"),
            Str(d, "tariff") ?? "",
            Str(d, "deposit_type") ?? "",
            Num(d, "deposit_amount"),
            Str(d, "deposit_method") ?? "",
            Str(d, "deposit_document") ?? "",
            items,
            Str(d, "condition"),
            Num(d, "penalty"),
            Str(d, "note") ?? "",
            Stamp(d, "created_at"),
            Stamp(d, "returned_at"),
            refunded,
            withheld);
    }

    /// <summary>2026-10-04, живой случай (Z-отчёт, прокат №8, залог 500 деньгами, возвращён): в блоке
    /// «Прокат за смену» не было «залог −500», хотя в «Расходе» смены 500 есть. Проверено на тестовом
    /// аккаунте (GET api/rentals/, только чтение): полей deposit_refunded / deposit_withheld в ответе
    /// сервера НЕТ (ни в списке, ни в карточке) — касса читала их как 0. Возврат залога сервер проводит
    /// движением денег «Возврат залога по прокату №N» (расход, source_kind=rental) на сумму залога минус
    /// штраф: №8 500−0 = 500, №4 500−200 = 300, №3 1000−200 = 800. Так и считаем, пока сервер не отдаёт
    /// эти поля сам (отдаст — берём его числа).</summary>
    private static (double Refunded, double Withheld) DepositSettlement(JsonElement d)
    {
        var serverRefunded = NumOrNull(d, "deposit_refunded");
        var serverWithheld = NumOrNull(d, "deposit_withheld");
        if (serverRefunded is not null || serverWithheld is not null)
            return (serverRefunded ?? 0, serverWithheld ?? 0);

        var returned = string.Equals(Str(d, "status"), "returned", StringComparison.OrdinalIgnoreCase)
                       || Stamp(d, "returned_at") is not null;
        if (!returned || string.Equals(Str(d, "deposit_type"), "document", StringComparison.OrdinalIgnoreCase))
            return (0, 0);

        var deposit = Math.Max(0, Num(d, "deposit_amount"));
        var withheld = Math.Min(deposit, Math.Max(0, Num(d, "penalty")));
        return (Math.Round(deposit - withheld, 2), Math.Round(withheld, 2));
    }

    private static double? NumOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String
            ? Num(e, name)
            : null;

    private static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array)
            return r.EnumerateArray();
        return Array.Empty<JsonElement>();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.GetDouble(),
                JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) => x,
                _ => 0,
            }
            : 0;

    private static DateTime? Day(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static DateTimeOffset? Stamp(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
