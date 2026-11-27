using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-26 (просьба владельца для мясных магазинов: «при покупке коровы вес сразу убавлять, для продажи остаётся другой вес»): «Выход мяса при приходе».
/// Расчёт (CarcassYield, decimal), состояние строки приёмки (ReceivingLineVm.Carcass.cs), примечание прихода, черновик, память выхода и включение модуля.
/// Главный пример: 300 кг по 400 сом/кг, выход 75 % → в продаже 225 кг, отходы 75 кг, сумма закупки 120 000, себестоимость в продаже 533,33 сом/кг.</summary>
[Collection("UserPreferences")]
public sealed class CarcassYieldTests : IDisposable
{
    private readonly AppLanguage _lang = UserPreferences.Instance.Language;
    private readonly bool _flag = UserPreferences.Instance.CarcassYieldEnabled;
    private readonly string? _account = UserPreferences.Instance.LastDataAccountKey;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "carcass-" + Guid.NewGuid().ToString("N")[..8]);

    public CarcassYieldTests()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        UserPreferences.Instance.CarcassYieldEnabled = true;
        UserPreferences.Instance.LastDataAccountKey = null; // без компании модули сервера не читаются — решает местный признак
        CompanyModules.ResetForTests();
        Directory.CreateDirectory(_dir);
        CarcassYieldMemory.DataDirOverride = _dir;
        CarcassYieldMemory.Reset();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _lang;
        UserPreferences.Instance.CarcassYieldEnabled = _flag;
        UserPreferences.Instance.LastDataAccountKey = _account;
        CompanyModules.ResetForTests();
        CarcassYieldMemory.DataDirOverride = null;
        CarcassYieldMemory.Reset();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static CarcassYieldResult Calc(decimal gross, decimal? price = null, decimal? total = null, decimal? yield = null, decimal? net = null, decimal? markup = null)
    {
        var r = CarcassYield.Calculate(new CarcassYieldInput(gross, price, total, yield, net, markup), out var problem);
        problem.Should().Be(CarcassYieldProblem.None);
        return r!;
    }

    private static CarcassYieldProblem Problem(decimal gross, decimal? price = 400, decimal? yield = 75, decimal? net = null)
    {
        CarcassYield.Calculate(new CarcassYieldInput(gross, price, null, yield, net), out var problem).Should().BeNull();
        return problem;
    }

    private static ReceivingLineVm Meat(double qty = 300, double price = 400, string unit = "кг") =>
        new() { ProductName = "Говядина", Unit = unit, Source = ReceivingSource.Unknown, Quantity = qty, PurchasePrice = price };

    // ───────────── расчёт ─────────────

    [Fact]
    public void MainExample_300kg_400som_75percent()
    {
        var r = Calc(300, price: 400, yield: 75);
        r.Net.Should().Be(225m);
        r.Waste.Should().Be(75m);
        r.WastePercent.Should().Be(25m);
        r.YieldPercent.Should().Be(75m);
        r.Total.Should().Be(120000m, "общая сумма закупки не меняется от выхода");
        r.PricePerUnitGross.Should().Be(400m);
        r.CostPerUnitNet.Should().Be(533.33m);
        r.UnitPrice.Should().Be(533.3333m);
        r.ServerLineTotal.Should().Be(119999.99m, "225 × 533,3333 до копеек");
        r.RoundingDelta.Should().Be(-0.01m);
        r.HasRoundingLoss.Should().BeFalse("копейка — не пометка: порог больше 0,01");
    }

    [Fact]
    public void NetTypedInsteadOfPercent_GivesTheSamePercent()
    {
        var r = Calc(300, price: 400, net: 225);
        r.Net.Should().Be(225m);
        r.YieldPercent.Should().Be(75m);
        r.Total.Should().Be(120000m);
    }

    [Fact]
    public void TotalTypedInsteadOfPrice_GivesPricePerKilogramOfGross()
    {
        var r = Calc(300, total: 120000, yield: 75);
        r.PricePerUnitGross.Should().Be(400m);
        r.CostPerUnitNet.Should().Be(533.33m);
        Calc(300, total: 100000, yield: 80).PricePerUnitGross.Should().Be(333.3333m);
    }

    [Fact]
    public void TotalWinsOverPriceAndNetWinsOverYield_WhenBothAreGiven()
    {
        var r = Calc(300, price: 999, total: 120000, yield: 10, net: 225);
        r.Total.Should().Be(120000m);
        r.Net.Should().Be(225m);
        r.YieldPercent.Should().Be(75m);
    }

    [Fact]
    public void Yield100_NoWaste_CostEqualsPrice()
    {
        var r = Calc(50, price: 400, yield: 100);
        r.Net.Should().Be(50m);
        r.Waste.Should().Be(0m);
        r.WastePercent.Should().Be(0m);
        r.CostPerUnitNet.Should().Be(400m);
        r.UnitPrice.Should().Be(400m);
        r.RoundingDelta.Should().Be(0m);
    }

    [Theory]
    [InlineData(0, CarcassYieldProblem.NoGross)]
    [InlineData(-5, CarcassYieldProblem.NoGross)]
    [InlineData(1.2345, CarcassYieldProblem.GrossPrecision)]
    [InlineData(10_000_000, CarcassYieldProblem.TooBig)]
    public void BadGross_IsRejected(double gross, CarcassYieldProblem expected) => Problem((decimal)gross).Should().Be(expected);

    [Theory]
    [InlineData(0)]
    [InlineData(-10)]
    [InlineData(100.01)]
    [InlineData(250)]
    public void YieldOutOfRange_IsRejected(double yield) => Problem(300, yield: (decimal)yield).Should().Be(CarcassYieldProblem.YieldRange);

    [Fact]
    public void NoYieldAndNoNet_IsRejected() => Problem(300, yield: null).Should().Be(CarcassYieldProblem.NoYield);

    [Theory]
    [InlineData(0, CarcassYieldProblem.NetRange)]
    [InlineData(300.001, CarcassYieldProblem.NetRange)]
    [InlineData(250.1234, CarcassYieldProblem.NetPrecision)]
    public void BadNet_IsRejected(double net, CarcassYieldProblem expected) => Problem(300, yield: null, net: (decimal)net).Should().Be(expected);

    [Fact]
    public void NetEqualToGross_IsAllowed() => Calc(300, price: 400, net: 300).Waste.Should().Be(0m);

    [Fact]
    public void TinyYield_ThatRoundsToZeroGrams_IsRejected()
    {
        Problem(0.001m, yield: 40).Should().Be(CarcassYieldProblem.NetRange); // 0,0004 кг — меньше грамма
        Calc(0.001m, price: 400, yield: 50).Net.Should().Be(0.001m, "0,0005 округляется до грамма вверх");
    }

    [Fact]
    public void NetFromYield_RoundsToGrams()
    {
        CarcassYield.NetFromYield(123.456m, 73.5m).Should().Be(90.740m); // 90,74016
        CarcassYield.NetFromYield(300, 75).Should().Be(225m);
        CarcassYield.NetFromYield(10, 33.3333m).Should().Be(3.333m);
        CarcassYield.YieldFromNet(300, 200).Should().Be(66.67m);
    }

    [Fact]
    public void NoPriceAndNoTotal_StillGivesNetWeight()
    {
        var r = Calc(300, yield: 75);
        r.HasTotal.Should().BeFalse();
        r.Net.Should().Be(225m);
        r.Total.Should().Be(0m);
        r.UnitPrice.Should().Be(0m);
    }

    [Fact]
    public void SalePriceHint_IsCostTimesMarkup_OnlyWhenMarkupIsGiven()
    {
        Calc(300, price: 400, yield: 75, markup: 30).SaleHint.Should().Be(693.33m); // 120000 / 225 × 1,3
        Calc(300, price: 400, yield: 75).SaleHint.Should().BeNull();
        Calc(300, yield: 75, markup: 30).SaleHint.Should().BeNull("суммы нет — считать нечем");
    }

    [Fact]
    public void ServerPrice_KeepsLineTotalCloseToThePurchaseTotal_ForUpTo100Kg()
    {
        foreach (var net in new[] { 7.5m, 33.333m, 50.5m, 99.999m, 100m })
            for (var total = 1000m; total < 600000m; total += 7777.77m)
            {
                var price = CarcassYield.PriceForTotal(net, total);
                price.Should().BeGreaterThan(0);
                Math.Abs(CarcassYield.LineTotal(net, price) - total).Should().BeLessOrEqualTo(0.01m, $"{net} кг, сумма {total}");
            }
    }

    [Fact]
    public void ServerPrice_ForHeavyLines_ShowsTheLossWhenItExceedsOneKopeck()
    {
        var r = Calc(1000, total: 100000.01m, yield: 99.999m);
        Math.Abs(r.RoundingDelta).Should().BeLessOrEqualTo(0.06m);
        r.HasRoundingLoss.Should().Be(Math.Abs(r.RoundingDelta) > 0.01m);
    }

    [Theory]
    [InlineData("12,5", 12.5)]
    [InlineData("12.5", 12.5)]
    [InlineData("1 234.5", 1234.5)]
    [InlineData("1 234,5", 1234.5)]
    [InlineData(" 75 ", 75)]
    [InlineData("0,001", 0.001)]
    public void TryParseNumber_AcceptsCommaDotAndSpaces(string text, double expected)
    {
        CarcassYield.TryParseNumber(text, out var v).Should().BeTrue();
        v.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(".")]
    [InlineData("-5")]
    [InlineData("1e3")]
    [InlineData("1,234,5")]
    [InlineData("abc")]
    [InlineData("4600000000019000000")]
    public void TryParseNumber_RejectsGarbage(string text) => CarcassYield.TryParseNumber(text, out _).Should().BeFalse();

    [Fact]
    public void FormatNumber_HasNoTrailingZeros_AndUsesComma()
    {
        CarcassYield.FormatNumber(225m, 3).Should().Be("225");
        CarcassYield.FormatNumber(533.3333m).Should().Be("533,3333");
        CarcassYield.FormatNumber(75.5m, 2).Should().Be("75,5");
    }

    // ───────────── строка приёмки ─────────────

    [Fact]
    public void Line_EnableThenYield75_WritesNetQuantityAndCostPerKilogram()
    {
        var line = Meat();
        line.CarcassAllowed.Should().BeTrue();
        line.SetCarcass(true);
        line.Quantity.Should().Be(300, "выход 100 % по умолчанию — ничего не меняется, пока выход не вписан");
        line.PurchasePrice.Should().Be(400);

        line.CarcassYieldText = "75";

        line.Quantity.Should().Be(225);
        line.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
        line.CarcassTotalText.Should().Be("120000");
        line.CarcassPriceText.Should().Be("400");
        line.CarcassNetText.Should().Be("225");
        line.LineTotal.Should().BeApproximately(120000, 0.011, "сумма строки сходится с суммой закупки с точностью до копейки");
        line.CarcassResultText.Should().Be("Куплено 300 кг → в продаже 225 кг (отходы 75 кг · 25 %) · себестоимость 533,33 сом/кг");
        line.CarcassPayText.Should().Be("К оплате поставщику: " + MoneyText.Som(120000m));
        line.CarcassLossText.Should().BeEmpty();
        line.CarcassNote.Should().Be("Брутто 300 кг, выход 75 %, отходы 75 кг");
    }

    [Fact]
    public void Line_NetTypedKeepsGrossAndTotal_YieldBecomesPercent()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassNetText = "225";
        line.Quantity.Should().Be(225);
        line.CarcassYieldText.Should().Be("75");
        line.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
    }

    [Fact]
    public void Line_TotalTyped_PriceBecomesPricePerKilogramOfGross()
    {
        var line = Meat(300, 0);
        line.SetCarcass(true);
        line.CarcassYieldText = "75";
        line.PurchasePrice.Should().Be(0, "суммы ещё нет");
        line.CarcassTotalText = "120000";
        line.CarcassPriceText.Should().Be("400");
        line.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
    }

    [Fact]
    public void Line_GrossChanged_KeepsYieldAndPrice()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassYieldText = "75";
        line.CarcassGrossText = "200";
        line.Quantity.Should().Be(150);
        line.CarcassTotalText.Should().Be("80000");
        line.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
    }

    [Fact]
    public void Line_ExternalQuantityEdit_RecomputesYield_AndKeepsTheTotal()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassYieldText = "75";

        line.Quantity = 200; // «Кол-во» поправили в основной ячейке (или «−»)

        line.CarcassYieldText.Should().Be("66,67");
        line.CarcassNetText.Should().Be("200");
        line.CarcassTotalText.Should().Be("120000");
        line.PurchasePrice.Should().BeApproximately(600, 1e-9, "сумма закупки та же, килограммов в продаже меньше — кг дороже");

        line.Quantity = 320; // больше купленного: купленное подтягивается, выход 100 %
        line.CarcassGrossText.Should().Be("320");
        line.CarcassYieldText.Should().Be("100");
    }

    [Fact]
    public void Line_ExternalPurchaseEdit_RecomputesTotalAndPricePerGross()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassYieldText = "75";

        line.PurchasePrice = 500; // себестоимость кг в продаже поправили в основной ячейке «Закупка»

        line.PurchasePrice.Should().Be(500, "введённую цену не переписываем");
        line.CarcassTotalText.Should().Be("112500");
        line.CarcassPriceText.Should().Be("375");
        line.Quantity.Should().Be(225);
    }

    [Fact]
    public void Line_InvalidYield_ZeroesQuantity_BlocksPosting_AndExplains()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassYieldText = "150";

        line.Quantity.Should().Be(0);
        line.CarcassProblem.Should().Be(CarcassYieldProblem.YieldRange);
        line.CarcassProblemText.Should().Contain("Выход — больше 0 и не больше 100 %");
        line.CarcassResultText.Should().BeEmpty();
        line.CarcassNote.Should().BeEmpty();
        PurchaseReceivingService.Validate([line], requireSalePrice: false).Should().Contain(p => p.Contains("Выход — больше 0"));

        line.CarcassYieldText = "75";
        line.CarcassProblem.Should().Be(CarcassYieldProblem.None);
        line.Quantity.Should().Be(225);
        PurchaseReceivingService.Validate([line], requireSalePrice: false).Should().NotContain(p => p.Contains("Выход"));
    }

    [Fact]
    public void Line_TurnOff_KeepsNetAndCost_AndDropsTheNote()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassYieldText = "75";
        line.SetCarcass(false);
        line.CarcassOn.Should().BeFalse();
        line.Quantity.Should().Be(225);
        line.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
        line.CarcassNote.Should().BeEmpty();
        line.Quantity = 100; // после выключения «Кол-во» снова обычное поле
        line.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
    }

    [Fact]
    public void Line_NotWeightOrInPackMode_OrModuleOff_DoesNotAllowCarcass()
    {
        Meat(10, 5, "шт").CarcassAllowed.Should().BeFalse("штучный товар");
        Meat(10, 5, "г").CarcassAllowed.Should().BeTrue("граммы тоже весовая единица");
        var pack = Meat();
        pack.SetPackMode(true, 12);
        pack.CarcassAllowed.Should().BeFalse("в режиме упаковок выхода нет");

        UserPreferences.Instance.CarcassYieldEnabled = false;
        var off = Meat();
        off.CarcassAllowed.Should().BeFalse("модуль не включён");
        off.SetCarcass(true);
        off.CarcassOn.Should().BeFalse();
    }

    [Fact]
    public void Line_RememberedYield_IsOfferedAtNextReceipt()
    {
        CarcassYieldMemory.Suggest("p-1", "2000000000011").Should().BeNull();
        CarcassYieldMemory.Remember("p-1", "2000000000011", 72.5m);
        CarcassYieldMemory.Suggest("p-1", null).Should().Be(72.5m);
        CarcassYieldMemory.Suggest(null, "2000000000011").Should().Be(72.5m);
        CarcassYieldMemory.Remember("p-1", null, 0m); // мусор не запоминается
        CarcassYieldMemory.Remember("p-1", null, 101m);
        CarcassYieldMemory.Suggest("p-1", null).Should().Be(72.5m);
        File.Exists(Path.Combine(_dir, "receiving-carcass-yield.json")).Should().BeTrue();

        CarcassYieldMemory.Reset(); // перезапуск: читается с диска
        CarcassYieldMemory.Suggest("p-1", null).Should().Be(72.5m);

        var next = Meat();
        next.ProductId = "p-1";
        next.SetCarcass(true);
        next.CarcassYieldText.Should().Be("72,5", "следующий приход предлагает прошлый процент");
        next.Quantity.Should().Be(217.5);
    }

    // ───────────── примечание прихода, черновик, очередь ─────────────

    [Fact]
    public void ReceiptComment_GetsTheCarcassNote_AfterTheDocumentNumber()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassYieldText = "75";

        var body = new Dictionary<string, object?> { ["comment"] = "№ ПР-2026-000123" };
        PurchaseReceivingService.AppendCarcassNotes(body, [line]);
        body["comment"].Should().Be("№ ПР-2026-000123; Брутто 300 кг, выход 75 %, отходы 75 кг");

        var empty = new Dictionary<string, object?>();
        PurchaseReceivingService.AppendCarcassNotes(empty, [line]);
        empty["comment"].Should().Be("Брутто 300 кг, выход 75 %, отходы 75 кг");

        var lamb = Meat(40, 500);
        lamb.ProductName = "Баранина";
        lamb.SetCarcass(true);
        lamb.CarcassYieldText = "60";
        var two = new Dictionary<string, object?>();
        PurchaseReceivingService.AppendCarcassNotes(two, [line, lamb]);
        two["comment"].Should().Be("Говядина: Брутто 300 кг, выход 75 %, отходы 75 кг; Баранина: Брутто 40 кг, выход 60 %, отходы 16 кг");

        var plain = new Dictionary<string, object?>();
        PurchaseReceivingService.AppendCarcassNotes(plain, [Meat()]);
        plain.Should().NotContainKey("comment", "строка без выхода примечания не добавляет");
    }

    [Fact]
    public void Draft_KeepsTheBlockState_AcrossRestart()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassNetText = "225";
        var json = JsonSerializer.Serialize(line.ToDraft());
        var restored = ReceivingLineVm.FromDraft(JsonSerializer.Deserialize<ReceivingDraftLine>(json)!);

        restored.CarcassOn.Should().BeTrue();
        restored.Quantity.Should().Be(225);
        restored.PurchasePrice.Should().BeApproximately(533.3333, 1e-9);
        restored.CarcassGrossText.Should().Be("300");
        restored.CarcassNetText.Should().Be("225");
        restored.CarcassYieldText.Should().Be("75");
        restored.CarcassTotalText.Should().Be("120000");
        restored.CarcassNote.Should().Be("Брутто 300 кг, выход 75 %, отходы 75 кг");

        // старый черновик без полей выхода читается как раньше
        var old = ReceivingLineVm.FromDraft(JsonSerializer.Deserialize<ReceivingDraftLine>(JsonSerializer.Serialize(Meat().ToDraft()))!);
        old.CarcassOn.Should().BeFalse();
        old.CarcassNote.Should().BeEmpty();
    }

    [Fact]
    public void JobLine_CarriesTheNote_ToTheBackgroundQueue()
    {
        var line = Meat();
        line.SetCarcass(true);
        line.CarcassYieldText = "75";
        var job = JobLine.From(line);
        job.CarcassNote.Should().Be("Брутто 300 кг, выход 75 %, отходы 75 кг");
        var back = JsonSerializer.Deserialize<JobLine>(JsonSerializer.Serialize(job))!.ToVm();
        back.CarcassNote.Should().Be("Брутто 300 кг, выход 75 %, отходы 75 кг");
        back.Quantity.Should().Be(225);
        PurchaseReceivingService.AppendCarcassNotes(new Dictionary<string, object?>(), [back]); // и из очереди уходит в comment
    }

    // ───────────── включение модуля ─────────────

    [Fact]
    public void Feature_ServerUnknown_LocalFlagDecides_ServerKnown_ServerDecides()
    {
        UserPreferences.Instance.CarcassYieldEnabled = false;
        CarcassYieldFeature.IsAvailable.Should().BeFalse();
        UserPreferences.Instance.CarcassYieldEnabled = true;
        CarcassYieldFeature.IsAvailable.Should().BeTrue("сервер модуль не знает — решает местный признак из Маркетплейса");

        using var off = JsonDocument.Parse("""{"profile":"grocery","version":"1","modules":[],"items":[{"id":"carcass_yield","enabled":false,"source":"off"}],"catalog":[],"profiles":[]}""");
        using var on = JsonDocument.Parse("""{"profile":"grocery","version":"2","modules":["carcass_yield"],"items":[{"id":"carcass_yield","enabled":true,"source":"added"}],"catalog":[],"profiles":[]}""");
        CompanyModules.Decide(CompanyModulesSnapshot.Parse(off.RootElement)!, ModuleIds.CarcassYield, false, true, true).Should().BeFalse("сервер знает модуль и говорит «выкл»");
        CompanyModules.Decide(CompanyModulesSnapshot.Parse(on.RootElement)!, ModuleIds.CarcassYield, false, false, true).Should().BeTrue();
    }

    [Fact]
    public void Flag_IsWrittenToUserSettings_AndSyncedAsCompanySetting()
    {
        var path = (string)typeof(UserPreferences).GetProperty("FilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        path.Should().Contain("nurmarket-tests", "файл настроек пишется только во временную папку test-safe — настоящий user-settings.json не трогаем");
        UserPreferences.Instance.CarcassYieldEnabled = true;
        UserPreferences.Instance.SaveToDisk();
        File.Exists(path).Should().BeTrue("в этом прогоне путь данных — временная папка (test-safe), настоящий файл не затрагивается");
        File.ReadAllText(path).Should().MatchRegex("(?i)\"CarcassYieldEnabled\"\\s*:\\s*true", "признак «Выход мяса» попадает в файл настроек");
        typeof(SettingsCloudSync).GetField("CompanyKeys", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)
            .Should().BeAssignableTo<string[]>().Which.Should().Contain("CarcassYieldEnabled", "включённый в Маркетплейсе модуль — общий для магазина, как «Смета» и «Прокат»");
    }

    // ───────────── 5 языков ─────────────

    [Fact]
    public void Texts_AreTranslatedInEveryLanguage()
    {
        var r = Calc(300, price: 400, yield: 75);
        var seen = new Dictionary<AppLanguage, string>();
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            UserPreferences.Instance.Language = lang;
            var all = string.Join("|", CarcassYieldTexts.ResultLine(r, "кг"), CarcassYieldTexts.PayLine(r), CarcassYieldTexts.LossLine(r), CarcassYieldTexts.Note(r, "кг"),
                string.Join("|", Enum.GetValues<CarcassYieldProblem>().Where(p => p != CarcassYieldProblem.None).Select(CarcassYieldTexts.ProblemText)));
            all.Should().NotContain("{").And.NotContain("}");
            seen[lang] = all;
            Enum.GetValues<CarcassYieldProblem>().Where(p => p != CarcassYieldProblem.None).Should().OnlyContain(p => CarcassYieldTexts.ProblemText(p).Length > 10, lang.ToString());
        }
        seen.Values.Distinct().Should().HaveCount(5, "тексты переведены, а не скопированы");
        UserPreferences.Instance.Language = AppLanguage.Russian;
    }
}
