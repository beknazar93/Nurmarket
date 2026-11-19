using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-13 (Л-13 «Отчёты владельца»): чеки периода из списка продаж сервера NurCRM (все кассы компании) в виде <see cref="OwnerReportsMath.SaleFact"/> —
/// для отчётов «По кассирам» и «По часам и дням недели». Поля — те же, что читают «История чеков» и утренний отчёт (status, discount_total, имя кассира,
/// время продажи); новых полей не выдумываем. Страницы по 200, не больше <see cref="MaxPages"/> страниц — упёрлись в предел, отчёт помечается «неполный».
/// Ошибки по кассирам — журнал «Удалено из чека» этого ПК (CartRemovalLogStore): сервера для него в NurCRM нет.</summary>
public static class OwnerSalesReportData
{
    private const int PageSize = 200;
    public const int MaxPages = 60;

    /// <summary>Чеки периода (оба дня включительно). Truncated — список оборван пределом страниц. Бросает ошибку сервера как есть
    /// (окно покажет «Не удалось / Повторить»).</summary>
    public static async Task<(IReadOnlyList<OwnerReportsMath.SaleFact> Sales, bool Truncated)> LoadAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var fromDay = from.Date;
        var toExclusive = to.Date.AddDays(1);
        var result = new List<OwnerReportsMath.SaleFact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        for (var page = 1; ; page++)
        {
            if (page > MaxPages)
            {
                truncated = true;
                PosLogger.Log($"Отчёты владельца: чеков за {fromDay:dd.MM}–{to:dd.MM} больше {MaxPages * PageSize} — отчёт неполный.", "WARNING");
                break;
            }
            ct.ThrowIfCancellationRequested();
            var items = await App.SalesApi.PosSalesListAsync(page, PageSize, null, ct, dateFrom: fromDay, dateToExclusive: toExclusive).ConfigureAwait(false);
            if (items.Count == 0)
                break;
            var added = 0;
            foreach (var row in items)
            {
                var id = PosSaleRowFormatter.TrySaleId(row) ?? "";
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                added++;
                if (TryFact(row, fromDay, toExclusive) is { } fact)
                    result.Add(fact);
            }
            if (added == 0 || items.Count < PageSize)
                break;
        }
        return (result, truncated);
    }

    /// <summary>Строка списка → чек. Черновики («new») и чеки вне периода пропускаются.</summary>
    internal static OwnerReportsMath.SaleFact? TryFact(JsonElement row, DateTime from, DateTime toExclusive)
    {
        var status = (Str(row, "status") ?? "").ToLowerInvariant();
        if (status == "new" || SaleTime(row) is not { } at || at < from || at >= toExclusive)
            return null;
        return new OwnerReportsMath.SaleFact(at, CartDisplayHelper.TryCashierName(row) ?? "", Money(row, "total", "grand_total", "total_amount", "amount", "total_sum", "final_total", "order_total"),
            Money(row, "discount_total"), status);
    }

    /// <summary>«Удалено из чека» по кассирам за период (этот ПК, своя компания). Файла нет — пусто. Читает файл — не на потоке интерфейса.</summary>
    public static IReadOnlyDictionary<string, int> RemovalsByCashier(DateTime from, DateTime to)
    {
        var map = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            if (!File.Exists(CartRemovalLogStore.FilePath))
                return map;
            var companyId = CompanyInfoService.LastCompany?.Id;
            foreach (var r in CartRemovalLogStore.ReadAll())
            {
                var day = r.At.LocalDateTime.Date;
                if (day < from.Date || day > to.Date)
                    continue;
                if (!string.IsNullOrWhiteSpace(r.CompanyId) && (string.IsNullOrWhiteSpace(companyId) || !string.Equals(r.CompanyId, companyId, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var name = OwnerReportsMath.CashierName(r.Cashier);
                map[name] = map.GetValueOrDefault(name) + 1;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отчёты владельца: журнал «Удалено из чека» не прочитан ({ex.GetType().Name}).", "WARNING");
        }
        return map;
    }

    private static DateTime? SaleTime(JsonElement row)
    {
        foreach (var key in new[] { "paid_at", "closed_at", "created_at", "date" })
        {
            if (Str(row, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at))
                return at.LocalDateTime;
        }
        return null;
    }

    private static decimal Money(JsonElement row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(key, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
                return d;
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
                return s;
        }
        return 0m;
    }

    private static string? Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;
}
