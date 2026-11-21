using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Invoice;

/// <summary>2026-11-20 (просьба владельца: «при загрузке накладных через наш ИИ — в один товар и доп. штрихкоды: «Асу» и «Асу со вкусом клубники» — один товар, цена одна, марка одна, отличается
/// только вкус»): разбор ответа ИИ (base/variant/brand), группировка вариантов (одна цена; разные цены; разные марки; без variant), добавление вкуса к существующему товару, команда «не объединяй»,
/// «Разъединить» / «Объединить с …», название варианта, доп. штрихкоды и разбивка прихода по вкусам. Сеть и каталог кассы не трогаются (поиски подменены, запись штрихкодов — через подмены Binder).</summary>
[Collection("InvoiceBarcodeBinder")]
public sealed class InvoiceVariantsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "invoice-variants-" + Guid.NewGuid().ToString("N"));

    public InvoiceVariantsTests()
    {
        InvoiceBarcodeBinder.OwnerOverride = null;
        InvoiceBarcodeBinder.ReadCardOverride = null;
        InvoiceBarcodeBinder.SaveOverride = null;
        InvoiceBarcodeBinder.AddOverride = null;
        InvoiceVariantPrefs.DataDirOverride = _dir;
        InvoiceVariantPrefs.ResetCache();
    }

    public void Dispose()
    {
        InvoiceBarcodeBinder.OwnerOverride = null;
        InvoiceBarcodeBinder.ReadCardOverride = null;
        InvoiceBarcodeBinder.SaveOverride = null;
        InvoiceBarcodeBinder.AddOverride = null;
        InvoiceVariantPrefs.DataDirOverride = null;
        InvoiceVariantPrefs.ResetCache();
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    // ------------------------------------------------------------------ помощники

    private static ProductActionPlan.Step Create(string invoiceName, string? baseName, string? variant, string? brand, string? barcode, double qty, double purchase, double? price = null, string unit = "шт") =>
        new("create", new CatalogProductTileVm("", invoiceName, "0.00", false), qty, null, null, purchase, price, barcode, unit, null,
            Base: baseName, Variant: variant, Brand: brand, InvoiceName: invoiceName);

    private static CatalogProductTileVm Tile(string id, string title, string barcode, string? brand = null, double price = 100, params (string Code, string? Name)[] alts) =>
        new(id, title, price.ToString("0.00"), false)
        {
            Unit = "шт",
            Barcode = barcode,
            Brand = brand,
            AlternateBarcodeVariants = alts.Select(a => new AlternateBarcodeVariant { Barcode = a.Code, Name = a.Name }).ToList(),
        };

    private static InvoiceVariantPlan.Options Opts(bool merge, params CatalogProductTileVm[] catalog) => new()
    {
        Merge = merge,
        FindExisting = (b, br) => InvoiceVariantPlan.FindInCatalog(catalog, b, br),
        OwnerOfCode = code => InvoiceBarcodeRules.OwnerIn(catalog, code),
    };

    private static List<ProductActionPlan.Step> Asu(double p1 = 90, double p2 = 90, double p3 = 90) =>
    [
        Create("Асу", "Асу", "", "Асу", "4870000000011", 5, p1),
        Create("Асу со вкусом клубники", "Асу", "клубничный", "Асу", "4870000000028", 10, p2),
        Create("Асу лимон", "Асу", "лимонный", "Асу", "4870000000035", 7, p3),
    ];

    // ------------------------------------------------------------------ разбор ответа ИИ

    [Fact]
    public void Extract_reads_base_variant_and_brand()
    {
        var answer = "| № | Наименование | ... |\n"
            + "ТОВАР: {\"op\": \"create\", \"n\": 1, \"name\": \"Асу со вкусом клубники\", \"base\": \"Асу\", \"variant\": \"Клубничный\", \"brand\": \"Асу\", \"qty\": 10, \"purchase\": 90, \"price\": 110, \"barcode\": \"4870000000028\"}\n"
            + "ТОВАР: {\"op\": \"create\", \"n\": 2, \"name\": \"Чай чёрный\", \"qty\": 3, \"purchase\": 50, \"price\": 70}\n";
        var (_, steps, _) = ProductActionPlan.Extract(answer, "накладная");
        steps.Should().HaveCount(2);
        var first = steps[0];
        first.Base.Should().Be("Асу");
        first.Variant.Should().Be("Клубничный");
        first.Brand.Should().Be("Асу");
        first.InvoiceName.Should().Be("Асу со вкусом клубники");
        first.Barcode.Should().Be("4870000000028");
        steps[1].Base.Should().BeNull("ИИ не дал base — строка идёт как раньше");
        steps[1].Variants.Should().BeNull();
    }

    // ------------------------------------------------------------------ группировка

    [Fact]
    public void Asu_plain_strawberry_lemon_with_one_price_become_one_product()
    {
        var plan = InvoiceVariantPlan.Build(Asu(), Opts(true));
        plan.Entries.Should().ContainSingle();
        plan.Entries[0].IsGroup.Should().BeTrue();
        var steps = plan.ToSteps();
        steps.Should().ContainSingle();
        var step = steps[0];
        step.Op.Should().Be("create");
        step.Product.Title.Should().Be("Асу");
        step.Qty.Should().Be(22);
        step.Purchase.Should().Be(90);
        step.Barcode.Should().Be("4870000000011", "основной штрихкод — у обычного «Асу» (первый вариант)");
        var extra = InvoiceVariantPlan.ExtraCodes(step, step.Barcode);
        extra.Select(v => v.Barcode).Should().Equal("4870000000028", "4870000000035");
        extra.Select(v => v.Name).Should().Equal(["Клубничный", "Лимонный"], "название варианта — с заглавной буквы");
        plan.ProductCount.Should().Be(1);
        plan.ExtraCodeCount.Should().Be(2);
    }

    [Fact]
    public void Quantity_of_each_variant_is_kept_separately_and_noted()
    {
        var step = InvoiceVariantPlan.Build(Asu(), Opts(true)).ToSteps()[0];
        step.Variants!.Select(v => v.Qty).Should().Equal(5, 10, 7);
        var note = InvoiceVariantPlan.BreakdownNote(step);
        note.Should().Contain("Асу:").And.Contain("Клубничный 10").And.Contain("Лимонный 7").And.Contain("обычный 5");
    }

    [Fact]
    public void Sum_of_lines_is_not_changed_by_merging_with_different_but_close_costs()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу клубника", "Асу", "клубничный", "Асу", "4870000000028", 10, 90),
            Create("Асу лимон", "Асу", "лимонный", "Асу", "4870000000035", 10, 90.5),
        };
        var merged = InvoiceVariantPlan.Build(steps, Opts(true)).ToSteps();
        merged.Should().ContainSingle("90 и 90,5 — в пределах допуска 1 %: одна цена");
        (merged[0].Qty!.Value * merged[0].Purchase!.Value).Should().BeApproximately(10 * 90 + 10 * 90.5, 0.01, "средневзвешенная закупка сохраняет сумму накладной");
    }

    [Fact]
    public void Different_prices_are_not_merged_automatically_and_ask_to_merge()
    {
        var plan = InvoiceVariantPlan.Build(Asu(p3: 95), Opts(true));
        plan.Entries.Should().HaveCount(2, "«Асу» + «клубничный» по 90 — группа, «лимонный» по 95 — отдельно");
        var group = plan.Entries.Single(e => e.IsGroup);
        group.Members.Should().HaveCount(2);
        var single = plan.Entries.Single(e => !e.IsGroup);
        plan.PriceConflictFor(single).Should().NotBeNull("окно спросит «Объединить? цены разные»");
        plan.PriceConflictFor(single)!.Value.Other.Should().BeSameAs(group);

        plan.Merge(single, group).Should().BeTrue();
        plan.Entries.Should().ContainSingle();
        var step = plan.ToSteps().Single();
        step.Qty.Should().Be(22);
        (step.Qty!.Value * step.Purchase!.Value).Should().BeApproximately(5 * 90 + 10 * 90 + 7 * 95, 0.01);
        plan.PriceConflictFor(plan.Entries[0]).Should().BeNull();
    }

    [Fact]
    public void Only_two_lines_with_different_prices_stay_two_products()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу", "Асу", "", "Асу", "4870000000011", 5, 90),
            Create("Асу лимон", "Асу", "лимонный", "Асу", "4870000000035", 7, 95),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true));
        plan.Entries.Should().HaveCount(2);
        plan.ToSteps().Should().HaveCount(2);
        plan.ToSteps().Select(s => s.Variants).Should().OnlyContain(v => v == null);
        plan.Entries.Should().OnlyContain(e => plan.PriceConflictFor(e) != null);
    }

    [Fact]
    public void Different_brands_are_never_merged()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Чай Липтон лимон", "Чай", "лимонный", "Липтон", "4870000000011", 5, 50),
            Create("Чай Ахмад клубника", "Чай", "клубничный", "Ахмад", "4870000000028", 5, 50),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true));
        plan.Entries.Should().HaveCount(2);
        plan.Entries.Should().OnlyContain(e => !e.IsGroup);
    }

    [Fact]
    public void Lines_without_variant_are_not_merged()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу", "Асу", "", "Асу", "4870000000011", 5, 90),
            Create("Асу", "Асу", "", "Асу", "4870000000028", 5, 90),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true));
        plan.Entries.Should().HaveCount(2);
        plan.ToSteps().Select(s => s.Variants).Should().OnlyContain(v => v == null);
    }

    [Fact]
    public void Different_units_are_not_merged()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Рис пропаренный", "Рис", "пропаренный", "", "4870000000011", 5, 90, unit: "кг"),
            Create("Рис длиннозёрный", "Рис", "длиннозёрный", "", "4870000000028", 5, 90, unit: "шт"),
        };
        InvoiceVariantPlan.Build(steps, Opts(true)).Entries.Should().HaveCount(2);
    }

    [Fact]
    public void Steps_without_base_pass_through_unchanged()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Чай чёрный", null, null, null, "4870000000011", 3, 50),
            Create("Кофе", null, null, null, "4870000000028", 2, 80),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true));
        plan.HasAnything.Should().BeFalse();
        plan.ToSteps().Should().BeEquivalentTo(steps);
    }

    // ------------------------------------------------------------------ существующий товар

    [Fact]
    public void New_flavor_is_added_to_existing_product_as_extra_barcode_not_as_new_product()
    {
        var existing = Tile("p1", "Асу", "4870000000011", "Асу", 120);
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу со вкусом клубники", "Асу", "клубничный", "Асу", "4870000000028", 10, 90, 110),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true, existing));
        plan.Entries.Should().ContainSingle();
        plan.Entries[0].IsGroup.Should().BeTrue("вкус добавляется существующему товару");
        var step = plan.ToSteps().Single();
        step.Op.Should().Be("receive");
        step.Product.Id.Should().Be("p1");
        step.Qty.Should().Be(10);
        step.Variants.Should().ContainSingle(v => v.Barcode == "4870000000028" && v.Name == "клубничный");
        step.Price.Should().BeNull("цена ниже текущей приходом не ставится (110 < 120)");
        step.PriceNotLowered.Should().Be(110);

        var add = InvoiceVariantWriter.StepsFor(plan.ToSteps()).Single();
        add.Op.Should().Be("add_variants");
        add.Product.Id.Should().Be("p1");
        AdvisorCapabilities.ForOp("add_variants").Should().Be("bc.set");
    }

    [Fact]
    public void Existing_product_with_other_brand_is_not_taken()
    {
        var existing = Tile("p1", "Асу", "4870000000011", "Другая марка", 120);
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу клубника", "Асу", "клубничный", "Асу", "4870000000028", 10, 90),
            Create("Асу лимон", "Асу", "лимонный", "Асу", "4870000000035", 10, 90),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true, existing));
        plan.Entries.Should().ContainSingle();
        plan.Entries[0].Existing.Should().BeNull("марка товара склада другая");
        plan.ToSteps().Single().Op.Should().Be("create");
    }

    [Fact]
    public void Line_whose_code_belongs_to_another_product_is_taken_out_of_the_group()
    {
        var other = Tile("z9", "Совсем другой товар", "4870000000028");
        var steps = Asu();
        var plan = InvoiceVariantPlan.Build(steps, Opts(true, other));
        plan.Entries.Should().HaveCount(2);
        plan.Entries.Single(e => e.IsGroup).Members.Should().HaveCount(2);
        plan.Entries.Single(e => !e.IsGroup).Members[0].Barcode.Should().Be("4870000000028");
    }

    [Fact]
    public async Task Writer_adds_variants_with_names_and_keeps_existing_codes_and_skips_foreign_codes()
    {
        var existing = Tile("p1", "Асу", "4870000000011", "Асу", 120, ("4870000000099", "Мятный"));
        string? savedId = null;
        var added = new List<AlternateBarcodeVariant>();
        InvoiceBarcodeBinder.OwnerOverride = (code, _) => Task.FromResult(code switch
        {
            "4870000000042" => new InvoiceCodeOwner(Tile("z9", "Чужой товар", "4870000000042"), false),
            "4870000000099" => new InvoiceCodeOwner(existing, false),
            _ => new InvoiceCodeOwner(null, false),
        });
        InvoiceBarcodeBinder.ReadCardOverride = (_, _) => Task.FromResult<JsonElement?>(JsonDocument.Parse(
            "{\"barcode\":\"4870000000011\",\"alternate_barcodes\":[{\"barcode\":\"4870000000099\",\"name\":\"Мятный\",\"quantity\":0}]}").RootElement.Clone());
        // 2026-11-21: каждый новый вариант — отдельный POST products/<id>/barcodes/ (старые коды сервер не трогает)
        InvoiceBarcodeBinder.AddOverride = (id, v, _, _) =>
        {
            savedId = id;
            added.Add(v);
            return Task.FromResult(new BarcodeWriteOutcome(true));
        };
        var step = new ProductActionPlan.Step("add_variants", existing, null, null, null, Base: "Асу", Variants:
        [
            new VariantPart("клубничный", "4870000000028", 10, 90, "Асу клубника"),
            new VariantPart("лимонный", "4870000000042", 5, 90, "Асу лимон"),
            new VariantPart("мятный", "4870000000099", 3, 90, "Асу мята"),
        ]);
        var result = await InvoiceVariantWriter.AddAsync(step, "тест");
        savedId.Should().Be("p1");
        added.Select(d => d.Barcode).Should().Equal(["4870000000028"], "записывается только новый вариант: прежний код не трогается, чужой пропущен");
        added.Single().Name.Should().Be("Клубничный");
        result.Message.Should().Contain("Клубничный").And.Contain("пропущены").And.Contain("Чужой товар").And.Contain("уже были");
        result.Partial.Should().BeTrue();
    }

    // ------------------------------------------------------------------ команда «не объединяй» и переключатель

    [Fact]
    public void Merge_off_keeps_every_invoice_line_a_separate_step()
    {
        var steps = Asu();
        var plan = InvoiceVariantPlan.Build(steps, Opts(false));
        plan.Entries.Should().HaveCount(3);
        plan.HasAnything.Should().BeFalse();
        plan.ToSteps().Should().HaveCount(3);
        plan.ToSteps().Select(s => s.Product.Title).Should().Equal("Асу", "Асу со вкусом клубники", "Асу лимон");
    }

    [Theory]
    [InlineData("объединяй вкусы в один товар", true, null)]
    [InlineData("Объедини варианты в один товар", true, null)]
    [InlineData("склей вкусы", true, null)]
    [InlineData("не объединяй", false, null)]
    [InlineData("не объединяй вкусы", false, null)]
    [InlineData("не склеивай варианты", false, null)]
    [InlineData("каждый вкус отдельным товаром", false, null)]
    [InlineData("don't merge flavors", false, null)]
    [InlineData("birleştirme", false, null)]
    [InlineData("бириктирбе", false, null)]
    [InlineData("бренд — Асу", null, "бренд — Асу")]
    [InlineData("марка: Асу", null, "бренд — Асу")]
    public void Chat_phrases_are_recognized_as_command(string text, bool? merge, string? command)
    {
        var r = InvoiceVariantCommand.Detect(text);
        r.Should().NotBeNull();
        r!.Merge.Should().Be(merge);
        r.Command.Should().Be(command);
        r.Reset.Should().BeFalse();
    }

    [Theory]
    [InlineData("сбрось команду ИИ")]
    [InlineData("сбросить настройку накладных")]
    public void Reset_phrase(string text) => InvoiceVariantCommand.Detect(text)!.Reset.Should().BeTrue();

    [Theory]
    [InlineData("какая выручка сегодня")]
    [InlineData("сколько Асу осталось на складе")]
    [InlineData("прочитай накладную и оприходуй товары с наценкой 25 процентов, вкусы можно объединить если цена одинаковая и это очень длинная фраза про многое другое что точно не команда")]
    [InlineData("")]
    public void Other_phrases_are_not_commands(string text) => InvoiceVariantCommand.Detect(text).Should().BeNull();

    [Fact]
    public void Prefs_default_on_persist_per_folder_and_reset()
    {
        InvoiceVariantPrefs.Merge.Should().BeTrue("по умолчанию объединение включено");
        InvoiceVariantPrefs.Command.Should().BeEmpty();
        InvoiceVariantPrefs.Set(false, "  бренд — Асу \n ");
        InvoiceVariantPrefs.ResetCache();
        InvoiceVariantPrefs.Merge.Should().BeFalse("запомнено в файле");
        InvoiceVariantPrefs.Command.Should().Be("бренд — Асу");
        InvoiceVariantPrefs.CommandBrand.Should().Be("Асу");
        InvoiceVariantPrefs.Reset();
        InvoiceVariantPrefs.ResetCache();
        InvoiceVariantPrefs.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Prompt_has_variant_rules_and_the_owner_command_and_is_short_when_merge_is_off()
    {
        var on = InvoiceVariantPrefs.PromptAddition();
        on.Should().Contain("\"base\"").And.Contain("\"variant\"").And.Contain("\"brand\"").And.Contain("Асу со вкусом клубники").And.NotContain("КОМАНДА ВЛАДЕЛЬЦА");

        InvoiceVariantPrefs.Set(true, "бренд — Асу; цену не менять");
        InvoiceVariantPrefs.PromptAddition().Should().Contain("КОМАНДА ВЛАДЕЛЬЦА").And.Contain("бренд — Асу; цену не менять");

        InvoiceVariantPrefs.Set(false, "");
        var off = InvoiceVariantPrefs.PromptAddition();
        off.Should().Contain("не объединяй").And.NotContain("\"brand\"");

        InvoiceVariantPrefs.Set(true, "не объединяй");
        InvoiceVariantPrefs.EffectiveMerge.Should().BeFalse("своя команда сама просит не объединять");
        InvoiceVariantPrefs.PromptAddition().Should().Contain("base = name");
    }

    [Fact]
    public void Command_text_is_cleaned_and_limited()
    {
        var t = InvoiceVariantPrefs.CleanCommand("  а\n\nб\t" + new string('я', 400));
        t.Length.Should().BeLessThanOrEqualTo(InvoiceVariantPrefs.MaxCommandLength);
        t.Should().StartWith("а б ").And.NotContain("\n");
    }

    // ------------------------------------------------------------------ правки владельца

    [Fact]
    public void Split_one_variant_and_split_all()
    {
        var plan = InvoiceVariantPlan.Build(Asu(), Opts(true));
        var group = plan.Entries[0];
        plan.Split(group, group.Members[2]);
        plan.Entries.Should().HaveCount(2);
        plan.Entries[0].Members.Should().HaveCount(2);
        plan.Entries[1].Members[0].InvoiceName.Should().Be("Асу лимон");
        plan.ToSteps().Should().HaveCount(2);
        plan.ToSteps()[1].Product.Title.Should().Be("Асу лимон", "разъединённый вариант — отдельный товар с названием из накладной");

        plan.SplitAll(plan.Entries[0]);
        plan.Entries.Should().HaveCount(3);
        plan.ToSteps().Select(s => s.Variants).Should().OnlyContain(v => v == null);
        plan.ToSteps().Select(s => s.Qty).Should().Equal(5, 10, 7);
    }

    [Fact]
    public void Split_items_with_the_same_price_can_be_merged_again_without_price_conflict()
    {
        var plan = InvoiceVariantPlan.Build(Asu(), Opts(true));
        plan.SplitAll(plan.Entries[0]);
        plan.Entries.Should().HaveCount(3);
        plan.Entries.Should().OnlyContain(e => plan.PriceConflictFor(e) == null, "цены одинаковые — это не «цены разные»");
        plan.SplitMates().Should().HaveCount(3);
        plan.HasAnything.Should().BeTrue("блок остаётся ради «Объединить снова»");

        plan.MergeSplit();
        plan.Entries.Should().ContainSingle();
        plan.Entries[0].IsGroup.Should().BeTrue();
        plan.ToSteps().Single().Qty.Should().Be(22);
        plan.SplitMates().Should().BeEmpty();
    }

    [Fact]
    public void Rename_variant_changes_extra_code_name()
    {
        var plan = InvoiceVariantPlan.Build(Asu(), Opts(true));
        plan.RenameVariant(plan.Entries[0].Members[1], "  со вкусом   клубники ");
        var step = plan.ToSteps().Single();
        InvoiceVariantPlan.ExtraCodes(step, step.Barcode).Select(v => v.Name).Should().Contain("Со вкусом клубники");
    }

    [Fact]
    public void Merge_with_another_line_by_hand_derives_base_and_variant()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу", null, null, null, "4870000000011", 5, 90),
            Create("Асу со вкусом клубники", null, null, null, "4870000000028", 10, 90),
            Create("Чай", null, null, null, "4870000000035", 1, 30),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true));
        plan.Entries.Should().HaveCount(3);
        plan.MergeTargets(plan.Entries[1]).Should().HaveCount(2);
        plan.Merge(plan.Entries[1], plan.Entries[0]).Should().BeTrue();
        var group = plan.Entries[0];
        group.IsGroup.Should().BeTrue();
        group.Base.Should().Be("Асу");
        group.Members.Select(m => m.Variant).Should().Equal("", "клубники");
        plan.ToSteps().Should().HaveCount(2);
    }

    [Fact]
    public void Merge_refuses_lines_of_different_units()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Рис", null, null, null, "4870000000011", 5, 90, unit: "кг"),
            Create("Рис пропаренный", null, null, null, "4870000000028", 10, 90, unit: "шт"),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true));
        plan.Merge(plan.Entries[1], plan.Entries[0]).Should().BeFalse();
        plan.Entries.Should().HaveCount(2);
    }

    [Fact]
    public void Typed_main_code_makes_the_first_variant_code_an_extra_one()
    {
        var plan = InvoiceVariantPlan.Build(Asu(), Opts(true));
        plan.ApplyTypedCodes(new Dictionary<int, string> { [0] = "4870000000999" });
        var step = plan.ToSteps().Single();
        step.Barcode.Should().Be("4870000000999");
        InvoiceVariantPlan.ExtraCodes(step, step.Barcode).Select(v => v.Barcode).Should().Equal("4870000000011", "4870000000028", "4870000000035");
    }

    [Fact]
    public void Variant_without_valid_code_is_kept_in_the_product_but_gives_no_extra_code_and_a_note()
    {
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу", "Асу", "", "Асу", "4870000000011", 5, 90),
            Create("Асу клубника", "Асу", "клубничный", "Асу", null, 10, 90),
            Create("Асу лимон", "Асу", "лимонный", "Асу", "12", 7, 90),
        };
        var plan = InvoiceVariantPlan.Build(steps, Opts(true));
        var step = plan.ToSteps().Single();
        step.Qty.Should().Be(22, "количество вариантов без кода не теряется");
        InvoiceVariantPlan.ExtraCodes(step, step.Barcode).Should().BeEmpty();
        plan.Entries[0].Notes.Should().HaveCount(2);
    }

    [Fact]
    public void Brand_from_owner_command_fills_missing_brand()
    {
        var existing = Tile("p1", "Асу", "4870000000011", "Асу", 120);
        var steps = new List<ProductActionPlan.Step>
        {
            Create("Асу клубника", "Асу", "клубничный", null, "4870000000028", 10, 90),
        };
        var plan = InvoiceVariantPlan.Build(steps, new InvoiceVariantPlan.Options
        {
            Merge = true,
            BrandOverride = "Асу",
            FindExisting = (b, br) => br == "Асу" ? InvoiceVariantPlan.FindInCatalog([existing], b, br) : null,
            OwnerOfCode = _ => null,
        });
        plan.Entries[0].Existing.Should().BeSameAs(existing);
    }

    // ------------------------------------------------------------------ в закупку

    [Fact]
    public void Merged_step_reaches_purchase_draft_with_extra_barcodes_and_note()
    {
        // Окно закупки (InvoiceEditorWindow.PurchaseDraft) — проект приложения; здесь проверяем то, что оно берёт из шага: доп. коды и разбивку.
        var step = InvoiceVariantPlan.Build(Asu(), Opts(true)).ToSteps().Single();
        var line = new NurMarketKassa.Models.ReceivingLineVm { ExtraBarcodes = InvoiceVariantPlan.ExtraCodes(step, step.Barcode), VariantNote = InvoiceVariantPlan.BreakdownNote(step) };
        line.ExtraBarcodes.Should().HaveCount(2);
        line.VariantNote.Should().Contain("Клубничный 10");
        var draft = line.ToDraft();
        draft.VariantNote.Should().Be(line.VariantNote);
        NurMarketKassa.Models.ReceivingLineVm.FromDraft(draft).VariantNote.Should().Be(line.VariantNote, "разбивка переживает черновик и очередь");
    }
}
