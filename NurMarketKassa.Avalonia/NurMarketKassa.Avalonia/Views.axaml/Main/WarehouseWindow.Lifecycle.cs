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

// 2026-11-06: В15г — часть класса WarehouseWindow (Lifecycle): перенесено из WarehouseWindow.axaml.cs без изменений логики.
public partial class WarehouseWindow
{
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
        // 2026-11-05 (аудит №3, Д-6): акт списания, оформленный из другого окна (удаление товара, «± Остаток»), сразу виден на вкладке «Списание».
        Action onWriteOffAppended = () => Dispatcher.UIThread.Post(() => _viewModel.ReloadWriteOffHistory());
        WriteOffHistoryStore.Appended += onWriteOffAppended;
        Closed += (_, _) => WriteOffHistoryStore.Appended -= onWriteOffAppended;
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
            // 2026-10-30, владелец: сортировка по клику на заголовок столбца (WarehouseWindow.Sort.cs).
            InitProductSort();
            // 2026-10-25, ТЗ «оприходование в фоне» (Q2): связь окна с очередью приходов (WarehouseWindow.ReceivingBg.cs).
            InitReceivingBackground();
            // 2026-10-20, этап П3: «В долг» — срок оплаты поставщику (WarehouseWindow.SupplierDebt.cs).
            InitSupplierDebtTerm();
            RefreshReceivingSummary();
            // 2026-11-01 (контрольный аудит, С2-32): по умолчанию «В долг» (было «Оплачено сразу» — деньги по ошибке уходили из кассы расходов); «Оплачено сразу» кассир выбирает явно, как в «Закупках».
            ReceivingDebtRadio.IsChecked = true;
            _viewModel.ReceivingPaidNow = false;
            _ = _viewModel.LoadReceivingSuppliersAsync();
            // 2026-10-24, пакет Р2: код извне идёт в открытую ревизию (строку-дубль в старой таблице больше не создаём).
            if (!string.IsNullOrWhiteSpace(InitialBarcode))
                RevisionExternalScan(InitialBarcode.Trim());
        }
        catch (Exception ex)
        {
            var loadFail = ServerOutageMonitor.FriendlyError(ex); // 2026-11-05 (С2-38): человеческий текст вместо сырого ex.Message
            PosMessageBox.Show(this, Tr.T($"Не удалось загрузить каталог: {loadFail}", $"Каталогду жүктөө мүмкүн болгон жок: {loadFail}", $"Could not load the catalog: {loadFail}", $"Katalog yüklenemedi: {loadFail}", $"Katalogni yuklab bo'lmadi: {loadFail}"), Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"),
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
            // 2026-10-30 (15 тыс. товаров): сначала лента изменений (секунды; общий путь программы — без второй параллельной полной загрузки),
            // таблица перестраивается сразу; полная сверка всего каталога (155 страниц, ~3 мин) — в фоне, если её не было 10 мин, с «Загружено N из M».
            // 2026-11-07 («при каждом изменении товара весь склад загружается заново»): «Обновить» — сначала быстрый путь (лента + последние изменённые, секунды), как кнопка в кассе;
            // нельзя (нет связи/ленты, идёт полная загрузка) — прежний общий путь (лента, а полная загрузка — если ленты нет).
            CatalogSyncResult? quick = null;
            try { quick = await (App.AppHost?.Services.GetService<ICatalogCacheService>()?.QuickRefreshAsync() ?? Task.FromResult<CatalogSyncResult?>(null)).ConfigureAwait(true); }
            catch (Exception qex) when (qex is not OperationCanceledException and not OutOfMemoryException) { PosLogger.Log($"Склад: быстрое обновление не вышло ({qex.Message}).", "CATALOG"); }
            var result = quick ?? await CatalogCacheService.SyncCatalogFullAsync().ConfigureAwait(true);
            if (result.Success)
                CatalogCacheService.NotifyCatalogChanged();
            else
                PosLogger.Log($"Склад: обновление с сервера не удалось ({result.ErrorMessage}) — показан локальный каталог.", "CATALOG");
            await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            RefreshWarehouseTotals();
            if (result.Success && CatalogCacheService.RequestBackgroundFullReload?.Invoke() is { } fullReload)
                _ = TrackBackgroundFullReloadAsync(fullReload);
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

    /// <summary>«Этикетка…» — прежнее окно с предпросмотром, копиями и выбором принтера (право «Печать штрих-кодов» проверяется).</summary>
    private async void PrintLabel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        await QuickLabelPrint.OpenWindowAsync(this, product).ConfigureAwait(true);
    }

    /// <summary>2026-11-01: значок «🏷» и пункт меню «Печать этикетки» — этикетка сразу на принтер этикеток, без окна (QuickLabelPrint).</summary>
    private async void QuickLabel_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;

        await QuickLabelPrint.RunAsync(this, product).ConfigureAwait(true);
    }

    private async void PrintPriceTag_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogProductTileVm product })
            return;
        if (!BarcodePrintAccess.Ensure(this)) // 2026-11-01 (П-10)
            return;
        if (!EnsurePriceTagEditorUnlocked())
            return;

        var dialog = new PriceTagPrintDialog(product);
        await dialog.ShowDialog(this).ConfigureAwait(true);
    }

    private void BulkImport_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-11-05 (аудит №3, С2-28): мастер загрузки создаёт и правит сотни товаров — по праву «Склад: редактирование», как остальные правки склада.
        if (!EnsureCanEditProducts())
            return;
        BulkImportWizardWindow.Open(this);
    }

    private async void BulkPriceTag_Click(object? sender, RoutedEventArgs e)
    {
        if (!BarcodePrintAccess.Ensure(this)) // 2026-11-01 (П-10)
            return;
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

}
