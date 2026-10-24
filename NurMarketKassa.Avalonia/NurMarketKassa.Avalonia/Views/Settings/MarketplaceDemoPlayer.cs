using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-10-23, ТЗ «Маркетплейс — редизайн и медиа» (П-3, 3.7 п.4): проигрыватель анимации «Как это работает» (GIF).
// Avalonia GIF сама не играет, поэтому кадры раскладывает KnowledgeBaseWindow.ReadGifFrames (SkiaSharp) и сменяет таймер.
// Нагрузка на UI-поток: декодирование — в фоне; один DispatcherTimer, пока страница на экране и анимация не на паузе.
// Утечки: при уходе со страницы (DetachedFromVisualTree) таймер останавливается, отмена декодирования, кадры освобождаются.
// Одновременно играет одна анимация (static Active).

/// <summary>Проигрыватель GIF: рамка 16:9, кадр, пауза/продолжить и счётчик «3 / 10» внизу справа, цикл без конца.</summary>
internal sealed class MarketplaceDemoPlayer : MarketplaceAspectBox
{
    private static MarketplaceDemoPlayer? s_active;

    /// <summary>Сколько таймеров проигрывателя сейчас запущено (для проверки в стенде: после ухода со страницы должно быть 0).</summary>
    internal static int RunningTimers;

    private readonly string _uri;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly Border _skeleton = new();
    private readonly Border _controls = new();
    private readonly TextBlock _counter = new() { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly PathIcon _playIcon = new() { Width = 14, Height = 14, Foreground = Brushes.White, IsHitTestVisible = false };
    private readonly Border _playButton = new();
    private readonly DispatcherTimer _timer = new();
    private List<(Bitmap Frame, TimeSpan Delay)>? _frames;
    private CancellationTokenSource? _cts;
    private int _index;
    private bool _paused;
    private bool _timerRunning;

    /// <summary>Анимация не открылась — страница уберёт блок.</summary>
    public event Action? Failed;

    public MarketplaceDemoPlayer(string uri)
    {
        _uri = uri;
        MaxWidth = 720;
        HorizontalAlignment = HorizontalAlignment.Left;

        // Скелет того же размера, пока кадры декодируются.
        var skeletonIcon = MarketplaceDetailUi.PathIconOf("play-circle-outline", 48, "BrushTextSoft");
        skeletonIcon.HorizontalAlignment = HorizontalAlignment.Center;
        skeletonIcon.VerticalAlignment = VerticalAlignment.Center;
        _skeleton.Child = skeletonIcon;
        MarketplaceDetailUi.Bind(_skeleton, Border.BackgroundProperty, "BrushPanelSoft");

        _counter.Margin = new Thickness(2, 0, 8, 0);
        _playButton.Width = _playButton.Height = 28;
        _playButton.CornerRadius = new CornerRadius(14);
        _playButton.Background = Brushes.Transparent;
        _playButton.Child = _playIcon;
        _playIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _playIcon.VerticalAlignment = VerticalAlignment.Center;
        MarketplaceDetailUi.MakeClickable(_playButton, MarketplaceDetailUi.Word("pause"), TogglePause);
        _controls.CornerRadius = new CornerRadius(14);
        _controls.Background = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        _controls.Padding = new Thickness(4, 2, 4, 2);
        _controls.HorizontalAlignment = HorizontalAlignment.Right;
        _controls.VerticalAlignment = VerticalAlignment.Bottom;
        _controls.Margin = new Thickness(0, 0, 10, 10);
        _controls.IsVisible = false;
        _controls.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _playButton, _counter } };
        UpdatePlayIcon();

        var frame = new Border
        {
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            Child = new Grid { Children = { _skeleton, _image, _controls } },
        };
        MarketplaceDetailUi.Bind(frame, Border.BorderBrushProperty, "BrushBorder");
        Child = frame;

