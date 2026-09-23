using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NurMarketKassa.Models;

public sealed class RevisionLineVm : INotifyPropertyChanged
{
    private double _actualQty;

    public string ProductId { get; init; } = "";
    public string Barcode { get; init; } = "";
    public string ProductName { get; init; } = "";
    public double ExpectedQty { get; init; }

    public double ActualQty
    {
        get => _actualQty;
        set
        {
            if (Math.Abs(_actualQty - value) < 1e-9)
                return;

            _actualQty = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Difference));
        }
    }

    public double Difference => ActualQty - ExpectedQty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
