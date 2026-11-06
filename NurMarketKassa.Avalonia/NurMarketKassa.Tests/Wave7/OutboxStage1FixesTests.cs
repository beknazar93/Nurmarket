using FluentAssertions;
using Microsoft.Data.Sqlite;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave7;

/// <summary>2026-11-06: исправления по «Живому тесту Этапа 1» — счёт неотправленного в «Требует внимания» владельца (очередь кассы того же ПК: чеки, возвраты, приходы, новые товары),
/// тексты пустого списка «Неотправленного», человеческие статусы, подпись шапки владельца при потерянной сессии. Папка «кассы» — временная (RootOverride), настоящая не читается.</summary>
[Collection("Outbox")]
public sealed class OutboxStage1FixesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nm-sibling-" + Guid.NewGuid().ToString("N")[..8]);

    public OutboxStage1FixesTests()
    {
        Directory.CreateDirectory(_root);
        SiblingTillQueue.RootOverride = _root;
        SessionExpiryMonitor.ResetForTests();
    }

    public void Dispose()
    {
        SiblingTillQueue.RootOverride = null;
        SessionExpiryMonitor.ResetForTests();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { /* временная папка теста */ }
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void MakeSalesDb(params (string Status, bool Autonomous)[] rows)
    {
        var dir = Path.Combine(_root, "data");
        Directory.CreateDirectory(dir);
        using var c = new SqliteConnection($"Data Source={Path.Combine(dir, "pos_local.db")}");
        c.Open();
        using (var create = c.CreateCommand())
        {
            create.CommandText = "CREATE TABLE OfflineSales (id TEXT PRIMARY KEY, created_at TEXT, sync_status TEXT, json_data TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }
        var i = 0;
        foreach (var (status, autonomous) in rows)
        {
            using var ins = c.CreateCommand();
            ins.CommandText = "INSERT INTO OfflineSales (id, created_at, sync_status, json_data) VALUES ($id, $at, $st, $json);";
            ins.Parameters.AddWithValue("$id", "s" + i++);
            ins.Parameters.AddWithValue("$at", "2026-11-06T10:00:00");
            ins.Parameters.AddWithValue("$st", status);
            ins.Parameters.AddWithValue("$json", $"{{\"isAutonomous\":{(autonomous ? "true" : "false")}}}");
            ins.ExecuteNonQuery();
        }
    }

    [Fact]
    public void Касса_того_же_ПК_без_файлов_даёт_ноль()
    {
        SiblingTillQueue.CountUnsent().Should().Be(0);
    }

    [Fact]
    public void Чеки_кассы_считаются_кроме_отправленных_и_автономных()
    {
        MakeSalesDb((OfflineSaleEntry.PendingSync, false), (OfflineSaleEntry.Syncing, false), (OfflineSaleEntry.Failed, false),
            (OfflineSaleEntry.Synced, false), (OfflineSaleEntry.PendingSync, true));
        SiblingTillQueue.CountSales(_root).Should().Be(3, "ждущий, отправляемый и с ошибкой; отправленный и автономный не считаются");
    }

    [Fact]
    public void Приходы_и_новые_товары_кассы_входят_в_счёт()
    {
        Write(Path.Combine("background-receipts", "queue.json"), "[{\"Id\":\"a\",\"Status\":\"queued\"},{\"Id\":\"b\",\"Status\":\"retry_wait\"},{\"Id\":\"c\",\"Status\":\"done\"},{\"Id\":\"d\",\"Status\":\"failed\"}]");
        Write("pending-products.json", "[{\"TempId\":\"t1\",\"Status\":\"queued\"},{\"TempId\":\"t2\",\"Status\":\"retry\"},{\"TempId\":\"t3\",\"Status\":\"done\"}]");
        SiblingTillQueue.CountReceivings(_root).Should().Be(3, "всё, что не принято сервером");
        SiblingTillQueue.CountProducts(_root).Should().Be(2);
        SiblingTillQueue.CountUnsent().Should().Be(5);
    }

    [Fact]
    public void Возвраты_и_оплаты_долга_кассы_считаются_кроме_отправленных_и_снятых_вручную()
    {
        Write("offline_operations.json",
            "{\"Operations\":[{\"Status\":\"pending_sync\"},{\"Status\":\"synced\"},{\"Status\":\"failed\",\"LastError\":\"[снято вручную]\"},{\"Status\":\"failed\",\"LastError\":\"отказ\"}]}");
        SiblingTillQueue.CountOperations(_root).Should().Be(2);
    }

    [Fact]
    public void Все_очереди_кассы_складываются()
    {
        MakeSalesDb((OfflineSaleEntry.PendingSync, false));
        Write(Path.Combine("background-receipts", "queue.json"), "[{\"Status\":\"queued\"}]");
        Write("pending-products.json", "[{\"Status\":\"queued\"}]");
        Write("offline_operations.json", "{\"Operations\":[{\"Status\":\"pending_sync\"}]}");
        SiblingTillQueue.CountUnsent().Should().Be(4);
    }

    [Fact]
    public void Битый_файл_очереди_не_роняет_счёт()
    {
        Write(Path.Combine("background-receipts", "queue.json"), "{ это не json");
        Write("pending-products.json", "[{\"Status\":\"queued\"}]");
        SiblingTillQueue.CountUnsent().Should().Be(1, "битый файл даёт 0 и строку в журнал, остальное считается");
    }

    [Fact]
    public void Пустой_список_различает_нет_вообще_и_скрыто_фильтром()
    {
        var none = OutboxText.EmptyList(false);
        var hidden = OutboxText.EmptyList(true);
        none.Should().Contain("всё на сервере");
        hidden.Should().Contain("фильтр").And.NotContain("всё на сервере");
    }

    [Theory]
    [InlineData("retry_wait")]
    [InlineData("pending_sync")]
    [InlineData("pending")]
    [InlineData("posted")]
    [InlineData("needs_check")]
    [InlineData("RetryWait")]
    public void Технические_статусы_показываются_словами(string raw)
    {
        var text = OutboxText.RawStatus(raw);
        text.Should().NotBe(raw).And.NotContain("_").And.NotContain("[");
    }

    [Fact]
    public void Неизвестный_статус_возвращается_как_есть()
    {
        OutboxText.RawStatus("что-то новое").Should().Be("что-то новое");
        OutboxText.RawStatus(null).Should().Be("—");
    }

    [Fact]
    public void Шапка_владельца_при_потерянной_сессии_говорит_войдите_снова()
    {
        SessionExpiryMonitor.HeaderLabel.Should().BeNull();
        SessionExpiryMonitor.SummaryHeaderText.Should().BeNull();

        SessionExpiryMonitor.Report("тест");

        SessionExpiryMonitor.HeaderLabel.Should().Contain("Войдите снова");
        SessionExpiryMonitor.SummaryHeaderText.Should().Contain("Войдите снова").And.NotContain("Сервер недоступен");
    }
}
