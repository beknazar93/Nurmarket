using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Приёмка товара на склад — по образцу «Массового сканирования» сайта (2026-09-24).
///
/// Раньше приёмка кассы проводилась актом инвентаризации: остаток прибавлялся, но ни цены
/// закупки, ни поставщика, ни следа в истории товара не оставалось. Теперь:
///  • штрихкод ищется по своему складу, затем по общей базе CRM (товар узнан, но у магазина
///    его ещё нет), и только потом считается неизвестным — те же три колонки, что на сайте;
///  • у каждой строки обязательны цена закупки и цена продажи;
///  • с поставщиком приход проводится документом «Закупки» сайта (сервер сам прибавляет
///    остаток и помнит цену закупки), без поставщика — как у сайта: остаток и цены пишутся
///    прямо в товар;
///  • каждая принятая строка ложится в историю закупок товара — её видно в карточке.</summary>
public sealed class PurchaseReceivingService
{
    public static PurchaseReceivingService Instance { get; } = new();

    /// <param name="Inn">2026-10-19: ИНН поставщика из NurCRM, если сервер его отдаёт (поле inn), — сверка с распознанной накладной.</param>
    /// <param name="Phone">2026-10-20: телефон поставщика (поле phone) — поиск поставщика в карточке товара по номеру.</param>
    public sealed record Supplier(string Id, string Name, string? Inn = null, string? Phone = null);

    public sealed record HistoryEntry(
        DateTime At,
        double Quantity,
        string? Unit,
        double PurchasePrice,
        double? SalePrice,
        string? SupplierName,
        string? Employee,
        string Source,
        string? ReceiptId);

    /// <param name="ReceiptId">2026-10-20, этап П3: id прихода от поставщика из ответа сервера (для записи долга «в долг»); без поставщика — null.</param>
    /// <param name="ReceiptTotal">2026-10-20, этап П3: сумма прихода по ответу сервера (total_amount), если сервер её вернул.</param>
    /// <param name="ReceiptAt">2026-10-20, этап П3: время прихода по ответу сервера (created_at, местное), если вернул.</param>
    /// <param name="ReceiptKey">2026-10-20, этап П3: ключ идемпотентности прихода — метка долга, если сервер не вернул id.</param>
    public sealed record PostResult(IReadOnlyList<ReceivingLineVm> PostedLines, double TotalAmount, int Created, IReadOnlyList<string> Errors,
        string? ReceiptId = null, double? ReceiptTotal = null, DateTime? ReceiptAt = null, string? ReceiptKey = null);

    private bool _tableReady;

    /// <summary>2026-10-15, ревью: ключ идемпотентности документа прихода от поставщика. Живёт до ответа сервера: таймаут —
    /// строки остаются в списке, кассир жмёт «Провести» ещё раз, и тот же приход (тот же поставщик, товары, количества,
    /// цены) уходит с тем же ключом, а не вторым документом с двойным остатком и двойным долгом поставщику.
    /// Новый ключ — после успеха, отказа сервера по существу (4xx) или если состав прихода изменился.
    /// 2026-10-16, ревью: общий PendingIdempotencyKey вместо скопированной логики (то же правило 4xx, кроме 408/429).</summary>
    private readonly PendingIdempotencyKey _pendingReceiptKey = new();

    /// <summary>2026-10-15, ревью: приёмка без поставщика — какой остаток касса записала в товар последним PATCH.
    /// Если ответ потерялся (таймаут), строка осталась в списке, и повторное «Провести» прибавляло количество ещё раз
    /// (остаток читается свежий, уже с прибавкой). Теперь при повторе: остаток на сервере равен записанному — PATCH дошёл,
    /// количество второй раз не прибавляется.
    /// 2026-10-16, ревью: сравнение только с записанным остатком ломалось от любой продажи в это время (остаток уже не
    /// равен ни прежнему, ни записанному) — количество прибавлялось второй раз. Теперь помним и остаток ДО прибавки:
    /// свежий = прежний — PATCH не дошёл, прибавляем; = записанный — дошёл, не прибавляем; иначе исход не понять —
    /// количество НЕ прибавляем, строка остаётся в списке с предупреждением проверить остаток вручную.</summary>
    private sealed record PendingStockPatch(double PreStock, double Target);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ReceivingLineVm, PendingStockPatch> _pendingStockPatches = new();

    /// <summary>2026-10-19, ревью: цена продажи, с которой касса создала новый товар строки. Она уже в карточке — отдельный PATCH
    /// цены после прихода нужен, только если её поправили позже (повтор после обрыва).</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ReceivingLineVm, StrongBox<double>> _createdSalePrice = new();

    /// <summary>2026-10-22 (закупки как в вебе): цена продажи товара строки на сервере до первой попытки прихода — какой ей остаться.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ReceivingLineVm, StrongBox<double>> _priceBefore = new();

    private static ICatalogApiService Api => PosApp.CatalogApi;

    // ------------------------------------------------------------------ 2026-10-25 (ТЗ «оприходование в фоне», Q1): состояние повторов

    /// <summary>Понятный текст вместо английского «The request was canceled due to the configured HttpClient.Timeout…» (TaskCanceledException).</summary>
    public static string NoAnswerText => Tr.T(
        "сервер не ответил вовремя или нет связи — операция будет повторена",
        "сервер өз убагында жооп берген жок же байланыш жок — операция кайталанат",
        "the server did not answer in time or there is no connection — the operation will be retried",
        "sunucu zamanında yanıt vermedi veya bağlantı yok — işlem tekrarlanacak",
        "server o'z vaqtida javob bermadi yoki aloqa yo'q — amal qayta bajariladi");

    private static string VariantsCheckText(string productName) => Tr.T(
        $"{productName}: товар принят, но остатки размеров могли не обновиться — проверьте в «Размеры и цвета».",
        $"{productName}: товар кабыл алынды, бирок өлчөмдөрдүн калдыгы жаңырбай калган болушу мүмкүн — «Өлчөмдөр жана түстөр» бөлүмүн текшериңиз.",
        $"{productName}: received, but the size stock may not have been updated — check “Sizes and colors”.",
        $"{productName}: kabul edildi ancak beden stokları güncellenmemiş olabilir — «Bedenler ve renkler»i kontrol edin.",
        $"{productName}: qabul qilindi, lekin o'lchamlar qoldig'i yangilanmagan bo'lishi mumkin — «O'lchamlar va ranglar»ni tekshiring.");

    private bool PatchTryGet(ReceivingPostState? st, ReceivingLineVm l, out PendingStockPatch patch)
    {
        if (st is null)
            return _pendingStockPatches.TryGetValue(l, out patch!);
        var ok = st.TryGetPatch(l, out var p);
        patch = new PendingStockPatch(p.PreStock, p.Target);
        return ok;
    }

    private void PatchSet(ReceivingPostState? st, ReceivingLineVm l, double preStock, double target)
    {
        if (st is null)
            _pendingStockPatches.AddOrUpdate(l, new PendingStockPatch(preStock, target));
        else
            st.SetPatch(l, preStock, target);
    }

    private void PatchRemove(ReceivingPostState? st, ReceivingLineVm l)
    {
        if (st is null)
            _pendingStockPatches.Remove(l);
        else
            st.RemovePatch(l);
    }

    private bool CreatedPriceTryGet(ReceivingPostState? st, ReceivingLineVm l, out double price)
    {
        if (st is not null)
            return st.TryGetCreatedPrice(l, out price);
        var ok = _createdSalePrice.TryGetValue(l, out var box);
        price = box?.Value ?? 0;
        return ok;
    }

    private void CreatedPriceSet(ReceivingPostState? st, ReceivingLineVm l, double price)
    {
        if (st is null)
            _createdSalePrice.AddOrUpdate(l, new StrongBox<double>(price));
        else
            st.SetCreatedPrice(l, price);
    }

    private bool PriceBeforeTryGet(ReceivingPostState? st, ReceivingLineVm l, out double price)
    {
        if (st is not null)
            return st.TryGetPriceBefore(l, out price);
        var ok = _priceBefore.TryGetValue(l, out var box);
        price = box?.Value ?? 0;
        return ok;
    }

    private void PriceBeforeSet(ReceivingPostState? st, ReceivingLineVm l, double price)
    {
        if (st is null)
            _priceBefore.AddOrUpdate(l, new StrongBox<double>(price));
        else
            st.SetPriceBefore(l, price);
    }

    private static bool IsLineOutcome(ReceivingPostState st, ReceivingLineVm l, string outcome)
    {
        lock (st.Gate)
            return st.LineOutcome.TryGetValue(st.KeyOf(l), out var o) && o == outcome;
    }

