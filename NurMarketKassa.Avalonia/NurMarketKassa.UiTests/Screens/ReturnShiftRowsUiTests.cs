using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-13 (проверка денег перед 1.17.86, п. 7): раскладка «в ящике должно быть» (окно закрытия смены и X-отчёт) показывает сторно возврата прошлой смены отдельной строкой
/// «Отмена возврата прошлой смены +N», не прячет её и сходится в итог (раньше при отрицательной сумме возвратов строка пропадала, а разница висела «Корректировкой»).</summary>
[Collection("UiScenarios")]
public sealed class ReturnShiftRowsUiTests
{
    private static List<string> Texts(Panel panel) => panel.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    [AvaloniaFact]
    public void DrawerBreakdown_PreviousShiftReversal_SeparateRow_SumsToExpected()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // начало 1000, наличные продажи 500, отмена вчерашнего возврата 400 наличными (в ящик +400) → должно быть 1900
        var panel = new StackPanel();
        DrawerBreakdownBuilder.Fill(new Window(), panel, 1000m, 500m, 0m, 0m, 0m, returns: -400m, expected: 1900m, previousShiftReturns: -400m);

        var texts = Texts(panel);
        texts.Should().Contain(t => t.Contains("Отмена возврата прошлой смены"));
        texts.Should().Contain(t => t.StartsWith("+400"), "отмена вернула 400 в ящик — со знаком «+»");
        texts.Should().NotContain(t => t.Contains("Корректировка"), "итог сходится без «Корректировки»: 1000 + 500 + 400 = 1900");
        texts.Should().NotContain(t => t.Contains("Возвраты наличными"), "возвратов этой смены нет");
    }

    [AvaloniaFact]
    public void DrawerBreakdown_ThisShiftReturnAndPreviousReversal_BothRows()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        // возврат этой смены 100 (−100) и отмена вчерашнего 400 (+400): returns = 100 − 400 = −300 в итоге
        var panel = new StackPanel();
        DrawerBreakdownBuilder.Fill(new Window(), panel, 1000m, 500m, 0m, 0m, 0m, returns: -300m + 0m, expected: 1800m, previousShiftReturns: -400m);

        // returns − previous = −300 + 400 = +100 — возвраты этой смены
        var texts = Texts(panel);
        texts.Should().Contain(t => t.Contains("Возвраты наличными"));
        texts.Should().Contain(t => t.Contains("Отмена возврата прошлой смены"));
        texts.Should().NotContain(t => t.Contains("Корректировка"));
    }

    [AvaloniaFact]
    public void DrawerBreakdown_NoPreviousShift_AsBefore()
    {
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var panel = new StackPanel();
        DrawerBreakdownBuilder.Fill(new Window(), panel, 1000m, 500m, 0m, 0m, 0m, returns: 100m, expected: 1400m);

        var texts = Texts(panel);
        texts.Should().Contain(t => t.Contains("Возвраты наличными"));
        texts.Should().NotContain(t => t.Contains("прошлой смены"));
    }
}
