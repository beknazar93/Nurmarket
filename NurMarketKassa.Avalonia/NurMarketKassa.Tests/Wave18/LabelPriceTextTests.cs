using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave18;

/// <summary>2026-11-07 (живая проверка 1.17.81, п. 5): видимая цена на этикетке и ценнике — «60,00 сом» (ru/ky/uz/tr), в английском интерфейсе — «60.00 сом».
/// Сама строка цены каталога (PriceLine, «60.00 сом»), штрихкод и числа протоколов не меняются.</summary>
[Collection("UserPreferences")]
public sealed class LabelPriceTextTests
{
    private static CatalogProductTileVm Tile(string priceLine) => new("id", "Товар", priceLine, false);

    private static void WithLanguage(AppLanguage language, Action body)
    {
        var prefs = UserPreferences.Instance;
        var was = prefs.Language;
        prefs.Language = language;
        try
        {
            body();
        }
        finally
        {
            prefs.Language = was;
        }
    }

    [Theory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    [InlineData(AppLanguage.Turkish)]
    public void LabelPrice_UsesComma_InCommaLanguages(AppLanguage language) =>
        WithLanguage(language, () =>
        {
            BarcodePrintCatalog.LabelPriceText("60.00 сом").Should().Be("60,00 сом");
            BarcodePrintCatalog.LabelPriceText("1250.50 сом").Should().Be("1250,50 сом");
            BarcodePrintCatalog.LabelPrice(Tile("60.00 сом")).Should().Be("60,00 сом");
            BarcodePrintCatalog.LabelPrice(new BarcodePrintItem(Tile("60.00 сом"), "123", 1, VariantPrice: 85)).Should().Be("85,00 сом");
            BarcodePrintCatalog.SheetNumber(85.5m).Should().Be("85,50");
        });

    [Fact]
    public void LabelPrice_UsesDot_InEnglish() =>
        WithLanguage(AppLanguage.English, () =>
        {
            BarcodePrintCatalog.LabelPriceText("60.00 сом").Should().Be("60.00 сом");
            BarcodePrintCatalog.LabelPrice(new BarcodePrintItem(Tile("60.00 сом"), "123", 1, VariantPrice: 85)).Should().Be("85.00 сом");
            BarcodePrintCatalog.SheetNumber(85.5m).Should().Be("85.50");
        });

    [Fact]
    public void LabelPrice_DoesNotTouchCatalogData_OrNonPrices() =>
        WithLanguage(AppLanguage.Russian, () =>
        {
            var tile = Tile("60.00 сом");
            BarcodePrintCatalog.LabelPrice(tile).Should().Be("60,00 сом");
            tile.PriceLine.Should().Be("60.00 сом", "PriceLine — данные, их разбирают другие места");
            BarcodePrintCatalog.LabelPriceText("—").Should().Be("—");
            BarcodePrintCatalog.LabelPriceText(null).Should().BeEmpty();
            BarcodePrintCatalog.ParsePrice("60.00 сом")!.Value.Value.Should().Be(60m, "разбор цены (число) от разделителя не зависит");
        });
}
