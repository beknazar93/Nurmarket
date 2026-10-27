using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using NurMarketKassa.Services;

namespace NurMarketKassa.Models.Pos;

public sealed class ReturnSaleLineVm : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string LineId { get; init; }

    public required string Title { get; init; }

    public required string SubLine { get; init; }

    public required string LineSumText { get; init; }

    /// <summary>2026-09-08: сумма как число — раньше сумма возврата считалась обратным
    /// парсингом LineSumText (Replace("Сумма: ", "")...), что ломалось на любом языке,
    /// кроме русского, как только текст стал переводиться. Это поле — единственный источник
    /// истины для расчётов, LineSumText — только для отображения.</summary>
    public decimal RefundSum { get; init; }

    public string? ProductId { get; init; }

    public double Quantity { get; init; }

    public double OriginalQuantity { get; init; }

    public bool CanReturn { get; init; }

    // 2026-10-27 (приёмка 1.17.63, Д-5): частичный возврат строки по количеству («клей × 2» — вернуть 1). Quantity и RefundSum —
    // по-прежнему «всё, что ещё можно вернуть по строке»; сколько вернуть сейчас — ReturnQuantity (по умолчанию всё).
    private double _returnQuantity = double.NaN;

    /// <summary>Сумма возврата за ОДНУ единицу строки до округления: сумма строки до возвратов / количество × доля скидки на чек.
    /// 0 — не задана, тогда берётся RefundSum / Quantity.</summary>
    public decimal RefundPerUnit { get; init; }

    /// <summary>Весовой или дробный товар: количество к возврату — дробное (шаг 0,1); у штучного — целое (шаг 1).</summary>
    public bool AllowFractionalQuantity { get; init; }

    /// <summary>Только у копии строки для оформления возврата (EffectiveReturnLines): сколько оставалось к возврату до этого возврата.</summary>
    public double RemainingQuantity { get; init; }

    /// <summary>Сколько можно вернуть по строке: для строки списка — Quantity, для копии — то, что оставалось.</summary>
    public double MaxReturnQuantity => RemainingQuantity > 0 ? RemainingQuantity : Quantity;

    /// <summary>Поле количества показывается, когда есть что выбирать: больше одной штуки или дробный товар.</summary>
    public bool CanEditQuantity => CanReturn && (AllowFractionalQuantity || Quantity > 1 + 1e-6);

    public double ReturnQuantity
    {
        get => double.IsNaN(_returnQuantity) ? Quantity : _returnQuantity;
        set
        {
            var step = AllowFractionalQuantity ? 0.001 : 1.0;
            var next = double.IsNaN(value) || double.IsInfinity(value) ? Quantity : value;
            next = AllowFractionalQuantity ? Math.Round(next, 3) : Math.Round(next);
            next = Math.Min(Quantity, Math.Max(Math.Min(step, Quantity), next));
            if (next >= Quantity - 1e-6)
                next = Quantity;
            if (Math.Abs(next - ReturnQuantity) < 1e-9)
                return;
            _returnQuantity = next;
            OnPropertyChanged(nameof(ReturnQuantity));
            OnPropertyChanged(nameof(ReturnQuantityText));
            OnPropertyChanged(nameof(ReturnRefundSum));
            OnPropertyChanged(nameof(ReturnSumText));
            OnPropertyChanged(nameof(IsPartialReturn));
        }
    }

    /// <summary>Количество к возврату для поля ввода: «1», «0,5».</summary>
    public string ReturnQuantityText => ReturnQuantity.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', ',');

    public bool IsPartialReturn => ReturnQuantity < Quantity - 1e-6;

    /// <summary>Сумма возврата выбранного количества: вся строка — RefundSum как есть; часть — цена единицы × количество с долей
    /// скидки на чек (та же формула, что у RefundSum).</summary>
    public decimal ReturnRefundSum
    {
        get
        {
            if (!IsPartialReturn)
                return RefundSum;
            var perUnit = RefundPerUnit > 0m ? RefundPerUnit : Quantity > 0 ? RefundSum / (decimal)Quantity : 0m;
            return Math.Min(RefundSum, Math.Round(perUnit * (decimal)ReturnQuantity, 2, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>Сумма справа в строке: вся строка — прежний текст, часть — пересчитанная.</summary>
    public string ReturnSumText => IsPartialReturn ? MoneyText.Som(ReturnRefundSum) : LineSumText;

    public string ReturnQtyLabel => Tr.T("Вернуть, шт.:", "Кайтаруу, даана:", "Return qty:", "İade adedi:", "Qaytarish, dona:");

    public string ReturnQtyLessName => Tr.T("Вернуть меньше", "Азыраак кайтаруу", "Return fewer", "Daha az iade et", "Kamroq qaytarish");

    public string ReturnQtyMoreName => Tr.T("Вернуть больше", "Көбүрөөк кайтаруу", "Return more", "Daha çok iade et", "Ko'proq qaytarish");

    public string ReturnQtyBoxName => Tr.T("Сколько вернуть", "Канча кайтаруу", "How many to return", "Kaç tane iade", "Nechta qaytarish");

    /// <summary>Шаг кнопок «−»/«+»: 1 — штучный, 0,1 — весовой.</summary>
    public double ReturnQuantityStep => AllowFractionalQuantity ? 0.1 : 1.0;

    public void ChangeReturnQuantity(int direction)
    {
        var step = ReturnQuantityStep;
        // Дробный: от целого шага — к ближайшему кратному 0,1, чтобы после ручного «0,55» кнопка не давала «0,65».
        var next = ReturnQuantity + direction * step;
        if (AllowFractionalQuantity)
            next = Math.Round(next, 1);
        ReturnQuantity = next;
    }

    /// <summary>Ввод количества руками: запятая или точка — десятичный разделитель; «1.000», «1e3» и прочее — отклоняется
    /// (значение остаётся прежним, возвращается false).</summary>
    public bool TrySetReturnQuantity(string? text)
    {
        var raw = (text ?? "").Trim().Replace(',', '.');
        if (raw.Length == 0 || !double.TryParse(raw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed <= 0)
            return false;
        // Штучный товар: только целое число цифрами («1,5», «1.000», «1e1» — нет).
        if (!AllowFractionalQuantity && !raw.All(char.IsDigit))
            return false;
        if (parsed > Quantity + 1e-6)
            return false;
        ReturnQuantity = parsed;
        return true;
    }

    public bool IsSelected
    {
        get => this._isSelected;
        set
        {
            if (!this.CanReturn & value || this._isSelected == value)
                return;
            this._isSelected = value;
            this.OnPropertyChanged(nameof(IsSelected));
        }
    }

    public string? RefundReason { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChangedEventHandler? propertyChanged = this.PropertyChanged;
        if (propertyChanged == null)
            return;
        propertyChanged((object)this, new PropertyChangedEventArgs(name));
    }
}
