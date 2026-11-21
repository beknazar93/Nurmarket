using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.ProductMerge;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Stock;

/// <summary>2026-11-21 (ответ Умара 21.11 02:02, «всё в бою»): серверное слияние товаров как ОСНОВНОЙ путь — dry_run на группу, POST main/products/merge/ с Idempotency-Key, журнал с merge_id,
/// откат POST …/undo/ (409 undo_blocked — причина), «не объединит» (409 merge_not_supported — причины), 404/405 → запасной ручной путь, идемпотентность при обрыве связи.
/// Часть 1 — раннер на заглушке сервера (<see cref="FakeMergeServer"/>), часть 2 — контракт HTTP (<see cref="ProductMergeApi"/> против WireMock). Запускать только tools/qa/test-safe.ps1.</summary>
[Collection("MergeStore")]
public sealed class ProductMergeServerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "merge-srv-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly TimeSpan _oldDelay = ProductMergeRunner.RetryDelay;

    public ProductMergeServerTests()
    {
        Directory.CreateDirectory(_dir);
        MergeStore.DirOverride = _dir;
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ProductMergeRunner.RetryDelay = TimeSpan.Zero;
        ProductMergeApi.ResetSupportCache();
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        ProductMergeRunner.RetryDelay = _oldDelay;
        ProductMergeApi.ResetSupportCache();
        MergeStore.DirOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    private static MergeCard Card(string id, string name, string barcode, double stock, double? purchase = null, params MergeAltCode[] alt) =>
        new(id, name, barcode, alt.ToList(), stock, 100, purchase, "Напитки", "Асу", "шт", ["http://img/" + id + ".jpg"], false);

    private static (MergeGroup Group, MergePlan Plan, FakeMergeServer Server) Setup(FakeMergeServer.MergeMode mode, Action<FakeMergeServer>? tune = null, string? newTitle = null)
    {
        var server = new FakeMergeServer { ServerMode = mode };
        server.Add(Card("m", "Асу", "111", 10, 50, new MergeAltCode("901", "Мята", 0)));
        server.Add(Card("s1", "Асу клубника", "222", 20, 80, new MergeAltCode("902", null, 6)));
        server.Add(Card("s2", "Асу лимон", "333", 5, 50));
        tune?.Invoke(server);
        var products = server.Cards.Values.Select(c => new MergeProduct(c.Id, c.Name, "Асу", "Напитки", 100, c.Barcode, c.Alt, "шт", false, "product", c.Stock, c.PurchasePrice ?? 0, false, null)).ToList();
        var g = MergeCandidateFinder.Find(products).Single();
        var draft = MergePlanner.DefaultDraft(g);
        draft.MainId.Should().Be("m");
        if (newTitle is not null)
            draft.NewMainTitle = newTitle;
        return (g, MergePlanner.Build(g, draft), server);
    }

    // ───────────── выполнение через сервер ─────────────

    [Fact]
    public async Task Server_merge_is_the_main_path_dry_run_first_then_one_request_with_key_and_variant_names()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported);
        var result = await new ProductMergeRunner(server, "Владелец", "acc").RunAsync(plan);

        result.Ok.Should().BeTrue(result.Message);
        result.Status.Should().Be("done");
        server.MergeCalls.Should().HaveCount(2);
        server.MergeCalls[0].Dry.Should().BeTrue("перед выполнением — dry_run на группу");
        server.MergeCalls[0].Key.Should().BeNull("dry_run без ключа");
        server.MergeCalls[1].Dry.Should().BeFalse();
        server.MergeCalls[1].Key.Should().NotBeNullOrWhiteSpace("Idempotency-Key обязателен");
        server.MergeCalls[1].Target.Should().Be("m");
        server.MergeCalls[1].Sources.Should().BeEquivalentTo(["s1", "s2"]);
        server.MergeCalls[1].Names.Should().Equal(new Dictionary<string, string> { ["s1"] = "Клубника", ["s2"] = "Лимон" });

        // всё сделал сервер: ни ручных PATCH источников, ни актов остатков, ни архива кассой
        server.Calls.Should().NotContain(c => c.StartsWith("stock:") || c.StartsWith("archive:") || c == "patch:s1" || c == "patch:s2");
        server.StockDocs.Should().BeEmpty();
        server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901", "222", "902", "333"]);
        server.Cards["m"].Stock.Should().Be(35);
        server.Cards["s1"].Archived.Should().BeTrue();

        var entry = MergeStore.LoadJournal("acc").Single();
        entry.Mode.Should().Be("server");
        entry.IsServer.Should().BeTrue();
        entry.ServerMergeId.Should().Be("srv-1");
        entry.ServerMovedBarcodes.Should().Be(3, "222, 902 и 333; 901 уже был у главного");
        entry.ServerMovedQty.Should().Be(25);
        entry.ServerStockDocument.Should().Be("doc-1");
        entry.Status.Should().Be("done");
        entry.Sources.Should().OnlyContain(s => s.State == "done" && s.Archived);
        entry.Sources.Single(s => s.Id == "s1").MovedStock.Should().Be(20);
        entry.CanRollback.Should().BeTrue();
        MergeStore.LoadBackup(entry.Id)!.Cards.Select(c => c.Id).Should().BeEquivalentTo(["m", "s1", "s2"], "бэкап JSON перед слиянием остаётся");
    }

    [Fact]
    public async Task Server_merge_still_writes_the_new_name_and_weighted_purchase_to_the_main()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported, newTitle: "Асу (вкусы)");
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);
        result.Ok.Should().BeTrue(result.Message);
        server.Cards["m"].Name.Should().Be("Асу (вкусы)");
        server.Cards["m"].PurchasePrice.Should().BeApproximately((10 * 50 + 20 * 80 + 5 * 50) / 35.0, 0.0001);
        MergeStore.LoadJournal().Single().NewPurchase.Should().NotBeNull();
    }

    // ───────────── «не объединит» ─────────────

    [Fact]
    public async Task Merge_not_supported_blocks_the_group_with_readable_reasons_and_changes_nothing()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.NotSupported, s =>
        {
            s.NotSupportedReasons.Clear();
            s.NotSupportedReasons.AddRange(["has_variants", "has_sale_units", "mixed_units", "has_shade_batches"]);
        });
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("blocked");
        result.Reasons.Should().BeEquivalentTo(["has_variants", "has_sale_units", "mixed_units", "has_shade_batches"]);
        result.Message.Should().Contain("варианты (размер/цвет)").And.Contain("упаковки").And.Contain("разные единицы").And.Contain("партии-оттенки");
        server.MergeCalls.Should().ContainSingle().Which.Dry.Should().BeTrue("только проверка");
        server.Calls.Should().OnlyContain(c => c.StartsWith("read:"), "ручной путь не запускается: он сломал бы то же самое");
        MergeStore.LoadJournal().Should().BeEmpty();
        Directory.GetFiles(_dir, "merge-*.json").Should().BeEmpty("бэкап пишется только когда слияние начинается");
    }

    [Fact]
    public async Task Check_reports_server_blocked_manual_and_error_without_touching_anything()
    {
        var (_, plan, ok) = Setup(FakeMergeServer.MergeMode.Supported);
        (await new ProductMergeRunner(ok, "t", "acc").CheckAsync(plan)).Kind.Should().Be(MergeCheckKind.Server);

        var (_, plan2, blocked) = Setup(FakeMergeServer.MergeMode.NotSupported);
        var c2 = await new ProductMergeRunner(blocked, "t", "acc").CheckAsync(plan2);
        c2.Kind.Should().Be(MergeCheckKind.Blocked);
        c2.Reasons.Should().Contain("has_variants");

        var (_, plan3, old) = Setup(FakeMergeServer.MergeMode.Missing404);
        (await new ProductMergeRunner(old, "t", "acc").CheckAsync(plan3)).Kind.Should().Be(MergeCheckKind.Manual);
        old.ServerMergeSupported.Should().BeFalse();

        new[] { ok, blocked, old }.Should().OnlyContain(s => s.Calls.Count == 0 && s.StockDocs.Count == 0);
    }

    // ───────────── старый сервер: ручной режим ─────────────

    [Fact]
    public async Task Old_server_404_falls_back_to_the_manual_path_and_marks_the_journal()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Missing404);
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);

        result.Ok.Should().BeTrue(result.Message);
        server.MergeCalls.Should().ContainSingle().Which.Dry.Should().BeTrue("проба: один dry_run, дальше ручной путь");
        server.StockDocs.Should().HaveCount(2, "ручной путь: по акту остатков на источник");
        server.Cards["s1"].Archived.Should().BeTrue();
        var entry = MergeStore.LoadJournal().Single();
        entry.Mode.Should().Be("manual");
        entry.ServerMergeId.Should().BeNull();
        MergeTexts.ModeName(entry.Mode).Should().NotBe(MergeTexts.ModeName("server"));
    }

    [Fact]
    public async Task Manual_path_without_archive_is_still_prepare_only_but_a_supporting_server_does_not_need_it()
    {
        var (_, plan, old) = Setup(FakeMergeServer.MergeMode.Missing404, s => s.CanArchive = false);
        var r = await new ProductMergeRunner(old, "t", "acc").RunAsync(plan);
        r.Ok.Should().BeFalse();
        r.Message.Should().Be(MergeTexts.PrepareOnlyReason());
        old.Calls.Should().BeEmpty();

        // сервер уже определился как умеющий слияние (проба прошла): архив товаров для него не нужен
        var (_, plan2, srv) = Setup(FakeMergeServer.MergeMode.Supported, s => s.CanArchive = false);
        await srv.ServerMergeAsync("m", ["s1"], new Dictionary<string, string>(), true, null, CancellationToken.None); // проба
        var r2 = await new ProductMergeRunner(srv, "t", "acc").RunAsync(plan2);
        r2.Ok.Should().BeTrue(r2.Message);
    }

    // ───────────── идемпотентность и обрыв связи ─────────────

    [Fact]
    public async Task Lost_response_is_retried_with_the_same_key_and_merges_only_once()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported, s => s.LoseResponseTimes = 2);
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);

        result.Ok.Should().BeTrue(result.Message);
        var real = server.MergeCalls.Where(c => !c.Dry).ToList();
        real.Should().HaveCount(3, "два потерянных ответа и третий успешный");
        real.Select(c => c.Key).Distinct().Should().ContainSingle("один ключ на одно намерение");
        server.AppliedMerges.Should().Be(1, "повтор с тем же ключом не сливает второй раз");
        server.Cards["m"].Stock.Should().Be(35, "остаток перенесён один раз");
        server.Cards["m"].Alt.Should().HaveCount(4);
        MergeStore.LoadJournal().Single().ServerMergeId.Should().Be("srv-1");
    }

    [Fact]
    public async Task Connection_lost_on_every_attempt_marks_the_merge_for_manual_check_and_keeps_the_key()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported, s => s.LoseResponseTimes = 10);
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("check");
        server.MergeCalls.Count(c => !c.Dry).Should().Be(3);
        server.AppliedMerges.Should().Be(1);
        var entry = MergeStore.LoadJournal().Single();
        entry.Status.Should().Be("check");
        entry.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
        entry.Note.Should().Contain(entry.IdempotencyKey!);
        entry.CanRollback.Should().BeFalse("номера слияния нет — откатывать по нему нечем");
    }

    [Fact]
    public async Task Server_rejection_is_not_retried_and_nothing_changes()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported, s => s.RejectMerge = true);
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("failed");
        server.MergeCalls.Count(c => !c.Dry).Should().Be(1, "отказ по существу (400) не повторяется");
        server.Cards["s1"].Archived.Should().BeFalse();
        var entry = MergeStore.LoadJournal().Single();
        entry.Status.Should().Be("failed");
        entry.Sources.Should().OnlyContain(s => s.State == "failed");
    }

    // ───────────── откат ─────────────

    [Fact]
    public async Task Undo_goes_through_the_server_by_merge_id_and_restores_the_main_name_and_purchase()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported, newTitle: "Асу (вкусы)");
        var runner = new ProductMergeRunner(server, "Владелец", "acc");
        (await runner.RunAsync(plan)).Status.Should().Be("done");
        server.Cards["m"] = server.Cards["m"] with { Stock = 32 }; // 3 шт продали
        var id = MergeStore.LoadJournal().Single().Id;

        var back = await runner.RollbackAsync(id);

        back.Ok.Should().BeTrue(back.Message);
        server.UndoCalls.Should().Equal("srv-1");
        server.Calls.Should().NotContain(c => c.StartsWith("unarchive:") || c.StartsWith("stock:"), "откат делает сервер, не касса");
        server.Cards["s1"].Archived.Should().BeFalse();
        server.Cards["s1"].Barcode.Should().Be("222");
        server.Cards["s1"].Stock.Should().Be(20);
        server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901"]);
        server.Cards["m"].Name.Should().Be("Асу");
        server.Cards["m"].PurchasePrice.Should().Be(50);
        var entry = MergeStore.LoadJournal().Single();
        entry.Status.Should().Be("rolled_back");
        entry.RolledBackBy.Should().Be("Владелец");
        entry.Sources.Should().OnlyContain(s => s.State == "rolled_back");
        entry.CanRollback.Should().BeFalse();

        var again = await runner.RollbackAsync(id);
        again.Ok.Should().BeFalse("откатывать уже нечего");
        server.UndoCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task Undo_blocked_shows_the_reason_and_leaves_the_merge_as_it_was()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported);
        var runner = new ProductMergeRunner(server, "t", "acc");
        (await runner.RunAsync(plan)).Status.Should().Be("done");
        server.UndoBlockedReason = "barcode_taken";
        var id = MergeStore.LoadJournal().Single().Id;

        var back = await runner.RollbackAsync(id);

        back.Ok.Should().BeFalse();
        back.Reasons.Should().Contain("barcode_taken");
        back.Message.Should().Contain("штрихкод уже отдан другому товару");
        var entry = MergeStore.LoadJournal().Single();
        entry.Status.Should().Be("done", "откат запрещён — слияние осталось");
        entry.CanRollback.Should().BeTrue("можно попробовать позже, когда причину устранят");
        server.Cards["s1"].Archived.Should().BeTrue();
        entry.Note.Should().Contain("штрихкод уже отдан другому товару");

        server.UndoBlockedReason = null;
        (await runner.RollbackAsync(id)).Ok.Should().BeTrue("причину убрали — откат проходит");
    }

    [Fact]
    public async Task Undo_of_an_expired_merge_says_30_days()
    {
        var (_, plan, server) = Setup(FakeMergeServer.MergeMode.Supported);
        (await new ProductMergeRunner(server, "t", "acc").RunAsync(plan)).Status.Should().Be("done");
        var id = MergeStore.LoadJournal().Single().Id;

        // другой сервер слияния «не знает» (срок вышел)
        var forgetful = new FakeMergeServer { ServerMode = FakeMergeServer.MergeMode.Supported };
        var back = await new ProductMergeRunner(forgetful, "t", "acc").RollbackAsync(id);

        back.Ok.Should().BeFalse();
        back.Message.Should().Contain("30 суток");
        MergeStore.LoadJournal().Single().Status.Should().Be("done");
    }

    [Fact]
    public void Old_journal_entries_without_mode_are_manual_and_keep_their_rollback()
    {
        var entry = new MergeJournalEntry { Id = "old", Status = "done", Sources = [new MergeJournalSource { Id = "s1", State = "done" }] };
        entry.IsServer.Should().BeFalse();
        entry.CanRollback.Should().BeTrue();
        var json = JsonSerializer.Serialize(entry);
        JsonSerializer.Deserialize<MergeJournalEntry>(json)!.Mode.Should().BeEmpty();
        var server = new MergeJournalEntry { Id = "n", Status = "done", Mode = "server", Sources = [new MergeJournalSource { Id = "s1", State = "done" }] };
        server.CanRollback.Should().BeFalse("серверная запись без merge_id");
    }

    // ───────────── тексты ─────────────

    [Fact]
    public void Server_texts_exist_in_all_languages_and_reasons_are_translated()
    {
        var prefs = UserPreferences.Instance;
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
        {
            prefs.Language = lang;
            MergeTexts.ManualModeBanner().Should().NotBeNullOrWhiteSpace(lang.ToString());
            MergeTexts.ServerCheckPending().Should().NotBeNullOrWhiteSpace();
            MergeTexts.ServerCheckOk().Should().NotBeNullOrWhiteSpace();
            MergeTexts.ServerCheckBlockedChip().Should().NotBeNullOrWhiteSpace();
            MergeTexts.ModeName("server").Should().NotBeNullOrWhiteSpace();
            MergeTexts.ModeName("manual").Should().NotBeNullOrWhiteSpace();
            MergeTexts.ServerBlocked("Асу", ["has_variants"]).Should().Contain("Асу");
            foreach (var raw in new[] { "has_variants", "shade_batches", "sale_units", "unit_mismatch", "barcode_taken", "not_enough_stock" })
                MergeTexts.ServerReason(raw).Should().NotBe(raw, $"{raw} {lang}");
            foreach (var kind in new[] { ServerMergeKind.UndoBlocked, ServerMergeKind.NotFound, ServerMergeKind.Unavailable })
                MergeTexts.UndoRefused(new ServerMergeResponse(kind) { Reasons = ["barcode_taken"] }).Should().NotBeNullOrWhiteSpace();
        }
        prefs.Language = AppLanguage.Russian;
        MergeTexts.ServerReason("что-то новое от сервера").Should().Be("что-то новое от сервера", "неизвестную причину показываем как прислал сервер");
    }

    // ───────────── контракт HTTP (WireMock) ─────────────

    private static async Task<(FakeNurCrmServer Server, ProductMergeApi Api)> ApiAsync()
    {
        var server = new FakeNurCrmServer();
        var client = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await client.LoginAsync("cashier@test.kg", "12345678");
        return (server, new ProductMergeApi(client));
    }

    private static WireMock.Logging.ILogEntry LastMerge(FakeNurCrmServer s, string path = "/api/main/products/merge/") =>
        s.Server.LogEntries.Last(e => e.RequestMessage.Path == path);

    [Fact]
    public async Task Api_dry_run_sends_target_sources_and_variant_names_without_a_key()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Json("POST", "/api/main/products/merge/", 200, "{}");
            var r = await api.MergeAsync("5", ["6", "7"], new Dictionary<string, string> { ["6"] = "Клубничный", ["7"] = "  " }, true, null, CancellationToken.None);

            r.Kind.Should().Be(ServerMergeKind.Ok);
            ProductMergeApi.Supported.Should().BeTrue();
            var entry = LastMerge(server);
            entry.RequestMessage.Headers!.ContainsKey("Idempotency-Key").Should().BeFalse("dry_run без ключа");
            using var body = JsonDocument.Parse(entry.RequestMessage.Body!);
            body.RootElement.GetProperty("target").GetInt64().Should().Be(5, "числовой id уходит числом");
            body.RootElement.GetProperty("sources").EnumerateArray().Select(x => x.GetInt64()).Should().Equal(6, 7);
            body.RootElement.GetProperty("dry_run").GetBoolean().Should().BeTrue();
            var names = body.RootElement.GetProperty("variant_names");
            names.GetProperty("6").GetString().Should().Be("Клубничный");
            names.TryGetProperty("7", out _).Should().BeFalse("пустое название не шлём — сервер возьмёт название источника");
        }
    }

    [Fact]
    public async Task Api_real_merge_sends_the_idempotency_key_and_reads_the_answer()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Json("POST", "/api/main/products/merge/", 200,
                """{"merge_id":"m-77","moved_barcodes":["222","902"],"moved_qty":"25.5","stock_document":{"id":"doc-9"},"archived":["6","7"],"replayed":true}""");
            var r = await api.MergeAsync("5", ["6", "7"], new Dictionary<string, string>(), false, "merge-key-1", CancellationToken.None);

            r.Kind.Should().Be(ServerMergeKind.Ok);
            r.MergeId.Should().Be("m-77");
            r.MovedBarcodes.Should().Be(2);
            r.MovedQty.Should().Be(25.5);
            r.StockDocument.Should().Be("doc-9");
            r.ArchivedIds.Should().Equal("6", "7");
            r.Replayed.Should().BeTrue();
            var entry = LastMerge(server);
            entry.RequestMessage.Headers!["Idempotency-Key"].Should().ContainSingle().Which.Should().Be("merge-key-1");
            using var body = JsonDocument.Parse(entry.RequestMessage.Body!);
            body.RootElement.TryGetProperty("dry_run", out _).Should().BeFalse();
            body.RootElement.TryGetProperty("variant_names", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task Api_real_merge_without_a_key_is_refused_before_sending()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            var act = () => api.MergeAsync("5", ["6"], new Dictionary<string, string>(), false, null, CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>();
            server.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Path == "/api/main/products/merge/");
        }
    }

    [Fact]
    public async Task Api_409_merge_not_supported_returns_the_reasons_in_any_shape()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Json("POST", "/api/main/products/merge/", 409,
                """{"code":"merge_not_supported","detail":"not supported","reasons":["has_variants",{"code":"has_sale_units","product":6},{"product":7,"reasons":["unit_mismatch"]}]}""");
            var r = await api.MergeAsync("5", ["6", "7"], new Dictionary<string, string>(), true, null, CancellationToken.None);

            r.Kind.Should().Be(ServerMergeKind.NotSupported);
            r.Reasons.Should().BeEquivalentTo(["has_variants", "has_sale_units", "unit_mismatch"]);
            ProductMergeApi.Supported.Should().BeTrue("сервер ответил по существу — слияние есть");
        }
    }

    [Fact]
    public async Task Api_other_409_is_not_mistaken_for_not_supported()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Json("POST", "/api/main/products/merge/", 409, """{"code":"barcode_taken","detail":"код занят"}""");
            var act = () => api.MergeAsync("5", ["6"], new Dictionary<string, string>(), true, null, CancellationToken.None);
            (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(409);
        }
    }

    [Fact]
    public async Task Api_404_without_json_means_old_server_and_is_remembered_for_the_session()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Server.Given(Request.Create().WithPath("/api/main/products/merge/").UsingPost())
                .RespondWith(Response.Create().WithStatusCode(404).WithHeader("Content-Type", "text/html").WithBody("<html>Not Found</html>"));
            var first = await api.MergeAsync("5", ["6"], new Dictionary<string, string>(), true, null, CancellationToken.None);
            first.Kind.Should().Be(ServerMergeKind.Unavailable);
            ProductMergeApi.Supported.Should().BeFalse();

            var before = server.Server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/products/merge/");
            var second = await api.MergeAsync("5", ["6"], new Dictionary<string, string>(), true, null, CancellationToken.None);
            second.Kind.Should().Be(ServerMergeKind.Unavailable);
            server.Server.LogEntries.Count(e => e.RequestMessage.Path == "/api/main/products/merge/").Should().Be(before, "результат пробы кэширован на сессию — запрос не повторяется");
        }
    }

    [Fact]
    public async Task Api_405_also_means_old_server()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Json("POST", "/api/main/products/merge/", 405, """{"detail":"Method not allowed"}""");
            (await api.MergeAsync("5", ["6"], new Dictionary<string, string>(), true, null, CancellationToken.None)).Kind.Should().Be(ServerMergeKind.Unavailable);
            ProductMergeApi.Supported.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Api_404_with_json_body_is_a_missing_product_not_a_missing_endpoint()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Json("POST", "/api/main/products/merge/", 404, """{"detail":"Not found."}""");
            (await api.MergeAsync("5", ["6"], new Dictionary<string, string>(), true, null, CancellationToken.None)).Kind.Should().Be(ServerMergeKind.Unavailable);
            ProductMergeApi.Supported.Should().BeNull("так можно отравить память сессии из-за одного удалённого товара — не кэшируем");
        }
    }

    [Fact]
    public async Task Api_server_error_is_thrown_so_the_runner_can_retry_with_the_same_key()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Error("POST", "/api/main/products/merge/", 503);
            var act = () => api.MergeAsync("5", ["6"], new Dictionary<string, string>(), false, "k", CancellationToken.None);
            (await act.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(503);
        }
    }

    [Fact]
    public async Task Api_undo_posts_to_the_merge_id_and_maps_blocked_and_not_found()
    {
        var (server, api) = await ApiAsync();
        using (server)
        {
            server.Json("POST", "/api/main/products/merge/m-1/undo/", 200, """{"merge_id":"m-1","restored":2}""");
            var ok = await api.UndoAsync("m-1", CancellationToken.None);
            ok.Kind.Should().Be(ServerMergeKind.Ok);
            LastMerge(server, "/api/main/products/merge/m-1/undo/").RequestMessage.Headers!.ContainsKey("Idempotency-Key").Should().BeTrue();

            server.Json("POST", "/api/main/products/merge/m-2/undo/", 409, """{"code":"undo_blocked","reasons":["barcode_taken","not_enough_stock"]}""");
            var blocked = await api.UndoAsync("m-2", CancellationToken.None);
            blocked.Kind.Should().Be(ServerMergeKind.UndoBlocked);
            blocked.Reasons.Should().BeEquivalentTo(["barcode_taken", "not_enough_stock"]);

            server.Json("POST", "/api/main/products/merge/m-3/undo/", 404, """{"detail":"Not found."}""");
            (await api.UndoAsync("m-3", CancellationToken.None)).Kind.Should().Be(ServerMergeKind.NotFound);
        }
    }
}
