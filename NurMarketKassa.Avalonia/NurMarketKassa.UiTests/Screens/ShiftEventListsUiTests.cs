using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (владелец, фото «Возвраты» и «Оплата долгов» смены, клиент «Нан», чек 000006): в окнах смены вместо «cash=119.75;noncash=0.00;debt=454.25» — понятный
/// текст (чек, товары, «Наличными 119,75 · В счёт долга 454,25»); у оплат долга — «Наличными» / «Безналом» / «Смешанно»; в заголовке — «Смена от 24.11.2026 13:42», а не код «76EE49C5».
/// Проверяется на ru / ky / uz при 1024×600: текст есть и не обрезан (окно прокручивается), поля формы «Оплата долга» с третьим сегментом «Смешанно» не налезают.</summary>
[Collection("UiScenarios")]
public sealed class ShiftEventListsUiTests : IDisposable
{
    private readonly DebtPaymentOptions? _savedOptions = UserPreferences.Instance.DebtPaymentOptions;
    private readonly string? _savedCompany = UserPreferences.Instance.DebtPaymentOptionsCompanyId;
    private readonly string? _savedLast = UserPreferences.Instance.LastDebtPaymentMethod;

    public ShiftEventListsUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
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

    public static IEnumerable<object[]> Languages()
    {
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
            yield return [lang];
    }

    private static ShiftModel Shift(string id) => new()
    {
        Id = id, ShiftNumber = "76EE49C5", Cashier = "Тест", Status = "Закрыта",
        OpenedAt = new DateTime(2026, 11, 24, 13, 42, 0), ClosedAt = new DateTime(2026, 11, 24, 16, 34, 0),
    };

