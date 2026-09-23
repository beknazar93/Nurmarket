using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog : Window, INotifyPropertyChanged
{
    private const int SalesPageSize = 35;

    private string? _currentSaleId;
    private int _salesPage;
    private readonly HashSet<string> _salesSeenIds = new(StringComparer.OrdinalIgnoreCase);
    private string _searchFilter = "";
    private bool _isBusy;
    private readonly string? _currentUserId;

    public ObservableCollection<ReturnSaleListItemVm> Sales { get; } = new();
    public ObservableCollection<ReturnSaleGridRow> FilteredSales { get; } = new();
    public ObservableCollection<ReturnSaleLineVm> Lines { get; } = new();

    public new event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    public ReturnSaleDialog()
    {
        InitializeComponent();
        _currentUserId = App.CurrentUserId;
        DataContext = this;
        Lines.CollectionChanged += (_, _) => UpdateReceiptChrome();
        Opened += OnFirstOpened;

        // Подписка на изменение свойств окна через стандартный PropertyChanged
        PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WindowState))
            {
                UpdateWindowStateUI();
            }
        };

        UpdateReceiptChrome();
        UpdateWindowStateUI();
    }

    private async void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        UpdateWindowStateUI();
        await LoadSalesAsync(true).ConfigureAwait(true);
    }

    #region Window State Logic (Fullscreen / Dimmer)

    private void ToggleFullscreen_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateWindowStateUI()
    {
        bool isMaximized = WindowState == WindowState.Maximized;

        // 1. Показываем полупрозрачное затемнение фона ТОЛЬКО в оконном режиме
        var overlayDimmer = this.FindControl<Border>("OverlayDimmer");
        if (overlayDimmer != null)
        {
            overlayDimmer.IsVisible = !isMaximized;
        }

        // 2. В полноэкранном режиме убираем лишние внешние отступы карточки
        var mainCardBorder = this.FindControl<Border>("MainCardBorder");
        if (mainCardBorder != null)
        {
            mainCardBorder.Margin = isMaximized ? new Thickness(0) : new Thickness(24);
            mainCardBorder.CornerRadius = isMaximized ? new CornerRadius(0) : new CornerRadius(16);
        }

        // 3. Обновляем иконку переключателя
        var fullscreenIconText = this.FindControl<TextBlock>("FullscreenIconText");
        if (fullscreenIconText != null)
        {
            fullscreenIconText.Text = isMaximized ? "🗗" : "🗖";
        }
    }

    #endregion

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);

    private async void RefreshSales_Click(object? sender, RoutedEventArgs e) =>
        await LoadSalesAsync(true).ConfigureAwait(true);

    private async void MoreSales_Click(object? sender, RoutedEventArgs e) =>
        await LoadSalesAsync(false).ConfigureAwait(true);

    private async Task LoadSalesAsync(bool reset)
    {
        ErrorText.IsVisible = false;
        ErrorText.Text = "";
        IsBusy = true;

        if (reset)
        {
            _salesPage = 1;
            Sales.Clear();
            _salesSeenIds.Clear();
        }
        else
        {
            _salesPage++;
        }

        var page = reset ? 1 : _salesPage;

        try
        {
            var jsonElementList = await App.SalesApi.PosSalesListAsync(page, SalesPageSize, App.PosCashboxId)
                .ConfigureAwait(true);

            var hasCustomerId = jsonElementList.Any(el => el.TryGetProperty("customer_id", out _));
            var added = 0;

            foreach (var row in jsonElementList)
            {
                if (!string.IsNullOrWhiteSpace(_currentUserId) && hasCustomerId &&
                    row.TryGetProperty("customer_id", out var cid) &&
                    !string.Equals(cid.GetString(), _currentUserId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var saleId = PosSaleRowFormatter.TrySaleId(row);
                if (string.IsNullOrEmpty(saleId) || !_salesSeenIds.Add(saleId))
                    continue;

                DateTime saleDate = DateTime.MinValue;
                _ = TryGetDateTime(row, "date", out saleDate)
                    || TryGetDateTime(row, "created_at", out saleDate)
                    || TryGetDateTime(row, "sale_date", out saleDate)
                    || TryGetDateTime(row, "order_date", out saleDate);

                decimal totalAmount = 0;
                _ = TryGetDecimal(row, "total", out totalAmount)
                    || TryGetDecimal(row, "grand_total", out totalAmount)
                    || TryGetDecimal(row, "total_amount", out totalAmount)
                    || TryGetDecimal(row, "amount", out totalAmount)
                    || TryGetDecimal(row, "total_sum", out totalAmount)
                    || TryGetDecimal(row, "sum", out totalAmount)
                    || TryGetDecimal(row, "price", out totalAmount)
                    || TryGetDecimal(row, "total_price", out totalAmount)
                    || TryGetDecimal(row, "final_total", out totalAmount)
                    || TryGetDecimal(row, "order_total", out totalAmount);

                Sales.Add(new ReturnSaleListItemVm
                {
                    SaleId = saleId,
                    SaleDate = saleDate,
                    TotalAmount = totalAmount,
                    Summary = PosSaleRowFormatter.SummaryLine(row),
                });
                added++;
            }

            ApplySalesFilter();

            if (reset && Sales.Count == 0)
                ShowErr("Список продаж пуст или API не вернул данные. Попробуйте «Обновить» или введите ID продажи вручную.");
            else if (!reset && added == 0)
            {
                PosMessageBox.Show(this, "Больше записей нет.", "Продажи",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _salesPage = Math.Max(1, _salesPage - 1);
            }
        }
        catch (ApiException ex)
        {
            if (reset)
                ShowErr(ex.Message);
            else
                PosMessageBox.Show(this, ex.Message, "Продажи", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        catch (HttpRequestException ex)
        {
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message;
            if (reset)
                ShowErr(msg);
            else
                PosMessageBox.Show(this, msg, "Продажи", MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        catch (TaskCanceledException)
        {
            if (reset)
                ShowErr("Превышено время ожидания.");
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySalesFilter()
    {
        FilteredSales.Clear();
        foreach (var item in Sales)
        {
            if (string.IsNullOrWhiteSpace(_searchFilter) ||
                item.SaleId.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                FilteredSales.Add(new ReturnSaleGridRow(item));
        }
    }

    private static bool TryGetDateTime(JsonElement element, string propertyName, out DateTime result)
    {
        result = DateTime.MinValue;
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.String)
            return DateTime.TryParse(prop.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

        return false;
    }

    private static bool TryGetDecimal(JsonElement element, string propertyName, out decimal result)
    {
        result = 0;
        if (!element.TryGetProperty(propertyName, out var prop))
            return false;

        if (prop.ValueKind == JsonValueKind.Number)
        {
            result = prop.GetDecimal();
            return true;
        }

        return prop.ValueKind == JsonValueKind.String &&
               decimal.TryParse(prop.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    private async void SelectSale_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string saleId } || string.IsNullOrWhiteSpace(saleId))
            return;

        await OpenSaleByIdAsync(saleId.Trim()).ConfigureAwait(true);
    }

    private async void LoadById_Click(object? sender, RoutedEventArgs e)
    {
        var saleId = (SaleIdBox.Text ?? "").Trim();
        if (saleId.Length == 0)
            ShowErr("Введите ID продажи.");
        else
            await OpenSaleByIdAsync(saleId).ConfigureAwait(true);
    }

    private async Task OpenSaleByIdAsync(string saleId)
    {
        ErrorText.IsVisible = false;
        ErrorText.Text = "";
        IsBusy = true;
        try
        {
            var sale = await App.SalesApi.PosSaleGetAsync(saleId).ConfigureAwait(true);
            _currentSaleId = saleId;
            FillLinesFromSale(sale);
            UpdateReceiptChrome();
            if (Lines.Count == 0)
                ShowErr("В ответе сервера нет позиций с идентификатором строки для возврата.");
        }
        catch (ApiException ex)
        {
            ShowErr(ex.Message);
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        catch (HttpRequestException ex)
        {
            ShowErr(string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message);
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        catch (TaskCanceledException)
        {
            ShowErr("Превышено время ожидания.");
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void FillLinesFromSale(JsonElement sale)
    {
        Lines.Clear();
        foreach (var lineItem in CartDisplayHelper.EnumerateSaleLineItems(sale))
        {
            var lineId = CartDisplayHelper.TryRefundLineId(lineItem);
            if (string.IsNullOrEmpty(lineId))
                continue;

            var title = CartDisplayHelper.ItemName(lineItem);
            var subLine = CartDisplayHelper.QuantityPriceLine(lineItem);
            var lineTotal = CartDisplayHelper.LineTotal(lineItem);
            var originalQty = CartDisplayHelper.LineQuantity(lineItem);
            var qty = CartDisplayHelper.RefundableQuantity(lineItem);
            var canReturn = qty > 0 && !CartDisplayHelper.LineLooksFullyReturned(lineItem);

            string? refundReason = null;
            if (lineItem.TryGetProperty("refund_reason", out var rr) && rr.ValueKind == JsonValueKind.String)
                refundReason = rr.GetString();
            else if (lineItem.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                refundReason = r.GetString();

            Lines.Add(new ReturnSaleLineVm
            {
                LineId = lineId,
                ProductId = CartDisplayHelper.TryProductId(lineItem),
                Quantity = qty,
                OriginalQuantity = originalQty,
                Title = title,
                SubLine = subLine,
                LineSumText = $"Сумма: {lineTotal} сом",
                CanReturn = canReturn,
                RefundReason = refundReason,
            });
        }
    }

    private void SelectAllReturnable_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var line in Lines.Where(x => x.CanReturn))
            line.IsSelected = true;
    }

    private void ClearReturnSelection_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var line in Lines)
            line.IsSelected = false;
    }

    private async void ReturnSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSaleId))
            return;

        var selected = Lines.Where(x => x.CanReturn && x.IsSelected).ToList();
        if (selected.Count == 0)
        {
            PosMessageBox.Show(this, "Отметьте галочками хотя бы одну позицию для возврата.", "Возврат",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        decimal total = 0;
        foreach (var line in selected)
        {
            if (decimal.TryParse(line.LineSumText?.Replace("Сумма: ", "").Replace(" сом", ""),
                    NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
                total += val;
        }

        if (PosMessageBox.Show(this,
                $"Выбрано позиций: {selected.Count}\nСумма возврата: ~{total:F2} сом\n\nПродолжить?",
                "Подтверждение возврата", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var reasonDialog = new ReturnLineReasonDialog(selected.Count);
        if (PosDialogHost.Show(reasonDialog, this) != true)
            return;

        var reason = reasonDialog.ReasonText;
        IsBusy = true;

        try
        {
            var requests = selected.Select(line => new PosRefundLineRequest
            {
                LineId = line.LineId,
                ProductId = line.ProductId,
                Title = line.Title,
                Quantity = line.Quantity > 0 ? line.Quantity : 1,
                OriginalQuantity = line.OriginalQuantity > 0 ? line.OriginalQuantity : line.Quantity,
            }).ToList();

            await PosRefundService.RefundLinesAsync(
                App.SalesApi,
                _currentSaleId,
                requests,
                reason,
                App.PosCashboxId).ConfigureAwait(true);

            PosMessageBox.Show(this, selected.Count == 1
                    ? "Возврат оформлен."
                    : $"Возврат оформлен ({selected.Count} поз.).",
                "Возврат", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex) when (ex.StatusCode == 502 && ex.Message.Contains("Возвращено позиций", StringComparison.Ordinal))
        {
            PosMessageBox.Show(this, ex.Message, "Возврат", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (ApiException ex)
        {
            PosMessageBox.Show(this, ex.Message, "Возврат", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            PosMessageBox.Show(this, string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message,
                "Возврат", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            PosMessageBox.Show(this, "Превышено время ожидания.", "Возврат",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshCurrentSaleAsync().ConfigureAwait(true);
    }

    private async Task RefreshCurrentSaleAsync()
    {
        if (string.IsNullOrEmpty(_currentSaleId))
            return;

        try
        {
            FillLinesFromSale(await App.SalesApi.PosSaleGetAsync(_currentSaleId).ConfigureAwait(true));
            UpdateReceiptChrome();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Return receipt refresh failed after operation: {ex}", "WARNING");
        }

        await LoadSalesAsync(true).ConfigureAwait(true);
    }

    private void UpdateReceiptChrome()
    {
        var hasSale = !string.IsNullOrEmpty(_currentSaleId);
        SelectedChequeBar.IsVisible = hasSale;
        ReturnWholeReceiptButton.IsEnabled = hasSale;

        if (!hasSale)
        {
            SelectedSaleText.Text = "";
            LinesPlaceholder.Text = "Сначала выберите чек в списке выше — здесь появится его содержимое.";
            LinesPlaceholder.IsVisible = true;
        }
        else if (Lines.Count == 0)
        {
            SelectedSaleText.Text = "Выбран чек · " + TruncateId(_currentSaleId);
            LinesPlaceholder.Text = "В этом чеке нет позиций с идентификатором строки для возврата через кассу.";
            LinesPlaceholder.IsVisible = true;
        }
        else
        {
            SelectedSaleText.Text = $"Выбран чек · {TruncateId(_currentSaleId)}  ·  позиций в чеке: {Lines.Count}";
            LinesPlaceholder.IsVisible = false;
        }
    }

    private static string TruncateId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return "—";
        return id.Length <= 36 ? id : id[..32] + "…";
    }

    private async void ReturnWholeReceipt_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSaleId))
        {
            PosMessageBox.Show(this, "Сначала выберите чек в списке выше.", "Возврат",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reasonDialog = new ReturnLineReasonDialog(kind: ReturnReasonDialogKind.FullReceipt);
        if (PosDialogHost.Show(reasonDialog, this) != true)
            return;

        if (PosMessageBox.Show(this,
                "Оформить полный возврат всего чека одной операцией? Позиции по отдельности возвращать не потребуется.",
                "Полный возврат", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            await PosRefundService.RefundWholeSaleAsync(
                App.SalesApi,
                _currentSaleId,
                reasonDialog.ReasonText,
                App.PosCashboxId).ConfigureAwait(true);
            PosMessageBox.Show(this, "Полный возврат чека оформлен.",
                "Возврат", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshCurrentSaleAsync().ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            PosMessageBox.Show(this, ex.Message, "Возврат", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            PosMessageBox.Show(this, string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message,
                "Возврат", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            PosMessageBox.Show(this, "Превышено время ожидания.", "Возврат",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowErr(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.IsVisible = true;
    }

    private void SearchReceiptBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchFilter = SearchReceiptBox.Text ?? "";
        ApplySalesFilter();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public sealed class ReturnSaleGridRow(ReturnSaleListItemVm item)
    {
        public ReturnSaleListItemVm Item { get; } = item;
        public string SaleId => Item.SaleId;
        public string SaleDateDisplay => Item.SaleDate == DateTime.MinValue
            ? "—"
            : Item.SaleDate.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        public string TotalAmountDisplay => $"{Item.TotalAmount:F2} сом";
    }
}
