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
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog : Window, INotifyPropertyChanged
{
    private const int SalesPageSize = 35;

    private string? _currentSaleId;

    /// <summary>Номер чека, по которому идёт возврат — печатается на чеке возврата, чтобы
    /// кассир и покупатель могли сопоставить две бумажки.</summary>
    private string? _currentReceiptNumber;
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

                string? receiptNumber = row.TryGetProperty("receipt_number", out var rn) && rn.ValueKind == JsonValueKind.String
                    ? rn.GetString()
                    : null;

                Sales.Add(new ReturnSaleListItemVm
                {
                    SaleId = saleId,
                    SaleDate = saleDate,
                    TotalAmount = totalAmount,
                    Summary = PosSaleRowFormatter.SummaryLine(row),
                    ReceiptNumber = string.IsNullOrWhiteSpace(receiptNumber) ? null : receiptNumber,
                });
                added++;
            }

            ApplySalesFilter();

            if (reset && Sales.Count == 0)
                ShowErr(Tr.T("Список продаж пуст или API не вернул данные. Попробуйте «Обновить» или введите ID продажи вручную.",
                    "Сатуулар тизмеси бош же API маалымат кайтарган жок. «Жаңылоо» баскычын басыңыз же сатуунун ID’син өзүңүз киргизиңиз.",
                    "The sales list is empty or the API returned no data. Try Refresh or enter the sale ID manually.",
                    "Satış listesi boş veya API veri döndürmedi. «Yenile»yi deneyin veya satış kimliğini manuel girin.",
                    "Sotuvlar ro'yxati bo'sh yoki API ma'lumot qaytarmadi. «Yangilash»ni sinab ko'ring yoki sotuv ID’sini qo'lda kiriting."));
            else if (!reset && added == 0)
            {
                PosMessageBox.Show(this, Tr.T("Больше записей нет.", "Башка жазуулар жок.", "No more records.", "Başka kayıt yok.", "Boshqa yozuvlar yo'q."),
                    Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _salesPage = Math.Max(1, _salesPage - 1);
            }
        }
        catch (ApiException ex)
        {
            if (reset)
                ShowErr(ex.Message);
            else
                PosMessageBox.Show(this, ex.Message, Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        catch (HttpRequestException ex)
        {
            var msg = string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message;
            if (reset)
                ShowErr(msg);
            else
                PosMessageBox.Show(this, msg, Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"), MessageBoxButton.OK, MessageBoxImage.Warning);
            if (!reset)
                _salesPage = Math.Max(1, _salesPage - 1);
        }
        catch (TaskCanceledException)
        {
            if (reset)
                ShowErr(Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."));
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
        var position = 0;
        // Поиск, кроме внутреннего SaleId (серверный GUID, не показывается пользователю),
        // должен уметь находить чек и по видимому "№N" — это то, что реально видно в списке.
        var trimmedFilter = (_searchFilter ?? "").Trim().TrimStart('№', '#');
        foreach (var item in Sales)
        {
            position++;
            var matches = string.IsNullOrWhiteSpace(_searchFilter)
                || item.SaleId.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(item.ReceiptNumber) &&
                    item.ReceiptNumber.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                || (string.IsNullOrWhiteSpace(item.ReceiptNumber) &&
                    string.Equals(trimmedFilter, position.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            if (matches)
                FilteredSales.Add(new ReturnSaleGridRow(item, position));
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
        var raw = (SaleIdBox.Text ?? "").Trim();
        if (raw.Length == 0)
        {
            ShowErr(Tr.T("Введите ID продажи.", "Сатуунун ID'син киргизиңиз.", "Enter the sale ID.", "Satış kimliğini girin.", "Sotuv ID’sini kiriting."));
            return;
        }

        // Пользователь видит в списке только "№N" (позиционный номер, не серверный GUID),
        // поэтому если он ввёл именно его — сначала пробуем найти по позиции в загруженном
        // списке, и только если не нашли — отправляем введённый текст как есть (вдруг это
        // настоящий ID, скопированный откуда-то ещё).
        var trimmed = raw.TrimStart('№', '#');
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            && position >= 1 && position <= Sales.Count)
        {
            await OpenSaleByIdAsync(Sales[position - 1].SaleId).ConfigureAwait(true);
            return;
        }

        await OpenSaleByIdAsync(raw).ConfigureAwait(true);
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
            _currentReceiptNumber = Sales.FirstOrDefault(x => x.SaleId == saleId)?.ReceiptNumber;
            FillLinesFromSale(sale);
            UpdateReceiptChrome();
            if (Lines.Count == 0)
                ShowErr(Tr.T("В ответе сервера нет позиций с идентификатором строки для возврата.",
                    "Сервердин жообунда кайтаруу үчүн сап идентификатору бар позициялар жок.",
                    "The server response has no line items with a returnable identifier.",
                    "Sunucu yanıtında iade edilebilir kimliğe sahip kalem yok.",
                    "Server javobida qaytarish uchun identifikatorga ega pozitsiyalar yo'q."));
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
            ShowErr(string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message);
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
        }
        catch (TaskCanceledException)
        {
            ShowErr(Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."));
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

            // Сумма к возврату — пропорционально остатку к возврату, а не полная сумма строки:
            // из 5×100 при уже возвращённых 2 к возврату идут 300, а не 500.
            var refundSum = lineTotal;
            decimal.TryParse(lineTotal, NumberStyles.Any, CultureInfo.InvariantCulture, out var refundSumValue);
            if (originalQty > 0 &&
                double.TryParse(lineTotal, NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotalValue))
            {
                refundSum = CartDisplayHelper.FormatMoney(lineTotalValue * qty / originalQty);
                refundSumValue = (decimal)(lineTotalValue * qty / originalQty);
            }

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
                LineSumText = Tr.T($"Сумма: {refundSum} сом", $"Сумма: {refundSum} сом", $"Amount: {refundSum} som", $"Tutar: {refundSum} som", $"Summa: {refundSum} so'm"),
                RefundSum = refundSumValue,
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
            PosMessageBox.Show(this,
                Tr.T("Отметьте галочками хотя бы одну позицию для возврата.", "Кайтаруу үчүн жок дегенде бир позицияны белгилеңиз.", "Check at least one item to return.", "İade için en az bir kalemi işaretleyin.", "Qaytarish uchun kamida bitta pozitsiyani belgilang."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var total = selected.Sum(line => line.RefundSum);

        if (PosMessageBox.Show(this,
                Tr.T($"Выбрано позиций: {selected.Count}\nСумма возврата: ~{total:F2} сом\n\nПродолжить?",
                    $"Тандалган позиция: {selected.Count}\nКайтаруу суммасы: ~{total:F2} сом\n\nУлантасызбы?",
                    $"Items selected: {selected.Count}\nRefund amount: ~{total:F2} som\n\nContinue?",
                    $"Seçilen kalem: {selected.Count}\nİade tutarı: ~{total:F2} som\n\nDevam edilsin mi?",
                    $"Tanlangan pozitsiyalar: {selected.Count}\nQaytarish summasi: ~{total:F2} so'm\n\nDavom etilsinmi?"),
                Tr.T("Подтверждение возврата", "Кайтарууну ырастоо", "Confirm return", "İadeyi onayla", "Qaytarishni tasdiqlash"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
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

            SaleDetailCache.Forget(_currentSaleId);

            // В итогах смены на сервере возвратов нет вовсе — записываем сами, иначе кассир
            // при закрытии смены их не увидит (см. ShiftEventsStore).
            ShiftEventsStore.Record(
                ShiftEventsStore.KindReturn,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(_currentSaleId),
                (double)total);

            // Баллы лояльности отменяются, когда возвращён ВЕСЬ чек — в том числе если кассир
            // сделал это, отметив все позиции галочками (самый естественный способ), а не кнопкой
            // «Полный возврат». Раньше отмена стояла только на кнопке полного возврата, поэтому
            // при построчном возврате покупатель оставлял себе бонусы за возвращённый товар.
            // Частичный возврат баллы не трогает: сервер не делит начисление по позициям.
            var returnedEverything = selected.Count == Lines.Count(x => x.CanReturn)
                && selected.All(l => l.OriginalQuantity <= 0 || l.Quantity >= l.OriginalQuantity - 1e-5);
            if (returnedEverything)
            {
                try
                {
                    ClientLoyaltyStore.ReverseRemainingForSale(_currentSaleId);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Loyalty reversal on line return failed: {ex.Message}", "WARNING");
                }
            }

            // Чек возврата обязателен: это выдача денег из кассы, её подтверждают бумагой
            // так же, как продажу. Печатаем после того, как сервер принял возврат — чтобы на
            // руках не оказалось чека по непрошедшей операции.
            // Печать — в фоне (2026-09-25): медленный или отключённый принтер больше не
            // подвешивает окно на время записи в порт.
            var printLines = selected.Select(l => (
                Name: l.Title,
                Quantity: l.Quantity > 0 ? l.Quantity : 1,
                UnitPrice: l.Quantity > 0 ? l.RefundSum / (decimal)l.Quantity : l.RefundSum,
                Sum: l.RefundSum)).ToList();
            var printReceiptNumber = _currentReceiptNumber;
            var printCashier = NurMarketKassa.PosApp.CurrentUserDisplayName;
            var printError = await Task.Run(() => OperationReceiptPrinter.PrintReturn(
                printReceiptNumber,
                printLines,
                total,
                reason,
                isWholeSale: false,
                printCashier)).ConfigureAwait(true);

            if (printError is not null)
            {
                PosMessageBox.Show(this, printError,
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            PosMessageBox.Show(this, selected.Count == 1
                    ? Tr.T("Возврат оформлен.", "Кайтаруу таризделди.", "Return completed.", "İade tamamlandı.", "Qaytarish rasmiylashtirildi.")
                    : Tr.T($"Возврат оформлен ({selected.Count} поз.).", $"Кайтаруу таризделди ({selected.Count} поз.).", $"Return completed ({selected.Count} items).", $"İade tamamlandı ({selected.Count} kalem).", $"Qaytarish rasmiylashtirildi ({selected.Count} poz.)."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (ApiException ex) when (ex.StatusCode == 502 && ex.Message.Contains("Возвращено позиций", StringComparison.Ordinal))
        {
            PosMessageBox.Show(this, ex.Message, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (ApiException ex)
        {
            PosMessageBox.Show(this, ex.Message, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            PosMessageBox.Show(this, string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message,
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            PosMessageBox.Show(this, Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
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
            LinesPlaceholder.Text = Tr.T("Сначала выберите чек в списке выше — здесь появится его содержимое.",
                "Алгач жогорудагы тизмеден чекти тандаңыз — анын курамы ушул жерде көрүнөт.",
                "First select a receipt in the list above — its contents will appear here.",
                "Önce yukarıdaki listeden bir fiş seçin — içeriği burada görünecek.",
                "Avval yuqoridagi ro'yxatdan chekni tanlang — uning tarkibi shu yerda ko'rinadi.");
            LinesPlaceholder.IsVisible = true;
        }
        else if (Lines.Count == 0)
        {
            SelectedSaleText.Text = Tr.T("Выбран чек · ", "Тандалган чек · ", "Selected receipt · ", "Seçilen fiş · ", "Tanlangan chek · ") + DisplaySaleNumber(_currentSaleId);
            LinesPlaceholder.Text = Tr.T("В этом чеке нет позиций с идентификатором строки для возврата через кассу.",
                "Бул чекте кассадан кайтаруу үчүн сап идентификатору бар позициялар жок.",
                "This receipt has no line items with an identifier that can be returned via the POS.",
                "Bu fişte kasadan iade edilebilecek satır kimliğine sahip kalem yok.",
                "Bu chekda kassa orqali qaytarish uchun qator identifikatoriga ega pozitsiyalar yo'q.");
            LinesPlaceholder.IsVisible = true;
        }
        else
        {
            SelectedSaleText.Text = Tr.T($"Выбран чек · {DisplaySaleNumber(_currentSaleId)}  ·  позиций в чеке: {Lines.Count}",
                $"Тандалган чек · {DisplaySaleNumber(_currentSaleId)}  ·  чектеги позиция: {Lines.Count}",
                $"Selected receipt · {DisplaySaleNumber(_currentSaleId)}  ·  items in receipt: {Lines.Count}",
                $"Seçilen fiş · {DisplaySaleNumber(_currentSaleId)}  ·  fişteki kalem: {Lines.Count}",
                $"Tanlangan chek · {DisplaySaleNumber(_currentSaleId)}  ·  chekdagi pozitsiyalar: {Lines.Count}");
            LinesPlaceholder.IsVisible = false;
        }
    }

    private string DisplaySaleNumber(string? saleId)
    {
        if (string.IsNullOrEmpty(saleId))
            return "—";
        var row = FilteredSales.FirstOrDefault(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
        return row?.DisplayNumber ?? TruncateId(saleId);
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
            PosMessageBox.Show(this, Tr.T("Сначала выберите чек в списке выше.", "Алгач жогорудагы тизмеден чекти тандаңыз.", "First select a receipt in the list above.", "Önce yukarıdaki listeden bir fiş seçin.", "Avval yuqoridagi ro'yxatdan chekni tanlang."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reasonDialog = new ReturnLineReasonDialog(kind: ReturnReasonDialogKind.FullReceipt);
        if (PosDialogHost.Show(reasonDialog, this) != true)
            return;

        if (PosMessageBox.Show(this,
                Tr.T("Оформить полный возврат всего чека одной операцией? Позиции по отдельности возвращать не потребуется.",
                    "Бүт чекти бир операция менен толук кайтарасызбы? Позицияларды өз-өзүнчө кайтаруунун кереги жок болот.",
                    "Return the whole receipt in one operation? You won't need to return items one by one.",
                    "Tüm fiş tek işlemde iade edilsin mi? Kalemleri tek tek iade etmeniz gerekmeyecek.",
                    "Butun chek bitta amal bilan qaytarilsinmi? Pozitsiyalarni birma-bir qaytarish shart bo'lmaydi."),
                Tr.T("Полный возврат", "Толук кайтаруу", "Full return", "Tam iade", "To'liq qaytarish"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        IsBusy = true;
        try
        {
            await PosRefundService.RefundWholeSaleAsync(
                App.SalesApi,
                _currentSaleId,
                reasonDialog.ReasonText,
                App.PosCashboxId).ConfigureAwait(true);

            // 2026-09-23, живой баг: полный возврат чека не писал событие смены вообще — запись
            // стояла только в построчном возврате. Вернули покупателю весь чек наличными, а в
            // Z-отчёте, Telegram-сводке и выгрузке этой суммы нет: кассир сдаёт смену с
            // «недостачей» ровно на сумму возврата.
            var wholeTotal = Lines.Sum(line => line.RefundSum);
            ShiftEventsStore.Record(
                ShiftEventsStore.KindReturn,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(_currentSaleId),
                (double)wholeTotal);

            var wholeLines = Lines.Where(l => l.CanReturn).Select(l => (
                Name: l.Title,
                Quantity: l.Quantity > 0 ? l.Quantity : 1,
                UnitPrice: l.Quantity > 0 ? l.RefundSum / (decimal)l.Quantity : l.RefundSum,
                Sum: l.RefundSum)).ToList();
            var wholeReceiptNumber = _currentReceiptNumber;
            var wholeReason = reasonDialog.ReasonText;
            var wholeCashier = NurMarketKassa.PosApp.CurrentUserDisplayName;
            var wholePrintError = await Task.Run(() => OperationReceiptPrinter.PrintReturn(
                wholeReceiptNumber,
                wholeLines,
                wholeTotal,
                wholeReason,
                isWholeSale: true,
                wholeCashier)).ConfigureAwait(true);

            if (wholePrintError is not null)
            {
                PosMessageBox.Show(this, wholePrintError,
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 2026-09-13, живой баг: полный возврат чека не отменял баллы лояльности,
            // начисленные/списанные при этой продаже (см. BasketPanelViewModel.
            // CreditOrRedeemLoyaltyPoints) — покупатель сохранял бонусы за возвращённый товар
            // навсегда, а списанные на оплату баллы не восстанавливались. Безопасно вызывать
            // всегда: если для этой продажи транзакции не было (лояльность выключена, продажа
            // без клиента, продажа была офлайн), метод просто ничего не делает.
            try
            {
                ClientLoyaltyStore.ReverseRemainingForSale(_currentSaleId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Loyalty reversal on full return failed: {ex.Message}", "WARNING");
            }

            PosMessageBox.Show(this, Tr.T("Полный возврат чека оформлен.", "Чектин толук кайтарылышы таризделди.", "Full receipt return completed.", "Fişin tam iadesi tamamlandı.", "Chekning to'liq qaytarilishi rasmiylashtirildi."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshCurrentSaleAsync().ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            PosMessageBox.Show(this, ex.Message, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            PosMessageBox.Show(this, string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message,
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException)
        {
            PosMessageBox.Show(this, Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
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

    public sealed class ReturnSaleGridRow(ReturnSaleListItemVm item, int position)
    {
        public ReturnSaleListItemVm Item { get; } = item;
        public string SaleId => Item.SaleId;

        /// <summary>
        /// Читаемый номер вместо серверного GUID: настоящий номер чека, если сервер
        /// его прислал (receipt_number), иначе порядковый номер в списке.
        /// </summary>
        public string DisplayNumber => !string.IsNullOrWhiteSpace(Item.ReceiptNumber)
            ? Item.ReceiptNumber!
            : $"№{position}";

        public string SaleDateDisplay => Item.SaleDate == DateTime.MinValue
            ? "—"
            : Item.SaleDate.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        public string TotalAmountDisplay => $"{Item.TotalAmount:F2} сом";
    }
}
