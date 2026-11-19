using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Estimate;

/// <summary>2026-11-19: «Смета» (ТЗ 19.11.2026) — запас и округление вверх, суммы и скидки, закупка и маржа, срок, пересчёт цен, калькуляторы материала (границы, проёмы, запас, упаковки),
/// локальное хранилище (запись, чтение, повреждённый файл, копия .bak), тексты (без закупки), «В чек» (изменение цены и остатка). Файлы — во временной папке.</summary>
public sealed class EstimateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-estimate-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DateTime _now = new(2026, 11, 19, 10, 0, 0);

    public EstimateTests()
    {
        Directory.CreateDirectory(_dir);
        HardwareCalcSettings.Use(HardwareCalcSettings.CreateDefault()); // типовые нормы, настоящие настройки компании не читаются
    }

    public void Dispose()
    {
        HardwareCalcSettings.Use(null);
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    private EstimateStore Store() => new(Path.Combine(_dir, "estimates.json"), () => _now);

    private static EstimateLine Line(string name, decimal qty, decimal price, string unit = "уп", decimal purchase = 0m, string? product = "p1") =>
        new() { Name = name, Qty = qty, Price = price, Unit = unit, Purchase = purchase, ProductId = product };

    private EstimateDoc Doc(params EstimateLine[] lines)
    {
        var d = EstimateRules.NewDoc(1, _now);
        d.Lines.AddRange(lines);
        return d;
    }

    // ───────────────────────── суммы, запас, скидки ─────────────────────────

    [Fact]
    public void Reserve_RoundsUp_ToWholePacks_AndIgnoresDivisionNoise()
    {
        var d = Doc(Line("Плитка", 10m, 100m));
        d.ReservePercent = 10m;
        EstimateRules.EffectiveQty(d.Lines[0], d).Should().Be(11m, "10 × 1,1 = 11, а не 12 из-за шума деления");
        d.Lines[0].Qty = 10.1m;
        EstimateRules.EffectiveQty(d.Lines[0], d).Should().Be(12m, "11,11 → вверх до целой упаковки");
    }

    [Fact]
    public void Reserve_OnFractionalUnit_RoundsUpToHundredths()
    {
        var d = Doc(Line("Ламинат", 13.64m, 500m, unit: "м²"));
        d.ReservePercent = 10m;
        EstimateRules.EffectiveQty(d.Lines[0], d).Should().Be(15.01m); // 15,004 → 15,01
    }

    [Fact]
    public void Reserve_LineOwnValue_WinsOverEstimate_AndZeroMeansNone()
    {
        var d = Doc(Line("A", 10m, 10m), Line("B", 10m, 10m));
        d.ReservePercent = 20m;
        d.Lines[1].ReservePercent = 0m;
        EstimateRules.EffectiveQty(d.Lines[0], d).Should().Be(12m);
        EstimateRules.EffectiveQty(d.Lines[1], d).Should().Be(10m);
        d.Lines[0].ReservePercent = 150m;
        EstimateRules.EffectiveQty(d.Lines[0], d).Should().Be(10m, "запас вне 0…100 игнорируется");
    }

    [Fact]
    public void Totals_LineDiscount_AndEstimateDiscount_AreRoundedToKopecks()
    {
        var d = Doc(Line("Краска", 3m, 1250m), Line("Работа", 1m, 3333.33m, "усл", product: null));
        d.Lines[0].DiscountPercent = 10m;
        EstimateRules.NetPrice(d.Lines[0]).Should().Be(1125m);
        EstimateRules.LineSum(d.Lines[0], d).Should().Be(3375m);
        EstimateRules.Subtotal(d).Should().Be(6708.33m);
        d.DiscountPercent = 5m;
        EstimateRules.DiscountAmount(d).Should().Be(335.42m);
        EstimateRules.Total(d).Should().Be(6372.91m);
    }

    [Fact]
    public void Cost_AndMargin_UseQuantityWithReserve_AndFlagUnknownPurchase()
    {
        var d = Doc(Line("Клей", 10m, 300m, purchase: 200m), Line("Грунт", 2m, 500m, purchase: 0m));
        d.ReservePercent = 10m;
        EstimateRules.Cost(d).Should().Be(2200m); // 11 × 200
        EstimateRules.Total(d).Should().Be(3300m + 1500m); // 11 × 300 + 3 × 500
        EstimateRules.Margin(d).Should().Be(4800m - 2200m);
        EstimateRules.CostKnown(d).Should().BeFalse();
        EstimateText.InternalSummary(d).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Expiry_ByValidUntil_AcceptedNeverExpires()
    {
        var d = EstimateRules.NewDoc(1, _now);
        d.ValidUntil.Should().Be(new DateTime(2026, 11, 26), "по умолчанию 7 дней");
        EstimateRules.IsExpired(d, new DateTime(2026, 11, 26, 23, 0, 0)).Should().BeFalse("в последний день ещё действует");
        EstimateRules.IsExpired(d, new DateTime(2026, 11, 27)).Should().BeTrue();
        d.Status = EstimateStatus.Accepted;
        EstimateRules.IsExpired(d, new DateTime(2027, 1, 1)).Should().BeFalse();
    }

    [Fact]
    public void Duplicate_MakesDraft_WithNewIds_AndKeepsSectionLinks()
    {
        var d = Doc(Line("A", 1m, 1m));
        d.Sections.Add(new EstimateSection { Name = "Стены" });
        d.Lines[0].SectionId = d.Sections[0].Id;
        d.Status = EstimateStatus.Accepted;
        var copy = EstimateRules.Duplicate(d, 2, _now.AddDays(10), "(копия)");
        copy.Number.Should().Be(2);
        copy.Id.Should().NotBe(d.Id);
        copy.Status.Should().Be(EstimateStatus.Draft);
        copy.Lines[0].Id.Should().NotBe(d.Lines[0].Id);
        copy.Sections[0].Id.Should().NotBe(d.Sections[0].Id);
        copy.Lines[0].SectionId.Should().Be(copy.Sections[0].Id);
        copy.ValidUntil.Should().Be(new DateTime(2026, 12, 6));
    }

    // ───────────────────────── цены из каталога ─────────────────────────

    private static EstimateCatalogInfo Info(string id, decimal retail, decimal wholesale = 0m, decimal stock = 100m, string unit = "уп", decimal purchase = 50m) =>
        new(id, "Товар " + id, unit, retail, wholesale, purchase, stock, false);

    [Fact]
    public void RefreshPrices_UpdatesPrices_ReportsChangesAndMissing_AndSkipsCustomLines()
    {
        var d = Doc(Line("A", 1m, 100m, product: "a"), Line("B", 1m, 200m, product: "b"), Line("Работа", 1m, 500m, "усл", product: null), Line("Нет", 1m, 10m, product: "gone"));
        var catalog = new Dictionary<string, EstimateCatalogInfo> { ["a"] = Info("a", 120m), ["b"] = Info("b", 200m) };
        var changes = EstimateRules.RefreshPrices(d, id => catalog.GetValueOrDefault(id), _now.AddDays(10));
        changes.Should().HaveCount(2);
        changes.Single(c => !c.Missing).Should().Match<EstimateRules.PriceChange>(c => c.OldPrice == 100m && c.NewPrice == 120m);
        changes.Single(c => c.Missing).Line.ProductId.Should().Be("gone");
        d.Lines[2].Price.Should().Be(500m);
        d.Lines[3].Price.Should().Be(10m, "товара нет — цена остаётся");
        d.PricesAt.Should().Be(_now.AddDays(10));
    }

    [Fact]
    public void Level_Foreman_UsesWholesale_WhenSet_OtherwiseRetail()
    {
        EstimateRules.PriceFor(Info("a", 100m, 90m), EstimateLevel.Foreman).Should().Be(90m);
        EstimateRules.PriceFor(Info("a", 100m, 0m), EstimateLevel.Foreman).Should().Be(100m);
        EstimateRules.PriceFor(Info("a", 100m, 90m), EstimateLevel.Retail).Should().Be(100m);
        var d = Doc(Line("A", 1m, 100m, product: "a"));
        EstimateRules.SetLevel(d, EstimateLevel.Foreman, _ => Info("a", 100m, 90m), _now);
        d.Lines[0].Price.Should().Be(90m);
    }

    // ───────────────────────── калькуляторы ─────────────────────────

    private static EstimateCalcInput Calc(EstimateMaterial m, Func<EstimateCalcInput, EstimateCalcInput>? change = null)
    {
        var i = EstimateCalc.Defaults(m);
        return change is null ? i : change(i);
    }

    [Fact]
    public void Calc_Tile_Room3x4_Reserve10_PackOf144_Is10Packs_RoundedUp()
    {
        var r = EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Length = 3m, Width = 4m }));
        r.IsValid.Should().BeTrue();
        r.Area.Should().Be(12m);
        r.Raw.WithReserve.Should().Be(13.2m);
        r.Raw.Packs.Should().Be(10, "13,2 / 1,44 = 9,17 → 10 упаковок");
        r.Formula.Should().Contain("10");
    }

    [Fact]
    public void Calc_Openings_AreSubtracted_AndCannotReachTheArea()
    {
        var ok = EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Length = 3m, Width = 4m, Openings = 2m }));
        ok.Raw.Need.Should().Be(10m);
        ok.Raw.Packs.Should().Be(8, "10 × 1,1 = 11 / 1,44 = 7,64 → 8");
        EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Length = 3m, Width = 4m, Openings = 12m })).Raw.Error.Should().Be(CalcError.OpeningsTooBig);
        EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Length = 3m, Width = 4m, Openings = 13m })).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Calc_Boundaries_EmptyHugeAndBadReserve()
    {
        EstimateCalc.Run(Calc(EstimateMaterial.Tile)).Raw.Error.Should().Be(CalcError.AreaMissing);
        EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Length = 3m, Width = 4m, PackSize = 0m })).Raw.Error.Should().Be(CalcError.PackMissing);
        EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Length = 3m, Width = 4m, ReservePercent = 101m })).Raw.Error.Should().Be(CalcError.ReserveInvalid);
        EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Surface = EstimateSurface.Direct, Area = 2_000_000m })).Raw.Error.Should().Be(CalcError.TooBig);
        EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Surface = EstimateSurface.Direct, Area = 0.01m })).Raw.Packs.Should().Be(1, "даже крошечная площадь — минимум одна упаковка");
    }

    [Fact]
    public void Calc_Paint_WallsMinusOpenings_TimesCoatsAndNorm_ToCans()
    {
        // стены комнаты 3 × 4, высота 2,5: периметр 14 → 35 м²; проём 3,5 м² → 31,5 × 2 слоя × 0,12 л = 7,56 л; запас 10 % = 8,316 л; банка 2,5 л → 4 банки
        var r = EstimateCalc.Run(Calc(EstimateMaterial.Paint, i => i with { Length = 3m, Width = 4m, Height = 2.5m, Openings = 3.5m, PackSize = 2.5m }));
        r.Area.Should().Be(35m);
        r.Raw.Need.Should().Be(7.56m);
        r.Raw.Packs.Should().Be(4);
        r.Raw.PackedTotal.Should().Be(10m);
    }

    [Fact]
    public void Calc_Norms_ComeFromTheOwnerTable()
    {
        var custom = HardwareCalcSettings.CreateDefault();
        custom.Norms.Single(n => n.Key == "putty").PerM2 = 2m;
        HardwareCalcSettings.Use(custom);
        var i = Calc(EstimateMaterial.Putty, x => x with { Surface = EstimateSurface.Direct, Area = 10m });
        i.PerM2.Should().Be(2m);
        var r = EstimateCalc.Run(i with { ReservePercent = 0m });
        r.Raw.Need.Should().Be(40m, "10 м² × 2 слоя × 2 кг");
        r.Raw.Packs.Should().Be(2, "40 кг / 25 кг → 2 мешка");
    }

    [Fact]
    public void Calc_Drywall_SheetsOf3SquareMetres_ExactMultipleDoesNotAddASheet()
    {
        EstimateCalc.Run(Calc(EstimateMaterial.Drywall, i => i with { Surface = EstimateSurface.Direct, Area = 30m, ReservePercent = 0m })).Raw.Packs.Should().Be(10);
        EstimateCalc.Run(Calc(EstimateMaterial.Drywall, i => i with { Surface = EstimateSurface.Direct, Area = 27m })).Raw.Packs.Should().Be(10, "27 × 1,1 = 29,7 / 3 = 9,9 → 10");
    }

    [Fact]
    public void Calc_Wallpaper_Room3x4_Is9Rolls_AsInTheTz()
    {
        var r = EstimateCalc.Run(Calc(EstimateMaterial.Wallpaper, i => i with { Length = 3m, Width = 4m, Height = 2.7m }));
        r.Raw.Packs.Should().Be(9);
        r.Raw.Strips.Should().Be(27);
        EstimateCalc.Run(Calc(EstimateMaterial.Wallpaper, i => i with { Length = 3m, Width = 4m, Height = 12m })).Raw.Error.Should().Be(CalcError.RollInvalid, "полоса длиннее рулона");
    }

    [Fact]
    public void ToLine_LinkedProduct_UsesPacksOrBaseUnits_AndRejectsWrongUnit()
    {
        var r = EstimateCalc.Run(Calc(EstimateMaterial.Tile, i => i with { Length = 3m, Width = 4m }));
        var byPack = EstimateCalc.ToLine(r, Info("t", 900m, unit: "уп"), EstimateLevel.Retail)!;
        byPack.Qty.Should().Be(10m);
        byPack.ReservePercent.Should().Be(0m, "запас уже в упаковках");
        byPack.CalcNote.Should().NotBeNullOrWhiteSpace();
        var bySquare = EstimateCalc.ToLine(r, Info("t", 625m, unit: "м²"), EstimateLevel.Retail)!;
        bySquare.Qty.Should().Be(14.4m);
        EstimateCalc.ToLine(r, Info("t", 100m, unit: "м"), EstimateLevel.Retail).Should().BeNull("плитка в погонных метрах не подходит");
        var free = EstimateCalc.ToLine(r, null, EstimateLevel.Retail)!;
        free.IsCustom.Should().BeTrue();
        free.Qty.Should().Be(10m);
        EstimateRules.LineSum(byPack, Doc(byPack)).Should().Be(9000m);
    }

    // ───────────────────────── хранилище ─────────────────────────

    [Fact]
    public void Store_CreateSaveLoadDelete_NumbersGrowAndNeverRepeat()
    {
        var s = Store();
        var a = s.Create(d => d.Title = "Ремонт комнаты");
        var b = s.Create();
        a.Number.Should().Be(1);
        b.Number.Should().Be(2);
        a.Lines.Add(Line("Краска", 2m, 1000m));
        s.Save(a);
        var loaded = s.Find(a.Id)!;
        loaded.Title.Should().Be("Ремонт комнаты");
        loaded.Lines.Should().ContainSingle().Which.Price.Should().Be(1000m);
        s.Delete(b.Id).Should().BeTrue();
        s.Create().Number.Should().Be(3, "номер удалённой не выдаётся повторно");
        s.All().Should().HaveCount(2);
    }

    [Fact]
    public void Store_Duplicate_AndStatus_AndLinks()
    {
        var s = Store();
        var a = s.Create();
        a.Lines.Add(Line("A", 1m, 5m));
        s.Save(a);
        var copy = s.Duplicate(a.Id, "(копия)")!;
        copy.Number.Should().Be(2);
        copy.Lines.Should().HaveCount(1);
        s.SetStatus(a.Id, EstimateStatus.Accepted).Should().BeTrue();
        s.SetStatus(a.Id, EstimateStatus.Accepted).Should().BeFalse("статус не менялся");
        s.Find(a.Id)!.Status.Should().Be(EstimateStatus.Accepted);
        s.SetLink("paint", "prod-7");
        s.GetLink("paint").Should().Be("prod-7");
        s.GetLink("tile").Should().BeNull();
    }

    [Fact]
    public void Store_CorruptFile_DoesNotThrow_IsSetAside_AndNewDataIsWritable()
    {
        var path = Path.Combine(_dir, "estimates.json");
        File.WriteAllText(path, "{ это не json ");
        var s = Store();
        s.All().Should().BeEmpty();
        File.Exists(path + ".bad").Should().BeTrue("испорченный файл отложен как .bad, а не затёрт");
        s.Create(d => d.Title = "После сбоя").Number.Should().Be(1);
        Store().All().Should().ContainSingle();
    }

    [Fact]
    public void Store_CorruptMainFile_FallsBackToBackupCopy()
    {
        var s = Store();
        var a = s.Create(d => d.Title = "Первая");
        s.Create(d => d.Title = "Вторая"); // вторая запись делает .bak с первой
        File.Exists(Path.Combine(_dir, "estimates.json.bak")).Should().BeTrue();
        File.WriteAllText(Path.Combine(_dir, "estimates.json"), "\0\0\0 битый");
        var recovered = Store().All();
        recovered.Should().NotBeEmpty("есть целая копия .bak");
        recovered.Should().Contain(e => e.Id == a.Id);
    }

    [Fact]
    public void Store_EmptyOrMissingFile_IsEmptyList()
    {
        Store().All().Should().BeEmpty();
        File.WriteAllText(Path.Combine(_dir, "estimates.json"), "");
        Store().All().Should().BeEmpty();
    }

    [Fact]
    public void Store_FileIsCompanyData()
    {
        CompanyStoreRegistry.CompanyLocalItems.Should().Contain("estimates.json").And.Contain("estimates.json.bak");
    }

    // ───────────────────────── тексты и документы ─────────────────────────

    [Fact]
    public void Message_HasLinesAndTotal_ButNeverPurchaseOrMargin()
    {
        var d = Doc(Line("Краска белая", 4m, 1250m, "банка", purchase: 777m));
        d.Title = "Ремонт комнаты";
        d.ClientName = "Бакыт";
        d.DiscountPercent = 10m;
        var text = EstimateText.Message(d, "Магазин");
        text.Should().Contain("Краска белая").And.Contain("Ремонт комнаты").And.Contain("Бакыт").And.Contain("Магазин");
        text.Should().Contain(MoneyText.Som(4500m), "итог: 5000 − 10 %");
        text.Should().NotContain("777").And.NotContain("Маржа").And.NotContain("Себестоимость");
        text.Should().Contain("26.11.2026");
    }

    [Fact]
    public void Receipt_FitsPaperWidth_OnBothRibbons()
    {
        var d = Doc(Line("Очень длинное название материала для проверки переноса строки", 2m, 12345.5m, "уп"));
        d.Title = "Объект";
        foreach (var width in new[] { 32, 42, 48 })
            EstimateText.Receipt(d, width).Split('\n').Should().OnlyContain(l => l.TrimEnd('\r').Length <= width, $"лента {width} знаков");
    }

    [Fact]
    public void ClientDoc_TotalMatchesEstimateTotal_AndTitleIsEstimate()
    {
        var d = Doc(Line("Краска", 4m, 1250m), Line("Работа", 1m, 3333.33m, "усл", product: null));
        d.Lines[0].DiscountPercent = 10m;
        d.DiscountPercent = 5m;
        d.ReservePercent = 10m;
        var cd = EstimateText.ToClientDoc(d);
        cd.Total.Should().BeApproximately((double)EstimateRules.Total(d), 0.011);
        cd.Number.Should().Be("С-0001");
        cd.Extra!["title"].GetString().Should().Be("Смета");
        cd.Lines.Should().HaveCount(2);
    }

    // ───────────────────────── «В чек» ─────────────────────────

    [Fact]
    public void CartPlan_FindsPriceChange_NoStock_Missing_AndKeepsCustomLines()
    {
        var d = Doc(Line("A", 5m, 100m, product: "a"), Line("B", 5m, 100m, product: "b"), Line("C", 1m, 10m, product: "c"), Line("Доставка", 1m, 300m, "усл", product: null));
        d.Lines[0].StockAtTime = 50m;
        d.Lines[1].StockAtTime = 50m;
        var catalog = new Dictionary<string, EstimateCatalogInfo> { ["a"] = Info("a", 110m, stock: 50m), ["b"] = Info("b", 100m, stock: 3m) };
        var plan = EstimateCartPlanner.Plan(d, id => catalog.GetValueOrDefault(id));
        plan.Should().HaveCount(4);
        plan[0].PriceChanged(EstimateLevel.Retail).Should().BeTrue();
        plan[0].NoStock.Should().BeFalse();
        plan[1].NoStock.Should().BeTrue();
        plan[1].StockChanged.Should().BeTrue();
        plan[2].Missing.Should().BeTrue();
        plan[3].IsCustom.Should().BeTrue();
        plan[3].Missing.Should().BeFalse();
        EstimateCartPlanner.HasChanges(plan, EstimateLevel.Retail).Should().BeTrue();
        var text = EstimateCartPlanner.ChangesText(plan, EstimateLevel.Retail);
        text.Should().Contain("A").And.Contain("B").And.Contain("C").And.NotContain("Доставка");
    }

    [Fact]
    public void CartPlan_NoChanges_WhenCatalogMatches()
    {
        var d = Doc(Line("A", 5m, 100m, product: "a"));
        d.Lines[0].StockAtTime = 50m;
        var plan = EstimateCartPlanner.Plan(d, _ => Info("a", 100m, stock: 50m));
        EstimateCartPlanner.HasChanges(plan, EstimateLevel.Retail).Should().BeFalse();
        EstimateCartPlanner.ChangesText(plan, EstimateLevel.Retail).Should().BeEmpty();
    }

    // ───────────────────────── доступность ─────────────────────────

    [Fact]
    public void Feature_FreeByDefault_AndEnabledByFlag()
    {
        EstimateFeature.PriceSom.Should().Be(0m);
        var prefs = UserPreferences.Instance;
        var was = prefs.EstimateEnabled;
        try
        {
            prefs.EstimateEnabled = true;
            EstimateFeature.IsAvailable.Should().BeTrue();
        }
        finally
        {
            prefs.EstimateEnabled = was;
        }
    }
}
