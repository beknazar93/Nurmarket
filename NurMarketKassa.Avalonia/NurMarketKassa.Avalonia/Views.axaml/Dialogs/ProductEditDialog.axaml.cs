using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Форма добавления/редактирования товара — упрощённая native-версия полной
/// формы товара из веб-версии NurCRM (название, штрихкоды, категория/бренд, единица,
/// цены, горячая клавиша). Фото/комплекты/поставщики/акции пока доступны только на сайте
/// (кнопка "NurCRM" в боковом меню).</summary>
public partial class ProductEditDialog : Window, INotifyPropertyChanged
{
    private static readonly string[] HotkeyValues =
        ["Без горячей клавиши", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"];

    /// <summary>Сколько ждём создание товара на сервере синхронно, прежде чем закрыть диалог
    /// локально и продолжить в фоне — см. комментарий в SaveAsync у CreateProductAsync.</summary>
    private static readonly TimeSpan FastCreateTimeout = TimeSpan.FromSeconds(4);

    private readonly ICatalogApiService _catalogApi;
    private readonly CatalogProductTileVm? _existing;
    private readonly IBarcodeInputService? _barcodeInputService;

    private string _name = "";
    private string _code = "";
    private string _barcode = "";
    private string _category = "";
    private string _brand = "";
    private string _unit = "шт";
    private bool _isWeight;
    private string _quantity = "0";
    private string _purchasePrice = "";
    private string _markupPercent = "";
    private string _price = "";
    private string _hotkeyGroup = "Без горячей клавиши";
    private string _description = "";
    private string _plu = "";
    private bool _enablePieceSale;
    private string _packageQuantity = "";
    private string _packagePiecePrice = "";
    private string _wholesalePrice = "";
    private string _discountPercent = "";
    private string _heightCm = "";
    private string _widthCm = "";
    private string _depthCm = "";
    private string _weightKg = "";
    private string _country = "";
    private string _errorMessage = "";
    private string _statusMessage = "";
    private bool _isSaving;
    private string _kind = "product";
    private readonly ObservableCollection<BundleComponent> _bundleItems = new();

    // 2026-09-16, по просьбе пользователя: "доп. штрихкод" — это не алиас для того же товара,
    // а быстрое создание ОТДЕЛЬНОГО товара-варианта (например, "Асу" + "клубничный" → новый
    // товар "Асу клубничный" со своим штрихкодом и остатком). Каждая строка ниже — черновик
    // будущего отдельного товара, создаваемого при сохранении (см. SaveAsync). 2026-09-17,
    // уточнение пользователя: у варианта своей цены нет — цена (закупка/наценка/продажа) всегда
    // берётся с ГЛАВНОГО товара формы, у варианта только штрихкод, название и остаток.
    private string _variantBarcode = "";
    private string _variantNameSuffix = "";
    private string _variantQuantity = "";
    private readonly ObservableCollection<VariantDraft> _variants = new();

    /// <summary>2026-10-11, тестировщик (склад.md, 2.1.2): доп. штрихкод можно поправить после добавления — ✎ открывает поля
    /// прямо в списке, ✓ сохраняет (значения Edit* переносятся в Barcode/Name/Quantity).</summary>
    public sealed class VariantDraft : INotifyPropertyChanged
    {
        private string _barcode = "", _name = "", _fullName = "";
        private double _quantity;
        private bool _isEditing;

        public required string Barcode { get => _barcode; set { _barcode = value; Raise(); } }
        /// <summary>Название ВАРИАНТА (например, "клубничный") — то, что уходит в
        /// alternate_barcodes[].name и комбинируется с названием товара при сканировании
        /// (2026-09-21). Не путать с FullName ниже.</summary>
        public required string Name { get => _name; set { _name = value; Raise(); } }
        /// <summary>Название товара + название варианта — только для показа в списке.</summary>
        public required string FullName { get => _fullName; set { _fullName = value; Raise(); } }
        public double Quantity { get => _quantity; set { _quantity = value; Raise(); } }

        public bool IsEditing { get => _isEditing; set { _isEditing = value; Raise(); Raise(nameof(EditGlyph)); } }
        public string EditGlyph => _isEditing ? "✓" : "✎";
        public string EditBarcode { get; set; } = "";
        public string EditName { get; set; } = "";
        public string EditQuantity { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public bool Saved { get; private set; }

    /// <summary>2026-09-07, по просьбе пользователя: при добавлении товара по неизвестному
    /// штрихкоду со сканера форма показывает только название/штрихкод/остаток/цены — остальное
    /// (категория, бренд, единица, характеристики и т.д.) можно позже дозаполнить через
    /// обычную карточку товара в Складе. См. OfferAddUnknownProductAsync в MainWindow.Dialogs.cs.</summary>
    public bool IsQuickAddMode { get; }

    public ProductEditDialog(ICatalogApiService catalogApi, CatalogProductTileVm? existing, bool quickAddMode = false)
    {
        _catalogApi = catalogApi;
        _existing = existing;
        IsQuickAddMode = quickAddMode;

        // Поля существующего товара нужно заполнить ДО того, как InitializeComponent/DataContext
        // подключат bindings — иначе Avalonia считывает начальные (ещё пустые) значения через
        // геттеры один раз при установке DataContext, а прямая запись в приватные поля ниже не
        // поднимает PropertyChanged и UI так и останется пустым (баг, который уже был замечен).
        if (existing != null)
        {
            _name = existing.Title;
            _barcode = existing.Barcode ?? "";
            _category = existing.Category ?? "";
            _brand = existing.Brand ?? "";
            _unit = string.IsNullOrWhiteSpace(existing.Unit) ? "шт" : existing.Unit!;
            _isWeight = existing.MustWeigh;
            _quantity = existing.Quantity.ToString("0.###", CultureInfo.InvariantCulture);
            _purchasePrice = existing.PurchasePrice > 0
                ? existing.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture) : "";
            _price = ExtractPrice(existing.PriceLine);
            _hotkeyGroup = string.IsNullOrWhiteSpace(existing.HotkeyGroup) ? "Без горячей клавиши" : existing.HotkeyGroup!;
            _plu = existing.Plu?.ToString(CultureInfo.InvariantCulture) ?? "";

            // Раньше эти поля не копировались из карточки товара при открытии "Редактировать" —
            // форма показывала их пустыми (хотя на сервере/сайте значения уже были), а Save
            // отправлял пустые значения обратно и МОЛЧА СТИРАЛ их на сервере.
            _code = existing.Article ?? "";

            // Доп. штрихкоды, уже известные с сервера (переделано 2026-09-21: раньше строка
            // "доп. штрихкода" в этой форме создавала ОТДЕЛЬНЫЙ новый товар при сохранении, по
            // просьбе пользователя от 2026-09-16 — теперь пишет в alternate_barcodes ЭТОГО ЖЕ
            // товара, как на сайте, см. CatalogApiService.BuildAlternateBarcodes). Загружаем уже
            // существующие варианты сюда же, иначе Save молча стёр бы их (список ниже пуст по
            // умолчанию, а он теперь единственный источник alternate_barcodes при сохранении).
            if (existing.AlternateBarcodeVariants is { Count: > 0 })
            {
                foreach (var v in existing.AlternateBarcodeVariants)
                {
                    var suffix = (v.Name ?? "").Trim();
                    _variants.Add(new VariantDraft
                    {
                        Barcode = v.Barcode,
                        Name = suffix,
                        FullName = suffix.Length == 0
                            ? v.Barcode
                            : (string.IsNullOrWhiteSpace(existing.Title) ? suffix : $"{existing.Title.Trim()} {suffix}"),
                        Quantity = v.Quantity,
                    });
                }
            }
            _markupPercent = existing.MarkupPercent > 0
                ? existing.MarkupPercent.ToString("0.##", CultureInfo.InvariantCulture) : "";
            _description = existing.Description ?? "";
            _wholesalePrice = existing.WholesalePrice > 0
                ? existing.WholesalePrice.ToString("0.##", CultureInfo.InvariantCulture) : "";
            _discountPercent = existing.DiscountPercent > 0
                ? existing.DiscountPercent.ToString("0.##", CultureInfo.InvariantCulture) : "";
            _country = existing.Country ?? "";
            _weightKg = existing.WeightKg is > 0
                ? existing.WeightKg.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";

            if (existing.PieceOption is { } piece)
            {
                _enablePieceSale = true;
                _packageQuantity = piece.QuantityInPackage > 0
                    ? piece.QuantityInPackage.ToString("0.###", CultureInfo.InvariantCulture) : "";
                _packagePiecePrice = piece.PieceUnitPrice > 0
                    ? piece.PieceUnitPrice.ToString("0.##", CultureInfo.InvariantCulture) : "";
            }

            if (existing.IsBundle)
            {
                _kind = "bundle";
                if (existing.BundleItems is { Count: > 0 })
                    foreach (var component in existing.BundleItems)
                        _bundleItems.Add(component);
            }
        }

        GenerateBarcodeCommand = new RelayCommand(GenerateBarcode);

        InitializeComponent();
        // 2026-10-18, редизайн (этап 5): карточка ModalShell одной высоты на всех вкладках (окно не прыгает при смене
        // вкладки) — 820, но не выше 85 % рабочей области экрана кассы (ModalShell.MaxHeight). На Android размер окна
        // задаёт WindowLayerHost (на телефоне — во весь экран), там карточка тянется по окну: окну, как до этапа 5,
        // постоянная высота 860 (WindowLayerHost ужмёт её до экрана) — иначе окно по содержимому меняло бы высоту по вкладкам.
        if (!OperatingSystem.IsAndroid())
            Shell.Height = 820;
        else
        {
            SizeToContent = SizeToContent.Manual;
            Height = 860;
        }
        // У существующего товара под заголовком — его название (как в макете MStock), только для показа.
        // 2026-10-18, ревью этапа 5: длинное название — одной строкой с «…» (перенос делал шапку выше).
        if (existing != null && !string.IsNullOrWhiteSpace(existing.Title))
        {
            var title = existing.Title.Trim();
            Shell.Subtitle = title.Length > 70 ? title[..69].TrimEnd() + "…" : title;
        }
        // 2026-10-06, владелец (фото моноблока клиента): «там поля для цены не видны». Окно ужато под экран (DialogScreenFit),
        // а поля цены оставались ниже видимой части вкладки — на сенсоре прокрутку не найти. На невысоком окне поля плотнее
        // (цены помещаются), а поле, получившее фокус (касание, Tab), прокручивается в вид.
        // 2026-10-11 (склад.md, 2.1): подписи вкладки «Доп. штрих-коды» и поля «Минимальный остаток»; значение — с сервера.
        AltBarcodesTab.Header = Tr.T("Доп. штрих-коды", "Кошумча штрихкоддор", "Extra barcodes", "Ek barkodlar", "Qo'shimcha shtrix-kodlar");
        MinimumQuantityLabel.Text = Tr.T("Минимальный остаток", "Минималдуу калдык", "Minimum stock", "Asgari stok", "Minimal qoldiq");
        Opened += (_, _) => _ = LoadMinimumQuantityAsync();
        // 2026-10-07, владелец: «если наименование есть — поставь штрихкод из barcode-list.ru, но дай проверить и сверить».
        NameBox.LostFocus += (_, _) => _ = BarcodeSuggestAsync();
        // Совсем низкое окно (< 700: моноблок 1366×768 с масштабом 125 %) — ещё плотнее и без подсказки про скан.
        SizeChanged += (_, e) =>
        {
            MainTabStack.Spacing = e.NewSize.Height < 700 ? 6 : e.NewSize.Height < 820 ? 10 : 20;
            ScanHintHost.IsVisible = e.NewSize.Height >= 700;
            // 2026-10-18, этап 5: карточка не выше 85 % экрана — на 1024×768 без заголовка «Цены» цены видны без прокрутки.
            PricesHeader.IsVisible = e.NewSize.Height >= 700;
            // 2026-10-18, ревью этапа 5: окно < 600 (1366×768 при 125 %: карточка 494–522) — плотнее шапка, низ, отступы
            // и сегменты (стили .compact в разметке), иначе цены «Основного» оставались под краем вкладки.
            var compact = e.NewSize.Height < 600;
            Classes.Set("compact", compact);
            Shell.Padding = compact ? new Thickness(24, 6, 24, 4) : new Thickness(24, 12, 24, 10);
            MainTabScroll.Padding = compact ? new Thickness(0, 4, 16, 6) : new Thickness(0, 10, 16, 6);
        };
        MainTabStack.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is TextBox box)
                box.BringIntoView();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble);
        // 2026-10-04, владелец (Android, телефон): «видишь модалку — там ничего не видно при добавлении нового
        // товара». Окно на 660 точек на телефоне обрезалось справа. На узком экране ряды полей — столбиком
        // (артикул/штрихкод, остаток/единица/PLU, цены, категория/бренд, упаковка), окно — во всю ширину.
        if (OperatingSystem.IsAndroid())
        {
            const double narrow = 560;
            NurMarketKassa.AvaloniaHost.Services.NarrowStack.Attach(this, NarrowRowMain, narrow, "Auto,10,Auto");
            NurMarketKassa.AvaloniaHost.Services.NarrowStack.Attach(this, NarrowRowStock, narrow, "Auto,10,Auto,Auto,10,Auto");
            NurMarketKassa.AvaloniaHost.Services.NarrowStack.Attach(this, NarrowRowPrices, narrow, "Auto,10,Auto,10,Auto");
            NurMarketKassa.AvaloniaHost.Services.NarrowStack.Attach(this, NarrowRowCategory, narrow, "Auto,10,Auto");
            NurMarketKassa.AvaloniaHost.Services.NarrowStack.Attach(this, NarrowRowPackage, narrow, "Auto,10,Auto");
        }
        // 2026-10-03, владелец: «используй базу готовых наименований NurCRM — если есть товар, вставь сразу
        // название». Из кассы («Товар не найден» → «Добавить на склад») штрихкод уже вписан — название из
        // общей базы спрашиваем сразу при открытии, не дожидаясь, пока кассир уйдёт из поля штрихкода.
        Opened += (_, _) =>
        {
            if (_existing is null && !string.IsNullOrWhiteSpace(Barcode))
                _ = FillNameFromGlobalBaseAsync(Barcode);
        };
        // 2026-10-03: скан прямо в поле штрихкода заканчивается Enter — сразу ищем в базе NurCRM.
        BarcodeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                _ = FillNameFromGlobalBaseAsync(Barcode);
            }
        };
        // 2026-10-02, вкладки карточки: история закупок грузится при раскрытии — раскрываем при выборе вкладки.
        ProductTabs.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(ProductTabs.SelectedItem, HistoryTab))
                PurchaseHistoryExpander.IsExpanded = true;
        };
        ApplyVariantsTexts();
        DataContext = this;
        SetupPurchaseHistory();
        BuildHotkeyOptions();
        BuildCategoryOptions();
        BuildBrandOptions();
        SetupSupplier();
        _ = LoadReferenceListsAsync();
        UpdateSaveButtonState();

        // 2026-09-15, аудит удобства: в отличие от "быстрого добавления" по неизвестному
        // штрихкоду со сканера на главном экране (где штрихкод подставляется сразу при
        // открытии, см. OfferAddUnknownProductAsync), при обычном "+ Добавить товар" со
        // Склада штрихкод приходилось печатать вручную или генерировать свой — хотя сканер
        // физически эмулирует клавиатуру и мог бы просто печатать в поле "Штрихкод", если
        // оно в фокусе. Проблема в том, что кассир не всегда держит фокус именно там (только
        // что вводил название, например) — тогда символы штрихкода улетали бы не в то поле.
        // Ловим сканирование ГЛОБАЛЬНО по окну (как уже сделано в Складе) и сами подставляем
        // результат в поле "Штрихкод", кроме случая, когда оно и так уже в фокусе — тогда не
        // мешаем обычному вводу с клавиатуры/сканера.
        _barcodeInputService = App.AppHost?.Services.GetService<IBarcodeInputService>();
        if (_barcodeInputService != null)
        {
            _barcodeInputService.BarcodeScanned += OnBarcodeScanned;
            Closed += (_, _) => _barcodeInputService.BarcodeScanned -= OnBarcodeScanned;
        }
    }

    private void Window_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (_barcodeInputService is null)
            return;
        if (ReferenceEquals(FocusManager?.GetFocusedElement(), BarcodeBox))
            return;

        _barcodeInputService.ProcessKeyDown(e);
    }

    /// <summary>2026-10-04, Android: камера аппарата как сканер — код приходит как скан USB-сканера (OnBarcodeScanned).</summary>
    private async void CameraScanButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await NurMarketKassa.AvaloniaHost.Services.CameraScan.ScanIntoKassaAsync().ConfigureAwait(true);

    private void OnBarcodeScanned(string barcode)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Barcode = barcode;
            BarcodeBox.Focus();
            BarcodeBox.CaretIndex = BarcodeBox.Text?.Length ?? 0;
            _ = FillNameFromGlobalBaseAsync(barcode);
        });
    }

    private void BarcodeBox_LostFocus(object? sender, RoutedEventArgs e) =>
        _ = FillNameFromGlobalBaseAsync(Barcode);

    /// <summary>Название нового товара из общей базы товаров NurCRM — как на сайте при
    /// добавлении товара (2026-09-24). Только для нового товара и только если название ещё
    /// пустое: вписанное руками не перетираем. Если такой штрихкод уже есть на складе —
    /// говорим об этом сразу, а не после «Сохранить».</summary>
    private string? _barcodeSuggestFor;

    /// <summary>2026-10-07: новый товар без штрихкода, название введено — до 3 штрихкодов из barcode-list.ru с названием из базы.
    /// Владелец сверяет с товаром (название, вес) и нажимает «Поставить»; сам штрихкод не ставится.</summary>
    private async Task BarcodeSuggestAsync()
    {
        var name = (ProductName ?? "").Trim();
        if (_existing is not null || IsQuickAddMode || !string.IsNullOrWhiteSpace(Barcode) || name.Length < 4 || name == _barcodeSuggestFor)
            return;
        _barcodeSuggestFor = name;
        BarcodeSuggestPanel.Children.Clear();
        var status = new TextBlock
        {
            Text = Tr.T("Ищу штрихкод по названию в базе barcode-list.ru…", "barcode-list.ru базасынан аталышы боюнча штрихкод издеп жатам…",
                "Looking up the barcode by name on barcode-list.ru…", "barcode-list.ru'da ada göre barkod aranıyor…", "barcode-list.ru bazasidan nomi bo'yicha shtrix-kod qidirilmoqda…"),
            FontSize = 12.5, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Opacity = 0.8,
        };
        BarcodeSuggestPanel.Children.Add(status);
        BarcodeSuggestPanel.IsVisible = true;
        var found = await NurMarketKassa.AvaloniaHost.Services.BarcodeListLookup.FindByNameAsync(name, CancellationToken.None).ConfigureAwait(true);
        if (_barcodeSuggestFor != name || !string.IsNullOrWhiteSpace(Barcode))
        {
            BarcodeSuggestPanel.IsVisible = false;
            return;
        }
        if (found.Count == 0)
        {
            BarcodeSuggestPanel.IsVisible = false;
            return;
        }
        status.Text = Tr.T("Штрихкод из базы barcode-list.ru — сверьте название и вес с товаром и нажмите нужный:",
            "barcode-list.ru базасынан штрихкод — аталышын жана салмагын товар менен салыштырып, керектүүсүн басыңыз:",
            "Barcode from barcode-list.ru — check the name and weight against the product and press the right one:",
            "barcode-list.ru'dan barkod — adı ve ağırlığı ürünle karşılaştırıp doğru olana basın:",
            "barcode-list.ru bazasidan shtrix-kod — nomi va og'irligini mahsulot bilan solishtirib, keraklisini bosing:");
        status.Opacity = 1;
        foreach (var s in found)
        {
            var button = new Button
            {
                Classes = { "SecondaryButton" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Content = new TextBlock { Text = $"{s.Barcode}  ·  {s.Name}", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            };
            ToolTip.SetTip(button, Tr.T("Поставить этот штрихкод", "Ушул штрихкодду коюу", "Use this barcode", "Bu barkodu kullan", "Shu shtrix-kodni qo'yish"));
            button.Click += (_, _) =>
            {
                Barcode = s.Barcode;
                BarcodeSuggestPanel.IsVisible = false;
                PosLogger.Log($"Новый товар «{name}»: штрихкод {s.Barcode} из barcode-list.ru поставлен владельцем («{s.Name}»).", "CATALOG");
            };
            BarcodeSuggestPanel.Children.Add(button);
        }
    }

    private async Task FillNameFromGlobalBaseAsync(string? barcode)
    {
        var code = barcode?.Trim() ?? "";
        // 2026-10-03, владелец: «изучи веб: при сканировании, если товар есть в базе готовых наименований, сразу всё
        // подставляется». Сайт (страница «добавить сканом») берёт из общей базы название, категорию и бренд — цен там
        // нет. Заполняем пустые поля (вписанное руками не трогаем) и пишем, нашёлся ли товар.
        if (_existing is not null || _catalogApi is null || code.Length < 8
            || (!string.IsNullOrWhiteSpace(ProductName) && !string.IsNullOrWhiteSpace(Category) && !string.IsNullOrWhiteSpace(Brand))
            || code == _globalLookedUp)
            return;
        _globalLookedUp = code;
        GlobalStatus = Tr.T("Ищу в базе NurCRM…", "NurCRM базасынан издеп жатам…", "Searching the NurCRM base…", "NurCRM tabanında aranıyor…", "NurCRM bazasidan qidirilmoqda…");
        GlobalFound = null;

        try
        {
            // 2026-10-03: тот же код в другой записи (UPC-A 12 цифр ↔ EAN-13 с нулём) — см. BarcodeForms.
            // 2026-10-03, владелец: «он не находит» — находил, но через 2,5 минуты: сервер NurCRM отвечал ~40 с на
            // запрос, а склад и общая база спрашивались по очереди (до 4 запросов). Теперь все запросы — сразу.
            var alternate = BarcodeForms.Alternate(code);
            var api = _catalogApi;
            Task<System.Text.Json.JsonElement?> Safe(Func<Task<System.Text.Json.JsonElement?>> call) => Task.Run(async () =>
            {
                try { return await call().ConfigureAwait(false); }
                catch (Exception ex) { PosLogger.Log($"Карточка товара: запрос по штрихкоду не удался: {ex.Message}", "DEBUG"); return null; }
            });
            var ownTask = Safe(() => api.FindWarehouseProductByBarcodeAsync(code));
            var ownAltTask = alternate != null ? Safe(() => api.FindWarehouseProductByBarcodeAsync(alternate)) : Task.FromResult<System.Text.Json.JsonElement?>(null);
            var globalTask = Safe(() => api.FindGlobalProductByBarcodeAsync(code));
            var globalAltTask = alternate != null ? Safe(() => api.FindGlobalProductByBarcodeAsync(alternate)) : Task.FromResult<System.Text.Json.JsonElement?>(null);
            var all = Task.WhenAll(ownTask, ownAltTask, globalTask, globalAltTask);
            if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(true) != all)
                GlobalStatus = Tr.T("Сервер NurCRM отвечает медленно, жду…", "NurCRM сервери жай жооп берип жатат, күтүп жатам…", "The NurCRM server is slow, waiting…",
                    "NurCRM sunucusu yavaş yanıt veriyor, bekleniyor…", "NurCRM serveri sekin javob bermoqda, kutilmoqda…");
            await all.ConfigureAwait(true);
            var own = ownTask.Result ?? ownAltTask.Result;
            if (own is { } ownProduct)
            {
                var ownName = ownProduct.TryGetProperty("name", out var n) ? n.GetString() : null;
                ErrorMessage = Tr.T($"Товар с этим штрихкодом уже есть на складе: {ownName}.",
                    $"Мындай штрихкоддуу товар кампада мурунтан эле бар: {ownName}.",
                    $"A product with this barcode already exists in the warehouse: {ownName}.",
                    $"Bu barkodlu ürün zaten depoda var: {ownName}.",
                    $"Bu shtrix-kodli mahsulot omborda allaqachon bor: {ownName}.");
                GlobalStatus = "";
                return;
            }

            var global = globalTask.Result ?? globalAltTask.Result;
            if (!string.Equals(Barcode?.Trim(), code, StringComparison.Ordinal))
                return; // штрихкод уже сменили — ответ устарел
            if (global is { } globalProduct && GlobalText(globalProduct, "name") is { } globalName)
            {
                var category = GlobalText(globalProduct, "category");
                var brand = GlobalText(globalProduct, "brand");
                if (string.IsNullOrWhiteSpace(ProductName))
                    ProductName = globalName;
                if (string.IsNullOrWhiteSpace(Category) && category != null)
                    Category = category;
                if (string.IsNullOrWhiteSpace(Brand) && brand != null)
                    Brand = brand;
                GlobalFound = true;
                GlobalStatus = "✓ " + Tr.T("Найдено в базе NurCRM: ", "NurCRM базасынан табылды: ", "Found in the NurCRM base: ", "NurCRM tabanında bulundu: ", "NurCRM bazasidan topildi: ")
                               + string.Join(" · ", new[] { globalName, category, brand }.Where(x => !string.IsNullOrWhiteSpace(x)))
                               + Tr.T(". Укажите цены и количество.", ". Бааларды жана санын көрсөтүңүз.", ". Enter the prices and quantity.", ". Fiyatları ve miktarı girin.", ". Narx va miqdorni kiriting.");
                PosLogger.Log($"Карточка товара: по штрихкоду {code} из общей базы NurCRM подставлены название, категория и бренд.", "CATALOG");
            }
            else
            {
                GlobalFound = false;
                GlobalStatus = Tr.T("Этого штрихкода нет в базе NurCRM — заполните карточку вручную.", "Бул штрихкод NurCRM базасында жок — карточканы колго толтуруңуз.",
                    "This barcode is not in the NurCRM base — fill in the card manually.", "Bu barkod NurCRM tabanında yok — kartı elle doldurun.",
                    "Bu shtrix-kod NurCRM bazasida yo'q — kartani qo'lda to'ldiring.");
            }
        }
        catch (Exception ex)
        {
            GlobalStatus = "";
            _globalLookedUp = null;
            PosLogger.Log($"Карточка товара: общая база по штрихкоду {code} недоступна: {ex.Message}", "DEBUG");
        }
    }

    // 2026-10-03: строка «найдено / нет в базе NurCRM» над карточкой нового товара.
    private string? _globalLookedUp;
    private string _globalStatus = "";
    private bool? _globalFound;

    public string GlobalStatus { get => _globalStatus; private set { _globalStatus = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasGlobalStatus)); } }
    public bool HasGlobalStatus => !string.IsNullOrEmpty(_globalStatus);
    public bool? GlobalFound { get => _globalFound; private set { _globalFound = value; OnPropertyChanged(); OnPropertyChanged(nameof(GlobalNotFound)); } }
    public bool GlobalNotFound => _globalFound == false;
    public bool ShowGlobalScanHint => _existing is null;

    /// <summary>Строка из ответа общей базы: «category»/«brand» бывают строкой или объектом с «name».</summary>
    private static string? GlobalText(System.Text.Json.JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => v.GetString(),
            System.Text.Json.JsonValueKind.Object when v.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String => n.GetString(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>Строка истории закупок в карточке.</summary>
    private sealed class PurchaseHistoryRow
    {
        public string WhenText { get; init; } = "";
        public string MainText { get; init; } = "";
        public string DetailText { get; init; } = "";
        public string TotalText { get; init; } = "";
    }

    private bool _purchaseHistoryLoaded;

    private void SetupPurchaseHistory()
    {
        // Только у существующего товара: у нового истории ещё нет.
        if (_existing is null || IsQuickAddMode || string.IsNullOrWhiteSpace(_existing.Id))
            return;

        PurchaseHistoryExpander.IsVisible = true;
        PurchaseHistoryExpander.PropertyChanged += async (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty && PurchaseHistoryExpander.IsExpanded && !_purchaseHistoryLoaded)
                await LoadPurchaseHistoryAsync();
        };
    }

    private async Task LoadPurchaseHistoryAsync()
    {
        _purchaseHistoryLoaded = true;
        PurchaseHistoryStatus.Text = Tr.T("Загружаю историю закупок…", "Сатып алуулар тарыхы жүктөлүүдө…",
            "Loading purchase history…", "Alım geçmişi yükleniyor…", "Xaridlar tarixi yuklanmoqda…");

        List<PurchaseReceivingService.HistoryEntry> history;
        try
        {
            history = await PurchaseReceivingService.Instance.LoadHistoryAsync(_existing!.Id).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _purchaseHistoryLoaded = false;
            PurchaseHistoryStatus.Text = Tr.T("История закупок не загрузилась: ", "Сатып алуулар тарыхы жүктөлгөн жок: ",
                "Purchase history failed to load: ", "Alım geçmişi yüklenemedi: ", "Xaridlar tarixi yuklanmadi: ") + ex.Message;
            return;
        }

        var som = Tr.T("сом", "сом", "som", "som", "so'm");
        PurchaseHistoryList.ItemsSource = history.Select(h =>
        {
            var unit = string.IsNullOrWhiteSpace(h.Unit) ? "шт" : h.Unit;
            var details = new List<string>();
            if (h.SalePrice is { } sale && sale > 0)
                details.Add(Tr.T("продажа", "сатуу", "sale", "satış", "sotuv narxi") + $" {sale:0.##} {som}");
            if (!string.IsNullOrWhiteSpace(h.SupplierName))
                details.Add(Tr.T("поставщик", "жеткирүүчү", "supplier", "tedarikçi", "yetkazib beruvchi") + " " + h.SupplierName);
            if (!string.IsNullOrWhiteSpace(h.Employee))
                details.Add(Tr.T("принял", "кабыл алган", "received by", "teslim alan", "qabul qiluvchi") + " " + h.Employee);
            details.Add(h.Source);

            return new PurchaseHistoryRow
            {
                WhenText = h.At == default ? "—" : h.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                MainText = $"{h.Quantity:0.###} {unit} × {h.PurchasePrice:0.##} {som}",
                DetailText = string.Join("  ·  ", details),
                TotalText = $"{h.Quantity * h.PurchasePrice:0.##} {som}",
            };
        }).ToList();

        PurchaseHistoryStatus.Text = history.Count == 0
            ? Tr.T("Закупок этого товара пока не было.", "Бул товар боюнча азырынча сатып алуу болгон эмес.",
                "No purchases of this product yet.", "Bu ürün için henüz alım yapılmadı.", "Bu mahsulot hali xarid qilinmagan.")
            : Tr.T("Последняя цена закупки", "Акыркы сатып алуу баасы", "Last purchase price", "Son alış fiyatı", "Oxirgi xarid narxi")
              + $": {history[0].PurchasePrice:0.##} {som}  ·  "
              + Tr.T("закупок", "сатып алуулар", "purchases", "alım", "xaridlar soni") + $": {history.Count}";
    }

    /// <summary>Parameterless ctor required by Avalonia XAML previewer/designer only.</summary>
    public ProductEditDialog() : this(null!, null)
    {
    }

    public ICommand GenerateBarcodeCommand { get; }

    /// <summary>Раскрывающийся список без Popup (см. комментарий в XAML) — та же причина и то же
    /// решение, что уже применялись в PayDebtDialog для выбора клиента.</summary>
    private void BuildHotkeyOptions()
    {
        HotkeyCurrentText.Text = _hotkeyGroup == HotkeyValues[0] ? Tr.T("Без горячей клавиши", "Ыкчам баскычсыз", "No hotkey", "Kısayol tuşu yok", "Tezkor tugmasiz") : _hotkeyGroup;
        HotkeyOptionsList.Items.Clear();
        foreach (var option in HotkeyValues)
        {
            var button = new Button
            {
                Classes = { "SecondaryButton" },
                Content = option == HotkeyValues[0] ? Tr.T("Без горячей клавиши", "Ыкчам баскычсыз", "No hotkey", "Kısayol tuşu yok", "Tezkor tugmasiz") : option,
                Tag = option,
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(10, 6),
            };
            if (option == _hotkeyGroup)
                button.Classes.Add("PrimaryButton");

            button.Click += HotkeyOption_Click;
            HotkeyOptionsList.Items.Add(button);
        }
    }

    private void HotkeyToggleButton_Click(object? sender, RoutedEventArgs e) =>
        HotkeyOptionsPanel.IsVisible = !HotkeyOptionsPanel.IsVisible;

    private void HotkeyOption_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string option })
            return;

        HotkeyGroup = option;
        HotkeyOptionsPanel.IsVisible = false;
        BuildHotkeyOptions();
    }

    // ---------- Категория / бренд: справочники NurCRM (2026-09-07) ----------
    // Раньше это были два свободных TextBox'а; на сайте — select из справочника + "Создать".
    // Значения остаются строками (Category/Brand — их же шлёт Save как category_name/brand_name),
    // меняется только способ выбора. Список грузится в фоне после открытия формы, чтобы не
    // задерживать её показ; пока грузится — панель показывает "Загрузка…".
    private List<string> _categoryOptions = new();
    private List<string> _brandOptions = new();
    private bool _referenceListsLoading;

    private async Task LoadReferenceListsAsync()
    {
        if (_catalogApi is null)
            return;

        _referenceListsLoading = true;
        BuildCategoryOptions();
        BuildBrandOptions();
        try
        {
            if (OfflineModeHelper.UseLocalOperations)
            {
                // 2026-09-09: офлайн — категории/бренды берём из уже сохранённых локальных
                // товаров (LocalProductRepository), а не с сервера.
                _categoryOptions = LocalProductRepository.Instance.GetDistinctCategories().ToList();
                _brandOptions = LocalProductRepository.Instance.GetDistinctBrands().ToList();
            }
            else
            {
                var categoriesTask = _catalogApi.GetCategoryNamesAsync();
                var brandsTask = _catalogApi.GetBrandNamesAsync();
                _categoryOptions = await categoriesTask.ConfigureAwait(true);
                _brandOptions = await brandsTask.ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Product categories/brands load failed: {ex.GetType().Name}: {ex.Message}", "WARNING");
        }
        finally
        {
            _referenceListsLoading = false;
            BuildCategoryOptions();
            BuildBrandOptions();
        }
    }

    private void BuildCategoryOptions() =>
        BuildReferenceOptions(CategoryCurrentText, CategoryOptionsList, _categoryOptions, Category,
            Tr.T("Выберите категорию", "Категорияны тандаңыз", "Select a category", "Kategori seçin", "Kategoriyani tanlang"), CategoryOption_Click);

    private void BuildBrandOptions() =>
        BuildReferenceOptions(BrandCurrentText, BrandOptionsList, _brandOptions, Brand,
            Tr.T("Выберите бренд", "Брендди тандаңыз", "Select a brand", "Marka seçin", "Brendni tanlang"), BrandOption_Click);

    private void BuildReferenceOptions(
        TextBlock currentText,
        ItemsControl list,
        List<string> options,
        string current,
        string placeholder,
        EventHandler<RoutedEventArgs> onClick)
    {
        currentText.Text = string.IsNullOrWhiteSpace(current) ? placeholder : current;
        list.Items.Clear();

        if (_referenceListsLoading && options.Count == 0)
        {
            list.Items.Add(MakeOptionHint(Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
            return;
        }

        list.Items.Add(MakeOptionButton(Tr.T("— не указано —", "— көрсөтүлгөн эмес —", "— not specified —", "— belirtilmedi —", "— ko'rsatilmagan —"), "",
            string.IsNullOrWhiteSpace(current), onClick));

        // Текущее значение, которого нет в справочнике (товар создан на сайте с категорией,
        // которую потом удалили/переименовали) — показываем, чтобы не потерять его при сохранении.
        if (!string.IsNullOrWhiteSpace(current) && !options.Contains(current, StringComparer.OrdinalIgnoreCase))
            list.Items.Add(MakeOptionButton(current, current, true, onClick));

        foreach (var option in options)
        {
            list.Items.Add(MakeOptionButton(option, option,
                string.Equals(option, current, StringComparison.OrdinalIgnoreCase), onClick));
        }

        if (options.Count == 0)
            list.Items.Add(MakeOptionHint(Tr.T("Справочник пуст — создайте первую запись ниже", "Тизме бош — биринчи жазууну төмөндө түзүңүз", "The list is empty — create the first entry below", "Liste boş — aşağıda ilk kaydı oluşturun", "Ro'yxat bo'sh — quyida birinchi yozuvni yarating")));
    }

    private static Button MakeOptionButton(string label, string value, bool selected, EventHandler<RoutedEventArgs> onClick)
    {
        var button = new Button
        {
            Classes = { "SecondaryButton" },
            Content = label,
            Tag = value,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 6),
        };
        if (selected)
            button.Classes.Add("PrimaryButton");
        button.Click += onClick;
        return button;
    }

    private static TextBlock MakeOptionHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(4, 2, 4, 6),
        Opacity = 0.7,
    };

    private void CategoryToggleButton_Click(object? sender, RoutedEventArgs e) =>
        CategoryOptionsPanel.IsVisible = !CategoryOptionsPanel.IsVisible;

    private void BrandToggleButton_Click(object? sender, RoutedEventArgs e) =>
        BrandOptionsPanel.IsVisible = !BrandOptionsPanel.IsVisible;

    private void CategoryOption_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value })
            return;

        Category = value;
        CategoryOptionsPanel.IsVisible = false;
        BuildCategoryOptions();
    }

    private void BrandOption_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value })
            return;

        Brand = value;
        BrandOptionsPanel.IsVisible = false;
        BuildBrandOptions();
    }

    private async void CreateCategory_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalogApi is null)
            return;
        await CreateReferenceAsync(NewCategoryBox, CategoryOptionsPanel,
            _catalogApi.CreateCategoryAsync, _catalogApi.GetCategoryNamesAsync,
            names => _categoryOptions = names, value => Category = value, BuildCategoryOptions).ConfigureAwait(true);
    }

    private async void CreateBrand_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalogApi is null)
            return;
        await CreateReferenceAsync(NewBrandBox, BrandOptionsPanel,
            _catalogApi.CreateBrandAsync, _catalogApi.GetBrandNamesAsync,
            names => _brandOptions = names, value => Brand = value, BuildBrandOptions).ConfigureAwait(true);
    }

    /// <summary>Создать запись справочника на сервере, перечитать список, выбрать созданное.
    /// Если такое имя уже есть — просто выбираем его, без запроса к серверу.</summary>
    private async Task CreateReferenceAsync(
        TextBox input,
        Border panel,
        Func<string, CancellationToken, Task> create,
        Func<CancellationToken, Task<List<string>>> reload,
        Action<List<string>> storeOptions,
        Action<string> select,
        Action rebuild)
    {
        var name = input.Text?.Trim() ?? "";
        if (name.Length == 0)
            return;

        // 2026-09-09: офлайн — категории/бренды не отдельная таблица на сервере, а просто текст
        // на самом товаре. Сервер тут не нужен вообще: выбираем введённое имя сразу, оно
        // попадёт в список (GetDistinctCategories/Brands) само, как только товар сохранится.
        if (OfflineModeHelper.UseLocalOperations)
        {
            select(name);
            input.Text = "";
            panel.IsVisible = false;
            rebuild();
            return;
        }

        try
        {
            ErrorMessage = "";
            var existing = (await reload(CancellationToken.None).ConfigureAwait(true))
                .FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                StatusMessage = Tr.T("Создание…", "Түзүлүүдө…", "Creating…", "Oluşturuluyor…", "Yaratilmoqda…");
                await create(name, CancellationToken.None).ConfigureAwait(true);
                storeOptions(await reload(CancellationToken.None).ConfigureAwait(true));
            }

            select(existing ?? name);
            input.Text = "";
            panel.IsVisible = false;
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            StatusMessage = "";
            ErrorMessage = Tr.T("Не удалось создать: ", "Түзүү мүмкүн болгон жок: ", "Could not create: ", "Oluşturulamadı: ", "Yaratib bo'lmadi: ") + ex.Message;
        }
        finally
        {
            rebuild();
        }
    }

    // ---------- Поставщик товара (2026-10-20, владелец: «в описание добавить поставщика — выбор и добавление нового») ----------
    // На сервере NurCRM поставщик товара — поле «client» товара (id из clients/?type=suppliers); в карточке GET products/{id}/
    // рядом «client_name». Форма товара сайта шлёт его тем же create-manual/PATCH — касса тоже (ProductEditRequest.SupplierId).
    // Список поставщиков — тот же, что у приёмки склада (PurchaseReceivingService.LoadSuppliersAsync), грузится при первом
    // раскрытии списка. У существующего товара поставщик читается из карточки при открытии окна (LoadMinimumQuantityAsync).
    private List<PurchaseReceivingService.Supplier> _suppliers = new();
    private bool _suppliersLoaded;
    private bool _suppliersLoading;
    private string? _suppliersError;
    private string _supplierId = "";
    private string _supplierName = "";
    private string _supplierPhone = "";
    private bool _supplierChanged;
    private const int SupplierOptionsLimit = 100;

    private void SetupSupplier()
    {
        SupplierLabel.Text = Tr.T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi");
        SupplierSearchBox.Watermark = Tr.T("Найти по названию или телефону", "Аты же телефону боюнча табуу", "Find by name or phone",
            "Ada veya telefona göre bul", "Nomi yoki telefoni bo'yicha topish");
        NewSupplierButton.Content = Tr.T("+ Новый поставщик", "+ Жаңы жеткирүүчү", "+ New supplier", "+ Yeni tedarikçi", "+ Yangi yetkazib beruvchi");
        Avalonia.Automation.AutomationProperties.SetName(SupplierToggleButton, SupplierLabel.Text);
        Avalonia.Automation.AutomationProperties.SetName(NewSupplierButton, (string)NewSupplierButton.Content);
        Avalonia.Automation.AutomationProperties.SetName(SupplierSearchBox, SupplierSearchBox.Watermark);
        SupplierSearchBox.TextChanged += (_, _) => BuildSupplierOptions();
        // Enter в поиске: найден ровно один — выбираем его.
        SupplierSearchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter)
                return;
            e.Handled = true;
            var matches = FilterSuppliers(SupplierSearchBox.Text).Take(2).ToList();
            if (matches.Count == 1)
                SelectSupplier(matches[0].Id, matches[0].Name, matches[0].Phone);
        };
        // Поставщики — справочник сервера: офлайн выбрать нельзя (как клиентов), подсказка вместо списка.
        if (OfflineModeHelper.UseLocalOperations)
        {
            SupplierToggleButton.IsEnabled = false;
            SupplierHint.Text = Tr.T("Без связи с сервером поставщика не выбрать.", "Сервер менен байланыш жок — жеткирүүчүнү тандоого болбойт.",
                "No connection to the server — a supplier can't be chosen.", "Sunucu bağlantısı yok — tedarikçi seçilemez.",
                "Server bilan aloqa yo'q — yetkazib beruvchini tanlab bo'lmaydi.");
            SupplierHint.IsVisible = true;
        }
        BuildSupplierCurrent();
    }

    private void BuildSupplierCurrent()
    {
        SupplierCurrentText.Text = _supplierName.Length > 0
            ? _supplierName
            : Tr.T("Выберите поставщика", "Жеткирүүчүнү тандаңыз", "Select a supplier", "Tedarikçi seçin", "Yetkazib beruvchini tanlang");
        SupplierCurrentPhone.Text = _supplierPhone;
        SupplierCurrentPhone.IsVisible = _supplierPhone.Length > 0;
    }

    /// <summary>Поставщик существующего товара из карточки сервера: «client» — id (строка, число или объект с id),
    /// название — «client_name», иначе «suppliers»[0].name, иначе из загруженного списка. Выбранного в окне не перетирает.</summary>
    private void ApplySupplierFromDetail(System.Text.Json.JsonElement el)
    {
        if (_supplierChanged || !el.TryGetProperty("client", out var client))
            return;
        var id = client.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => client.GetString(),
            System.Text.Json.JsonValueKind.Number => client.GetRawText(),
            System.Text.Json.JsonValueKind.Object when client.TryGetProperty("id", out var cid) => cid.ToString(),
            _ => null,
        };
        id = id?.Trim() ?? "";
        if (id.Length == 0)
            return;
        string? name = el.TryGetProperty("client_name", out var cn) && cn.ValueKind == System.Text.Json.JsonValueKind.String ? cn.GetString() : null;
        if (string.IsNullOrWhiteSpace(name) && el.TryGetProperty("suppliers", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var s in list.EnumerateArray())
                if (s.ValueKind == System.Text.Json.JsonValueKind.Object && s.TryGetProperty("id", out var sid) && sid.ToString() == id
                    && s.TryGetProperty("name", out var sn) && sn.ValueKind == System.Text.Json.JsonValueKind.String)
                    name = sn.GetString();
        var known = _suppliers.FirstOrDefault(s => s.Id == id);
        _supplierId = id;
        _supplierName = (string.IsNullOrWhiteSpace(name) ? known?.Name : name)?.Trim() ?? "";
        _supplierPhone = known?.Phone ?? "";
        if (_supplierName.Length == 0)
            _supplierName = Tr.T("Поставщик выбран на сайте", "Жеткирүүчү сайтта тандалган", "Supplier set on the website", "Tedarikçi sitede seçildi",
                "Yetkazib beruvchi saytda tanlangan");
        BuildSupplierCurrent();
        if (SupplierOptionsPanel.IsVisible)
            BuildSupplierOptions();
    }

    private async void SupplierToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        SupplierOptionsPanel.IsVisible = !SupplierOptionsPanel.IsVisible;
        if (!SupplierOptionsPanel.IsVisible)
            return;
        BuildSupplierOptions();
        SupplierSearchBox.Focus();
        await EnsureSuppliersLoadedAsync().ConfigureAwait(true);
    }

    private async Task EnsureSuppliersLoadedAsync()
    {
        if (_suppliersLoaded || _suppliersLoading || OfflineModeHelper.UseLocalOperations)
            return;
        _suppliersLoading = true;
        _suppliersError = null;
        BuildSupplierOptions();
        try
        {
            _suppliers = await PurchaseReceivingService.Instance.LoadSuppliersAsync().ConfigureAwait(true);
            _suppliersLoaded = true;
            // Телефон выбранного (у карточки товара его нет) — из списка.
            if (_supplierId.Length > 0 && _suppliers.FirstOrDefault(s => s.Id == _supplierId) is { } current)
            {
                _supplierPhone = current.Phone ?? "";
                BuildSupplierCurrent();
            }
        }
        catch (Exception ex)
        {
            _suppliersError = ex.Message;
            PosLogger.Log($"Карточка товара: список поставщиков не загружен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        finally
        {
            _suppliersLoading = false;
            BuildSupplierOptions();
        }
    }

    /// <summary>Поиск: по части названия (без регистра) или по цифрам телефона (от 3 цифр: «555 12» найдёт +996 555 12…).</summary>
    private IEnumerable<PurchaseReceivingService.Supplier> FilterSuppliers(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return _suppliers;
        var digits = new string(q.Where(char.IsDigit).ToArray());
        return _suppliers.Where(s =>
            s.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (digits.Length >= 3 && new string((s.Phone ?? "").Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)));
    }

    private void BuildSupplierOptions()
    {
        SupplierOptionsList.Children.Clear();
        if (_suppliersLoading && _suppliers.Count == 0)
        {
            SupplierOptionsList.Children.Add(MakeOptionHint(Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
            return;
        }
        if (_suppliersError != null)
            SupplierOptionsList.Children.Add(MakeOptionHint(Tr.T("Список поставщиков не загрузился: ", "Жеткирүүчүлөрдүн тизмеси жүктөлгөн жок: ",
                "The supplier list didn't load: ", "Tedarikçi listesi yüklenmedi: ", "Yetkazib beruvchilar ro'yxati yuklanmadi: ") + _suppliersError));

        var query = SupplierSearchBox.Text?.Trim() ?? "";
        if (query.Length == 0)
            SupplierOptionsList.Children.Add(MakeSupplierOption("", Tr.T("— без поставщика —", "— жеткирүүчүсүз —", "— no supplier —", "— tedarikçi yok —",
                "— yetkazib beruvchisiz —"), null, _supplierId.Length == 0));
        // Выбранный, которого нет в списке (список ещё не загружен, поставщика удалили) — показываем, чтобы было видно, что выбрано.
        if (query.Length == 0 && _supplierId.Length > 0 && _suppliers.All(s => s.Id != _supplierId))
            SupplierOptionsList.Children.Add(MakeSupplierOption(_supplierId, _supplierName, _supplierPhone, true));

        var matches = FilterSuppliers(query).ToList();
        foreach (var s in matches.Take(SupplierOptionsLimit))
            SupplierOptionsList.Children.Add(MakeSupplierOption(s.Id, s.Name, s.Phone, s.Id == _supplierId));
        if (matches.Count > SupplierOptionsLimit)
            SupplierOptionsList.Children.Add(MakeOptionHint(Tr.T($"Показаны первые {SupplierOptionsLimit} из {matches.Count} — уточните поиск.",
                $"{matches.Count} ичинен алгачкы {SupplierOptionsLimit} көрсөтүлдү — издөөнү тактаңыз.",
                $"Showing the first {SupplierOptionsLimit} of {matches.Count} — refine the search.",
                $"{matches.Count} kayıttan ilk {SupplierOptionsLimit} gösteriliyor — aramayı daraltın.",
                $"{matches.Count} tadan birinchi {SupplierOptionsLimit} ko'rsatildi — qidiruvni aniqlang.")));
        if (!_suppliersLoading && _suppliersError == null && matches.Count == 0)
            SupplierOptionsList.Children.Add(MakeOptionHint(query.Length > 0
                ? Tr.T("Не найдено — добавьте нового поставщика.", "Табылган жок — жаңы жеткирүүчү кошуңуз.", "Not found — add a new supplier.",
                    "Bulunamadı — yeni tedarikçi ekleyin.", "Topilmadi — yangi yetkazib beruvchi qo'shing.")
                : Tr.T("Поставщиков пока нет — добавьте первого.", "Азырынча жеткирүүчүлөр жок — биринчисин кошуңуз.", "No suppliers yet — add the first one.",
                    "Henüz tedarikçi yok — ilkini ekleyin.", "Hozircha yetkazib beruvchilar yo'q — birinchisini qo'shing.")));
    }

    /// <summary>Строка списка: название слева, телефон справа; выбранная — тёмно-синяя (SecondaryButton + PrimaryButton).</summary>
    private Button MakeSupplierOption(string id, string name, string? phone, bool selected)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = name, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        if (!string.IsNullOrWhiteSpace(phone))
        {
            var phoneText = new TextBlock { Text = phone, Opacity = 0.75, FontWeight = Avalonia.Media.FontWeight.Normal, Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            Grid.SetColumn(phoneText, 1);
            grid.Children.Add(phoneText);
        }
        var button = new Button
        {
            Classes = { "SecondaryButton" },
            Content = grid,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Padding = new Thickness(12, 7),
            MinHeight = 38,
        };
        if (selected)
            button.Classes.Add("PrimaryButton");
        Avalonia.Automation.AutomationProperties.SetName(button, string.IsNullOrWhiteSpace(phone) ? name : $"{name}, {phone}");
        button.Click += (_, _) => SelectSupplier(id, id.Length == 0 ? "" : name, phone);
        return button;
    }

    private void SelectSupplier(string id, string name, string? phone)
    {
        _supplierId = id;
        _supplierName = name;
        _supplierPhone = phone ?? "";
        _supplierChanged = true;
        SupplierOptionsPanel.IsVisible = false;
        SupplierSearchBox.Text = "";
        BuildSupplierCurrent();
    }

    private async void NewSupplier_Click(object? sender, RoutedEventArgs e)
    {
        if (OfflineModeHelper.UseLocalOperations)
            return;
        try
        {
            // Список нужен для подсказки «такой номер уже есть»; не загрузился — добавить всё равно можно.
            await EnsureSuppliersLoadedAsync().ConfigureAwait(true);
            var existing = _suppliers.Select(s => new ClientRow { Id = s.Id, FullName = s.Name, Phone = s.Phone ?? "" }).ToList();
            await new NewClientDialog(AddSupplierAsync, existing, supplier: true).ShowDialog(this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно «Новый поставщик» не открылось: {ex}", "WARNING");
        }
    }

    /// <summary>Сохранение окна «Новый поставщик»: null — добавлен и выбран (окно закроется), иначе текст ошибки.
    /// Поставщик с тем же названием уже есть — выбираем его, второй такой же не создаём.</summary>
    private async Task<string?> AddSupplierAsync(string name, string phone, string email, string address)
    {
        var key = PurchaseReceivingService.SupplierNameKey(name);
        if (key.Length > 0 && _suppliers.FirstOrDefault(s => PurchaseReceivingService.SupplierNameKey(s.Name) == key) is { } same)
        {
            SelectSupplier(same.Id, same.Name, same.Phone);
            return null;
        }
        try
        {
            var created = await PurchaseReceivingService.Instance.CreateSupplierAsync(name, phone).ConfigureAwait(true);
            if (created is null)
                return Tr.T("Не удалось добавить поставщика.", "Жеткирүүчүнү кошуу мүмкүн болгон жок.", "Could not add the supplier.", "Tedarikçi eklenemedi.",
                    "Yetkazib beruvchini qo'shib bo'lmadi.");
            _suppliers = _suppliers.Append(created).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            SelectSupplier(created.Id, created.Name, created.Phone);
            PosLogger.Log($"Карточка товара: добавлен поставщик {created.Id}.", "WAREHOUSE");
            return null;
        }
        catch (ApiException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Supplier create failed: {ex}", "WARNING");
            return Tr.T("Не удалось добавить поставщика: ", "Жеткирүүчүнү кошуу мүмкүн болгон жок: ", "Could not add the supplier: ", "Tedarikçi eklenemedi: ",
                "Yetkazib beruvchini qo'shib bo'lmadi: ") + ex.Message;
        }
    }

    /// <summary>Save-кнопка использует прямой Click вместо Command+IsEnabled — тот же баг,
    /// уже дважды пойманный в этой сессии (ClientsWindow): при первом открытии формы с уже
    /// заполненным именем (редактирование) кнопка сразу активна и баг незаметен, но у формы
    /// добавления имя пустое, и когда пользователь его вводит, RaiseCanExecuteChanged не всегда
    /// реально перерисовывает IsEnabled — кнопка "Добавить" остаётся серой навсегда.</summary>
    private void UpdateSaveButtonState()
    {
        if (SaveButton != null)
            SaveButton.IsEnabled = CanSaveNow;
    }

    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanSaveNow)
            return;

        // 2026-09-08: личный код доступа сотрудника — отдельно на добавление нового товара и
        // на редактирование существующего (владелец/админ проходит без кода). Не активен, пока
        // в Настройки → Сотрудники не задан хотя бы один код на соответствующее действие.
        var action = _existing is null ? EmployeeAccessGate.ProductAdd : EmployeeAccessGate.ProductEdit;
        var permissions = App.AppHost?.Services.GetService<IPermissionService>();
        if (EmployeeAccessGate.IsActiveFor(action, permissions))
        {
            var prompts = App.AppHost?.Services.GetService<IUserPrompts>();
            var granted = prompts != null && await prompts.ConfirmWithCodeAsync(
                _existing is null
                    ? Tr.T("Добавление товара", "Товар кошуу", "Add product", "Ürün ekleme", "Mahsulot qo'shish")
                    : Tr.T("Редактирование товара", "Товарды түзөтүү", "Edit product", "Ürün düzenleme", "Mahsulotni tahrirlash"),
                Tr.T("Введите свой код доступа, чтобы продолжить.",
                    "Улантуу үчүн жеке кодуңузду киргизиңиз.",
                    "Enter your access code to continue.",
                    "Devam etmek için erişim kodunuzu girin.",
                    "Davom etish uchun kirish kodingizni kiriting."),
                entered => EmployeeAccessGate.TryValidate(action, entered)).ConfigureAwait(true);
            if (!granted)
                return;
        }

        await SaveAsync().ConfigureAwait(true);
    }

    public string HeaderText => _existing is null ? Tr.T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot") : Tr.T("Редактирование товара", "Товарды түзөтүү", "Edit product", "Ürün düzenleme", "Mahsulotni tahrirlash");
    public string SaveButtonText => _existing is null ? Tr.T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish") : Tr.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash");

    public string ProductName
    {
        get => _name;
        set
        {
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanSaveNow));
            OnPropertyChanged(nameof(VariantFullNamePreview));
            OnPropertyChanged(nameof(HasVariantPreview));
            OnPropertyChanged(nameof(VariantPreviewText));
            UpdateSaveButtonState();
        }
    }

    public string Code { get => _code; set { _code = value; OnPropertyChanged(); } }
    public string Barcode { get => _barcode; set { _barcode = value; OnPropertyChanged(); } }
    public string VariantBarcode { get => _variantBarcode; set { _variantBarcode = value; OnPropertyChanged(); } }

    public string VariantNameSuffix
    {
        get => _variantNameSuffix;
        set
        {
            _variantNameSuffix = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(VariantFullNamePreview));
            OnPropertyChanged(nameof(HasVariantPreview));
            OnPropertyChanged(nameof(VariantPreviewText));
        }
    }

    public string VariantQuantity { get => _variantQuantity; set { _variantQuantity = value; OnPropertyChanged(); } }

    /// <summary>Итоговое имя нового товара-варианта — главное имя + название варианта, живой
    /// предпросмотр над кнопкой "Добавить".</summary>
    public string VariantFullNamePreview =>
        string.IsNullOrWhiteSpace(_variantNameSuffix) || string.IsNullOrWhiteSpace(_name)
            ? ""
            : $"{_name.Trim()} {_variantNameSuffix.Trim()}";

    public bool HasVariantPreview => !string.IsNullOrEmpty(VariantFullNamePreview);

    public string VariantPreviewText => HasVariantPreview
        ? Tr.T("При сканировании в чеке будет: «", "Сканерлегенде чекте мындай болот: «", "On scan, the receipt line will read: \"", "Okutulduğunda fişte şöyle görünür: \"", "Skanerlanganda chekda shunday ko'rinadi: «") + VariantFullNamePreview
          + Tr.T("»", "»", "\"", "\"", "»")
        : "";

    public ObservableCollection<VariantDraft> Variants => _variants;
    public bool HasVariants => _variants.Count > 0;
    public string Category { get => _category; set { _category = value; OnPropertyChanged(); } }
    public string Brand { get => _brand; set { _brand = value; OnPropertyChanged(); } }
    public string Unit { get => _unit; set { _unit = value; OnPropertyChanged(); } }
    public bool IsWeight { get => _isWeight; set { _isWeight = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShowWeightToggle)); } }

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-13): в сфере «Одежда» переключатель «Весовой» (и PLU) не нужен —
    /// одежду не взвешивают, а случайно включённый «Весовой» ломал продажу. Если товар уже весовой — переключатель остаётся,
    /// чтобы его можно было выключить.</summary>
    // 2026-10-07, владелец (снимок окна товара в режиме «Одежда»): «где при добавлении тумблер «Весовой», как на вебе?» —
    // переключатель снова виден во всех режимах магазина, как на сайте NurCRM.
    public bool ShowWeightToggle => !IsQuickAddMode;
    public string Quantity { get => _quantity; set { _quantity = value; OnPropertyChanged(); } }
    public string PurchasePrice { get => _purchasePrice; set { _purchasePrice = value; OnPropertyChanged(); RaisePriceWarning(); } }

    /// <summary>2026-10-05, владелец: «если во время редактирования или создания товара закупочная цена превышает
    /// продажную — там должно быть красное предупреждение». Пусто — цены в порядке (или одна из них не введена).</summary>
    public string PriceWarning
    {
        get
        {
            var purchase = TryParseNumber(_purchasePrice);
            var sale = TryParseNumber(_price);
            if (purchase is not > 0 || sale is not > 0 || purchase.Value <= sale.Value)
                return "";
            var p = purchase.Value.ToString("0.##", CultureInfo.InvariantCulture);
            var s = sale.Value.ToString("0.##", CultureInfo.InvariantCulture);
            var loss = (purchase.Value - sale.Value).ToString("0.##", CultureInfo.InvariantCulture);
            return Tr.T($"⚠ Закупочная цена ({p} сом) больше цены продажи ({s} сом) — каждая продажа в убыток {loss} сом.",
                $"⚠ Сатып алуу баасы ({p} сом) сатуу баасынан ({s} сом) жогору — ар бир сатуу {loss} сом зыян.",
                $"⚠ The purchase price ({p} som) is higher than the sale price ({s} som) — each sale loses {loss} som.",
                $"⚠ Alış fiyatı ({p} som) satış fiyatından ({s} som) yüksek — her satış {loss} som zarar.",
                $"⚠ Xarid narxi ({p} so'm) sotuv narxidan ({s} so'm) yuqori — har bir sotuv {loss} so'm zarar.");
        }
    }

    public bool HasPriceWarning => PriceWarning.Length > 0;

    private void RaisePriceWarning()
    {
        OnPropertyChanged(nameof(PriceWarning));
        OnPropertyChanged(nameof(HasPriceWarning));
    }

    /// <summary>2026-09-07, по просьбе пользователя: цена продажи и наценка держатся в
    /// синхроне друг с другом — ввод одного пересчитывает другое от текущей цены закупки.
    /// _suppressPriceSync не даёт зациклиться, когда один сеттер программно меняет другой.</summary>
    public string MarkupPercent
    {
        get => _markupPercent;
        set
        {
            _markupPercent = value;
            OnPropertyChanged();
            if (!_suppressPriceSync)
                RecalculateSaleFromMarkup();
        }
    }

    public string Price
    {
        get => _price;
        set
        {
            _price = value;
            OnPropertyChanged();
            RaisePriceWarning();
            if (!_suppressPriceSync)
                RecalculateMarkupFromSale();
        }
    }

    private bool _suppressPriceSync;

    // 2026-09-16, по просьбе пользователя ("маржа считает неправильно") — поле временно считало
    // МАРЖУ (% от цены продажи) вместо НАЦЕНКИ (% от закупочной цены), с ограничением <100% (у
    // маржи это математическое свойство — цена не может быть меньше закупки в долях продажи).
    // 2026-09-17, пользователь подтвердил через уточняющий вопрос: нужна именно НАЦЕНКА, как было
    // изначально — закупка 100, продажа 120 → 20% (а не маржа 16,7%). Ограничения <100% у наценки
    // нет: закупка 10, продажа 25 — это законные 150%. Возвращаем формулу наценки, поле снова
    // называется "Наценка, %" (см. XAML/переводы).
    private void RecalculateSaleFromMarkup()
    {
        var purchase = ParseNumber(_purchasePrice);
        var markup = TryParseNumber(_markupPercent);
        if (purchase <= 0 || markup is null)
            return;

        _suppressPriceSync = true;
        // 2026-10-20: цена по наценке — с запятой, как в поле при открытии (ExtractPrice).
        Price = (purchase * (1 + markup.Value / 100.0)).ToString("0.##", CultureInfo.InvariantCulture).Replace('.', ',');
        _suppressPriceSync = false;
    }

    private void RecalculateMarkupFromSale()
    {
        var purchase = ParseNumber(_purchasePrice);
        var sale = TryParseNumber(_price);
        if (purchase <= 0 || sale is null || sale.Value <= 0)
            return;

        _suppressPriceSync = true;
        MarkupPercent = ((sale.Value - purchase) / purchase * 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        _suppressPriceSync = false;
    }
    public string HotkeyGroup { get => _hotkeyGroup; set { _hotkeyGroup = value; OnPropertyChanged(); } }
    public string Description { get => _description; set { _description = value; OnPropertyChanged(); } }
    public string Plu { get => _plu; set { _plu = value; OnPropertyChanged(); } }
    public bool EnablePieceSale { get => _enablePieceSale; set { _enablePieceSale = value; OnPropertyChanged(); } }
    public string PackageQuantity { get => _packageQuantity; set { _packageQuantity = value; OnPropertyChanged(); } }
    public string PackagePiecePrice { get => _packagePiecePrice; set { _packagePiecePrice = value; OnPropertyChanged(); } }
    public string WholesalePrice { get => _wholesalePrice; set { _wholesalePrice = value; OnPropertyChanged(); } }

    // 2026-10-11, тестировщик (склад.md, 2.1.3): «Минимальный остаток» на вкладке «Упаковка». У существующего товара значение
    // читается с сервера при открытии; не прочитали и не вписали — на сервер не отправляется (не затираем).
    private string _minimumQuantity = "";
    private bool _minimumQuantityKnown;
    public string MinimumQuantity { get => _minimumQuantity; set { _minimumQuantity = value; _minimumQuantityKnown = true; OnPropertyChanged(); } }

    private async Task LoadMinimumQuantityAsync()
    {
        if (_existing is null || string.IsNullOrWhiteSpace(_existing.Id) || OfflineModeHelper.UseLocalOperations)
            return;
        try
        {
            if (await PosApp.CatalogApi.ProductsDetailAsync(_existing.Id).ConfigureAwait(true) is not { ValueKind: System.Text.Json.JsonValueKind.Object } el)
                return;
            // 2026-10-20: из той же карточки — поставщик товара (client / client_name), без второго запроса.
            ApplySupplierFromDetail(el);
            if (el.TryGetProperty("minimum_quantity", out var v) && !_minimumQuantityKnown
                && double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var min))
            {
                _minimumQuantity = min > 0 ? min.ToString("0.###", CultureInfo.InvariantCulture) : "";
                _minimumQuantityKnown = true;
                OnPropertyChanged(nameof(MinimumQuantity));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Карточка товара: минимальный остаток не прочитан ({ex.Message}).", "WAREHOUSE");
        }
    }
    public string DiscountPercent { get => _discountPercent; set { _discountPercent = value; OnPropertyChanged(); } }
    public string HeightCm { get => _heightCm; set { _heightCm = value; OnPropertyChanged(); } }
    public string WidthCm { get => _widthCm; set { _widthCm = value; OnPropertyChanged(); } }
    public string DepthCm { get => _depthCm; set { _depthCm = value; OnPropertyChanged(); } }
    public string WeightKg { get => _weightKg; set { _weightKg = value; OnPropertyChanged(); } }
    public string Country { get => _country; set { _country = value; OnPropertyChanged(); } }

    // ---------- Тип товара: Товар / Услуга / Комплект (2026-09-12) ----------
    public string Kind
    {
        get => _kind;
        private set
        {
            if (_kind == value)
                return;
            _kind = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsProductKind));
            OnPropertyChanged(nameof(IsServiceKind));
            OnPropertyChanged(nameof(IsBundleKind));
        }
    }

    public bool IsProductKind => _kind == "product";
    public bool IsServiceKind => _kind == "service";
    public bool IsBundleKind => _kind == "bundle";

    public ObservableCollection<BundleComponent> BundleItems => _bundleItems;
    public bool HasBundleItems => _bundleItems.Count > 0;

    private void KindProduct_Click(object? sender, RoutedEventArgs e) => Kind = "product";
    private void KindService_Click(object? sender, RoutedEventArgs e) => Kind = "service";
    private void KindBundle_Click(object? sender, RoutedEventArgs e) => Kind = "bundle";

    private async void SelectBundleItems_Click(object? sender, RoutedEventArgs e)
    {
        var allProducts = OfflineModeHelper.UseLocalOperations
            ? LocalProductRepository.Instance.LoadAllTiles()
            : CatalogCacheService.Products;

        var picker = new BundleItemPickerDialog(allProducts, _bundleItems.Select(i => i.ProductId));
        var confirmed = await picker.ShowDialog<bool>(this).ConfigureAwait(true);
        if (!confirmed)
            return;

        _bundleItems.Clear();
        foreach (var product in picker.Result)
            _bundleItems.Add(new BundleComponent { ProductId = product.Id, ProductName = product.Title, Quantity = 1 });

        OnPropertyChanged(nameof(HasBundleItems));
    }

    private void RemoveBundleItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string productId })
            return;

        var toRemove = _bundleItems.FirstOrDefault(i => i.ProductId == productId);
        if (toRemove != null)
            _bundleItems.Remove(toRemove);
        OnPropertyChanged(nameof(HasBundleItems));
    }

    private void AddVariant_Click(object? sender, RoutedEventArgs e)
    {
        var barcode = _variantBarcode.Trim();
        var suffix = _variantNameSuffix.Trim();
        if (barcode.Length == 0 || suffix.Length == 0)
        {
            ErrorMessage = Tr.T(
                "Введите штрихкод и название варианта.",
                "Вариант үчүн штрихкодду жана атын киргизиңиз.",
                "Enter the variant's barcode and name.",
                "Varyant için barkod ve ad girin.",
                "Variantning shtrix-kodi va nomini kiriting.");
            return;
        }

        _variants.Add(new VariantDraft
        {
            Barcode = barcode,
            Name = suffix,
            FullName = string.IsNullOrWhiteSpace(_name) ? suffix : $"{_name.Trim()} {suffix}",
            Quantity = ParseNumber(_variantQuantity),
        });
        OnPropertyChanged(nameof(HasVariants));

        VariantBarcode = "";
        VariantNameSuffix = "";
        VariantQuantity = "";
        ErrorMessage = "";
    }

    private void EditVariant_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VariantDraft draft })
            return;
        if (!draft.IsEditing)
        {
            draft.EditBarcode = draft.Barcode;
            draft.EditName = draft.Name;
            draft.EditQuantity = draft.Quantity > 0 ? draft.Quantity.ToString("0.###", CultureInfo.InvariantCulture) : "";
            draft.IsEditing = true;
            return;
        }
        var barcode = (draft.EditBarcode ?? "").Trim();
        var name = (draft.EditName ?? "").Trim();
        if (barcode.Length == 0 || _variants.Any(v => !ReferenceEquals(v, draft) && string.Equals(v.Barcode.Trim(), barcode, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = barcode.Length == 0
                ? Tr.T("Штрихкод не может быть пустым.", "Штрихкод бош болбошу керек.", "The barcode can't be empty.", "Barkod boş olamaz.", "Shtrix-kod bo'sh bo'lishi mumkin emas.")
                : Tr.T("Такой доп. штрихкод уже есть в списке.", "Мындай кошумча штрихкод тизмеде бар.", "This extra barcode is already in the list.",
                    "Bu ek barkod listede zaten var.", "Bunday qo'shimcha shtrix-kod ro'yxatda bor.");
            return;
        }
        draft.Barcode = barcode;
        draft.Name = name;
        draft.Quantity = ParseNumber(draft.EditQuantity);
        draft.FullName = name.Length == 0 ? barcode : (string.IsNullOrWhiteSpace(_name) ? name : $"{_name.Trim()} {name}");
        draft.IsEditing = false;
        ErrorMessage = "";
    }

    private void RemoveVariant_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VariantDraft draft })
            return;
        _variants.Remove(draft);
        OnPropertyChanged(nameof(HasVariants));
    }

    /// <summary>2026-09-17: не своя экранная клавиатура — пользователь уже один раз (2026-09-15,
    /// см. MainWindow.ToggleKeyboard) явно попросил вернуть системную клавиатуру Windows вместо
    /// самодельной, та же логика применяется и здесь.</summary>
    private void ToggleKeyboard_Click(object? sender, RoutedEventArgs e) =>
        App.GetRequiredService<IOperatingSystemKeyboardService>().ShowSystemKeyboard();

    public string ErrorMessage { get => _errorMessage; private set { _errorMessage = value; OnPropertyChanged(); } }
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; OnPropertyChanged(); } }

    public bool CanSaveNow => !_isSaving && !string.IsNullOrWhiteSpace(_name);

    private static string ExtractPrice(string priceLine)
    {
        var digits = new string((priceLine ?? "").TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ',').ToArray());
        // 2026-10-20, аудит программы владельца: «Цена продажи 85.00» — с точкой, а везде запятая. Показываем с запятой;
        // ParseNumber принимает и запятую, и точку — сохранение не меняется.
        return digits.Replace('.', ',');
    }

    private void GenerateBarcode()
    {
        // EAN-13: случайные первые 12 цифр + контрольная. Простой и достаточный способ
        // получить уникальный штрихкод для товара без своего штрихкода от поставщика.
        // Первая цифра — не 0 (2026-09-05, по просьбе пользователя): ведущий ноль в EAN-13
        // визуально путают с UPC-A и он часто отваливается при вводе/экспорте как "незначащий".
        var rnd = new Random();
        var digits = new int[13];
        digits[0] = rnd.Next(1, 10);
        for (var i = 1; i < 12; i++)
            digits[i] = rnd.Next(0, 10);

        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += digits[i] * (i % 2 == 0 ? 1 : 3);
        digits[12] = (10 - sum % 10) % 10;

        Barcode = string.Concat(digits);
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            ErrorMessage = Tr.T("Введите наименование товара.", "Товардын аталышын киргизиңиз.", "Enter the product name.", "Ürün adını girin.", "Mahsulot nomini kiriting.");
            return;
        }
        if (string.IsNullOrWhiteSpace(_barcode))
        {
            ErrorMessage = Tr.T(
                "Введите штрихкод (или нажмите «Сгенерировать»).",
                "Штрихкодду киргизиңиз (же «Түзүү» баскычын басыңыз).", "Enter a barcode (or click “Generate”).", "Barkodu girin (veya «Oluştur»a basın).", "Shtrix-kodni kiriting (yoki «Yaratish» tugmasini bosing).");
            return;
        }

        _isSaving = true;
        OnPropertyChanged(nameof(CanSaveNow));
        UpdateSaveButtonState();
        ErrorMessage = "";
        StatusMessage = Tr.T("Сохранение...", "Сакталууда...", "Saving...", "Kaydediliyor...", "Saqlanmoqda...");
        try
        {
            int? pluValue = null;
            if (_isWeight)
            {
                if (int.TryParse(_plu.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPlu))
                {
                    pluValue = parsedPlu;
                }
                else
                {
                    // Как на сайте (useAddProductBootstrap/buildProductPayload): пустой PLU у
                    // весового товара автоназначается как count существующих весовых + 1.
                    // 2026-09-09: в офлайне (обычном или автономном) считаем по локальной базе —
                    // GetWeightProductCountAsync всё равно недоступен без сервера.
                    try
                    {
                        var count = OfflineModeHelper.UseLocalOperations
                            ? LocalProductEditor.GetLocalWeightProductCount()
                            : await _catalogApi.GetWeightProductCountAsync().ConfigureAwait(true);
                        pluValue = count + 1;
                        // 2026-09-30: «число весовых + 1» бывает уже занято (товары удаляли, PLU
                        // вписывали руками) — сервер тогда отвечает 400 «Этот PLU уже используется»,
                        // и товар не сохранялся. Берём следующий номер, свободный в каталоге кассы.
                        var usedPlu = CatalogCacheService.Products.Where(p => p.Plu is > 0).Select(p => p.Plu!.Value).ToHashSet();
                        while (usedPlu.Contains(pluValue.Value))
                            pluValue++;
                        Plu = pluValue.Value.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex)
                    {
                        PosLogger.Log($"PLU autogen failed, saving without PLU: {ex}", "WAREHOUSE");
                    }
                }
            }

            var request = new ProductEditRequest
            {
                Name = _name,
                Article = _code,
                Barcode = _barcode,
                // 2026-09-21: список "доп. штрихкодов" (_variants) — единственный источник
                // alternate_barcodes при сохранении (см. doc-comment у _variants.Add в конструкторе
                // и CatalogApiService.BuildAlternateBarcodes). Раньше эти строки создавали отдельные
                // новые товары вместо записи в это же поле — теперь пишут прямо сюда, как на сайте.
                AlternateBarcodes = string.Join("\n", _variants.Select(v => v.Barcode.Trim()).Where(b => b.Length > 0)),
                KnownAlternateBarcodeVariants = _variants
                    .Select(v => new AlternateBarcodeVariant
                    {
                        Barcode = v.Barcode.Trim(),
                        Name = string.IsNullOrWhiteSpace(v.Name) ? null : v.Name.Trim(),
                        Quantity = v.Quantity,
                    })
                    .ToList(),
                CategoryName = _category,
                BrandName = _brand,
                Unit = string.IsNullOrWhiteSpace(_unit) ? "шт" : _unit,
                IsWeight = _isWeight,
                Quantity = ParseNumber(_quantity),
                PurchasePrice = TryParseNumber(_purchasePrice),
                MarkupPercent = TryParseNumber(_markupPercent),
                Price = TryParseNumber(_price),
                Description = _description,
                HotkeyGroup = _hotkeyGroup == "Без горячей клавиши" ? null : _hotkeyGroup,
                Plu = pluValue,
                WholesalePrice = TryParseNumber(_wholesalePrice),
                MinimumQuantity = _minimumQuantityKnown ? TryParseNumber(_minimumQuantity) ?? 0 : null,
                // 2026-10-20: поставщик — только если кассир его выбрал/сменил в этом окне (иначе «client» на сервере не трогаем).
                SupplierId = _supplierChanged ? _supplierId : null,
                DiscountPercent = TryParseNumber(_discountPercent),
                HeightCm = TryParseNumber(_heightCm),
                WidthCm = TryParseNumber(_widthCm),
                DepthCm = TryParseNumber(_depthCm),
                WeightKg = TryParseNumber(_weightKg),
                Country = _country,
                EnablePieceSale = _enablePieceSale,
                PackageQuantity = TryParseNumber(_packageQuantity),
                PackagePiecePrice = TryParseNumber(_packagePiecePrice),
                IsNew = _existing is null,
                Kind = _kind,
                BundleItems = _kind == "bundle" ? _bundleItems.ToList() : null,
            };

            if (OfflineModeHelper.UseLocalOperations)
            {
                // 2026-09-09: офлайн (обычный или автономный режим) — товар пишется прямо в
                // локальную SQLite, минуя ICatalogApiService (реальный HTTP к NurCRM, которого
                // сейчас нет). Никогда не попадёт на сервер — ожидаемо для автономного режима.
                LocalProductEditor.SaveLocally(request, _existing?.Id);
            }
            else if (_existing is null)
            {
                // 2026-09-16, по репорту пользователя ("долгое добавление и зависание") — см.
                // CreateNewProductLocalFirstAsync.
                await CreateNewProductLocalFirstAsync(request).ConfigureAwait(true);
            }
            else
                await _catalogApi.UpdateProductAsync(_existing.Id, request).ConfigureAwait(true);

            Saved = true;
            Close(true);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось сохранить товар: ", "Товарды сактоо мүмкүн болгон жок: ", "Could not save the product: ", "Ürün kaydedilemedi: ", "Mahsulotni saqlab bo'lmadi: ") + ex.Message;
            StatusMessage = "";
            PosLogger.Log($"Product save failed: {ex}", "WAREHOUSE");
        }
        finally
        {
            _isSaving = false;
            OnPropertyChanged(nameof(CanSaveNow));
            UpdateSaveButtonState();
        }
    }

    /// <summary>Мгновенное создание нового товара: короткая попытка создать на сервере (несколько
    /// секунд) — этого достаточно в норме, и товар сразу получает настоящий id без риска
    /// рассинхронизации. Если сеть реально тормозит или недоступна — товар всё равно СРАЗУ
    /// пишется в локальную базу (сканируется и продаётся немедленно), а создание на сервере
    /// продолжается в фоне (CreateOnServerInBackgroundAsync) и подменяет временный локальный id
    /// на настоящий, когда получит ответ. Используется и для основного товара, и для каждого
    /// товара-варианта из "доп. штрихкодов".</summary>
    private async Task CreateNewProductLocalFirstAsync(ProductEditRequest request)
    {
        try
        {
            using var cts = new CancellationTokenSource(FastCreateTimeout);
            var created = await _catalogApi.CreateProductAsync(request, cts.Token).ConfigureAwait(true);
            var realId = ProductCatalogMapper.TryId(created) ?? Guid.NewGuid().ToString("N");
            LocalProductEditor.SaveLocally(request, realId);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
        {
            var tempId = LocalProductEditor.SaveLocally(request, null);
            _ = CreateOnServerInBackgroundAsync(request, tempId);
        }
    }

    /// <summary>Продолжение создания товара на сервере после того, как диалог уже закрылся
    /// локально (см. SaveAsync) — подменяет временный локальный id на настоящий серверный,
    /// когда ответ наконец придёт. Работает уже без окна: используем только _catalogApi
    /// (сервис из DI, не привязан к жизни окна) и статические сервисы каталога.</summary>
    private async Task CreateOnServerInBackgroundAsync(ProductEditRequest request, string tempLocalId)
    {
        try
        {
            var created = await _catalogApi.CreateProductAsync(request).ConfigureAwait(false);
            var realId = ProductCatalogMapper.TryId(created);
            if (string.IsNullOrEmpty(realId) || string.Equals(realId, tempLocalId, StringComparison.OrdinalIgnoreCase))
                return;

            LocalProductEditor.DeleteLocally(tempLocalId);
            LocalProductEditor.SaveLocally(request, realId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Background product create failed for temp id {tempLocalId}: {ex}", "WAREHOUSE");
            CatalogCacheService.RaiseToast(
                Tr.T(
                    "Не удалось отправить новый товар на сервер — он сохранён только локально.",
                    "Жаңы товарды серверге жөнөтүү мүмкүн болгон жок — ал ушул компьютерде гана сакталды.",
                    "Could not send the new product to the server — it was saved locally only.",
                    "Yeni ürün sunucuya gönderilemedi — yalnızca yerel olarak kaydedildi.",
                    "Yangi mahsulotni serverga yuborib bo'lmadi — u faqat shu kompyuterda saqlandi."),
                true);
        }
    }

    private static double ParseNumber(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double? TryParseNumber(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : ParseNumber(s);

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(Saved);

    /// <summary>2026-10-01: тексты блока «Размеры и цвета» (5 языков). Вызывается при открытии окна.</summary>
    private void ApplyVariantsTexts()
    {
        VariantsTitle.Text = Tr.T("Размеры и цвета", "Өлчөмдөр жана түстөр", "Sizes and colors", "Bedenler ve renkler", "O'lchamlar va ranglar");
        VariantsHint.Text = _existing is null
            ? Tr.T("Сначала сохраните товар — потом добавьте размеры и цвета.", "Адегенде товарды сактаңыз — андан кийин өлчөмдөрдү жана түстөрдү кошуңуз.",
                "Save the product first, then add sizes and colors.", "Önce ürünü kaydedin, sonra beden ve renk ekleyin.", "Avval mahsulotni saqlang, so'ng o'lcham va ranglarni qo'shing.")
            : Tr.T("Для магазина одежды: свой остаток и цена у каждого размера/цвета (скидка на размер — цена ниже).",
                "Кийим дүкөнү үчүн: ар бир өлчөм/түстүн өз калдыгы жана баасы (өлчөмгө арзандатуу — баасы төмөн).",
                "For clothing stores: each size/color has its own stock and price (a lower price = a discount).",
                "Giyim mağazası için: her beden/rengin kendi stoku ve fiyatı (düşük fiyat = indirim).",
                "Kiyim do'koni uchun: har bir o'lcham/rangning o'z qoldig'i va narxi (pastroq narx = chegirma).");
        VariantsButton.Content = Tr.T("Размеры и цвета…", "Өлчөмдөр жана түстөр…", "Sizes and colors…", "Bedenler ve renkler…", "O'lchamlar va ranglar…");
        VariantsButton.IsEnabled = _existing is not null && !string.IsNullOrWhiteSpace(_existing.Id);
    }

    private async void VariantsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_existing is null || string.IsNullOrWhiteSpace(_existing.Id) || _catalogApi is null)
            return;
        var editor = new VariantEditorWindow(_catalogApi, _existing.Id, _existing.Title, LocalCartService.ParsePrice(_existing.PriceLine));
        await editor.ShowDialog(this).ConfigureAwait(true);
    }

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        CloseDropdownPanelsIfClickedOutside(e);
        // 2026-10-18, ревью этапа 5: окно без рамки — тянем только мышью; касание пальцем по пустому месту
        // (сенсорный моноблок) должно прокручивать/выбирать вкладку, а не двигать окно.
        if (e.Pointer.Type == Avalonia.Input.PointerType.Mouse && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    /// <summary>2026-09-15, аудит удобства: панели Категория/Бренд/Горячая клавиша — не Popup
    /// (см. комментарий в XAML — обычный Popup у ComboBox не рендерится в окне без системного
    /// хрома), а обычные Border внутри самой формы, поэтому клик мимо них никак их не закрывал.
    /// Кассир открывал список категорий, кликал в соседнее поле продолжить заполнение — панель
    /// оставалась раскрытой и просто раздувала форму, пока по ней не кликали ещё раз явно.</summary>
    private void CloseDropdownPanelsIfClickedOutside(Avalonia.Input.PointerPressedEventArgs e)
    {
        ClosePanelIfClickedOutside(e, CategoryOptionsPanel, CategoryToggleButton);
        ClosePanelIfClickedOutside(e, BrandOptionsPanel, BrandToggleButton);
        ClosePanelIfClickedOutside(e, HotkeyOptionsPanel, HotkeyToggleButton);
    }

    private static void ClosePanelIfClickedOutside(Avalonia.Input.PointerPressedEventArgs e, Border panel, Control toggleButton)
    {
        if (!panel.IsVisible)
            return;
        if (IsPointInsideControl(e, panel) || IsPointInsideControl(e, toggleButton))
            return;
        panel.IsVisible = false;
    }

    private static bool IsPointInsideControl(Avalonia.Input.PointerPressedEventArgs e, Control control)
    {
        var p = e.GetPosition(control);
        return p.X >= 0 && p.Y >= 0 && p.X <= control.Bounds.Width && p.Y <= control.Bounds.Height;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
