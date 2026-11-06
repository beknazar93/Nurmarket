using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Domain;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Wave14;

/// <summary>2026-11-06: исправления по живому тесту Этапа 2 — Б2-3 (колонка «Сумма» = 0,00), Б2-5 («с расходами» в строке приёмки), удаление весов «Штрих РС-200».
/// Запускать только tools/qa/test-safe.ps1.</summary>
public sealed class MoneyCellTextTests
{
    // Б2-3: колонка «Сумма» («Продажи и финансы») строит текст через MoneyText.SomOf — одинаково для чека сервера (decimal из JSON),
    // офлайн-чека (сумма строк кассы, double), долга и смешанной оплаты (меняется только способ оплаты, сумма — та же).
    [Fact]
    public void ServerReceipt_Decimal150_ShowsAmount()
    {
        MoneyText.SomOf(150m).Should().Be("150,00 " + MoneyText.Currency);
    }

    [Fact]
    public void OfflineReceipt_Double150_ShowsSameText()
    {
        MoneyText.SomOf(150.0).Should().Be(MoneyText.SomOf(150m));
        MoneyText.SomOf(150f).Should().Be(MoneyText.SomOf(150m));
    }

    [Theory]
    [InlineData(30.5)]
    [InlineData(1234.5)]
    [InlineData(0.01)]
    public void DebtAndMixedReceipts_ShowTheirOwnAmount_NotZero(double amount)
    {
        var text = MoneyText.SomOf((decimal)amount);
        text.Should().Be(MoneyText.Som((decimal)amount));
        text.Should().NotStartWith("0,00");
    }

    [Fact]
    public void IntegerAmounts_AndOddValues()
    {
        MoneyText.SomOf(7).Should().Be("7,00 " + MoneyText.Currency);
        MoneyText.SomOf(12L).Should().Be("12,00 " + MoneyText.Currency);
        MoneyText.SomOf(-0.001m).Should().Be("0,00 " + MoneyText.Currency, "минус ноль не показываем");
        MoneyText.SomOf(null).Should().Be("");
        MoneyText.SomOf("текст").Should().Be("текст");
    }
}

/// <summary>Б2-5: после «Доп. расходы → Применить» строка приёмки сразу узнаёт о доле расходов (таблица слушает PropertyChanged).</summary>
public sealed class LandedCostLineRefreshTests
{
    [Fact]
    public void LandedExtra_Set_NotifiesCostProfitAndText()
    {
        var line = new ReceivingLineVm { ProductName = "Товар", Quantity = 4, PurchasePrice = 2, SalePrice = 5 };
        line.LandedCostText.Should().BeEmpty("расходов ещё нет");
        var changed = new List<string>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        line.LandedExtraPerUnit = 0.125;

        changed.Should().Contain(new[] { nameof(ReceivingLineVm.LandedCostText), nameof(ReceivingLineVm.CostPrice), nameof(ReceivingLineVm.ProfitText), nameof(ReceivingLineVm.LandedExtraPerUnit) });
        line.LandedCostText.Should().Contain("2,125");
    }

    [Fact]
    public void LandedExtra_SameValue_NoNotification_ClearedValue_Notifies()
    {
        var line = new ReceivingLineVm { Quantity = 1, PurchasePrice = 10, SalePrice = 12 };
        line.LandedExtraPerUnit = 1;
        var changed = new List<string>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");

        line.LandedExtraPerUnit = 1;
        changed.Should().BeEmpty("то же значение — лишнего обновления нет");

        line.LandedExtraPerUnit = 0;
        changed.Should().Contain(nameof(ReceivingLineVm.LandedCostText));
        line.LandedCostText.Should().BeEmpty();
    }
}

/// <summary>Удаление последних весов «Штрих РС-200»: папка обмена, логин и пароль сбрасываются, раскладку и правило префикса возвращаем по вопросу кассиру.</summary>
[Collection("UserPreferences")]
public sealed class ShtrikhPcRemovalResetTests : IDisposable
{
    private readonly ShtrikhPcSettings _before = ShtrikhPcSettings.Current;
    private readonly string _amount = UserPreferences.Instance.ScaleAmountPrefixes ?? "";
    private readonly string _weight = UserPreferences.Instance.ScaleWeightPrefixes ?? "";
    private readonly IReadOnlySet<string> _amountSet = WeightBarcodeParser.AmountPrefixes;
    private readonly IReadOnlySet<string> _weightSet = WeightBarcodeParser.WeightPrefixes;
    private readonly IReadOnlyList<WeightBarcodeCustomLayout> _custom = WeightBarcodeParser.CustomLayouts;

