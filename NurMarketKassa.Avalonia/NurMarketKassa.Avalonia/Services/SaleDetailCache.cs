using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Детали уже проведённых чеков для отчётов («Финансы», «Продажи», отчёт смены).
///
/// 2026-09-25: каждое открытие отчёта заново скачивало с сервера каждый чек периода, и
/// несколько окон подряд упирались в ограничение частоты запросов NurCRM — на эти секунды
/// сервер отказывал всей кассе. Строки проведённого чека не меняются (возврат приходит
/// отдельной записью), поэтому скачанный чек запоминается до закрытия кассы, а новые идут
/// через общий темп массовых загрузок (<see cref="ApiThrottle.RunBulkAsync{T}"/>).
///
/// Только для отчётов: оплата, возврат и долги читают чек напрямую — им нужно свежее
/// состояние, а не строки.</summary>
public static class SaleDetailCache
{
    private const int MaxEntries = 5000;
    private static readonly ConcurrentDictionary<string, JsonElement> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<JsonElement> GetAsync(string saleId, CancellationToken ct = default)
    {
        if (Cache.TryGetValue(saleId, out var cached))
            return cached;

        var detail = await ApiThrottle.RunBulkAsync(() => App.SalesApi.PosSaleGetAsync(saleId, ct), ct).ConfigureAwait(false);
        if (detail.ValueKind == JsonValueKind.Object)
        {
            if (Cache.Count >= MaxEntries)
                Cache.Clear();
            Cache[saleId] = detail;
        }

        return detail;
    }

    /// <summary>Чек изменился (возврат по нему) — следующий отчёт возьмёт его с сервера.</summary>
    /// <summary>То же, что <see cref="GetAsync"/>, но с повтором при временной ошибке (нет ответа,
    /// 5xx, 429). 2026-09-25: при сверке «Финансов» за месяц с сайтом 81 чек из 461 не дочитался,
    /// и их себестоимость выпала из прибыли — прибыль вышла завышенной (41 % против 30,5 %).
    /// Ошибки «нет доступа/не найден» не повторяются.</summary>
    public static async Task<JsonElement> GetWithRetryAsync(string saleId, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await GetAsync(saleId, ct).ConfigureAwait(false);
            }
            catch (ApiException ex) when (attempt < 3 && (ex.StatusCode is null or 429 or >= 500))
            {
                var pause = ApiThrottle.RemainingBlock;
                if (pause < TimeSpan.FromSeconds(attempt))
                    pause = TimeSpan.FromSeconds(attempt);
                await Task.Delay(pause, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Коротко для журнала: код ответа и текст, а не только тип исключения.</summary>
    public static string Describe(Exception ex) =>
        ex is ApiException api ? $"ApiException {api.StatusCode?.ToString() ?? "—"}: {api.Message}" : $"{ex.GetType().Name}: {ex.Message}";

    public static void Forget(string? saleId)
    {
        if (!string.IsNullOrWhiteSpace(saleId))
            Cache.TryRemove(saleId, out _);
    }
}
