using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>2026-11-20 (обход кассы, п. 22): подпись вкладки чека «Чек 2 · 25,00». При нехватке места сокращается только название
/// (многоточие), сумма всегда целиком; у неактивной вкладки название короче. Активная вкладка сама прокручивается в видимую часть
/// списка вкладок (раньше при 2–3 чеках на 1024 она уходила под край и сумма читалась «25» вместо «25,00»).
/// Текст — тот же <c>ReceiptTabVm.Title</c> («название · сумма»); без « · » вся подпись считается названием.</summary>
public sealed class ReceiptTabLabel : StackPanel
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<ReceiptTabLabel, string?>(nameof(Text));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<ReceiptTabLabel, bool>(nameof(IsActive));

    /// <summary>Самая длинная часть названия у активной вкладки.</summary>
    public const double ActiveNameMaxWidth = 170;

    /// <summary>У неактивной — короче: места хватает и на соседние вкладки.</summary>
    public const double InactiveNameMaxWidth = 104;

    private const string Separator = " · ";

    private readonly TextBlock _name = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _amount = new() { TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private bool _posted;

    public ReceiptTabLabel()
    {
        Orientation = Orientation.Horizontal;
        Children.Add(_name);
        Children.Add(_amount);
        SizeChanged += (_, _) => KeepVisible();
        Apply();
    }

    static ReceiptTabLabel()
    {
        TextProperty.Changed.AddClassHandler<ReceiptTabLabel>((l, _) => l.Apply());
        IsActiveProperty.Changed.AddClassHandler<ReceiptTabLabel>((l, _) => l.Apply());
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Разбор «название · сумма» по последнему « · »: (название, сумма или пусто).</summary>
    public static (string Name, string Amount) Split(string? title)
    {
        var t = title ?? "";
        var i = t.LastIndexOf(Separator, StringComparison.Ordinal);
        return i < 0 ? (t, "") : (t[..i], t[(i + Separator.Length)..]);
    }

    private void Apply()
    {
        var (name, amount) = Split(Text);
        _name.Text = name;
        _name.MaxWidth = IsActive ? ActiveNameMaxWidth : InactiveNameMaxWidth;
        _amount.IsVisible = amount.Length > 0;
        _amount.Text = amount.Length > 0 ? Separator + amount : ""; // тот же текст, что был одной строкой: « · сумма»
        ToolTip.SetTip(this, string.IsNullOrEmpty(Text) ? null : Text);
        KeepVisible();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        KeepVisible();
    }

    /// <summary>Активная вкладка — в видимую область прокрутки (после раскладки, не из самой раскладки).</summary>
    private void KeepVisible()
    {
        if (!IsActive || _posted || this.GetVisualRoot() is null)
            return;
        _posted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _posted = false;
            if (!IsActive || this.GetVisualRoot() is null)
                return;
            (this.FindAncestorOfType<Button>() ?? (Control)this).BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
