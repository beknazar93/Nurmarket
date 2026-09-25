using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NurSupportBot.Services;

public sealed record LoginResult(string Refresh, string Access, string Name, string Company, string Tariff, string Sector, string Role);

public sealed record SalesReport(
    decimal Revenue, long Receipts, decimal AvgReceipt, decimal Cash, decimal NonCash,
    decimal Returns, long ReturnsCount, decimal? Profit, decimal? Margin);

public sealed class NurCrmException : Exception
{
    public NurCrmException(string message, HttpStatusCode? status = null) : base(message) => Status = status;
    public HttpStatusCode? Status { get; }
}

/// <summary>Сервер NurCRM: вход клиента и финансовый отчёт — та же аналитика, что на сайте
/// (api/main/analytics/market/?tab=sales), поэтому цифры совпадают с сайтом и не зависят от того,
/// включена ли касса.</summary>
public sealed class NurCrmClient
{
    private readonly HttpClient _http;

    public NurCrmClient(string baseUrl)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("NurSupportBot/1.0");
    }

    public async Task<LoginResult> LoginAsync(string email, string password, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/users/auth/login/", new { email, password }, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new NurCrmException("Неверный логин или пароль.", resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new NurCrmException($"Сервер NurCRM ответил {(int)resp.StatusCode}. Попробуйте позже.", resp.StatusCode);

        using var login = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var access = Str(login.RootElement, "access") ?? throw new NurCrmException("Сервер не выдал токен.");
        var refresh = Str(login.RootElement, "refresh") ?? throw new NurCrmException("Сервер не выдал токен.");
        var name = string.Join(" ", new[] { Str(login.RootElement, "first_name"), Str(login.RootElement, "last_name") }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        var company = await GetJsonAsync("api/users/company/", access, ct).ConfigureAwait(false);
        var profile = await GetJsonAsync("api/users/profile/", access, ct).ConfigureAwait(false);
        return new LoginResult(
            refresh,
            access,
            name.Length > 0 ? name : email,
            Str(company, "name") ?? "—",
            company.TryGetProperty("subscription_plan", out var plan) && plan.ValueKind == JsonValueKind.Object ? Str(plan, "name") ?? "—" : "—",
            company.TryGetProperty("sector", out var sector) && sector.ValueKind == JsonValueKind.Object ? Str(sector, "name") ?? "—" : "—",
            Str(profile, "role_display") ?? Str(profile, "role") ?? "—");
    }

    /// <summary>Новый access-токен по refresh; null — refresh истёк, нужен повторный вход.</summary>
    public async Task<string?> RefreshAsync(string refresh, CancellationToken ct)
    {
        using var resp = await _http.PostAsJsonAsync("api/users/auth/refresh/", new { refresh }, ct).ConfigureAwait(false);
        if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        if (!resp.IsSuccessStatusCode)
            throw new NurCrmException($"Сервер NurCRM ответил {(int)resp.StatusCode}. Попробуйте позже.", resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Str(doc.RootElement, "access");
    }

    public async Task<SalesReport> SalesReportAsync(string access, DateTime from, DateTime to, CancellationToken ct)
    {
        var url = "api/main/analytics/market/?tab=sales"
                  + "&period_start=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                  + "&period_end=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var data = await GetJsonAsync(url, access, ct).ConfigureAwait(false);

        var cards = data.GetProperty("cards");
        decimal cash = 0, nonCash = 0;
        if (data.TryGetProperty("charts", out var charts) && charts.TryGetProperty("payment_methods", out var methods)
            && methods.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in methods.EnumerateArray())
            {
                var total = Dec(m, "total") ?? 0;
                if (Str(m, "method") == "cash")
                    cash += total;
                else if (Str(m, "method") != "debt")
                    nonCash += total;
            }
        }

        decimal returns = 0;
        long returnsCount = 0;
        if (data.TryGetProperty("tables", out var tables) && tables.TryGetProperty("documents", out var docs)
            && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in docs.EnumerateArray())
            {
                if (Str(d, "name") != "Возврат продажи")
                    continue;
                returns = Dec(d, "sum") ?? 0;
                returnsCount = d.TryGetProperty("count", out var c) && c.TryGetInt64(out var n) ? n : 0;
            }
        }

        return new SalesReport(
            Dec(cards, "revenue") ?? 0,
            cards.TryGetProperty("transactions", out var tr) && tr.TryGetInt64(out var t) ? t : 0,
            Dec(cards, "avg_check") ?? 0,
            cash,
            nonCash,
            returns,
            returnsCount,
            Dec(cards, "gross_profit"),
            Dec(cards, "margin_percent"));
    }

    private async Task<JsonElement> GetJsonAsync(string path, string access, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            throw new NurCrmException("У вашей учётной записи нет доступа к аналитике в NurCRM.", resp.StatusCode);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new NurCrmException("Вход устарел — войдите заново.", resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new NurCrmException($"Сервер NurCRM ответил {(int)resp.StatusCode}. Попробуйте позже.", resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static decimal? Dec(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v))
            return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
