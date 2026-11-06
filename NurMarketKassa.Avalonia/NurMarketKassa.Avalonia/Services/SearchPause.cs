using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06 (В11, шаг 7, п. 26.2/26.3): пауза после последней введённой буквы поиска (по умолчанию 220 мс). Фильтр списка на тысячи строк раньше запускался
/// на КАЖДУЮ букву; теперь — один раз, когда владелец перестал печатать. Таймер живёт в окне; <see cref="Dispose"/> (при закрытии окна) останавливает его,
/// чтобы фильтр не сработал по уже закрытому разделу.</summary>
public sealed class SearchPause : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _apply;

    public SearchPause(Action apply, int milliseconds = 220)
    {
        _apply = apply;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _apply();
        };
    }

    /// <summary>Ещё одна буква: отсчёт паузы начинается заново.</summary>
    public void Trigger()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Применить сразу, если пауза ещё идёт (например, по Enter); иначе ничего.</summary>
    public void Flush()
    {
        if (!_timer.IsEnabled)
            return;
        _timer.Stop();
        _apply();
    }

    /// <summary>Отменить ожидающее применение (фильтр уже применён другим путём).</summary>
    public void Cancel() => _timer.Stop();

    public void Dispose() => _timer.Stop();
}
