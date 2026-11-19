using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-13 (Н-2, одно поле ввода): количество перед кодом — «3*код», «1,5*код», «3*» + скан; весовые этикетки 20–29; набор «3», «*» вне поля ввода.</summary>
[Collection("UserPreferences")]
public sealed class ScanQuantityPrefixTests : IDisposable
{
    private readonly IReadOnlyList<WeightBarcodeCustomLayout> _savedLayouts = WeightBarcodeParser.CustomLayouts;

    public ScanQuantityPrefixTests() => WeightBarcodeParser.CustomLayouts = new[] { new ShtrikhPcSettings().Normalize().ToLayout() };

    public void Dispose() => WeightBarcodeParser.CustomLayouts = _savedLayouts;

    // ───────────── разбор строки ─────────────

    [Theory]
    [InlineData("3*4600000000001", 3, "4600000000001")]
    [InlineData("3 * 4600000000001", 3, "4600000000001")]
    [InlineData("12*ABC-1", 12, "ABC-1")]
    [InlineData("1,5*4600000000002", 1.5, "4600000000002")]
    [InlineData("1.5*4600000000002", 1.5, "4600000000002")]
    [InlineData("0,25*молоко", 0.25, "молоко")]
    [InlineData("3*", 3, "")]
    [InlineData("3×4600000000001", 3, "4600000000001")]
    [InlineData("9999*x", 9999, "x")]
    public void TryParse_AcceptsQuantityBeforeCode(string input, double quantity, string rest)
    {
        ScanQuantityPrefix.TryParse(input, out var q, out var r).Should().BeTrue();
        q.Should().BeApproximately(quantity, 0.0001);
        r.Should().Be(rest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]
    [InlineData("*4600000000001")]
    [InlineData("0*4600000000001")]
    [InlineData("0,0*код")]
    [InlineData("abc*4600000000001")]
    [InlineData("-3*4600000000001")]
    [InlineData("1,2,3*код")]
    [InlineData("1,*код")]
    [InlineData("1,2345*код")]
    [InlineData("12345*код")] // пять цифр перед «*» — это штрихкод со знаком внутри, не количество
    [InlineData("4600000000001")]
    [InlineData("4600000*000001")]
    public void TryParse_RejectsEverythingElse(string input)
    {
        ScanQuantityPrefix.TryParse(input, out var q, out var r).Should().BeFalse();
        q.Should().Be(0);
        r.Should().BeEmpty();
    }

    [Fact]
    public void QuantityText_UsesCommaAndNoTrailingZeros()
    {
        ScanQuantityPrefix.QuantityText(3).Should().Be("3");
        ScanQuantityPrefix.QuantityText(1.5).Should().Be("1,5");
        ScanQuantityPrefix.QuantityText(0.125).Should().Be("0,125");
    }

    [Theory]
    [InlineData("2700123012505", true)]
    [InlineData("2012345012349", true)]
    [InlineData("2900000000000", true)]
    [InlineData("4600000000001", false)]
    [InlineData("270012301250", false)]
    [InlineData("27001230125O5", false)]
    public void LooksLikeScaleLabel_Is13DigitsStartingWith2(string code, bool expected) =>
        ScanQuantityPrefix.LooksLikeScaleLabel(code).Should().Be(expected);

    // ───────────── в чеке (BasketPanelViewModel) ─────────────

    private const string PieceCode = "4600000000001";
    private const string WeighedCode = "4600000000002";

    private static BasketPanelViewModel MakeVm(CartService cart, FakePrompts prompts, Func<string, Task>? notFound = null)
    {
        CartTestHelper.StartEmptyCart(cart);
        var piece = CartTestHelper.CreateProduct("p1", "Хлеб", 45m);
        piece.Barcode = PieceCode;
        var weighed = CartTestHelper.CreateProduct("p2", "Яблоки", 120m, mustWeigh: true);
        weighed.Barcode = WeighedCode;
        CatalogProductTileVm? Lookup(string code) => code == PieceCode ? piece : code == WeighedCode ? weighed : null;
        return new BasketPanelViewModel(cart, prompts, new FakeCheckout(), new FakeDeferred(), new FakeDisplay(), new FakeWindows(), new FakeDialogs(), new InlineDispatcher(),
            catalogLookup: Lookup, onBarcodeNotFound: notFound);
    }

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
    public async Task QuantityAndCode_AddsThatManyPieces_AndResetsQuantity()
    {
        using var cart = new CartService();
        using var vm = MakeVm(cart, new FakePrompts());

        vm.EnqueueBarcode("3*" + PieceCode);

        (await WaitAsync(() => vm.Lines.Count == 1)).Should().BeTrue();
        vm.Lines[0].Quantity.Should().Be(3);
        vm.ManualQuantity.Should().Be("1", "количество из «3*» — только на один товар");
        vm.HasPendingQuantity.Should().BeFalse();
    }

    [Fact]
    public async Task QuantityAlone_WaitsForNextScan_ThenApplies()
    {
        using var cart = new CartService();
        using var vm = MakeVm(cart, new FakePrompts());

        vm.EnqueueBarcode("3*");
        (await WaitAsync(() => vm.HasPendingQuantity)).Should().BeTrue();
        vm.ManualQuantity.Should().Be("3");
        vm.PendingQuantityText.Should().Contain("3");
        vm.Lines.Should().BeEmpty();

        vm.EnqueueBarcode(PieceCode);
        (await WaitAsync(() => vm.Lines.Count == 1)).Should().BeTrue();
        vm.Lines[0].Quantity.Should().Be(3);
        vm.HasPendingQuantity.Should().BeFalse();

        vm.EnqueueBarcode(PieceCode); // следующий скан — снова одна штука
        (await WaitAsync(() => vm.Lines[0].Quantity == 4)).Should().BeTrue();
    }

    [Fact]
    public async Task PendingQuantity_ResetByEsc()
    {
        using var cart = new CartService();
        using var vm = MakeVm(cart, new FakePrompts());

        vm.EnqueueBarcode("5*");
        (await WaitAsync(() => vm.HasPendingQuantity)).Should().BeTrue();
        vm.ResetPendingQuantity();
        vm.ManualQuantity.Should().Be("1");

        vm.EnqueueBarcode(PieceCode);
        (await WaitAsync(() => vm.Lines.Count == 1)).Should().BeTrue();
        vm.Lines[0].Quantity.Should().Be(1);
    }

    [Theory]
    [InlineData("1,5*")]
    [InlineData("1.5*")]
    public async Task FractionalQuantity_ForWeighedProduct_IsKept(string prefix)
    {
        using var cart = new CartService();
        using var vm = MakeVm(cart, new FakePrompts());

        vm.EnqueueBarcode(prefix + WeighedCode);

        (await WaitAsync(() => vm.Lines.Count == 1)).Should().BeTrue();
        vm.Lines[0].Quantity.Should().BeApproximately(1.5, 0.0001);
        vm.ManualQuantity.Should().Be("1");
    }

    [Fact]
    public async Task FractionalQuantity_ForPieceProduct_IsRoundedToWholePieces()
    {
        using var cart = new CartService();
        using var vm = MakeVm(cart, new FakePrompts());

        vm.EnqueueBarcode("2,4*" + PieceCode);

        (await WaitAsync(() => vm.Lines.Count == 1)).Should().BeTrue();
        vm.Lines[0].Quantity.Should().Be(2, "штучный товар продаётся целыми штуками — правило поля «Количество» то же");
    }

    [Fact]
    public async Task ScaleLabel_AfterQuantity_IgnoresQuantity_AndDoesNotLeaveItForNextItem()
    {
        using var cart = new CartService();
        var prompts = new FakePrompts();
        using var vm = MakeVm(cart, prompts);

        vm.EnqueueBarcode("3*");
        (await WaitAsync(() => vm.HasPendingQuantity)).Should().BeTrue();
        vm.EnqueueBarcode("2700123012505"); // этикетка весов: PLU 123, 1,250 кг; товара с таким PLU в каталоге нет

        (await WaitAsync(() => prompts.Errors.Count > 0)).Should().BeTrue();
        (await WaitAsync(() => !vm.HasPendingQuantity)).Should().BeTrue("количество из «3*» к весовой этикетке не применяется и следующему товару не достаётся");
        cart.Items.Should().BeEmpty();

        vm.EnqueueBarcode(PieceCode);
        (await WaitAsync(() => vm.Lines.Count == 1)).Should().BeTrue();
        vm.Lines[0].Quantity.Should().Be(1);
    }

    [Fact]
    public async Task ScaleLabel_WithQuantityInOneString_IsReadAsLabel_NotMultiplied()
    {
        using var cart = new CartService();
        var prompts = new FakePrompts();
        using var vm = MakeVm(cart, prompts);

        vm.EnqueueBarcode("3*2700123012505");

        (await WaitAsync(() => prompts.Errors.Count > 0)).Should().BeTrue();
        prompts.Errors.Should().ContainSingle().Which.Should().Contain("PLU", "код разобран как весовая этикетка, а не как «товар не найден»");
        (await WaitAsync(() => vm.ManualQuantity == "1")).Should().BeTrue();
        cart.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownCode_WithQuantity_DoesNotKeepQuantity()
    {
        using var cart = new CartService();
        var offered = new List<string>();
        using var vm = MakeVm(cart, new FakePrompts(), code => { offered.Add(code); return Task.CompletedTask; });

        vm.EnqueueBarcode("4*4699999999999");

        (await WaitAsync(() => vm.ManualQuantity == "1" && !vm.IsBusy)).Should().BeTrue("количество из «4*» не остаётся следующему товару");
        await Task.Delay(300);
        offered.Where(code => code != "4699999999999").Should().BeEmpty("в «добавить товар» (если касса его предложила) уходит код без «4*»");
        cart.Items.Should().BeEmpty();
    }

    // ───────────── «3», «*» вне поля ввода (TypedQuantityTracker) ─────────────

    [Fact]
    public void Tracker_HumanTypedDigitsThenStar_GiveQuantity()
    {
        var t = new TypedQuantityTracker();
        t.Digit('3', 1000);
        t.Star(1400).Should().Be("3");

        t.Digit('1', 5000);
        t.Digit('2', 5300);
        t.Star(5700).Should().Be("12");
    }

    [Fact]
    public void Tracker_ScannerSpeed_IsNotQuantity()
    {
        var t = new TypedQuantityTracker();
        t.Digit('4', 1000);
        t.Digit('6', 1010);
        t.Digit('0', 1020);
        t.Star(1030).Should().BeNull("цифры пришли со скоростью сканера — это код со знаком «*», не количество");

        t.Digit('3', 2000);
        t.Star(2010).Should().BeNull("«*» сразу за цифрой (быстрее человека) — часть кода");
    }

    [Fact]
    public void Tracker_RejectsZeroLongStaleAndForgetsAfterOtherKey()
    {
        var t = new TypedQuantityTracker();
        t.Star(1000).Should().BeNull("цифр не было");

        t.Digit('0', 2000);
        t.Star(2400).Should().BeNull("ноль — не количество");

        foreach (var (ch, at) in new[] { ('1', 3000L), ('2', 3200L), ('3', 3400L), ('4', 3600L), ('5', 3800L) })
            t.Digit(ch, at);
        t.Star(4200).Should().BeNull("пять цифр — не количество");

        t.Digit('3', 10000);
        t.Star(10000 + TypedQuantityTracker.StaleMs + 1).Should().BeNull("цифра набрана давно");

        t.Digit('3', 20000);
        t.Reset(); // нажали другую клавишу
        t.Star(20400).Should().BeNull();

        t.Digit('7', 30000);
        t.Star(30300).Should().Be("7");
        t.Star(30600).Should().BeNull("количество отдаётся один раз");
    }
}
