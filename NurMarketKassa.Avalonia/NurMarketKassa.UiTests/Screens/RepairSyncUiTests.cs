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
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (Умар, этап 2 «ремонт: синхронизация»): окно «Ремонт» и диалог конфликта версий в Avalonia.Headless на заглушках (сервер подменён, файлы — во временной папке).
/// Проверяется: номер от сервера попадает на печать и в запись предоплаты, временный номер без связи и итоговый после; чужие (серверные) квитанции и смена статуса на лету; плашка и диалог
/// «Квитанцию изменили на другом устройстве» помещаются на 1024×600 и 1366×768 (ru, ky, uz): ни одно поле и подпись не выходят за окно, не налезают друг на друга, текст не обрезан, кнопки целиком видны.
/// Снимки для глаз: %TEMP%\nurmarket-tests\ui-actual\repairsync-*.png.</summary>
[Collection("UiScenarios")]
public sealed class RepairSyncUiTests : IDisposable
{
    private const string ValidImei = "490154203237518";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "repairsync-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public RepairSyncUiTests()
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
        public decimal CartTotal => 0m;
        public bool CartHasItems => false;
        public List<string> Reasons { get; } = new();
        public bool AddLine(string name, decimal amount, bool credit) => true;
        public Task<bool> CashAsync(bool withdrawal, decimal amount, string reason) { Reasons.Add(reason); return Task.FromResult(true); }
    }

    private static string Code(AppLanguage l) => l == AppLanguage.Kyrgyz ? "ky" : l == AppLanguage.Uzbek ? "uz" : "ru";

    private RepairOrderStore NewStore() => new(Path.Combine(_dir, "repair-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 26, 10, 0, 0));

    private static RepairOrdersDeps Deps(RepairOrderStore store, FakeTill till, List<string> printed, Func<RepairOrderStore, RepairOrder, Task<string?>>? statusUrl = null, Func<RepairOrderStore, Task<int>>? pull = null) => new()
    {
        Store = store, Till = till, Print = t => { printed.Add(t); return Task.FromResult<string?>(null); }, OpenUrl = _ => true,
        Confirm = (_, _) => Task.FromResult(true), Cashier = () => "Тест", Today = () => new DateTime(2026, 11, 26),
        StatusUrl = statusUrl ?? ((_, _) => Task.FromResult<string?>(null)),
        Pull = pull ?? (_ => Task.FromResult(0)),
    };

    private static Dictionary<string, string> Values(string prepay = "") => new()
    {
        ["name"] = "Айгуль Сыдыкова", ["phone"] = "+996 700 123 456", ["device"] = "Samsung Galaxy A54", ["serial"] = ValidImei,
        ["fault"] = "Не включается после падения", ["equipment"] = "Зарядка, чехол", ["estimate"] = "1500", ["prepay"] = prepay, ["days"] = "3",
    };

    private static List<Button> Buttons(Window w) => w.GetVisualDescendants().OfType<Button>().ToList();

    private static bool HasText(Window w, string part) => w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(part, StringComparison.Ordinal) == true);

    private static string ConflictJson => "{\"id\":\"b1\",\"status\":\"in_work\",\"device\":\"Samsung Galaxy A54 (замена экрана)\",\"final_price\":\"1700.50\",\"problem\":\"Не включается после падения и не заряжается\",\"updated_at\":\"2026-11-26T10:05:00Z\"}";

    public static IEnumerable<object[]> Cases()
    {
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
        foreach (var size in new[] { new UiSize(1024, 600), new UiSize(1366, 768) })
            yield return [lang, size.Width, size.Height];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Conflict_BannerAndDialog_Fit_NoOverlap_NoClipping(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var store = NewStore();
        var till = new FakeTill();
        var window = new RepairOrdersWindow(Deps(store, till, new List<string>()));
        var screen = new UiSize(width, height);
        var tag = $"{Code(lang)} {screen}";
        try
        {
            // Окно открывается раньше, чем появятся конфликты: стартовое чтение с сервера не должно само открыть диалог посреди проверки.
            using var _ = UiShot.Render(window, screen);
            UiShot.Settle(8);
            var order = store.Create(new RepairOrder { ClientName = "Айгуль Сыдыкова", ClientPhone = "+996 700 123 456", Device = "Samsung Galaxy A54", Fault = "Не включается после падения", Estimate = 1500m }, "Тест", serverNumber: true);
            store.MarkConflict(order.Id, ConflictJson);
            var temp = store.Create(new RepairOrder { ClientName = "Нурлан", ClientPhone = "0555123456", Device = "Часы Casio", Fault = "Не идут" }, "Тест", serverNumber: true);
            store.MergeServerRows(new[] { RepairServerRow.Parse(System.Text.Json.JsonDocument.Parse("{\"id\":\"s1\",\"external_id\":null,\"code\":\"Р-0010\",\"status\":\"ready\",\"device\":\"iPhone 13\",\"customer_name\":\"Эльнура\",\"customer_phone\":\"0700111222\",\"final_price\":\"2300.00\",\"updated_at\":\"2026-11-26T10:02:00Z\"}").RootElement)! }, null);

            window.Reload();
            UiShot.Settle(6);
            window.Select(order.Id);
            UiShot.Settle(6);
            Buttons(window).Should().Contain(b => b.Name == "RepairKeepMine", tag);
            Buttons(window).Should().Contain(b => b.Name == "RepairTakeServer", tag);
            window.GetVisualDescendants().OfType<Control>().Should().Contain(c => c.Name == "RepairConflictBanner", tag);
            ServiceFormsFitUiTests.Save(window, $"repairsync-banner-{Code(lang)}-{screen}.png");
            var problems = ServiceFormsFitUiTests.FitProblems(window, screen, formButtons: false);
            problems.RemoveAll(p => p.StartsWith("окно ", StringComparison.Ordinal)); // размер главного окна по экрану подгоняет FitToKassaScreen, в Headless его нет
            problems.AddRange(FormFieldsOverlapUiTests.FindViolations(window, "banner " + tag).Select(v => v.Detail));
            problems.AddRange(TextClipChecker.Find(window, "repair-conflict-banner", Code(lang)).Select(v => v.Detail));
            problems.Should().BeEmpty("плашка конфликта " + tag + "\n  " + string.Join("\n  ", problems));

            // временный номер и квитанция «с сервера»
            window.Select(temp.Id);
            UiShot.Settle(4);
            HasText(window, Tr.T("Номер временный", "Номер убактылуу", "Temporary number", "Geçici numara", "Vaqtincha raqam")).Should().BeTrue(tag);
            window.Select("srv-s1");
            UiShot.Settle(4);
            // 2026-11-27 (ответ Умара 27.11, п. 3): серверная квитанция больше не «только просмотр» — «готов» с итогом 2300 даёт оплату работ, отмену, возврат в работу и предоплату (сервером)
            Buttons(window).Should().Contain(b => b.Name == "RepairPay" || b.Name == "RepairIssue", "серверную квитанцию касса меняет обычными адресами сервера: " + tag);
            Buttons(window).Should().Contain(b => b.Name == "RepairCancel", tag);
            Buttons(window).Should().Contain(b => b.Name == "RepairServerPrepay", tag);
            var serverCardProblems = ServiceFormsFitUiTests.FitProblems(window, screen, formButtons: false);
            serverCardProblems.RemoveAll(p => p.StartsWith("окно ", StringComparison.Ordinal));
            serverCardProblems.AddRange(FormFieldsOverlapUiTests.FindViolations(window, "server-card " + tag).Select(v => v.Detail));
            serverCardProblems.Should().BeEmpty("карточка серверной квитанции с кнопками " + tag + "\n  " + string.Join("\n  ", serverCardProblems));
            Buttons(window).Should().Contain(b => b.Name == "RepairPrintReceipt", tag);
            ServiceFormsFitUiTests.Save(window, $"repairsync-server-{Code(lang)}-{screen}.png");

            // диалог конфликта
            var dlg = new RepairConflictDialog(store.Find(order.Id)!);
            try
            {
                ServiceFormsFitUiTests.ShowLikeInApp(dlg, screen);
                ServiceFormsFitUiTests.Save(dlg, $"repairsync-dialog-{Code(lang)}-{screen}.png");
                var dp = ServiceFormsFitUiTests.FitProblems(dlg, screen, formButtons: false);
                dp.AddRange(FormFieldsOverlapUiTests.FindViolations(dlg, "dialog " + tag).Select(v => v.Detail));
                dp.AddRange(TextClipChecker.Find(dlg, "repair-conflict-dialog", Code(lang)).Select(v => v.Detail));
                foreach (var name in new[] { "RepairConflictLater", "RepairConflictServer", "RepairConflictMine" })
                {
                    var b = Buttons(dlg).FirstOrDefault(x => x.Name == name);
                    if (b is null || b.Bounds.Width <= 0)
                    {
                        dp.Add($"кнопка {name} не видна");
                        continue;
                    }
                    var pt = b.TranslatePoint(default, dlg);
                    if (pt is { } p && (p.X < -2 || p.X + b.Bounds.Width > dlg.ClientSize.Width + 2 || p.Y + b.Bounds.Height > dlg.ClientSize.Height + 2))
                        dp.Add($"кнопка {name} не помещается в окно");
                }
                dp.Should().BeEmpty("диалог конфликта " + tag + "\n  " + string.Join("\n  ", dp));
            }
            finally
            {
                dlg.Close();
                UiShot.Pump();
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Conflict_Buttons_KeepMine_And_TakeServer_Work()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var store = NewStore();
        var window = new RepairOrdersWindow(Deps(store, new FakeTill(), new List<string>()));
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(8);
            var a = store.Create(new RepairOrder { ClientName = "А", ClientPhone = "0700123456", Device = "Телефон", Fault = "Сломан" }, "Тест", serverNumber: false);
            var b = store.Create(new RepairOrder { ClientName = "Б", ClientPhone = "0700123457", Device = "Часы", Fault = "Сломаны" }, "Тест", serverNumber: false);
            store.MarkSynced(a.Id, 1, null, null);
            store.MarkSynced(b.Id, 1, null, null);
            store.Move(a.Id, RepairStatus.InWork, "Тест"); // правки кассира
            store.Move(b.Id, RepairStatus.InWork, "Тест");
            store.MarkConflict(a.Id, ConflictJson);
            store.MarkConflict(b.Id, ConflictJson);

            window.Select(a.Id);
            UiShot.Settle(4);
            window.KeepMine(a.Id);
            store.Find(a.Id)!.HasConflict.Should().BeFalse();
            store.Find(a.Id)!.Status.Should().Be(RepairStatus.InWork, "мои правки остались");
            store.Find(a.Id)!.UpdatedAt.Should().Be("2026-11-26T10:05:00Z");

            window.TakeServer(b.Id);
            var taken = store.Find(b.Id)!;
            taken.HasConflict.Should().BeFalse();
            taken.Device.Should().Contain("замена экрана");
            taken.FinalPrice.Should().Be(1700.50m);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Accept_UsesServerNumber_ForPrepayAndReceipt_AndShowsBothCodesAfterOfflineNumberChange()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var store = NewStore();
        var till = new FakeTill();
        var printed = new List<string>();
        var online = true;
        // Заглушка сервера: PUT с server_number — номер Р-0005; без связи — временный номер остаётся.
        var deps = Deps(store, till, printed, statusUrl: (st, o) =>
        {
            st.Update(o.Id, x => { x.TempNumber = true; return true; }, "тест", "тест"); // как при server_number: номер временный
            if (online)
                st.ApplySyncResult(o.Id, st.Find(o.Id)!.Rev, new RepairSyncResult(RepairSyncKind.Ok, true, Code: "Р-0005", Number: 5, UpdatedAt: "2026-11-26T10:00:00Z"), "{}", askedServerNumber: true);
            return Task.FromResult<string?>(null);
        });
        var window = new RepairOrdersWindow(deps);
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(8);
            var first = await window.AcceptAsync(Values(prepay: "500"));
            first!.Number.Should().Be(5);
            till.Reasons.Should().ContainSingle().Which.Should().Contain("Р-0005", "предоплата записана уже под серверным номером");
            printed.Should().ContainSingle().Which.Should().Contain("Р-0005");
            window.StatusMessage.Should().Contain("Р-0005").And.NotContain("временный");

            // без связи: печатаем временный номер, потом сервер выдаёт другой
            online = false;
            var second = await window.AcceptAsync(Values());
            RepairRules.Code(second!).Should().Be("Р-0006");
            second.TempNumber.Should().BeTrue();
            printed.Should().HaveCount(2);
            printed[1].Should().Contain("Р-0006");
            window.StatusMessage.Should().Contain("временный");
            store.Find(second.Id)!.PrintedCode.Should().Be("Р-0006");

            store.ApplySyncResult(second.Id, store.Find(second.Id)!.Rev, new RepairSyncResult(RepairSyncKind.Ok, true, Code: "Р-0009", Number: 9, UpdatedAt: "2026-11-26T10:01:00Z"), "{}", askedServerNumber: true);
            window.Select(second.Id);
            UiShot.Settle(4);
            HasText(window, "Р-0006 (итоговый номер — Р-0009)").Should().BeTrue("на экране видны оба номера: на бумаге старый, итоговый — серверный");
            window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Квитанция Р-0009").Should().BeTrue("заголовок карточки — итоговый номер");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Pull_ShowsServerOrders_AndStatusChanges_OnTheFly()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var store = NewStore();
        var statusOnServer = "accepted";
        Task<int> Pull(RepairOrderStore s) => RepairServerSync.PullAsync(s, (_, _) => Task.FromResult(System.Text.Json.JsonDocument.Parse(
            "{\"results\":[{\"id\":\"s7\",\"external_id\":null,\"source\":\"server\",\"code\":\"Р-0007\",\"status\":\"" + statusOnServer + "\",\"device\":\"Планшет\",\"customer_name\":\"Мээрим\",\"customer_phone\":\"0700999888\",\"updated_at\":\"2026-11-26T1" + (statusOnServer == "accepted" ? "0" : "1") + ":00:00Z\"}],\"has_more\":false}").RootElement.Clone()));
        var window = new RepairOrdersWindow(Deps(store, new FakeTill(), new List<string>(), pull: Pull));
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1366, 768));
            UiShot.Settle(8);
            await window.PullNowAsync(); // стартовое чтение окна могло уже принести квитанцию — тогда здесь 0, важно, что она в списке
            for (var i = 0; i < 50 && store.Find("srv-s7") is null; i++)
                await Task.Delay(100);
            UiShot.Settle(4);
            window.Reload();
            Buttons(window).Should().Contain(b => b.Name == "Row_7", "серверная квитанция появилась в списке без перезапуска");
            statusOnServer = "ready";
            (await window.PullNowAsync()).Should().Be(1);
            UiShot.Settle(4);
            store.Find("srv-s7")!.Status.Should().Be(RepairStatus.Ready);
            Avalonia.Automation.AutomationProperties.GetName(Buttons(window).Single(b => b.Name == "Row_7")).Should().Contain(RepairRules.StatusName(RepairStatus.Ready));
            (await window.PullNowAsync()).Should().Be(0, "без изменений на сервере список не перестраивается");
            store.All().Should().ContainSingle("дублей нет");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
