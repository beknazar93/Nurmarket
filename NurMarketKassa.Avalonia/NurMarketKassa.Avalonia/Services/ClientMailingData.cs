using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-26 («Рассылки по сегментам клиентов»): сбор клиентов для рассылки из уже существующих запросов — список клиентов (общий кэш
/// <see cref="IClientsApiService"/>), долги (точный расчёт «Долгов клиентов», <see cref="OwnerShopFacts.GetDebtsAsync"/>) и чеки за год
/// (<see cref="ISalesApiService.PosSalesListAsync"/>, как «Бонусы» — страницами по 500, по 4 страницы параллельно): последняя покупка и сумма покупок клиента.
/// Новых методов сервера нет. Результат кэшируется на 10 минут («Обновить» — заново). Нет чеков или долгов — рассылка по остальным сегментам работает,
/// окно честно пишет, чего не хватило.</summary>
public static class ClientMailingData
{
    /// <summary>За сколько дней берём чеки (последняя покупка и сумма для VIP).</summary>
    public const int SalesDays = 365;

    public sealed record Snapshot(IReadOnlyList<ClientMailing.Contact> Contacts, DateTime AtLocal, bool DebtsKnown, bool SalesKnown, bool SalesTruncated);

    private static Snapshot? _cache;
    private static string? _cacheKey;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Сбросить кэш (смена компании, «Обновить»).</summary>
    public static void Invalidate() => _cache = null;

    private static Snapshot? _seed;

    /// <summary>Для тестов: окно берёт готовый список, а не ходит на сервер (null — снова настоящая загрузка).</summary>
    public static void SeedForTests(Snapshot? snapshot) => _seed = snapshot;

    public static async Task<Snapshot> LoadAsync(CancellationToken ct, bool force = false)
    {
        if (_seed is { } seeded)
            return seeded;
        var key = App.GetRequiredService<NurMarketApiClient>().AccessToken ?? "";
        if (!force && _cache is { } cached && _cacheKey == key && DateTime.Now - cached.AtLocal < TimeSpan.FromMinutes(10))
            return cached;
        await Gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            if (!force && _cache is { } again && _cacheKey == key && DateTime.Now - again.AtLocal < TimeSpan.FromMinutes(10))
                return again;
            if (force)
                ClientsApiService.InvalidateCache();
            var clientsTask = App.GetRequiredService<IClientsApiService>().GetClientsAsync(null, ct);
            var debtsTask = LoadDebtsAsync(ct);
            var salesTask = LoadSalesAsync(App.GetRequiredService<ISalesApiService>(), ct);
            await Task.WhenAll(clientsTask, debtsTask, salesTask).ConfigureAwait(true);
            var clients = await clientsTask.ConfigureAwait(true);
            var debts = await debtsTask.ConfigureAwait(true);
            var (sales, truncated) = await salesTask.ConfigureAwait(true);

            var local = ClientMailing.LocalBirthdays();
            var list = new List<ClientMailing.Contact>(clients.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in clients)
            {
                if (Str(el, "id") is not { Length: > 0 } id || !seen.Add(id))
                    continue;
                var name = (Str(el, "full_name") ?? Str(el, "name") ?? "").Trim();
                var phone = (Str(el, "phone") ?? "").Trim();
                var debt = debts?.GetValueOrDefault(id) ?? 0;
                var stat = sales is not null && sales.TryGetValue(id, out var st) ? st : default;
                DateTime? birthday = local.TryGetValue(id, out var lb) ? lb : ServerBirthday(el);
                // 2026-11-26 (стресс-тест): номер WhatsApp — тем же правилом, что ссылка wa.me (ClientMailing.WhatsAppNumber), городские — без WhatsApp
                list.Add(new ClientMailing.Contact(id, name, phone, ClientMailing.WhatsAppNumber(phone), debt,
                    stat.Receipts > 0 ? stat.Last : null, stat.Spent, stat.Receipts, birthday));
            }
            var snapshot = new Snapshot(list, DateTime.Now, debts is not null, sales is not null, truncated);
            _cache = snapshot;
            _cacheKey = key;
            return snapshot;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<Dictionary<string, double>?> LoadDebtsAsync(CancellationToken ct)
    {
        try
        {
            var facts = await OwnerShopFacts.GetDebtsAsync(ct).ConfigureAwait(false);
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in facts.Debtors.Where(d => !string.IsNullOrWhiteSpace(d.ClientId) && d.Amount > 0.005))
                result[d.ClientId] = d.Amount;
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Рассылка: долги не получены ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    private readonly record struct Stat(DateTime Last, double Spent, int Receipts);

    private static async Task<(Dictionary<string, Stat>? Stats, bool Truncated)> LoadSalesAsync(ISalesApiService sales, CancellationToken ct)
    {
        const int pageSize = 500;
        const int maxPages = 40;
        const int wave = 4;
        var to = DateTime.Today;
        var from = to.AddDays(-SalesDays);
        var stats = new Dictionary<string, Stat>(StringComparer.OrdinalIgnoreCase);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        try
        {
            for (var first = 1; first <= maxPages; first = first == 1 ? 2 : first + wave)
            {
                ct.ThrowIfCancellationRequested();
                var count = first == 1 ? 1 : Math.Min(wave, maxPages - first + 1);
                var pages = await Task.WhenAll(Enumerable.Range(first, count).Select(pg =>
                    sales.PosSalesListAsync(pg, pageSize, null, ct, from, to.AddDays(1)))).ConfigureAwait(false);
                var stop = false;
                for (var k = 0; k < pages.Length && !stop; k++)
                {
                    var raw = pages[k];
                    var fresh = 0;
                    foreach (var el in raw)
                    {
                        var id = Str(el, "id");
                        if (!string.IsNullOrEmpty(id) && !seenIds.Add(id))
                            continue;
                        fresh++;
                        var status = (Str(el, "status") ?? "").Trim().ToLowerInvariant();
                        if (status is not ("" or "paid" or "partially_returned" or "debt"))
                            continue;
                        if (ClientIdOf(el) is not { } client || !TryServerDay(Str(el, "created_at"), out var day))
                            continue;
                        var total = Num(el, "total");
                        stats[client] = stats.TryGetValue(client, out var old)
                            ? new Stat(old.Last > day ? old.Last : day, old.Spent + total, old.Receipts + 1)
                            : new Stat(day, total, 1);
                    }
                    if (raw.Count < pageSize || fresh == 0)
                        stop = true;
                    else if (first + k == maxPages)
                        truncated = true;
                }
                if (stop)
                    break;
            }
            return (stats, truncated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Рассылка: чеки за год не получены ({ex.GetType().Name}) — сегменты по покупкам недоступны.", "WARNING");
            return (null, false);
        }
    }

    private static string? ClientIdOf(JsonElement sale)
    {
        foreach (var name in new[] { "client", "client_id", "customer", "customer_id" })
        {
            if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty(name, out var v))
                continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.String when !string.IsNullOrWhiteSpace(v.GetString()):
                    return v.GetString()!.Trim();
                case JsonValueKind.Number:
                    return v.GetRawText();
                case JsonValueKind.Object when Str(v, "id") is { Length: > 0 } id:
                    return id;
            }
        }
        return null;
    }

    private static bool TryServerDay(string? text, out DateTime day)
    {
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            day = stamp.DateTime.Date;
            return true;
        }
        day = default;
        return false;
    }

    /// <summary>День рождения из карточки клиента, если сервер его отдаёт (поля birth_date / birthday / date_of_birth), иначе null.</summary>
    private static DateTime? ServerBirthday(JsonElement client)
    {
        foreach (var name in new[] { "birth_date", "birthday", "date_of_birth" })
            if (Str(client, name) is { Length: > 0 } text && ClientMailing.TryParseBirthday(text, out var value) && value is not null)
                return value;
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;

    private static double Num(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }
}
