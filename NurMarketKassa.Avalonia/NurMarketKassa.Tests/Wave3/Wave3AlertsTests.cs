using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave3;

/// <summary>2026-11-06: В3 — оповещения: закончившийся товар группы A не приходит каждый день (Н9), отметки «уже отправляли» общие для двух программ (Л5-22).</summary>
[Collection("UserPreferences")]
public sealed class Wave3AlertsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "wave3-alerts-" + Guid.NewGuid().ToString("N")[..8]);

    public Wave3AlertsTests()
    {
        ShopAlerts.DataDirOverride = _dir;
        ShopAlerts.ResetForTests();
    }

    public void Dispose()
    {
        ShopAlerts.DataDirOverride = null;
        ShopAlerts.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    [Fact]
    public void Sold_out_product_is_reported_once_until_it_comes_back()
    {
        ShopAlerts.NewlyOutOfStock(new[] { "Молоко", "Хлеб" }).Should().BeEquivalentTo(new[] { "Молоко", "Хлеб" });
        ShopAlerts.NewlyOutOfStock(new[] { "Молоко", "Хлеб" }).Should().BeEmpty("на следующий день тот же товар не повторяется");

        ShopAlerts.NewlyOutOfStock(new[] { "Молоко" }).Should().BeEmpty("хлеб завезли — из памяти ушёл, молоко всё ещё нет");
        ShopAlerts.NewlyOutOfStock(new[] { "Молоко", "Хлеб" }).Should().BeEquivalentTo(new[] { "Хлеб" }, "хлеб снова закончился — сообщаем заново");
    }

    [Fact]
    public void Sold_out_memory_survives_restart_and_is_not_wiped_by_an_incomplete_list()
    {
        ShopAlerts.NewlyOutOfStock(new[] { "Сыр" }).Should().HaveCount(1);
        ShopAlerts.ResetForTests(); // «перезапуск»: состояние читается с диска
        ShopAlerts.NewlyOutOfStock(new[] { "Сыр" }).Should().BeEmpty();

        ShopAlerts.NewlyOutOfStock(Array.Empty<string>(), listIsComplete: false).Should().BeEmpty();
        ShopAlerts.NewlyOutOfStock(new[] { "Сыр" }).Should().BeEmpty("список остатков не получен — память не стирается");
        ShopAlerts.NewlyOutOfStock(Array.Empty<string>(), listIsComplete: true).Should().BeEmpty();
        ShopAlerts.NewlyOutOfStock(new[] { "Сыр" }).Should().HaveCount(1, "полный пустой список = всё завезли");
    }

    [Fact]
    public void Mark_sent_today_rereads_the_shared_file_written_by_the_other_program()
    {
        var now = new DateTime(2026, 11, 6, 10, 0, 0);
        ShopAlerts.TryMarkSentToday("evening", now).Should().BeTrue();

        // «вторая программа» отметила другой ключ в том же файле; наш кеш об этом не знает
        var file = Path.Combine(_dir, "shop-alerts-state.json");
        File.WriteAllText(file, "{\"Sent\":{\"evening\":\"2026-11-06\",\"weekly\":\"2026-11-06\"},\"Pending\":[],\"OutOfStock\":[]}");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-5));

        ShopAlerts.TryMarkSentToday("weekly", now).Should().BeFalse("чужая отметка видна, второго сообщения не будет");
    }
}