    public void Dispose()
    {
        ShtrikhPcSettings.Save(_before);
        UserPreferences.Instance.ScaleAmountPrefixes = _amount;
        UserPreferences.Instance.ScaleWeightPrefixes = _weight;
        WeightBarcodeParser.AmountPrefixes = _amountSet;
        WeightBarcodeParser.WeightPrefixes = _weightSet;
        WeightBarcodeParser.CustomLayouts = _custom;
    }

    private static void Prepare(bool layoutOn)
    {
        ScaleBarcodeRules.ResetAll();
        var s = ShtrikhPcSettings.Current;
        s.Folder = @"\\192.168.0.200\Shared";
        s.Login = @"web-scale1\kassa";
        s.Password = "secret";
        s.BarcodeEnabled = layoutOn;
        s.BarcodePrefix = 27;
        s.CodeLength = 5;
        s.BarcodeFormat = ShtrikhPcSettings.FormatCodeWeight;
        ShtrikhPcSettings.Save(s);
        if (layoutOn)
            ScaleBarcodeRules.Apply("27", WeightBarcodeValueKind.Weight); // это правило включил пресет
    }

    [Fact]
    public async Task Accepted_FolderLoginCleared_LayoutOff_PrefixRuleRemoved()
    {
        Prepare(layoutOn: true);
        string? asked = null;

        var reverted = await ScaleBarcodeRules.OfferShtrikhPcResetAsync(q => { asked = q; return Task.FromResult(true); });

        reverted.Should().BeTrue();
        asked.Should().Contain("Вернуть раскладку штрихкода к прежней").And.Contain("27");
        var now = ShtrikhPcSettings.Current;
        now.Folder.Should().BeEmpty();
        now.Login.Should().BeEmpty();
        now.Password.Should().BeEmpty();
        now.BarcodeEnabled.Should().BeFalse();
        WeightBarcodeParser.WeightPrefixes.Should().NotContain("27");
        WeightBarcodeParser.CustomLayouts.Should().BeEmpty("раскладка РС-200 больше не читает этикетки");
    }

    [Fact]
    public async Task Declined_ConnectionCleared_ButLayoutAndPrefixRuleStay()
    {
        Prepare(layoutOn: true);

        var reverted = await ScaleBarcodeRules.OfferShtrikhPcResetAsync(_ => Task.FromResult(false));

        reverted.Should().BeFalse();
        var now = ShtrikhPcSettings.Current;
        now.Folder.Should().BeEmpty("папка обмена принадлежит удалённым весам");
        now.BarcodeEnabled.Should().BeTrue("кассир оставил раскладку");
        WeightBarcodeParser.WeightPrefixes.Should().Contain("27");
    }

    [Fact]
    public async Task LayoutNeverEnabled_NoQuestion_JustClearsConnection()
    {
        Prepare(layoutOn: false);
        var asked = false;

        var reverted = await ScaleBarcodeRules.OfferShtrikhPcResetAsync(_ => { asked = true; return Task.FromResult(true); });

        reverted.Should().BeFalse();
        asked.Should().BeFalse("раскладку не включали — спрашивать не о чем");
        ShtrikhPcSettings.Current.Folder.Should().BeEmpty();
    }

    [Fact]
    public async Task NothingLeft_NoQuestion_NoChange()
    {
        Prepare(layoutOn: false);
        var s = ShtrikhPcSettings.Current;
        s.Folder = "";
        s.Login = "";
        s.Password = "";
        ShtrikhPcSettings.Save(s);
        var asked = false;

        var reverted = await ScaleBarcodeRules.OfferShtrikhPcResetAsync(_ => { asked = true; return Task.FromResult(true); });

        reverted.Should().BeFalse();
        asked.Should().BeFalse();
    }
}
