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
        // 2026-10-21 (пакет 6, п. 51): два разных «отложенных» — кнопка «Отложить» оставляет чек вкладкой над чеком,
        // а сюда чек попадает, только когда вкладок уже 10. Пустая очередь объясняет, где искать отложенный чек.
        SummaryText.Text = items.Count == 0
            ? Tr.T("Очередь пуста. Чеки, отложенные кнопкой «Отложить», — во вкладках над чеком («Отложен 18:34»). Сюда чек попадает, только когда открыто уже 10 вкладок.",
                "Кезек бош. «Калтыруу» баскычы менен калтырылган чектер — чектин үстүндөгү өтмөктөрдө («Калтырылды 18:34»). Бул жерге чек 10 өтмөк ачык болгондо гана түшөт.",
                "The queue is empty. Receipts held with “Hold” stay as tabs above the receipt (“Held 18:34”). A receipt comes here only when 10 tabs are already open.",
                "Kuyruk boş. «Beklet» ile bekletilen fişler, fişin üstündeki sekmelerde («Bekletildi 18:34»). Fiş buraya yalnızca 10 sekme açıkken gelir.",
                "Navbat bo'sh. «Kutish» tugmasi bilan qo'yilgan cheklar — chek ustidagi yorliqlarda («Kutishda 18:34»). Bu yerga chek faqat 10 ta yorliq ochiq bo'lganda tushadi.")
            : Tr.T($"В очереди: {items.Count} чек(ов). Последний: {items[0].SavedAt.LocalDateTime:g}.", $"Кезекте: {items.Count} чек. Акыркысы: {items[0].SavedAt.LocalDateTime:g}.", $"In queue: {items.Count} receipt(s). Latest: {items[0].SavedAt.LocalDateTime:g}.", $"Kuyrukta: {items.Count} fiş. Sonuncusu: {items[0].SavedAt.LocalDateTime:g}.", $"Navbatda: {items.Count} ta chek. Oxirgisi: {items[0].SavedAt.LocalDateTime:g}.");

        foreach (var e in items)
            CartListBox.Items.Add(new DeferredCartListRow(e));

        // 2026-10-22 (модалки, пакет 1): пустая очередь — только подсказка обычным текстом (не жирным), без пустого
        // серого списка; есть чеки — сводка полужирным над списком, как раньше.
        CartListBox.IsVisible = items.Count > 0;
        SummaryText.FontWeight = items.Count == 0 ? Avalonia.Media.FontWeight.Normal : Avalonia.Media.FontWeight.SemiBold;
        SummaryText.Foreground = null;
        SummaryText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(items.Count == 0 ? "BrushTextSoft" : "BrushText"));

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
        // 2026-10-22 (модалки, пакет 1): на время переноса чека окно занято — подвал и ✕ выключены, Enter/Esc не
        // срабатывают (второй клик не переносит чек дважды и не закрывает окно посреди операции).
        Shell.IsBusy = busy;
        DeleteSelectedButton.IsEnabled = !busy;
        MergeIntoCurrentButton.IsEnabled = !busy && CartListBox.Items.Count > 0;
        LoadAsSeparateButton.IsEnabled = !busy && CartListBox.Items.Count > 0;
        CartListBox.IsEnabled = !busy;
    }

    private async void DeleteSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;
        var rows = GetSelectedRows();
        if (rows.Count == 0)
        {
            PosMessageBox.Show(this, Tr.T("Выберите строки в списке.", "Тизмеден саптарды тандаңыз.", "Select rows in the list.", "Listeden satırları seçin.", "Ro'yxatdan qatorlarni tanlang."), Tr.T("Отложенные", "Калтырылган себеттер", "Held carts", "Bekleyen sepetler", "Kechiktirilgan savatlar"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2026-10-22 (модалки, пакет 1): удаление необратимо — подтверждение с глаголом «Удалить N чеков» (красная),
        // фокус и Enter — не на удалении («Отмена» рядом). Раньше чеки удалялись сразу по кнопке.
        var n = rows.Count;
        var confirmed = await PosDialogs.ConfirmYesNoModalAsync(this,
            Tr.T($"Удалить из очереди {ReceiptsRu(n)}? Товары этих чеков вернуть будет нельзя.",
                $"Кезектен {n} чекти өчүрөсүзбү? Бул чектердин товарларын кайтаруу мүмкүн болбойт.",
                $"Delete {n} receipt(s) from the queue? Their items can't be restored.",
                $"{n} fiş kuyruktan silinsin mi? Bu fişlerdeki ürünler geri getirilemez.",
                $"Navbatdan {n} ta chek o'chirilsinmi? Bu cheklardagi mahsulotlarni qaytarib bo'lmaydi."),
            Tr.T("Удалить отложенные чеки", "Калтырылган чектерди өчүрүү", "Delete held receipts", "Bekleyen fişleri sil", "Kutishdagi cheklarni o'chirish"),
            confirmText: Tr.T($"Удалить {ReceiptsRu(n)}", $"{n} чекти өчүрүү", $"Delete {n} receipt(s)", $"{n} fişi sil", $"{n} ta chekni o'chirish"),
            cancelText: Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            danger: true).ConfigureAwait(true);
        if (!confirmed)
            return;

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

    /// <summary>2026-10-22 (модалки, пакет 1): «1 чек», «3 чека», «5 чеков» — для подтверждения удаления.</summary>
    private static string ReceiptsRu(int n)
    {
        var m100 = n % 100;
        var m10 = n % 10;
        var word = m100 is >= 11 and <= 14 ? "чеков" : m10 == 1 ? "чек" : m10 is >= 2 and <= 4 ? "чека" : "чеков";
        return $"{n} {word}";
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
            return Tr.T($"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} поз.", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} сап", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} item(s)", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} kalem", $"{Entry.Label} · {Entry.SavedAt.LocalDateTime:g} · {n} ta mahsulot");
        }
    }
}
