using System.Text.Json;

namespace NurMarketKassa.Services.Fiscal;

/// <summary>Итоги одного прохода очереди.</summary>
public sealed record FiscalQueueRunSummary(int Fiscalized, int WrittenToServer, int Failed, int Waiting, bool Skipped, string? SkipReason)
{
    public static FiscalQueueRunSummary Skip(string reason) => new(0, 0, 0, 0, true, reason);
}

/// <summary>2026-11-14: проход по очереди фискальных чеков. Порядок для каждого чека: (1) ККМ ещё не приняла — провайдер пробивает (ключ повтора неизменен); (2) ККМ приняла,
/// сервер NurCRM ещё не знает — пишем реквизиты (POST …/fiscal/). Каркас: реальный провайдер подставит eKassa (Ф1); без провайдера (<see cref="NotConnectedFiscalProvider"/>) проход
/// ничего не делает и ничего не считает ошибкой. Касса пробивает ТОЛЬКО в режиме kassa (<see cref="FiscalSettings.KassaMustFiscalize"/>): при server и off проход пропускается.
/// В сеть eKassa здесь ничего не уходит — только через переданный провайдер; на сервер NurCRM — только через переданный <paramref name="writer"/>.</summary>
public sealed class FiscalQueueRunner
{
    /// <summary>Запись реквизитов на сервер NurCRM (обычно <see cref="FiscalApi.WriteAsync"/> с клиентом кассы).</summary>
    public delegate Task<FiscalWriteResult> ServerWriter(FiscalDocKind kind, string docId, FiscalRequisites requisites, CancellationToken ct);

    private readonly FiscalQueueStore _store;
    private readonly Func<IFiscalProvider> _provider;
    private readonly ServerWriter _writer;
    private readonly Func<bool> _mustFiscalize;

    public FiscalQueueRunner(FiscalQueueStore store, ServerWriter writer, Func<IFiscalProvider>? provider = null, Func<bool>? mustFiscalize = null)
    {
        _store = store;
        _writer = writer;
        _provider = provider ?? (() => FiscalProviders.Current);
        _mustFiscalize = mustFiscalize ?? (() => FiscalSettings.KassaMustFiscalize);
    }

    /// <summary>Один проход. Не в режиме kassa (server — сервер пробивает сам и запись реквизитов отвечает 409 fiscal_mode_server; off — фискализации нет) проход пропускается целиком.</summary>
    public async Task<FiscalQueueRunSummary> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_mustFiscalize())
            return FiscalQueueRunSummary.Skip("mode");
        var provider = _provider();
        int fiscalized = 0, written = 0, failed = 0, waiting = 0;

        foreach (var entry in _store.All())
        {
            ct.ThrowIfCancellationRequested();
            var current = entry;

            if (current.Status == FiscalStatus.Pending)
            {
                if (!provider.IsConnected)
                {
                    waiting++; // ККМ не подключена — чек ждёт, попытка не считается
                    continue;
                }
                var request = ReadRequest(current);
                if (request is null)
                {
                    _store.RecordResult(current.Key, FiscalResult.Fail("request_missing", Tr.T("Нет данных чека для ККМ — пробейте вручную или удалите запись.",
                        "ККМ үчүн чектин маалыматы жок — кол менен бекитиңиз же жазууну өчүрүңүз.", "No receipt data for the cash register — fiscalize manually or delete the entry.",
                        "ÖKC için fiş verisi yok — elle kesin veya kaydı silin.", "KKM uchun chek ma'lumoti yo'q — qo'lda uring yoki yozuvni o'chiring."), retryable: false));
                    failed++;
                    continue;
                }
                FiscalResult result;
                try
                {
                    result = current.Kind == FiscalDocKind.Return
                        ? await provider.ReturnAsync(request, ct).ConfigureAwait(false)
                        : await provider.SaleAsync(request, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"Фискальная очередь: провайдер {provider.Name} вернул сбой по чеку {current.ReceiptNumber}: {ex.GetType().Name}", "WARNING");
                    result = FiscalResult.Fail("provider_error", ex.Message, retryable: true);
                }
                _store.RecordResult(current.Key, result);
                if (!result.Success)
                {
                    if (result.Retryable) waiting++; else failed++;
                    continue;
                }
                fiscalized++;
                current = _store.FindByKey(current.Key) ?? current;
            }

            if (current.Status == FiscalStatus.Accepted && !current.ServerWritten && current.ServerProblem is null && current.Requisites is { } requisites)
            {
                FiscalWriteResult write;
                try
                {
                    write = await _writer(current.Kind, current.DocId, requisites, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    write = new FiscalWriteResult(FiscalWriteOutcome.Unavailable, ex.Message);
                }
                if (write.IsDone)
                {
                    _store.MarkServerWritten(current.Key);
                    written++;
                }
                else if (write.RetryLater)
                    waiting++;
                else
                {
                    // Отказ без надежды на повтор (fiscal_already_set, return_voided, fiscal_mode_server, validation_error, 404): чек уже пробит в ККМ — реквизиты НЕ теряем,
                    // записываем причину, человек решает (в «Требует внимания»).
                    _store.MarkServerProblem(current.Key, write.Outcome + ": " + write.Message);
                    failed++;
                }
            }
        }
        return new FiscalQueueRunSummary(fiscalized, written, failed, waiting, false, null);
    }

    private static FiscalReceiptRequest? ReadRequest(FiscalQueueEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.RequestJson))
            return null;
        try { return JsonSerializer.Deserialize<FiscalReceiptRequest>(entry.RequestJson); }
        catch (JsonException) { return null; }
    }

    /// <summary>Запрос к ККМ → JSON для записи в очередь.</summary>
    public static string SerializeRequest(FiscalReceiptRequest request) => JsonSerializer.Serialize(request);
}
