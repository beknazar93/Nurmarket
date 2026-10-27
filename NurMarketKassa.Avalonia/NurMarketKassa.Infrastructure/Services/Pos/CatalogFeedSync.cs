using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-27 (Б-46 сервера, CATALOG_FEED): каталог по ленте изменений. Жалоба «на складе 195, а касса продаёт 205»: «версия каталога»
/// (ordering=-updated_at) цену не замечает, и другие кассы до 15–30 минут продают по старой цене. Лента
/// GET api/main/products/changes/?since=&lt;курсор&gt;&amp;limit=500 отдаёт только изменённые (и удалённые) товары; опрос раз в ~8 секунд
/// (ETag / 304 — ничего нового). Изменённые товары пишутся в локальную базу (схема pos_local.db не менялась), плитки и поиск
/// обновляются сразу (NotifyCatalogChanged) — цена на плитке меняется за секунды.
///
/// Безопасность:
/// • сервер ленту ещё может не выложить — 404/405/501/непонятный ответ = «не поддерживается»: касса молчит и живёт по-старому (полная
///   загрузка по таймеру 15–30 мин остаётся страховкой), через 10 минут проверяет ещё раз;
/// • 429 — пауза не меньше 30 секунд (ApiThrottle); сеть/5xx — пауза и тихий повтор;
/// • курсор хранится в своём JSON (catalog_feed_cursor.json в папке данных программы: у кассы и у программы владельца он свой) вместе
///   с «ключом области» (аккаунт|филиал|адрес сервера): сменился аккаунт или филиал — начинаем с начала;
/// • остаток из ленты НЕ применяется, когда включена локальная сеть касс или есть неотправленные офлайн-чеки: сервер ещё не знает этих
///   продаж, и остаток «откатился» бы (полная загрузка делает это же вычитание через LanStockAdjuster; для ленты оно не подходит).
/// </summary>
public sealed class CatalogFeedSync
{
    public sealed record TickResult(int Added, int Changed, int Deleted, bool NeedFullSync, string Note)
    {
        public bool Applied => Added + Changed + Deleted > 0;
        public static TickResult None(string note) => new(0, 0, 0, false, note);
    }

    private sealed class StoreDto
    {
        public string? Scope { get; set; }
        public string? Cursor { get; set; }
        public string? ETag { get; set; }
        public string? ETagCursor { get; set; }
        public DateTime SavedUtc { get; set; }
    }

