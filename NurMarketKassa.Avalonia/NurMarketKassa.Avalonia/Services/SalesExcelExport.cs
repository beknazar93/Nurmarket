using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-19, владелец: «при нажатии на Excel просто выведи все продажи, а не аналитику». Кнопка «Excel» в
/// «Продажах и финансах» и в «Продажах» — все чеки периода (SalesExportService.ExportToExcel), Word — прежняя аналитика.
///
/// Данные — тем же API, что список «Чеки» (SalesApiService.PosSalesListAsync, страницы по 500, по 3 сразу, до 100 страниц,
/// как FetchAllSalesAsync окон), а не загруженная в окно страница. Состав чека — из локальной истории кассы
/// (SoldLineItemsStore), чего там нет — из деталей чека с сервера (SaleDetailCache, общий темп массовых загрузок).
/// Без связи — по проданным строкам кассы, как экран (LoadFromLocalHistory).</summary>
internal static class SalesExcelExport
{
    private const int PageSize = 500;
    private const int MaxPages = 100;
    private const int ParallelPages = 3;

    /// <summary>Не больше стольких деталей чеков с сервера за одну выгрузку (≈ 5 минут при темпе ApiThrottle);
    /// больше — состав этих чеков не выгружается, в файле об этом строка (как MaxPerReceiptFallback «Финансов»).</summary>
    private const int MaxDetailRequests = 2000;

    /// <summary>Одна выгрузка за раз: второе нажатие, пока готовится первая, ничего не запускает.</summary>
    private static int _running;

    private static string Preparing => Tr.T("Готовлю отчёт…", "Отчёт даярдалууда…", "Preparing the report…", "Rapor hazırlanıyor…", "Hisobot tayyorlanmoqda…");

    /// <param name="setMessage">Строка сообщений окна (ErrorMessage).</param>
    /// <param name="markSuccess">Перекрасить строку сообщений в «готово».</param>
    public static async Task RunAsync(Window owner, DateTime from, DateTime to, Action<string?> setMessage, Action markSuccess)
    {
        if (!TariffGate.CanUseAnalyticsExport)
        {
            setMessage(TariffGate.AnalyticsExportLockedMessage);
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            setMessage(Preparing);
            return;
        }

        using var cts = new CancellationTokenSource();
        EventHandler onClosed = (_, _) => cts.Cancel();
        owner.Closed += onClosed;
        try
        {
            var shop = UserPreferences.Instance.StoreName;
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Tr.T("Сохранить продажи в Excel", "Сатууларды Excel'ге сактоо", "Save sales to Excel", "Satışları Excel olarak kaydet", "Sotuvlarni Excel'ga saqlash"),
                SuggestedFileName = SalesExportService.SuggestFileName(from, to, shop),
                FileTypeChoices = [new FilePickerFileType("Excel") { Patterns = ["*.xlsx"] }],
            }).ConfigureAwait(true);
            if (file is null)
                return;

