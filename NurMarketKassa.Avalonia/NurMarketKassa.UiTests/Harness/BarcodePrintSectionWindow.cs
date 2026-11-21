using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-21: ТЕСТОВАЯ оболочка. Раздела программы «Штрихкоды и ценники» больше нет (владелец: «все редакторы — в одну вкладку Склада»,
/// вкладка «Ценники и штрихкоды» — <see cref="LabelsHubView"/>). Раскладочные проверки (размеры окон, утечки, смена разделов) по-прежнему нужны для
/// содержимого этой вкладки, поэтому здесь то же окно-обёртка, что было у раздела, но с содержимым единой вкладки.</summary>
public sealed class BarcodePrintSectionWindow : Window, IOwnerSection, IOwnerSectionKeepAlive
{
    private readonly LabelsHubView _hub = new();

    public BarcodePrintSectionWindow()
    {
        Title = "Ценники и штрихкоды (тестовая оболочка)";
        Width = 1000;
        Height = 760;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        var card = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = _hub,
        };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanel"));
        Content = new Border { Padding = new Thickness(24, 12, 24, 20), Child = card, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        Closed += (_, _) => _hub.Shutdown();
    }

    public void AsOwnerSection()
    {
    }

    public bool? HasUnsavedChanges => _hub.PrintHost.View?.IsBusy == true ? true : null;

    public bool KeepAliveWhenHidden => _hub.PrintHost.View?.IsBusy == true;

    /// <summary>Режим «Печать» вкладки (подсказка «не куплено» или печать).</summary>
    public BarcodePrintTabHost Host => _hub.PrintHost;

    public LabelsHubView Hub => _hub;
}
