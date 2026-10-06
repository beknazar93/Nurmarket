using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, редизайн программы владельца («займись редизайном и удобством админки»). «Сводка» за «Сегодня»
/// сравнивала неполный сегодняшний день с целым вчерашним: днём плитки показывали «▼ 95% к вчера», хотя магазин
/// торговал как обычно. Теперь сравнение — со вчерашним днём до того же часа: по списку вчерашних чеков (качается
/// один раз в день, 1–3 запроса) считается, какая доля вчерашней выручки и чеков набралась к этому часу, и итоги
/// вчера с сервера уменьшаются на эту долю (прибыль — как выручка). Подпись — «к вчера на это время». Нет списка
/// (сервер не ответил) — как раньше, к целому вчерашнему дню.</summary>
public partial class OwnerShellWindow
{
    private DateTime _yesterdayLoadedFor = DateTime.MinValue;
    private List<(TimeSpan At, double Total)>? _yesterdaySales;
    private bool _yesterdayLoading;

    /// <summary>Доля вчерашней выручки и вчерашних чеков, набранная к этому часу; null — сравнивать с целым днём.</summary>
    private (double Revenue, double Checks)? YesterdayShareNow()
    {
        if (_period != "today" || _yesterdaySales is not { Count: > 0 } list || _yesterdayLoadedFor != DateTime.Today.AddDays(-1))
            return null;
        var total = list.Sum(x => x.Total);
        if (total <= 0)
            return null;
        var now = DateTime.Now.TimeOfDay;
        return (list.Where(x => x.At <= now).Sum(x => x.Total) / total, (double)list.Count(x => x.At <= now) / list.Count);
    }

    /// <summary>Вчерашние чеки (время и сумма) — один раз в день; ошибки не мешают «Сводке».</summary>
    private async Task EnsureYesterdaySalesAsync(CancellationToken ct)
    {
        var day = DateTime.Today.AddDays(-1);
        if (_period != "today" || _yesterdayLoadedFor == day || _yesterdayLoading)
            return;
        _yesterdayLoading = true;
        try
        {
            var list = new List<(TimeSpan At, double Total)>();
            for (var page = 1; page <= 20; page++)
            {
                var batch = await App.SalesApi.PosSalesListAsync(page, 500, null, ct, day, day.AddDays(1)).ConfigureAwait(true);
                foreach (var s in batch)
                {
                    if (s.TryGetProperty("status", out var st) && string.Equals(st.GetString(), "canceled", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!s.TryGetProperty("created_at", out var at) || at.ValueKind != JsonValueKind.String
                        || !DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                        || created.LocalDateTime.Date != day)
                        continue;
                    var total = s.TryGetProperty("total", out var t)
                                && double.TryParse(t.ValueKind == JsonValueKind.String ? t.GetString() : t.GetRawText(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
                        ? v
                        : 0;
                    list.Add((created.LocalDateTime.TimeOfDay, total));
                }
                if (batch.Count < 500)
                    break;
            }
            _yesterdaySales = list;
            _yesterdayLoadedFor = day;
            PosLogger.Log($"Owner app: «к вчера на это время» — вчерашних чеков {list.Count}.", "UI");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: вчерашние чеки для сравнения не получены ({ex.Message}) — сравнение с целым днём.", "WARNING");
        }
        finally
        {
            _yesterdayLoading = false;
        }
    }
}
