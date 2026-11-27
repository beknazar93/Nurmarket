using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (владелец, по-кыргызски: «Карызды төлөп атканда наличныйбы же безналичныйбы чыгыш» — при оплате долга должно быть видно, наличными или безналом):
/// окно «Оплата долга» в кассе показывает выбор «Наличные / Безнал (карта, перевод)» ВСЕГДА (раньше — только если в настройках «Оплата долга» были включены оба способа,
/// а безнал был выключен по умолчанию). Проверяется: обе кнопки на экране и не ниже 44 px (сенсор), по умолчанию — наличные; последний выбор запоминается;
/// выключенный в настройках способ виден приглушённым с пояснением; поля формы не налезают на 1024×600 и 1366×768 (ru, ky, uz); печатный X/Z-отчёт смены
/// показывает оплату долгов наличными и безналом отдельными строками, а «В ящике должно быть» безнал не меняет.</summary>
[Collection("UiScenarios")]
public sealed class DebtPaymentMethodUiTests : IDisposable
{
    private readonly DebtPaymentOptions? _savedOptions = UserPreferences.Instance.DebtPaymentOptions;
    private readonly string? _savedCompany = UserPreferences.Instance.DebtPaymentOptionsCompanyId;
    private readonly string? _savedLast = UserPreferences.Instance.LastDebtPaymentMethod;

