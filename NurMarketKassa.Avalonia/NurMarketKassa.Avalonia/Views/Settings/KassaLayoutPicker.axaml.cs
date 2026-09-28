using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>Выбор вида кассы карточками (2026-09-28, «виды касс в маркетплейс добавь»).
///
/// Раньше карточки собирал ScreenSettingsView (Настройки → Экран). Теперь они нужны и в
/// Маркетплейсе на вкладке «Виды кассы», поэтому вынесены сюда целиком — обе страницы показывают
/// один и тот же элемент. Карточка на каждую раскладку KassaLayouts: миниатюра цветами текущей
/// темы, название и строка о том, чем она отличается. Нажатие применяет вид сразу
/// (App.ApplyMainLayoutMode) — касса и экран покупателя перестраиваются на глазах.</summary>
public partial class KassaLayoutPicker : UserControl
{
    /// <summary>Вид кассы выбран (id раскладки) — хозяин страницы может обновить своё (карточка
    /// «1С» в Маркетплейсе, подпись экрана покупателя).</summary>
    public event Action<string>? LayoutApplied;

    public KassaLayoutPicker()
    {
        InitializeComponent();
        Rebuild();
        // Миниатюры нарисованы цветами темы и подписаны на языке программы — после смены
        // языка и при каждом показе страницы перестраиваем.
        AttachedToVisualTree += (_, _) =>
        {
            Tr.LanguageChanged -= OnLanguageChanged;
            Tr.LanguageChanged += OnLanguageChanged;
            Rebuild();
        };
        DetachedFromVisualTree += (_, _) => Tr.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

    /// <summary>Пересобрать карточки (вид мог смениться в другом месте — например, карточкой «1С»).</summary>
    public void Rebuild()
    {
        LayoutPickerPanel.Children.Clear();
        var current = KassaLayouts.Normalize(UserPreferences.Instance.MainLayoutMode);

        foreach (var option in KassaLayouts.All)
        {
            var isActive = option.Id == current;
            var title = new TextBlock
            {
                Text = option.Label(),
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 10, 0, 4) };
            header.Children.Add(title);
            if (isActive)
            {
                var badgeText = new TextBlock
                {
                    Text = Tr.T("✓ Выбрана", "✓ Тандалган", "✓ Selected", "✓ Seçili", "✓ Tanlangan"),
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                };
                badgeText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushAccentForeground"));
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = badgeText,
                };
                badge.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushAccent"));
                Grid.SetColumn(badge, 1);
                header.Children.Add(badge);
            }

            var description = new TextBlock
            {
                Text = option.Description(),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 5,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            ToolTip.SetTip(description, option.Description());
            description.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

            var card = new Button
            {
                Tag = option.Id,
                Width = 252,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(12),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
                BorderThickness = new Thickness(isActive ? 2 : 1),
                CornerRadius = new CornerRadius(12),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Content = new StackPanel
                {
                    Children = { KassaLayouts.BuildPreview(option.Id, 226, 134), header, description },
                },
            };
            card.Classes.Add("LayoutCard");
            card.Bind(Button.BackgroundProperty, this.GetResourceObservable("BrushPanel"));
            card.Bind(Button.BorderBrushProperty, this.GetResourceObservable(isActive ? "BrushAccentStrong" : "BrushBorder"));
            Avalonia.Automation.AutomationProperties.SetName(card, option.Label());
            card.Click += LayoutCard_Click;
            LayoutPickerPanel.Children.Add(card);
        }
    }

    private void LayoutCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        App.ApplyMainLayoutMode(id);
        Rebuild();
        LayoutApplied?.Invoke(id);
    }
}
