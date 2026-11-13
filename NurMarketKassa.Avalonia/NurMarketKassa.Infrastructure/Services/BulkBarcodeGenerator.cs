using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

#nullable enable

namespace NurMarketKassa.Services;

/// <summary>2026-11-12 (владелец: «а где генерация штрихкода, если их нет?»): массовая генерация штрихкодов для товаров без кода — вкладка/окно «Штрихкоды и ценники».
/// Для каждого товара свой уникальный EAN-13 (<see cref="BarcodeGenerator"/>: не с 0 и не с 2, без префикса 470; занятость — по каталогу, индексу размеров и кодам этого же прохода);
/// запись — тот же путь, что окно товара (<see cref="ProductActions.UpdateFieldsAsync"/>: PATCH только поля barcode). Перед записью карточка перечитывается с сервера: если штрихкод уже появился
/// (завёл другой человек или касса) — товар пропускается, чужой код не затирается. Весовые товары, комплекты, услуги, упаковки и товары с размерами/вариантами пропускаются с причиной.
/// Ошибка по одному товару прохода не прерывает. Пакетами по 20 (после каждого — точечное обновление каталога), не чаще <see cref="RequestsPerSecond"/> товаров в секунду, с отменой.</summary>
public static class BulkBarcodeGenerator
{
    public sealed record Created(CatalogProductTileVm Product, string Code);

    public sealed record Skipped(CatalogProductTileVm Product, string Reason);

    public sealed record Failure(CatalogProductTileVm Product, string Message);

    public sealed class RunResult
    {
        public List<Created> Created { get; } = new();
        public List<Skipped> Skipped { get; } = new();
        public List<Failure> Failed { get; } = new();
        public bool Cancelled { get; set; }
    }

    /// <summary>Размер пакета: после каждых 20 созданных каталог обновляется точечно.</summary>
    public const int BatchSize = 20;

    /// <summary>Не чаще стольких товаров в секунду (каждый — чтение карточки и запись).</summary>
    public static int RequestsPerSecond { get; set; } = 8;

    /// <summary>2026-11-13: оценка времени прохода в секундах (округление вверх) — раньше считалось в окне и ломало проверку «нет расчётов во view».</summary>
    public static int EstimateSeconds(int count) => (int)Math.Ceiling(count / (double)Math.Max(1, RequestsPerSecond));

    /// <summary>2026-11-13: то же в минутах (округление вверх) для текста «около N мин».</summary>
    public static int EstimateMinutes(int count) => (int)Math.Ceiling(EstimateSeconds(count) / 60.0);

    // ---- Подмены для проверочного стенда (в программе всегда null).
    /// <summary>Штрихкод карточки на сервере сейчас (null/пусто — нет).</summary>
    public static Func<string, CancellationToken, Task<string?>>? ReadBarcodeOverride;

    /// <summary>Записать код в карточку; вернуть текст ошибки или null.</summary>
    public static Func<string, string, CancellationToken, Task<string?>>? SaveOverride;

    /// <summary>Точечное обновление каталога по id товаров пакета.</summary>
    public static Func<IReadOnlyList<string>, Task>? RefreshOverride;

    /// <summary>Пауза между товарами (стенд — без ожидания).</summary>
    public static Func<TimeSpan, CancellationToken, Task>? DelayOverride;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Нужен ли товару штрихкод (нет ни основного, ни дополнительного).</summary>
    public static bool NeedsCode(CatalogProductTileVm p) => !BarcodePrintCatalog.HasBarcode(p);

    /// <summary>Сколько товаров из списка без штрихкода (весь список — один проход, без обращений к серверу).</summary>
    public static int CountMissing(IReadOnlyList<CatalogProductTileVm> list)
    {
        var n = 0;
        for (var i = 0; i < list.Count; i++)
            if (NeedsCode(list[i]))
                n++;
        return n;
    }

