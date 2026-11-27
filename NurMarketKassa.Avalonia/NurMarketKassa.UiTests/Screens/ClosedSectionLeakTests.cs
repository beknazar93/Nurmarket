using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-07 (память владельца, живая проверка 1.17.81, Б-J1): разделы программы владельца после закрытия должны уходить из памяти. Раздел открывается ТАК ЖЕ,
/// как в программе (OwnerShellWindow.OpenSection — вся обвязка оболочки: масштаб, отслеживание ввода, Esc, подписки Closed), закрывается (Window.Close — как при закрытии
/// спрятанного раздела по простою), затем идут сборки мусора; слабая ссылка на окно обязана очиститься (так же, как её проверяет ClosedObjectWatch). Красный тест — утечка:
/// закрытый раздел кто-то держит (статическое событие без отписки, анимация на общих часах, таймер, замыкание в статическом кэше…); сообщение теста называет цепочку удержания
/// от статического поля (Harness/HeapPath.cs).</summary>
public sealed class ClosedSectionLeakTests
{
    private static T Svc<T>() where T : notnull => TestHost.Services.GetRequiredService<T>();

    /// <summary>Разделы, как их создаёт OwnerShellWindow (OpenSection(key, create)).</summary>
    private static readonly Dictionary<string, Func<Window>> Sections = new()
    {
        ["purchases"] = () => new PurchasesWindow(),
        ["warehouse"] = () => Svc<WarehouseWindow>(),
        // AsSalesAndFinance — internal (отражение: приложение для теста не менялось)
        ["sales+finance"] = () => (Window)typeof(FinanceWindow).GetMethod("AsSalesAndFinance", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Svc<FinanceWindow>(), null)!,
        ["finance"] = () => Svc<FinanceWindow>(),
        ["sales"] = () => Svc<SalesWindow>(),
        ["clients"] = () => Svc<ClientsWindow>(),
        ["suppliers"] = () => new SuppliersWindow(),
        ["supplierreturns"] = () => new SupplierReturnsWindow(),
        ["debts"] = () => new DebtsWindow(),
        ["abc"] = () => Svc<AbcAnalysisWindow>(),
        // остальные разделы меню владельца, которые поднимаются без железа и сети (WebView-разделы CRM/WhatsApp/редактор сайта не берём)
        ["calculator"] = () => new CalculatorWindow(),
        ["barcodes"] = () => new BarcodePrintSectionWindow(), // 2026-11-11
        ["restock"] = () => Svc<RestockSuggestionsWindow>(),
        ["branches"] = () => new BranchesWindow(),
        ["rentals"] = () => new RentalsWindow(null, null),
        ["investments"] = () => new InvestmentsWindow(),
        ["losssales"] = () => new LossSalesWindow(),
        ["bonusanalytics"] = () => new BonusAnalyticsWindow(),
        ["sizesreport"] = () => new SizesReportWindow(),
        ["salary"] = () => new SalaryWindow(),
        ["funnel"] = () => new FunnelWindow(),
        ["mailing"] = () => new ClientMailingWindow(), // 2026-11-26
        ["telegrambot"] = () => new TelegramBotAnalyticsWindow(),
        ["profitcash"] = () => new ProfitCashReconcileWindow(),
        ["siteorders"] = () => new SiteOrdersWindow(),
        ["appshop"] = () => new AppShopWindow(),
        ["marketplace"] = () => new MarketplaceWindow().AsSection(),
        ["settings"] = () => Svc<PosSettingsWindow>(),
        ["kb"] = () => Svc<KnowledgeBaseWindow>(),
        ["logs"] = () => Svc<LogsAndErrorsWindow>(),
        ["analytics"] = () => Svc<FinanceWindow>().AsAnalyticsSection(),
    };

    public static IEnumerable<object[]> Names() => Sections.Keys.Select(k => new object[] { k });

    [AvaloniaTheory]
    [MemberData(nameof(Names))]
    public async Task ClosedSection_IsCollected(string name)
    {
        await TestHost.WarmAsync(); UiRobot.Reset(); // 2026-11-21: UiRobot.Opened (статический) держал окна прежних сценариев и ложно «утекал» в этом тесте в общем прогоне
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var weak = OpenAndClose(name, Sections[name], null);
        AssertCollected(weak, $"Раздел «{name}» остался в памяти после закрытия и сборок мусора — его кто-то держит (подписка на статическое событие, анимация, таймер, кэш).");
        CloseShell();
    }

