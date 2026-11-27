using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using SkiaSharp;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-27 (владелец: «в ценники добавь возможность добавить рамку»): рамка этикетки и ценника на листе A4 — толщина, цвет, скругление,
/// отступ, пунктир; старые шаблоны без полей читаются как раньше (рамки нет); рамка внутри этикетки/ячейки; содержимое отодвигается от неё.</summary>
public sealed class LabelFrameTests
{
    [Fact]
    public void OldJson_HasNoFrame_AndDefaultStyle()
    {
        var t = JsonSerializer.Deserialize<LabelTemplate>("{\"WidthMm\":40,\"HeightMm\":30,\"Frame\":false}")!;
        t.Frame.Should().BeFalse();
        t.FrameThicknessMm.Should().Be(0.3);
        t.FrameRadiusMm.Should().Be(0);
        t.FrameGray.Should().BeFalse();
        var o = JsonSerializer.Deserialize<A4SheetOptions>("{\"Columns\":3,\"Rows\":8}")!;
        o.Frame.Should().BeFalse("лист A4 из старого файла — без рамки, как раньше");
        o.ContentPadMm.Should().Be(1.2);
    }

    [Fact]
    public void FrameSettings_SurviveCloneAndJson()
    {
        var t = new LabelTemplate { Frame = true, FrameThicknessMm = 1.2, FrameRadiusMm = 3, FrameInsetMm = 0.5, FrameGray = true, FrameDashed = true };
        var back = JsonSerializer.Deserialize<LabelTemplate>(JsonSerializer.Serialize(t.Clone()))!;
        (back.Frame, back.FrameThicknessMm, back.FrameRadiusMm, back.FrameInsetMm, back.FrameGray, back.FrameDashed).Should().Be((true, 1.2, 3.0, 0.5, true, true));
        var o = new A4SheetOptions { Frame = true, FrameThicknessMm = 0.8, FrameRadiusMm = 2, FrameGray = true, FrameDashed = true }.Clone();
        (o.Frame, o.FrameThicknessMm, o.FrameRadiusMm, o.FrameGray, o.FrameDashed).Should().Be((true, 0.8, 2.0, true, true));
    }

    [Fact]
    public void Spec_ClampsValues_AndStrokeIsAtLeastOneDot()
    {
        var s = new LabelFrameSpec(99, -3, 50, false, false);
        s.Thickness.Should().Be(LabelFrameSpec.MaxThicknessMm);
        s.Radius.Should().Be(0);
        s.Inset.Should().Be(LabelFrameSpec.MaxInsetMm);
        new LabelFrameSpec(0.01, 0, 0, false, false).StrokePx.Should().BeGreaterThanOrEqualTo(1);
        new LabelFrameSpec(double.NaN, double.NaN, double.NaN, true, false).Thickness.Should().Be(LabelFrameSpec.DefaultThicknessMm);
        new LabelFrameSpec(0.3, 0, 0, true, false).Argb.Should().Be(LabelFrameSpec.GrayArgb);
    }

