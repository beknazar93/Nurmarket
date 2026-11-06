using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-11-06 (живой прогон 3, P3-54): у недавно созданного товара со штрихкодом колонка «Код / штрихкод» Склада показывала только штрихкод, хотя на сервере есть код (15516).
/// Корень: товар, созданный через очередь (PendingProductQueue — заведён без связи или создание не успело за 4 с), после успеха записывался в базу только из запроса
/// (LocalProductEditor.SaveLocally без ответа сервера), а дочитывание кодов (ProductCodeBackfill) смотрело только на строки «без единого кода» — у строки со штрихкодом код не дочитывался.
/// Настоящая база (NURMARKET_DATA_ROOT из test-safe.ps1) и поддельный сервер WireMock. Запускать только tools/qa/test-safe.ps1.
/// </summary>
[Collection("BackgroundReceivingQueue")]
public sealed class NewProductCodeTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-newproduct-code-" + Guid.NewGuid().ToString("N"));
    private readonly TimeSpan[] _oldDelays = PendingProductQueue.RetryDelays;
    private readonly TimeSpan _oldPoll = PendingProductQueue.ServerPoll;
    private readonly ICatalogApiService? _previousApi;
    private readonly List<PendingProductQueue> _queues = new();

    public NewProductCodeTests()
    {
        Directory.CreateDirectory(_dir);
        _repo.EnsureSchema();
        _repo.ClearAll();
        _previousApi = PosApp.CatalogApi;
        PendingProductQueue.DataDirOverride = _dir;
        PendingProductQueue.CompanyIdOverride = "co-newproduct-code";
        PendingProductQueue.RetryDelays = [TimeSpan.FromMilliseconds(400)];
        PendingProductQueue.ServerPoll = TimeSpan.FromMilliseconds(200);
        PendingProductQueue.DeleteLocalOverride = null; // настоящая подмена временного товара настоящим — в настоящей базе
        PendingProductQueue.SaveLocalOverride = null;
        PendingProductQueue.ToastOverride = (_, _) => { };
        ServerOutageMonitor.ResetForTests();
        ProductCodeBackfill.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var q in _queues)
        {
            try { Task.WhenAny(q.StopAsync(), Task.Delay(10000)).GetAwaiter().GetResult(); }
            catch { /* воркер уже остановлен */ }
        }
        PosApp.CatalogApi = _previousApi!;
        ServerOutageMonitor.ResetForTests();
        PendingProductQueue.DataDirOverride = null;
        PendingProductQueue.CompanyIdOverride = null;
        PendingProductQueue.RetryDelays = _oldDelays;
        PendingProductQueue.ServerPoll = _oldPoll;
        PendingProductQueue.ToastOverride = null;
        ProductCodeBackfill.ResetForTests();
        _repo.ClearAll();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<CatalogApiService> LoginAsync(FakeNurCrmServer server)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    private static async Task<bool> WaitAsync(Func<bool> cond, int ms = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            if (cond())
                return true;
            await Task.Delay(40);
        }
        return cond();
    }

    [Fact]
    public async Task QueueCreatedProduct_WithBarcode_ShowsServerCode_InWarehouseColumn()
    {
        using var server = new FakeNurCrmServer();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/products/warehouse-barcode/*", 404, "{}");
        server.Json("POST", "/api/main/products/create-manual/", 201,
            """{"id":"srv-offline-2","name":"Товар офлайн-2","price":"7.00","quantity":"5","unit":"шт","barcode":"2061106000024","code":"15516","kind":"simple"}""");
        var api = await LoginAsync(server);

        var request = new ProductEditRequest { Name = "Товар офлайн-2", Barcode = "2061106000024", Unit = "шт", Quantity = 5, Price = 7, IsNew = true, IdempotencyKey = Guid.NewGuid().ToString("N") };
        var tempId = LocalProductEditor.SaveLocally(request, null); // как QueueNewProductForServer: товар сразу в базе под временным id
        var queue = new PendingProductQueue();
        _queues.Add(queue);
        queue.Resume(api);
        queue.Enqueue(request, tempId);

        (await WaitAsync(() => _repo.LoadTileCopy("srv-offline-2") is not null)).Should().BeTrue("очередь создала товар и заменила временный настоящим");
        var tile = _repo.LoadTileCopy("srv-offline-2")!;
        tile.BarcodeDisplay.Should().Be("2061106000024", "штрихкод, введённый в карточке, на месте");
        tile.CodeDisplay.Should().Be("15516", "код товара, который присвоил сервер, виден в верхней строке колонки сразу — без ожидания дочитывания");
        _repo.LoadTileCopy(tempId).Should().BeNull("временный товар заменён");
    }

    [Fact]
    public async Task RowWithBarcodeButNoCode_IsReadFromServer_WhenShownInList()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/n-1/", 200,
            """{"id":"n-1","name":"Товар офлайн-2","price":"7.00","quantity":"5","unit":"шт","barcode":"2061106000024","code":"15516","kind":"simple"}""");
        PosApp.CatalogApi = await LoginAsync(server);
        // Запись, как её оставляла прежняя версия: штрихкод есть (из запроса), кода и артикула нет.
        _repo.SyncReplaceAllWithDiff([ProductCatalogMapper.TryTile(J("""{"id":"n-1","name":"Товар офлайн-2","price":"7.00","quantity":"5","barcode":"2061106000024","updated_at":"2026-11-06T02:00:00Z"}"""), ApiBase)!]);
        var row = _repo.LoadTileCopy("n-1")!;
        row.CodeDisplay.Should().BeEmpty("воспроизведение: верхняя строка колонки пуста");
        ProductCodeBackfill.NeedsCodes(row).Should().BeFalse("штрихкод есть — старое условие «нет ни одного кода» не срабатывало");
        ProductCodeBackfill.NeedsServerCode(row).Should().BeTrue();

        ProductCodeBackfill.ScheduleForRows([row]).Should().Be(1);
        (await WaitAsync(() => !ProductCodeBackfill.IsRunning && _repo.LoadTileCopy("n-1")!.CodeDisplay.Length > 0)).Should().BeTrue();

        var after = _repo.LoadTileCopy("n-1")!;
        after.CodeDisplay.Should().Be("15516");
        after.BarcodeDisplay.Should().Be("2061106000024", "штрихкод не тронут");
    }
}
