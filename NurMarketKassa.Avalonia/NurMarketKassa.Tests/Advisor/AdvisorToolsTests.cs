using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Advisor;

/// <summary>2026-11-27, владелец: «Дай Нур-советнику доступы ко всем новым функциям и разделам … Полный доступ». Реестр разделов советника
/// (каждый пункт меню владельца есть в реестре и описан), права и тумблеры, маскирование секретов и телефонов, только чтение (отказ на запись),
/// лимиты вызовов/времени/размера, разбор вызовов функций Gemini и круг «модель → программа → модель» с подставным Gemini (без сети и ключа).</summary>
[Collection("AdvisorTools")]
public sealed class AdvisorToolsTests : IDisposable
{
    private readonly List<string> _log = new();
    private readonly Action<string, string> _oldLog;

    public AdvisorToolsTests()
    {
        _oldLog = AdvisorToolDispatcher.Log;
        AdvisorToolDispatcher.Log = (m, _) =>
        {
            lock (_log)
                _log.Add(m);
        };
    }

    public void Dispose()
    {
        AdvisorToolDispatcher.Log = _oldLog;
        TelegramAiChat._standGemini = null;
    }

    private const string Marker = "SECRET-MARKER-7731";

    private sealed class Probe
    {
        public int DataCalls;
        public int OpenCalls;
        public int DraftCalls;
        public AdvisorDataRequest? LastRequest;
    }

    private static AdvisorToolHost Host(Probe probe, Func<string, bool>? has = null, Func<string, bool>? cap = null, Func<string, bool>? inMenu = null,
        Func<AdvisorDataRequest, CancellationToken, Task<object?>>? data = null, bool actions = false, bool confirm = false) => new()
    {
        HasPermission = has ?? (_ => true),
        CapOn = cap ?? (_ => true),
        InMenu = inMenu,
        Open = (id, _) =>
        {
            Interlocked.Increment(ref probe.OpenCalls);
            return Task.FromResult(new NavOutcome(true, id));
        },
        Data = data ?? ((r, _) =>
        {
            Interlocked.Increment(ref probe.DataCalls);
            probe.LastRequest = r;
            return Task.FromResult<object?>(new { name = "Чай " + Marker, price = 120, purchase_price = 80, phone = "+996 700 123 456" });
        }),
        Confirm = (_, _) => Task.FromResult(confirm),
        Draft = (_, _) =>
        {
            Interlocked.Increment(ref probe.DraftCalls);
            return Task.FromResult("ЧЕРНОВИК");
        },
        ActionsEnabled = actions,
        Today = () => new DateTime(2026, 11, 27),
    };

