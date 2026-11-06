using System.Reflection;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;
using NurMarketKassa.Tests.Basket;
using NurMarketKassa.Tests.Subscription;

namespace NurMarketKassa.Tests.Wave14;

/// <summary>2026-11-06: исправления по независимому аудиту Этапа 2 — В-1 (ревизия: штрихкод упаковки), С-1 (сверка смены по устаревшему списку), С-2 (прокат привязан к чеку),
/// Н-1 (подключение к папке весов), Н-2 (поздний ответ другой компании). Запускать только tools/qa/test-safe.ps1.</summary>
[Collection("Wave5Database")]
public sealed class AuditStage2FixesTests
{
    // ───────────── В-1: ревизия ─────────────

    [Fact]
    public void Revision_NoMainBarcode_FirstAlternateIsPack12_ScanGivesTwelve_MainBarcodeGivesOne()
    {
        var noMain = new CatalogProductTileVm("a1", "Вода без основного", "30,00 сом", false, null)
        {
            Quantity = 24,
            Barcode = "",
            AlternateBarcodesRaw = "4601111111119",
            AlternateBarcodeVariants = new List<AlternateBarcodeVariant> { new() { Barcode = "4601111111119", Quantity = 12 } },
        };
        var withMain = new CatalogProductTileVm("a2", "Сок", "50,00 сом", false, null)
        {
            Quantity = 10,
            Barcode = "4602222222226",
            AlternateBarcodesRaw = "4603333333333",
            AlternateBarcodeVariants = new List<AlternateBarcodeVariant> { new() { Barcode = "4603333333333", Quantity = 6 } },
        };
        var s = RevisionSession.Start(new[] { noMain, withMain }, new RevisionOptions { PersistDraft = false });

        var pack = s.FindByCode("4601111111119");
        pack.Should().NotBeNull();
        pack!.PackageQuantity.Should().Be(12, "первый дополнительный код — упаковка ×12; основного штрихкода нет, но это не «одна штука»");
        pack.Kind.Should().Be(RevisionMatchKind.AlternateBarcode);
        s.Rows.First(r => r.ProductId == "a1").Barcode.Should().Be("4601111111119", "в таблице и Excel по-прежнему показывается первый дополнительный");

        var main = s.FindByCode("4602222222226");
        main!.PackageQuantity.Should().Be(1);
        main.Kind.Should().Be(RevisionMatchKind.Barcode);
        s.FindByCode("4603333333333")!.PackageQuantity.Should().Be(6);
    }

    // ───────────── С-1: список открытых смен «из прошлого» ─────────────

    [Fact]
    public async Task PrefetchedOpenShifts_AreDiscarded_WhenShiftClosedAfterPrefetch()
    {
        var oldShift = PosApp.ActiveShiftId;
        try
        {
            StartupShiftTests.ShiftProxy.ListCalls = 0;
            PosApp.ActiveShiftId = "shift-x";
            var api = DispatchProxy.Create<IShiftApiService, StartupShiftTests.ShiftProxy>();
            var service = new ShiftStateService(api, DispatchProxy.Create<IAutonomousAuthService, StartupShiftTests.NotAutonomous>());

            ShiftStateService.PrefetchOpenShifts(api, CancellationToken.None); // запрос «из t0»: смена X ещё открыта
            PosApp.ActiveShiftId = null;                                       // кассир закрыл смену (Z) до сверки
            await service.RefreshAsync();

            StartupShiftTests.ShiftProxy.ListCalls.Should().Be(2, "список из t0 устарел (смена закрывалась) — сверка спросила сервер заново и не воскресила закрытую смену");
        }
        finally
        {
            PosApp.ActiveShiftId = oldShift;
        }
    }

    [Fact]
    public async Task PrefetchedOpenShifts_AreKept_WhenShiftDidNotChange()
    {
        var oldShift = PosApp.ActiveShiftId;
        try
        {
            StartupShiftTests.ShiftProxy.ListCalls = 0;
            PosApp.ActiveShiftId = "shift-y";
            var api = DispatchProxy.Create<IShiftApiService, StartupShiftTests.ShiftProxy>();
            var service = new ShiftStateService(api, DispatchProxy.Create<IAutonomousAuthService, StartupShiftTests.NotAutonomous>());

            ShiftStateService.PrefetchOpenShifts(api, CancellationToken.None);
            await service.RefreshAsync();

            StartupShiftTests.ShiftProxy.ListCalls.Should().Be(1, "смена не менялась — заранее запрошенный список берётся как раньше");
        }
        finally
        {
            PosApp.ActiveShiftId = oldShift;
        }
    }