    /// <summary>Файл курсора. Стенд подменяет, чтобы не трогать настоящий профиль.</summary>
    public static string StorePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppMode.DataFolderName,
        "catalog_feed_cursor.json");

    /// <summary>Не больше стольких страниц (по 500 товаров) за один опрос: первый проход по большому каталогу не должен держать поток минутами.</summary>
    public const int MaxPagesPerTick = 40;
    public const int PageLimit = 500;
    public static readonly TimeSpan UnsupportedRetry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan ThrottlePause = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ErrorPause = TimeSpan.FromSeconds(30);

    private readonly ICatalogFeedApi _api;
    private readonly Func<string> _apiBaseUrl;
    private readonly Func<string> _accountKey;
    private readonly Func<bool> _allowStock;

    private bool _loaded;
    private string? _scope;
    private string? _cursor;
    private string? _etag;
    private string? _etagCursor;
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private string? _lastLoggedProblem;

    /// <param name="accountKey">Ключ аккаунта (LastCatalogUserKey): другой аккаунт — другой курсор.</param>
    /// <param name="allowStock">Можно ли применять остаток из ленты (см. описание класса).</param>
    public CatalogFeedSync(ICatalogFeedApi api, Func<string> apiBaseUrl, Func<string> accountKey, Func<bool> allowStock)
    {
        _api = api;
        _apiBaseUrl = apiBaseUrl;
        _accountKey = accountKey;
        _allowStock = allowStock;
    }

    public string? Cursor => _cursor;

    public bool IsPaused => DateTime.UtcNow < _pausedUntilUtc;

    /// <summary>Смена аккаунта: курсор и ETag забываются (и на диске тоже).</summary>
    public void Reset()
    {
        _cursor = null;
        _etag = null;
        _etagCursor = null;
        _scope = null;
        _loaded = true;
        _pausedUntilUtc = DateTime.MinValue;
        try { if (File.Exists(StorePath)) File.Delete(StorePath); }
        catch (Exception ex) { PosLogger.Log($"CATALOG_FEED: файл курсора не удалён: {ex.Message}", "CATALOG_FEED"); }
    }

    /// <summary>Один опрос: читает страницы ленты, пока сервер говорит has_more, применяет к локальному каталогу и сдвигает курсор.
    /// Не бросает (кроме отмены): любая ошибка = тихая пауза и прежняя логика полной загрузки.</summary>
    /// <param name="stillValid">Спрашивается прямо перед записью в базу: false (сменился аккаунт, пошла полная загрузка) — страница не применяется
    /// и курсор не двигается, её отдаст следующий опрос.</param>
    public async Task<TickResult> TickAsync(Func<bool>? stillValid = null, CancellationToken ct = default)
    {
        if (!ServerPriceContract.CatalogFeedEnabled)
            return TickResult.None("лента выключена настройкой");
        if (IsPaused)
            return TickResult.None("пауза");
        var block = ApiThrottle.RemainingBlock;
        if (block > TimeSpan.Zero)
            return TickResult.None($"сервер просит паузу {block.TotalSeconds:0} с");

        EnsureLoaded();
        int added = 0, changed = 0, deleted = 0;
        var needFull = false;
        var pages = 0;
        var notes = "";
        try
        {
            while (pages < MaxPagesPerTick)
            {
                ct.ThrowIfCancellationRequested();
                pages++;
                var requested = _cursor;
                var etag = string.Equals(_etagCursor, requested, StringComparison.Ordinal) ? _etag : null;
                var page = await _api.ProductsChangesAsync(requested, PageLimit, etag, ct).ConfigureAwait(false);

                if (page.Status == CatalogFeedStatus.Unsupported)
                {
                    Pause(UnsupportedRetry, $"лента не поддерживается сервером ({page.Note}) — прежняя логика, повтор через {UnsupportedRetry.TotalMinutes:0} мин");
                    break;
                }

                if (page.Status == CatalogFeedStatus.NotModified)
                {
                    notes = "304";
                    ClearProblem();
                    break;
                }

                ClearProblem();
                if ((page.Items.Count > 0 || page.DeletedIds.Count > 0) && string.IsNullOrWhiteSpace(page.NextCursor))
                {
                    // Без курсора не продвинуться: применили бы одно и то же каждые 8 секунд.
                    Pause(UnsupportedRetry, "ответ ленты без курсора — прежняя логика");
                    break;
                }

                if ((page.Items.Count > 0 || page.DeletedIds.Count > 0) && stillValid is not null && !stillValid())
                {
                    notes = "применение отложено (идёт полная загрузка или смена аккаунта)";
                    break;
                }

                if (page.Items.Count > 0 || page.DeletedIds.Count > 0)
                {
                    var r = Apply(page, _apiBaseUrl(), _allowStock());
                    added += r.Added;
                    changed += r.Changed;
                    deleted += r.Deleted;
                    needFull |= r.NeedFullSync;
                    PosLogger.Log(
                        $"CATALOG_FEED: страница {pages}: {page.Note}; в базе добавлено {r.Added}, изменено {r.Changed}, удалено {r.Deleted}"
                        + (r.NeedFullSync ? "; есть товары, которых нет локально — нужна полная загрузка" : ""),
                        "CATALOG_FEED");
                }

                var moved = !string.IsNullOrWhiteSpace(page.NextCursor) && !string.Equals(page.NextCursor, requested, StringComparison.Ordinal);
                if (moved)
                    _cursor = page.NextCursor;
                // ETag относится к ответу на запрос с курсором requested; уйдёт вместе с ним, пока курсор не изменился.
                _etag = page.ETag;
                _etagCursor = requested;
                Save();

                if (!page.HasMore || !moved)
                    break;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiException ex) when (ex.StatusCode == 429)
        {
            var wait = ApiThrottle.RemainingBlock > ThrottlePause ? ApiThrottle.RemainingBlock : ThrottlePause;
            Pause(wait, $"сервер ответил 429 — пауза {wait.TotalSeconds:0} с");
        }
        catch (ApiException ex) when (ex.StatusCode is 400 or 403)
        {
            // Сервер отказал в самом запросе (например, курсор не принят): пробуем с начала, но не чаще раза в 10 минут.
            Reset();
            Pause(UnsupportedRetry, $"сервер отказал ({ex.StatusCode}): курсор сброшен, повтор через {UnsupportedRetry.TotalMinutes:0} мин");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException or TimeoutException or OperationCanceledException or JsonException)
        {
            Pause(ErrorPause, $"ошибка {ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Лента — помощь каталогу, а не условие работы кассы: любой сбой = пауза.
            Pause(ErrorPause, $"сбой {ex.GetType().Name}: {ex.Message}");
        }

        return new TickResult(added, changed, deleted, needFull, notes);
    }

    private void Pause(TimeSpan span, string why)
    {
        _pausedUntilUtc = DateTime.UtcNow + span;
        // В журнал — только когда причина новая: опрос идёт каждые 8 секунд, одинаковые строки не нужны.
        if (!string.Equals(_lastLoggedProblem, why, StringComparison.Ordinal))
        {
            _lastLoggedProblem = why;
            PosLogger.Log($"CATALOG_FEED: {why}.", "CATALOG_FEED");
        }
    }

    private void ClearProblem()
    {
        if (_lastLoggedProblem != null)
            PosLogger.Log("CATALOG_FEED: лента снова отвечает.", "CATALOG_FEED");
        _lastLoggedProblem = null;
    }

    private string CurrentScope() => $"{_accountKey()}|{_api.ScopeKey}|{_apiBaseUrl()}";

    private void EnsureLoaded()
    {
        var scope = CurrentScope();
        if (_loaded && string.Equals(_scope, scope, StringComparison.Ordinal))
            return;

        _loaded = true;
        _scope = scope;
        _cursor = null;
        _etag = null;
        _etagCursor = null;
        try
        {
            if (!File.Exists(StorePath))
                return;
            var dto = JsonSerializer.Deserialize<StoreDto>(File.ReadAllText(StorePath));
            if (dto is not null && string.Equals(dto.Scope, scope, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(dto.Cursor))
            {
                _cursor = dto.Cursor;
                _etag = dto.ETag;
                _etagCursor = dto.ETagCursor;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG_FEED: файл курсора не прочитан ({ex.Message}) — начинаем сначала.", "CATALOG_FEED");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(new StoreDto
            {
                Scope = _scope,
                Cursor = _cursor,
                ETag = _etag,
                ETagCursor = _etagCursor,
                SavedUtc = DateTime.UtcNow,
            });
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG_FEED: курсор не сохранён: {ex.Message}", "CATALOG_FEED");
        }
    }

    // ---------------------------------------------------------------- применение страницы

    public sealed record ApplyOutcome(int Added, int Changed, int Deleted, bool NeedFullSync);

    private static readonly string[] NameKeys = ["name", "title", "display_name", "label"];

    /// <summary>Применить страницу ленты к локальной базе. Карточка товара (есть название) — как при полной загрузке; без названия — только
    /// присланные поля (цена, закупка, опт, остаток) поверх записи из базы. Товар, которого нет локально и карточки нет, — просьба о полной загрузке.</summary>
    public static ApplyOutcome Apply(CatalogFeedPage page, string apiBaseUrl, bool allowStock)
    {
        var repo = LocalProductRepository.Instance;
        var tiles = new List<CatalogProductTileVm>();
        var patches = new List<LocalProductRepository.FeedFieldPatch>();
        foreach (var item in page.Items)
        {
            var id = ProductCatalogMapper.TryId(item);
            if (string.IsNullOrEmpty(id))
                continue;
            if (HasName(item))
            {
                var tile = ProductCatalogMapper.TryTile(item, apiBaseUrl);
                if (tile is null)
                    continue;
                var local = repo.LoadTileCopy(id);
                // 2026-10-27 (ревью, К-11): карточка ленты может быть сокращённой (без packages / alternate_barcodes / plu / kind) — тогда эти поля
                // остаются локальными, а не затираются пустыми до следующей полной загрузки (пропадали поштучная продажа, доп. штрихкоды, PLU весов).
                if (local is not null)
                    KeepLocalForAbsentFields(item, tile, local);
                // Остаток из ленты нельзя — оставляем локальный (его уже уменьшили свои/соседние продажи).
                if (!allowStock && local is not null)
                    StockSyncService.ApplyQuantityToTile(tile, local.Quantity, tile.MustWeigh);
                tiles.Add(tile);
                continue;
            }

            double? price = ProductCatalogMapper.TryPrice(item) is { } p && double.IsFinite(p) && p > 0 ? p : null;
            patches.Add(new LocalProductRepository.FeedFieldPatch(
                id,
                price,
                ReadMoney(item, "purchase_price"),
                ReadMoney(item, "wholesale_price"),
                allowStock ? ReadStock(item) : null));
        }

        PromotionRules.SaveIfDirty();
        var result = repo.ApplyFeedChanges(tiles, patches, page.DeletedIds);
        return new ApplyOutcome(result.Added, result.Changed, result.Deleted, result.MissingIds.Count > 0);
    }

    /// <summary>2026-10-27 (ревью, К-11): поля, которых в присланной карточке нет вовсе (ключа нет — не «пустой список»), берём из локальной записи.
    /// Ключ есть и пуст (сервер сознательно очистил) — применяется как пришло.</summary>
    internal static void KeepLocalForAbsentFields(JsonElement item, CatalogProductTileVm tile, CatalogProductTileVm local)
    {
        if (!item.TryGetProperty("kind", out _))
        {
            tile.Kind = local.Kind;
            tile.IsBundle = local.IsBundle;
            tile.BundleItems = local.BundleItems;
        }

        if (!item.TryGetProperty("packages", out _) && !tile.IsBundle)
            tile.PieceOption = local.PieceOption;
        if (!item.TryGetProperty("alternate_barcodes", out _))
        {
            tile.AlternateBarcodesRaw = local.AlternateBarcodesRaw;
            tile.AlternateBarcodeVariants = local.AlternateBarcodeVariants;
        }

        if (!item.TryGetProperty("plu", out _))
            tile.Plu = local.Plu;
    }

    private static bool HasName(JsonElement item)
    {
        foreach (var key in NameKeys)
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return true;
        return false;
    }

    private static double? ReadMoney(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) && d >= 0)
            return d;
        if (v.ValueKind == JsonValueKind.String
            && double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) && x >= 0)
            return x;
        return null;
    }

    private static double? ReadStock(JsonElement item)
    {
        foreach (var key in new[] { "quantity", "stock", "stock_quantity" })
            if (item.TryGetProperty(key, out _))
                return ReadMoney(item, key);
        return null;
    }
}