    // ── Реестр ───────────────────────────────────────────────────────────────────────────────────────────────

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }

    /// <summary>Ключи пунктов меню владельца прямо из OwnerShellWindow.BuildNavigation (Add("key", …)).</summary>
    private static List<string> OwnerMenuKeys()
    {
        var source = File.ReadAllText(FindRepoFile("NurMarketKassa.Avalonia", "Views.axaml", "Main", "OwnerShellWindow.axaml.cs"));
        var keys = Regex.Matches(source, @"^\s+Add\(""([a-z0-9]+)"",", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        if (source.Contains("Add(SalesFinanceKey,"))
            keys.Add("salesfinance");
        return keys.Distinct().ToList();
    }

    [Fact]
    public void Every_owner_menu_item_is_in_the_advisor_registry()
    {
        var keys = OwnerMenuKeys();
        keys.Should().HaveCountGreaterThan(30, "меню владельца разобрано из исходника");
        var missing = keys.Where(k => !AdvisorSections.MenuKeys.Contains(k) && !AdvisorSections.Excluded.ContainsKey(k)).ToList();
        missing.Should().BeEmpty("новый пункт меню владельца нужно добавить в словарь AdvisorNavRouter и в AdvisorSections.Info (описание, право, данные)");
    }

    [Fact]
    public void Every_registry_entry_has_description_names_and_unique_id()
    {
        AdvisorSections.MissingInfo().Should().BeEmpty("у каждой записи словаря переходов должно быть описание в AdvisorSections.Info");
        foreach (var s in AdvisorSections.All)
        {
            s.About.Trim().Length.Should().BeGreaterThan(15, $"описание раздела «{s.Id}» обязательно — без него советник не может объяснить раздел");
            s.Names.Should().HaveCount(5);
            s.Names.Should().OnlyContain(n => !string.IsNullOrWhiteSpace(n), $"названия «{s.Id}» на 5 языках");
        }
        AdvisorSections.All.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("акции", "promotions")]
    [InlineData("акция", "promotions")]
    [InlineData("диалоги бота", "telegrambot.dialogs")]
    [InlineData("экспорт в 1с", "settings.onec")]
    [InlineData("отчёт торга", "telegrambot.offers")]
    [InlineData("бот продавец", "telegrambot.seller")]
    [InlineData("ремонт", "repair")]
    [InlineData("рассылка", "mailing")]
    [InlineData("табель", "timesheet")]
    [InlineData("маркетплейс", "marketplace")]
    [InlineData("repair", "repair")]
    public void Find_resolves_new_sections_by_words(string words, string id) =>
        AdvisorSections.Find(words)?.Id.Should().Be(id);

    [Theory]
    [InlineData("открой диалоги бота", "telegrambot.dialogs")]
    [InlineData("открой отчёт торга", "telegrambot.offers")]
    [InlineData("открой экспорт в 1С", "settings.onec")]
    [InlineData("покажи ремонт", "repair")]
    [InlineData("открой рассылку", "mailing")]
    [InlineData("открой табель", "timesheet")]
    [InlineData("открой зарплату", "salary")]
    public void Router_opens_new_sections(string phrase, string id)
    {
        var route = AdvisorNavRouter.Parse(phrase);
        route.Kind.Should().Be(AdvisorNavRouter.RouteKind.Single, phrase);
        route.Target!.Id.Should().Be(id);
    }

    [Fact]
    public void Declarations_list_only_read_tools_with_registry_enums()
    {
        var d = new AdvisorToolDispatcher(Host(new Probe())).Declarations();
        var names = d.Select(f => f!["name"]!.GetValue<string>()).ToList();
        names.Should().BeEquivalentTo(AdvisorToolDispatcher.ReadTools, "по умолчанию — только чтение и переход");
        names.Should().NotContain(AdvisorToolDispatcher.PrepareDraft);
        var data = d.First(f => f!["name"]!.GetValue<string>() == AdvisorToolDispatcher.GetSectionData)!;
        var areas = data["parameters"]!["properties"]!["section"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        areas.Should().Contain(new[] { "promotions", "telegrambot.offers", "telegrambot.dialogs", "repair", "mailing", "settings.onec", "salesfinance", "marketplace", "tariff" });
        var open = d.First(f => f!["name"]!.GetValue<string>() == AdvisorToolDispatcher.OpenSection)!;
        var targets = open["parameters"]!["properties"]!["target"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        targets.Should().Contain(AdvisorSections.All.Where(s => s.Openable).Select(s => s.Id));
        targets.Should().NotContain("exit", "советник не закрывает программу");
    }

    // ── Права, тумблеры, подключение ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_permission_means_no_data_and_no_open()
    {
        var probe = new Probe();
        var tools = new AdvisorToolDispatcher(Host(probe, has: p => p != PosPermissions.ViewClients));
        (await tools.CallAsync("get_section_data", "{\"section\":\"clients\"}", default)).Should().StartWith("НЕТ ПРАВА");
        (await tools.CallAsync("open_section", "{\"target\":\"mailing\"}", default)).Should().StartWith("НЕТ ПРАВА");
        probe.DataCalls.Should().Be(0, "без права данные даже не запрашиваются");
        probe.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task Cashier_without_analytics_never_sees_purchase_prices()
    {
        var probe = new Probe();
        var cashier = new AdvisorToolDispatcher(Host(probe, has: p => p is PosPermissions.ViewProducts or PosPermissions.ViewSales));
        cashier.PurchaseVisible.Should().BeFalse();
        var text = await cashier.CallAsync("get_section_data", "{\"section\":\"warehouse\"}", default);
        text.Should().Contain("price").And.NotContain("purchase_price").And.NotContain("80");
        probe.LastRequest!.PurchaseVisible.Should().BeFalse("провайдер тоже знает, что закупку класть нельзя");

        var owner = new AdvisorToolDispatcher(Host(new Probe()));
        (await owner.CallAsync("get_section_data", "{\"section\":\"warehouse\"}", default)).Should().Contain("purchase_price");

        var privacyOff = new AdvisorToolDispatcher(Host(new Probe(), cap: c => c != "priv.purchase"));
        (await privacyOff.CallAsync("get_section_data", "{\"section\":\"warehouse\"}", default)).Should().NotContain("purchase_price", "тумблер «Закупочные цены уходят в ИИ» выключен");
    }

    [Fact]
    public async Task Disabled_capability_and_not_connected_section_are_refused()
    {
        var probe = new Probe();
        var noSales = new AdvisorToolDispatcher(Host(probe, cap: c => c != "read.sales"));
        (await noSales.CallAsync("get_section_data", "{\"section\":\"salesfinance\"}", default)).Should().StartWith("ВЫКЛЮЧЕНО");
        var noNav = new AdvisorToolDispatcher(Host(probe, cap: c => c != "nav.open"));
        (await noNav.CallAsync("open_section", "{\"target\":\"warehouse\"}", default)).Should().StartWith("ВЫКЛЮЧЕНО");
        var notConnected = new AdvisorToolDispatcher(Host(probe, inMenu: k => k != "repair"));
        var reply = await notConnected.CallAsync("open_section", "{\"target\":\"repair\"}", default);
        reply.Should().StartWith("НЕ ПОДКЛЮЧЕНО").And.Contain("Маркетплейс");
        probe.DataCalls.Should().Be(0);
        probe.OpenCalls.Should().Be(0);
    }

    [Fact]
    public async Task Open_returns_fact_and_marks_used()
    {
        var probe = new Probe();
        var tools = new AdvisorToolDispatcher(Host(probe));
        (await tools.CallAsync("open_section", "{\"target\":\"telegrambot.offers\"}", default)).Should().StartWith("ОТКРЫТО");
        probe.OpenCalls.Should().Be(1);
        tools.Used.Should().ContainSingle(u => u.StartsWith(AdvisorSections.Find("telegrambot.offers")!.Title));
        (await tools.CallAsync("open_section", "{\"target\":\"unknown\",\"words\":\"космодром\"}", default)).Should().StartWith("НЕ ОТКРЫТО");
    }

    // ── Секреты ──────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Redactor_removes_secrets_masks_phones_keeps_codes_and_sums()
    {
        var raw = new JsonObject
        {
            ["api_key"] = "AIzaSyD-very-secret-key-1234567890abc",
            ["gemini_api_key"] = "x",
            ["botToken"] = "123456789:AAE-secret-token-abcdefghijklmnopqrstuv",
            ["password"] = "qwerty",
            ["pin"] = 1234,
            ["card_number"] = "4111 1111 1111 1111",
            ["card"] = 1500, // сумма оплат картой — не секрет
            ["phone"] = "+996 700 123 456",
            ["barcode"] = "4600000000017",
            ["note"] = "ключ AIzaSyD-very-secret-key-1234567890abc, jwt eyJhbGciOiJIUzI1.eyJzdWIiOiIxMjM0NTY3.SflKxwRJSMeKKF2QT4fwpMeJf, звоните 0700 123 456, карта 4111-1111-1111-1111",
        };
        var text = AdvisorRedactor.Compact(raw, purchaseVisible: true, 6000);
        text.Should().NotContain("AIzaSy").And.NotContain("secret-token").And.NotContain("qwerty").And.NotContain("1234,")
            .And.NotContain("4111 1111").And.NotContain("4111-1111").And.NotContain("700 123").And.NotContain("eyJhbGci").And.NotContain("gemini_api_key");
        text.Should().Contain("*** ** *56").And.Contain("4600000000017").And.Contain("\"card\":1500").And.Contain("**** 1111");
    }

    [Fact]
    public async Task Phones_in_provider_data_are_masked_and_content_is_not_logged()
    {
        var tools = new AdvisorToolDispatcher(Host(new Probe()));
        var text = await tools.CallAsync("get_section_data", "{\"section\":\"warehouse\",\"query\":\"чай\"}", default);
        text.Should().NotContain("700 123 456").And.Contain("*** ** *56");
        lock (_log)
        {
            _log.Should().NotBeEmpty();
            _log.Should().OnlyContain(l => !l.Contains(Marker) && !l.Contains("чай"), "в журнал — только имя инструмента, раздел, время и размер");
        }
    }

    // ── Только чтение ────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("delete_product")]
    [InlineData("set_price")]
    [InlineData("send_message")]
    [InlineData("refund_receipt")]
    [InlineData("updatePromotion")]
    [InlineData("prepare_draft")]
    [InlineData("whatever_tool")]
    public async Task Write_tools_are_refused(string name)
    {
        var probe = new Probe();
        var tools = new AdvisorToolDispatcher(Host(probe));
        (await tools.CallAsync(name, "{\"kind\":\"promo\"}", default)).Should().Contain("ОТКАЗ");
        probe.DataCalls.Should().Be(0);
        probe.DraftCalls.Should().Be(0);
    }

    [Fact]
    public async Task Draft_needs_enabled_actions_and_owner_confirmation()
    {
        var refused = new Probe();
        var noConfirm = new AdvisorToolDispatcher(Host(refused, actions: true, confirm: false));
        noConfirm.Declarations().Select(f => f!["name"]!.GetValue<string>()).Should().Contain(AdvisorToolDispatcher.PrepareDraft);
        (await noConfirm.CallAsync("prepare_draft", "{\"kind\":\"promo\"}", default)).Should().Contain("отказался");
        refused.DraftCalls.Should().Be(0, "без «Выполнить» владельца ничего не делается");

        var accepted = new Probe();
        var yes = new AdvisorToolDispatcher(Host(accepted, actions: true, confirm: true));
        (await yes.CallAsync("prepare_draft", "{\"kind\":\"supplier_order\"}", default)).Should().Be("ЧЕРНОВИК");
        accepted.DraftCalls.Should().Be(1);
    }

    // ── Лимиты ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Calls_per_answer_are_limited()
    {
        var probe = new Probe();
        var tools = new AdvisorToolDispatcher(Host(probe));
        for (var i = 0; i < AdvisorToolDispatcher.MaxCallsPerAnswer; i++)
            (await tools.CallAsync("get_section_data", "{\"section\":\"warehouse\"}", default)).Should().NotStartWith("ЛИМИТ");
        (await tools.CallAsync("get_section_data", "{\"section\":\"warehouse\"}", default)).Should().StartWith("ЛИМИТ");
        probe.DataCalls.Should().Be(AdvisorToolDispatcher.MaxCallsPerAnswer);
    }

    [Fact]
    public async Task Slow_section_times_out()
    {
        var tools = new AdvisorToolDispatcher(Host(new Probe(), data: async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "поздно";
        }));
        var watch = Stopwatch.StartNew();
        (await tools.CallAsync("get_section_data", "{\"section\":\"salesfinance\"}", default)).Should().StartWith("НЕ УСПЕЛО");
        watch.Elapsed.Should().BeLessThan(AdvisorToolDispatcher.CallTimeout + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Big_answers_are_cut_to_the_limit()
    {
        var tools = new AdvisorToolDispatcher(Host(new Probe(), data: (_, _) =>
            Task.FromResult<object?>(Enumerable.Range(1, 5000).Select(i => new { name = "Товар номер " + i + new string('ж', 200), stock = i }).ToList())));
        var text = await tools.CallAsync("get_section_data", "{\"section\":\"warehouse\"}", default);
        text.Length.Should().BeLessThanOrEqualTo(AdvisorToolDispatcher.MaxResultChars);
        JsonNode.Parse(text).Should().NotBeNull("ответ — корректный JSON");
    }

    [Fact]
    public async Task Provider_failure_does_not_leak_exception_text()
    {
        var tools = new AdvisorToolDispatcher(Host(new Probe(), data: (_, _) => throw new InvalidOperationException("db password=" + Marker)));
        var text = await tools.CallAsync("get_section_data", "{\"section\":\"warehouse\"}", default);
        text.Should().StartWith("НЕ ВЫПОЛНЕНО").And.NotContain(Marker);
        lock (_log)
            _log.Should().OnlyContain(l => !l.Contains(Marker));
    }

    // ── Формат и разбор ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Data_answer_is_compact_json_with_section_and_period()
    {
        var probe = new Probe();
        var tools = new AdvisorToolDispatcher(Host(probe));
        var text = await tools.CallAsync("get_section_data", "{\"section\":\"salesfinance\",\"period\":\"week\",\"top\":99}", default);
        var json = JsonNode.Parse(text)!;
        json["section"]!.GetValue<string>().Should().Be(AdvisorSections.Find("salesfinance")!.TitleRu);
        json["data"].Should().NotBeNull();
        probe.LastRequest!.From.Should().Be(new DateTime(2026, 11, 21));
        probe.LastRequest.To.Should().Be(new DateTime(2026, 11, 27));
        probe.LastRequest.Top.Should().Be(30, "не больше 30 строк");
        tools.Used.Should().ContainSingle(u => u.StartsWith(AdvisorSections.Find("salesfinance")!.Title));
    }

    [Fact]
    public void Arguments_and_function_calls_are_parsed_safely()
    {
        AdvisorToolDispatcher.ParseArgs("{не json").Should().BeEmpty();
        AdvisorToolDispatcher.ParseArgs(null).Should().BeEmpty();
        var calls = TelegramAiChat.ReadFunctionCalls("""{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"get_section_data","args":{"section":"repair"}}},{"text":""}]}}]}""");
        calls.Should().ContainSingle();
        calls[0].Name.Should().Be("get_section_data");
        calls[0].Args.Should().Contain("repair");
        TelegramAiChat.ReadFunctionCalls("мусор").Should().BeEmpty();
    }

    [Theory]
    [InlineData("today", "2026-11-27", "2026-11-27")]
    [InlineData("yesterday", "2026-11-26", "2026-11-26")]
    [InlineData("prev_month", "2026-10-01", "2026-10-31")]
    [InlineData("", "2026-11-01", "2026-11-27")]
    public void Periods_are_parsed(string period, string from, string to)
    {
        var (f, t, _) = AdvisorPeriod.Parse(period, null, null, new DateTime(2026, 11, 27));
        f.Should().Be(DateTime.Parse(from));
        t.Should().Be(DateTime.Parse(to));
    }

    [Fact]
    public void Custom_period_is_ordered_clamped_and_not_longer_than_a_year()
    {
        var (f, t, _) = AdvisorPeriod.Parse("custom", "2026-12-31", "2024-01-01", new DateTime(2026, 11, 27));
        t.Should().Be(new DateTime(2026, 11, 27), "будущее обрезается сегодняшним днём");
        (t - f).TotalDays.Should().BeLessThanOrEqualTo(366);
    }

    // ── Круг с подставным Gemini ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Chat_round_trip_with_fake_gemini_calls_tool_then_answers()
    {
        var probe = new Probe();
        var tools = new AdvisorToolDispatcher(Host(probe, has: p => p != PosPermissions.ViewAnalytics));
        var bodies = new List<string>();
        TelegramAiChat._standGemini = body =>
        {
            bodies.Add(body);
            return body.Contains("functionResponse")
                ? """{"candidates":[{"content":{"role":"model","parts":[{"text":"Выручка за неделю — 100 сом."}]}}]}"""
                : """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"get_section_data","args":{"section":"salesfinance","period":"week"}},"thoughtSignature":"sig-1"}]}}]}""";
        };
        var (answer, error) = await TelegramAiChat.AskWithToolsForTestsAsync("какая выручка за неделю?", tools, default);
        error.Should().BeNull();
        answer.Should().Be("Выручка за неделю — 100 сом.");
        probe.DataCalls.Should().Be(1);
        bodies.Should().HaveCount(2);
        bodies[0].Should().Contain("functionDeclarations").And.Contain("get_section_data");
        bodies[1].Should().Contain("sig-1", "ответ модели с подписью мысли возвращается как есть (Gemini 3)");
        bodies[1].Should().NotContain("purchase_price", "без права «Аналитика» закупка в ИИ не уходит").And.NotContain("700 123 456");
        tools.Used.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Fake_gemini_that_keeps_calling_tools_gets_a_round_without_tools()
    {
        var tools = new AdvisorToolDispatcher(Host(new Probe()));
        var bodies = new List<string>();
        TelegramAiChat._standGemini = body =>
        {
            bodies.Add(body);
            return body.Contains("functionDeclarations")
                ? """{"candidates":[{"content":{"role":"model","parts":[{"functionCall":{"name":"list_sections","args":{}}}]}}]}"""
                : """{"candidates":[{"content":{"role":"model","parts":[{"text":"Готово."}]}}]}""";
        };
        var (answer, _) = await TelegramAiChat.AskWithToolsForTestsAsync("какие есть разделы?", tools, default);
        answer.Should().Be("Готово.");
        bodies.Should().HaveCount(AdvisorToolDispatcher.MaxRounds + 1);
        bodies[^1].Should().NotContain("functionDeclarations", "последний круг — без инструментов, модель отвечает текстом");
    }
}
