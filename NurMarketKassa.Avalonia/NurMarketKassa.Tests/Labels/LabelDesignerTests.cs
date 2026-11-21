using System.Drawing;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using SkiaSharp;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-21 («Редактор ценников»): модель шаблона со своими элементами, расчёты холста (сетка, границы, выравнивание), рисование своих
/// элементов (GDI+ и сцена/Skia), PDF, «Мои шаблоны» (общие настройки магазина), данные товара для этикетки.</summary>
[Collection("LabelDesignLibrary")]
public sealed class LabelDesignerTests : IDisposable
{
    private readonly string? _cloud = UserPreferences.Instance.LabelDesignsJson;

    public LabelDesignerTests()
    {
        LabelDesignLibrary.ResetForTests();
        UserPreferences.Instance.LabelDesignsJson = null;
        LabelDesignLibrary.Shared.Clear();
    }

    public void Dispose()
    {
        LabelDesignLibrary.Shared.Clear();
        UserPreferences.Instance.LabelDesignsJson = _cloud;
        LabelDesignLibrary.ResetForTests();
    }

    // ------------------------------------------------------------------ модель

    [Fact]
    public void OldTemplateFile_WithoutNewFields_ReadsAsBefore()
    {
        const string old = """{"WidthMm":40,"HeightMm":30,"FontFamily":"Arial","Price":{"XMm":2,"YMm":24.5,"WidthMm":36,"HeightMm":5,"Enabled":true}}""";
        var t = JsonSerializer.Deserialize<LabelTemplate>(old)!;
        t.Extras.Should().NotBeNull().And.BeEmpty();
        t.Price.Bold.Should().BeNull();
        t.Price.Align.Should().BeNull();
        t.Price.Rotation.Should().Be(0);
        t.Item.Should().BeNull();
    }

    [Fact]
    public void Template_RoundTrip_KeepsExtrasAndStyle_ButNotItemData()
    {
        var t = new LabelTemplate { Item = LabelExtras.Sample };
        t.Price.Bold = false;
        t.Price.Align = 2;
        t.Price.Rotation = 90;
        var logo = LabelExtras.Create(LabelExtraKind.Logo, t);
        logo.ImagePath = @"C:\x\logo.png";
        t.Extras.Add(logo);
        t.Extras.Add(LabelExtras.Create(LabelExtraKind.FreeText, t));

        var json = JsonSerializer.Serialize(t);
        json.Should().NotContain("Brand").And.NotContain("OldPriceText"); // данные товара в файл не пишутся
        var back = JsonSerializer.Deserialize<LabelTemplate>(json)!;
        back.Extras.Should().HaveCount(2);
        back.Extras[0].Kind.Should().Be(LabelExtraKind.Logo);
        back.Extras[0].ImagePath.Should().Be(@"C:\x\logo.png");
        back.Extras[1].Text.Should().Be("Текст");
        back.Price.Align.Should().Be(2);
        back.Price.Rotation.Should().Be(90);
        back.Item.Should().BeNull();
    }

    [Fact]
    public void Clone_IsDeep_ForExtras_AndKeepsItem()
    {
        var t = new LabelTemplate { Item = LabelExtras.Sample };
        t.Extras.Add(LabelExtras.Create(LabelExtraKind.Brand, t));
        var c = t.Clone();
        c.Extras[0].XMm = 33;
        t.Extras[0].XMm.Should().NotBe(33);
        c.Item.Should().BeSameAs(t.Item);
    }

    // ------------------------------------------------------------------ расчёты холста

    [Theory]
    [InlineData(1.26, 0.5, 1.5)]
    [InlineData(1.24, 0.5, 1.0)]
    [InlineData(3.3, 1, 3)]
    [InlineData(3.3, 0, 3.3)]
    public void Snap_RoundsToGrid(double value, double step, double expected) =>
        LabelDesignerMath.Snap(value, step).Should().BeApproximately(expected, 1e-9);

