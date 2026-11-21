using FluentAssertions;
using NurMarketKassa.ViewModels.Main;
using Xunit;

namespace NurMarketKassa.Tests.Basket;

/// <summary>2026-11-21 (жалоба «зависание при переключении вкладок», оптимизация): вкладки чеков обновляются на месте — плитки не пересоздаются на каждое добавление товара
/// и каждое переключение; суммы неактивных вкладок берутся из запомненного итога (разбор JSON чека — один раз на снимок).</summary>
[Collection("Wave5Database")]
public sealed class ReceiptTabsInPlaceTests
{
    [Fact]
    public void Adding_goods_and_switching_keeps_the_same_tab_objects_and_right_totals()
    {
        using var h = new BasketVmHarness();
        h.AddProduct("t1", "Хлеб", 50);
        h.Vm.CreateNewReceipt();
        h.AddProduct("t2", "Молоко", 80);
        h.Vm.ReceiptTabs.Should().HaveCount(2);
        var tabs = h.Vm.ReceiptTabs.ToList();

        h.AddProduct("t3", "Сыр", 120);
        h.Vm.ReceiptTabs.Should().Equal(tabs, "набор вкладок тот же — плитки не пересоздаются");
        h.Vm.ReceiptTabs[1].IsActive.Should().BeTrue();
        h.Vm.ReceiptTabs[1].Title.Should().Contain("200", "80 + 120 в активном чеке");
        h.Vm.ReceiptTabs[0].Title.Should().Contain("50");

        h.Vm.SelectReceiptTabCommand.Execute(h.Vm.ReceiptTabs[0]);
        h.Vm.ReceiptTabs.Should().Equal(tabs);
        h.Vm.ReceiptTabs[0].IsActive.Should().BeTrue();
        h.Vm.ReceiptTabs[1].IsActive.Should().BeFalse();
        h.Vm.ReceiptTabs[1].Title.Should().Contain("200", "неактивная вкладка показывает свой итог из запомненного снимка");

        h.AddProduct("t4", "Чай", 30);
        h.Vm.ReceiptTabs[0].Title.Should().Contain("80", "50 + 30; запомненный итог неактивной вкладки не мешает активной");
        h.Vm.ReceiptTabs[1].Title.Should().Contain("200");
    }

    [Fact]
    public void Opening_or_closing_a_receipt_rebuilds_the_set_of_tabs()
    {
        using var h = new BasketVmHarness();
        h.AddProduct("t1", "Хлеб", 50);
        h.Vm.CreateNewReceipt();
        h.Vm.ReceiptTabs.Should().HaveCount(2);
        h.AddProduct("t2", "Молоко", 80); // пустой чек второй новый не открывает
        h.Vm.CreateNewReceipt();
        h.Vm.ReceiptTabs.Should().HaveCount(3);
        h.Vm.ReceiptTabs.Count(t => t.IsActive).Should().Be(1);
    }
}
