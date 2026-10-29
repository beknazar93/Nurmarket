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
        if (ReferenceEquals(e.Column, RcvColBarcode) && line.Source != NurMarketKassa.Models.ReceivingSource.Unknown)
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
            line.CommitText();
        RefreshReceivingSummary();
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

    private void CreateTransfer_Click(object? sender, RoutedEventArgs e)
    {
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
            SuggestedFileName = StockTransferExportService.SuggestJournalFileName(UserPreferences.Instance.StoreName, extension),
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

        WarehouseTotalsPanel.ItemsSource = new List<KpiCardVm>
        {
            new() { Label = Tr.T("Позиций:", "Позициялар:", "Items:", "Kalem:", "Pozitsiyalar:"), Value = products.Count.ToString("N0") },
            new() { Label = Tr.T("Единиц на складе:", "Кампадагы бирдик:", "Units in stock:", "Stoktaki adet:", "Ombordagi birliklar:"), Value = units.ToString("N0") },
            new() { Label = Tr.T("По закупке:", "Сатып алуу баасы боюнча:", "At cost:", "Alış fiyatıyla:", "Xarid narxida:"), Value = $"{purchaseValue:N0} " + Tr.T("сом", "сом", "som", "som", "so'm") },
            new() { Label = Tr.T("По продаже:", "Сатуу баасы боюнча:", "At sale price:", "Satış fiyatıyla:", "Sotuv narxida:"), Value = $"{saleValue:N0} " + Tr.T("сом", "сом", "som", "som", "so'm") },
            new() { Label = Tr.T("Заканчивается:", "Түгөнүп баратат:", "Running low:", "Tükenmek üzere:", "Tugab bormoqda:"), Value = low.ToString("N0") },
            new() { Label = Tr.T("Нет в наличии:", "Калдыкта жок:", "Out of stock:", "Stokta yok:", "Mavjud emas:"), Value = outOfStock.ToString("N0") },
        };
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
            if (_bulkTab != null)
                _bulkTab.IsVisible = false;
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
            // 2026-10-24, ТЗ «Склад» (Б1): строка встаёт сверху, курсор — в «Принято» (WarehouseWindow.ReceivingScan.cs).
            _viewModel.ReceivingLineAdded += OnReceivingLineAdded;
            // 2026-10-06 (О-70): приёмка одежды по размерам — см. WarehouseWindow.Variants.cs.
            InitVariantReceiving();
            // 2026-10-06: редизайн «Товаров» — плитки сводки, фильтр наличия, сроки годности (WarehouseWindow.Redesign.cs).
            InitRedesign();
            // 2026-10-11: замечания тестировщика — крестики, «Очистить список», подсказки не «зависают» (WarehouseWindow.Fixes.cs).
            InitWarehouseFixes();
            // 2026-10-25, ТЗ «оприходование в фоне» (Q2): связь окна с очередью приходов (WarehouseWindow.ReceivingBg.cs).
            InitReceivingBackground();
            // 2026-10-20, этап П3: «В долг» — срок оплаты поставщику (WarehouseWindow.SupplierDebt.cs).
            InitSupplierDebtTerm();
            RefreshReceivingSummary();
            ReceivingPaidRadio.IsChecked = true;
            _viewModel.ReceivingPaidNow = true;
            _ = _viewModel.LoadReceivingSuppliersAsync();
            // 2026-10-24, пакет Р2: код извне идёт в открытую ревизию (строку-дубль в старой таблице больше не создаём).
            if (!string.IsNullOrWhiteSpace(InitialBarcode))
                RevisionExternalScan(InitialBarcode.Trim());
        }
        catch (Exception ex)
        {
            PosMessageBox.Show(this, Tr.T($"Не удалось загрузить каталог: {ex.Message}", $"Каталогду жүктөө мүмкүн болгон жок: {ex.Message}", $"Could not load the catalog: {ex.Message}", $"Katalog yüklenemedi: {ex.Message}", $"Katalogni yuklab bo'lmadi: {ex.Message}"), Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (AnalyticsOnly && UserPreferences.Instance.WarehouseAnalyticsUnlocked)
            WarehouseTabs.SelectedItem = AnalyticsTabItem;
    }

    /// <summary>2026-10-04, клиент: «кнопка "обновить страницу" для склада» — товары и остатки заново с сервера
    /// (то же, что фоновая синхронизация каталога), затем таблица и итоги склада.</summary>
    private async void RefreshWarehouse_Click(object? sender, RoutedEventArgs e)
    {
        RefreshWarehouseButton.IsEnabled = false;
        // 2026-10-11, тестировщик (склад.md, 1): «непонятно — обновляет или завис». Значок крутится, рядом с поиском — «Загружаю… N с».
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var spinAngle = RefreshWarehouseIcon.RenderTransform as Avalonia.Media.RotateTransform;
        var spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        spin.Tick += (_, _) =>
        {
            if (spinAngle is not null)
                spinAngle.Angle = (spinAngle.Angle + 12) % 360;
            ProductCountLoading.Text = Tr.T("Загружаю товары с сервера… ", "Товарларды серверден жүктөп жатам… ", "Loading products from the server… ",
                "Ürünler sunucudan yükleniyor… ", "Mahsulotlar serverdan yuklanmoqda… ") + $"{watch.Elapsed.TotalSeconds:0} " + Tr.T("с", "сек", "s", "sn", "s");
        };
        RefreshWarehouseText.Text = Tr.T("Обновляю…", "Жаңыртып жатам…", "Refreshing…", "Yenileniyor…", "Yangilanmoqda…");
        ProductCountLabel.IsVisible = false;
        ProductCountLoading.IsVisible = true;
        spin.Start();
        try
        {
            var result = await CatalogCacheService.SyncCatalogFullAsync().ConfigureAwait(true);
            if (result.Success)
                CatalogCacheService.NotifyCatalogChanged();
            else
                PosLogger.Log($"Склад: обновление с сервера не удалось ({result.ErrorMessage}) — показан локальный каталог.", "CATALOG");
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            RefreshWarehouseTotals();
            // 2026-10-06: и сроки годности — заново с сервера.
            _ = RefreshExpiryAsync(force: true);
            // Удачное обновление видно по таблице — окно-сообщение только если сервер не ответил.
            if (!result.Success)
                App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(
                    Tr.T("Сервер не ответил — показаны данные этого компьютера.", "Сервер жооп берген жок — бул компьютердеги маалыматтар көрсөтүлдү.", "The server did not respond — showing this computer's data.", "Sunucu yanıt vermedi — bu bilgisayardaki veriler gösteriliyor.", "Server javob bermadi — shu kompyuterdagi ma'lumotlar ko'rsatildi."),
                    isWarning: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: обновление упало: {ex.Message}", "CATALOG");
        }
        finally
        {
            spin.Stop();
            if (spinAngle is not null)
                spinAngle.Angle = 0;
            RefreshWarehouseText.Text = this.TryFindResource("warehouse.refresh", out var refreshLabel) && refreshLabel is string label
                ? label
                : Tr.T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash");
            ProductCountLoading.IsVisible = false;
            ProductCountLabel.IsVisible = true;
            RefreshWarehouseButton.IsEnabled = true;
        }
    }

    public void FocusWithBarcode(string? barcode)
    {
        if (!string.IsNullOrWhiteSpace(barcode))
            RevisionExternalScan(barcode.Trim());
        Activate();
        Focus();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _stockAbcCts?.Cancel();
        _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        // 2026-10-24, пакет Р2: подсчёт ревизии сохраняется черновиком, замок «открыта в другой программе» снимается.
        // 2026-10-27 (утечки): сбой в ревизии не должен оставлять окно в статическом списке «Приходов в фоне» и подписанным на очередь.
        try { RevisionOnWindowClosed(); }
        finally { ReceivingBackgroundOnClosed(); }
        // 2026-10-23: список «Массового добавления» не теряется — последний вид сохраняется черновиком.
        _bulk.SaveDraftNow();
    }

    /// <summary>Сканер штрих-кодов — это просто клавиатура: если фокус завис в поле поиска
    /// (кассир кликнул туда, а потом сразу сканирует, не кликнув мимо), символы штрих-кода
    /// печатаются как текст в это поле вместо перехвата сканером — Window_KeyDown ниже
    /// специально пропускает обработку, пока фокус в TextBox. Esc — быстрый, заметный способ
    /// вернуть фокус на окно самому, не переключая вкладки туда-обратно.</summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // 2026-10-21: Enter в поиске «Товаров», ревизии и списания — найти по коду, как касса (склад не находит товар
        // по скану): сканер пишет код в поле и жмёт Enter, а здесь Enter раньше ничего не делал.
        if (e.Key == Key.Enter && sender is TextBox enterBox)
        {
            HandleSearchEnter(enterBox, e);
            return;
        }

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
        // 2026-10-23: «Массовое добавление» — тоже перехват (штрихкод не должен попасть в «Название» или цену); своего поля скана там нет.
        var inBulk = _bulkTab != null && ReferenceEquals(WarehouseTabs.SelectedItem, _bulkTab);
        // 2026-10-24, пакет Р2: на «Ревизии» перехват тоже — иначе штрихкод печатался бы в поле «Факт». Поле «Скан / поиск» ловит скан само (Enter).
        var inRevision = ReferenceEquals(WarehouseTabs.SelectedItem, RevisionTabItem);
        if (inRevision)
        {
            if (!RevisionNeedsFieldScanIntercept(box))
                return;
        }
        else if (!inBulk && (!ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem) || ReferenceEquals(box, ReceivingScanBox)))
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
    private ulong? _fieldScanLastRaw; // 2026-10-24: метка времени нажатия (не обработки) — при подтормаживании окна пачка клавиш не выглядит «быстрой»
    private int _fieldScanCaret;
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
        // 2026-10-24, владелец: «в полях вводится только 2 символа и перестаёт» — паузу меряем по времени нажатия клавиши
        // (как в AvaloniaKeyboardWedgeBarcodeService), а не по времени обработки: клавиши в очереди идут пачкой.
        var raw = AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp();
        var sinceLast = raw is { } rawTs && _fieldScanLastRaw is { } lastRawTs && rawTs >= lastRawTs
            ? (long)Math.Min(rawTs - lastRawTs, long.MaxValue)
            : now - _fieldScanLastTick;
        var sameBurst = _fieldScanBuffer.Length > 0 && ReferenceEquals(box, _fieldScanBox);
        var fast = sameBurst && sinceLast is >= 0 and <= FieldScanIntervalMs;
        _fieldScanLastTick = now;
        _fieldScanLastRaw = raw;

        if (e.Key == Key.Enter)
        {
            if (sameBurst && sinceLast is >= 0 and <= FieldScanEnterMs
                && _fieldScanBuffer.Length >= 4 && _fieldScanFastRun >= 2)
            {
                e.Handled = true;
                var code = _fieldScanBuffer;
                // Первые символы скана успели напечататься в поле — возвращаем его как было.
                box.Text = _fieldScanSnapshot;
                box.CaretIndex = Math.Min(_fieldScanCaret, box.Text?.Length ?? 0);
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
            _fieldScanCaret = box.CaretIndex;
        }

        // 2026-10-24: символы в поле не глушим (раньше с 3-го быстрого e.Handled = true — в Windows символ не доходил до поля);
        // скан распознаётся только по Enter выше, и тогда поле возвращается к тексту до серии.
        _fieldScanBuffer += ch;
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

    /// <summary>2026-10-04, Android: камера аппарата как сканер — код приходит как скан USB-сканера (OnBarcodeScanned).</summary>
    private async void CameraScanButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await NurMarketKassa.AvaloniaHost.Services.CameraScan.ScanIntoKassaAsync().ConfigureAwait(true);

    private void OnBarcodeScanned(string barcode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 2026-10-26 (приёмка, Д-1): над складом открыто окно-диалог (карточка товара и т.п.) — скан принадлежит ему, склад его
            // не обрабатывает. Раньше скан кода в «Наименовании» карточки ещё и искался на «Товарах» («Товар не найден» после закрытия)
            // или добавлял строку в «Приёмку»/«Массовое добавление». Окна без активации (плашки, IsActive = false) скан не блокируют.
            if (OwnedWindows.Any(w => w.IsVisible && w.IsActive))
                return;

            // Вкладку узнаём по ней самой, а не по номеру. Раньше здесь стояли номера
            // (1 = Ревизия, остальное — Списание), и с появлением «Приёмки» и «Перемещения»
            // скан в приёмке уходил строкой ревизии, а скан в ревизии — в списание.
            var selected = WarehouseTabs.SelectedItem;
            // 2026-10-21: на «Товарах» скан (фокус не в поле) раньше молча пропадал — теперь ищет товар в списке.
            if (ReferenceEquals(selected, ProductsTabItem))
            {
                _ = FindScannedInProductsAsync(barcode);
                return;
            }

            if (ReferenceEquals(selected, ReceivingTabItem))
            {
                // Поле приёмки само ловит скан по Enter — второй раз его не добавляем.
                if (!ReceivingScanBox.IsFocused)
                    _ = ScanIntoReceivingAsync(barcode);
                return;
            }

            if (_bulkTab != null && ReferenceEquals(selected, _bulkTab))
            {
                _ = _bulk.ScanAsync(barcode);
                return;
            }

            if (ReferenceEquals(selected, RevisionTabItem))
            {
                RevisionOnScanned(barcode);
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

    private void BulkImport_Click(object? sender, RoutedEventArgs e) => BulkImportWizardWindow.Open(this);

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
    // 2026-10-27 (аудит редакторов, Б-2): проверка покупки — одна на все окна (PaidFeatureGate), здесь только вход из Склада.
    private bool EnsureLabelEditorUnlocked() => PaidFeatureGate.EnsureUnlocked(PaidFeature.LabelEditor, this);

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
                "Эми бир товар үчүн ценник басып чыгаруу жеткиликтүү: Склад → Товарлар → товар карточкасындагы «💲 Ценник» баскычы.", "Price tag printing for a single product is now available: Warehouse → Products → the “💲 Price tag” button on the product card.", "Artık tek bir ürün için fiyat etiketi yazdırılabilir: Depo → Ürünler → ürün kartındaki «💲 Fiyat etiketi» düğmesi.", "Endi bitta mahsulot uchun narx yorlig'ini chop etish mumkin: Ombor → Mahsulotlar → mahsulot kartochkasidagi «💲 Narx yorlig'i» tugmasi."),
            PosAlertKind.Success);
        return true;
    }

    /// <summary>Массовая печать ценников — отдельная от EnsurePriceTagEditorUnlocked доп. услуга
    /// (2026-09-04, по явной просьбе пользователя: активация одной не должна включать другую),
    /// хотя серийник и диалог активации те же самые (единый мастер-ключ для всех доп. услуг).</summary>
    private bool EnsureBulkPriceTagUnlocked()
    {
        // 2026-10-27 (аудит редакторов, Б-2): проверка покупки — общая (PaidFeatureGate); здесь только обновление видимости кнопок после покупки.
        var wasUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
        var ok = PaidFeatureGate.EnsureUnlocked(PaidFeature.BulkPriceTag, this);
        if (ok && !wasUnlocked)
            RefreshPaidFeatureVisibility();
        return ok;
    }

    private async void AddProduct_Click(object? sender, RoutedEventArgs e)
    {
        var catalogApi = App.AppHost?.Services.GetService<ICatalogApiService>();
        var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
        if (catalogApi is null)
        {
            prompts?.ShowToast(Tr.T("Добавление товара недоступно в этом режиме.", "Бул режимде товар кошууга болбойт.", "Adding products isn't available in this mode.", "Bu modda ürün eklenemez.", "Bu rejimda mahsulot qo'shib bo'lmaydi."), isWarning: true);
            return;
        }

        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ProductEditDialog(catalogApi, null);
        var saved = await dialog.ShowDialog<bool>(this).ConfigureAwait(true);
        if (saved)
        {
            SuccessToast.Show(Tr.T("Товар сохранён.", "Товар сакталды.", "Product saved.", "Ürün kaydedildi.", "Mahsulot saqlandi."));
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
            prompts?.ShowToast(Tr.T("Редактирование товара недоступно в этом режиме.", "Бул режимде товарды түзөтүүгө болбойт.", "Editing products isn't available in this mode.", "Bu modda ürün düzenlenemez.", "Bu rejimda mahsulotni tahrirlab bo'lmaydi."), isWarning: true);
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
            SuccessToast.Show(Tr.T("Товар сохранён.", "Товар сакталды.", "Product saved.", "Ürün kaydedildi.", "Mahsulot saqlandi."));
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
            prompts?.ShowToast(Tr.T("Удаление товара недоступно в этом режиме.", "Бул режимде товарды өчүрүүгө болбойт.", "Deleting products isn't available in this mode.", "Bu modda ürün silinemez.", "Bu rejimda mahsulotni o'chirib bo'lmaydi."), isWarning: true);
            return;
        }

        var confirmed = PosConfirmDialog.Show(
            this,
            Tr.T("Удалить товар", "Товарды өчүрүү", "Delete product", "Ürünü sil", "Mahsulotni o'chirish"),
            Tr.T($"Удалить «{product.Title}»? Товар исчезнет из каталога NurCRM и из кассы. Это действие нельзя отменить.",
                 $"«{product.Title}» өчүрүлсүнбү? Товар NurCRM каталогунан жана кассадан жоголот. Бул аракетти артка кайтарууга болбойт.", $"Delete “{product.Title}”? The product will be removed from the NurCRM catalog and from the till. This cannot be undone.", $"«{product.Title}» silinsin mi? Ürün NurCRM kataloğundan ve kasadan kaldırılacak. Bu işlem geri alınamaz.", $"«{product.Title}» o'chirilsinmi? Mahsulot NurCRM katalogidan va kassadan yo'qoladi. Bu amalni bekor qilib bo'lmaydi."),
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
                Tr.T("Удаление товара со склада", "Товарды кампадан өчүрүү", "Delete a product from the warehouse", "Depodan ürün silme", "Mahsulotni ombordan o'chirish"),
                Tr.T($"Введите свой код доступа, чтобы удалить «{product.Title}» со склада.",
                    $"«{product.Title}» товарын кампадан өчүрүү үчүн жеке кодуңузду киргизиңиз.",
                    $"Enter your access code to delete \"{product.Title}\" from the warehouse.",
                    $"«{product.Title}» ürününü depodan silmek için erişim kodunuzu girin.",
                    $"«{product.Title}» mahsulotini ombordan o'chirish uchun kirish kodingizni kiriting."),
                entered => EmployeeAccessGate.TryValidate(EmployeeAccessGate.WarehouseDelete, entered)).ConfigureAwait(true))
            return;

        try
        {
            // 2026-09-09: офлайн (обычный или автономный режим) — удаляем прямо из локальной
            // SQLite, минуя ICatalogApiService.DeleteProductAsync (реальный HTTP к NurCRM).
            if (OfflineModeHelper.UseLocalOperations)
            {
                LocalProductEditor.DeleteLocally(product.Id);
                SuccessToast.Show(Tr.T("Товар удалён.", "Товар өчүрүлдү.", "Product deleted.", "Ürün silindi.", "Mahsulot o'chirildi."));
                await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
                return;
            }

            var deleted = await catalogApi.DeleteProductAsync(product.Id).ConfigureAwait(true);
            if (!deleted)
            {
                prompts?.ShowToast(Tr.T("Не удалось удалить товар: сервер не принял запрос на удаление.", "Товарды өчүрүү мүмкүн болгон жок: сервер өчүрүү сурамын кабыл алган жок.", "Could not delete the product: the server rejected the delete request.", "Ürün silinemedi: sunucu silme isteğini kabul etmedi.", "Mahsulotni o'chirib bo'lmadi: server o'chirish so'rovini qabul qilmadi."), isWarning: true);
                return;
            }

            SuccessToast.Show(Tr.T("Товар удалён.", "Товар өчүрүлдү.", "Product deleted.", "Ürün silindi.", "Mahsulot o'chirildi."));
            await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            prompts?.ShowToast(Tr.T($"Не удалось удалить товар: {ex.Message}", $"Товарды өчүрүү мүмкүн болгон жок: {ex.Message}", $"Could not delete the product: {ex.Message}", $"Ürün silinemedi: {ex.Message}", $"Mahsulotni o'chirib bo'lmadi: {ex.Message}"), isWarning: true);
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
            prompts?.ShowToast(Tr.T("Загрузка фото недоступна в этом режиме.", "Бул режимде сүрөт жүктөөгө болбойт.", "Photo upload isn't available in this mode.", "Bu modda fotoğraf yüklenemez.", "Bu rejimda rasm yuklab bo'lmaydi."), isWarning: true);
            return;
        }

        var filePath = await photoPicker.PickProductPhotoAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        // 2026-10-27 (расследование витрины NBS): перед загрузкой — предпросмотр и явный ответ «Сделать главным / Добавить в галерею»
        // (раньше фото сразу уходило главным, и плохой кадр нельзя было отменить).
        var choice = await ConfirmProductPhotoAsync(product, filePath).ConfigureAwait(true);
        if (choice == ProductPhotoChoice.Cancel)
            return;
        var asPrimary = choice == ProductPhotoChoice.Primary;

        try
        {
            var uploadedUrl = await catalogApi
                .UploadProductImageAsync(product.Id, filePath, isPrimary: asPrimary)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(uploadedUrl))
            {
                prompts?.ShowToast(Tr.T("Не удалось загрузить фото.", "Сүрөттү жүктөө мүмкүн болгон жок.", "Could not upload the photo.", "Fotoğraf yüklenemedi.", "Rasmni yuklab bo'lmadi."), isWarning: true);
                return;
            }

            // Show the picked file immediately — same bytes just landed on the server,
            // no need to round-trip a redownload through the thumbnail cache.
            if (asPrimary)
                product.ProductImagePath = filePath;
            App.AppHost?.Services.GetService<MySqlAuditService>()?
                .LogEvent("catalog", "product_photo_upload", new { productId = product.Id });
            SuccessToast.Show(asPrimary
                ? Tr.T("Фото товара загружено.", "Товардын сүрөтү жүктөлдү.", "Product photo uploaded.", "Ürün fotoğrafı yüklendi.", "Mahsulot rasmi yuklandi.")
                : Tr.T("Фото добавлено в галерею товара.", "Сүрөт товардын галереясына кошулду.", "Photo added to the product gallery.", "Fotoğraf ürün galerisine eklendi.", "Rasm mahsulot galereyasiga qo'shildi."));
        }
        catch (ApiException ex)
        {
            prompts?.ShowToast(Tr.T($"Загрузка фото: {ex.Message}", $"Сүрөт жүктөө: {ex.Message}", $"Photo upload: {ex.Message}", $"Fotoğraf yükleme: {ex.Message}", $"Rasm yuklash: {ex.Message}"), isWarning: true);
        }
        catch (HttpRequestException)
        {
            prompts?.ShowToast(Tr.T("Не удалось загрузить фото — нет сети.", "Сүрөттү жүктөө мүмкүн болгон жок — тармак жок.", "Could not upload the photo — no network.", "Fotoğraf yüklenemedi — ağ bağlantısı yok.", "Rasmni yuklab bo'lmadi — tarmoq yo'q."), isWarning: true);
        }
    }

    private enum ProductPhotoChoice { Cancel, Primary, Gallery }

    /// <summary>2026-10-27: окно «Фото товара» перед загрузкой — крупный предпросмотр выбранного файла. У товара ещё нет фото — «Загрузить фото»
    /// (станет главным); есть — «Сделать главным» (по умолчанию, как было) или «Добавить в галерею» (главное остаётся прежним). Esc и ✕ — отмена.</summary>
    private async System.Threading.Tasks.Task<ProductPhotoChoice> ConfirmProductPhotoAsync(CatalogProductTileVm product, string filePath)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<ProductPhotoChoice>();
        var dialog = new Window { Title = Title };
        SectionCards.AsModal(dialog, 640);
        var hasPhoto = !string.IsNullOrWhiteSpace(product.ProductImagePath);
        var preview = new Avalonia.Controls.Image { Height = 280, Stretch = Avalonia.Media.Stretch.Uniform };
        try
        {
            preview.Source = new Avalonia.Media.Imaging.Bitmap(filePath);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Склад: предпросмотр фото не показан ({ex.Message}).", "DEBUG");
        }
        var note = new TextBlock
        {
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13,
            Text = hasPhoto
                ? Tr.T("У товара уже есть фото. «Сделать главным» — новое заменит его на плитке товара и на сайте. «Добавить в галерею» — главным останется прежнее.",
                    "Товарда сүрөт бар. «Негизги кылуу» — жаңысы аны товар плиткасында жана сайтта алмаштырат. «Галереяга кошуу» — негизгиси мурункусу бойдон калат.",
                    "The product already has a photo. “Make main” — the new one replaces it on the tile and on the website. “Add to gallery” — the old one stays main.",
                    "Ürünün zaten bir fotoğrafı var. «Ana yap» — yenisi kartta ve sitede onun yerine geçer. «Galeriye ekle» — ana fotoğraf eskisi kalır.",
                    "Mahsulotda rasm bor. «Asosiy qilish» — yangisi uni kartochkada va saytda almashtiradi. «Galereyaga qo'shish» — asosiysi eskisi qoladi.")
                : Tr.T("Это фото станет главным: его увидят в кассе и на сайте. Проверьте, что на нём сам товар, а не штрихкод.",
                    "Бул сүрөт негизги болот: аны кассада жана сайтта көрүшөт. Анда товардын өзү экенин, штрихкод эмес экенин текшериңиз.",
                    "This photo becomes the main one: it shows in the till and on the website. Check it shows the product itself, not a barcode.",
                    "Bu fotoğraf ana fotoğraf olacak: kasada ve sitede görünür. Fotoğrafta barkodun değil ürünün kendisinin göründüğünden emin olun.",
                    "Bu rasm asosiy bo'ladi: kassada va saytda ko'rinadi. Unda shtrixkod emas, mahsulotning o'zi borligini tekshiring."),
        };
        note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushTextSoft"));
        var content = new StackPanel { Spacing = 10, Children = { preview, note } };
        var cancel = SectionCards.Cancel(Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => { tcs.TrySetResult(ProductPhotoChoice.Cancel); dialog.Close(); };
        var buttons = new List<Button> { cancel };
        Button primary;
        if (hasPhoto)
        {
            var gallery = SectionCards.Secondary(Tr.T("Добавить в галерею", "Галереяга кошуу", "Add to gallery", "Galeriye ekle", "Galereyaga qo'shish"));
            gallery.Click += (_, _) => { tcs.TrySetResult(ProductPhotoChoice.Gallery); dialog.Close(); };
            buttons.Add(gallery);
            primary = SectionCards.Primary(Tr.T("Сделать главным", "Негизги кылуу", "Make main", "Ana yap", "Asosiy qilish"), isDefault: true);
        }
        else
            primary = SectionCards.Primary(Tr.T("Загрузить фото", "Сүрөттү жүктөө", "Upload photo", "Fotoğrafı yükle", "Rasmni yuklash"), isDefault: true);
        primary.Click += (_, _) => { tcs.TrySetResult(ProductPhotoChoice.Primary); dialog.Close(); };
        buttons.Add(primary);
        dialog.Closed += (_, _) => tcs.TrySetResult(ProductPhotoChoice.Cancel);
        var shell = SectionCards.Shell(Tr.T("Фото товара", "Товардын сүрөтү", "Product photo", "Ürün fotoğrafı", "Mahsulot rasmi"), product.Title, content, SectionCards.Buttons(buttons.ToArray()));
        shell.InitialFocus = primary;
        dialog.Content = shell;
        await dialog.ShowDialog(this).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

    #region Аналитика склада/товаров

    /// <summary>Плитка итога под таблицей товаров (RefreshWarehouseTotals).</summary>
    private sealed class KpiCardVm
    {
        public string Label { get; init; } = "";
        public string Value { get; init; } = "";
    }

    private static double ParsePriceValue(string? priceLine) =>
        LocalCartService.ParsePrice(priceLine ?? "");

    /// <summary>Считается локально из уже загруженного каталога. Сам расчёт и разметка — в
    /// WarehouseStockAnalyticsView (2026-09-27): тот же блок стоит в разделе «Аналитика»
    /// программы владельца.</summary>
    private void RefreshAnalytics() => StockAnalyticsView.Refresh();

    #endregion

    #region ABC склада (программа владельца)

    private bool _isOwnerSection;
    private TabItem? _ownerAbcTab;
    private AbcSectionView? _stockAbc;
    private System.Threading.CancellationTokenSource? _stockAbcCts;

    /// <summary>Вкладка «ABC» склада в программе владельца (2026-09-27, «склад у склада ABC»):
    /// один срез — «Склад по стоимости остатка». Стоит там, где в кассе стоит «Аналитика»
    /// (она сама ушла в раздел «Аналитика»). Как и раздел «ABC-анализ», на тарифе «Старт» её нет.</summary>
    private void AddOwnerAbcTab()
    {
        if (!TariffGate.CanUseSalesAnalytics)
            return;

        _stockAbc = new AbcSectionView
        {
            SliceKeys = [AnalyticsReportData.KeyStock],
            ShowSeasonality = false,
            TableMaxHeight = 520,
        };
        // Разбор товара — за последние 30 дней: у среза склада своего периода нет.
        _stockAbc.ProductAnalyticsRequested += name =>
        {
            try
            {
                new ProductAnalyticsWindow(name, DateTime.Today.AddDays(-29), DateTime.Today).Show(this);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Разбор товара не открылся: {ex}", "WARNING");
            }
        };

        var body = new Border
        {
            CornerRadius = new CornerRadius(0, 14, 14, 14),
            Padding = new Thickness(16),
            BoxShadow = Avalonia.Media.BoxShadows.Parse("0 4 12 0 #0D000000"),
            Child = _stockAbc,
        };
        body.Bind(Border.BackgroundProperty, body.GetResourceObservable("BrushPanel"));

        _ownerAbcTab = new TabItem { Header = "ABC", Content = body };
        var index = WarehouseTabs.Items.IndexOf(AnalyticsTabItem);
        WarehouseTabs.Items.Insert(index < 0 ? WarehouseTabs.Items.Count : index, _ownerAbcTab);
    }

    /// <summary>Считается при каждом открытии вкладки: остатки меняются приёмкой, списанием и
    /// продажами, а срез — снимок на сейчас. Расчёт в фоне: на большом каталоге это заметно.</summary>
    private async System.Threading.Tasks.Task RefreshStockAbcAsync()
    {
        if (_stockAbc == null)
            return;

        _stockAbcCts?.Cancel();
        var cts = new System.Threading.CancellationTokenSource();
        _stockAbcCts = cts;
        try
        {
            var slice = await System.Threading.Tasks.Task.Run(() => AnalyticsReportData.BuildStockSlice(), cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;
            _stockAbc.Update(new AnalyticsReportData { AbcSlices = slice is null ? [] : [slice] });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ABC склада не построен: {ex.Message}", "WARNING");
        }
        finally
        {
            if (ReferenceEquals(_stockAbcCts, cts))
                _stockAbcCts = null;
            cts.Dispose();
        }
    }

    #endregion

    #region Window Controls & Dragging

    /// <summary>2026-10-20, память программы владельца: несохранённую приёмку или ревизию (строки живут только в окне)
    /// и проведение документа оболочка не закрывает при уходе из раздела (IOwnerSectionKeepAlive).</summary>
    public bool KeepAliveWhenHidden =>
        _viewModel.IsBusy || _viewModel.ReceivingLines.Count > 0 || RevisionKeepAlive || _bulk.HasUnsaved || _bulk.IsLoading;

    /// <summary>Склад разделом программы владельца (см. <see cref="IOwnerSection"/>): над разделом
    /// уже написано «Склад», поэтому своя шапка «Управление складом» с кнопками «свернуть» и ✕ не
    /// нужна. Подсказка про сканер остаётся — узкой строкой справа над вкладками.</summary>
    public void AsOwnerSection()
    {
        // Аналитика склада — в разделе «Аналитика» (вкладка «Склад»), здесь вместо неё «ABC».
        _isOwnerSection = true;
        RefreshPaidFeatureVisibility();
        AddOwnerAbcTab();

        WindowTitlePanel.IsVisible = false;
        MinimizeWindowButton.IsVisible = false;
        CloseWindowButton.IsVisible = false;

        WindowHeaderBorder.Background = Avalonia.Media.Brushes.Transparent;
        WindowHeaderBorder.BoxShadow = default;
        WindowHeaderBorder.Padding = new Thickness(0);
        WindowHeaderBorder.Margin = new Thickness(0, 0, 0, 8);
        ScanHintBorder.Margin = new Thickness(0);
        RootGrid.Margin = OwnerSectionLayout.Margin;
        // 2026-10-06, ТЗ ч.12, п. 2.6: кнопка «Дубли штрихкодов» — если они есть (WarehouseWindow.Duplicates.cs).
        _ = ShowBarcodeDuplicatesButtonAsync();
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    // 2026-10-23: ✕ окна тоже спрашивает про несохранённый список (раньше закрывал молча; Esc спрашивал).
    private void CloseButton_Click(object? sender, RoutedEventArgs e) => CloseByEscape();

    private void CloseByEscape()
    {
        var receiving = _viewModel.ReceivingLines.Count;
        // 2026-10-24, пакет Р2: подсчёт ревизии сохраняется черновиком сам — окно из-за него не переспрашивает.
        var bulk = _bulk.Rows.Count;
        if (receiving + bulk > 0)
        {
            var parts = new List<string>();
            if (bulk > 0)
                parts.Add(Tr.T($"массовое добавление — {bulk}", $"массалык кошуу — {bulk}", $"bulk add — {bulk}", $"toplu ekleme — {bulk}", $"ommaviy qo'shish — {bulk}"));
            if (receiving > 0)
                parts.Add(Tr.T($"приёмка — {receiving}", $"кабыл алуу — {receiving}", $"receiving — {receiving}", $"mal kabul — {receiving}", $"qabul qilish — {receiving}"));
            var close = PosConfirmDialog.Show(
                this,
                Tr.T("Закрыть склад?", "Кампаны жабасызбы?", "Close the warehouse?", "Depo kapatılsın mı?", "Omborni yopasizmi?"),
                Tr.T("Есть непроведённые строки: ", "Өткөрүлө элек саптар бар: ", "There are unposted lines: ", "İşlenmemiş satırlar var: ", "O'tkazilmagan qatorlar bor: ")
                    + string.Join(", ", parts)
                    + Tr.T(". После закрытия они пропадут.", ". Жабылгандан кийин алар жоголот.", ". They will be lost after closing.", ". Kapattığınızda bunlar kaybolacak.", ". Yopilgandan keyin ular yo'qoladi."),
                confirmText: Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"),
                cancelText: Tr.T("Остаться", "Калуу", "Stay", "Vazgeç", "Qolish"));
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