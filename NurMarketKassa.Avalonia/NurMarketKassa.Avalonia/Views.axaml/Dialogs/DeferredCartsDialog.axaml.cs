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
            ? Tr.T("Очередь пуста.", "Кезек бош.", "The queue is empty.", "Kuyruk boş.", "Navbat bo'sh.")
            : Tr.T($"В очереди: {items.Count} чек(ов). Последний: {items[0].SavedAt.LocalDateTime:g}.", $"Кезекте: {items.Count} чек. Акыркысы: {items[0].SavedAt.LocalDateTime:g}.", $"In queue: {items.Count} receipt(s). Latest: {items[0].SavedAt.LocalDateTime:g}.", $"Kuyrukta: {items.Count} fiş. Sonuncusu: {items[0].SavedAt.LocalDateTime:g}.", $"Navbatda: {items.Count} ta chek. Oxirgisi: {items[0].SavedAt.LocalDateTime:g}.");

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
            PosMessageBox.Show(this, Tr.T("Выберите строки в списке.", "Тизмеден саптарды тандаңыз.", "Select rows in the list.", "Listeden satırları seçin.", "Ro'yxatdan qatorlarni tanlang."), Tr.T("Отложенные", "Калтырылган себеттер", "Held carts", "Bekleyen sepetler", "Kechiktirilgan savatlar"),
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
            PosMessageBox.Show(this, Tr.T("Выберите одну или несколько корзин.", "Бир же бир нече себетти тандаңыз.", "Select one or more carts.", "Bir veya birden fazla sepet seçin.", "Bitta yoki bir nechta savatni tanlang."), Tr.T("Отложенные", "Калтырылган себеттер", "Held carts", "Bekleyen sepetler", "Kechiktirilgan savatlar"),
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
            PosMessageBox.Show(this, Tr.T("Выберите корзину в списке.", "Тизмеден себетти тандаңыз.", "Select a cart in the list.", "Listeden bir sepet seçin.", "Ro'yxatdan savatni tanlang."), Tr.T("Отложенные", "Калтырылган себеттер", "Held carts", "Bekleyen sepetler", "Kechiktirilgan savatlar"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (rows.Count > 1)
        {
            PosMessageBox.Show(this, Tr.T("Открыть как отдельный чек можно только одну корзину за раз.", "Өзүнчө чек катары бир эле учурда бир гана себетти ачууга болот.", "Only one cart at a time can be opened as a separate receipt.", "Ayrı fiş olarak aynı anda yalnızca bir sepet açılabilir.", "Alohida chek sifatida bir vaqtda faqat bitta savatni ochish mumkin."),
                Tr.T("Отложенные", "Калтырылган себеттер", "Held carts", "Bekleyen sepetler", "Kechiktirilgan savatlar"), MessageBoxButton.OK, MessageBoxImage.Information);
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
            PosMessageBox.Show(this, Tr.T("Выберите одну или несколько корзин.", "Бир же бир нече себетти тандаңыз.", "Select one or more carts.", "Bir veya birden fazla sepet seçin.", "Bitta yoki bir nechta savatni tanlang."), Tr.T("Отложенные", "Калтырылган себеттер", "Held carts", "Bekleyen sepetler", "Kechiktirilgan savatlar"),
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
            return Tr.T($"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} поз.", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} позиция", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} item(s)", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} kalem", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} ta mahsulot");
        }
    }
}
