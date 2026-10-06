using System.Globalization;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, владелец: «следить за сроками годностей». Сроки годности товаров — с сервера NurCRM: вкладка «Товары»
/// аналитики (GET analytics/market/?tab=products) отдаёт таблицу expiring_products — товар, остаток, expiration_date,
/// days_left, status expired/expiring (проверено 06.10: «Нан» — просрочен на 24 дня). Своих сроков у кассы нет (раньше
/// «Пополнение и сроки» только оценивали срок по категории). Справочник общий: склад (метки и фильтр «Срок годности»),
/// ИИ-советник и бот. Обновляется не чаще раза в 5 минут.</summary>
public static class ProductExpiryIndex
{
    public sealed record Mark(string ProductId, string Name, DateTime Date, int DaysLeft, bool Expired, double Quantity);

    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, Mark> _marks = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _loadedUtc = DateTime.MinValue;

    public static IReadOnlyDictionary<string, Mark> Marks => _marks;

    public static bool IsLoaded => _loadedUtc > DateTime.MinValue;

    /// <summary>Сроки с сервера (из кеша, если моложе 5 минут). Ошибка — прежние данные.</summary>
    public static async Task<IReadOnlyDictionary<string, Mark>> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && DateTime.UtcNow - _loadedUtc < MaxAge)
            return _marks;
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!force && DateTime.UtcNow - _loadedUtc < MaxAge)
                return _marks;
            var today = DateTime.Today;
            var report = await PosApp.SalesApi.MarketProductsReportAsync(today, today, ct).ConfigureAwait(false);
            var marks = new Dictionary<string, Mark>(StringComparer.OrdinalIgnoreCase);
            if (report.ValueKind == JsonValueKind.Object && report.TryGetProperty("tables", out var tables)
                && tables.TryGetProperty("expiring_products", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var id = Str(row, "id") ?? Str(row, "product_id");
                    if (string.IsNullOrWhiteSpace(id)
                        || !DateTime.TryParse(Str(row, "expiration_date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                        continue;
                    var days = int.TryParse(Str(row, "days_left"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : (int)(date.Date - today).TotalDays;
                    var expired = string.Equals(Str(row, "status"), "expired", StringComparison.OrdinalIgnoreCase) || days < 0;
                    var qty = double.TryParse(Str(row, "quantity"), NumberStyles.Any, CultureInfo.InvariantCulture, out var q) ? q : 0;
                    marks[id] = new Mark(id, Str(row, "name") ?? "", date.Date, days, expired, qty);
                }
            }
            _marks = marks;
            _loadedUtc = DateTime.UtcNow;
            return marks;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Сроки годности: не получены с сервера ({ex.Message}).", "WARNING");
            return _marks;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Метка для строки склада: «Просрочен 12.09» / «Срок до 10.10 · 4 дн.».</summary>
    public static string BadgeText(Mark m) => m.Expired
        ? Tr.T($"Просрочен {m.Date:dd.MM}", $"Мөөнөтү өткөн {m.Date:dd.MM}", $"Expired {m.Date:dd.MM}", $"SKT geçti {m.Date:dd.MM}", $"Muddati o'tgan {m.Date:dd.MM}")
        : Tr.T($"Срок до {m.Date:dd.MM} · {m.DaysLeft} дн.", $"Мөөнөтү {m.Date:dd.MM} чейин · {m.DaysLeft} күн", $"Expires {m.Date:dd.MM} · {m.DaysLeft} d",
            $"SKT {m.Date:dd.MM} · {m.DaysLeft} gün", $"Muddati {m.Date:dd.MM} gacha · {m.DaysLeft} kun");

    /// <summary>Для ИИ и бота: просроченные и скоро истекающие — строками.</summary>
    public static string ContextText(int max = 20)
    {
        var list = _marks.Values.OrderBy(m => m.DaysLeft).Take(max).ToList();
        if (list.Count == 0)
            return IsLoaded ? "Сроки годности: просроченных и скоро истекающих товаров нет." : "";
        return "Сроки годности (с сервера):\n" + string.Join("\n", list.Select(m =>
            $"• id={m.ProductId} | {m.Name} | {(m.Expired ? $"ПРОСРОЧЕН с {m.Date:dd.MM.yyyy} ({-m.DaysLeft} дн.)" : $"годен до {m.Date:dd.MM.yyyy} (осталось {m.DaysLeft} дн.)")} | остаток {m.Quantity:0.###}"));
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
}
