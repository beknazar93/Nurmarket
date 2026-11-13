using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-11 (жалоба владельца «по дизайну почему пустота!»): плитки сводки «Склад → Товары» были по 165 px — все выравнивались по высоте плитки
/// «Срок годности» с двумя кнопками столбиком, а в остальных пяти плитках под текстом оставалось пустое место. Теперь «Настройка» — значок в углу,
/// «Списать просроченное» — одна строка, пояснения — до 2 строк. Проверяем на ширинах 1024/1366/1620/1900, масштабе 90/100 %, русском и кыргызском:
/// высота плиток в ряду одинакова и не больше 110 px, кнопки целиком внутри плитки, текст в плитках не обрезан (подписи, суммы, «Списать просроченное»),
/// на узкой ширине ряд делится 3 + 3. Снимки для глаз (не эталоны): %TEMP%\nurmarket-tests\ui-actual\warehouse-kpi-*.png.</summary>
[Collection("UiScenarios")]
public sealed class WarehouseKpiTilesUiTests
{
    private const BindingFlags Inst = BindingFlags.NonPublic | BindingFlags.Instance;

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 800), (1366, 768), (1620, 900), (1900, 1060) }
            .SelectMany(s => new[] { 1.0, 0.9 }.SelectMany(scale => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, scale, l })));

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task OwnerWarehouse_KpiTiles_AreCompact_Uniform_AndInside(int w, int h, double scale, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var shell = new OwnerShellWindow { Width = w, Height = h };
        shell.Show();
        UiShot.Settle(3, 30);
        typeof(OwnerShellWindow).GetField("_shellScale", Inst)!.SetValue(shell, scale);
        WarehouseWindow? wh = null;
        var marks = SetExpiryMarks(3);
        try
        {
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", Inst, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(8, 30);
            Check(wh!, shell, $"владелец {w}x{h} масштаб {scale} {lang}", w, lang, withShot: true);
        }
        finally
        {
            RestoreExpiryMarks(marks);
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
    }

    [AvaloniaTheory]
    [InlineData(1366, 768, AppLanguage.Russian)]
    [InlineData(1024, 800, AppLanguage.Kyrgyz)]
    public async Task KassaWarehouse_KpiTiles_AreCompact_Uniform_AndInside(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var wh = TestHost.Services.GetRequiredService<WarehouseWindow>();
        var marks = SetExpiryMarks(3);
        try
        {
            using var _ = UiShot.Render(wh, new UiSize(w, h));
            UiShot.Settle(6, 30);
            Check(wh, wh, $"касса {w}x{h} {lang}", w, lang, withShot: false);
        }
        finally
        {
            RestoreExpiryMarks(marks);
            wh.Close();
            UiShot.Settle(2, 20);
        }
    }

    /// <summary>Нет просроченных — плитка «Срок годности» без «Списать просроченное», ряд всё равно ровный.</summary>
    [AvaloniaFact]
    public async Task OwnerWarehouse_KpiTiles_NoExpired_StillUniform()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var shell = new OwnerShellWindow { Width = 1620, Height = 900 };
        shell.Show();
        UiShot.Settle(3, 30);
        WarehouseWindow? wh = null;
        var marks = SetExpiryMarks(0);
        try
        {
            var open = typeof(OwnerShellWindow).GetMethod("OpenSection", Inst, null, [typeof(string), typeof(Func<Window>)], null)!;
            open.Invoke(shell, ["warehouse", (Func<Window>)(() => wh = TestHost.Services.GetRequiredService<WarehouseWindow>())]);
            UiShot.Settle(8, 30);
            Check(wh!, shell, "владелец 1620x900 без просроченных", 1620, AppLanguage.Russian, withShot: false, expectWriteOff: false);
        }
        finally
        {
            RestoreExpiryMarks(marks);
            wh?.Close();
            shell.Close();
            UiShot.Settle(2, 20);
        }
    }

    // ------------------------------------------------------------------ проверки

    private static void Check(WarehouseWindow wh, Window root, string tag, int w, AppLanguage lang, bool withShot, bool expectWriteOff = true)
    {
        UiDrive.Find<TabControl>(wh, "WarehouseTabs").SelectedItem = UiDrive.Find<TabItem>(wh, "ProductsTabItem");
        // Цифры как на снимке владельца: 15 468 товаров, 173 767 ед., закупка 50 442 510, продажа 61 605 514, 187 заканчивается, 1 нет в наличии.
        typeof(WarehouseWindow).GetMethod("BuildKpiCards", Inst, null, [typeof(int), typeof(double), typeof(double), typeof(double), typeof(int), typeof(int)], null)!
            .Invoke(wh, [15468, 173767d, 50442510d, 61605514d, 187, 1]);
        UiShot.Settle(4, 30);
        // Фоновое обновление итогов могло перестроить плитки — выставляем те же цифры ещё раз и ждём раскладку.
        typeof(WarehouseWindow).GetMethod("BuildKpiCards", Inst, null, [typeof(int), typeof(double), typeof(double), typeof(double), typeof(int), typeof(int)], null)!
            .Invoke(wh, [15468, 173767d, 50442510d, 61605514d, 187, 1]);
        UiShot.Settle(3, 30);

        var panel = UiDrive.Find<KpiTileGrid>(wh, "WarehouseKpiPanel");
        // 2026-11-12 (аудит Т-5): тихие return убраны. Ниже 660 px плитки скрыты намеренно (те же фильтры — кнопками «Наличие», ApplyProductsCompact) — это теперь проверяется
        // (плитки скрыты И окно действительно ниже 660), а не пропускается; выше 660 плитки обязаны быть видны.
        var tilesHiddenByDesign = wh.ClientSize.Height < 660;
        panel.IsVisible.Should().Be(!tilesHiddenByDesign, $"{tag}: плитки скрыты ровно при высоте окна склада < 660 (сейчас {wh.ClientSize})");
        if (tilesHiddenByDesign)
        {
            WriteDump($"{tag}: плитки скрыты намеренно (окно склада {wh.ClientSize.Height:0} px < 660)");
            return;
        }
        panel.IsVisible.Should().BeTrue($"{tag}: плитки сводки видны; окно склада {wh.ClientSize}");
        // 2026-11-12 (Т-17): папка записи могла быть удалена очисткой TEMP — создаём перед записью
        static void WriteDump(string line)
        {
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "warehouse-kpi-heights.txt"), line + Environment.NewLine);
        }
        // Раскладка выполняется на кадре отрисовки — ждём, пока у плиток появятся размеры.
        for (var i = 0; i < 40 && !(panel.Children.OfType<Border>().All(t => t.Bounds.Height > 0 && t.GetVisualRoot() != null)); i++)
            UiShot.Settle(1, 30);
        if (panel.Children.OfType<Border>().Any(t => t.Bounds.Height <= 0))
        {
            // Диагностика: раскладка плиток не выполнена — состояние раскладки панели и плиток; пробуем довести раскладку окна принудительно.
            var t0 = panel.Children.OfType<Border>().First();
            WriteDump($"{tag}: ДИАГНОСТИКА до UpdateLayout: panel measure={panel.IsMeasureValid} arrange={panel.IsArrangeValid} desired={panel.DesiredSize} bounds={panel.Bounds}; tile0 measure={t0.IsMeasureValid} arrange={t0.IsArrangeValid} desired={t0.DesiredSize} bounds={t0.Bounds} visible={t0.IsVisible}");
            wh.UpdateLayout();
            UiShot.Settle(2, 30);
            WriteDump($"{tag}: после UpdateLayout tile0 desired={t0.DesiredSize} bounds={t0.Bounds}");
        }
        var tiles = panel.Children.OfType<Border>().Where(t => t.IsVisible).ToList();
        tiles.Should().HaveCountGreaterOrEqualTo(5, tag);
        // Для глаз и отчёта: фактические высоты, ширина панели и число плиток в рядах.
        var dumpRows = string.Join("+", tiles.GroupBy(t => Math.Round(t.Bounds.Y)).OrderBy(g => g.Key).Select(g => g.Count()));
        WriteDump($"{tag}: панель {panel.Bounds.Width:0} px, ряды {dumpRows}, высоты {string.Join(", ", tiles.Select(t => t.Bounds.Height.ToString("0.#")))}, ширина плитки {tiles[0].Bounds.Width:0}");

        // 2026-11-12 (аудит Т-5): раньше при нулевых размерах плиток (случай владельца: 90 %, широкое окно) проверялась лишь желаемая высота и тест тихо выходил —
        // остальные проверки не выполнялись вовсе. Теперь нулевые размеры после всех попыток — провал.
        tiles.Should().OnlyContain(t => t.Bounds.Height > 0 && t.Bounds.Width > 0, $"{tag}: раскладка плиток выполнена (Bounds ≠ 0); panel arrange={panel.IsArrangeValid} bounds={panel.Bounds}; высоты {string.Join(", ", tiles.Select(t => t.Bounds.Height.ToString("0.#")))}");

        // Ряды по верхнему краю; в каждом ряду высота одинакова, любая плитка не выше 110.
        var rows = tiles.GroupBy(t => Math.Round(t.Bounds.Y)).OrderBy(g => g.Key).ToList();
        var heights = tiles.Select(t => t.Bounds.Height).ToList();
        foreach (var row in rows)
            row.Select(t => t.Bounds.Height).Distinct().Count().Should().Be(1, $"{tag}: плитки одного ряда одной высоты; {string.Join(", ", heights.Select(x => x.ToString("0.#")))}");
        heights.Max().Should().BeLessThanOrEqualTo(110, $"{tag}: плитка не выше 110 px; высоты {string.Join(", ", heights.Select(x => x.ToString("0.#")))}");
        heights.Min().Should().BeGreaterThan(60, $"{tag}: плитка не сплющена; высоты {string.Join(", ", heights.Select(x => x.ToString("0.#")))}");
        // Узкая раздел (до 1100): 3 + 3, а не растянутый один ряд из шести тесных плиток.
        var perRow = rows.Select(r => r.Count()).ToList();
        if (w <= 1100)
            perRow.Should().Equal([3, 3], $"{tag}: на узкой ширине 3 + 3; сейчас {string.Join("+", perRow)}, панель {panel.Bounds.Width:0}");
        else
            perRow.Should().Equal([tiles.Count], $"{tag}: на широкой ширине один ряд; сейчас {string.Join("+", perRow)}, панель {panel.Bounds.Width:0}");

        // Все кнопки и все тексты — внутри своей плитки.
        foreach (var tile in tiles)
        {
            var tl = tile.TranslatePoint(default, wh)!.Value;
            var tr = tile.TranslatePoint(new Point(tile.Bounds.Width, tile.Bounds.Height), wh)!.Value;
            foreach (var c in tile.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && (c is Button || c is TextBlock)))
            {
                var a = c.TranslatePoint(default, wh)!.Value;
                var b = c.TranslatePoint(new Point(c.Bounds.Width, c.Bounds.Height), wh)!.Value;
                var what = c is Button ? "кнопка" : "текст «" + ((c as TextBlock)?.Text ?? "") + "»";
                a.X.Should().BeGreaterThanOrEqualTo(tl.X - 0.5, $"{tag}: {what} не левее плитки");
                b.X.Should().BeLessThanOrEqualTo(tr.X + 0.5, $"{tag}: {what} не правее плитки");
                a.Y.Should().BeGreaterThanOrEqualTo(tl.Y - 0.5, $"{tag}: {what} не выше плитки");
                b.Y.Should().BeLessThanOrEqualTo(tr.Y + 0.5, $"{tag}: {what} не ниже плитки (плитка {tile.Bounds.Height:0.#})");
            }
        }

        // Кнопки на плитке «Срок годности»: шестерёнка всегда, «Списать просроченное» — при просроченных; обе в одной плитке, «Списать» в одну строку.
        var expiry = tiles[^1];
        var buttons = expiry.GetVisualDescendants().OfType<Button>().ToList();
        buttons.Count.Should().Be(expectWriteOff ? 2 : 1, $"{tag}: кнопки плитки «Срок годности»");
        if (expectWriteOff)
        {
            var writeOff = buttons.Single(b => b.Content is TextBlock { Text: not "⚙" });
            var text = (TextBlock)writeOff.Content!;
            writeOff.Bounds.Height.Should().BeInRange(35.5, 42, $"{tag}: «Списать просроченное» — цель касания ≥ 36 px (Д-12), текст в одну строку");
            writeOff.Bounds.Width.Should().BeGreaterThanOrEqualTo(36, $"{tag}: цель касания «Списать» не уже 36 px");
            NotTrimmed(text, $"{tag}: «Списать просроченное»");
            ToolTip.GetTip(writeOff).Should().NotBeNull();
        }
        var gear = buttons.Single(b => b.Content is TextBlock { Text: "⚙" });
        gear.Bounds.Width.Should().BeGreaterThanOrEqualTo(35.5, $"{tag}: значок «Настройка» — цель касания ≥ 36 px (Д-12)");
        gear.Bounds.Height.Should().BeGreaterThanOrEqualTo(35.5, $"{tag}: значок «Настройка» — цель касания ≥ 36 px (Д-12)");

        // 2026-11-12 (аудит Д-3): «Архив» рядом с переключателями — видимая кнопка с видимым текстом тёмного цвета (на снимке 1900 px / 90 % была пустая рамка).
        var archive = wh.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content is string s && s == "Архив");
        archive.Bounds.Width.Should().BeGreaterThan(60, $"{tag}: «Архив» не пустая рамка (ширина)");
        archive.Bounds.Height.Should().BeGreaterThanOrEqualTo(36, $"{tag}: «Архив» — цель касания ≥ 36 px");
        var archiveText = archive.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Архив");
        archiveText.IsEffectivelyVisible.Should().BeTrue();
        archiveText.Bounds.Width.Should().BeGreaterThan(20, $"{tag}: текст «Архив» разложен");
        (archiveText.Foreground as ISolidColorBrush)?.Color.A.Should().BeGreaterThan(200, $"{tag}: текст «Архив» не прозрачный");
        (archiveText.Foreground as ISolidColorBrush)?.Color.R.Should().BeLessThan(160, $"{tag}: текст «Архив» тёмный на светлой кнопке, не белый");

        // Текст: подписи и суммы не обрезаны; пояснение — до 2 строк, без обрезки на этих ширинах.
        foreach (var tile in tiles)
            foreach (var tb in tile.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text) && t.Text != "⚙"))
                NotTrimmed(tb, $"{tag}: «{tb.Text}»");

        // Подсказки плиток (полный текст и «нажмите, чтобы показать») на месте.
        tiles.Count(t => ToolTip.GetTip(t) == null).Should().Be(0, $"{tag}: у плиток есть подсказки");

        if (withShot)
            Shot(wh, $"warehouse-kpi-{(lang == AppLanguage.Kyrgyz ? "ky" : "ru")}-{w}-{tag.Split("масштаб ").Last().Split(' ')[0]}");
    }

    /// <summary>Текст не обрезан ни многоточием, ни границами: при ширине TextBlock все строки умещаются в его высоту (или одна строка — в его ширину).</summary>
    private static void NotTrimmed(TextBlock tb, string what)
    {
        var typeface = new Typeface(tb.FontFamily ?? FontFamily.Default, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var wraps = tb.TextWrapping != TextWrapping.NoWrap;
        var ft = new FormattedText(tb.Text ?? "", CultureInfo.CurrentUICulture, tb.FlowDirection, typeface, tb.FontSize, null)
        { MaxTextWidth = wraps ? Math.Max(1, tb.Bounds.Width) : double.PositiveInfinity };
        if (wraps)
            ft.Height.Should().BeLessThanOrEqualTo(tb.Bounds.Height + 3, $"{what}: текст целиком (нужно {ft.Height:0.#}, есть {tb.Bounds.Height:0.#}, ширина {tb.Bounds.Width:0.#})");
        else
            ft.Width.Should().BeLessThanOrEqualTo(tb.Bounds.Width + 3, $"{what}: текст целиком по ширине (нужно {ft.Width:0.#}, есть {tb.Bounds.Width:0.#})");
    }

    // 2026-11-11: сроки годности — статика ProductExpiryIndex; ставим просроченные (с 0 — без них) и возвращаем как было.
    private static (object Marks, object Loaded) SetExpiryMarks(int expired)
    {
        var t = typeof(ProductExpiryIndex);
        var fm = t.GetField("_marks", BindingFlags.NonPublic | BindingFlags.Static)!;
        var fl = t.GetField("_loadedUtc", BindingFlags.NonPublic | BindingFlags.Static)!;
        var old = (fm.GetValue(null)!, fl.GetValue(null)!);
        var dict = new Dictionary<string, ProductExpiryIndex.Mark>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < expired; i++)
            dict["kpi-" + i] = new ProductExpiryIndex.Mark("kpi-" + i, "Просрочка " + i, DateTime.Today.AddDays(-5 - i), -5 - i, true, 2);
        fm.SetValue(null, dict);
        fl.SetValue(null, DateTime.UtcNow);
        return old;
    }

    private static void RestoreExpiryMarks((object Marks, object Loaded) old)
    {
        var t = typeof(ProductExpiryIndex);
        t.GetField("_marks", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, old.Marks);
        t.GetField("_loadedUtc", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, old.Loaded);
    }

    private static void Shot(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name.Replace(' ', '-') + ".png"), data.ToArray());
    }
}
