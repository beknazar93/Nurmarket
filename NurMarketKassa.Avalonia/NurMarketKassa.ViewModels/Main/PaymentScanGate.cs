namespace NurMarketKassa.ViewModels.Main;

/// <summary>2026-10-27 (аудит кассы, С-4): очередь сканов чека с отдельным флагом «идёт оплата».
/// Раньше сканы шли в чек даже во время оплаты (общий IsBusy не держал их): товар попадал в оплачиваемый чек, а сброс IsBusy
/// сканом посреди оплаты открывал кнопку «Оплатить» второй раз. Теперь, пока оплата удержана (<see cref="Hold"/>), коды копятся
/// в очереди и не обрабатываются; после оплаты (<see cref="Release"/>) они уходят в чек по порядку — в новый чек следующего
/// покупателя, а при отмене оплаты — в тот же чек. Ни один скан не теряется и не дублируется.</summary>
public sealed class PaymentScanGate
{
    private readonly Queue<string> _pending = new();
    private readonly object _lock = new();
    private bool _loopRunning;
    private bool _held;

    /// <summary>Сколько кодов ждёт обработки.</summary>
    public int Count { get { lock (_lock) return _pending.Count; } }

    /// <summary>Оплата идёт — сканы только копятся.</summary>
    public bool IsHeld { get { lock (_lock) return _held; } }

    /// <summary>Код в очередь. true — вызывающий должен запустить цикл обработки (цикл не работает и оплаты нет).</summary>
    public bool Enqueue(string code)
    {
        lock (_lock)
        {
            _pending.Enqueue(code);
            if (_loopRunning || _held)
                return false;
            _loopRunning = true;
            return true;
        }
    }

    /// <summary>Следующий код для цикла обработки. false — очередь пуста или идёт оплата: цикл завершается (флаг цикла снят).</summary>
    public bool TryTakeNext(out string code)
    {
        lock (_lock)
        {
            if (_pending.Count == 0 || _held)
            {
                _loopRunning = false;
                code = "";
                return false;
            }
            code = _pending.Dequeue();
            return true;
        }
    }

    /// <summary>Оплата началась: новые и ещё не взятые сканы ждут.</summary>
    public void Hold()
    {
        lock (_lock)
            _held = true;
    }

    /// <summary>Оплата закончилась. true — в очереди есть коды и цикл не работает: вызывающий должен его запустить.</summary>
    public bool Release()
    {
        lock (_lock)
        {
            _held = false;
            if (_pending.Count == 0 || _loopRunning)
                return false;
            _loopRunning = true;
            return true;
        }
    }
}
