using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-26 (замечание владельца: «Где описание и гифки его работы!? Создай для ВСЕХ функций!»): проигрыватель демонстрации, нарисованной КОДОМ
// (данные — MockStep из MarketplaceDemos.*). Вместо тёмного баннера-обложки на странице функции: цветной фон функции, в нём «окно кассы» 640×340
// (масштабируется под ширину страницы), подпись шага ниже, точки шагов, пауза и счётчик «2 / 4». Шаги листаются сами (≈3,2 с), цикл без конца.
// Почему не GIF: см. MarketplaceMockDemo.cs. Нагрузка: один DispatcherTimer, пока страница на экране и не на паузе; при уходе со страницы (DetachedFromVisualTree) он
// останавливается (счётчик RunningTimers для проверки в стенде).

/// <summary>Рисует один кадр демонстрации (окно кассы из простых частей). Размер сцены фиксирован — MarketplaceMockDemoPlayer масштабирует всю рамку целиком.</summary>
public static class MarketplaceMockScene
{
    /// <summary>Размер «окна» демонстрации в условных пикселях (потом сжимается/растягивается под страницу).</summary>
    public const double StageWidth = 640, StageHeight = 340;

    /// <summary>Высота строки заголовка окна.</summary>
    public const double TitleHeight = 40;

    /// <summary>Поля вокруг содержимого.</summary>
    public static readonly Thickness BodyPadding = new(18, 12, 18, 12);

    /// <summary>Сколько места остаётся под содержимое кадра (тест: желаемая высота содержимого не больше этого).</summary>
    public static double BodyHeight => StageHeight - TitleHeight - BodyPadding.Top - BodyPadding.Bottom;

    /// <summary>Ширина содержимого кадра.</summary>
    public static double BodyWidth => StageWidth - BodyPadding.Left - BodyPadding.Right;

    /// <summary>Только содержимое кадра (без заголовка) — им измеряют переполнение в тестах.</summary>
    public static StackPanel BuildBody(MockScene scene)
    {
        var body = new StackPanel { Spacing = 8, Width = BodyWidth };
        foreach (var part in scene.Parts)
            body.Children.Add(Part(part));
        return body;
    }

