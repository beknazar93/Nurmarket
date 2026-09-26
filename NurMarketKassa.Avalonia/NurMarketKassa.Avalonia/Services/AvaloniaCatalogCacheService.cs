using System.Text.Json;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>
/// Этот файл предназначен для работы с локальным каталогом товаров в Avalonia-кассе:
/// загрузка из SQLite, полная синхронизация с REST API сайта и кэширование плиток каталога в памяти.
/// </summary>
public sealed class AvaloniaCatalogCacheService : ICatalogCacheService
{
    private readonly ICatalogApiService _catalogApi;
    private readonly AppSettings _settings;
    private readonly MySqlAuditService _auditDb;
    private List<CatalogProductTileVm> _products = [];

    public AvaloniaCatalogCacheService(
        ICatalogApiService catalogApi,
        AppSettings settings,
        MySqlAuditService auditDb)
    {
        _catalogApi = catalogApi;
        _settings = settings;
        _auditDb = auditDb;
    }

    public bool TryLoadFromDatabase()
    {
        try
        {
            LocalProductRepository.Instance.EnsureSchema();
            var tiles = LocalProductRepository.Instance.LoadAllTiles();
            _products = tiles.ToList();
            SyncInMemoryCatalog();
            return tiles.Count > 0;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG load from SQLite failed: {ex}", "CATALOG");
            return false;
        }
    }

    public IReadOnlyList<CatalogProductTileVm> GetProducts() => _products;

    // Один полный sync за раз + окно свежести (2026-09-07, оптимизация): при старте кассы
    // SyncService (первый проход цикла) и CatalogPanelViewModel.RefreshCatalogAsync запускали
    // ДВЕ полные загрузки каталога подряд с разницей в 2–3 секунды (в логе два "CATALOG fetch")
    // — вторая просто повторяла первую: сеть, маппинг, diff в SQLite, чекпоинт WAL. Теперь
    // (1) параллельный вызов дожидается уже идущего sync'а, (2) вызов в течение 15 с после
    // успешного получает его результат без повторной загрузки. Явные обновления после
    // редактирования/удаления товара в Складе идут через статический CatalogCacheService и
    // этим окном не ограничены.
    private static readonly TimeSpan FreshSyncWindow = TimeSpan.FromSeconds(15);
    private readonly object _syncGate = new();
    private Task<CatalogSyncResult>? _inFlightSync;
    private DateTime _lastSuccessfulSyncUtc = DateTime.MinValue;
    private CatalogSyncResult? _lastSuccessfulResult;
    private int _accountGeneration;

    /// <summary>Смена аккаунта (2026-09-26, «очищай старую базу товаров другого аккаунта»): база
    /// очищается в AccountCatalogIsolation, но этот список в памяти жил дальше — касса показывала и
    /// находила по штрихкоду товары прежнего аккаунта, пока не пройдёт синхронизация. Заодно
    /// забываем «свежую» синхронизацию прежнего аккаунта и отбрасываем ту, что ещё идёт.</summary>
    public void ResetForAccountChange()
    {
        lock (_syncGate)
        {
            _accountGeneration++;
            _inFlightSync = null;
            _lastSuccessfulResult = null;
            _lastSuccessfulSyncUtc = DateTime.MinValue;
        }

        _products = [];
        SyncInMemoryCatalog();
        CatalogCacheService.NotifyCatalogChanged();
    }

    public Task<CatalogSyncResult> SyncCatalogFullAsync(CancellationToken cancellationToken = default)
    {
        lock (_syncGate)
        {
            if (_inFlightSync is { IsCompleted: false } running)
                return running;

            if (_lastSuccessfulResult is { } fresh && DateTime.UtcNow - _lastSuccessfulSyncUtc < FreshSyncWindow)
            {
                PosLogger.Log("CATALOG sync skipped: previous successful sync is fresh (<15s)", "CATALOG");
                return Task.FromResult(fresh);
            }

            var task = SyncCatalogFullCoreAsync(cancellationToken);
            _inFlightSync = task;
            return task;
        }
    }

