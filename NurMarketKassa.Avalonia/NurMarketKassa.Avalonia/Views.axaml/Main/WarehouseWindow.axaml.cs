using System;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Analytics;
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

public partial class WarehouseWindow : Window, IOwnerSection, IOwnerSectionKeepAlive
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
        // 2026-10-24 (аудит, круг 8, С-14): заголовок окна — «Склад» (в окне пять вкладок, не только ревизия и списание); ресурс не менялся.
        Title = Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor");
        // Сканер ловим на ТУННЕЛЬНОЙ фазе (раньше любых элементов окна) — см. Window_KeyDown.
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        this.FitToScreen();
        DataContext = _viewModel;
        // 2026-10-30 (приёмка 1.17.65, «Права»): колонка «Себестоимость» (закупка за единицу и сумма закупки) — только ролям с правом видеть закупку.
        if (!RevisionShowPurchase)
            foreach (var cost in ProductsGrid.Columns.Where(c => c.SortMemberPath == "PurchasePrice"))
                cost.IsVisible = false;
        // Esc закрывает склад, но не молча выбрасывает набранные строки приёмки и ревизии:
        // они живут только в этом окне и после закрытия пропадут.
        EscapeKey.Attach(this, closeWindow: CloseByEscape);
        // 2026-10-21: скан не нашёлся нигде — «Товар со штрихкодом … не найден» с «Создать товар» (WarehouseWindow.ScanLookup.cs).
        _viewModel.BarcodeNotFound = OnWarehouseBarcodeNotFoundAsync;
        InitializeWriteOffReasonPicker();
        // 2026-10-19, этап О9: пустые состояния и узкие таблицы (WarehouseWindow.O9.cs).
        InitO9Look();
        // 2026-10-23: вкладка «Массовое добавление» — скан подряд, новые товары и приход (WarehouseWindow.BulkScan.cs).
        InitBulkScan();
        // 2026-10-24, ТЗ «Склад», пакеты Б1 и В: приёмка «скан → строка», панель товаров поставщика, «＋ поставщик» (WarehouseWindow.ReceivingScan.cs).
        InitReceivingScan();
        // 2026-10-27: «Загрузить накладную» (Excel/CSV/XML) в приёмке (WarehouseWindow.InvoiceImport.cs).
        InitInvoiceImport();
        // 2026-10-24, ТЗ «Склад», пакет Р2: вкладка «Ревизия» — «скан → факт» на движке RevisionSession (WarehouseWindow.Revision.cs).
        InitRevision();
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
        // В программе владельца аналитика склада живёт в разделе «Аналитика» (вкладка «Склад»). На
        // тарифе «Старт» этого раздела нет (как и «ABC-анализа») — там купленная аналитика остаётся
        // вкладкой склада, иначе её было бы негде открыть.
        AnalyticsTabItem.IsVisible = (!_isOwnerSection || !TariffGate.CanUseSalesAnalytics)
                                     && UserPreferences.Instance.WarehouseAnalyticsUnlocked;
        BulkPriceTagButton.IsVisible = UserPreferences.Instance.BulkPriceTagUnlocked;
    }

    /// <summary>Аналитика считается только когда кассир реально открыл эту вкладку — сразу
    /// с актуальными данными (в т.ч. после добавления/списания товара), без лишней работы
    /// на каждое открытие окна склада.</summary>
    private void WarehouseTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 2026-10-24 (замечание к Р2): SelectionChanged всплывает от вложенных списков (поставщики, ревизия) — берём только вкладки.
        if (!ReferenceEquals(e.Source, WarehouseTabs))
            return;
        // Сравниваем с самими вкладками, а не с их номерами: номера уже один раз поехали,
        // когда между «Товарами» и «Ревизией» встала «Приёмка», и аналитика начала считаться
        // при открытии перемещений.
        if (ReferenceEquals(WarehouseTabs.SelectedItem, MovementsTabItem))
            MovementsSection_Changed(this, new RoutedEventArgs());
        else if (ReferenceEquals(WarehouseTabs.SelectedItem, AnalyticsTabItem))
            RefreshAnalytics();
        else if (_ownerAbcTab != null && ReferenceEquals(WarehouseTabs.SelectedItem, _ownerAbcTab))
            _ = RefreshStockAbcAsync();
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

        // 2026-10-29: единый поиск (слова в любом порядке, ё=е, раскладка/транслит «Asu»→«Асу»).
        var matches = NurMarketKassa.Core.Application.SearchMatch.Products(CatalogCacheService.Products, query)
            // 2026-10-24, ТЗ «Склад» (Б1): при выбранном поставщике его товары (приходили раньше) идут первыми — со значком «поставщик».
            .OrderBy(p => IsSupplierProduct(p.Id) ? 0 : 1)
            .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(6)
            .ToList();
        // 2026-10-21: точный код (доп. штрихкод, 12 ↔ 13 цифр, артикул) — первой подсказкой, как ищет касса (склад не находит товар по скану).
        WithExactCodeFirst(matches, query, 6);

        ReceivingSuggestionsPanel.ItemsSource = matches;
        ReceivingSuggestionsBox.IsVisible = matches.Count > 0;
    }

    private void ReceivingSuggestion_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        var added = _viewModel.AddReceivingProduct(product, quantity);

        ReceivingScanBox.Text = "";
        ReceivingQuantityBox.Text = "1";
        ReceivingSuggestionsBox.IsVisible = false;
        ReceivingSuggestionsPanel.ItemsSource = null;
        RefreshReceivingSummary();
        // 2026-10-24, ТЗ «Склад» (Б1): курсор — в «Принято» новой строки (Enter — по ценам и снова в поле скана); с флажком «Сразу к
        // следующему скану» — обратно в поле скана (после клика по подсказке фокус оставался на кнопке, и следующий скан уходил мимо).
        OnReceivingLineAdded(added);
    }

    private async void ReceivingScan_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        // 2026-10-19: Enter в поле скана всегда обработан здесь — дальше по окну он не идёт (раньше при видимых подсказках не
        // ставился e.Handled, и тот же Enter мог сработать ещё раз). Пустое поле — Enter просто гасится.
        e.Handled = true;
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
            // 2026-10-19: ReceivingSuggestion_Click сам очищает поле и подсказки и возвращает фокус; от двойного добавления
            // защищают e.Handled выше и пустое поле (повторный Enter ничего не добавит).
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
        // 2026-10-24, ТЗ «Склад» (Б1): скан во время правки ячейки сначала дописывает правку (поле возвращено к тексту до серии символов
        // скана в HandleScanInsideField), и только потом обрабатывается — введённое «Принято»/цена не теряется.
        ReceivingGrid.CommitEdit();
        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        try
        {
            // 2026-10-06 (О-70): этикетка размера — +N этому размеру в строке товара.
            if (TryReceiveVariantScan(code, quantity))
            {
                ReceivingQuantityBox.Text = "1";
                RefreshReceivingSummary();
                return;
            }
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
        {
            // 2026-10-24, ТЗ «Склад» (В): поиск по поставщикам; выбор текущего программный — не должен закрывать список (флаг _supplierListSync).
            _supplierListSync = true;
            try
            {
                ReceivingSupplierSearch.Text = "";
                ReceivingSupplierList.ItemsSource = _viewModel.ReceivingSuppliers;
                ReceivingSupplierList.SelectedItem = _viewModel.ReceivingSupplier;
            }
            finally
            {
                _supplierListSync = false;
            }
            ReceivingSupplierSearch.Focus();
        }
    }

    private void ReceivingSupplierList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 2026-10-24: SelectionChanged всплывает и от вложенных списков — реагируем только на свой.
        if (_supplierListSync || !ReferenceEquals(e.Source, ReceivingSupplierList) || !ReceivingSupplierPanel.IsVisible
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

        // 2026-10-24: колонки — по имени (RcvColBarcode), а не по номеру: «Наценка %» и «Изменение» сдвинули номера.
        if (ReferenceEquals(e.Column, RcvColBarcode) && line.Source is not (NurMarketKassa.Models.ReceivingSource.Unknown or NurMarketKassa.Models.ReceivingSource.Internet)) // 2026-11-09: «Из интернета» — тот же новый товар
            e.Cancel = true;
        _rcvBarcodeEditing = !e.Cancel && ReferenceEquals(e.Column, RcvColBarcode); // 2026-10-29: в правке штрихкода — только цифры (BarcodeInput)
    }

    /// <summary>Правка с одного нажатия. DataGrid входит в правку только по второму нажатию на уже
    /// выбранную ячейку — на сенсорном экране казалось, что таблица не редактируется вовсе.</summary>
    private void ReceivingGrid_CellPointerPressed(object? sender, DataGridCellPointerPressedEventArgs e)
    {
        // 2026-10-06 (О-70): колонка «Размеры» — сетка размеров строки.
        if (e.Column is not null && ReferenceEquals(e.Column, SizesColumn) && e.Row?.DataContext is NurMarketKassa.Models.ReceivingLineVm sized)
        {
            Dispatcher.UIThread.Post(() => _ = OpenVariantGridAsync(sized, automatic: false), DispatcherPriority.Background);
            return;
        }
        if (e.Column is null || e.Column.IsReadOnly || e.Row?.DataContext is not NurMarketKassa.Models.ReceivingLineVm line)
            return;
        // 2026-11-07: нажатие на подпись-ссылку «упак. ▾» открывает меню способа приёмки, а не правку ячейки (правка заменила бы ячейку и закрыла меню).
        if (e.PointerPressedEventArgs.Source is Avalonia.Controls.Control { Tag: "rcv-packlink" })
            return;
        // 2026-11-09: то же для кнопки «Найти в интернете» (нажатие может прийти от значка внутри кнопки — смотрим вверх по дереву до ячейки).
        for (var v = e.PointerPressedEventArgs.Source as Avalonia.Visual; v is not null && v is not Avalonia.Controls.DataGridCell; v = v.GetVisualParent())
            if (v is Avalonia.Controls.Control { Tag: "rcv-netlink" })
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
                System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        var line = new NurMarketKassa.Models.ReceivingLineVm
        {
            Source = NurMarketKassa.Models.ReceivingSource.Unknown,
            Unit = "шт",
            Quantity = quantity,
        };
        ReceivingGrid.CommitEdit();
        line.EnableAutoSale(); // 2026-10-28: новый товар вручную — цена продажи сразу по наценке, как только введена закупка
        _viewModel.ReceivingLines.Insert(0, line); // 2026-10-24, ТЗ «Склад» (Б1): новая строка сверху
        ReceivingQuantityBox.Text = "1";
        RefreshReceivingSummary();

        Dispatcher.UIThread.Post(() => BeginReceivingEdit(line, RcvColName), DispatcherPriority.Background);
    }

    private void ReceivingGrid_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        // 2026-10-28 (ДЕНЬГИ): пока ячейка правилась, в ней жил сырой набранный текст («100.», «100,6»); по выходу — нормальный показ («100,60»).
        if (e.Row?.DataContext is NurMarketKassa.Models.ReceivingLineVm line)
        {
            line.CommitText();
            // 2026-11-09: пустое название нельзя — у товара склада возвращается прежнее (у нового проведение попросит ввести).
            if (ReferenceEquals(e.Column, RcvColName) && line.CommitName())
                _viewModel.ShowReceivingInfo(Tr.T("Название не может быть пустым — вернули прежнее.", "Аты бош болбошу керек — мурункусу кайтарылды.",
                    "The name cannot be empty — the previous one was restored.", "Ad boş olamaz — önceki geri yüklendi.", "Nom bo'sh bo'lmasligi kerak — avvalgisi qaytarildi."));
        }
        RefreshReceivingSummary();
    }

    /// <summary>2026-11-09: «Найти в интернете» у строки «Неизвестно» — поиск названия по штрихкоду ТОЛЬКО по этому нажатию (ReceivingInternetLookup, один запрос на нажатие).
    /// Нашлось — название и метка «Из интернета»; не нашлось/нет связи — строка остаётся «Неизвестно».</summary>
    private async void ReceivingInternetSearch_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as Avalonia.Controls.Control)?.DataContext is not NurMarketKassa.Models.ReceivingLineVm line)
            return;
        try
        {
            var result = await ReceivingInternetLookup.ApplyAsync(line).ConfigureAwait(true);
            var text = ReceivingInternetLookup.MessageFor(result);
            if (text.Length > 0)
                _viewModel.ShowReceivingInfo(text);
            RefreshReceivingSummary();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: «Найти в интернете» — {ex.Message}", "WARNING");
        }
    }

    private async void ReceivingPickFromList_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new BundleItemPickerDialog(CatalogCacheService.Products, []);
        var confirmed = await dialog.ShowDialog<bool?>(this);
        if (confirmed != true || dialog.Result.Count == 0)
            return;

        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;

        ReceivingLineVm? lastAdded = null;
        foreach (var product in dialog.Result)
            lastAdded = _viewModel.AddReceivingProduct(product, quantity);

        ReceivingQuantityBox.Text = "1";
        RefreshReceivingSummary();
        // 2026-10-24: одну строку — сразу в «Принято»; несколько — как раньше, в поле скана.
        if (dialog.Result.Count == 1 && lastAdded is not null)
            OnReceivingLineAdded(lastAdded);
        else
            ReceivingScanBox.Focus();
    }

    /// <summary>2026-10-03: «Оприходовать» при открытой правке ячейки — сначала дописываем правку (Click идёт раньше
    /// команды): иначе проведённая строка вернулась бы в таблицу при уходе с вкладки и её провели бы второй раз.</summary>
    private void ReceivingCommit_Click(object? sender, RoutedEventArgs e) => ReceivingGrid.CommitEdit();

    private void ReceivingRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (ReceivingGrid.SelectedItem is NurMarketKassa.Models.ReceivingLineVm line)
        {
            // 2026-10-03, владелец: «в приёмке нажал „Убрать“, перешёл на другие вкладки — убранные товары
            // вернулись». Воспроизведено: строка была в режиме правки ячейки (нажали «Принято»/цену); при уходе
            // с вкладки таблица дописывала незаконченную правку и возвращала строку. Сначала отменяем правку.
            ReceivingGrid.CancelEdit();
            _viewModel.ReceivingLines.Remove(line);
            RefreshReceivingSummary();
        }
    }

    /// <summary>Итог под накладной: позиции, единицы, сумма закупки и сколько товаров будет
    /// создано. Нужен до проведения — после него правки уже не отменить.</summary>
    private void RefreshReceivingSummary()
    {
        // 2026-10-29, спека Fable: итоговая панель — Позиций · Штук · Сумма закупки · Выручка · Прибыль · Средняя наценка · флаги. Подписи (5 языков) — тут же.
        var t = ReceivingPricing.Totals(_viewModel.ReceivingLines);
        var som = " " + Tr.T("сом", "сом", "som", "som", "so'm");
        SumPosCap.Text = Tr.T("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar");
        SumQtyCap.Text = Tr.T("Штук", "Даана", "Pieces", "Adet", "Dona");
        SumPurchaseCap.Text = Tr.T("Сумма закупки", "Сатып алуу суммасы", "Purchase total", "Alış tutarı", "Xarid summasi");
        SumSaleCap.Text = Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum");
        SumProfitCap.Text = Tr.T("Прибыль", "Пайда", "Profit", "Kâr", "Foyda");
        SumMarkupCap.Text = Tr.T("Средняя наценка", "Орточо кошумча", "Average markup", "Ortalama kâr", "O'rtacha ustama");
        SumPos.Text = t.Positions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SumQty.Text = t.Quantity.ToString("0.###", RuCulture);
        SumPurchase.Text = MoneyText.Amount(t.Purchase) + som;
        SumSale.Text = MoneyText.Amount(t.Sale) + som;
        SumProfit.Text = (t.Profit >= 0 ? "+" : "−") + MoneyText.Amount(Math.Abs(t.Profit)) + som;
        SumProfit.Classes.Set("ok", t.Profit >= 0);
        SumProfit.Classes.Set("neg", t.Profit < 0);
        SumMarkup.Text = t.AvgMarkup is { } avg ? avg.ToString("0.#", RuCulture) + " %" : "—";

        SumFlagLowBox.IsVisible = t.LowCount > 0;
        // Короткие флаги (рядом с KPI и кнопкой «Провести» места мало); полный смысл — в подсказке строки («⚠ маржа 8 % — мало», «⛔ ниже закупки»).
        SumFlagLow.Text = Tr.T($"⚠ {t.LowCount} с малой маржой", $"⚠ аз маржа: {t.LowCount}", $"⚠ {t.LowCount} low margin", $"⚠ düşük marj: {t.LowCount}", $"⚠ kam marja: {t.LowCount}");
        ToolTip.SetTip(SumFlagLowBox, Tr.T("Строки, где маржа меньше 10 %. Проводить можно.", "Маржасы 10 %дан аз саптар. Өткөрүүгө болот.", "Lines with a margin below 10%. You can still post.", "Marjı %10'un altındaki satırlar. Kaydedebilirsiniz.", "Marjasi 10 % dan kam qatorlar. O'tkazish mumkin."));
        SumFlagBelowBox.IsVisible = t.BelowCount > 0;
        SumFlagBelow.Text = Tr.T($"⛔ {t.BelowCount} ниже закупки", $"⛔ сатып алуудан төмөн: {t.BelowCount}", $"⛔ {t.BelowCount} below cost", $"⛔ alışın altında: {t.BelowCount}", $"⛔ xariddan past: {t.BelowCount}");
        ToolTip.SetTip(SumFlagBelowBox, Tr.T("Цена продажи ниже закупки — «Провести» спросит подтверждение.", "Сатуу баасы сатып алуудан төмөн — «Өткөрүү» ырастоо суранат.", "The sale price is below cost — “Post” will ask for confirmation.", "Satış fiyatı alışın altında — «Kaydet» onay isteyecek.", "Sotuv narxi xariddan past — «O'tkazish» tasdiq so'raydi."));
        // 2026-10-24, ТЗ «Склад» (Б0/Б1): «подорожало: N» и кнопка «Обновить цены продажи (N)»; новые товары — тоже мелкой подписью рядом с флагами.
        var extra = new List<string>();
        if (t.NewCount > 0)
            extra.Add(Tr.T("новых товаров", "жаңы товар", "new products", "yeni ürün", "yangi mahsulotlar") + $": {t.NewCount}");
        if (t.RaisedCount > 0)
            extra.Add(Tr.T("подорожало", "кымбаттады", "price went up", "zamlandı", "qimmatlashdi") + $": {t.RaisedCount}");
        RefreshReceivingRaiseButton();
        ReceivingSummaryText.Text = string.Join("  ·  ", extra);
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
        StockTransferService.StatusCancelled => Tr.T("Отменено", "Жокко чыгарылды", "Canceled", "İptal edildi", "Bekor qilindi"),
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

        // 2026-10-21 (пакет 1, п. 108): «кнопка справа», а кнопка «+ Создать перемещение» — слева, в строке фильтров.
        MovementsSummaryText.Text = (rows.Count == 0
            ? Tr.T("Перемещений пока нет. Создайте первое кнопкой «+ Создать перемещение» выше.",
                   "Жылышуулар азырынча жок. Биринчисин жогорудагы «+ Жылдыруу түзүү» баскычы менен түзүңүз.",
                   "No transfers yet. Create the first one with «+ New transfer» above.",
                   "Henüz transfer yok. İlkini yukarıdaki «+ Yeni transfer» düğmesiyle oluşturun.",
                   "Hozircha ko'chirishlar yo'q. Birinchisini yuqoridagi «+ Yangi ko'chirish» tugmasi bilan yarating.")
            : Tr.T("Документов", "Документтер", "Documents", "Belgeler", "Hujjatlar") + $": {rows.Count}")
            // 2026-09-25, по замечанию владельца: перемещение выглядело так, будто двигает остатки.
            // У товара магазина в NurCRM один остаток на весь магазин (поле quantity, без
            // разбивки по складам), перемещать его на сервере не между чем — документ только
            // фиксирует перенос между своими складами/зонами/ячейками и хранится на этой кассе.
            + "   ·   " + Tr.T(
                "Документ о переносе между вашими местами хранения: общий остаток товара в NurCRM он не меняет, журнал хранится на этой кассе.",
                "Сактоо жайларыңыздын ортосундагы жылыштыруу документи: NurCRMдеги товардын жалпы калдыгын өзгөртпөйт, журнал ушул кассада сакталат.",
                "A document for moving goods between your storage locations: it does not change the total stock in NurCRM, and the log is kept on this till.",
                "Depolama yerleriniz arasındaki transfer belgesi: NurCRM'deki toplam stoğu değiştirmez, kayıt defteri bu kasada tutulur.",
                "Saqlash joylaringiz o'rtasidagi ko'chirish hujjati: NurCRMdagi umumiy qoldiqni o'zgartirmaydi, jurnal shu kassada saqlanadi.");
    }

    private static string BuildRouteText(StockTransferService.Transfer t)
    {
        var from = string.IsNullOrWhiteSpace(t.FromPlaceName)
            ? Tr.T("склад не указан", "кампа көрсөтүлгөн эмес", "source not set", "kaynak belirtilmedi", "ombor ko'rsatilmagan")
            : t.FromPlaceName;
        var to = string.IsNullOrWhiteSpace(t.ToPlaceName)
            ? Tr.T("получатель не указан", "алуучу көрсөтүлгөн эмес", "destination not set", "hedef belirtilmedi", "qabul qiluvchi ko'rsatilmagan")
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
            // 2026-10-19, О9: сегмент (выбранный — тёмно-синий), рамка sec-segment — в разметке.
            pill.Classes.Add("sec-seg");
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

    private long _lastCreateTransferTick = -10_000;

    private void CreateTransfer_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-11-01 (контрольный аудит, С2-16): двойной клик не заводит два документа перемещения подряд.
        if (Environment.TickCount64 - _lastCreateTransferTick is >= 0 and < 1500)
            return;
        _lastCreateTransferTick = Environment.TickCount64;
        var id = StockTransferService.Instance.CreateTransfer(
            fromPlaceId: null, toPlaceId: null,
            responsible: App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().CurrentUserDisplayName,
            carrier: null, trackingNumber: null, note: null,
            employee: App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>().CurrentUserDisplayName);

        RefreshTransfers();
        OpenTransferCard(id, deleteIfEmpty: true);
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
                ? Tr.T("Сохранить журнал в Word", "Журналды Word форматында сактоо", "Save the log to Word", "Günlüğü Word olarak kaydet", "Jurnalni Word formatida saqlash")
                : Tr.T("Сохранить журнал в Excel", "Журналды Excel форматында сактоо", "Save the log to Excel", "Günlüğü Excel olarak kaydet", "Jurnalni Excel formatida saqlash"),
            SuggestedFileName = StockTransferExportService.SuggestJournalFileName(ReportShopName.Current, extension), // 2026-11-01 (Ж-16): в программе владельца — название компании NurCRM, не «Market»
            FileTypeChoices = [new FilePickerFileType(toWord ? "Word" : "Excel") { Patterns = [$"*.{extension}"] }],
        });

        // 2026-11-05 (аудит №4, А4-07): на Android нет локального пути — пишем во временный файл и копируем в выбранный (ExportFileBridge).
        var path = file is null ? null : NurMarketKassa.AvaloniaHost.Services.ExportFileBridge.PathFor(file);
        if (file is null || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var search = MovementsSearchBox?.Text?.Trim() ?? "";
            var status = _transferStatusFilter;
            var transfers = StockTransferService.Instance.LoadTransfers(search, status);
            var shop = ReportShopName.Current;

            await Task.Run(() =>
            {
                if (toWord)
                    StockTransferExportService.ExportJournalToWord(path!, transfers, TransferStatusText, shop);
                else
                    StockTransferExportService.ExportJournalToExcel(path!, transfers, TransferStatusText, shop);
            }).ConfigureAwait(true);
            await NurMarketKassa.AvaloniaHost.Services.ExportFileBridge.CompleteAsync(file, path!).ConfigureAwait(true);
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

    private void OpenTransferCard(string transferId, bool deleteIfEmpty = false)
    {
        var owner = this;
        var dialog = new StockTransferDialog(transferId);
        _ = dialog.ShowDialog(owner);
        dialog.Closed += (_, _) =>
        {
            // 2026-10-11 (склад.md, 5): новый документ закрыли пустым — не оставляем черновик в журнале.
            if (deleteIfEmpty && StockTransferService.Instance.DeleteIfEmptyDraft(transferId))
                PosLogger.Log("Перемещение: пустой черновик удалён при закрытии.", "STOCK");
            RefreshTransfers();
        };
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
                        NurMarketKassa.Core.Application.SearchMatch.Matches(search, m.ProductName))
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
            Tr.T("За период", "Мезгил ичинде", "For the period", "Dönem içinde", "Davr ichida")
            + $": {rows.Count} "
            + Tr.T("записей", "жазуу", "records", "kayıt", "yozuv")
            + " · " + Tr.T("продано", "сатылды", "sold", "satılan", "sotildi") + $" {soldTotal:N0}"
            + " · " + Tr.T("списано", "эсептен чыгарылды", "written off", "zayi edilen", "hisobdan chiqarildi") + $" {writeOffTotal:N0}";
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

    // 2026-10-20: проверка фильтров — поиск в «Документах» не искал: поле обновляло только скрытый журнал движения
    // (и подменяло его итогом строку сводки). Теперь — тот список, который на экране.
    private void MovementsFilter_Changed(object? sender, TextChangedEventArgs e)
    {
        if (TransfersDocsRadio?.IsChecked == true)
            RefreshTransfers();
        else
            RefreshMovements();
    }

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
        UpdateSortPickerVisibility(); // 2026-10-30: «Сортировка» списком — только в плитках
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
            BuildKpiCards(0, 0, 0, 0, 0, 0);
            return;
        }

        var units = products.Sum(p => p.Quantity);
        var purchaseValue = products.Sum(p => p.Quantity * p.PurchasePrice);
        var saleValue = products.Sum(p => p.Quantity * ParsePriceValue(p.PriceLine));
        // 2026-10-03, владелец: «„Мало на складе“: 22 в одном отчёте и 11 в другом». Здесь «Заканчивается»
        // считало вместе и малый остаток, и нулевые, а «Аналитика склада» — только малый. Теперь как там:
        // «Заканчивается» — остаток больше 0, но меньше 10; «Нет в наличии» — отдельно.
        var low = products.Count(p => p.IsLowStock && p.Quantity > 0);
        var outOfStock = products.Count(p => p.Quantity <= 0);

        var totals = new List<KpiCardVm>
        {
            new() { Label = Tr.T("Позиций:", "Позициялар:", "Items:", "Kalem:", "Pozitsiyalar:"), Value = products.Count.ToString("N0") },
            new() { Label = Tr.T("Единиц на складе:", "Кампадагы бирдик:", "Units in stock:", "Stoktaki adet:", "Ombordagi birliklar:"), Value = units.ToString("N0") },
            new() { Label = Tr.T("По закупке:", "Сатып алуу баасы боюнча:", "At cost:", "Alış fiyatıyla:", "Xarid narxida:"), Value = $"{purchaseValue:N0} " + Tr.T("сом", "сом", "som", "som", "so'm") },
            new() { Label = Tr.T("По продаже:", "Сатуу баасы боюнча:", "At sale price:", "Satış fiyatıyla:", "Sotuv narxida:"), Value = $"{saleValue:N0} " + Tr.T("сом", "сом", "som", "som", "so'm") },
            new() { Label = Tr.T("Заканчивается:", "Түгөнүп баратат:", "Running low:", "Tükenmek üzere:", "Tugab bormoqda:"), Value = low.ToString("N0") },
            new() { Label = Tr.T("Нет в наличии:", "Калдыкта жок:", "Out of stock:", "Stokta yok:", "Mavjud emas:"), Value = outOfStock.ToString("N0") },
        };
        if (!RevisionShowPurchase)
            totals.RemoveAt(2); // 2026-10-30: «По закупке» — сумма закупки склада, только по праву
        WarehouseTotalsPanel.ItemsSource = totals;
        // 2026-10-06: те же итоги — плитками над таблицей (WarehouseWindow.Redesign.cs).
        BuildKpiCards(products.Count, units, purchaseValue, saleValue, low, outOfStock);
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
                // 2026-10-19, О9: пункт списка без рамки (было — серая кнопка на каждую причину).
                Classes = { "list-item" },
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

}
