using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ1–АЗ6): профиль сферы, нормализация и поиск по каталожному номеру (в т. ч. кириллицей и с неверной раскладкой),
/// тело запроса товара (attributes / storage_location / related_input — только когда заполнено), разбор ответа сервера, аналоги.
/// Ключевое правило: вне сферы «Автозапчасти» тело запроса и плитки такие же, как раньше. Настройки — общий UserPreferences.Instance (значения возвращаются),
/// файл данных запчастей — во временной папке. Демо-данные — локальный docs/Образцы/Автозапчасти/demo-autoparts-catalog.csv (на сервер не уходят).</summary>
[Collection("UserPreferences")]
public sealed class AutoPartsSphereTests : IDisposable
{
    private readonly string _sphere;
    private readonly string? _detail;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "autoparts-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoPartsSphereTests()
    {
        var p = UserPreferences.Instance;
        _sphere = p.MarketSphere;
        _detail = p.MarketSphereDetail;
        Directory.CreateDirectory(_dir);
        PartMetaStore.FileOverride = Path.Combine(_dir, "autopart-meta.json");
        PartMetaStore.ResetForTests();
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        PartMetaStore.FileOverride = null;
        PartMetaStore.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static void SetSphere(string baseSphere, string? detail)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = baseSphere;
        p.MarketSphereDetail = detail;
    }

    private static void AutoParts() => SetSphere(MarketSpheres.Grocery, AutoPartsProfile.Id);

    private static CatalogProductTileVm Tile(string id, string name, string? article, double qty = 5, string price = "100.00 сом") =>
        new(id, name, price, false, null) { Article = article, Quantity = qty };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    // ── АЗ1: профиль ──

    [Fact]
    public void Profile_is_registered_with_grocery_base_and_five_complete_languages()
    {
        var p = SphereCatalog.TryGet(AutoPartsProfile.Id);
        p.Should().NotBeNull();
        p!.BaseBehavior.Should().Be(MarketSpheres.Grocery, "на сервер уходит grocery");
        p.Title.IsComplete.Should().BeTrue();
        p.ShortTitle.IsComplete.Should().BeTrue();
        p.Description.IsComplete.Should().BeTrue();
        p.CategoryHints.Should().HaveCount(16).And.OnlyContain(c => c.IsComplete);
        p.Units.Should().OnlyContain(u => u.Name.IsComplete && !u.Fractional);
        AutoPartsProfile.ServiceTemplates.Should().HaveCount(4).And.OnlyContain(c => c.IsComplete);
        p.ChoiceVisible.Should().BeTrue();
        p.ForemanPriceAvailable.Should().BeTrue();
        p.ShowProductDetails.Should().BeTrue();
        SphereCatalog.ChoiceList().Select(x => x.Id).Should().Contain(AutoPartsProfile.Id);
        foreach (var other in new[] { "hardware", "stationery", "electronics" })
            SphereCatalog.TryGet(other).Should().NotBeNull("остальные сферы не потеряны");
    }

    [Fact]
    public void IsAutoParts_only_for_autoparts()
    {
        AutoParts();
        MarketSpheres.IsAutoParts.Should().BeTrue();
        MarketSpheres.Normalize(AutoPartsProfile.Id).Should().Be(MarketSpheres.Grocery);
        SetSphere(MarketSpheres.Grocery, null);
        MarketSpheres.IsAutoParts.Should().BeFalse();
        SetSphere(MarketSpheres.Grocery, ElectronicsProfile.Id);
        MarketSpheres.IsAutoParts.Should().BeFalse();
    }

    [Fact]
    public void State_labels_are_filled_in_every_language()
    {
        foreach (var state in AutoPartsProfile.States.Where(s => s.Length > 0))
        {
            AutoPartsProfile.StateName(state).Should().NotBeNullOrWhiteSpace();
            AutoPartsProfile.StateBadge(state).Should().NotBeNullOrWhiteSpace();
        }
        AutoPartsProfile.CrossLimitHint.Should().Contain("5").And.Contain("Стандарт");
    }

    // ── АЗ2: нормализация ──

