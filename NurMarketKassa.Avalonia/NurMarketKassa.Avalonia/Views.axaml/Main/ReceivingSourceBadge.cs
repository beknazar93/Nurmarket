using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-09 (владелец: «указывать вначале как на старом — база / база NurCRM / неизвестно / из интернета, можно значками; при наведении показывает, что означает»):
/// значок источника товара строки приёмки — «Склад → Приёмка» (в ячейке названия) и «Закупки → Приём товара». Значок рисуется фигурой (Path) и знаком «?», а не эмодзи:
/// шрифты Linux/Android эмодзи не гарантируют. Форма у каждого источника своя, поэтому смысл не держится на одном цвете; ToolTip и AutomationProperties.Name — «Название: пояснение»
/// (ReceivingLineVm.SourceTitle / SourceHintFor, 5 языков). Источник меняется (скан → «Из интернета» по кнопке) — значок перерисовывается сам.</summary>
public sealed class ReceivingSourceBadge : Border
{
    public static readonly StyledProperty<ReceivingSource> SourceProperty =
        AvaloniaProperty.Register<ReceivingSourceBadge, ReceivingSource>(nameof(Source));

    static ReceivingSourceBadge()
    {
        SourceProperty.Changed.AddClassHandler<ReceivingSourceBadge>((b, _) => b.Rebuild());
    }

    public ReceivingSourceBadge()
    {
        Width = 26;
        Height = 26;
        CornerRadius = new CornerRadius(13);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Focusable = false;
        Rebuild();
    }

    public ReceivingSource Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    // Простые контуры 24×24: «База» — склад-домик, «База NurCRM» — облако, «Из интернета» — глобус; «Неизвестно» — знак «?».
    private const string WarehouseGeometry = "M3,9 L12,4 L21,9 L21,20 L3,20 Z M9,20 L9,13 L15,13 L15,20";
    private const string CloudGeometry = "M7,18 C3.5,18 2.5,14 5,12.5 C5,9 9,7.5 11.5,9.5 C13,5.5 19,6.5 19,11.5 C22,12 22,18 18,18 Z";
    private const string GlobeGeometry = "M12,3 A9,9 0 1 1 11.99,3 Z M3,12 L21,12 M12,3 C7,8 7,16 12,21 M12,3 C17,8 17,16 12,21";

    private void Rebuild()
    {
        var (soft, strong) = Source switch
        {
            ReceivingSource.GlobalBase => ("BrushAccentSoft", "BrushText"),
            ReceivingSource.Unknown => ("BrushWarningSoft", "BrushWarning"),
            ReceivingSource.Internet => ("BrushPanelSoft", "BrushText"),
            _ => ("BrushSuccessSoft", "BrushSuccess"),
        };
        this.Bind(BackgroundProperty, this.GetResourceObservable(soft));
        Control icon;
        if (Source == ReceivingSource.Unknown)
        {
            var q = new TextBlock { Text = "?", FontSize = 16, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            q.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(strong));
            icon = q;
        }
        else
        {
            var path = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse(Source switch
                {
                    ReceivingSource.GlobalBase => CloudGeometry,
                    ReceivingSource.Internet => GlobeGeometry,
                    _ => WarehouseGeometry,
                }),
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 16,
                StrokeThickness = 1.8,
                StrokeJoin = PenLineJoin.Round,
                StrokeLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            path.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, this.GetResourceObservable(strong));
            icon = path;
        }
        Child = icon;
        var text = ReceivingLineVm.SourceTitle(Source) + ": " + ReceivingLineVm.SourceHintFor(Source);
        ToolTip.SetTip(this, text);
        AutomationProperties.SetName(this, text);
    }
}
