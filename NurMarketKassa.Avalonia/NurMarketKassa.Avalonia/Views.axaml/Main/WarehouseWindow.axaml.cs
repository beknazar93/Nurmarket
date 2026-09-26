using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views;

public partial class WarehouseWindow : Window
{
    private readonly IBarcodeInputService _barcodeInputService;
    private readonly WarehouseViewModel _viewModel;

    public string? InitialBarcode { get; set; }

    /// <summary>Установить ДО Show() — прячет вкладки Товары/Ревизия/Списание и сразу открывает
    /// Аналитику. Используется при переходе сюда из бургер-меню Финансов, где нужен только
    /// обзор склада, а не операции над товарами.</summary>
    public bool AnalyticsOnly { get; set; }

    /// <summary>Parameterless ctor required by Avalonia XAML runtime loader / designer.</summary>
    public WarehouseWindow() : this(
        ResolveService<WarehouseViewModel>(),
        ResolveService<IBarcodeInputService>())
    {
    }

    private static T ResolveService<T>() where T : notnull
    {
        var sp = App.AppHost?.Services
            ?? throw new InvalidOperationException($"{typeof(T).Name} requires running AppHost DI.");
        return sp.GetRequiredService<T>();
    }

    public WarehouseWindow(WarehouseViewModel viewModel, IBarcodeInputService barcodeInputService)
    {
        _viewModel = viewModel;
        _barcodeInputService = barcodeInputService;
        InitializeComponent();
        // Сканер ловим на ТУННЕЛЬНОЙ фазе (раньше любых элементов окна) — см. Window_KeyDown.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        this.FitToScreen();
        DataContext = _viewModel;
        // Esc закрывает склад, но не молча выбрасывает набранные строки приёмки и ревизии:
        // они живут только в этом окне и после закрытия пропадут.
        EscapeKey.Attach(this, closeWindow: CloseByEscape);
        _viewModel.RevisionLineAdded += OnRevisionLineAdded;
        InitializeWriteOffReasonPicker();
        WarehouseTabs.SelectionChanged += WarehouseTabs_SelectionChanged;
        RefreshPaidFeatureVisibility();
    }

    /// <summary>Аналитика склада и массовая печать ценников — платные доп. услуги (2026-09-04,
    /// см. LicenseKeys/UserPreferences.WarehouseAnalyticsUnlocked/PriceTagEditorUnlocked). По
    /// просьбе пользователя они не просто заблокированы поверх — их вообще не видно в складе,
    /// пока не куплены и не активированы (сейчас — только через карточки в Маркетплейс → Доп.
    /// функции; см. MarketplaceView.BuildWarehouseAnalyticsCard/BuildPriceTagEditorCard). Вызов
    /// здесь нужен на случай, если пользователь купил доп. услугу, не закрывая окно склада —
    /// маловероятно (Маркетплейс в другом окне), но раз уж эта функция уже есть.</summary>
    private void RefreshPaidFeatureVisibility()
    {
        AnalyticsTabItem.IsVisible = UserPreferences.Instance.WarehouseAnalyticsUnlocked;
        BulkPriceTagButton.IsVisible = UserPreferences.Instance.BulkPriceTagUnlocked;
    }

