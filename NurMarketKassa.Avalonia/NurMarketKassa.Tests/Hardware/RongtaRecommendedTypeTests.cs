using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-13 (проверено вживую на RLS1000: тип 07 решил «нет штрихкода при сумме > 1000 сом»): выбор рекомендуемого типа Rongta — когда окно показывает кнопку
/// «Исправить: тип 07», порядок списка типов (07 первым), умолчание новых настроек. Тесты чистые, на весы ничего не шлют.</summary>
[Collection("UserPreferences")]
public sealed class RongtaRecommendedTypeTests
{
    private static RongtaBarcodeFormat.BarcodeType Type(int n) => RongtaBarcodeFormat.Find(n)!;

    [Fact]
    public void Recommended_is_07_by_weight_with_prefix_from_department_and_grams()
    {
        var t = RongtaBarcodeFormat.RecommendedType;
        t.Type.Should().Be(7);
        t.Value.Should().Be(RongtaBarcodeFormat.ValueKind.Weight);
        t.Prefix.Should().Be(RongtaBarcodeFormat.PrefixKind.Department2);
        t.GramsPerUnit.Should().Be(1);
        // живой пример владельца: «Айфон» PLU 11, 5,165 кг, 1074,32 сом → 2000011051655
        RongtaBarcodeFormat.BuildSample(t, 20, 11, 5165, 1074.32m).Should().Be("2000011051655");
    }

    [Theory]
    [InlineData(2, true, true)]   // по сумме, 5 цифр: предел суммы
    [InlineData(2, false, true)]
    [InlineData(3, true, true)]   // по сумме, 7 цифр: предел всё равно есть
    [InlineData(10, true, true)]  // без веса и суммы
    [InlineData(27, false, true)] // касса не читает пример (отдел из одной цифры → «00…»)
    [InlineData(7, true, false)]  // уже 07
    [InlineData(7, false, false)] // 07 исправлять нечем
    [InlineData(17, true, false)] // по весу и касса читает — исправлять не нужно
    [InlineData(17, false, true)]
    public void Fix_button_is_for_any_type_the_till_cannot_read_or_that_has_an_amount_limit(int type, bool kassaReads, bool expected) =>
        RongtaBarcodeFormat.NeedsRecommendedFix(Type(type), kassaReads).Should().Be(expected);

    [Fact]
    public void Picker_lists_07_first_then_the_rest_in_order_with_00_to_09_present()
    {
        var order = RongtaBarcodeFormat.PickerOrder();
        order.Should().HaveCount(RongtaBarcodeFormat.Types.Count);
        order[0].Type.Should().Be(7);
        order.Skip(1).Select(t => t.Type).Should().BeInAscendingOrder();
        order.Select(t => t.Type).Should().Contain(Enumerable.Range(0, 10));
        order.Select(t => t.Type).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void New_settings_default_to_type_07_and_department_20()
    {
        var prefs = new UserPreferences();
        prefs.RongtaBarcodeType.Should().Be(7);
        prefs.RongtaServerBarcodeType.Should().Be(7, "у новых настроек касса сразу пишет 07 в карточки");
        prefs.RongtaLanBarcodePrefix.Should().Be(20);
    }
}
