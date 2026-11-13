using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-06, жалоба клиента (магазин одежды, 1.17.71): «Штрих-код и код не показывает» — в Складе колонка «Код / штрихкод» пустая, хотя в карточке товара штрихкод
/// есть (карточка читается с сервера, список — из локальной базы). Причина: база, заполненная сокращённой лентой products/changes (Б-1, см.
/// CatalogFullLoadMarks), и товары, которые пришли в базу только лентой, не имеют ни штрихкода, ни артикула, ни кода до следующей полной загрузки.
/// Здесь — минимальное «досчитывание»: для видимых строк списка без единого кода один раз читается полная карточка (products/{id}/), и в базу и на строку
/// дописываются ТОЛЬКО пустые поля (непустое не трогается, пустым не затирается). Не больше <see cref="MaxPerBatch"/> товаров за раз, по одному, один раз за запуск.
/// </summary>
public static class ProductCodeBackfill
{
    /// <summary>Сколько карточек читаем за один показ страницы списка (страница склада — 50 строк; остальное подтянет полная загрузка).</summary>
    public const int MaxPerBatch = 20;

    private static readonly ConcurrentDictionary<string, byte> Tried = new(StringComparer.OrdinalIgnoreCase);
    private static int _running;

    /// <summary>У строки нет ни штрихкода, ни доп. штрихкодов, ни артикула, ни кода, ни PLU — колонке «Код / штрихкод» нечего показать.</summary>
    public static bool NeedsCodes(CatalogProductTileVm tile) =>
        string.IsNullOrWhiteSpace(tile.Barcode)
        && string.IsNullOrWhiteSpace(tile.Article)
        && string.IsNullOrWhiteSpace(tile.ProductCode)
        && string.IsNullOrWhiteSpace(tile.AlternateBarcodesRaw)
        && tile.Plu is not > 0;

    /// <summary>2026-11-06 (живой прогон 3, P3-54): штрихкод у строки есть (его ввели в карточке), а кода товара, который присваивает сервер, нет — ни артикула, ни кода, ни PLU.
    /// Колонка «Код / штрихкод» показывает только штрихкод. Такие строки в видимой части списка тоже дочитываются (один раз за запуск).</summary>
    public static bool NeedsServerCode(CatalogProductTileVm tile) =>
        !string.IsNullOrWhiteSpace(tile.Barcode)
        && string.IsNullOrWhiteSpace(tile.Article)
        && string.IsNullOrWhiteSpace(tile.ProductCode)
        && tile.Plu is not > 0;

