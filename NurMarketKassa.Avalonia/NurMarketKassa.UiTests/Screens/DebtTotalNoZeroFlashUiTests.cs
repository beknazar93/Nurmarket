using System.Reflection;
using Avalonia.Headless.XUnit;
using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.UiTests.Harness;
using NurMarketKassa.ViewModels;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-26 (жёсткая сверка): после оплаты долга «Итого к оплате» ~3 с показывало «0,00 сом». Причины: список долгов очищался до ответа сервера, а потом все строки
/// были «остаток уточняется» (сервер ещё не отдал остаток по сделке) — итог по точным остаткам равен нулю. Теперь: на время чтения держится прежний текст, а при строках «уточняется»
/// без единой точной вместо «0,00 сом» — «уточняется…» (ru / ky / uz).</summary>
[Collection("UiScenarios")]
public sealed class DebtTotalNoZeroFlashUiTests
{
    public DebtTotalNoZeroFlashUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        UiFake.Server.Json("GET", "/api/main/clients/", 200, "{\"count\":0,\"next\":null,\"previous\":null,\"results\":[]}");
        UiFake.Server.Json("GET", "/api/main/clients/debtors/", 200, "[]");
    }

    private static DebtSaleRow Row(string id, double amount, bool unknown) => new()
    {
        Id = id, DealId = "deal-" + id, Number = id, DateDisplay = "24.11.2026 13:55", FirstItemName = "Хлеб", Amount = amount, SaleRemaining = amount,
        AmountToPayText = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), RemainingUnknown = unknown,
        CreatedAt = new DateTimeOffset(2026, 11, 24, 13, 55, 0, TimeSpan.Zero),
    };

    [AvaloniaTheory]
    [InlineData(AppLanguage.Russian, "Итого к оплате: уточняется…")]
    [InlineData(AppLanguage.Kyrgyz, "Жалпы төлөнө турган сумма: такталууда…")]
    [InlineData(AppLanguage.Uzbek, "Jami to'lanadigan summa: aniqlanmoqda…")]
    public async Task AllRowsUnknown_NoZeroSom(AppLanguage lang, string expected)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var dialog = new PayDebtDialog();
        try
        {
            dialog.DebtSales.Add(Row("1", 0, unknown: true));
            dialog.TotalOwedText.Should().Be(expected);
            dialog.TotalOwedText.Should().NotContain("0,00");

            // есть хотя бы одна точная строка — сумма показывается как раньше
            dialog.DebtSales.Add(Row("2", 320, unknown: false));
            dialog.TotalOwedText.Should().Contain("320,00");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }

    [AvaloniaFact]
    public async Task HeldText_IsShownUntilReleased()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var dialog = new PayDebtDialog();
        try
        {
            dialog.DebtSales.Add(Row("1", 320, unknown: false));
            var before = dialog.TotalOwedText;
            before.Should().Contain("320,00");

            var held = typeof(PayDebtDialog).GetField("_heldTotalText", BindingFlags.Instance | BindingFlags.NonPublic)!;
            held.SetValue(dialog, before); // как в LoadDebtSalesAsync перед очисткой списка
            dialog.DebtSales.Clear();
            dialog.TotalOwedText.Should().Be(before, "пока сервер не ответил, «Итого» не прыгает в 0,00");

            held.SetValue(dialog, null); // ответ пришёл / сбой чтения
            dialog.TotalOwedText.Should().Contain("0,00");
        }
        finally
        {
            dialog.Close();
            UiShot.Pump();
        }
    }
}