    public DebtPaymentMethodUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        // Список клиентов и должников окно запрашивает при открытии — пустой ответ, а не «Не удалось загрузить клиентов» поверх проверяемого блока.
        UiFake.Server.Json("GET", "/api/main/clients/", 200, "{\"count\":0,\"next\":null,\"previous\":null,\"results\":[]}");
        UiFake.Server.Json("GET", "/api/main/clients/debtors/", 200, "[]");
    }

    public void Dispose()
    {
        var prefs = UserPreferences.Instance;
        prefs.DebtPaymentOptions = _savedOptions;
        prefs.DebtPaymentOptionsCompanyId = _savedCompany;
        prefs.LastDebtPaymentMethod = _savedLast;
    }

    private static void SetOptions(bool cash, bool transfer, string? remembered)
    {
        var prefs = UserPreferences.Instance;
        prefs.DebtPaymentOptions = new DebtPaymentOptions { KassaCash = cash, KassaTransfer = transfer };
        prefs.DebtPaymentOptionsCompanyId = CompanyInfoService.LastCompany?.Id;
        prefs.LastDebtPaymentMethod = remembered;
    }

    /// <summary>Окно с выбранным клиентом и одним непогашенным чеком (как после выбора клиента в списке) — блок способа оплаты виден только в таком состоянии.</summary>
    private static PayDebtDialog CreateWithClient()
    {
        var dialog = new PayDebtDialog();
        typeof(PayDebtDialog).GetProperty("SelectedClient", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(dialog, new ClientOption { Id = "c-1", DisplayName = "Нан · 099980" });
        dialog.DebtSales.Add(new DebtSaleRow
        {
            Id = "sale-1", DealId = "deal-1", Number = "1001", DateDisplay = "24.11.2026 13:55", FirstItemName = "Хлеб",
            Amount = 320, AmountDisplay = "320,00 сом", AmountToPayText = "320.00", SaleRemaining = 320, CreatedAt = new DateTimeOffset(2026, 11, 24, 13, 55, 0, TimeSpan.Zero),
        });
        return dialog;
    }

    [AvaloniaFact]
    public async Task MethodChoice_IsVisible_CashByDefault_ButtonsAreTouchSize()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SetOptions(cash: true, transfer: true, remembered: null);
        var dialog = CreateWithClient();
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(1024, 600));
            var cash = UiDrive.Find<RadioButton>(dialog, "MethodCashRadio");
            var transfer = UiDrive.Find<RadioButton>(dialog, "MethodTransferRadio");
            cash.IsEffectivelyVisible.Should().BeTrue("выбор способа виден кассиру");
            transfer.IsEffectivelyVisible.Should().BeTrue();
            cash.IsChecked.Should().BeTrue("по умолчанию — наличные");
            transfer.IsChecked.Should().BeFalse();
            cash.IsEnabled.Should().BeTrue();
            transfer.IsEnabled.Should().BeTrue();
            cash.Bounds.Height.Should().BeGreaterThanOrEqualTo(44);
            transfer.Bounds.Height.Should().BeGreaterThanOrEqualTo(44);
            dialog.ShowMethodOffHint.Should().BeFalse("оба способа разрешены — пояснение про выключенный не нужно");
            UiDrive.AllText(dialog).Should().Contain("Наличные").And.Contain("Безнал");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task MethodChoice_RemembersLastChoice()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SetOptions(cash: true, transfer: true, remembered: DebtPaymentSettings.MethodTransfer);
        var first = CreateWithClient();
        try
        {
            using var _ = UiShot.Render(first, new UiSize(1024, 600));
            UiDrive.Find<RadioButton>(first, "MethodTransferRadio").IsChecked.Should().BeTrue("в прошлый раз выбрали безнал — окно открывается с ним");
            UiDrive.Find<RadioButton>(first, "MethodCashRadio").IsChecked.Should().BeFalse();
            UiDrive.Click(first, UiDrive.Find<RadioButton>(first, "MethodCashRadio"));
            UiShot.Settle(2);
            UiDrive.Find<RadioButton>(first, "MethodCashRadio").IsChecked.Should().BeTrue();
            UserPreferences.Instance.LastDebtPaymentMethod.Should().Be(DebtPaymentSettings.MethodCash, "выбор кассира запомнен");
        }
        finally
        {
            first.Close();
            UiShot.Pump();
        }

        var second = CreateWithClient();
        try
        {
            using var _ = UiShot.Render(second, new UiSize(1024, 600));
            UiDrive.Find<RadioButton>(second, "MethodCashRadio").IsChecked.Should().BeTrue("следующее окно — с последним выбором (наличные)");
        }
        finally
        {
            second.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task MethodChoice_NonCashSwitchedOff_IsShownDisabled_WithHint()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        SetOptions(cash: true, transfer: false, remembered: DebtPaymentSettings.MethodTransfer); // запомнен безнал, но его выключили
        var dialog = CreateWithClient();
        try
        {
            using var _ = UiShot.Render(dialog, new UiSize(1024, 600));
            var cash = UiDrive.Find<RadioButton>(dialog, "MethodCashRadio");
            var transfer = UiDrive.Find<RadioButton>(dialog, "MethodTransferRadio");
            transfer.IsEffectivelyVisible.Should().BeTrue("кассир видит, что безнал есть, но выключен");
            transfer.IsEnabled.Should().BeFalse();
            cash.IsChecked.Should().BeTrue("безнал выключен — платит наличными, даже если запомнен безнал");
            dialog.ShowMethodOffHint.Should().BeTrue();
            UiDrive.AllText(dialog).Should().Contain("Безнал выключен в настройках");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    public static IEnumerable<object[]> LanguageSizes()
    {
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
        foreach (var size in new[] { new UiSize(1024, 600), new UiSize(1366, 768) })
            yield return [lang, size.Width, size.Height];
    }

    [AvaloniaTheory]
    [MemberData(nameof(LanguageSizes))]
    public async Task MethodChoice_FormFields_DoNotOverlap(AppLanguage lang, int width, int height)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        SetOptions(cash: true, transfer: true, remembered: null);
        var dialog = CreateWithClient();
        try
        {
            using (var _ = UiShot.Render(dialog, new UiSize(width, height)))
            {
            }
            UiShot.Settle(3);
            var violations = FormFieldsOverlapUiTests.FindViolations(dialog, "pay-debt-method").Select(v => v.Detail).ToList();
            violations.Should().BeEmpty($"поля формы «Оплата долга» с выбором способа не должны налезать ({lang}, {width}×{height})");
            UiDrive.Find<RadioButton>(dialog, "MethodTransferRadio").Bounds.Width.Should().BeGreaterThan(100, "кнопка безнала не схлопнута");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian)]
    [InlineData(AppLanguage.Kyrgyz)]
    [InlineData(AppLanguage.Uzbek)]
    public async Task PrintedShiftReport_DebtPayments_CashAndNonCash_InOwnLines_NonCashNotInDrawer(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var id = "dpm-ui-" + Guid.NewGuid().ToString("N")[..10];
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPayment, id, "pay-debt:" + Guid.NewGuid().ToString("N")[..6], 300, "Нан");
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPaymentNonCash, id, "pay-debt:" + Guid.NewGuid().ToString("N")[..6], 70, "Нан");
        var shift = new ShiftModel
        {
            Id = id, ShiftNumber = "76EE49C5", Cashier = "Тест", Status = "Закрыта",
            OpenedAt = new DateTime(2026, 11, 24, 13, 42, 0), ClosedAt = new DateTime(2026, 11, 24, 16, 34, 0),
            Revenue = 5000m, SalesCount = 5, CashSales = 5000m, NonCashSales = 0m, OpeningCash = 1000m,
        };
        var type = typeof(PayDebtDialog).Assembly.GetTypes().Single(t => t.Name == "ShiftDetailsDialog");
        var build = type.GetMethod("BuildPrintableReport", BindingFlags.NonPublic | BindingFlags.Static)!;
        var text = (string)build.Invoke(null, [shift, null, null, null])!;
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var cashTitle = lang switch { AppLanguage.Kyrgyz => "Карыздарды накталай төлөө", AppLanguage.Uzbek => "Qarz to'lovlari naqd:", _ => "Оплата долгов наличными" };
        var nonCashTitle = lang switch { AppLanguage.Kyrgyz => "Карыздарды накталай эмес төлөө", AppLanguage.Uzbek => "Qarz to'lovlari naqd emas", _ => "Оплата долгов безналом" };
        var cashLine = lines.Single(l => l.StartsWith(cashTitle, StringComparison.Ordinal));
        var nonCashLine = lines.Single(l => l.StartsWith(nonCashTitle, StringComparison.Ordinal));
        cashLine.Should().Contain("+300.00").And.NotContain("70.00");
        nonCashLine.Should().Contain("+70.00").And.NotContain("300.00");
        // в ящик: 1000 (начало) + 5000 (наличные) + 300 (долг наличными) = 6300; безнал 70 не входит
        lines.Any(l => l.Contains("6300.00")).Should().BeTrue("«В ящике должно быть» = 6300.00 (без безнала)");
        text.Should().NotContain("6370.00");
    }
}
