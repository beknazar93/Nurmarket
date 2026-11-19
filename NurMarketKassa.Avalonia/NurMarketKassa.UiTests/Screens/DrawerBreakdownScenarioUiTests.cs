using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (ночная проверка, Д-2): сценарий «продажа наличными 50 + продажа в долг 100 + офлайн-чек наличными 30, все три полностью возвращены».
/// Сервер убрал возвращённые продажи из продаж и из «ожидаемой суммы» (45 = начало 45), в журнале кассы наличных возвратов 80 (долговой возврат идёт в счёт долга, ящик не трогает).
/// Раскладка должна показать «+80 / −80» с пояснением и сойтись в 45 без «Корректировки»; тот же ящик без возвратов — без пояснения.</summary>
[Collection("UiScenarios")]
public sealed class DrawerBreakdownScenarioUiTests
{
    private static List<string> Texts(Panel panel) => panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void FullyReturnedCashDebtOffline_ShowsGrossAndReturns_TotalUnchanged_WithNote()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var panel = new StackPanel();
        DrawerBreakdownBuilder.Fill(new Window(), panel, opening: 45m, cashSales: 0m, debtPaidCash: 0m, deposits: 0m, withdrawals: 0m, returns: 80m, expected: 45m);

        var texts = Texts(panel);
        texts.Should().Contain(t => t.StartsWith("+80,00") || t.StartsWith("+80.00") || t.StartsWith("+80 "), "наличные продажи — валовые, с вернувшимися чеками");
        texts.Should().Contain(t => t.Contains("Возвраты наличными"));
        texts.Should().Contain(t => t.StartsWith("−80"), "наличный возврат 80");
        texts.Should().NotContain(t => t.Contains("Корректировка"), "45 + 80 − 80 = 45, итог ящика не меняется");
        texts.Should().Contain(t => t.StartsWith("Здесь только наличные"), "пояснение, почему «+80/−80», а карточка «Возвраты» может быть −180");
        texts.Should().Contain(t => t.StartsWith("45"), "итог «В ящике должно быть» 45");
    }

    [AvaloniaFact]
    public void NoReturns_NoExplanationNote()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var panel = new StackPanel();
        DrawerBreakdownBuilder.Fill(new Window(), panel, opening: 45m, cashSales: 50m, debtPaidCash: 0m, deposits: 0m, withdrawals: 0m, returns: 0m, expected: 95m);

        Texts(panel).Should().NotContain(t => t.StartsWith("Здесь только наличные"));
    }
}
