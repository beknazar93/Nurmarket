using NurMarketKassa.Core.Application;
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
using AvKey = Avalonia.Input.Key; // 2026-10-24: System.Windows.Input.Key тоже виден
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.AvaloniaHost.Views.Controls;
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
    // 2026-11-01 (аудит 10 из 10, С-01): значения цен при открытии карточки — при сохранении шлём на сервер только изменённые.
    private string _origPurchasePrice = "";
    private string _origMarkupPercent = "";
    private string _origPrice = "";
    // 2026-11-01 (контрольный аудит, С2-03): исходные скидка, опт, описание, страна и вес/габариты — на сервер уходят только изменённые.
    private string _origDiscount = "", _origWholesale = "", _origDescription = "", _origCountry = "", _origDimensions = "";
    private string _hotkeyGroup = "Без горячей клавиши";
    // 2026-11-06 (класс Б, Б-3): значения названия/кода/штрихкода/категории/бренда/единицы/«весовой»/PLU/клавиши/вида при открытии — на сервер уходят только изменённые.
    private string _origName = "", _origCode = "", _origBarcode = "", _origCategory = "", _origBrand = "", _origUnit = "", _origHotkey = "", _origPlu = "", _origKind = "product";
    private bool _origIsWeight;
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
    /// <summary>2026-11-03 (ревью Н-2): запись карточки ещё идёт на сервере (ждали дольше 8 с) — итог придёт плашкой.</summary>
    public bool SavePending { get; private set; }
    /// <summary>2026-11-07 («при каждом изменении товара весь склад загружается заново»): код сохранённого товара на сервере (правка — его id, создание — id из ответа сервера) —
    /// по нему окно обновляет в каталоге только этот товар (CatalogCacheService.RefreshProductsAsync), а не весь каталог. null — товар записан только локально (нет связи / в очереди).</summary>
    public string? SavedProductId { get; private set; }

    /// <summary>2026-09-07, по просьбе пользователя: при добавлении товара по неизвестному
    /// штрихкоду со сканера форма показывает только название/штрихкод/остаток/цены — остальное
    /// (категория, бренд, единица, характеристики и т.д.) можно позже дозаполнить через
    /// обычную карточку товара в Складе. См. OfferAddUnknownProductAsync в MainWindow.Dialogs.cs.</summary>
    public bool IsQuickAddMode { get; }

    /// <summary>2026-10-30 (приёмка 1.17.65, «Права»): закупка, наценка, прибыль и закупка упаковки видны только ролям с правом «видеть закупку»
    /// (как в ревизии склада и во вкладке «Поступления»). Кассиру поля скрыты; внутри окна значение хранится лишь затем, чтобы сохранение
    /// не стёрло закупку на сервере (в привязки она не отдаётся).</summary>
    public bool ShowPurchase { get; } = WarehouseWindow.RevisionShowPurchase;

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
                ? existing.PurchasePrice.ToString("0.####", CultureInfo.InvariantCulture).Replace('.', ',') : ""; // 2026-11-05 (Н-12): запятая, как у цены продажи; 2026-11-02 (аудит №2, С3-07): сервер хранит закупку с 4 знаками
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
            _origPurchasePrice = _purchasePrice;
            _origMarkupPercent = _markupPercent;
            _origPrice = _price;
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

            // Поштучная опция уже есть: цену штуки считаем «ручной», если она не равна цене упаковки ÷ штук.
            if (existing.PieceOption is { QuantityInPackage: > 0, PieceUnitPrice: > 0 } po)
            {
                var autoPiece = Math.Round(LocalCartService.ParsePrice(existing.PriceLine) / po.QuantityInPackage, 2);
                _packManual = Math.Abs(autoPiece - po.PieceUnitPrice) > 0.004;
            }

            if (existing.IsBundle)
            {
                _kind = "bundle";
                if (existing.BundleItems is { Count: > 0 })
                    foreach (var component in existing.BundleItems)
                        _bundleItems.Add(component);
            }
            // 2026-11-01 (контрольный аудит, С2-02): услуга остаётся услугой — раньше правка цены уходила с kind=product, и услуга начинала списываться со склада.
            else if (existing.IsService)
                _kind = "service";
        }
        if (existing != null)
        {
            _origDiscount = _discountPercent;
            _origWholesale = _wholesalePrice;
            _origDescription = _description;
            _origCountry = _country;
            _origDimensions = DimensionsSignature();
            _origName = _name; _origCode = _code; _origBarcode = _barcode; _origCategory = _category; _origBrand = _brand;
            _origUnit = _unit; _origIsWeight = _isWeight; _origHotkey = _hotkeyGroup; _origPlu = _plu; _origKind = _kind;
        }

        GenerateBarcodeCommand = new RelayCommand(GenerateBarcode);

        InitializeComponent();
        // 2026-10-30 (приёмка 1.17.65): без права видеть закупку в ряду цен остаётся только «Цена продажи» (на всю половину ряда, не справа в углу).
        if (!ShowPurchase)
        {
            NarrowRowPrices.ColumnDefinitions = new ColumnDefinitions("*,18,*");
            Grid.SetColumn(SalePriceStack, 0);
        }
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
        // 2026-10-29, жалоба владельца «нет цены, количества и единицы»: обязательные поля помечены «*», у нового товара количество названо
        // «начальный остаток» (в подсказке под полем) — понятно, что это сколько штук (кг, литров) лежит сейчас.
        PackQtyLabel.Text = Tr.T("Штук в упаковке", "Таңгактагы штук саны", "Pieces in a pack", "Paketteki adet", "Qadoqdagi dona soni");
        PackPieceLabel.Text = Tr.T("Цена за штуку", "Штуканын баасы", "Price per piece", "Adet fiyatı", "Dona narxi");
        PackAutoButton.Content = Tr.T("Авто", "Авто", "Auto", "Oto", "Avto");
        PackWholePriceLabel.Text = Tr.T("Цена упаковки (целой)", "Таңгактын баасы (бүтүн)", "Whole pack price", "Tüm paket fiyatı", "Butun qadoq narxi");
        PackWholeBuyLabel.Text = Tr.T("Закупка упаковки", "Таңгактын сатып алуу баасы", "Pack cost", "Paket alış fiyatı", "Qadoq xarid narxi");
        PackGoMainButton.Content = Tr.T("из «Основное» →", "«Негизгиден» →", "from “Main” →", "«Ana»'dan →", "«Asosiy»dan →");
        PackQtyHint.Text = Tr.T("Целое число, не меньше 2", "Бүтүн сан, 2ден кем эмес", "A whole number, 2 or more", "Tam sayı, en az 2", "Butun son, kamida 2");
        PackExample.Text = Tr.T("Пример: пачка 200 сом / 20 шт → штука 10 сом", "Мисал: пачка 200 сом / 20 шт → штука 10 сом", "Example: a pack of 200 som / 20 pcs → a piece is 10 som",
            "Örnek: paket 200 som / 20 adet → adet 10 som", "Misol: pachka 200 so'm / 20 dona → dona 10 so'm");
        foreach (var (title, qty) in new[]
                 {
                     (Tr.T("Пачка 20", "Пачка 20", "Pack of 20", "20'li paket", "20 talik pachka"), "20"),
                     (Tr.T("Упаковка 6", "Таңгак 6", "Pack of 6", "6'lı paket", "6 talik qadoq"), "6"),
                     (Tr.T("Упаковка 12", "Таңгак 12", "Pack of 12", "12'li paket", "12 talik qadoq"), "12"),
                     (Tr.T("Ящик 24", "Ящик 24", "Case of 24", "24'lü koli", "24 talik quti"), "24"),
                     (Tr.T("Блок 10", "Блок 10", "Block of 10", "10'lu blok", "10 talik blok"), "10"),
                 })
        {
            var chip = new Button { Content = title, Tag = qty, Height = 40, MinHeight = 40, Margin = new Thickness(0, 0, 8, 6) };
            chip.Classes.Add("packChip");
            chip.Click += PackTemplate_Click;
            PackTemplates.Children.Add(chip);
        }
        UnitFieldLabel.Text = Tr.T("Единица измерения *", "Өлчөө бирдиги *", "Unit *", "Birim *", "O'lchov birligi *");
        QuantityFieldLabel.Text = existing is null
            ? Tr.T("Количество *", "Саны *", "Quantity *", "Miktar *", "Miqdor *")
            : Tr.T("Остаток *", "Калдык *", "Stock *", "Stok *", "Qoldiq *");
        SalePriceFieldLabel.Text = Tr.T("Цена продажи *", "Сатуу баасы *", "Sale price *", "Satış fiyatı *", "Sotuv narxi *");
        InitSerialsBlock(existing); // 2026-11-13 (сервер sn1311): серийники и гарантия товара — только сфера «Электроника»
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
        // 2026-10-22 (модалки, пакет 3): у нового товара фокус сразу в «Название» (ModalShell.InitialFocus); скан со сканера
        // окно и так перехватывает в поле «Штрихкод» (OnBarcodeScanned).
        if (_existing is null)
            Shell.InitialFocus = NameBox;
        // 2026-10-03: скан прямо в поле штрихкода заканчивается Enter — сразу ищем в базе NurCRM.
        // 2026-10-29, владелец «в штрихкод сделай только number»: «Штрихкод» и «Доп. штрихкод» принимают только цифры (ввод, скан, вставка).
        BarcodeInput.Attach(this, tb => ReferenceEquals(tb, BarcodeBox) || tb.Classes.Contains("scan-direct"));
        BarcodeBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                e.Handled = true;
                _ = FillNameFromGlobalBaseAsync(Barcode);
            }
        };
        // 2026-10-02, вкладки карточки: история закупок грузится при первом показе вкладки (запрос к серверу, карточку открывают часто).
        // 2026-10-30: вкладка стала «Поступления» (партии, поставщик, накладная) — ProductReceiptsView.
        ProductTabs.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(ProductTabs.SelectedItem, HistoryTab))
                _receiptsView?.EnsureLoaded();
        };
        Closed += (_, _) => _receiptsView?.Stop();
        // 2026-10-30: «Поступления и партии» из меню строки товара — карточка сразу открывается на этой вкладке.
        Opened += (_, _) =>
        {
            if (StartOnReceiptsTab && HistoryTab.IsVisible)
                ProductTabs.SelectedItem = HistoryTab;
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
        var focused = FocusManager?.GetFocusedElement();
        // 2026-10-29: «Доп. штрихкод» (scan-direct) — как и основной «Штрихкод»: код сканера печатается прямо в поле. Раньше скан здесь
        // «возвращал» поле к прежнему тексту и уходил в основной «Штрихкод», а в «Доп. штрихкод» не попадало ничего.
        if (ReferenceEquals(focused, BarcodeBox) || focused is TextBox { Classes: var cls } && cls.Contains("scan-direct"))
            return;

        // 2026-10-24, владелец: «в полях (наименование, штрихкод, цена) вводится только 2 символа и перестаёт» —
        // раньше клавиши всех полей шли сканеру, он глушил символы с 3-го быстрого (человек печатает 80–200 мс).
        // В правимом поле символы не трогаем; скан — только Enter после строгой серии (как поиск кассы).
        if (focused is TextBox { IsReadOnly: false } box && _barcodeInputService is NurMarketKassa.AvaloniaHost.Services.AvaloniaKeyboardWedgeBarcodeService wedge)
        {
            FeedScannerFromField(box, wedge, e);
            return;
        }

        _barcodeInputService.ProcessKeyDown(e);
    }

    private const int FieldScanIntervalMs = 50;
    private const int FieldScanEnterMs = 150;
    private int _fieldScanFastRun;
    private long _fieldScanLastTick;
    private ulong? _fieldScanLastRaw;
    private TextBox? _fieldScanBox;
    private string? _fieldScanSnapshot;
    private int _fieldScanCaret;

    /// <summary>2026-10-24: клавиши правимого поля — и сканеру, но символы остаются в поле. Скан = ≥ 4 знака подряд быстрее
    /// 50 мс (по времени нажатия) и Enter сразу за ними (до 150 мс): поле возвращается к тексту до серии, код уходит в «Штрихкод».
    /// Иначе Enter не трогаем, а набранное сканером забываем (текст «Наименования» больше не попадает в «Штрихкод»).</summary>
    private void FeedScannerFromField(TextBox box, NurMarketKassa.AvaloniaHost.Services.AvaloniaKeyboardWedgeBarcodeService wedge, Avalonia.Input.KeyEventArgs e)
    {
        var now = Environment.TickCount64;
        if (e.Key == AvKey.Enter)
        {
            var rawEnter = NurMarketKassa.AvaloniaHost.Services.AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp();
            long sinceLast = rawEnter is { } re && _fieldScanLastRaw is { } le && re >= le ? (long)Math.Min(re - le, long.MaxValue) : now - _fieldScanLastTick;
            var fresh = sinceLast is >= 0 and < FieldScanEnterMs;
            if (_fieldScanFastRun >= 3 && fresh && ReferenceEquals(box, _fieldScanBox) && wedge.HasCompleteScan)
            {
                // Первые знаки скана успели напечататься в поле — возвращаем его как было.
                box.Text = _fieldScanSnapshot;
                box.CaretIndex = Math.Min(_fieldScanCaret, box.Text?.Length ?? 0);
                _fieldScanFastRun = 0;
                wedge.ProcessKeyDown(e); // готовый код → BarcodeScanned → OnBarcodeScanned («Штрихкод»)
                e.Handled = true;
                return;
            }
            wedge.DiscardBuffer();
            _fieldScanFastRun = 0;
            return;
        }

        var mods = e.KeyModifiers;
        if (e.Key is AvKey.LeftShift or AvKey.RightShift && mods == KeyModifiers.Shift)
            return; // одинокий Shift (сканер шлёт его между буквами кода) серию не рвёт
        var isScanKey = !mods.HasFlag(KeyModifiers.Control) && !mods.HasFlag(KeyModifiers.Alt) && !mods.HasFlag(KeyModifiers.Meta)
            && (e.Key is >= AvKey.D0 and <= AvKey.D9 or >= AvKey.NumPad0 and <= AvKey.NumPad9 or >= AvKey.A and <= AvKey.Z
                or AvKey.Space or AvKey.OemMinus or AvKey.Subtract or AvKey.OemPeriod or AvKey.Decimal);
        if (!isScanKey)
        {
            _fieldScanFastRun = 0;
            _fieldScanBox = null;
            wedge.ProcessKeyDown(e);
            return;
        }

        var raw = NurMarketKassa.AvaloniaHost.Services.AvaloniaKeyboardWedgeBarcodeService.KeyPressTimestamp();
        long delta = raw is { } r && _fieldScanLastRaw is { } lr && r >= lr ? (long)Math.Min(r - lr, long.MaxValue) : now - _fieldScanLastTick;
        if (ReferenceEquals(box, _fieldScanBox) && delta is >= 0 and < FieldScanIntervalMs)
            _fieldScanFastRun++;
        else
        {
            _fieldScanFastRun = 0;
            _fieldScanBox = box;
            _fieldScanSnapshot = box.Text;
            _fieldScanCaret = box.CaretIndex;
        }
        _fieldScanLastTick = now;
        _fieldScanLastRaw = raw;
        var handled = e.Handled;
        wedge.ProcessKeyDown(e);
        e.Handled = handled; // символ остаётся в поле — набор не страдает
    }

    /// <summary>2026-10-04, Android: камера аппарата как сканер — код приходит как скан USB-сканера (OnBarcodeScanned).</summary>
    private async void CameraScanButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await NurMarketKassa.AvaloniaHost.Services.CameraScan.ScanIntoKassaAsync().ConfigureAwait(true);

    private void OnBarcodeScanned(string barcode)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            // 2026-10-29: в поле «Штрихкод» — только цифры. Скан не из цифр (чужая раскладка, «реакр») не принимается; пробелы и дефисы убираются.
            var clean = (barcode ?? "").Replace(" ", "").Replace("-", "");
            if (clean.Length == 0 || !clean.All(char.IsAsciiDigit))
                return;
            // 2026-11-01 (контрольный аудит, С2-12): у СУЩЕСТВУЮЩЕГО товара скан (например, «посмотреть» другой товар) не должен молча подменять его собственный
            // штрихкод — спрашиваем. Скан, который совпадает с текущим кодом, и скан в пустое поле принимаются сразу.
            if (_existing is not null && !string.IsNullOrWhiteSpace(Barcode) && !string.Equals(Barcode.Trim(), clean, StringComparison.Ordinal))
            {
                var replace = await PosConfirmDialog.ShowModalAsync(this,
                    Tr.T("Заменить штрихкод?", "Штрихкодду алмаштырабызбы?", "Replace the barcode?", "Barkod değiştirilsin mi?", "Shtrix-kod almashtirilsinmi?"),
                    Tr.T($"У товара «{ProductName}» штрихкод {Barcode.Trim()}. Отсканирован {clean}. Заменить? После «Сохранить» старый код перестанет находить этот товар.",
                        $"«{ProductName}» товарынын штрихкоду {Barcode.Trim()}. Сканерленди: {clean}. Алмаштырабызбы? «Сактоодон» кийин эски код бул товарды таппай калат.",
                        $"The product “{ProductName}” has barcode {Barcode.Trim()}. Scanned: {clean}. Replace it? After Save the old code will no longer find this product.",
                        $"«{ProductName}» ürününün barkodu {Barcode.Trim()}. Okutulan: {clean}. Değiştirilsin mi? Kaydettikten sonra eski kod bu ürünü bulamaz.",
                        $"«{ProductName}» mahsulotining shtrix-kodi {Barcode.Trim()}. Skanerlandi: {clean}. Almashtirilsinmi? «Saqlash»dan keyin eski kod bu mahsulotni topa olmaydi."),
                    Tr.T("Заменить", "Алмаштыруу", "Replace", "Değiştir", "Almashtirish"), Tr.T("Оставить", "Калтыруу", "Keep", "Koru", "Qoldirish")).ConfigureAwait(true);
                if (!replace)
                    return;
            }
            barcode = clean;
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
            // 2026-10-27, встроенный справочник готовых наименований: локальная копия отвечает сразу — без сервера и интернета.
            // Поля заполняем не дожидаясь запросов к складу и общей базе (они бывают медленными); то, что кассир уже ввёл, не трогаем.
            var quick = await Task.Run(() => NurMarketKassa.Services.GlobalProducts.GlobalProductCatalog.Instance.Find(code)).ConfigureAwait(true);
            if (quick is not null && string.Equals(Barcode?.Trim(), code, StringComparison.Ordinal))
            {
                var quickCategory = string.IsNullOrWhiteSpace(quick.Category) ? null : quick.Category;
                var quickBrand = string.IsNullOrWhiteSpace(quick.Brand) ? null : quick.Brand;
                if (string.IsNullOrWhiteSpace(ProductName))
                    ProductName = quick.Name;
                if (string.IsNullOrWhiteSpace(Category) && quickCategory != null)
                    Category = quickCategory;
                if (string.IsNullOrWhiteSpace(Brand) && quickBrand != null)
                    Brand = quickBrand;
                GlobalFound = true;
                // 2026-10-27 (приёмка 1.17.63, Д-11): у части кодов справочника есть только название (бренд есть примерно у 5 % записей, категория — у 16 %) —
                // говорим об этом, а не молчим: поле остаётся пустым, его заполняют вручную.
                var noBrandCategory = quickBrand is null && quickCategory is null
                    ? Tr.T("бренда и категории", "бренд жана категория", "brand and category", "marka ve kategori", "brend va kategoriya")
                    : quickBrand is null ? Tr.T("бренда", "бренд", "brand", "marka", "brend")
                    : quickCategory is null ? Tr.T("категории", "категория", "category", "kategori", "kategoriya") : null;
                var missingNote = noBrandCategory is null ? "" : Tr.T(
                    $"; {noBrandCategory} в справочнике нет — заполните вручную",
                    $"; маалымдамада {noBrandCategory} жок — кол менен толтуруңуз",
                    $"; the catalog has no {noBrandCategory} — fill in by hand",
                    $"; katalogda {noBrandCategory} yok — elle doldurun",
                    $"; ma'lumotnomada {noBrandCategory} yo'q — qo'lda to'ldiring");
                GlobalStatus = "✓ " + Tr.T("Из справочника NurCRM: ", "NurCRM маалымдамасынан: ", "From the NurCRM catalog: ", "NurCRM kataloğundan: ", "NurCRM ma'lumotnomasidan: ")
                               + string.Join(" · ", new[] { quick.Name, quickCategory, quickBrand }.Where(x => !string.IsNullOrWhiteSpace(x)))
                               + missingNote
                               + Tr.T(". Можно поправить; укажите цены и количество.", ". Оңдосоңуз болот; бааларды жана санын көрсөтүңүз.", ". You can edit it; enter the prices and quantity.", ". Düzenleyebilirsiniz; fiyatları ve miktarı girin.", ". Tahrirlash mumkin; narx va miqdorni kiriting.");
                PosLogger.Log($"Карточка товара: по штрихкоду {code} из справочника NurCRM (локальная копия) подставлены название, категория и бренд.", "CATALOG");
            }

            // 2026-10-03: тот же код в другой записи (UPC-A 12 цифр ↔ EAN-13 с нулём) — см. BarcodeForms.
            // 2026-10-03, владелец: «он не находит» — находил, но через 2,5 минуты: сервер NurCRM отвечал ~40 с на
            // запрос, а склад и общая база спрашивались по очереди (до 4 запросов). Теперь все запросы — сразу.
            var alternate = BarcodeForms.Alternate(code);
            var api = _catalogApi;
            var ambiguousBarcode = false; // 2026-11-11 (ответ Умара): сервер ответил «неоднозначный штрихкод» — код уже у нескольких товаров
            Task<System.Text.Json.JsonElement?> Safe(Func<Task<System.Text.Json.JsonElement?>> call) => Task.Run(async () =>
            {
                try { return await call().ConfigureAwait(false); }
                catch (AmbiguousBarcodeException) { ambiguousBarcode = true; return null; }
                catch (Exception ex) { PosLogger.Log($"Карточка товара: запрос по штрихкоду не удался: {ex.Message}", "DEBUG"); return null; }
            });
            var ownTask = Safe(() => api.FindWarehouseProductByBarcodeAsync(code));
            var ownAltTask = alternate != null ? Safe(() => api.FindWarehouseProductByBarcodeAsync(alternate)) : Task.FromResult<System.Text.Json.JsonElement?>(null);
            var globalTask = Safe(() => api.FindGlobalProductByBarcodeAsync(code));
            var globalAltTask = alternate != null ? Safe(() => api.FindGlobalProductByBarcodeAsync(alternate)) : Task.FromResult<System.Text.Json.JsonElement?>(null);
            var all = Task.WhenAll(ownTask, ownAltTask, globalTask, globalAltTask);
            if (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(4))).ConfigureAwait(true) != all && GlobalFound != true)
                GlobalStatus = Tr.T("Сервер NurCRM отвечает медленно, жду…", "NurCRM сервери жай жооп берип жатат, күтүп жатам…", "The NurCRM server is slow, waiting…",
                    "NurCRM sunucusu yavaş yanıt veriyor, bekleniyor…", "NurCRM serveri sekin javob bermoqda, kutilmoqda…");
            await all.ConfigureAwait(true);
            // 2026-11-11 (разбор «Кыргызстанские штрихкоды», п. 4): «уже есть на складе» и подстановка из общей базы — только если в ответе ИМЕННО этот код
            // (основной или доп.); ответ сервера про другой товар/код отбрасываем, как в приёмке.
            var own = PurchaseReceivingService.OnlyIfHasCode(ownTask.Result, code, requireField: true)
                      ?? PurchaseReceivingService.OnlyIfHasCode(ownAltTask.Result, code, requireField: true);
            if (ambiguousBarcode && own is null)
            {
                ErrorMessage = AmbiguousBarcodeException.FriendlyText;
                GlobalStatus = "";
                return;
            }
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

            var global = PurchaseReceivingService.OnlyIfHasCode(globalTask.Result, code, requireField: false)
                         ?? PurchaseReceivingService.OnlyIfHasCode(globalAltTask.Result, code, requireField: false);
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

    /// <summary>2026-10-30: открыть карточку сразу на вкладке «Поступления» (меню строки товара на складе).</summary>
    public bool StartOnReceiptsTab { get; set; }

    private ProductReceiptsView? _receiptsView;

    private void SetupPurchaseHistory()
    {
        // Только у существующего товара: у нового истории ещё нет.
        if (_existing is null || IsQuickAddMode || string.IsNullOrWhiteSpace(_existing.Id))
            return;

        // 2026-10-30: закупочные цены и поставщики — только ролям с правом (как «Закупка» в ревизии склада); остальным — дата, количество, остаток партии.
        var showMoney = WarehouseWindow.RevisionShowPurchase;
        var productId = _existing.Id;
        HistoryTab.Header = Tr.T("Поступления", "Кирүүлөр", "Receipts", "Girişler", "Kirimlar");
        var api = new NurMarketKassa.Services.Api.SupplierApiService(App.GetRequiredService<NurMarketKassa.Services.NurMarketApiClient>());
        var view = new ProductReceiptsView(productId, _existing.Quantity, "", showMoney,
            (progress, ct) => ProductReceiptsService.LoadAsync(api, productId, null, progress, ct, showMoney,
                new[] { _existing.Barcode }.Concat((_existing.AlternateBarcodeVariants ?? new()).Select(v => v.Barcode)).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList())); // 2026-10-30: + совпадение по штрихкоду и доп. штрихкодам
        view.OpenReceipt = (row, filePath) => ProductReceiptsActions.OpenReceiptAsync(this, api, row, productId, filePath);
        view.OpenSupplier = row => ProductReceiptsActions.OpenSupplierAsync(this, api, row);
        _receiptsView = view;
        ReceiptsHost.Child = view;
        HistoryTab.IsVisible = true;
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

}