    [Fact]
    public void ShiftEpoch_GrowsOnlyWhenShiftChanges()
    {
        var old = PosApp.ActiveShiftId;
        try
        {
            PosApp.ActiveShiftId = "e1";
            var e = PosApp.ShiftEpoch;
            PosApp.ActiveShiftId = "e1";
            PosApp.ShiftEpoch.Should().Be(e, "та же смена — эпоха та же");
            PosApp.ActiveShiftId = null;
            PosApp.ShiftEpoch.Should().Be(e + 1);
            PosApp.ActiveShiftId = "e2";
            PosApp.ShiftEpoch.Should().Be(e + 2);
        }
        finally
        {
            PosApp.ActiveShiftId = old;
        }
    }

    // ───────────── С-2: прокат привязан к чеку ─────────────

    private static RentalDto Rental(int number, params RentalItem[] items) => new("rent-" + number, number, "c1", "Клиент", "active", false,
        DateTime.Today, DateTime.Today.AddDays(2), "day", "money", 500, "cash", "", items, null, 0, "", null, null, 0, 0);

    private static async Task<bool> WaitAsync(Func<bool> condition, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }

    [Fact]
    public async Task Rental_IsBoundToItsReceipt_PaymentCancelInAnotherReceiptAsksNothing_ThenCancelRemovesLineFromThatReceipt()
    {
        using var h = new BasketVmHarness();
        h.Windows.RentalRequestOnFirst = true;
        h.AddProduct("p1", "Лыжи", 300);
        var cancelledIds = new List<string>();
        h.Vm.RentalFromCart = (_, _, _) => Task.FromResult<(RentalDto Rental, double Total)?>((Rental(31), 300));
        h.Vm.CancelUnpaidRental = id => { cancelledIds.Add(id); return Task.FromResult(true); };
        h.Prompts.Answer = false; // «Оставить»

        h.Vm.PayCommand.Execute(null);
        (await WaitAsync(() => h.Prompts.Questions.Count == 1)).Should().BeTrue();
        await Task.Delay(300);

        // Другой чек: обычная продажа, оплату отменили.
        h.Vm.CreateNewReceipt();
        h.AddProduct("p2", "Хлеб", 50);
        h.Prompts.Answer = true; // если бы про прокат спросили — кассир нажал бы «Отменить»
        h.Vm.PayCommand.Execute(null);
        (await WaitAsync(() => h.Windows.PaymentDialogs == 3)).Should().BeTrue();
        await Task.Delay(400);
        h.Prompts.Questions.Should().HaveCount(1, "прокат №31 оформлен в первом чеке — про него не спрашивают при отмене оплаты второго");
        cancelledIds.Should().BeEmpty();

        // Вернулись в первый чек (строка проката на месте) и снова отменили оплату — вот теперь вопрос и отмена.
        var firstTab = h.Vm.ReceiptTabs.First();
        h.Vm.SelectReceiptTabCommand.Execute(firstTab);
        h.Cart.Items.Should().ContainSingle(i => i.Name.StartsWith("Прокат №31"));
        h.Vm.PayCommand.Execute(null);
        (await WaitAsync(() => cancelledIds.Count == 1)).Should().BeTrue();
        cancelledIds.Should().Equal("rent-31");
        (await WaitAsync(() => h.Cart.Items.Count == 0)).Should().BeTrue("строка «Прокат №31» убрана из того же чека");
        h.Prompts.Errors.Should().ContainSingle(t => t.Contains("Прокат №31 отменён") && t.Contains("Верните клиенту залог"), "кассиру прямо сказано вернуть залог");
    }

    [Fact]
    public async Task Rental_AfterReceiptCleared_NoQuestionAtPaymentCancel()
    {
        using var h = new BasketVmHarness();
        h.Windows.RentalRequestOnFirst = true;
        h.AddProduct("p1", "Лыжи", 300);
        var cancelled = 0;
        h.Vm.RentalFromCart = (_, _, _) => Task.FromResult<(RentalDto Rental, double Total)?>((Rental(32), 300));
        h.Vm.CancelUnpaidRental = _ => { cancelled++; return Task.FromResult(true); };
        h.Prompts.Answer = false;

        h.Vm.PayCommand.Execute(null);
        (await WaitAsync(() => h.Prompts.Questions.Count == 1)).Should().BeTrue();
        await Task.Delay(300);

        h.Prompts.Answer = true;
        h.Vm.ClearCartCommand.Execute(null);   // «Очистить чек»
        (await WaitAsync(() => h.Cart.Items.Count == 0)).Should().BeTrue();
        var asked = h.Prompts.Questions.Count;
        h.AddProduct("p2", "Хлеб", 50);
        h.Vm.PayCommand.Execute(null);
        (await WaitAsync(() => h.Windows.PaymentDialogs == 3)).Should().BeTrue();
        await Task.Delay(400);

        h.Prompts.Questions.Should().HaveCount(asked, "чек очищен — про прокат №32 больше не спрашивают");
        cancelled.Should().Be(0);
    }

