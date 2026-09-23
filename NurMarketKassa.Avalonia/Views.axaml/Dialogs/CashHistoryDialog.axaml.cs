using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using static NurMarketKassa.AvaloniaHost.Views.FinanceWindow;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class CashHistoryDialog : Window
{
    public ObservableCollection<CashSessionEntry> HistoryItems { get; } = new();

    public bool? DialogResult { get; set; }

    public CashHistoryDialog()
    {
        InitializeComponent();
        DataContext = this;
        EmptyHint.IsVisible = true;
    }

    public CashHistoryDialog(ObservableCollection<CashSessionEntry> allSessions, string? currentUserId)
        : this()
    {
        var filtered = string.IsNullOrWhiteSpace(currentUserId)
            ? allSessions.ToList()
            : allSessions.Where(s => s.UserId == currentUserId).ToList();

        LoadItems(filtered);
    }

    public CashHistoryDialog(IEnumerable<CashSessionEntry> sessions)
        : this()
    {
        LoadItems(sessions.ToList());
    }

    private void LoadItems(IReadOnlyList<CashSessionEntry> entries)
    {
        HistoryItems.Clear();
        foreach (var entry in entries)
            HistoryItems.Add(entry);

        EmptyHint.IsVisible = HistoryItems.Count == 0;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close(false);
    }
}