    [Fact]
    public void ClampInside_KeepsElementOnLabel_AndMinimumSize()
    {
        var t = new LabelTemplate { WidthMm = 40, HeightMm = 30 };
        var e = new LabelElementLayout { XMm = 38, YMm = 29, WidthMm = 10, HeightMm = 10 };
        LabelDesignerMath.ClampInside(e, t);
        (e.XMm + e.WidthMm).Should().BeLessThanOrEqualTo(40.0001);
        (e.YMm + e.HeightMm).Should().BeLessThanOrEqualTo(30.0001);
        e.XMm.Should().BeGreaterThanOrEqualTo(0);
        var tiny = new LabelElementLayout { XMm = 1, YMm = 1, WidthMm = 0, HeightMm = -4 };
        LabelDesignerMath.ClampInside(tiny, t);
        tiny.WidthMm.Should().BeGreaterThanOrEqualTo(LabelDesignerMath.MinElementMm);
        tiny.HeightMm.Should().BeGreaterThanOrEqualTo(LabelDesignerMath.MinElementMm);
    }

    [Fact]
    public void Move_SnapsAndStaysInside()
    {
        var t = new LabelTemplate { WidthMm = 40, HeightMm = 30 };
        var e = new LabelElementLayout { XMm = 2, YMm = 2, WidthMm = 10, HeightMm = 5 };
        LabelDesignerMath.Move(e, t, 2, 2, 3.13, 1.9, 0.5);
        e.XMm.Should().Be(5);
        e.YMm.Should().Be(4);
        LabelDesignerMath.Move(e, t, 2, 2, 500, -500, 0.5);
        e.XMm.Should().Be(30);
        e.YMm.Should().Be(0);
    }

    [Fact]
    public void Resize_SnapsAndDoesNotLeaveLabel()
    {
        var t = new LabelTemplate { WidthMm = 40, HeightMm = 30 };
        var e = new LabelElementLayout { XMm = 30, YMm = 20, WidthMm = 5, HeightMm = 5 };
        LabelDesignerMath.Resize(e, t, 5, 5, 40, 40, 1);
        (e.XMm + e.WidthMm).Should().BeLessThanOrEqualTo(40.0001);
        (e.YMm + e.HeightMm).Should().BeLessThanOrEqualTo(30.0001);
    }

    [Fact]
    public void Align_PutsElementOnTheEdges()
    {
        var t = new LabelTemplate { WidthMm = 40, HeightMm = 30 };
        var e = new LabelElementLayout { XMm = 5, YMm = 5, WidthMm = 10, HeightMm = 6 };
        LabelDesignerMath.Align(e, t, LabelAlignTo.Right);
        e.XMm.Should().Be(30);
        LabelDesignerMath.Align(e, t, LabelAlignTo.CenterH);
        e.XMm.Should().Be(15);
        LabelDesignerMath.Align(e, t, LabelAlignTo.Bottom);
        e.YMm.Should().Be(24);
        LabelDesignerMath.Align(e, t, LabelAlignTo.MiddleV);
        e.YMm.Should().Be(12);
        LabelDesignerMath.Align(e, t, LabelAlignTo.Left);
        LabelDesignerMath.Align(e, t, LabelAlignTo.Top);
        (e.XMm, e.YMm).Should().Be((0.0, 0.0));
    }

    [Fact]
    public void Nudge_MovesByGridStep()
    {
        var t = new LabelTemplate { WidthMm = 40, HeightMm = 30 };
        var e = new LabelElementLayout { XMm = 10, YMm = 10, WidthMm = 5, HeightMm = 5 };
        LabelDesignerMath.Nudge(e, t, 1, -0.5, 0.5);
        (e.XMm, e.YMm).Should().Be((11.0, 9.5));
    }

