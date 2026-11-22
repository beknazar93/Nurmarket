using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Stock;

/// <summary>2026-11-22, владелец: «в кассе минус товары — количество, ушедшее в минус, показывать по-особенному, что он в минусе на N».
/// Подпись остатка плитки: 0 / 5 / −3 / −0,5 для штучного и весового, остаток не обрезается до 0 после продажи в минус, флаг IsNegativeStock для цвета.
/// Расчёт «доступно к добавлению» (StockAvailabilityService.GetAvailableToAdd) остаётся Math.Max(0, …) — здесь его не трогаем.</summary>
[Collection("UserPreferences")]
public sealed class NegativeStockLabelTests : IDisposable
{
    private readonly AppLanguage _lang = UserPreferences.Instance.Language;
    private readonly bool _sellWithoutStock = UserPreferences.Instance.SellWithoutStock;

    public NegativeStockLabelTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        UserPreferences.Instance.SellWithoutStock = true;
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _lang;
        UserPreferences.Instance.SellWithoutStock = _sellWithoutStock;
    }

    private const string Minus = "−";

    [Theory]
    [InlineData(0d, false, "шт", "0 шт.")]
    [InlineData(5d, false, "шт", "5 шт.")]
    [InlineData(-3d, false, "шт", "Минус " + Minus + "3 шт.")]
    [InlineData(-12d, false, "шт", "Минус " + Minus + "12 шт.")]
    [InlineData(-0.5d, false, "шт", "Минус " + Minus + "0,5 шт.")]
    [InlineData(-0.0001d, false, "шт", "0 шт.")]      // почти ноль — это ноль, не «минус» и не «-0»
    [InlineData(0d, true, "кг", "0 кг")]
    [InlineData(2.5d, true, "кг", "2,5 кг")]
    [InlineData(-3d, true, "кг", "Минус " + Minus + "3 кг")]
    [InlineData(-0.5d, true, "кг", "Минус " + Minus + "0,5 кг")]
    [InlineData(-1.234d, true, "кг", "Минус " + Minus + "1,234 кг")]
    public void FormatStockInfo_Ru(double qty, bool weighed, string unit, string expected)
    {
        StockSyncService.FormatStockInfo(qty, weighed, unit).Should().Be(expected);
    }

    [Theory]
    [InlineData(AppLanguage.Kyrgyz, "Минус −3 даана")]
    [InlineData(AppLanguage.English, "Minus −3 pcs")]
    [InlineData(AppLanguage.Turkish, "Eksi −3 adet")]
    [InlineData(AppLanguage.Uzbek, "Minus −3 dona")]
    public void FormatStockInfo_AllLanguages(AppLanguage lang, string expected)
    {
        UserPreferences.Instance.Language = lang;
        StockSyncService.FormatStockInfo(-3, false, "шт").Should().Be(expected);
    }

    [Fact]
    public void ApplyQuantityToTile_Negative_KeepsRealQuantity_AndShowsMinusLabel()
    {
        var tile = new CatalogProductTileVm("p1", "DECO ПЫЛЕСОС", "9100.00 сом", false) { Unit = "шт" };
        StockSyncService.ApplyQuantityToTile(tile, -3, false);

        tile.Quantity.Should().Be(-3, "остаток не обрезается до 0 — по нему считается долг по остатку");
        tile.StockInfo.Should().Be("Минус " + Minus + "3 шт.");
        tile.IsNegativeStock.Should().BeTrue();
        tile.IsLowStock.Should().BeTrue("фильтры «мало/нет в наличии» работают как раньше");
        tile.StockWithUnitText.Should().Be(Minus + "3 шт");
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(4d)]
    [InlineData(-0.0001d)]
    public void NotNegative_IsNotMinus(double qty)
    {
        var tile = new CatalogProductTileVm("p1", "Товар", "10.00 сом", false) { Unit = "шт" };
        StockSyncService.ApplyQuantityToTile(tile, qty, false);
        tile.IsNegativeStock.Should().BeFalse();
        tile.StockInfo.Should().NotContain("Минус");
        tile.StockWithUnitText.Should().NotContain(Minus);
    }

    [Fact]
    public void QuantityChange_RaisesIsNegativeStock()
    {
        var tile = new CatalogProductTileVm("p1", "Товар", "10.00 сом", false) { Unit = "шт", Quantity = 2 };
        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tile.Quantity = -1;
        raised.Should().Contain(nameof(CatalogProductTileVm.IsNegativeStock));
    }

    [Theory]
    [InlineData(5d, 2d, 3d)]
    [InlineData(1d, 4d, -3d)]       // продали сверх остатка — остаток в минусе на 3
    [InlineData(0d, 0.5d, -0.5d)]   // весовой товар
    [InlineData(2d, 2d, 0d)]
    public void AfterSale_SellWithoutStock_GoesBelowZero(double current, double sold, double expected)
    {
        StockSyncService.AfterSale(current, sold).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void AfterSale_Service_NeverNegative()
    {
        StockSyncService.AfterSale(0, 1, isService: true).Should().Be(0, "у услуги остатка нет");
    }

    [Fact]
    public void AfterSale_SettingOff_NotBelowZero()
    {
        UserPreferences.Instance.SellWithoutStock = false;
        StockSyncService.AfterSale(1, 4).Should().Be(0, "настройка выключена — как раньше, не ниже нуля");
    }

    [Fact]
    public void AfterSale_NoFloatingNoise()
    {
        StockSyncService.AfterSale(0.3, 0.1 + 0.2 + 0.1).Should().Be(-0.1, "вычитание округляется до 3 знаков, без «−0,10000000000000003»");
    }

    [Fact]
    public void Label_TextWidth_IsShort()
    {
        // Подпись должна влезать в плитку ~165 px (минус отступы ~140 px) и в колонку списка 112 px: по 13 px шрифту ~7 px на знак — не длиннее 16 знаков.
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
        {
            UserPreferences.Instance.Language = lang;
            StockSyncService.FormatStockInfo(-12, false, "шт").Length.Should().BeLessThanOrEqualTo(16, lang.ToString());
            StockSyncService.FormatStockInfo(-12.5, true, "кг").Length.Should().BeLessThanOrEqualTo(16, lang.ToString());
        }
    }
}
