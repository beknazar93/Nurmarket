using Avalonia;
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

        // 2026-10-22 (модалки, пакет 0): MaxHeight — высота ВНУТРИ рамки; у окна с рамкой ОС заголовок и края
        // добавляются сверху, и низ окна уходил под панель задач. Вычитаем их.
        window.MaxHeight = Math.Max(minHeight, workingHeight - margin - ChromeSize(window).Height);
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
        if (OperatingSystem.IsAndroid())
            return; // 2026-10-04: на Android размер окна задаёт WindowLayerHost (см. FitToKassaScreen)
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null)
            return;

        var workingWidth = screen.WorkingArea.Width / screen.Scaling;
        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        if (workingWidth <= 0 || workingHeight <= 0)
            return;

        // 2026-10-22 (модалки, пакет 0): размеры окна — без рамки ОС; вычитаем заголовок и края, иначе окно
        // с рамкой выше рабочей области на 30–40 px и низ с кнопками — под панелью задач.
        var chrome = ChromeSize(window);
        var maxWidth = Math.Max(window.MinWidth, workingWidth - margin - chrome.Width);
        var maxHeight = Math.Max(window.MinHeight, workingHeight - margin - chrome.Height);

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
        // 2026-10-04, Android (владелец: «клавиатура открывается — модалка закрывается и больше не открывается»):
        // здесь окну ставились точка и предельный размер по «экрану»; на Android окно с заданной точкой
        // становится «окном в точке экрана», и при открытии клавиатуры (экран ниже) оно уезжало из вида, оставаясь
        // открытым и модальным — касса больше ничего не открывала. На Android размер и место окна задаёт
        // WindowLayerHost (по центру, в пределах вида, и с клавиатурой тоже).
        if (OperatingSystem.IsAndroid())
            return;
        var screen = KassaScreen(window);
        if (screen is null)
            return;

        var scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
        var area = screen.WorkingArea;
        var workingWidth = area.Width / scaling;
        var workingHeight = area.Height / scaling;
        if (workingWidth <= 0 || workingHeight <= 0)
            return;

        // 2026-10-22 (модалки, пакет 0): Width/Height/MaxHeight окна — без рамки ОС. У окна с системной рамкой
        // (≈35 окон: весы, редакторы, сотрудники, Настройки/Продажи/Прокат) заголовок и края добавлялись сверху —
        // низ с кнопками уходил под панель задач на 15–40 px (стенд модалок, отчёт «Сенсорный моноблок» п. 5.2).
        // Теперь вся рамка окна (заголовок + края) помещается в рабочую область минус отступ.
        var chrome = ChromeSize(window);
        var maxWidth = Math.Max(320, workingWidth - margin - chrome.Width);
        var maxHeight = Math.Max(240, workingHeight - margin - chrome.Height);
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
        // 2026-10-22 (модалки, пакет 0): проверяем и ставим по центру всю рамку окна (с заголовком), а не только
        // его содержимое — Position у Windows — левый верхний угол рамки.
        var width = ((double.IsNaN(window.Width) ? window.Bounds.Width : window.Width) + chrome.Width) * scaling;
        var height = ((double.IsNaN(window.Height) ? window.Bounds.Height : window.Height) + chrome.Height) * scaling;
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

    /// <summary>2026-10-22 (модалки, пакет 0): сколько добавляет к окну рамка ОС (заголовок и края), в единицах
    /// Avalonia. Окно без рамки (SystemDecorations.None — все модалки на ModalShell) — 0, поведение у них прежнее.
    /// Окно уже показано — точная разница FrameSize − ClientSize; до показа (или если система её не сообщает,
    /// Linux/X11) — заголовок 32 + края 8 по высоте и 16 по ширине (Windows 10/11 при 100–150 %).</summary>
    internal static Size ChromeSize(Window window)
    {
        try
        {
            if (window.SystemDecorations == SystemDecorations.None || window.WindowState == WindowState.FullScreen)
                return default;
            var client = window.ClientSize;
            if (window.FrameSize is { } frame && client.Width > 0 && client.Height > 0
                && frame.Height > client.Height && frame.Width >= client.Width)
            {
                // Разумный предел: чужие числа (окно ещё не измерено) не должны «съесть» пол-экрана.
                return new Size(Math.Min(frame.Width - client.Width, 40), Math.Min(frame.Height - client.Height, 80));
            }
            if (window.SystemDecorations == SystemDecorations.BorderOnly || window.ExtendClientAreaToDecorationsHint)
                return new Size(16, 8);
            return new Size(16, 40);
        }
        catch
        {
            return default;
        }
    }

    private static bool _registered;
    private static WindowIcon? _linuxWindowIcon;

    /// <summary>2026-09-30, просьба владельца «на некоторых экранах окна модалки выходят за рамки
    /// экрана»: подгонка вызывалась только в 7 окнах из 104. Теперь — один раз на запуск: КАЖДОЕ
    /// открывающееся обычное окно (не развёрнутое, не главное, не экран покупателя) ужимается до
    /// рабочей области экрана кассы и ставится по центру, если вылезает за край. Окна, которые
    /// уже зовут FitToKassaScreen сами, это не портит — повторный вызов ничего не меняет.</summary>
    public static void RegisterForAllWindows()
    {
        if (_registered)
            return;
        _registered = true;
#if !NURANDROID
        ModalWindowManager.Register(); // 2026-10-30: модалки всегда спереди, фон блюрится (см. ModalWindowManager)
#endif
        // 2026-10-29: на Linux (X11) у окон не было значка (в Windows его берёт сам exe, на Mac — пакет .app) — окна кассы
        // в панели задач и в Alt+Tab были с безликим значком.
        if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
        {
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
            {
                try
                {
                    if (window.Icon is null)
                    {
                        _linuxWindowIcon ??= new WindowIcon(new Avalonia.Media.Imaging.Bitmap(
                            Avalonia.Platform.AssetLoader.Open(new Uri("avares://NurMarketKassa.Avalonia/Assets/app-logo.png"))));
                        window.Icon = _linuxWindowIcon;
                    }
                }
                catch
                {
                    // значок — украшение, окно без него работает
                }
            });
        }
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            try
            {
                if (window.WindowState != WindowState.Normal)
                    return;
                var name = window.GetType().Name;
                if (name.Contains("CustomerDisplay", StringComparison.Ordinal) || name is "SplashWindow" or "MainWindow" or "OwnerShellWindow")
                    return;
                var main = (Avalonia.Application.Current?.ApplicationLifetime
                    as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                if (ReferenceEquals(main, window))
                    return;
                // 2026-10-27: «Полный экран» — окна разделов (Склад, Закупки, Клиенты…) открываются развёрнутыми, а не окном 1200×780.
                if (NurMarketKassa.AvaloniaHost.Services.FullscreenHelper.TryApplySectionWindowMode(window))
                    return;
                window.FitToKassaScreen();
                // 2026-10-23: окно с SizeToContent растёт вниз, когда внутри появляется ошибка/подсказка, а позиция остаётся —
                // подвал с кнопками уходил под панель задач (жалоба владельца). Каждый рост возвращает окно в рабочую область.
                window.SizeChanged += (w, _) => KeepInsideWorkingArea((Window)w!);
            }
            catch (Exception ex)
            {
                NurMarketKassa.Services.PosLogger.Log($"Подгонка окна {window.GetType().Name} под экран: {ex.Message}", "WARNING");
            }
        });
    }

    /// <summary>2026-10-23: если нижний край обычного окна ушёл за рабочую область экрана (после роста по содержимому) —
    /// поднимает окно, но не выше верхнего края рабочей области.</summary>
    private static void KeepInsideWorkingArea(Window window)
    {
        try
        {
            if (window.WindowState != WindowState.Normal || !window.IsVisible)
                return;
            var screen = window.Screens.ScreenFromWindow(window) ?? KassaScreen(window);
            if (screen is null)
                return;
            var scale = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
            var heightPx = (int)Math.Ceiling((window.FrameSize ?? window.ClientSize).Height * scale);
            var area = screen.WorkingArea;
            var bottom = window.Position.Y + heightPx;
            var overflow = bottom - (area.Y + area.Height);
            if (overflow <= 0)
                return;
            var newY = Math.Max(area.Y, window.Position.Y - overflow);
            if (newY != window.Position.Y)
                window.Position = new Avalonia.PixelPoint(window.Position.X, newY);
        }
        catch
        {
            // позиция — удобство, не ошибка
        }
    }

    /// <summary>Экран главного окна кассы (или программы владельца); если его не определить —
    /// экран владельца окна, затем основной. 2026-10-16: internal — им пользуется и ModalShell.</summary>
    internal static Avalonia.Platform.Screen? KassaScreen(Window window)
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
