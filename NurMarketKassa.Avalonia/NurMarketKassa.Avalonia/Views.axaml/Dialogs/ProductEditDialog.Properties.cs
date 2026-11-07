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

// 2026-11-06: В15г — часть класса ProductEditDialog (Properties): перенесено из ProductEditDialog.axaml.cs без изменений логики.
public partial class ProductEditDialog
{

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
    public string Barcode { get => _barcode; set { _barcode = value; OnPropertyChanged(); OnPropertyChanged(nameof(BarcodeHint)); OnPropertyChanged(nameof(HasBarcodeHint)); } }
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
    // 2026-10-29: выбрали кг/г у НОВОГО товара — «Весовой» включается сам (как у весового товара на сайте), пока человек сам не тронул переключатель.
    private bool _weightTouched;
    private bool _autoWeightChange;

    public string Unit
    {
        get => _unit;
        set
        {
            _unit = value;
            OnPropertyChanged();
            RaiseQuantityHint();
            if (_existing is null && !_weightTouched && ShowWeightToggle)
            {
                _autoWeightChange = true;
                IsWeight = UnitCatalog.IsWeightUnit(_unit);
                _autoWeightChange = false;
            }
        }
    }

    public bool IsWeight
    {
        get => _isWeight;
        set
        {
            if (!_autoWeightChange)
                _weightTouched = true;
            _isWeight = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowWeightToggle));
            RaiseQuantityHint();
        }
    }

    /// <summary>2026-10-29: подсказка под единицей — когда количество дробное (0,5 кг, 2,25 л), а когда только целое (шт, уп, пара).</summary>
    public string QuantityHint
    {
        get
        {
            var unit = UnitCatalog.Display(_unit);
            var start = _existing is null
                ? Tr.T("Начальный остаток. ", "Баштапкы калдык. ", "Opening stock. ", "Başlangıç stoku. ", "Boshlang'ich qoldiq. ")
                : "";
            return start + UnitCatalog.QuantityDecimals(_unit, _isWeight) switch
            {
                0 => Tr.T($"«{unit}» — количество целое: 1, 2, 24.", $"«{unit}» — сан бүтүн: 1, 2, 24.", $"“{unit}” — whole quantity: 1, 2, 24.",
                    $"«{unit}» — tam sayı miktar: 1, 2, 24.", $"«{unit}» — butun miqdor: 1, 2, 24."),
                3 => Tr.T($"«{unit}» — можно дробное количество, например 0,5 или 2,25.", $"«{unit}» — бөлчөк сан мүмкүн, мисалы 0,5 же 2,25.",
                    $"“{unit}” — fractional quantity allowed, e.g. 0.5 or 2.25.", $"«{unit}» — ondalık miktar olabilir, örn. 0,5 veya 2,25.",
                    $"«{unit}» — kasr miqdor mumkin, masalan 0,5 yoki 2,25."),
                _ => start.Length > 0 ? start.TrimEnd() : "",
            };
        }
    }

    public bool HasQuantityHint => QuantityHint.Length > 0;

    private void RaiseQuantityHint()
    {
        OnPropertyChanged(nameof(QuantityHint));
        OnPropertyChanged(nameof(HasQuantityHint));
        OnPropertyChanged(nameof(ProfitText));
        OnPropertyChanged(nameof(HasProfitText));
    }

    /// <summary>2026-10-29: «Наценка 25 % · прибыль с единицы 20 сом · с остатка 480 сом» — показывается, когда введены закупка и цена продажи.</summary>
    public string ProfitText
    {
        get
        {
            var purchase = TryParseNumber(_purchasePrice);
            var sale = TryParseNumber(_price);
            if (!ShowPurchase || purchase is not > 0 || sale is not > 0) // 2026-10-30: прибыль/наценка выдают закупку — кассиру не показываем
                return "";
            var profit = sale.Value - purchase.Value;
            var markup = (sale.Value / purchase.Value - 1) * 100;
            var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            string F(double v) => v.ToString("0.##", ru);
            var text = Tr.T($"Наценка {F(markup)} % · прибыль с единицы {F(profit)} сом", $"Үстөк {F(markup)} % · бирдигинен пайда {F(profit)} сом",
                $"Markup {F(markup)}% · profit per unit {F(profit)} som", $"Kâr oranı {F(markup)} % · birim başına kâr {F(profit)} som",
                $"Ustama {F(markup)} % · birlikdan foyda {F(profit)} so'm");
            var qty = TryParseNumber(_quantity);
            if (qty is > 0)
                text += Tr.T($" · с остатка {F(profit * qty.Value)} сом", $" · калдыктан {F(profit * qty.Value)} сом", $" · on stock {F(profit * qty.Value)} som",
                    $" · stoktan {F(profit * qty.Value)} som", $" · qoldiqdan {F(profit * qty.Value)} so'm");
            return text;
        }
    }

    public bool HasProfitText => ProfitText.Length > 0;

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-13): в сфере «Одежда» переключатель «Весовой» (и PLU) не нужен —
    /// одежду не взвешивают, а случайно включённый «Весовой» ломал продажу. Если товар уже весовой — переключатель остаётся,
    /// чтобы его можно было выключить.</summary>
    // 2026-10-07, владелец (снимок окна товара в режиме «Одежда»): «где при добавлении тумблер «Весовой», как на вебе?» —
    // переключатель снова виден во всех режимах магазина, как на сайте NurCRM.
    public bool ShowWeightToggle => !IsQuickAddMode;
    public string Quantity { get => _quantity; set { _quantity = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProfitText)); OnPropertyChanged(nameof(HasProfitText)); } }
    public string PurchasePrice { get => ShowPurchase ? _purchasePrice : ""; set { _purchasePrice = value; OnPropertyChanged(); RaisePriceWarning(); } }

    /// <summary>2026-10-05, владелец: «если во время редактирования или создания товара закупочная цена превышает
    /// продажную — там должно быть красное предупреждение». Пусто — цены в порядке (или одна из них не введена).</summary>
    public string PriceWarning
    {
        get
        {
            var purchase = TryParseNumber(_purchasePrice);
            var sale = TryParseNumber(_price);
            if (!ShowPurchase || purchase is not > 0 || sale is not > 0 || purchase.Value <= sale.Value) // 2026-10-30: предупреждение называет закупку — только по праву
                return "";
            var p = purchase.Value.ToString("0.####", CultureInfo.InvariantCulture);
            var s = sale.Value.ToString("0.##", CultureInfo.InvariantCulture);
            var loss = (purchase.Value - sale.Value).ToString("0.####", CultureInfo.InvariantCulture);
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
        if (_enablePieceSale && !_packManual && !_packSuppress)
            PackAutoPiece(); // цена или закупка упаковки изменились — цена штуки (если её не правили руками) пересчитывается
        RaisePack();
        OnPropertyChanged(nameof(PriceWarning));
        OnPropertyChanged(nameof(HasPriceWarning));
        OnPropertyChanged(nameof(ProfitText));
        OnPropertyChanged(nameof(HasProfitText));
    }

    /// <summary>2026-09-07, по просьбе пользователя: цена продажи и наценка держатся в
    /// синхроне друг с другом — ввод одного пересчитывает другое от текущей цены закупки.
    /// _suppressPriceSync не даёт зациклиться, когда один сеттер программно меняет другой.</summary>
    public string MarkupPercent
    {
        get => ShowPurchase ? _markupPercent : ""; // 2026-10-30: наценка вместе с ценой продажи выдаёт закупку
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
    // ------------------------------------------------------------------ 2026-10-29: «Упаковка» (поштучная продажа), как в мобилке (product_packages.dart)
    // Сам товар — упаковка: его цена продажи (вкладка «Основное») — цена целой упаковки, закупка — закупка упаковки. Здесь — сколько штук в упаковке
    // и цена штуки: «сама» = цена упаковки ÷ штук (до копейки), пока её не правили руками; «Авто» возвращает расчёт.
    private bool _packManual;
    private bool _packSuppress;
    private const double PackMismatchLimit = 0.05;

    public bool EnablePieceSale
    {
        get => _enablePieceSale;
        set
        {
            _enablePieceSale = value;
            OnPropertyChanged();
            // Включили галочку — поля сразу заполнены: штук по умолчанию 10, цена штуки считается сама.
            if (value && string.IsNullOrWhiteSpace(_packageQuantity))
            {
                _packManual = false;
                PackageQuantity = "10";
            }
            else if (value && !_packManual)
                PackAutoPiece();
            RaisePack();
        }
    }

    public string PackageQuantity
    {
        get => _packageQuantity;
        set
        {
            _packageQuantity = value;
            OnPropertyChanged();
            if (!_packManual)
                PackAutoPiece();
            RaisePack();
        }
    }

    public string PackagePiecePrice
    {
        get => _packagePiecePrice;
        set
        {
            _packagePiecePrice = value;
            OnPropertyChanged();
            if (!_packSuppress)
                _packManual = true; // цену штуки вписали руками — «сама» больше не пересчитывается
            RaisePack();
        }
    }

    public bool PackIsManual => _packManual;

    private void PackAutoPiece()
    {
        var pack = TryParseNumber(_price) ?? 0;
        var qty = TryParseNumber(_packageQuantity) ?? 0;
        var auto = qty >= 2 && pack > 0 ? Math.Round(pack / qty, 2) : 0;
        _packSuppress = true;
        PackagePiecePrice = auto > 0 ? auto.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU")) : "";
        _packSuppress = false;
    }

    private void PackAuto_Click(object? sender, RoutedEventArgs e)
    {
        _packManual = false;
        PackAutoPiece();
        RaisePack();
    }

    private void PackTemplate_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string qty })
            PackageQuantity = qty;
    }

    private void RaisePack()
    {
        foreach (var name in new[] { nameof(PackIsManual), nameof(PackBaseText), nameof(PackPieceInfo), nameof(HasPackPieceInfo), nameof(PackWarning), nameof(HasPackWarning),
                     nameof(PackWholePrice), nameof(PackWholeBuy), nameof(PackCalcOk), nameof(PackCalcWarn), nameof(PackAutoHint), nameof(PackReturnAutoText), nameof(PackOffText) })
            OnPropertyChanged(name);
        // выбранный шаблон подсвечен
        var current = (TryParseNumber(_packageQuantity) ?? 0).ToString("0.##", CultureInfo.InvariantCulture);
        foreach (var chip in PackTemplates.Children.OfType<Button>())
            chip.Classes.Set("on", string.Equals(chip.Tag as string, current, StringComparison.Ordinal));
    }

    private static string Money(double v) => v.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Вверху вкладки: цена и закупка ЦЕЛОЙ упаковки — это поля вкладки «Основное».</summary>
    public string PackBaseText
    {
        get
        {
            var pack = TryParseNumber(_price) ?? 0;
            var buy = TryParseNumber(_purchasePrice) ?? 0;
            if (pack <= 0)
                return Tr.T("Цена упаковки (целой) не указана — впишите «Цена продажи» на вкладке «Основное»: от неё считается цена штуки.",
                    "Таңгактын (бүтүндүн) баасы көрсөтүлгөн эмес — «Негизги» өтмөгүндө «Сатуу баасын» жазыңыз: штуканын баасы ошондон эсептелет.",
                    "The price of the whole pack is not set — enter the “Sale price” on the “Main” tab: the piece price is calculated from it.",
                    "Tüm paketin fiyatı girilmedi — «Ana» sekmesinde «Satış fiyatı»nı girin: adet fiyatı ondan hesaplanır.",
                    "Butun qadoq narxi kiritilmagan — «Asosiy» yorlig'ida «Sotuv narxi»ni kiriting: dona narxi undan hisoblanadi.");
            var buyText = ShowPurchase && buy > 0 // 2026-10-30: закупку упаковки видят только роли с правом
                ? Tr.T($" · закупка упаковки: {Money(buy)} сом", $" · таңгактын сатып алуу баасы: {Money(buy)} сом", $" · pack cost: {Money(buy)} som", $" · paket alış fiyatı: {Money(buy)} som", $" · qadoq xarid narxi: {Money(buy)} so'm")
                : "";
            return Tr.T($"Цена упаковки (целой): {Money(pack)} сом{buyText} — берётся с вкладки «Основное».",
                $"Таңгактын (бүтүндүн) баасы: {Money(pack)} сом{buyText} — «Негизги» өтмөгүнөн алынат.",
                $"Price of the whole pack: {Money(pack)} som{buyText} — taken from the “Main” tab.",
                $"Tüm paketin fiyatı: {Money(pack)} som{buyText} — «Ana» sekmesinden alınır.",
                $"Butun qadoq narxi: {Money(pack)} so'm{buyText} — «Asosiy» yorlig'idan olinadi.");
        }
    }

    /// <summary>«Наценка за штуку 33,3 % · прибыль со штуки 2,5 сом · закупка штуки 7,5».</summary>
    public string PackPieceInfo
    {
        get
        {
            var qty = TryParseNumber(_packageQuantity) ?? 0;
            var piece = TryParseNumber(_packagePiecePrice) ?? 0;
            var buy = TryParseNumber(_purchasePrice) ?? 0;
            if (qty < 2 || piece <= 0)
                return "";
            // 2026-10-30: без права на закупку — только цены (наценка, прибыль и закупка штуки раскрыли бы закупку).
            if (!ShowPurchase)
                return Tr.T($"Цена штуки {Money(piece)} сом × {Money(qty)} шт", $"Штуканын баасы {Money(piece)} сом × {Money(qty)} шт", $"Piece price {Money(piece)} som × {Money(qty)} pcs",
                    $"Adet fiyatı {Money(piece)} som × {Money(qty)} adet", $"Dona narxi {Money(piece)} so'm × {Money(qty)} dona");
            if (buy <= 0)
                return Tr.T("Закупка штуки неизвестна — укажите закупку упаковки на вкладке «Основное».", "Штуканын сатып алуу баасы белгисиз — «Негизги» өтмөгүндө таңгактын сатып алуу баасын жазыңыз.",
                    "The piece cost is unknown — enter the pack cost on the “Main” tab.", "Adet alış fiyatı bilinmiyor — «Ana» sekmesinde paket alış fiyatını girin.", "Dona xarid narxi noma'lum — «Asosiy» yorlig'ida qadoq xarid narxini kiriting.");
            var cost = buy / qty;
            var profit = piece - cost;
            var markup = (piece / cost - 1) * 100;
            return Tr.T($"Наценка за штуку {Money(markup)} % · прибыль со штуки {Money(profit)} сом · закупка штуки {Money(cost)}",
                $"Штуканын үстөгү {Money(markup)} % · штуканын пайдасы {Money(profit)} сом · штуканын сатып алуу баасы {Money(cost)}",
                $"Markup per piece {Money(markup)}% · profit per piece {Money(profit)} som · piece cost {Money(cost)}",
                $"Adet başına kâr oranı {Money(markup)} % · adet başına kâr {Money(profit)} som · adet alışı {Money(cost)}",
                $"Dona ustamasi {Money(markup)} % · donadan foyda {Money(profit)} so'm · dona xarid narxi {Money(cost)}");
        }
    }

    public bool HasPackPieceInfo => PackPieceInfo.Length > 0;

    /// <summary>Цена штуки × штук расходится с ценой упаковки больше чем на 5 %.</summary>
    public string PackWarning
    {
        get
        {
            var pack = TryParseNumber(_price) ?? 0;
            var qty = TryParseNumber(_packageQuantity) ?? 0;
            var piece = TryParseNumber(_packagePiecePrice) ?? 0;
            if (pack <= 0 || qty < 2 || piece <= 0)
                return "";
            var diff = Math.Abs(piece * qty - pack) / pack;
            if (diff <= PackMismatchLimit)
                return "";
            var total = Money(piece * qty);
            var packText = Money(pack);
            var percent = Money(Math.Round(diff * 100, 1));
            return Tr.T($"⚠ Цена штуки × штук = {total} сом, а цена упаковки {packText} сом — расхождение {percent} %. Проверьте цифры или нажмите «Авто».",
                $"⚠ Штуканын баасы × штук саны = {total} сом, ал таңгактын баасы {packText} сом — айырма {percent} %. Сандарды текшериңиз же «Авто» басыңыз.",
                $"⚠ Piece price × pieces = {total} som, but the pack price is {packText} som — a {percent}% difference. Check the numbers or press “Auto”.",
                $"⚠ Adet fiyatı × adet = {total} som, paket fiyatı ise {packText} som — %{percent} fark. Rakamları kontrol edin veya «Oto»ya basın.",
                $"⚠ Dona narxi × dona = {total} so'm, qadoq narxi esa {packText} so'm — {percent} % farq. Raqamlarni tekshiring yoki «Avto»ni bosing.");
        }
    }

    public bool HasPackWarning => PackWarning.Length > 0;

    // ---- только вид (спека Fable): карточка «Целая упаковка», подписи под полем, цвет строки расчёта, кнопка «Вернуть авто»
    public string PackWholePrice => (TryParseNumber(_price) ?? 0) > 0 ? Money(TryParseNumber(_price)!.Value) : "—";
    public string PackWholeBuy => !ShowPurchase ? "" : (TryParseNumber(_purchasePrice) ?? 0) > 0 ? Money(TryParseNumber(_purchasePrice)!.Value) : "—";
    public string PackSom => Tr.T("сом", "сом", "som", "som", "so'm");
    public bool PackCalcOk => HasPackPieceInfo && !HasPackWarning;
    public bool PackCalcWarn => HasPackPieceInfo && HasPackWarning;

    private double PackAutoValue()
    {
        var pack = TryParseNumber(_price) ?? 0;
        var qty = TryParseNumber(_packageQuantity) ?? 0;
        return qty >= 2 && pack > 0 ? Math.Round(pack / qty, 2) : 0;
    }

    private static string Money2(double v) => v.ToString("0.00", CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Под полем «Цена за штуку»: «авто = 200 ÷ 20» или «вручную · авто было 10,00».</summary>
    public string PackAutoHint
    {
        get
        {
            var pack = TryParseNumber(_price) ?? 0;
            var qty = TryParseNumber(_packageQuantity) ?? 0;
            if (_packManual)
                return Tr.T($"вручную · авто было {Money2(PackAutoValue())}", $"кол менен · авто {Money2(PackAutoValue())} болчу", $"manual · auto was {Money2(PackAutoValue())}",
                    $"elle · oto {Money2(PackAutoValue())} idi", $"qo'lda · avto {Money2(PackAutoValue())} edi");
            if (pack <= 0 || qty < 2)
                return Tr.T("авто = цена упаковки ÷ штук", "авто = таңгактын баасы ÷ штук", "auto = pack price ÷ pieces", "oto = paket fiyatı ÷ adet", "avto = qadoq narxi ÷ dona");
            return Tr.T($"авто = {Money(pack)} ÷ {Money(qty)}", $"авто = {Money(pack)} ÷ {Money(qty)}", $"auto = {Money(pack)} ÷ {Money(qty)}", $"oto = {Money(pack)} ÷ {Money(qty)}", $"avto = {Money(pack)} ÷ {Money(qty)}");
        }
    }

    public string PackReturnAutoText => Tr.T($"Вернуть авто · {Money2(PackAutoValue())}", $"Автого кайтуу · {Money2(PackAutoValue())}", $"Back to auto · {Money2(PackAutoValue())}",
        $"Otoya dön · {Money2(PackAutoValue())}", $"Avtoga qaytish · {Money2(PackAutoValue())}");

    /// <summary>Галочка выключена: одна фраза с ценой упаковки.</summary>
    public string PackOffText
    {
        get
        {
            var pack = TryParseNumber(_price) ?? 0;
            var price = pack > 0 ? Money(pack) : "—";
            return Tr.T($"Сейчас товар продаётся только целой упаковкой за {price} сом. Включите — и его можно будет продавать ещё и поштучно.",
                $"Азыр товар бүтүн таңгак катары {price} сомго гана сатылат. Күйгүзүңүз — анан дарасын да сатууга болот.",
                $"Right now the product is sold only as a whole pack for {price} som. Turn it on to sell single pieces too.",
                $"Şu anda ürün yalnızca tüm paket olarak {price} som'a satılıyor. Açın — adet olarak da satılabilsin.",
                $"Hozir mahsulot faqat butun qadoq sifatida {price} so'mga sotiladi. Yoqing — donalab ham sotish mumkin bo'ladi.");
        }
    }

    private void PackGoMain_Click(object? sender, RoutedEventArgs e) => ProductTabs.SelectedIndex = 0;

    /// <summary>2026-10-29: подсказка под штрихкодом — только цифры, 4–14 знаков (у существующего неизменённого кода её нет).</summary>
    public string BarcodeHint => _existing is not null && string.Equals(_barcode, _existing.Barcode, StringComparison.Ordinal) ? "" : BarcodeInput.Hint(_barcode);

    public bool HasBarcodeHint => BarcodeHint.Length > 0;
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
            // 2026-11-02 (аудит №2, С3-10): высота, ширина, глубина и вес читаем из characteristics той же карточки: раньше при открытии грузился только вес, и ввод одной высоты
            // отправлял characteristics с width_cm/depth_cm = null. Поля, которые кассир уже успел изменить, не трогаем; сравнение «менялось ли» обновляем.
            if (el.TryGetProperty("characteristics", out var chars) && chars.ValueKind == System.Text.Json.JsonValueKind.Object
                && string.Equals(DimensionsSignature(), _origDimensions, StringComparison.Ordinal))
            {
                static string Dim(System.Text.Json.JsonElement c, string name) =>
                    c.TryGetProperty(name, out var d) && d.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number
                    && double.TryParse(d.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var number) && number > 0
                        ? number.ToString("0.###", CultureInfo.InvariantCulture)
                        : "";
                HeightCm = Dim(chars, "height_cm");
                WidthCm = Dim(chars, "width_cm");
                DepthCm = Dim(chars, "depth_cm");
                if (string.IsNullOrWhiteSpace(_weightKg) && Dim(chars, "factual_weight_kg") is { Length: > 0 } serverWeight)
                    WeightKg = serverWeight;
                _origDimensions = DimensionsSignature();
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
        var barcode = BarcodeInput.DigitsOnly(_variantBarcode);
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

        if (!BarcodeInput.IsValid(barcode))
        {
            ErrorMessage = BarcodeInput.Hint(barcode) is { Length: > 0 } variantHint ? variantHint : BarcodeInput.Hint("0");
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

}
