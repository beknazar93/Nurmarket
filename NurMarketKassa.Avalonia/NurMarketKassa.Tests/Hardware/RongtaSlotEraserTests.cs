using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-21 («старые ячейки стирать», Rongta): правила стирания ячеек PLU — строка пустой карточки «!0V», разбор списка номеров, что стирать и что защищено, запоминание старых ячеек.
/// Тесты чистые, на весы ничего не шлют.</summary>
public sealed class RongtaSlotEraserTests
{
    // ------------------------------------------------------------------ строка пустой карточки

    [Fact]
    public void Blank_command_is_an_ordinary_0V_line_with_empty_name_code_zero_and_price_zero()
    {
        var line = RongtaSlotEraser.BuildBlankCommand(37, priceDecimals: 2);

        line.Should().StartWith("!0V0037A");
        line.Should().EndWith("B0000C0000D0000E\r\n"); // имя и обе заметки пусты: только «0000» Rongta в конце поля
        var fixedPart = line["!0V0037A".Length..line.IndexOf('B')];
        fixedPart.Should().HaveLength(DahuaTmProtocol.FixedPartLength);
        fixedPart.Should().MatchRegex("^0+$"); // код 0, цена 0, срок 0, префикс 0, тара 0
    }

    [Theory]
    [InlineData(1, "!0V0001A")]
    [InlineData(4000, "!0V4000A")]
    public void Blank_command_carries_the_slot_number_in_four_digits(int slot, string head) =>
        RongtaSlotEraser.BuildBlankCommand(slot, 0).Should().StartWith(head);

