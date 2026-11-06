using System.Net.Http;
using System.IO;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Wave2;

/// <summary>2026-11-06: В2 Л5-14 / Л4-08 — ключи повтора денежных POST переживают перезапуск кассы (хранятся на диске до ответа сервера).
/// Перезапуск имитируется забыванием памяти PendingMoneyKeys (ключи читаются с диска). Подписи уникальны: тесты идут параллельно с другими.</summary>
// 2026-11-06: общий файл pending-money-keys.json перезаписывают соседние параллельные тесты — идём в коллекции без параллельности (единичный сбой в общем прогоне).
[Collection("BackgroundReceivingQueue")]
public sealed class PendingMoneyKeysTests
{
    private static string Sig() => "POST|api/main/w2/" + Guid.NewGuid().ToString("N") + "|HASH";

    [Fact]
    public void Key_SurvivesRestart_SameSignatureSameKey()
    {
        var sig = Sig();
        var key = PendingMoneyKeys.GetOrCreate(sig);

        PendingMoneyKeys.ResetMemoryForTests(); // «перезапуск»

        PendingMoneyKeys.Has(sig).Should().BeTrue("ключ записан на диск до отправки");
        PendingMoneyKeys.GetOrCreate(sig).Should().Be(key, "повтор после перезапуска уходит с тем же ключом — сервер не проведёт операцию второй раз");
    }

    [Fact]
    public void ForgottenKey_IsNotRestored_AfterRestart()
    {
        var sig = Sig();
        var key = PendingMoneyKeys.GetOrCreate(sig);

        PendingMoneyKeys.Forget(sig); // ответ по существу получен
        PendingMoneyKeys.ResetMemoryForTests();

        PendingMoneyKeys.Has(sig).Should().BeFalse();
        PendingMoneyKeys.GetOrCreate(sig).Should().NotBe(key, "следующая такая же операция — новая");
    }

    [Fact]
    public void DifferentSignatures_GetDifferentKeys()
    {
        PendingMoneyKeys.GetOrCreate(Sig()).Should().NotBe(PendingMoneyKeys.GetOrCreate(Sig()));
    }

    [Fact]
    public void MoneyRequestKeys_WritesKeyToDisk_BeforeSending_AndForgetsAfterSuccess()
    {
        var sig = Sig();
        MoneyRequestKeys.TryBegin(sig, out var key).Should().BeTrue();

        PendingMoneyKeys.ResetMemoryForTests();
        PendingMoneyKeys.Has(sig).Should().BeTrue("ключ записан ДО отправки: касса могла закрыться посреди запроса");
        PendingMoneyKeys.GetOrCreate(sig).Should().Be(key);

        MoneyRequestKeys.OnSuccess(sig);
        PendingMoneyKeys.Has(sig).Should().BeFalse("успех — ключ забыт");
    }

    [Fact]
    public void MoneyRequestKeys_UnknownOutcome_KeepsDiskKey_BusinessRefusal_DropsIt()
    {
        var unknown = Sig();
        MoneyRequestKeys.TryBegin(unknown, out var key).Should().BeTrue();
        MoneyRequestKeys.OnError(unknown, new HttpRequestException("нет связи"));
        PendingMoneyKeys.ResetMemoryForTests();
        PendingMoneyKeys.Has(unknown).Should().BeTrue("исход неизвестен — повтор пойдёт с тем же ключом");
        MoneyRequestKeys.TryBegin(unknown, out var again).Should().BeTrue();
        again.Should().Be(key);

        var refused = Sig();
        MoneyRequestKeys.TryBegin(refused, out _).Should().BeTrue();
        MoneyRequestKeys.OnError(refused, new ApiException("не хватает денег", 400));
        PendingMoneyKeys.Has(refused).Should().BeFalse("отказ по существу — ключ забыт");
    }

    [Fact]
    public void FileRemovedExternally_CompanySwitch_KeysAreNotResurrected()
    {
        var sig = Sig();
        PendingMoneyKeys.GetOrCreate(sig);
        var file = Path.Combine(AppPaths.Local, "NurMarketKassa", "pending-money-keys.json");
        File.Exists(file).Should().BeTrue();

        // смена компании уносит файл; другие тесты могли успеть записать — проверяем только свою подпись после перечитывания
        File.WriteAllText(file, "[]");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(7));

        PendingMoneyKeys.Has(sig).Should().BeFalse("файл перечитан — ключ прежней компании не лёг на операцию новой");
    }

    // ───────────── Л4-08: какие пути считаются денежными (контракт по реальным адресам кассы) ─────────────

    [Theory]
    [InlineData("api/main/clients/5/debts/")]
    [InlineData("api/main/clients/5/pay-debt/")]
    [InlineData("api/main/pos/sales/5/pay-debt/")]
    [InlineData("api/main/pos/sales/5/return/")]
    [InlineData("api/main/pos/sales/5/exchange/")]
    [InlineData("api/main/clients/5/deals/7/pay/")]
    [InlineData("api/main/suppliers/5/returns/")]
    [InlineData("api/main/suppliers/receipts/")]
    [InlineData("api/main/clients/bonus/import/")]
    [InlineData("api/construction/cashflows/")]
    [InlineData("api/main/rentals/")]
    [InlineData("api/main/rentals/9/return/")]
    [InlineData("api/main/inventory/sessions/")]
    [InlineData("api/main/inventory/sessions/3/apply/")]
    [InlineData("api/main/branch-transfers/3/cancel/")]
    [InlineData("api/main/market-sale-employee-pay-profiles/")]
    public void Applies_AllKnownMoneyAndStockPostPaths_GetAKey(string path) =>
        MoneyRequestKeys.Applies(HttpMethod.Post, path).Should().BeTrue();

    [Theory]
    [InlineData("api/main/products/list/")]
    [InlineData("api/main/pos/sales/start/")]
    [InlineData("api/main/analytics/market/")]
    [InlineData("api/users/company/employees/")]
    [InlineData("api/main/telegram-bot/")]
    public void Applies_NonMoneyPostPaths_DoNotGetAKey(string path) =>
        MoneyRequestKeys.Applies(HttpMethod.Post, path).Should().BeFalse();
}