    [Theory]
    [InlineData("0 986 452-041", "0986452041")]
    [InlineData("0986-452-041", "0986452041")]
    [InlineData("W 712/75", "W71275")]
    [InlineData("w712/75", "W71275")]
    [InlineData("04465-0К090", "044650K090")]      // К — кириллическая
    [InlineData("04465-0K090", "044650K090")]      // К — латинская
    [InlineData("ВМW 123", "BMW123")]              // В, М — кириллические
    [InlineData("Ц 712/75", "W71275")]             // русская раскладка вместо W
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Norm_unifies_spaces_dashes_case_and_lookalike_letters(string? raw, string expected) =>
        PartNumbers.Norm(raw).Should().Be(expected);

    [Fact]
    public void Norm_is_not_longer_than_64()
    {
        PartNumbers.Norm(new string('1', 200)).Length.Should().Be(64);
        PartNumbers.Norm("").Should().BeEmpty();
    }

    [Fact]
    public void Index_of_20000_numbers_builds_fast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var set = new HashSet<string>();
        for (var i = 0; i < 20000; i++)
            set.Add(PartNumbers.Norm($"{i % 10} {986000 + i} 452-0{i % 100}"));
        sw.Stop();
        set.Count.Should().BeGreaterThan(1000);
        sw.ElapsedMilliseconds.Should().BeLessThan(1000);
    }

    [Theory]
    [InlineData("0 986 452 041", true)]
    [InlineData("W 712/75", true)]
    [InlineData("04465-0К090", true)]
    [InlineData("фильтр масляный", false)]
    [InlineData("колодки камри 2012", false)]
    [InlineData("ab", false)]
    public void LooksLikePart_tells_numbers_from_names(string query, bool expected) =>
        PartNumbers.LooksLikePart(query).Should().Be(expected);

    [Fact]
    public void ParseList_drops_brand_words_duplicates_and_empties()
    {
        PartNumbers.ParseList("Bosch 0 451 103 316, Knecht OC 983\nMann W 811/80;; W 811/80 ")
            .Should().Equal("0451103316", "OC983", "W81180");
        PartNumbers.ParseList("W 712/94").Should().Equal(new[] { "W71294" }); // короткое слово — не бренд
        PartNumbers.ParseList(null).Should().BeEmpty();
    }

    [Fact]
    public void Rank_exact_prefix_contains_and_letter_O_vs_zero()
    {
        var q = PartNumbers.Norm("0986452041");
        PartNumbers.Rank(q, new[] { "0986452041" }).Should().Be(0);
        PartNumbers.Rank(q, new[] { "09864520410" }).Should().Be(1);
        PartNumbers.Rank(q, new[] { "X0986452041" }).Should().Be(2);
        PartNumbers.Rank(q, new[] { "1111" }).Should().Be(PartNumbers.NoMatch);
        // «О» вместо «0» — нестрогое совпадение, но не лучше «начало номера»
        PartNumbers.Rank(PartNumbers.Norm("0K090"), new[] { "OK090" }).Should().Be(1);
    }

    // ── АЗ4: ответ сервера и тело запроса ──

    private const string ServerCard = """
        {"id":"7","name":"Фильтр масляный","article":"W 712/94",
         "storage_location":"A-16-5",
         "attributes":[{"key":"part_no_norm","value":"W71294"},{"key":"oem","value":"04152YZZA1"},{"key":"cross_1","value":"0451103316"},
                       {"key":"cross_2","value":"OC983, OC1013"},{"key":"fits_1","value":"Toyota Camry 2006-2017"},{"key":"fits_2","value":"Lexus ES"},
                       {"key":"condition","value":"new"},{"key":"part_type","value":"oes"},{"key":"color","value":"black","unit":"x"}],
         "related":[{"id":"8"},"9",10]}
        """;

    [Fact]
    public void Parse_reads_attributes_location_and_related()
    {
        var info = AutoPartInfo.Parse(Json(ServerCard));
        info.Should().NotBeNull();
        info!.PartNo.Should().Be("W71294");
        info.Oem.Should().Equal("04152YZZA1");
        info.Cross.Should().Equal("0451103316", "OC983", "OC1013");
        info.Fits.Should().Equal("Toyota Camry 2006-2017", "Lexus ES");
        info.State.Should().Be("oes");
        info.Location.Should().Be("A-16-5");
        info.Related.Should().Equal("8", "9", "10");
        info.Other.Should().ContainSingle(o => o.Key == "color" && o.Unit == "x", "чужие атрибуты сохраняются");
    }

    [Fact]
    public void Parse_without_autopart_fields_is_null()
    {
        AutoPartInfo.Parse(Json("""{"id":"1","name":"Хлеб"}""")).Should().BeNull();
        AutoPartInfo.Parse(Json("""{"id":"1","attributes":[],"storage_location":"","related":[]}""")).Should().BeNull();
    }

    [Theory]
    [InlineData("original", "new", "original")]
    [InlineData("oes", "new", "oes")]
    [InlineData("aftermarket", "new", "aftermarket")]
    [InlineData("used", "used", "")]
    [InlineData("refurb", "refurb", "")]
    public void State_maps_to_server_condition(string state, string condition, string partType)
    {
        AutoPartInfo.FromState(state).Should().Be((condition, partType));
        var info = new AutoPartInfo { Condition = condition, PartType = partType };
        info.State.Should().Be(state);
    }

    [Fact]
    public void BuildAttributes_roundtrips_keeps_other_and_respects_server_limits()
    {
        var info = AutoPartInfo.Parse(Json(ServerCard))!;
        var attrs = info.BuildAttributes();
        attrs.Select(a => a.Key).Should().Contain(new[] { "part_no_norm", "oem", "cross_1", "cross_2", "cross_3", "fits_1", "condition", "part_type", "color" });

        var big = new AutoPartInfo { PartNo = "A", Oem = Enumerable.Range(0, 10).Select(i => "OEM" + i).ToList(), Cross = Enumerable.Range(0, 20).Select(i => "CR" + i).ToList(),
            Fits = Enumerable.Range(0, 20).Select(i => new string('x', 100) + i).ToList() };
        var list = big.BuildAttributes();
        list.Count.Should().BeLessOrEqualTo(30);
        list.Should().OnlyContain(a => a.Value.Length <= 64);
        list.Select(a => a.Key).Should().OnlyHaveUniqueItems();
        list.Should().OnlyContain(a => System.Text.RegularExpressions.Regex.IsMatch(a.Key, "^[a-z][a-z0-9_]{0,31}$"));
    }

    private static ProductEditRequest Request() => new() { Name = "Фильтр", Unit = "шт", Quantity = 1, Price = 100, PurchasePrice = 50, MarkupPercent = 100 };

    [Fact]
    public void Body_without_autopart_fields_has_no_new_keys()
    {
        var body = CatalogApiService.BuildProductBody(Request());
        body.Keys.Should().NotContain(new[] { "attributes", "storage_location", "related_input" });
    }

    [Fact]
    public void Body_with_autopart_fields_has_attributes_location_and_related()
    {
        var r = Request();
        r.Attributes = new AutoPartInfo { PartNo = "W71294", Cross = { "OC983" }, Condition = "new", PartType = "aftermarket" }.BuildAttributes();
        r.SendStorageLocation = true;
        r.StorageLocation = " A-1-1 ";
        r.RelatedInput = new List<string> { "8", "8", "", "9" };
        var body = CatalogApiService.BuildProductBody(r);
        var json = JsonSerializer.Serialize(body);
        json.Should().Contain("\"attributes\":[{\"key\":\"part_no_norm\",\"value\":\"W71294\"}");
        json.Should().Contain("\"key\":\"cross_1\",\"value\":\"OC983\"").And.Contain("\"key\":\"condition\",\"value\":\"new\"");
        body["storage_location"].Should().Be("A-1-1");
        ((string[])body["related_input"]!).Should().Equal("8", "9");
    }

    [Fact]
    public void Body_can_clear_attributes_and_location()
    {
        var r = Request();
        r.Attributes = new List<AutoPartAttr>();
        r.SendStorageLocation = true;
        r.StorageLocation = "";
        r.RelatedInput = new List<string>();
        var body = CatalogApiService.BuildProductBody(r);
        ((Array)body["attributes"]!).Length.Should().Be(0);
        body["storage_location"].Should().Be("");
        ((Array)body["related_input"]!).Length.Should().Be(0);
    }

    // ── хранилище и плитка ──

    [Fact]
    public void Store_remembers_from_server_card_and_survives_corrupt_file()
    {
        PartMetaStore.Remember("7", Json(ServerCard));
        PartMetaStore.Get("7")!.Location.Should().Be("A-16-5");
        PartMetaStore.SaveIfDirty();
        File.Exists(PartMetaStore.FileOverride!).Should().BeTrue();

        PartMetaStore.ResetForTests();
        PartMetaStore.Get("7")!.Cross.Should().Contain("OC983", "после перезапуска данные читаются из файла");

        PartMetaStore.ResetForTests();
        File.WriteAllText(PartMetaStore.FileOverride!, "{ это не json");
        PartMetaStore.Get("7").Should().BeNull("повреждённый файл не роняет кассу");

        // карточка без полей запчасти не стирает известное; карточка с пустыми полями — стирает (сервер главный)
        PartMetaStore.Remember("7", Json(ServerCard));
        PartMetaStore.Remember("7", Json("""{"id":"7","name":"Фильтр"}"""));
        PartMetaStore.Get("7").Should().NotBeNull();
        PartMetaStore.Remember("7", Json("""{"id":"7","attributes":[],"storage_location":"","related":[]}"""));
        PartMetaStore.Get("7").Should().BeNull();
    }

    [Fact]
    public void Tile_shows_part_data_only_in_autoparts_sphere()
    {
        PartMetaStore.Remember("7", Json(ServerCard));
        var tile = Tile("7", "Фильтр масляный", "W 712/94");

        SetSphere(MarketSpheres.Grocery, null);
        tile.Part.Should().BeNull();
        tile.HasPartBadge.Should().BeFalse();
        tile.HasPartLine.Should().BeFalse();
        tile.PartLine.Should().BeEmpty();

        AutoParts();
        tile.Part.Should().NotBeNull();
        tile.PartBadgeText.Should().Be("OES");
        tile.PartLine.Should().StartWith("W 712/94").And.Contain("A-16-5");
        tile.PartCellText.Should().Contain("A-16-5");
    }

    // ── АЗ3: поиск и порядок ──

    private static List<CatalogProductTileVm> Catalog()
    {
        PartMetaStore.Remember("1", Json("""{"attributes":[{"key":"part_no_norm","value":"0986452041"},{"key":"condition","value":"new"},{"key":"part_type","value":"original"}]}"""));
        PartMetaStore.Remember("2", Json("""{"attributes":[{"key":"cross_1","value":"0986452041"},{"key":"condition","value":"new"},{"key":"part_type","value":"aftermarket"}]}"""));
        PartMetaStore.Remember("3", Json("""{"attributes":[{"key":"cross_1","value":"0986452041"},{"key":"condition","value":"used"}]}"""));
        PartMetaStore.Remember("4", Json("""{"attributes":[{"key":"oem","value":"09864520410"}]}"""));
        PartMetaStore.Remember("5", Json("""{"attributes":[{"key":"part_no_norm","value":"W71275"}]}"""));
        return new List<CatalogProductTileVm>
        {
            Tile("1", "Колодки Bosch", "0 986 452 041", qty: 0),
            Tile("2", "Колодки Ferodo", "FDB 1"),
            Tile("3", "Колодки б/у", "USED-1"),
            Tile("4", "Колодки длинные", "LONG-1"),
            Tile("5", "Фильтр W 712/75", "W 712/75"),
            Tile("6", "Хлеб", null),
        };
    }

    [Theory]
    [InlineData("0 986 452 041")]
    [InlineData("0986452041")]
    [InlineData("0986-452-041")]
    [InlineData("0986.452.041")]
    public void Search_by_number_finds_article_cross_and_prefix_in_any_spelling(string query)
    {
        AutoParts();
        var catalog = Catalog();
        var q = PartNumbers.Norm(query);
        var ordered = catalog.Where(p => p.PartRank(q) < PartNumbers.NoMatch).OrderBy(p => p.PartRank(q)).ThenBy(p => p.Quantity <= 0 ? 1 : 0).Select(p => p.Id).ToList();
        ordered.Should().BeEquivalentTo(new[] { "1", "2", "3", "4" });
        ordered.Take(3).Should().BeEquivalentTo(new[] { "1", "2", "3" }, "точное совпадение (артикул и кроссы) — впереди префикса");
        ordered.Last().Should().Be("4", "начало номера — после точного");
        // среди точных: в наличии выше, чем нет в наличии
        ordered.IndexOf("2").Should().BeLessThan(ordered.IndexOf("1"));
    }

    [Fact]
    public void Search_with_cyrillic_letters_and_wrong_layout_finds_the_same_card()
    {
        AutoParts();
        var catalog = Catalog();
        var typed = new[] { "W 712/75", "w712/75", "Ц 712/75", "Ц712-75" };
        foreach (var t in typed)
        {
            PartNumbers.LooksLikePart(t).Should().BeTrue(t);
            catalog.Where(p => p.PartRank(PartNumbers.Norm(t)) == 0).Select(p => p.Id).Should().Equal("5");
        }
        // «О» кириллицей и «К» кириллицей вместо латиницы в номере карточки
        var cyr = Tile("9", "Свеча", "04465-0К090");
        cyr.PartRank(PartNumbers.Norm("04465-0K090")).Should().Be(0);
        cyr.PartRank(PartNumbers.Norm("044650К090")).Should().Be(0);
    }

    [Fact]
    public void Non_autoparts_tile_has_no_part_rank_data_and_name_search_untouched()
    {
        SetSphere(MarketSpheres.Grocery, null);
        var catalog = Catalog();
        // Article у карточки остаётся прежним полем: ранг по нему считается всегда, но выдача по номеру включается только в сфере (MarketSpheres.IsAutoParts).
        catalog.All(p => p.Part is null).Should().BeTrue();
        MarketSpheres.IsAutoParts.Should().BeFalse();
        SearchMatch.Products(catalog, "хлеб").Select(p => p.Id).Should().Equal("6");
    }

    // ── АЗ6: аналоги ──

    [Fact]
    public void Analogs_are_cards_with_common_number_or_related()
    {
        AutoParts();
        var catalog = Catalog();
        PartMetaStore.Remember("6", Json("""{"related":["5"]}"""));
        PartAnalogs.Find(catalog[0], catalog).Select(p => p.Id).Should().BeEquivalentTo(new[] { "2", "3" }, "общий номер 0986452041");
        PartAnalogs.Find(catalog[4], catalog).Select(p => p.Id).Should().BeEquivalentTo(new[] { "6" }, "связь related в любую сторону");
        PartAnalogs.Find(catalog[5], catalog).Select(p => p.Id).Should().BeEquivalentTo(new[] { "5" });
        PartAnalogs.Count(catalog[2], catalog).Should().Be(2);
    }

    // ── демо-каталог (локально, на сервер не уходит) ──

    private static string? FindDemoCsv()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "docs", "Образцы", "Автозапчасти", "demo-autoparts-catalog.csv");
            if (File.Exists(path))
                return path;
            dir = dir.Parent;
        }
        return null;
    }

    private static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    [Fact]
    public void Demo_catalog_rows_are_searchable_by_their_numbers()
    {
        var csv = FindDemoCsv();
        if (csv is null)
            return; // репозиторий без docs (сборка из архива) — демо-данных нет
        AutoParts();
        var lines = File.ReadAllLines(csv, System.Text.Encoding.UTF8).Skip(1).Where(l => l.Trim().Length > 0).ToList();
        lines.Count.Should().BeGreaterThan(100);
        var tiles = new List<CatalogProductTileVm>();
        var n = 0;
        foreach (var line in lines)
        {
            var f = SplitCsv(line);
            if (f.Count < 13)
                continue;
            var id = "d" + (++n);
            // Название;Бренд;Каталожный номер;OEM;Кросс-номера;Подходит к;Состояние;Страна;Ячейка;Закупка;Розница;Цена СТО;Остаток;...
            var info = new AutoPartInfo
            {
                PartNo = PartNumbers.Norm(f[2]),
                Oem = PartNumbers.ParseList(f[3]).Take(AutoPartInfo.MaxOem).ToList(),
                Cross = PartNumbers.ParseList(f[4]).Take(AutoPartInfo.MaxCross).ToList(),
                Fits = f[5].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(AutoPartInfo.MaxFits).ToList(),
                Location = f[8].Trim(),
            };
            info.BuildAttributes().Count.Should().BeLessOrEqualTo(30);
            PartMetaStore.Set(id, info);
            tiles.Add(Tile(id, f[0], f[2]));
        }
        tiles.Count.Should().BeGreaterThan(100);
        var withCross = tiles.First(t => t.Part!.Cross.Count > 0);
        var cross = withCross.Part!.Cross[0];
        tiles.Where(t => t.PartRank(cross) == 0).Select(t => t.Id).Should().Contain(withCross.Id);
        var own = PartNumbers.Norm(withCross.Article);
        tiles.Where(t => t.PartRank(own) == 0).Select(t => t.Id).Should().Contain(withCross.Id);
    }
}
