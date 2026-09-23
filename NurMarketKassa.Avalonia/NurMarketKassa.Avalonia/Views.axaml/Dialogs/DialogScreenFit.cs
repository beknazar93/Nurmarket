using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// SizeToContent="Height" (or a fixed Height) sizes a dialog to its full desired height
/// regardless of the actual monitor — on small/low-res POS screens (compact all-in-one
/// terminals) that pushes bottom buttons off-screen. Cap the window to the real working area.
/// </summary>
internal static class DialogScreenFit
{
    public static void ClampToScreenHeight(this Window window, double minHeight = 360, double margin = 24)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null)
            return;

        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        if (workingHeight <= 0)
            return;

        window.MaxHeight = Math.Max(minHeight, workingHeight - margin);
    }

    /// <summary>2026-09-21, живой баг владельца (фото маленького квадратного POS-экрана):
    /// большие окна (Склад, Настройки, Редактор этикеток и т.п.) заданы в XAML фиксированным
    /// Width/Height, рассчитанным на обычный монитор — на компактном моноблоке-кассе это
    /// обрезает часть окна за физическим краем экрана (например, колонку "Действия" в таблице
    /// или кнопку "Сохранить" внизу), в отличие от главного окна кассы (MainWindow), которое
    /// открывается на весь экран (WindowState="Maximized") и использует резиновую раскладку.
    /// Подгоняет заданные в XAML Width/Height под реальную рабочую область экрана, если они её
    /// превышают — окно становится компактнее, но целиком помещается на экран; то, что всё
    /// равно не влезло внутри (например широкая таблица), по-прежнему доступно через штатную
    /// прокрутку.</summary>
    public static void FitToScreen(this Window window, double margin = 24)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null)
            return;

        var workingWidth = screen.WorkingArea.Width / screen.Scaling;
        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        if (workingWidth <= 0 || workingHeight <= 0)
            return;

        var maxWidth = Math.Max(window.MinWidth, workingWidth - margin);
        var maxHeight = Math.Max(window.MinHeight, workingHeight - margin);

        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;

        if (double.IsNaN(window.Width) || window.Width > maxWidth)
            window.Width = maxWidth;
        if (double.IsNaN(window.Height) || window.Height > maxHeight)
            window.Height = maxHeight;
    }
}
