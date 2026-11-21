using System.Globalization;

namespace NurMarketKassa.Services.ProductMerge;

/// <summary>Исход шага неизвестен (обрыв связи после «провести» остаток): ничего не откатываем сами — запись в журнале помечается «проверить», владелец смотрит остатки.</summary>
public sealed class MergeOutcomeUnknownException(string message) : Exception(message);

/// <summary>Всё, что слиянию нужно от сервера NurCRM. Настоящая реализация — <see cref="MergeServerAdapter"/> (поверх каталога и актов остатков); в проверках — заглушка в памяти.</summary>
public interface IProductMergeServer
{
    /// <summary>Есть ли у сервера архив товаров (признак product_archive): без него источники нечем убрать из каталога — слияние не делаем.</summary>
    bool CanArchive { get; }

    /// <summary>Себестоимость считается по партиям (costing_fifo): закупку главного вручную не пишем.</summary>
    bool CostingByBatches { get; }

    /// <summary>Свежая карточка с сервера (не из кэша); null — товара нет или он в архиве.</summary>
    Task<MergeCard?> ReadCardAsync(string id, CancellationToken ct);

    /// <summary>PATCH только перечисленных полей.</summary>
    Task PatchAsync(string id, IReadOnlyDictionary<string, object?> fields, CancellationToken ct);

    /// <summary>Один акт остатков с итоговыми количествами по товарам (сервер считает разницу сам). Исход неизвестен — <see cref="MergeOutcomeUnknownException"/>.</summary>
    Task SetStocksAsync(string note, IReadOnlyList<(string Id, double Total)> targets, CancellationToken ct);

    Task ArchiveAsync(string id, CancellationToken ct);

    Task UnarchiveAsync(string id, CancellationToken ct);

    /// <summary>2026-11-21: итог пробы серверного слияния за сессию: true — POST main/products/merge/ есть; false — 404/405 (старый сервер, работает ручной путь); null — ещё не пробовали.</summary>
    bool? ServerMergeSupported => null;

    /// <summary>2026-11-21: серверное слияние (POST main/products/merge/; <paramref name="dryRun"/> — только проверка, ключ не нужен). По умолчанию сервера нет — <see cref="ServerMergeKind.Unavailable"/>.
    /// Обрыв связи/5xx — исключение (исход неизвестен: повтор с тем же ключом безопасен).</summary>
    Task<ServerMergeResponse> ServerMergeAsync(string targetId, IReadOnlyList<string> sourceIds, IReadOnlyDictionary<string, string> variantNames, bool dryRun, string? idempotencyKey, CancellationToken ct) =>
        Task.FromResult(ServerMergeResponse.Unavailable);

    /// <summary>2026-11-21: серверный откат слияния (POST main/products/merge/&lt;id&gt;/undo/, 30 суток).</summary>
    Task<ServerMergeResponse> ServerUndoAsync(string mergeId, CancellationToken ct) => Task.FromResult(ServerMergeResponse.Unavailable);
}

/// <summary>Итог выполнения или отката. <see cref="Reasons"/> — причины отказа сервера (merge_not_supported / undo_blocked), если были.</summary>
public sealed record MergeRunResult(bool Ok, string Status, string Message, MergeJournalEntry? Entry, IReadOnlyList<string>? Reasons = null);

public enum MergeCheckKind
{
    /// <summary>Сервер объединит (dry_run прошёл).</summary>
    Server,
    /// <summary>Сервер не объединит (409 merge_not_supported): причины — в <see cref="MergeCheck.Reasons"/>.</summary>
    Blocked,
    /// <summary>Сервер без слияния (404/405): ручной режим через карточки.</summary>
    Manual,
    /// <summary>Проверить не удалось (нет связи, отказ): ничего не изменено.</summary>
    Error,
}

public sealed record MergeCheck(MergeCheckKind Kind, IReadOnlyList<string> Reasons, string Message);

