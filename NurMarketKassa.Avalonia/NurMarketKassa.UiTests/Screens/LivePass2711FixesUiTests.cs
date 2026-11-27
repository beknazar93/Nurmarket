using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-27 (исправления после живого прохода): A — Z сразу после закрытия смены без ответа сервера: итоги сервера до закрытия, пометка
/// «данные уточняются», без ложной «Корректировки» и «Остатка долга»; отчёт закрытой смены с подставного сервера (с задержкой) сам заменяет цифры.
/// J — строка «Использовано: …» только когда советник вызывал инструменты. H — вкладки «Весов» переключаются без мыши (IsChecked и стрелки).</summary>
[Collection("UiScenarios")]
public sealed class LivePass2711FixesUiTests
{
    private static string N(string? s) => (s ?? "").Replace('\u00A0', ' ').Replace('\u202F', ' ');

    private static ServerShiftReport Report(string shiftId, string status) => ServerShiftReport.TryParse(JsonDocument.Parse(ReportJson(shiftId, status)).RootElement)!;

    private static string ReportJson(string shiftId, string status) => $$"""
        {"shift":"{{shiftId}}","status":"{{status}}","opening_cash":"500.00","sales_count":13,"sales_total":"1189.75",
         "by_payment":{"cash":"880.00","transfer":"234.75","mixed_cash":"50.00","mixed_card":"25.00","debt":"0"},
         "cash_sales":"930.00","noncash_sales":"259.75","debt_payments_cash":"65.00","returns_total":"834.90",
         "deposits":"0","withdrawals":"0","expected_cash":"{{(status == "closed" ? "660.10" : "600.00")}}"{{(status == "closed" ? ",\"counted_cash\":\"660.10\",\"difference\":\"0\"" : "")}}}
        """;

    [AvaloniaFact]
    public async Task ZAfterCloseTimeout_ShowsServerFigures_Provisional_ThenUpdatesFromSlowServer()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        UiFake.Reset();
        var shiftId = Guid.NewGuid().ToString();
        // Подставной сервер: отчёт закрытой смены отвечает с задержкой 1,5 с (как NBS после закрытия).
        UiFake.Server.Json("GET", $"/api/construction/shifts/{shiftId}/report/", 200, ReportJson(shiftId, "closed"), TimeSpan.FromMilliseconds(1500));
        var savedDelay = ShiftDetailsDialog.ProvisionalRetryDelay;
        ShiftDetailsDialog.ProvisionalRetryDelay = TimeSpan.FromMilliseconds(200);
        var shift = new ShiftModel
        {
            Id = shiftId, ShiftNumber = shiftId, Status = "Закрыта", Cashier = "Тест", OpenedAt = DateTime.Now.AddHours(-8), ClosedAt = DateTime.Now,
            Revenue = 1189.75m, CashSales = 930m, NonCashSales = 259.75m, DebtSales = 0m, SalesCount = 13, OpeningCash = 500m, ClosingCash = 660.10m,
            ExpectedCash = 660.10m, // «в ящике должно быть» из окна закрытия
        };
        var dialog = new ShiftDetailsDialog(shift) { RefreshFromServer = false, Provisional = true, PreCloseReport = Report(shiftId, "open") };
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(1366, 768));
            UiShot.Settle(4);
            var note = UiDrive.Find<TextBlock>(dialog, "LocalDataNote");
            note.IsVisible.Should().BeTrue();
            note.Text.Should().Contain("уточняются");
            dialog.IsProvisional.Should().BeTrue();
            N(UiDrive.Find<TextBlock>(dialog, "RevenueText").Text).Should().Contain("1 189,75");
            N(UiDrive.Find<TextBlock>(dialog, "DebtText").Text).Should().StartWith("0,00", "долг оплачен — не «65»");
            UiDrive.AllText(dialog).Should().NotContain("Корректировка", "раскладка ящика складывается без заплатки");

            var until = DateTime.UtcNow.AddSeconds(20);
            while (dialog.IsProvisional && DateTime.UtcNow < until)
            {
                await Task.Delay(100);
                UiShot.Pump();
            }
            dialog.IsProvisional.Should().BeFalse("сервер отдал отчёт закрытой смены — окно обновилось само");
            note.Text.Should().Contain("обновлены");
            N(UiDrive.Find<TextBlock>(dialog, "DrawerExpectedText").Text).Should().Contain("660,10");
        }
        finally
        {
            ShiftDetailsDialog.ProvisionalRetryDelay = savedDelay;
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task Advisor_NoToolCalls_NoUsedLine_ToolCall_HasLine()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var dispatcher = new AdvisorToolDispatcher(new AdvisorToolHost());
        dispatcher.Used.Should().BeEmpty("вопрос решён сводкой — инструменты не вызывались, строки «Использовано» быть не должно");
        await dispatcher.CallAsync(AdvisorToolDispatcher.ListSections, "{}", CancellationToken.None);
        dispatcher.Used.Should().HaveCount(1);

        var window = new AiAdvisorWindow();
        try
        {
            using var _ = UiShot.Render(window, new UiSize(1024, 600));
            UiShot.Settle(4);
            var add = typeof(AiAdvisorWindow).GetMethod("AddBubble", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var used = typeof(AiAdvisorWindow).GetMethod("AddToolsUsedLine", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var body = add.Invoke(window, new object?[] { "Больше всех должен Атай — 3 950 сом.", false })!;
            used.Invoke(window, new object?[] { body, Array.Empty<string>() });
            UiShot.Settle(2);
            window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "AdvisorToolsUsed").Should().BeFalse();
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task ScaleSettings_Tabs_SwitchByIsCheckedAndArrows_HaveNames()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var view = new NurMarketKassa.AvaloniaHost.Views.Settings.ScaleSettingsView();
        var host = new Window { Content = view };
        try
        {
            using var _ = UiShot.Render(host, new UiSize(1366, 768));
            UiShot.Settle(4);
            var weighing = UiDrive.Find<RadioButton>(host, "TabWeighingButton");
            var display = UiDrive.Find<RadioButton>(host, "TabDisplayButton");
            foreach (var tab in new[] { weighing, display, UiDrive.Find<RadioButton>(host, "TabBarcodeButton") })
                Avalonia.Automation.AutomationProperties.GetName(tab).Should().NotBeNullOrWhiteSpace("диктор читает имя вкладки");

            // Как UI Automation (выбор элемента): только IsChecked, без щелчка.
            display.IsChecked = true;
            UiShot.Settle(2);
            UiDrive.Find<Control>(host, "DisplaySection").IsVisible.Should().BeTrue("раздел следует за выбранной вкладкой");

            // Клавиатура: Home — первая видимая вкладка.
            display.Focus();
            display.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Home, Source = display });
            UiShot.Settle(2);
            weighing.IsChecked.Should().BeTrue();
            UiDrive.Find<Control>(host, "WeighingSection").IsVisible.Should().BeTrue();
            UiDrive.Find<Control>(host, "DisplaySection").IsVisible.Should().BeFalse();
        }
        finally
        {
            host.Close();
            UiShot.Pump();
        }
    }
}