    /// <summary>Дописать в <paramref name="target"/> пустые поля кодов значениями из <paramref name="fresh"/> (полная карточка). true — что-то дописано.</summary>
    public static bool MergeCodes(CatalogProductTileVm target, CatalogProductTileVm fresh)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(target.Barcode) && !string.IsNullOrWhiteSpace(fresh.Barcode))
        {
            target.Barcode = fresh.Barcode;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(target.Article) && !string.IsNullOrWhiteSpace(fresh.Article))
        {
            target.Article = fresh.Article;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(target.ProductCode) && !string.IsNullOrWhiteSpace(fresh.ProductCode))
        {
            target.ProductCode = fresh.ProductCode;
            changed = true;
        }

        // 2026-11-13: PLU из только что прочитанной карточки сервера главнее (возврат товара из архива даёт новый PLU) — см. FillMissingCodes.
        if (fresh.Plu is > 0 && target.Plu != fresh.Plu)
        {
            target.Plu = fresh.Plu;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(target.AlternateBarcodesRaw) && !string.IsNullOrWhiteSpace(fresh.AlternateBarcodesRaw))
        {
            target.AlternateBarcodesRaw = fresh.AlternateBarcodesRaw;
            target.AlternateBarcodeVariants = fresh.AlternateBarcodeVariants;
            changed = true;
        }

        if (changed)
            target.RaiseCodesChanged();
        return changed;
    }

    /// <summary>Полная карточка товара пришла: дописать коды в базу и (если передана) в строку списка. true — коды дописаны.</summary>
    public static bool ApplyDetail(CatalogProductTileVm? shown, JsonElement detail, string apiBaseUrl)
    {
        if (detail.ValueKind != JsonValueKind.Object || ProductCatalogMapper.TryTile(detail, apiBaseUrl) is not { } fresh)
            return false;
        var stored = LocalProductRepository.Instance.FillMissingCodes(fresh);
        if (shown is not null && string.Equals(shown.Id, fresh.Id, StringComparison.OrdinalIgnoreCase))
        {
            // Строка списка — объект интерфейса: свойства меняем в потоке интерфейса (Barcode уведомляет привязку).
            UiDispatcherHolder.Post(() => MergeCodes(shown, fresh));
        }

        return stored;
    }

    /// <summary>Видимые строки списка склада: у тех, где нет ни одного кода, один раз (за запуск) читаем полную карточку с сервера. Без связи и без ответа — молча пропускаем.</summary>
    /// <param name="force">2026-11-06 (P3-54): читать карточку, даже если какой-то код уже есть (только что созданный товар: код присвоил сервер).</param>
    public static int ScheduleForRows(IEnumerable<CatalogProductTileVm> rows, bool force = false)
    {
        try
        {
            if (PosApp.CatalogApi is not { } api || OfflineModeHelper.SellLocally)
                return 0;
            var pick = new List<CatalogProductTileVm>();
            foreach (var row in rows)
            {
                if (pick.Count >= MaxPerBatch)
                    break;
                if (string.IsNullOrWhiteSpace(row.Id) || (!force && !NeedsCodes(row) && !NeedsServerCode(row)) || Tried.ContainsKey(row.Id))
                    continue;
                pick.Add(row);
            }

            if (pick.Count == 0 || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return 0;
            if (Tried.Count > 20_000)
                Tried.Clear();
            foreach (var row in pick)
                Tried[row.Id] = 0;
            _ = Task.Run(() => RunAsync(api, pick));
            return pick.Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: коды товаров не досчитаны ({ex.GetType().Name}: {ex.Message}).", "CATALOG");
            return 0;
        }
    }

    // 2026-11-06 (Б-10): строка, у которой карточку не прочитали (нет связи, 5xx), пробуется снова при следующем показе, но не больше трёх раз за запуск.
    private static readonly ConcurrentDictionary<string, int> Failures = new(StringComparer.OrdinalIgnoreCase);

    private static void ForgetTried(string id)
    {
        if (Failures.AddOrUpdate(id, 1, (_, n) => n + 1) < 3)
            Tried.TryRemove(id, out _);
    }

    // Для тестов: состояние фонового дочитывания.
    internal static bool IsMarkedTried(string id) => Tried.ContainsKey(id);

    internal static bool IsRunning => Volatile.Read(ref _running) != 0;

    internal static void ResetForTests()
    {
        Tried.Clear();
        Failures.Clear();
        Interlocked.Exchange(ref _fromStorageScheduled, 0);
        Interlocked.Exchange(ref _lastFromStorageTick, 0);
    }

    /// <summary>Сколько товаров без кодов берём из хранилища за один запуск программы: у настоящих товаров без штрихкода (услуги, весовые без кода) карточку читать незачем.</summary>
    public const int MaxFromStoragePerRun = 400;

    private static int _fromStorageScheduled;
    private static long _lastFromStorageTick;

    /// <summary>2026-11-06 (класс Б, Б-7): самолечение базы на уровне хранилища, а не страницы списка Склада. Ревизия (и её Excel), сопоставление накладных по штрихкоду
    /// и фильтры печати читают те же плитки; в базе, заполненной сокращённой лентой, у части товаров нет ни одного кода. Раз в 30 секунд (зовёт опрос ленты) берём
    /// до <see cref="MaxPerBatch"/> таких товаров и дописываем пустые коды из полной карточки — не больше <see cref="MaxFromStoragePerRun"/> за запуск.</summary>
    public static void ScheduleFromStorage()
    {
        try
        {
            if (Volatile.Read(ref _fromStorageScheduled) >= MaxFromStoragePerRun)
                return;
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastFromStorageTick) < 30_000 && Interlocked.Read(ref _lastFromStorageTick) != 0)
                return;
            if (PosApp.CatalogApi is null || OfflineModeHelper.SellLocally)
                return;
            Interlocked.Exchange(ref _lastFromStorageTick, now);
            var rows = LocalProductRepository.Instance.LoadAllTiles()
                .Where(t => !string.IsNullOrWhiteSpace(t.Id) && NeedsCodes(t) && !Tried.ContainsKey(t.Id))
                .Take(MaxPerBatch)
                .ToList();
            if (rows.Count > 0)
                Interlocked.Add(ref _fromStorageScheduled, ScheduleForRows(rows));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: коды товаров из хранилища не досчитаны ({ex.GetType().Name}: {ex.Message}).", "CATALOG");
        }
    }

    private static async Task RunAsync(NurMarketKassa.Services.Api.ICatalogApiService api, List<CatalogProductTileVm> rows)
    {
        try
        {
            var apiBase = PosApp.Settings?.ApiBaseUrl ?? "";
            var filled = 0;
            foreach (var row in rows)
            {
                if (OfflineModeHelper.SellLocally)
                    break;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                JsonElement? detail;
                try { detail = await api.ProductsDetailAsync(row.Id, cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or ApiException)
                {
                    ForgetTried(row.Id); // 2026-11-06 (Б-10): не ответили — отметку «пробовали» снимаем, повтор при следующем показе
                    continue;
                }

                if (detail is not { } el)
                    ForgetTried(row.Id); // 5xx/429/404 — ProductsDetailAsync отдаёт null
                else if (ApplyDetail(row, el, apiBase))
                    filled++;
                await Task.Delay(150).ConfigureAwait(false); // не нагружаем сервер: по одной карточке
            }

            if (filled > 0)
                PosLogger.Log($"CATALOG: в списке склада у {filled} товаров дописаны штрихкод/код из карточки сервера (в базе их не было).", "CATALOG");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"CATALOG: коды товаров не досчитаны ({ex.GetType().Name}: {ex.Message}).", "CATALOG");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