    /// <summary>Весь кадр: заголовок окна и содержимое; рамка StageWidth × StageHeight, лишнее обрезается.</summary>
    public static Control Build(MockScene scene)
    {
        var title = Txt(scene.Title, 14, "BrushText", FontWeight.SemiBold);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.TextWrapping = TextWrapping.NoWrap;
        title.VerticalAlignment = VerticalAlignment.Center;
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 8, Height = 8 };
            MarketplaceDetailUi.Bind(dot, Shape.FillProperty, "BrushBorderStrong");
            dots.Children.Add(dot);
        }
        var bar = new Grid { Height = TitleHeight, ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(18, 0) };
        bar.Children.Add(title);
        Grid.SetColumn(dots, 1);
        bar.Children.Add(dots);
        var barBox = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = bar };
        MarketplaceDetailUi.Bind(barBox, Border.BackgroundProperty, "BrushPanelSoft");
        MarketplaceDetailUi.Bind(barBox, Border.BorderBrushProperty, "BrushBorder");

        var body = BuildBody(scene);
        var bodyBox = new Border { Padding = BodyPadding, Child = body, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        root.Children.Add(barBox);
        Grid.SetRow(bodyBox, 1);
        root.Children.Add(bodyBox);
        var stage = new Border
        {
            Width = StageWidth,
            Height = StageHeight,
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            Child = root,
        };
        MarketplaceDetailUi.Bind(stage, Border.BackgroundProperty, "BrushPanel");
        MarketplaceDetailUi.Bind(stage, Border.BorderBrushProperty, "BrushBorder");
        return stage;
    }

    // ------------------------------------------------------------------ части

    private static Control Part(MockPart part) => part switch
    {
        MockInput p => InputBox(p),
        MockRow p => RowBox(p),
        MockButton p => ButtonBox(p),
        MockNote p => NoteBox(p),
        MockToggle p => ToggleBox(p),
        MockTotal p => TotalBox(p),
        MockChips p => ChipsBox(p),
        MockTiles p => TilesBox(p),
        _ => new Border(),
    };

    private static TextBlock Txt(string text, double size, string brush, FontWeight? weight = null) =>
        MarketplaceDetailUi.Text(text, size, brush, weight);

    private static void Ring(Border box, bool hot, double normal = 1)
    {
        box.BorderThickness = new Thickness(hot ? 2 : normal);
        MarketplaceDetailUi.Bind(box, Border.BorderBrushProperty, hot ? "BrushAccent" : "BrushBorder");
    }

    private static string ToneBrush(MockTone tone) => tone switch
    {
        MockTone.Good => "BrushSuccess",
        MockTone.Bad => "BrushDangerOnSoft",
        MockTone.Warn => "BrushAccentText",
        MockTone.Accent => "BrushAccentText",
        _ => "BrushText",
    };

    private static string ToneSoft(MockTone tone) => tone switch
    {
        MockTone.Good => "BrushSuccessSoft",
        MockTone.Bad => "BrushDangerSoft",
        MockTone.Warn => "BrushShiftWarnBg",
        MockTone.Accent => "BrushAccentSoft",
        _ => "BrushPanelSoft",
    };

    private static Control InputBox(MockInput p)
    {
        var label = Txt(p.Label, 12, "BrushTextSoft");
        var value = Txt(p.Value + (p.Hot ? " |" : ""), 15, "BrushText");
        var field = new Border { MinHeight = 36, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(8), Child = value };
        MarketplaceDetailUi.Bind(field, Border.BackgroundProperty, "BrushInput");
        Ring(field, p.Hot);
        return new StackPanel { Spacing = 3, Children = { label, field } };
    }

    private static Control RowBox(MockRow p)
    {
        var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(Txt(p.Text, 14, "BrushText", p.Hot ? FontWeight.SemiBold : FontWeight.Normal));
        if (p.Sub.Length > 0)
            left.Children.Add(Txt(p.Sub, 12, "BrushTextSoft"));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(p.Right.Length > 0 ? "*,12,Auto" : "*") };
        grid.Children.Add(left);
        if (p.Right.Length > 0)
        {
            var right = Txt(p.Right, 14, ToneBrush(p.Tone), FontWeight.SemiBold);
            right.VerticalAlignment = VerticalAlignment.Center;
            right.TextAlignment = TextAlignment.Right;
            right.MaxWidth = 240;
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);
        }
        var box = new Border { MinHeight = 40, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(8), Child = grid };
        MarketplaceDetailUi.Bind(box, Border.BackgroundProperty, p.Hot ? "BrushAccentSoft" : "BrushPanelSoft");
        Ring(box, p.Hot, p.Hot ? 2 : 0);
        return box;
    }

    private static Control ButtonBox(MockButton p)
    {
        var text = Txt(p.Text, 14, p.Primary ? "BrushAccentForeground" : "BrushText", FontWeight.SemiBold);
        text.TextAlignment = TextAlignment.Center;
        var box = new Border { MinHeight = 40, Padding = new Thickness(20, 8), CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = BodyWidth, Child = text };
        MarketplaceDetailUi.Bind(box, Border.BackgroundProperty, p.Primary ? "BrushAccent" : "BrushPanelSoft");
        Ring(box, p.Hot, p.Primary ? 0 : 1);
        if (p.Hot)
            MarketplaceDetailUi.Bind(box, Border.BorderBrushProperty, p.Primary ? "BrushText" : "BrushAccent");
        return box;
    }

    private static Control NoteBox(MockNote p)
    {
        var icon = MarketplaceDetailUi.PathIconOf(p.Tone switch { MockTone.Good => "check-circle-outline", MockTone.Bad => "close", _ => "information-outline" }, 18, ToneBrush(p.Tone));
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 10, 0);
        var text = Txt(p.Text, 14, p.Tone == MockTone.Bad ? "BrushDangerOnSoft" : "BrushText");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var box = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), Child = grid };
        MarketplaceDetailUi.Bind(box, Border.BackgroundProperty, ToneSoft(p.Tone));
        return box;
    }

    private static Control ToggleBox(MockToggle p)
    {
        var knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, Margin = new Thickness(3), HorizontalAlignment = p.On ? HorizontalAlignment.Right : HorizontalAlignment.Left };
        var track = new Border { Width = 46, Height = 24, CornerRadius = new CornerRadius(12), Child = knob, VerticalAlignment = VerticalAlignment.Center };
        MarketplaceDetailUi.Bind(track, Border.BackgroundProperty, p.On ? "BrushAccent" : "BrushBorderStrong");
        var label = Txt(p.Label, 14, "BrushText");
        label.VerticalAlignment = VerticalAlignment.Center;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        grid.Children.Add(label);
        Grid.SetColumn(track, 2);
        grid.Children.Add(track);
        var box = new Border { MinHeight = 40, Padding = new Thickness(12, 6), CornerRadius = new CornerRadius(8), Child = grid };
        MarketplaceDetailUi.Bind(box, Border.BackgroundProperty, p.Hot ? "BrushAccentSoft" : "BrushPanelSoft");
        Ring(box, p.Hot, p.Hot ? 2 : 0);
        return box;
    }

    private static Control TotalBox(MockTotal p)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        var label = Txt(p.Label, 15, "BrushTextMuted", FontWeight.SemiBold);
        label.VerticalAlignment = VerticalAlignment.Center;
        var value = Txt(p.Value, 22, "BrushText", FontWeight.Bold);
        value.TextAlignment = TextAlignment.Right;
        value.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(label);
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        var box = new Border { Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), Child = grid };
        if (p.Hot)
            MarketplaceDetailUi.Bind(box, Border.BackgroundProperty, "BrushAccentSoft");
        Ring(box, p.Hot, 0);
        return box;
    }

    private static Control ChipsBox(MockChips p)
    {
        var wrap = new WrapPanel();
        for (var i = 0; i < p.Items.Count; i++)
        {
            var selected = i == p.Selected;
            var text = Txt(p.Items[i], 13.5, selected ? "BrushAccentForeground" : "BrushText", selected ? FontWeight.SemiBold : FontWeight.Normal);
            var chip = new Border { Padding = new Thickness(14, 6), Margin = new Thickness(0, 0, 8, 6), CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = text };
            MarketplaceDetailUi.Bind(chip, Border.BackgroundProperty, selected ? "BrushAccent" : "BrushPanelSoft");
            MarketplaceDetailUi.Bind(chip, Border.BorderBrushProperty, selected ? "BrushAccent" : "BrushBorder");
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    private static Control TilesBox(MockTiles p)
    {
        var grid = new UniformGrid { Columns = Math.Max(1, p.Items.Count), Margin = new Thickness(0, 0, -8, 0) };
        for (var i = 0; i < p.Items.Count; i++)
        {
            var hot = i == p.Hot;
            var text = Txt(p.Items[i], 13.5, "BrushText", hot ? FontWeight.SemiBold : FontWeight.Normal);
            text.TextAlignment = TextAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            var tile = new Border { MinHeight = 60, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(6, 6), CornerRadius = new CornerRadius(10), Child = text };
            MarketplaceDetailUi.Bind(tile, Border.BackgroundProperty, hot ? "BrushAccentSoft" : "BrushPanelSoft");
            Ring(tile, hot, hot ? 2 : 0);
            grid.Children.Add(tile);
        }
        return grid;
    }
}

/// <summary>Баннер-демонстрация: цветной фон функции, окно-макет, подпись шага, точки, пауза и счётчик.</summary>
public sealed class MarketplaceMockDemoPlayer : Border
{
    /// <summary>Сколько таймеров демонстрации сейчас запущено (в стенде после ухода со страницы должно быть 0).</summary>
    public static int RunningTimers;

    /// <summary>Сколько держится один шаг.</summary>
    public static readonly TimeSpan StepDelay = TimeSpan.FromMilliseconds(3200);

    private readonly IReadOnlyList<MockStep> _steps;
    private readonly List<Control> _scenes = [];
    private readonly List<TextBlock> _captions = [];
    private readonly List<Ellipse> _dots = [];
    private readonly TextBlock _counter = new() { FontSize = 12, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly PathIcon _playIcon = new() { Width = 14, Height = 14, Foreground = Brushes.White, IsHitTestVisible = false };
    private readonly Border _playButton = new();
    private readonly DispatcherTimer _timer = new() { Interval = StepDelay };
    private int _index;
    private bool _paused;
    private bool _timerRunning;

    /// <summary>Номер показанного шага (с нуля) — для проверок.</summary>
    public int Index => _index;

    public int StepCount => _steps.Count;

    /// <summary>Перейти на шаг (для проверок и снимков).</summary>
    public void Show(int index)
    {
        if (_steps.Count == 0)
            return;
        _index = ((index % _steps.Count) + _steps.Count) % _steps.Count;
        Refresh();
    }

    public MarketplaceMockDemoPlayer(IReadOnlyList<MockStep> steps, string colorHex, double minHeight = 0)
    {
        _steps = steps;
        CornerRadius = new CornerRadius(20);
        ClipToBounds = true;
        Padding = new Thickness(16, 14, 16, 12);
        var color = Color.TryParse(colorHex, out var c) ? c : Color.Parse("#FFB208");
        var black = Color.FromRgb(0x10, 0x10, 0x10);
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Mix(color, black, 0.6), 0), new GradientStop(Mix(color, black, 0.38), 1) },
        };
        MinHeight = minHeight;

        // Все кадры лежат в одной ячейке (виден один): высота баннера не прыгает от шага к шагу.
        var stageHost = new Grid();
        var captionHost = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var step in steps)
        {
            var scene = MarketplaceMockScene.Build(step.Scene);
            scene.IsVisible = false;
            _scenes.Add(scene);
            stageHost.Children.Add(scene);
            var caption = new TextBlock
            {
                Text = step.Caption,
                FontSize = 15,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0, // невидимые подписи держат высоту самой длинной
            };
            _captions.Add(caption);
            captionHost.Children.Add(caption);
        }
        var viewbox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Center, Child = stageHost };

        // Точки шагов, пауза, счётчик.
        var dotsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < steps.Count; i++)
        {
            var dot = new Ellipse { Width = 8, Height = 8 };
            var index = i;
            dot.PointerPressed += (_, _) => Show(index);
            _dots.Add(dot);
            dotsRow.Children.Add(dot);
        }
        _playButton.Width = _playButton.Height = 28;
        _playButton.CornerRadius = new CornerRadius(14);
        _playButton.Background = Brushes.Transparent;
        _playButton.Child = _playIcon;
        _playIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _playIcon.VerticalAlignment = VerticalAlignment.Center;
        MarketplaceDetailUi.MakeClickable(_playButton, MarketplaceDetailUi.Word("pause"), TogglePause);
        _counter.Margin = new Thickness(2, 0, 8, 0);
        var controls = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            Padding = new Thickness(10, 2, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { dotsRow, _playButton, _counter } },
        };

        Child = new StackPanel { Children = { viewbox, captionHost, controls } };
        UpdatePlayIcon();
        Refresh();

        _timer.Tick += (_, _) => Advance();
        AttachedToVisualTree += (_, _) => ResumeTimer();
        DetachedFromVisualTree += (_, _) => StopTimer();
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromRgb((byte)(a.R + (b.R - a.R) * t + 0.5), (byte)(a.G + (b.G - a.G) * t + 0.5), (byte)(a.B + (b.B - a.B) * t + 0.5)); // цвет, не деньги

    private void Refresh()
    {
        for (var i = 0; i < _scenes.Count; i++)
        {
            _scenes[i].IsVisible = i == _index;
            _captions[i].Opacity = i == _index ? 1 : 0;
            _dots[i].Fill = new SolidColorBrush(i == _index ? Colors.White : Color.FromArgb(0x66, 255, 255, 255));
        }
        _counter.Text = $"{_index + 1} / {_steps.Count}";
    }

    private void Advance()
    {
        if (_steps.Count < 2)
            return;
        _index = (_index + 1) % _steps.Count;
        Refresh();
    }

    private void ResumeTimer()
    {
        if (_steps.Count < 2 || _paused || _timerRunning)
            return;
        _timer.Start();
        _timerRunning = true;
        Interlocked.Increment(ref RunningTimers);
    }

    private void StopTimer()
    {
        _timer.Stop();
        if (_timerRunning)
        {
            _timerRunning = false;
            Interlocked.Decrement(ref RunningTimers);
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused)
            StopTimer();
        else
            ResumeTimer();
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon()
    {
        _playIcon.Data = MarketplaceDetailUi.Icon(_paused ? "play" : "pause");
        Avalonia.Automation.AutomationProperties.SetName(_playButton, MarketplaceDetailUi.Word(_paused ? "play" : "pause"));
        ToolTip.SetTip(_playButton, MarketplaceDetailUi.Word(_paused ? "play" : "pause"));
    }
}