/// <summary>2026-11-21: выполнение слияния «через карточки» (сервер не умеет слияние сам). Порядок на каждый источник:
/// (1) освободить его штрихкоды (штрихкод уникален в компании: пока они стоят на источнике, главному их не дадут), (2) добавить их главному доп. штрихкодами (весь список целиком, с названиями
/// вариантов) и проверить по свежей карточке, (3) перенести остаток одним актом (источник → 0, главный → +остаток), (4) отправить источник в архив (история продаж остаётся на нём).
/// Любой сбой на шагах 1–3 откатывает ЭТОТ источник; уже слитые остаются слитыми и откатываются кнопкой «Откатить слияние». ПЕРЕД первой правкой пишется бэкап-файл;
/// каждый шаг пишется в журнал. Историю продаж не трогаем: чеки остаются на карточках-источниках (в архиве).</summary>
public sealed class ProductMergeRunner(IProductMergeServer server, string who, string account)
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Qty(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static object AltBody(MergeAltCode a) => new Dictionary<string, object?> { ["barcode"] = a.Barcode, ["name"] = a.Name ?? "", ["quantity"] = a.Quantity };

    private static object[] AltBody(IEnumerable<MergeAltCode> list) => list.Select(AltBody).ToArray();

    private static bool Has(IEnumerable<MergeAltCode> list, string code) => list.Any(a => string.Equals(a.Barcode?.Trim(), code, StringComparison.OrdinalIgnoreCase));

    public async Task<MergeRunResult> RunAsync(MergePlan plan, CancellationToken ct = default, Action<string>? progress = null)
    {
        if (!plan.CanRun || plan.Main is null)
            return new MergeRunResult(false, "failed", MergeTexts.Problem(plan.Problem), null);
        // 2026-11-21: без архива товаров ручной путь невозможен; серверное слияние архивирует само, но признак его поддержки известен только после пробы — пока не пробовали, ведём себя как раньше
        if (!server.CanArchive && server.ServerMergeSupported != true)
            return new MergeRunResult(false, "failed", MergeTexts.PrepareOnlyReason(), null);

        var mergeId = MergeStore.NewMergeId();
        var main = plan.Main;
        MergeCard? mainCard;
        var sourceCards = new List<(MergeVariantLine Line, MergeCard Card)>();
        try
        {
            mainCard = await server.ReadCardAsync(main.Id, ct).ConfigureAwait(false);
            foreach (var v in plan.Variants)
            {
                var c = await server.ReadCardAsync(v.Source.Id, ct).ConfigureAwait(false);
                if (c is null)
                    return new MergeRunResult(false, "failed", T($"Карточка «{v.Source.Title}» не найдена на сервере (удалена или в архиве). Ничего не изменено.", $"«{v.Source.Title}» карточкасы серверден табылган жок (өчүрүлгөн же архивде). Эч нерсе өзгөргөн жок.",
                        $"The card “{v.Source.Title}” was not found on the server (deleted or archived). Nothing changed.", $"«{v.Source.Title}» kartı sunucuda bulunamadı (silinmiş veya arşivde). Hiçbir şey değişmedi.", $"«{v.Source.Title}» kartasi serverda topilmadi (o'chirilgan yoki arxivda). Hech narsa o'zgarmadi."), null);
                sourceCards.Add((v, c));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return new MergeRunResult(false, "failed", T("Не удалось прочитать карточки с сервера. Ничего не изменено: " + ex.Message, "Карточкаларды серверден окуу мүмкүн болгон жок. Эч нерсе өзгөргөн жок: " + ex.Message,
                "Could not read the cards from the server. Nothing changed: " + ex.Message, "Kartlar sunucudan okunamadı. Hiçbir şey değişmedi: " + ex.Message, "Kartalarni serverdan o'qib bo'lmadi. Hech narsa o'zgarmadi: " + ex.Message), null);
        }
        if (mainCard is null)
            return new MergeRunResult(false, "failed", T("Главный товар не найден на сервере. Ничего не изменено.", "Башкы товар серверден табылган жок. Эч нерсе өзгөргөн жок.", "The main product was not found on the server. Nothing changed.",
                "Ana ürün sunucuda bulunamadı. Hiçbir şey değişmedi.", "Asosiy mahsulot serverda topilmadi. Hech narsa o'zgarmadi."), null);

        // ── 2026-11-21: серверное слияние — основной путь. dry_run на группу: «не объединит» (merge_not_supported) — стоп с причинами, ничего не изменено;
        // 404/405 — старый сервер, запасной ручной путь (ниже). Бэкап и журнал — как и раньше, ДО первой правки.
        var serverCheck = await CheckAsync(plan, ct).ConfigureAwait(false);
        if (serverCheck.Kind == MergeCheckKind.Blocked)
            return new MergeRunResult(false, "blocked", serverCheck.Message, null, serverCheck.Reasons);
        if (serverCheck.Kind == MergeCheckKind.Error)
            return new MergeRunResult(false, "failed", serverCheck.Message, null);
        var viaServer = serverCheck.Kind == MergeCheckKind.Server;
        if (!viaServer && !server.CanArchive)
            return new MergeRunResult(false, "failed", MergeTexts.PrepareOnlyReason(), null);

        // ── бэкап ПЕРЕД любой правкой ──
        var backup = new MergeBackup(mergeId, DateTime.UtcNow, account, who, main.Id, plan.NewMainTitle, [mainCard, .. sourceCards.Select(s => s.Card)],
            plan.Variants.ToDictionary(v => v.Source.Id, v => v.Name, StringComparer.Ordinal));
        try
        {
            MergeStore.SaveBackup(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            PosLogger.Log($"Слияние товаров: бэкап не записан ({ex.Message}) — слияние не начато.", "ERROR");
            return new MergeRunResult(false, "failed", T("Не удалось сохранить бэкап исходных карточек — слияние не начато, ничего не изменено. " + ex.Message, "Баштапкы карточкалардын резервин сактоо мүмкүн болгон жок — бириктирүү башталган жок. " + ex.Message,
                "Could not save the backup of the original cards — the merge did not start, nothing changed. " + ex.Message, "Orijinal kartların yedeği kaydedilemedi — birleştirme başlamadı, hiçbir şey değişmedi. " + ex.Message, "Asl kartalar zaxirasini saqlab bo'lmadi — birlashtirish boshlanmadi, hech narsa o'zgarmadi. " + ex.Message), null);
        }

        var entry = new MergeJournalEntry
        {
            Id = mergeId, WhenUtc = backup.CreatedUtc, Account = account, Who = who, MainId = main.Id, MainTitle = mainCard.Name, NewMainTitle = plan.NewMainTitle,
            BackupFile = MergeStore.BackupPath(mergeId), Status = "running", OldPurchase = mainCard.PurchasePrice, Mode = viaServer ? "server" : "manual",
            Sources = sourceCards.Select(s => new MergeJournalSource { Id = s.Card.Id, Title = s.Card.Name, Barcode = s.Card.Barcode, VariantName = s.Line.Name }).ToList(),
        };
        Save(entry);
        PosLogger.Log($"AUDIT: слияние товаров {mergeId} начато ({who}, {entry.Mode}): главный «{mainCard.Name}» ({main.Id}), источников {sourceCards.Count}; бэкап {entry.BackupFile}.", "INFO");
        if (viaServer)
            return await RunOnServerAsync(plan, mainCard, sourceCards, entry, progress, ct).ConfigureAwait(false);

        var mainAlt = mainCard.Alt.ToList();
        var stockMembers = new List<(double Stock, double? Purchase)> { (mainCard.Stock, mainCard.PurchasePrice) };
        var failedText = "";
        var index = 0;
        foreach (var (line, s) in sourceCards)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Invoke(T($"Объединяю {index} из {sourceCards.Count}: «{s.Name}»…", $"Бириктирүү {index}/{sourceCards.Count}: «{s.Name}»…", $"Merging {index} of {sourceCards.Count}: “{s.Name}”…", $"Birleştiriliyor {index}/{sourceCards.Count}: «{s.Name}»…", $"Birlashtirilmoqda {index}/{sourceCards.Count}: «{s.Name}»…"));
            var js = entry.Sources.First(x => x.Id == s.Id);

            // коды, которые перейдут главному (по свежей карточке источника)
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in mainAlt.Select(a => a.Barcode).Append(mainCard.Barcode ?? ""))
                if (!string.IsNullOrWhiteSpace(c))
                    known.Add(c.Trim());
            var add = new List<MergeAltCode>();
            if (!string.IsNullOrWhiteSpace(s.Barcode) && known.Add(s.Barcode.Trim()))
                add.Add(new MergeAltCode(s.Barcode.Trim(), line.Name, 0));
            foreach (var a in s.Alt)
                if (!string.IsNullOrWhiteSpace(a.Barcode) && known.Add(a.Barcode.Trim()))
                    add.Add(new MergeAltCode(a.Barcode.Trim(), string.IsNullOrWhiteSpace(a.Name) ? line.Name : a.Name, a.Quantity));

            var freed = false;
            var movedToMain = false;
            var mainAltBefore = mainAlt.ToList();
            try
            {
                // (1) освободить коды источника
                var free = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(s.Barcode))
                    free["barcode"] = "";
                if (s.Alt.Count > 0)
                    free["alternate_barcodes"] = Array.Empty<object>();
                if (free.Count > 0)
                    await server.PatchAsync(s.Id, free, ct).ConfigureAwait(false);
                freed = true;

                // (2) отдать главному весь список целиком и проверить по свежей карточке
                var newAlt = mainAlt.Concat(add).ToList();
                if (add.Count > 0)
                {
                    await server.PatchAsync(main.Id, new Dictionary<string, object?> { ["alternate_barcodes"] = AltBody(newAlt) }, ct).ConfigureAwait(false);
                    movedToMain = true;
                    var check = await server.ReadCardAsync(main.Id, ct).ConfigureAwait(false);
                    var missing = check is null ? add.Select(a => a.Barcode).ToList() : add.Where(a => !Has(check.Alt, a.Barcode)).Select(a => a.Barcode).ToList();
                    if (missing.Count > 0)
                        throw new InvalidOperationException(T("сервер не сохранил штрихкоды у главного товара: " + string.Join(", ", missing), "сервер башкы товарга штрихкоддорду сактаган жок: " + string.Join(", ", missing),
                            "the server did not save the barcodes on the main product: " + string.Join(", ", missing), "sunucu barkodları ana ürüne kaydetmedi: " + string.Join(", ", missing), "server shtrix-kodlarni asosiy mahsulotga saqlamadi: " + string.Join(", ", missing)));
                }

                // (3) остаток: источник → 0, главный → +остаток, один акт
                var srcNow = await server.ReadCardAsync(s.Id, ct).ConfigureAwait(false) ?? s;
                var mainNow = await server.ReadCardAsync(main.Id, ct).ConfigureAwait(false) ?? mainCard;
                if (srcNow.Stock > 0.0005)
                {
                    try
                    {
                        await server.SetStocksAsync(T($"Объединение товаров: «{s.Name}» → «{mainNow.Name}»", $"Товарларды бириктирүү: «{s.Name}» → «{mainNow.Name}»", $"Product merge: “{s.Name}” → “{mainNow.Name}”", $"Ürün birleştirme: «{s.Name}» → «{mainNow.Name}»", $"Mahsulotlarni birlashtirish: «{s.Name}» → «{mainNow.Name}»"),
                            [(s.Id, 0), (main.Id, Math.Round(mainNow.Stock + srcNow.Stock, 3))], ct).ConfigureAwait(false);
                    }
                    catch (MergeOutcomeUnknownException)
                    {
                        js.State = "check";
                        js.MovedStock = srcNow.Stock;
                        js.AddedCodes = add.Select(a => a.Barcode).ToList();
                        entry.Status = "check";
                        entry.Note = T($"Остаток «{s.Name}» ({Qty(srcNow.Stock)}): связь оборвалась на проведении — проверьте остатки на NurCRM, затем при необходимости нажмите «Откатить слияние».",
                            $"«{s.Name}» калдыгы ({Qty(srcNow.Stock)}): байланыш үзүлдү — NurCRM'де калдыктарды текшериңиз, керек болсо «Бириктирүүнү кайтаруу» басыңыз.",
                            $"Stock of “{s.Name}” ({Qty(srcNow.Stock)}): the connection dropped while posting — check the stock on NurCRM, then use “Roll back” if needed.",
                            $"«{s.Name}» stoku ({Qty(srcNow.Stock)}): işlem sırasında bağlantı koptu — NurCRM'de stokları kontrol edin, gerekirse «Geri al» düğmesine basın.",
                            $"«{s.Name}» qoldig'i ({Qty(srcNow.Stock)}): o'tkazish paytida aloqa uzildi — NurCRM'da qoldiqlarni tekshiring, kerak bo'lsa «Qaytarish» ni bosing.");
                        Save(entry);
                        return new MergeRunResult(false, "check", entry.Note, entry);
                    }
                    js.MovedStock = srcNow.Stock;
                    stockMembers.Add((srcNow.Stock, s.PurchasePrice));
                    mainCard = mainCard with { Stock = mainNow.Stock + srcNow.Stock };
                }
                js.AddedCodes = add.Select(a => a.Barcode).ToList();

                // (4) в архив (не откатываем: слияние по сути выполнено, источник пуст и без кодов)
                try
                {
                    await server.ArchiveAsync(s.Id, ct).ConfigureAwait(false);
                    js.Archived = true;
                }
                catch (Exception aex) when (aex is not OperationCanceledException and not OutOfMemoryException)
                {
                    js.Error = "archive: " + aex.Message;
                    PosLogger.Log($"Слияние товаров {mergeId}: «{s.Name}» слит, но в архив не отправлен ({aex.Message}).", "WARNING");
                }
                mainAlt = newAlt;
                js.State = "done";
                Save(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // вернуть ЭТОТ источник как был
                var revertNote = "";
                try
                {
                    if (movedToMain)
                        await server.PatchAsync(main.Id, new Dictionary<string, object?> { ["alternate_barcodes"] = AltBody(mainAltBefore) }, CancellationToken.None).ConfigureAwait(false);
                    if (freed)
                    {
                        var back = new Dictionary<string, object?>();
                        if (!string.IsNullOrWhiteSpace(s.Barcode))
                            back["barcode"] = s.Barcode;
                        if (s.Alt.Count > 0)
                            back["alternate_barcodes"] = AltBody(s.Alt);
                        if (back.Count > 0)
                            await server.PatchAsync(s.Id, back, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception rex) when (rex is not OutOfMemoryException)
                {
                    revertNote = " " + T($"Вернуть «{s.Name}» автоматически не вышло ({rex.Message}) — восстановите из бэкапа {entry.BackupFile}.", $"«{s.Name}» автоматтык кайтарылган жок ({rex.Message}) — {entry.BackupFile} резервинен калыбына келтириңиз.",
                        $"Could not restore “{s.Name}” automatically ({rex.Message}) — restore it from the backup {entry.BackupFile}.", $"«{s.Name}» otomatik geri alınamadı ({rex.Message}) — {entry.BackupFile} yedeğinden geri yükleyin.", $"«{s.Name}» ni avtomatik qaytarib bo'lmadi ({rex.Message}) — {entry.BackupFile} zaxirasidan tiklang.");
                    PosLogger.Log($"Слияние товаров {mergeId}: возврат «{s.Name}» не удался ({rex.Message}).", "ERROR");
                }
                js.State = "failed";
                js.Error = ex.Message;
                failedText = T($"«{s.Name}» не объединён: {ex.Message}. Этот товар оставлен как был.", $"«{s.Name}» бириктирилген жок: {ex.Message}. Бул товар мурункудай калды.",
                    $"“{s.Name}” was not merged: {ex.Message}. This product was left as it was.", $"«{s.Name}» birleştirilmedi: {ex.Message}. Bu ürün olduğu gibi bırakıldı.", $"«{s.Name}» birlashtirilmadi: {ex.Message}. Bu mahsulot avvalgidek qoldirildi.") + revertNote;
                PosLogger.Log($"Слияние товаров {mergeId}: «{s.Name}» не объединён ({ex.Message}).", "WARNING");
                Save(entry);
                break; // остальные не трогаем: владелец разберётся с причиной
            }
        }

        // ── главный: название и средневзвешенная закупка ──
        var doneCount = entry.Sources.Count(x => x.State == "done");
        if (doneCount > 0)
            await FinishMainAsync(plan, mainCard, stockMembers, entry, mergeId).ConfigureAwait(false);

        entry.Status = doneCount == entry.Sources.Count ? "done" : doneCount == 0 ? "failed" : "partial";
        Save(entry);
        PosLogger.Log($"AUDIT: слияние товаров {mergeId} завершено: {entry.Status}, слито {doneCount} из {entry.Sources.Count}.", "INFO");
        var okText = T($"Готово: объединено {doneCount} из {entry.Sources.Count}. Бэкап: {entry.BackupFile}", $"Бүттү: {doneCount}/{entry.Sources.Count} бириктирилди. Резерв: {entry.BackupFile}",
            $"Done: {doneCount} of {entry.Sources.Count} merged. Backup: {entry.BackupFile}", $"Tamam: {doneCount}/{entry.Sources.Count} birleştirildi. Yedek: {entry.BackupFile}", $"Tayyor: {doneCount}/{entry.Sources.Count} birlashtirildi. Zaxira: {entry.BackupFile}");
        return new MergeRunResult(entry.Status == "done", entry.Status, failedText.Length > 0 ? failedText + " " + okText : okText + (entry.Note is { Length: > 0 } n ? " " + n : ""), entry);
    }

    // ───────────── серверное слияние (2026-11-21) ─────────────

    /// <summary>Пауза между повторами при обрыве связи (проверки ставят ноль).</summary>
    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Сколько раз отправлять слияние с тем же ключом при неизвестном исходе (обрыв, 5xx).</summary>
    private const int ServerAttempts = 3;

    private static Dictionary<string, string> VariantNames(IEnumerable<MergeVariantLine> lines) =>
        lines.Where(l => !string.IsNullOrWhiteSpace(l.Name)).ToDictionary(l => l.Source.Id, l => l.Name, StringComparer.Ordinal);

    /// <summary>dry_run серверного слияния для группы: ничего не меняет. Окно зовёт его при выборе группы (чтобы заранее показать причины), «Объединить» — перед выполнением.</summary>
    public async Task<MergeCheck> CheckAsync(MergePlan plan, CancellationToken ct = default)
    {
        if (!plan.CanRun || plan.Main is null)
            return new MergeCheck(MergeCheckKind.Error, [], MergeTexts.Problem(plan.Problem));
        try
        {
            var r = await server.ServerMergeAsync(plan.Main.Id, plan.SourceIds.ToList(), VariantNames(plan.Variants), true, null, ct).ConfigureAwait(false);
            return r.Kind switch
            {
                ServerMergeKind.Ok => new MergeCheck(MergeCheckKind.Server, [], ""),
                ServerMergeKind.NotSupported => new MergeCheck(MergeCheckKind.Blocked, r.Reasons, MergeTexts.ServerBlocked(plan.Main.Title, r.Reasons)),
                ServerMergeKind.Unavailable => new MergeCheck(MergeCheckKind.Manual, [], ""),
                _ => new MergeCheck(MergeCheckKind.Error, [], T("Сервер не ответил на проверку слияния. Ничего не изменено.", "Сервер бириктирүүнү текшерүүгө жооп берген жок. Эч нерсе өзгөргөн жок.",
                    "The server did not answer the merge check. Nothing changed.", "Sunucu birleştirme kontrolüne yanıt vermedi. Hiçbir şey değişmedi.", "Server birlashtirishni tekshirishga javob bermadi. Hech narsa o'zgarmadi.")),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            PosLogger.Log($"Слияние товаров: проверка на сервере не удалась ({ex.Message}).", "WARNING");
            return new MergeCheck(MergeCheckKind.Error, [], T("Не удалось проверить слияние на сервере: " + ex.Message + " Ничего не изменено.", "Бириктирүүнү серверде текшерүү мүмкүн болгон жок: " + ex.Message + " Эч нерсе өзгөргөн жок.",
                "Could not check the merge on the server: " + ex.Message + " Nothing changed.", "Birleştirme sunucuda kontrol edilemedi: " + ex.Message + " Hiçbir şey değişmedi.", "Birlashtirishni serverda tekshirib bo'lmadi: " + ex.Message + " Hech narsa o'zgarmadi."));
        }
    }

    /// <summary>Само слияние на сервере: ОДИН запрос с Idempotency-Key (ключ — на одно намерение; при обрыве повтор уходит с тем же ключом, второго слияния не будет). Остаток, коды и архив сервер делает в одной транзакции.</summary>
    private async Task<MergeRunResult> RunOnServerAsync(MergePlan plan, MergeCard mainCard, List<(MergeVariantLine Line, MergeCard Card)> sourceCards, MergeJournalEntry entry, Action<string>? progress, CancellationToken ct)
    {
        var main = plan.Main!;
        var key = "merge-" + entry.Id;
        entry.IdempotencyKey = key;
        Save(entry);
        progress?.Invoke(T($"Объединяю на сервере: {sourceCards.Count} шт…", $"Серверде бириктирүү: {sourceCards.Count}…", $"Merging on the server: {sourceCards.Count}…", $"Sunucuda birleştiriliyor: {sourceCards.Count}…", $"Serverda birlashtirilmoqda: {sourceCards.Count}…"));

        ServerMergeResponse? resp = null;
        Exception? failure = null;
        var unknown = false;
        for (var attempt = 1; attempt <= ServerAttempts; attempt++)
        {
            try
            {
                resp = await server.ServerMergeAsync(main.Id, plan.SourceIds.ToList(), VariantNames(plan.Variants), false, key, ct).ConfigureAwait(false);
                failure = null;
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ex;
                unknown = ex is OperationCanceledException || PendingIdempotencyKey.IsOutcomeUnknown(ex);
                if (!unknown || ct.IsCancellationRequested || attempt == ServerAttempts)
                    break;
                PosLogger.Log($"Слияние товаров {entry.Id}: ответ сервера не получен ({ex.Message}), повтор {attempt + 1} с тем же ключом.", "WARNING");
                if (RetryDelay > TimeSpan.Zero)
                    await Task.Delay(RetryDelay, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (resp is null)
        {
            var why = failure?.Message ?? "";
            if (unknown)
            {
                foreach (var s in entry.Sources)
                    s.State = "check";
                entry.Status = "check";
                entry.Note = T($"Связь оборвалась, и неизвестно, выполнил ли сервер слияние ({why}). Проверьте товары в NurCRM: если слияние прошло, повторное «Объединить» ничего не удвоит (тот же ключ запроса {key}).",
                    $"Байланыш үзүлдү, сервер бириктирүүнү аткарганы белгисиз ({why}). NurCRM'де товарларды текшериңиз: бириктирүү өткөн болсо, кайра «Бириктирүү» эч нерсени эки эсе кылбайт (суроо ачкычы {key}).",
                    $"The connection dropped and it is unknown whether the server did the merge ({why}). Check the products in NurCRM: if it went through, pressing “Merge” again will not double anything (same request key {key}).",
                    $"Bağlantı koptu ve sunucunun birleştirmeyi yapıp yapmadığı bilinmiyor ({why}). NurCRM'de ürünleri kontrol edin: yapıldıysa tekrar «Birleştir» hiçbir şeyi ikiye katlamaz (aynı istek anahtarı {key}).",
                    $"Aloqa uzildi va server birlashtirishni bajargani noma'lum ({why}). NurCRM'da mahsulotlarni tekshiring: bajarilgan bo'lsa, qayta «Birlashtirish» hech narsani ikki baravar qilmaydi (so'rov kaliti {key}).");
            }
            else
            {
                foreach (var s in entry.Sources)
                    s.State = "failed";
                entry.Status = "failed";
                entry.Note = T($"Сервер отклонил слияние: {why}. Ничего не изменено.", $"Сервер бириктирүүнү четке какты: {why}. Эч нерсе өзгөргөн жок.", $"The server rejected the merge: {why}. Nothing changed.", $"Sunucu birleştirmeyi reddetti: {why}. Hiçbir şey değişmedi.", $"Server birlashtirishni rad etdi: {why}. Hech narsa o'zgarmadi.");
            }
            Save(entry);
            PosLogger.Log($"AUDIT: слияние товаров {entry.Id} (сервер): {entry.Status} — {why}.", unknown ? "WARNING" : "INFO");
            return new MergeRunResult(false, entry.Status, entry.Note, entry);
        }

        if (resp.Kind != ServerMergeKind.Ok)
        {
            // dry_run разрешил, а само слияние отказало (товары изменили между проверкой и запросом): сервер ничего не сделал
            foreach (var s in entry.Sources)
                s.State = "failed";
            entry.Status = "failed";
            entry.Note = resp.Kind == ServerMergeKind.NotSupported ? MergeTexts.ServerBlocked(main.Title, resp.Reasons)
                : T("Сервер не выполнил слияние. Ничего не изменено.", "Сервер бириктирүүнү аткарган жок. Эч нерсе өзгөргөн жок.", "The server did not do the merge. Nothing changed.", "Sunucu birleştirmeyi yapmadı. Hiçbir şey değişmedi.", "Server birlashtirishni bajarmadi. Hech narsa o'zgarmadi.");
            Save(entry);
            return new MergeRunResult(false, resp.Kind == ServerMergeKind.NotSupported ? "blocked" : "failed", entry.Note, entry, resp.Reasons);
        }

        entry.ServerMergeId = resp.MergeId;
        entry.ServerMovedBarcodes = resp.MovedBarcodes;
        entry.ServerMovedQty = resp.MovedQty;
        entry.ServerStockDocument = resp.StockDocument;
        var expectedQty = Math.Round(sourceCards.Sum(s => Math.Max(0, s.Card.Stock)), 3);
        foreach (var (line, card) in sourceCards)
        {
            var js = entry.Sources.First(x => x.Id == card.Id);
            js.State = "done";
            js.MovedStock = Math.Max(0, card.Stock);
            js.AddedCodes = line.CodesToAdd.Select(a => a.Barcode).ToList();
            js.Archived = resp.ArchivedIds is null || resp.ArchivedIds.Contains(card.Id, StringComparer.Ordinal);
        }
        if (string.IsNullOrWhiteSpace(resp.MergeId))
            entry.Note = T("Сервер не вернул номер слияния — откат кнопкой невозможен.", "Сервер бириктирүүнүн номерин кайтарган жок — баскыч менен кайтаруу мүмкүн эмес.", "The server returned no merge id — rollback by button is not possible.", "Sunucu birleştirme numarası vermedi — düğmeyle geri alma mümkün değil.", "Server birlashtirish raqamini qaytarmadi — tugma bilan qaytarish mumkin emas.");
        else if (Math.Abs(resp.MovedQty - expectedQty) > 0.01)
            entry.Note = T($"Сервер перенёс остаток {Qty(resp.MovedQty)}, по карточкам ожидалось {Qty(expectedQty)} — остаток мог измениться во время слияния.", $"Сервер калдыкты {Qty(resp.MovedQty)} которду, карточкалар боюнча {Qty(expectedQty)} күтүлгөн — калдык бириктирүү учурунда өзгөргөн болушу мүмкүн.",
                $"The server moved a stock of {Qty(resp.MovedQty)}; the cards promised {Qty(expectedQty)} — the stock may have changed during the merge.", $"Sunucu {Qty(resp.MovedQty)} stok taşıdı, kartlara göre {Qty(expectedQty)} bekleniyordu — stok birleştirme sırasında değişmiş olabilir.", $"Server {Qty(resp.MovedQty)} qoldiq ko'chirdi, kartalar bo'yicha {Qty(expectedQty)} kutilgan edi — qoldiq birlashtirish paytida o'zgargan bo'lishi mumkin.");

        var stockMembers = new List<(double Stock, double? Purchase)> { (mainCard.Stock, mainCard.PurchasePrice) };
        stockMembers.AddRange(sourceCards.Where(s => s.Card.Stock > 0.0005).Select(s => (s.Card.Stock, s.Card.PurchasePrice)));
        await FinishMainAsync(plan, mainCard, stockMembers, entry, entry.Id).ConfigureAwait(false);

        entry.Status = "done";
        Save(entry);
        PosLogger.Log($"AUDIT: слияние товаров {entry.Id} (сервер, merge_id {resp.MergeId}{(resp.Replayed ? ", повтор" : "")}) завершено: кодов перенесено {resp.MovedBarcodes}, остаток {Qty(resp.MovedQty)}, документ склада {resp.StockDocument}.", "INFO");
        var n = sourceCards.Count;
        var okText = T($"Готово: объединено {n} из {n} (на сервере). Бэкап: {entry.BackupFile}", $"Бүттү: {n}/{n} бириктирилди (серверде). Резерв: {entry.BackupFile}", $"Done: {n} of {n} merged (on the server). Backup: {entry.BackupFile}", $"Tamam: {n}/{n} birleştirildi (sunucuda). Yedek: {entry.BackupFile}", $"Tayyor: {n}/{n} birlashtirildi (serverda). Zaxira: {entry.BackupFile}");
        return new MergeRunResult(true, "done", okText + (entry.Note is { Length: > 0 } note ? " " + note : ""), entry);
    }

    /// <summary>Главный после слияния: новое название и средневзвешенная закупка (если сервер не считает себестоимость по партиям). Сбой записи — не откат слияния, а заметка в журнале.</summary>
    private async Task FinishMainAsync(MergePlan plan, MergeCard mainCard, List<(double Stock, double? Purchase)> stockMembers, MergeJournalEntry entry, string mergeId)
    {
        var fields = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(plan.NewMainTitle))
            fields["name"] = plan.NewMainTitle;
        var stocked = stockMembers.Where(m => m.Stock > 0.0005).ToList();
        if (!server.CostingByBatches && stocked.Count > 1 && stocked.All(m => m.Purchase is > 0))
        {
            var avg = Math.Round(stocked.Sum(m => m.Stock * m.Purchase!.Value) / stocked.Sum(m => m.Stock), 4);
            if (mainCard.PurchasePrice is not { } oldP || Math.Abs(oldP - avg) > 0.00005)
            {
                fields["purchase_price"] = avg.ToString("0.####", CultureInfo.InvariantCulture);
                entry.NewPurchase = avg;
            }
        }
        if (fields.Count == 0)
            return;
        try
        {
            await server.PatchAsync(plan.Main!.Id, fields, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            entry.NewPurchase = null;
            entry.Note = T($"Слияние выполнено, но название/закупку главного записать не удалось ({ex.Message}).", $"Бириктирүү аткарылды, бирок башкынын атын/сатып алуу баасын жазуу мүмкүн болгон жок ({ex.Message}).",
                $"The merge is done, but the main product's name/purchase price could not be saved ({ex.Message}).", $"Birleştirme yapıldı ama ana ürünün adı/alış fiyatı kaydedilemedi ({ex.Message}).", $"Birlashtirish bajarildi, lekin asosiy mahsulot nomi/xarid narxini yozib bo'lmadi ({ex.Message}).");
            PosLogger.Log($"Слияние товаров {mergeId}: главный — название/закупка не записаны ({ex.Message}).", "WARNING");
        }
    }

    private static void Save(MergeJournalEntry entry)
    {
        try
        {
            MergeStore.SaveJournal(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            PosLogger.Log($"Слияние товаров: журнал не записан ({ex.Message}).", "WARNING");
        }
    }

    // ───────────── откат ─────────────

    /// <summary>«Откатить слияние»: вернуть источники (из архива, с их кодами и остатком) и убрать их коды у главного. По бэкапу и журналу; безопасно повторять — уже возвращённые пропускаются.</summary>
    public async Task<MergeRunResult> RollbackAsync(string mergeId, CancellationToken ct = default, Action<string>? progress = null)
    {
        var entry = MergeStore.LoadJournal().FirstOrDefault(e => e.Id == mergeId);
        var backup = MergeStore.LoadBackup(mergeId);
        if (entry is null || backup is null)
            return new MergeRunResult(false, "failed", T("Запись журнала или бэкап слияния не найдены — откатить нечем.", "Бириктирүүнүн журнал жазуусу же резерви табылган жок — кайтаруу мүмкүн эмес.", "The journal record or the backup of this merge was not found — nothing to roll back.",
                "Birleştirmenin günlük kaydı veya yedeği bulunamadı — geri alınacak bir şey yok.", "Birlashtirishning jurnal yozuvi yoki zaxirasi topilmadi — qaytariladigan narsa yo'q."), null);
        var cards = backup.Cards.ToDictionary(c => c.Id, StringComparer.Ordinal);
        if (!cards.TryGetValue(entry.MainId, out var mainOrig))
            return new MergeRunResult(false, "failed", "Backup has no main card.", entry);

        // 2026-11-21: слияние выполнял сервер — откатывает тоже сервер (по номеру слияния); ручной откат по карточкам — только для слияний запасного пути
        if (entry.Mode == "server" && !string.IsNullOrWhiteSpace(entry.ServerMergeId))
            return await RollbackOnServerAsync(entry, mainOrig, ct, progress).ConfigureAwait(false);

        var problems = new List<string>();
        var index = 0;
        var todo = entry.Sources.Where(x => x.State is "done" or "check").ToList();
        if (todo.Count == 0)
            return new MergeRunResult(false, entry.Status, T("Откатывать нечего: в этом слиянии нет объединённых товаров (уже откатано или не выполнено).", "Кайтарууга эч нерсе жок: бул бириктирүүдө бириктирилген товар жок (мурун кайтарылган же аткарылган эмес).",
                "Nothing to roll back: this merge has no merged products (already rolled back or not done).", "Geri alınacak bir şey yok: bu birleştirmede birleştirilmiş ürün yok (zaten geri alındı veya yapılmadı).", "Qaytariladigan narsa yo'q: bu birlashtirishda birlashtirilgan mahsulot yo'q (allaqachon qaytarilgan yoki bajarilmagan)."), entry);
        foreach (var js in todo)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            progress?.Invoke(T($"Откатываю {index} из {todo.Count}: «{js.Title}»…", $"Кайтарылууда {index}/{todo.Count}: «{js.Title}»…", $"Rolling back {index} of {todo.Count}: “{js.Title}”…", $"Geri alınıyor {index}/{todo.Count}: «{js.Title}»…", $"Qaytarilmoqda {index}/{todo.Count}: «{js.Title}»…"));
            if (!cards.TryGetValue(js.Id, out var orig))
                continue;
            try
            {
                // 1) источник из архива
                try { await server.UnarchiveAsync(js.Id, ct).ConfigureAwait(false); }
                catch (Exception uex) when (uex is not OperationCanceledException and not OutOfMemoryException)
                {
                    PosLogger.Log($"Откат слияния {mergeId}: «{js.Title}» — из архива не вернулся ({uex.Message}); проверяю карточку.", "WARNING");
                }
                var srcNow = await server.ReadCardAsync(js.Id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(T("карточка источника не найдена (в архиве и не возвращается)", "булак карточка табылган жок (архивде жана кайтарылбайт)", "the source card was not found (archived and does not come back)", "kaynak kart bulunamadı (arşivde ve geri gelmiyor)", "manba kartasi topilmadi (arxivda va qaytmaydi)"));

                // 2) убрать коды источника у главного
                var mainNow = await server.ReadCardAsync(entry.MainId, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(T("главный товар не найден", "башкы товар табылган жок", "the main product was not found", "ana ürün bulunamadı", "asosiy mahsulot topilmadi"));
                var codes = new HashSet<string>(orig.Alt.Select(a => a.Barcode).Append(orig.Barcode ?? "").Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()), StringComparer.OrdinalIgnoreCase);
                var keep = mainNow.Alt.Where(a => !codes.Contains(a.Barcode.Trim())).ToList();
                if (keep.Count != mainNow.Alt.Count)
                    await server.PatchAsync(entry.MainId, new Dictionary<string, object?> { ["alternate_barcodes"] = AltBody(keep) }, ct).ConfigureAwait(false);

                // 3) вернуть источнику его коды
                var back = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(orig.Barcode) && !string.Equals(srcNow.Barcode?.Trim(), orig.Barcode.Trim(), StringComparison.OrdinalIgnoreCase))
                    back["barcode"] = orig.Barcode;
                if (orig.Alt.Count > 0 && srcNow.Alt.Count != orig.Alt.Count)
                    back["alternate_barcodes"] = AltBody(orig.Alt);
                if (back.Count > 0)
                    await server.PatchAsync(js.Id, back, ct).ConfigureAwait(false);

                // 4) остаток обратно (для «проверить» — только если источник реально опустел)
                var moved = js.MovedStock;
                var stockMoved = moved > 0.0005 && (js.State == "done" || srcNow.Stock < moved - 0.01);
                if (stockMoved)
                {
                    var mainAfter = Math.Max(0, mainNow.Stock - moved);
                    if (mainNow.Stock < moved - 0.0005)
                        problems.Add(T($"У главного остаток {Qty(mainNow.Stock)} меньше перенесённого {Qty(moved)} (часть уже продана) — вернули то, что есть.", $"Башкыда калдык {Qty(mainNow.Stock)} которулгандан {Qty(moved)} аз (бир бөлүгү сатылган) — бар болгонун кайтардык.",
                            $"The main has {Qty(mainNow.Stock)}, less than the {Qty(moved)} moved (part was sold) — returned what is there.", $"Ana üründe {Qty(mainNow.Stock)} var, taşınan {Qty(moved)} değerinden az (bir kısmı satıldı) — olan geri verildi.", $"Asosiyda qoldiq {Qty(mainNow.Stock)}, ko'chirilgan {Qty(moved)} dan kam (bir qismi sotilgan) — borini qaytardik."));
                    await server.SetStocksAsync(T($"Откат объединения товаров: «{js.Title}»", $"Товарларды бириктирүүнү кайтаруу: «{js.Title}»", $"Product merge rollback: “{js.Title}”", $"Ürün birleştirme geri alma: «{js.Title}»", $"Mahsulotlarni birlashtirishni qaytarish: «{js.Title}»"),
                        [(js.Id, Math.Round(srcNow.Stock + Math.Min(moved, mainNow.Stock), 3)), (entry.MainId, Math.Round(mainAfter, 3))], ct).ConfigureAwait(false);
                }
                js.State = "rolled_back";
                js.Archived = false;
                Save(entry);
            }
            catch (MergeOutcomeUnknownException ex)
            {
                problems.Add($"«{js.Title}»: {ex.Message}");
                js.State = "check";
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                problems.Add($"«{js.Title}»: {ex.Message}");
                PosLogger.Log($"Откат слияния {mergeId}: «{js.Title}» не откатан ({ex.Message}).", "WARNING");
            }
        }

        var rolled = entry.Sources.Count(x => x.State == "rolled_back");
        var allBack = entry.Sources.All(x => x.State is "rolled_back" or "failed" or "pending");
        if (allBack && rolled > 0)
        {
            var restoreProblem = await RestoreMainAsync(entry, mainOrig).ConfigureAwait(false);
            if (restoreProblem is not null)
                problems.Add(restoreProblem);
        }
        if (rolled > 0 && allBack)
        {
            entry.Status = "rolled_back";
            entry.RolledBackUtc = DateTime.UtcNow;
            entry.RolledBackBy = who;
        }
        entry.Note = problems.Count > 0 ? string.Join(" ", problems) : entry.Note;
        Save(entry);
        PosLogger.Log($"AUDIT: откат слияния {mergeId} ({who}): возвращено {rolled}, проблем {problems.Count}.", "INFO");
        var ok = problems.Count == 0 && rolled > 0;
        return new MergeRunResult(ok, entry.Status, ok
            ? T($"Слияние откатано: возвращено товаров {rolled}.", $"Бириктирүү кайтарылды: товар {rolled}.", $"Merge rolled back: {rolled} products restored.", $"Birleştirme geri alındı: {rolled} ürün döndü.", $"Birlashtirish qaytarildi: {rolled} ta mahsulot tiklandi.")
            : T($"Откат выполнен не полностью (возвращено {rolled}). ", $"Кайтаруу толук эмес (кайтарылды {rolled}). ", $"The rollback is incomplete ({rolled} restored). ", $"Geri alma eksik ({rolled} döndü). ", $"Qaytarish to'liq emas ({rolled} tiklandi). ") + string.Join(" ", problems), entry);
    }

    /// <summary>Главный: прежнее название и закупка (если слияние их меняло). null — всё вернулось; иначе текст проблемы.</summary>
    private async Task<string?> RestoreMainAsync(MergeJournalEntry entry, MergeCard mainOrig)
    {
        var restore = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(entry.NewMainTitle))
            restore["name"] = mainOrig.Name;
        if (entry.NewPurchase is not null && mainOrig.PurchasePrice is { } op)
            restore["purchase_price"] = op.ToString("0.####", CultureInfo.InvariantCulture);
        if (restore.Count == 0)
            return null;
        try
        {
            await server.PatchAsync(entry.MainId, restore, CancellationToken.None).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return T($"Название/закупку главного вернуть не удалось ({ex.Message}).", $"Башкынын атын/сатып алуу баасын кайтаруу мүмкүн болгон жок ({ex.Message}).", $"Could not restore the main product's name/purchase price ({ex.Message}).", $"Ana ürünün adı/alış fiyatı geri alınamadı ({ex.Message}).", $"Asosiy mahsulot nomi/xarid narxini qaytarib bo'lmadi ({ex.Message}).");
        }
    }

    /// <summary>2026-11-21: откат серверного слияния — POST main/products/merge/&lt;id&gt;/undo/ (возвращает коды, PLU, остаток и партии; 30 суток). 409 undo_blocked (код отдан другому товару,
    /// у главного не хватает остатка) — слияние остаётся как есть, причина показывается. Повтор после обрыва безопасен: ключ запроса — по номеру слияния.</summary>
    private async Task<MergeRunResult> RollbackOnServerAsync(MergeJournalEntry entry, MergeCard mainOrig, CancellationToken ct, Action<string>? progress)
    {
        var todo = entry.Sources.Where(x => x.State is "done" or "check").ToList();
        if (todo.Count == 0)
            return new MergeRunResult(false, entry.Status, T("Откатывать нечего: в этом слиянии нет объединённых товаров (уже откатано или не выполнено).", "Кайтарууга эч нерсе жок: бул бириктирүүдө бириктирилген товар жок (мурун кайтарылган же аткарылган эмес).",
                "Nothing to roll back: this merge has no merged products (already rolled back or not done).", "Geri alınacak bir şey yok: bu birleştirmede birleştirilmiş ürün yok (zaten geri alındı veya yapılmadı).", "Qaytariladigan narsa yo'q: bu birlashtirishda birlashtirilgan mahsulot yo'q (allaqachon qaytarilgan yoki bajarilmagan)."), entry);
        progress?.Invoke(T("Откатываю на сервере…", "Серверде кайтарууда…", "Rolling back on the server…", "Sunucuda geri alınıyor…", "Serverda qaytarilmoqda…"));
        ServerMergeResponse r;
        try
        {
            r = await server.ServerUndoAsync(entry.ServerMergeId!, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            PosLogger.Log($"Откат слияния {entry.Id} (сервер): {ex.Message}", "WARNING");
            return new MergeRunResult(false, entry.Status, T("Связь оборвалась на откате — проверьте товары в NurCRM и нажмите «Откатить» ещё раз (повтор безопасен). " + ex.Message, "Кайтарууда байланыш үзүлдү — NurCRM'де товарларды текшерип, «Кайтаруу» баскычын дагы басыңыз (кайталоо коопсуз). " + ex.Message,
                "The connection dropped during the rollback — check the products in NurCRM and press “Roll back” again (repeating is safe). " + ex.Message, "Geri alma sırasında bağlantı koptu — NurCRM'de ürünleri kontrol edip «Geri al» düğmesine tekrar basın (tekrar güvenli). " + ex.Message, "Qaytarishda aloqa uzildi — NurCRM'da mahsulotlarni tekshirib, «Qaytarish» ni yana bosing (takrorlash xavfsiz). " + ex.Message), entry);
        }
        if (r.Kind != ServerMergeKind.Ok)
        {
            var text = MergeTexts.UndoRefused(r);
            entry.Note = text;
            Save(entry);
            PosLogger.Log($"AUDIT: откат слияния {entry.Id} (сервер, {entry.ServerMergeId}) отказан: {r.Kind} {string.Join("; ", r.Reasons)}.", "INFO");
            return new MergeRunResult(false, entry.Status, text, entry, r.Reasons);
        }

        foreach (var js in todo)
        {
            js.State = "rolled_back";
            js.Archived = false;
        }
        var problems = new List<string>();
        if (await RestoreMainAsync(entry, mainOrig).ConfigureAwait(false) is { } restoreProblem)
            problems.Add(restoreProblem);
        entry.Status = "rolled_back";
        entry.RolledBackUtc = DateTime.UtcNow;
        entry.RolledBackBy = who;
        entry.Note = problems.Count > 0 ? string.Join(" ", problems) : null;
        Save(entry);
        PosLogger.Log($"AUDIT: откат слияния {entry.Id} (сервер, {entry.ServerMergeId}) ({who}): возвращено {todo.Count}.", "INFO");
        var done = T($"Слияние откатано на сервере: возвращено товаров {todo.Count}.", $"Бириктирүү серверде кайтарылды: товар {todo.Count}.", $"Merge rolled back on the server: {todo.Count} products restored.", $"Birleştirme sunucuda geri alındı: {todo.Count} ürün döndü.", $"Birlashtirish serverda qaytarildi: {todo.Count} ta mahsulot tiklandi.");
        return new MergeRunResult(problems.Count == 0, entry.Status, problems.Count == 0 ? done : done + " " + string.Join(" ", problems), entry);
    }
}
