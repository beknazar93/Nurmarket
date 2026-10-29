using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-10-28: общая кнопка «Обновить» (⟳, 44×44) для всех видов кассы — Классика, Табличная, Карточки, Минимал, Профи, 1С.
/// Сама ищет MainWindowViewModel среди владельцев по дереву (у «Классики» DataContext — каталог, у остальных — вся модель)
/// и привязывает RefreshAllCommand; пока идёт обновление, значок крутится. Подсказка и имя для экранного диктора — на 5 языках.</summary>
public partial class RefreshButton : UserControl
{
    private MainWindowViewModel? _vm;

    public RefreshButton()
    {
        InitializeComponent();
        UpdateTexts();
        AttachedToLogicalTree += (_, _) => { Tr.LanguageChanged += UpdateTexts; BindVm(); };
        DetachedFromLogicalTree += (_, _) => { Tr.LanguageChanged -= UpdateTexts; Unbind(); };
        DataContextChanged += (_, _) => BindVm();
    }

    private void UpdateTexts()
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTexts);
            return;
        }

        var text = Tr.T("Обновить данные (F5)", "Маалыматты жаңылоо (F5)", "Refresh data (F5)", "Verileri yenile (F5)", "Ma'lumotlarni yangilash (F5)");
        ToolTip.SetTip(Btn, text);
        AutomationProperties.SetName(Btn, text);
    }

    private void BindVm()
    {
        Unbind();
        for (StyledElement? e = this; e is not null; e = e.Parent as StyledElement)
        {
            if (e.DataContext is MainWindowViewModel vm)
            {
                _vm = vm;
                break;
            }
        }

        if (_vm is null)
            return;
        Btn.Command = _vm.RefreshAllCommand;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Btn.Classes.Set("busy", _vm.IsRefreshing);
    }

    private void Unbind()
    {
        if (_vm is null)
            return;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        Btn.Command = null;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRefreshing) && _vm is not null)
            Btn.Classes.Set("busy", _vm.IsRefreshing);
    }
}
