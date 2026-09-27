using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// Списание товара прямо из кассы: «⋮ Ещё» → «Списание» (2026-09-27, просьба владельца).
/// Само списание — то же, что во вкладке «Списание» склада (WarehouseViewModel.RunWriteOffAsync):
/// остаток перечитывается с сервера, проводится акт, запись идёт в журнал списаний и в отчёт
/// смены. Здесь только выбор товара (из текущего чека, поиском или сканером), количество и причина.
/// </summary>
public partial class WriteOffDialog : Window
{
    private readonly WarehouseViewModel _vm;
    private readonly List<RadioButton> _reasonButtons = new();
    private CatalogProductTileVm? _selected;
    private bool _busy;

    /// <summary>Что списали — для строки под корзиной после закрытия.</summary>
    public string? ResultMessage { get; private set; }

    public WriteOffDialog() : this(Array.Empty<CatalogProductTileVm>()) { }

    public WriteOffDialog(IReadOnlyList<CatalogProductTileVm> cartProducts)
    {
        InitializeComponent();
        _vm = new WarehouseViewModel(App.GetRequiredService<IInventoryApiService>(), new DialogPrompts(this));
        DataContext = _vm;

        Title = Tr.T("Списание товара", "Товарды эсептен чыгаруу", "Write off a product", "Ürün düşümü", "Mahsulotni hisobdan chiqarish");
        HeaderText.Text = Title;
        HintText.Text = Tr.T(
            "Отсканируйте товар, найдите его по названию или выберите из текущего чека. Остаток уменьшится на складе сразу.",
            "Товарды сканерлеңиз, аталышы боюнча табыңыз же учурдагы чектен тандаңыз. Калдык кампада дароо азаят.",
            "Scan the product, find it by name, or pick it from the current receipt. Stock is reduced right away.",
            "Ürünü okutun, adıyla bulun veya mevcut fişten seçin. Stok hemen düşer.",
            "Mahsulotni skanerlang, nomi bo'yicha toping yoki joriy chekdan tanlang. Ombordagi qoldiq darhol kamayadi.");
        CartLabel.Text = Tr.T("Из текущего чека", "Учурдагы чектен", "From the current receipt", "Mevcut fişten", "Joriy chekdan");
        SearchBox.Watermark = Tr.T("Штрихкод или название товара…", "Штрихкод же товардын аталышы…", "Barcode or product name…",
            "Barkod veya ürün adı…", "Shtrix-kod yoki mahsulot nomi…");
        SelectedLabel.Text = Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot");
        QtyLabel.Text = Tr.T("Сколько списать", "Канча эсептен чыгаруу керек", "Quantity to write off", "Düşülecek miktar", "Qancha hisobdan chiqarish");
        ReasonLabel.Text = Tr.T("Причина", "Себеби", "Reason", "Neden", "Sabab");
        CancelButton.Content = Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");
        WriteOffButton.Content = Tr.T("Списать", "Эсептен чыгаруу", "Write off", "Düş", "Hisobdan chiqarish");

        foreach (var product in cartProducts.Where(p => !string.IsNullOrEmpty(p.Id)).GroupBy(p => p.Id).Select(g => g.First()))
        {
            var chip = new Button { Classes = { "pick" }, Content = product.Title, Tag = product };
            chip.Click += (_, _) => Select(product);
            CartItemsPanel.Children.Add(chip);
        }
        CartPanel.IsVisible = CartItemsPanel.Children.Count > 0;

        foreach (var reason in WarehouseViewModel.WriteOffReasons)
        {
            var radio = new RadioButton { Classes = { "reason" }, GroupName = "writeoff-reason", Content = reason };
            _reasonButtons.Add(radio);
            ReasonsPanel.Children.Add(radio);
        }
        _reasonButtons[0].IsChecked = true;

        // Один товар в чеке — почти наверняка его и списывают.
        if (CartItemsPanel.Children.Count == 1)
            Select(cartProducts.First(p => !string.IsNullOrEmpty(p.Id)));
        else
            ShowSelection();

        Opened += (_, _) => SearchBox.Focus();
    }

    private void Select(CatalogProductTileVm product)
    {
        _selected = product;
        _vm.WriteOffPickCommand.Execute(product);
        ShowError(null);
        ShowSelection();
        QtyBox.Focus();
        QtyBox.SelectAll();
    }

    private void ShowSelection()
    {
        if (_selected == null)
        {
            SelectedName.Text = Tr.T("Не выбран", "Тандалган жок", "Not selected", "Seçilmedi", "Tanlanmagan");
            SelectedStock.Text = "";
            return;
        }

        SelectedName.Text = _selected.Title;
        SelectedStock.Text = Tr.T($"На складе: {_selected.StockInfo}", $"Кампада: {_selected.StockInfo}", $"In stock: {_selected.StockInfo}",
            $"Stokta: {_selected.StockInfo}", $"Omborda: {_selected.StockInfo}");
    }