            var path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path))
            {
                setMessage(Tr.T("Не удалось определить путь файла — выберите папку на этом компьютере.", "Файлдын жолун аныктоо мүмкүн болгон жок — ушул компьютердеги папканы тандаңыз.", "Could not determine the file path — choose a folder on this computer.", "Dosya yolu belirlenemedi — bu bilgisayarda bir klasör seçin.", "Fayl yo'lini aniqlab bo'lmadi — shu kompyuterdagi papkani tanlang."));
                return;
            }

            setMessage(Preparing);
            // Progress создан на потоке окна — сообщения приходят туда же.
            var progress = new Progress<string>(text => setMessage(text));
            var data = await LoadAsync(from, to, progress, cts.Token).ConfigureAwait(true);
            await Task.Run(() => SalesExportService.ExportToExcel(path!, data, shop), cts.Token).ConfigureAwait(true);

            setMessage(Tr.T($"Отчёт сохранён: {path}", $"Отчёт сакталды: {path}", $"Report saved: {path}", $"Rapor kaydedildi: {path}", $"Hisobot saqlandi: {path}"));
            markSuccess();
        }
        catch (OperationCanceledException)
        {
            PosLogger.Log("Выгрузка продаж в Excel отменена (окно закрыто).", "INFO");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выгрузка продаж в Excel не удалась: {ex}", "WARNING");
            setMessage(Tr.T("Не удалось сохранить отчёт: ", "Отчётту сактоо мүмкүн болгон жок: ", "Could not save the report: ", "Rapor kaydedilemedi: ", "Hisobotni saqlab bo'lmadi: ") + ex.Message);
        }
        finally
        {
            owner.Closed -= onClosed;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Все чеки периода с составом. Сервер не отвечает — по данным этой кассы.</summary>
    public static async Task<SalesExportService.Data> LoadAsync(DateTime from, DateTime to, IProgress<string>? progress, CancellationToken ct)
    {
        // Снимок каталога — на потоке окна, до первого await (общий список каталога не потокобезопасен).
        var catalog = CatalogCacheService.Products.ToList();

        if (!ServerOutageMonitor.IsOutage && !OfflineModeHelper.UseLocalOperations)
        {
            try
            {
                return await LoadFromServerAsync(from.Date, to.Date, catalog, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Выгрузка продаж: сервер недоступен ({ex.Message}) — беру данные этой кассы.", "WARNING");
            }
        }

        return await Task.Run(() => LoadLocal(from.Date, to.Date, catalog), ct).ConfigureAwait(false);
    }

    private static async Task<SalesExportService.Data> LoadFromServerAsync(DateTime from, DateTime to,
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> catalog, IProgress<string>? progress, CancellationToken ct)
    {
        // Сводка сервера (плитка «Выручка») — параллельно со списком; обычно уже в коротком кэше отчётов.
        var summaryTask = ServerSalesSummary.FetchAsync(from, to, ct);

        var rows = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = false;
        for (var firstPage = 1; firstPage <= MaxPages; firstPage += ParallelPages)
        {
            ct.ThrowIfCancellationRequested();
            var batch = await Task.WhenAll(Enumerable
                .Range(firstPage, Math.Min(ParallelPages, MaxPages - firstPage + 1))
                .Select(page => App.SalesApi.PosSalesListAsync(page, PageSize, null, ct, dateFrom: from, dateToExclusive: to.AddDays(1))))
                .ConfigureAwait(false);

            var done = false;
            foreach (var page in batch)
            {
                // Страница за последней повторяет последнюю — новых строк нет, это конец (как FetchAllSalesAsync окон).
                var fresh = 0;
                foreach (var row in page)
                {
                    var id = Str(row, "id");
                    if (id is not null && !seen.Add(id))
                        continue;
                    fresh++;
                    rows.Add(row);
                }

                if (fresh == 0 || page.Count < PageSize)
                {
                    done = true;
                    break;
                }
            }

            progress?.Report(Tr.T($"Готовлю отчёт… загружено чеков: {rows.Count}", $"Отчёт даярдалууда… жүктөлгөн чектер: {rows.Count}",
                $"Preparing the report… receipts loaded: {rows.Count}", $"Rapor hazırlanıyor… yüklenen fiş: {rows.Count}",
                $"Hisobot tayyorlanmoqda… yuklangan cheklar: {rows.Count}"));
            if (done)
                break;
            if (firstPage + ParallelPages > MaxPages)
                truncated = true;
        }

        var receipts = new List<SalesExportService.Receipt>(rows.Count);
        foreach (var row in rows)
        {
            var status = Str(row, "status") ?? "";
            // Полностью возвращённый чек сервер помечает «canceled» — в списке «Чеки» и в выручке его нет (2026-09-25).
            if (status.Equals("canceled", StringComparison.OrdinalIgnoreCase) || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!FinanceWindow.TryServerTime(Str(row, "created_at"), out var at))
                continue;
            // Подстраховка, как у окон: сервер проигнорировал период — период всё равно соблюдён.
            if (at.Date < from || at.Date > to)
                continue;
            receipts.Add(new SalesExportService.Receipt(
                Str(row, "id") ?? "",
                at,
                SalesWindow.TryReceiptNumber(row) ?? "",
                Num(row, "total"),
                Num(row, "discount_total"),
                Str(row, "payment_method") ?? "",
                status,
                Str(row, "user_display") ?? Str(row, "cashier_display") ?? Str(row, "cashier_name"),
                Str(row, "client_name") ?? Str(row, "client_display") ?? Str(row, "customer_name"),
                Str(row, "first_item_name"),
                null));
        }

        receipts = AssignFallbackNumbers(receipts);
        var (withLines, skipped, failed) = await AttachLinesAsync(receipts, catalog, progress, ct).ConfigureAwait(false);

        double? serverRevenue = null;
        try
        {
            if (await summaryTask.ConfigureAwait(false) is { Revenue: { } revenue })
                serverRevenue = (double)revenue;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Выгрузка продаж: сводка сервера не получена ({ex.Message}).", "WARNING");
        }

        return new SalesExportService.Data(from, to, withLines, LocalOnly: false, truncated, skipped, failed, serverRevenue);
    }

    /// <summary>Состав чеков: сначала локальная история (свои чеки и подтянутые с других касс — без запросов), остальное —
    /// детали чека с сервера через общий кэш и темп (ApiThrottle: не больше 3 сразу и не чаще раза в 150 мс).</summary>
    private static async Task<(List<SalesExportService.Receipt> Receipts, int Skipped, int Failed)> AttachLinesAsync(
        List<SalesExportService.Receipt> receipts, List<NurMarketKassa.Models.Pos.CatalogProductTileVm> catalog,
        IProgress<string>? progress, CancellationToken ct)
    {
        var ids = receipts.Select(r => r.Id).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var local = await Task.Run(() => SoldLineItemsStore.LinesBySale(ids), ct).ConfigureAwait(false);
        var byId = catalog.Where(p => !string.IsNullOrEmpty(p.Id))
            .GroupBy(p => p.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = catalog.Where(p => !string.IsNullOrWhiteSpace(p.Title))
            .GroupBy(p => p.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var lines = new Dictionary<string, List<SalesExportService.Line>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, own) in local)
        {
            if (own.Count > 0)
                lines[id] = own.Select(l => new SalesExportService.Line(l.ProductName, l.Quantity,
                    UnitOf(null, null, l.ProductName, l.Quantity, byId, byName), l.UnitPrice, Math.Round(l.Quantity * l.UnitPrice, 2))).ToList();
        }

        var missing = ids.Where(id => !lines.ContainsKey(id)).ToList();
        var skipped = 0;
        var failed = 0;
        if (missing.Count > MaxDetailRequests)
        {
            skipped = missing.Count;
            PosLogger.Log($"Выгрузка продаж: состав {missing.Count} чеков не запрашивался (больше {MaxDetailRequests}).", "WARNING");
        }
        else if (missing.Count > 0)
        {
            var done = 0;
            var total = missing.Count;
            await Task.WhenAll(missing.Select(async id =>
            {
                try
                {
                    var detail = await SaleDetailCache.GetWithRetryAsync(id, ct).ConfigureAwait(false);
                    var parsed = CartDisplayHelper.EnumerateSaleLineItems(detail)
                        .Select(it =>
                        {
                            var qty = CartDisplayHelper.LineQuantity(it);
                            var name = CartDisplayHelper.ItemName(it);
                            var sum = double.TryParse(CartDisplayHelper.LineTotal(it), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) ? s : 0;
                            return new SalesExportService.Line(name, qty,
                                UnitOf(Str(it, "unit"), CartDisplayHelper.TryProductId(it), name, qty, byId, byName),
                                CartDisplayHelper.UnitPrice(it), sum);
                        })
                        .ToList();
                    lock (lines)
                        lines[id] = parsed;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failed);
                    PosLogger.Log($"Выгрузка продаж: чек {id} не прочитан: {SaleDetailCache.Describe(ex)}", "DEBUG");
                }

                var n = Interlocked.Increment(ref done);
                progress?.Report(Tr.T($"Готовлю отчёт… товары чеков: {n} из {total}", $"Отчёт даярдалууда… чектердин товарлары: {n} / {total}",
                    $"Preparing the report… receipt products: {n} of {total}", $"Rapor hazırlanıyor… fiş ürünleri: {n} / {total}",
                    $"Hisobot tayyorlanmoqda… chek mahsulotlari: {n} / {total}"));
            })).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }

        var result = receipts
            .Select(r => lines.TryGetValue(r.Id, out var l) ? r with { Lines = l } : r)
            .ToList();
        return (result, skipped, failed);
    }

    /// <summary>Без связи — то же, что экран (LoadFromLocalHistory окон): проданные строки кассы, чек — одна отметка времени.</summary>
    private static SalesExportService.Data LoadLocal(DateTime from, DateTime to, List<NurMarketKassa.Models.Pos.CatalogProductTileVm> catalog)
    {
        var byId = catalog.Where(p => !string.IsNullOrEmpty(p.Id))
            .GroupBy(p => p.Id!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var byName = new Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);

        var lines = SoldLineItemsStore.LoadWithPriceSince(from.ToUniversalTime(), to.AddDays(1).ToUniversalTime());
        var receipts = lines
            .GroupBy(l => l.SoldAt)
            .OrderBy(g => g.Key)
            .Select((g, index) =>
            {
                var items = g.Select(l => new SalesExportService.Line(l.ProductName, l.Quantity,
                    UnitOf(null, l.ProductId, l.ProductName, l.Quantity, byId, byName), l.UnitPrice, Math.Round(l.Quantity * l.UnitPrice, 2))).ToList();
                return new SalesExportService.Receipt(
                    "local-" + g.Key.Ticks.ToString(CultureInfo.InvariantCulture),
                    g.Key.ToLocalTime(),
                    "№" + (index + 1).ToString(CultureInfo.InvariantCulture),
                    g.Sum(x => x.Quantity * x.UnitPrice),
                    0, "", "", null, null, items.FirstOrDefault()?.Name, items);
            })
            .ToList();
        return new SalesExportService.Data(from, to, receipts, LocalOnly: true);
    }

    /// <summary>Сервер не всегда присылает номер чека — порядковый «№1», «№2»… по времени, как AssignFallbackReceiptNumbers окон.</summary>
    private static List<SalesExportService.Receipt> AssignFallbackNumbers(List<SalesExportService.Receipt> receipts)
    {
        var position = 0;
        return receipts.OrderBy(r => r.CreatedAt)
            .Select(r =>
            {
                position++;
                return string.IsNullOrWhiteSpace(r.Number) ? r with { Number = "№" + position.ToString(CultureInfo.InvariantCulture) } : r;
            })
            .ToList();
    }

    /// <summary>Единица строки: из строки чека, иначе из каталога (по товару или названию), иначе кг для дробного
    /// количества и шт для целого.</summary>
    private static string UnitOf(string? lineUnit, string? productId, string name, double quantity,
        Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm> byId,
        Dictionary<string, NurMarketKassa.Models.Pos.CatalogProductTileVm> byName)
    {
        if (!string.IsNullOrWhiteSpace(lineUnit))
            return lineUnit!.Trim();
        if (!string.IsNullOrEmpty(productId) && byId.TryGetValue(productId!, out var tile) && !string.IsNullOrWhiteSpace(tile.Unit))
            return tile.Unit!.Trim();
        if (byName.TryGetValue(name.Trim(), out var named) && !string.IsNullOrWhiteSpace(named.Unit))
            return named.Unit!.Trim();
        return Math.Abs(quantity % 1) > 0.0000001
            ? ReportLabels.Kg
            : Tr.T("шт", "даана", "pcs", "adet", "dona");
    }

    private static string? Str(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static double Num(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && JsonNumericReader.TryToDouble(v, out var d)
            ? d
            : 0;
}