    [Fact]
    public void Sizes_AreInsideAllowedRange_AndContainWebSizes()
    {
        foreach (var (w, h) in LabelDesignerMath.Sizes)
        {
            w.Should().BeInRange(LabelDesignerMath.MinSizeMm, LabelDesignerMath.MaxSizeMm);
            h.Should().BeInRange(LabelDesignerMath.MinSizeMm, LabelDesignerMath.MaxSizeMm);
        }
        LabelDesignerMath.Sizes.Should().Contain(new[] { (30.0, 20.0), (40.0, 30.0), (58.0, 40.0), (58.0, 30.0) });
        LabelDesignerMath.ClampSize(5).Should().Be(15);
        LabelDesignerMath.ClampSize(400).Should().Be(150);
    }

    [Fact]
    public void Overlaps_FindsOnlyRealIntersections()
    {
        var a = new LabelElementLayout { XMm = 0, YMm = 0, WidthMm = 10, HeightMm = 10 };
        LabelDesignerMath.Overlaps(a, new LabelElementLayout { XMm = 5, YMm = 5, WidthMm = 10, HeightMm = 10 }).Should().BeTrue();
        LabelDesignerMath.Overlaps(a, new LabelElementLayout { XMm = 10, YMm = 0, WidthMm = 5, HeightMm = 10 }).Should().BeFalse();
    }

    [Fact]
    public void Presets_AreValid_FitTheLabel_AndRender()
    {
        LabelDesignPresets.All.Select(p => p.Id).Should().OnlyHaveUniqueItems();
        LabelDesignPresets.All.Should().Contain(p => p.Id == "promo");
        foreach (var p in LabelDesignPresets.All)
        {
            var t = p.Build();
            t.WidthMm.Should().BeInRange(15, 150, p.Id);
            foreach (var e in LabelDesignerMath.Visible(t))
            {
                (e.XMm + e.WidthMm).Should().BeLessThanOrEqualTo(t.WidthMm + 0.01, p.Id);
                (e.YMm + e.HeightMm).Should().BeLessThanOrEqualTo(t.HeightMm + 0.01, p.Id);
            }
            t.Item = LabelExtras.Sample;
            using var bmp = LabelSceneSkia.Render(BarcodeLabelService.BuildLabelScene("Тест", "4870145004807", "285,00 сом", t, "A1", "шт", "Магазин"));
            Dark(bmp, 0, 0, bmp.Width, bmp.Height).Should().BeGreaterThan(50, p.Id);
        }
    }

    // ------------------------------------------------------------------ рисование (сцена / Skia)

    private static int Dark(SKBitmap b, int x0, int y0, int x1, int y1)
    {
        var n = 0;
        x1 = Math.Min(x1, b.Width);
        y1 = Math.Min(y1, b.Height);
        for (var y = Math.Max(y0, 0); y < y1; y++)
            for (var x = Math.Max(x0, 0); x < x1; x++)
                if (SkiaRaster.Luminance(b.GetPixel(x, y)) < 128)
                    n++;
        return n;
    }

    private static (int X0, int Y0, int X1, int Y1) Px(LabelElementLayout e) =>
        ((int)(e.XMm / 25.4 * 203), (int)(e.YMm / 25.4 * 203), (int)((e.XMm + e.WidthMm) / 25.4 * 203) + 1, (int)((e.YMm + e.HeightMm) / 25.4 * 203) + 1);

    /// <summary>Шаблон без встроенных элементов — на холсте только то, что проверяем.</summary>
    private static LabelTemplate Bare(double w = 58, double h = 40)
    {
        var t = new LabelTemplate { WidthMm = w, HeightMm = h };
        foreach (var (_, e) in LabelDesignerMath.BuiltIns(t))
            e.Enabled = false;
        return t;
    }

    private static SKBitmap Scene(LabelTemplate t) =>
        LabelSceneSkia.Render(BarcodeLabelService.BuildLabelScene("Молоко", "4870145004807", "95,50 сом", t, "SKU-1", "шт", "Магазин"));