    /// <summary>Аналитика считается только когда кассир реально открыл эту вкладку — сразу
    /// с актуальными данными (в т.ч. после добавления/списания товара), без лишней работы
    /// на каждое открытие окна склада.</summary>
    private void WarehouseTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Сравниваем с самими вкладками, а не с их номерами: номера уже один раз поехали,
        // когда между «Товарами» и «Ревизией» встала «Приёмка», и аналитика начала считаться
        // при открытии перемещений.
        if (ReferenceEquals(WarehouseTabs.SelectedItem, MovementsTabItem))
            MovementsSection_Changed(this, new RoutedEventArgs());
        else if (ReferenceEquals(WarehouseTabs.SelectedItem, AnalyticsTabItem))
            RefreshAnalytics();
    }

    /// <summary>Живой поиск в приёмке. С двух букв: с одной в каталоге совпадает почти всё.
    /// Сканер пишет в это же поле, но заканчивает переводом строки — подсказки мелькнут и
    /// уступят место обычному добавлению по Enter.</summary>
    private void ReceivingScan_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = ReceivingScanBox.Text?.Trim() ?? "";
        if (query.Length < 2)
        {
            ReceivingSuggestionsBox.IsVisible = false;
            ReceivingSuggestionsPanel.ItemsSource = null;
            return;
        }

        var matches = CatalogCacheService.Products
            .Where(p => p.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || (p.Barcode ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (p.Article ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToList();

        ReceivingSuggestionsPanel.ItemsSource = matches;
        ReceivingSuggestionsBox.IsVisible = matches.Count > 0;
    }

    private void ReceivingSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        _viewModel.AddReceivingProduct(product, quantity);

        ReceivingScanBox.Text = "";
        ReceivingQuantityBox.Text = "1";
        ReceivingSuggestionsBox.IsVisible = false;
        ReceivingSuggestionsPanel.ItemsSource = null;
        RefreshReceivingSummary();
        // Сразу к следующему товару: фокус обратно в поле скана (после клика по подсказке он
        // оставался на кнопке, и следующий скан уходил мимо).
        ReceivingScanBox.Focus();
    }

    private async void ReceivingScan_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        var code = ReceivingScanBox.Text?.Trim() ?? "";
        if (code.Length == 0)
            return;

        // Если поиск что-то нашёл, Enter берёт первое совпадение: набранное руками название
        // иначе ушло бы в список как неизвестный штрихкод и не привязалось бы к товару.
        // Но не для штрихкода (одни цифры): подсказки ищут вхождение и в названиях, и в артикулах,
        // и первым по алфавиту мог встать чужой товар, в чьём коде эти цифры просто встречаются.
        var looksLikeBarcode = code.Length >= 6 && code.All(char.IsDigit);
        if (!looksLikeBarcode
            && ReceivingSuggestionsBox.IsVisible
            && ReceivingSuggestionsPanel.ItemsSource is IEnumerable<CatalogProductTileVm> found
            && found.FirstOrDefault() is { } first)
        {
            ReceivingSuggestion_Click(new Button { Tag = first }, new RoutedEventArgs());
            return;
        }

        ReceivingScanBox.Text = "";
        ReceivingSuggestionsBox.IsVisible = false;
        ReceivingSuggestionsPanel.ItemsSource = null;
        await ScanIntoReceivingAsync(code);
    }

    /// <summary>Enter в поле количества — сразу к скану: количество задали, дальше сканер.</summary>
    private void ReceivingQuantity_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        ReceivingScanBox.Focus();
    }

    /// <summary>Скан в приёмку — и из поля ввода, и со сканера, когда фокус не в поле.
    /// Количество берётся из поля рядом: коробку можно принять одним сканом.</summary>
    private async Task ScanIntoReceivingAsync(string code)
    {
        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        try
        {
            await _viewModel.HandleReceivingScanAsync(code, quantity);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: скан {code} не обработан: {ex.Message}", "WARNING");
        }

        ReceivingQuantityBox.Text = "1";
        RefreshReceivingSummary();
    }

    private void ReceivingPayment_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.ReceivingPaidNow = ReceivingDebtRadio.IsChecked != true;
    }

    /// <summary>Список поставщиков раскрывается прямо в окне — всплывающий список ComboBox в окне
    /// склада не открывался (см. комментарий в разметке).</summary>
    private void ReceivingSupplierButton_Click(object? sender, RoutedEventArgs e)
    {
        ReceivingSupplierPanel.IsVisible = !ReceivingSupplierPanel.IsVisible;
        if (ReceivingSupplierPanel.IsVisible)
            ReceivingSupplierList.SelectedItem = _viewModel.ReceivingSupplier;
    }

    private void ReceivingSupplierList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReceivingSupplierPanel.IsVisible
            || ReceivingSupplierList.SelectedItem is not PurchaseReceivingService.Supplier supplier)
            return;

        _viewModel.ReceivingSupplier = supplier;
        ReceivingSupplierPanel.IsVisible = false;
    }

    /// <summary>2026-09-26, «разрешить редактирование»: название и единицу теперь правят и у товара
    /// со склада — при проведении они уходят в его карточку (PurchaseReceivingService.AddNameAndUnit).
    /// Штрихкод вписывают только у нового товара, заведённого кнопкой «+ Новый товар»: у
    /// отсканированного он и есть то, по чему товар нашёлся.</summary>
    private void ReceivingGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.DataContext is not NurMarketKassa.Models.ReceivingLineVm line)
            return;

        // Колонка 2 — «Штрихкод», см. разметку ReceivingGrid.
        if (ReferenceEquals(e.Column, ReceivingGrid.Columns[2]) && line.Source != NurMarketKassa.Models.ReceivingSource.Unknown)
            e.Cancel = true;
    }

    /// <summary>Правка с одного нажатия. DataGrid входит в правку только по второму нажатию на уже
    /// выбранную ячейку — на сенсорном экране казалось, что таблица не редактируется вовсе.</summary>
    private void ReceivingGrid_CellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        if (e.Column is null || e.Column.IsReadOnly || e.Row?.DataContext is not NurMarketKassa.Models.ReceivingLineVm line)
            return;

        var column = e.Column;
        Dispatcher.UIThread.Post(() =>
        {
            ReceivingGrid.SelectedItem = line;
            ReceivingGrid.CurrentColumn = column;
            ReceivingGrid.BeginEdit();
        }, DispatcherPriority.Background);
    }

    /// <summary>«+ Новый товар»: пустая строка нового товара, сразу в правке названия. Кнопку можно
    /// нажимать подряд — каждая строка станет отдельным товаром при проведении.</summary>
    private void ReceivingAddNew_Click(object? sender, RoutedEventArgs e)
    {
        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        var line = new NurMarketKassa.Models.ReceivingLineVm
        {
            Source = NurMarketKassa.Models.ReceivingSource.Unknown,
            Unit = "шт",
            Quantity = quantity,
        };
        _viewModel.ReceivingLines.Add(line);
        ReceivingQuantityBox.Text = "1";
        RefreshReceivingSummary();

        Dispatcher.UIThread.Post(() =>
        {
            ReceivingGrid.SelectedItem = line;
            ReceivingGrid.ScrollIntoView(line, ReceivingGrid.Columns[1]);
            ReceivingGrid.CurrentColumn = ReceivingGrid.Columns[1];
            ReceivingGrid.BeginEdit();
        }, DispatcherPriority.Background);
    }

    private void ReceivingGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e) =>
        RefreshReceivingSummary();

    private async void ReceivingPickFromList_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new BundleItemPickerDialog(CatalogCacheService.Products, []);
        var confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || dialog.Result.Count == 0)
            return;

        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        foreach (var product in dialog.Result)
            _viewModel.AddReceivingProduct(product, quantity);

        ReceivingQuantityBox.Text = "1";
        RefreshReceivingSummary();
        ReceivingScanBox.Focus();
    }

    private void ReceivingRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (ReceivingGrid.SelectedItem is NurMarketKassa.Models.ReceivingLineVm line)
        {
            _viewModel.ReceivingLines.Remove(line);
            RefreshReceivingSummary();
        }
    }

    /// <summary>Итог под накладной: позиции, единицы, сумма закупки и сколько товаров будет
    /// создано. Нужен до проведения — после него правки уже не отменить.</summary>
    private void RefreshReceivingSummary()
    {
        var lines = _viewModel.ReceivingLines;
        var created = lines.Count(l => l.IsNew);

        var text = Tr.T("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiya") + $": {lines.Count}"
            + "   ·   " + Tr.T("единиц", "бирдик", "units", "adet", "birlik")
            + $": {lines.Sum(l => l.Quantity):0.###}"
            + "   ·   " + Tr.T("на сумму", "суммасы", "total", "tutar", "summa")
            + $": {lines.Sum(l => l.LineTotal):0.##} " + Tr.T("сом", "сом", "som", "som", "so'm");

        if (created > 0)
            text += "   ·   " + Tr.T("новых товаров", "жаңы товар", "new products", "yeni ürün", "yangi mahsulot")
                + $": {created}";

        ReceivingSummaryText.Text = text;
    }

    /// <summary>Строка журнала документов перемещения. Отдельный тип, потому что DataGrid
    /// привязывается к именам свойств, а показать нужно не то, что лежит в базе, а собранный
    /// текст: маршрут одной строкой, статус словами, количество позиций с единицами.</summary>
    private sealed class TransferRow
    {
        public string Id { get; init; } = "";
        public string Number { get; init; } = "";
        public string WhenText { get; init; } = "";
        public string RouteText { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string ItemsText { get; init; } = "";
        public string ResponsibleText { get; init; } = "";
        public bool IsInTransit { get; init; }
        public bool IsDelivered { get; init; }
        public bool IsCancelled { get; init; }
    }

    private static string TransferStatusText(string status) => status switch
    {
        StockTransferService.StatusInTransit => Tr.T("В пути", "Жолдо", "In transit", "Yolda", "Yo'lda"),
        StockTransferService.StatusDelivered => Tr.T("Доставлено", "Жеткирилди", "Delivered", "Teslim edildi", "Yetkazildi"),
        StockTransferService.StatusCancelled => Tr.T("Отменено", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"),
        _ => Tr.T("Создано", "Түзүлдү", "Created", "Oluşturuldu", "Yaratildi"),
    };

    /// <summary>Журнал документов перемещения: поиск идёт и по самому документу, и по товарам
    /// внутри него — кладовщик ищет «где та коробка», а не номер бумаги.</summary>
    private void RefreshTransfers()
    {
        if (TransfersGrid is null)
            return;

        var search = MovementsSearchBox?.Text?.Trim() ?? "";
        var status = _transferStatusFilter;

        var rows = StockTransferService.Instance.LoadTransfers(search, status)
            .Select(t => new TransferRow
            {
                Id = t.Id,
                Number = t.Number,
                WhenText = t.CreatedAt.ToString("dd.MM.yyyy HH:mm"),
                RouteText = BuildRouteText(t),
                StatusText = TransferStatusText(t.Status),
                ItemsText = t.ItemCount == 0
                    ? "—"
                    : $"{t.ItemCount} · {t.TotalQuantity:0.###}",
                ResponsibleText = string.IsNullOrWhiteSpace(t.Responsible) ? "—" : t.Responsible!,
                IsInTransit = t.Status == StockTransferService.StatusInTransit,
                IsDelivered = t.Status == StockTransferService.StatusDelivered,
                IsCancelled = t.Status == StockTransferService.StatusCancelled,
            })
            .ToList();

        TransfersGrid.ItemsSource = rows;

        MovementsSummaryText.Text = (rows.Count == 0
            ? Tr.T("Перемещений пока нет. Создайте первое — кнопка справа.",
                   "Жылышуулар азырынча жок. Биринчисин түзүңүз — оң жактагы баскыч.",
                   "No transfers yet. Create the first one with the button on the right.",
                   "Henüz transfer yok. İlkini sağdaki düğmeyle oluşturun.",
                   "Hozircha ko'chirishlar yo'q. Birinchisini o'ngdagi tugma bilan yarating.")
            : Tr.T("Документов", "Документтер", "Documents", "Belgeler", "Hujjatlar") + $": {rows.Count}")
            // 2026-09-25, по замечанию владельца: перемещение выглядело так, будто двигает остатки.
            // У товара магазина в NurCRM один остаток на весь магазин (поле quantity, без
            // разбивки по складам), перемещать его на сервере не между чем — документ только
            // фиксирует перенос между своими складами/зонами/ячейками и хранится на этой кассе.
            + "   ·   " + Tr.T(
                "Документ о переносе между вашими местами хранения: общий остаток товара в NurCRM он не меняет, журнал хранится на этой кассе.",
                "Сактоо жайларыңыздын ортосундагы жылыштыруу документи: NurCRMдеги товардын жалпы калдыгын өзгөртпөйт, журнал ушул кассада сакталат.",
                "A record of moving goods between your storage places: it does not change the product's total stock in NurCRM, and the log is kept on this register.",
                "Depolama yerleriniz arasındaki taşıma belgesi: NurCRM'deki toplam stoğu değiştirmez, kayıt bu kasada tutulur.",
                "Saqlash joylaringiz o'rtasidagi ko'chirish hujjati: NurCRMdagi umumiy qoldiqni o'zgartirmaydi, jurnal shu kassada saqlanadi.");
    }

    private static string BuildRouteText(StockTransferService.Transfer t)
    {
        var from = string.IsNullOrWhiteSpace(t.FromPlaceName)
            ? Tr.T("склад не указан", "кампа көрсөтүлгөн эмес", "source not set", "kaynak yok", "manba ko'rsatilmagan")
            : t.FromPlaceName;
        var to = string.IsNullOrWhiteSpace(t.ToPlaceName)
            ? Tr.T("получатель не указан", "алуучу көрсөтүлгөн эмес", "destination not set", "hedef yok", "qabul qiluvchi ko'rsatilmagan")
            : t.ToPlaceName;

        var route = from + "  →  " + to;
        if (!string.IsNullOrWhiteSpace(t.Carrier))
            route += "   ·   " + t.Carrier;
        if (!string.IsNullOrWhiteSpace(t.TrackingNumber))
            route += " " + t.TrackingNumber;
        return route;
    }

    /// <summary>Переключение «Документы / Движение товара». Фильтр по периоду относится только к
    /// движению, фильтр по статусу и кнопка создания — только к документам, поэтому лишние
    /// элементы прячутся, а не стоят неактивными.</summary>
    private void MovementsSection_Changed(object? sender, RoutedEventArgs e)
    {
        if (TransfersGrid is null || MovementsGrid is null)
            return;

        var docs = TransfersDocsRadio.IsChecked == true;
        TransfersGrid.IsVisible = docs;
        MovementsGrid.IsVisible = !docs;

        TransferStatusPills.IsVisible = docs;
        CreateTransferButton.IsVisible = docs;
        TransfersExcelButton.IsVisible = docs;
        TransfersWordButton.IsVisible = docs;
        MovementsWeekRadio.IsVisible = !docs;
        MovementsMonthRadio.IsVisible = !docs;
        MovementsQuarterRadio.IsVisible = !docs;

        if (docs)
        {
            InitializeTransferStatusFilter();
            RefreshTransfers();
        }
        else
            RefreshMovements();
    }

    /// <summary>Выбранный статус в фильтре перемещений; null — все.</summary>
    private string? _transferStatusFilter;

    /// <summary>Заполняет фильтр статусов кнопками-переключателями (2026-09-25: выпадающий список
    /// в окне склада без системной рамки не открывался). Собирается в коде, а не в разметке:
    /// подписи переводятся на язык интерфейса, а он меняется на ходу.</summary>
    private void InitializeTransferStatusFilter()
    {
        // Заполняется один раз, при первом открытии вкладки. Раньше вызов стоял в загрузке
        // окна, после ожидания каталога: стоило загрузке затянуться или упасть — и список
        // статусов оставался пустым, хотя сама вкладка работала.
        if (TransferStatusPills.Children.Count > 0)
            return;

        void AddPill(string text, string? status)
        {
            var pill = new RadioButton
            {
                Content = text,
                GroupName = "TransferStatus",
                IsChecked = status == _transferStatusFilter,
            };
            pill.Classes.Add("SaleUnitPill");
            pill.Click += (_, _) =>
            {
                _transferStatusFilter = status;
                RefreshTransfers();
            };
            TransferStatusPills.Children.Add(pill);
        }

        AddPill(Tr.T("Все статусы", "Бардык абалдар", "All statuses", "Tüm durumlar", "Barcha holatlar"), null);
        foreach (var status in new[]
                 {
                     StockTransferService.StatusCreated,
                     StockTransferService.StatusInTransit,
                     StockTransferService.StatusDelivered,
                     StockTransferService.StatusCancelled,
                 })
        {
            AddPill(TransferStatusText(status), status);
        }
    }

    private void CreateTransfer_Click(object? sender, RoutedEventArgs e)
    {
        var id = StockTransferService.Instance.CreateTransfer(
            fromPlaceId: null, toPlaceId: null,
            responsible: App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().CurrentUserDisplayName,
            carrier: null, trackingNumber: null, note: null,
            employee: App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().CurrentUserDisplayName);

        RefreshTransfers();
        OpenTransferCard(id);
    }

    private async void ExportTransfersExcel_Click(object? sender, RoutedEventArgs e) => await ExportTransfersAsync(toWord: false);

    private async void ExportTransfersWord_Click(object? sender, RoutedEventArgs e) => await ExportTransfersAsync(toWord: true);

    /// <summary>Выгрузка журнала перемещений. Берётся то, что сейчас отобрано фильтрами: если
    /// кладовщик ищет отгрузки одного склада, в файл должны попасть они, а не всё подряд.</summary>
    private async Task ExportTransfersAsync(bool toWord)
    {
        var extension = toWord ? "docx" : "xlsx";
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = toWord
                ? Tr.T("Сохранить журнал в Word", "Журналды Word форматында сактоо", "Save the journal to Word", "Günlüğü Word olarak kaydet", "Jurnalni Word formatida saqlash")
                : Tr.T("Сохранить журнал в Excel", "Журналды Excel форматында сактоо", "Save the journal to Excel", "Günlüğü Excel olarak kaydet", "Jurnalni Excel formatida saqlash"),
            SuggestedFileName = $"transfers-{DateTime.Now:yyyy-MM-dd}.{extension}",
            FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
        });

        var path = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var search = MovementsSearchBox?.Text?.Trim() ?? "";
            var status = _transferStatusFilter;
            var transfers = StockTransferService.Instance.LoadTransfers(search, status);
            var shop = UserPreferences.Instance.StoreName;

            await Task.Run(() =>
            {
                if (toWord)
                    StockTransferExportService.ExportJournalToWord(path!, transfers, TransferStatusText, shop);
                else
                    StockTransferExportService.ExportJournalToExcel(path!, transfers, TransferStatusText, shop);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выгрузка журнала перемещений не удалась: {ex}", "WARNING");
        }
    }

    private void TransferRow_Opened(object? sender, TappedEventArgs e)
    {
        if (TransfersGrid?.SelectedItem is TransferRow row)
            OpenTransferCard(row.Id);
    }

    private void OpenTransferCard(string transferId)
    {
        var owner = this;
        var dialog = new StockTransferDialog(transferId);
        _ = dialog.ShowDialog(owner);
        dialog.Closed += (_, _) => RefreshTransfers();
    }

    /// <summary>Строка журнала перемещений. Отдельный тип, а не кортеж: DataGrid привязывается
    /// к именам свойств.</summary>
    private sealed class MovementRow
    {
        public string WhenText { get; init; } = "";
        public string ProductName { get; init; } = "";
        public string KindText { get; init; } = "";
        public string QuantityText { get; init; } = "";
        public string Note { get; init; } = "";
        public bool IsWriteOff { get; init; }
    }

    /// <summary>Журнал движения товаров за выбранный период. Продажи и списания читаются из
    /// локальной базы одним запросом и показываются вперемешку по времени — так видно всю
    /// историю расхода, а не только одну её половину.</summary>
    private void RefreshMovements()
    {
        if (MovementsGrid is null)
            return;

        var days = MovementsQuarterRadio.IsChecked == true ? 90
            : MovementsMonthRadio.IsChecked == true ? 30
            : 7;

        var to = DateTime.Now;
        var from = to.Date.AddDays(-(days - 1));
        var search = MovementsSearchBox?.Text?.Trim() ?? "";

        var movements = DatabaseService.Instance.LoadStockMovements(from, to);

        var rows = movements
            .Where(m => search.Length == 0 ||
                        m.ProductName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(m => new MovementRow
            {
                WhenText = m.At.ToString("dd.MM.yyyy HH:mm"),
                ProductName = m.ProductName,
                KindText = m.Kind == "writeoff"
                    ? Tr.T("Списание", "Эсептен чыгаруу", "Write-off", "Zayiat", "Hisobdan chiqarish")
                    : Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv"),
                QuantityText = "−" + m.Quantity.ToString("0.###"),
                Note = DescribeMovementNote(m.Kind, m.Note),
                IsWriteOff = m.Kind == "writeoff",
            })
            .ToList();

        MovementsGrid.ItemsSource = rows;

        var soldTotal = movements.Where(m => m.Kind != "writeoff").Sum(m => m.Quantity);
        var writeOffTotal = movements.Where(m => m.Kind == "writeoff").Sum(m => m.Quantity);
        MovementsSummaryText.Text =
            Tr.T("За период", "Мезгил ичинде", "For the period", "Dönem boyunca", "Davr ichida")
            + $": {rows.Count} "
            + Tr.T("записей", "жазуу", "records", "kayıt", "yozuv")
            + " · " + Tr.T("продано", "сатылды", "sold", "satıldı", "sotildi") + $" {soldTotal:N0}"
            + " · " + Tr.T("списано", "эсептен чыгарылды", "written off", "zayiat", "hisobdan chiqarildi") + $" {writeOffTotal:N0}";
    }

    /// <summary>Примечание к строке журнала. У продаж в базе лежит служебное слово источника
    /// («local» — записано кассой при продаже, «backfill» — подтянуто с сервера при догрузке
    /// истории); кассиру оно ничего не говорит, поэтому переводится на человеческий язык.
    /// У списаний примечание — это причина, её показываем как есть.</summary>
    private static string DescribeMovementNote(string kind, string note)
    {
        if (kind == "writeoff")
            return note;

        return note switch
        {
            "backfill" => Tr.T("из истории сервера", "сервердин тарыхынан", "from server history",
                               "sunucu geçmişinden", "server tarixidan"),
            "local" or "" => Tr.T("продажа на кассе", "кассадагы сатуу", "sold at the till",
                                  "kasada satış", "kassadagi sotuv"),
            _ => note,
        };
    }

    private void MovementsFilter_Changed(object? sender, RoutedEventArgs e) => RefreshMovements();

    private void MovementsFilter_Changed(object? sender, TextChangedEventArgs e) => RefreshMovements();

    /// <summary>Переключение «Список / Плитки». Обе панели стоят в одной ячейке сетки и просто
    /// показываются по очереди — данные у них общие (та же страница каталога), поэтому
    /// перезагружать ничего не нужно.</summary>
    private void WarehouseViewMode_Changed(object? sender, RoutedEventArgs e)
    {
        if (ProductsGrid is null || ProductsTilesScroll is null)
            return;

        var tiles = ViewTilesRadio.IsChecked == true;
        ProductsGrid.IsVisible = !tiles;
        ProductsTilesScroll.IsVisible = tiles;
    }

    /// <summary>Итог под таблицей товаров: позиции, единицы и стоимость склада по закупке и по
    /// продаже. Считается по всему каталогу, а не по показанной странице — иначе цифра менялась
    /// бы при каждом листании и не отвечала бы на вопрос «сколько всего лежит на складе».</summary>
    private void RefreshWarehouseTotals()
    {
        var products = CatalogCacheService.Products.ToList();
        if (products.Count == 0)
        {
            WarehouseTotalsPanel.ItemsSource = System.Array.Empty<KpiCardVm>();
            return;
        }

        var units = products.Sum(p => p.Quantity);
        var purchaseValue = products.Sum(p => p.Quantity * p.PurchasePrice);
        var saleValue = products.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine));
        var lowOrOut = products.Count(p => p.Quantity <= 0 || p.IsLowStock);

        WarehouseTotalsPanel.ItemsSource = new List<KpiCardVm>
        {
            new() { Label = Tr.T("Позиций:", "Позициялар:", "Items:", "Kalem:", "Pozitsiya:"), Value = products.Count.ToString("N0") },
            new() { Label = Tr.T("Единиц на складе:", "Кампадагы бирдик:", "Units in stock:", "Stoktaki adet:", "Ombordagi birlik:"), Value = units.ToString("N0") },
            new() { Label = Tr.T("По закупке:", "Сатып алуу боюнча:", "At cost:", "Maliyetle:", "Tannarxda:"), Value = $"{purchaseValue:N0} " + Tr.T("сом", "сом", "KGS", "som", "so'm") },
            new() { Label = Tr.T("По продаже:", "Сатуу боюнча:", "At sale price:", "Satışta:", "Sotuvda:"), Value = $"{saleValue:N0} " + Tr.T("сом", "сом", "KGS", "som", "so'm") },
            new() { Label = Tr.T("Заканчивается:", "Аяктап жатат:", "Running low:", "Azalıyor:", "Tugayapti:"), Value = lowOrOut.ToString("N0") },
        };
    }

    /// <summary>Списки причин списания через "Брак" собираются вручную вместо ComboBox — тот же
    /// приём, что уже надёжно решил похожую проблему с кнопками в клиентской базе.</summary>
    private void InitializeWriteOffReasonPicker()
    {
        WriteOffReasonListPanel.Children.Clear();
        foreach (var reason in _viewModel.WriteOffReasonOptions)
        {
            var button = new Button
            {
                Classes = { "SecondaryButton" },
                Content = reason,
                Tag = reason,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            button.Click += WriteOffReasonItem_Click;
            WriteOffReasonListPanel.Children.Add(button);
        }

        SetWriteOffReason(_viewModel.WriteOffReason);
    }

    private void SetWriteOffReason(string reason)
    {
        _viewModel.WriteOffReason = reason;
        WriteOffReasonButtonText.Text = reason;
    }

    private void WriteOffReasonButton_Click(object? sender, RoutedEventArgs e)
    {
        WriteOffReasonListBorder.IsVisible = !WriteOffReasonListBorder.IsVisible;
    }

    private void WriteOffReasonItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string reason })
            SetWriteOffReason(reason);
        WriteOffReasonListBorder.IsVisible = false;
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        // AnalyticsOnly просят и уже существующий ярлык из "Финансов" (FinanceWindow.
        // NavigateToStock_Click), и он не знает про платную блокировку — если аналитика не
        // куплена, её вкладка теперь вообще скрыта (см. RefreshPaidFeatureVisibility), и слепо
        // прятать ещё и Товары/Ревизию/Списание оставило бы окно полностью пустым. Поэтому
        // AnalyticsOnly действует, только когда аналитика реально разблокирована.
        if (AnalyticsOnly && UserPreferences.Instance.WarehouseAnalyticsUnlocked)
        {
            ProductsTabItem.IsVisible = false;
            RevisionTabItem.IsVisible = false;
            WriteOffTabItem.IsVisible = false;
            // Подсказка про сканер штрих-кода бессмысленна здесь — в этом режиме нет поиска
            // и редактирования товаров, только просмотр аналитики.
            ScanHintBorder.IsVisible = false;
        }

        _barcodeInputService.BarcodeScanned += OnBarcodeScanned;
        try
        {
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            RefreshWarehouseTotals();
            _viewModel.ReceivingLines.CollectionChanged += (_, _) => RefreshReceivingSummary();
            _viewModel.ReceivingLineAdded += line =>
            {
                RefreshReceivingSummary();
                // Длинная накладная: принятая строка должна быть видна, а не уходить за край.
                ReceivingGrid.ScrollIntoView(line, null);
            };
            RefreshReceivingSummary();
            ReceivingPaidRadio.IsChecked = true;
            _viewModel.ReceivingPaidNow = true;
            _ = _viewModel.LoadReceivingSuppliersAsync();
            if (!string.IsNullOrWhiteSpace(InitialBarcode))
                _viewModel.HandleBarcodeScan(InitialBarcode.Trim(), isRevisionTab: true);
        }
        catch (Exception ex)
        {
            PosMessageBox.Show(this, $"Не удалось загрузить каталог: {ex.Message}", "Склад",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (AnalyticsOnly && UserPreferences.Instance.WarehouseAnalyticsUnlocked)
            WarehouseTabs.SelectedItem = AnalyticsTabItem;
    }

    public void FocusWithBarcode(string? barcode)
    {
        if (!string.IsNullOrWhiteSpace(barcode))
            _viewModel.HandleBarcodeScan(barcode.Trim(), isRevisionTab: true);
        Activate();
        Focus();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        _viewModel.RevisionLineAdded -= OnRevisionLineAdded;
    }

    /// <summary>Сканер штрих-кодов — это просто клавиатура: если фокус завис в поле поиска
    /// (кассир кликнул туда, а потом сразу сканирует, не кликнув мимо), символы штрих-кода
    /// печатаются как текст в это поле вместо перехвата сканером — Window_KeyDown ниже
    /// специально пропускает обработку, пока фокус в TextBox. Esc — быстрый, заметный способ
    /// вернуть фокус на окно самому, не переключая вкладки туда-обратно.</summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        e.Handled = true;
        if (sender is TextBox tb)
            tb.Text = "";
        Focus();
    }

    /// <summary>Фильтр "Единица продажи" (2026-09-07) — четыре радиокнопки над таблицей Склада,
    /// см. WarehouseViewModel.SetSaleUnitFilter.</summary>
    private void SaleUnitFilter_Changed(object? sender, RoutedEventArgs e)
    {
        var filter = sender switch
        {
            _ when ReferenceEquals(sender, SaleUnitWeightRadio) => "weight",
            _ when ReferenceEquals(sender, SaleUnitPieceRadio) => "piece",
            _ when ReferenceEquals(sender, SaleUnitPiecePackageRadio) => "piecepackage",
            _ => "all",
        };
        _viewModel.SetSaleUnitFilter(filter);
    }

    /// <summary>Сканер в окне склада (2026-09-26, жалоба «невозможно сразу сканировать следующий
    /// товар» в приёмке). Раньше обработчик стоял на обычной (всплывающей) фазе и молчал, пока фокус
    /// в любом текстовом поле. Отсюда два тупика:
    /// • фокус на строке таблицы — Enter сканера съедала сама таблица (переход на строку ниже), скан
    ///   не завершался;
    /// • фокус в поле количества или в ячейке таблицы — штрихкод печатался прямо туда
    ///   («12» превращалось в «122990000…»).
    /// Теперь обработчик туннельный — видит клавиши раньше таблицы. Вне текстового поля работает
    /// общий сервис сканера. Внутри поля — строгая проверка: скан это только очень быстрый набор
    /// (символы чаще раза в 40 мс), человек так не печатает, поэтому обычный ввод названия и цен не
    /// страдает (ср. живую ошибку 2026-09-16 в AvaloniaKeyboardWedgeBarcodeService). Когда скан
    /// распознан, поле возвращается к тексту, который был в нём до скана.</summary>
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement();
        if (focused is not TextBox box)
        {
            _barcodeInputService.ProcessKeyDown(e);
            return;
        }

        // Внутри полей скан перехватываем только в приёмке. На других вкладках скан в поле поиска —
        // обычный способ найти товар по штрихкоду, его не трогаем. Поле скана приёмки ловит скан
        // само: Enter, подсказки по названию.
        if (!ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem) || ReferenceEquals(box, ReceivingScanBox))
            return;

        HandleScanInsideField(box, e);
    }

    // Порог между символами скана. Меряется по обработке на UI-потоке, а не по самому сканеру:
    // окно может подтормозить, поэтому с запасом — 50 мс (человек так быстро три клавиши подряд
    // не нажимает). Enter после уже опознанного скана ждём дольше — до 150 мс: в первой проверке
    // 2026-09-26 скан не распознался именно из-за паузы перед Enter.
    private const int FieldScanIntervalMs = 50;
    private const int FieldScanEnterMs = 150;
    private long _fieldScanLastTick;
    private string _fieldScanBuffer = "";
    private int _fieldScanFastRun;
    private TextBox? _fieldScanBox;
    private string? _fieldScanSnapshot;

    private void HandleScanInsideField(TextBox box, KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || mods.HasFlag(KeyModifiers.Meta))
            return;

        var now = Environment.TickCount64;
        var sinceLast = now - _fieldScanLastTick;
        var sameBurst = _fieldScanBuffer.Length > 0 && ReferenceEquals(box, _fieldScanBox);
        var fast = sameBurst && sinceLast is >= 0 and <= FieldScanIntervalMs;
        _fieldScanLastTick = now;

        if (e.Key == Key.Enter)
        {
            if (sameBurst && sinceLast is >= 0 and <= FieldScanEnterMs
                && _fieldScanBuffer.Length >= 4 && _fieldScanFastRun >= 2)
            {
                e.Handled = true;
                var code = _fieldScanBuffer;
                // Первые символы скана успели напечататься в поле — возвращаем его как было.
                box.Text = _fieldScanSnapshot;
                ResetFieldScan();
                OnBarcodeScanned(code);
                return;
            }

            ResetFieldScan();
            return;
        }

        var ch = ScanChar(e.Key, mods.HasFlag(KeyModifiers.Shift));
        if (ch is null)
        {
            ResetFieldScan();
            return;
        }

        if (fast)
        {
            _fieldScanFastRun++;
        }
        else
        {
            _fieldScanBuffer = "";
            _fieldScanFastRun = 0;
            _fieldScanBox = box;
            _fieldScanSnapshot = box.Text;
        }

        _fieldScanBuffer += ch;
        if (_fieldScanFastRun >= 2)
            e.Handled = true;
    }

    private void ResetFieldScan()
    {
        _fieldScanBuffer = "";
        _fieldScanFastRun = 0;
        _fieldScanBox = null;
        _fieldScanSnapshot = null;
    }

    private static string? ScanChar(Key key, bool shift)
    {
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return ((char)('0' + (key - Key.NumPad0))).ToString();
        if (key is >= Key.A and <= Key.Z)
        {
            var c = (char)('a' + (key - Key.A));
            return shift ? char.ToUpperInvariant(c).ToString() : c.ToString();
        }
        if (key is Key.OemMinus or Key.Subtract)
            return "-";
        if (key is Key.OemPeriod or Key.Decimal)
            return ".";
        return null;
    }

    private void OnBarcodeScanned(string barcode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Вкладку узнаём по ней самой, а не по номеру. Раньше здесь стояли номера
            // (1 = Ревизия, остальное — Списание), и с появлением «Приёмки» и «Перемещения»
            // скан в приёмке уходил строкой ревизии, а скан в ревизии — в списание.
            var selected = WarehouseTabs.SelectedItem;
            if (ReferenceEquals(selected, ReceivingTabItem))
            {
                // Поле приёмки само ловит скан по Enter — второй раз его не добавляем.
                if (!ReceivingScanBox.IsFocused)
                    _ = ScanIntoReceivingAsync(barcode);
                return;
            }

            if (ReferenceEquals(selected, RevisionTabItem))
            {
                _viewModel.HandleBarcodeScan(barcode, isRevisionTab: true);
                return;
            }

            if (ReferenceEquals(selected, WriteOffTabItem))
            {
                _viewModel.HandleBarcodeScan(barcode, isRevisionTab: false);
                WriteOffQuantityBox?.Focus();
            }
        });
    }

    private async void PrintLabel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var dialog = new LabelPrintDialog(product);
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private async void PrintPriceTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        if (!EnsurePriceTagEditorUnlocked())
            return;

        var dialog = new PriceTagPrintDialog(product);
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private void BulkImport_Click(object? sender, RoutedEventArgs e) => BulkImportWindow.Open(this);

    private async void BulkPriceTag_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureBulkPriceTagUnlocked())
            return;

        await BulkPriceTagPrintDialog.Open(this).ConfigureAwait(true);
    }

    private async void OpenLabelEditorTab_Click(object? sender, RoutedEventArgs e)
    {
        if (!EnsureLabelEditorUnlocked())
            return;

        var editor = new LabelTemplateEditorDialog();
        await editor.ShowDialog(this).ConfigureAwait(true);
    }

    /// <summary>Редактор этикеток — стал платной доп. услугой (2026-09-07, раньше был бесплатным).
    /// Тот же паттерн, что и EnsurePriceTagEditorUnlocked ниже.</summary>
    private bool EnsureLabelEditorUnlocked()
    {
        if (UserPreferences.Instance.LabelEditorUnlocked)
            return true;

        var serial = SerialActivationDialog.Show(this, Tr.T("Редактор этикетки", "Этикетка редактору", "Label editor", "Etiket düzenleyici", "Yorliq muharriri"));
        if (serial == null)
            return false;

        var isPermanent = LicenseKeys.IsPermanentSerial("labeleditor", serial);
        if (!isPermanent && !LicenseKeys.IsMasterSerial(serial))
        {
            PosAlertDialog.Show(this,
                Tr.T("Неверный серийный номер", "Серия номери туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return false;
        }

        UserPreferences.Instance.LabelEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("labeleditor");

        PosAlertDialog.Show(this,
            Tr.T("Редактор этикетки активирован", "Этикетка редактору иштетилди", "Label editor activated", "Etiket düzenleyici etkinleştirildi", "Yorliq muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступен редактор этикеток.",
                "Эми этикетка редактору жеткиликтүү."),
            PosAlertKind.Success);
        return true;
    }

    /// <summary>Редактор ценников — тоже платная доп. услуга (2026-09-04). Если ещё не
    /// разблокирован, сразу предлагает ввести код (без промежуточного "это платно" алерта —
    /// один клик до ввода серийника, отменить можно прямо в этом же диалоге).</summary>
    private bool EnsurePriceTagEditorUnlocked()
    {
        if (UserPreferences.Instance.PriceTagEditorUnlocked)
            return true;

        var serial = SerialActivationDialog.Show(this, Tr.T("Редактор ценников", "Ценник редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorliqlari muharriri"));
        if (serial == null)
            return false;

        var isPermanent = LicenseKeys.IsPermanentSerial("pricetag", serial);
        if (!isPermanent && !LicenseKeys.IsMasterSerial(serial))
        {
            PosAlertDialog.Show(this,
                Tr.T("Неверный серийный номер", "Серия номери туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return false;
        }

        UserPreferences.Instance.PriceTagEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("pricetag");

        PosAlertDialog.Show(this,
            Tr.T("Редактор ценников активирован", "Ценник редактору иштетилди", "Price tag editor activated", "Fiyat etiketi düzenleyici etkinleştirildi", "Narx yorliqlari muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступна печать ценника для одного товара: Склад → Товары → кнопка «💲 Ценник» на карточке товара.",
                "Эми бир товар үчүн ценник басып чыгаруу жеткиликтүү: Склад → Товарлар → товар карточкасындагы «💲 Ценник» баскычы."),
            PosAlertKind.Success);
        return true;
    }

    /// <summary>Массовая печать ценников — отдельная от EnsurePriceTagEditorUnlocked доп. услуга
    /// (2026-09-04, по явной просьбе пользователя: активация одной не должна включать другую),
    /// хотя серийник и диалог активации те же самые (единый мастер-ключ для всех доп. услуг).</summary>
    private bool EnsureBulkPriceTagUnlocked()
    {
        if (UserPreferences.Instance.BulkPriceTagUnlocked)
            return true;

        var serial = SerialActivationDialog.Show(this, Tr.T("Массовая печать ценников", "Ценниктерди массалык басып чыгаруу", "Bulk price tag printing", "Toplu fiyat etiketi baskısı", "Ommaviy narx yorliqlarini chop etish"));
        if (serial == null)
            return false;

        var isPermanent = LicenseKeys.IsPermanentSerial("bulktag", serial);
        if (!isPermanent && !LicenseKeys.IsMasterSerial(serial))
        {
            PosAlertDialog.Show(this,
                Tr.T("Неверный серийный номер", "Серия номери туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return false;
        }

        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("bulktag");
        RefreshPaidFeatureVisibility();

        PosAlertDialog.Show(this,
            Tr.T("Массовая печать ценников активирована", "Ценниктерди массалык басып чыгаруу иштетилди", "Bulk price tag printing activated", "Toplu fiyat etiketi baskısı etkinleştirildi", "Ommaviy narx yorlig'i chop etish faollashtirildi"),
            Tr.T(
                "Теперь доступна кнопка «🏷 Массовая печать ценников» в Склад → Товары — выберите нужные товары и распечатайте ценники сразу для всех.",
                "Эми Склад → Товарлар бетинде «🏷 Ценниктерди массалык басып чыгаруу» баскычы жеткиликтүү — керектүү товарларды тандап, баарына бирден ценник басып чыгарыңыз."),
            PosAlertKind.Success);
        return true;
    }

    private async void AddProduct_Click(object? sender, RoutedEventArgs e)
    {
        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast("Добавление товара недоступно в этом режиме.", isWarning: true);
            return;
        }

        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ProductEditDialog(catalogApi, null);
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (saved)
        {
            prompts?.ShowToast("Товар сохранён.");
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
    }

    private async void EditProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast("Редактирование товара недоступно в этом режиме.", isWarning: true);
            return;
        }

        // Свежая карточка с сервера (2026-09-07): плитка из SQLite-кэша не хранит скидку, наценку,
        // описание, оптовую цену, страну, вес и доп. штрихкоды (LocalProductRecord их не имеет) —
        // форма показывала их пустыми, а «Сохранить» отправлял пустые значения и СТИРАЛ их на
        // сервере. Офлайн или ошибка — открываем как раньше, из кэша.
        var toEdit = product;
        try
        {
            var detail = await catalogApi.ProductsDetailAsync(product.Id).ConfigureAwait(true);
            var apiBaseUrl = App.AppHost?.Services.GetService<AppSettings>()?.ApiBaseUrl ?? "";
            if (detail is { } json && ProductCatalogMapper.TryTile(json, apiBaseUrl) is { } fresh)
            {
                fresh.ProductImagePath = product.ProductImagePath;
                toEdit = fresh;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Product detail fetch before edit failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }

        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ProductEditDialog(catalogApi, toEdit);
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (saved)
        {
            prompts?.ShowToast("Товар сохранён.");
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Кнопка "Удалить" в строке таблицы товаров (2026-09-07). Необратимо — поэтому
    /// сначала PosConfirmDialog с красной кнопкой; после успешного DELETE каталог перечитывается
    /// с сервера тем же путём, что и после редактирования (RefreshFromApiAsync → sync выкинет
    /// удалённый товар из локального кэша и из списка).</summary>
    private async void DeleteProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        var permissions = App.AppHost?.Services.GetService<IPermissionService>();
        if (catalogApi is null)
        {
            prompts?.ShowToast("Удаление товара недоступно в этом режиме.", isWarning: true);
            return;
        }

        var confirmed = PosConfirmDialog.Show(
            this,
            Tr.T("Удалить товар", "Товарды өчүрүү", "Delete product", "Ürünü sil", "Mahsulotni o'chirish"),
            Tr.T($"Удалить «{product.Title}»? Товар исчезнет из каталога NurCRM и из кассы. Это действие нельзя отменить.",
                 $"«{product.Title}» өчүрүлсүнбү? Товар NurCRM каталогунан жана кассадан жоголот. Бул аракетти артка кайтарууга болбойт."),
            Tr.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"),
            Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            PosConfirmAccent.Danger);
        if (!confirmed)
            return;

        // 2026-09-08: личный код доступа сотрудника (владелец/админ проходит без кода) —
        // см. EmployeeAccessGate. Не активен, пока в Настройки → Сотрудники не задан хотя бы
        // один код для удаления со склада.
        if (EmployeeAccessGate.IsActiveFor(EmployeeAccessGate.WarehouseDelete, permissions) &&
            prompts != null &&
            !await prompts.ConfirmWithCodeAsync(
                Tr.T("Удаление товара со склада", "Товарды кампадан өчүрүү", "Deleting product from warehouse", "Depodan ürün silme", "Mahsulotni ombordan o'chirish"),
                Tr.T($"Введите свой код доступа, чтобы удалить «{product.Title}» со склада.",
                    $"«{product.Title}» товарын кампадан өчүрүү үчүн жеке кодуңузду киргизиңиз.",
                    $"Enter your access code to delete \"{product.Title}\" from the warehouse.",
                    $"\"{product.Title}\" ürününü depodan silmek için erişim kodunuzu girin.",
                    $"\"{product.Title}\" mahsulotini ombordan o'chirish uchun kirish kodingizni kiriting."),
                entered => EmployeeAccessGate.TryValidate(EmployeeAccessGate.WarehouseDelete, entered)).ConfigureAwait(true))
            return;

        try
        {
            // 2026-09-09: офлайн (обычный или автономный режим) — удаляем прямо из локальной
            // SQLite, минуя ICatalogApiService.DeleteProductAsync (реальный HTTP к NurCRM).
            if (OfflineModeHelper.UseLocalOperations)
            {
                LocalProductEditor.DeleteLocally(product.Id);
                prompts?.ShowToast("Товар удалён.");
                await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
                return;
            }

            var deleted = await catalogApi.DeleteProductAsync(product.Id).ConfigureAwait(true);
            if (!deleted)
            {
                prompts?.ShowToast("Не удалось удалить товар: сервер не принял запрос на удаление.", isWarning: true);
                return;
            }

            prompts?.ShowToast("Товар удалён.");
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            prompts?.ShowToast($"Не удалось удалить товар: {ex.Message}", isWarning: true);
        }
    }

    private async void UploadPhoto_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var photoPicker = App.AppHost?.Services.GetService<ISettingsImagePicker>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null || photoPicker is null)
        {
            prompts?.ShowToast("Загрузка фото недоступна в этом режиме.", isWarning: true);
            return;
        }

        var filePath = await photoPicker.PickProductPhotoAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        try
        {
            var uploadedUrl = await catalogApi
                .UploadProductImageAsync(product.Id, filePath, isPrimary: true)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(uploadedUrl))
            {
                prompts?.ShowToast("Не удалось загрузить фото.", isWarning: true);
                return;
            }

            // Show the picked file immediately — same bytes just landed on the server,
            // no need to round-trip a redownload through the thumbnail cache.
            product.ProductImagePath = filePath;
            App.AppHost?.Services.GetService<MySqlAuditService>()?
                .LogEvent("catalog", "product_photo_upload", new { productId = product.Id });
            prompts?.ShowToast("Фото товара загружено.");
        }
        catch (ApiException ex)
        {
            prompts?.ShowToast($"Загрузка фото: {ex.Message}", isWarning: true);
        }
        catch (HttpRequestException)
        {
            prompts?.ShowToast("Не удалось загрузить фото — нет сети.", isWarning: true);
        }
    }

    private void OnRevisionLineAdded(RevisionLineVm line)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnRevisionLineAdded(line));
            return;
        }

        RevisionGrid.SelectedItem = line;
        RevisionGrid.ScrollIntoView(line, null);
    }

    #region Аналитика склада/товаров

    private sealed class KpiCardVm
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private sealed class AnalyticsTopValueRow
    {
        public string Title { get; init; } = "";
        public string StockText { get; init; } = "";
        public string PriceText { get; init; } = "";
        public string ValueText { get; init; } = "";
    }

    private sealed class AnalyticsCategoryRow
    {
        public string CategoryName { get; init; } = "";
        public string SkuCountText { get; init; } = "";
        public string StockText { get; init; } = "";
        public string ValueText { get; init; } = "";
    }

    private static double ParsePriceValue(string? priceLine) =>
        LocalCartService.ParsePrice(priceLine ?? "");

    /// <summary>Считается локально из уже загруженного каталога (CatalogCacheService.Products) —
    /// без отдельных запросов к серверу, поэтому мгновенно и без дополнительной нагрузки.</summary>
    private void RefreshAnalytics()
    {
        var products = CatalogCacheService.Products.ToList();

        var skuCount = products.Count;
        var outOfStock = products.Count(p => p.Quantity <= 0);
        var lowStock = products.Count(p => p.IsLowStock && p.Quantity > 0);
        var purchaseValue = products.Sum(p => p.Quantity * p.PurchasePrice);
        var saleValue = products.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine));

        AnalyticsKpiPanel.ItemsSource = new List<KpiCardVm>
        {
            new() { Label = Tr.T("Всего товаров (SKU)", "Бардык товарлар (SKU)", "Total products (SKU)", "Toplam ürün (SKU)", "Jami mahsulotlar (SKU)"), Value = skuCount.ToString() },
            new() { Label = Tr.T("Нет в наличии", "Дүкөндө жок", "Out of stock", "Stokta yok", "Mavjud emas"), Value = outOfStock.ToString() },
            new() { Label = Tr.T("Низкий остаток", "Аз калды", "Low stock", "Düşük stok", "Kam qoldiq"), Value = lowStock.ToString() },
            new() { Label = Tr.T("Остаток по закупке", "Сатып алуу баасы боюнча калдык", "Stock at purchase price", "Alış fiyatına göre stok", "Sotib olish narxi bo'yicha qoldiq"), Value = $"{purchaseValue:N0} сом" },
            new() { Label = Tr.T("Остаток по продаже", "Сатуу баасы боюнча калдык", "Stock at sale price", "Satış fiyatına göre stok", "Sotuv narxi bo'yicha qoldiq"), Value = $"{saleValue:N0} сом" },
        };

        var topValue = products
            .Select(p => new { Product = p, Value = p.Quantity * ParsePriceValue(p.PriceLine) })
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .Take(10)
            .ToList();

        AnalyticsTopValueGrid.ItemsSource = topValue
            .Select(x => new AnalyticsTopValueRow
            {
                Title = x.Product.Title,
                StockText = x.Product.Quantity.ToString("0.###"),
                PriceText = $"{ParsePriceValue(x.Product.PriceLine):N2} сом",
                ValueText = $"{x.Value:N2} сом",
            })
            .ToList();
        BarChartRenderer.Render(AnalyticsTopValueChart, topValue
            .Select(x => (x.Product.Title, (double)x.Value, $"{x.Value:N0} сом"))
            .ToList());

        var byCategory = products
            .GroupBy(p => string.IsNullOrWhiteSpace(p.Category) ? Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz") : p.Category!.Trim())
            .Select(g => new
            {
                Name = g.Key,
                SkuCount = g.Count(),
                Stock = g.Sum(p => p.Quantity),
                Value = g.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine)),
            })
            .OrderByDescending(g => g.Value)
            .ToList();

        AnalyticsCategoryGrid.ItemsSource = byCategory
            .Select(g => new AnalyticsCategoryRow
            {
                CategoryName = g.Name,
                SkuCountText = g.SkuCount.ToString(),
                StockText = g.Stock.ToString("0.###"),
                ValueText = $"{g.Value:N2} сом",
            })
            .ToList();
        var categoryChartData = byCategory
            .Take(10)
            .Select(g => (g.Name, g.Value, $"{g.Value:N0} сом"))
            .ToList();
        BarChartRenderer.Render(AnalyticsCategoryChart, categoryChartData);
        BarChartRenderer.RenderPie(AnalyticsCategoryPie, categoryChartData);
    }

    #endregion

    #region Window Controls & Dragging

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void CloseByEscape()
    {
        var receiving = _viewModel.ReceivingLines.Count;
        var revision = _viewModel.RevisionLines.Count;
        if (receiving + revision > 0)
        {
            var parts = new List<string>();
            if (receiving > 0)
                parts.Add(Tr.T($"приёмка — {receiving}", $"кабыл алуу — {receiving}", $"receiving — {receiving}", $"kabul — {receiving}", $"qabul — {receiving}"));
            if (revision > 0)
                parts.Add(Tr.T($"ревизия — {revision}", $"ревизия — {revision}", $"stocktake — {revision}", $"sayım — {revision}", $"reviziya — {revision}"));
            var close = PosConfirmDialog.Show(
                this,
                Tr.T("Закрыть склад?", "Кампаны жабуу керекпи?", "Close the warehouse?", "Depo kapatılsın mı?", "Omborni yopasizmi?"),
                Tr.T("Есть непроведённые строки: ", "Өткөрүлө элек саптар бар: ", "There are unposted lines: ", "Kaydedilmemiş satırlar var: ", "O'tkazilmagan qatorlar bor: ")
                    + string.Join(", ", parts)
                    + Tr.T(". После закрытия они пропадут.", ". Жабылгандан кийин алар жоголот.", ". They will be lost after closing.", ". Kapatınca kaybolacaklar.", ". Yopilgandan keyin ular yo'qoladi."),
                confirmText: Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"),
                cancelText: Tr.T("Остаться", "Калуу", "Stay", "Kal", "Qolish"));
            if (!close)
                return;
        }

        Close();
    }

    private void ExpandWriteOffHistory_Click(object? sender, RoutedEventArgs e) =>
        WriteOffHistoryWindow.Open(this);

    /// <summary>
    /// Позволяет перетаскивать кастомное окно за шапку или пустые области.
    /// </summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    #endregion
}