using System.Globalization;
using System.Windows.Input;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>
/// Этот файл описывает одну строку чека для панели корзины Avalonia-кассы:
/// название товара, количество, цену и сумму позиции.
/// </summary>
public sealed class CartLineItemVm : ViewModelBase
{
    private double _quantity;
    private double _lineTotal;
    private double? _maxStockQuantity;
    private double _discountAmount;
    private double? _discountPercent;
    private double? _fixedDiscountAmount;

    public int ItemNumber { get; init; }
    public string ItemId { get; init; } = "";
    public string ProductId { get; init; } = "";
    public string Barcode { get; init; } = "";
    public string Code { get; init; } = "";
    public string Title { get; init; } = "";

    // 2026-10-22 (модалки, пакет 1, живой аудит): экранный диктор читал у строки чека имя класса «CartLineItemVm», а у
    // кнопок «−»/«+» — «PathIcon». Строка читается названием товара, кнопки — «Убавить: товар» / «Прибавить: товар».
    public override string ToString() => Title;
    public string DecreaseName => Tr.T("Убавить", "Азайтуу", "Decrease", "Azalt", "Kamaytirish") + ": " + Title;
    public string IncreaseName => Tr.T("Прибавить", "Көбөйтүү", "Increase", "Artır", "Ko'paytirish") + ": " + Title;
    // 2026-11-06 (В7, ДС-2): корзина строки — «Удалить из чека: товар»; после первого нажатия (двойное нажатие, Д4-04) — «Подтвердить удаление: товар».
    public string RemoveName => Tr.T("Удалить из чека", "Чектен өчүрүү", "Remove from receipt", "Fişten kaldır", "Chekdan olib tashlash") + ": " + Title;
    public string RemoveConfirmName => Tr.T("Подтвердить удаление", "Өчүрүүнү ырастоо", "Confirm removal", "Kaldırmayı onayla", "O'chirishni tasdiqlash") + ": " + Title;

    public string Unit { get; init; } = "шт";
    public double UnitPrice { get; init; }
    public bool IsWeight { get; init; }

    /// <summary>2026-10-26 (ХМ2): шаг «+» у дробной единицы «Хозтоваров» (0.1 м); null — шаг 1 (штучный) или 0.1 кг (весовой).</summary>
    public double? FractionalStep { get; init; }

    /// <summary>Заполнен, если строка продаётся поштучно из пачки — тогда Quantity в ШТУКАХ,
    /// а остаток товара в каталоге хранится в ПАЧКАХ.</summary>
    public string? SalePackageId { get; init; }

    public ICommand? RemoveCommand { get; init; }
    public ICommand? IncreaseCommand { get; init; }
    public ICommand? DecreaseCommand { get; init; }
    public ICommand? WeighCommand { get; init; }
    public ICommand? DiscountCommand { get; init; }
    public ICommand? SetQuantityCommand { get; init; }
    /// <summary>2026-10-03: строка ↔ оптовая цена.</summary>
    public ICommand? WholesaleCommand { get; init; }

    /// <summary>2026-11-21 («Базар», модуль «Торг»): кнопка «Торг» на строке — До 10 / 50 / 100 и «Договорились за …».</summary>
    public ICommand? HaggleCommand { get; init; }
    public bool ShowHaggle { get; init; }
    public string HaggleText => Tr.T("Торг", "Соодалашуу", "Haggle", "Pazarlık", "Savdolashuv");
    public string HaggleTooltip => Tr.T("Торг: округлить вниз или договориться о сумме", "Соодалашуу: төмөн тегеректөө же сумманы макулдашуу", "Haggle: round down or agree on a sum", "Pazarlık: aşağı yuvarla veya tutarda anlaş", "Savdolashuv: pastga yaxlitlash yoki summani kelishish");

    /// <summary>2026-11-21 («Базар», модуль «Цена по количеству»): «Опт от 10 — 450» — по какой ступени стоит цена строки; пусто — обычная цена.</summary>
    public string PriceNote { get; init; } = "";

    /// <summary>2026-10-06, исследование «Кассы для одежды» (О-03): «Размер» на строке чека — покупатель передумал
    /// с размером или цветом, строка меняется на другой вариант того же товара без удаления и поиска заново.</summary>
    public string? VariantId { get; init; }
    public ICommand? ChangeVariantCommand { get; init; }
    public bool CanChangeVariant => !string.IsNullOrWhiteSpace(VariantId) && MarketSpheres.IsClothing && ChangeVariantCommand != null;
    public string ChangeVariantText => Tr.T("Размер", "Өлчөм", "Size", "Beden", "O'lcham");
    public string ChangeVariantTooltip => Tr.T("Сменить размер или цвет", "Өлчөмүн же түсүн алмаштыруу", "Change size or colour", "Beden veya rengi değiştir", "O'lcham yoki rangni almashtirish");
    public bool IsWholesale { get; init; }

