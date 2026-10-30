#if !NURANDROID
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-30, срочный баг владельца («модалка уходит на 2 план и программа перестаёт нажиматься; модалки должны быть
/// ВСЕГДА спереди; задний фон заблюрить»). Единый механизм для всех модальных окон (ShowDialog), Windows/Linux/macOS:
/// 1) ведёт стек открытых модалок; верхняя модалка всегда поднимается и получает фокус, когда активируется любое другое
///    окно кассы (клик, Alt-Tab, возврат из свёрнутого, смена экранов, экран покупателя) — с лёгким «миганием»;
/// 2) окно-владелец для диалогов без явного владельца — верхняя модалка/активное окно, а не главное окно кассы
///    (раздел поверх главного окна закрывал диалог, открытый «от главного»);
/// 3) Topmost владельца переносится на модалку;
/// 4) пока модалка открыта, всё остальное содержимое окон кассы размывается (BlurEffect) и слегка затемняется (слой в
///    OverlayLayer окна), 140 мс; в режиме слабого устройства — только затемнение;
/// 5) страховка: если окно осталось заблокированным (Windows), а модалки нет — оно разблокируется.
/// Окна выбора файла, подсказки (Popup), экран покупателя, всплывающие уведомления и окна «поверх всех» не затрагиваются.</summary>
internal static class ModalWindowManager
{
    private static readonly List<Window> Modals = new();
    private static readonly Dictionary<Window, Backdrop> Backdrops = new();
    private static readonly HashSet<Window> Hooked = new();
    private static readonly HashSet<Window> Tracked = new(); // 2026-10-30: окна без ShowDialog, которые должны вести себя как модалки (см. Track)
    private static readonly HashSet<Window> Suspended = new(); // 2026-10-30: Tracked-окна, временно не модальные (см. SetSuspended)
    private static bool _blurSlow; // 2026-10-30: отрисовка с размытием оказалась медленной (программный рендер) — до конца сеанса только затемнение
    private static int _slowFrames;
    private static readonly FieldInfo? DialogField = typeof(Window).GetField("_showingAsDialog", BindingFlags.NonPublic | BindingFlags.Instance);
    private static bool _registered;
    private static Window? _lastActive;
    private static DispatcherTimer? _frameTimer;
    private static DispatcherTimer? _watchTimer;
    private static readonly HashSet<IntPtr> DisabledSeen = new();
    private const double FadeMs = 140;
    private static long _lastFrame;

    public static void Register()
    {
        if (_registered)
            return;
        _registered = true;
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => OnOpened(window));
        _watchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchTimer.Tick += (_, _) => Watch();
        _watchTimer.Start();
    }

    /// <summary>Окно для нового диалога: верхняя открытая модалка, иначе активное окно кассы, иначе null (вызывающий возьмёт главное).</summary>
    public static Window? PreferredOwner()
    {
        try
        {
            Prune();
            if (Modals.Count > 0)
                return Modals[^1];
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var active = desktop.Windows.LastOrDefault(w => w.IsActive && w.IsVisible && !IsExempt(w));
                if (active is not null)
                    return active;
            }
            if (_lastActive is { IsVisible: true } last && last.WindowState != WindowState.Minimized && !IsExempt(last))
                return last;
        }
        catch
        {
            // выбор владельца — удобство; без него диалог откроется от главного окна, как раньше
        }
        return null;
    }

    public static bool IsOpenModal(Window w) => Modals.Contains(w);

    /// <summary>2026-10-30: окно, открытое через Show() (а не ShowDialog) и стоящее поверх остальных (вопрос «Разрешить доступ?»
    /// удалённой помощи — Topmost, без владельца), тоже считается модалкой: фон размывается и затемняется. Вызывать до Show().</summary>
    public static void Track(Window w, bool suspended = false)
    {
        Tracked.Add(w);
        // 2026-10-31 (приёмка 1.17.68, п. 3): окно «Тех поддержка» открывается НЕ модальным (иначе до сеанса касса размыта и не нажимается);
        // модальным оно становится только на время вопроса «Разрешить доступ?» (см. SetSuspended).
        if (suspended)
            Suspended.Add(w);
    }

    private static bool IsModal(Window w) => DialogField?.GetValue(w) is true;

    /// <summary>2026-10-30 (приёмка 1.17.65, блокер 2): модалкой считается только окно из ShowDialog или явно зарегистрированное через
    /// <see cref="Track"/> (окно «Тех поддержка NurCRM», «Некорректные чеки», вопрос «Разрешить доступ?»). Любое другое окно, открытое через
    /// Show(владелец) — «Приходы в фоне», разделы, CRM из ИИ, Воронки, Напоминания о долгах, печать штрихкодов, — НЕ модалка:
    /// фон не размывается и фокус не отбирается.</summary>
    private static bool IsOverlay(Window w) =>
        Tracked.Contains(w) && !Suspended.Contains(w) && w.WindowState != WindowState.Minimized; // 2026-10-30: свёрнутое/приостановленное — не модалка (приёмка 1.17.67, п. 7а)

    /// <summary>2026-10-30 (приёмка 1.17.67, п. 7а): окно из <see cref="Track"/> временно НЕ модалка — «Тех поддержка» во время сеанса:
    /// оператор и кассир работают в кассе, окно поддержки не должно размывать фон и перехватывать щелчки. true — снять с модальности,
    /// false — вернуть (если окно открыто, снова размывает фон).</summary>
    public static void SetSuspended(Window w, bool suspended)
    {
        // 2026-10-31 (приёмка 1.17.68, п. 3): закрытое окно (OnClosed снял его с Tracked) или незарегистрированное в Suspended не попадает —
        // раньше позднее SetSuspended(true) от окна, закрытого до конца проверки связи, оставляло его в списке навсегда.
        if (suspended && !Tracked.Contains(w))
            return;
        if (suspended ? !Suspended.Add(w) : !Suspended.Remove(w))
            return;
        if (suspended)
        {
            if (Modals.Remove(w))
            {
                NurMarketKassa.Services.PosLogger.Log($"Модалка приостановлена: {w.GetType().Name}, осталось {Modals.Count}.", "UI");
                Refresh();
            }
        }
        else if (Tracked.Contains(w) && w.IsVisible)
        {
            Classify(w);
        }
    }

    private static bool IsExempt(Window w)
    {
        var name = w.GetType().Name;
        return name.Contains("CustomerDisplay", StringComparison.Ordinal) || name is "SplashWindow" or "FrmKeyboard"
            || (w.Topmost && !Modals.Contains(w));
    }

    private static void OnOpened(Window window)
    {
        try
        {
            if (Hooked.Add(window))
            {
                window.Activated += (_, _) => OnActivated(window);
                window.Closed += (_, _) => OnClosed(window);
            }
            DialogDragHelper.AttachAuto(window); // 2026-10-30: окна без системной рамки двигаются за шапку («модалка не двигается!!!»)
            Classify(window);
            // Признак «показано как диалог» мог быть выставлен уже после события — проверяем ещё раз после показа.
            Dispatcher.UIThread.Post(() => Classify(window), DispatcherPriority.Send);
            Dispatcher.UIThread.Post(() => Classify(window), DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"ModalWindowManager: открытие окна {window.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    private static void Classify(Window window)
    {
        if (!window.IsVisible || Modals.Contains(window))
        {
            if (Modals.Count > 0)
                Refresh();
            return;
        }
        if (IsModal(window) || IsOverlay(window))
        {
            Modals.Add(window);
            if (OwnerChainTopmost(window))
                window.Topmost = true;
            NurMarketKassa.Services.PosLogger.Log($"Модалка открыта: {window.GetType().Name}, владелец {window.Owner?.GetType().Name ?? "нет"}, модалок {Modals.Count}.", "UI");
            Refresh();
            Raise(window, pulse: false);
        }
        else if (Modals.Count > 0)
        {
            Refresh(); // окно открылось под открытой модалкой — оно тоже фон
        }
    }

    private static bool OwnerChainTopmost(Window w)
    {
        for (var o = w.Owner as Window; o is not null; o = o.Owner as Window)
            if (o.Topmost)
                return true;
        return false;
    }

    private static void OnClosed(Window window)
    {
        Hooked.Remove(window);
        Tracked.Remove(window);
        Suspended.Remove(window);
        if (_lastActive == window)
            _lastActive = null;
        if (Modals.Remove(window))
        {
            NurMarketKassa.Services.PosLogger.Log($"Модалка закрыта: {window.GetType().Name}, осталось {Modals.Count}.", "UI");
            Refresh();
            if (Modals.Count > 0)
                // 2026-10-30 (приёмка 1.17.67, п. 7д): к моменту выполнения список мог опустеть (родитель закрыт сразу за дочерним) — [^1] падал.
                Dispatcher.UIThread.Post(() =>
                {
                    Prune();
                    if (Modals.Count > 0)
                        Raise(Modals[^1], pulse: false);
                }, DispatcherPriority.Background);
        }
        if (Backdrops.Remove(window, out var back))
            back.Dispose();
    }

    private static void OnActivated(Window window)
    {
        if (!IsExempt(window) && !Modals.Contains(window))
            _lastActive = window;
        // 2026-10-30: окно из Track развернули из свёрнутого — снова модалка (если не приостановлено).
        if (Tracked.Contains(window) && !Modals.Contains(window) && window.IsVisible)
            Classify(window);
        Prune();
        if (Modals.Count == 0)
            return;
        var top = Modals[^1];
        if (ReferenceEquals(window, top))
            return;
        // Активировалось другое окно (клик по заблокированному окну, Alt-Tab, возврат из свёрнутого) — модалка обратно наверх.
        Dispatcher.UIThread.Post(() =>
        {
            Prune();
            if (Modals.Count > 0)
                Raise(Modals[^1], pulse: !IsExempt(window));
            Refresh();
        }, DispatcherPriority.Background);
    }

    private static void Prune()
    {
        for (var i = Modals.Count - 1; i >= 0; i--)
        {
            var m = Modals[i];
            // 2026-10-30: свёрнутое окно из Track (не ShowDialog) — не модалка, сторож его не разворачивает (приёмка 1.17.67, п. 7а).
            if (!m.IsVisible || (m.WindowState == WindowState.Minimized && Tracked.Contains(m)))
            {
                Modals.RemoveAt(i);
                Refresh();
            }
        }
    }

    // ---- Подъём модалки ----

    private static void Raise(Window modal, bool pulse)
    {
        try
        {
            if (!modal.IsVisible)
                return;
            if (modal.WindowState == WindowState.Minimized)
            {
                if (Tracked.Contains(modal))
                    return; // 2026-10-30: окно из Track свернули сами — обратно не разворачиваем
                modal.WindowState = WindowState.Normal;
            }
            if (OwnerChainTopmost(modal) && !modal.Topmost)
                modal.Topmost = true;
            if (OperatingSystem.IsWindows() && modal.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero)
                Native.SetWindowPos(h, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            if (!modal.IsActive)
                modal.Activate();
            if (pulse)
                Pulse(modal);
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"ModalWindowManager: подъём {modal.GetType().Name}: {ex.Message}", "WARNING");
        }
    }

    /// <summary>Короткое «мигание» модалки: человек нажал на заблокированное окно — видит, где нужно ответить.</summary>
    private static void Pulse(Window modal)
    {
        if (modal.Opacity < 0.99)
            return;
        modal.Opacity = 0.86;
        DispatcherTimer.RunOnce(() => modal.Opacity = 1, TimeSpan.FromMilliseconds(90));
    }

    // ---- Размытие и затемнение фона ----

    /// <summary>Фон — все видимые окна кассы, кроме верхней модалки. Нижние модалки (вложенные диалоги) — тоже фон.</summary>
    private static void Refresh()
    {
        try
        {
            Window? top = Modals.Count > 0 ? Modals[^1] : null;
            var wanted = new HashSet<Window>();
            if (top is not null && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var w in desktop.Windows)
                    if (!ReferenceEquals(w, top) && w.IsVisible && w.WindowState != WindowState.Minimized && !IsExempt(w)
                        && !Modals.Any(m => !ReferenceEquals(m, w) && Covers(m, w)))
                        wanted.Add(w);
            }
            foreach (var w in wanted)
            {
                if (!Backdrops.TryGetValue(w, out var b))
                    Backdrops[w] = b = new Backdrop(w);
                b.Goal = 1;
            }
            foreach (var pair in Backdrops)
                if (!wanted.Contains(pair.Key))
                    pair.Value.Goal = 0;
            StartFrames();
        }
        catch (Exception ex)
        {
            NurMarketKassa.Services.PosLogger.Log($"ModalWindowManager: фон: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-10-30: окно <paramref name="top"/> целиком закрывает окно <paramref name="back"/> (раздел на весь экран) —
    /// размывать закрытое окно незачем (лишняя нагрузка на видеокарту). Окно с прозрачностью не закрывает.</summary>
    private static bool Covers(Window top, Window back)
    {
        try
        {
            if (top.Opacity < 0.99 || top.SystemDecorations == SystemDecorations.None && top.Background is null)
                return false;
            if (top.TransparencyLevelHint.Any(l => l == WindowTransparencyLevel.Transparent || l == WindowTransparencyLevel.AcrylicBlur))
                return false;
            var ts = (top.FrameSize ?? top.ClientSize) * top.RenderScaling;
            var bs = (back.FrameSize ?? back.ClientSize) * back.RenderScaling;
            if (ts.Width <= 0 || bs.Width <= 0)
                return false;
            return top.Position.X <= back.Position.X + 1 && top.Position.Y <= back.Position.Y + 1
                   && top.Position.X + ts.Width + 1 >= back.Position.X + bs.Width
                   && top.Position.Y + ts.Height + 1 >= back.Position.Y + bs.Height;
        }
        catch
        {
            return false;
        }
    }

    private static void StartFrames()
    {
        if (_frameTimer is { IsEnabled: true })
            return;
        _frameTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _lastFrame = System.Diagnostics.Stopwatch.GetTimestamp();
        _frameTimer.Tick -= Frame;
        _frameTimer.Tick += Frame;
        _frameTimer.Start();
    }

    private static void Frame(object? sender, EventArgs e)
    {
        // Шаг — по реальному времени между кадрами (таймер на слабом ПК опаздывает), чтобы появление/снятие шли ~140 мс.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var dt = Math.Clamp((now - _lastFrame) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, 1, 100);
        _lastFrame = now;
        var step = dt / FadeMs;
        // 2026-10-30: замер кадра. Размытие на программном рендере (старая видеокарта) может тормозить — три кадра подряд
        // дольше 90 мс при идущем появлении/снятии = отключаем размытие до конца сеанса, остаётся затемнение.
        if (!_blurSlow && Backdrops.Values.Any(b => b.HasBlur))
        {
            _slowFrames = dt > 90 ? _slowFrames + 1 : 0;
            if (_slowFrames >= 3)
            {
                _blurSlow = true;
                foreach (var b in Backdrops.Values)
                    b.DisableBlur();
                NurMarketKassa.Services.PosLogger.Log("ModalWindowManager: кадры с размытием медленные (>90 мс) — фон только затемняется.", "WARNING");
            }
        }
        var busy = false;
        foreach (var pair in Backdrops.ToList())
        {
            var b = pair.Value;
            if (b.Step(step))
                busy = true;
            if (b.Done)
            {
                Backdrops.Remove(pair.Key);
                b.Dispose();
            }
        }
        if (!busy)
            _frameTimer?.Stop();
    }

    private sealed class Backdrop
    {
        private readonly Window _window;
        private readonly Control? _content;
        private readonly IEffect? _originalEffect;
        private bool _blur;
        private readonly double _radius;
        private readonly Border? _dim;
        private OverlayLayer? _layer;
        private readonly double _dimAlpha;
        private double _level;
        private bool _disposed;

        public bool HasBlur => _blur;

        public void DisableBlur()
        {
            if (_blur && _content is not null)
                _content.Effect = _originalEffect;
            _blur = false;
        }

        public double Goal { get; set; } = 1;
        public bool Done => _disposed || (Goal == 0 && _level <= 0);

        public Backdrop(Window window)
        {
            _window = window;
            var low = NurMarketKassa.Services.UserPreferences.Instance.LowPerformanceMode;
            _content = window.Content as Control;
            _originalEffect = _content?.Effect;
            // Блюр только если у содержимого нет своего эффекта и устройство не слабое; иначе — одно затемнение.
            _blur = !low && !_blurSlow && _content is not null && _originalEffect is null;
            // 2026-10-30: большое окно (пикселей много) — радиус 4, меньше работы для видеокарты/процессора; обычное — 6.
            _radius = window.ClientSize.Width * window.ClientSize.Height * window.RenderScaling * window.RenderScaling > 2_200_000 ? 4 : 6;
            var dark = window.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            _dimAlpha = low ? (dark ? 0.55 : 0.45) : (dark ? 0.42 : 0.32);
            try
            {
                _layer = OverlayLayer.GetOverlayLayer(window);
                if (_layer is not null)
                {
                    _dim = new Border
                    {
                        Background = Brushes.Black,
                        Opacity = 0,
                        IsHitTestVisible = true,
                        Cursor = new Cursor(StandardCursorType.Arrow),
                    };
                    _dim.PointerPressed += OnDimPressed;
                    SyncSize();
                    window.SizeChanged += OnWindowSizeChanged;
                    _layer.Children.Add(_dim);
                }
            }
            catch
            {
                _dim = null;
            }
        }

        private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => SyncSize();

        private void SyncSize()
        {
            if (_dim is null)
                return;
            var size = _layer is { Bounds.Width: > 0 } l ? l.Bounds.Size : _window.ClientSize;
            _dim.Width = Math.Max(0, size.Width);
            _dim.Height = Math.Max(0, size.Height);
        }

        private void OnDimPressed(object? sender, PointerPressedEventArgs e)
        {
            e.Handled = true;
            if (Modals.Count > 0)
                Raise(Modals[^1], pulse: true);
        }

        /// <summary>Один шаг анимации; true — ещё не закончили.</summary>
        public bool Step(double step)
        {
            if (_disposed)
                return false;
            _level = Goal > _level ? Math.Min(1, _level + step) : Math.Max(0, _level - step);
            if (_dim is not null)
            {
                if (_dim.Width <= 0 || _dim.Height <= 0)
                    SyncSize();
                _dim.Opacity = _level * _dimAlpha;
            }
            if (_blur && _content is not null)
                _content.Effect = _level <= 0 ? _originalEffect : new BlurEffect { Radius = _level * _radius };
            return _level != Goal;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (_blur && _content is not null)
                    _content.Effect = _originalEffect;
                _window.SizeChanged -= OnWindowSizeChanged;
                if (_dim is not null)
                {
                    _dim.PointerPressed -= OnDimPressed;
                    _layer?.Children.Remove(_dim);
                }
            }
            catch
            {
                // окно уже закрыто
            }
        }
    }

    // ---- Страховка и проверка ----

    private static void Watch()
    {
        try
        {
            Prune();
            if (Modals.Count > 0)
            {
                var top = Modals[^1];
                var appActive = Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d
                                && d.Windows.Any(w => w.IsActive);
                // Наше окно активно, а модалка нет — поднимаем (чужое приложение на переднем плане не трогаем).
                if (appActive && !top.IsActive)
                    Raise(top, pulse: false);
                return;
            }
            if (Backdrops.Count > 0)
                Refresh();
            if (OperatingSystem.IsWindows())
                UnlockStuckWindows();
        }
        catch
        {
            // сторож не должен ничего ронять
        }
    }

    /// <summary>Windows: окно осталось заблокированным (его модалка закрылась мимо Avalonia), а модалок нет — «программа не нажимается».
    /// Разблокируем после двух проверок подряд. Окно выбора файла системы (передний план — не окно Avalonia нашего процесса) не трогаем.</summary>
    private static void UnlockStuckWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        var known = new HashSet<IntPtr>();
        foreach (var w in desktop.Windows)
            if (w.TryGetPlatformHandle()?.Handle is { } h && h != IntPtr.Zero)
                known.Add(h);
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && !known.Contains(fg))
        {
            Native.GetWindowThreadProcessId(fg, out var pid);
            if (pid == (uint)Environment.ProcessId)
            {
                DisabledSeen.Clear();
                return; // системное окно нашего процесса (выбор файла) — владелец и должен быть заблокирован
            }
        }
        // 2026-10-30 (приёмка 1.17.67, п. 7г): человек ушёл в другую программу, пока открыт выбор файла, — передний план чужой, и сторож снимал
        // блокировку (можно было нажать «Загрузить» второй раз). Системное диалоговое окно (#32770) нашего процесса есть — ничего не трогаем.
        if (HasOwnSystemDialog(known))
        {
            DisabledSeen.Clear();
            return;
        }
        foreach (var w in desktop.Windows)
        {
            if (!w.IsVisible || w.WindowState == WindowState.Minimized || w.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
                continue;
            if (Native.IsWindowEnabled(hwnd))
            {
                DisabledSeen.Remove(hwnd);
                continue;
            }
            if (!DisabledSeen.Add(hwnd))
            {
                Native.EnableWindow(hwnd, true);
                DisabledSeen.Remove(hwnd);
                NurMarketKassa.Services.PosLogger.Log($"Окно {w.GetType().Name} осталось заблокированным без модалки — разблокировано.", "WARNING");
            }
        }
    }

    /// <summary>В процессе есть видимое системное диалоговое окно (класс #32770: выбор файла, окно сообщения), которого нет среди окон Avalonia.</summary>
    private static bool HasOwnSystemDialog(HashSet<IntPtr> known)
    {
        var found = false;
        var pidSelf = (uint)Environment.ProcessId;
        var sb = new System.Text.StringBuilder(64);
        try
        {
            Native.EnumWindows((h, _) =>
            {
                if (known.Contains(h) || !Native.IsWindowVisible(h))
                    return true;
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pid != pidSelf)
                    return true;
                sb.Clear();
                Native.GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == "#32770")
                {
                    found = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // не получилось перечислить окна — работаем как раньше
        }
        return found;
    }

    private static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool EnableWindow(IntPtr hWnd, bool enable);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    }
}
#endif
