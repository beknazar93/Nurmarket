using System.Net.Http;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-29: отправка операций без связи (возврат, оплата долга) — после чеков очереди. Сами правила — OfflineOperationsReplayer; здесь только
/// проводка в проход синхронизации и объявление аварии сервера, если связь оборвалась посреди отправки.
/// </summary>
public sealed partial class SyncService
{
    private async Task ReplayOfflineOperationsAsync(CancellationToken ct)
    {
        try
        {
            if (!OfflineOperationsStore.HasPending)
                return;
            var outcome = await OfflineOperationsReplayer.RunAsync(new SalesOfflineOpsApi(_sales), ct).ConfigureAwait(false);
            if (outcome.Sent > 0)
            {
                Interlocked.Add(ref _replayedCount, outcome.Sent);
                PosLogger.Log($"OFFLINE ops: отправлено операций {outcome.Sent}, ждут {outcome.Waiting}, на проверке {outcome.Review}.", "OFFLINE");
            }

            if (outcome.Failure is { } failure)
            {
                // Обрыв связи или недоступность сервера посреди отправки — как при досылке чеков: следующие операции не бьются в лежащий сервер.
                // Ошибка запроса БЕЗ причины от сокета/TLS — не авария и не «Офлайн» (так же, как в SyncBatchAsync): операция просто повторится.
                if (failure is not HttpRequestException { InnerException: null })
                {
                    if (failure is HttpRequestException)
                        IsOnline = false;
                    ServerOutageMonitor.ReportFailure(ReplayContext, failure, hard: true);
                }
            }

            UpdateStatusText();
            RaiseStateChanged();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"OFFLINE ops: проход отправки операций не завершён: {ex.Message}", "WARNING");
        }
    }
}