        _timer.Tick += (_, _) => Advance();
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => Stop();
    }

    // ------------------------------------------------------------------ загрузка

    private void Start()
    {
        if (s_active != null && s_active != this)
            s_active.Stop(); // одна анимация за раз
        s_active = this;
        if (_frames != null)
        {
            ResumeTimer();
            return;
        }

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var token = cts.Token;
        _ = LoadAsync(token);
    }

    private async Task LoadAsync(CancellationToken token)
    {
        List<(Bitmap Frame, TimeSpan Delay)>? frames = null;
        try
        {
            frames = await Task.Run(Decode);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: анимация «{_uri}» не декодировалась в фоне: {ex.Message}", "WARNING");
        }
        OnDecoded(frames, token);
    }

    private List<(Bitmap Frame, TimeSpan Delay)>? Decode()
    {
        using var stream = MarketplaceMedia.Source.Open(_uri);
        return stream is null ? null : KnowledgeBaseWindow.ReadGifFrames(stream);
    }

    private void OnDecoded(List<(Bitmap Frame, TimeSpan Delay)>? frames, CancellationToken token)
    {
        if (token.IsCancellationRequested || !this.IsAttachedToVisualTree())
        {
            DisposeFrames(frames);
            return;
        }

        if (frames == null)
        {
            // Фон не справился — повторяем на UI-потоке один раз (так делает база знаний).
            try
            {
                frames = Decode();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Маркетплейс: анимация «{_uri}» не открылась: {ex.Message}", "WARNING");
            }
        }

        if (frames == null || frames.Count == 0)
        {
            DisposeFrames(frames);
            Failed?.Invoke();
            return;
        }

        _frames = frames;
        _index = 0;
        _image.Source = frames[0].Frame;
        _skeleton.IsVisible = false;
        _controls.IsVisible = frames.Count > 1;
        UpdateCounter();
        ResumeTimer();
    }

    // ------------------------------------------------------------------ воспроизведение

    private void ResumeTimer()
    {
        if (_frames is not { Count: > 1 } frames || _paused || _timerRunning)
            return;
        _timer.Interval = frames[_index].Delay;
        _timer.Start();
        _timerRunning = true;
        Interlocked.Increment(ref RunningTimers);
    }

    private void StopTimer()
    {
        _timer.Stop();
        if (_timerRunning)
        {
            _timerRunning = false;
            Interlocked.Decrement(ref RunningTimers);
        }
    }

    private void Advance()
    {
        if (_frames is not { Count: > 1 } frames)
            return;
        _index = (_index + 1) % frames.Count; // цикл: после последнего кадра — снова первый
        _image.Source = frames[_index].Frame;
        _timer.Interval = frames[_index].Delay;
        UpdateCounter();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
            StopTimer();
        else
            ResumeTimer();
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon()
    {
        _playIcon.Data = MarketplaceDetailUi.Icon(_paused ? "play" : "pause");
        Avalonia.Automation.AutomationProperties.SetName(_playButton, MarketplaceDetailUi.Word(_paused ? "play" : "pause"));
        ToolTip.SetTip(_playButton, MarketplaceDetailUi.Word(_paused ? "play" : "pause"));
    }

    private void UpdateCounter()
    {
        if (_frames != null)
            _counter.Text = $"{_index + 1} / {_frames.Count}";
    }

    // ------------------------------------------------------------------ освобождение

    private void Stop()
    {
        StopTimer();
        _cts?.Cancel();
        _cts = null;
        if (s_active == this)
            s_active = null;
        var frames = _frames;
        _frames = null;
        _image.Source = null;
        _skeleton.IsVisible = true;
        _controls.IsVisible = false;
        // Освобождаем после отрисовки: последний кадр мог ещё быть в очереди на показ.
        MarketplaceDetailUi.PostBackground(() => DisposeFrames(frames));
    }

    private static void DisposeFrames(List<(Bitmap Frame, TimeSpan Delay)>? frames)
    {
        if (frames == null)
            return;
        foreach (var (frame, _) in frames)
            frame.Dispose();
    }
}
