using FluentAssertions;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-14 (ответ Умара 13.11 вечер, П0-3): «Возвращён частично» в истории чеков — по статусу partially_returned и по return_status=partial (у долгового чека статус остаётся «debt»).</summary>
[Collection("UiScenarios")]
public sealed class ReceiptHistoryPartialReturnTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public ReceiptHistoryPartialReturnTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose() => UserPreferences.Instance.Language = _oldLanguage;

    private static ReceiptHistoryEntry Entry(string status, string returnStatus) => new() { Id = "1", Status = status, ReturnStatus = returnStatus };

    [Theory]
    [InlineData("paid", "partial", "Возвращён частично")]
    [InlineData("partially_returned", "", "Возвращён частично")]
    [InlineData("debt", "partial", "В долг · Возвращён частично")]
    [InlineData("debt", "none", "В долг")]
    [InlineData("paid", "none", "Оплачен")]
    public void ReceiptHistory_StatusLabel_ShowsPartialReturn(string status, string returnStatus, string expected) =>
        ReceiptHistoryService.StatusLabel(Entry(status, returnStatus)).Should().Be(expected);

    [Fact]
    public void ReceiptHistory_FullReturn_StaysReturnedOrCanceled() =>
        ReceiptHistoryService.StatusLabel(Entry("canceled", "full")).Should().Contain("Возврат");
}
