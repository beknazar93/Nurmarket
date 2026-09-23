using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class HotkeySettingsWindow : Window, INotifyPropertyChanged
{
    private readonly PosHotkeyService _hotkeys;
    private string _errorText = "";

    public HotkeySettingsWindow() : this(new PosHotkeyService()) { }

    public HotkeySettingsWindow(PosHotkeyService hotkeys)
    {
        _hotkeys = hotkeys;
        Rows = new ObservableCollection<HotkeyRow>(
            PosHotkeyService.Definitions.Select(definition =>
                new HotkeyRow(
                    definition.Action,
                    definition.Title,
                    definition.Description,
                    _hotkeys.GetGesture(definition.Action))));
        InitializeComponent();
        DataContext = this;
    }

    public ObservableCollection<HotkeyRow> Rows { get; }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (_errorText == value) return;
            _errorText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorText)));
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnGestureKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (PosHotkeyService.IsModifierKey(e.Key) || sender is not TextBox { Tag: PosHotkeyAction action })
            return;

        var row = Rows.First(x => x.Action == action);
        row.Gesture = PosHotkeyService.Format(e.Key, e.KeyModifiers);
        ErrorText = "";
    }

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var definition in PosHotkeyService.Definitions)
            Rows.First(x => x.Action == definition.Action).Gesture = definition.DefaultGesture;
        ErrorText = "";
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        var values = Rows.ToDictionary(x => x.Action, x => x.Gesture);
        if (!_hotkeys.Save(values, out var error))
        {
            ErrorText = error ?? "Не удалось сохранить комбинации.";
            return;
        }

        Close(true);
    }
}

public sealed class HotkeyRow : INotifyPropertyChanged
{
    private string _gesture;

    public HotkeyRow(PosHotkeyAction action, string title, string description, string gesture)
    {
        Action = action;
        Title = title;
        Description = description;
        _gesture = gesture;
    }

    public PosHotkeyAction Action { get; }
    public string Title { get; }
    public string Description { get; }
    public string Gesture
    {
        get => _gesture;
        set
        {
            if (_gesture == value) return;
            _gesture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Gesture)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
