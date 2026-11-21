using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using NurMarketKassa.AvaloniaHost.Services;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-21 («Редактор ценников»): режим «Шаблоны» вкладки «Ценники и штрихкоды» — готовые и сохранённые («Мои шаблоны») шаблоны карточками с
/// предпросмотром: «Править» (открыть в редакторе), «Печатать этим» (сделать рабочим и перейти к печати), у своих — «Переименовать» и «Удалить».
/// «Мои шаблоны» общие для магазина: <see cref="LabelDesignLibrary"/> (идут на все компьютеры через общие настройки).</summary>
public sealed class LabelTemplatesView : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly WrapPanel _ready = new() { Orientation = Orientation.Horizontal };
    private readonly WrapPanel _mine = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _mineTitle = new() { FontSize = 15, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 12, 0, 6) };
    private readonly TextBlock _mineEmpty = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly List<Bitmap> _bitmaps = new();
    private readonly Action<LabelTemplate, string> _edit;
    private readonly Action<LabelTemplate> _usePrint;

    public LabelTemplatesView(Action<LabelTemplate, string> edit, Action<LabelTemplate> usePrint)
    {
        _edit = edit;
        _usePrint = usePrint;
        var readyTitle = new TextBlock { Text = T("Готовые шаблоны", "Даяр шаблондор", "Ready templates", "Hazır şablonlar", "Tayyor shablonlar"), FontSize = 15, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 6) };
        Bind(readyTitle, TextBlock.ForegroundProperty, "BrushText");
        Bind(_mineTitle, TextBlock.ForegroundProperty, "BrushText");
        Bind(_mineEmpty, TextBlock.ForegroundProperty, "BrushTextSoft");
        Bind(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var stack = new StackPanel { Spacing = 0, Children = { readyTitle, _ready, _mineTitle, _mineEmpty, _mine, _status } };
        Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        AttachedToVisualTree += (_, _) => Refresh();
        DetachedFromVisualTree += (_, _) => DisposeBitmaps();
    }

    private void Bind(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));

    private void DisposeBitmaps()
    {
        foreach (var b in _bitmaps)
            b.Dispose();
        _bitmaps.Clear();
    }

    /// <summary>Заново построить карточки (после сохранения/удаления).</summary>
    public void Refresh()
    {
        DisposeBitmaps();
        _ready.Children.Clear();
        _mine.Children.Clear();
        foreach (var p in LabelDesignPresets.All)
        {
            var build = p.Build;
            _ready.Children.Add(Card(p.Name(), build(), null, true));
        }
        var mine = LabelDesignLibrary.Shared.ToList();
        _mineTitle.Text = T($"Мои шаблоны ({mine.Count} из {LabelDesignLibrary.MaxCount})", $"Менин шаблондорум ({mine.Count} / {LabelDesignLibrary.MaxCount})", $"My templates ({mine.Count} of {LabelDesignLibrary.MaxCount})", $"Şablonlarım ({mine.Count} / {LabelDesignLibrary.MaxCount})", $"Mening shablonlarim ({mine.Count} / {LabelDesignLibrary.MaxCount})");
        _mineEmpty.IsVisible = mine.Count == 0;
        _mineEmpty.Text = T("Пока пусто. В режиме «Редактор» нажмите «В «Мои шаблоны»…» — шаблон появится здесь и на всех компьютерах магазина.",
            "Азырынча бош. «Редактор» режиминде «Менин шаблондорума…» басыңыз — шаблон бул жерде жана дүкөндүн бардык компьютерлеринде чыгат.",
            "Empty for now. In the “Editor” mode press “To My templates…” — the template appears here and on all computers of the store.",
            "Şimdilik boş. «Düzenleyici» modunda «Şablonlarıma…» düğmesine basın — şablon burada ve mağazanın tüm bilgisayarlarında görünür.",
            "Hozircha bo'sh. «Muharrir» rejimida «Mening shablonlarimga…» ni bosing — shablon shu yerda va do'konning barcha kompyuterlarida paydo bo'ladi.");
        foreach (var m in mine)
            _mine.Children.Add(Card(m.Name, m.Template, m.Name, false));
    }

    private Control Card(string name, LabelTemplate template, string? libraryName, bool ready)
    {
        var t = template.Clone();
        t.Item = LabelExtras.Sample;
        var image = new Image { Stretch = Stretch.Uniform, Height = 96, HorizontalAlignment = HorizontalAlignment.Center };
        try
        {
            var bmp = LabelSceneRenderer.LabelPreview(T("Образец товара", "Товардын үлгүсү", "Sample product", "Örnek ürün", "Mahsulot namunasi"), "4870145004807", "285,00 сом", t, "SKU-001", "шт",
                UserPreferences.Instance.StoreName, t.Variant is { Enabled: true } ? "44, " + T("Красный", "Кызыл", "Red", "Kırmızı", "Qizil") : null);
            _bitmaps.Add(bmp);
            image.Source = bmp;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label templates thumbnail failed: {ex.GetType().Name}", "WARNING");
        }
        var title = new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
        Bind(title, TextBlock.ForegroundProperty, "BrushText");
        var size = new TextBlock { Text = $"{template.WidthMm:0.#}×{template.HeightMm:0.#} " + T("мм", "мм", "mm", "mm", "mm"), FontSize = 12 };
        Bind(size, TextBlock.ForegroundProperty, "BrushTextSoft");

        var buttons = new WrapPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(Btn(T("Править", "Түзөтүү", "Edit", "Düzenle", "Tahrirlash"), () => _edit(template.Clone(), name), true));
        buttons.Children.Add(Btn(T("Печатать этим", "Ушуну менен басуу", "Print with this", "Bununla yazdır", "Shu bilan chop etish"), () => _usePrint(template.Clone()), false));
        if (!ready && libraryName != null)
        {
            var rename = Btn(T("Переименовать", "Атын өзгөртүү", "Rename", "Yeniden adlandır", "Nomini o'zgartirish"), null, false);
            var nameBox = new TextBox { Text = libraryName, MaxLength = 40, MinHeight = 34, MinWidth = 200 };
            var ok = Btn("OK", null, true);
            var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Width = 240, Children = { nameBox, ok } } };
            rename.Flyout = flyout;
            ok.Click += (_, _) =>
            {
                flyout.Hide();
                if (LabelDesignLibrary.Rename(libraryName, nameBox.Text ?? ""))
                    Refresh();
                else
                    _status.Text = T("Не получилось: имя пустое или уже занято.", "Болгон жок: аты бош же ээленген.", "Not possible: the name is empty or taken.", "Olmadı: ad boş veya kullanılıyor.", "Bo'lmadi: nom bo'sh yoki band.");
            };
            buttons.Children.Add(rename);
            buttons.Children.Add(Btn(T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), () =>
            {
                LabelDesignLibrary.Delete(libraryName);
                Refresh();
            }, false));
        }
        var panel = new StackPanel { Spacing = 4, Children = { image, title, size, buttons } };
        var card = new Border { Width = 236, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = panel };
        Bind(card, Border.BackgroundProperty, "BrushSurfaceSubtle");
        Bind(card, Border.BorderBrushProperty, "BrushBorder");
        AutomationProperties.SetName(card, name);
        return card;
    }

    private static Button Btn(string text, Action? click, bool primary)
    {
        var b = new Button
        {
            Content = text, MinHeight = 32, Padding = new Thickness(8, 0), CornerRadius = new CornerRadius(8), FontSize = 12.5, Margin = new Thickness(0, 0, 4, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Classes.Add(primary ? "sec-primary" : "sec-outline");
        if (click != null)
            b.Click += (_, _) => click();
        return b;
    }
}
