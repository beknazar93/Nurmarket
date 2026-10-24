using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-23, просьба владельца: «на складе массовая загрузка товаров через сканер тоже сделай». Вкладка «Массовое добавление»:
/// оператор подряд сканирует штрихкоды — известный товар +1, неизвестный — новая строка с курсором в «Название»; «Загрузить всё»
/// создаёт товары и принимает количество сервисом приёмки (BulkScanSession → PurchaseReceivingService.PostAsync). Вкладка собрана
/// кодом (вид — как у соседних вкладок), без Windows-only API: те же файлы собираются для Linux и Android. Сканер — как в приёмке:
/// вне полей ввода его читает общий сервис, внутри полей — перехват быстрого ввода (Window_KeyDown → HandleScanInsideField),
/// поэтому штрихкод не попадает в «Название».</summary>
public partial class WarehouseWindow
{
    private BulkScanSession _bulk = null!;
    private TabItem? _bulkTab;
    private ItemsControl _bulkList = null!;
    private Border _bulkDraftBanner = null!;
    private TextBlock _bulkDraftText = null!;
    private Border _bulkSummaryBox = null!;
    private TextBlock _bulkLastScan = null!;
    /// <summary>2026-10-24 (приёмка, ПР-11): строка, по которой написан «✓ …» в строке последнего скана, — чтобы «−»/«+» обновляли число.</summary>
    private BulkScanRow? _bulkLastScanRow;
    private TextBlock _bulkEmpty = null!;
    private Control _bulkHeader = null!;
    private TextBox _bulkScanBox = null!;
    private readonly ObservableCollection<string> _bulkCategories = new();
    private static readonly string[] BulkUnits = { "шт", "кг", "л", "м", "уп" };

