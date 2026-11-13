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

    /// <summary>2026-11-03 (аудит №3, Д3-05): возврат по этому чеку остался без ответа, а сервер по ключу его «не нашёл» даже после повторных проверок — исход неясен.
    /// Пока ключ жив (до 10 минут или до выяснения), другой возврат по этому чеку (иное количество = иной ключ) не отправляем: сервер мог провести оба.</summary>
    private static (string SaleId, string Key)? _unresolvedReturn;

    /// <summary>2026-11-06 (Б-2): снимок выбранного чека на момент загрузки (до отправки возврата) — после ошибки сервера чек сравнивается с ним (ReturnSnapshot).</summary>
    private ReturnSnapshot? _saleSnapshot;

    /// <summary>2026-11-06 (Б-2): сервер ответил ошибкой, а перечитать чек не удалось — мог провести. Возврат по этому чеку не даём, пока окно не прочитало чек заново (FillLinesFromSale).</summary>
    private string? _returnUnknownSaleId;

    /// <summary>2026-11-06 (Б-2): после возврата долгового чека сверить долг клиента с ожидаемым (пересчёт сервера) — по свежему чеку в RefreshCurrentSaleAsync.</summary>
    private (string SaleId, ReturnSnapshot Before, bool Partial, decimal DebtReduced)? _pendingDebtCheck;

    /// <summary>true — по чеку есть неясный возврат, а этот (подпись <paramref name="signature"/>) — не его повтор; кассиру показан текст.</summary>
    private bool BlockedByUnresolvedReturn(string saleId, string signature)
    {
        if (string.Equals(_returnUnknownSaleId, saleId, StringComparison.OrdinalIgnoreCase))
        {
            // 2026-11-06 (Б-2): сервер ответил ошибкой, чек перечитать не удалось — возврат мог пройти; повтор запрещён до обновления окна.
            PosMessageBox.Show(this, Tr.T(
                    "Прошлый возврат по этому чеку не выяснен: сервер ответил ошибкой, но мог его провести, а чек перечитать не удалось. Не оформляйте возврат повторно — закройте и откройте окно возврата, когда появится связь, и проверьте чек.",
                    "Бул чек боюнча мурунку кайтаруу такталган жок: сервер ката менен жооп берди, бирок аны жүргүзгөн болушу мүмкүн, чекти кайра окуу мүмкүн болгон жок. Кайра тариздебеңиз — байланыш пайда болгондо кайтаруу терезесин жаап-ачып, чекти текшериңиз.",
                    "The previous return for this receipt is unresolved: the server answered with an error but may have processed it, and the receipt could not be re-read. Do not repeat the return — close and reopen the return window when the connection is back, and check the receipt.",
                    "Bu fişteki önceki iade belirsiz: sunucu hata ile yanıt verdi ama işlemiş olabilir, fiş yeniden okunamadı. İadeyi tekrarlamayın — bağlantı gelince iade penceresini kapatıp açın ve fişi kontrol edin.",
                    "Bu chek bo'yicha oldingi qaytarish aniqlanmadi: server xato bilan javob berdi, lekin o'tkazgan bo'lishi mumkin, chekni qayta o'qib bo'lmadi. Qayta rasmiylashtirmang — aloqa tiklangach qaytarish oynasini yopib qayta oching va chekni tekshiring."),
                Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
        if (_unresolvedReturn is not { } u || !string.Equals(u.SaleId, saleId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(PendingReturn.PeekKey, u.Key, StringComparison.Ordinal) || PendingReturn.Matches(signature))
            return false;
        PosMessageBox.Show(this, Tr.T(
                "Прошлый возврат по этому чеку не подтверждён сервером: неясно, прошёл он или нет. Не оформляйте другой возврат — проверьте на сайте NurCRM («Возвраты» этого чека), затем повторите тот же возврат или подождите 10 минут.",
                "Бул чек боюнча мурунку кайтарууну сервер ырастаган жок: өткөнү же жокпу — белгисиз. Башка кайтарууну тариздебеңиз — NurCRM сайтынан («Кайтаруулар») текшерип, ошол эле кайтарууну кайталаңыз же 10 мүнөт күтүңүз.",
                "The previous return for this receipt was not confirmed by the server: it is unclear whether it went through. Do not process a different return — check on the NurCRM website (the receipt's returns), then repeat the same return or wait 10 minutes.",
                "Bu fişteki önceki iade sunucu tarafından doğrulanmadı: geçip geçmediği belirsiz. Başka bir iade yapmayın — NurCRM sitesinde (fişin iadeleri) kontrol edin, sonra aynı iadeyi tekrarlayın veya 10 dakika bekleyin.",
                "Bu chek bo'yicha oldingi qaytarishni server tasdiqlamadi: o'tgan-o'tmagani noaniq. Boshqa qaytarishni rasmiylashtirmang — NurCRM saytida (chekning qaytarishlari) tekshiring, so'ng o'sha qaytarishni takrorlang yoki 10 daqiqa kuting."),
            Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"), MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

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
        BuildFilterUi(); // 2026-11-10: поле «Товар», «Фильтры (N)», панель фильтров и счётчик над таблицей чеков
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
        // 2026-11-12 (владелец: «Возврат чеков добавь и программу владельца»): обмен — это новая продажа с доплатой в ящик кассы; в программе владельца
        // кассовой смены и ящика нет — обмен оформляется на кассе, здесь только возврат.
        ExchangeSelectedButton.IsVisible = MarketSpheres.IsClothing && !AppMode.IsOwner;

        UpdateReceiptChrome();
        UpdateWindowStateUI();
    }

    private async void OnFirstOpened(object? sender, EventArgs e)
    {
        Opened -= OnFirstOpened;
        UpdateWindowStateUI();
        ApplyInitialFilters(); // 2026-11-10: статус «Долговой» из карточки клиента, товар из «Продаж», режим клиента
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

        /// <summary>2026-11-10 (поиск по товару): найденный в чеке товар — подписью под датой.</summary>
        public string? HitText { get; init; }
        public bool HasHit => !string.IsNullOrWhiteSpace(HitText);
        public string HitDisplay => HasHit ? "• " + HitText : "";

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