    [Fact]
    public void Blank_command_differs_from_a_real_card_only_in_the_fields_that_hold_the_goods()
    {
        var real = DahuaTmProtocol.BuildPluCommand(new DahuaTmPlu { PluNumber = 5, ProductCode = 5, Price = 160m, Name = "Яблоки", BarcodePrefix = 20 }, 2, codec: DahuaTmNameCodec.Rongta);
        var blank = RongtaSlotEraser.BuildBlankCommand(5, 2);
        blank.Should().NotBe(real);
        blank.Length.Should().BeLessThan(real.Length);
        blank.Should().StartWith("!0V0005A"); // та же ячейка
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4001)]
    [InlineData(-3)]
    public void Blank_command_for_a_slot_outside_the_scale_table_is_refused(int slot)
    {
        var act = () => RongtaSlotEraser.BuildBlankCommand(slot, 2);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Blank_record_passes_the_ordinary_protocol_check_for_any_price_point()
    {
        for (var decimals = 0; decimals <= 3; decimals++)
            DahuaTmProtocol.Validate(RongtaSlotEraser.BlankRecord(100), decimals).Should().Be(DahuaTmPluProblem.None);
    }

    // ------------------------------------------------------------------ разбор списка номеров

    [Fact]
    public void Slot_list_accepts_numbers_ranges_and_mixed_separators_sorted_without_duplicates()
    {
        RongtaSlotEraser.TryParseSlots("5, 10-12; 33 7 11", out var slots).Should().Be(RongtaSlotListProblem.None);
        slots.Should().Equal(5, 7, 10, 11, 12, 33);
    }

    [Fact]
    public void Slot_list_understands_long_dashes_and_a_reversed_range()
    {
        RongtaSlotEraser.TryParseSlots("20–18", out var slots).Should().Be(RongtaSlotListProblem.None);
        slots.Should().Equal(18, 19, 20);
        RongtaSlotEraser.TryParseSlots("1—3", out slots).Should().Be(RongtaSlotListProblem.None);
        slots.Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ,  ; ")]
    public void Slot_list_empty_is_a_problem(string? text) =>
        RongtaSlotEraser.TryParseSlots(text, out _).Should().Be(RongtaSlotListProblem.Empty);

    [Theory]
    [InlineData("abc")]
    [InlineData("5-")]
    [InlineData("-5")]
    [InlineData("1-2-3")]
    [InlineData("5, x")]
    [InlineData("1.5")]
    [InlineData("-")]
    public void Slot_list_garbage_is_a_problem_and_returns_no_slots(string text)
    {
        RongtaSlotEraser.TryParseSlots(text, out var slots).Should().Be(RongtaSlotListProblem.BadToken);
        slots.Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("4001")]
    [InlineData("3990-4005")]
    [InlineData("0-5")]
    public void Slot_list_outside_1_to_4000_is_a_problem(string text) =>
        RongtaSlotEraser.TryParseSlots(text, out _).Should().Be(RongtaSlotListProblem.OutOfRange);

    [Fact]
    public void Slot_list_full_table_range_is_allowed()
    {
        RongtaSlotEraser.TryParseSlots("1-4000", out var slots).Should().Be(RongtaSlotListProblem.None);
        slots.Should().HaveCount(4000);
        slots.Should().Equal(RongtaSlotEraser.AllSlots());
    }

    // ------------------------------------------------------------------ план: что стереть, что защищено

    [Fact]
    public void Plan_skips_protected_slots_and_reports_them_separately()
    {
        var plan = RongtaSlotEraser.Plan(new[] { 3, 1, 2, 2, 7, 9 }, new HashSet<int> { 2, 9 });

        plan.Slots.Should().Equal(1, 3, 7);
        plan.SkippedProtected.Should().Equal(2, 9);
    }

    [Fact]
    public void Plan_never_includes_a_ticked_products_slot_even_in_all_mode()
    {
        var ticked = new HashSet<int> { 1, 17, 4000 };
        var plan = RongtaSlotEraser.Plan(RongtaSlotEraser.AllSlots(), ticked);

        plan.Slots.Should().HaveCount(4000 - 3);
        plan.Slots.Should().NotContain(new[] { 1, 17, 4000 });
        plan.SkippedProtected.Should().Equal(1, 17, 4000);
    }

    [Fact]
    public void Plan_drops_numbers_outside_the_table()
    {
        var plan = RongtaSlotEraser.Plan(new[] { 0, -1, 5, 4001 }, new HashSet<int>());
        plan.Slots.Should().Equal(5);
        plan.SkippedProtected.Should().BeEmpty();
    }

    [Fact]
    public void Plan_of_nothing_is_empty()
    {
        var plan = RongtaSlotEraser.Plan(Array.Empty<int>(), new HashSet<int> { 1 });
        plan.Slots.Should().BeEmpty();
        plan.SkippedProtected.Should().BeEmpty();
    }

    // ------------------------------------------------------------------ старые ячейки после отправки

    private static ScaleSentPlu Sent(int plu, string name) => new() { Plu = plu, Name = name, Code = plu.ToString() };

    [Fact]
    public void Moved_product_leaves_its_old_slot_in_the_stale_list_with_the_old_name()
    {
        var lastSent = new Dictionary<string, ScaleSentPlu> { ["a"] = Sent(5, "Яблоки"), ["b"] = Sent(6, "Груши") };
        var writtenNow = new Dictionary<string, ScaleSentPlu> { ["a"] = Sent(12, "Яблоки"), ["b"] = Sent(6, "Груши") };
        var plan = ScalePluPlanner.PlanMoves(lastSent, writtenNow);
        var stale = new Dictionary<int, string>();

        RongtaSlotEraser.RememberStale(stale, plan.SlotsToClear, lastSent);
        RongtaSlotEraser.Forget(stale, writtenNow.Values.Select(v => v.Plu));

        stale.Should().Equal(new Dictionary<int, string> { [5] = "Яблоки" });
    }

    [Fact]
    public void Old_slot_that_another_product_now_occupies_is_not_stale()
    {
        // товар a переехал 5 → 12, а в ячейку 5 в эту же отправку записан товар c
        var lastSent = new Dictionary<string, ScaleSentPlu> { ["a"] = Sent(5, "Яблоки") };
        var writtenNow = new Dictionary<string, ScaleSentPlu> { ["a"] = Sent(12, "Яблоки"), ["c"] = Sent(5, "Дыня") };
        var plan = ScalePluPlanner.PlanMoves(lastSent, writtenNow);
        var stale = new Dictionary<int, string>();

        RongtaSlotEraser.RememberStale(stale, plan.SlotsToClear, lastSent);
        RongtaSlotEraser.Forget(stale, writtenNow.Values.Select(v => v.Plu));

        stale.Should().BeEmpty();
    }

    [Fact]
    public void Writing_a_product_into_a_remembered_stale_slot_forgets_it()
    {
        var stale = new Dictionary<int, string> { [5] = "Яблоки", [8] = "Сливы" };
        RongtaSlotEraser.Forget(stale, new[] { 5, 99 });
        stale.Should().Equal(new Dictionary<int, string> { [8] = "Сливы" });
    }

    [Fact]
    public void Stale_candidates_are_remembered_slots_plus_slots_of_products_missing_from_the_catalog()
    {
        var stale = new Dictionary<int, string> { [5] = "Яблоки" };
        var lastSent = new Dictionary<string, ScaleSentPlu>
        {
            ["live"] = Sent(7, "Груши"),
            ["gone"] = Sent(9, "Старый товар"),
            ["gone-dup"] = Sent(5, "Другое имя"), // номер уже в запомненных — имя из запомненных
        };

        var result = RongtaSlotEraser.StaleCandidates(stale, lastSent, id => id == "live");

        result.Should().Equal(new Dictionary<int, string> { [5] = "Яблоки", [9] = "Старый товар" });
        result.Keys.Should().BeInAscendingOrder();
    }

    [Fact]
    public void Without_a_loaded_catalog_nothing_is_called_missing()
    {
        var lastSent = new Dictionary<string, ScaleSentPlu> { ["x"] = Sent(9, "Товар") };
        RongtaSlotEraser.StaleCandidates(new Dictionary<int, string>(), lastSent, null).Should().BeEmpty();
    }

    [Fact]
    public void Erasing_removes_the_products_of_those_slots_from_the_sent_list()
    {
        // то же, что делает окно после успешного стирания: что на весах — без стёртых ячеек
        var sent = new Dictionary<string, ScaleSentPlu> { ["a"] = Sent(5, "Яблоки"), ["b"] = Sent(6, "Груши") };
        ScalePluPlanner.ApplySent(sent, new Dictionary<string, ScaleSentPlu>(), new[] { 5 });
        sent.Keys.Should().Equal("b");
    }

    // ------------------------------------------------------------------ подпись номеров

    [Theory]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 7 }, "7")]
    [InlineData(new[] { 7, 8 }, "7, 8")]
    [InlineData(new[] { 1, 2, 3, 4, 5, 7, 10, 11, 12 }, "1–5, 7, 10–12")]
    public void Slots_are_written_compactly(int[] slots, string expected) =>
        RongtaSlotEraser.FormatSlots(slots).Should().Be(expected);

    [Fact]
    public void Long_slot_lists_are_cut_with_an_ellipsis()
    {
        var slots = Enumerable.Range(1, 100).Select(i => i * 2).ToList(); // 2, 4, 6 … — без соседних номеров
        var text = RongtaSlotEraser.FormatSlots(slots, maxItems: 5);
        text.Should().Be("2, 4, 6, 8, 10, …");
    }
}
