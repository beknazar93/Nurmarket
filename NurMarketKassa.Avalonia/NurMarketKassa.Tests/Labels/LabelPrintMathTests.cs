using System.Drawing;
using System.Runtime.Versioning;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-19 (перенос из веб-редактора ценников): «Сколько на товар» (по одной / по остатку / вручную), границы «Своего размера», точки принтера и сдвиги,
/// сохранение настроек (старый файл читается как раньше), «Рамка» на этикетке (сцена и растр драйвера).</summary>
public sealed class LabelPrintMathTests
{
    [Theory]
    [InlineData("default", 3, 17, 3)]   // «По одной»: «Копий каждой»
    [InlineData("stock", 3, 17, 17)]    // «По остатку»
    [InlineData("manual", 3, 17, 1)]    // «Вручную»: по умолчанию 1, остальное — в карточке
    [InlineData(null, 4, 17, 4)]        // старый файл без поля
    [InlineData("мусор", 4, 17, 4)]     // незнакомое значение = «По одной»
    [InlineData("default", 500, 17, 99)] // потолок 99
    [InlineData("stock", 3, 0, 1)]      // нулевой остаток — не меньше 1
    public void BaseCopies_ByMode(string? mode, int each, int stock, int expected) =>
        LabelPrintMath.BaseCopies(mode, each, stock).Should().Be(expected);

    [Theory]
    [InlineData("stock", 5, 17, 2, 2)]     // своё число карточки сильнее режима
    [InlineData("manual", 5, 17, 40, 40)]
    [InlineData("manual", 5, 17, 500, 99)] // и оно не выше 99
    [InlineData("manual", 5, 17, 0, 1)]
    public void CopiesOf_OwnNumberWins(string mode, int each, int stock, int own, int expected) =>
        LabelPrintMath.CopiesOf(mode, each, stock, own).Should().Be(expected);

    [Fact]
    public void CopiesOf_WithoutOwnNumber_UsesMode() =>
        LabelPrintMath.CopiesOf("stock", 1, 12, null).Should().Be(12);

    /// <summary>«По остатку» берёт остаток каталога: целое — как есть, больше 99 — потолок и пометка, дробное/весовое/измеряемое — 1.</summary>
    [Theory]
    [InlineData(5.0, "шт", false, 5, false)]
    [InlineData(99.0, "шт", false, 99, false)]
    [InlineData(250.0, "шт", false, 99, true)]
    [InlineData(2.5, "шт", false, 1, false)]
    [InlineData(10.0, "кг", false, 1, false)]
    [InlineData(10.0, "шт", true, 1, false)]
    [InlineData(0.0, "шт", false, 1, false)]
    public void StockMode_CopiesFromCatalogQuantity(double qty, string unit, bool weighed, int expected, bool capped)
    {
        var copies = BarcodePrintCatalog.CopiesForQuantity(qty, unit, weighed, out var wasCapped);
        copies.Should().Be(expected);
        wasCapped.Should().Be(capped);
        LabelPrintMath.BaseCopies("stock", 1, copies).Should().Be(expected);
    }

    [Theory]
    [InlineData(40, 30, 40, 30)]
    [InlineData(5, 5, 15, 10)]          // ниже нижних границ
    [InlineData(15, 10, 15, 10)]        // сами границы допустимы
    [InlineData(150, 150, 150, 150)]
    [InlineData(999, 999, 150, 150)]    // выше верхних
    [InlineData(-3, -3, 40, 30)]        // отрицательное — размер по умолчанию
    [InlineData(0, 0, 40, 30)]
    [InlineData(double.NaN, double.NaN, 40, 30)]
    public void CustomSize_Bounds(double w, double h, double ew, double eh) =>
        LabelPrintMath.ClampSize(w, h).Should().Be((ew, eh));

    [Theory]
    [InlineData(58, 40, 463, 319)]
    [InlineData(58, 30, 463, 239)]
    [InlineData(40, 30, 319, 239)]
    [InlineData(30, 20, 239, 159)]
    public void StandardSizes_InPrinterDots(double w, double h, int dw, int dh) =>
        LabelPrintMath.LabelDots(w, h).Should().Be((dw, dh));

