using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Labels;

/// <summary>2026-11-27 (владелец): «Редактор этикеток ценников добавь в состав тарифа Стандарт» — редактор открыт и на «Старте», и на «Стандарте», без серийного номера.</summary>
[Collection("ServerAddonsStatic")]
public sealed class LabelEditorTariffTests : IDisposable
{
    private readonly CompanyDto? _previousCompany = CompanyInfoService.LastCompany;

    private static void SetPlan(string? plan) =>
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!
            .SetValue(null, new CompanyDto { Id = "c-label-tariff", SubscriptionPlanName = plan });

    [Theory]
    [InlineData("Стандарт")]
    [InlineData("Старт")]
    [InlineData("Бизнес")]
    public void LabelEditor_IsIncludedInEveryPlan(string plan)
    {
        SetPlan(plan);

        TariffGate.LabelEditorAvailable.Should().BeTrue($"редактор этикеток входит в тариф «{plan}»");
    }

    public void Dispose() =>
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, _previousCompany);
}
