using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-10-25, ТЗ «оприходование в фоне» (Q5): значок «в фоне: N» / «ошибка: K» рядом со строкой состояния кассы и
/// в шапке программы владельца. Скрыт, когда в очереди приходов пусто. Нажатие открывает окно «Приходы в фоне».
/// Собран кодом (без XAML), цвета — кисти темы (светлая и тёмная), как у значка очереди продаж в строке состояния.
/// Подписка на очередь — пока значок на экране (отписка при уходе из дерева, без утечек); обновление не чаще раза
/// в 250 мс, события очереди приходят не с UI-потока.</summary>
public sealed class BackgroundReceiptsBadge : StackPanel
{
    private readonly Border _active = Pill("BrushWarning"); // 2026-11-01 (Д-05): белый на BrushUiStatusWarn — 1,67:1 в тёмной; BrushWarning + BrushOnWarning ≥ 4,5:1
    private readonly Border _failed = Pill("BrushDangerFill"); // 2026-11-01 (Д-05): белый на BrushDanger в тёмной — 3,6:1, DangerFill — 4,8:1
    private readonly TextBlock _activeText;
    private readonly TextBlock _failedText;
    private bool _refreshPending;
    private bool _subscribed;

    /// <summary>2026-11-06 (Б-6): компактный вид — без связи на значке только «↑ N», полный текст «Неотправленных приходов: N · …» в подсказке.
    /// Шапка программы владельца: длинный значок налезал на «Сводку» и дату при масштабе Windows 125/150 %.</summary>
    private readonly bool _compact;

    /// <summary>Создать значок (окна ставят его рядом с кнопкой/в строку состояния).</summary>
    public static Control? Create(bool compact = false) => new BackgroundReceiptsBadge(compact);

    public BackgroundReceiptsBadge(bool compact = false)
    {
        _compact = compact;
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Hand);
        Background = Brushes.Transparent;
        IsVisible = false;
        _activeText = (TextBlock)_active.Child!;
        _failedText = (TextBlock)_failed.Child!;
        Children.Add(_active);
        Children.Add(_failed);
        PointerReleased += OnPointerReleased;
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        ApplyTexts();
    }

    private static Border Pill(string brushKey)
    {
        var text = new TextBlock { FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        if (brushKey == "BrushWarning")
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushOnWarning"));
        var pill = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4), MinHeight = 24, VerticalAlignment = VerticalAlignment.Center,
            Child = text, IsVisible = false,
        };
        pill.Bind(Border.BackgroundProperty, pill.GetResourceObservable(brushKey));
        return pill;
    }

    private void Subscribe()
    {
        if (_subscribed)
            return;
        _subscribed = true;
        BackgroundReceivingQueue.Instance.JobChanged += OnJobChanged;
        PendingProductQueue.Instance.Changed += OnProductsChanged; // 2026-11-06: новые товары, заведённые без интернета, — тот же значок
        ServerOutageMonitor.StateChanged += OnLinkChanged; // 2026-11-06: подпись «Неотправленных приходов» меняется вместе со связью
        Tr.LanguageChanged += OnLanguageChanged;
        ApplyTexts();
        Refresh();
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;
        _subscribed = false;
        BackgroundReceivingQueue.Instance.JobChanged -= OnJobChanged;
        PendingProductQueue.Instance.Changed -= OnProductsChanged;
        ServerOutageMonitor.StateChanged -= OnLinkChanged;
        Tr.LanguageChanged -= OnLanguageChanged;
    }

    private void OnJobChanged(ReceivingJob _) => Dispatcher.UIThread.Post(Schedule);

    private void OnProductsChanged() => Dispatcher.UIThread.Post(Schedule);

    private void OnLinkChanged(ServerLinkState from, ServerLinkState to) => Dispatcher.UIThread.Post(Schedule);

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() => { ApplyTexts(); Refresh(); });

    /// <summary>Прореживание 250 мс: пачка переходов статуса — одно обновление.</summary>
    private void Schedule()
    {
        if (_refreshPending || !_subscribed)
            return;
        _refreshPending = true;
        DispatcherTimer.RunOnce(() =>
        {
            _refreshPending = false;
            if (_subscribed)
                Refresh();
        }, TimeSpan.FromMilliseconds(250));
    }

    private void ApplyTexts() =>
        ToolTip.SetTip(this, Tr.T("Неотправленное (приходы в фоне и др.) — нажмите, чтобы открыть список", "Жөнөтүлбөгөндөр (фондогу кирүүлөр ж.б.) — тизмени ачуу үчүн басыңыз",
            "Unsent (background receivings and more) — click to open the list", "Gönderilmeyenler (arka plandaki mal kabulleri vb.) — listeyi açmak için tıklayın", "Yuborilmaganlar (fondagi kirimlar va boshqalar) — ro'yxatni ochish uchun bosing"));

    /// <summary>Перечитать счётчики. Вызывать с UI-потока.</summary>
    public void Refresh()
    {
        int n, k, p, pk;
        try
        {
            var queue = BackgroundReceivingQueue.Instance;
            n = queue.ActiveCount;
            k = queue.FailedCount;
            p = PendingProductQueue.Instance.ActiveCount; // 2026-11-06: новые товары, ещё не созданные на сервере
            pk = PendingProductQueue.Instance.FailedCount;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Значок «в фоне»: не удалось прочитать очередь: {ex.Message}", "WARNING");
            return;
        }

        // 2026-11-06: офлайн-приход — пока нет связи, значок честно говорит «Неотправленных приходов: N».
        var total = n + p;
        var failedTotal = k + pk;
        var offlineText = string.Join(" · ", new[] { n > 0 ? BackgroundReceivingQueue.UnsentText(n) : null, p > 0 ? PendingProductQueue.UnsentText(p) : null }.Where(s => s is not null));
        var noLink = BackgroundReceivingQueue.NoConnectionNow;
        _activeText.Text = noLink
            ? (_compact ? "↑ " + total : offlineText)
            : Tr.T($"в фоне: {total}", $"фондо: {total}", $"in background: {total}", $"arka planda: {total}", $"fonda: {total}");
        _failedText.Text = Tr.T($"ошибка: {failedTotal}", $"ката: {failedTotal}", $"error: {failedTotal}", $"hata: {failedTotal}", $"xato: {failedTotal}");
        if (_compact)
        {
            ApplyTexts();
            if (noLink && offlineText.Length > 0)
                ToolTip.SetTip(this, offlineText + "\n" + ToolTip.GetTip(this));
        }
        _active.IsVisible = total > 0;
        _failed.IsVisible = failedTotal > 0;
        IsVisible = total > 0 || failedTotal > 0;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Handled = true;
        // 2026-11-06: значок открывает единое окно «Неотправленное» (приходы в фоне, новые товары и всё остальное, что не ушло на сервер); «Приходы в фоне» — кнопкой там.
        OutboxWindow.ShowOrActivate(TopLevel.GetTopLevel(this) as Window);
    }
}
