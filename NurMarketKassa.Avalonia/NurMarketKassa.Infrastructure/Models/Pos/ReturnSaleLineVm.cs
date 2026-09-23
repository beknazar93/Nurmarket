using System.ComponentModel;
using System.Runtime.CompilerServices;

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