    [Fact]
    public async Task RentalCancel_ReturnsItemsToVariantCache_AndNamesDeposit()
    {
        var oldLoader = ProductVariantCache.Loader;
        try
        {
            ProductVariantCache.Loader = null;
            ProductVariantCache.Put("pv1", new List<ProductVariantDto> { new() { Id = "v1", Size = "42", Quantity = 2 } });
            using var h = new BasketVmHarness();
            h.Windows.RentalRequestOnFirst = true;
            h.AddProduct("pv1", "Лыжи", 300);
            h.Vm.RentalFromCart = (_, _, _) => Task.FromResult<(RentalDto Rental, double Total)?>((Rental(33, new RentalItem("pv1", "v1", "Лыжи", "42", "", 1)), 300));
            h.Vm.CancelUnpaidRental = _ => Task.FromResult(true);
            h.Prompts.Answer = true;

            h.Vm.PayCommand.Execute(null);

            (await WaitAsync(() => h.Prompts.Errors.Count == 1)).Should().BeTrue();
            h.Prompts.Errors[0].Should().Contain("500").And.Contain("сом", "залог назван суммой");
            var variants = await ProductVariantCache.GetAsync("pv1", TimeSpan.FromSeconds(1));
            variants!.Single().Quantity.Should().Be(3, "вещь вернулась на склад — остаток размера вырос сразу");
        }
        finally
        {
            ProductVariantCache.Loader = oldLoader;
            ProductVariantCache.Clear();
        }
    }

    // ───────────── Н-1: подключение к папке весов ─────────────

    private sealed class FakeConnector : IShtrikhPcShareConnector
    {
        public List<string> Calls { get; } = new();
        public int Delay { get; set; }

        public int Connect(string share, string user, string password)
        {
            lock (Calls) Calls.Add("connect");
            if (Delay > 0)
                Thread.Sleep(Delay);
            return 0;
        }

        public int Disconnect(string share)
        {
            lock (Calls) Calls.Add("disconnect");
            return 0;
        }
    }

    [Fact]
    public async Task ShareConnectedAfterTimeout_IsDisconnected_NotLeftHanging()
    {
        var saved = ShtrikhPcShare.Connector;
        var fake = new FakeConnector { Delay = (int)ShtrikhPcImportWriter.AvailabilityTimeout.TotalMilliseconds + 800 };
        ShtrikhPcShare.Connector = fake;
        try
        {
            var settings = new ShtrikhPcSettings { Folder = @"\\192.168.0.200\Shared", Login = "scale", Password = "p" }.Normalize();
            var (status, _, lease) = await ShtrikhPcShare.OpenAsync(settings);
            status.Should().Be(ShtrikhPcNetStatus.NoConnection);
            lease.Should().BeNull();

            (await WaitAsync(() => { lock (fake.Calls) return fake.Calls.Contains("disconnect"); }, 6000)).Should().BeTrue("подключение, удавшееся позже 3 с, отключается — иначе висело бы до выхода из Windows");
        }
        finally
        {
            ShtrikhPcShare.Connector = saved;
        }
    }

    // ───────────── Н-2: поздний ответ другой компании ─────────────

    [Fact]
    public void BackgroundCompany_OfAnotherCompany_IsNotApplied_AndCashierIsWarned()
    {
        var prefs = UserPreferences.Instance;
        var oldKey = prefs.LastDataAccountKey;
        var before = CompanyInfoService.LastCompany;
        string? warnedFor = null;
        Action<string?> handler = id => warnedFor = id;
        CompanyInfoService.OtherCompanyDetected += handler;
        try
        {
            prefs.LastDataAccountKey = AppPaths.CompanyKey("111");
            var other = new CompanyDto { Id = "222", Name = "Другая" };

            CompanyInfoService.ApplyBackgroundCompany(other, null!);

            warnedFor.Should().Be("222", "данные на ходу не меняем, но кассир узнаёт, что компания другая");
            CompanyInfoService.LastCompany.Should().BeSameAs(before, "чужая компания не применена");
        }
        finally
        {
            CompanyInfoService.OtherCompanyDetected -= handler;
            prefs.LastDataAccountKey = oldKey;
        }
    }
}

