using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-27 (владелец: «Ошибка в редактировании штрих кода ценников!»): что нашёл живой проход редактора ценников.
/// 1) смена размера в «Печати»/мастере принтера не растягивала свои элементы редактора (бренд, ячейка, дата) — они уходили за край;
/// 2) «Толщина штриха» 1 держала штрихкод узким, как ни растягивай блок, — подсказка редактора объясняет причину.</summary>
public sealed class LabelBarcodeEditFixesTests
{
    [Fact]
    public void ScaleToSize_StretchesCustomElements_Too()
    {
        var t = LabelDesignPresets.ById("shelf")!.Build(); // 58×40, бренд/ячейка/дата справа
        var brand = t.Extras.First(e => e.Kind == LabelExtraKind.Brand);
        brand.FontSizePx = 10;
        var (x, y, w, h) = (brand.XMm, brand.YMm, brand.WidthMm, brand.HeightMm);

        LabelAutoLayout.ScaleToSize(t, 40, 30);

        brand.XMm.Should().BeApproximately(x * 40 / 58.0, 1e-9);
        brand.WidthMm.Should().BeApproximately(w * 40 / 58.0, 1e-9);
        brand.YMm.Should().BeApproximately(y * 30 / 40.0, 1e-9);
        brand.HeightMm.Should().BeApproximately(h * 30 / 40.0, 1e-9);
        brand.FontSizePx!.Value.Should().BeApproximately(10 * Math.Sqrt(40 / 58.0 * (30 / 40.0)), 1e-9);
        foreach (var e in t.Extras)
        {
            (e.XMm + e.WidthMm).Should().BeLessThanOrEqualTo(40 + 1e-9, $"«{e.Kind}» не уходит за правый край этикетки 40 мм");
            (e.YMm + e.HeightMm).Should().BeLessThanOrEqualTo(30 + 1e-9, $"«{e.Kind}» не уходит за нижний край этикетки 30 мм");
        }

        // Обратно — та же раскладка (путь 58 → 40 → 58 ничего не теряет).
        LabelAutoLayout.ScaleToSize(t, 58, 40);
        brand.XMm.Should().BeApproximately(x, 1e-9);
        brand.WidthMm.Should().BeApproximately(w, 1e-9);
        brand.FontSizePx!.Value.Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void ThinBarsLimit_IsReported_OnlyWhenThicknessOneHoldsTheBarcodeNarrow()
    {
        var t = LabelTemplate.CreateDefault(); // 40×30, блок 36 мм
        const string ean = "4870145004807";
        BarcodeLabelService.DescribeThinBarsLimit(ean, t).Should().BeNull("«Авто» — штрих максимально широкий, подсказки нет");

        t.BarcodeModuleDots = 1;
        BarcodeLabelService.DescribeThinBarsLimit(ean, t).Should().NotBeNullOrWhiteSpace("в блок 36 мм поместился бы штрих в 2–3 точки, а «1 (тонкий)» держит 0,125 мм");

        t.BarcodeModuleDots = 2;
        BarcodeLabelService.DescribeThinBarsLimit(ean, t).Should().BeNull();

        t.BarcodeModuleDots = 1;
        t.BarcodeFormat = LabelBarcodeFormat.QrCode;
        BarcodeLabelService.DescribeThinBarsLimit(ean, t).Should().BeNull("у QR толщины штриха нет");

        t.BarcodeFormat = LabelBarcodeFormat.Auto;
        t.Barcode.WidthMm = 12.5; // в узкий блок и «Авто» даёт 1 точку — об этом и так говорит «очень плотный»
        BarcodeLabelService.DescribeThinBarsLimit(ean, t).Should().BeNull();

        t.Barcode.WidthMm = 36;
        t.Barcode.Enabled = false;
        BarcodeLabelService.DescribeThinBarsLimit(ean, t).Should().BeNull("штрихкод выключен");
    }

    [Theory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Turkish)]
    public void ThinBarsLimit_Text_IsTranslated(AppLanguage lang)
    {
        var saved = UserPreferences.Instance.Language;
        try
        {
            UserPreferences.Instance.Language = lang;
            var t = LabelTemplate.CreateDefault();
            t.BarcodeModuleDots = 1;
            var text = BarcodeLabelService.DescribeThinBarsLimit("4870145004807", t)!;
            text.Should().Contain("0").And.Contain("2");
            if (lang != AppLanguage.Russian)
            {
                UserPreferences.Instance.Language = AppLanguage.Russian;
                BarcodeLabelService.DescribeThinBarsLimit("4870145004807", t).Should().NotBe(text, $"текст на языке {lang}, а не русский");
            }
        }
        finally
        {
            UserPreferences.Instance.Language = saved;
        }
    }
}
