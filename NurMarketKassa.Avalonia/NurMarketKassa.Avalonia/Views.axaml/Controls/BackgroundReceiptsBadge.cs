using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-10-25, ТЗ «оприходование в фоне» (Q5): значок «в фоне: N» / «ошибка: K» рядом со строкой состояния кассы и
/// в шапке программы владельца. Скрыт, когда в очереди приходов пусто. Нажатие открывает окно «Приходы в фоне».
/// Собран кодом (без XAML), цвета — кисти темы (светлая и тёмная), как у значка очереди продаж в строке состояния.
/// Подписка на очередь — пока значок на экране (отписка при уходе из дерева, без утечек); обновление не чаще раза
/// в 250 мс, события очереди приходят не с UI-потока.</summary>
public sealed class BackgroundReceiptsBadge : StackPanel
{
    private readonly Border _active = Pill("BrushUiStatusWarn");
    private readonly Border _failed = Pill("BrushDanger");
    private readonly TextBlock _activeText;
    private readonly TextBlock _failedText;
    private bool _refreshPending;
    private bool _subscribed;

    /// <summary>Создать значок (окна ставят его рядом с кнопкой/в строку состояния).</summary>
    public static Control? Create() => new BackgroundReceiptsBadge();

    public BackgroundReceiptsBadge()
    {
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
        Tr.LanguageChanged -= OnLanguageChanged;
    }

    private void OnJobChanged(ReceivingJob _) => Dispatcher.UIThread.Post(Schedule);

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
        ToolTip.SetTip(this, Tr.T("Приходы в фоне — нажмите, чтобы открыть список", "Фондогу кирүүлөр — тизмени ачуу үчүн басыңыз",
            "Background receivings — click to open the list", "Arka plandaki mal kabulleri — listeyi açmak için tıklayın", "Fondagi kirimlar — ro'yxatni ochish uchun bosing"));

    /// <summary>Перечитать счётчики. Вызывать с UI-потока.</summary>
    public void Refresh()
    {
        int n, k;
        try
        {
            var queue = BackgroundReceivingQueue.Instance;
            n = queue.ActiveCount;
            k = queue.FailedCount;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Значок «в фоне»: не удалось прочитать очередь: {ex.Message}", "WARNING");
            return;
        }

        _activeText.Text = Tr.T($"в фоне: {n}", $"фондо: {n}", $"in background: {n}", $"arka planda: {n}", $"fonda: {n}");
        _failedText.Text = Tr.T($"ошибка: {k}", $"ката: {k}", $"error: {k}", $"hata: {k}", $"xato: {k}");
        _active.IsVisible = n > 0;
        _failed.IsVisible = k > 0;
        IsVisible = n > 0 || k > 0;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Handled = true;
        BackgroundReceiptsWindow.ShowOrActivate(TopLevel.GetTopLevel(this) as Window);
    }
}
