using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using Xunit;
using Pointer = Avalonia.Input.Pointer;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-08, владелец: «Android (NurMarket владелец): сенсор откликается очень плохо».
/// (1) Порог начала прокрутки пальцем в шаблонах ScrollViewer и DataGrid поднимается с 5 до 8 точек (TouchScrollSlop) — короткое
/// касание с «дрожью» пальца остаётся нажатием, а не уходит в прокрутку.
/// (2) Отменённое системой касание (TouchCancel — без PointerReleased) больше не превращает следующее касание во «второй палец»:
/// после сброса из платформы (TouchGuard.ResetFromPlatform — Android вызывает его на ACTION_DOWN/ACTION_CANCEL) нажатие проходит.</summary>
[Collection("UiScenarios")]
public sealed class TouchResponsivenessTests
{
    private static readonly Type TouchGuardType =
        typeof(TouchScrollSlop).Assembly.GetType("NurMarketKassa.AvaloniaHost.Services.TouchGuard", throwOnError: true)!;

    private static bool _guardRegistered;

    private static void InvokeGuard(string method) =>
        TouchGuardType.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);

    [AvaloniaFact]
    public void ScrollViewer_and_DataGrid_get_8dp_touch_scroll_threshold()
    {
        var list = new StackPanel();
        for (var i = 0; i < 50; i++)
            list.Children.Add(new Button { Content = "Строка " + i, Height = 40 });
        var scroller = new ScrollViewer { Content = list, Height = 200 };
        var grid = new DataGrid { Height = 200, ItemsSource = Enumerable.Range(0, 50).Select(i => new { N = i }).ToList(), AutoGenerateColumns = true };
        var window = new Window { Width = 400, Height = 500, Content = new StackPanel { Children = { scroller, grid } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var presenters = window.GetVisualDescendants().OfType<InputElement>()
            .Where(e => e is ScrollContentPresenter || e is DataGridRowsPresenter)
            .Where(e => e.GestureRecognizers.OfType<ScrollGestureRecognizer>().Any())
            .ToList();
        presenters.Should().Contain(p => p is ScrollContentPresenter, "в шаблоне ScrollViewer есть распознаватель прокрутки пальцем");
        presenters.Should().Contain(p => p is DataGridRowsPresenter, "в шаблоне DataGrid есть распознаватель прокрутки пальцем");
        presenters.SelectMany(p => p.GestureRecognizers.OfType<ScrollGestureRecognizer>())
            .Should().OnlyContain(r => r.ScrollStartDistance < TouchScrollSlop.Distance, "по умолчанию Avalonia начинает прокрутку через 5 точек");

        foreach (var p in presenters)
            TouchScrollSlop.Apply(p).Should().BeGreaterThan(0);

        presenters.SelectMany(p => p.GestureRecognizers.OfType<ScrollGestureRecognizer>())
            .Should().OnlyContain(r => r.ScrollStartDistance == TouchScrollSlop.Distance);
        // Повторно — ничего не меняется (больший порог не уменьшается).
        presenters.Sum(p => TouchScrollSlop.Apply(p)).Should().Be(0);
        window.Close();
    }

    [AvaloniaFact]
    public void Cancelled_touch_does_not_swallow_next_tap_after_platform_reset()
    {
        if (!_guardRegistered)
        {
            InvokeGuard("Register");
            _guardRegistered = true;
        }
        var target = new Border { Width = 200, Height = 100, Background = Avalonia.Media.Brushes.Gray };
        var window = new Window { Width = 300, Height = 200, Content = target };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        InvokeGuard("ResetFromPlatform");

        // Палец 1 нажал, затем Android отменил касание (TouchCancel): PointerReleased не приходит.
        Press(window, target, new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true)).Should().BeFalse("первое касание — обычное нажатие");
        // Без сброса следующее касание считается «вторым пальцем» и глушится (так было: до 5 секунд кнопки молчали).
        Press(window, target, new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, false)).Should().BeTrue("палец 1 «висит» — это второй палец");

        // Android: ACTION_CANCEL / новое ACTION_DOWN — пальцев на экране нет, сбрасываем.
        InvokeGuard("ResetFromPlatform");
        ((bool)TouchGuardType.GetProperty("IsTouchActive")!.GetValue(null)!).Should().BeFalse();
        Press(window, target, new Pointer(Pointer.GetNextFreeId(), PointerType.Touch, true)).Should().BeFalse("после сброса касание снова нажимает");

        InvokeGuard("ResetFromPlatform");
        window.Close();
    }

    /// <summary>Нажатие пальцем; true — TouchGuard пометил его обработанным («второй палец»).</summary>
    private static bool Press(Window window, Control target, IPointer pointer)
    {
        var args = new PointerPressedEventArgs(target, pointer, window, new Point(50, 50), 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed), KeyModifiers.None);
        target.RaiseEvent(args);
        return args.Handled;
    }
}