    /// <summary>Отказ по строке в приходе из очереди: сеть/таймаут/5xx/429 — «исход неизвестен» (повторим), 4xx — «отказ» (не повторяем сами).</summary>
    private static void NoteLineFailure(ReceivingPostState? st, ReceivingLineVm line, Exception ex)
    {
        if (st is null)
            return;
        st.NoteFailure(ex);
        st.SetOutcome(line, IsQueueRetryable(ex) ? "unknown" : "rejected");
        if (ex is OperationCanceledException)
            st.Interrupted = true;
    }

    /// <summary>Приход от поставщика из очереди: если прошлая отправка осталась без ответа (исход неизвестен) — сперва ищем этот приход на
    /// сервере (тот же поставщик, не раньше первой отправки минус 2 мин, тот же набор «товар:количество:цена закупки»); нашли — считаем
    /// проведённым и НЕ шлём второй раз (сервер может не смотреть idempotency_key). Список не прочитали — не шлём, повтор позже.
    /// Иначе — POST с ключом задачи (создаётся и записывается на диск до отправки).</summary>
    private async Task<(bool Ok, string? Id, double? Total, DateTime? At)> SendReceiptWithStateAsync(ReceivingPostState st, Supplier supplier,
        Dictionary<string, object?> body, List<ReceivingLineVm> ready, List<string> errors, CancellationToken ct)
    {
        if (st.ReceiptOutcome == ReceivingReceiptOutcome.Posted)
            return (true, st.ReceiptId, st.ReceiptTotal, st.ReceiptAt);

        if (st.ReceiptOutcome == ReceivingReceiptOutcome.Unknown)
        {
            try
            {
                if (await FindPostedReceiptAsync(st, supplier, ready, ct).ConfigureAwait(false) is { } found)
                {
                    PosLogger.Log($"Приход из очереди: приход {found.Id} уже есть на сервере — второй раз не отправляем.", "STOCK");
                    st.MarkPosted(found.Id, found.Total, found.At);
                    return (true, found.Id, found.Total, found.At);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !st.IsStopping(ct))
            {
                st.NoteFailure(ex);
                if (ex is OperationCanceledException)
                    st.Interrupted = true;
                errors.Add(Tr.T("Не удалось проверить, проведён ли приход (повторим): ", "Кабыл алуу өткөрүлгөнүн текшерүү мүмкүн болгон жок (кайталайбыз): ",
                    "Could not check whether the delivery was posted (will retry): ", "Mal kabulünün işlenip işlenmediği kontrol edilemedi (tekrarlanacak): ",
                    "Kirim o'tkazilganini tekshirib bo'lmadi (qayta uriniladi): ") + Msg2(ex));
                return (false, null, null, null);
            }
        }

        body["idempotency_key"] = st.BeginSend();
        try
        {
            var response = await Api.CreateSupplierReceiptAsync(supplier.Id, body, ct).ConfigureAwait(false);
            var id = Str(response, "id");
            double? total = Num(response, "total_amount") is > 0 and var serverTotal ? serverTotal : null;
            var at = SupplierApiService.Date(Str(response, "created_at") ?? "");
            st.MarkPosted(id, total, at);
            return (true, id, total, at);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !st.IsStopping(ct))
        {
            st.NoteFailure(ex);
            if (ex is OperationCanceledException)
                st.Interrupted = true;
            else if (!IsQueueRetryable(ex))
                st.MarkRejected();
            errors.Add(Tr.T("Приход от поставщика не проведён: ", "Жеткирүүчүдөн келген товар кабыл алынган жок: ",
                "The supplier delivery was not posted: ", "Tedarikçiden mal kabulü yapılamadı: ",
                "Yetkazib beruvchidan kirim o'tkazilmadi: ") + Msg2(ex));
            return (false, null, null, null);
        }
    }

    /// <summary>Сбой, после которого очередь повторит попытку: исход неизвестен (сеть, таймаут, 5xx, 408, 429) или нет входа (401 — ждём вход).</summary>
    private static bool IsQueueRetryable(Exception ex) => PendingIdempotencyKey.IsOutcomeUnknown(ex) || ex is ApiException { StatusCode: 401 };

    private static string Msg2(Exception ex) => IsNoAnswer(ex) ? NoAnswerText : ex.Message;

    /// <summary>Таймаут, обрыв, нет связи — один понятный текст вместо английского «The request was canceled due to the configured HttpClient.Timeout…».</summary>
    private static bool IsNoAnswer(Exception ex) => ex is OperationCanceledException or System.Net.Http.HttpRequestException or IOException or TimeoutException;

    private async Task<(string? Id, double? Total, DateTime? At)?> FindPostedReceiptAsync(ReceivingPostState st, Supplier supplier,
        List<ReceivingLineVm> ready, CancellationToken ct)
    {
        string Sig(string? product, double qty, double price) =>
            product + ":" + qty.ToString("0.###", CultureInfo.InvariantCulture) + ":" + price.ToString("0.##", CultureInfo.InvariantCulture);
        var wanted = ready.Select(l => Sig(l.ProductId, l.Quantity, l.PurchasePrice)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var since = (st.ReceiptFirstSentUtc ?? DateTime.UtcNow).ToLocalTime().AddMinutes(-2);
        for (var page = 1; page <= 3; page++)
        {
            var data = await Api.ListSupplierReceiptsAsync(page, 50, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("results", out var receipts) || receipts.ValueKind != JsonValueKind.Array)
                break;
            foreach (var receipt in receipts.EnumerateArray())
            {
                var id = Str(receipt, "id");
                if (id is null || st.ReceiptClaimed?.Invoke(id) == true)
                    continue;
                var supplierId = Str(receipt, "supplier") ?? Str(receipt, "supplier_id");
                var supplierName = Str(receipt, "supplier_name");
                var ours = supplierId is not null
                    ? string.Equals(supplierId, supplier.Id, StringComparison.OrdinalIgnoreCase)
                    : supplierName is null || string.Equals(supplierName.Trim(), supplier.Name.Trim(), StringComparison.CurrentCultureIgnoreCase);
                if (!ours || SupplierApiService.Date(Str(receipt, "created_at") ?? "") is not { } at || at < since)
                    continue;
                if (!receipt.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                var have = items.EnumerateArray()
                    .Select(i => Sig(Str(i, "product_id") ?? Str(i, "product"), Num(i, "qty"), Num(i, "purchase_price")))
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
                if (have.SequenceEqual(wanted, StringComparer.Ordinal))
                    return (id, Num(receipt, "total_amount") is > 0 and var total ? total : null, at);
            }
            if (!data.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                break;
        }
        return null;
    }

    // ------------------------------------------------------------------ поставщики

    public async Task<List<Supplier>> LoadSuppliersAsync(CancellationToken ct = default)
    {
        var data = await Api.ListSuppliersAsync(ct).ConfigureAwait(false);
        var rows = data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray()
                : default;

        var list = new List<Supplier>();
        foreach (var row in rows)
        {
            var id = Str(row, "id");
            var name = Str(row, "full_name") ?? Str(row, "name") ?? Str(row, "llc") ?? Str(row, "phone");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                list.Add(new Supplier(id!, name!.Trim(), Str(row, "inn")?.Trim(), Str(row, "phone")?.Trim()));
        }

        return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>2026-10-20, владелец: «+ Новый поставщик» в карточке товара — создаёт поставщика в NurCRM (clients/, type=suppliers,
    /// как сайт). Возвращает созданного (id из ответа сервера); null — сервер не вернул id.</summary>
    public async Task<Supplier?> CreateSupplierAsync(string name, string phone, CancellationToken ct = default)
    {
        var row = await Api.CreateSupplierAsync(name, phone, ct).ConfigureAwait(false);
        var id = Str(row, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
        return new Supplier(id!, (Str(row, "full_name") ?? name).Trim(), Str(row, "inn")?.Trim(), (Str(row, "phone") ?? phone).Trim());
    }

    /// <summary>2026-10-19: поставщик из списка NurCRM, похожий на распознанного в накладной: сначала по ИНН (цифры совпали), затем
    /// по названию без кавычек и формы (ОсОО, ООО, ИП…). Только предложение — выбирает владелец. null — похожего нет.</summary>
    public static Supplier? MatchSupplier(IEnumerable<Supplier> suppliers, string? name, string? inn)
    {
        var list = suppliers.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();
        var innDigits = new string((inn ?? "").Where(char.IsDigit).ToArray());
        if (innDigits.Length >= 6 && list.FirstOrDefault(s => new string((s.Inn ?? "").Where(char.IsDigit).ToArray()) == innDigits) is { } byInn)
            return byInn;
        var key = SupplierNameKey(name);
        if (key.Length < 3)
            return null;
        return list.FirstOrDefault(s => SupplierNameKey(s.Name) == key)
               ?? (list.Where(s => SupplierNameKey(s.Name) is { Length: >= 4 } k && (k.Contains(key, StringComparison.Ordinal) || key.Contains(k, StringComparison.Ordinal)))
                   .Take(2).ToList() is { Count: 1 } one ? one[0] : null);
    }

    /// <summary>2026-10-19: название поставщика для сверки — без регистра, кавычек, знаков и организационно-правовой формы.</summary>
    public static string SupplierNameKey(string? name)
    {
        var words = System.Text.RegularExpressions.Regex.Split((name ?? "").ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0 && w is not ("осоо" or "ооо" or "оао" or "зао" or "ао" or "пао" or "ип" or "чп" or "тоо" or "llc" or "ltd" or "фх"))
            .ToList();
        return string.Join(" ", words);
    }

    /// <summary>2026-10-19: итог проведения для кассира — тот же текст у «Приёмки» склада и закупки по скану накладной.</summary>
    public static string SummaryText(PostResult result)
    {
        var summary = Tr.T($"Принято позиций: {result.PostedLines.Count} на {result.TotalAmount:0.##} сом.",
            $"Кабыл алынды: {result.PostedLines.Count} позиция, {result.TotalAmount:0.##} сом.",
            $"Received {result.PostedLines.Count} items worth {result.TotalAmount:0.##} som.",
            $"Kabul edildi: {result.PostedLines.Count} kalem, {result.TotalAmount:0.##} som.",
            $"Qabul qilingan pozitsiyalar: {result.PostedLines.Count}, jami {result.TotalAmount:0.##} so'm.");
        if (result.Created > 0)
            summary += " " + Tr.T($"Новых товаров создано: {result.Created}.", $"Жаңы товар түзүлдү: {result.Created}.",
                $"New products created: {result.Created}.", $"Oluşturulan yeni ürün: {result.Created}.",
                $"Yaratilgan yangi mahsulotlar: {result.Created}.");
        return summary;
    }

    // ------------------------------------------------------------------ скан

    /// <summary>Строка приёмки по штрихкоду: свой склад (сначала локальный каталог, он мгновенный,
    /// потом сервер — вдруг товар добавили на сайте минуту назад), общая база CRM, неизвестный.</summary>
    public async Task<ReceivingLineVm> LookupAsync(string barcode, double quantity, CancellationToken ct = default)
    {
        var code = barcode.Trim();

        // 2026-10-21: этот компьютер — тем же поиском, что касса (ProductCodeLookup: штрихкод в базе кассы, id, доп.
        // штрихкод пока индекс не прогрет, размер, артикул), без весовой этикетки (склад не находит товар по скану).
        var tile = ProductCodeLookup.FindLocal(code, allowWeightLabel: false)?.Product;
        if (tile is not null)
            return FromTile(tile, quantity);

        try
        {
            var product = await Api.FindWarehouseProductByBarcodeAsync(code, ct).ConfigureAwait(false);
            if (product is { } p)
            {
                var price = Num(p, "price");
                var purchase = Num(p, "purchase_price");
                var name = Str(p, "name") ?? code;
                var unit = Str(p, "unit") ?? "шт";
                return new ReceivingLineVm
                {
                    ProductId = Str(p, "id"),
                    Barcode = Str(p, "barcode") ?? code,
                    ProductName = name,
                    Unit = unit,
                    OriginalName = name,
                    OriginalUnit = unit,
                    Source = ReceivingSource.Warehouse,
                    StockBefore = Num(p, "quantity"),
                    Quantity = quantity,
                    PurchasePrice = purchase,
                    SalePrice = price,
                    OriginalPurchasePrice = purchase,
                    OriginalSalePrice = price,
                    OriginalMarkupPercent = Num(p, "markup_percent"),
                };
            }

            var global = await Api.FindGlobalProductByBarcodeAsync(code, ct).ConfigureAwait(false);
            // 2026-10-03: UPC-A из сканера (12 цифр) ↔ EAN-13 с нулём в общей базе — см. BarcodeForms.
            if (global is null && BarcodeForms.Alternate(code) is { } alternate)
                global = await Api.FindGlobalProductByBarcodeAsync(alternate, ct).ConfigureAwait(false);
            if (global is { } g)
            {
                return new ReceivingLineVm
                {
                    Barcode = Str(g, "barcode") ?? code,
                    ProductName = Str(g, "name") ?? "",
                    Source = ReceivingSource.GlobalBase,
                    Quantity = quantity,
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Нет связи — не повод терять скан: строка встанет как новая, и название можно
            // вписать руками. При проведении сервер всё равно ещё раз увидит штрихкод.
            PosLogger.Log($"Приёмка: поиск штрихкода {code} на сервере не удался: {ex.Message}", "WARNING");
        }

        return new ReceivingLineVm
        {
            Barcode = code,
            ProductName = "",
            Source = ReceivingSource.Unknown,
            Quantity = quantity,
        };
    }

    public static ReceivingLineVm FromTile(NurMarketKassa.Models.Pos.CatalogProductTileVm tile, double quantity)
    {
        var price = LocalCartService.ParsePrice(tile.PriceLine ?? "");
        var unit = string.IsNullOrWhiteSpace(tile.Unit) ? "шт" : tile.Unit!;
        return new ReceivingLineVm
        {
            ProductId = tile.Id,
            Barcode = tile.Barcode ?? "",
            ProductName = tile.Title,
            Unit = unit,
            OriginalName = tile.Title,
            OriginalUnit = unit,
            Source = ReceivingSource.Warehouse,
            StockBefore = tile.Quantity,
            Quantity = quantity,
            PurchasePrice = tile.PurchasePrice,
            SalePrice = price,
            OriginalPurchasePrice = tile.PurchasePrice,
            OriginalSalePrice = price,
            OriginalMarkupPercent = tile.MarkupPercent,
        };
    }

    // ------------------------------------------------------------------ проведение

    /// <summary>Проверка перед проведением: названия у новых товаров, количество и обе цены.
    /// Пустой список — можно проводить.</summary>
    public static List<string> Validate(IReadOnlyList<ReceivingLineVm> lines)
    {
        var problems = new List<string>();
        var newBarcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            // Строка «+ Новый товар» может быть без штрихкода — тогда в сообщении нечем её назвать.
            var label = string.IsNullOrWhiteSpace(line.Barcode)
                ? Tr.T("новый товар", "жаңы товар", "new product", "yeni ürün", "yangi mahsulot")
                : line.Barcode;
            var name = string.IsNullOrWhiteSpace(line.ProductName) ? label : line.ProductName;
            if (string.IsNullOrWhiteSpace(line.ProductName))
                problems.Add(Tr.T($"{label}: впишите название нового товара",
                    $"{label}: жаңы товардын атын жазыңыз", $"{label}: enter a name for the new product",
                    $"{label}: yeni ürünün adını girin", $"{label}: yangi mahsulot nomini kiriting"));

            // Штрихкод, вписанный руками у нового товара, не должен совпасть с уже существующим —
            // иначе на складе появятся два товара с одним кодом (2026-09-26).
            if (line.IsNew && !string.IsNullOrWhiteSpace(line.Barcode))
            {
                if (!newBarcodes.Add(line.Barcode))
                    problems.Add(Tr.T($"{name}: штрихкод {line.Barcode} встречается в приёмке дважды",
                        $"{name}: {line.Barcode} штрихкоду кабыл алууда эки жолу кездешет", $"{name}: barcode {line.Barcode} appears twice in this receiving",
                        $"{name}: barkod {line.Barcode} mal kabulde iki kez geçiyor", $"{name}: {line.Barcode} shtrix-kodi qabul qilishda ikki marta uchraydi"));
                // 2026-10-19, ревью: товар строки уже создан прошлой попыткой (ProductId задан, ответ на приход потерялся) — после
                // обновления каталога его штрихкод находит ЭТОТ же товар: это не чужой товар, а приход к нему (повтор не блокируем).
                else if (line.Source == ReceivingSource.Unknown
                         && CatalogCacheService.Products.FirstOrDefault(p =>
                             string.Equals(p.Barcode, line.Barcode, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } owner
                         && string.IsNullOrWhiteSpace(line.ProductId))
                    problems.Add(Tr.T($"{name}: штрихкод {line.Barcode} уже есть у товара «{owner.Title}» — отсканируйте его, чтобы принять",
                        $"{name}: {line.Barcode} штрихкоду «{owner.Title}» товарында мурунтан бар — кабыл алуу үчүн аны сканерлеңиз",
                        $"{name}: barcode {line.Barcode} already belongs to “{owner.Title}” — scan that product to receive it",
                        $"{name}: barkod {line.Barcode} zaten «{owner.Title}» ürününde var — kabul etmek için o ürünü okutun",
                        $"{name}: {line.Barcode} shtrix-kodi «{owner.Title}» mahsulotida allaqachon bor — qabul qilish uchun uni skanerlang"));
            }
            // 2026-10-06 (О-70): количество строки с размерами — сумма по размерам; поправили руками — пусть откроют «Размеры».
            if (line.VariantQuantities is { Count: > 0 } && Math.Abs(line.Quantity - line.VariantTotal) > 0.0005)
                problems.Add(Tr.T($"{name}: принято {line.Quantity:0.###}, а по размерам {line.VariantTotal:0.###} — поправьте в колонке «Размеры»",
                    $"{name}: {line.Quantity:0.###} кабыл алынды, ал эми өлчөмдөр боюнча {line.VariantTotal:0.###} — «Өлчөмдөр» тилкесинде оңдоңуз",
                    $"{name}: received {line.Quantity:0.###}, but {line.VariantTotal:0.###} by size — fix it in the “Sizes” column",
                    $"{name}: {line.Quantity:0.###} kabul edildi, bedenlere göre {line.VariantTotal:0.###} — «Bedenler» sütununda düzeltin",
                    $"{name}: {line.Quantity:0.###} qabul qilindi, o'lchamlar bo'yicha {line.VariantTotal:0.###} — «O'lchamlar» ustunida tuzating"));
            if (line.Quantity <= 0)
                problems.Add(Tr.T($"{name}: не указано количество", $"{name}: саны көрсөтүлгөн эмес",
                    $"{name}: quantity missing", $"{name}: miktar belirtilmedi", $"{name}: miqdor ko'rsatilmagan"));
            if (line.PurchasePrice <= 0)
                problems.Add(Tr.T($"{name}: не указана цена закупки", $"{name}: сатып алуу баасы көрсөтүлгөн эмес",
                    $"{name}: purchase price missing", $"{name}: alış fiyatı belirtilmedi", $"{name}: xarid narxi ko'rsatilmagan"));
            if (line.SalePrice <= 0)
                problems.Add(Tr.T($"{name}: не указана цена продажи", $"{name}: сатуу баасы көрсөтүлгөн эмес",
                    $"{name}: sale price missing", $"{name}: satış fiyatı belirtilmedi", $"{name}: sotuv narxi ko'rsatilmagan"));
        }

        return problems;
    }

    /// <summary>Провести приёмку. <paramref name="paidNow"/> — только с поставщиком: true — оплачено
    /// сразу (деньги уходят из кассы расходов, как у сайта), false — в долг поставщику.</summary>
    /// <param name="paymentFields">2026-10-22 (закупки как в вебе): «Оплачено сразу» не из кассы расходов — поля кассы для прихода
    /// (cashbox_role, cashbox, shift: «Я заплатил из ящика смены», как галочка сайта). null — как раньше (expense_variable).</param>
    /// <param name="state">2026-10-25 (ТЗ «оприходование в фоне», Q1): состояние повторов задачи очереди фоновых приходов (BackgroundReceivingQueue):
    /// свой ключ прихода, прошлые PATCH остатка, созданные товары; таймаут/авария не выпускаются исключением. null — поведение прежнее.</param>
    public async Task<PostResult> PostAsync(
        IReadOnlyList<ReceivingLineVm> lines,
        Supplier? supplier,
        bool paidNow,
        string? employee,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? paymentFields = null,
        ReceivingPostState? state = null)
    {
        var errors = new List<string>();
        var created = 0;

        // 2026-10-25 (ТЗ «оприходование в фоне», Q1): state != null — состояние повторов хранит очередь фоновых приходов на диске (свой ключ
        // прихода у каждой задачи, прошлые PATCH остатка, созданные товары), таймаут/авария не выпускаются исключением, а помечают шаг
        // «исход неизвестен». state == null — всё как раньше.
        var st = state;
        bool Soft(Exception ex) => ex is not OperationCanceledException || (st is not null && !st.IsStopping(ct));
        string Msg(Exception ex) => st is not null && IsNoAnswer(ex) ? NoAnswerText : ex.Message;

        // 1. Новые товары заводим на складе. Товар из общей базы — тем же запросом, что сайт
        //    (создаётся по его шаблону), неизвестный — обычной карточкой.
        foreach (var line in lines.Where(l => string.IsNullOrWhiteSpace(l.ProductId)))
        {
            if (st is { Interrupted: true })
                break;
            try
            {
                // 2026-10-25: прошлая попытка уже посылала создание этого товара, а ответа мы не видели — не создаём второй: ищем
                // на складе по штрихкоду; нет штрихкода — искать нечем, строка ждёт проверки кассиром.
                if (st is not null && st.WasCreateAttempted(line))
                {
                    if (string.IsNullOrWhiteSpace(line.Barcode))
                    {
                        errors.Add(Tr.T($"{line.ProductName}: товар мог быть создан при прошлой попытке — проверьте склад и добавьте строку заново, если его там нет",
                            $"{line.ProductName}: товар мурунку аракетте түзүлгөн болушу мүмкүн — кампаны текшериңиз, жок болсо сапты кайра кошуңуз",
                            $"{line.ProductName}: the product may have been created on the previous attempt — check the warehouse and add the line again if it is not there",
                            $"{line.ProductName}: ürün önceki denemede oluşturulmuş olabilir — depoyu kontrol edin, yoksa satırı yeniden ekleyin",
                            $"{line.ProductName}: mahsulot oldingi urinishda yaratilgan bo'lishi mumkin — omborni tekshiring, yo'q bo'lsa qatorni qayta qo'shing"));
                        st.SetOutcome(line, "check");
                        continue;
                    }
                    if (await Api.FindWarehouseProductByBarcodeAsync(line.Barcode, ct).ConfigureAwait(false) is { } existing
                        && Str(existing, "id") is { Length: > 0 } existingId)
                    {
                        line.ProductId = existingId;
                        st.SetCreatedPrice(line, line.SalePrice);
                        created++;
                        continue;
                    }
                }
                st?.MarkCreateAttempt(line);
                JsonElement response;
                if (line.Source == ReceivingSource.GlobalBase)
                {
                    response = await Api.CreateProductFromGlobalBarcodeAsync(line.Barcode, line.ProductName, line.SalePrice, ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    response = await Api.CreateProductAsync(new ProductEditRequest
                    {
                        Name = line.ProductName.Trim(),
                        Barcode = line.Barcode,
                        Unit = line.Unit,
                        // 2026-10-19: накладная — категория и весовой товар (у «Приёмки» пусто/false, как раньше).
                        CategoryName = line.CategoryName,
                        IsWeight = line.IsWeight,
                        Quantity = 0,
                        PurchasePrice = line.PurchasePrice,
                        Price = line.SalePrice,
                        // 2026-10-24 (Б0): наценка вместе с ценами, как карточка товара, — иначе на сервере остаётся 0, а следующая правка закупки
                        // из карточки/другой приёмки пересчитает цену продажи от неё.
                        MarkupPercent = ReceivingPricing.MarkupPercent(line.PurchasePrice, line.SalePrice),
                    }, ct).ConfigureAwait(false);
                }

                var id = Str(response, "id") ?? (response.TryGetProperty("product", out var pr) ? Str(pr, "id") : null);
                if (string.IsNullOrWhiteSpace(id))
                {
                    errors.Add(Tr.T($"{line.ProductName}: сервер не вернул номер созданного товара",
                        $"{line.ProductName}: сервер түзүлгөн товардын номерин кайтарган жок",
                        $"{line.ProductName}: the server didn't return the ID of the created product",
                        $"{line.ProductName}: sunucu oluşturulan ürünün numarasını döndürmedi",
                        $"{line.ProductName}: server yaratilgan mahsulot raqamini qaytarmadi"));
                    st?.SetOutcome(line, "unknown");
                    continue;
                }

                line.ProductId = id;
                CreatedPriceSet(st, line, line.SalePrice);
                created++;
            }
            catch (Exception ex) when (Soft(ex))
            {
                var why = Msg(ex);
                errors.Add(Tr.T($"{line.ProductName}: не удалось создать товар — {why}",
                    $"{line.ProductName}: товарды түзүү мүмкүн болгон жок — {why}",
                    $"{line.ProductName}: could not create the product — {why}",
                    $"{line.ProductName}: ürün oluşturulamadı — {why}",
                    $"{line.ProductName}: mahsulotni yaratib bo'lmadi — {why}"));
                NoteLineFailure(st, line, ex);
            }
        }

        var ready = lines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId)).ToList();
        if (ready.Count == 0)
            return new PostResult(Array.Empty<ReceivingLineVm>(), 0, created, errors);

        // 2026-10-25: приход документом — одно целое. Если создание какого-то товара не удалось по причине «исход неизвестен» (сеть, таймаут),
        // документ сейчас не отправляем — очередь повторит все создания, а затем отправит приход одним документом (второго не будет).
        if (st is not null && (st.Interrupted || (supplier is not null && lines.Any(l => string.IsNullOrWhiteSpace(l.ProductId) && IsLineOutcome(st, l, "unknown")))))
            return new PostResult(Array.Empty<ReceivingLineVm>(), 0, created, errors);

        var posted = new List<ReceivingLineVm>();
        string? receiptId = null;
        // 2026-10-20, этап П3: что вернул сервер о приходе — для записи долга (сам приход не меняется).
        double? receiptTotal = null;
        DateTime? receiptAt = null;
        string? receiptKey = null;

        if (supplier is not null)
        {
            // 2. С поставщиком — документ прихода. Остаток прибавляет сервер.
            var body = new Dictionary<string, object?>
            {
                ["items"] = ready.Select(l => new Dictionary<string, object?>
                {
                    ["product"] = l.ProductId,
                    ["qty"] = l.Quantity,
                    ["purchase_price"] = l.PurchasePrice,
                }).ToList(),
                ["payment_type"] = paidNow ? "cash" : "debt",
            };
            // Как у сайта: оплата «сразу» уходит из кассы переменных расходов.
            if (paidNow)
                body["cashbox_role"] = "expense_variable";
            // 2026-10-22 (закупки как в вебе): оплачено из ящика смены — касса и смена прихода (поля в ключ повтора тоже входят).
            if (paidNow && paymentFields is { Count: > 0 })
                foreach (var (key, value) in paymentFields)
                    body[key] = value;

            // 2026-10-15, ревью: тот же приход при повторе — тот же ключ (см. _pendingReceiptKey). Поле в теле — как в
            // остальных денежных POST NurCRM (возврат, оплата долга); сервер, не знающий поля, его просто пропускает.
            var signature = supplier.Id + "|" + paidNow + "|"
                + (paidNow && paymentFields is { Count: > 0 } ? string.Join(",", paymentFields.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + x.Value)) + "|" : "")
                + string.Join(";", ready
                .Select(l => l.ProductId + ":" + l.Quantity.ToString("0.###", CultureInfo.InvariantCulture)
                             + ":" + l.PurchasePrice.ToString("0.##", CultureInfo.InvariantCulture))
                .OrderBy(x => x, StringComparer.Ordinal));
            // 2026-10-25: с состоянием задачи ключ свой у каждого прихода и создаётся перед самой отправкой (BeginSend), а не один на приложение.
            if (st is null)
            {
                body["idempotency_key"] = _pendingReceiptKey.Get(signature);
                receiptKey = body["idempotency_key"] as string;
            }

            // 2026-10-22 (закупки как в вебе): приход пишет в товар цену закупки, а сервер у товара с наценкой (markup_percent) может
            // пересчитать от неё и цену продажи. Цены продажи товаров со склада — свежие с сервера ДО прихода (сравнивать после).
            // Повтор после обрыва: цена «до» — из первой попытки (прошлый приход мог уже дойти и сменить цену).
            // Пересчитать цену продажи сервер может только у товара с наценкой: у товара каталога с наценкой 0 цену не читаем (большая
            // накладная — без лишних запросов); товара нет в каталоге этого ПК — читаем.
            static bool MayRecalculate(ReceivingLineVm l)
            {
                try
                {
                    return CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, l.ProductId, StringComparison.OrdinalIgnoreCase)) is not { } tile || tile.MarkupPercent > 0;
                }
                catch (InvalidOperationException)
                {
                    return true; // каталог обновлялся в этот момент — проверим цену
                }
            }
            // 2026-10-25: приход уже проведён или отправлялся (исход неизвестен) — цену «до» заново не читаем: она могла измениться самим приходом.
            var fresh = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (st is null || st.ReceiptOutcome is ReceivingReceiptOutcome.NotSent or ReceivingReceiptOutcome.Rejected)
            {
                try
                {
                    fresh = await ReadSalePricesAsync(ready.Where(l => !l.IsNew && !PriceBeforeTryGet(st, l, out _) && MayRecalculate(l)).Select(l => l.ProductId!), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (st is not null && !st.IsStopping(ct))
                {
                    st.Interrupted = true;
                    errors.Add(NoAnswerText);
                    return new PostResult(Array.Empty<ReceivingLineVm>(), 0, created, errors);
                }
            }
            foreach (var l in ready.Where(l => !l.IsNew && fresh.ContainsKey(l.ProductId!)))
                PriceBeforeSet(st, l, fresh[l.ProductId!]);

            if (st is null)
            {
                try
                {
                    var response = await Api.CreateSupplierReceiptAsync(supplier.Id, body, ct).ConfigureAwait(false);
                    _pendingReceiptKey.OnSuccess();
                    receiptId = Str(response, "id");
                    if (Num(response, "total_amount") is > 0 and var serverTotal)
                        receiptTotal = serverTotal;
                    receiptAt = SupplierApiService.Date(Str(response, "created_at") ?? "");
                    posted.AddRange(ready);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _pendingReceiptKey.OnError(ex);
                    errors.Add(Tr.T("Приход от поставщика не проведён: ", "Жеткирүүчүдөн келген товар кабыл алынган жок: ",
                        "The supplier delivery was not posted: ", "Tedarikçiden mal kabulü yapılamadı: ",
                        "Yetkazib beruvchidan kirim o'tkazilmadi: ") + ex.Message);
                    return new PostResult(Array.Empty<ReceivingLineVm>(), 0, created, errors);
                }
            }
            else
            {
                // 2026-10-25: приход из очереди — сверка с сервером, потом отправка с ключом задачи (см. SendReceiptWithStateAsync).
                var sent = await SendReceiptWithStateAsync(st, supplier, body, ready, errors, ct).ConfigureAwait(false);
                if (!sent.Ok)
                    return new PostResult(Array.Empty<ReceivingLineVm>(), 0, created, errors);
                receiptId = sent.Id;
                receiptTotal = sent.Total;
                receiptAt = sent.At;
                receiptKey = st.ReceiptKey;
                posted.AddRange(ready);
            }

            // Цена продажи в документ прихода не входит — у сайта она тоже пишется в товар
            // отдельным запросом, и только если её поменяли. Так же — поправленные в приёмке
            // название и единица товара со склада.
            // 2026-10-19, ревью: у нового товара цена задана при создании — сравниваем с ней, а не с OriginalSalePrice (0), иначе
            // каждый новый товар получал лишний PATCH цены.
            bool PriceChanged(ReceivingLineVm l) => l.IsNew
                ? CreatedPriceTryGet(st, l, out var createdAt) && Math.Abs(l.SalePrice - createdAt) > 0.004
                : Math.Abs(l.SalePrice - l.OriginalSalePrice) > 0.004;
            // 2026-10-22 (закупки как в вебе): какой должна остаться цена продажи — поправленная в приёмке (явный выбор), у нового товара —
            // заданная при создании, иначе — та, что была на сервере до прихода.
            var priceTargets = new List<(ReceivingLineVm Line, double Target)>();
            foreach (var l in ready)
            {
                if (PriceChanged(l))
                    priceTargets.Add((l, l.SalePrice));
                else if (l.IsNew && CreatedPriceTryGet(st, l, out var createdPrice))
                    priceTargets.Add((l, createdPrice));
                else if (!l.IsNew && PriceBeforeTryGet(st, l, out var before))
                    priceTargets.Add((l, before));
            }
            // 2026-10-24 (Б0, ТЗ «Склад» 4.2): цена продажи и наценка уходят вместе ({price, markup_percent}, как карточка товара), чтобы закупка,
            // продажа и наценка на сервере были согласованы. Строка попадает сюда, если цену продажи поправили, закупка изменилась (цена
            // продажи остаётся прежней, наценка пересчитывается под новую закупку) или это товар из общей базы CRM (его закупка пришла только
            // документом прихода, наценки на сервере нет). Новый товар «своей» карточкой наценку получил при создании.
            bool PurchaseChanged(ReceivingLineVm l) => !l.IsNew && Math.Abs(l.PurchasePrice - l.OriginalPurchasePrice) > 0.004;
            bool NeedsPricePatch(ReceivingLineVm l) => PriceChanged(l) || PurchaseChanged(l) || l.Source == ReceivingSource.GlobalBase;
            double SaleToWrite(ReceivingLineVm l) =>
                priceTargets.FirstOrDefault(t => ReferenceEquals(t.Line, l)) is { Line: not null, Target: > 0 } t ? t.Target : l.SalePrice;
            foreach (var line in ready.Where(l => NeedsPricePatch(l) || l.NameOrUnitChanged))
            {
                if (st is { Interrupted: true })
                    break;
                try
                {
                    var fields = new Dictionary<string, object?>();
                    if (NeedsPricePatch(line))
                    {
                        var sale = SaleToWrite(line);
                        fields["price"] = sale;
                        if (ReceivingPricing.MarkupPercent(line.PurchasePrice, sale) is { } markup)
                            fields["markup_percent"] = markup;
                    }
                    AddNameAndUnit(line, fields);
                    await Api.PatchProductFieldsAsync(line.ProductId!, fields, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (Soft(ex))
                {
                    var why = Msg(ex);
                    errors.Add(Tr.T($"{line.ProductName}: товар принят, но цена продажи не обновилась — {why}",
                        $"{line.ProductName}: товар кабыл алынды, бирок сатуу баасы жаңырган жок — {why}",
                        $"{line.ProductName}: the product was received, but the selling price wasn't updated — {why}",
                        $"{line.ProductName}: ürün kabul edildi ancak satış fiyatı güncellenmedi — {why}",
                        $"{line.ProductName}: mahsulot qabul qilindi, lekin sotuv narxi yangilanmadi — {why}"));
                    if (st is not null && ex is OperationCanceledException)
                        st.Interrupted = true;
                }
            }

            // 2026-10-22 (закупки как в вебе): цена продажи после прихода не должна смениться без явного выбора — возвращаем.
            // 2026-10-25: приход в фоне — оборвалась связь на полпути: приход уже проведён, возврат цены не проверен (предупреждение, не повтор).
            if (st is { Interrupted: true })
                errors.Add(Tr.T("Приход проведён, но цены продажи после него не проверены (нет ответа сервера) — проверьте цены в карточках товаров.",
                    "Кабыл алуу өткөрүлдү, бирок андан кийинки сатуу баалары текшерилген жок (сервер жооп берген жок) — баасын товар карточкаларынан текшериңиз.",
                    "The delivery is posted, but selling prices after it were not checked (no answer from the server) — check the prices in the product cards.",
                    "Mal kabulü işlendi ancak sonraki satış fiyatları kontrol edilemedi (sunucu yanıt vermedi) — fiyatları ürün kartlarından kontrol edin.",
                    "Kirim o'tkazildi, lekin undan keyingi sotuv narxlari tekshirilmadi (server javob bermadi) — narxlarni mahsulot kartalarida tekshiring."));
            else
            {
                try
                {
                    await RestoreSalePricesAsync(priceTargets, errors, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (st is not null && !st.IsStopping(ct))
                {
                    st.Interrupted = true;
                    errors.Add(NoAnswerText);
                }
            }
        }
        else
        {
            // 3. Без поставщика — как у сайта: остаток и цены пишутся в сам товар. Остаток
            //    берётся свежий с сервера, а не из каталога кассы: за время приёмки товар могли
            //    продать на другой кассе.
            foreach (var line in ready)
            {
                if (st is { Interrupted: true })
                    break;
                try
                {
                    var current = await Api.ProductsDetailAsync(line.ProductId!, ct).ConfigureAwait(false);
                    var stock = current is { } c ? Num(c, "quantity") : line.StockBefore;

                    // 2026-10-15, ревью: прошлое «Провести» уже записало этот остаток (ответ потерялся) — не прибавляем
                    // количество второй раз, только цены/название (см. _pendingStockPatches).
                    // 2026-10-16, ревью: три исхода по свежему остатку (см. PendingStockPatch). Прибавка — всегда к остатку
                    // до первой попытки: и поправленное после таймаута количество строки не задвоится.
                    var preStock = stock;
                    if (PatchTryGet(st, line, out var pending))
                    {
                        if (current is not null && Math.Abs(stock - pending.Target) < 0.0005)
                            preStock = pending.PreStock;
                        else if (current is null || Math.Abs(stock - pending.PreStock) >= 0.0005)
                        {
                            var stockText = current is null ? "?" : stock.ToString("0.###", CultureInfo.CurrentCulture);
                            PosLogger.Log($"Приёмка: товар {line.ProductId} — прошлый PATCH остатка без ответа, остаток сейчас {stockText} (до прибавки {pending.PreStock:0.###}, записывали {pending.Target:0.###}) — количество не прибавляем, нужна проверка.", "WARNING");
                            errors.Add(Tr.T(
                                $"{line.ProductName}: прошлая попытка провести строку осталась без ответа сервера, а остаток с тех пор изменился (сейчас {stockText}, до прибавки было {pending.PreStock:0.###}, записывали {pending.Target:0.###}) — не понять, прибавилось ли количество. Повторно НЕ прибавлено, чтобы не задвоить. Проверьте остаток товара (например, ревизией): если количество прибавилось — удалите строку (✕), если нет — удалите строку и добавьте товар заново.",
                                $"{line.ProductName}: сапты өткөрүүнүн мурунку аракети сервердин жообусуз калды, ал эми калдык андан бери өзгөрдү (азыр {stockText}, кошууга чейин {pending.PreStock:0.###} болчу, {pending.Target:0.###} жазылган) — саны кошулганы белгисиз. Эки эсе болбосун деп кайра КОШУЛГАН ЖОК. Товардын калдыгын текшериңиз (мисалы, ревизия менен): саны кошулган болсо — сапты өчүрүңүз (✕), кошулбаса — сапты өчүрүп, товарды кайра кошуңуз.",
                                $"{line.ProductName}: the previous attempt to post this line got no reply from the server, and the stock has changed since then (now {stockText}, before adding it was {pending.PreStock:0.###}, {pending.Target:0.###} was written) — it is unclear whether the quantity was added. It was NOT added again to avoid doubling. Check the product stock (e.g. with a stocktake): if the quantity was added, remove the line (✕); if not, remove the line and add the product again.",
                                $"{line.ProductName}: bu satırı işlemeye yönelik önceki deneme sunucudan yanıt alamadı ve stok o zamandan beri değişti (şu an {stockText}, eklemeden önce {pending.PreStock:0.###} idi, {pending.Target:0.###} yazılmıştı) — miktarın eklenip eklenmediği belli değil. İki katına çıkmasın diye tekrar EKLENMEDİ. Ürün stoğunu kontrol edin (ör. sayımla): miktar eklendiyse satırı silin (✕), eklenmediyse satırı silip ürünü yeniden ekleyin.",
                                $"{line.ProductName}: qatorni o'tkazishning oldingi urinishi serverdan javobsiz qoldi, qoldiq esa o'shandan beri o'zgardi (hozir {stockText}, qo'shishdan oldin {pending.PreStock:0.###} edi, {pending.Target:0.###} yozilgan) — miqdor qo'shilgan-qo'shilmagani noma'lum. Ikki baravar bo'lmasligi uchun qayta QO'SHILMADI. Mahsulot qoldig'ini tekshiring (masalan, inventarizatsiya bilan): miqdor qo'shilgan bo'lsa — qatorni o'chiring (✕), qo'shilmagan bo'lsa — qatorni o'chirib, mahsulotni qayta qo'shing."));
                            st?.SetOutcome(line, "check");
                            continue;
                        }
                    }
                    var target = Math.Round(preStock + line.Quantity, 3);

                    var fields = new Dictionary<string, object?>
                    {
                        ["purchase_price"] = line.PurchasePrice,
                        ["price"] = line.SalePrice,
                    };
                    // 2026-10-24 (Б0, ТЗ «Склад» 4.2): наценка вместе с закупкой и продажей, как карточка товара.
                    if (ReceivingPricing.MarkupPercent(line.PurchasePrice, line.SalePrice) is { } markupPercent)
                        fields["markup_percent"] = markupPercent;
                    if (Math.Abs(stock - target) < 0.0005)
                        PosLogger.Log($"Приёмка: остаток товара {line.ProductId} уже {stock:0.###} — прошлая попытка дошла, количество повторно не прибавляем.", "CATALOG");
                    else
                    {
                        fields["quantity"] = target;
                        PatchSet(st, line, preStock, target);
                    }
                    AddNameAndUnit(line, fields);
                    try
                    {
                        await Api.PatchProductFieldsAsync(line.ProductId!, fields, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (fields.ContainsKey("quantity") && !PendingIdempotencyKey.IsOutcomeUnknown(ex))
                    {
                        // 2026-10-16, ревью: отказ сервера по существу (4xx, кроме 408/429) — этот остаток не записан, на
                        // сервере по-прежнему stock: без прошлой дошедшей прибавки помнить нечего, с ней — помним её.
                        if (Math.Abs(stock - preStock) < 0.0005)
                            PatchRemove(st, line);
                        else
                            PatchSet(st, line, preStock, stock);
                        throw;
                    }
                    PatchRemove(st, line);
                    posted.Add(line);
                    st?.SetOutcome(line, "posted");
                }
                catch (Exception ex) when (Soft(ex))
                {
                    var why = Msg(ex);
                    NoteLineFailure(st, line, ex);
                    // 2026-10-03, живой случай: у товара есть дубль с тем же штрихкодом — сервер на любое
                    // изменение дубля отвечает 500. Говорим кассиру, что делать, а не «внутренняя ошибка».
                    if (DuplicateOf(line) is { } dup)
                    {
                        if (ex is not OperationCanceledException)
                            st?.SetOutcome(line, "rejected");
                        errors.Add(Tr.T($"{line.ProductName}: не принят — на складе два товара со штрихкодом {line.Barcode} (второй: «{dup}»). Удалите лишний на складе и повторите.",
                            $"{line.ProductName}: кабыл алынган жок — кампада {line.Barcode} штрихкоддуу эки товар бар (экинчиси: «{dup}»). Ашыкчасын өчүрүп, кайталаңыз.",
                            $"{line.ProductName}: not received — the warehouse has two products with barcode {line.Barcode} (the other: “{dup}”). Delete the extra one and retry.",
                            $"{line.ProductName}: kabul edilmedi — depoda {line.Barcode} barkodlu iki ürün var (diğeri: «{dup}»). Fazlasını silip tekrar deneyin.",
                            $"{line.ProductName}: qabul qilinmadi — omborda {line.Barcode} shtrix-kodli ikkita mahsulot bor (ikkinchisi: «{dup}»). Ortiqchasini o'chirib, qayta urining."));
                        continue;
                    }
                    errors.Add(Tr.T($"{line.ProductName}: не принят — {why}",
                        $"{line.ProductName}: кабыл алынган жок — {why}",
                        $"{line.ProductName}: not received — {why}",
                        $"{line.ProductName}: kabul edilmedi — {why}",
                        $"{line.ProductName}: qabul qilinmadi — {why}"));
                }
            }
        }

        // 3а. 2026-10-06 (О-70): приёмка сеткой — прибавляем остатки размеров. Сервер сам выравнивает остаток товара по сумме
        //     размеров, поэтому это делается после прихода и правки товара выше (иначе их остаток перезаписал бы размеры).
        foreach (var line in posted.Where(l => l.VariantQuantities is { Count: > 0 }))
        {
            // 2026-10-25: размеры прибавляются один раз: начато в прошлой попытке (оборвалась) или связь пропала — только предупреждение.
            if (st is not null)
            {
                if (st.VariantsTouched(line, out var variantsDone))
                {
                    if (!variantsDone)
                        errors.Add(VariantsCheckText(line.ProductName));
                    continue;
                }
                if (st.Interrupted)
                {
                    errors.Add(VariantsCheckText(line.ProductName));
                    continue;
                }
                st.MarkVariants(line, false);
            }
            try
            {
                var fresh = await Api.GetProductVariantsAsync(line.ProductId!, ct).ConfigureAwait(false);
                foreach (var (variantId, qty) in line.VariantQuantities!)
                {
                    var v = fresh.FirstOrDefault(x => string.Equals(x.Id, variantId, StringComparison.OrdinalIgnoreCase));
                    if (v is null || qty <= 0)
                        continue;
                    v.Quantity = Math.Round(v.Quantity + qty, 3);
                    await Api.SaveProductVariantAsync(line.ProductId!, v, ct).ConfigureAwait(false);
                }
                ProductVariantCache.Put(line.ProductId!, await Api.GetProductVariantsAsync(line.ProductId!, ct).ConfigureAwait(false));
                PosLogger.Log($"Приёмка: размеры товара {line.ProductId} пополнены ({line.VariantSummary}).", "CATALOG");
                st?.MarkVariants(line, true);
            }
            catch (Exception ex) when (Soft(ex))
            {
                var why = Msg(ex);
                errors.Add(Tr.T($"{line.ProductName}: товар принят, но остатки размеров не обновились — {why}. Проверьте в «Размеры и цвета».",
                    $"{line.ProductName}: товар кабыл алынды, бирок өлчөмдөрдүн калдыгы жаңырган жок — {why}.",
                    $"{line.ProductName}: received, but the size stock was not updated — {why}.",
                    $"{line.ProductName}: kabul edildi ancak beden stokları güncellenmedi — {why}.",
                    $"{line.ProductName}: qabul qilindi, lekin o'lchamlar qoldig'i yangilanmadi — {why}."));
                if (st is not null && ex is OperationCanceledException)
                    st.Interrupted = true;
            }
        }

        // 4. История закупок товара — локально. С поставщиком она есть и на сервере (документ
        //    «Закупки»), но цену продажи на момент приёмки помнит только касса.
        foreach (var line in posted)
        {
            try
            {
                AppendHistory(line, supplier?.Name, employee, receiptId);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приёмка: строка истории закупок не записана: {ex.Message}", "WARNING");
            }
        }

        return new PostResult(posted, posted.Sum(l => l.LineTotal), created, errors, receiptId, receiptTotal, receiptAt, receiptKey);
    }

    /// <summary>2026-10-22 (закупки как в вебе): цены продажи товаров на сервере (GET товара, до 4 запросов сразу). Не прочитанные —
    /// не в словаре (проверки после прихода для них не будет, только журнал).</summary>
    private async Task<Dictionary<string, double>> ReadSalePricesAsync(IEnumerable<string> productIds, CancellationToken ct)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = productIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(async id =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await Api.ProductsDetailAsync(id, ct).ConfigureAwait(false) is { ValueKind: JsonValueKind.Object } c && c.TryGetProperty("price", out _))
                    lock (result)
                        result[id] = Num(c, "price");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Приход: цена продажи товара {id} до прихода не прочитана: {ex.Message}", "WARNING");
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return result;
    }

    /// <summary>2026-10-22 (закупки как в вебе): после прихода от поставщика — цена продажи каждого принятого товара на сервере
    /// (GET товара, до 4 запросов сразу). Отличается от нужной (поправленной в приёмке или прежней) — сервер пересчитал её сам
    /// (наценка от новой цены закупки): PATCH нужной цены и запись в журнал. Не прочитать — предупреждение (приход уже проведён).</summary>
    public async Task<int> RestoreSalePricesAsync(IReadOnlyList<(ReceivingLineVm Line, double Target)> targets, List<string>? errors, CancellationToken ct = default)
    {
        var restored = 0;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = targets.Where(x => !string.IsNullOrWhiteSpace(x.Line.ProductId) && x.Target > 0).Select(async x =>
        {
            var line = x.Line;
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var current = await Api.ProductsDetailAsync(line.ProductId!, ct).ConfigureAwait(false);
                if (current is not { } c || c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("price", out _))
                    return;
                var serverPrice = Num(c, "price");
                if (Math.Abs(serverPrice - x.Target) <= 0.004)
                    return;
                await Api.PatchProductFieldsAsync(line.ProductId!, new Dictionary<string, object?> { ["price"] = x.Target }, ct).ConfigureAwait(false);
                Interlocked.Increment(ref restored);
                PosLogger.Log($"Приход: у товара {line.ProductId} цена продажи после прихода {serverPrice:0.##} вместо {x.Target:0.##} (сервер пересчитал от цены закупки {line.PurchasePrice:0.##}) — возвращена {x.Target:0.##}.", "STOCK");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Приход: цена продажи товара {line.ProductId} после прихода не проверена: {ex.Message}", "WARNING");
                lock (gate)
                    errors?.Add(Tr.T($"{line.ProductName}: принят; проверьте цену продажи в карточке — сервер мог пересчитать её от цены закупки ({ex.Message}).",
                        $"{line.ProductName}: кабыл алынды; карточкадан сатуу баасын текшериңиз — сервер аны сатып алуу баасынан кайра эсептеши мүмкүн ({ex.Message}).",
                        $"{line.ProductName}: received; check the selling price in the card — the server may have recalculated it from the purchase price ({ex.Message}).",
                        $"{line.ProductName}: kabul edildi; karttaki satış fiyatını kontrol edin — sunucu onu alış fiyatından yeniden hesaplamış olabilir ({ex.Message}).",
                        $"{line.ProductName}: qabul qilindi; kartadagi sotuv narxini tekshiring — server uni xarid narxidan qayta hisoblagan bo'lishi mumkin ({ex.Message})."));
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return restored;
    }

    // ------------------------------------------------------------------ 2026-10-24 (Б0): «Обновить цены продажи» (расчёт — ReceivingPricing)

    /// <summary>Сколько строк может поправить «Обновить цены продажи (N)»: закупка подорожала, цену продажи не трогали.</summary>
    public static int CountRaisableSalePrices(IEnumerable<ReceivingLineVm> lines) => ReceivingPricing.CountRaisable(lines);

    /// <summary>Превью кнопки «Обновить цены продажи»: закупка было → стало, продажа было → станет, наценка. Ничего не меняет, на сервер не ходит.</summary>
    public static List<SalePriceUpdateRow> PreviewSalePriceUpdate(IEnumerable<ReceivingLineVm> lines, SalePriceRaiseMode mode) =>
        ReceivingPricing.PreviewRaise(lines, mode);

    /// <summary>«Применить к N строкам»: новая цена продажи — в строки приёмки (на сервер уходит только при PostAsync, вместе с markup_percent).</summary>
    public static int ApplySalePriceUpdate(IEnumerable<SalePriceUpdateRow> rows) => ReceivingPricing.ApplyRaise(rows);

    /// <summary>Перед «Провести»: товары, у которых цена продажи изменится («Цена продажи изменится у K товаров»).</summary>
    public static List<ReceivingLineVm> SalePriceChanges(IEnumerable<ReceivingLineVm> lines) => ReceivingPricing.SalePriceChanges(lines);

    /// <summary>Название и единица, поправленные в приёмке у товара со склада, — в карточку.</summary>
    private static void AddNameAndUnit(ReceivingLineVm line, Dictionary<string, object?> fields)
    {
        if (!line.NameOrUnitChanged)
            return;
        if (!string.IsNullOrWhiteSpace(line.ProductName))
            fields["name"] = line.ProductName.Trim();
        fields["unit"] = line.Unit;
    }

    // ------------------------------------------------------------------ история закупок

    private void EnsureTable(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        if (_tableReady)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ProductPurchaseHistory (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                company_id TEXT,
                product_id TEXT NOT NULL,
                product_name TEXT,
                barcode TEXT,
                quantity REAL NOT NULL,
                unit TEXT,
                purchase_price REAL NOT NULL,
                sale_price REAL,
                supplier_name TEXT,
                receipt_id TEXT,
                employee TEXT,
                at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_purchase_history_product ON ProductPurchaseHistory(product_id);
            """;
        command.ExecuteNonQuery();
        _tableReady = true;
    }

    private void AppendHistory(ReceivingLineVm line, string? supplierName, string? employee, string? receiptId)
    {
        DatabaseService.Instance.WithConnection(connection =>
        {
            EnsureTable(connection);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ProductPurchaseHistory
                    (company_id, product_id, product_name, barcode, quantity, unit, purchase_price,
                     sale_price, supplier_name, receipt_id, employee, at)
                VALUES ($company, $product, $name, $barcode, $qty, $unit, $purchase,
                        $sale, $supplier, $receipt, $employee, $at);
                """;
            command.Parameters.AddWithValue("$company", (object?)DatabaseService.Instance.CurrentCompany() ?? DBNull.Value);
            command.Parameters.AddWithValue("$product", line.ProductId);
            command.Parameters.AddWithValue("$name", line.ProductName);
            command.Parameters.AddWithValue("$barcode", line.Barcode);
            command.Parameters.AddWithValue("$qty", line.Quantity);
            command.Parameters.AddWithValue("$unit", line.Unit);
            command.Parameters.AddWithValue("$purchase", line.PurchasePrice);
            command.Parameters.AddWithValue("$sale", line.SalePrice);
            command.Parameters.AddWithValue("$supplier", (object?)supplierName ?? DBNull.Value);
            command.Parameters.AddWithValue("$receipt", (object?)receiptId ?? DBNull.Value);
            command.Parameters.AddWithValue("$employee", (object?)employee ?? DBNull.Value);
            command.Parameters.AddWithValue("$at", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        });
    }

    public List<HistoryEntry> LoadLocalHistory(string productId)
    {
        var list = new List<HistoryEntry>();
        DatabaseService.Instance.WithConnection(connection =>
        {
            EnsureTable(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT at, quantity, unit, purchase_price, sale_price, supplier_name, employee, receipt_id "
                + "FROM ProductPurchaseHistory WHERE product_id = $id"
                + DatabaseService.Instance.OwnRowsClauseFor()
                + " ORDER BY at DESC;";
            command.Parameters.AddWithValue("$id", productId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                DateTime.TryParse(reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                list.Add(new HistoryEntry(
                    at,
                    reader.GetDouble(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    Tr.T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        });
        return list;
    }

    /// <summary>История закупок товара: приёмки этой кассы плюс приходы от поставщиков с сервера
    /// (в том числе сделанные на сайте). Приход, который провела сама касса, есть в обоих
    /// местах — показываем его один раз, из кассы: там помнится ещё и цена продажи.</summary>
    public async Task<List<HistoryEntry>> LoadHistoryAsync(string productId, CancellationToken ct = default)
    {
        var local = LoadLocalHistory(productId);
        var known = local.Where(h => !string.IsNullOrWhiteSpace(h.ReceiptId)).Select(h => h.ReceiptId!).ToHashSet();
        var result = new List<HistoryEntry>(local);

        try
        {
            for (var page = 1; page <= 3; page++)
            {
                var data = await Api.ListSupplierReceiptsAsync(page, 100, ct).ConfigureAwait(false);
                if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("results", out var receipts)
                    || receipts.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var receipt in receipts.EnumerateArray())
                {
                    var receiptId = Str(receipt, "id");
                    if (receiptId is not null && known.Contains(receiptId))
                        continue;
                    if (!receipt.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                        continue;

                    DateTime.TryParse(Str(receipt, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                    foreach (var item in items.EnumerateArray())
                    {
                        if (!string.Equals(Str(item, "product_id") ?? Str(item, "product"), productId, StringComparison.OrdinalIgnoreCase))
                            continue;

                        result.Add(new HistoryEntry(
                            at,
                            Num(item, "qty"),
                            Str(item, "unit"),
                            Num(item, "purchase_price"),
                            null,
                            Str(receipt, "supplier_name"),
                            Str(receipt, "created_by_name"),
                            Tr.T("Закупки на сайте", "Сайттагы сатып алуулар", "Website purchases", "Sitedeki alımlar", "Saytdagi xaridlar"),
                            receiptId));
                    }
                }

                if (!data.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"История закупок: приходы с сервера не загружены: {ex.Message}", "WARNING");
        }

        return result.OrderByDescending(h => h.At).ToList();
    }

    /// <summary>Товар, который раньше приходовали от поставщика: последняя цена закупки и количество.</summary>
    public sealed record SupplierProduct(string ProductId, string Name, double LastPurchasePrice, double LastQuantity, DateTime LastAt);

    /// <summary>2026-10-11, тестировщик (склад.md, 2.2.1): «при выборе поставщика не отображаются товары, которые
    /// оприходовались на его имя (как на сайте)». Приходы с сервера (suppliers/receipts — и сделанные на сайте, и кассой),
    /// только этого поставщика: каждый товар один раз, с последней ценой закупки; свежие первыми.</summary>
    public async Task<List<SupplierProduct>> LoadSupplierProductsAsync(Supplier supplier, CancellationToken ct = default)
    {
        var byProduct = new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= 5; page++)
        {
            var data = await Api.ListSupplierReceiptsAsync(page, 100, ct).ConfigureAwait(false);
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("results", out var receipts)
                || receipts.ValueKind != JsonValueKind.Array)
                break;
            foreach (var receipt in receipts.EnumerateArray())
            {
                var supplierId = Str(receipt, "supplier") ?? Str(receipt, "supplier_id");
                var supplierName = Str(receipt, "supplier_name");
                var ours = (supplierId is not null && string.Equals(supplierId, supplier.Id, StringComparison.OrdinalIgnoreCase))
                           || (supplierId is null && supplierName is not null && string.Equals(supplierName.Trim(), supplier.Name.Trim(), StringComparison.CurrentCultureIgnoreCase));
                if (!ours || !receipt.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    continue;
                DateTime.TryParse(Str(receipt, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
                foreach (var item in items.EnumerateArray())
                {
                    var productId = Str(item, "product_id") ?? Str(item, "product");
                    if (productId is null || (byProduct.TryGetValue(productId, out var known) && known.LastAt >= at))
                        continue;
                    byProduct[productId] = new SupplierProduct(productId, Str(item, "product_name") ?? Str(item, "name") ?? "",
                        Num(item, "purchase_price"), Num(item, "qty"), at);
                }
            }
            if (!data.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.String)
                break;
        }
        PosLogger.Log($"Приёмка: товары поставщика «{supplier.Name}» — {byProduct.Count}.", "STOCK");
        return byProduct.Values.OrderByDescending(p => p.LastAt).ToList();
    }

    // ------------------------------------------------------------------ JSON

    private static string? Str(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    /// <summary>2026-10-03: другой товар каталога с тем же штрихкодом (дубль) — его название; null — дубля нет.</summary>
    private static string? DuplicateOf(ReceivingLineVm line)
    {
        if (string.IsNullOrWhiteSpace(line.Barcode))
            return null;
        try
        {
            return CatalogCacheService.Products
                .Where(p => !string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(p.Barcode?.Trim(), line.Barcode.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Title).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static double Num(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v)
        && JsonNumericReader.TryToDouble(v, out var d)
            ? d
            : 0;
}