/// <summary>2026-11-06 (аудит Этапа 2, С-3): выбор марки «Штрих РС-200» не меняет разбор этикеток без подтверждения кассира.</summary>
[Collection("UserPreferences")]
public sealed class ShtrikhPcPresetConfirmTests : IDisposable
{
    private readonly ShtrikhPcSettings _settings = ShtrikhPcSettings.Current;
    private readonly bool _enabled = ShtrikhPcSettings.Current.BarcodeEnabled;
    private readonly int _prefix = ShtrikhPcSettings.Current.BarcodePrefix;
    private readonly int _length = ShtrikhPcSettings.Current.CodeLength;
    private readonly string _format = ShtrikhPcSettings.Current.BarcodeFormat;
    private readonly string _amount = UserPreferences.Instance.ScaleAmountPrefixes ?? "";
    private readonly string _weight = UserPreferences.Instance.ScaleWeightPrefixes ?? "";
    private readonly IReadOnlySet<string> _amountSet = WeightBarcodeParser.AmountPrefixes;
    private readonly IReadOnlySet<string> _weightSet = WeightBarcodeParser.WeightPrefixes;
    private readonly IReadOnlyList<WeightBarcodeCustomLayout> _custom = WeightBarcodeParser.CustomLayouts;

    public void Dispose()
    {
        _settings.BarcodeEnabled = _enabled;
        _settings.BarcodePrefix = _prefix;
        _settings.CodeLength = _length;
        _settings.BarcodeFormat = _format;
        ShtrikhPcSettings.Save(_settings);
        UserPreferences.Instance.ScaleAmountPrefixes = _amount;
        UserPreferences.Instance.ScaleWeightPrefixes = _weight;
        WeightBarcodeParser.AmountPrefixes = _amountSet;
        WeightBarcodeParser.WeightPrefixes = _weightSet;
        WeightBarcodeParser.CustomLayouts = _custom;
    }

    private static void Prepare()
    {
        var s = ShtrikhPcSettings.Current;
        s.BarcodeEnabled = false;
        s.BarcodePrefix = 27;
        s.CodeLength = 5;
        s.BarcodeFormat = ShtrikhPcSettings.FormatCodeWeight;
        ShtrikhPcSettings.Save(s);
        // На кассе уже есть явное правило других весов: префикс 27 — «сумма».
        ScaleBarcodeRules.Apply("27", NurMarketKassa.Core.Domain.WeightBarcodeValueKind.Amount);
    }

    [Fact]
    public async Task Declined_NothingChanges_OldRuleOfPrefixStays()
    {
        Prepare();
        string? asked = null;

        var applied = await ScaleBarcodeRules.OfferShtrikhPcPresetAsync(q => { asked = q; return Task.FromResult(false); });

        applied.Should().BeFalse();
        asked.Should().Contain("27").And.Contain("5", "вопрос называет префикс и число знаков").And.Contain("заменено", "и что прежнее правило префикса будет заменено");
        ShtrikhPcSettings.Current.BarcodeEnabled.Should().BeFalse("раскладка не включена");
        WeightBarcodeParser.AmountPrefixes.Should().Contain("27", "прежнее правило «сумма» для префикса 27 не тронуто");
        WeightBarcodeParser.WeightPrefixes.Should().NotContain("27");
    }

    [Fact]
    public async Task Accepted_LayoutEnabled_PrefixRuleReplaced()
    {
        Prepare();

        var applied = await ScaleBarcodeRules.OfferShtrikhPcPresetAsync(_ => Task.FromResult(true));

        applied.Should().BeTrue();
        ShtrikhPcSettings.Current.BarcodeEnabled.Should().BeTrue();
        WeightBarcodeParser.WeightPrefixes.Should().Contain("27");
    }

    [Fact]
    public async Task AlreadyEnabled_NoQuestion()
    {
        Prepare();
        var s = ShtrikhPcSettings.Current;
        s.BarcodeEnabled = true;
        ShtrikhPcSettings.Save(s);
        var asked = false;

        var applied = await ScaleBarcodeRules.OfferShtrikhPcPresetAsync(_ => { asked = true; return Task.FromResult(true); });

        applied.Should().BeFalse();
        asked.Should().BeFalse();
    }
}
