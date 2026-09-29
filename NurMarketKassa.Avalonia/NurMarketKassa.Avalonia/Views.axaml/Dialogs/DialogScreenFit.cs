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

    /// <summary>2026-09-29, живой случай клиента «Алтымыш ата» (сенсорный монитор + экран покупателя):
    /// окно «Поиск весов в сети» (1280×760, минимум 960×520) было шире экрана — обрезалось слева и
    /// справа вместе с кнопками «Использовать для…», а часть окна вылезала на второй монитор (экран
    /// покупателя). FitToScreen здесь не помогал: он брал экран, на котором окно уже оказалось (при
    /// окне шире экрана это мог быть и экран покупателя), и не опускал MinWidth/MinHeight — окно с
    /// минимумом больше экрана всё равно оставалось шире него.
    ///
    /// Этот вариант: экран — тот, где главное окно кассы (кассир работает на нём; экран покупателя
    /// им не бывает), иначе экран владельца окна, иначе основной; минимум опускается до рабочей
    /// области, размер ограничивается ею, и если окно хоть краем выходит за этот экран — оно
    /// ставится по его центру. Вызывать в Opened/Loaded.</summary>
    public static void FitToKassaScreen(this Window window, double margin = 16)
    {
        var screen = KassaScreen(window);
        if (screen is null)
            return;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var area = screen.WorkingArea;
        var workingWidth = area.Width / scaling;
        var workingHeight = area.Height / scaling;
        if (workingWidth <= 0 || workingHeight <= 0)
            return;

        var maxWidth = Math.Max(320, workingWidth - margin);
        var maxHeight = Math.Max(240, workingHeight - margin);
        if (window.MinWidth > maxWidth)
            window.MinWidth = maxWidth;
        if (window.MinHeight > maxHeight)
            window.MinHeight = maxHeight;
        window.MaxWidth = maxWidth;
        window.MaxHeight = maxHeight;
        if (!double.IsNaN(window.Width) && window.Width > maxWidth)
            window.Width = maxWidth;
        if (!double.IsNaN(window.Height) && window.Height > maxHeight)
            window.Height = maxHeight;

        // Окно целиком на экране кассы — оставляем там, где его поставил CenterOwner.
        var width = (double.IsNaN(window.Width) ? window.Bounds.Width : window.Width) * scaling;
        var height = (double.IsNaN(window.Height) ? window.Bounds.Height : window.Height) * scaling;
        if (width <= 0 || height <= 0)
            return;
        var pos = window.Position;
        var inside = pos.X >= area.X && pos.Y >= area.Y
                     && pos.X + width <= area.X + area.Width + 1
                     && pos.Y + height <= area.Y + area.Height + 1;
        if (inside)
            return;
        window.Position = new Avalonia.PixelPoint(
            area.X + (int)Math.Max(0, (area.Width - width) / 2),
            area.Y + (int)Math.Max(0, (area.Height - height) / 2));
    }

    /// <summary>Экран главного окна кассы (или программы владельца); если его не определить —
    /// экран владельца окна, затем основной.</summary>
    private static Avalonia.Platform.Screen? KassaScreen(Window window)
    {
        try
        {
            var main = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
            if (main is not null && !ReferenceEquals(main, window) && main.WindowState != WindowState.Minimized
                && window.Screens.ScreenFromWindow(main) is { } mainScreen)
                return mainScreen;
            if (window.Owner is Window owner && window.Screens.ScreenFromWindow(owner) is { } ownerScreen)
                return ownerScreen;
            return window.Screens.Primary ?? window.Screens.ScreenFromWindow(window);
        }
        catch
        {
            return window.Screens.Primary;
        }
    }
}