    private void Result_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CatalogProductTileVm product })
            Select(product);
    }

    /// <summary>Enter в поле поиска: сканер вводит штрихкод и Enter — ищем товар с этим кодом,
    /// иначе берём первый найденный по названию.</summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;

        var text = (SearchBox.Text ?? "").Trim();
        if (text.Length == 0)
            return;

        var byBarcode = CatalogCacheService.Products.FirstOrDefault(p =>
            string.Equals(p.Barcode, text, StringComparison.OrdinalIgnoreCase));
        var product = byBarcode ?? _vm.WriteOffSearchResults.FirstOrDefault();
        if (product == null)
        {
            ShowError(Tr.T("Товар не найден в каталоге.", "Товар каталогдон табылган жок.", "Product not found in the catalog.",
                "Ürün katalogda bulunamadı.", "Mahsulot katalogda topilmadi."));
            return;
        }

        SearchBox.Text = "";
        Select(product);
    }

    private double ReadQty()
    {
        var raw = (QtyBox.Text ?? "").Trim().Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var qty) ? qty : 0;
    }

    private void SetQty(double qty) =>
        QtyBox.Text = qty.ToString(_selected?.MustWeigh == true ? "0.###" : "0", CultureInfo.InvariantCulture);

    private void Minus_Click(object? sender, RoutedEventArgs e) => SetQty(Math.Max(1, ReadQty() - 1));

    private void Plus_Click(object? sender, RoutedEventArgs e) => SetQty(Math.Max(0, ReadQty()) + 1);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void WriteOff_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        if (_selected == null)
        {
            ShowError(Tr.T("Выберите товар для списания.", "Эсептен чыгаруу үчүн товар тандаңыз.", "Select a product to write off.",
                "Düşülecek ürünü seçin.", "Hisobdan chiqarish uchun mahsulotni tanlang."));
            return;
        }

        var qty = ReadQty();
        if (qty <= 0)
        {
            ShowError(Tr.T("Укажите количество для списания.", "Эсептен чыгарылуучу санды көрсөтүңүз.", "Enter the quantity to write off.",
                "Düşülecek miktarı girin.", "Hisobdan chiqariladigan miqdorni kiriting."));
            return;
        }

        var reason = _reasonButtons.FirstOrDefault(r => r.IsChecked == true)?.Content as string ?? WarehouseViewModel.WriteOffReasons[0];
        var name = _selected.Title;
        _vm.WriteOffQuantity = qty;
        _vm.WriteOffReason = reason;

        _busy = true;
        WriteOffButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        ShowError(null);
        try
        {
            var ok = await _vm.RunWriteOffAsync().ConfigureAwait(true);
            if (!ok)
                return;

            PosLogger.Log($"Списание из кассы: {name} × {qty:0.###} ({reason})", "WRITEOFF");
            var qtyText = qty.ToString("0.###", CultureInfo.InvariantCulture);
            ResultMessage = Tr.T($"Списано: {name} — {qtyText} ({reason}).", $"Эсептен чыгарылды: {name} — {qtyText} ({reason}).",
                $"Written off: {name} — {qtyText} ({reason}).", $"Düşüldü: {name} — {qtyText} ({reason}).",
                $"Hisobdan chiqarildi: {name} — {qtyText} ({reason}).");
            Close(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Списание из кассы не удалось: {ex}", "WARNING");
            ShowError(Tr.T("Не удалось провести списание.", "Эсептен чыгаруу мүмкүн болгон жок.", "Could not post the write-off.",
                "Düşüm yapılamadı.", "Hisobdan chiqarishni o'tkazib bo'lmadi."));
        }
        finally
        {
            _busy = false;
            WriteOffButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
        }
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? "";
        ErrorText.IsVisible = !string.IsNullOrEmpty(message);
    }

    /// <summary>Сообщения списания — в само окно: всплывашка главного окна пряталась бы под ним.</summary>
    private sealed class DialogPrompts : IUserPrompts
    {
        private readonly WriteOffDialog _dialog;

        public DialogPrompts(WriteOffDialog dialog) => _dialog = dialog;

        public Task<bool> ConfirmAsync(string message) => Task.FromResult(false);

        public void ShowToast(string message, bool isWarning = false)
        {
            if (isWarning)
                _dialog.ShowError(message);
        }

        public void ShowWarning(string message) => _dialog.ShowError(message);

        public void ShowError(string message) => _dialog.ShowError(message);

        public Task<bool> ConfirmWithPasswordAsync(string title, string message, string expectedPassword) => Task.FromResult(false);

        public Task<bool> ConfirmWithCodeAsync(string title, string message, Func<string, bool> validator) => Task.FromResult(false);
    }
}