    private static string Open(ShiftDrillDownDialog dialog, UiSize size)
    {
        using (UiShot.Render(dialog, size))
        {
        }

        UiShot.Settle(4);
        return UiDrive.AllText(dialog);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task Returns_List_IsReadableText_NotInternalCode(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var shiftId = "sel-ret-" + Guid.NewGuid().ToString("N")[..10];
        var saleId = "sale-" + Guid.NewGuid().ToString("N")[..10];
        // Чек 000006 «в долг»: итог 1 074,25, внесено 500; возврат 574 → в счёт долга 454,25 и наличными 119,75 (как на фото клиента).
        SaleReturnLedger.RememberPayment(saleId, 1074.25m, "debt", 500m, 0m, 500m, null, 1074.25m, DateTimeOffset.Now, shiftId: shiftId);
        SaleReturnLedger.RememberNumber(saleId, "000006");
        SaleReturnLedger.AddRefund(saleId, new SaleLedgerRefund
        {
            At = DateTimeOffset.UtcNow, Amount = 574m, Cash = 119.75m, DebtReduced = 454.25m, Source = SaleLedgerRefund.SourceReturn,
            Lines = [new SaleLedgerLine { Name = "Баранина", Quantity = 0.575 }],
        });
        ShiftEventsStore.Record(ShiftEventsStore.KindReturn, shiftId, ShiftEventsStore.OperationKey(saleId), 574, "cash=119.75;noncash=0.00;debt=454.25");

        var dialog = new ShiftDrillDownDialog(Shift(shiftId), ShiftDrillDownDialog.KindReturns);
        try
        {
            var text = Open(dialog, new UiSize(1024, 600));
            text.Should().NotContain("cash=").And.NotContain("noncash=").And.NotContain("debt=", "служебная запись не показывается кассиру");
            text.Should().Contain("000006").And.Contain("Баранина").And.Contain("119,75").And.Contain("454,25");
            text.Should().Contain("24.11.2026 13:42").And.NotContain("76EE49C5", "в заголовке — дата открытия смены, а не код");
            if (lang == AppLanguage.Russian)
                text.Should().Contain("Наличными 119,75 · В счёт долга 454,25");
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(600.5, "окно умещается в 1024×600");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task DebtPayments_List_ShowsMethod_CashNonCashMixed(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var shiftId = "sel-debt-" + Guid.NewGuid().ToString("N")[..10];
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPayment, shiftId, "a:" + Guid.NewGuid().ToString("N")[..6], 50, "Нан");
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPaymentNonCash, shiftId, "b:" + Guid.NewGuid().ToString("N")[..6], 20, "Айбек");
        // смешанная: 40 наличными + 60 безналом одним приёмом «Смешанно» из окна (общий идентификатор группы в source_id)
        var mix = Guid.NewGuid().ToString("N")[..8];
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPayment, shiftId, ShiftEventText.DebtSourceId("c:" + Guid.NewGuid().ToString("N")[..6], mix), 40, "Нурлан");
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPaymentNonCash, shiftId, ShiftEventText.DebtSourceId("d:" + Guid.NewGuid().ToString("N")[..6], mix), 60, "Нурлан");
        // две ОТДЕЛЬНЫЕ оплаты подряд (наличные 100 и безнал 100, без общего идентификатора) — две строки, не «Смешанно»
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPayment, shiftId, "e:" + Guid.NewGuid().ToString("N")[..6], 100, "Асан");
        ShiftEventsStore.Record(ShiftEventsStore.KindDebtPaymentNonCash, shiftId, "f:" + Guid.NewGuid().ToString("N")[..6], 100, "Асан");

        var dialog = new ShiftDrillDownDialog(Shift(shiftId), ShiftDrillDownDialog.KindDebtPaid);
        try
        {
            var text = Open(dialog, new UiSize(1024, 600));
            text.Should().Contain("24.11.2026 13:42").And.NotContain("76EE49C5");
            var cashWord = lang switch { AppLanguage.Kyrgyz => "Накталай", AppLanguage.Uzbek => "Naqd", _ => "Наличными" };
            var mixedWord = lang switch { AppLanguage.Kyrgyz => "Аралаш", AppLanguage.Uzbek => "Aralash", _ => "Смешанно" };
            text.Should().Contain("Нан · " + cashWord).And.Contain("Нурлан · " + mixedWord);
            text.Should().Contain("Асан · " + cashWord).And.NotContain("Асан · " + mixedWord, "отдельные оплаты Асана не сливаются в «Смешанно»");
            text.Should().Contain("100,00", "смешанная оплата — одна строка на всю сумму 40 + 60");
            text.Should().Contain("40,00").And.Contain("60,00");
            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(600.5);
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

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

    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task PayDebt_MixedSegment_ShowsNonCashField_FormFieldsDoNotOverlap(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var prefs = UserPreferences.Instance;
        prefs.DebtPaymentOptions = new DebtPaymentOptions { KassaCash = true, KassaTransfer = true };
        prefs.DebtPaymentOptionsCompanyId = CompanyInfoService.LastCompany?.Id;
        prefs.LastDebtPaymentMethod = null;
        var dialog = CreateWithClient();
        try
        {
            using (var _ = UiShot.Render(dialog, new UiSize(1024, 600)))
            {
            }

            UiShot.Settle(3);
            var mixed = UiDrive.Find<RadioButton>(dialog, "MethodMixedRadio");
            mixed.IsEffectivelyVisible.Should().BeTrue("при двух разрешённых способах есть «Смешанно»");
            mixed.Bounds.Height.Should().BeGreaterThanOrEqualTo(44);
            dialog.ShowMixedInput.Should().BeFalse("пока выбраны наличные — поле безнала скрыто");

            UiDrive.Click(dialog, mixed);
            UiShot.Settle(3);
            mixed.IsChecked.Should().BeTrue();
            dialog.IsMixedMethod.Should().BeTrue();
            dialog.ShowMixedInput.Should().BeTrue();
            UiDrive.Find<TextBox>(dialog, "MixedNonCashBox").IsEffectivelyVisible.Should().BeTrue();
            dialog.IsCashMethod.Should().BeFalse();
            dialog.IsTransferMethod.Should().BeFalse();

            dialog.PayAllAmountText = "100";
            dialog.MixedNonCashText = "60";
            dialog.MixedHint.Should().Contain("40,00").And.Contain("60,00", "подсказка показывает наличную и безналичную часть");

            UiShot.Settle(3);
            var violations = FormFieldsOverlapUiTests.FindViolations(dialog, "pay-debt-mixed").Select(v => v.Detail).ToList();
            violations.Should().BeEmpty($"поля «Оплата долга» с выбором «Смешанно» не должны налезать ({lang}, 1024×600)");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    /// <summary>2026-11-26 (живая проверка 1.17.94+): при способе «Смешанно» на 1024×600 верхний блок окна «Оплата долга» был обрезан («Погасить одной суммой» и «Погасить» видны наполовину).
    /// Весь верх (способ, «Из них безналом», «Погасить одной суммой», кнопка «Погасить») должен помещаться без прокрутки и без обрезания, а таблица долгов ниже — оставаться видимой.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Languages))]
    public async Task PayDebt_MixedSegment_TopBlockFitsWithoutClipping_1024x600(AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var prefs = UserPreferences.Instance;
        prefs.DebtPaymentOptions = new DebtPaymentOptions { KassaCash = true, KassaTransfer = true };
        prefs.DebtPaymentOptionsCompanyId = CompanyInfoService.LastCompany?.Id;
        prefs.LastDebtPaymentMethod = DebtPaymentSettings.MethodCash;
        var dialog = CreateWithClient();
        try
        {
            using (var _ = UiShot.Render(dialog, new UiSize(1024, 600)))
            {
            }

            UiShot.Settle(3);
            UiDrive.Click(dialog, UiDrive.Find<RadioButton>(dialog, "MethodMixedRadio"));
            UiShot.Settle(4);
            dialog.IsMixedMethod.Should().BeTrue();
            dialog.PayAllAmountText = "100";
            dialog.MixedNonCashText = "60";
            UiShot.Settle(3);

            var top = UiDrive.Find<ScrollViewer>(dialog, "TopScroll");
            top.Extent.Height.Should().BeLessThanOrEqualTo(top.Viewport.Height + 0.5, $"верх окна «Оплата долга» помещается без прокрутки ({lang}, «Смешанно», 1024×600)");
            foreach (var name in new[] { "MethodMixedRadio", "MixedNonCashBox", "PayAllAmountBox", "PayAllButton" })
            {
                var el = UiDrive.Find<Control>(dialog, name);
                el.IsEffectivelyVisible.Should().BeTrue(name);
                var topLeft = el.TranslatePoint(default, top);
                topLeft.Should().NotBeNull(name);
                topLeft!.Value.Y.Should().BeGreaterThanOrEqualTo(-0.5, $"{name}: не выше видимой области");
                (topLeft.Value.Y + el.Bounds.Height).Should().BeLessThanOrEqualTo(top.Viewport.Height + 0.5, $"{name}: целиком в видимой области, не обрезан снизу ({lang})");
            }

            dialog.Bounds.Height.Should().BeLessThanOrEqualTo(600.5);
            UiDrive.Find<DataGrid>(dialog, "UnpaidGrid").Bounds.Height.Should().BeGreaterThanOrEqualTo(90, $"под верхним блоком остаётся таблица долгов — шапка и целая строка ({lang})");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task PayDebt_MixedSegment_Hidden_WhenNonCashSwitchedOff()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var prefs = UserPreferences.Instance;
        prefs.DebtPaymentOptions = new DebtPaymentOptions { KassaCash = true, KassaTransfer = false };
        prefs.DebtPaymentOptionsCompanyId = CompanyInfoService.LastCompany?.Id;
        var dialog = CreateWithClient();
        try
        {
            using (var _ = UiShot.Render(dialog, new UiSize(1024, 600)))
            {
            }

            UiShot.Settle(3);
            dialog.MixedMethodAllowed.Should().BeFalse();
            UiDrive.Find<RadioButton>(dialog, "MethodMixedRadio").IsEffectivelyVisible.Should().BeFalse("безнал выключен — «Смешанно» не предлагается");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }
}
