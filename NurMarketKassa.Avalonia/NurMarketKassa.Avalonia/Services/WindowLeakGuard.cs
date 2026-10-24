using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-23, живой аудит (круг 5, утечка памяти программы владельца): закрытое окно с видимой «бегущей» полосой
/// загрузки (ProgressBar IsIndeterminate — у разделов и окон она зажигается на время запроса, у «Склада» — на время проведения
/// приёмки/ревизии/списания) оставалось в памяти навсегда. Стиль полосы запускает бесконечную анимацию, её держит общие часы
/// отрисовки (MediaContextClock), а анимация держит полосу, а та — всё окно со всеми таблицами и данными (~1–16 МБ на каждое
/// открытие раздела). Закрытие окна дерево не отсоединяет, поэтому анимация не останавливалась; прятать полосу (IsVisible=false)
/// тоже недостаточно. Проверено стендом со слабыми ссылками: после «IsIndeterminate = false» окно собирается сборщиком.
/// Здесь одно место на всю программу: окно закрылось — все полосы в нём перестают «бежать».</summary>
internal static class WindowLeakGuard
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => StopRunningBars(window));
    }

    internal static void StopRunningBars(Window window)
    {
        try
        {
            foreach (var bar in window.GetVisualDescendants().OfType<ProgressBar>())
                if (bar.IsIndeterminate)
                    bar.SetCurrentValue(ProgressBar.IsIndeterminateProperty, false);
        }
        catch
        {
            // закрытие окна важнее: не вышло остановить полосу — окно просто останется до конца работы, как раньше
        }
    }
}
