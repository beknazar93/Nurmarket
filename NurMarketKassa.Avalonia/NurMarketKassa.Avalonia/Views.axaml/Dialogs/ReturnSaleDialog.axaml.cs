using NurMarketKassa.Core.Application;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
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

    /// <summary>2026-10-04, стресс-тест: итог выбранной продажи по серверу (поле total, уже за вычетом
    /// прошлых возвратов) — запасная сумма полного возврата, если сервер не отдал документ возврата.</summary>
    private decimal? _currentSaleTotal;

    /// <summary>2026-10-27 (ревью, См-5): <c>refundable_total</c> продажи — сколько ещё можно вернуть, если сервер уже отдаёт это поле (Б-09); null — нет.</summary>
    private decimal? _currentSaleRefundable;

    /// <summary>2026-10-06 (О-31, магазин одежды): дата выбранного чека — от неё считается срок обмена и возврата.</summary>
    private DateTime? _currentSaleDate;
    private int _salesPage;
    private readonly HashSet<string> _salesSeenIds = new(StringComparer.OrdinalIgnoreCase);
    private string _searchFilter = "";
    private bool _isBusy;
    private readonly string? _currentUserId;

    /// <summary>2026-10-15, ревью: идёт возврат/обмен (от первого подтверждения до конца). IsBusy раньше ставился только
    /// после диалогов подтверждения — двойной клик успевал запустить два возврата одного чека. Отдельный флаг, а не
    /// сам IsBusy: его по ходу сбрасывает перезагрузка списка (LoadSalesAsync).</summary>
    private bool _operationInProgress;

    /// <summary>2026-10-15, ревью: ключ идемпотентности возврата живёт до ответа сервера (как _pendingSalePayKey в
    /// PayDebtDialog): после «Превышено время ожидания» повтор ТОГО ЖЕ возврата (тот же чек, те же строки и количества)
    /// уходит с тем же ключом — сервер вернёт прежний итог, деньги и товар не вернутся дважды. Новый ключ — после
    /// успеха, отказа сервера по существу (4xx) или если состав возврата изменился. Static — переживает и
    /// повторное открытие окна.
    /// 2026-10-16, ревью: ключ, оставшийся после таймаута, когда сервер возврат всё-таки провёл, жил до конца работы
    /// кассы: следующий настоящий возврат того же состава получал от сервера итог прошлого, а касса печатала чек и
    /// выдавала деньги без возврата на сервере. Теперь (общий PendingIdempotencyKey): ключ привязан к чеку (id продажи
    /// в подписи), живёт не дольше 10 минут, а после ответа, который не дошёл, касса перечитывает чек и, если возврат
    /// уже прошёл, забывает ключ (см. ReportReturnIfAppliedAsync).</summary>
    private static readonly PendingIdempotencyKey PendingReturn = new(TimeSpan.FromMinutes(10));

    public ObservableCollection<ReturnSaleListItemVm> Sales { get; } = new();
    public ObservableCollection<ReturnSaleGridRow> FilteredSales { get; } = new();
    public ObservableCollection<ReturnSaleLineVm> Lines { get; } = new();

    public new event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Чек, который открыть сразу (кнопка «Вернуть…» в «Истории чеков», 2026-09-27):
    /// кассир уже нашёл его там и не должен искать второй раз.</summary>
    public string? InitialSaleId { get; set; }

    /// <summary>Номер этого чека, как его видел кассир, — печатается на чеке возврата.</summary>
    public string? InitialReceiptNumber { get; set; }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
                return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanReturnSelected));
            OnPropertyChanged(nameof(HasNoSales));
        }
    }

    /// <summary>2026-10-19, К1 (аудит 19.10, проблема 4): «Вернуть выбранные» активна, только когда отмечена хотя бы
    /// одна позиция, которую можно вернуть (то же условие, что берёт ReturnSelected_Click). Только вид кнопки.</summary>
    public bool CanReturnSelected => !IsBusy && Lines.Any(x => x.CanReturn && x.IsSelected);

    /// <summary>2026-10-19, К1: пустой список чеков — подсказка вместо пустой таблицы.</summary>
    public bool HasNoSales => !IsBusy && FilteredSales.Count == 0;

    public string NoSalesTitle => string.IsNullOrWhiteSpace(_searchFilter)
        ? Tr.T("Чеков пока нет", "Азырынча чектер жок", "No receipts yet", "Henüz fiş yok", "Hozircha cheklar yo'q")
        : Tr.T("Чек не найден", "Чек табылган жок", "Receipt not found", "Fiş bulunamadı", "Chek topilmadi");

    public string NoSalesHint => string.IsNullOrWhiteSpace(_searchFilter)
        ? Tr.T("Нажмите «Обновить» или введите номер чека в поиске.", "«Жаңыртуу» баскычын басыңыз же издөөгө чектин номерин жазыңыз.",
            "Press “Refresh” or type the receipt number in the search.", "«Yenile»ye basın veya aramaya fiş numarasını yazın.",
            "«Yangilash»ni bosing yoki qidiruvga chek raqamini yozing.")
        : Tr.T("Проверьте номер или нажмите «Ещё», чтобы загрузить более старые чеки.", "Номерди текшериңиз же эски чектерди жүктөө үчүн «Дагы» баскычын басыңыз.",
            "Check the number or press “More” to load older receipts.", "Numarayı kontrol edin veya eski fişleri yüklemek için «Daha fazla»ya basın.",
            "Raqamni tekshiring yoki eski cheklarni yuklash uchun «Yana»ni bosing.");

    private void RaiseSelectionState()
    {
        OnPropertyChanged(nameof(CanReturnSelected));
        // 2026-10-22 (модалки, пакет 1): подсказка «отметьте позицию» (строкой в окне) снимается, как только отметили.
        if (_inputError is not null && Lines.Any(x => x.CanReturn && x.IsSelected) && ErrorText.Text == _inputError)
        {
            ErrorText.IsVisible = false;
            ErrorText.Text = "";
            _inputError = null;
        }
    }

    private void OnReturnLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ReturnSaleLineVm.IsSelected) or nameof(ReturnSaleLineVm.CanReturn) or null)
            RaiseSelectionState();
    }

    /// <summary>2026-10-19, К1: двойной щелчок по строке чека — то же, что кнопка «Выбрать» этой строки.</summary>
    private async void SalesDataGrid_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        // 2026-10-21 (ревью денег, мелочь 4): пока идёт возврат (в т.ч. сверка «ответ не дошёл») — другой чек не открываем,
        // иначе разбивка возврата посчиталась бы по способу оплаты чужого чека.
        if (IsBusy || _operationInProgress || SalesDataGrid.SelectedItem is not ReturnSaleGridRow row || string.IsNullOrWhiteSpace(row.SaleId))
            return;
        if (e.Source is Avalonia.Visual v && Avalonia.VisualTree.VisualExtensions.FindAncestorOfType<Button>(v, true) is not null)
            return;
        await OpenSaleByIdAsync(row.SaleId.Trim()).ConfigureAwait(true);
    }

    /// <summary>2026-10-05: телефон — чеки сверху, состав чека снизу, высота окна делится между ними поровну
    /// (а не «по содержимому», как при общей автоподгонке); в таблице чеков без колонки «Дата»; «Оконный режим»
    /// на Android не нужен.</summary>
    private void ApplyNarrowLayout(bool narrow)
    {
        RootCard.Padding = new Avalonia.Thickness(narrow ? 12 : 24);
        RootCard.Margin = new Avalonia.Thickness(narrow ? 6 : 12);
        WindowModeButton.IsVisible = !narrow;
        var salesBlock = MainAreaGrid.Children[0];
        if (narrow)
        {
            MainAreaGrid.ColumnDefinitions = new ColumnDefinitions("*");
            MainAreaGrid.RowDefinitions = new RowDefinitions("*,12,*");
            Grid.SetRow(LinesBlock, 2);
            Grid.SetColumn(LinesBlock, 0);
        }
        else
        {
            MainAreaGrid.ColumnDefinitions = new ColumnDefinitions("*,16,*");
            MainAreaGrid.RowDefinitions = new RowDefinitions();
            Grid.SetRow(LinesBlock, 0);
            Grid.SetColumn(LinesBlock, 2);
        }
        Grid.SetRow(salesBlock, 0);
        Grid.SetColumn(salesBlock, 0);

        // Колонки: № чека, Дата, Сумма, «Выбрать».
        if (SalesDataGrid.Columns.Count >= 4)
        {
            SalesDataGrid.Columns[0].Width = new DataGridLength(narrow ? 96 : 110);
            SalesDataGrid.Columns[1].IsVisible = !narrow;
            SalesDataGrid.Columns[2].Width = narrow ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(110);
            SalesDataGrid.Columns[3].Width = new DataGridLength(narrow ? 96 : 110);
        }
    }

    public ReturnSaleDialog()
    {
        InitializeComponent();
        _currentUserId = App.CurrentUserId;
        DataContext = this;
        Lines.CollectionChanged += (_, _) => UpdateReceiptChrome();
        // 2026-10-19, К1: состояние «Вернуть выбранные» и пустого списка чеков — по отметкам строк и списку.
        Lines.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (ReturnSaleLineVm l in e.OldItems) l.PropertyChanged -= OnReturnLinePropertyChanged;
            if (e.NewItems != null)
                foreach (ReturnSaleLineVm l in e.NewItems) l.PropertyChanged += OnReturnLinePropertyChanged;
            RaiseSelectionState();
        };
        FilteredSales.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNoSales));
            OnPropertyChanged(nameof(NoSalesTitle));
            OnPropertyChanged(nameof(NoSalesHint));
        };
        Opened += OnFirstOpened;
        // 2026-10-05, снимок владельца с телефона: список чеков уходил под «Ввести ID продажи вручную» и
        // кнопки окна, колонка «Дата» сжималась в пустую полоску, суммы обрезались («20150,00 со»).
        NarrowLayout.Attach(this, 700, ApplyNarrowLayout);

        // Подписка на изменение свойств окна через стандартный PropertyChanged
        PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WindowState))
            {
                UpdateWindowStateUI();
            }
        };

        // 2026-10-06, исследование «Кассы для одежды» (О-30): обмен одним документом — в сфере «Одежда».
        ExchangeSelectedButton.Content = Tr.T("⇄  Обменять выбранное на другой товар", "⇄  Тандалганды башка товарга алмаштыруу",
            "⇄  Exchange selected for another item", "⇄  Seçileni başka ürünle değiştir", "⇄  Tanlanganini boshqa mahsulotga almashtirish");
        ExchangeSelectedButton.IsVisible = MarketSpheres.IsClothing;

        UpdateReceiptChrome();
        UpdateWindowStateUI();
    }

    private async void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        UpdateWindowStateUI();
        // 2026-10-04: сервер не ответил на список (авария объявлена, текст уже в окне) — чек из
        // «Истории чеков» тоже не открываем: это ещё один запрос к молчащему серверу.
        if (!await LoadSalesAsync(true).ConfigureAwait(true) && _serverDown)
            return;

        if (!string.IsNullOrWhiteSpace(InitialSaleId))
        {
            await OpenSaleByIdAsync(InitialSaleId.Trim()).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(_currentReceiptNumber) && !string.IsNullOrEmpty(_currentSaleId))
                _currentReceiptNumber = InitialReceiptNumber;
        }
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

    /// <summary>2026-10-04: подпись сбоя для журнала аварии (ServerOutageMonitor).</summary>
    private static string ReturnContext => Tr.T("возврат", "кайтаруу", "return", "iade", "qaytarish");

    /// <summary>2026-10-04: сервер не ответил на первую порцию данных окна — авария объявлена,
    /// в окне текст «возврат недоступен».</summary>
    private bool _serverDown;

    /// <summary>2026-10-04: идёт фоновая подгрузка следующей страницы («Ещё»).</summary>
    private bool _loadingMore;

    /// <summary>2026-10-04: «Обновить» во время фоновой подгрузки «Ещё» — её страница уже не к месту.</summary>
    private int _salesLoadGeneration;

    /// <summary>true — список загружен. 2026-10-04, отчёт «офлайн и сбои сервера»: первая страница
    /// ждёт сервер не дольше ServerAnswerWait.FirstPortionBudget (5 с с последнего ответа; раньше —
    /// до 55 с, пока касса ещё «Онлайн», а сервер уже молчит), сбой — авария и понятный текст в окне.
    /// Следующие страницы («Ещё») грузятся фоном: окно не блокируется, кнопки возврата доступны.</summary>
    private async Task<bool> LoadSalesAsync(bool reset)
    {
        if (!reset && _loadingMore)
            return false;

        ErrorText.IsVisible = false;
        ErrorText.Text = "";
        _serverDown = false;

        if (OfflineModeHelper.IsServerOutage)
        {
            // Касса уже знает, что сервер лежит, — не ждём его ещё раз.
            _serverDown = true;
            ShowErr(OfflineModeHelper.ReturnUnavailableInOutage);
            return false;
        }

        if (reset)
        {
            IsBusy = true;
            _salesLoadGeneration++;
            _salesPage = 1;
            Sales.Clear();
            _salesSeenIds.Clear();
        }
        else
        {
            _loadingMore = true;
            MoreSalesButton.IsEnabled = false;
            _salesPage++;
        }

        var page = reset ? 1 : _salesPage;
        var generation = _salesLoadGeneration;

        try
        {
            var jsonElementList = reset
                ? await ServerAnswerWait.FirstPortionAsync(ReturnContext,
                        ct => App.SalesApi.PosSalesListAsync(page, SalesPageSize, App.PosCashboxId, ct))
                    .ConfigureAwait(true)
                : await App.SalesApi.PosSalesListAsync(page, SalesPageSize, App.PosCashboxId)
                    .ConfigureAwait(true);

            // Пока шла фоновая страница, кассир нажал «Обновить» — список уже другой.
            if (!reset && generation != _salesLoadGeneration)
                return false;

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
                // 2026-09-28 (BE-08): постоянный номер продажи с сервера — тот же, что в «Истории
                // чеков», в «Продажах» и на печатном чеке (SalesWindow.TryReceiptNumber). Раньше
                // здесь был номер по месту в списке, который рос с каждой новой продажей.
                if (string.IsNullOrWhiteSpace(receiptNumber))
                    receiptNumber = SalesWindow.TryReceiptNumber(row);

                Sales.Add(new ReturnSaleListItemVm
                {
                    SaleId = saleId,
                    SaleDate = saleDate,
                    TotalAmount = totalAmount,
                    Summary = PosSaleRowFormatter.SummaryLine(row),
                    ReceiptNumber = string.IsNullOrWhiteSpace(receiptNumber) ? null : receiptNumber,
                    Status = SaleStatus(row),
                });
                added++;
            }

            ApplySalesFilter();

            if (reset && Sales.Count == 0)
                ShowErr(Tr.T("Список продаж пуст или API не вернул данные. Попробуйте «Обновить» или введите ID продажи вручную.",
                    "Сатуулар тизмеси бош же API маалымат кайтарган жок. «Жаңылоо» баскычын басыңыз же сатуунун ID’син өзүңүз киргизиңиз.",
                    "The sales list is empty or the API returned no data. Try “Refresh” or enter the sale ID manually.",
                    "Satış listesi boş veya API veri döndürmedi. «Yenile»yi deneyin veya satış ID'sini elle girin.",
                    "Sotuvlar ro'yxati bo'sh yoki API ma'lumot qaytarmadi. «Yangilash»ni bosib ko'ring yoki sotuv ID'sini qo'lda kiriting."));
            else if (!reset && added == 0)
            {
                // 2026-10-22 (модалки, пакет 1): сообщение — строкой в окне, а не всплывающим окном.
                ShowInputErr(Tr.T("Больше чеков нет — загружены все.", "Башка чек жок — баары жүктөлдү.", "No more receipts — all are loaded.", "Başka fiş yok — hepsi yüklendi.", "Boshqa chek yo'q — hammasi yuklandi."));
                _salesPage = Math.Max(1, _salesPage - 1);
            }

            return true;
        }
        catch (ServerNotAnsweringException ex)
        {
            // 2026-10-04: первая страница — сервер не ответил за 5 с или ответил сбоем: авария уже
            // объявлена (ServerAnswerWait), кассиру — что делать, а не «Превышено время ожидания» через 55 с.
            _serverDown = !ex.Throttled;
            ShowErr(ex.Throttled ? ServerAnswerWait.ThrottledMessage : OfflineModeHelper.ReturnUnavailableInOutage);
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
            if (reset)
            {
                IsBusy = false;
            }
            else
            {
                _loadingMore = false;
                MoreSalesButton.IsEnabled = true;
            }
        }

        return false;
    }

    private void ApplySalesFilter()
    {
        FilteredSales.Clear();
        var position = 0;
        // Поиск, кроме внутреннего SaleId (серверный GUID, не показывается пользователю),
        // должен уметь находить чек и по видимому "№N" — это то, что реально видно в списке.
        var trimmedFilter = (_searchFilter ?? "").Trim().TrimStart('№', '#');
        // 2026-09-28: «№1115», «1115» и «001115» — один и тот же постоянный номер чека.
        var filterNumber = NurCrmReportsApi.ParseSaleNumber(_searchFilter);
        foreach (var item in Sales)
        {
            position++;
            var matches = string.IsNullOrWhiteSpace(_searchFilter)
                || item.SaleId.Contains(_searchFilter.Trim(), StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(item.ReceiptNumber) &&
                    item.ReceiptNumber.Contains(trimmedFilter, StringComparison.OrdinalIgnoreCase)) // 2026-10-29: «№1115» и «#1115» находят чек (раньше искали с символом №)
                || (filterNumber is { } wanted && NurCrmReportsApi.ParseSaleNumber(item.ReceiptNumber) == wanted)
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
        // 2026-10-21 (ревью денег, мелочь 4): во время возврата другой чек не открываем (см. SalesDataGrid_DoubleTapped).
        if (IsBusy || _operationInProgress || sender is not Button { Tag: string saleId } || string.IsNullOrWhiteSpace(saleId))
            return;

        // 2026-10-21 (пакет 6, п. 35): строка выбранного чека выделяется в списке (было — слева ничего не менялось).
        SelectRowInGrid(saleId.Trim());
        await OpenSaleByIdAsync(saleId.Trim()).ConfigureAwait(true);
    }

    /// <summary>2026-10-21 (пакет 6, п. 35): выделить в таблице строку чека (после «Выбрать» и после перечитывания списка).</summary>
    private void SelectRowInGrid(string? saleId)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return;
        var row = FilteredSales.FirstOrDefault(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return;
        SalesDataGrid.SelectedItem = row;
        SalesDataGrid.ScrollIntoView(row, null);
    }

    /// <summary>2026-10-21 (пакет 6, п. 35): status продажи сервера — для пометки «возвращён» в списке.</summary>
    private static string? SaleStatus(JsonElement row) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? st.GetString()?.Trim().ToLowerInvariant()
            : null;

    /// <summary>2026-10-21 (пакет 6, п. 35): окно возврата — на месте и по размеру окна кассы, если касса не развёрнута
    /// (было: касса 1296 px, а возврат раскрывался на весь экран 1920). Развёрнутая касса — как раньше, на весь экран.</summary>
    public void FitToOwnerWindow(Window owner)
    {
        if (OperatingSystem.IsAndroid() || owner.WindowState is WindowState.Maximized or WindowState.FullScreen
            || owner.Bounds.Width < MinWidth || owner.Bounds.Height < MinHeight)
            return;
        WindowState = WindowState.Normal;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = owner.PointToScreen(new Avalonia.Point(0, 0));
        Width = owner.Bounds.Width;
        Height = owner.Bounds.Height;
    }

    private async void LoadById_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-21 (ревью денег, мелочь 4): во время возврата другой чек не открываем.
        if (_operationInProgress)
            return;
        var raw = (SaleIdBox.Text ?? "").Trim();
        if (raw.Length == 0)
        {
            ShowErr(Tr.T("Введите ID продажи.", "Сатуунун ID'син киргизиңиз.", "Enter the sale ID.", "Satış ID'sini girin.", "Sotuv ID'sini kiriting."));
            return;
        }

        // Пользователь видит в списке только "№N" (позиционный номер, не серверный GUID),
        // поэтому если он ввёл именно его — сначала пробуем найти по позиции в загруженном
        // списке, и только если не нашли — отправляем введённый текст как есть (вдруг это
        // настоящий ID, скопированный откуда-то ещё).
        //
        // 2026-09-28 (BE-08): у продажи на сервере теперь постоянный номер. Введённый номер ищем
        // сначала среди загруженных чеков, затем на сервере (?number=) — чек мог быть пробит давно
        // и в первые страницы списка не попасть. Номер по месту в списке — только если сервер
        // номеров не присылает (старый сервер).
        if (NurCrmReportsApi.ParseSaleNumber(raw) is { } saleNumber)
        {
            var loaded = Sales.FirstOrDefault(s => NurCrmReportsApi.ParseSaleNumber(s.ReceiptNumber) == saleNumber);
            if (loaded != null)
            {
                await OpenSaleByIdAsync(loaded.SaleId).ConfigureAwait(true);
                return;
            }

            IsBusy = true;
            JsonElement? found;
            try
            {
                // 2026-10-04: не дольше 5 с без ответа сервера (раньше — до 55 с), сбой — авария и текст.
                found = await ServerAnswerWait.FirstPortionAsync(ReturnContext,
                    ct => NurCrmReportsApi.FindSaleByNumberAsync(saleNumber, ct)).ConfigureAwait(true);
            }
            catch (ServerNotAnsweringException ex)
            {
                ShowErr(ex.Throttled ? ServerAnswerWait.ThrottledMessage : OfflineModeHelper.ReturnUnavailableInOutage);
                return;
            }
            finally
            {
                IsBusy = false;
            }

            if (found is { } foundSale && PosSaleRowFormatter.TrySaleId(foundSale) is { Length: > 0 } foundId)
            {
                AddFoundSale(foundSale, foundId);
                await OpenSaleByIdAsync(foundId).ConfigureAwait(true);
                return;
            }

            if (Sales.Any(s => !string.IsNullOrWhiteSpace(s.ReceiptNumber)))
            {
                ShowErr(Tr.T($"Чек №{saleNumber} не найден.", $"№{saleNumber} чек табылган жок.",
                    $"Receipt No. {saleNumber} was not found.", $"{saleNumber} numaralı fiş bulunamadı.",
                    $"№{saleNumber} chek topilmadi."));
                return;
            }
        }

        var trimmed = raw.TrimStart('№', '#');
        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
            && position >= 1 && position <= Sales.Count)
        {
            await OpenSaleByIdAsync(Sales[position - 1].SaleId).ConfigureAwait(true);
            return;
        }

        await OpenSaleByIdAsync(raw).ConfigureAwait(true);
    }

    /// <summary>Чек, найденный на сервере по номеру (2026-09-28, BE-08), — в список, если его там
    /// ещё нет: чтобы он был виден и выбран, как любой другой.</summary>
    private void AddFoundSale(JsonElement row, string saleId)
    {
        if (!_salesSeenIds.Add(saleId))
            return;

        _ = TryGetDateTime(row, "created_at", out var saleDate);
        _ = TryGetDecimal(row, "total", out var totalAmount);
        Sales.Insert(0, new ReturnSaleListItemVm
        {
            SaleId = saleId,
            SaleDate = saleDate,
            TotalAmount = totalAmount,
            Summary = PosSaleRowFormatter.SummaryLine(row),
            ReceiptNumber = SalesWindow.TryReceiptNumber(row),
            Status = SaleStatus(row),
        });
        ApplySalesFilter();
    }

    private CancellationTokenSource? _numberLookupCts;

    /// <summary>Строка поиска: номер, которого нет среди загруженных чеков, ищем на сервере
    /// (?number=, 2026-09-28) — с паузой, чтобы не спрашивать на каждую набранную цифру.</summary>
    private async Task LookupSaleByNumberAsync(long saleNumber, CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct).ConfigureAwait(true);
            var found = await NurCrmReportsApi.FindSaleByNumberAsync(saleNumber, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested || found is not { } sale
                || PosSaleRowFormatter.TrySaleId(sale) is not { Length: > 0 } id)
                return;
            AddFoundSale(sale, id);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: поиск чека №{saleNumber} на сервере не удался: {ex.Message}", "SALES");
        }
    }

    private async Task OpenSaleByIdAsync(string saleId)
    {
        ErrorText.IsVisible = false;
        ErrorText.Text = "";
        if (OfflineModeHelper.IsServerOutage)
        {
            ShowErr(OfflineModeHelper.ReturnUnavailableInOutage);
            return;
        }

        IsBusy = true;
        try
        {
            // 2026-10-04: не дольше 5 с без ответа сервера (раньше — до 55 с), сбой — авария и текст.
            var sale = await ServerAnswerWait.FirstPortionAsync(ReturnContext,
                ct => App.SalesApi.PosSaleGetAsync(saleId, ct)).ConfigureAwait(true);
            _currentSaleId = saleId;
            // Чека нет в загруженном списке (открыт из «Истории чеков» или по ID) — постоянный
            // номер берём из самой продажи (2026-09-28, BE-08): он же печатается на чеке возврата.
            _currentReceiptNumber = Sales.FirstOrDefault(x => x.SaleId == saleId)?.ReceiptNumber
                                    ?? SalesWindow.TryReceiptNumber(sale);
            FillLinesFromSale(sale);
            UpdateReceiptChrome();
            if (Lines.Count == 0)
                ShowErr(Tr.T("В ответе сервера нет позиций с идентификатором строки для возврата.",
                    "Сервердин жообунда кайтаруу үчүн сап идентификатору бар позициялар жок.",
                    "The server response has no items with a line ID that can be returned.",
                    "Sunucu yanıtında iade için satır kimliği olan kalem yok.",
                    "Server javobida qaytarish uchun qator identifikatoriga ega pozitsiyalar yo'q."));
        }
        catch (ServerNotAnsweringException ex)
        {
            ShowErr(ex.Throttled ? ServerAnswerWait.ThrottledMessage : OfflineModeHelper.ReturnUnavailableInOutage);
            _currentSaleId = null;
            Lines.Clear();
            UpdateReceiptChrome();
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

    /// <summary>2026-10-20 (К1–К3): выбранная продажа, как её заплатили, и возвраты по ней с этой кассы (null — касса
    /// чек до возвратов не видела: суммы — по серверу, как раньше).</summary>
    private SaleLedgerEntry? _ledger;

    /// <summary>2026-10-27 (аудит кассы, В-3): у чека нет записи кассы, но часть позиций уже возвращали (на сайте/другой кассе) — сумма
    /// возврата считается по данным сервера, кассиру показываем оценку «возвращено ранее».</summary>
    private bool _noLedgerTouched;
    private decimal _noLedgerPriorReturned;

    /// <summary>2026-10-20 (К2, К3): payment_method продажи с сервера — если касса чек до возвратов не видела.</summary>
    private string? _currentSaleMethod;

    /// <summary>2026-10-21 (ревью денег, мелочь 4): payment_method каждого открытого чека — расчёт возврата берёт способ
    /// оплаты своего чека, даже если на экране уже другой.</summary>
    private readonly Dictionary<string, string?> _saleMethods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-21 (ревью денег, п. 2): текущий остаток долга чека «в долг» с сервера (remaining_debt сделки),
    /// запрошенный при открытии чека — до возврата. null в результате — остаток не получен. Берётся одним возвратом.</summary>
    private readonly Dictionary<string, Task<decimal?>> _serverDebtLeft = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-20 (К8): id строки → единиц остатка каталога в одной единице строки.</summary>
    private readonly Dictionary<string, double> _stockUnitsPerQty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-20 (К1): у продажи ещё нет возвратов — запоминаем её строки и итог (SaleReturnLedger); иначе — что
    /// касса запомнила раньше (при оплате или первом открытии).</summary>
    private SaleLedgerEntry? RememberSaleForReturns(JsonElement sale)
    {
        if (string.IsNullOrWhiteSpace(_currentSaleId) || sale.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            var items = CartDisplayHelper.EnumerateSaleLineItems(sale).ToList();
            var untouched = items.Count > 0
                && !LinesReturnedSince(sale, new Dictionary<string, double>())
                && items.All(it => !CartDisplayHelper.LineLooksFullyReturned(it)
                                   && CartDisplayHelper.RefundableQuantity(it) >= CartDisplayHelper.LineQuantity(it) - 1e-6);
            var known = SaleReturnLedger.Get(_currentSaleId);
            if (!untouched || _currentSaleTotal is not { } total || known is { Refunds.Count: > 0 })
                return known;

            var lines = new List<SaleLedgerLine>();
            foreach (var it in items)
            {
                decimal.TryParse(CartDisplayHelper.LineTotal(it), NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotal);
                lines.Add(new SaleLedgerLine
                {
                    LineId = CartDisplayHelper.TryRefundLineId(it),
                    Name = CartDisplayHelper.ItemName(it),
                    Quantity = CartDisplayHelper.LineQuantity(it),
                    UnitPrice = (decimal)CartDisplayHelper.UnitPrice(it),
                    Total = lineTotal,
                });
            }

            var method = sale.TryGetProperty("payment_method", out var pm) && pm.ValueKind == JsonValueKind.String ? pm.GetString() : null;
            decimal? received = TryGetDecimal(sale, "cash_received", out var r) ? r : null;
            decimal? change = TryGetDecimal(sale, "change", out var c) ? c : null;
            return SaleReturnLedger.RememberFromSale(_currentSaleId, _currentReceiptNumber, total, lines.Sum(l => l.Total), lines,
                method, received, change, _currentSaleDate is { } d ? new DateTimeOffset(d) : null);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: чек {_currentSaleId} не запомнен для расчёта суммы возврата: {ex.Message}", "WARNING");
            return SaleReturnLedger.Get(_currentSaleId);
        }
    }

    /// <summary>2026-10-21 (ревью денег, п. 2): журнал кассы знает возвраты, но не оплаты долга — клиент мог погасить долг
    /// по этому чеку (в кассе, у владельца, на соседней кассе). Остаток долга по сделке — с сервера, запросом при открытии
    /// чека (до возврата), чтобы возврат не уменьшал долг, которого уже нет.</summary>
    private void StartServerDebtLeftLookup(JsonElement sale)
    {
        if (string.IsNullOrWhiteSpace(_currentSaleId) || sale.ValueKind != JsonValueKind.Object)
            return;
        var saleId = _currentSaleId;
        var method = (_ledger?.Method is { Length: > 0 } known ? known : _currentSaleMethod ?? "").Trim().ToLowerInvariant();
        if (method != "debt" && _ledger is not { Debt: > 0.005m })
        {
            _serverDebtLeft.Remove(saleId);
            return;
        }

        string? IdOf(string key) => sale.TryGetProperty(key, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;
        var clientId = IdOf("client");
        var dealId = IdOf("deal_id");
        // Сделки нет — оплат через сделку не было, остаток самой продажи точный (как в PayDebtDialog).
        decimal? saleOwn = TryGetDecimal(sale, "debt_remaining", out var dr) ? dr
            : TryGetDecimal(sale, "remaining_debt", out var rd) ? rd
            : TryGetDecimal(sale, "debt_amount", out var da) ? da
            : null;
        _serverDebtLeft[saleId] = FetchServerDebtLeftAsync(saleId, clientId, dealId, saleOwn);
    }

    private static async Task<decimal?> FetchServerDebtLeftAsync(string saleId, string? clientId, string? dealId, decimal? saleOwn)
    {
        if (string.IsNullOrWhiteSpace(dealId))
            return saleOwn is { } own ? Math.Max(0m, own) : null;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            PosLogger.Log($"Возврат по продаже {saleId}: остаток долга не получен (у продажи нет клиента).", "WARNING");
            return null;
        }

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var deal = await App.SalesApi.ClientDealGetAsync(clientId, dealId, cts.Token).ConfigureAwait(false);
            if (deal.ValueKind == JsonValueKind.Object && TryGetDecimal(deal, "remaining_debt", out var remaining))
                return Math.Max(0m, remaining);
            PosLogger.Log($"Возврат по продаже {saleId}: в сделке нет remaining_debt — остаток долга неизвестен.", "WARNING");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат по продаже {saleId}: остаток долга с сервера не получен ({ex.GetType().Name}).", "WARNING");
        }

        return null;
    }

    private void FillLinesFromSale(JsonElement sale)
    {
        Lines.Clear();
        _stockUnitsPerQty.Clear();
        _currentSaleTotal = sale.ValueKind == JsonValueKind.Object
                            && sale.TryGetProperty("total", out var saleTotalEl)
                            && decimal.TryParse(saleTotalEl.ValueKind == JsonValueKind.String ? saleTotalEl.GetString() : saleTotalEl.GetRawText(),
                                NumberStyles.Number, CultureInfo.InvariantCulture, out var saleTotalValue)
            ? saleTotalValue
            : null;
        _currentSaleRefundable = sale.ValueKind == JsonValueKind.Object
                                 && sale.TryGetProperty("refundable_total", out var refundableEl)
                                 && refundableEl.ValueKind is JsonValueKind.String or JsonValueKind.Number
                                 && decimal.TryParse(refundableEl.ValueKind == JsonValueKind.String ? refundableEl.GetString() : refundableEl.GetRawText(),
                                     NumberStyles.Number, CultureInfo.InvariantCulture, out var refundableValue)
            ? refundableValue
            : null;
        // 2026-10-06 (О-31): дата чека — для срока обмена и возврата.
        _currentSaleDate = sale.ValueKind == JsonValueKind.Object
                           && (TryGetDateTime(sale, "created_at", out var soldAt) || TryGetDateTime(sale, "paid_at", out soldAt))
            ? soldAt
            : Sales.FirstOrDefault(x => string.Equals(x.SaleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))?.SaleDate is { } listed && listed > DateTime.MinValue
                ? listed
                : null;
        // 2026-10-20 (живой аудит кассы, К1): сервер считает возврат строки без доли скидки на чек и после первого
        // частичного возврата убирает скидку из продажи (001837: 80 − 5 = 75, вернул 30 + 50 = 80). Чек без возвратов
        // запоминаем как есть (SaleReturnLedger): итог, сумма строк до скидки на чек, строки с id; сумма к возврату
        // строки = её доля с учётом скидки на чек, все возвраты чека вместе — не больше оплаченного.
        _currentSaleMethod = sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("payment_method", out var payMethod)
            && payMethod.ValueKind == JsonValueKind.String ? payMethod.GetString() : null;
        _ledger = RememberSaleForReturns(sale);
        if (!string.IsNullOrWhiteSpace(_currentSaleId))
            _saleMethods[_currentSaleId] = _currentSaleMethod;
        StartServerDebtLeftLookup(sale);
        var ledgerLines = _ledger?.Lines
            .Where(l => !string.IsNullOrEmpty(l.LineId))
            .GroupBy(l => l.LineId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        // 2026-10-27 (аудит кассы, В-3): своей записи о чеке нет (первый частичный возврат сделан на сайте/другой кассе) — раньше доля
        // скидки на чек была 1, и возврат шёл по полной цене строки: сумма возвратов могла превысить заплаченное. Теперь доля — из итога
        // и суммы строк, как их отдаёт сервер (итог = строки − скидка на чек), а ранее возвращённое оценивается для подсказки.
        _noLedgerPriorReturned = 0m;
        _noLedgerTouched = false;
        var factor = _ledger?.Factor ?? 1m;
        if (_ledger is null)
        {
            var linesSum = 0m;
            foreach (var each in CartDisplayHelper.EnumerateSaleLineItems(sale))
            {
                if (decimal.TryParse(CartDisplayHelper.LineTotal(each), NumberStyles.Any, CultureInfo.InvariantCulture, out var eachTotal))
                    linesSum += eachTotal;
            }

            factor = ReturnShareEstimator.Factor(_currentSaleTotal, linesSum);
        }
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
            var refundPerUnit = 0m; // 2026-10-27 (Д-5): сумма возврата за единицу строки без округления — для частичного возврата по количеству
            // 2026-10-20 (К1): строка — как в продаже до возвратов (снимок), и её доля скидки на весь чек.
            if (ledgerLines is not null && ledgerLines.TryGetValue(lineId, out var was) && was.Quantity > 0)
            {
                originalQty = was.Quantity;
                lineTotal = CartDisplayHelper.FormatMoney((double)was.Total);
            }
            if (originalQty > 0 &&
                decimal.TryParse(lineTotal, NumberStyles.Any, CultureInfo.InvariantCulture, out var lineTotalValue))
            {
                refundSumValue = Math.Round(lineTotalValue * (decimal)qty / (decimal)originalQty * factor, 2, MidpointRounding.AwayFromZero);
                refundPerUnit = lineTotalValue / (decimal)originalQty * factor;
                refundSum = CartDisplayHelper.FormatMoney((double)refundSumValue);
                if (_ledger is null && qty < originalQty - 1e-6)
                {
                    _noLedgerTouched = true;
                    _noLedgerPriorReturned += ReturnShareEstimator.PriorReturned(lineTotalValue, originalQty, qty, factor);
                }
            }

            string? refundReason = null;
            if (lineItem.TryGetProperty("refund_reason", out var rr) && rr.ValueKind == JsonValueKind.String)
                refundReason = rr.GetString();
            else if (lineItem.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                refundReason = r.GetString();

            // 2026-10-20 (К8): сколько единиц остатка каталога в одной единице строки (продажа штуками из упаковки).
            var tile = CatalogCacheService.Products.FirstOrDefault(p =>
                string.Equals(p.Id, CartDisplayHelper.TryProductId(lineItem), StringComparison.OrdinalIgnoreCase));
            var lineQtyNow = CartDisplayHelper.LineQuantity(lineItem);
            _stockUnitsPerQty[lineId] = lineQtyNow > 0 ? CartDisplayHelper.LineQuantityInStockUnits(lineItem, tile) / lineQtyNow : 1.0;

            Lines.Add(new ReturnSaleLineVm
            {
                LineId = lineId,
                ProductId = CartDisplayHelper.TryProductId(lineItem),
                Quantity = qty,
                OriginalQuantity = originalQty,
                RefundPerUnit = refundPerUnit,
                // 2026-10-27 (Д-5): весовой/дробный товар — количество к возврату дробное; штучный — целое.
                AllowFractionalQuantity = tile is { IsWeighted: true } || tile is { MustWeigh: true }
                    || Math.Abs(qty - Math.Round(qty)) > 1e-6 || Math.Abs(originalQty - Math.Round(originalQty)) > 1e-6,
                Title = title,
                SubLine = subLine,
                // 2026-10-19, К1: сумма к возврату — «1 234,50 сом» (MoneyText), крупно справа в строке; сама сумма прежняя.
                LineSumText = decimal.TryParse(refundSum, NumberStyles.Any, CultureInfo.InvariantCulture, out var refundShown)
                    ? MoneyText.Som(refundShown)
                    : Tr.T($"{refundSum} сом", $"{refundSum} сом", $"{refundSum} som", $"{refundSum} som", $"{refundSum} so'm"),
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

    // 2026-10-27 (приёмка 1.17.63, Д-5): сколько вернуть из строки — кнопки −/+ и ввод числа. Изменение количества отмечает строку.
    private void ReturnQtyLess_Click(object? sender, RoutedEventArgs e) => StepReturnQuantity(sender, -1);

    private void ReturnQtyMore_Click(object? sender, RoutedEventArgs e) => StepReturnQuantity(sender, +1);

    private void StepReturnQuantity(object? sender, int direction)
    {
        if (IsBusy || _operationInProgress || (sender as Control)?.DataContext is not ReturnSaleLineVm line || !line.CanEditQuantity)
            return;
        line.ChangeReturnQuantity(direction);
        line.IsSelected = true;
        HideQtyError();
    }

    private void HideQtyError()
    {
        if (_inputError is not null && ErrorText.Text == _inputError)
            ErrorText.IsVisible = false;
    }

    private void ReturnQtyBox_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != Avalonia.Input.Key.Enter)
            return;
        e.Handled = true;
        CommitReturnQuantity(sender);
    }

    private void ReturnQtyBox_LostFocus(object? sender, RoutedEventArgs e) => CommitReturnQuantity(sender);

    private void CommitReturnQuantity(object? sender)
    {
        if (sender is not TextBox { DataContext: ReturnSaleLineVm line } box || IsBusy || _operationInProgress)
            return;
        var typed = box.Text;
        // Текст не менялся (то же число) — ничего не делаем, строку не отмечаем.
        if (string.Equals((typed ?? "").Trim(), line.ReturnQuantityText, StringComparison.Ordinal))
            return;
        if (line.TrySetReturnQuantity(typed))
        {
            line.IsSelected = true;
            HideQtyError();
        }
        else
        {
            var max = line.Quantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');
            ShowInputErr(line.AllowFractionalQuantity
                ? Tr.T($"Количество к возврату — число больше 0 и не больше {max}.", $"Кайтаруу саны — 0дөн чоң жана {max}дан ашпаган сан.", $"The return quantity must be more than 0 and not more than {max}.", $"İade miktarı 0'dan büyük ve en fazla {max} olmalı.", $"Qaytarish miqdori 0 dan katta va {max} dan oshmasligi kerak.")
                : Tr.T($"У штучного товара количество целое: от 1 до {max}.", $"Даана товардын саны бүтүн: 1ден {max}ге чейин.", $"Whole pieces only: from 1 to {max}.", $"Adetli üründe miktar tam sayı: 1 ile {max} arası.", $"Donalab sotiladigan mahsulotda miqdor butun: 1 dan {max} gacha."));
        }
        // Всегда показываем принятое значение (при ошибке — прежнее), а не набранный мусор.
        box.Text = line.ReturnQuantityText;
    }

    private void ClearReturnSelection_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var line in Lines)
            line.IsSelected = false;
    }

    private async void ReturnSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSaleId) || IsBusy || _operationInProgress)
            return;

        // 2026-10-15, ревью: защита от двойного клика — до первого подтверждения, снимается на любом выходе.
        _operationInProgress = true;
        IsBusy = true;
        try
        {
            await ReturnSelectedCoreAsync(_currentSaleId).ConfigureAwait(true);
        }
        finally
        {
            _operationInProgress = false;
            IsBusy = false;
        }
    }

    /// <summary>2026-10-27 (приёмка 1.17.63, Д-5): копии отмеченных строк для оформления возврата: Quantity и RefundSum — то, что
    /// кассир выбрал вернуть (вся строка или часть по количеству). Дальше возврат, журнал, остаток, чек и обмен работают как раньше.</summary>
    private static List<ReturnSaleLineVm> EffectiveReturnLines(IEnumerable<ReturnSaleLineVm> picked) =>
        picked.Select(l => new ReturnSaleLineVm
        {
            LineId = l.LineId,
            Title = l.Title,
            SubLine = l.SubLine,
            LineSumText = l.ReturnSumText,
            RefundSum = l.ReturnRefundSum,
            ProductId = l.ProductId,
            Quantity = l.ReturnQuantity,
            OriginalQuantity = l.OriginalQuantity,
            RemainingQuantity = l.Quantity,
            AllowFractionalQuantity = l.AllowFractionalQuantity,
            RefundPerUnit = l.RefundPerUnit,
            CanReturn = l.CanReturn,
            RefundReason = l.RefundReason,
        }).ToList();

    /// <summary>2026-10-27 (Д-5): в подтверждении возврата — какие строки возвращаются частично: «клей: 1 / 2».</summary>
    private static string PartialReturnNote(IEnumerable<ReturnSaleLineVm> selected)
    {
        var parts = selected.Where(l => l.Quantity < l.MaxReturnQuantity - 1e-6)
            .Select(l => $"{l.Title}: {l.Quantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',')} / {l.MaxReturnQuantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',')}")
            .ToList();
        return parts.Count == 0
            ? ""
            : "\n" + Tr.T("Частично: ", "Жарым-жартылай: ", "Partially: ", "Kısmen: ", "Qisman: ") + string.Join("; ", parts);
    }

    /// <summary>2026-10-20 (К1), 2026-10-27 (Д-5): сумма возврата отмеченных строк. Все возвраты чека вместе — не больше оплаченного;
    /// последний возврат (отмечены все строки целиком) — ровно остаток чека без копеек округления. Частичный по количеству — только
    /// доля строки, остаток чека не «дочищается». Чистая функция — проверяется на стенде.</summary>
    internal static decimal ComputeSelectedTotal(IReadOnlyList<ReturnSaleLineVm> selected, int returnableLines, SaleLedgerEntry? ledgerEntry,
        decimal? saleTotal, decimal? refundableTotal)
    {
        var total = selected.Sum(line => line.RefundSum);
        // 2026-10-20 (К1): все возвраты чека вместе — не больше оплаченного; последние строки чека — ровно остаток
        // (без копеек округления). Касса чек до возвратов не видела (_ledger null) — как раньше, по строкам.
        if (ledgerEntry is { Total: > 0m } ledger)
        {
            // Каждая отмеченная строка возвращается целиком (весь её остаток) — отмечены все: это весь остаток чека.
            // Копейки округления — в последний возврат; возвраты мимо этой кассы (сайт) остаток не увеличат.
            var returnsAllLeft = selected.Count == returnableLines
                && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6);
            total = returnsAllLeft && ledger.Remaining <= total + 0.01m * selected.Count
                ? ledger.Remaining
                : Math.Min(total, ledger.Remaining);
        }
        else
        {
            // 2026-10-27 (В-3): записи кассы о чеке нет — сумма не больше итога чека, как его сейчас отдаёт сервер.
            total = ReturnShareEstimator.Cap(total, saleTotal);
        }
        // 2026-10-27 (ревью, См-5): сервер сам сказал, сколько ещё можно вернуть (refundable_total) — это точнее и записи кассы, и оценки по итогу чека.
        return ReturnShareEstimator.CapRefundable(total, refundableTotal);
    }

    private async Task ReturnSelectedCoreAsync(string saleId)
    {
        // 2026-10-27 (приёмка 1.17.63, Д-5): дальше везде — строки с тем количеством, которое кассир выбрал (по умолчанию всё).
        var selected = EffectiveReturnLines(Lines.Where(x => x.CanReturn && x.IsSelected));
        if (selected.Count == 0)
        {
            ShowInputErr(Tr.T("Отметьте галочками хотя бы одну позицию для возврата.", "Кайтаруу үчүн жок дегенде бир позицияны белгилеңиз.", "Check at least one item to return.", "İade için en az bir kalemi işaretleyin.", "Qaytarish uchun kamida bitta pozitsiyani belgilang."));
            return;
        }

        var total = ComputeSelectedTotal(selected, Lines.Count(x => x.CanReturn), _ledger, _currentSaleTotal, _currentSaleRefundable);
        var linesSum = selected.Sum(line => line.RefundSum);

        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        // 2026-10-20 (К1): сумма — с долей скидки на чек, без «~» и в виде «1 234,50 сом».
        var totalShown = MoneyText.Som(total);
        // 2026-10-27 (В-3): чек уже частично возвращали не на этой кассе — сумму подтверждает кассир с цифрами перед глазами.
        var elsewhere = _ledger is null && _noLedgerTouched
            ? "\n\n" + Tr.T($"Чек уже возвращали не на этой кассе (сайт или другая касса). Сумма рассчитана по данным сервера: по чеку оплачено {MoneyText.Som(_currentSaleTotal ?? 0m)}, ранее возвращено примерно {MoneyText.Som(_noLedgerPriorReturned)}. Сверьте сумму.",
                $"Чек бул кассада эмес (сайт же башка касса) мурун кайтарылган. Сумма сервер маалыматы боюнча эсептелди: чек боюнча төлөнгөн {MoneyText.Som(_currentSaleTotal ?? 0m)}, мурун болжол менен {MoneyText.Som(_noLedgerPriorReturned)} кайтарылган. Сумманы текшериңиз.",
                $"This receipt was already returned elsewhere (website or another till). The amount is calculated from server data: paid {MoneyText.Som(_currentSaleTotal ?? 0m)}, returned earlier about {MoneyText.Som(_noLedgerPriorReturned)}. Please check the amount.",
                $"Bu fiş daha önce bu kasada değil (web sitesi veya başka kasa) iade edilmiş. Tutar sunucu verisinden hesaplandı: ödenen {MoneyText.Som(_currentSaleTotal ?? 0m)}, daha önce yaklaşık {MoneyText.Som(_noLedgerPriorReturned)} iade edilmiş. Tutarı kontrol edin.",
                $"Bu chek avval bu kassada emas (sayt yoki boshqa kassa) qaytarilgan. Summa server ma'lumoti bo'yicha hisoblandi: to'langan {MoneyText.Som(_currentSaleTotal ?? 0m)}, avval taxminan {MoneyText.Som(_noLedgerPriorReturned)} qaytarilgan. Summani tekshiring.")
            : "";
        if (await PosMessageBox.ShowModalAsync(this,
                Tr.T($"Выбрано позиций: {selected.Count}\nСумма возврата: {totalShown}",
                    $"Тандалган позиция: {selected.Count}\nКайтаруу суммасы: {totalShown}",
                    $"Items selected: {selected.Count}\nRefund amount: {totalShown}",
                    $"Seçilen kalem: {selected.Count}\nİade tutarı: {totalShown}",
                    $"Tanlangan pozitsiyalar: {selected.Count}\nQaytarish summasi: {totalShown}") + PartialReturnNote(selected) + elsewhere,
                Tr.T("Подтверждение возврата", "Кайтарууну ырастоо", "Confirm return", "İadeyi onayla", "Qaytarishni tasdiqlash"),
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                // 2026-10-22 (модалки, пакет 1): кнопки — глагол с суммой («Вернуть 1 620,00 сом» / «Отмена») вместо «Продолжить?».
                confirmText: Tr.T($"Вернуть {totalShown}", $"{totalShown} кайтаруу", $"Refund {totalShown}", $"{totalShown} iade et", $"{totalShown} qaytarish")).ConfigureAwait(true) != MessageBoxResult.Yes)
            return;

        // 2026-10-06 (О-31, О-32): срок обмена и товары «без обмена» — в сфере «Одежда».
        var defectPreset = await CheckReturnRulesAsync(selected, exchange: false).ConfigureAwait(true);
        if (defectPreset is null)
            return;

        var reasonDialog = new ReturnLineReasonDialog(selected.Count) { IsDefect = defectPreset == true };
        if (await PosDialogHost.ShowModalAsync(reasonDialog, this).ConfigureAwait(true) != true)
            return;

        if (!string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))
            return;

        var reason = reasonDialog.ReasonText;
        var isDefect = reasonDialog.IsDefect;
        // 2026-10-16, ревью: сколько было к возврату по строкам до отправки — для сверки, если ответ не дойдёт.
        var refundableBefore = RefundableByLine(selected);
        var outcomeUnknown = false;
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

            // 2026-10-15, ревью: тот же чек и те же строки/количества — тот же ключ (см. PendingReturn).
            var returnKey = PendingReturn.Get("lines|" + saleId + "|" + string.Join(";", requests
                .OrderBy(r => r.LineId, StringComparer.Ordinal)
                .Select(r => r.LineId + ":" + r.Quantity.ToString("0.###", CultureInfo.InvariantCulture))));

            var cashBefore = await ReadDrawerBeforeRefundAsync().ConfigureAwait(true);
            await PosRefundService.RefundLinesAsync(
                App.SalesApi,
                saleId,
                requests,
                reason,
                App.PosCashboxId,
                isDefect,
                idempotencyKey: returnKey).ConfigureAwait(true);
            PendingReturn.OnSuccess();

            SaleDetailCache.Forget(saleId);

            // 2026-10-04, стресс-тест: сумма возврата — та, что записал сервер (документ возврата).
            // Оценка выше (сумма строк) не знает скидки на чек: сервер хранит её у продажи, а не в
            // строках, и чек возврата печатал больше, чем сервер вернул (полный возврат №1348: касса
            // 56,00, сервер 50,40). Сервер не ответил — остаётся оценка, как раньше.
            // 2026-10-20 (живой аудит, К1–К3): сумма — расчёт кассы с долей скидки на чек (сервер её не учитывает и
            // мог вернуть больше оплаченного — тогда его число кассиру не показываем, а пишем в журнал и в
            // «Некорректные чеки»); к выдаче — только внесённые деньги (наличными / на карту), долг уменьшается.
            var estimatedTotal = linesSum;
            var outcome = await ResolveRefundAsync(saleId, total, whole: false,
                selected.Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList())
                .ConfigureAwait(true);
            total = outcome.Amount;
            CheckDrawerAfterRefund(cashBefore, outcome.Cash, "lines|" + returnKey, saleId, outcome.NonCash);

            // В итогах смены на сервере возвратов нет вовсе — записываем сами, иначе кассир
            // при закрытии смены их не увидит (см. ShiftEventsStore).
            ShiftEventsStore.Record(
                ShiftEventsStore.KindReturn,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(saleId),
                (double)total,
                outcome.Note);
            // 2026-10-20 (К8): остаток вернувшегося товара — сразу (брак на склад не возвращается).
            if (!isDefect)
                ReturnToStock(selected);

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
                    ClientLoyaltyStore.ReverseRemainingForSale(saleId);
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
            var printLines = ScaleRefundLines(selected.Select(l => (
                Name: l.Title,
                Quantity: l.Quantity > 0 ? l.Quantity : 1,
                UnitPrice: l.Quantity > 0 ? l.RefundSum / (decimal)l.Quantity : l.RefundSum,
                Sum: l.RefundSum)).ToList(), estimatedTotal, total);
            var printReceiptNumber = _currentReceiptNumber;
            var printCashier = NurMarketKassa.PosApp.CurrentUserDisplayName;
            var printError = await Task.Run(() => OperationReceiptPrinter.PrintReturn(
                printReceiptNumber,
                printLines,
                total,
                reason,
                isWholeSale: false,
                printCashier)).ConfigureAwait(true);

            // 2026-10-20 (живой аудит, К7): ошибка печати — строкой в том же итоговом окне, а не отдельным окном
            // (было 4 окна на один возврат: подтверждение, причина, ошибка печати, итог).
            // 2026-10-23 (аудит К6): возврат прошёл — это не ошибка; предупреждение о принтере показываем как info (без «Отложить ошибку»).
            var returnDoneText = (selected.Count == 1
                    ? Tr.T("Возврат оформлен.", "Кайтаруу таризделди.", "Return completed.", "İade tamamlandı.", "Qaytarish rasmiylashtirildi.")
                    : Tr.T($"Возврат оформлен ({selected.Count} поз.).", $"Кайтаруу таризделди ({selected.Count} поз.).", $"Return completed ({selected.Count} items).", $"İade tamamlandı ({selected.Count} kalem).", $"Qaytarish rasmiylashtirildi ({selected.Count} poz.)."))
                    // 2026-10-20 (К2, К3): к выдаче — внесённые деньги с разбивкой; у долга — на сколько уменьшен долг.
                    + "\n" + RefundToGiveText(outcome)
                    + ReturnPrintNote(printError);
            if (printError is null)
                PosMessageBox.Show(this, returnDoneText, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                PosAlertDialog.Show(this, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), returnDoneText, PosAlertKind.Warning, null, info: true);
        }
        catch (ApiException ex) when (ex.StatusCode == 502 && ex.Message.Contains("Возвращено позиций", StringComparison.Ordinal))
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ex.Message, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (ApiException ex)
        {
            // 2026-10-16, ревью: 4xx (кроме 408/429) — ключ забыт; иначе исход неизвестен, ключ оставлен для повтора.
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ex.Message, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message,
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        // 2026-10-16, ревью: ответ не дошёл — сервер мог провести возврат; сверяем по чеку (см. ReportReturnIfAppliedAsync).
        if (outcomeUnknown)
            await ReportReturnIfAppliedAsync(saleId, refundableBefore, total,
                reverseLoyalty: selected.Count == Lines.Count(x => x.CanReturn)
                                && selected.All(l => l.OriginalQuantity <= 0 || l.Quantity >= l.OriginalQuantity - 1e-5),
                returnedLines: selected, isDefect: isDefect).ConfigureAwait(true);
        await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
    }

    /// <summary>2026-10-04, стресс-тест на тестовом аккаунте: документ только что оформленного возврата
    /// так, как его записал сервер (GET pos/returns/?sale=…, самый новый), и сколько денег он выдал (движения
    /// «Возврат по чеку»; у продажи «в долг» документ — на весь чек, а выдано только внесённое).
    /// 2026-10-20 (живой аудит, К1–К3): только цифры сервера для сверки — сумму к выдаче считает ResolveRefundAsync.
    /// Документ, уже записанный в журнал прошлым возвратом этой кассы, — не этот возврат (сервер мог не успеть
    /// показать новый). null — сервер не ответил.</summary>
    private static async Task<(decimal? Doc, string? DocId, decimal? CashOut)> ServerReturnFiguresAsync(string? saleId, SaleLedgerEntry? ledger)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return (null, null, null);
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
            var returns = await NurCrmReportsApi.ListReturnsAsync(null, null, saleId: saleId, ct: cts.Token).ConfigureAwait(true);
            var seen = ledger?.Refunds.Select(r => r.ServerDocId).Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string?>();
            var newest = returns?
                .Where(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase) && r.Amount > 0m && !seen.Contains(r.Id))
                .OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            if (newest is null)
                return (null, null, null);
            var cashOut = await NurCrmReportsApi.ReturnCashOutAsync(saleId, newest.CreatedAt, cts.Token).ConfigureAwait(true);
            return (newest.Amount, newest.Id, cashOut);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Сумма возврата с сервера не получена, берём расчёт кассы: {ex.Message}", "WARNING");
            return (null, null, null);
        }
    }

    /// <summary>2026-10-20 (живой аудит, К1–К3): что вернули по этому возврату. Amount — стоимость возвращённого (с долей
    /// скидки на чек, не больше остатка оплаченного); Cash/NonCash — что выдать покупателю наличными и на карту/перевод;
    /// DebtReduced — на сколько уменьшился долг клиента (продажа «в долг»). SplitKnown=false — как заплатили, касса не знает
    /// (чек до возвратов не видела, смешанная оплата): показывается одна сумма.</summary>
    /// <param name="DebtUnknown">2026-10-21 (ревью денег, п. 2): продажа «в долг», а текущий остаток долга с сервера не получен —
    /// уменьшение долга посчитано по журналу кассы (оплаты долга он не знает): кассиру — предупреждение, а не «Выдать 0,00».</param>
    /// <param name="ServerOver">2026-10-21 (ревью денег, мелочь 5): на сколько сервер записал возврат больше выданного кассой
    /// (скидка на чек не учтена сервером, ТЗ 19.9) — окно закрытия смены объясняет «излишек» в ящике.</param>
    /// <summary>2026-10-22 (аудит 1.17.59, П-А, Б1): остаток смены на сервере до возврата — сервер не вычитает наличные при
    /// возврате долгового чека, и шапка «Касса» оставалась завышенной (см. ExchangeCashCorrections). null — не узнали.</summary>
    private static async Task<decimal?> ReadDrawerBeforeRefundAsync()
    {
        var shiftId = PosApp.ActiveShiftId;
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;
        return await ExchangeCashCorrections.ReadExpectedCashAsync(App.GetRequiredService<NurMarketApiClient>(), shiftId).ConfigureAwait(true);
    }

    /// <summary>Сверка после возврата: наличные ушли из ящика на <paramref name="cashOut"/>, а сервер остаток не уменьшил — поправка.</summary>
    private static void CheckDrawerAfterRefund(decimal? before, decimal cashOut, string key, string? saleId = null, decimal nonCash = 0m)
    {
        var shiftId = PosApp.ActiveShiftId;
        // 2026-10-23 (живой аудит, Б-1): долговой чек с безналичной предоплатой — наличные не уходят (cashOut 0), но сервер
        // вычитает предоплату из остатка смены и старая поправка предоплаты становится лишней: снимаем её, если остаток
        // на сервере действительно уменьшился на безнал.
        if (before is { } b0 && cashOut <= 0m && nonCash > 0m && !string.IsNullOrWhiteSpace(shiftId) && !string.IsNullOrWhiteSpace(saleId))
        {
            var client0 = App.GetRequiredService<NurMarketApiClient>();
            _ = Task.Run(async () =>
            {
                var after0 = await ExchangeCashCorrections.ReadExpectedCashAsync(client0, shiftId).ConfigureAwait(false);
                if (after0 is { } a0 && Math.Abs((a0 - b0) + nonCash) < 0.01m)
                    ExchangeCashCorrections.RemoveDebtPrepay(shiftId, saleId);
            });
            return;
        }
        if (before is null || cashOut <= 0m || string.IsNullOrWhiteSpace(shiftId))
            return;
        var client = App.GetRequiredService<NurMarketApiClient>();
        _ = Task.Run(async () =>
        {
            var after = await ExchangeCashCorrections.ReadExpectedCashAsync(client, shiftId).ConfigureAwait(false);
            if (after is { } a)
                ExchangeCashCorrections.RecordIfKnownServerError(shiftId, "refund:" + key, (a - before.Value) + cashOut, cashOut, "Возврат (наличные)");
        });
    }

    private sealed record RefundOutcome(decimal Amount, decimal Cash, decimal NonCash, decimal DebtReduced, bool SplitKnown, bool DebtUnknown = false,
        decimal ServerOver = 0m)
    {
        public decimal Money => Cash + NonCash;

        /// <summary>Примечание события смены (ShiftEventsStore): разбивка для окна закрытия смены.</summary>
        public string Note => $"cash={SaleReturnLedger.Inv(Cash)};noncash={SaleReturnLedger.Inv(NonCash)};debt={SaleReturnLedger.Inv(DebtReduced)}"
                              + (ServerOver > 0.005m ? $";server_over={SaleReturnLedger.Inv(ServerOver)}" : "");
    }

    /// <summary>2026-10-20 (живой аудит, К1–К3). <paramref name="expected"/> — расчёт кассы (доля строк со скидкой на чек,
    /// не больше остатка оплаченного). Сервер вернул меньше — берём его (денег ушло меньше); больше (скидка на чек не
    /// учтена: 001837 — 80 из 75) — его число кассиру не показываем: журнал (WARNING) и «Некорректные чеки». Деньги:
    /// у продажи «в долг» сначала уменьшается долг, выдаётся только внесённое (если сервер сказал, сколько выдал, — его
    /// цифра); у смешанной — пропорционально тому, что ещё не возвращено наличными и безналом.</summary>
    private async Task<RefundOutcome> ResolveRefundAsync(string saleId, decimal expected, bool whole, List<SaleLedgerLine> lines)
    {
        // 2026-10-21 (ревью денег, мелочь 4): журнал и способ оплаты — именно этого чека, даже если кассир уже открыл другой.
        var ledger = _ledger is { Total: > 0m } known && string.Equals(known.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
            ? known
            : SaleReturnLedger.Get(saleId) is { Total: > 0m } stored ? stored : null;
        var saleMethod = _saleMethods.TryGetValue(saleId, out var m) ? m
            : string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleMethod : null;
        var (doc, docId, cashOut) = await ServerReturnFiguresAsync(saleId, ledger).ConfigureAwait(true);
        // 2026-10-21 (ревью денег, п. 2): остаток долга по чеку с сервера (запрошен при открытии чека, до возврата).
        decimal? serverDebtLeft = null;
        if (_serverDebtLeft.Remove(saleId, out var debtLookup))
            serverDebtLeft = await debtLookup.ConfigureAwait(true);
        var amount = Math.Max(0m, expected);
        var serverOver = 0m;
        var number = DisplaySaleNumber(saleId);
        if (doc is { } server)
        {
            if (server < amount - 0.009m)
            {
                PosLogger.Log($"Возврат по чеку {number} ({saleId}): сервер вернул {server:0.00}, касса насчитала {amount:0.00} — берём сумму сервера.", "WARNING");
                amount = server;
            }
            else if (server > amount + 0.009m)
            {
                serverOver = server - amount;
                var paid = ledger is null ? "" : $", оплачено {MoneyText.Amount(ledger.Total)}, возвращено раньше {MoneyText.Amount(ledger.Refunded)}";
                PosLogger.Log($"Возврат по чеку {number} ({saleId}): сервер вернул {server:0.00} — больше, чем положено ({amount:0.00}{paid}; скидка на чек не учтена сервером). Кассиру показана сумма кассы.", "WARNING");
                try
                {
                    IrregularReceiptsStore.Append(new IrregularReceiptEntry
                    {
                        Tag = IrregularReceiptEntry.ReturnOverpaid,
                        CashierName = PosApp.CurrentUserDisplayName,
                        Note = Tr.T(
                            $"Возврат по чеку {number}: сервер записал {MoneyText.Som(server)}, положено {MoneyText.Som(amount)}{(ledger is null ? "" : $" (оплачено {MoneyText.Som(ledger.Total)})")}. Покупателю выдано по кассе.",
                            $"{number} чеги боюнча кайтаруу: сервер {MoneyText.Som(server)} жазды, туурасы {MoneyText.Som(amount)}{(ledger is null ? "" : $" (төлөнгөн {MoneyText.Som(ledger.Total)})")}. Сатып алуучуга касса боюнча берилди.",
                            $"Return for receipt {number}: the server recorded {MoneyText.Som(server)}, due {MoneyText.Som(amount)}{(ledger is null ? "" : $" (paid {MoneyText.Som(ledger.Total)})")}. The customer got the till's amount.",
                            $"{number} fişi iadesi: sunucu {MoneyText.Som(server)} kaydetti, olması gereken {MoneyText.Som(amount)}{(ledger is null ? "" : $" (ödenen {MoneyText.Som(ledger.Total)})")}. Müşteriye kasanın tutarı verildi.",
                            $"{number} chek bo'yicha qaytarish: server {MoneyText.Som(server)} yozdi, to'g'risi {MoneyText.Som(amount)}{(ledger is null ? "" : $" (to'langan {MoneyText.Som(ledger.Total)})")}. Xaridorga kassa summasi berildi."),
                        Total = (double)(server - amount),
                        CartJson = "{}",
                    });
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Некорректный возврат не записан в журнал: {ex.Message}", "WARNING");
                }
            }
        }

        var (cash, nonCash, debtReduced, splitKnown, debtUnknown) = ComputeRefundSplit(ledger, saleMethod, amount, cashOut, serverDebtLeft);
        if (debtUnknown && debtReduced > 0.005m)
            PosLogger.Log($"Возврат по чеку {number}: остаток долга с сервера неизвестен — кассиру показано предупреждение проверить долг клиента (по кассе долг −{debtReduced:0.00}).", "WARNING");

        if (cashOut is { } flows && Math.Abs(flows - (cash + nonCash)) > 0.009m)
            PosLogger.Log($"Возврат по чеку {number}: по движениям денег сервера выдано {flows:0.00}, по кассе к выдаче {cash + nonCash:0.00} (наличными {cash:0.00}, безнал {nonCash:0.00}, долг −{debtReduced:0.00}).", "WARNING");

        if (ledger is not null)
        {
            SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund
            {
                At = DateTimeOffset.Now,
                Amount = amount,
                Cash = cash,
                NonCash = nonCash,
                DebtReduced = debtReduced,
                ServerAmount = doc,
                ServerDocId = docId,
                Whole = whole,
                Lines = lines,
            });
            if (string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase))
                _ledger = SaleReturnLedger.Get(saleId);
        }

        PosLogger.Log($"Возврат по чеку {number}: {amount:0.00} (наличными {cash:0.00}, безнал {nonCash:0.00}, долг −{debtReduced:0.00}); документ сервера {(doc is { } d ? d.ToString("0.00", CultureInfo.InvariantCulture) : "—")}.", "SALES");
        return new RefundOutcome(amount, cash, nonCash, debtReduced, splitKnown, debtUnknown, serverOver);
    }

    /// <summary>2026-10-20 (живой аудит, К2, К3): чем вернуть <paramref name="amount"/>. Продажа «в долг» — сначала уменьшается
    /// долг, выдаётся только внесённое (если сервер сказал, сколько выдал, — его цифра; без журнала кассы и без движения денег
    /// сервера — 0); смешанная — пропорционально тому, что ещё не возвращено наличными и безналом; без журнала — по способу
    /// оплаты продажи с сервера (<paramref name="fallbackMethod"/>). Чистая функция — проверяется на стенде.</summary>
    /// <param name="serverDebtLeft">2026-10-21 (ревью денег, п. 2): текущий остаток долга по чеку с сервера (null — не получен).
    /// Долг уменьшается не больше него: клиент мог уже погасить долг, тогда возврат выдаётся деньгами.</param>
    internal static (decimal Cash, decimal NonCash, decimal DebtReduced, bool SplitKnown, bool DebtUnknown) ComputeRefundSplit(
        SaleLedgerEntry? ledger, string? fallbackMethod, decimal amount, decimal? cashOut, decimal? serverDebtLeft = null)
    {
        decimal cash, nonCash, debtReduced;
        var splitKnown = true;
        var debtUnknown = false;
        var method = (ledger?.Method is { Length: > 0 } known ? known : fallbackMethod ?? "").Trim().ToLowerInvariant();
        var isDebtSale = ledger is { Debt: > 0.005m } || method == "debt";
        if (isDebtSale)
        {
            // Долг — сначала; выдаётся только то, что покупатель реально внёс.
            decimal money;
            if (cashOut is { } givenByServer)
                money = Math.Min(amount, Math.Max(0m, givenByServer));
            else if (serverDebtLeft is { } debtNow)
            {
                // 2026-10-21 (ревью денег, п. 2): долг уменьшается не больше текущего остатка сервера (и не больше остатка
                // по журналу кассы — сервер мог не уменьшить долг на прошлые возвраты); погашенный долг — выдаётся деньгами.
                var debtLeft = ledger is not null ? Math.Min(Math.Max(0m, debtNow), ledger.DebtLeft) : Math.Max(0m, debtNow);
                money = Math.Max(0m, amount - Math.Min(amount, debtLeft));
            }
            else
            {
                // 2026-10-21 (ревью денег, п. 2): остаток сервера неизвестен — журнал не знает оплат долга, уверенное
                // «Выдать 0,00» было бы неправдой: расчёт по журналу, но кассиру — предупреждение проверить долг.
                debtUnknown = true;
                money = ledger is not null ? Math.Max(0m, amount - Math.Min(amount, ledger.DebtLeft)) : 0m;
            }
            debtReduced = amount - money;
            var cashShareLeft = ledger?.CashLeft ?? money;
            var nonCashShareLeft = ledger?.NonCashLeft ?? 0m;
            // 2026-10-21 (ревью денег, п. 2): к выдаче больше внесённого при продаже — разница пришла оплатами долга, способ
            // которых касса не знает: внесённое делим по способам, остальное — одной суммой (наличными, без «на карту»).
            var prepaidMoney = ledger is not null ? Math.Min(money, cashShareLeft + nonCashShareLeft) : money;
            if (money - prepaidMoney > 0.005m)
                splitKnown = false;
            cash = cashShareLeft + nonCashShareLeft > 0m
                ? Math.Round(prepaidMoney * cashShareLeft / (cashShareLeft + nonCashShareLeft), 2, MidpointRounding.AwayFromZero)
                : prepaidMoney;
            cash += money - prepaidMoney;
            nonCash = money - cash;
        }
        else
        {
            debtReduced = 0m;
            switch (method)
            {
                case "cash":
                    cash = amount;
                    nonCash = 0m;
                    break;
                case "mixed" or "split" when ledger is not null && ledger.CashLeft + ledger.NonCashLeft > 0m:
                    cash = Math.Min(amount, Math.Round(amount * ledger.CashLeft / (ledger.CashLeft + ledger.NonCashLeft), 2, MidpointRounding.AwayFromZero));
                    nonCash = amount - cash;
                    break;
                case "mixed" or "split" or "":
                    // Касса не знает, как заплатили (чек до возвратов не видела) — одна сумма, как раньше.
                    cash = amount;
                    nonCash = 0m;
                    splitKnown = false;
                    break;
                default:
                    cash = 0m;
                    nonCash = amount;
                    break;
            }
        }
        return (cash, nonCash, debtReduced, splitKnown, debtUnknown);
    }

    /// <summary>2026-10-04: строка «Выдать покупателю: N сом» в сообщении об оформленном возврате.
    /// 2026-10-20 (живой аудит, К2, К3): только внесённые деньги — с разбивкой «наличными · на карту/перевод»; у продажи
    /// «в долг» — «Выдать: 0,00 · долг клиента уменьшен на 15,00».</summary>
    private static string RefundToGiveText(decimal amount) => Tr.T(
        $"Выдать покупателю: {MoneyText.Som(amount)}.",
        $"Сатып алуучуга берүү: {MoneyText.Som(amount)}.",
        $"Give the customer: {MoneyText.Som(amount)}.",
        $"Müşteriye verilecek: {MoneyText.Som(amount)}.",
        $"Xaridorga berish: {MoneyText.Som(amount)}.");

    private static string RefundToGiveText(RefundOutcome outcome)
    {
        var parts = new List<string>();
        if (outcome.Cash > 0.005m)
            parts.Add(Tr.T("наличными ", "накталай ", "in cash ", "nakit ", "naqd ") + MoneyText.Som(outcome.Cash));
        if (outcome.NonCash > 0.005m)
            parts.Add(Tr.T("на карту/перевод ", "картага/которуу менен ", "to the card/transfer ", "karta/havale ile ", "kartaga/o'tkazma bilan ") + MoneyText.Som(outcome.NonCash));
        var give = !outcome.SplitKnown
            ? MoneyText.Som(outcome.Money)
            : parts.Count == 0 ? MoneyText.Som(0m) : string.Join(" · ", parts);
        var text = Tr.T($"Выдать покупателю: {give}.", $"Сатып алуучуга берүү: {give}.", $"Give the customer: {give}.",
            $"Müşteriye verilecek: {give}.", $"Xaridorga berish: {give}.");
        if (outcome.DebtReduced > 0.005m && outcome.DebtUnknown)
        {
            // 2026-10-21 (ревью денег, п. 2): остаток долга с сервера не получен — не уверенное «долг уменьшен», а проверка.
            var debt = MoneyText.Som(outcome.DebtReduced);
            text += "\n" + Tr.T(
                $"Внимание: остаток долга клиента с сервера не получен. По кассе долг уменьшен на {debt}, но если клиент уже погасил долг по этому чеку — ему нужно вернуть до {debt}. Проверьте долг клиента («Клиенты» → долги) перед выдачей.",
                $"Көңүл буруңуз: кардардын карызынын калдыгы серверден алынган жок. Касса боюнча карыз {debt} азайды, бирок кардар бул чек боюнча карызын төлөп койгон болсо — ага {debt} чейин кайтаруу керек. Берүүдөн мурун кардардын карызын текшериңиз («Кардарлар» → карыздар).",
                $"Warning: the client's remaining debt was not received from the server. By the till the debt is reduced by {debt}, but if the client has already paid off this receipt's debt, up to {debt} must be refunded. Check the client's debt (“Clients” → debts) before paying out.",
                $"Dikkat: müşterinin kalan borcu sunucudan alınamadı. Kasaya göre borç {debt} azaldı, ancak müşteri bu fişin borcunu zaten ödediyse kendisine {debt} tutarına kadar iade edilmeli. Ödemeden önce müşterinin borcunu kontrol edin («Müşteriler» → borçlar).",
                $"Diqqat: mijoz qarzining qoldig'i serverdan olinmadi. Kassa bo'yicha qarz {debt} ga kamaydi, lekin mijoz bu chek bo'yicha qarzini to'lab bo'lgan bo'lsa — unga {debt} gacha qaytarish kerak. Berishdan oldin mijoz qarzini tekshiring («Mijozlar» → qarzlar).");
        }
        else if (outcome.DebtReduced > 0.005m)
        {
            var debt = MoneyText.Som(outcome.DebtReduced);
            text += "\n" + Tr.T($"Долг клиента уменьшен на {debt} — эти деньги не выдаются.",
                $"Кардардын карызы {debt} азайды — бул акча берилбейт.",
                $"The client's debt is reduced by {debt} — this money is not paid out.",
                $"Müşterinin borcu {debt} azaldı — bu para verilmez.",
                $"Mijozning qarzi {debt} ga kamaydi — bu pul berilmaydi.");
        }
        if (outcome.NonCash > 0.005m && outcome.SplitKnown)
            text += "\n" + Tr.T("На карту/перевод — вернуть через банк, не из кассы.",
                "Картага/которуу — банк аркылуу кайтарыңыз, кассадан эмес.",
                "Card/transfer part — refund through the bank, not from the till.",
                "Kart/havale kısmı — kasadan değil, banka üzerinden iade edin.",
                "Karta/o'tkazma qismi — kassadan emas, bank orqali qaytaring.");
        return text;
    }

    /// <summary>2026-10-20 (живой аудит, К7): чек возврата не напечатался — одной строкой в итоговом окне (подробности — в журнале).</summary>
    private static string ReturnPrintNote(string? printError) => printError is null
        ? ""
        : "\n\n" + Tr.T("Чек возврата не напечатан — принтер не подключён или не отвечает.",
            "Кайтаруу чеги басылган жок — принтер туташкан эмес же жооп бербейт.",
            "The return receipt was not printed — the printer is not connected or not responding.",
            "İade fişi yazdırılmadı — yazıcı bağlı değil veya yanıt vermiyor.",
            "Qaytarish cheki chop etilmadi — printer ulanmagan yoki javob bermayapti.");

    /// <summary>2026-10-20 (живой аудит, К8): возвращённый товар — сразу в остаток каталога (как «Остаток после продажи
    /// уменьшен сразу»), потом сверка с сервером (запоздавший ответ со старым числом не откатит остаток).</summary>
    private void ReturnToStock(IEnumerable<ReturnSaleLineVm> lines)
    {
        try
        {
            var returned = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line.ProductId) || line.Quantity <= 0)
                    continue;
                var perQty = _stockUnitsPerQty.TryGetValue(line.LineId, out var k) && k > 0 ? k : 1.0;
                returned[line.ProductId] = (returned.TryGetValue(line.ProductId, out var before) ? before : 0) + line.Quantity * perQty;
            }
            if (returned.Count == 0)
                return;
            var expected = StockSyncService.ApplyReturnedItemsIncrement(returned);
            _ = StockSyncService.RefreshReturnedItemsStockAsync(expected);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Остаток после возврата не обновлён сразу: {ex.Message}", "STOCK");
        }
    }

    /// <summary>2026-10-16, ревью: сколько было к возврату по строкам чека (id строки → количество) перед отправкой.</summary>
    private static Dictionary<string, double> RefundableByLine(IEnumerable<ReturnSaleLineVm> lines) =>
        lines.DistinctBy(l => l.LineId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(l => l.LineId, l => l.Quantity, StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-10-16, ревью: возврат (или обмен — ExchangeWindow) уже есть на сервере — чек отменён целиком или по
    /// какой-то из отправленных строк к возврату осталось меньше, чем было перед отправкой.</summary>
    internal static bool LinesReturnedSince(JsonElement sale, IReadOnlyDictionary<string, double> refundableBefore)
    {
        if (sale.ValueKind != JsonValueKind.Object)
            return false;
        if (sale.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String
            && status.GetString()?.Trim().ToLowerInvariant() is "canceled" or "cancelled" or "returned" or "refunded")
            return true;
        return CartDisplayHelper.EnumerateSaleLineItems(sale).Any(item =>
            CartDisplayHelper.TryRefundLineId(item) is { } lineId
            && refundableBefore.TryGetValue(lineId, out var before) && before > 0
            && (CartDisplayHelper.LineLooksFullyReturned(item) || CartDisplayHelper.RefundableQuantity(item) < before - 1e-6));
    }

    /// <summary>2026-10-16, ревью: возврат кончился без ответа сервера (таймаут, обрыв, 5xx) — сервер мог его провести.
    /// Перечитываем чек (LinesReturnedSince). Прошёл — ключ забываем (следующий возврат того же состава — новая
    /// операция, а не повтор прошлой), пишем возврат в журнал смены (касса его не записала) и говорим кассиру, что
    /// повторять не нужно и сколько выдать — один раз. Не прошёл или чек не прочитался — ключ остаётся: повтор уйдёт с
    /// тем же ключом, и сервер вернёт прежний итог. Чек возврата здесь не печатается.</summary>
    /// <param name="returnedLines">2026-10-20 (К8): строки этого возврата — для остатка каталога (брак — нет).</param>
    private async Task ReportReturnIfAppliedAsync(string saleId, IReadOnlyDictionary<string, double> refundableBefore,
        decimal estimate, bool reverseLoyalty, bool whole = false, IReadOnlyList<ReturnSaleLineVm>? returnedLines = null, bool isDefect = false)
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var sale = await App.SalesApi.PosSaleGetAsync(saleId, cts.Token).ConfigureAwait(true);
            if (!LinesReturnedSince(sale, refundableBefore))
                return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат по продаже {saleId}: ответ не дошёл, чек перечитать не удалось — ключ оставлен для повтора: {ex.Message}", "WARNING");
            return;
        }

        PendingReturn.OnSuccess();
        SaleDetailCache.Forget(saleId);
        // 2026-10-20 (живой аудит, К1–К3): та же сумма и разбивка, что у дошедшего ответа (ResolveRefundAsync).
        var outcome = await ResolveRefundAsync(saleId, estimate, whole,
            (returnedLines ?? Array.Empty<ReturnSaleLineVm>()).Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList())
            .ConfigureAwait(true);
        var amount = outcome.Amount;
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, PosApp.ActiveShiftId, ShiftEventsStore.OperationKey(saleId), (double)amount, outcome.Note);
        if (!isDefect && returnedLines is { Count: > 0 })
            ReturnToStock(returnedLines);
        if (reverseLoyalty)
        {
            try
            {
                ClientLoyaltyStore.ReverseRemainingForSale(saleId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Loyalty reversal after unanswered return failed: {ex.Message}", "WARNING");
            }
        }
        PosLogger.Log($"Возврат по продаже {saleId}: ответ не дошёл, но по чеку сервер его провёл ({amount:0.00}) — записан в смену, ключ забыт.", "WARNING");
        // 2026-10-20 (К2, К3): к выдаче — та же строка, что у обычного возврата (разбивка, долг).
        PosMessageBox.Show(this, Tr.T(
                "Сервер уже провёл этот возврат — ответ не дошёл до кассы из-за связи. Повторно не оформляйте. Деньги — только если ещё не выдали (не выдавайте дважды).",
                "Сервер бул кайтарууну мурунтан эле жүргүзгөн — байланыштан улам жооп кассага жеткен жок. Кайра тариздебеңиз. Акчаны — эгер али бере элек болсоңуз гана (эки жолу бербеңиз).",
                "The server has already processed this return — its reply did not reach the till because of the connection. Do not process it again. Pay out only if you have not done so yet (do not pay twice).",
                "Sunucu bu iadeyi zaten işledi — bağlantı nedeniyle yanıt kasaya ulaşmadı. Tekrar yapmayın. Parayı yalnızca henüz vermediyseniz verin (iki kez vermeyin).",
                "Server bu qaytarishni allaqachon o'tkazgan — aloqa tufayli javob kassaga yetib kelmadi. Qayta rasmiylashtirmang. Pulni faqat hali bermagan bo'lsangiz bering (ikki marta bermang).")
            + "\n" + RefundToGiveText(outcome),
            Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Строки чека возврата пропорционально приводятся к сумме сервера (см.
    /// ServerRefundAmountAsync), чтобы сумма строк на бумаге совпадала с итогом.</summary>
    private static List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> ScaleRefundLines(
        List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)> lines, decimal estimated, decimal actual)
    {
        if (lines.Count == 0 || estimated <= 0m || Math.Abs(estimated - actual) < 0.005m)
            return lines;

        var factor = actual / estimated;
        var result = new List<(string Name, double Quantity, decimal UnitPrice, decimal Sum)>(lines.Count);
        var left = actual;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var sum = i == lines.Count - 1 ? left : Math.Round(line.Sum * factor, 2, MidpointRounding.AwayFromZero);
            left -= sum;
            var qty = line.Quantity > 0 ? (decimal)line.Quantity : 1m;
            result.Add((line.Name, line.Quantity, Math.Round(sum / qty, 2, MidpointRounding.AwayFromZero), sum));
        }

        return result;
    }

    private async Task RefreshCurrentSaleAsync(string? expectedSaleId = null)
    {
        var saleId = expectedSaleId ?? _currentSaleId;
        if (string.IsNullOrEmpty(saleId))
            return;

        try
        {
            var sale = await App.SalesApi.PosSaleGetAsync(saleId).ConfigureAwait(true);
            if (!string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase))
                return;
            FillLinesFromSale(sale);
            UpdateReceiptChrome();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Return receipt refresh failed after operation: {ex}", "WARNING");
        }

        await LoadSalesAsync(true).ConfigureAwait(true);
        // 2026-10-21 (пакет 6, п. 35): после возврата строка чека снова выделена (и с пометкой «возвращён»).
        SelectRowInGrid(saleId);
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
                "Select a receipt in the list above first — its contents will appear here.",
                "Önce yukarıdaki listeden bir fiş seçin — içeriği burada görünecek.",
                "Avval yuqoridagi ro'yxatdan chekni tanlang — uning tarkibi shu yerda ko'rinadi.");
            LinesPlaceholder.IsVisible = true;
        }
        else if (Lines.Count == 0)
        {
            SelectedSaleText.Text = Tr.T("Выбран чек · ", "Тандалган чек · ", "Selected receipt · ", "Seçilen fiş · ", "Tanlangan chek · ") + DisplaySaleNumber(_currentSaleId);
            LinesPlaceholder.Text = Tr.T("В этом чеке нет позиций с идентификатором строки для возврата через кассу.",
                "Бул чекте кассадан кайтаруу үчүн сап идентификатору бар позициялар жок.",
                "This receipt has no items with a line ID that can be returned through the till.",
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

        UpdateReturnTermText(hasSale);
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-31): дата чека и сколько дней прошло; после срока
    /// обмена (закон КР — 14 дней, не считая дня покупки; см. NonExchangeableRules) — красное предупреждение.</summary>
    private void UpdateReturnTermText(bool hasSale)
    {
        var limit = NonExchangeableRules.DaysLimit;
        if (!hasSale || limit <= 0 || _currentSaleDate is not { } date)
        {
            ReturnTermText.IsVisible = false;
            return;
        }
        var days = NonExchangeableRules.DaysPassed(date);
        var outOfTerm = days > limit;
        ReturnTermText.Text = Tr.T($"Чек от {date:dd.MM.yyyy}: прошло {days} дн. из {limit}.", $"Чек {date:dd.MM.yyyy}: {limit} күндүн {days} күнү өттү.",
                                  $"Receipt of {date:dd.MM.yyyy}: {days} of {limit} days have passed.", $"{date:dd.MM.yyyy} tarihli fiş: {limit} günün {days} günü geçti.",
                                  $"{date:dd.MM.yyyy} sanadagi chek: {limit} kundan {days} kun o'tdi.")
                              + (outOfTerm
                                  ? Tr.T(" Срок обмена и возврата прошёл — без брака по закону не обменивают.", " Алмаштыруу жана кайтаруу мөөнөтү өттү — мыйзам боюнча бузук эмес товар алмаштырылбайт.",
                                      " The exchange and return period is over — by law, items without defects are not exchanged.", " Değişim ve iade süresi doldu — kanuna göre kusursuz ürün değiştirilmez.",
                                      " Almashtirish va qaytarish muddati o'tdi — qonun bo'yicha nuqsonsiz mahsulot almashtirilmaydi.")
                                  : "");
        ReturnTermText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(outOfTerm ? "BrushDanger" : "BrushTextSoft"));
        ReturnTermText.IsVisible = true;
    }

    /// <summary>2026-10-06 (О-31, О-32): проверки перед возвратом и обменом в сфере «Одежда». null — кассир отказался;
    /// true — товар из категории «без обмена», кассир подтвердил брак (у возврата сразу стоит отметка «Брак»); false — обычный случай.
    /// Не запрет: брак принимают и после срока, и из этих категорий.</summary>
    private async Task<bool?> CheckReturnRulesAsync(IReadOnlyList<ReturnSaleLineVm> lines, bool exchange)
    {
        var title = exchange ? Tr.T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish") : Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish");
        if (NonExchangeableRules.IsOutOfTerm(_currentSaleDate))
        {
            var date = _currentSaleDate!.Value;
            var days = NonExchangeableRules.DaysPassed(date);
            var limit = NonExchangeableRules.DaysLimit;
            var what = exchange ? Tr.T("обмен", "алмаштырууну", "the exchange", "değişimi", "almashtirishni") : Tr.T("возврат", "кайтарууну", "the return", "iadeyi", "qaytarishni");
            if (await PosMessageBox.ShowModalAsync(this,
                    Tr.T($"Чек от {date:dd.MM.yyyy}: прошло {days} дн., а срок обмена и возврата — {limit} дн. (не считая дня покупки).\n\nПо закону товар без брака после этого срока не обменивают и не возвращают; брак принимают дольше.\n\nОформить {what} всё равно?",
                        $"Чек {date:dd.MM.yyyy}: {days} күн өттү, ал эми алмаштыруу жана кайтаруу мөөнөтү — {limit} күн (сатып алган күн эсептелбейт).\n\nМыйзам боюнча бул мөөнөттөн кийин бузук эмес товар алмаштырылбайт жана кайтарылбайт; бузук товар кечирээк да кабыл алынат.\n\nБаары бир {what} таризделсинби?",
                        $"Receipt of {date:dd.MM.yyyy}: {days} days have passed, but the exchange and return period is {limit} days (not counting the day of purchase).\n\nBy law, items without defects are not exchanged or returned after this period; defective items are accepted longer.\n\nProcess {what} anyway?",
                        $"{date:dd.MM.yyyy} tarihli fiş: {days} gün geçti, değişim ve iade süresi ise {limit} gün (satın alma günü sayılmaz).\n\nKanuna göre bu süreden sonra kusursuz ürün değiştirilmez ve iade edilmez; kusurlu ürün daha uzun süre kabul edilir.\n\nYine de {what} yapılsın mı?",
                        $"{date:dd.MM.yyyy} sanadagi chek: {days} kun o'tdi, almashtirish va qaytarish muddati esa {limit} kun (xarid kuni hisoblanmaydi).\n\nQonun bo'yicha bu muddatdan keyin nuqsonsiz mahsulot almashtirilmaydi va qaytarilmaydi; nuqsonli mahsulot uzoqroq qabul qilinadi.\n\nBaribir {what} rasmiylashtirilsinmi?"),
                    Tr.T("Срок обмена прошёл", "Алмаштыруу мөөнөтү өттү", "Exchange period is over", "Değişim süresi doldu", "Almashtirish muddati o'tdi"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    // 2026-10-22 (модалки, пакет 1): глагол вместо «Продолжить»: «Оформить возврат» / «Оформить обмен».
                    confirmText: exchange
                        ? Tr.T("Оформить обмен", "Алмаштырууну тариздөө", "Process exchange", "Değişimi yap", "Almashtirishni rasmiylashtirish")
                        : Tr.T("Оформить возврат", "Кайтарууну тариздөө", "Process return", "İadeyi yap", "Qaytarishni rasmiylashtirish")).ConfigureAwait(true) != MessageBoxResult.Yes)
                return null;
            PosLogger.Log($"{title} по чеку {_currentSaleId} после срока: прошло {days} дн. из {limit}, кассир подтвердил.", "SALES");
        }

        var flagged = lines
            .Select(l => (Line: l, Category: NonExchangeableRules.NonExchangeableCategoryOf(l.ProductId)))
            .Where(x => x.Category != null)
            .ToList();
        if (flagged.Count == 0)
            return false;
        var names = string.Join("\n", flagged.Select(x => $"• {x.Line.Title} — «{x.Category}»"));
        var text = exchange
            ? Tr.T($"Эти товары без брака по закону не обменивают:\n{names}\n\nЕсли это брак — лучше оформите «Возврат» с отметкой «Брак» (товар не вернётся в продажу), а новый товар продайте обычным чеком: обмен возвращает товар на склад.\n\nВсё равно оформить обмен?",
                $"Бул товарлар бузук болбосо мыйзам боюнча алмаштырылбайт:\n{names}\n\nЭгер бузук болсо — «Бузук» белгиси менен «Кайтарууну» таризделиңиз (товар сатууга кайтпайт), жаңы товарды кадимки чек менен сатыңыз: алмаштыруу товарды кампага кайтарат.\n\nБаары бир алмаштырылсынбы?",
                $"By law, these items are not exchanged unless defective:\n{names}\n\nIf it is a defect, better make a “Return” marked “Defective” (the item won't go back on sale) and sell the new item with a regular receipt: an exchange puts the item back in stock.\n\nExchange anyway?",
                $"Kanuna göre bu ürünler kusurlu değilse değiştirilmez:\n{names}\n\nKusurluysa «Kusurlu» işaretli «İade» yapın (ürün satışa dönmez) ve yeni ürünü normal fişle satın: değişim ürünü stoğa geri koyar.\n\nYine de değiştirilsin mi?",
                $"Qonun bo'yicha bu mahsulotlar nuqsonsiz bo'lsa almashtirilmaydi:\n{names}\n\nAgar nuqson bo'lsa — «Nuqsonli» belgisi bilan «Qaytarish»ni rasmiylashtiring (mahsulot sotuvga qaytmaydi), yangi mahsulotni oddiy chek bilan soting: almashtirish mahsulotni omborga qaytaradi.\n\nBaribir almashtirilsinmi?")
            : Tr.T($"Эти товары без брака по закону не обменивают и не возвращают:\n{names}\n\nЕсли у товара брак — оформите возврат как брак: товар не вернётся в продажу.",
                $"Бул товарлар бузук болбосо мыйзам боюнча алмаштырылбайт жана кайтарылбайт:\n{names}\n\nТовар бузук болсо — кайтарууну бузук катары тариздеңиз: товар сатууга кайтпайт.",
                $"By law, these items are not exchanged or returned unless defective:\n{names}\n\nIf the item is defective, return it as defective: it won't go back on sale.",
                $"Kanuna göre bu ürünler kusurlu değilse değiştirilmez ve iade edilmez:\n{names}\n\nÜrün kusurluysa kusurlu olarak iade edin: satışa dönmez.",
                $"Qonun bo'yicha bu mahsulotlar nuqsonsiz bo'lsa almashtirilmaydi va qaytarilmaydi:\n{names}\n\nMahsulot nuqsonli bo'lsa — nuqsonli sifatida qaytaring: sotuvga qaytmaydi.");
        var answer = await PosMessageBox.ShowModalAsync(this, text,
            Tr.T("Товар не подлежит обмену", "Товар алмаштырылбайт", "Item cannot be exchanged", "Ürün değiştirilemez", "Mahsulot almashtirilmaydi"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning,
            // 2026-10-22 (модалки, пакет 1): глаголы вместо «Да» / «Нет» (текст больше не называет кнопки): у возврата —
            // «Оформить как брак», у обмена — «Оформить обмен»; вторая — «Отмена» (умолчание окна). Смысл ответов прежний.
            confirmText: exchange
                ? Tr.T("Оформить обмен", "Алмаштырууну тариздөө", "Process exchange", "Değişimi yap", "Almashtirishni rasmiylashtirish")
                : Tr.T("Оформить как брак", "Бузук катары тариздөө", "Return as defective", "Kusurlu olarak iade et", "Nuqsonli sifatida rasmiylashtirish")).ConfigureAwait(true);
        if (answer != MessageBoxResult.Yes)
            return null;
        PosLogger.Log($"{title} по чеку {_currentSaleId}: товар из категории без обмена ({string.Join(", ", flagged.Select(x => x.Category))}), кассир подтвердил.", "SALES");
        return !exchange;
    }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-30): обмен одним документом — отмеченные позиции
    /// возвращаются, покупатель берёт другой товар, деньгами проходит только разница (окно ExchangeWindow).</summary>
    private async void ExchangeSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentSaleId) || IsBusy || _operationInProgress)
            return;

        // 2026-10-15, ревью: двойной клик открывал бы два окна обмена одного чека (см. _operationInProgress).
        _operationInProgress = true;
        IsBusy = true;
        try
        {
            await ExchangeSelectedCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _operationInProgress = false;
            IsBusy = false;
        }
    }

    private async Task ExchangeSelectedCoreAsync()
    {
        if (string.IsNullOrEmpty(_currentSaleId))
            return;
        // 2026-10-27 (Д-5): обмен тоже берёт выбранное количество (часть строки), а не всю строку.
        var selected = EffectiveReturnLines(Lines.Where(x => x.CanReturn && x.IsSelected));
        var exchangeTitle = Tr.T("Обмен", "Алмаштыруу", "Exchange", "Değişim", "Almashtirish");
        if (selected.Count == 0)
        {
            ShowInputErr(Tr.T("Отметьте галочками, что покупатель возвращает.", "Сатып алуучу эмнени кайтарарын белгилеңиз.", "Check what the customer is returning.",
                "Müşterinin iade ettiklerini işaretleyin.", "Xaridor nimani qaytarayotganini belgilang."));
            return;
        }
        if (await CheckReturnRulesAsync(selected, exchange: true).ConfigureAwait(true) is null)
            return;

        var lines = selected.Select(l => new ExchangeReturnLine(l.LineId, l.ProductId, l.Title,
            l.Quantity > 0 ? l.Quantity : 1,
            l.Quantity > 0 ? Math.Round(l.RefundSum / (decimal)l.Quantity, 2) : l.RefundSum)).ToList();
        var saleId = _currentSaleId;
        var window = new ExchangeWindow(saleId, _currentReceiptNumber ?? DisplaySaleNumber(saleId), _currentSaleDate, lines);
        if (await PosDialogHost.ShowModalAsync(window, this).ConfigureAwait(true) != true || window.Outcome is not { } done)
        {
            // 2026-10-16, ревью: обмен остался без ответа сервера и мог пройти — строки и количества к возврату
            // перечитываем с сервера, а не оставляем прежние.
            if (window.SaleMayHaveChanged)
                await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
            return;
        }

        var money = done.Difference > 0.004m
            ? (done.PaymentMethod == "transfer"
                ? Tr.T($"Доплата переводом: {MoneyText.Amount(done.Difference)} сом — проверьте, что перевод пришёл.", $"Которуу менен кошумча төлөм: {MoneyText.Amount(done.Difference)} сом — которуу келгенин текшериңиз.",
                    $"Paid by transfer: {MoneyText.Amount(done.Difference)} som — check that the transfer arrived.", $"Havale ile ek ödeme: {MoneyText.Amount(done.Difference)} som — havalenin geldiğini kontrol edin.",
                    $"O'tkazma orqali qo'shimcha to'lov: {MoneyText.Amount(done.Difference)} so'm — o'tkazma kelganini tekshiring.")
                : Tr.T($"Получить с покупателя: {MoneyText.Amount(done.Difference)} сом наличными.", $"Сатып алуучудан алуу: {MoneyText.Amount(done.Difference)} сом накталай.",
                    $"Take from the customer: {MoneyText.Amount(done.Difference)} som in cash.", $"Müşteriden alınacak: {MoneyText.Amount(done.Difference)} som nakit.",
                    $"Xaridordan olish: {MoneyText.Amount(done.Difference)} so'm naqd."))
            : done.Difference < -0.004m
                ? RefundToGiveText(-done.Difference)
                : Tr.T("Без доплаты.", "Кошумча төлөмсүз.", "No difference to pay.", "Fark yok.", "Qo'shimcha to'lovsiz.");
        PosMessageBox.Show(this,
            Tr.T("Обмен оформлен.", "Алмаштыруу таризделди.", "Exchange completed.", "Değişim tamamlandı.", "Almashtirish rasmiylashtirildi.")
            + (string.IsNullOrWhiteSpace(done.NewSaleNumber) ? "" : Tr.T($"\nНовый чек №{done.NewSaleNumber}.", $"\nЖаңы чек №{done.NewSaleNumber}.", $"\nNew receipt No. {done.NewSaleNumber}.",
                $"\nYeni fiş No. {done.NewSaleNumber}.", $"\nYangi chek №{done.NewSaleNumber}."))
            + "\n" + money,
            exchangeTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        await RefreshCurrentSaleAsync().ConfigureAwait(true);
    }

    private string DisplaySaleNumber(string? saleId)
    {
        if (string.IsNullOrEmpty(saleId))
            return "—";
        var row = FilteredSales.FirstOrDefault(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase));
        if (row != null)
            return row.DisplayNumber;
        // 2026-09-28 (BE-08): постоянный номер, прочитанный из самой продажи при открытии.
        if (string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_currentReceiptNumber))
            return _currentReceiptNumber!;
        // 2026-09-28: чек открыт из «Истории чеков», а в загруженные страницы списка он не попал
        // (старше первых 35 продаж) — номер тот же, что показала история (он считается по тому
        // же правилу, что и здесь, — как на сайте), а не обрезанный код продажи.
        return string.Equals(saleId, InitialSaleId?.Trim(), StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(InitialReceiptNumber)
            ? InitialReceiptNumber!
            : TruncateId(saleId);
    }

    private static string TruncateId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return "—";
        return id.Length <= 36 ? id : id[..32] + "…";
    }

    private async void ReturnWholeReceipt_Click(object? sender, RoutedEventArgs e)
    {
        if (_operationInProgress)
            return;
        if (string.IsNullOrEmpty(_currentSaleId) || IsBusy)
        {
            // 2026-10-22 (модалки, пакет 1): строкой в окне (снимется, когда кассир откроет чек), а не всплывающим окном;
            // во время загрузки (IsBusy) — молча, как кнопка «Вернуть выбранные».
            if (string.IsNullOrEmpty(_currentSaleId))
                ShowInputErr(Tr.T("Сначала выберите чек в списке слева.", "Алгач сол жактагы тизмеден чекти тандаңыз.", "Select a receipt in the list on the left first.", "Önce soldaki listeden bir fiş seçin.", "Avval chapdagi ro'yxatdan chekni tanlang."));
            return;
        }

        // 2026-10-15, ревью: защита от двойного клика — до первого подтверждения, снимается на любом выходе.
        _operationInProgress = true;
        IsBusy = true;
        try
        {
            await ReturnWholeReceiptCoreAsync(_currentSaleId).ConfigureAwait(true);
        }
        finally
        {
            _operationInProgress = false;
            IsBusy = false;
        }
    }

    private async Task ReturnWholeReceiptCoreAsync(string saleId)
    {
        // 2026-10-20 (К1): касса видела чек до возвратов — остаток оплаченного по её журналу (сервер после частичного
        // возврата убирает скидку на чек из продажи, его total больше остатка); иначе — как раньше.
        var estimatedWhole = _ledger is { Total: > 0m } wholeLedger
            ? Math.Min(wholeLedger.Remaining, _currentSaleTotal ?? wholeLedger.Remaining)
            // 2026-10-27 (В-3): чек уже частично возвращали не здесь — остаток по строкам (с долей скидки), не больше итога чека.
            : _noLedgerTouched ? ReturnShareEstimator.Cap(Lines.Where(l => l.CanReturn).Sum(line => line.RefundSum), _currentSaleTotal)
            : _currentSaleTotal ?? Lines.Where(l => l.CanReturn).Sum(line => line.RefundSum);
        estimatedWhole = ReturnShareEstimator.CapRefundable(estimatedWhole, _currentSaleRefundable); // 2026-10-27 (ревью, См-5)

        // 2026-10-21 (пакет 6, п. 35): порядок как у частичного возврата — сначала подтверждение с суммой, потом причина
        // (было наоборот: у частичного «подтверждение → причина», у полного «причина → подтверждение»).
        var wholeShown = MoneyText.Som(estimatedWhole);
        if (await PosMessageBox.ShowModalAsync(this,
                Tr.T($"Оформить полный возврат всего чека одной операцией? Позиции по отдельности возвращать не потребуется.\nСумма возврата: {wholeShown}",
                    $"Бүт чекти бир операция менен толук кайтарасызбы? Позицияларды өз-өзүнчө кайтаруунун кереги жок болот.\nКайтаруу суммасы: {wholeShown}",
                    $"Return the whole receipt in one operation? You won't need to return items one by one.\nRefund amount: {wholeShown}",
                    $"Tüm fiş tek işlemde iade edilsin mi? Kalemleri tek tek iade etmeniz gerekmeyecek.\nİade tutarı: {wholeShown}",
                    $"Butun chek bitta amal bilan qaytarilsinmi? Pozitsiyalarni birma-bir qaytarish shart bo'lmaydi.\nQaytarish summasi: {wholeShown}"),
                Tr.T("Полный возврат", "Толук кайтаруу", "Full return", "Tam iade", "To'liq qaytarish"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                // 2026-10-22 (модалки, пакет 1): глагол с суммой вместо «Продолжить».
                confirmText: Tr.T($"Вернуть весь чек — {wholeShown}", $"Бүт чекти кайтаруу — {wholeShown}", $"Refund whole receipt — {wholeShown}",
                    $"Tüm fişi iade et — {wholeShown}", $"Butun chekni qaytarish — {wholeShown}")).ConfigureAwait(true) != MessageBoxResult.Yes)
            return;

        // 2026-10-06 (О-31, О-32): срок обмена и товары «без обмена» — в сфере «Одежда».
        var defectPreset = await CheckReturnRulesAsync(Lines.Where(l => l.CanReturn).ToList(), exchange: false).ConfigureAwait(true);
        if (defectPreset is null)
            return;

        var reasonDialog = new ReturnLineReasonDialog(kind: ReturnReasonDialogKind.FullReceipt) { IsDefect = defectPreset == true };
        // 2026-10-04: ShowModalAsync — в Windows прежний синхронный показ, на Android — без вложенного цикла.
        if (await PosDialogHost.ShowModalAsync(reasonDialog, this).ConfigureAwait(true) != true)
            return;

        if (!string.Equals(saleId, _currentSaleId, StringComparison.OrdinalIgnoreCase))
            return;

        // 2026-10-16, ревью: остаток к возврату по строкам до отправки и оценка суммы — для сверки, если ответ не дойдёт.
        var refundableBefore = RefundableByLine(Lines.Where(l => l.CanReturn));
        var outcomeUnknown = false;
        IsBusy = true;
        try
        {
            // 2026-10-15, ревью: повтор полного возврата того же чека — тот же ключ (см. PendingReturn).
            var returnKey = PendingReturn.Get("whole|" + saleId);
            var cashBeforeWhole = await ReadDrawerBeforeRefundAsync().ConfigureAwait(true);
            await PosRefundService.RefundWholeSaleAsync(
                App.SalesApi,
                saleId,
                reasonDialog.ReasonText,
                App.PosCashboxId,
                reasonDialog.IsDefect,
                idempotencyKey: returnKey).ConfigureAwait(true);
            PendingReturn.OnSuccess();

            // 2026-09-23, живой баг: полный возврат чека не писал событие смены вообще — запись
            // стояла только в построчном возврате. Вернули покупателю весь чек наличными, а в
            // Z-отчёте, Telegram-сводке и выгрузке этой суммы нет: кассир сдаёт смену с
            // «недостачей» ровно на сумму возврата.
            // 2026-10-04, стресс-тест: сумма строк не учитывает скидку на чек — чек возврата печатал
            // больше, чем заплатил покупатель (№1348: 56,00 вместо 50,40). Берём сумму документа
            // возврата сервера, без него — итог продажи (total), и только потом — сумму строк.
            var linesTotal = Lines.Where(l => l.CanReturn).Sum(line => line.RefundSum);
            // 2026-10-20 (живой аудит, К1–К3): «Вернуть весь чек» после частичного — только остаток оплаченного
            // (estimatedWhole: остаток по журналу кассы), к выдаче — внесённые деньги с разбивкой, у долга — уменьшение долга.
            var wholeOutcome = await ResolveRefundAsync(saleId, estimatedWhole, whole: true,
                Lines.Where(l => l.CanReturn).Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList())
                .ConfigureAwait(true);
            var wholeTotal = wholeOutcome.Amount;
            CheckDrawerAfterRefund(cashBeforeWhole, wholeOutcome.Cash, "whole|" + returnKey, saleId, wholeOutcome.NonCash);
            ShiftEventsStore.Record(
                ShiftEventsStore.KindReturn,
                PosApp.ActiveShiftId,
                ShiftEventsStore.OperationKey(saleId),
                (double)wholeTotal,
                wholeOutcome.Note);
            // 2026-10-20 (К8): остаток вернувшегося товара — сразу (брак на склад не возвращается).
            if (!reasonDialog.IsDefect)
                ReturnToStock(Lines.Where(l => l.CanReturn).ToList());

            var wholeLines = ScaleRefundLines(Lines.Where(l => l.CanReturn).Select(l => (
                Name: l.Title,
                Quantity: l.Quantity > 0 ? l.Quantity : 1,
                UnitPrice: l.Quantity > 0 ? l.RefundSum / (decimal)l.Quantity : l.RefundSum,
                Sum: l.RefundSum)).ToList(), linesTotal, wholeTotal);
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

            // 2026-09-13, живой баг: полный возврат чека не отменял баллы лояльности,
            // начисленные/списанные при этой продаже (см. BasketPanelViewModel.
            // CreditOrRedeemLoyaltyPoints) — покупатель сохранял бонусы за возвращённый товар
            // навсегда, а списанные на оплату баллы не восстанавливались. Безопасно вызывать
            // всегда: если для этой продажи транзакции не было (лояльность выключена, продажа
            // без клиента, продажа была офлайн), метод просто ничего не делает.
            try
            {
                ClientLoyaltyStore.ReverseRemainingForSale(saleId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Loyalty reversal on full return failed: {ex.Message}", "WARNING");
            }

            // 2026-10-20 (живой аудит, К7): ошибка печати — строкой в этом же окне, а не отдельным окном.
            // 2026-10-23 (аудит, круг 7): как в возврате позиций — предупреждение о принтере показываем как info (без «Отложить ошибку»).
            var wholeDoneText = Tr.T("Полный возврат чека оформлен.", "Чек толугу менен кайтарылды.", "Full receipt return completed.", "Fişin tam iadesi tamamlandı.", "Chek to'liq qaytarildi.")
                    + "\n" + RefundToGiveText(wholeOutcome)
                    + ReturnPrintNote(wholePrintError);
            if (wholePrintError is null)
                PosMessageBox.Show(this, wholeDoneText, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Information);
            else
                PosAlertDialog.Show(this, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), wholeDoneText, PosAlertKind.Warning, null, info: true);
            await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            // 2026-10-16, ревью: 4xx (кроме 408/429) — ключ забыт; иначе исход неизвестен, ключ оставлен для повтора.
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ex.Message, Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, string.IsNullOrWhiteSpace(ex.Message) ? Tr.T("Нет подключения.", "Байланыш жок.", "No connection.", "Bağlantı yok.", "Aloqa yo'q.") : ex.Message,
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, Tr.T("Превышено время ожидания.", "Күтүү убактысы аяктады.", "Timed out.", "Zaman aşımına uğradı.", "Kutish vaqti tugadi."), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }

        // 2026-10-16, ревью: ответ не дошёл — сервер мог провести возврат; сверяем по чеку и показываем чек как на сервере.
        if (outcomeUnknown)
        {
            await ReportReturnIfAppliedAsync(saleId, refundableBefore, estimatedWhole, reverseLoyalty: true,
                whole: true, returnedLines: Lines.Where(l => l.CanReturn).ToList(), isDefect: reasonDialog.IsDefect).ConfigureAwait(true);
            await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
        }
    }

    private void ShowErr(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.IsVisible = true;
    }

    // 2026-10-22 (модалки, пакет 1): ошибка ввода («отметьте позицию», «выберите чек») — красной строкой в самом окне
    // (ErrorText над кнопками), а не всплывающим окном поверх. Ошибки сервера/сети/печати — по-прежнему окном.
    private string? _inputError;

    private void ShowInputErr(string msg)
    {
        _inputError = msg;
        ShowErr(msg);
    }

    private void SearchReceiptBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchFilter = SearchReceiptBox.Text ?? "";
        ApplySalesFilter();

        // Номер, которого нет среди загруженных чеков, — ищем на сервере (2026-09-28, BE-08).
        _numberLookupCts?.Cancel();
        _numberLookupCts = null;
        if (FilteredSales.Count == 0
            && NurCrmReportsApi.ParseSaleNumber(_searchFilter) is { } saleNumber
            && Sales.Any(s => !string.IsNullOrWhiteSpace(s.ReceiptNumber)))
        {
            _numberLookupCts = new CancellationTokenSource();
            _ = LookupSaleByNumberAsync(saleNumber, _numberLookupCts.Token);
        }
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
        /// 2026-09-28: список — все продажи компании, новые первыми, страницы подряд, то есть
        /// номер совпадает с «№» в «Продажах» сайта и в «Истории чеков» кассы
        /// (ReceiptHistoryService.LoadServerAsync считает так же).
        /// </summary>
        public string DisplayNumber => !string.IsNullOrWhiteSpace(Item.ReceiptNumber)
            ? Item.ReceiptNumber!
            : $"№{position}";

        public string SaleDateDisplay => Item.SaleDate == DateTime.MinValue
            ? "—"
            // 2026-10-19, К1: год двумя цифрами — на 1024 дата помещается целиком (было «19.10.2026 2…»).
            : Item.SaleDate.ToString("dd.MM.yy HH:mm", CultureInfo.InvariantCulture);

        public bool HasReturnMark => ReturnMark.Length > 0;

        /// <summary>2026-10-21 (пакет 6, п. 35): чек после возврата помечен в списке — по статусу сервера или по журналу
        /// возвратов этой кассы (SaleReturnLedger): «↩ возвращён» / «↩ частично».</summary>
        public string ReturnMark
        {
            get
            {
                var status = Item.Status ?? "";
                var ledger = SaleReturnLedger.Get(Item.SaleId);
                var full = status is "returned" or "refunded" or "canceled" or "cancelled"
                    || (ledger is { Total: > 0m } && ledger.Remaining <= 0.004m && ledger.Refunds.Count > 0);
                if (full)
                    return "↩ " + Tr.T("возвращён", "кайтарылды", "returned", "iade edildi", "qaytarildi");
                if (status is "partially_returned" or "partial_return" || ledger is { Refunds.Count: > 0 })
                    return "↩ " + Tr.T("частично", "жарым-жартылай", "partly", "kısmen", "qisman");
                return "";
            }
        }
        // 2026-10-19, К1: «1 382,50 сом» (MoneyText), как в чеке.
        public string TotalAmountDisplay => MoneyText.Som(Item.TotalAmount);
    }
}
