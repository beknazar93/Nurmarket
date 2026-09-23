using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NurMarketKassa.Models;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.ViewModels;

/// <summary>Avalonia warehouse VM (local stub until full inventory services are ported).</summary>
public sealed class WarehouseViewModel : INotifyPropertyChanged
{
    public static readonly string[] WriteOffReasons = { "Брак", "Просрочка", "Порча упаковки", "Другое" };

    private bool _isBusy;
    private string _writeOffProductName = "";
    private string _writeOffBarcode = "";
    private double _writeOffQuantity = 1;
    private string _writeOffReason = WriteOffReasons[0];
    private RevisionLineVm? _focusedRevisionLine;

    public WarehouseViewModel()
    {
        CommitRevisionCommand = new RelayCommand(() => { }, () => !IsBusy);
        WriteOffCommand = new RelayCommand(() => { }, () => !IsBusy && !string.IsNullOrWhiteSpace(WriteOffBarcode));
        WriteOffReasonOptions = new ObservableCollection<string>(WriteOffReasons);
    }

    public ObservableCollection<RevisionLineVm> RevisionLines { get; } = new();
    public ObservableCollection<string> WriteOffReasonOptions { get; }

    public ICommand CommitRevisionCommand { get; }
    public ICommand WriteOffCommand { get; }

    public event Action<RevisionLineVm>? RevisionLineAdded;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => _isBusy;
        private set { _isBusy = value; OnPropertyChanged(); }
    }

    public RevisionLineVm? FocusedRevisionLine
    {
        get => _focusedRevisionLine;
        set { _focusedRevisionLine = value; OnPropertyChanged(); }
    }

    public string WriteOffProductName
    {
        get => _writeOffProductName;
        set { _writeOffProductName = value; OnPropertyChanged(); }
    }

    public string WriteOffBarcode
    {
        get => _writeOffBarcode;
        set { _writeOffBarcode = value; OnPropertyChanged(); }
    }

    public double WriteOffQuantity
    {
        get => _writeOffQuantity;
        set { _writeOffQuantity = value; OnPropertyChanged(); }
    }

    public string WriteOffReason
    {
        get => _writeOffReason;
        set { _writeOffReason = value; OnPropertyChanged(); }
    }

    public Task EnsureCatalogLoadedAsync() => Task.CompletedTask;

    public void HandleBarcodeScan(string barcode, bool isRevisionTab)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return;
        if (isRevisionTab)
        {
            var line = new RevisionLineVm
            {
                Barcode = barcode.Trim(),
                ProductName = barcode.Trim(),
                ExpectedQty = 0,
                ActualQty = 1,
            };
            RevisionLines.Add(line);
            FocusedRevisionLine = line;
            RevisionLineAdded?.Invoke(line);
        }
        else
        {
            WriteOffBarcode = barcode.Trim();
            WriteOffProductName = barcode.Trim();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
