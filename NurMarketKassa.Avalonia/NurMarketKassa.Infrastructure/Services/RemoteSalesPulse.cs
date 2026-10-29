using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-29, «продажа с кассы появляется в Сводке владельца через 60–100 секунд, остатки на складе — до 15 минут».
/// Сервер пока не присылает событий (Б-102 в ТЗ: WebSocket/SSE), поэтому каждые ~20 с один лёгкий запрос «последние 5 чеков компании»
/// (один запрос, ~1 КБ) сравнивается с прошлым ответом. Изменилось — значит появилась продажа/возврат/отмена на другой кассе, сайте или в
/// телефоне: поднимается <see cref="PosDataEvents.RaiseSalesChanged"/> (сводка, «Продажи», «Финансы», ABC перечитываются сами; кэш отчётов сбрасывается)
/// и <see cref="Detected"/> (кэш каталога перечитывает остатки, не чаще <see cref="SyncCenter.StockReloadMinSpacing"/>).
/// Свои продажи тоже меняют ответ: если остаток на этом ПК менялся за последние 25 с — это скорее всего своя продажа, каталог не перечитывается
/// (остаток уже уменьшен сразу), сигнал для отчётов всё равно уходит.
/// Бюджет запросов: 3 в минуту; пауза при 429/аварии/работе без связи; при сбоях интервал растёт вдвое, до 4×; разброс ±15 %.
/// </summary>
public static class RemoteSalesPulse
{
    /// <summary>Появились изменения в чеках компании. Аргумент: true — скорее всего это своя продажа (остаток здесь только что менялся).</summary>
    public static event Action<bool>? Detected;

    private static int _started;
    private static string? _lastFingerprint;
    private static int _failures;

    /// <summary>Запустить пульс (один раз за запуск). Идёт, только пока есть вход в NurCRM и не автономный режим.</summary>
    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _ = Task.Run(LoopAsync);
    }

    private static async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                var interval = TimeSpan.FromTicks(SyncCenter.SalesPulseInterval.Ticks * (1L << Math.Min(_failures, 2)));
                await Task.Delay(SyncCenter.Jitter(interval)).ConfigureAwait(false);
                if (SyncCenter.ShouldPause() || PosApp.SalesApi is not { } sales)
                    continue;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var rows = await sales.PosSalesListAsync(1, 5, null, cts.Token).ConfigureAwait(false);
                _failures = 0;
                SyncCenter.RecordSuccess("sales");
                var fingerprint = Fingerprint(rows);
                var previous = _lastFingerprint;
                _lastFingerprint = fingerprint;
                if (previous is null || previous == fingerprint)
                    continue; // первый ответ — просто запоминаем
                var own = StockSyncService.LocalActivityWithin(TimeSpan.FromSeconds(25));
                PosLogger.Log($"SYNC: в чеках компании есть изменения ({(own ? "похоже на свою продажу" : "чужая продажа или возврат")}).", "SYNC");
                PosDataEvents.RaiseSalesChanged();
                try { Detected?.Invoke(own); }
                catch (Exception ex) { PosLogger.Log($"SYNC: обработчик пульса продаж не отработал: {ex.Message}", "WARNING"); }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _failures = Math.Min(_failures + 1, 2);
                PosLogger.Log($"SYNC: пульс продаж не удался ({ex.GetType().Name}: {ex.Message}).", "SYNC");
            }
        }
    }

    private static string Fingerprint(List<JsonElement> rows)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var row in rows)
            sb.Append(row.GetRawText()).Append('\n');
        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