    [Fact]
    public void FreeText_IsDrawnInsideItsBlock_OnlyThere()
    {
        var t = Bare();
        var x = LabelExtras.Create(LabelExtraKind.FreeText, t);
        x.Text = "АКЦИЯ";
        (x.XMm, x.YMm, x.WidthMm, x.HeightMm) = (4, 4, 30, 8);
        t.Extras.Add(x);
        using var bmp = Scene(t);
        var r = Px(x);
        Dark(bmp, r.X0, r.Y0, r.X1, r.Y1).Should().BeGreaterThan(30);
        Dark(bmp, 0, r.Y1 + 2, bmp.Width, bmp.Height).Should().Be(0);
    }

    [Fact]
    public void Align_Left_And_Right_MoveTheText()
    {
        int InkCenterX(int align)
        {
            var t = Bare();
            var x = LabelExtras.Create(LabelExtraKind.FreeText, t);
            x.Text = "Ab";
            (x.XMm, x.YMm, x.WidthMm, x.HeightMm) = (4, 4, 50, 8);
            x.Align = align;
            t.Extras.Add(x);
            using var bmp = Scene(t);
            long sum = 0, n = 0;
            for (var py = 0; py < bmp.Height; py++)
                for (var px = 0; px < bmp.Width; px++)
                    if (SkiaRaster.Luminance(bmp.GetPixel(px, py)) < 128) { sum += px; n++; }
            n.Should().BeGreaterThan(0);
            return (int)(sum / n);
        }
        var left = InkCenterX(0);
        var center = InkCenterX(1);
        var right = InkCenterX(2);
        left.Should().BeLessThan(center);
        center.Should().BeLessThan(right);
    }

    [Fact]
    public void Rotation90_MakesTheTextVertical()
    {
        (int W, int H) InkSize(int rotation)
        {
            var t = Bare();
            var x = LabelExtras.Create(LabelExtraKind.FreeText, t);
            x.Text = "ВЕРТИКАЛЬ";
            (x.XMm, x.YMm, x.WidthMm, x.HeightMm) = (20, 4, 8, 32); // узкая высокая рамка
            x.Rotation = rotation;
            t.Extras.Add(x);
            using var bmp = Scene(t);
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            for (var py = 0; py < bmp.Height; py++)
                for (var px = 0; px < bmp.Width; px++)
                    if (SkiaRaster.Luminance(bmp.GetPixel(px, py)) < 128) { minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minY = Math.Min(minY, py); maxY = Math.Max(maxY, py); }
            maxX.Should().BeGreaterThan(-1);
            return (maxX - minX, maxY - minY);
        }
        var flat = InkSize(0);
        var vertical = InkSize(90);
        vertical.H.Should().BeGreaterThan(vertical.W);
        flat.W.Should().BeGreaterThan(flat.H);
    }

