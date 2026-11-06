using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Ui;

/// <summary>
/// 2026-11-06, А-8 (отчёт «Аналогичные баги — прыгающие кнопки»): меню программы владельца пересобиралось целиком от каждого события прав/тарифа,
/// хотя набор пунктов не менялся — левая колонка «мигала» и значки прыгали. Теперь пересборка — только при изменении подписи состава
/// (OwnerNavSignature). Заодно проверяется раскладка верха колонки (OwnerRailTopSlot): место под «»» не зависит от раскрытия колонки пунктов.
/// Чистая логика без интерфейса: можно запускать tools/qa/test-safe.ps1.
/// </summary>
public sealed class OwnerNavSignatureTests
{
    private static string Sign(Action<OwnerNavSignature> fill)
    {
        var s = new OwnerNavSignature();
        fill(s);
        return s.ToString();
    }

    private static void Menu(OwnerNavSignature s, bool withDebts = true, string overviewTitle = "Сводка")
    {
        s.Group("home", "Главная");
        s.Item("overview", overviewTitle);
        s.Group("sales", "Продажи");
        s.Item("sales", "Продажи и финансы");
        if (withDebts)
            s.Item("debts", "Долги клиентов");
    }

    [Fact]
    public void Same_composition_gives_same_signature_and_no_rebuild()
    {
        var first = Sign(s => Menu(s));
        var second = Sign(s => Menu(s));
        second.Should().Be(first);
        OwnerNavSignature.NeedsRebuild(first, second).Should().BeFalse("событие без изменения состава не должно пересоздавать колонку");
    }

    [Fact]
    public void First_build_always_rebuilds()
    {
        OwnerNavSignature.NeedsRebuild(null, Sign(s => Menu(s))).Should().BeTrue();
    }

    [Fact]
    public void Hidden_or_tariff_locked_item_changes_signature()
    {
        var before = Sign(s => Menu(s, withDebts: true));
        var after = Sign(s => Menu(s, withDebts: false));
        OwnerNavSignature.NeedsRebuild(before, after).Should().BeTrue("пункт «Долги» скрыт тарифом/«Разделами меню» — меню нужно пересобрать");
    }

    [Fact]
    public void Language_change_changes_signature()
    {
        var ru = Sign(s => Menu(s));
        var en = Sign(s => Menu(s, overviewTitle: "Overview"));
        OwnerNavSignature.NeedsRebuild(ru, en).Should().BeTrue();
    }

    [Fact]
    public void Group_with_all_items_hidden_does_not_appear_in_signature()
    {
        // Группа входит в подпись только с первым видимым пунктом (так же, как в меню).
        var s = new OwnerNavSignature();
        s.Group("home", "Главная");
        s.Item("overview", "Сводка");
        s.ToString().Should().NotContain("sales");
    }

    [Fact]
    public void Item_moved_between_groups_changes_signature()
    {
        var a = Sign(s => { s.Group("g1", "Один"); s.Item("x", "X"); s.Group("g2", "Два"); s.Item("y", "Y"); });
        var b = Sign(s => { s.Group("g1", "Один"); s.Item("x", "X"); s.Item("y", "Y"); });
        OwnerNavSignature.NeedsRebuild(a, b).Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false, false, true)]   // новый вид, колонка пунктов свёрнута: кнопка «»» видна
    [InlineData(true, false, false, false)]   // колонка пунктов раскрыта: кнопка прозрачна, но место занято
    public void Expand_button_slot_does_not_depend_on_open_column(bool navItemsOpen, bool phone, bool tabs, bool expectedShown)
    {
        OwnerRailTopSlot.ReservesSpace(phone, tabs).Should().BeTrue("место под «»» занято всегда — высота верха колонки не меняется");
        OwnerRailTopSlot.ButtonShown(navItemsOpen, phone, tabs).Should().Be(expectedShown);
    }

    [Theory]
    [InlineData(true, false)]   // телефон
    [InlineData(false, true)]   // «Вкладки»
    public void Expand_button_slot_is_not_reserved_on_phone_and_tabs(bool phone, bool tabs)
    {
        OwnerRailTopSlot.ReservesSpace(phone, tabs).Should().BeFalse();
        OwnerRailTopSlot.ButtonShown(false, phone, tabs).Should().BeFalse();
    }
}
