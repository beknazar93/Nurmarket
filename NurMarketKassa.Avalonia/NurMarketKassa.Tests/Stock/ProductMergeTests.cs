using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.ProductMerge;

namespace NurMarketKassa.Tests.Stock;

/// <summary>2026-11-21 («Найти дубли и варианты»): группировка («Асу» / «Асу клубника» / «Асу лимон»; разные цены, объёмы, бренды, весовые, пустые бренды), предпросмотр (остатки, средневзвешенная закупка,
/// слияние доп. штрихкодов), выполнение и откат на заглушке сервера (уникальность штрихкодов как у NurCRM: 409 barcode_taken), журнал, отказы, «только подготовка».</summary>
[CollectionDefinition("MergeStore", DisableParallelization = true)]
public sealed class MergeStoreCollection;

[Collection("MergeStore")]
public sealed class ProductMergeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "merge-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public ProductMergeTests()
    {
        Directory.CreateDirectory(_dir);
        MergeStore.DirOverride = _dir;
        UserPreferences.Instance.Language = AppLanguage.Russian;
    }

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        MergeStore.DirOverride = null;
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    private static int _seq;

    private static MergeProduct P(string title, double price = 100, string? brand = "Асу", string? category = "Напитки", string? barcode = null, double qty = 0, double purchase = 0,
        string? unit = "шт", bool weight = false, string? kind = "product", bool expiry = false, params MergeAltCode[] alt) =>
        new("p" + (++_seq), title, brand, category, price, barcode ?? "46" + (1000000 + _seq), alt, unit, weight, kind, qty, purchase, expiry, null);

    // ───────────── группировка ─────────────

    [Fact]
    public void Asu_three_flavours_become_one_group_with_variant_names()
    {
        var plain = P("Асу", qty: 10);
        var straw = P("Асу клубника", qty: 4);
        var lemon = P("Асу лимон", qty: 6);
        var groups = MergeCandidateFinder.Find([plain, straw, lemon]);
        groups.Should().HaveCount(1);
        var g = groups[0];
        g.Members.Should().HaveCount(3);
        g.BaseTitle.Should().Be("Асу");
        g.ByDictionary.Should().BeTrue();
        g.Confidence.Should().Be(MergeConfidence.High);
        g.Members.Select(m => m.VariantName).Should().BeEquivalentTo(["", "Клубника", "Лимон"]);
        g.Warnings.Should().BeEmpty();
        MergePlanner.DefaultDraft(g).MainId.Should().Be(plain.Id, "главный по умолчанию — товар без отличительного слова");
        MergeTexts.Reason(g).Should().Contain("Асу").And.Contain("Клубника");
    }

    [Fact]
    public void Adjective_forms_and_colours_are_recognised()
    {
        var groups = MergeCandidateFinder.Find([P("Футболка Nike красная"), P("Футболка Nike синяя"), P("Футболка Nike")]);
        groups.Should().HaveCount(1);
        groups[0].Members.Select(m => m.VariantName).Should().Contain(["Красная", "Синяя"]);
        MergeCandidateFinder.Find([P("Чай со вкусом лимона"), P("Чай с мятой")]).Should().HaveCount(1, "«со вкусом» и «с» — шум, не часть основы");
    }

    [Fact]
    public void Different_prices_are_flagged_and_lower_the_score()
    {
        var same = MergeCandidateFinder.Find([P("Асу", price: 100), P("Асу клубника", price: 100)])[0];
        var diff = MergeCandidateFinder.Find([P("Асу", price: 100), P("Асу клубника", price: 120)])[0];
        diff.PricesDiffer.Should().BeTrue();
        diff.Warnings.Should().Contain(MergeWarning.PricesDiffer);
        diff.Score.Should().BeLessThan(same.Score);
        same.PricesDiffer.Should().BeFalse();
        MergeCandidateFinder.SamePrice(100, 100.4).Should().BeTrue("допуск 0,5 %");
        MergeCandidateFinder.SamePrice(100, 101).Should().BeFalse();
    }

    [Fact]
    public void Different_volume_is_never_grouped_but_the_same_volume_written_differently_is()
    {
        MergeCandidateFinder.Find([P("Асу 0,5 л"), P("Асу клубника 1 л")]).Should().BeEmpty("0,5 л и 1 л — разные товары");
        MergeCandidateFinder.Find([P("Асу 0,5 л"), P("Асу лимон 1,5 л")]).Should().BeEmpty();
        var g = MergeCandidateFinder.Find([P("Асу 0,5 л"), P("Асу клубника 500 мл")]);
        g.Should().HaveCount(1, "0,5 л = 500 мл");
        g[0].BaseTitle.Should().Contain("0,5 л");
        MergeCandidateFinder.Find([P("Молоко 3,2%"), P("Молоко 2,5%")]).Should().BeEmpty("процент — часть товара");
    }

    [Fact]
    public void Different_brands_are_not_grouped_and_empty_brand_joins_the_only_brand()
    {
        MergeCandidateFinder.Find([P("Вода лимон", brand: "Асу"), P("Вода клубника", brand: "Бон Аква")]).Should().BeEmpty();
        var g = MergeCandidateFinder.Find([P("Асу лимон", brand: "Асу"), P("Асу клубника", brand: null)]);
        g.Should().HaveCount(1);
        g[0].Warnings.Should().Contain(MergeWarning.BrandMissing);
    }

    [Fact]
    public void Empty_brands_still_group_but_with_a_lower_score()
    {
        var withBrand = MergeCandidateFinder.Find([P("Асу лимон"), P("Асу клубника")])[0];
        var noBrand = MergeCandidateFinder.Find([P("Асу лимон", brand: null), P("Асу клубника", brand: null)])[0];
        noBrand.Score.Should().BeLessThan(withBrand.Score);
        noBrand.Warnings.Should().Contain(MergeWarning.BrandMissing);
    }

    [Fact]
    public void Weighed_products_and_other_units_get_blockers_and_are_excluded_by_default()
    {
        var w = MergeCandidateFinder.Find([P("Конфеты шоколад", unit: "кг", weight: true), P("Конфеты карамель", unit: "кг", weight: true)])[0];
        w.Members.Should().OnlyContain(m => m.Blockers.Contains(MergeBlocker.Weighed));
        w.MergeableCount.Should().Be(0);

        var u = MergeCandidateFinder.Find([P("Сок яблоко", unit: "шт"), P("Сок вишня", unit: "шт"), P("Сок персик", unit: "кг")])[0];
        u.Members.Single(m => m.Product.Unit == "кг").Blockers.Should().Contain(MergeBlocker.OtherUnit);
        var draft = MergePlanner.DefaultDraft(u);
        draft.Excluded.Should().Contain(u.Members.Single(m => m.Product.Unit == "кг").Product.Id);
        MergePlanner.Build(u, draft).Variants.Should().HaveCount(1, "кг-товар в слияние не попадает");
    }

    [Fact]
    public void Services_bundles_and_products_without_barcode_are_not_candidates()
    {
        var noCode = new MergeProduct("x1", "Асу лимон", "Асу", "Напитки", 100, null, [], "шт", false, "product", 0, 0, false, null);
        MergeCandidateFinder.Find([P("Асу"), P("Асу клубника", kind: "service"), P("Асу лимон", kind: "bundle"), noCode]).Should().BeEmpty();
    }

    [Fact]
    public void A_lone_flavour_word_is_not_a_base_and_unrelated_products_do_not_group()
    {
        MergeCandidateFinder.Find([P("Клубника"), P("Лимон")]).Should().BeEmpty();
        MergeCandidateFinder.Find([P("Асу"), P("Хлеб"), P("Молоко")]).Should().BeEmpty();
    }

    [Fact]
    public void Kyrgyz_uzbek_and_english_flavours_are_recognised()
    {
        MergeCandidateFinder.Find([P("Шире алма"), P("Шире лимон")]).Should().HaveCount(1);
        MergeCandidateFinder.Find([P("Sharbat olma"), P("Sharbat limon")]).Should().HaveCount(1);
        MergeCandidateFinder.Find([P("Soda strawberry"), P("Soda lemon"), P("Soda")]).Should().HaveCount(1);
    }

    [Fact]
    public void Same_title_with_different_barcodes_is_a_duplicate_group()
    {
        var g = MergeCandidateFinder.Find([P("Сгущёнка 380 г", brand: "Рогачёв"), P("Сгущёнка 380 г", brand: "Рогачёв")]);
        g.Should().HaveCount(1);
        g[0].ByDictionary.Should().BeFalse();
        MergeTexts.Reason(g[0]).Should().Contain("Сгущёнка");
    }

    [Fact]
    public void Common_name_start_groups_unknown_tails_only_with_same_price()
    {
        var yes = MergeCandidateFinder.Find([P("Печенье Юбилейное Классик", price: 90), P("Печенье Юбилейное Кунжут", price: 90)]);
        yes.Should().HaveCount(1);
        yes[0].ByDictionary.Should().BeFalse();
        yes[0].BaseTitle.Should().Be("Печенье Юбилейное");
        yes[0].Members.Select(m => m.VariantName).Should().BeEquivalentTo(["Классик", "Кунжут"]);
        yes[0].Confidence.Should().NotBe(MergeConfidence.High, "общее начало без словаря — не «уверенно»: на «Отметить уверенные» такие группы не попадают");
        MergeCandidateFinder.Find([P("Печенье Юбилейное Классик", price: 90), P("Печенье Юбилейное Кунжут", price: 140)]).Should().BeEmpty("в слабой группе цены обязаны совпасть");
    }

    [Fact]
    public void Groups_are_sorted_by_confidence_and_ids_are_stable()
    {
        var a = P("Асу");
        var b = P("Асу лимон");
        var g1 = MergeCandidateFinder.Find([a, b]);
        var g2 = MergeCandidateFinder.Find([b, a]);
        g1[0].Id.Should().Be(g2[0].Id);
        MergeCandidateFinder.GroupId(["b", "a"]).Should().Be(MergeCandidateFinder.GroupId(["a", "b"]));
    }

    // ───────────── предпросмотр ─────────────

    [Fact]
    public void Preview_sums_stock_averages_purchase_and_merges_codes()
    {
        var plain = P("Асу", qty: 10, purchase: 50, barcode: "111", alt: new MergeAltCode("901", "Мята", 0));
        var straw = P("Асу клубника", qty: 20, purchase: 80, barcode: "222", alt: new MergeAltCode("902", null, 6));
        var lemon = P("Асу лимон", qty: 0, purchase: 0, barcode: "333");
        var g = MergeCandidateFinder.Find([plain, straw, lemon])[0];
        var plan = MergePlanner.Build(g, MergePlanner.DefaultDraft(g));
        plan.CanRun.Should().BeTrue();
        plan.Main!.Id.Should().Be(plain.Id);
        plan.TotalStock.Should().Be(30);
        plan.NewPurchase.Should().BeApproximately((10 * 50 + 20 * 80) / 30.0, 0.0001, "средневзвешенная по остаткам; пустой источник не участвует");
        plan.OldPurchase.Should().Be(50);
        plan.FinalAltCodes.Select(a => a.Barcode).Should().BeEquivalentTo(["901", "222", "902", "333"]);
        plan.FinalAltCodes.Single(a => a.Barcode == "222").Name.Should().Be("Клубника");
        plan.FinalAltCodes.Single(a => a.Barcode == "902").Name.Should().Be("Клубника", "у чужого доп. кода без названия — название варианта");
        plan.FinalAltCodes.Single(a => a.Barcode == "902").Quantity.Should().Be(6);
        plan.FinalAltCodes.Single(a => a.Barcode == "901").Name.Should().Be("Мята", "прежние коды главного не трогаем");
        MergeTexts.StockLine(plan).Should().Contain("30");
    }

    [Fact]
    public void Purchase_is_left_alone_when_a_stocked_product_has_none()
    {
        var a = P("Асу", qty: 10, purchase: 50);
        var b = P("Асу лимон", qty: 5, purchase: 0);
        var g = MergeCandidateFinder.Find([a, b])[0];
        var plan = MergePlanner.Build(g, MergePlanner.DefaultDraft(g));
        plan.NewPurchase.Should().BeNull();
        plan.PurchaseUnknown.Should().BeTrue();
    }

    [Fact]
    public void Excluding_a_member_and_choosing_another_main_change_the_plan_and_rename_the_main()
    {
        var plain = P("Асу", qty: 1);
        var straw = P("Асу клубника", qty: 9);
        var lemon = P("Асу лимон", qty: 2);
        var g = MergeCandidateFinder.Find([plain, straw, lemon])[0];
        var draft = MergePlanner.DefaultDraft(g);
        draft.Excluded.Add(lemon.Id);
        MergePlanner.Build(g, draft).Variants.Select(v => v.Source.Id).Should().BeEquivalentTo([straw.Id]);

        MergePlanner.SetMain(g, draft, straw.Id);
        var plan = MergePlanner.Build(g, draft);
        plan.Main!.Id.Should().Be(straw.Id);
        plan.NewMainTitle.Should().Be("Асу", "в названии главного остался вкус — карточка получает название основы");
        plan.Variants.Select(v => v.Source.Id).Should().BeEquivalentTo([plain.Id]);

        draft.Excluded.Add(plain.Id);
        MergePlanner.Build(g, draft).Problem.Should().Be(MergeProblem.NothingToMerge);
        MergeTexts.Problem(MergeProblem.NothingToMerge).Should().NotBeEmpty();
    }

    [Fact]
    public void Variant_names_are_editable_and_duplicate_codes_are_not_repeated()
    {
        var a = P("Асу", barcode: "111");
        var b = P("Асу лимон", barcode: "222", alt: new MergeAltCode("111", null, 0));
        var g = MergeCandidateFinder.Find([a, b])[0];
        var draft = MergePlanner.DefaultDraft(g);
        draft.VariantNames[b.Id] = "Лимонный";
        var plan = MergePlanner.Build(g, draft);
        plan.FinalAltCodes.Select(x => x.Barcode).Should().BeEquivalentTo(["222"], "код 111 уже основной у главного");
        plan.FinalAltCodes[0].Name.Should().Be("Лимонный");
    }

    [Fact]
    public void Texts_exist_in_all_languages()
    {
        var prefs = UserPreferences.Instance;
        var was = prefs.Language;
        try
        {
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
            {
                prefs.Language = lang;
                foreach (var b in Enum.GetValues<MergeBlocker>())
                    MergeTexts.Blocker(b).Should().NotBeNullOrWhiteSpace($"{b} {lang}");
                foreach (var w in Enum.GetValues<MergeWarning>())
                    MergeTexts.Warning(w).Should().NotBeNullOrWhiteSpace($"{w} {lang}");
                foreach (var c in Enum.GetValues<MergeConfidence>())
                    MergeTexts.Confidence(c).Should().NotBeNullOrWhiteSpace($"{c} {lang}");
                MergeTexts.PrepareOnlyReason().Should().NotBeNullOrWhiteSpace();
                MergeTexts.OfflineReason().Should().NotBeNullOrWhiteSpace();
                MergeTexts.NoRightsReason().Should().NotBeNullOrWhiteSpace();
            }
        }
        finally
        {
            prefs.Language = was;
        }
    }

    // ───────────── выполнение и откат на заглушке ─────────────

    private static MergeCard Card(string id, string name, string barcode, double stock, double? purchase = null, params MergeAltCode[] alt) =>
        new(id, name, barcode, alt.ToList(), stock, 100, purchase, "Напитки", "Асу", "шт", ["http://img/" + id + ".jpg"], false);

    private static (MergeGroup Group, MergePlan Plan, FakeMergeServer Server) Setup(Action<FakeMergeServer>? tune = null)
    {
        var server = new FakeMergeServer();
        server.Add(Card("m", "Асу", "111", 10, 50, new MergeAltCode("901", "Мята", 0)));
        server.Add(Card("s1", "Асу клубника", "222", 20, 80, new MergeAltCode("902", null, 6)));
        server.Add(Card("s2", "Асу лимон", "333", 5, 50));
        tune?.Invoke(server);
        var products = server.Cards.Values.Select(c => new MergeProduct(c.Id, c.Name, "Асу", "Напитки", 100, c.Barcode, c.Alt, "шт", false, "product", c.Stock, c.PurchasePrice ?? 0, false, null)).ToList();
        var g = MergeCandidateFinder.Find(products).Single();
        var draft = MergePlanner.DefaultDraft(g);
        draft.MainId.Should().Be("m");
        return (g, MergePlanner.Build(g, draft), server);
    }

    [Fact]
    public async Task Run_moves_codes_and_stock_archives_sources_and_writes_backup_and_journal()
    {
        var (_, plan, server) = Setup();
        var result = await new ProductMergeRunner(server, "Тест Владелец", "acc").RunAsync(plan);

        result.Ok.Should().BeTrue(result.Message);
        result.Status.Should().Be("done");
        var main = server.Cards["m"];
        main.Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901", "222", "902", "333"]);
        main.Alt.Single(a => a.Barcode == "222").Name.Should().Be("Клубника");
        main.Stock.Should().Be(35);
        main.PurchasePrice.Should().BeApproximately((10 * 50 + 20 * 80 + 5 * 50) / 35.0, 0.0001);
        server.Cards["s1"].Archived.Should().BeTrue();
        server.Cards["s1"].Stock.Should().Be(0);
        server.Cards["s1"].Barcode.Should().BeNullOrEmpty("коды источника освобождены");
        server.Cards["s2"].Archived.Should().BeTrue();
        server.StockDocs.Should().HaveCount(2, "по одному акту на источник");
        server.StockDocs[0].Note.Should().Contain("Объединение товаров");

        var entry = MergeStore.LoadJournal("acc").Single();
        entry.Status.Should().Be("done");
        entry.Who.Should().Be("Тест Владелец");
        entry.Sources.Should().OnlyContain(s => s.State == "done" && s.Archived);
        entry.Sources.Single(s => s.Id == "s1").MovedStock.Should().Be(20);
        var backup = MergeStore.LoadBackup(entry.Id)!;
        backup.Cards.Select(c => c.Id).Should().BeEquivalentTo(["m", "s1", "s2"]);
        backup.Cards.Single(c => c.Id == "s1").Barcode.Should().Be("222");
        backup.Cards.Single(c => c.Id == "s1").Images.Should().ContainSingle();
        File.Exists(Path.ChangeExtension(MergeStore.BackupPath(entry.Id), ".csv")).Should().BeTrue();
        File.ReadAllText(Path.ChangeExtension(MergeStore.BackupPath(entry.Id), ".csv")).Should().Contain("222").And.Contain("Асу клубника");
    }

    [Fact]
    public async Task Backup_failure_stops_the_merge_before_any_change()
    {
        var (_, plan, server) = Setup();
        // «папка» бэкапов — на самом деле файл: записать нельзя
        var blocked = Path.Combine(_dir, "blocked");
        File.WriteAllText(blocked, "x");
        MergeStore.DirOverride = blocked;
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);
        result.Ok.Should().BeFalse();
        server.Calls.Should().NotContain(c => c.StartsWith("patch") || c.StartsWith("stock") || c.StartsWith("archive"), "до бэкапа на сервере ничего не меняется");
        server.Cards["s1"].Barcode.Should().Be("222");
    }

    [Fact]
    public async Task Cannot_run_without_server_archive_prepare_only()
    {
        var (_, plan, server) = Setup(s => s.CanArchive = false);
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);
        result.Ok.Should().BeFalse();
        result.Message.Should().Be(MergeTexts.PrepareOnlyReason());
        server.Calls.Should().BeEmpty();
        MergeStore.LoadJournal().Should().BeEmpty();
    }

    [Fact]
    public async Task Failure_while_giving_codes_to_the_main_restores_that_source_and_stops()
    {
        // главный уже «занят» кодом 902? — нет: имитируем отказ сервера при записи доп. кодов главного (barcode_taken)
        var (_, plan, server) = Setup(s => s.FailPatchOfMainWith = "barcode_taken: уже есть у товара «Чужой»");
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);
        result.Ok.Should().BeFalse();
        result.Status.Should().Be("failed");
        server.Cards["s1"].Barcode.Should().Be("222", "источник возвращён как был");
        server.Cards["s1"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["902"]);
        server.Cards["s1"].Archived.Should().BeFalse();
        server.Cards["s2"].Barcode.Should().Be("333", "остальные источники не тронуты");
        server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901"]);
        server.Cards["m"].Stock.Should().Be(10);
        result.Message.Should().Contain("не объединён");
        var entry = MergeStore.LoadJournal().Single();
        entry.Sources.Select(s => s.State).Should().Equal("failed", "pending");
    }

    [Fact]
    public async Task Failure_in_the_stock_step_reverts_codes_of_that_source_but_keeps_earlier_ones()
    {
        var (_, plan, server) = Setup(s => s.FailStockFor = "s2");
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);
        result.Ok.Should().BeFalse();
        result.Status.Should().Be("partial");
        // первым идёт источник с большим остатком (s1) — он слит; s2 — отказ на остатке, его коды возвращены
        server.Cards["s1"].Archived.Should().BeTrue();
        server.Cards["s2"].Barcode.Should().Be("333");
        server.Cards["s2"].Archived.Should().BeFalse();
        server.Cards["s2"].Stock.Should().Be(5);
        server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901", "222", "902"]);
        server.Cards["m"].Stock.Should().Be(30);
        MergeStore.LoadJournal().Single().Status.Should().Be("partial");
    }

    [Fact]
    public async Task Archive_failure_does_not_undo_the_merge_but_is_recorded()
    {
        var (_, plan, server) = Setup(s => s.FailArchive = true);
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);
        result.Status.Should().Be("done");
        server.Cards["m"].Alt.Should().HaveCount(4);
        MergeStore.LoadJournal().Single().Sources.Should().OnlyContain(s => s.State == "done" && !s.Archived && s.Error != null);
    }

    [Fact]
    public async Task Unknown_outcome_of_the_stock_document_is_marked_for_manual_check_and_nothing_is_reverted()
    {
        var (_, plan, server) = Setup(s => s.UnknownStockFor = "s1");
        var result = await new ProductMergeRunner(server, "t", "acc").RunAsync(plan);
        result.Status.Should().Be("check");
        result.Ok.Should().BeFalse();
        server.Cards["m"].Alt.Select(a => a.Barcode).Should().Contain("222");
        MergeStore.LoadJournal().Single().Sources.Single(s => s.Id == "s1").State.Should().Be("check");
    }

    [Fact]
    public async Task Rollback_restores_sources_codes_stock_name_and_purchase()
    {
        var (g, _, server) = Setup();
        var draft = MergePlanner.DefaultDraft(g);
        draft.NewMainTitle = "Асу (вкусы)";
        var plan = MergePlanner.Build(g, draft);
        var runner = new ProductMergeRunner(server, "Владелец", "acc");
        (await runner.RunAsync(plan)).Status.Should().Be("done");
        server.Cards["m"].Name.Should().Be("Асу (вкусы)");
        // за это время на главном продали 3 шт
        server.Cards["m"] = server.Cards["m"] with { Stock = 32 };

        var id = MergeStore.LoadJournal().Single().Id;
        var back = await runner.RollbackAsync(id);

        back.Ok.Should().BeTrue(back.Message);
        server.Cards["s1"].Archived.Should().BeFalse();
        server.Cards["s1"].Barcode.Should().Be("222");
        server.Cards["s1"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["902"]);
        server.Cards["s1"].Stock.Should().Be(20);
        server.Cards["s2"].Stock.Should().Be(5);
        server.Cards["s2"].Barcode.Should().Be("333");
        server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901"]);
        server.Cards["m"].Stock.Should().Be(7, "было 10 + 25 перенесено − 3 проданных; вернули 25 источникам");
        server.Cards["m"].Name.Should().Be("Асу");
        server.Cards["m"].PurchasePrice.Should().Be(50);
        var entry = MergeStore.LoadJournal().Single();
        entry.Status.Should().Be("rolled_back");
        entry.RolledBackBy.Should().Be("Владелец");
        entry.Sources.Should().OnlyContain(s => s.State == "rolled_back");

        // повторный откат ничего не делает и не ломается
        var again = await runner.RollbackAsync(id);
        again.Ok.Should().BeFalse("откатывать уже нечего");
        server.Cards["m"].Stock.Should().Be(7);
    }

    [Fact]
    public async Task Rollback_after_partial_run_returns_only_the_merged_source()
    {
        var (_, plan, server) = Setup(s => s.FailStockFor = "s2");
        var runner = new ProductMergeRunner(server, "t", "acc");
        (await runner.RunAsync(plan)).Status.Should().Be("partial");
        var id = MergeStore.LoadJournal().Single().Id;
        (await runner.RollbackAsync(id)).Ok.Should().BeTrue();
        server.Cards["s1"].Barcode.Should().Be("222");
        server.Cards["s1"].Stock.Should().Be(20);
        server.Cards["m"].Stock.Should().Be(10);
        server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901"]);
    }

    [Fact]
    public async Task Rollback_without_backup_says_so()
    {
        var (_, _, server) = Setup();
        var result = await new ProductMergeRunner(server, "t", "acc").RollbackAsync("нет-такого");
        result.Ok.Should().BeFalse();
        server.Calls.Should().BeEmpty();
    }

    // ───────────── отказы, журнал, CSV ─────────────

    [Fact]
    public void Declined_groups_are_remembered_and_can_be_restored()
    {
        var id = MergeCandidateFinder.GroupId(["a", "b"]);
        MergeStore.Declined().Should().BeEmpty();
        MergeStore.SetDeclined(id, true);
        MergeStore.Declined().Should().Contain(id);
        MergeStore.SetDeclined(id, false);
        MergeStore.Declined().Should().BeEmpty();
    }

    [Fact]
    public void Plan_export_writes_a_csv_with_one_row_per_source()
    {
        var a = P("Асу", barcode: "111", qty: 3);
        var b = P("Асу лимон", barcode: "222", qty: 2);
        var c = P("Асу клубника", barcode: "333");
        var g = MergeCandidateFinder.Find([a, b, c])[0];
        var draft = MergePlanner.DefaultDraft(g);
        draft.VariantNames[c.Id] = "Клуб;ника \"х\"";
        var path = Path.Combine(_dir, "plan.csv");
        MergeStore.ExportPlanCsv([MergePlanner.Build(g, draft)], path);
        var lines = File.ReadAllLines(path);
        lines.Should().HaveCount(3, "заголовок + два источника");
        lines[0].Should().Contain("главный штрихкод");
        File.ReadAllText(path).Should().Contain("222").And.Contain("333").And.Contain("\"Клуб;ника \"\"х\"\"\"", "разделитель и кавычки экранируются");
    }
}
