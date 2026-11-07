using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-10-06, владелец: «проведи полный редизайн склада… и ещё к ИИ и боту дай полный доступ к товарам — количество
/// добавить, уменьшить, следить за сроками годности, количеством и, если надо, как с фото полностью наполнить информацией
/// товары, описание». Общие действия с товаром — для кнопки «±» на складе, ИИ-советника программы владельца и бота:
/// • остаток: приход, списание, точное количество — так же, как «Списание» склада: остаток перечитывается с сервера
///   (не из кеша — иначе откатились бы продажи других касс), и сервер получает документ ревизии с итоговым количеством
///   (CreateSessionAsync + ApplySessionAsync). Списание пишется в журнал списаний и в смену (по закупочной цене);
/// • поля карточки (описание, срок годности, срок хранения, минимальный остаток, цены, страна) — PATCH товара.
/// Сами ИИ и бот ничего не меняют без подтверждения владельца — подтверждение показывают они (см. ProductActionPlan).</summary>
public static class ProductActions
{
    public sealed record Result(bool Ok, string Message, bool Partial = false);

    /// <summary>Учёт остатков (документ ревизии). Задаётся приложением при запуске.</summary>
    public static IInventoryApiService? InventoryApi { get; set; }

    /// <summary>Перечитать каталог после правки карточки (задаётся приложением: SyncService.RequestCatalogSyncNow).</summary>
    public static Action? RequestCatalogRefresh { get; set; }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Товар по id, штрихкоду или названию (точно, затем единственное совпадение по части названия).</summary>
    public static CatalogProductTileVm? Find(string? idOrName)
    {
        var q = (idOrName ?? "").Trim();
        if (q.Length == 0)
            return null;
        List<CatalogProductTileVm> all;
        try
        {
            all = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        return all.FirstOrDefault(p => string.Equals(p.Id, q, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.Barcode) && string.Equals(p.Barcode, q, StringComparison.OrdinalIgnoreCase))
               ?? all.FirstOrDefault(p => string.Equals(p.Title.Trim(), q, StringComparison.CurrentCultureIgnoreCase))
               ?? (all.Where(p => p.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(2).ToList() is { Count: 1 } one ? one[0] : null);
    }

    /// <summary>2026-11-01 (приёмка Ф-3): акт ревизии, оборвавшийся на «провести»: остаток на момент создания, что вводил кассир, подтверждённый новый остаток.</summary>
    private sealed class PendingChange(string sessionId, long tick, double baseStock, double? delta, double? setTo)
    {
        public string SessionId { get; } = sessionId;
        public long Tick { get; } = tick;
        public double BaseStock { get; } = baseStock;
        public double? Delta { get; } = delta;
        public double? SetTo { get; } = setTo;
        public double? AckedCurrent { get; set; }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, PendingChange> PendingChanges = new(StringComparer.OrdinalIgnoreCase);

    public static string Qty(double v) => v.ToString("0.###", Ru);

    /// <summary>2026-11-05 (аудит №3, Т3-10): количество с запятой и единицей («2,5 кг», «3 шт.»); единица не задана — «кг» для весового, иначе «шт.».</summary>
    public static string QtyWithUnit(double v, string? unit, bool mustWeigh) =>
        Qty(v) + " " + (string.IsNullOrWhiteSpace(unit) ? (mustWeigh ? "кг" : "шт.") : unit.Trim());

    /// <summary>Изменить остаток: <paramref name="delta"/> (+ приход, − списание) или <paramref name="setTo"/> (точное количество).</summary>
    public static async Task<Result> ChangeStockAsync(string productId, double? delta, double? setTo, string reason, string? actor,
        CancellationToken ct = default, double? expectedCurrent = null, string? historyReason = null)
    {
        var product = Find(productId);
        if (product is null)
            return new Result(false, T("Товар не найден в каталоге.", "Товар каталогдон табылган жок.", "Product not found in the catalog.", "Ürün katalogda bulunamadı.", "Mahsulot katalogda topilmadi."));
        if (InventoryApi is not { } api)
            return new Result(false, T("Учёт остатков недоступен в этом режиме.", "Бул режимде калдыкты эсепке алуу жеткиликсиз.", "Stock accounting is not available in this mode.", "Stok takibi bu modda kullanılamıyor.", "Bu rejimda qoldiq hisobi mavjud emas."));

        double current;
        try
        {
            var detail = await PosApp.CatalogApi.ProductsDetailAsync(product.Id, ct).ConfigureAwait(false);
            if (detail is not { } el)
                return new Result(false, T("Сервер не вернул остаток товара — ничего не изменено.", "Сервер товардын калдыгын кайтарган жок — эч нерсе өзгөргөн жок.", "The server didn't return the stock — nothing changed.", "Sunucu stoğu döndürmedi — hiçbir şey değişmedi.", "Server qoldiqni qaytarmadi — hech narsa o'zgarmadi."));
            current = StockSyncService.ResolveStockQuantity(el, product.MustWeigh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Действие с товаром: остаток «{product.Title}» не получен ({ex.Message}).", "WARNING");
            return new Result(false, T("Сервер NurCRM недоступен — остаток не получен, ничего не изменено.", "NurCRM сервери жеткиликсиз — калдык алынган жок, эч нерсе өзгөргөн жок.", "The NurCRM server is unavailable — nothing changed.", "NurCRM sunucusu kullanılamıyor — hiçbir şey değişmedi.", "NurCRM serveri mavjud emas — hech narsa o'zgarmadi.")); // 2026-11-01 (В-08): единая фраза о сбое сервера
        }

        if (expectedCurrent is { } expected && Math.Abs(current - expected) >= 0.0005)
            return new Result(false, T($"«{product.Title}»: остаток изменился после подготовки действия ({Qty(expected)} → {Qty(current)}). Ничего не изменено — обновите данные и подтвердите заново.",
                $"«{product.Title}»: аракет даярдалгандан кийин калдык өзгөрдү ({Qty(expected)} → {Qty(current)}). Өзгөртүү болгон жок — маалыматты жаңыртып, кайра ырастаңыз.",
                $"“{product.Title}”: stock changed after this action was prepared ({Qty(expected)} → {Qty(current)}). Nothing changed — refresh and confirm again.",
                $"«{product.Title}»: işlem hazırlandıktan sonra stok değişti ({Qty(expected)} → {Qty(current)}). Değişiklik yapılmadı — yenileyip tekrar onaylayın.",
                $"«{product.Title}»: amal tayyorlangandan keyin qoldiq o'zgardi ({Qty(expected)} → {Qty(current)}). O'zgarish qilinmadi — yangilab, qayta tasdiqlang."));

        // 2026-11-01 (контрольный аудит, С2-15; приёмка Ф-3): прошлое изменение этого товара оборвалось на «провести» (тайм-аут) — акт создан, исход неизвестен.
        // Акт ревизии задаёт АБСОЛЮТНЫЙ итог, поэтому старый акт проводим, только если остаток с тех пор не менялся и кассир вводит то же самое. Остаток изменился
        // (например, продали 2 шт) — старый итог устарел: просим подтвердить пересчёт от текущего остатка и создаём новый акт. Другое число — новое действие.
        if (PendingChanges.TryGetValue(product.Id, out var pending))
        {
            if (Environment.TickCount64 - pending.Tick is < 0 or >= 600_000)
                PendingChanges.TryRemove(product.Id, out _);
            else
            {
                var sameInput = Nullable.Equals(pending.Delta, delta) && Nullable.Equals(pending.SetTo, setTo);
                InventorySessionInfo? info = null;
                try
                {
                    info = await api.GetSessionAsync(pending.SessionId, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PosLogger.Log($"Действие с товаром: состояние прошлого акта {pending.SessionId} не получено ({ex.Message}) — действие не повторяем, проверьте остаток.", "WARNING");
                    return new Result(false, T($"«{product.Title}»: прошлое изменение остатка осталось без ответа сервера, и проверить его сейчас не удалось. Проверьте остаток и повторите позже — чтобы не прибавить дважды.",
                        $"«{product.Title}»: мурунку калдык өзгөртүүгө сервер жооп берген жок, азыр текшерүү мүмкүн болгон жок. Эки жолу кошпоо үчүн калдыкты текшерип, кийинчерээк кайталаңыз.",
                        $"“{product.Title}”: the previous stock change got no answer from the server and could not be checked now. Check the stock and retry later — to avoid adding twice.",
                        $"«{product.Title}»: önceki stok değişikliği sunucudan yanıt almadı ve şu an kontrol edilemedi. İki kez eklememek için stoğu kontrol edip daha sonra tekrar deneyin.",
                        $"«{product.Title}»: oldingi qoldiq o'zgarishiga server javob bermadi va hozir tekshirib bo'lmadi. Ikki marta qo'shmaslik uchun qoldiqni tekshirib, keyinroq qaytaring."));
                }

                if (info is { IsApplied: true })
                {
                    // Прошлый акт проведён; остаток current уже его включает.
                    PendingChanges.TryRemove(product.Id, out _);
                    if (sameInput)
                    {
                        PosLogger.Log($"Действие с товаром: прошлый акт {pending.SessionId} по «{product.Title}» уже проведён — повтор не нужен.", "STOCK");
                        StockSyncService.ScheduleStockReread(product.Id, TimeSpan.Zero);
                        return new Result(true, T($"«{product.Title}»: прошлое изменение остатка уже проведено на сервере (остаток {Qty(current)}) — второй раз не добавляем.",
                            $"«{product.Title}»: мурунку калдык өзгөртүү серверде өткөрүлгөн (калдык {Qty(current)}) — экинчи жолу кошпойбуз.",
                            $"“{product.Title}”: the previous stock change is already posted on the server (stock {Qty(current)}) — not applying it again.",
                            $"«{product.Title}»: önceki stok değişikliği sunucuda zaten işlendi (stok {Qty(current)}) — tekrar uygulanmıyor.",
                            $"«{product.Title}»: oldingi qoldiq o'zgarishi serverda allaqachon o'tkazilgan (qoldiq {Qty(current)}) — qayta qo'llanmaydi."));
                    }
                }
                else if (info is not null)
                {
                    if (sameInput && Math.Abs(current - pending.BaseStock) < 0.0005)
                    {
                        // Остаток не менялся с создания акта и ввод тот же — доводим тот же акт (итог актуален).
                        await api.ApplySessionAsync(pending.SessionId, allowNegative: false, CancellationToken.None).ConfigureAwait(false);
                        PendingChanges.TryRemove(product.Id, out _);
                        StockSyncService.ScheduleStockReread(product.Id, TimeSpan.Zero);
                        return new Result(true, T($"«{product.Title}»: прошлый акт изменения остатка доведён до конца. Остаток обновится.",
                            $"«{product.Title}»: мурунку калдык өзгөртүү актысы аякталды. Калдык жаңыланат.",
                            $"“{product.Title}”: the previous stock change document is now completed. The stock will refresh.",
                            $"«{product.Title}»: önceki stok değişikliği belgesi tamamlandı. Stok yenilenecek.",
                            $"«{product.Title}»: oldingi qoldiq o'zgarishi hujjati yakunlandi. Qoldiq yangilanadi."));
                    }
                    if (sameInput && (pending.AckedCurrent is not { } acked || Math.Abs(acked - current) >= 0.0005))
                    {
                        // Остаток изменился (продажа, приход на другой кассе): старый итог неверен. Показываем новый итог и просим подтвердить.
                        pending.AckedCurrent = current;
                        var newTarget = Math.Round(setTo ?? current + (delta ?? 0), 3);
                        return new Result(false, T($"«{product.Title}»: остаток изменился (было {Qty(pending.BaseStock)}, стало {Qty(current)}). Применить ваше изменение к текущему остатку — итог будет {Qty(newTarget)}? Нажмите ещё раз, чтобы подтвердить.",
                            $"«{product.Title}»: калдык өзгөрдү (болчу {Qty(pending.BaseStock)}, болду {Qty(current)}). Өзгөртүүнү азыркы калдыкка колдонобузбу — жыйынтык {Qty(newTarget)} болот? Ырастоо үчүн дагы бир жолу басыңыз.",
                            $"“{product.Title}”: the stock changed (was {Qty(pending.BaseStock)}, now {Qty(current)}). Apply your change to the current stock — the result will be {Qty(newTarget)}? Press again to confirm.",
                            $"«{product.Title}»: stok değişti (önceden {Qty(pending.BaseStock)}, şimdi {Qty(current)}). Değişikliğiniz mevcut stoğa uygulansın mı — sonuç {Qty(newTarget)} olacak? Onaylamak için tekrar basın.",
                            $"«{product.Title}»: qoldiq o'zgardi (avval {Qty(pending.BaseStock)}, hozir {Qty(current)}). O'zgarishingiz joriy qoldiqqa qo'llansinmi — natija {Qty(newTarget)} bo'ladi? Tasdiqlash uchun yana bosing."));
                    }
                    // Подтверждено или другой ввод: старый непроведённый акт брошен (его итог устарел) — номер в журнал, дальше новый акт от текущего остатка.
                    PosLogger.Log($"Действие с товаром: прошлый акт {pending.SessionId} по «{product.Title}» не проведён и устарел (остаток {Qty(pending.BaseStock)} → {Qty(current)}) — создаём новый от текущего остатка; старый черновик можно удалить на NurCRM.", "WARNING");
                    PendingChanges.TryRemove(product.Id, out _);
                }
                else
                {
                    PendingChanges.TryRemove(product.Id, out _);
                }
            }
        }

        var target = Math.Round(setTo ?? current + (delta ?? 0), 3);
        // 2026-10-31: сервер принимает остаток с 2 знаками (quantity_fact "0.##") — округляем явно, пояснение добавим к ответу; операцию не блокируем.
        string? roundNote = null;
        if (target >= 0 && InventoryApiService.RoundingNote(target) is { } rn)
        {
            roundNote = rn;
            target = InventoryApiService.RoundForServer(target);
        }
        if (target < 0)
            return new Result(false, T($"«{product.Title}»: списать больше остатка нельзя (есть {Qty(current)}).", $"«{product.Title}»: калдыктан көп чыгарууга болбойт (бар {Qty(current)}).",
                $"“{product.Title}”: can't write off more than the stock ({Qty(current)}).", $"«{product.Title}»: stoktan fazla düşülemez (mevcut {Qty(current)}).", $"«{product.Title}»: qoldiqdan ko'p chiqarib bo'lmaydi (bor {Qty(current)})."));
        if (Math.Abs(target - current) < 0.0005)
            return new Result(true, T($"«{product.Title}»: остаток уже {Qty(current)}.", $"«{product.Title}»: калдык мурунтан {Qty(current)}.", $"“{product.Title}”: stock is already {Qty(current)}.", $"«{product.Title}»: stok zaten {Qty(current)}.", $"«{product.Title}»: qoldiq allaqachon {Qty(current)}."));

        string? sessionId = null;
        try
        {
            var note = string.IsNullOrWhiteSpace(reason) ? "Изменение остатка" : reason.Trim();
            // 2026-10-16: у сервера есть только документ ревизии с итоговым количеством (прихода/списания «на разницу» нет). Вчерашний
            // цикл (2026-10-15) создавал черновик и лишь потом перечитывал остаток: изменился — черновик бросался непроведённым, и на
            // NurCRM копились «висящие» ревизии со старым абсолютным итогом (до 3 на строку) — проведи такую кто-то с сайта, остаток
            // откатился бы. Удалить/отменить черновик API не умеет (IInventoryApiService: только create и apply), поэтому теперь:
            // свежий остаток читается с сервера прямо перед созданием (выше, без кеша), итог считается от него, черновик создаётся и
            // сразу проводится — один документ на действие, брошенных нет. Остаётся узкое окно: продажа другой кассы между чтением
            // остатка и «провести» (два запроса) будет перетёрта итогом документа; закрыть его можно только серверным
            // приходом/списанием «на разницу».
            sessionId = await api.CreateSessionAsync(note, new[] { new InventorySessionItem(product.Id, target) }, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(sessionId))
                PendingChanges[product.Id] = new PendingChange(sessionId, Environment.TickCount64, current, delta, setTo); // 2026-11-01 (С2-15): снимается после успешного «провести»
            if (string.IsNullOrWhiteSpace(sessionId))
                return new Result(false, T("Сервер не создал документ — ничего не изменено.", "Сервер документ түзгөн жок — эч нерсе өзгөргөн жок.", "The server didn't create the document — nothing changed.", "Sunucu belgeyi oluşturmadı — hiçbir şey değişmedi.", "Server hujjat yaratmadi — hech narsa o'zgarmadi."));
            // 2026-10-16: черновик уже на сервере — проводим без отмены (CancellationToken.None): отмена между «создать» и «провести»
            // оставила бы на NurCRM непроведённую ревизию со старым итогом.
            await api.ApplySessionAsync(sessionId, allowNegative: false, CancellationToken.None).ConfigureAwait(false);
            PendingChanges.TryRemove(product.Id, out _);
        }
        catch (OperationCanceledException) when (!string.IsNullOrWhiteSpace(sessionId))
        {
            // 2026-10-16: тайм-аут «провести» — номер черновика в журнал (как ниже), ошибка идёт дальше, как раньше.
            PosLogger.Log($"Действие с товаром: «{product.Title}» — тайм-аут проведения, черновик ревизии {sessionId} мог остаться непроведённым — проверьте его на NurCRM.", "WARNING");
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 2026-10-16: не провёлся созданный черновик — его номер в журнал: удалить его можно только на NurCRM (API удаления нет).
            // 2026-11-02 (аудит №2, С3-03): исход неизвестен (обрыв сети, 502/503/504, сброс соединения) — акт мог пройти на сервере: запись НЕ снимаем, как при тайм-ауте,
            // повтор сначала проверит прошлый акт. Запись снимается только при определённом отказе сервера (4xx по существу).
            var outcomeUnknown = !string.IsNullOrWhiteSpace(sessionId) && PendingIdempotencyKey.IsOutcomeUnknown(ex);
            if (!outcomeUnknown)
                PendingChanges.TryRemove(product.Id, out _); // определённый отказ — повтор создаст новый акт
            PosLogger.Log($"Действие с товаром: остаток «{product.Title}» не изменён ({ex.Message})"
                          + (string.IsNullOrWhiteSpace(sessionId) ? "." : $"; черновик ревизии {sessionId} мог остаться непроведённым — проверьте его на NurCRM."), "WARNING");
            if (outcomeUnknown)
                return new Result(false, T($"«{product.Title}»: связь с сервером прервалась — неизвестно, прошло ли изменение остатка. Нажмите ещё раз: касса сначала проверит прошлое изменение и не добавит остаток дважды.",
                    $"«{product.Title}»: сервер менен байланыш үзүлдү — калдык өзгөртүү өттүбү, белгисиз. Дагы бир жолу басыңыз: касса адегенде мурунку өзгөртүүнү текшерет жана калдыкты эки жолу кошпойт.",
                    $"“{product.Title}”: the connection to the server was lost — it is unknown whether the stock change went through. Press again: the till will first check the previous change and will not add the stock twice.",
                    $"«{product.Title}»: sunucu bağlantısı koptu — stok değişikliğinin yapılıp yapılmadığı bilinmiyor. Tekrar basın: kasa önce önceki değişikliği kontrol eder ve stoğu iki kez eklemez.",
                    $"«{product.Title}»: server bilan aloqa uzildi — qoldiq o'zgarishi o'tgani noma'lum. Yana bosing: kassa avval oldingi o'zgarishni tekshiradi va qoldiqni ikki marta qo'shmaydi."));
            return new Result(false, T("Не удалось изменить остаток: ", "Калдыкты өзгөртүү мүмкүн болгон жок: ", "Couldn't change the stock: ", "Stok değiştirilemedi: ", "Qoldiqni o'zgartirib bo'lmadi: ") + ex.Message);
        }

        StockSyncService.ApplyQuantityToTileOnUi(product, target, product.MustWeigh);
        CatalogCacheService.PersistProductStock(product.Id, target, product.MustWeigh);
        var diff = target - current;
        if (diff < 0)
        {
            // Как «Списание» склада: журнал списаний и строка «Списания» смены — по закупочной цене.
            try
            {
                var unitCost = product.PurchasePrice > 0 ? product.PurchasePrice : LocalCartService.ParsePrice(product.PriceLine);
                WriteOffHistoryStore.Append(product.Id, product.Title, -diff, historyReason ?? reason, actor ?? PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId, unitCost > 0 ? unitCost : null);
                if (unitCost > 0)
                    ShiftEventsStore.Record(ShiftEventsStore.KindWriteOff, PosApp.ActiveShiftId, ShiftEventsStore.OperationKey(product.Id + ":" + DateTime.UtcNow.Ticks), -diff * unitCost, product.Title);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Действие с товаром: журнал списаний не записан ({ex.Message}).", "WARNING");
            }
        }
        PosLogger.Log($"Действие с товаром ({actor ?? "—"}): «{product.Title}» {Qty(current)} → {Qty(target)} ({reason}).", "STOCK");
        return new Result(true, T($"«{product.Title}»: было {Qty(current)}, стало {Qty(target)}.", $"«{product.Title}»: {Qty(current)} болчу, {Qty(target)} болду.",
            $"“{product.Title}”: was {Qty(current)}, now {Qty(target)}.", $"«{product.Title}»: {Qty(current)} idi, şimdi {Qty(target)}.", $"«{product.Title}»: {Qty(current)} edi, endi {Qty(target)}.")
            + (roundNote is null ? "" : " " + roundNote));
    }

    /// <summary>Поля карточки товара (имена — как у сервера: description, expiration_date, shelf_life_days, minimum_quantity,
    /// price, purchase_price, country). <paramref name="what"/> — что меняем, словами для ответа.</summary>
    public static async Task<Result> UpdateFieldsAsync(string productId, IReadOnlyDictionary<string, object?> fields, string what, string? actor, CancellationToken ct = default)
    {
        var product = Find(productId);
        if (product is null)
            return new Result(false, T("Товар не найден в каталоге.", "Товар каталогдон табылган жок.", "Product not found in the catalog.", "Ürün katalogda bulunamadı.", "Mahsulot katalogda topilmadi."));
        if (fields.Count == 0)
            return new Result(false, T("Нечего менять.", "Өзгөртө турган эч нерсе жок.", "Nothing to change.", "Değiştirilecek bir şey yok.", "O'zgartiradigan narsa yo'q."));
        try
        {
            await PosApp.CatalogApi.PatchProductFieldsAsync(product.Id, fields, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Действие с товаром: «{product.Title}» — {what} не сохранено ({ex.Message}).", "WARNING");
            return new Result(false, T("Сервер не сохранил: ", "Сервер сактаган жок: ", "The server didn't save it: ", "Sunucu kaydetmedi: ", "Server saqlamadi: ") + ex.Message);
        }
        RequestCatalogRefresh?.Invoke();
        // 2026-10-27 (цена склад-касса): RequestCatalogRefresh идёт через проверку «версии» каталога, а она не замечает смену цены —
        // при изменении цены каталог перечитываем полностью (как «Склад» после сохранения карточки).
        // 2026-11-07 («при каждом изменении товара весь склад загружается заново»): вместо полной загрузки каталога — карточка только этого товара (точечное обновление).
        if (fields.ContainsKey("price") || fields.ContainsKey("purchase_price"))
        {
            var changedId = product.Id;
            _ = Task.Run(async () =>
            {
                try { await CatalogCacheService.RefreshProductsAsync([changedId]).ConfigureAwait(false); }
                catch (Exception ex) { PosLogger.Log($"Каталог после смены цены не обновлён: {ex.Message}", "WARNING"); }
            });
        }
        PosLogger.Log($"Действие с товаром ({actor ?? "—"}): «{product.Title}» — {what} ({string.Join(", ", fields.Keys)}).", "STOCK");
        return new Result(true, T($"«{product.Title}»: {what} — сохранено.", $"«{product.Title}»: {what} — сакталды.", $"“{product.Title}”: {what} — saved.",
            $"«{product.Title}»: {what} — kaydedildi.", $"«{product.Title}»: {what} — saqlandi."));
    }

    /// <summary>Карточка товара для ИИ: всё, что известно (id нужен ИИ, чтобы точно указать товар в действии).</summary>
    public static async Task<string> DescribeForAiAsync(CatalogProductTileVm p, CancellationToken ct = default)
    {
        var line = $"• id={p.Id} | {p.Title} | цена {p.PriceLine} | {(AdvisorCapabilities.HidePurchase ? "" : $"закупка {p.PurchasePrice:0.####} | ")}остаток {Qty(p.Quantity)} {(p.MustWeigh ? "кг" : "шт")}"
                   + (string.IsNullOrWhiteSpace(p.Barcode) ? "" : $" | штрихкод {p.Barcode}")
                   + (string.IsNullOrWhiteSpace(p.Category) ? "" : $" | категория {p.Category}")
                   + (string.IsNullOrWhiteSpace(p.Brand) ? "" : $" | бренд {p.Brand}");
        try
        {
            if (await PosApp.CatalogApi.ProductsDetailAsync(p.Id, ct).ConfigureAwait(false) is { } el && el.ValueKind == JsonValueKind.Object)
            {
                string? S(string name) => el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
                if (S("expiration_date") is { Length: > 0 } exp)
                    line += $" | срок годности до {exp}";
                if (S("shelf_life_days") is { Length: > 0 } shelf && shelf != "0")
                    line += $" | срок хранения {shelf} дн.";
                if (S("minimum_quantity") is { Length: > 0 } min && double.TryParse(min, NumberStyles.Any, CultureInfo.InvariantCulture, out var m) && m > 0)
                    line += $" | минимальный остаток {Qty(m)}";
                if (S("country") is { Length: > 0 } country)
                    line += $" | страна {country}";
                var description = S("description") ?? "";
                line += description.Length > 0 ? $" | описание: {(description.Length > 200 ? description[..200] + "…" : description)}" : " | описания нет";
                line += el.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0 ? " | фото есть" : " | фото нет";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Без подробностей сервера — то, что есть в каталоге.
        }
        return line;
    }
}