    private bool _canWholesale;

    /// <summary>У товара есть оптовая цена (или строка уже по опту) — кнопка «Опт» видна.</summary>
    public bool CanWholesale
    {
        get => _canWholesale;
        set => SetProperty(ref _canWholesale, value);
    }

    // 2026-10-04, клиент: «место, чтобы поставить галочку оптовой продажи определённого товара» — вид галочки.
    public string WholesaleButtonText => IsWholesale
        ? Tr.T("☑ Опт", "☑ Дүң", "☑ Wholesale", "☑ Toptan", "☑ Ulgurji")
        : Tr.T("☐ Опт", "☐ Дүң", "☐ Wholesale", "☐ Toptan", "☐ Ulgurji");

    private string _quantityInput = "";

    public double Quantity
    {
        get => _quantity;
        set
        {
            if (!SetProperty(ref _quantity, value))
                return;
            LineTotal = UnitPrice * _quantity;
            OnPropertyChanged(nameof(QuantityDisplay));
            OnPropertyChanged(nameof(PriceQuantityLine));
            OnPropertyChanged(nameof(CanIncrease));
            QuantityInput = QuantityDisplay;
        }
    }

    /// <summary>Editable text for the cart-row quantity box; committed via SetQuantityCommand
    /// (Enter or losing focus), not applied to Quantity directly while the cashier is typing.</summary>
    public string QuantityInput
    {
        get => _quantityInput;
        set => SetProperty(ref _quantityInput, value);
    }

    /// <summary>Maximum quantity permitted in this receipt after other reservations.</summary>
    public double? MaxStockQuantity
    {
        get => _maxStockQuantity;
        set
        {
            if (!SetProperty(ref _maxStockQuantity, value))
                return;
            OnPropertyChanged(nameof(CanIncrease));
        }
    }

    public bool CanIncrease =>
        !MaxStockQuantity.HasValue || Quantity + (IsWeight ? 0.1 : FractionalStep ?? 1) <= MaxStockQuantity.Value + 1e-6;

