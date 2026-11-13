using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13: окна «Ремонт и сервис» и «Trade-in / скупка» в Avalonia.Headless на заглушках (касса, принтер, склад, ссылка WhatsApp; файлы — во временной папке, сервер не нужен).
/// Проверяется: приём и статусы, деньги идут только через кассу (внесение предоплаты, строка оплаты в чек, изъятие, зачёт), выдача без оплаты запрещена, текст не обрезан на 1024×600 и 1366×768 (ru, ky).
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\servicedesk-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ServiceDeskUiTests : IDisposable
{
    private const string ValidImei = "490154203237518";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "servicedesk-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public ServiceDeskUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private sealed class FakeTill : IServiceDeskTill
    {
        public decimal CartTotal { get; set; }
        public bool CartHasItems => CartTotal > 0;
        public bool AllowCash { get; set; } = true;
        public List<(bool Withdrawal, decimal Amount, string Reason)> Cash { get; } = new();
        public List<(string Name, decimal Amount, bool Credit)> Lines { get; } = new();
        public bool AddLine(string name, decimal amount, bool credit) { Lines.Add((name, amount, credit)); return true; }
        public Task<bool> CashAsync(bool withdrawal, decimal amount, string reason)
        {
            if (!AllowCash)
                return Task.FromResult(false);
            Cash.Add((withdrawal, amount, reason));
            return Task.FromResult(true);
        }
    }

    private (RepairOrdersDeps Deps, FakeTill Till, List<string> Printed, List<string> Urls) RepairDeps(FakeTill? till = null)
    {
        till ??= new FakeTill();
        var printed = new List<string>();
        var urls = new List<string>();
        var deps = new RepairOrdersDeps
        {
            Store = new RepairOrderStore(Path.Combine(_dir, "repair-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 13, 10, 0, 0)),
            Till = till,
            Print = text => { printed.Add(text); return Task.FromResult<string?>(null); },
            OpenUrl = url => { urls.Add(url ?? ""); return true; },
            Confirm = (_, _) => Task.FromResult(true),
            Cashier = () => "Тест",
            Today = () => new DateTime(2026, 11, 13),
        };
        return (deps, till, printed, urls);
    }

    private static Dictionary<string, string> RepairValues(string prepay = "") => new()
    {
        ["name"] = "Айгуль Сыдыкова", ["phone"] = "+996 700 123 456", ["device"] = "Samsung Galaxy A54", ["serial"] = ValidImei,
        ["fault"] = "Не включается после падения", ["equipment"] = "Зарядка, чехол", ["estimate"] = "1500", ["prepay"] = prepay, ["days"] = "3",
    };

    public static IEnumerable<object[]> Cases() =>
        new (int W, int H)[] { (1024, 600), (1366, 768) }
            .SelectMany(s => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz }.Select(l => new object[] { s.W, s.H, l }));

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }

    private static List<Button> Buttons(Window w) => w.GetVisualDescendants().OfType<Button>().ToList();

    private static Button Named(Window w, string name) => Buttons(w).Single(b => b.Name == name);

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Repair_FullFlow_MoneyOnlyThroughTill_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (deps, till, printed, urls) = RepairDeps();
        var window = new RepairOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var tag = $"{width}x{height} {lang}";

            // Приём с предоплатой: квитанция записана, предоплата внесена в кассу, квитанция напечатана
            var order = await window.AcceptAsync(RepairValues(prepay: "500"));
            UiShot.Settle(4);
            order.Should().NotBeNull();
            order!.Prepay.Should().Be(500m, tag);
            till.Cash.Should().ContainSingle(c => !c.Withdrawal && c.Amount == 500m && (c.Reason.Contains("Р-0001") || c.Reason.Contains("R-0001")), "предоплата — внесение в кассу со ссылкой на номер");
            printed.Should().ContainSingle().Which.Should().Contain("Р-0001").And.Contain(ValidImei);

            // Статусы
            window.StartWork(order.Id);
            window.MarkReady(order.Id, 1700m);
            UiShot.Settle(4);
            deps.Store.Find(order.Id)!.Status.Should().Be(RepairStatus.Ready);
            Named(window, "RepairPay").Should().NotBeNull("доплата 1200 — кнопка оплаты в чеке");

            // Выдача без оплаты запрещена
            (await window.IssueAsync(order.Id)).Should().BeFalse("доплата ещё не проведена");
            deps.Store.Find(order.Id)!.Status.Should().Be(RepairStatus.Ready);

            // Оплата — строка «Доп. услуга» в чеке на «итог минус предоплата»
            await window.AddPaymentToCartAsync(order.Id);
            till.Lines.Should().ContainSingle(l => l.Amount == 1200m && !l.Credit);
            UiShot.Settle(4);
            Named(window, "RepairIssue").Should().NotBeNull();

            // Клиенту «готово» — ссылка WhatsApp с готовым текстом
            window.Notify(order.Id).Should().BeTrue();
            urls.Should().ContainSingle().Which.Should().StartWith("https://wa.me/996700123456?text=");

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            TextClipChecker.Find(window, "repair-orders", langCode).Should().BeEmpty($"текст не обрезан ({tag})");
            Save(window, $"servicedesk-repair-{langCode}-{width}x{height}.png");

            // Форма «Новый приём»: помещается на экране, текст не обрезан
            var form = window.BuildNewForm();
            using (UiShot.Render(form, new UiSize(width, height)))
            {
                UiShot.Settle(4);
                TextClipChecker.Find(form, "repair-form", langCode).Should().BeEmpty($"форма приёма не обрезана ({tag})");
                form.Bounds.Height.Should().BeLessThanOrEqualTo(height, tag);
                Save(form, $"servicedesk-repair-form-{langCode}-{width}x{height}.png");
            }
            form.Close();

            // Выдача после «чек оплачен»: статус «Выдан», акт напечатан
            (await window.IssueAsync(order.Id)).Should().BeTrue();
            var issued = deps.Store.Find(order.Id)!;
            issued.Status.Should().Be(RepairStatus.Issued);
            issued.PaidAtIssue.Should().Be(1200m);
            printed.Should().HaveCount(2);
            printed[1].Should().Contain("Р-0001");
            till.Cash.Should().HaveCount(1, "кроме предоплаты, из кассы денег не двигалось");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Repair_PrepayNotAcceptedByTill_IsNotRecorded_AndCancelRefundsFromTill()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var till = new FakeTill { AllowCash = false };
        var (deps, _, _, _) = RepairDeps(till);
        var window = new RepairOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var order = await window.AcceptAsync(RepairValues(prepay: "500"));
            order!.Prepay.Should().Be(0m, "касса предоплату не приняла (смена закрыта) — в квитанции её нет");
            window.StatusMessage.Should().Contain("НЕ внесена");

            till.AllowCash = true;
            var second = await window.AcceptAsync(RepairValues(prepay: "400"));
            second!.Prepay.Should().Be(400m);
            (await window.CancelAsync(second.Id)).Should().BeTrue();
            till.Cash.Should().Contain(c => c.Withdrawal && c.Amount == 400m, "отмена возвращает предоплату изъятием из кассы");
            deps.Store.Find(second.Id)!.Status.Should().Be(RepairStatus.Cancelled);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Repair_OwnerProgram_HasNoTill_PrepayFieldDisabled_AndMoneyActionsRefused()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, _, _, _) = RepairDeps();
        var ownerDeps = new RepairOrdersDeps { Store = deps.Store, Till = null, Print = deps.Print, OpenUrl = deps.OpenUrl, Confirm = deps.Confirm, Cashier = deps.Cashier, Today = deps.Today };
        var window = new RepairOrdersWindow(ownerDeps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var form = window.BuildNewForm();
            form.Boxes["prepay"].IsEnabled.Should().BeFalse("в программе владельца кассы нет — предоплату принимает касса");
            var order = await window.AcceptAsync(RepairValues(prepay: "300"));
            order!.Prepay.Should().Be(0m);
            window.MarkReady(order.Id, 900m);
            await window.AddPaymentToCartAsync(order.Id);
            window.StatusMessage.Should().Contain("только в кассе");
            (await window.IssueAsync(order.Id)).Should().BeFalse("оплаты в чеке нет");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Repair_NewForm_ShowsErrorsForEmptyFields_AndBadImei()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, _, _, _) = RepairDeps();
        var window = new RepairOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var form = window.BuildNewForm();
            using var __ = UiShot.Render(form, new UiSize(1366, 768));
            form.Confirm();
            form.ErrorText.IsVisible.Should().BeTrue();
            form.ErrorText.Text.Should().Contain("имя клиента").And.Contain("телефон").And.Contain("неисправность");
            form.Set("serial", "490154203237519");
            form.Confirm();
            form.ErrorText.Text.Should().Contain("IMEI");
            form.Values.Should().BeEmpty("форма с ошибками не закрывается значениями");
            form.Close();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    private (TradeInDeps Deps, FakeTill Till, List<ReceivingLineVm> Stocked, List<string> Printed) TradeDeps(FakeTill? till = null, bool stockOk = true)
    {
        till ??= new FakeTill();
        var stocked = new List<ReceivingLineVm>();
        var printed = new List<string>();
        var deps = new TradeInDeps
        {
            Store = new TradeInStore(Path.Combine(_dir, "trade-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 13, 10, 0, 0)),
            Till = till,
            Print = text => { printed.Add(text); return Task.FromResult<string?>(null); },
            Confirm = (_, _) => Task.FromResult(true),
            Cashier = () => "Тест",
            Stock = line => { stocked.Add(line); return Task.FromResult(stockOk ? (true, "") : (false, "нет связи")); },
        };
        return (deps, till, stocked, printed);
    }

    private static Dictionary<string, string> TradeValues(string amount = "10000") => new()
    {
        ["name"] = "Бакыт Осмонов", ["phone"] = "0700 123 456", ["document"] = "ID 1234567, МКК 1234, 01.01.2020", ["device"] = "iPhone 11 64GB",
        ["serial"] = ValidImei, ["condition"] = "Царапины на корпусе, работает", ["equipment"] = "Зарядка", ["appraisal"] = "10000", ["amount"] = amount,
    };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task TradeIn_CreditAndPayout_MoneyOnlyThroughTill_StockedAsUsed_AndFitsScreen(int width, int height, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (deps, till, stocked, printed) = TradeDeps(new FakeTill { CartTotal = 7000m });
        var window = new TradeInWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(width, height));
            UiShot.Settle(6);
            var tag = $"{width}x{height} {lang}";

            var rec = await window.CreateAsync(TradeValues());
            UiShot.Settle(4);
            rec.Should().NotBeNull();
            printed.Should().ContainSingle().Which.Should().Contain("Т-0001").And.Contain("ID 1234567");
            till.Cash.Should().BeEmpty("создание выкупа денег не двигает");

            // Зачёт в чек на 7000 (итог чека), отрицательная строка
            (await window.AddCreditAsync(rec!.Id)).Should().BeTrue();
            till.Lines.Should().ContainSingle(l => l.Credit && l.Amount == 7000m);
            deps.Store.Find(rec.Id)!.CreditAdded.Should().Be(7000m);
            UiShot.Settle(4);
            Named(window, "TradeCreditConfirm").Should().NotBeNull();
            Buttons(window).Should().NotContain(b => b.Name == "TradePayout", $"пока зачёт не подтверждён, выплата скрыта ({tag})");

            // Чек оплачен — зачёт состоялся; остаток 3000 — выплата из кассы изъятием
            (await window.ConfirmCreditAsync(rec.Id)).Should().BeTrue();
            UiShot.Settle(4);
            (await window.PayoutAsync(rec.Id)).Should().BeTrue();
            till.Cash.Should().ContainSingle(c => c.Withdrawal && c.Amount == 3000m && c.Reason.Contains("Т-0001"));
            var bought = deps.Store.Find(rec.Id)!;
            bought.Status.Should().Be(TradeInStatus.Bought);
            UiShot.Settle(4);

            var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
            TextClipChecker.Find(window, "trade-in", langCode).Should().BeEmpty($"текст не обрезан ({tag})");
            Save(window, $"servicedesk-tradein-{langCode}-{width}x{height}.png");

            var form = window.BuildNewForm();
            using (UiShot.Render(form, new UiSize(width, height)))
            {
                UiShot.Settle(4);
                TextClipChecker.Find(form, "tradein-form", langCode).Should().BeEmpty($"форма выкупа не обрезана ({tag})");
                form.Bounds.Height.Should().BeLessThanOrEqualTo(height, tag);
                Save(form, $"servicedesk-tradein-form-{langCode}-{width}x{height}.png");
            }
            form.Close();

            // Приход на склад как б/у: закупка = выкуп
            (await window.StockAsync(rec.Id, 12000m)).Should().BeTrue();
            stocked.Should().ContainSingle();
            stocked[0].PurchasePrice.Should().Be(10000);
            stocked[0].SalePrice.Should().Be(12000);
            deps.Store.Find(rec.Id)!.Status.Should().Be(TradeInStatus.Stocked);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task TradeIn_EmptyCart_CannotCredit_FailedPayoutKeepsRecord_FailedStockKeepsBought()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var till = new FakeTill { CartTotal = 0m };
        var (deps, _, stocked, _) = TradeDeps(till, stockOk: false);
        var window = new TradeInWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var rec = await window.CreateAsync(TradeValues("5000"));
            (await window.AddCreditAsync(rec!.Id)).Should().BeFalse("в чеке нет суммы");
            window.StatusMessage.Should().Contain("добавьте в чек");
            deps.Store.Find(rec.Id)!.CreditAdded.Should().Be(0m);

            till.AllowCash = false;
            (await window.PayoutAsync(rec.Id)).Should().BeFalse();
            deps.Store.Find(rec.Id)!.PaidOut.Should().Be(0m, "касса отказала — выплата не записана");

            till.AllowCash = true;
            (await window.PayoutAsync(rec.Id)).Should().BeTrue();
            (await window.StockAsync(rec.Id, 8000m)).Should().BeFalse("склад не принял");
            deps.Store.Find(rec.Id)!.Status.Should().Be(TradeInStatus.Bought, "при ошибке склада выкуп остаётся «Выкуплен» — можно повторить");
            window.StatusMessage.Should().Contain("нет связи");
            stocked.Should().HaveCount(1);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task TradeIn_NewForm_RequiresDocumentAndImei()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (deps, _, _, _) = TradeDeps();
        var window = new TradeInWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            var form = window.BuildNewForm();
            using var __ = UiShot.Render(form, new UiSize(1366, 768));
            form.Confirm();
            form.ErrorText.Text.Should().Contain("паспорта").And.Contain("IMEI").And.Contain("сумму выкупа");
            form.Close();
            (await window.CreateAsync(new Dictionary<string, string>())).Should().BeNull("пустая форма не создаёт выкуп");
            deps.Store.All().Should().BeEmpty();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
