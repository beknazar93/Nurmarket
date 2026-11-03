using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-03 (С-01, аудит «зависание при медленном сервере»): «Ждём ответ сервера NurCRM… N с» и «Отменить ожидание» для окон вне ModalShell
/// (главное окно, склад, программа владельца). Окно даёт два действия: показать строку статуса (текст или null — убрать) и показать/спрятать свою кнопку
/// «Отменить ожидание» (по клику — <see cref="Cancel"/>). Вызов сервера — <see cref="RunAsync{T}"/>: не дольше <see cref="ServerAnswerWait.ForegroundBudget"/> (8 с)
/// с последнего ответа сервера, через 3 с без ответа появляется строка с секундами. Таймер — DispatcherTimer (UI не блокируется).
/// Запись и деньги этим классом НЕ ждут: для них ServerAnswerWait.ForegroundWriteAsync.</summary>
public sealed class ServerWaitIndicator : IDisposable
{
    private readonly Action<string?> _setStatus;
    private readonly Action<bool>? _setCancelVisible;
    private CancellationTokenSource? _cts;
    private DispatcherTimer? _timer;
    private DateTime _shownAtUtc;
    private bool _disposed;

    public ServerWaitIndicator(Action<string?> setStatus, Action<bool>? setCancelVisible = null)
    {
        _setStatus = setStatus;
        _setCancelVisible = setCancelVisible;
    }

    /// <summary>Ждём сервер прямо сейчас (запрос идёт).</summary>
    public bool IsWaiting => _cts is { IsCancellationRequested: false };

    /// <summary>«Отменить ожидание»: возвращает управление окну, текущий вызов бросает OperationCanceledException.</summary>
    public void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Серверный вызов окна (чтение) со сроком 8 с и надписью. Отмена кнопкой — OperationCanceledException (вызывающий выходит молча);
    /// сервер не ответил за срок — <see cref="ServerNotAnsweringException"/> (вызывающий пишет «Сервер отвечает медленно…»).</summary>
    public async Task<T> RunAsync<T>(string context, Func<CancellationToken, Task<T>> operation, CancellationToken windowCancel = default)
    {
        Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(windowCancel);
        _cts = cts;
        try
        {
            return await ServerAnswerWait.ForegroundAsync(context, operation, cts.Token, OnSlow).ConfigureAwait(true);
        }
        finally
        {
            Hide();
            if (ReferenceEquals(_cts, cts))
                _cts = null;
        }
    }

    /// <summary>Обработчик <c>onSlow</c> для <c>ServerAnswerWait.ForegroundAsync</c> (вызывается из любого потока).</summary>
    public void OnSlow(bool slow)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Apply(slow);
        else
            Dispatcher.UIThread.Post(() => Apply(slow));
    }

    private void Apply(bool slow)
    {
        if (_disposed)
            return;
        if (!slow)
        {
            Hide();
            return;
        }

        _shownAtUtc = DateTime.UtcNow - ServerAnswerWait.SlowNoticeAfter;
        _setCancelVisible?.Invoke(true);
        Update();
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= Timer_Tick;
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var seconds = Math.Max(1, (int)(DateTime.UtcNow - _shownAtUtc).TotalSeconds);
        _setStatus(Tr.T(
            $"Ждём ответ сервера NurCRM… {seconds} с",
            $"NurCRM серверинин жообу күтүлүүдө… {seconds} с",
            $"Waiting for the NurCRM server… {seconds} s",
            $"NurCRM sunucusunun yanıtı bekleniyor… {seconds} sn",
            $"NurCRM server javobi kutilmoqda… {seconds} s"));
    }

    private void Hide()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            _setStatus(null);
            _setCancelVisible?.Invoke(false);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                _setStatus(null);
                _setCancelVisible?.Invoke(false);
            });
        }
    }

    /// <summary>Подпись кнопки «Отменить ожидание».</summary>
    public static string CancelText => Tr.T("Отменить ожидание", "Күтүүнү токтотуу", "Stop waiting", "Beklemeyi bırak", "Kutishni to'xtatish");

    /// <summary>Текст для окна, когда сервер не ответил за срок (чтение): «Сервер NurCRM отвечает медленно. Попробуйте ещё раз чуть позже.»</summary>
    public static string SlowText => ServerAnswerWait.SlowMessage;

    /// <summary>Текст для записи/денег, когда за срок ответа нет, но запрос НЕ отменён (<see cref="ForegroundResult{T}.Pending"/>).</summary>
    public static string WritePendingText => Tr.T(
        "Сервер отвечает медленно. Операция не отменена — касса дождётся ответа сама. Повторно не нажимайте.",
        "Сервер жай жооп берип жатат. Операция жокко чыгарылган жок — касса жоопту өзү күтөт. Кайра баспаңыз.",
        "The server is responding slowly. The operation was not cancelled — the register will wait for the answer itself. Do not press again.",
        "Sunucu yavaş yanıt veriyor. İşlem iptal edilmedi — kasa yanıtı kendisi bekleyecek. Tekrar basmayın.",
        "Server sekin javob bermoqda. Amal bekor qilinmadi — kassa javobni o'zi kutadi. Qayta bosmang.");

    public void Dispose()
    {
        _disposed = true;
        Cancel();
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
        }
    }
}