    /// <summary>Почему товару нельзя генерировать код автоматически; null — можно.</summary>
    public static string? SkipReason(CatalogProductTileVm p)
    {
        if (p.MustWeigh || p.IsWeighted || p.ServerIsWeight == true)
            return T("весовой товар (код — по PLU и этикетке весов)", "салмактуу товар (код — PLU жана таразанын этикеткасы боюнча)", "weighed product (the code comes from the PLU and the scale label)", "tartılan ürün (kod PLU ve terazi etiketinden gelir)", "tarozida tortiladigan mahsulot (kod PLU va tarozi yorlig'idan)");
        if (p.IsBundle)
            return T("комплект", "комплект", "bundle", "set", "komplekt");
        if (p.IsService)
            return T("услуга", "кызмат", "service", "hizmet", "xizmat");
        if (p.HasPieceOption)
            return T("упаковка с поштучной продажей (коды упаковки и штуки заводятся в карточке)", "даана сатылуучу таңгак (таңгактын жана дананын коддору карточкада түзүлөт)", "pack with single-piece sale (pack and piece codes are set in the card)", "adetli satışlı paket (paket ve adet kodları kartta girilir)", "donalab sotiladigan qadoq (qadoq va dona kodlari kartochkada kiritiladi)");
        if (VariantBarcodeIndex.ForProduct(p.Id).Count > 0)
            return T("товар с размерами/вариантами (у каждого размера свой код)", "өлчөмдөрү/варианттары бар товар (ар бир өлчөмдүн өз коду бар)", "product with sizes/variants (each size has its own code)", "bedenli/çeşitli ürün (her bedenin kendi kodu var)", "o'lchamli/variantli mahsulot (har bir o'lchamning o'z kodi bor)");
        return null;
    }

    /// <summary>Товары без штрихкода: годные к генерации и пропускаемые с причиной.</summary>
    public static (List<CatalogProductTileVm> Eligible, List<Skipped> Skipped) Classify(IEnumerable<CatalogProductTileVm> source)
    {
        var eligible = new List<CatalogProductTileVm>();
        var skipped = new List<Skipped>();
        foreach (var p in source)
        {
            if (!NeedsCode(p))
                continue;
            if (SkipReason(p) is { } reason)
                skipped.Add(new Skipped(p, reason));
            else
                eligible.Add(p);
        }
        return (eligible, skipped);
    }

    /// <summary>Пример кода для окна подтверждения (в карточки не записывается).</summary>
    public static string SampleCode() => BarcodeGenerator.GenerateForCatalog();

