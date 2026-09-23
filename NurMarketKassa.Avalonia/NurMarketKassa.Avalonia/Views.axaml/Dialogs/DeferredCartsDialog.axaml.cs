using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public enum DeferredRestoreMode
{
    ReplaceCurrentCart,
    MergeIntoCurrentCart,
}

public partial class DeferredCartsDialog : Window
{
    private readonly DeferredCartsDialogActions? _actions;
    private bool _busy;

    public IReadOnlyList<DeferredCartEntry> EntriesToRestore { get; private set; } = [];
    public DeferredRestoreMode RestoreMode { get; private set; } = DeferredRestoreMode.ReplaceCurrentCart;

    public DeferredCartsDialog() : this(null) { }

    public DeferredCartsDialog(DeferredCartsDialogActions? actions)
    {
        _actions = actions;
        InitializeComponent();

        // Динамически обновляем кнопки при клике по элементам списка
        CartListBox.SelectionChanged += (_, _) => UpdateButtonsState();

        ReloadList();
    }

    private void UpdateButtonsState()
    {
        var hasSelection = CartListBox.SelectedItems?.Count > 0;
        DeleteSelectedButton.IsEnabled = hasSelection && !_busy;
        MergeIntoCurrentButton.IsEnabled = hasSelection && !_busy;
        LoadAsSeparateButton.IsEnabled = (CartListBox.SelectedItems?.Count == 1) && !_busy; // Открыть отдельным можно только 1 чек
    }

    private void ReloadList()
    {
        CartListBox.Items.Clear();
        var items = DeferredCartsStore.LoadAll().OrderByDescending(x => x.SavedAt).ToList();
        SummaryText.Text = items.Count == 0
            ? "Очередь пуста."
            : $"В очереди: {items.Count} чек(ов). Последний: {items[0].SavedAt.LocalDateTime:g}.";

        foreach (var e in items)
            CartListBox.Items.Add(new DeferredCartListRow(e));

        UpdateButtonsState();
    }

    private static int CountLines(string cartJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(cartJson) ? "{}" : cartJson);
            return CartDisplayHelper.EnumerateItems(doc.RootElement).Count();
        }
        catch
        {
            return 0;
        }
    }

    private List<DeferredCartListRow> GetSelectedRows() =>
        CartListBox.SelectedItems?.OfType<DeferredCartListRow>().ToList() ?? [];

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DeleteSelectedButton.IsEnabled = !busy;
        MergeIntoCurrentButton.IsEnabled = !busy && CartListBox.Items.Count > 0;
        LoadAsSeparateButton.IsEnabled = !busy && CartListBox.Items.Count > 0;
        CartListBox.IsEnabled = !busy;
    }

    private void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            PosMessageBox.Show(this, "Выберите строки в списке.", "Отложенные",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DeferredCartsStore.RemoveIds(rows.Select(r => r.Entry.Id));
        ReloadList();
    }

    private async void MergeIntoCurrent_Click(object? sender, RoutedEventArgs e)
    {
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            PosMessageBox.Show(this, "Выберите одну или несколько корзин.", "Отложенные",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_actions?.MergeIntoCurrentAsync != null)
        {
            SetBusy(true);
            try
            {
                var entries = rows.Select(r => r.Entry).ToList();
                if (await _actions.MergeIntoCurrentAsync(entries).ConfigureAwait(true))
                    Close(true);
                else
                    ReloadList();
            }
            finally
            {
                SetBusy(false);
                ReloadList();
            }

            return;
        }

        AcceptSelection(DeferredRestoreMode.MergeIntoCurrentCart);
    }

    private async void LoadAsSeparate_Click(object? sender, RoutedEventArgs e)
    {
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            PosMessageBox.Show(this, "Выберите корзину в списке.", "Отложенные",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (rows.Count > 1)
        {
            PosMessageBox.Show(this, "Открыть как отдельный чек можно только одну корзину за раз.",
                "Отложенные", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_actions?.OpenAsSeparateAsync != null)
        {
            SetBusy(true);
            try
            {
                if (await _actions.OpenAsSeparateAsync(rows[0].Entry).ConfigureAwait(true))
                    Close(true);
                else
                    ReloadList();
            }
            finally
            {
                SetBusy(false);
                ReloadList();
            }

            return;
        }

        AcceptSelection(DeferredRestoreMode.ReplaceCurrentCart);
    }

    private void AcceptSelection(DeferredRestoreMode mode)
    {
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            PosMessageBox.Show(this, "Выберите одну или несколько корзин.", "Отложенные",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RestoreMode = mode;
        EntriesToRestore = rows.Select(r => r.Entry).ToList();
        Close(true);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class DeferredCartListRow(DeferredCartEntry entry)
    {
        internal DeferredCartEntry Entry { get; } = entry;

        public override string ToString()
        {
            var n = CountLines(Entry.CartJson);
            return $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} поз.";
        }
    }
}