    /// <summary>«Закупки» без связи: поставщики берутся из памяти кассы, и выбор поставщика подписывается на ServerOutageMonitor.Recovered (так было в живой проверке).</summary>
    [AvaloniaFact]
    public async Task ClosedPurchases_OfflineSuppliers_IsCollected()
    {
        await TestHost.WarmAsync(); UiRobot.Reset(); // 2026-11-21: UiRobot.Opened (статический) держал окна прежних сценариев и ложно «утекал» в этом тесте в общем прогоне
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        PurchaseReceivingService.RememberSuppliers([new SupplierInfo("s-leak-1", "Тестовый поставщик", "0555000111", "", "", "", "", "", "", "", null)]);
        UiFake.Server.Server.Reset(); // заглушки сняты: список поставщиков не придёт → «из памяти кассы»
        var weak = OpenAndClose("purchases", () => new PurchasesWindow(), null);
        AssertCollected(weak, "«Закупки» (поставщики из памяти кассы) остались в памяти после закрытия.");
        CloseShell();
        UiFake.Reset();
    }

    /// <summary>Страница «Приём товара» внутри «Закупок» (свои подписки на статические события настроек наценки) тоже не должна держать окно после закрытия раздела.</summary>
    [AvaloniaFact]
    public async Task ClosedPurchases_WithReceivePage_IsCollected()
    {
        await TestHost.WarmAsync(); UiRobot.Reset(); // 2026-11-21: UiRobot.Opened (статический) держал окна прежних сценариев и ложно «утекал» в этом тесте в общем прогоне
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var weak = OpenAndClose("purchases", () => new PurchasesWindow(), w => ((PurchasesWindow)w).OpenReceive(null));
        AssertCollected(weak, "«Закупки» с открытой страницей «Приём товара» остались в памяти после закрытия.");
        CloseShell();
    }

    /// <summary>Приём открыли и закрыли («← К закупкам»), потом закрыли раздел: подписка на ReceivingMarkupSettingsStore.Changed не должна пережить страницу приёма.</summary>
    [AvaloniaFact]
    public async Task ClosedPurchases_AfterReceivePageClosed_IsCollected()
    {
        await TestHost.WarmAsync(); UiRobot.Reset(); // 2026-11-21: UiRobot.Opened (статический) держал окна прежних сценариев и ложно «утекал» в этом тесте в общем прогоне
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var weak = OpenAndClose("purchases", () => new PurchasesWindow(), w =>
        {
            var p = (PurchasesWindow)w;
            p.OpenReceive(null);
            UiShot.Settle(2, 20);
            typeof(PurchasesWindow).GetMethod("CloseSub", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, null);
        });
        AssertCollected(weak, "«Закупки» после закрытия страницы «Приём товара» остались в памяти после закрытия раздела.");
        CloseShell();
    }

    /// <summary>2026-11-27 (живой проход 27.11, журнал владельца: «закрытые разделы всё ещё в памяти: «debts» 247 мин, «salesfinance» 230 мин»): в программе раздел не закрывают
    /// сразу — его прячут переходом на «Сводку»/другой раздел, а закрывает простой (CloseHiddenSection). Проверяем именно этот путь: показан → спрятан → закрыт по простою.</summary>
    [AvaloniaTheory]
    [InlineData("debts")]
    [InlineData("salesfinance")]
    public async Task HiddenThenIdleClosedSection_IsCollected(string key)
    {
        await TestHost.WarmAsync(); UiRobot.Reset();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var create = key == "debts" ? Sections["debts"] : Sections["sales+finance"];
        var weak = OpenHideAndIdleClose(key, create);
        AssertCollected(weak, $"Раздел «{key}», закрытый по простою после того, как его спрятали, остался в памяти.");
        CloseShell();
    }