    /// <summary>Сгенерировать и сохранить коды. <paramref name="progress"/> — (сделано, всего) после каждого товара.</summary>
    /// <param name="runOnUi">как менять общие карточки каталога (из фонового потока — через поток интерфейса); null — прямо (стенд).</param>
    /// <param name="sink">если задан — результат пишется в него по ходу (при сбое остаётся частичный итог).</param>
    public static async Task<RunResult> RunAsync(IReadOnlyList<CatalogProductTileVm> items, IProgress<(int Done, int Total)>? progress, string? actor, CancellationToken ct, Action<Action>? runOnUi = null, RunResult? sink = null)
    {
        var result = sink ?? new RunResult();
        runOnUi ??= a => a();
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in CatalogCacheService.Products.ToList())
            {
                if (!string.IsNullOrWhiteSpace(p.Barcode))
                    busy.Add(p.Barcode.Trim());
                foreach (var a in p.AlternateBarcodeVariants ?? new List<AlternateBarcodeVariant>())
                    if (!string.IsNullOrWhiteSpace(a.Barcode))
                        busy.Add(a.Barcode.Trim());
            }
        }
        catch (InvalidOperationException)
        {
            // каталог обновлялся в этот момент — дубль всё равно не пройдёт проверку сервера
        }
        var delay = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, RequestsPerSecond));
        var batch = new List<string>();
        var done = 0;
        foreach (var p in items)
        {
            if (ct.IsCancellationRequested)
            {
                result.Cancelled = true;
                break;
            }
            try
            {
                // повторное чтение карточки: штрихкод уже появился — не затираем
                var current = await ReadBarcodeAsync(p.Id, ct).ConfigureAwait(false);
                if (current is null)
                {
                    // 2026-11-12 (аудит Fable, Л-7): карточка не прочиталась (500/403/нет связи) — это не «штрихкода нет»; ничего не записываем, чтобы не затереть возможный чужой код
                    result.Failed.Add(new Failure(p, T("карточка не прочиталась с сервера — код не записан", "карточка серверден окулган жок — код жазылган жок", "the card could not be read from the server — no code written", "kart sunucudan okunamadı — kod yazılmadı", "kartochka serverdan o'qilmadi — kod yozilmadi")));
                }
                else if (!string.IsNullOrWhiteSpace(current))
                {
                    var seen = current.Trim();
                    runOnUi(() => p.Barcode = seen);
                    busy.Add(seen);
                    result.Skipped.Add(new Skipped(p, T("штрихкод уже появился в карточке — не меняли", "штрих-код карточкада пайда болгон — өзгөртүлгөн жок", "a barcode has already appeared in the card — left as is", "kartta barkod zaten var — değiştirilmedi", "kartochkada shtrix-kod allaqachon paydo bo'lgan — o'zgartirilmadi")));
                }
                else
                {
                    var code = BarcodeGenerator.Generate(null, c => busy.Contains(c) || SafeVariantContains(c));
                    var error = await SaveAsync(p.Id, code, actor, ct).ConfigureAwait(false);
                    // 2026-11-12 (Л-18): код занят на сервере (товар заведён на другой кассе) — новый код и ещё до двух попыток
                    for (var retry = 0; retry < 2 && error is not null && LooksLikeDuplicate(error); retry++)
                    {
                        busy.Add(code);
                        code = BarcodeGenerator.Generate(null, c => busy.Contains(c) || SafeVariantContains(c));
                        error = await SaveAsync(p.Id, code, actor, ct).ConfigureAwait(false);
                    }
                    if (error is null)
                    {
                        busy.Add(code);
                        var saved = code;
                        runOnUi(() => p.Barcode = saved);
                        result.Created.Add(new Created(p, code));
                        batch.Add(p.Id);
                    }
                    else
                        result.Failed.Add(new Failure(p, error));
                }
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Массовая генерация штрихкодов: «{p.Title}» — {ex.Message}", "WARNING");
                result.Failed.Add(new Failure(p, ex.Message));
            }
            done++;
            progress?.Report((done, items.Count));
            if (batch.Count >= BatchSize)
            {
                await RefreshAsync(batch).ConfigureAwait(false);
                batch = new List<string>();
            }
            try
            {
                await DelayAsync(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                break;
            }
        }
        if (batch.Count > 0)
            await RefreshAsync(batch).ConfigureAwait(false);
        PosLogger.Log($"Массовая генерация штрихкодов ({actor ?? "—"}): создано {result.Created.Count}, пропущено {result.Skipped.Count}, ошибок {result.Failed.Count}{(result.Cancelled ? ", остановлено" : "")}.", "STOCK");
        return result;
    }

    private static bool LooksLikeDuplicate(string error) =>
        System.Text.RegularExpressions.Regex.IsMatch(error, "дубл|уже сущ|уже исп|уже есть|занят|already|exist|duplicate|unique", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool SafeVariantContains(string code)
    {
        try { return VariantBarcodeIndex.Contains(code); }
        catch { return false; }
    }

    private static async Task<string?> ReadBarcodeAsync(string id, CancellationToken ct)
    {
        if (ReadBarcodeOverride is { } read)
            return await read(id, ct).ConfigureAwait(false);
        var el = await PosApp.CatalogApi.ProductsDetailAsync(id, ct).ConfigureAwait(false);
        if (el is not { ValueKind: JsonValueKind.Object } obj)
            return null; // не прочиталась (ProductsDetailAsync возвращает null при ошибке/404) — вызывающий не пишет код
        return obj.TryGetProperty("barcode", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : ""; // карточка прочитана: штрихкода нет
    }

    private static async Task<string?> SaveAsync(string id, string code, string? actor, CancellationToken ct)
    {
        if (SaveOverride is { } save)
            return await save(id, code, ct).ConfigureAwait(false);
        var r = await ProductActions.UpdateFieldsAsync(id, new Dictionary<string, object?> { ["barcode"] = code },
            T("штрихкод", "штрих-код", "barcode", "barkod", "shtrix-kod"), actor, ct).ConfigureAwait(false);
        return r.Ok ? null : r.Message;
    }

    private static async Task RefreshAsync(IReadOnlyList<string> ids)
    {
        try
        {
            if (RefreshOverride is { } refresh)
                await refresh(ids).ConfigureAwait(false);
            else
                await CatalogCacheService.RefreshProductsAsync(ids).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Массовая генерация штрихкодов: каталог после пакета не обновлён ({ex.Message}).", "WARNING");
        }
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken ct) =>
        DelayOverride is { } d ? d(delay, ct) : Task.Delay(delay, ct);

    /// <summary>Журнал прохода для буфера обмена: «название — код», затем пропущенные и ошибки.</summary>
    public static string BuildLog(RunResult result)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in result.Created)
            sb.Append(c.Product.Title).Append(" — ").AppendLine(c.Code);
        foreach (var s in result.Skipped)
            sb.Append(T("Пропущено: ", "Өткөрүлдү: ", "Skipped: ", "Atlandı: ", "O'tkazib yuborildi: ")).Append(s.Product.Title).Append(" — ").AppendLine(s.Reason);
        foreach (var f in result.Failed)
            sb.Append(T("Ошибка: ", "Ката: ", "Error: ", "Hata: ", "Xato: ")).Append(f.Product.Title).Append(" — ").AppendLine(f.Message);
        return sb.ToString();
    }
}