    [Theory]
    [InlineData(0.3, 0.0, false)]
    [InlineData(2.0, 0.0, false)]
    [InlineData(0.5, 3.0, false)]
    [InlineData(0.5, 0.0, true)]
    public void Scene_Frame_IsInsideTheLabel_AndDrawn(double thickness, double radius, bool dashed)
    {
        var t = LabelTemplate.CreateDefault();
        t.Frame = true;
        t.FrameThicknessMm = thickness;
        t.FrameRadiusMm = radius;
        t.FrameDashed = dashed;
        var scene = BarcodeLabelService.BuildLabelScene("Товар", "4870145004807", "309,00 сом", t);
        if (radius == 0 && !dashed)
        {
            var bars = scene.Items.OfType<LabelSceneRect>().Where(r => r.X == 0 && r.Y == 0).ToList();
            bars.Should().NotBeEmpty("сплошная прямоугольная рамка — полосы от угла этикетки");
        }
        else
        {
            var f = scene.Items.OfType<LabelSceneFrame>().Single();
            (f.X - f.StrokePx / 2).Should().BeGreaterThanOrEqualTo(-0.01f, "рамка не выходит за край — принтер её не обрежет");
            (f.X + f.W + f.StrokePx / 2).Should().BeLessThanOrEqualTo(scene.WidthPx + 0.01f);
            f.Dashed.Should().Be(dashed);
        }
        using var bmp = LabelSceneSkia.Render(scene);
        // Левый край посередине высоты — тёмный (рамка), а не белый.
        var edge = (int)Math.Ceiling(thickness / 25.4 * 203) + 3;
        var darkAtEdge = Enumerable.Range(0, edge).Any(x => Enumerable.Range(bmp.Height / 2, 30).Any(y => SkiaRaster.Luminance(bmp.GetPixel(x, y)) < 128));
        darkAtEdge.Should().BeTrue("рамка нарисована у левого края");
    }

    [Fact]
    public void AutoLayout_KeepsContentOffTheFrame()
    {
        var t = LabelTemplate.CreateDefault();
        t.Frame = true;
        t.FrameThicknessMm = 2;
        t.FrameInsetMm = 1;
        LabelAutoLayout.Apply(t, LabelAutoLayout.BarcodeHeightPx(t));
        foreach (var e in new[] { t.Barcode, t.ProductName, t.Price }.Where(e => e.Enabled))
            LabelDesignerMath.TouchesFrame(e, t).Should().BeFalse("автораскладка ставит элементы внутри рамки 2 мм с отступом 1 мм");
        t.Barcode.XMm = 0.5;
        LabelDesignerMath.TouchesFrame(t.Barcode, t).Should().BeTrue("элемент у края — предупреждение редактора");
        t.Frame = false;
        LabelDesignerMath.TouchesFrame(t.Barcode, t).Should().BeFalse();
    }

    [Fact]
    public void ScaleToSize_FrameStaysOnTheNewEdge()
    {
        var t = LabelTemplate.CreateDefault();
        t.Frame = true;
        LabelAutoLayout.ScaleToSize(t, 58, 40);
        var scene = BarcodeLabelService.BuildLabelScene("Товар", "4870145004807", "309,00 сом", t);
        scene.Items.OfType<LabelSceneRect>().Should().Contain(r => r.X == 0 && r.Y == 0 && Math.Abs(r.W - scene.WidthPx) < 1.01, "верхняя полоса рамки — во всю новую ширину");
    }

    [Fact]
    public void A4Sheet_FrameInsideEveryCell_InsteadOfCutLines_AndContentPadded()
    {
        var item = new A4SheetItem(new PriceTagData("CIF Крем чистящий 500мл", "4600000000001", "309,00 сом"), 1);
        var plain = new A4SheetOptions { Preset = "s24", CutLines = false };
        var framed = plain.Clone();
        framed.Frame = true;
        framed.FrameThicknessMm = 1;
        framed.ContentPadMm.Should().BeGreaterThan(plain.ContentPadMm, "текст и штрихкод отходят от рамки");
        using var a = A4SheetSkia.RenderPage(new[] { item }, 0, plain, 120);
        using var b = A4SheetSkia.RenderPage(new[] { item }, 0, framed, 120);
        var geo = A4SheetService.GetGeometry(framed);
        var x = (int)(geo.GridLeftMm / 25.4 * 120) + 1; // у левого края первой ячейки
        var y = (int)((geo.GridTopMm + geo.CellHmm / 2) / 25.4 * 120);
        SkiaRaster.Luminance(a.GetPixel(x, y)).Should().BeGreaterThan(200, "без рамки у края ячейки бело");
        SkiaRaster.Luminance(b.GetPixel(x, y)).Should().BeLessThan(128, "с рамкой — линия по краю ячейки");
    }
}