    [Fact]
    public void Dots_AndMillimetres_RoundTrip_AndOffsetsAreBounded()
    {
        LabelPrintMath.MmToDots(1).Should().Be(8, "203 dpi — 8 точек на мм");
        for (var d = -160; d <= 160; d++)
            LabelPrintMath.MmToDots(LabelPrintMath.DotsToMm(d)).Should().Be(d, $"{d} точек не меняются при хранении в мм");
        LabelPrintMath.ClampOffsetDots(10_000).Should().Be(160);
        LabelPrintMath.ClampOffsetDots(-10_000).Should().Be(-160);
        LabelPrintMath.ClampOffsetDots(7).Should().Be(7);
    }

    [Fact]
    public void Settings_Serialization_RoundTrip_KeepsManualMode()
    {
        var s = new BarcodePrintSettings { CopiesMode = "manual", DefaultCopies = 4 };
        s.Presets.Add(new BarcodeLabelPreset { Name = "Мой", CopiesMode = "manual", DefaultCopies = 2, Template = new LabelTemplate { Frame = true, WidthMm = 58, HeightMm = 40 } });
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<BarcodePrintSettings>(json)!;
        back.CopiesMode.Should().Be("manual");
        back.DefaultCopies.Should().Be(4);
        back.Presets.Should().ContainSingle().Which.Template.Frame.Should().BeTrue();
        back.Presets[0].Template.WidthMm.Should().Be(58);
    }

    [Fact]
    public void Settings_OldFile_ReadsAsBefore()
    {
        // файл 1.17.x: нет ни режима «Вручную», ни шаблонов, ни рамки
        var old = JsonSerializer.Deserialize<BarcodePrintSettings>("{\"DefaultCopies\":3,\"CopiesMode\":\"stock\",\"A4Copies\":2}")!;
        old.CopiesMode.Should().Be("stock");
        old.DefaultCopies.Should().Be(3);
        old.Presets.Should().BeEmpty();
        LabelPrintMath.NormalizeMode(old.CopiesMode).Should().Be("stock");
        LabelPrintMath.NormalizeMode("manual").Should().Be("manual");
        LabelPrintMath.NormalizeMode("что-то").Should().Be("default");
        JsonSerializer.Deserialize<LabelTemplate>("{\"WidthMm\":40,\"HeightMm\":30}")!.Frame.Should().BeFalse("вид старых шаблонов не меняется");
    }

    [Fact]
    public void Template_Clone_KeepsFrame() =>
        new LabelTemplate { Frame = true }.Clone().Frame.Should().BeTrue();

    [Fact]
    public void Scene_Frame_AddsFourLines_OnlyWhenEnabled()
    {
        var t = LabelTemplate.CreateDefault();
        var plain = BarcodeLabelService.BuildLabelScene("Молоко", "4600682000130", "95 сом", t);
        t.Frame = true;
        var framed = BarcodeLabelService.BuildLabelScene("Молоко", "4600682000130", "95 сом", t);
        (framed.Items.Count - plain.Items.Count).Should().Be(4, "четыре линии рамки");
        framed.Items.OfType<LabelSceneRect>().Count(r => r.X == 0 && r.Y == 0 && (r.W == framed.WidthPx || r.H == framed.HeightPx)).Should().BeGreaterThan(0);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Bitmap_Frame_DrawsEdgePixels_OnlyWhenEnabled()
    {
        var t = LabelTemplate.CreateDefault();
        using (var plain = BarcodeLabelService.GenerateLabelBitmap("Молоко", "4600682000130", "95 сом", t))
            plain.GetPixel(0, 0).ToArgb().Should().Be(Color.White.ToArgb());
        t.Frame = true;
        using var framed = BarcodeLabelService.GenerateLabelBitmap("Молоко", "4600682000130", "95 сом", t);
        framed.GetPixel(0, 0).ToArgb().Should().Be(Color.Black.ToArgb());
        framed.GetPixel(framed.Width - 1, framed.Height - 1).ToArgb().Should().Be(Color.Black.ToArgb());
        framed.GetPixel(framed.Width / 2, 0).ToArgb().Should().Be(Color.Black.ToArgb());
    }
}
