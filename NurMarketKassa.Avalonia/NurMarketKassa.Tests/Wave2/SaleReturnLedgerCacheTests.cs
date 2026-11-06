using System.IO;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave2;

/// <summary>2026-11-06: В2 Л4-18 — журнал возвратов по чекам не держит устаревший кэш: запись другой программы (программа владельца пишет тот же файл)
/// видна при следующем чтении. Файл — во временной папке данных тестов (AppPaths.Local внутри NURMARKET_DATA_ROOT).</summary>
[Collection("ServerFeatures")]
public sealed class SaleReturnLedgerCacheTests
{
    private static string LedgerFile => Path.Combine(AppPaths.Local, "NurMarketKassa", "sale-returns.json");

    [Fact]
    public void Get_SeesRefundWrittenByAnotherProgram_AfterFileChanged()
    {
        var id = "w2-cache-" + Guid.NewGuid().ToString("N");
        SaleReturnLedger.RememberPayment(id, 100m, "cash", 100m, 0m, null, null, 100m, DateTimeOffset.Now);
        SaleReturnLedger.Get(id)!.Refunds.Should().BeEmpty("кэш прогрет: возвратов ещё нет");

        // «другая программа» дописывает возврат прямо в файл, минуя наш кэш
        var all = JsonSerializer.Deserialize<List<SaleLedgerEntry>>(File.ReadAllText(LedgerFile))!;
        all.Single(e => e.SaleId == id).Refunds.Add(new SaleLedgerRefund { At = DateTimeOffset.Now, Amount = 40m, Cash = 40m });
        File.WriteAllText(LedgerFile, JsonSerializer.Serialize(all));
        File.SetLastWriteTimeUtc(LedgerFile, DateTime.UtcNow.AddSeconds(5));

        var seen = SaleReturnLedger.Get(id)!;

        seen.Refunds.Should().ContainSingle().Which.Amount.Should().Be(40m);
        seen.Refunded.Should().Be(40m, "потолок «не больше оплаченного» считается с учётом чужого возврата");
    }

    [Fact]
    public void Get_WithoutFileChange_KeepsServingFromCache()
    {
        var id = "w2-cache2-" + Guid.NewGuid().ToString("N");
        SaleReturnLedger.RememberPayment(id, 50m, "cash", 50m, 0m, null, null, 50m, DateTimeOffset.Now);

        var first = SaleReturnLedger.Get(id);
        var second = SaleReturnLedger.Get(id);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.Total.Should().Be(50m);
    }
}
