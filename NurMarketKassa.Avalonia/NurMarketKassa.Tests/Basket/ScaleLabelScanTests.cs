using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-06 (В8, весы РС-200, ТЗ Умара): этикетка с неверной контрольной цифрой не уходит в серверный поиск; весовой код неизвестного PLU — понятный текст.</summary>
[Collection("UserPreferences")]
public sealed class ScaleLabelScanTests : IDisposable
{
    private readonly IReadOnlyList<WeightBarcodeCustomLayout> _savedLayouts = WeightBarcodeParser.CustomLayouts;

    public ScaleLabelScanTests() => WeightBarcodeParser.CustomLayouts = new[] { new ShtrikhPcSettings().Normalize().ToLayout() };

    public void Dispose() => WeightBarcodeParser.CustomLayouts = _savedLayouts;

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(30);
        }
        return condition();
    }

    [Fact]
    public async Task BadCheckDigit_AsksToRescan_NoServerSearch_NoAddToStockOffer()
    {
        var offers = 0;
        using var h = new BasketVmHarness(_ => { offers++; return Task.CompletedTask; });

        h.Vm.EnqueueBarcode("2700123012508"); // верная этикетка — 2700123012505

        (await WaitAsync(() => h.Prompts.Errors.Count > 0)).Should().BeTrue();
        h.Prompts.Errors.Should().ContainSingle().Which.Should().Be(ScanMessages.BadScan);
        await Task.Delay(200);
        offers.Should().Be(0, "«Добавить на склад» для испорченного чтения не предлагается");
        h.Cart.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownPlu_ShowsWeighedProductText()
    {
        using var h = new BasketVmHarness();

        h.Vm.EnqueueBarcode("2700123012505"); // PLU 123, 1,250 кг; такого товара в каталоге нет

        (await WaitAsync(() => h.Prompts.Errors.Count > 0)).Should().BeTrue();
        h.Prompts.Errors.Should().ContainSingle().Which.Should().Contain("PLU").And.Contain("NurCRM");
        h.Cart.Items.Should().BeEmpty();
    }
}