    /// <summary>2026-11-27: в журнале владельца разделы открывались подряд каждые 9 с — шестой по счёту закрывался «давно не открывали», пока ещё грузил данные.
    /// Здесь сервер отвечает на все чтения через 2 с: раздел закрывают посреди загрузки; после ответов он обязан уйти из памяти.</summary>
    [AvaloniaTheory]
    [InlineData("debts")]
    [InlineData("salesfinance")]
    public async Task ClosedWhileLoading_IsCollected(string key)
    {
        await TestHost.WarmAsync(); UiRobot.Reset();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Server.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/*").UsingGet())
            .AtPriority(100)
            .RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\":0,\"next\":null,\"previous\":null,\"results\":[]}").WithDelay(TimeSpan.FromSeconds(2)));
        try
        {
            var create = key == "debts" ? Sections["debts"] : Sections["sales+finance"];
            var weak = OpenAndClose(key, create, null);
            AssertCollected(weak, $"Раздел «{key}», закрытый посреди загрузки данных, остался в памяти.");
        }
        finally
        {
            CloseShell();
            UiFake.Reset();
        }
    }

    /// <summary>2026-11-27: обход разделов подряд, как в журнале владельца 27.11 (07:37–07:42): «Продажи и финансы», «Долги клиентов», затем ещё семь разделов —
    /// оболочка сама закрывает спрятанные сверх пяти (TrimHiddenSections). Оба раздела после этого должны уйти из памяти.</summary>
    [AvaloniaFact]
    public async Task SectionsCrawl_TrimmedSalesFinanceAndDebts_AreCollected()
    {
        await TestHost.WarmAsync(); UiRobot.Reset();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var (salesFinance, debts) = Crawl();
        try
        {
            AssertCollected(salesFinance, "«Продажи и финансы», закрытые оболочкой при обходе разделов, остались в памяти.");
            AssertCollected(debts, "«Долги клиентов», закрытые оболочкой при обходе разделов, остались в памяти.");
        }
        finally
        {
            CloseShell();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference SalesFinance, WeakReference Debts) Crawl()
    {
        var shell = _shell = new OwnerShellWindow { Width = 1366, Height = 800 };
        shell.Show();
        UiShot.Settle(3, 30);
        var open = typeof(OwnerShellWindow).GetMethod("OpenSection", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(string), typeof(Func<Window>)], null)!;
        WeakReference? sf = null, db = null;
        foreach (var key in new[] { "salesfinance", "debts", "abc", "investments", "losssales", "bonusanalytics", "funnel", "mailing", "calculator" })
        {
            var create = key switch { "salesfinance" => Sections["sales+finance"], _ => Sections[key] };
            Window? w = null;
            open.Invoke(shell, [key, (Func<Window>)(() => w = create())]);
            UiShot.Settle(6, 30);
            if (key == "salesfinance") sf = new WeakReference(w);
            if (key == "debts") db = new WeakReference(w);
            w = null;
        }
        UiShot.Settle(3, 20);
        return (sf!, db!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenHideAndIdleClose(string key, Func<Window> create)
    {
        var shell = _shell = new OwnerShellWindow { Width = 1366, Height = 800 };
        shell.Show();
        UiShot.Settle(3, 30);
        Window? opened = null;
        var t = typeof(OwnerShellWindow);
        var open = t.GetMethod("OpenSection", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(string), typeof(Func<Window>)], null)!;
        open.Invoke(shell, [key, (Func<Window>)(() => opened = create())]);
        UiShot.Settle(10, 40);
        // «Сводка» — раздел спрятан (как переход владельца в другое место)
        var show = t.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Single(m => m.Name == "ShowSection" && m.GetParameters().Length == 1);
        show.Invoke(shell, [null]);
        UiShot.Settle(6, 30);
        var weak = new WeakReference(opened);
        // закрытие по простою — как TrimIdleHiddenSections → CloseHiddenSection
        var sections = (System.Collections.IList)t.GetField("_sections", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(shell)!;
        var section = sections.Cast<object>().Single(s => (string)s.GetType().GetProperty("Key")!.GetValue(s)! == key);
        t.GetMethod("CloseHiddenSection", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(shell, [section, "простой (тест)"]);
        opened = null;
        section = null;
        UiShot.Settle(3, 20);
        return weak;
    }

    private static OwnerShellWindow? _shell;

    private static void CloseShell()
    {
        _shell?.Close();
        _shell = null;
        UiShot.Settle(2, 20);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClose(string key, Func<Window> create, Action<Window>? afterShow)
    {
        var shell = _shell = new OwnerShellWindow();
        shell.Show();
        UiShot.Settle(3, 30);
        Window? opened = null;
        var open = typeof(OwnerShellWindow).GetMethod("OpenSection", BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(string), typeof(Func<Window>)], null)!;
        open.Invoke(shell, [key, (Func<Window>)(() => opened = create())]);
        UiShot.Settle(8, 30);
        afterShow?.Invoke(opened!);
        UiShot.Settle(3, 30);
        var weak = new WeakReference(opened);
        // Закрытие — как CloseHiddenSection оболочки: просто Window.Close(); всё остальное (Closed оболочки, ClosedWindowCleanup) должно отработать само.
        opened!.Close();
        opened = null;
        UiShot.Settle(3, 20);
        return weak;
    }

    /// <summary>Красный тест называет виновника: цепочка удержания от статического поля (HeapPath).</summary>
    private static void AssertCollected(WeakReference weak, string message)
    {
        if (Collected(weak))
            return;
        var target = weak.Target;
        Assert.Fail(message + Environment.NewLine + (target is null ? "(объект собрался во время поиска)" : HeapPath.Find(target)));
    }

    private static bool Collected(WeakReference weak)
    {
        // Утечки видны сразу; фоновые запросы закрытого окна (заглушка отвечает «нет связи») за несколько секунд заканчиваются.
        for (var i = 0; i < 40; i++)
        {
            UiShot.Settle(2, 50);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weak.IsAlive)
                return true;
        }

        return false;
    }
}
