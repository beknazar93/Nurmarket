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

    public sealed class VariantDraft
    {
        public required string Barcode { get; init; }
        /// <summary>Название ВАРИАНТА (например, "клубничный") — то, что уходит в
        /// alternate_barcodes[].name и комбинируется с названием товара при сканировании
        /// (2026-09-21). Не путать с FullName ниже.</summary>
        public required string Name { get; init; }
        /// <summary>Название товара + название варианта — только для показа в списке уже
        /// добавленных штрихкодов, вычисляется один раз в момент добавления/загрузки (не
        /// live-реактивно к последующему редактированию основного названия товара).</summary>
        public required string FullName { get; init; }
        public double Quantity { get; init; }
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
        DataContext = this;
        SetupPurchaseHistory();
        BuildHotkeyOptions();
        BuildCategoryOptions();
        BuildBrandOptions();
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
    private async Task FillNameFromGlobalBaseAsync(string? barcode)
    {
        var code = barcode?.Trim() ?? "";
        if (_existing is not null || _catalogApi is null || code.Length < 8 || !string.IsNullOrWhiteSpace(ProductName))
            return;

        try
        {
            if (await _catalogApi.FindWarehouseProductByBarcodeAsync(code).ConfigureAwait(true) is { } own)
            {
                var ownName = own.TryGetProperty("name", out var n) ? n.GetString() : null;
                ErrorMessage = Tr.T($"Товар с этим штрихкодом уже есть на складе: {ownName}.",
                    $"Бул штрихкоддогу товар кампада бар: {ownName}.",
                    $"A product with this barcode is already in stock: {ownName}.",
                    $"Bu barkodlu ürün zaten depoda var: {ownName}.",
                    $"Bu shtrix-kodli mahsulot omborda bor: {ownName}.");
                return;
            }

            if (await _catalogApi.FindGlobalProductByBarcodeAsync(code).ConfigureAwait(true) is { } global
                && global.TryGetProperty("name", out var gn) && !string.IsNullOrWhiteSpace(gn.GetString())
                && string.IsNullOrWhiteSpace(ProductName)
                && string.Equals(Barcode?.Trim(), code, StringComparison.Ordinal))
            {
                ProductName = gn.GetString()!.Trim();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Карточка товара: общая база по штрихкоду {code} недоступна: {ex.Message}", "DEBUG");
        }
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
            PurchaseHistoryStatus.Text = Tr.T("История закупок не загрузилась: ", "Тарых жүктөлгөн жок: ",
                "Purchase history failed to load: ", "Alım geçmişi yüklenemedi: ", "Xaridlar tarixi yuklanmadi: ") + ex.Message;
            return;
        }

        var som = Tr.T("сом", "сом", "som", "som", "so'm");
        PurchaseHistoryList.ItemsSource = history.Select(h =>
        {
            var unit = string.IsNullOrWhiteSpace(h.Unit) ? "шт" : h.Unit;
            var details = new List<string>();
            if (h.SalePrice is { } sale && sale > 0)
                details.Add(Tr.T("продажа", "сатуу", "sale", "satış", "sotuv") + $" {sale:0.##} {som}");
            if (!string.IsNullOrWhiteSpace(h.SupplierName))
                details.Add(Tr.T("поставщик", "жеткирүүчү", "supplier", "tedarikçi", "yetkazib beruvchi") + " " + h.SupplierName);
            if (!string.IsNullOrWhiteSpace(h.Employee))
                details.Add(Tr.T("принял", "кабыл алган", "received by", "teslim alan", "qabul qildi") + " " + h.Employee);
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
            ? Tr.T("Закупок этого товара пока не было.", "Бул товар азырынча сатылып алынган эмес.",
                "No purchases of this product yet.", "Bu ürün henüz alınmadı.", "Bu mahsulot hali xarid qilinmagan.")
            : Tr.T("Последняя цена закупки", "Акыркы сатып алуу баасы", "Last purchase price", "Son alış fiyatı", "Oxirgi xarid narxi")
              + $": {history[0].PurchasePrice:0.##} {som}  ·  "
              + Tr.T("закупок", "сатып алуулар", "purchases", "alım", "xaridlar") + $": {history.Count}";
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
        HotkeyCurrentText.Text = _hotkeyGroup;
        HotkeyOptionsList.Items.Clear();
        foreach (var option in HotkeyValues)
        {
            var button = new Button
            {
                Classes = { "SecondaryButton" },
                Content = option,
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
            Tr.T("Выберите категорию", "Категорияны тандаңыз", "Choose a category", "Kategori seçin", "Kategoriyani tanlang"), CategoryOption_Click);

    private void BuildBrandOptions() =>
        BuildReferenceOptions(BrandCurrentText, BrandOptionsList, _brandOptions, Brand,
            Tr.T("Выберите бренд", "Брендди тандаңыз", "Choose a brand", "Marka seçin", "Brendni tanlang"), BrandOption_Click);

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
                    ? Tr.T("Добавление товара", "Товар кошуу", "Adding product", "Ürün ekleme", "Mahsulot qo'shish")
                    : Tr.T("Редактирование товара", "Товарды түзөтүү", "Editing product", "Ürün düzenleme", "Mahsulotni tahrirlash"),
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

    public string HeaderText => _existing is null ? Tr.T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot") : Tr.T("Редактирование товара", "Товарды түзөтүү", "Editing product", "Ürün düzenleme", "Mahsulotni tahrirlash");
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
        ? Tr.T("При сканировании в чеке будет: «", "Сканерленгенде чекте болот: «", "On scan, the receipt line will read: \"", "Okutulduğunda fişte şöyle görünür: \"", "Skanerlanganda chekda shunday bo'ladi: \"") + VariantFullNamePreview
          + Tr.T("»", "»", "\"", "\"", "\"")
        : "";

    public ObservableCollection<VariantDraft> Variants => _variants;
    public bool HasVariants => _variants.Count > 0;
    public string Category { get => _category; set { _category = value; OnPropertyChanged(); } }
    public string Brand { get => _brand; set { _brand = value; OnPropertyChanged(); } }
    public string Unit { get => _unit; set { _unit = value; OnPropertyChanged(); } }
    public bool IsWeight { get => _isWeight; set { _isWeight = value; OnPropertyChanged(); } }
    public string Quantity { get => _quantity; set { _quantity = value; OnPropertyChanged(); } }
    public string PurchasePrice { get => _purchasePrice; set { _purchasePrice = value; OnPropertyChanged(); } }

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
        Price = (purchase * (1 + markup.Value / 100.0)).ToString("0.##", CultureInfo.InvariantCulture);
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
                "Variant uchun shtrix-kod va nom kiriting.");
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
        return digits.Replace(',', '.');
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
                "Штрихкодду киргизиңиз (же «Түзүү» баскычын басыңыз).");
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
                    "Жаңы товар серверге жөнөтүлгөн жок — ал жөн гана жергиликтүү сакталды.",
                    "Could not send the new product to the server — it was saved locally only.",
                    "Yeni ürün sunucuya gönderilemedi — yalnızca yerel olarak kaydedildi.",
                    "Yangi mahsulot serverga yuborilmadi — u faqat lokal saqlandi."),
                true);
        }
    }

    private static double ParseNumber(string? s) =>
        double.TryParse((s ?? "").Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double? TryParseNumber(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : ParseNumber(s);

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close(Saved);

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        CloseDropdownPanelsIfClickedOutside(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
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
