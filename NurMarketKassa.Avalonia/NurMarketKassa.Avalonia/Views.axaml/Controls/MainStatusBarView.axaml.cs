using Avalonia.Controls;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

public partial class MainStatusBarView : UserControl
{
    public MainStatusBarView()
    {
        InitializeComponent();
        // 2026-10-25, ТЗ «оприходование в фоне» (Q5): значок «в фоне: N» после значка очереди продаж (скрыт, пока приходов в фоне нет).
        if (Content is StackPanel bar && BackgroundReceiptsBadge.Create() is { } badge)
            bar.Children.Insert(Math.Min(3, bar.Children.Count), badge);
    }
}