    [Fact]
    public void Line_IsDrawn_AndLogoIsDrawnFromFile()
    {
        var t = Bare();
        var line = LabelExtras.Create(LabelExtraKind.Line, t);
        (line.XMm, line.YMm, line.WidthMm, line.HeightMm) = (2, 10, 54, 0.6);
        t.Extras.Add(line);
        var path = Path.Combine(Path.GetTempPath(), "nm-logo-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            using (var src = new SKBitmap(40, 40))
            {
                using var c = new SKCanvas(src);
                c.Clear(SKColors.White);
                using var paint = new SKPaint { Color = SKColors.Black };
                c.DrawRect(new SKRect(5, 5, 35, 35), paint);
                File.WriteAllBytes(path, SkiaRaster.ToPng(src));
            }
            var logo = LabelExtras.Create(LabelExtraKind.Logo, t);
            (logo.XMm, logo.YMm, logo.WidthMm, logo.HeightMm) = (4, 16, 12, 12);
            logo.ImagePath = path;
            t.Extras.Add(logo);
            using var bmp = Scene(t);
            var l = Px(line);
            Dark(bmp, l.X0, l.Y0, l.X1, l.Y1).Should().BeGreaterThan(100);
            var g = Px(logo);
            var ink = Dark(bmp, g.X0, g.Y0, g.X1, g.Y1);
            ink.Should().BeGreaterThan(200);
            // «Квадрат с белой каймой»: углы блока светлые.
            Dark(bmp, g.X0, g.Y0, g.X0 + 3, g.Y0 + 3).Should().Be(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LogoWithoutFile_IsSkippedWithoutError()
    {
        var t = Bare();
        var logo = LabelExtras.Create(LabelExtraKind.Logo, t);
        logo.ImagePath = @"C:\no\such\logo.png";
        t.Extras.Add(logo);
        using var bmp = Scene(t);
        Dark(bmp, 0, 0, bmp.Width, bmp.Height).Should().Be(0);
    }

    [Fact]
    public void ProductData_ElementsAppearOnlyWhenTheProductHasThem()
    {
        var t = Bare();
        foreach (var k in new[] { LabelExtraKind.OldPrice, LabelExtraKind.Discount, LabelExtraKind.Brand, LabelExtraKind.Cell })
        {
            var e = LabelExtras.Create(k, t);
            e.YMm = 2 + 8 * Array.IndexOf(new[] { LabelExtraKind.OldPrice, LabelExtraKind.Discount, LabelExtraKind.Brand, LabelExtraKind.Cell }, k);
            e.XMm = 2;
            e.WidthMm = 30;
            e.HeightMm = 6;
            t.Extras.Add(e);
        }
        using (var none = Scene(t))
            Dark(none, 0, 0, none.Width, none.Height).Should().Be(0); // у образца без данных — пусто
        t.Item = LabelExtras.Sample;
        using var full = Scene(t);
        foreach (var e in t.Extras)
        {
            var r = Px(e);
            Dark(full, r.X0, r.Y0, r.X1, r.Y1).Should().BeGreaterThan(20, e.Kind.ToString());
        }
    }

    [Fact]
    public void ExtraTexts_FollowTheRules()
    {
        var t = new LabelTemplate { PriceCurrencyText = "сом", Item = new LabelItemData("Bosch", "350.00 сом", 18.5, "B-7") };
        string? Text(LabelExtraKind k, string? custom = null) => LabelExtras.TextFor(new LabelCustomElement { Kind = k, Text = custom }, t);
        Text(LabelExtraKind.Brand).Should().Be("Bosch");
        Text(LabelExtraKind.Cell).Should().Be("B-7");
        Text(LabelExtraKind.Discount).Should().Be("−18.5%");
        Text(LabelExtraKind.OldPrice).Should().Contain("350");
        Text(LabelExtraKind.FreeText, "Новинка").Should().Be("Новинка");
        Text(LabelExtraKind.Date).Should().MatchRegex(@"^\d{2}\.\d{2}\.\d{4}$");
        Text(LabelExtraKind.Date, "yyyy").Should().Be(DateTime.Now.Year.ToString());
        Text(LabelExtraKind.Date, "ддд").Should().NotBeNull(); // странный формат не роняет
        t.Item = new LabelItemData();
        Text(LabelExtraKind.Brand).Should().BeNull();
        Text(LabelExtraKind.Discount).Should().BeNull();
    }

    [Fact]
    public void BoldAndItalic_OverrideTheDefaults_InScene()
    {
        var t = Bare();
        t.Price.Enabled = true;
        t.Price.Bold = false;
        t.Price.Italic = true;
        var scene = BarcodeLabelService.BuildLabelScene("x", "4870145004807", "10 сом", t);
        var text = scene.Items.OfType<LabelSceneText>().Single();
        text.Bold.Should().BeFalse();
        text.Italic.Should().BeTrue();
        t.Price.Bold = null;
        t.Price.Italic = null;
        BarcodeLabelService.BuildLabelScene("x", "4870145004807", "10 сом", t).Items.OfType<LabelSceneText>().Single().Bold.Should().BeTrue(); // как раньше
    }

    [SupportedOSPlatform("windows")]
    [Fact]
    public void Gdi_DrawsExtras_LikeTheScene()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var t = Bare();
        var x = LabelExtras.Create(LabelExtraKind.FreeText, t);
        x.Text = "АКЦИЯ";
        (x.XMm, x.YMm, x.WidthMm, x.HeightMm) = (4, 4, 30, 8);
        x.Align = 0;
        t.Extras.Add(x);
        var line = LabelExtras.Create(LabelExtraKind.Line, t);
        (line.XMm, line.YMm, line.WidthMm, line.HeightMm) = (2, 20, 54, 0.6);
        t.Extras.Add(line);
        var rot = LabelExtras.Create(LabelExtraKind.FreeText, t);
        rot.Text = "ВЕРТ";
        (rot.XMm, rot.YMm, rot.WidthMm, rot.HeightMm) = (44, 4, 8, 30);
        rot.Rotation = 90;
        t.Extras.Add(rot);
        using var bmp = BarcodeLabelService.GenerateLabelBitmap("x", "4870145004807", null, t);
        int Ink(LabelElementLayout e)
        {
            var r = Px(e);
            var n = 0;
            for (var py = r.Y0; py < Math.Min(r.Y1, bmp.Height); py++)
                for (var px = r.X0; px < Math.Min(r.X1, bmp.Width); px++)
                    if (bmp.GetPixel(px, py).GetBrightness() < 0.5f) n++;
            return n;
        }
        Ink(x).Should().BeGreaterThan(30);
        Ink(line).Should().BeGreaterThan(100);
        Ink(rot).Should().BeGreaterThan(30);
        // Повёрнутая рамка: чернила тянутся по высоте, а не по ширине.
        var rr = Px(rot);
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var py = rr.Y0; py < Math.Min(rr.Y1, bmp.Height); py++)
            for (var px = rr.X0; px < Math.Min(rr.X1, bmp.Width); px++)
                if (bmp.GetPixel(px, py).GetBrightness() < 0.5f) { minX = Math.Min(minX, px); maxX = Math.Max(maxX, px); minY = Math.Min(minY, py); maxY = Math.Max(maxY, py); }
        (maxY - minY).Should().BeGreaterThan(maxX - minX);
    }

    // ------------------------------------------------------------------ PDF

    [Fact]
    public void Pdf_HasLabelPageSize_AndOnePagePerCopy()
    {
        var t = new LabelTemplate { WidthMm = 58, HeightMm = 40, Item = LabelExtras.Sample };
        t.Extras.Add(LabelExtras.Create(LabelExtraKind.Brand, t));
        var pdf = LabelPdf.Create("Молоко", "4870145004807", "95,50 сом", t, 3, "A1", "шт", "Магазин");
        System.Text.Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        var m = Regex.Match(text, @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)\s*\]");
        m.Success.Should().BeTrue();
        double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(58 * 72 / 25.4, 0.6);
        double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture).Should().BeApproximately(40 * 72 / 25.4, 0.6);
        Regex.Matches(text, @"/Type\s*/Page\b(?!s)").Count.Should().Be(3);
        LabelPdf.Create("x", "4870145004807", null, t, 500).Length.Should().BeGreaterThan(100); // копии ограничены 99
    }

    // ------------------------------------------------------------------ «Мои шаблоны»

    [Fact]
    public void Library_SaveRenameDelete_AndKeepsTheLimit()
    {
        var t = new LabelTemplate { WidthMm = 58, HeightMm = 40 };
        t.Extras.Add(LabelExtras.Create(LabelExtraKind.FreeText, t));
        LabelDesignLibrary.Upsert("Акция", t, 2)!.Name.Should().Be("Акция");
        LabelDesignLibrary.Upsert("акция", t, 3); // то же имя (без учёта регистра) — перезапись
        LabelDesignLibrary.Shared.Should().HaveCount(1);
        LabelDesignLibrary.Shared[0].DefaultCopies.Should().Be(3);
        LabelDesignLibrary.Shared[0].Template.Extras.Should().HaveCount(1);
        LabelDesignLibrary.Upsert("   ", t).Should().BeNull();
        LabelDesignLibrary.Rename("Акция", "Полка").Should().BeTrue();
        LabelDesignLibrary.Rename("Полка", "Полка").Should().BeTrue();
        LabelDesignLibrary.Upsert("Второй", t);
        LabelDesignLibrary.Rename("Второй", "полка").Should().BeFalse(); // занято
        LabelDesignLibrary.Delete("Полка").Should().BeTrue();
        LabelDesignLibrary.Delete("нет такого").Should().BeFalse();
        for (var i = 0; i < 40; i++)
            LabelDesignLibrary.Upsert("Ш" + i, t);
        LabelDesignLibrary.Shared.Count.Should().Be(LabelDesignLibrary.MaxCount);
        LabelDesignLibrary.Upsert("Лишний", t).Should().BeNull();
    }

    [Fact]
    public void Library_MirrorsToSharedStoreSettings_AndTakesNewOnesFromThem()
    {
        var t = new LabelTemplate { WidthMm = 40, HeightMm = 20 };
        LabelDesignLibrary.Upsert("На все кассы", t);
        var json = UserPreferences.Instance.LabelDesignsJson;
        json.Should().NotBeNullOrEmpty();
        JsonSerializer.Deserialize<List<BarcodeLabelPreset>>(json!)!.Select(p => p.Name).Should().Equal("На все кассы");
        System.Text.Encoding.UTF8.GetByteCount(json!).Should().BeLessThan(LabelDesignLibrary.MaxJsonBytes);

        // Другой компьютер магазина поменял список — общие настройки принесли новое значение: общий список заменяется.
        var other = new List<BarcodeLabelPreset> { new() { Name = "С другого ПК", Template = new LabelTemplate { WidthMm = 30, HeightMm = 20 } } };
        UserPreferences.Instance.LabelDesignsJson = JsonSerializer.Serialize(other);
        LabelDesignLibrary.Shared.Select(p => p.Name).Should().Equal("С другого ПК");
        BarcodePrintSettingsStore.Load().Presets.Select(p => p.Name).Should().Equal("С другого ПК"); // окно печати видит тот же список
    }

    [Fact]
    public void Library_SkipsBrokenCloudValue()
    {
        LabelDesignLibrary.Upsert("Свой", new LabelTemplate());
        UserPreferences.Instance.LabelDesignsJson = "{это не json";
        LabelDesignLibrary.Shared.Select(p => p.Name).Should().Equal("Свой");
    }

    [Fact]
    public void LabelDesignsJson_IsInTheCompanyKeysOfCloudSync()
    {
        var file = FindRepoFile("NurMarketKassa.Infrastructure", "Services", "SettingsCloudSync.cs");
        File.ReadAllText(file).Should().Contain("\"LabelDesignsJson\"");
        typeof(UserPreferences).GetProperty("LabelDesignsJson").Should().NotBeNull();
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    // ------------------------------------------------------------------ данные товара

    [Fact]
    public void LabelFor_UsesDiscountOnlyWhenTheTemplateAsksForIt()
    {
        var p = new CatalogProductTileVm("1", "Товар", "100,00 сом", false) { Brand = " Nur ", DiscountPercent = 20 };
        var plain = new LabelTemplate();
        var (price0, data0) = BarcodePrintCatalog.LabelFor(new BarcodePrintItem(p, "4870145004807", 1), plain);
        price0.Should().Be("100,00 сом");
        data0.OldPriceText.Should().BeNull();
        data0.Brand.Should().Be("Nur");

        var promo = new LabelTemplate();
        promo.Extras.Add(LabelExtras.Create(LabelExtraKind.OldPrice, promo));
        var (price1, data1) = BarcodePrintCatalog.LabelFor(new BarcodePrintItem(p, "4870145004807", 1), promo);
        price1.Should().Be("80,00 сом");
        data1.OldPriceText.Should().Be("100,00 сом");
        data1.DiscountPercent.Should().Be(20);

        p.DiscountPercent = 0;
        var (price2, data2) = BarcodePrintCatalog.LabelFor(new BarcodePrintItem(p, "4870145004807", 1), promo);
        price2.Should().Be("100,00 сом");
        data2.OldPriceText.Should().BeNull();
    }
}
