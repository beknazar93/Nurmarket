using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.ProductMerge;
using NurMarketKassa.Tests.Stock;
using NurMarketKassa.UiTests.Harness;
using SkiaSharp;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-21 («Найти дубли и варианты»): окно «Объединение товаров» на заглушке сервера — список групп, состав, «Предпросмотр», отметки и массовое «Объединить», «Объединять не надо»,
/// режим «только подготовка» (нет архива/нет прав/нет связи), CSV, журнал и откат. 1024×600 и 1366×768, ru/ky/uz: текст не обрезан, поля не налезают, окно не больше экрана.
/// Слияние на живых данных не запускается: сервер — <see cref="FakeMergeServer"/> с уникальностью штрихкодов. Снимки: %TEMP%\nurmarket-tests\ui-actual\product-merge-*.png.</summary>
[Collection("UiScenarios")]
public sealed class ProductMergeUiTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "merge-ui-" + Guid.NewGuid().ToString("N")[..8]);

    public ProductMergeUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
        MergeStore.DirOverride = _dir;
    }

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        MergeStore.DirOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static string Code(AppLanguage l) => l switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", AppLanguage.Turkish => "tr", AppLanguage.English => "en", _ => "ru" };

    public static IEnumerable<object[]> Cases() =>
        new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek, AppLanguage.Turkish, AppLanguage.English }
            .SelectMany(l => new[] { (1024, 600), (1366, 768) }.Select(s => new object[] { l, s.Item1, s.Item2 }));

    private static MergeProduct P(string id, string title, string barcode, double qty, double price = 100, string brand = "Асу", double purchase = 60, params MergeAltCode[] alt) =>
        new(id, title, brand, "Напитки", price, barcode, alt, "шт", false, "product", qty, purchase, false, null);

    private static MergeCard Card(MergeProduct p) =>
        new(p.Id, p.Title, p.Barcode, p.AltCodes.ToList(), p.Quantity, p.Price, p.PurchasePrice, p.Category, p.Brand, p.Unit, [], false);

    private sealed class Rig
    {
        public required FakeMergeServer Server { get; init; }
        public ProductMergeDeps Deps { get; set; } = null!;
        public List<MergeProduct> Products { get; } = [];
        public List<string> Confirms { get; } = [];
        public List<string> RemovedSources { get; } = [];
        public string? CsvPath { get; set; }
    }

    private Rig NewRig(bool canArchive = true, bool canMerge = true, bool offline = false, FakeMergeServer.MergeMode mode = FakeMergeServer.MergeMode.Missing404)
    {
        var server = new FakeMergeServer { CanArchive = canArchive, ServerMode = mode };
        var products = new List<MergeProduct>
        {
            P("m", "Асу", "111", 10, alt: new MergeAltCode("901", "Мята", 0)),
            P("s1", "Асу клубника", "222", 20, purchase: 80),
            P("s2", "Асу лимон", "333", 5),
            P("a1", "Сок яблоко", "401", 3, price: 80, brand: "Добрый"),
            P("a2", "Сок вишня", "402", 4, price: 95, brand: "Добрый"),
            P("a3", "Сок персик", "403", 6, price: 80, brand: "Добрый"),
            P("d1", "Сгущёнка 380 г", "501", 8, brand: "Рогачёв"),
            P("d2", "Сгущёнка 380 г", "502", 2, brand: "Рогачёв"),
            P("o1", "Хлеб", "601", 30, brand: "Пекарня"),
            P("o2", "Молоко 1 л", "602", 12, brand: "Фермер"),
        };
        var words = new[] { "Отвёртка", "Гвозди", "Шурупы", "Лампа", "Розетка", "Кабель", "Молоток", "Пила", "Дрель", "Рулетка", "Скотч", "Перчатки", "Краска", "Кисть", "Валик", "Уровень", "Клей", "Герметик", "Саморезы", "Дюбель", "Плинтус", "Линолеум", "Обои", "Шпатель", "Ножовка" };
        for (var i = 0; i < words.Length; i++)
            products.Add(P("n" + i, words[i], "7" + (100 + i), i, brand: "Бренд" + i));
        foreach (var p in products)
            server.Add(Card(p));
        var rig = new Rig { Server = server };
        rig.Products.AddRange(products);
        rig.Deps = new ProductMergeDeps
        {
            Products = () => rig.Products.ToList(),
            CreateServer = () => Task.FromResult<IProductMergeServer?>(server),
            CanMerge = () => canMerge,
            IsOffline = () => offline,
            Who = () => "Тест Владелец",
            Account = () => "acc",
            Confirm = (_, text, title, yes, no, danger) => { rig.Confirms.Add(text); return Task.FromResult(true); },
            AfterMerge = (sources, mains) =>
            {
                rig.RemovedSources.AddRange(sources);
                rig.Products.RemoveAll(p => sources.Contains(p.Id));
                return Task.CompletedTask;
            },
            SaveCsv = (_, name, write) =>
            {
                var path = Path.Combine(_dir, name);
                write(path);
                rig.CsvPath = path;
                return Task.FromResult<string?>(path);
            },
        };
        return rig;
    }

    private static async Task WaitUntil(Func<bool> condition, int seconds = 8)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until)
        {
            UiShot.Settle(2, 25);
            await Task.Yield();
        }
    }

    private static T Named<T>(Window w, string name) where T : Control => UiDrive.Find<T>(w, name);

    private static void Save(Window window, string name)
    {
        using var shot = UiShot.Render(window);
        using var image = SKImage.FromBitmap(shot);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
    }

    private async Task<ProductMergeWindow> OpenAsync(Rig rig, int width, int height)
    {
        ModalShell.WorkingAreaForTests = new Size(width, height);
        var window = new ProductMergeWindow(rig.Deps);
        using var _ = UiShot.Render(window, new UiSize(width, height));
        await WaitUntil(() => window.Groups.Count > 0);
        UiShot.Settle(4, 30);
        return window;
    }

    private static string GroupIdOf(ProductMergeWindow w, string title) => w.Groups.First(g => g.BaseTitle == title).Id;

    // ───────────── вид и размеры ─────────────

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Window_shows_groups_and_preview_and_fits_the_screen(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var rig = NewRig();
        var window = await OpenAsync(rig, width, height);
        try
        {
            window.Groups.Select(g => g.BaseTitle).Should().Contain(["Асу", "Сок", "Сгущёнка 380 г"]);
            window.GetVisualDescendants().OfType<Button>().Count(b => b.Name?.StartsWith("MergeGroupRow_", StringComparison.Ordinal) == true).Should().Be(window.Groups.Count);

            window.Select(GroupIdOf(window, "Асу"));
            UiShot.Settle(6, 30);
            Named<TextBox>(window, "MergeMainTitle").Text.Should().Be("Асу");
            Named<TextBox>(window, "MergeVariant_s1").Text.Should().Be("Клубника");
            Named<TextBox>(window, "MergeVariant_s2").Text.Should().Be("Лимон");
            Named<RadioButton>(window, "MergeMain_m").IsChecked.Should().BeTrue();
            var text = UiDrive.AllText(window);
            text.Should().Contain("35", "общий остаток 10 + 20 + 5 в предпросмотре");
            text.Should().Contain("222").And.Contain("333");
            window.CanMergeNow.Should().BeTrue();

            window.Bounds.Width.Should().BeLessThanOrEqualTo(width + 0.5);
            window.Bounds.Height.Should().BeLessThanOrEqualTo(height + 0.5, $"окно не выше экрана ({width}x{height} {lang})");
            var footer = Named<Button>(window, "MergeCheckedButton");
            footer.TranslatePoint(new Point(0, footer.Bounds.Height), window)!.Value.Y.Should().BeLessThanOrEqualTo(height + 0.5, "нижняя панель кнопок видна");
            TextClipChecker.Find(window, "product-merge", Code(lang)).Should().BeEmpty($"текст не обрезан ({width}x{height} {lang})");
            FormFieldsOverlapUiTests.FindViolations(window, "product-merge").Should().BeEmpty($"поля не налезают ({width}x{height} {lang})");

            // группа с разными ценами: предупреждение и жёлтая цена
            window.Select(GroupIdOf(window, "Сок"));
            UiShot.Settle(6, 30);
            UiDrive.AllText(window).Should().Contain("⚠");
            FormFieldsOverlapUiTests.FindViolations(window, "product-merge-prices").Should().BeEmpty();
            TextClipChecker.Find(window, "product-merge-prices", Code(lang)).Should().BeEmpty();

            window.Select(GroupIdOf(window, "Асу"));
            UiShot.Settle(4, 30);
            Save(window, $"product-merge-{Code(lang)}-{width}x{height}.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ───────────── сценарии ─────────────

    [AvaloniaFact]
    public async Task Merge_one_group_through_the_window_moves_everything_and_clears_the_group()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var rig = NewRig();
        var window = await OpenAsync(rig, 1366, 768);
        try
        {
            window.Select(GroupIdOf(window, "Асу"));
            UiShot.Settle(4, 30);
            UiDrive.Click(window, Named<Button>(window, "MergeOneButton"));
            await WaitUntil(() => rig.RemovedSources.Count == 2 && window.Groups.All(g => g.BaseTitle != "Асу"));

            rig.Confirms.Should().ContainSingle().Which.Should().Contain("бэкап");
            var main = rig.Server.Cards["m"];
            main.Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901", "222", "333"]);
            main.Stock.Should().Be(35);
            rig.Server.Cards["s1"].Archived.Should().BeTrue();
            rig.RemovedSources.Should().BeEquivalentTo(["s1", "s2"]);
            window.Groups.Should().NotContain(g => g.BaseTitle == "Асу");
            window.StatusMessage.Should().Contain("Объединено групп: 1");
            MergeStore.LoadJournal("acc").Should().ContainSingle().Which.Status.Should().Be("done");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Marked_groups_are_merged_together_and_confident_ones_can_be_ticked_in_one_click()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var rig = NewRig();
        var window = await OpenAsync(rig, 1366, 768);
        try
        {
            UiDrive.Click(window, Named<Button>(window, "MergeMarkConfident"));
            UiShot.Settle(4, 30);
            var mergeButton = Named<Button>(window, "MergeCheckedButton");
            (mergeButton.Content as string).Should().Contain("(");
            mergeButton.IsEnabled.Should().BeTrue();
            // группа «Сок» с разными ценами «уверенной» не отмечается
            Named<CheckBox>(window, "MergeGroupCheck_" + GroupIdOf(window, "Сок")).IsChecked.Should().BeFalse();
            Named<CheckBox>(window, "MergeGroupCheck_" + GroupIdOf(window, "Асу")).IsChecked.Should().BeTrue();

            UiDrive.Click(window, mergeButton);
            await WaitUntil(() => rig.RemovedSources.Count >= 3 && window.Groups.All(g => g.BaseTitle != "Асу"));
            rig.Server.Cards["s1"].Archived.Should().BeTrue();
            rig.Server.Cards["d1"].Archived.Should().BeFalse("главный — товар с большим остатком; архивируется второй");
            rig.Server.Cards["d2"].Archived.Should().BeTrue();
            MergeStore.LoadJournal("acc").Should().HaveCount(2);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Excluding_a_member_and_renaming_a_variant_change_the_result()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var rig = NewRig();
        var window = await OpenAsync(rig, 1366, 768);
        try
        {
            window.Select(GroupIdOf(window, "Асу"));
            UiShot.Settle(4, 30);
            UiDrive.Type(window, Named<TextBox>(window, "MergeVariant_s1"), "Клубничный");
            UiShot.Settle(3, 30);
            UiDrive.AllText(window).Should().Contain("Клубничный");
            // исключить «Асу лимон»
            Named<CheckBox>(window, "MergeInclude_s2").IsChecked = false;
            UiShot.Settle(4, 30);
            Named<TextBox>(window, "MergeVariant_s2").IsEnabled.Should().BeFalse();

            UiDrive.Click(window, Named<Button>(window, "MergeOneButton"));
            await WaitUntil(() => rig.RemovedSources.Count == 1);
            rig.Server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901", "222"]);
            rig.Server.Cards["m"].Alt.Single(a => a.Barcode == "222").Name.Should().Be("Клубничный");
            rig.Server.Cards["s2"].Archived.Should().BeFalse("исключённый товар не тронут");
            rig.Server.Cards["s2"].Barcode.Should().Be("333");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Decline_hides_the_group_and_the_hidden_filter_returns_it()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var rig = NewRig();
        var window = await OpenAsync(rig, 1366, 768);
        try
        {
            var id = GroupIdOf(window, "Сгущёнка 380 г");
            window.Select(id);
            UiShot.Settle(4, 30);
            UiDrive.Click(window, Named<Button>(window, "MergeDeclineButton"));
            UiShot.Settle(4, 30);
            MergeStore.Declined().Should().Contain(id);
            window.GetVisualDescendants().OfType<Button>().Select(b => b.Name).Should().NotContain("MergeGroupRow_" + id, "отказ скрывает группу из списка");

            Named<ComboBox>(window, "MergeFilter").SelectedIndex = 3;
            UiShot.Settle(4, 30);
            window.GetVisualDescendants().OfType<Button>().Select(b => b.Name).Should().Contain("MergeGroupRow_" + id);
            window.Select(id);
            UiShot.Settle(4, 30);
            UiDrive.Click(window, Named<Button>(window, "MergeDeclineButton")); // «Вернуть в список»
            MergeStore.Declined().Should().NotContain(id);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData("noarchive")]
    [InlineData("norights")]
    [InlineData("offline")]
    public async Task Prepare_only_modes_block_merge_but_keep_groups_and_csv(string mode)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var rig = NewRig(canArchive: mode != "noarchive", canMerge: mode != "norights", offline: mode == "offline");
        var window = await OpenAsync(rig, 1024, 600);
        try
        {
            window.CanMergeNow.Should().BeFalse();
            var expected = mode switch { "noarchive" => MergeTexts.PrepareOnlyReason(), "norights" => MergeTexts.NoRightsReason(), _ => MergeTexts.OfflineReason() };
            window.BannerMessage.Should().Be(expected);
            window.Groups.Should().NotBeEmpty("группы видно и без возможности слияния");
            window.Select(GroupIdOf(window, "Асу"));
            UiShot.Settle(4, 30);
            Named<Button>(window, "MergeOneButton").IsEnabled.Should().BeFalse();
            Named<Button>(window, "MergeCheckedButton").IsEnabled.Should().BeFalse();
            TextClipChecker.Find(window, "product-merge-" + mode, "ru").Should().BeEmpty();
            FormFieldsOverlapUiTests.FindViolations(window, "product-merge-" + mode).Should().BeEmpty();

            UiDrive.Click(window, Named<Button>(window, "MergeCsvButton"));
            await WaitUntil(() => rig.CsvPath is not null);
            File.ReadAllText(rig.CsvPath!).Should().Contain("Асу клубника").And.Contain("222");
            rig.Server.Calls.Should().NotContain(c => c.StartsWith("patch") || c.StartsWith("stock") || c.StartsWith("archive"), "в режиме подготовки сервер не меняется");
            Save(window, $"product-merge-prepare-{mode}-1024x600.png");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Journal_lists_the_merge_and_rolls_it_back()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var rig = NewRig();
        var window = await OpenAsync(rig, 1366, 768);
        string entryId;
        try
        {
            window.Select(GroupIdOf(window, "Асу"));
            UiShot.Settle(4, 30);
            UiDrive.Click(window, Named<Button>(window, "MergeOneButton"));
            await WaitUntil(() => rig.RemovedSources.Count == 2);
            entryId = MergeStore.LoadJournal("acc").Single().Id;
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }

        ModalShell.WorkingAreaForTests = new Size(1024, 600);
        var journal = new ProductMergeJournalWindow(rig.Deps, rig.Server, "");
        try
        {
            using var _ = UiShot.Render(journal, new UiSize(1024, 600));
            await WaitUntil(() => journal.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "MergeRollback_" + entryId));
            UiDrive.AllText(journal).Should().Contain("Тест Владелец").And.Contain("Асу клубника");
            TextClipChecker.Find(journal, "product-merge-journal", "ru").Should().BeEmpty();
            Save(journal, "product-merge-journal-ru-1024x600.png");

            UiDrive.Click(journal, Named<Button>(journal, "MergeRollback_" + entryId));
            await WaitUntil(() => journal.RolledBackAny);
            journal.RolledBackAny.Should().BeTrue();
            rig.Server.Cards["s1"].Archived.Should().BeFalse();
            rig.Server.Cards["s1"].Barcode.Should().Be("222");
            rig.Server.Cards["s1"].Stock.Should().Be(20);
            rig.Server.Cards["m"].Stock.Should().Be(10);
            rig.Server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901"]);
            MergeStore.LoadJournal("acc").Single().Status.Should().Be("rolled_back");
            journal.RestoredIds.Should().Contain(["m", "s1", "s2"]);
        }
        finally
        {
            journal.Close();
            UiShot.Pump();
        }
    }

    // ───────────── серверное слияние (2026-11-21) ─────────────

    [AvaloniaFact]
    public async Task Server_mode_checks_the_group_then_merges_through_the_server_and_the_journal_shows_merge_id()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var rig = NewRig(mode: FakeMergeServer.MergeMode.Supported);
        var window = await OpenAsync(rig, 1366, 768);
        string entryId;
        try
        {
            window.Select(GroupIdOf(window, "Асу"));
            await window.PreflightDone;
            UiShot.Settle(4, 30);
            UiDrive.AllText(window).Should().Contain(MergeTexts.ServerCheckOk());
            window.BannerMessage.Should().BeEmpty("сервер умеет слияние — ручного режима нет");
            Named<Button>(window, "MergeOneButton").IsEnabled.Should().BeTrue();
            rig.Server.MergeCalls.Should().OnlyContain(c => c.Dry, "при выборе группы сервер только проверяется");
            Save(window, "product-merge-server-ru-1366x768.png");

            UiDrive.Click(window, Named<Button>(window, "MergeOneButton"));
            await WaitUntil(() => rig.RemovedSources.Count == 2 && window.Groups.All(g => g.BaseTitle != "Асу"));

            rig.Server.MergeCalls.Count(c => !c.Dry).Should().Be(1);
            rig.Server.StockDocs.Should().BeEmpty("остаток переносит сам сервер");
            rig.Server.Cards["m"].Stock.Should().Be(35);
            rig.Server.Cards["s1"].Archived.Should().BeTrue();
            window.StatusMessage.Should().Contain("Объединено групп: 1");
            var entry = MergeStore.LoadJournal("acc").Single();
            entry.Mode.Should().Be("server");
            entryId = entry.Id;
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }

        ModalShell.WorkingAreaForTests = new Size(1024, 600);
        var journal = new ProductMergeJournalWindow(rig.Deps, rig.Server, "");
        try
        {
            using var _ = UiShot.Render(journal, new UiSize(1024, 600));
            await WaitUntil(() => journal.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "MergeRollback_" + entryId));
            UiDrive.AllText(journal).Should().Contain("merge_id srv-1").And.Contain(MergeTexts.ModeName("server"));
            TextClipChecker.Find(journal, "product-merge-journal-server", "ru").Should().BeEmpty();
            Save(journal, "product-merge-journal-server-ru-1024x600.png");

            // откат запрещён сервером (undo_blocked): причина на экране, слияние осталось
            rig.Server.UndoBlockedReason = "barcode_taken";
            UiDrive.Click(journal, Named<Button>(journal, "MergeRollback_" + entryId));
            await WaitUntil(() => journal.StatusMessage.Contains("штрихкод уже отдан другому товару"));
            journal.StatusMessage.Should().Contain("штрихкод уже отдан другому товару");
            journal.RolledBackAny.Should().BeFalse();
            MergeStore.LoadJournal("acc").Single().Status.Should().Be("done");
            Save(journal, "product-merge-journal-undo-blocked-ru-1024x600.png");

            // причина устранена — откат через сервер
            rig.Server.UndoBlockedReason = null;
            UiDrive.Click(journal, Named<Button>(journal, "MergeRollback_" + entryId));
            await WaitUntil(() => journal.RolledBackAny);
            journal.RolledBackAny.Should().BeTrue();
            rig.Server.UndoCalls.Should().HaveCount(2);
            rig.Server.Cards["s1"].Archived.Should().BeFalse();
            rig.Server.Cards["m"].Alt.Select(a => a.Barcode).Should().BeEquivalentTo(["901"]);
        }
        finally
        {
            journal.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Group_the_server_will_not_merge_is_marked_with_reasons_and_cannot_be_merged(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var rig = NewRig(mode: FakeMergeServer.MergeMode.NotSupported);
        rig.Server.NotSupportedReasons.Clear();
        rig.Server.NotSupportedReasons.AddRange(["has_variants", "has_sale_units", "mixed_units", "has_shade_batches"]);
        var window = await OpenAsync(rig, width, height);
        try
        {
            var id = GroupIdOf(window, "Асу");
            window.Select(id);
            await window.PreflightDone;
            UiShot.Settle(6, 30);
            var text = UiDrive.AllText(window);
            text.Should().Contain(MergeTexts.ServerCheckBlocked(["has_variants", "has_sale_units", "mixed_units", "has_shade_batches"]));
            Named<Button>(window, "MergeOneButton").IsEnabled.Should().BeFalse("сервер не объединит — кнопка выключена");
            text.Should().Contain(MergeTexts.ServerCheckBlockedChip(), "группа помечена в списке слева");

            TextClipChecker.Find(window, "product-merge-blocked", Code(lang)).Should().BeEmpty($"текст не обрезан ({width}x{height} {lang})");
            FormFieldsOverlapUiTests.FindViolations(window, "product-merge-blocked").Should().BeEmpty($"поля не налезают ({width}x{height} {lang})");
            window.Bounds.Height.Should().BeLessThanOrEqualTo(height + 0.5);
            Save(window, $"product-merge-blocked-{Code(lang)}-{width}x{height}.png");

            // отмеченная группа, которую сервер уже назвал «не объединит», слиянием пропускается; сервер не меняется
            window.SetChecked(id, true);
            UiDrive.Click(window, Named<Button>(window, "MergeCheckedButton"));
            var expectedStatus = MergeTexts.ServerBlocked("Асу", ["has_variants", "has_sale_units", "mixed_units", "has_shade_batches"]);
            await WaitUntil(() => window.StatusMessage == expectedStatus);
            window.StatusMessage.Should().Be(expectedStatus);
            rig.Server.MergeCalls.Should().OnlyContain(c => c.Dry);
            rig.Server.Cards["s1"].Archived.Should().BeFalse();
            rig.Server.Calls.Should().NotContain(c => c.StartsWith("patch") || c.StartsWith("stock") || c.StartsWith("archive"));
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task Old_server_shows_the_manual_mode_banner_and_still_merges_by_cards(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var rig = NewRig(mode: FakeMergeServer.MergeMode.Missing404);
        var window = await OpenAsync(rig, 1024, 600);
        try
        {
            window.Select(GroupIdOf(window, "Асу"));
            await window.PreflightDone;
            UiShot.Settle(6, 30);
            window.BannerMessage.Should().Be(MergeTexts.ManualModeBanner(), "сервер ответил 404/405 — ручной режим");
            Named<Button>(window, "MergeOneButton").IsEnabled.Should().BeTrue("ручной режим работает");
            TextClipChecker.Find(window, "product-merge-manual", Code(lang)).Should().BeEmpty();
            FormFieldsOverlapUiTests.FindViolations(window, "product-merge-manual").Should().BeEmpty();
            window.Bounds.Height.Should().BeLessThanOrEqualTo(600.5);
            Save(window, $"product-merge-manual-{Code(lang)}-1024x600.png");

            UiDrive.Click(window, Named<Button>(window, "MergeOneButton"));
            await WaitUntil(() => rig.RemovedSources.Count == 2);
            rig.Server.StockDocs.Should().HaveCount(2, "ручной путь: по акту остатков на источник");
            MergeStore.LoadJournal("acc").Single().Mode.Should().Be("manual");
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }
}