    public double LineTotal
    {
        get => _lineTotal;
        set
        {
            if (!SetProperty(ref _lineTotal, value))
                return;
            OnPropertyChanged(nameof(LineTotalDisplay));
            OnPropertyChanged(nameof(LineTotalAmount));
            OnPropertyChanged(nameof(HasDiscount));
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public double DiscountAmount
    {
        get => _discountAmount;
        set
        {
            if (!SetProperty(ref _discountAmount, value))
                return;
            OnPropertyChanged(nameof(HasDiscount));
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public bool HasDiscount =>
        DiscountAmount > 1e-6 || LineTotal + 1e-6 < UnitPrice * Quantity;

    public double? DiscountPercent
    {
        get => _discountPercent;
        set
        {
            if (!SetProperty(ref _discountPercent, value))
                return;
            OnPropertyChanged(nameof(IsPercentageDiscount));
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public double? FixedDiscountAmount
    {
        get => _fixedDiscountAmount;
        set
        {
            if (!SetProperty(ref _fixedDiscountAmount, value))
                return;
            OnPropertyChanged(nameof(DiscountDisplayText));
        }
    }

    public bool IsPercentageDiscount => DiscountPercent is > 1e-6;

    public string DiscountDisplayText
    {
        get
        {
            if (!HasDiscount)
                return string.Empty;

            if (DiscountPercent is { } percent && percent > 1e-6)
                return $"-{MoneyText.Weight(percent, "0.##")}%"; // 2026-11-12 (Д-5): запятая вместо точки

            var amount = FixedDiscountAmount is > 1e-6
                ? FixedDiscountAmount.Value
                : DiscountAmount;
            // 2026-11-27 (живой проход): весовой «по сумме» 100 сом → 1,429 кг × 70 = 100,03, строка ровно 100 — DiscountAmount = 0,
            // и над строкой висел значок «-0 сом». Берём настоящую разницу «цена × вес − сумма строки»; меньше копейки — значка нет.
            if (amount < 0.005)
                amount = Math.Round(UnitPrice * Quantity - LineTotal, 2, MidpointRounding.AwayFromZero);
            if (amount < 0.005)
                return string.Empty;
            return $"-{MoneyText.Weight(amount, "0.##")} {Tr.T("сом", "сом", "som", "som", "so'm")}";
        }
    }

    /// <summary>2026-10-27, аудит Н-5: длинное название — кнопки строки уходят вторым рядом (стиль longtitle в BasketPanelView), название получает всю ширину.</summary>
    public bool HasLongTitle => Title.Length > 10;

    public string NumberedTitle => $"#{ItemNumber}. {Title}";

    public string QuantityDisplay => IsWeight
        ? MoneyText.Weight(Quantity, "0.000") // 2026-11-12 (Д-5): вес с запятой
        : MoneyText.Weight(Quantity);

    // 2026-10-19, К1: суммы на экране — одним видом «1 234,50» (MoneyText), как у владельца; только показ.
    public string UnitPriceDisplay => MoneyText.Som(UnitPrice);

    /// <summary>2026-10-02: обычная цена, если вариант продаётся по акции (см. CartItem.PromoBasePrice).</summary>
    public double? PromoBasePrice { get; init; }

    /// <summary>Подпись вида «0.500 кг × 115.71 сом» или «1 шт × 150.00 сом»; у варианта по акции —
    /// «1 шт × 2200.00 сом · было 2500.00 (−12%)».</summary>
    // 2026-10-20 (живой аудит кассы, К18): «0,300 кг × 70,00 сом» — количество с запятой, как сумма рядом (только показ;
    // QuantityDisplay/QuantityInput для ввода — прежние, с точкой).
    public string PriceQuantityLine =>
        $"{Quantity.ToString(IsWeight ? "0.000" : "0.###", CultureInfo.GetCultureInfo("ru-RU"))} {Unit} × {MoneyText.Som(UnitPrice)}"
        + (PromoBasePrice is { } basePrice && basePrice > UnitPrice + 0.005
            ? " · " + Tr.T("было", "болгон", "was", "önce", "edi") + $" {MoneyText.Amount(basePrice)} (−{Math.Round((1 - UnitPrice / basePrice) * 100):0}%)"
            : "")
        + (PriceNote.Length > 0 ? " · " + PriceNote : "");

    // 2026-10-03, владелец: «при скидке, если в убыток продаёт, — на экране в корзине рядом с товаром показывать,
    // сколько убытка». Считает BasketPanelViewModel.UpdateLossMarks: закупка × количество − сумма строки
    // после скидки на позицию и её доли скидки на весь чек.
    private double _lossAmount;
    private double _unitCost;

    public double LossAmount
    {
        get => _lossAmount;
        set
        {
            if (!SetProperty(ref _lossAmount, value))
                return;
            OnPropertyChanged(nameof(HasLoss));
            OnPropertyChanged(nameof(LossDisplayText));
            OnPropertyChanged(nameof(LossTooltip));
        }
    }

    /// <summary>Закупочная цена за единицу строки (за штуку при продаже поштучно из пачки).</summary>
    public double UnitCost
    {
        get => _unitCost;
        set
        {
            if (SetProperty(ref _unitCost, value))
                OnPropertyChanged(nameof(LossTooltip));
        }
    }

    public bool HasLoss => LossAmount > 0.005;

    public string LossDisplayText => HasLoss
        ? Tr.T("убыток", "зыян", "loss", "zarar", "zarar") + " " + MoneyText.Som(LossAmount)
        : "";

    public string LossTooltip => HasLoss
        ? Tr.T($"Продаётся дешевле закупки: закупка {MoneyText.Amount(UnitCost)} × {QuantityDisplay}, со скидкой — {MoneyText.Amount(LossAmount)} сом убытка.",
            $"Сатып алуу баасынан арзан сатылат: сатып алуу {MoneyText.Amount(UnitCost)} × {QuantityDisplay}, арзандатуу менен — {MoneyText.Amount(LossAmount)} сом зыян.",
            $"Sold below cost: cost {MoneyText.Amount(UnitCost)} × {QuantityDisplay}, with the discount — {MoneyText.Amount(LossAmount)} som loss.",
            $"Alış fiyatının altında satılıyor: alış {MoneyText.Amount(UnitCost)} × {QuantityDisplay}, indirimle — {MoneyText.Amount(LossAmount)} som zarar.",
            $"Xarid narxidan arzon sotilmoqda: xarid {MoneyText.Amount(UnitCost)} × {QuantityDisplay}, chegirma bilan — {MoneyText.Amount(LossAmount)} so'm zarar.")
        : "";

    // 2026-10-19, К1: суммы на экране — одним видом «1 234,50» (MoneyText), как у владельца; только показ.
    public string LineTotalDisplay => MoneyText.Som(LineTotal);
    public string LineTotalAmount => MoneyText.Amount(LineTotal);
}
