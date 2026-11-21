using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.UiTests.Harness;
using Xunit;
using Pointer = Avalonia.Input.Pointer;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21, владелец: «На сенсорном экране ползунки не слушаются пальцев» (масштаб экрана, размер карточек, обрезка QR,
/// конструктор тем, экран покупателя, ценники). Причины: (1) прокрутка пальцем (ScrollGestureRecognizer в ScrollViewer страницы настроек)
/// забирает касание у ползунка, как только палец сдвинется на 5 точек по вертикали — ползунок получает «потерю захвата» и замирает;
/// (2) Fluent-ползунок слишком тонкий для пальца (строка 32 px, ползунок 20 px). Исправление — общий стиль Slider в AppControlStyles.axaml
/// (строка не ниже 44, ползунок 32) и TouchSliderGuard (касание ползунка не отдаётся прокрутке; тап по дорожке ставит значение).
/// Тесты проверяют размеры цели касания и отклик на касание/перетаскивание; жест прокрутки внутри ScrollViewer headless не воспроизводится — он проверяется чек-листом на настоящем сенсоре.</summary>
[Collection("UiScenarios")]
public sealed class TouchSliderUiTests
{
    /// <summary>Slider в вертикальной прокрутке (как страница настроек): сверху и снизу много содержимого, чтобы жест прокрутки был возможен.</summary>
    private static (Window window, Slider slider, ScrollViewer scroller) Build(Control? sliderHost = null)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, Value = 0, TickFrequency = 10, IsSnapToTickEnabled = true, Width = 400 };
        var page = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        for (var i = 0; i < 12; i++)
            page.Children.Add(new Border { Height = 60, Background = Avalonia.Media.Brushes.LightGray });
        page.Children.Add(sliderHost ?? slider);
        for (var i = 0; i < 12; i++)
            page.Children.Add(new Border { Height = 60, Background = Avalonia.Media.Brushes.LightGray });
        var scroller = new ScrollViewer { Content = page, Height = 500 };
        var window = new Window { Width = 520, Height = 520, Content = scroller };
        window.Show();
        UiShot.Pump();
        scroller.Offset = new Vector(0, 12 * 72 - 200); // ползунок ближе к середине окна
        UiShot.Pump();
        return (window, slider, scroller);
    }

    private static Point Centre(Window window, Visual v)
    {
        var p = v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), window);
        return p!.Value;
    }

    [AvaloniaFact]
    public void Slider_target_is_at_least_40px_for_finger()
    {
        var (window, slider, _) = Build();
        try
        {
            slider.Bounds.Height.Should().BeGreaterThanOrEqualTo(40, "строка ползунка — цель касания пальцем (Fluent по умолчанию 32)");
            var track = slider.GetVisualDescendants().OfType<Track>().Single();
            var tree = string.Join("; ", new[] { (Visual)slider }.Concat(slider.GetVisualDescendants()).Select(v => $"{v.GetType().Name}#{(v as Control)?.Name}={v.Bounds.Width:0}x{v.Bounds.Height:0}"));
            track.Bounds.Height.Should().BeGreaterThanOrEqualTo(40, "дорожка (область тапа и перетаскивания) не тоньше 40 px; дерево: " + tree);
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            thumb.Bounds.Width.Should().BeGreaterThanOrEqualTo(30, "ползунок крупнее штатных 20 px");
            thumb.Bounds.Height.Should().BeGreaterThanOrEqualTo(30);
            // Снимок для просмотра глазами (в сравнение не входит): %TEMP%/nurmarket-tests/ui-actual/touch-slider.png
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            using var shot = UiShot.Render(window);
            using var image = SkiaSharp.SKImage.FromBitmap(shot);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            File.WriteAllBytes(Path.Combine(dir, "touch-slider.png"), data.ToArray());
            Console.WriteLine($"touch-slider: slider {slider.Bounds.Width}x{slider.Bounds.Height}, track {track.Bounds.Width}x{track.Bounds.Height}, thumb {thumb.Bounds.Width}x{thumb.Bounds.Height}");
        }
        finally { window.Close(); }
    }

    /// <summary>Тап пальцем по дорожке (касание как событие нажатия с PointerType.Touch) ставит значение под пальцем.</summary>
    [AvaloniaFact]
    public void Finger_tap_on_track_sets_value()
    {
        NurMarketKassa.AvaloniaHost.Services.TouchSliderGuard.Register();
        var (window, slider, _) = Build();
        try
        {
            var track = slider.GetVisualDescendants().OfType<Track>().Single();
            var p = track.TranslatePoint(new Point(track.Bounds.Width * 0.75, track.Bounds.Height / 2), window)!.Value;
            var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true);
            var target = (Interactive)window.GetVisualAt(p)!;
            var args = new PointerPressedEventArgs(target, pointer, window, p, 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
            target.RaiseEvent(args);
            slider.Value.Should().BeInRange(60, 90, "тап пальцем по дорожке ставит значение под пальцем");
            var release = new PointerReleasedEventArgs(target, pointer, window, p, 0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased), KeyModifiers.None, MouseButton.Left);
            target.RaiseEvent(release);
        }
        finally { window.Close(); }
    }

    /// <summary>Перетаскивание ползунка (нажатие на Thumb, движение, отпускание) двигает значение — путь, общий для мыши и пальца.</summary>
    [AvaloniaFact]
    public void Thumb_drag_delta_moves_value()
    {
        var (window, slider, _) = Build();
        try
        {
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var start = Centre(window, thumb);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(new Point(start.X + 150, start.Y + 10));
            window.MouseUp(new Point(start.X + 150, start.Y + 10), MouseButton.Left);
            slider.Value.Should().BeGreaterThan(20);
        }
        finally { window.Close(); }
    }
}
