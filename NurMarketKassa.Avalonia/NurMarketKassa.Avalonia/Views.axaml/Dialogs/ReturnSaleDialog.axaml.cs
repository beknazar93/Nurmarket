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

    /// <summary>2026-11-02 (К2-24): нет связи при возврате — понятный текст вместо технического сообщения; исход неизвестен, касса проверит чек сама.</summary>
    private static string ReturnNoConnectionText() => Tr.T(
        "Нет связи с сервером. Возврат мог пройти — касса проверит чек сама. Проверьте результат в «Истории чеков» перед повтором и перед выдачей денег.",
        "Сервер менен байланыш жок. Кайтаруу өтүп кетиши мүмкүн — касса чекти өзү текшерет. Кайталоодон жана акча берүүдөн мурун «Чектердин тарыхынан» жыйынтыкты текшериңиз.",
        "No connection to the server. The return may have gone through — the till will check the receipt itself. Check the result in “Receipt history” before retrying and before paying out.",
        "Sunucuyla bağlantı yok. İade gerçekleşmiş olabilir — kasa fişi kendisi kontrol edecek. Tekrar etmeden ve ödeme yapmadan önce sonucu «Fiş geçmişi»nde kontrol edin.",
        "Server bilan aloqa yo'q. Qaytarish o'tgan bo'lishi mumkin — kassa chekni o'zi tekshiradi. Qayta urinishdan va pul berishdan oldin natijani «Cheklar tarixi»da tekshiring.");

    /// <summary>2026-11-02 (аудит №2, К3-03): когда ушёл запрос на возврат — документ возврата сервера старше этого момента не принимается за свой.</summary>
    private DateTimeOffset _refundSentAt;
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
            OnPropertyChanged(nameof(CanReturnWhole));
            RefreshWholeReceiptButton();
            OnPropertyChanged(nameof(HasNoSales));
        }
    }

    /// <summary>2026-11-01 (аудит «10 из 10», Ж-05): «Вернуть весь чек» не активна у чека, где всё уже возвращено (по returned_qty / returnable_qty
    /// строк) — раньше доходила до «0,00 сом» и причины, и только потом сервер отказывал.</summary>
    public bool CanReturnWhole => !IsBusy && !(Lines.Count > 0 && !Lines.Any(x => x.CanReturn));

    /// <summary>2026-11-01 (живая проверка NBS, Д-2): UpdateReceiptChrome ставил кнопке IsEnabled прямо в коде — это стирало привязку к CanReturnWhole, и у полностью
    /// возвращённого чека кнопка оставалась яркой. Теперь доступность задаётся одним местом: есть чек и CanReturnWhole (стиль :disabled — серый).</summary>
    private void RefreshWholeReceiptButton()
    {
        if (ReturnWholeReceiptButton is { } button)
            button.IsEnabled = !string.IsNullOrEmpty(_currentSaleId) && CanReturnWhole;
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
        OnPropertyChanged(nameof(CanReturnWhole));
        RefreshWholeReceiptButton();
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
            SalesDataGrid.Columns[0].Width = new DataGridLength(narrow ? 120 : 132); // 2026-11-01: заголовок «№ чека»/«Receipt #» обрезался
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
    /// <summary>2026-10-29: сервер недоступен — это окно закрывается, открывается «Возврат без связи» (чеки из памяти кассы). Раньше здесь был тупик с текстом ошибки.</summary>
    private void SwitchToOfflineReturn()
    {
        var owner = Owner as Window;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Close();
            _ = OfflineOperationsWindow.OpenAsync(owner, OfflineOperationsWindow.Mode.Return);
        });
    }

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
            SwitchToOfflineReturn();
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
            if (ex.Throttled)
                ShowErr(ServerAnswerWait.ThrottledMessage);
            else
                SwitchToOfflineReturn();
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
            else if (!Lines.Any(x => x.CanReturn))
                ShowErr(AlreadyReturnedText()); // 2026-11-01 (Ж-05): чек уже возвращён целиком — сразу при открытии, а не после ввода причины
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

    /// <summary>2026-11-01 (code-review, Ж-01): имя клиента чека «в долг» (чек → имя) — по нему считаются оплаты долга, записанные на этой кассе.</summary>
    private readonly Dictionary<string, string> _saleClientNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>2026-11-01 (Ж-08): «к выдаче», показанное кассиру в подтверждении до отправки (чек → сумма). Итоговое окно сверяет с ним фактическую выдачу.</summary>
    private readonly Dictionary<string, decimal?> _expectedPayouts = new(StringComparer.OrdinalIgnoreCase); // null — «сумму определит сервер» (подпись кнопки без суммы)

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
        // 2026-11-01 (сверка контракта с NBS, п. 5): чек возвращён целиком — остаток долга по нему 0 (сервер оставляет старое число), «долг по чеку» не берём.
        if (CartDisplayHelper.IsSaleFullyReturned(sale))
        {
            _serverDebtLeft[saleId] = Task.FromResult<decimal?>(0m);
            return;
        }
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
        _saleClientNames[saleId] = IdOf("client_name") ?? IdOf("client_display") ?? IdOf("debt_client_name") ?? ""; // 2026-11-01: имя клиента — ключ локальных оплат долга (ShiftEventsStore.DebtPaidByClientName)
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
        // 2026-11-01 (ответ Умара: в чеке есть return_status): чек возвращён целиком — строки не возвращаются повторно, «Вернуть весь чек» неактивна.
        var saleFullyReturned = sale.ValueKind == JsonValueKind.Object && sale.TryGetProperty("return_status", out var saleReturnStatus)
            && saleReturnStatus.ValueKind == JsonValueKind.String
            && CartDisplayHelper.IsFullyReturnedStatus(saleReturnStatus.GetString())
            && CartDisplayHelper.EnumerateSaleLineItems(sale).All(it => CartDisplayHelper.RefundableQuantity(it) <= 1e-6); // статус — только вместе с количеством (контрольное ревью)
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
            // 2026-10-29 (ревью, В-5): возвраты, оформленные без связи и ещё не дошедшие до сервера, вычитаются из «можно вернуть» — сервер их пока не знает.
            qty = Math.Max(0, qty - NurMarketKassa.Services.OfflineOperationsStore.UnsentRefundQuantity(_currentSaleId, CartDisplayHelper.TryProductId(lineItem), CartDisplayHelper.ItemName(lineItem)));
            var canReturn = qty > 0 && !CartDisplayHelper.LineLooksFullyReturned(lineItem) && !saleFullyReturned;

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
        // 2026-10-30 («Возврат больше положенного: сервер записал 2,00, положено 1,80»): последний возврат по чеку (отмечены все оставшиеся строки
        // целиком) — ровно остаток сервера, как у него; иначе — не больше остатка (RefundAmountMath).
        if (refundableTotal is { } left && left > 0m)
        {
            var allLeft = selected.Count == returnableLines && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6);
            return RefundAmountMath.AmountForReturn(total, left, allLeft && left <= total + 0.01m * selected.Count);
        }
        return total;
    }

    /// <summary>2026-11-01 (аудит «10 из 10», Ж-05): по этому чеку все строки уже возвращены (returnable_qty = 0).</summary>
    private static string AlreadyReturnedText() => Tr.T(
        "По этому чеку всё уже возвращено — возвращать нечего.",
        "Бул чек боюнча баары мурунтан эле кайтарылган — кайтарууга эч нерсе жок.",
        "Everything on this receipt has already been returned — nothing left to return.",
        "Bu fişte her şey zaten iade edilmiş — iade edilecek bir şey yok.",
        "Bu chek bo'yicha hammasi allaqachon qaytarilgan — qaytariladigan narsa yo'q.");

    private async Task ReturnSelectedCoreAsync(string saleId)
    {
        // 2026-10-27 (приёмка 1.17.63, Д-5): дальше везде — строки с тем количеством, которое кассир выбрал (по умолчанию всё).
        var selected = EffectiveReturnLines(Lines.Where(x => x.CanReturn && x.IsSelected));
        if (selected.Count == 0 && Lines.Count > 0 && !Lines.Any(x => x.CanReturn))
        {
            ShowInputErr(AlreadyReturnedText()); // 2026-11-01 (Ж-05)
            return;
        }
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
        // 2026-11-01 (Ж-08): «К выдаче» — до отправки, локальным расчётом по данным чека (не дольше 300 мс на остаток долга).
        var payoutPreview = await ExpectedPayoutTextAsync(saleId, total,
            reversesLoyalty: selected.Count == Lines.Count(x => x.CanReturn) && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6)).ConfigureAwait(true);
        if (await PosMessageBox.ShowModalAsync(this,
                Tr.T($"Выбрано позиций: {selected.Count}\nСумма возврата: {totalShown}",
                    $"Тандалган позиция: {selected.Count}\nКайтаруу суммасы: {totalShown}",
                    $"Items selected: {selected.Count}\nRefund amount: {totalShown}",
                    $"Seçilen kalem: {selected.Count}\nİade tutarı: {totalShown}",
                    $"Tanlangan pozitsiyalar: {selected.Count}\nQaytarish summasi: {totalShown}") + PartialReturnNote(selected) + elsewhere + payoutPreview,
                Tr.T("Подтверждение возврата", "Кайтарууну ырастоо", "Confirm return", "İadeyi onayla", "Qaytarishni tasdiqlash"),
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                // 2026-10-22 (модалки, пакет 1): кнопки — глагол с суммой («Вернуть 1 620,00 сом» / «Отмена») вместо «Продолжить?».
                confirmText: ConfirmButtonText(saleId, total, whole: false, totalShown)).ConfigureAwait(true) != MessageBoxResult.Yes)
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
            var returnKey = PendingReturn.Get("lines|" + (isDefect ? "defect|" : "") + saleId + "|" + string.Join(";", requests
                .OrderBy(r => r.LineId, StringComparer.Ordinal)
                .Select(r => r.LineId + ":" + r.Quantity.ToString("0.###", CultureInfo.InvariantCulture))));

            var debtSaleForDrawer = IsDebtSaleForDrawer(saleId);
            var cashBefore = await ReadDrawerBeforeRefundAsync(debtSaleForDrawer).ConfigureAwait(true);
            _refundSentAt = DateTimeOffset.Now; // 2026-11-02 (К3-03): документ возврата старше этого момента — не наш
            var refundResponse = await PosRefundService.RefundLinesAsync(
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
                selected.Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList(),
                ServerRefundFigures.TryRead(refundResponse)) // 2026-11-01 (ответ Умара): выдача — из refund.cash
                .ConfigureAwait(true);
            total = outcome.Amount;
            CheckDrawerAfterRefund(cashBefore, outcome.Cash, "lines|" + returnKey, saleId, outcome.NonCash, outcome.ServerCashOut);

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
                && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6);
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
                    + "\n" + RefundToGiveText(outcome, TakeExpectedPayout(saleId))
                    + DrawerUnverifiedNote(debtSaleForDrawer, cashBefore, outcome.Cash)
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
            var ownRetry = PendingReturn.HasUnansweredAttempt; // 2026-11-01 (приёмка, К-01): до OnError — ключ ещё помнит, что прошлая отправка осталась без ответа
            outcomeUnknown = PendingReturn.OnError(ex);
            // 2026-10-30: «возврат больше положенного» — понятный текст с суммой, которую сервер разрешает (дальше окно пересчитает остаток чека).
            PosMessageBox.Show(this, ExceedsOrServerText(ex, ownRetry), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ReturnNoConnectionText(), // 2026-11-02 (К2-24): без сырого ex.Message
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            // 2026-11-01 (контрольный аудит, К2-12): честное состояние вместо «Превышено время ожидания» — возврат не потерян, касса сама проверит чек; кассиру — что делать.
            PosMessageBox.Show(this, Tr.T(
                    "Сервер медленно отвечает — возврат не потерян. Касса проверит чек сама; проверьте результат в «Истории чеков» перед повтором и перед выдачей денег.",
                    "Сервер жай жооп берүүдө — кайтаруу жоголгон жок. Касса чекти өзү текшерет; кайталоодон жана акча берүүдөн мурун «Чектердин тарыхынан» жыйынтыкты текшериңиз.",
                    "The server is slow to respond — the return is not lost. The till will check the receipt itself; check the result in “Receipt history” before retrying and before paying out.",
                    "Sunucu yavaş yanıt veriyor — iade kaybolmadı. Kasa fişi kendisi kontrol edecek; tekrar etmeden ve ödeme yapmadan önce sonucu «Fiş geçmişi»nde kontrol edin.",
                    "Server sekin javob bermoqda — qaytarish yo'qolgani yo'q. Kassa chekni o'zi tekshiradi; qayta urinishdan va pul berishdan oldin natijani «Cheklar tarixi»da tekshiring."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
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
                                && selected.All(l => l.Quantity >= l.MaxReturnQuantity - 1e-6),
                returnedLines: selected, isDefect: isDefect).ConfigureAwait(true);
        await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true);
    }

    /// <summary>2026-10-04, стресс-тест на тестовом аккаунте: документ только что оформленного возврата
    /// так, как его записал сервер (GET pos/returns/?sale=…, самый новый), и сколько денег он выдал (движения
    /// «Возврат по чеку»; у продажи «в долг» документ — на весь чек, а выдано только внесённое).
    /// 2026-10-20 (живой аудит, К1–К3): только цифры сервера для сверки — сумму к выдаче считает ResolveRefundAsync.
    /// Документ, уже записанный в журнал прошлым возвратом этой кассы, — не этот возврат (сервер мог не успеть
    /// показать новый). null — сервер не ответил.</summary>
    private static async Task<(decimal? Doc, string? DocId, decimal? CashOut)> ServerReturnFiguresAsync(string? saleId, SaleLedgerEntry? ledger, DateTimeOffset? notBefore = null)
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
                .Where(r => string.Equals(r.SaleId, saleId, StringComparison.OrdinalIgnoreCase) && r.Amount > 0m && !seen.Contains(r.Id)
                            // 2026-11-02 (К3-03): документ, созданный задолго до отправки этого возврата, — прошлый возврат, а не этот (минута — запас на часы сервера)
                            && (notBefore is not { } sentAt || r.CreatedAt is not { } created || created >= sentAt - TimeSpan.FromMinutes(1)))
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
    private static async Task<decimal?> ReadDrawerBeforeRefundAsync(bool debtSale = false)
    {
        var shiftId = PosApp.ActiveShiftId;
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;
        // 2026-11-01 (финальная приёмка 1.17.73, Ф-2): у долгового чека сервер не вычитает выдачу из expected_cash — поправка ящика нужна, а она считается по остатку «до». Поэтому для долгового
        // чека ждём остаток прежние 6 с (внутренний предел запроса), для остальных — 2,5 с (там ошибки сервера нет, пропуск сверки безвреден).
        var maxWait = debtSale ? TimeSpan.FromSeconds(6.5) : TimeSpan.FromMilliseconds(2500);
        // 2026-11-01 (контрольный аудит, К2-12): остаток «до» ждём не дольше 2,5 с (раньше до 6 с перед каждым возвратом). Не успели — null: сверка ящика после возврата пропускается,
        // сам возврат не задерживается (поправка не запишется, но шапку и X/Z можно обновить F5/следующей сверкой).
        var read = ExchangeCashCorrections.ReadExpectedCashAsync(App.GetRequiredService<NurMarketApiClient>(), shiftId);
        var first = await Task.WhenAny(read, Task.Delay(maxWait)).ConfigureAwait(true);
        if (first == read)
            return await read.ConfigureAwait(true);
        PosLogger.Log($"Возврат: остаток смены до возврата не получен за {maxWait.TotalSeconds:0.#} с — сверка ящика после возврата пропускается{(debtSale ? " (долговой чек: проверьте «Касса:» при закрытии смены)" : "")}.", "WARNING");
        return null;
    }

    /// <summary>2026-11-01 (Ф-2): чек «в долг» — выдача по ответу сервера (журнал кассы + движение денег): для поправки ящика нужен остаток смены «до» возврата.</summary>
    private bool IsDebtSaleForDrawer(string saleId)
    {
        var ledger = _ledger is { Total: > 0m } known && string.Equals(known.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
            ? known
            : SaleReturnLedger.Get(saleId) is { Total: > 0m } stored ? stored : null;
        var method = (ledger?.Method is { Length: > 0 } m ? m
            : _saleMethods.TryGetValue(saleId, out var sm) ? sm
            : string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleMethod : null) ?? "";
        return ledger is { Debt: > 0.005m } || method.Trim().ToLowerInvariant() == "debt";
    }

    /// <summary>2026-11-01 (Ф-2): остаток смены «до» возврата долгового чека не получен — поправка ящика не записана; кассиру — честное предупреждение (в журнале WARNING есть).</summary>
    private static string DrawerUnverifiedNote(bool debtSale, decimal? cashBefore, decimal cashOut) =>
        debtSale && cashBefore is null && cashOut > 0.005m
            ? "\n" + Tr.T(
                "Внимание: остаток смены с сервера не получен вовремя — «Касса:» после возврата долгового чека может быть завышена на выданную сумму. Проверьте «Касса:» и пересчёт ящика при закрытии смены.",
                "Көңүл буруңуз: смена калдыгы серверден өз убагында алынган жок — карыз чегин кайтаргандан кийин «Касса:» берилген сумма боюнча жогору көрүнүшү мүмкүн. Смена жабылганда «Касса:» жана кассаны кайра санаңыз.",
                "Warning: the shift balance was not received from the server in time — “Till” may be overstated by the amount paid out after a credit-receipt return. Check “Till” and the drawer count when closing the shift.",
                "Dikkat: vardiya bakiyesi sunucudan zamanında alınamadı — veresiye fişi iadesinden sonra «Kasa» verilen tutar kadar fazla görünebilir. Vardiyayı kapatırken «Kasa»yı ve çekmece sayımını kontrol edin.",
                "Diqqat: smena qoldig'i serverdan o'z vaqtida olinmadi — qarz cheki qaytarilgandan keyin «Kassa» berilgan summaga ortiq ko'rinishi mumkin. Smenani yopishda «Kassa»ni va qutini qayta sanashni tekshiring.")
            : "";

    /// <summary>Сверка после возврата: наличные ушли из ящика на <paramref name="cashOut"/>, а сервер остаток не уменьшил — поправка.</summary>
    /// <param name="serverCashOut">2026-11-01 (приёмка, Ж-01): сколько наличных выдачи записал сервер, если меньше выданного кассой (касса выдала по оплатам клиента, сервер
    /// — по движениям денег: чек 001967, касса 2, сервер 1). Остаток смены на сервере тогда завышен на разницу — поправка пишется на неё, ящик сходится.</param>
    private static void CheckDrawerAfterRefund(decimal? before, decimal cashOut, string key, string? saleId = null, decimal nonCash = 0m, decimal? serverCashOut = null)
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
            {
                var overstated = (a - before.Value) + cashOut;
                // 2026-11-01 (приёмка, Ж-01): сервер вычел из ящика меньше, чем выдала касса, — ожидаемое завышение = разница; иначе прежняя проверка (сервер не вычел ничего).
                var suspect = cashOut;
                if (serverCashOut is { } serverGave && serverGave < cashOut - 0.009m)
                {
                    // 2026-11-01 (code-review, Ж-01): поправка — по факту выданных денег (завышение остатка сервера), а не по точному совпадению с записью сервера; допуск 0,01 — только округление.
                    // Правдоподобное завышение (0 < x ≤ выдано) пишется как есть; расхождение с ожидаемым (выдано − записано сервером) — в журнал.
                    var expectedGap = cashOut - serverGave;
                    if (overstated > 0.009m && overstated <= cashOut + 0.01m)
                    {
                        suspect = overstated;
                        if (Math.Abs(overstated - expectedGap) > 0.01m)
                            PosLogger.Log($"Возврат (наличные): остаток смены на сервере завышен на {overstated:0.00}, ожидалось {expectedGap:0.00} (выдано {cashOut:0.00}, сервер записал {serverGave:0.00}) — поправка по факту.", "WARNING");
                    }
                    else
                        PosLogger.Log($"Возврат (наличные): остаток смены разошёлся на {overstated:0.00} при выдаче {cashOut:0.00} (сервер записал {serverGave:0.00}) — поправка не записана, сверить смену вручную.", "WARNING");
                }
                ExchangeCashCorrections.RecordIfKnownServerError(shiftId, "refund:" + key, overstated, suspect, "Возврат (наличные)");
            }
        });
    }

    /// <param name="ServerCashOut">2026-11-01 (аудит «10 из 10», Ж-01): сколько выдачи записал сервер, если оно не совпало с положенным по оплатам клиента
    /// (предоплата + оплаты долга, текущий долг) — кассиру показывается сумма и причина расхождения; null — совпало или сервер не сказал.</param>
    /// <param name="CalcOverServer">2026-11-01 (code-review, Ж-01): расчёт по журналу кассы был выше цифры сервера и не объяснён оплатами на этой кассе — выдана меньшая (сервера), это расчёт кассы.</param>
    private sealed record RefundOutcome(decimal Amount, decimal Cash, decimal NonCash, decimal DebtReduced, bool SplitKnown, bool DebtUnknown = false,
        decimal ServerOver = 0m, decimal? ServerCashOut = null, decimal? CalcOverServer = null, decimal? DebtRemaining = null, decimal Balance = 0m, decimal? ServerRejected = null, decimal? ServerDiffersCalc = null)
    {
        public decimal Money => Cash + NonCash;

        /// <summary>Примечание события смены (ShiftEventsStore): разбивка для окна закрытия смены.</summary>
        public string Note => $"cash={SaleReturnLedger.Inv(Cash)};noncash={SaleReturnLedger.Inv(NonCash)};debt={SaleReturnLedger.Inv(DebtReduced)}"
                              + (ServerOver > 0.005m ? $";server_over={SaleReturnLedger.Inv(ServerOver)}" : "")
                              + (ServerCashOut is { } under && under < Cash - 0.005m ? $";server_under={SaleReturnLedger.Inv(Cash - under)}" : ""); // 2026-11-01 (Ж-01): сервер выдал меньше — окно закрытия смены объяснит разницу
    }

    /// <summary>2026-10-20 (живой аудит, К1–К3). <paramref name="expected"/> — расчёт кассы (доля строк со скидкой на чек,
    /// не больше остатка оплаченного). Сервер вернул меньше — берём его (денег ушло меньше); больше (скидка на чек не
    /// учтена: 001837 — 80 из 75) — его число кассиру не показываем: журнал (WARNING) и «Некорректные чеки». Деньги:
    /// у продажи «в долг» сначала уменьшается долг, выдаётся только внесённое (если сервер сказал, сколько выдал, — его
    /// цифра); у смешанной — пропорционально тому, что ещё не возвращено наличными и безналом.</summary>
    /// <param name="figures">2026-11-01 (ответ Умара, п. 3): цифры из ответа сервера на возврат — выдача наличными в <c>refund.cash</c> (не <c>debt_adjustment.reduced_by</c>, это уменьшение долга).
    /// Есть <c>refund.cash</c> — сервер называет выдачу, касса ему доверяет (без «Расчёт кассы X, сервер Y» и поправок ящика); нет (старый сервер, ответ не дошёл) — прежний расчёт.</param>
    private async Task<RefundOutcome> ResolveRefundAsync(string saleId, decimal expected, bool whole, List<SaleLedgerLine> lines, ServerRefundFigures? figures = null)
    {
        // 2026-10-21 (ревью денег, мелочь 4): журнал и способ оплаты — именно этого чека, даже если кассир уже открыл другой.
        var ledger = _ledger is { Total: > 0m } known && string.Equals(known.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
            ? known
            : SaleReturnLedger.Get(saleId) is { Total: > 0m } stored ? stored : null;
        var saleMethod = _saleMethods.TryGetValue(saleId, out var m) ? m
            : string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleMethod : null;
        // 2026-11-01 (К2-12): сервер назвал выдачу (refund.cash) — документ возврата и движения нужны только для сверки и журнала: ждём их не дольше 3 с.
        // Старый сервер (цифры выдачи только в документе) — как раньше, без ограничения.
        (decimal? Doc, string? DocId, decimal? CashOut) serverFigures = (null, null, null);
        if (figures is { HasCash: true, RefundId: { Length: > 0 } responseRefundId })
        {
            // 2026-11-02 (аудит №2, К3-03): сервер прислал id и сумму документа возврата в ответе — берём их, список возвратов не запрашиваем (быстрее, и устаревший документ не подставится).
            serverFigures = (figures.RefundAmount, responseRefundId, null);
        }
        else
        {
            var figuresTask = ServerReturnFiguresAsync(saleId, ledger, _refundSentAt == default ? null : _refundSentAt);
            if (figures is { HasCash: true })
            {
                var first = await Task.WhenAny(figuresTask, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(true);
                if (first == figuresTask)
                    serverFigures = await figuresTask.ConfigureAwait(true);
                else
                    PosLogger.Log($"Возврат по чеку {saleId}: документ возврата не получен за 3 с — итог по ответу сервера (refund.cash); проверить возврат в «Истории чеков».", "WARNING");
            }
            else
                serverFigures = await figuresTask.ConfigureAwait(true);
        }
        var (doc, docId, cashOut) = serverFigures;
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

        // 2026-11-01 (code-review, Ж-01): сколько оплат долга по этому клиенту записано на этой кассе — объясняет ли оно расхождение расчёта и цифры сервера.
        var localDebtPaid = LocalDebtPaidHere(saleId, ledger, serverDebtLeft, cashOnly: false);
        var localDebtCash = LocalDebtPaidHere(saleId, ledger, serverDebtLeft, cashOnly: true);
        var (cash, nonCash, debtReduced, splitKnown, debtUnknown, serverCashOut, calcOverServer) = ComputeRefundSplit(ledger, saleMethod, amount, cashOut, serverDebtLeft, localDebtPaid, localDebtCash);
        decimal? debtRemaining = null;
        var balancePart = 0m;
        decimal? rejectedServerMoney = null;
        decimal? serverDiffersCalc = null; // расчёт кассы, если выдача по серверу допустима, но отличается от суммы возврата
        if (figures is { HasCash: true } trusted)
        {
            // 2026-11-01 (ответ Умара, п. 3): сервер назвал выдачу — доверяем ему, НО только если цифры правдоподобны (приёмка 1.17.72, В-1). Расчёт кассы остаётся для блока
            // «К выдаче (ожидаемо)» ДО проведения, для старых серверов и как запасной вариант, когда ответ сервера не сходится с суммой возврата.
            var local = ComputeRefundSplit(ledger, saleMethod, amount, null, serverDebtLeft, null, localDebtCash);
            var methodNow = (ledger?.Method is { Length: > 0 } knownMethodNow ? knownMethodNow : saleMethod ?? "").Trim().ToLowerInvariant();
            var debtSaleNow = ledger is { Debt: > 0.005m } || methodNow == "debt";
            var rawCash = trusted.Cash!.Value;
            var srvCash = Math.Max(0m, rawCash);
            var srvBalance = Math.Max(0m, trusted.Balance ?? 0m);
            decimal srvNon, srvDebt;
            bool plausible;
            if (!debtSaleNow)
            {
                // Продажа не в долг. Цифру сервера отбрасываем ТОЛЬКО при явной нелепости (контрольное ревью): наличные < 0, наличные больше суммы возврата, либо чек оплачен
                // только наличными, а сервер говорит «наличных 0» при положительной сумме. Расхождение суммы частей с суммой возврата (чек оплачен бонусами, часть возвращена
                // на другой кассе/сайте) — не повод отбрасывать: выдаём по серверу и предупреждаем. Безнал без refund.noncash — доля расчёта кассы по способу оплаты чека, не «сумма − наличные».
                srvNon = Math.Max(0m, trusted.NonCash ?? local.NonCash);
                // 2026-11-01 (контрольный аудит, К2-04): наличные + безнал + «на баланс» не больше суммы возврата (допуск 0,01). Безнал из расчёта кассы (сервер его не назвал)
                // урезается до остатка суммы; названный сервером безнал сверх суммы — цифра нелепа, берём расчёт кассы.
                if (trusted.NonCash is null && srvCash + srvNon + srvBalance > amount + 0.01m)
                    srvNon = Math.Max(0m, amount - srvCash - srvBalance);
                srvDebt = 0m;
                plausible = rawCash >= -0.005m && rawCash <= amount + 0.01m
                            && srvCash + srvNon + srvBalance <= amount + 0.01m
                            && !(methodNow == "cash" && srvCash <= 0.005m && amount > 0.005m);
            }
            else
            {
                // Чек в долг: 0 ≤ выдача ≤ сумма возврата − уменьшение долга (если сервер его назвал).
                srvNon = Math.Max(0m, trusted.NonCash ?? (local.DebtUnknown ? 0m : local.NonCash));
                srvDebt = trusted.DebtReducedBy is { } reducedBy ? Math.Max(0m, reducedBy) : Math.Max(0m, amount - srvCash - srvNon - srvBalance);
                plausible = rawCash >= -0.005m && srvCash + srvNon + srvBalance <= amount - (trusted.DebtReducedBy is not null ? srvDebt : 0m) + 0.01m
                            // 2026-11-02 (аудит №2, К3-04): сервер не прислал debt_adjustment — выдача не больше того, что покупатель реально внёс по расчёту кассы (при известном остатке долга)
                            && (trusted.DebtReducedBy is not null || local.DebtUnknown || srvCash + srvNon <= local.Cash + local.NonCash + 0.01m);
            }
            if (plausible)
            {
                cash = srvCash;
                nonCash = srvNon;
                balancePart = srvBalance;
                debtReduced = srvDebt;
                // «Остаток долга» — только из ответа на ЭТОТ возврат и если чек не возвращён целиком (у full сервер оставляет старый остаток; фактически долг по чеку 0).
                debtRemaining = CartDisplayHelper.IsFullyReturnedStatus(trusted.ReturnStatus) ? null : trusted.RemainingDebt;
                splitKnown = true;
                debtUnknown = false;
                serverCashOut = null;
                calcOverServer = null;
                if (!local.DebtUnknown && Math.Abs(local.Cash + local.NonCash - (cash + nonCash)) > 0.009m)
                {
                    PosLogger.Log($"Возврат по чеку {number} ({saleId}): сервер (refund.cash) выдаёт {cash + nonCash:0.00}, расчёт кассы был {local.Cash + local.NonCash:0.00} — берём сервер.", "SALES");
                    // Не-долговой чек: сумма выдачи сервера не равна сумме возврата — это допустимо, но кассиру «Сервер выдаёт X, расчёт кассы Y — проверьте».
                    if (!debtSaleNow && Math.Abs(cash + nonCash + balancePart - amount) > 0.01m)
                        serverDiffersCalc = local.Cash + local.NonCash;
                }
            }
            else
            {
                rejectedServerMoney = srvCash + srvNon + srvBalance;
                PosLogger.Log($"Возврат по чеку {number} ({saleId}): ответ сервера не сходится с суммой возврата {amount:0.00} (наличными {srvCash:0.00}, безнал {srvNon:0.00}, на баланс {srvBalance:0.00}, долг −{srvDebt:0.00}) — "
                              + $"используется расчёт кассы {cash + nonCash:0.00}, проверить возврат.", "WARNING");
            }
        }
        if (calcOverServer is { } calcShown)
            PosLogger.Log($"Возврат по чеку {number} ({saleId}): расчёт по журналу кассы {calcShown:0.00}, сервер {cash + nonCash:0.00}; оплаты долга на этой кассе ({localDebtPaid:0.00}) разницу не объясняют — выдаётся меньшая, проверить долг клиента.", "WARNING");
        if (serverCashOut is { } serverGave)
            PosLogger.Log($"Возврат по чеку {number} ({saleId}): по оплатам клиента положено выдать {cash + nonCash:0.00} (долг сейчас {serverDebtLeft ?? 0m:0.00}, долг уменьшен на {debtReduced:0.00}), сервер записал выдачу {serverGave:0.00} — кассиру показана сумма по оплатам.", "WARNING");
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
        return new RefundOutcome(amount, cash, nonCash, debtReduced, splitKnown, debtUnknown, serverOver, serverCashOut, calcOverServer, debtRemaining, balancePart, rejectedServerMoney, serverDiffersCalc);
    }

    /// <summary>2026-10-20 (живой аудит, К2, К3): чем вернуть <paramref name="amount"/>. Продажа «в долг» — сначала уменьшается
    /// долг, выдаётся только внесённое (если сервер сказал, сколько выдал, — его цифра; без журнала кассы и без движения денег
    /// сервера — 0); смешанная — пропорционально тому, что ещё не возвращено наличными и безналом; без журнала — по способу
    /// оплаты продажи с сервера (<paramref name="fallbackMethod"/>). Чистая функция — проверяется на стенде.</summary>
    /// <param name="serverDebtLeft">2026-10-21 (ревью денег, п. 2): текущий остаток долга по чеку с сервера (null — не получен).
    /// Долг уменьшается не больше него: клиент мог уже погасить долг, тогда возврат выдаётся деньгами.</param>
    internal static (decimal Cash, decimal NonCash, decimal DebtReduced, bool SplitKnown, bool DebtUnknown, decimal? ServerCashOut, decimal? CalcOverServer) ComputeRefundSplit(
        SaleLedgerEntry? ledger, string? fallbackMethod, decimal amount, decimal? cashOut, decimal? serverDebtLeft = null, decimal? localDebtPaid = null, decimal? localDebtCash = null)
    {
        decimal cash, nonCash, debtReduced;
        var splitKnown = true;
        var debtUnknown = false;
        decimal? serverCashOut = null;
        decimal? calcOverServer = null;
        var method = (ledger?.Method is { Length: > 0 } known ? known : fallbackMethod ?? "").Trim().ToLowerInvariant();
        var isDebtSale = ledger is { Debt: > 0.005m } || method == "debt";
        if (isDebtSale)
        {
            // Долг — сначала; выдаётся только то, что покупатель реально внёс.
            decimal money;
            if (serverDebtLeft is { } debtNow)
            {
                // 2026-10-21 (ревью денег, п. 2): долг уменьшается не больше текущего остатка сервера (и не больше остатка
                // по журналу кассы — сервер мог не уменьшить долг на прошлые возвраты); погашенный долг — выдаётся деньгами.
                // 2026-11-01 (аудит «10 из 10», Ж-01): остаток долга известен — это главное: клиент внёс предоплату и оплаты долга, всё внесённое
                // и есть «положено выдать» (чек 3: предоплата 1 + оплата долга 1, долг сейчас 1 → выдать 2). Раньше цифра сервера (выдача по
                // движениям денег) брала верх и не знала оплат долга — выдавалось 1. Цифра сервера теперь только сверяется (ServerCashOut).
                var debtLeft = ledger is not null ? Math.Min(Math.Max(0m, debtNow), ledger.DebtLeft) : Math.Max(0m, debtNow);
                var byDebt = Math.Max(0m, amount - Math.Min(amount, debtLeft));
                decimal? byServer = cashOut is { } serverGave ? Math.Min(amount, Math.Max(0m, serverGave)) : null;
                if (ledger is null)
                {
                    // 2026-11-01 (приёмка, Ж-01): журнала кассы нет (чек с другой кассы/сайта) — остаток долга сделки может быть общим или неточным, оплаты
                    // не подтверждены: выдачу не уменьшаем ниже цифры сервера, а если цифры расходятся или сервер выдачу не назвал — кассиру предупреждение
                    // «проверьте долг клиента перед выдачей» (как при неизвестном остатке).
                    money = byServer is { } bs ? Math.Max(byDebt, bs) : byDebt;
                    if (byServer is null || Math.Abs(byDebt - byServer.Value) > 0.009m)
                        debtUnknown = true;
                }
                else if (byServer is { } lesser && byDebt > lesser + 0.009m && localDebtPaid is { } paidHere && paidHere < byDebt - lesser - 0.009m)
                {
                    // 2026-11-01 (code-review, Ж-01): расчёт по журналу выше цифры сервера, а разницу не объясняют оплаты долга, записанные на ЭТОЙ кассе (клиент мог
                    // платить на другой кассе или на сайте — журнал устарел): выдаём меньшую сумму, из ящика не уходит то, чего сервер не отразил, кассиру — предупреждение.
                    // Чек 001967-типа (оплаты долга сделаны здесь и записаны в смену) сюда не попадает: выдача по долгу, расхождение — ServerCashOut.
                    money = lesser;
                    calcOverServer = byDebt;
                }
                else
                    money = byDebt;
                if (calcOverServer is null && byServer is { } given && Math.Abs(given - money) > 0.009m)
                    serverCashOut = given;
            }
            else if (cashOut is { } givenByServer)
                money = Math.Min(amount, Math.Max(0m, givenByServer));
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
            // 2026-11-01 (живая регрессия, Р-5): оплаты долга наличными записаны на этой кассе (журнал событий смены) — их способ известен, «неизвестно» не пишем.
            if (money - prepaidMoney > 0.005m && !(localDebtCash is { } paidHereCash && paidHereCash >= money - prepaidMoney - 0.009m))
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
        return (cash, nonCash, debtReduced, splitKnown, debtUnknown, serverCashOut, calcOverServer);
    }

    /// <summary>2026-11-01 (приёмка 1.17.72, С-2): сколько оплат долга по ЭТОМУ чеку записано на этой кассе. Журнал смены хранит имя клиента, а не id сделки/чека, поэтому
    /// берутся только оплаты этого клиента не раньше даты продажи чека и не больше, чем сервер считает погашенным по сделке (журнальный долг чека − текущий остаток).
    /// Нет даты продажи, журнала кассы, имени или остатка долга сервера — 0 (не подтверждено: нейтральная пометка «возвращайте тем же способом»).</summary>
    private decimal LocalDebtPaidHere(string saleId, SaleLedgerEntry? ledger, decimal? serverDebtLeft, bool cashOnly)
    {
        try
        {
            var since = string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleDate : null;
            since ??= ledger?.SoldAt?.LocalDateTime;
            if (since is null || ledger is null || serverDebtLeft is not { } debtNow
                || !_saleClientNames.TryGetValue(saleId, out var name) || string.IsNullOrWhiteSpace(name))
                return 0m;
            var paidOnDeal = Math.Max(0m, ledger.DebtLeft - Math.Max(0m, debtNow));
            if (paidOnDeal <= 0.005m)
                return 0m;
            var sinceUtc = since.Value.Kind == DateTimeKind.Utc ? since.Value : since.Value.ToUniversalTime();
            var recorded = (decimal)ShiftEventsStore.DebtPaidByClientName(name, cashOnly, sinceUtc);
            return Math.Min(recorded, paidOnDeal);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: оплаты долга на этой кассе не подсчитаны: {ex.Message}", "WARNING");
            return 0m;
        }
    }

    /// <summary>2026-11-01 (живая регрессия, Р-5): подпись кнопки подтверждения. Сумма чека/возврата и сумма К ВЫДАЧЕ — разные вещи: если к выдаче иная сумма — «Вернуть весь чек (к выдаче 2,00)»,
    /// если сумму определит сервер — без суммы; иначе прежняя подпись с суммой возврата.</summary>
    private string ConfirmButtonText(string saleId, decimal amount, bool whole, string amountShown)
    {
        if (_expectedPayouts.TryGetValue(saleId, out var payout))
        {
            if (payout is null)
                return whole
                    ? Tr.T("Вернуть весь чек", "Бүт чекти кайтаруу", "Refund whole receipt", "Tüm fişi iade et", "Butun chekni qaytarish")
                    : Tr.T("Вернуть выбранное", "Тандалганды кайтаруу", "Refund selected", "Seçilenleri iade et", "Tanlanganlarni qaytarish");
            if (Math.Abs(payout.Value - amount) > 0.009m)
            {
                var give = MoneyText.Som(payout.Value);
                return whole
                    ? Tr.T($"Вернуть весь чек (к выдаче {give})", $"Бүт чекти кайтаруу (берүүгө {give})", $"Refund whole receipt (pay out {give})", $"Tüm fişi iade et (verilecek {give})", $"Butun chekni qaytarish (berish {give})")
                    : Tr.T($"Вернуть (к выдаче {give})", $"Кайтаруу (берүүгө {give})", $"Refund (pay out {give})", $"İade et (verilecek {give})", $"Qaytarish (berish {give})");
            }
        }
        return whole
            ? Tr.T($"Вернуть весь чек — {amountShown}", $"Бүт чекти кайтаруу — {amountShown}", $"Refund whole receipt — {amountShown}", $"Tüm fişi iade et — {amountShown}", $"Butun chekni qaytarish — {amountShown}")
            : Tr.T($"Вернуть {amountShown}", $"{amountShown} кайтаруу", $"Refund {amountShown}", $"{amountShown} iade et", $"{amountShown} qaytarish");
    }

    /// <summary>2026-11-01 (Ж-08): забирает «к выдаче», показанное в подтверждении (один раз).</summary>
    private decimal? TakeExpectedPayout(string saleId) => _expectedPayouts.Remove(saleId, out var v) ? v : null;

    /// <summary>2026-11-01 (Ж-08, владелец): до отправки возврата — «К выдаче (ожидаемо): наличными A, на карту B; долг клиента уменьшится на C; бонусы −D». Тот же расчёт, что после
    /// ответа сервера (<see cref="ComputeRefundSplit"/>), но без цифры сервера: журнал кассы, способ оплаты чека, остаток долга сделки (запрошен при открытии чека; ждём не дольше
    /// 300 мс — иначе считаем «неизвестен»). Данных не хватает (нет журнала кассы или остатка долга у долгового чека) — «сумму определит сервер» и предупреждение проверить долг клиента.
    /// Кнопка подтверждения расчётом не блокируется. Возвращает текст с начальным переводом строки или "".</summary>
    private async Task<string> ExpectedPayoutTextAsync(string saleId, decimal amount, bool reversesLoyalty)
    {
        _expectedPayouts.Remove(saleId);
        try
        {
            var ledger = _ledger is { Total: > 0m } known && string.Equals(known.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
                ? known
                : SaleReturnLedger.Get(saleId) is { Total: > 0m } stored ? stored : null;
            var method = _saleMethods.TryGetValue(saleId, out var m) ? m
                : string.Equals(_currentSaleId, saleId, StringComparison.OrdinalIgnoreCase) ? _currentSaleMethod : null;
            decimal? debtLeft = null;
            if (_serverDebtLeft.TryGetValue(saleId, out var debtTask))
            {
                if (!debtTask.IsCompleted)
                    await Task.WhenAny(debtTask, Task.Delay(300)).ConfigureAwait(true);
                if (debtTask.IsCompletedSuccessfully)
                    debtLeft = debtTask.Result;
            }

            var previewCashPaid = LocalDebtPaidHere(saleId, ledger, debtLeft, cashOnly: true);
            var (cash, nonCash, debtReduced, splitKnown, debtUnknown, _, _) = ComputeRefundSplit(ledger, method, Math.Max(0m, amount), null, debtLeft, null, previewCashPaid);
            var lead = "\n\n";
            if (debtUnknown)
            {
                _expectedPayouts[saleId] = null;
                return lead + Tr.T(
                    "К выдаче: сумму определит сервер — остаток долга клиента или оплаты чека кассе неизвестны. Проверьте долг клиента перед выдачей.",
                    "Берүүгө: сумманы сервер аныктайт — кардардын карызынын калдыгы же чектин төлөмдөрү кассага белгисиз. Берүүдөн мурун кардардын карызын текшериңиз.",
                    "To pay out: the server will determine the amount — the client's remaining debt or the receipt's payments are unknown to the till. Check the client's debt before paying out.",
                    "Verilecek: tutarı sunucu belirler — müşterinin kalan borcu veya fişin ödemeleri kasaya bilinmiyor. Vermeden önce müşterinin borcunu kontrol edin.",
                    "Berish: summani server aniqlaydi — mijoz qarzining qoldig'i yoki chek to'lovlari kassaga noma'lum. Berishdan oldin mijoz qarzini tekshiring.");
            }

            _expectedPayouts[saleId] = cash + nonCash;
            var parts = new List<string>();
            if (cash > 0.005m)
                parts.Add(Tr.T("наличными ", "накталай ", "in cash ", "nakit ", "naqd ") + MoneyText.Som(cash));
            if (nonCash > 0.005m)
                parts.Add(Tr.T("на карту/перевод ", "картага/которуу менен ", "to the card/transfer ", "karta/havale ile ", "kartaga/o'tkazma bilan ") + MoneyText.Som(nonCash));
            var give = !splitKnown ? MoneyText.Som(cash + nonCash) : parts.Count == 0 ? MoneyText.Som(0m) : string.Join(" · ", parts);
            var text = lead + Tr.T($"К выдаче (ожидаемо): {give}.", $"Берүүгө (күтүлүүдө): {give}.", $"To pay out (expected): {give}.", $"Verilecek (beklenen): {give}.", $"Berish (kutilmoqda): {give}.");
            if (!splitKnown)
                text += " " + Tr.T("Как заплатил покупатель, кассе неизвестно — возвращайте тем же способом.", "Сатып алуучу кандай төлөгөнү кассага белгисиз — ошол эле жол менен кайтарыңыз.",
                    "The till does not know how the customer paid — refund the same way.", "Müşterinin nasıl ödediği kasaya bilinmiyor — aynı yolla iade edin.", "Xaridor qanday to'lagani kassaga noma'lum — shu usulda qaytaring.");
            if (debtReduced > 0.005m)
            {
                var debt = MoneyText.Som(debtReduced);
                text += "\n" + Tr.T($"Долг клиента уменьшится на {debt} — эти деньги не выдаются.", $"Кардардын карызы {debt} азаят — бул акча берилбейт.", $"The client's debt will be reduced by {debt} — this money is not paid out.",
                    $"Müşterinin borcu {debt} azalacak — bu para verilmez.", $"Mijozning qarzi {debt} ga kamayadi — bu pul berilmaydi.");
            }
            if (reversesLoyalty && ClientLoyaltyStore.RemainingForSale(saleId) is var points && Math.Abs(points) >= 0.005)
            {
                var p = Math.Abs(points).ToString("0.##", CultureInfo.InvariantCulture);
                text += "\n" + (points > 0
                    ? Tr.T($"Бонусы клиента: −{p} (начисленные за этот чек будут сняты).", $"Кардардын бонустары: −{p} (бул чек үчүн эсептелгендер алынат).", $"Client bonuses: −{p} (those earned for this receipt will be removed).",
                        $"Müşteri bonusları: −{p} (bu fiş için kazanılanlar silinecek).", $"Mijoz bonuslari: −{p} (bu chek uchun hisoblanganlar olinadi).")
                    : Tr.T($"Бонусы клиента: +{p} (списанные на оплату будут возвращены).", $"Кардардын бонустары: +{p} (төлөмгө жумшалгандар кайтарылат).", $"Client bonuses: +{p} (those spent on this receipt will be returned).",
                        $"Müşteri bonusları: +{p} (bu fişte harcananlar iade edilecek).", $"Mijoz bonuslari: +{p} (to'lovga sarflanganlar qaytariladi)."));
            }
            return text;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат: «к выдаче» до отправки не посчитано: {ex.Message}", "WARNING");
            return "";
        }
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

    private static string RefundToGiveText(RefundOutcome outcome, decimal? expectedBefore = null)
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
        if (outcome.DebtReduced > 0.005m && outcome.DebtRemaining is { } debtLeftNow && !outcome.DebtUnknown)
        {
            // 2026-11-01 (ответ Умара, п. 3): сервер называет остаток долга после возврата (debt_adjustment.remaining_debt).
            var left = MoneyText.Som(debtLeftNow);
            text += " " + Tr.T($"Остаток долга: {left}.", $"Карыздын калдыгы: {left}.", $"Remaining debt: {left}.", $"Kalan borç: {left}.", $"Qarz qoldig'i: {left}.");
        }
        if (outcome.Balance > 0.005m)
        {
            var onBalance = MoneyText.Som(outcome.Balance);
            text += "\n" + Tr.T($"На баланс клиента: {onBalance}.", $"Кардардын балансына: {onBalance}.", $"To the client's balance: {onBalance}.", $"Müşteri bakiyesine: {onBalance}.", $"Mijoz balansiga: {onBalance}.");
        }
        if (outcome.ServerDiffersCalc is { } calcDiffers)
        {
            // Контрольное ревью: выдача по серверу допустима, но не равна сумме возврата / расчёту кассы (чек частично оплачен бонусами, часть возвращена на другой кассе или сайте).
            var srvGives = MoneyText.Som(outcome.Money);
            var calcGives = MoneyText.Som(calcDiffers);
            text += "\n" + Tr.T(
                $"Внимание: сервер выдаёт {srvGives}, расчёт кассы {calcGives} — проверьте. Выдайте {srvGives} (по серверу).",
                $"Көңүл буруңуз: сервер {srvGives} берет, касса эсеби {calcGives} — текшериңиз. {srvGives} бериңиз (сервер боюнча).",
                $"Warning: the server pays out {srvGives}, the till's calculation is {calcGives} — please check. Pay out {srvGives} (per the server).",
                $"Dikkat: sunucu {srvGives} veriyor, kasa hesabı {calcGives} — kontrol edin. {srvGives} verin (sunucuya göre).",
                $"Diqqat: server {srvGives} beradi, kassa hisobi {calcGives} — tekshiring. {srvGives} bering (server bo'yicha).");
        }
        if (outcome.ServerRejected is { } rejectedShown)
        {
            // 2026-11-01 (приёмка 1.17.72, В-1): цифры сервера не сходятся с суммой возврата — выдаётся расчёт кассы, кассиру — предупреждение.
            var calcMoney = MoneyText.Som(outcome.Money);
            var srvMoney = MoneyText.Som(rejectedShown);
            text += "\n" + Tr.T(
                $"Внимание: расчёт кассы {calcMoney}, сервер {srvMoney} — проверьте. Выдаётся расчёт кассы; сверьте возврат в «Возвратах» и долг клиента.",
                $"Көңүл буруңуз: касса эсеби {calcMoney}, сервер {srvMoney} — текшериңиз. Касса эсеби берилет; кайтарууну «Кайтаруулардан» жана кардардын карызын салыштырыңыз.",
                $"Warning: the till's calculation is {calcMoney}, the server's is {srvMoney} — please check. The till's amount is paid out; compare the return in “Returns” and the client's debt.",
                $"Dikkat: kasa hesabı {calcMoney}, sunucu {srvMoney} — kontrol edin. Kasa hesabı verilir; iadeyi «İadeler»de ve müşterinin borcunu karşılaştırın.",
                $"Diqqat: kassa hisobi {calcMoney}, server {srvMoney} — tekshiring. Kassa hisobi beriladi; qaytarishni «Qaytarishlar»da va mijoz qarzini solishtiring.");
        }
        if (outcome.CalcOverServer is { } calcShown)
        {
            var calc = MoneyText.Som(calcShown);
            var serverSum = MoneyText.Som(outcome.Money);
            text += "\n" + Tr.T(
                $"Внимание: расчёт кассы {calc}, сервер {serverSum} — выдаётся меньшая сумма {serverSum}. Проверьте долг клиента: возможно, он платил на другой кассе или на сайте.",
                $"Көңүл буруңуз: касса эсеби {calc}, сервер {serverSum} — аз сумма {serverSum} берилет. Кардардын карызын текшериңиз: ал башка кассада же сайтта төлөгөн болушу мүмкүн.",
                $"Warning: the till's calculation is {calc}, the server's is {serverSum} — the smaller amount {serverSum} is paid out. Check the client's debt: they may have paid at another till or on the website.",
                $"Dikkat: kasa hesabı {calc}, sunucu {serverSum} — küçük tutar {serverSum} verilir. Müşterinin borcunu kontrol edin: başka kasada veya sitede ödemiş olabilir.",
                $"Diqqat: kassa hisobi {calc}, server {serverSum} — kichik summa {serverSum} beriladi. Mijoz qarzini tekshiring: u boshqa kassa yoki saytda to'lagan bo'lishi mumkin.");
        }
        if (expectedBefore is { } expectedShown && Math.Abs(expectedShown - outcome.Money) > 0.009m)
        {
            // 2026-11-01 (Ж-08): кассиру до отправки показали «к выдаче» X, а итог другой — видно «ожидали X, итог Y» (причина — в строке сервера ниже или в журнале).
            var was = MoneyText.Som(expectedShown);
            var now = MoneyText.Som(outcome.Money);
            text += "\n" + Tr.T(
                $"Внимание: до отправки ожидалось к выдаче {was}, итог — {now}. Выдайте {now}.",
                $"Көңүл буруңуз: жөнөтүүдөн мурун {was} берүү күтүлгөн, жыйынтык — {now}. {now} бериңиз.",
                $"Warning: {was} was expected to be paid out before sending, the result is {now}. Pay out {now}.",
                $"Dikkat: göndermeden önce {was} ödeme bekleniyordu, sonuç {now}. {now} verin.",
                $"Diqqat: yuborishdan oldin {was} berish kutilgan edi, natija — {now}. {now} bering.");
        }
        if (outcome.ServerCashOut is { } serverGave)
        {
            // 2026-11-01 (аудит «10 из 10», Ж-01): сервер записал другую выдачу, чем положено по оплатам клиента — сумма и причина.
            var should = MoneyText.Som(outcome.Money);
            var recorded = MoneyText.Som(serverGave);
            if (serverGave > outcome.Money)
                text += "\n" + Tr.T(
                    $"Внимание: сервер записал выдачу {recorded} — больше, чем положено по оплатам клиента ({should}). Выдайте {should}; расхождение с сервером записано в журнал кассы.",
                    $"Көңүл буруңуз: сервер {recorded} берүүнү жазды — кардардын төлөмдөрү боюнча туура болгондон ({should}) көп. {should} бериңиз; сервер менен айырма касса журналына жазылды.",
                    $"Warning: the server recorded a payout of {recorded} — more than is due by the client's payments ({should}). Pay out {should}; the difference with the server is written to the till log.",
                    $"Dikkat: sunucu {recorded} ödeme kaydetti — müşterinin ödemelerine göre olması gerekenden ({should}) fazla. {should} verin; sunucuyla fark kasa günlüğüne yazıldı.",
                    $"Diqqat: server {recorded} berishni yozdi — mijoz to'lovlariga ko'ra to'g'ri summadan ({should}) ko'p. {should} bering; server bilan farq kassa jurnaliga yozildi.");
            else
            text += "\n" + Tr.T(
                $"Внимание: сервер записал выдачу {recorded}, а клиент внёс {should} (предоплата и оплаты долга по этому чеку, долг сейчас меньше). Выдайте {should}; расхождение с сервером записано в журнал кассы.",
                $"Көңүл буруңуз: сервер {recorded} берүүнү жазды, ал эми кардар {should} төлөгөн (бул чек боюнча алдын ала төлөм жана карыздын төлөмдөрү, азыр карыз аз). {should} бериңиз; сервер менен айырма касса журналына жазылды.",
                $"Warning: the server recorded a payout of {recorded}, but the client paid in {should} (prepayment and debt payments on this receipt; the debt is now smaller). Pay out {should}; the difference with the server is written to the till log.",
                $"Dikkat: sunucu {recorded} ödeme kaydetti, oysa müşteri {should} ödemiş (bu fişin ön ödemesi ve borç ödemeleri; borç artık daha az). {should} verin; sunucuyla fark kasa günlüğüne yazıldı.",
                $"Diqqat: server {recorded} berishni yozdi, mijoz esa {should} to'lagan (bu chek bo'yicha oldindan to'lov va qarz to'lovlari, qarz hozir kamroq). {should} bering; server bilan farq kassa jurnaliga yozildi.");
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
        NurMarketKassa.Services.OfflineOperationsService.NoteUnknownOnlineReturn(saleId); // 2026-10-29 (ревью, В-5): онлайн-возврат без ответа — офлайн-возврат по чеку закрыт, пока исход не выяснен
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            // 2026-11-02 (аудит №2, К2-09): сервер сам знает исход по ключу идемпотентности (GET idempotency/{key}/): найден — наш возврат прошёл; не найден — не прошёл (даже если по чеку
            // «к возврату стало меньше» из-за возврата на другой кассе или на сайте). Сервер ключей не знает (старый) — прежняя сверка по чеку.
            IdempotencyRecord? record = null;
            if (PendingReturn.PeekKey is { Length: > 0 } pendingKey && App.SalesApi is IPosQuickCheckoutApi lookupApi)
            {
                try { record = await lookupApi.PosIdempotencyRecordAsync(pendingKey, cts.Token).ConfigureAwait(true); }
                catch (Exception ex) when (ex is not OperationCanceledException) { record = null; }
            }
            if (record is { Found: false })
            {
                PosLogger.Log($"Возврат по продаже {saleId}: сервер не знает ключ возврата — наш возврат не прошёл, ключ оставлен для повтора.", "WARNING");
                return;
            }
            if (record is { IsFailure: true } failed)
            {
                // «Найден» не значит «проведён»: сервер хранит и отказы. Сохранённый ответ — ошибка: возврат НЕ проведён, деньги не выдаются, ключ забываем (отказ по существу).
                PosLogger.Log($"Возврат по продаже {saleId}: по ключу на сервере сохранён отказ (код {failed.StatusCode}, {failed.Code ?? "—"}) — возврат не проведён.", "WARNING");
                PendingReturn.OnSuccess();
                PosMessageBox.Show(this, Tr.T(
                        $"Сервер отказал в возврате (код {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — возврат НЕ проведён. Деньги покупателю не выдавайте; чек будет перечитан.",
                        $"Сервер кайтарууга уруксат берген жок (код {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — кайтаруу ЖҮРГҮЗҮЛГӨН ЖОК. Сатып алуучуга акча бербеңиз; чек кайра окулат.",
                        $"The server refused the return (code {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — the return was NOT processed. Do not pay the customer; the receipt will be reloaded.",
                        $"Sunucu iadeyi reddetti (kod {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — iade YAPILMADI. Müşteriye ödeme yapmayın; fiş yeniden okunacak.",
                        $"Server qaytarishni rad etdi (kod {failed.StatusCode}{(string.IsNullOrWhiteSpace(failed.Code) ? "" : ", " + failed.Code)}) — qaytarish O'TKAZILMADI. Xaridorga pul bermang; chek qayta o'qiladi."),
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var sale = await App.SalesApi.PosSaleGetAsync(saleId, cts.Token).ConfigureAwait(true);
            var linesReduced = LinesReturnedSince(sale, refundableBefore);
            if (record is { Found: true } && !(record.IsSuccess && linesReduced))
            {
                // Запись найдена, но это не успех (операция ещё идёт, неизвестное состояние/код) либо успех, который чек не подтверждает — деньги не выдаём, как при таймауте.
                PosLogger.Log($"Возврат по продаже {saleId}: запись по ключу неоднозначна (state={record.State}, code={record.StatusCode}, чек {(linesReduced ? "изменился" : "не изменился")}) — деньги не выдаются, проверить в «Истории чеков».", "WARNING");
                PosMessageBox.Show(this, Tr.T(
                        "Не удалось точно определить, проведён ли возврат (сервер ещё обрабатывает его или ответ неоднозначен). Деньги пока не выдавайте — проверьте результат в «Истории чеков» и повторите сверку.",
                        "Кайтаруу жүргүзүлгөнү так аныкталган жок (сервер аны дагы иштетип жатат же жооп так эмес). Азырынча акча бербеңиз — жыйынтыкты «Чектердин тарыхынан» текшериңиз.",
                        "It could not be determined exactly whether the return went through (the server is still processing it or the answer is ambiguous). Do not pay out yet — check the result in “Receipt history”.",
                        "İadenin gerçekleşip gerçekleşmediği kesin olarak belirlenemedi (sunucu hâlâ işliyor veya yanıt belirsiz). Henüz ödeme yapmayın — sonucu «Fiş geçmişi»nde kontrol edin.",
                        "Qaytarish o'tgan-o'tmagani aniq aniqlanmadi (server hali qayta ishlamoqda yoki javob noaniq). Hozircha pul bermang — natijani «Cheklar tarixi»da tekshiring."),
                    Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!linesReduced)
                return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Возврат по продаже {saleId}: ответ не дошёл, чек перечитать не удалось — ключ оставлен для повтора: {ex.Message}", "WARNING");
            return;
        }

        PendingReturn.OnSuccess();
        NurMarketKassa.Services.OfflineOperationsService.ClearUnknownOnlineReturn(saleId); // 2026-10-29: исход выяснен — возврат без связи по чеку снова доступен
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
            // 2026-11-03 (С-19, аудит «зависание при медленном сервере»): перечитывание чека после возврата — не дольше 8 с (раньше до 55 с без токена).
            var sale = await ServerAnswerWait.ForegroundAsync("pos/sales/get", t => App.SalesApi.PosSaleGetAsync(saleId, t)).ConfigureAwait(true);
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
        RefreshWholeReceiptButton(); // 2026-11-01 (Д-2): было IsEnabled = hasSale — стирало привязку CanReturnWhole

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
        ReturnTermText.Bind(TextBlock.ForegroundProperty, ReturnTermText.GetResourceObservable(outOfTerm ? "BrushDanger" : "BrushTextSoft"));
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
        if (selected.Count == 0 && Lines.Count > 0 && !Lines.Any(x => x.CanReturn))
        {
            ShowInputErr(AlreadyReturnedText()); // 2026-11-01 (Ж-05)
            return;
        }
        if (selected.Count == 0)
        {
            ShowInputErr(Tr.T("Отметьте галочками, что покупатель возвращает.", "Сатып алуучу эмнени кайтарарын белгилеңиз.", "Check what the customer is returning.",
                "Müşterinin iade ettiklerini işaretleyin.", "Xaridor nimani qaytarayotganini belgilang."));
            return;
        }
        // 2026-11-01 (аудит «10 из 10», К-05): обмен проходит те же защиты, что возврат. Чек «в долг»: что внесено, а что в долге, сервер при обмене
        // не различает (разницу выдают наличными целиком) — обмен не оформляется, вернуть и продать заново (там долг считается верно).
        var saleMethodNow = (_ledger?.Method is { Length: > 0 } knownMethod ? knownMethod : _currentSaleMethod ?? "").Trim().ToLowerInvariant();
        if (saleMethodNow == "debt" || _ledger is { Debt: > 0.005m })
        {
            PosMessageBox.Show(this, Tr.T(
                    "Чек продан в долг — обмен по нему не оформляется: касса не может разделить внесённые деньги и долг клиента. Оформите возврат (долг уменьшится сам) и продайте новый товар отдельным чеком.",
                    "Чек карызга сатылган — ал боюнча алмаштыруу тариздетилбейт: касса киргизилген акчаны жана кардардын карызын бөлө албайт. Кайтарууну тариздеңиз (карыз өзү азаят) жана жаңы товарды өзүнчө чек менен сатыңыз.",
                    "This receipt was sold on credit — an exchange cannot be processed: the till cannot separate the money paid from the client's debt. Process a return (the debt is reduced automatically) and sell the new item on a separate receipt.",
                    "Bu fiş veresiye satıldı — değişim yapılamaz: kasa ödenen parayı müşterinin borcundan ayıramaz. İade yapın (borç kendiliğinden azalır) ve yeni ürünü ayrı fişle satın.",
                    "Chek qarzga sotilgan — u bo'yicha almashtirish rasmiylashtirilmaydi: kassa kiritilgan pulni va mijoz qarzini ajrata olmaydi. Qaytarishni rasmiylashtiring (qarz o'zi kamayadi) va yangi tovarni alohida chek bilan soting."),
                exchangeTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var payDiff = done.CorrectedDifference ?? done.Difference; // К2-03: при «сервер засчитал лишнее» — разница по расчёту кассы
        var money = payDiff > 0.004m
            ? (done.PaymentMethod == "transfer"
                ? Tr.T($"Доплата переводом: {MoneyText.Amount(payDiff)} сом — проверьте, что перевод пришёл.", $"Которуу менен кошумча төлөм: {MoneyText.Amount(payDiff)} сом — которуу келгенин текшериңиз.",
                    $"Paid by transfer: {MoneyText.Amount(payDiff)} som — check that the transfer arrived.", $"Havale ile ek ödeme: {MoneyText.Amount(payDiff)} som — havalenin geldiğini kontrol edin.",
                    $"O'tkazma orqali qo'shimcha to'lov: {MoneyText.Amount(payDiff)} so'm — o'tkazma kelganini tekshiring.")
                : Tr.T($"Получить с покупателя: {MoneyText.Amount(payDiff)} сом наличными.", $"Сатып алуучудан алуу: {MoneyText.Amount(payDiff)} сом накталай.",
                    $"Take from the customer: {MoneyText.Amount(payDiff)} som in cash.", $"Müşteriden alınacak: {MoneyText.Amount(payDiff)} som nakit.",
                    $"Xaridordan olish: {MoneyText.Amount(payDiff)} so'm naqd."))
            : payDiff < -0.004m
                ? RefundToGiveText(-payDiff)
                : Tr.T("Без доплаты.", "Кошумча төлөмсүз.", "No difference to pay.", "Fark yok.", "Qo'shimcha to'lovsiz.");
        PosMessageBox.Show(this,
            Tr.T("Обмен оформлен.", "Алмаштыруу таризделди.", "Exchange completed.", "Değişim tamamlandı.", "Almashtirish rasmiylashtirildi.")
            + (done.Warning is { Length: > 0 } exchangeWarning ? "\n" + exchangeWarning : "") // 2026-11-01 (К-05)
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
        // 2026-11-01 (аудит «10 из 10», Ж-05): всё уже возвращено — останавливаем сразу, до подтверждения «0,00 сом» и ввода причины.
        if (Lines.Count > 0 && !Lines.Any(l => l.CanReturn))
        {
            ShowInputErr(AlreadyReturnedText());
            return;
        }
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
        var payoutPreview = await ExpectedPayoutTextAsync(saleId, estimatedWhole, reversesLoyalty: true).ConfigureAwait(true); // 2026-11-01 (Ж-08)
        if (await PosMessageBox.ShowModalAsync(this,
                Tr.T($"Оформить полный возврат всего чека одной операцией? Позиции по отдельности возвращать не потребуется.\nСумма возврата: {wholeShown}",
                    $"Бүт чекти бир операция менен толук кайтарасызбы? Позицияларды өз-өзүнчө кайтаруунун кереги жок болот.\nКайтаруу суммасы: {wholeShown}",
                    $"Return the whole receipt in one operation? You won't need to return items one by one.\nRefund amount: {wholeShown}",
                    $"Tüm fiş tek işlemde iade edilsin mi? Kalemleri tek tek iade etmeniz gerekmeyecek.\nİade tutarı: {wholeShown}",
                    $"Butun chek bitta amal bilan qaytarilsinmi? Pozitsiyalarni birma-bir qaytarish shart bo'lmaydi.\nQaytarish summasi: {wholeShown}") + payoutPreview,
                Tr.T("Полный возврат", "Толук кайтаруу", "Full return", "Tam iade", "To'liq qaytarish"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                // 2026-10-22 (модалки, пакет 1): глагол с суммой вместо «Продолжить».
                confirmText: ConfirmButtonText(saleId, estimatedWhole, whole: true, wholeShown)).ConfigureAwait(true) != MessageBoxResult.Yes)
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
        var exceedsRefused = false; // 2026-11-01 (К-01): сервер отказал «больше положенного» — чек перечитываем, деньги не выдаём
        IsBusy = true;
        try
        {
            // 2026-10-15, ревью: повтор полного возврата того же чека — тот же ключ (см. PendingReturn).
            var returnKey = PendingReturn.Get("whole|" + (reasonDialog.IsDefect ? "defect|" : "") + saleId); // 2026-11-02 (К2-24): «брак» — другая операция, другой ключ
            var debtSaleForDrawer = IsDebtSaleForDrawer(saleId);
            var cashBeforeWhole = await ReadDrawerBeforeRefundAsync(debtSaleForDrawer).ConfigureAwait(true);
            _refundSentAt = DateTimeOffset.Now;
            // 2026-11-02 (К2-19): по чеку есть неотправленный возврат без связи — «весь чек» без строк вернул бы и его (сервер вернёт всё, что осталось); шлём построчно остаток, который кассе известен.
            var wholeHasUnsent = Lines.Any(l => l.CanReturn && NurMarketKassa.Services.OfflineOperationsStore.UnsentRefundQuantity(saleId, l.ProductId, l.Title) > 0);
            var wholeResponse = wholeHasUnsent
                ? await PosRefundService.RefundLinesAsync(
                    App.SalesApi,
                    saleId,
                    Lines.Where(l => l.CanReturn).Select(l => new PosRefundLineRequest
                    {
                        LineId = l.LineId,
                        ProductId = l.ProductId,
                        Title = l.Title,
                        Quantity = l.Quantity > 0 ? l.Quantity : 1,
                        OriginalQuantity = l.OriginalQuantity > 0 ? l.OriginalQuantity : l.Quantity,
                    }).ToList(),
                    reasonDialog.ReasonText,
                    App.PosCashboxId,
                    reasonDialog.IsDefect,
                    idempotencyKey: returnKey).ConfigureAwait(true)
                : await PosRefundService.RefundWholeSaleAsync(
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
                Lines.Where(l => l.CanReturn).Select(l => new SaleLedgerLine { LineId = l.LineId, Name = l.Title, Quantity = l.Quantity, Total = l.RefundSum }).ToList(),
                ServerRefundFigures.TryRead(wholeResponse)) // 2026-11-01 (ответ Умара): выдача — из refund.cash
                .ConfigureAwait(true);
            var wholeTotal = wholeOutcome.Amount;
            CheckDrawerAfterRefund(cashBeforeWhole, wholeOutcome.Cash, "whole|" + returnKey, saleId, wholeOutcome.NonCash, wholeOutcome.ServerCashOut);
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
                    + "\n" + RefundToGiveText(wholeOutcome, TakeExpectedPayout(saleId))
                    + DrawerUnverifiedNote(debtSaleForDrawer, cashBeforeWhole, wholeOutcome.Cash)
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
            var ownRetry = PendingReturn.HasUnansweredAttempt; // 2026-11-01 (приёмка, К-01): см. возврат по позициям
            outcomeUnknown = PendingReturn.OnError(ex);
            exceedsRefused = RefundAmountMath.TryParseExceeds(ex, out _);
            // 2026-10-30: «возврат больше положенного» — понятный текст с суммой, которую сервер разрешает (дальше окно пересчитает остаток чека).
            PosMessageBox.Show(this, ExceedsOrServerText(ex, ownRetry), Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (HttpRequestException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            PosMessageBox.Show(this, ReturnNoConnectionText(), // 2026-11-02 (К2-24): без сырого ex.Message
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (TaskCanceledException ex)
        {
            outcomeUnknown = PendingReturn.OnError(ex);
            // 2026-11-01 (контрольный аудит, К2-12): честное состояние вместо «Превышено время ожидания» — возврат не потерян, касса сама проверит чек; кассиру — что делать.
            PosMessageBox.Show(this, Tr.T(
                    "Сервер медленно отвечает — возврат не потерян. Касса проверит чек сама; проверьте результат в «Истории чеков» перед повтором и перед выдачей денег.",
                    "Сервер жай жооп берүүдө — кайтаруу жоголгон жок. Касса чекти өзү текшерет; кайталоодон жана акча берүүдөн мурун «Чектердин тарыхынан» жыйынтыкты текшериңиз.",
                    "The server is slow to respond — the return is not lost. The till will check the receipt itself; check the result in “Receipt history” before retrying and before paying out.",
                    "Sunucu yavaş yanıt veriyor — iade kaybolmadı. Kasa fişi kendisi kontrol edecek; tekrar etmeden ve ödeme yapmadan önce sonucu «Fiş geçmişi»nde kontrol edin.",
                    "Server sekin javob bermoqda — qaytarish yo'qolgani yo'q. Kassa chekni o'zi tekshiradi; qayta urinishdan va pul berishdan oldin natijani «Cheklar tarixi»da tekshiring."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
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
        else if (exceedsRefused)
            await RefreshCurrentSaleAsync(saleId).ConfigureAwait(true); // 2026-11-01 (К-01): «можно вернуть» пересчитывается по чеку
    }

    /// <summary>2026-11-01 (приёмка, К-01): текст ошибки возврата. Свой повтор с тем же ключом (прошлая отправка осталась без ответа) получил 409 «больше положенного» —
    /// первый запрос мог на самом деле пройти: отдельный текст «проверьте в „Возвратах“ перед выдачей денег». В смену, на склад и в баллы ничего не пишется.</summary>
    private static string ExceedsOrServerText(ApiException ex, bool ownRetry)
    {
        if (ownRetry && RefundAmountMath.TryParseExceeds(ex, out _))
        {
            PosLogger.Log("Возврат: повтор своего запроса получил 409 «больше положенного» — первый запрос мог пройти; деньги не выдаются до проверки в «Возвратах».", "WARNING");
            return Tr.T(
                "Возможно, возврат уже проведён (прошлая отправка осталась без ответа). Проверьте в «Возвратах» перед выдачей денег — не выдавайте, пока не убедитесь. В смену этот возврат не записан.",
                "Кайтаруу мурунтан эле жүргүзүлгөн болушу мүмкүн (мурунку жөнөтүүгө жооп келген жок). Акча берүүдөн мурун «Кайтаруулардан» текшериңиз — ишенмейинче бербеңиз. Бул кайтаруу сменага жазылган жок.",
                "The return may already have been processed (the previous attempt got no reply). Check “Returns” before paying out — do not pay until you are sure. This return is not recorded in the shift.",
                "İade zaten yapılmış olabilir (önceki gönderim yanıtsız kaldı). Ödemeden önce «İadeler»i kontrol edin — emin olmadan vermeyin. Bu iade vardiyaya yazılmadı.",
                "Qaytarish allaqachon o'tkazilgan bo'lishi mumkin (oldingi yuborishga javob kelmadi). Pul berishdan oldin «Qaytarishlar»ni tekshiring — ishonch hosil qilmay bermang. Bu qaytarish smenaga yozilmadi.");
        }
        return RefundExceedsText(ex) ?? ex.Message;
    }

    /// <summary>2026-10-30: ответ сервера «refund_exceeds_paid» (возврат больше положенного) — текст для кассира с цифрами сервера
    /// (оплачено / уже возвращено / можно вернуть). Сумма в запросе не передаётся (сервер считает по строкам), поэтому повторять с другой суммой нельзя:
    /// окно после ответа заново читает чек и пересчитывает «можно вернуть» (RefreshCurrentSaleAsync). null — другая ошибка.</summary>
    internal static string? RefundExceedsText(ApiException ex)
    {
        if (!RefundAmountMath.TryParseExceeds(ex, out var info))
            return null;
        // 2026-11-01 (аудит «10 из 10», К-01): это отказ, а не прошедший возврат — деньги не выдаются (ниже к тексту добавлено «Деньги не выдавайте»).
        var noPay = "\n" + Tr.T("Деньги покупателю по этой операции не выдавайте.", "Бул операция боюнча сатып алуучуга акча бербеңиз.", "Do not pay the customer anything for this operation.", "Bu işlem için müşteriye para vermeyin.", "Bu amal bo'yicha xaridorga pul bermang.");
        var left = info.Refundable is { } r && r > 0m ? MoneyText.Som(r) : null;
        var paid = info.Paid is { } p ? MoneyText.Som(p) : null;
        var back = info.Refunded is { } b ? MoneyText.Som(b) : null;
        var numbers = paid is null || back is null ? "" : "\n" + Tr.T($"Оплачено {paid}, уже возвращено {back}.", $"Төлөндү {paid}, мурун кайтарылды {back}.", $"Paid {paid}, already returned {back}.", $"Ödenen {paid}, daha önce iade edilen {back}.", $"To'langan {paid}, avval qaytarilgan {back}.");
        return left is null
            ? Tr.T("Сервер не принял возврат: по этому чеку больше нечего возвращать (возможно, часть уже возвращена на сайте или другой кассе).", "Сервер кайтарууну кабыл алган жок: бул чек боюнча кайтарууга эч нерсе калган жок (бир бөлүгү сайтта же башка кассада кайтарылган болушу мүмкүн).", "The server did not accept the return: nothing is left to return on this receipt (part may have been returned on the website or another till).", "Sunucu iadeyi kabul etmedi: bu fişte iade edilecek bir şey kalmadı (bir kısmı sitede veya başka kasada iade edilmiş olabilir).", "Server qaytarishni qabul qilmadi: bu chek bo'yicha qaytariladigan narsa qolmadi (bir qismi saytda yoki boshqa kassada qaytarilgan bo'lishi mumkin).") + numbers + noPay
            : Tr.T($"Сервер не принял возврат: по чеку можно вернуть не больше {left}. Выберите меньше строк или количество.", $"Сервер кайтарууну кабыл алган жок: чек боюнча {left} ашык кайтарууга болбойт. Азыраак позиция же сан тандаңыз.", $"The server did not accept the return: no more than {left} can be returned on this receipt. Select fewer items or a smaller quantity.", $"Sunucu iadeyi kabul etmedi: bu fişte en fazla {left} iade edilebilir. Daha az kalem veya miktar seçin.", $"Server qaytarishni qabul qilmadi: bu chek bo'yicha {left} dan ko'p qaytarib bo'lmaydi. Kamroq pozitsiya yoki miqdor tanlang.") + numbers + noPay;
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