    private async Task<CatalogSyncResult> SyncCatalogFullCoreAsync(CancellationToken cancellationToken = default)
    {
        if (OfflineModeHelper.UseLocalOperations)
            return CatalogSyncResult.Failed("Нет подключения — каталог из локальной базы.");

        int generation;
        lock (_syncGate)
            generation = _accountGeneration;

        try
        {
            var remoteVersion = await _catalogApi
                .ProductsCatalogVersionAsync(cancellationToken)
                .ConfigureAwait(false);

            var rawItems = await _catalogApi.ProductsCatalogAsync(
                _settings.Catalog.QuickCatalogLimit,
                _settings.Catalog.CatalogMaxPages,
                cancellationToken).ConfigureAwait(false);

            var apiBaseUrl = _settings.ApiBaseUrl;
            var newList = new List<CatalogProductTileVm>();

            foreach (JsonElement el in rawItems)
            {
                var vm = ProductCatalogMapper.TryTile(el, apiBaseUrl);
                if (vm != null)
                    newList.Add(vm);
            }

            await StockSyncService.OverlayAgentStockAsync(newList, cancellationToken).ConfigureAwait(false);

            // Гарантируем StockInfo перед записью в SQLite (на случай отсутствия overlay).
            foreach (var vm in newList)
            {
                if (string.IsNullOrWhiteSpace(vm.StockInfo))
                    StockSyncService.ApplyQuantityToTile(vm, vm.Quantity, vm.MustWeigh);
            }

            // Пока товары качались, сменился аккаунт — это каталог прежнего, в базу нового его не пишем.
            if (generation != Volatile.Read(ref _accountGeneration))
            {
                PosLogger.Log("CATALOG sync: аккаунт сменился во время загрузки — каталог прежнего аккаунта отброшен.", "CATALOG");
                return CatalogSyncResult.Failed("Аккаунт сменился во время загрузки каталога.");
            }

            var (added, changed, deleted) = LocalProductRepository.Instance.SyncReplaceAllWithDiff(newList);

            if (remoteVersion != null && !remoteVersion.IsEmpty)
                LocalProductRepository.Instance.SetCatalogVersionToken(remoteVersion.Token);

            var syncTime = DateTime.UtcNow;
            LocalProductRepository.Instance.SetLastSyncTime(syncTime);

            _auditDb.LogEvent("catalog", "refresh", new { count = newList.Count, added, changed, deleted });
            PosLogger.Log(
                $"CATALOG sync: added={added}, changed={changed}, deleted={deleted}, total={newList.Count}",
                "CATALOG");

            _products = newList;
            SyncInMemoryCatalog();

            // Экран кассира узнаёт об изменениях каталога (2026-09-07) — раньше результат фонового
            // sync'а оседал только в SQLite и до плиток не доходил до перезапуска.
            if (added + changed + deleted > 0)
                CatalogCacheService.NotifyCatalogChanged();

            // Сливает WAL в основной файл БД на самой частой в приложении операции записи
            // (каждые ~45с) — не единственная защита (см. App.axaml.cs, штатное закрытие), но
            // держит "риск-окно" для резкого завершения процесса (taskkill /F и т.п.) коротким,
            // а не на всю сессию (см. DatabaseService.CheckpointWal, 2026-09-04).
            DatabaseService.CheckpointWal();

            var okResult = CatalogSyncResult.Ok(added, changed, deleted);
            lock (_syncGate)
            {
                _lastSuccessfulSyncUtc = DateTime.UtcNow;
                _lastSuccessfulResult = okResult;
            }

            return okResult;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ApiException ex)
        {
            return CatalogSyncResult.Failed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return CatalogSyncResult.Failed(string.IsNullOrWhiteSpace(ex.Message) ? "Нет подключения." : ex.Message);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CATALOG sync error: {ex}", "CATALOG");
            return CatalogSyncResult.Failed(ex.Message);
        }
    }

    /// <summary>Общий список <see cref="CatalogCacheService.Products"/> читают экраны кассы и склада
    /// из UI-потока (WarehouseViewModel.ApplyProductFilter копирует его целиком в
    /// ObservableCollection), а полная синхронизация каталога идёт в фоне каждые ~45 с. Пока запись
    /// шла прямо из фонового потока, Clear()+AddRange успевали пересоздать внутренний буфер List'а
    /// между чтением его длины и самим копированием — копирование падало с "Source array was not
    /// long enough … (Parameter 'sourceArray')", и склад показывал "Не удалось загрузить каталог"
    /// при том, что товары на месте (живой баг 2026-09-21, каталог 784 позиции). Теперь запись
    /// всегда идёт в UI-потоке — том же, где читают, — поэтому гонки нет. Dispatcher выполняет
    /// действие inline, если поток уже UI-шный, так что порядок на старте не меняется.
    /// Ср. <see cref="CatalogCacheService.SetProducts"/> — там перевод в UI-поток был с самого начала.</summary>
    private void SyncInMemoryCatalog()
    {
        // _products присваивается целиком (новый список), а не мутируется, поэтому копия ссылки
        // безопасна: фоновый поток не изменит его во время AddRange.
        var snapshot = _products;
        UiDispatcherHolder.Post(() =>
        {
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(snapshot);
        });
    }
}