    private static string TrB(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private void InitBulkScan()
    {
        _bulk = new BulkScanSession();
        _bulk.Scanned += OnBulkScanned;
        _bulk.Completed += OnBulkCompleted;
        _bulk.Rows.CollectionChanged += (_, _) =>
        {
            _bulkEmpty.IsVisible = _bulk.Rows.Count == 0;
            _bulkHeader.IsVisible = _bulk.Rows.Count > 0;
            if (_bulk.Rows.Count > 0)
                _bulkDraftBanner.IsVisible = false;
        };

        var body = BuildBulkBody();
        _bulkTab = new TabItem { Header = TrB("Массовое добавление", "Массалык кошуу", "Bulk add", "Toplu ekleme", "Ommaviy qo'shish"), Content = body };
        var index = WarehouseTabs.Items.IndexOf(ReceivingTabItem);
        WarehouseTabs.Items.Insert(index < 0 ? WarehouseTabs.Items.Count : index + 1, _bulkTab);
        WarehouseTabs.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(WarehouseTabs.SelectedItem, _bulkTab))
                OnBulkTabOpened();
        };
    }

    private Control BuildBulkBody()
    {
        var hint = new TextBlock
        {
            Text = TrB("Сканируйте штрихкоды подряд: известный товар — +1 к количеству, незнакомый — новая строка: впишите название и цены. Потом «Загрузить всё».",
                "Штрихкодторду удаа-удаа сканерлеңиз: белгилүү товар — санына +1, тааныш эмес — жаңы сап: атын жана баасын жазыңыз. Анан «Баарын жүктөө».",
                "Scan barcodes one after another: a known product adds +1, an unknown one becomes a new row — enter its name and prices. Then press “Load all”.",
                "Barkodları art arda okutun: bilinen ürün +1 olur, bilinmeyen yeni satır olur — adını ve fiyatlarını yazın. Sonra «Hepsini yükle».",
                "Shtrix-kodlarni ketma-ket skanerlang: tanish mahsulot +1, notanish — yangi qator: nomi va narxini yozing. Keyin «Hammasini yuklash»."),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 0, 0, 6),
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));

        // 2026-10-24 (аудит, круг 8, С-2): ввод кода руками без сканера — как в «Приёмке»: поле «Штрихкод — Enter» и «Выбрать из списка».
        // Скан сканером внутри поля по-прежнему перехватывает Window_KeyDown → HandleScanInsideField (в «Название» и цены он не попадает).
        _bulkScanBox = new TextBox
        {
            Watermark = TrB("Штрихкод — Enter", "Штрихкод — Enter", "Barcode — Enter", "Barkod — Enter", "Shtrix-kod — Enter"),
            MinHeight = 44,
            Width = 320,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _bulkScanBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            var code = _bulkScanBox.Text?.Trim() ?? "";
            if (code.Length == 0)
                return;
            _bulkScanBox.Text = "";
            _ = _bulk.ScanAsync(code);
        };
        var pickFromList = BulkButton(TrB("Выбрать из списка", "Тизмеден тандоо", "Pick from list", "Listeden seç", "Ro'yxatdan tanlash"), primary: false);
        pickFromList.Click += async (_, _) => await BulkPickFromListAsync().ConfigureAwait(true);
        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        inputRow.Children.Add(_bulkScanBox);
        inputRow.Children.Add(pickFromList);

        // Заголовки колонок над строками (те же ширины, что в BuildBulkRow): «Штрихкод · Название · Кол-во».
        _bulkHeader = BuildBulkHeader();
        _bulkHeader.IsVisible = false;

        _bulkLastScan = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 6), IsVisible = false };
        _bulkLastScan.Bind(TextBlock.ForegroundProperty, _bulkLastScan.GetResourceObservable("BrushText"));

        // Черновик: «Продолжить» / «Удалить» (как «Продолжить черновик» у документов).
        _bulkDraftText = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _bulkDraftText.Bind(TextBlock.ForegroundProperty, _bulkDraftText.GetResourceObservable("BrushText"));
        var resume = BulkButton(TrB("Продолжить", "Улантуу", "Continue", "Devam et", "Davom etish"), primary: true);
        resume.Click += (_, _) => BulkResumeDraft();
        var discard = BulkButton(TrB("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), primary: false);
        discard.Click += (_, _) =>
        {
            _bulk.DiscardDraft();
            _bulkDraftBanner.IsVisible = false;
        };
        var draftGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        Grid.SetColumn(_bulkDraftText, 0);
        resume.Margin = new Thickness(8, 0, 0, 0);
        discard.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(resume, 1);
        Grid.SetColumn(discard, 2);
        draftGrid.Children.Add(_bulkDraftText);
        draftGrid.Children.Add(resume);
        draftGrid.Children.Add(discard);
        _bulkDraftBanner = SoftBox(draftGrid);
        _bulkDraftBanner.IsVisible = false;
        _bulkDraftBanner.Margin = new Thickness(0, 0, 0, 8);

        // Итог загрузки: «Создано N, принято M, ошибок K», список ошибок, «Повторить ошибочные».
        var summaryText = new TextBlock { FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        summaryText.Bind(TextBlock.TextProperty, new Binding(nameof(BulkScanSession.SummaryText)) { Source = _bulk });
        summaryText.Bind(TextBlock.ForegroundProperty, summaryText.GetResourceObservable("BrushText"));
        var retry = BulkButton(TrB("Повторить ошибочные", "Катасы барларды кайталоо", "Retry failed", "Hatalıları tekrarla", "Xatolarni qaytarish"), primary: true);
        retry.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanSession.HasFailedRows)) { Source = _bulk });
        retry.Bind(Button.IsEnabledProperty, new Binding(nameof(BulkScanSession.IsLoading)) { Source = _bulk, Converter = Avalonia.Data.Converters.BoolConverters.Not });
        retry.Click += async (_, _) => await _bulk.LoadAsync(onlyFailed: true).ConfigureAwait(true);
        var errorsList = new ItemsControl { ItemsSource = _bulk.Errors };
        errorsList.ItemTemplate = new FuncDataTemplate<string>((s, _) =>
        {
            var t = new TextBlock { Text = "• " + s, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushDanger"));
            return t;
        });
        var errorsScroll = new ScrollViewer { MaxHeight = 90, Content = errorsList, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        var summaryTop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(summaryText, 0);
        retry.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(retry, 1);
        summaryTop.Children.Add(summaryText);
        summaryTop.Children.Add(retry);
        var summaryStack = new StackPanel { Spacing = 6 };
        summaryStack.Children.Add(summaryTop);
        summaryStack.Children.Add(errorsScroll);
        _bulkSummaryBox = SoftBox(summaryStack);
        _bulkSummaryBox.Margin = new Thickness(0, 0, 0, 8);
        _bulkSummaryBox.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanSession.HasSummary)) { Source = _bulk });

        // Список строк.
        _bulkList = new ItemsControl { ItemsSource = _bulk.Rows };
        _bulkList.ItemTemplate = new FuncDataTemplate<BulkScanRow>((row, _) => row is null ? new Control() : BuildBulkRow(row));
        var listScroll = new ScrollViewer
        {
            Content = _bulkList,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        _bulkEmpty = new TextBlock
        {
            Text = TrB("Список пуст. Отсканируйте первый штрихкод.", "Тизме бош. Биринчи штрихкодду сканерлеңиз.",
                "The list is empty. Scan the first barcode.", "Liste boş. İlk barkodu okutun.", "Ro'yxat bo'sh. Birinchi shtrix-kodni skanerlang."),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        _bulkEmpty.Bind(TextBlock.ForegroundProperty, _bulkEmpty.GetResourceObservable("BrushTextSoft"));
        var listArea = new Grid();
        listArea.Children.Add(listScroll);
        listArea.Children.Add(_bulkEmpty);

        // Низ: счётчики, прогресс «12 из 40», «Очистить», «Загрузить всё».
        var stats = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        stats.Bind(TextBlock.TextProperty, new Binding(nameof(BulkScanSession.StatsText)) { Source = _bulk });
        stats.Bind(TextBlock.ForegroundProperty, stats.GetResourceObservable("BrushText"));
        var progress = new ProgressBar { Width = 140, Height = 8, VerticalAlignment = VerticalAlignment.Center, Minimum = 0 };
        progress.Bind(ProgressBar.MaximumProperty, new Binding(nameof(BulkScanSession.ProgressMax)) { Source = _bulk });
        progress.Bind(ProgressBar.ValueProperty, new Binding(nameof(BulkScanSession.ProgressValue)) { Source = _bulk });
        var progressText = new TextBlock { FontSize = 13, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        progressText.Bind(TextBlock.TextProperty, new Binding(nameof(BulkScanSession.ProgressText)) { Source = _bulk });
        progressText.Bind(TextBlock.ForegroundProperty, progressText.GetResourceObservable("BrushText"));
        var progressBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        progressBox.Children.Add(progress);
        progressBox.Children.Add(progressText);
        progressBox.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanSession.IsLoading)) { Source = _bulk });

        var clear = BulkButton(TrB("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"), primary: false);
        clear.Bind(Button.IsEnabledProperty, new Binding(nameof(BulkScanSession.IsLoading)) { Source = _bulk, Converter = Avalonia.Data.Converters.BoolConverters.Not });
        clear.Click += (_, _) => BulkClear();
        var load = BulkButton(TrB("Загрузить всё", "Баарын жүктөө", "Load all", "Hepsini yükle", "Hammasini yuklash"), primary: true);
        load.Bind(Button.IsEnabledProperty, new Binding(nameof(BulkScanSession.CanLoad)) { Source = _bulk });
        load.Click += async (_, _) => await BulkLoadAsync(onlyFailed: false).ConfigureAwait(true);
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var footerLeft = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        footerLeft.Children.Add(stats);
        footerLeft.Children.Add(progressBox);
        var footerRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footerRight.Children.Add(clear);
        footerRight.Children.Add(load);
        var footerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        Grid.SetColumn(footerLeft, 0);
        Grid.SetColumn(footerRight, 1);
        footerGrid.Children.Add(footerLeft);
        footerGrid.Children.Add(footerRight);

        var top = new StackPanel();
        top.Children.Add(hint);
        top.Children.Add(inputRow);
        top.Children.Add(_bulkLastScan);
        top.Children.Add(_bulkDraftBanner);
        top.Children.Add(_bulkSummaryBox);
        top.Children.Add(_bulkHeader);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(top, 0);
        Grid.SetRow(listArea, 1);
        Grid.SetRow(footerGrid, 2);
        root.Children.Add(top);
        root.Children.Add(listArea);
        root.Children.Add(footerGrid);

        var panel = new Border
        {
            CornerRadius = new CornerRadius(0, 14, 14, 14),
            Padding = new Thickness(14),
            BoxShadow = BoxShadows.Parse("0 4 12 0 #0D000000"),
            Child = root,
        };
        panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("BrushPanel"));
        return panel;
    }

    /// <summary>2026-10-24 (аудит, круг 8, С-2): шапка колонок над строками; ширины и отступы совпадают с line1 в BuildBulkRow
    /// (150 | * | кол-во 44+4+68+4+44 +8 отступ | 52), слева/справа — рамка и поля строки (1 + 10).</summary>
    private Control BuildBulkHeader()
    {
        TextBlock Cap(string text, int col, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var t = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeight.SemiBold, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            Grid.SetColumn(t, col);
            return t;
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,172,52"), Margin = new Thickness(11, 0, 11, 4) };
        grid.Children.Add(Cap(TrB("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), 0));
        var nameCap = Cap(TrB("Название", "Аталышы", "Name", "Ad", "Nomi"), 1);
        nameCap.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(nameCap);
        var qtyCap = Cap(TrB("Кол-во", "Саны", "Qty", "Adet", "Soni"), 2, HorizontalAlignment.Center);
        qtyCap.Margin = new Thickness(8, 0, 0, 0);
        grid.Children.Add(qtyCap);
        return grid;
    }

    /// <summary>2026-10-24 (аудит, круг 8, С-2): «Выбрать из списка» — тот же выбор товара, что в «Приёмке» (BundleItemPickerDialog);
    /// товар добавляется как скан его штрихкода. Товары без штрихкода пропускаются (скан по id здесь не поддерживается) — об этом пишем.</summary>
    private async Task BulkPickFromListAsync()
    {
        var dialog = new BundleItemPickerDialog(CatalogCacheService.Products, []);
        var confirmed = await dialog.ShowDialog<bool?>(this).ConfigureAwait(true);
        if (confirmed != true || dialog.Result.Count == 0)
            return;
        var skipped = 0;
        foreach (var product in dialog.Result)
        {
            var code = product.Barcode?.Trim() ?? "";
            if (code.Length == 0)
            {
                skipped++;
                continue;
            }
            await _bulk.ScanAsync(code).ConfigureAwait(true);
        }
        if (skipped > 0)
        {
            _bulkLastScan.IsVisible = true;
            _bulkLastScanRow = null;
            _bulkLastScan.Text = TrB($"Без штрихкода не добавлено: {skipped} — такие товары принимайте во вкладке «Приёмка».",
                $"Штрихкоду жок кошулган жок: {skipped} — мындай товарларды «Кабыл алуу» өтмөгүндө кабыл алыңыз.",
                $"Not added (no barcode): {skipped} — receive such products on the “Receiving” tab.",
                $"Barkodsuz eklenmedi: {skipped} — bu ürünleri «Mal kabul» sekmesinde kabul edin.",
                $"Shtrix-kodsiz qo'shilmadi: {skipped} — bunday mahsulotlarni «Qabul qilish» yorlig'ida qabul qiling.");
        }
        _bulkScanBox.Focus();
    }

    private static Border SoftBox(Control child)
    {
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1), Child = child };
        box.Bind(Border.BackgroundProperty, box.GetResourceObservable("BrushPanelSoft"));
        box.Bind(Border.BorderBrushProperty, box.GetResourceObservable("BrushBorder"));
        return box;
    }

    /// <summary>Кнопка 44 px (сенсор и мышь). Фокус не берёт — Enter сканера не должен её нажимать (стили Primary/SecondaryButton).</summary>
    private static Button BulkButton(string text, bool primary, double minWidth = 0)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 44,
            MinWidth = minWidth,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Classes.Add(primary ? "PrimaryButton" : "SecondaryButton");
        return button;
    }

    private static TextBox BulkInput(string watermark, double width)
    {
        var box = new TextBox { Watermark = watermark, MinHeight = 44, Width = width, VerticalContentAlignment = VerticalAlignment.Center };
        return box;
    }

    // ------------------------------------------------------------------ строка

    private Control BuildBulkRow(BulkScanRow row)
    {
        // Строка 1: код/вид | название | количество | ✕.
        var barcode = new TextBlock { Text = row.Line.Barcode, FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        barcode.Bind(TextBlock.ForegroundProperty, barcode.GetResourceObservable("BrushText"));
        var kind = new TextBlock { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        kind.Bind(TextBlock.ForegroundProperty, kind.GetResourceObservable("BrushTextSoft"));
        kind.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Bindings = { new Binding(nameof(BulkScanRow.KindText)), new Binding(nameof(BulkScanRow.StockInfo)) },
            Converter = new JoinConverter(),
        });
        var price = new TextBlock { FontSize = 11.5 };
        price.Bind(TextBlock.ForegroundProperty, price.GetResourceObservable("BrushTextSoft"));
        price.Bind(TextBlock.TextProperty, new Binding("Line.SalePriceText")
        {
            StringFormat = TrB("цена {0}", "баасы {0}", "price {0}", "fiyat {0}", "narxi {0}"),
        });
        price.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanRow.IsKnown)));
        var codeCol = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Width = 150 };
        codeCol.Children.Add(barcode);
        codeCol.Children.Add(kind);
        codeCol.Children.Add(price);

        var name = new TextBox
        {
            Watermark = TrB("Название *", "Аталышы *", "Name *", "Ad *", "Nomi *"),
            MinHeight = 44,
            VerticalContentAlignment = VerticalAlignment.Center,
            Tag = "name",
        };
        name.Bind(TextBox.TextProperty, new Binding("Line.ProductName") { Mode = BindingMode.TwoWay });
        name.Bind(TextBox.IsReadOnlyProperty, new Binding(nameof(BulkScanRow.IsKnown)));
        // 2026-10-24 (приёмка, ПР-11): у известного товара название не правится — серый фон и приглушённый текст (IsReadOnly уже стоит).
        void NameLook()
        {
            if (row.IsKnown)
            {
                name.Bind(TextBox.BackgroundProperty, name.GetResourceObservable("BrushInputAlt"));
                name.Bind(TextBox.ForegroundProperty, name.GetResourceObservable("BrushTextSoft"));
            }
            else
            {
                name.ClearValue(TextBox.BackgroundProperty);
                name.ClearValue(TextBox.ForegroundProperty);
            }
        }
        NameLook();
        PropertyChangedEventHandler nameHandler = (_, e) =>
        {
            if (e.PropertyName is nameof(BulkScanRow.IsKnown) or nameof(BulkScanRow.Line) or null)
                NameLook();
        };
        name.AttachedToVisualTree += (_, _) =>
        {
            row.PropertyChanged += nameHandler;
            NameLook();
        };
        name.DetachedFromVisualTree += (_, _) => row.PropertyChanged -= nameHandler;

        var minus = BulkButton("−", false, 44);
        minus.Width = 44;
        minus.Click += (_, _) =>
        {
            row.Line.Quantity = Math.Max(1, Math.Round(row.Line.Quantity - 1, 3));
            RefreshBulkLastScan(row);
        };
        var qty = new TextBox { MinHeight = 44, Width = 68, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Tag = "qty" };
        qty.Bind(TextBox.TextProperty, new Binding("Line.QuantityText") { Mode = BindingMode.TwoWay });
        var plus = BulkButton("+", false, 44);
        plus.Width = 44;
        plus.Click += (_, _) =>
        {
            row.Line.Quantity = Math.Round(row.Line.Quantity + 1, 3);
            RefreshBulkLastScan(row);
        };
        var qtyBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        qtyBox.Children.Add(minus);
        qtyBox.Children.Add(qty);
        qtyBox.Children.Add(plus);

        var remove = BulkButton("✕", false, 44);
        remove.Width = 44;
        remove.Click += (_, _) => _bulk.Remove(row);
        ToolTip.SetTip(remove, TrB("Убрать строку", "Сапты өчүрүү", "Remove the row", "Satırı kaldır", "Qatorni olib tashlash"));

        var line1 = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*,Auto,52") };
        name.Margin = new Thickness(8, 0, 0, 0);
        qtyBox.Margin = new Thickness(8, 0, 0, 0);
        remove.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(codeCol, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(qtyBox, 2);
        Grid.SetColumn(remove, 3);
        line1.Children.Add(codeCol);
        line1.Children.Add(name);
        line1.Children.Add(qtyBox);
        line1.Children.Add(remove);

        // Строка 2 (только новый товар): закупка, продажа, единица, категория.
        var purchase = BulkInput(TrB("Закупка *", "Сатып алуу *", "Cost *", "Alış *", "Xarid *"), 110);
        purchase.Bind(TextBox.TextProperty, new Binding("Line.PurchasePriceText") { Mode = BindingMode.TwoWay });
        var sale = BulkInput(TrB("Продажа *", "Сатуу *", "Price *", "Satış *", "Sotuv *"), 110);
        sale.Bind(TextBox.TextProperty, new Binding("Line.SalePriceText") { Mode = BindingMode.TwoWay });

        var unitSeg = new Border { Classes = { "sec-segment" }, VerticalAlignment = VerticalAlignment.Center };
        var unitPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var group = "bulkUnit" + Guid.NewGuid().ToString("N");
        var unitButtons = new List<RadioButton>();
        foreach (var unit in BulkUnits)
        {
            var rb = new RadioButton { Content = unit, GroupName = group, Tag = unit, MinHeight = 44, MinWidth = 44, Focusable = false };
            rb.Classes.Add("sec-seg");
            rb.IsCheckedChanged += (_, _) =>
            {
                if (rb.IsChecked == true && !string.Equals(row.Line.Unit, unit, StringComparison.Ordinal))
                    row.Line.Unit = unit;
            };
            unitButtons.Add(rb);
            unitPanel.Children.Add(rb);
        }
        unitSeg.Child = unitPanel;

        var category = BulkInput(TrB("Категория", "Категория", "Category", "Kategori", "Kategoriya"), 190);
        category.Bind(TextBox.TextProperty, new Binding(nameof(BulkScanRow.Category)) { Mode = BindingMode.TwoWay });
        var chips = new WrapPanel { Orientation = Orientation.Horizontal, IsVisible = false };

        var line2 = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(158, 6, 52, 0) };
        foreach (var c in new Control[] { purchase, sale, unitSeg, category })
        {
            c.Margin = new Thickness(0, 0, 8, 6);
            line2.Children.Add(c);
        }
        // Цены — у нового товара и у известного без цен; единица и категория — только у нового.
        line2.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanRow.ShowPrices)));
        unitSeg.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanRow.IsNewProduct)));
        category.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanRow.IsNewProduct)));
        chips.Margin = new Thickness(158, 0, 52, 0);

        void RebuildChips()
        {
            chips.Children.Clear();
            if (!category.IsFocused)
            {
                chips.IsVisible = false;
                return;
            }
            var typed = category.Text?.Trim() ?? "";
            var matches = _bulkCategories
                .Where(c => typed.Length == 0 || (c.Contains(typed, StringComparison.CurrentCultureIgnoreCase) && !string.Equals(c, typed, StringComparison.CurrentCultureIgnoreCase)))
                .Take(8).ToList();
            foreach (var c in matches)
            {
                var chip = BulkButton(c, false);
                chip.Margin = new Thickness(0, 0, 6, 6);
                chip.Click += (_, _) =>
                {
                    row.Category = c;
                    chips.IsVisible = false;
                };
                chips.Children.Add(chip);
            }
            chips.IsVisible = matches.Count > 0;
        }
        category.GotFocus += (_, _) => RebuildChips();
        category.LostFocus += (_, _) => chips.IsVisible = false;
        category.TextChanged += (_, _) => RebuildChips();

        var error = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(158, 4, 0, 0) };
        error.Bind(TextBlock.ForegroundProperty, error.GetResourceObservable("BrushDanger"));
        error.Bind(TextBlock.TextProperty, new Binding(nameof(BulkScanRow.Error)));
        error.Bind(Visual.IsVisibleProperty, new Binding(nameof(BulkScanRow.HasError)));

        var stack = new StackPanel();
        stack.Children.Add(line1);
        stack.Children.Add(line2);
        stack.Children.Add(chips);
        stack.Children.Add(error);

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(1),
            Child = stack,
            DataContext = row,
        };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("BrushBorder"));

        void Look()
        {
            var key = row.HasError ? "BrushDangerSoft" : row.IsFlash ? "BrushSuccessSoft" : "BrushPanelSoft";
            border.Bind(Border.BackgroundProperty, border.GetResourceObservable(key));
            foreach (var rb in unitButtons)
                rb.IsChecked = string.Equals((string?)rb.Tag, row.Line.Unit, StringComparison.Ordinal);
        }
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName is nameof(BulkScanRow.IsFlash) or nameof(BulkScanRow.HasError) or nameof(BulkScanRow.IsNewProduct) or nameof(BulkScanRow.ShowPrices) or null)
                Look();
        };
        border.AttachedToVisualTree += (_, _) =>
        {
            row.PropertyChanged += handler;
            Look();
        };
        border.DetachedFromVisualTree += (_, _) => row.PropertyChanged -= handler;
        return border;
    }

    private sealed class JoinConverter : Avalonia.Data.Converters.IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            string.Join(" · ", values.OfType<string>().Where(s => s.Length > 0));
    }

    // ------------------------------------------------------------------ события

    private void OnBulkTabOpened()
    {
        _ = LoadBulkCategoriesAsync();
        if (_bulk.Rows.Count == 0 && !_bulk.IsLoading)
        {
            var (count, savedAt) = BulkScanSession.DraftInfo();
            if (count > 0)
            {
                _bulkDraftText.Text = TrB($"Есть незавершённый список: {count} поз., сохранён {savedAt:dd.MM HH:mm}.",
                    $"Бүтпөгөн тизме бар: {count} поз., {savedAt:dd.MM HH:mm} сакталган.",
                    $"There is an unfinished list: {count} items, saved {savedAt:dd.MM HH:mm}.",
                    $"Tamamlanmamış liste var: {count} kalem, {savedAt:dd.MM HH:mm} tarihinde kaydedildi.",
                    $"Tugallanmagan ro'yxat bor: {count} pozitsiya, {savedAt:dd.MM HH:mm} da saqlangan.");
                _bulkDraftBanner.IsVisible = true;
            }
            else
                _bulkDraftBanner.IsVisible = false;
        }
        Focus();
    }

    private void BulkResumeDraft()
    {
        _bulk.RestoreDraft();
        _bulkDraftBanner.IsVisible = false;
        foreach (var c in _bulk.Rows.Select(r => r.Category).Where(c => c.Length > 0).Distinct())
            if (!_bulkCategories.Contains(c))
                _bulkCategories.Add(c);
    }

    private async Task LoadBulkCategoriesAsync()
    {
        try
        {
            var names = new List<string>();
            try
            {
                names.AddRange(NurMarketKassa.Services.LocalProductRepository.Instance.GetDistinctCategories());
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовое добавление: категории с этого компьютера не прочитаны: {ex.Message}", "WARNING");
            }
            if (names.Count == 0 && PosApp.CatalogApi is { } api)
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(4));
                names.AddRange(await api.GetCategoryNamesAsync(cts.Token).ConfigureAwait(true));
            }
            foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
                if (!_bulkCategories.Contains(name))
                    _bulkCategories.Add(name);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: категории не загружены: {ex.Message}", "WARNING");
        }
    }

    private void OnBulkScanned(BulkScanRow? row, BulkScanOutcome outcome)
    {
        _bulkLastScan.IsVisible = true;
        switch (outcome)
        {
            case BulkScanOutcome.Ignored:
                _bulkLastScanRow = null;
                _bulkLastScan.Text = TrB("Код не распознан — отсканируйте ещё раз.", "Код таанылган жок — кайра сканерлеңиз.",
                    "The code was not recognised — scan again.", "Kod tanınmadı — tekrar okutun.", "Kod tanilmadi — qayta skanerlang.");
                break;
            case BulkScanOutcome.NewRow:
                _bulkLastScanRow = null;
                _bulkLastScan.Text = TrB($"Новый штрихкод {row!.Line.Barcode} — впишите название и цены.",
                    $"Жаңы штрихкод {row.Line.Barcode} — атын жана баасын жазыңыз.",
                    $"New barcode {row.Line.Barcode} — enter the name and prices.",
                    $"Yeni barkod {row.Line.Barcode} — adı ve fiyatları yazın.",
                    $"Yangi shtrix-kod {row.Line.Barcode} — nomi va narxini yozing.");
                FocusBulkName(row);
                break;
            default:
                _bulkLastScanRow = row;
                _bulkLastScan.Text = $"✓ {row!.Line.ProductName} — {row.Line.Quantity:0.###}";
                // Курсор остаётся там, где работал оператор; в поле количества скан не попадёт (перехват быстрого ввода).
                break;
        }
    }

    /// <summary>2026-10-24 (приёмка, ПР-11): «−»/«+» в строке, по которой был последний скан, — обновить «✓ название — число».</summary>
    private void RefreshBulkLastScan(BulkScanRow row)
    {
        if (!ReferenceEquals(row, _bulkLastScanRow))
            return;
        _bulkLastScan.Text = $"✓ {row.Line.ProductName} — {row.Line.Quantity:0.###}";
    }

    /// <summary>Курсор в «Название» новой строки (она встаёт сверху). Строка рисуется не мгновенно — пробуем несколько раз.</summary>
    private async void FocusBulkName(BulkScanRow row)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(40).ConfigureAwait(true);
            var index = _bulk.Rows.IndexOf(row);
            if (index < 0)
                return;
            if (_bulkList.ContainerFromIndex(index) is { } container
                && container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => Equals(t.Tag, "name")) is { } box)
            {
                box.BringIntoView();
                box.Focus();
                return;
            }
        }
    }

    private void BulkClear()
    {
        if (_bulk.Rows.Count == 0)
        {
            _bulk.Clear();
            return;
        }
        var ok = PosConfirmDialog.Show(this,
            TrB("Очистить список?", "Тизме тазалансынбы?", "Clear the list?", "Liste temizlensin mi?", "Ro'yxat tozalansinmi?"),
            TrB($"Будут удалены все строки ({_bulk.Rows.Count}) — товары не создаются и остатки не меняются.",
                $"Бардык саптар ({_bulk.Rows.Count}) өчүрүлөт — товар түзүлбөйт, калдык өзгөрбөйт.",
                $"All rows ({_bulk.Rows.Count}) will be removed — no products are created and stock is not changed.",
                $"Tüm satırlar ({_bulk.Rows.Count}) silinecek — ürün oluşturulmaz, stok değişmez.",
                $"Barcha qatorlar ({_bulk.Rows.Count}) o'chiriladi — mahsulot yaratilmaydi, qoldiq o'zgarmaydi."),
            confirmText: TrB("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"),
            cancelText: TrB("Отмена", "Жок", "Cancel", "Vazgeç", "Bekor qilish"));
        if (ok)
            _bulk.Clear();
    }

    private async Task BulkLoadAsync(bool onlyFailed)
    {
        await _bulk.LoadAsync(onlyFailed).ConfigureAwait(true);
    }

    private async void OnBulkCompleted(int created, int received)
    {
        try
        {
            // Созданные товары появятся в каталоге кассы только после синхронизации — просим её сразу (как после приёмки).
            if (created > 0 && BulkScanSession.CatalogRefreshEnabled)
            {
                try
                {
                    await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Массовое добавление: каталог после создания товаров не обновлён: {ex.Message}", "WARNING");
                }
                await _viewModel.EnsureCatalogLoadedAsync().ConfigureAwait(true);
            }
            RefreshWarehouseTotals();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовое добавление: обновление склада после загрузки: {ex.Message}", "WARNING");
        }
    }
}
