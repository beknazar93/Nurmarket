using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

// 2026-11-10 (ТЗ владельца «NurCRM Market — редактирование и удаление возвратных накладных в течение 24 часов»): общий клиентский механизм для обоих
// возвратных документов — «Возврат от клиента» (api/main/pos/returns/) и «Возврат поставщику» (api/main/suppliers/returns/).
// Контракт ПРЕДЛОЖЕН НАМИ: docs/Отчеты/«ТЗ Умару — правка и аннулирование возвратных накладных 24 ч.md».
// 2026-11-11: сервер (Умар, боевой app.nurcrm.kg) реализовал — сверено с РЕАЛЬНЫМ контрактом: тела PATCH (pos: items[{sale_item_id, quantity}];
// supplier: items[{id, qty, purchase_price}], без скидки/примечания строки), edit_block_reason (legacy_return_not_supported, debt_sale_not_supported,
// agent_sale_not_supported, has_later_documents, shift_closed, edit_window_expired, already_voided), коды 428/403/412/400/409, списки без аннулированных
// по умолчанию (?status=all — со всеми).
// Всё включается только признаком сервера (supplier_return_edit_24h / pos_return_edit_24h); без него кнопок нет, касса ведёт себя как раньше.
// Решения по сроку 24 ч принимает ТОЛЬКО сервер (can_edit / can_void, повторная проверка при сохранении); часы устройства — лишь подсказка «осталось N ч».
// PIN руководителя проверяет сервер (POST users/auth/pin/verify/ с областью действия → одноразовый токен X-Confirm-Token); PIN нигде не хранится и не пишется в журнал.

/// <summary>Какой возвратный документ.</summary>
public enum ReturnDocKind
{
    /// <summary>Возврат поставщику (api/main/suppliers/returns/).</summary>
    Supplier,

    /// <summary>Возврат от клиента (api/main/pos/returns/).</summary>
    Customer,
}

/// <summary>Что подтверждает PIN руководителя.</summary>
public enum ReturnEditAction
{
    Edit,
    Void,
}

public static class ReturnDocKinds
{
    /// <summary>object_type в запросе подтверждения PIN.</summary>
    public static string ObjectType(ReturnDocKind kind) => kind == ReturnDocKind.Supplier ? "supplier_return" : "pos_return";

    /// <summary>Адрес документа (с «/» в конце).</summary>
    public static string DocPath(ReturnDocKind kind, string id) =>
        (kind == ReturnDocKind.Supplier ? "api/main/suppliers/returns/" : "api/main/pos/returns/") + Uri.EscapeDataString(id.Trim()) + "/";

    /// <summary>Признак сервера в api/main/capabilities/.</summary>
    public static string Capability(ReturnDocKind kind) => kind == ReturnDocKind.Supplier ? ServerCapabilities.SupplierReturnEdit24h : ServerCapabilities.PosReturnEdit24h;

    public static string Scope(ReturnEditAction action) => action == ReturnEditAction.Edit ? "return_edit" : "return_void";
}

/// <summary>Можно ли править/аннулировать документ — ПО ОТВЕТУ СЕРВЕРА (поля version, status, can_edit, can_void, closed_at, editable_until, server_now).
/// Если сервер полей не прислал — <see cref="HasServerFlags"/> = false и все кнопки скрыты.</summary>
public sealed record ReturnEditState(
    int Version, bool IsVoided, bool CanEdit, bool CanVoid, bool HasServerFlags,
    DateTimeOffset? ClosedAt, DateTimeOffset? EditableUntil, DateTimeOffset? ServerNow, string BlockReason,
    DateTimeOffset? VoidedAt, string VoidedBy, string VoidReason, string VoidApprovedBy, int EditCount, DateTime ReceivedUtc)
{
    public static readonly ReturnEditState None = new(0, false, false, false, false, null, null, null, "", null, "", "", "", 0, DateTime.MinValue);

    /// <summary>Разбор полей редактируемости из строки списка или карточки возврата. <paramref name="receivedUtc"/> — когда пришёл ответ (для подсказки «осталось N ч»).</summary>
    public static ReturnEditState Parse(JsonElement x, DateTime? receivedUtc = null)
    {
        if (x.ValueKind != JsonValueKind.Object)
            return None;
        var status = Str(x, "status").ToLowerInvariant();
        var voided = Bool(x, "is_voided") == true || status is "voided" or "annulled";
        var canEdit = Bool(x, "can_edit");
        var canVoid = Bool(x, "can_void");
        return new ReturnEditState(
            (int)Num(x, "version"), voided, canEdit == true && !voided, canVoid == true && !voided, canEdit is not null || canVoid is not null,
            Date(x, "closed_at") ?? Date(x, "posted_at"), Date(x, "editable_until"), Date(x, "server_now"), Str(x, "edit_block_reason"),
            // 2026-11-11: поля voided_* реального сервера (voided_reason / voided_by) — плюс прежние имена из нашего ТЗ
            Date(x, "voided_at"), First(x, "voided_by_name", "voided_by"), First(x, "void_reason", "voided_reason"), First(x, "void_approved_by_name", "voided_approved_by_name"), (int)Num(x, "edit_count"),
            receivedUtc ?? DateTime.UtcNow);
    }

    /// <summary>Сколько осталось до конца срока правки — ТОЛЬКО ДЛЯ ПОДСКАЗКИ: editable_until − server_now из того же ответа минус время, прошедшее с ответа.
    /// Часы устройства в расчёт не входят (только разница «сейчас − когда пришёл ответ»). null — сервер не дал обоих времён.</summary>
    public TimeSpan? TimeLeft(DateTime? utcNow = null)
    {
        if (EditableUntil is not { } until || ServerNow is not { } now || ReceivedUtc == DateTime.MinValue)
            return null;
        var elapsed = (utcNow ?? DateTime.UtcNow) - ReceivedUtc;
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;
        var left = until - now - elapsed;
        return left < TimeSpan.Zero ? TimeSpan.Zero : left;
    }

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

    private static string First(JsonElement e, params string[] names) => names.Select(n => Str(e, n)).FirstOrDefault(v => v.Length > 0) ?? "";

    internal static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    internal static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    internal static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { Length: > 0 } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>Показывать ли кнопки: признак сервера для этого типа И разрешение сервера в самом документе.</summary>
public static class ReturnEditGate
{
    public static bool FeatureOn(ReturnDocKind kind) => ServerCapabilities.Has(ReturnDocKinds.Capability(kind));

    public static bool ShowEdit(ReturnDocKind kind, ReturnEditState state) =>
        FeatureOn(kind) && state.HasServerFlags && state.CanEdit && !state.IsVoided && state.Version > 0;

    public static bool ShowVoid(ReturnDocKind kind, ReturnEditState state) =>
        FeatureOn(kind) && state.HasServerFlags && state.CanVoid && !state.IsVoided && state.Version > 0;

    /// <summary>История (журнал аудита) — только владельцу/администратору (окончательно решает сервер: 403).</summary>
    public static bool ShowHistory(ReturnDocKind kind, ReturnEditState state, bool isManager) =>
        isManager && FeatureOn(kind) && state.Version > 0;

    /// <summary>2026-11-11 (реальный контракт): почему кнопок «Редактировать»/«Удалить» нет — по edit_block_reason сервера (5 языков); null — пояснять нечего
    /// (нет признака, сервер не прислал причину, документ аннулирован — у него своя плашка).</summary>
    public static string? BlockHint(ReturnDocKind kind, ReturnEditState state)
    {
        if (!FeatureOn(kind) || !state.HasServerFlags || state.IsVoided || (state.CanEdit && state.CanVoid) || state.BlockReason.Length == 0)
            return null;
        return ReturnEditErrors.BlockReasonText(state.BlockReason);
    }

    /// <summary>Загрузить признаки сервера заранее (окно списка ждёт не дольше <paramref name="budget"/>); сбой — кнопок нет.</summary>
    public static async Task WarmAsync(NurMarketApiClient api, TimeSpan? budget = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(budget ?? TimeSpan.FromSeconds(8));
            await ServerCapabilities.LoadAsync(api, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Возвраты (правка 24 ч): признаки сервера не получены ({ex.GetType().Name}) — кнопки правки скрыты.", "WARNING");
        }
    }
}

/// <summary>Почему не получилось (коды из ТЗ серверу, п. 9).</summary>
public enum ReturnEditFailure
{
    None,
    PinInvalid,
    PinLocked,
    PinNotSet,
    ConfirmRequired,
    ConfirmInvalid,
    PermissionDenied,
    WindowExpired,
    AlreadyVoided,
    VersionConflict,
    NegativeStock,
    ExceedsReturnable,
    RefundExceedsPaid,
    Blocked,
    Connection,
    Server,
    // 2026-11-11 (реальный контракт Умара): отдельные коды вместо общего «Blocked»/«VersionConflict»
    VersionRequired,
    Validation,
    IdempotencyKeyRequired,
    DebtAlreadyPaid,
    PeriodClosed,
    NewLinesNotAllowed,
}

/// <summary>Отказ сервера при подтверждении PIN, правке, аннулировании или истории.</summary>
public sealed class ReturnEditException : Exception
{
    public ReturnEditFailure Failure { get; }
    public string Code { get; }
    public int? AttemptsLeft { get; init; }
    public int? RetryAfterSeconds { get; init; }
    public int? CurrentVersion { get; init; }
    public int? StatusCode { get; init; }

    public ReturnEditException(ReturnEditFailure failure, string code, string message, Exception? inner = null) : base(message, inner)
    {
        Failure = failure;
        Code = code;
    }

    /// <summary>После этой ошибки PIN вводить повторно бессмысленно (срок вышел, документ аннулирован/изменён, нет прав).</summary>
    public bool IsFinal => Failure is ReturnEditFailure.WindowExpired or ReturnEditFailure.AlreadyVoided or ReturnEditFailure.VersionConflict
        or ReturnEditFailure.PermissionDenied or ReturnEditFailure.PinNotSet
        // 2026-11-11: документ надо перечитать (версия) или сервер запретил правку по существу — повтор PIN бессмыслен
        or ReturnEditFailure.VersionRequired or ReturnEditFailure.Blocked or ReturnEditFailure.DebtAlreadyPaid or ReturnEditFailure.PeriodClosed;
}

public static class ReturnEditErrors
{
    /// <summary>Любая ошибка запроса → <see cref="ReturnEditException"/> (оригинал — InnerException, для решения о ключе повтора).</summary>
    public static ReturnEditException Classify(Exception ex)
    {
        if (ex is ReturnEditException ready)
            return ready;
        if (ex is not ApiException api)
            return new ReturnEditException(SyncService.IsConnectivityFailure(ex) ? ReturnEditFailure.Connection : ReturnEditFailure.Server, "", ex.Message, ex);
        if (SyncService.IsConnectivityFailure(api))
            return new ReturnEditException(ReturnEditFailure.Connection, "", ex.Message, ex) { StatusCode = api.StatusCode };
        var code = ServerErrorCodes.Code(api) ?? "";
        var failure = code switch
        {
            "pin_invalid" or "invalid_pin" or "wrong_pin" => ReturnEditFailure.PinInvalid,
            "pin_locked" => ReturnEditFailure.PinLocked,
            "pin_not_set" => ReturnEditFailure.PinNotSet,
            "confirm_required" => ReturnEditFailure.ConfirmRequired,
            "confirm_invalid" or "confirm_expired" => ReturnEditFailure.ConfirmInvalid,
            "permission_denied" or "forbidden" => ReturnEditFailure.PermissionDenied,
            "edit_window_expired" => ReturnEditFailure.WindowExpired,
            "already_voided" => ReturnEditFailure.AlreadyVoided,
            "version_conflict" => ReturnEditFailure.VersionConflict,
            // 2026-11-11 (реальный контракт): 428 version_required, 400 validation_error / idempotency_key_required, 409 поставщика — свои тексты
            "version_required" => ReturnEditFailure.VersionRequired,
            "validation_error" => ReturnEditFailure.Validation,
            "idempotency_key_required" => ReturnEditFailure.IdempotencyKeyRequired,
            "negative_stock" => ReturnEditFailure.NegativeStock,
            "exceeds_returnable" => ReturnEditFailure.ExceedsReturnable,
            "refund_exceeds_paid" => ReturnEditFailure.RefundExceedsPaid,
            "debt_already_paid" => ReturnEditFailure.DebtAlreadyPaid,
            "period_closed" => ReturnEditFailure.PeriodClosed,
            "new_lines_not_allowed" => ReturnEditFailure.NewLinesNotAllowed,
            "has_later_documents" or "shift_closed" or "legacy_return_not_supported" or "debt_sale_not_supported" or "agent_sale_not_supported" => ReturnEditFailure.Blocked,
            _ when api.StatusCode == 400 && api.Payload is { ValueKind: JsonValueKind.Object } pp && pp.TryGetProperty("pin", out _) => ReturnEditFailure.PinInvalid,
            _ => api.StatusCode switch
            {
                412 => ReturnEditFailure.VersionConflict,
                428 => ReturnEditFailure.ConfirmRequired,
                429 => ReturnEditFailure.PinLocked,
                401 or 403 => ReturnEditFailure.PermissionDenied,
                >= 500 => ReturnEditFailure.Server,
                _ => ReturnEditFailure.Server,
            },
        };
        int? currentVersion = api.Payload is { } p && ServerErrorCodes.Number(p, "current_version") is { } cv && cv > 0 ? (int)cv : null;
        return new ReturnEditException(failure, code, api.Message, ex)
        {
            StatusCode = api.StatusCode,
            AttemptsLeft = ServerErrorCodes.AttemptsLeft(api),
            RetryAfterSeconds = ServerErrorCodes.RetryAfterSeconds(api),
            CurrentVersion = currentVersion,
        };
    }

    /// <summary>Понятный текст на языке кассы (5 языков). Для «прочих» отказов — слова сервера.</summary>
    public static string Text(ReturnEditException e) => e.Failure switch
    {
        ReturnEditFailure.PinInvalid => ServerErrorCodes.PinInvalidText(e.AttemptsLeft),
        ReturnEditFailure.PinLocked => ServerErrorCodes.PinLockedText(e.RetryAfterSeconds),
        ReturnEditFailure.PinNotSet => Tr.T("У руководителя не задан PIN. Задайте PIN владельца/администратора на сайте NurCRM и повторите.",
            "Жетекчинин PIN коду коюлган эмес. NurCRM сайтында ээсинин/администратордун PIN кодун коюп, кайталаңыз.",
            "The manager has no PIN set. Set the owner/administrator PIN on the NurCRM website and try again.",
            "Yöneticinin PIN'i ayarlanmamış. NurCRM sitesinde sahip/yönetici PIN'ini ayarlayıp tekrar deneyin.",
            "Rahbarning PIN kodi o'rnatilmagan. NurCRM saytida egasi/administrator PIN kodini o'rnating va qayta urining."),
        ReturnEditFailure.ConfirmRequired => Tr.T("Нужно подтверждение руководителя (PIN). Ничего не изменено — нажмите кнопку ещё раз и введите PIN.",
            "Жетекчинин ырастоосу (PIN) керек. Эч нерсе өзгөргөн жок — баскычты кайра басып, PIN кодду киргизиңиз.",
            "The manager's confirmation (PIN) is required. Nothing was changed — press the button again and enter the PIN.",
            "Yönetici onayı (PIN) gerekli. Hiçbir şey değişmedi — düğmeye tekrar basıp PIN'i girin.",
            "Rahbar tasdig'i (PIN) kerak. Hech narsa o'zgarmadi — tugmani qayta bosib, PIN ni kiriting."),
        ReturnEditFailure.ConfirmInvalid => Tr.T("Подтверждение руководителя устарело или уже использовано. Нажмите кнопку ещё раз и введите PIN заново.",
            "Жетекчинин ырастоосу эскирди же колдонулган. Баскычты кайра басып, PIN кодду кайра киргизиңиз.",
            "The manager's confirmation has expired or was already used. Press the button again and re-enter the PIN.",
            "Yönetici onayının süresi doldu veya kullanıldı. Düğmeye tekrar basıp PIN'i yeniden girin.",
            "Rahbar tasdig'i eskirgan yoki ishlatilgan. Tugmani qayta bosib, PIN ni qayta kiriting."),
        ReturnEditFailure.PermissionDenied => Tr.T("Нет прав на это действие. Обратитесь к владельцу.", "Бул аракетке укук жок. Ээсине кайрылыңыз.",
            "No rights for this action. Ask the owner.", "Bu işlem için yetki yok. Sahibine başvurun.", "Bu amalga huquq yo'q. Egasiga murojaat qiling."),
        ReturnEditFailure.WindowExpired => Tr.T("Срок изменения истёк: документ можно править или удалять только 24 часа после проведения (по времени сервера).",
            "Өзгөртүү мөөнөтү бүттү: документти өткөрүлгөндөн кийин 24 саат ичинде гана оңдоого же өчүрүүгө болот (сервердин убактысы боюнча).",
            "The change period has expired: the document can be edited or deleted only within 24 hours after posting (server time).",
            "Değişiklik süresi doldu: belge yalnızca işlendikten sonraki 24 saat içinde düzenlenebilir veya silinebilir (sunucu saati).",
            "O'zgartirish muddati tugadi: hujjatni o'tkazilgandan keyin faqat 24 soat ichida tahrirlash yoki o'chirish mumkin (server vaqti bo'yicha)."),
        ReturnEditFailure.AlreadyVoided => Tr.T("Документ уже аннулирован.", "Документ мурунтан жокко чыгарылган.", "The document is already voided.", "Belge zaten iptal edildi.", "Hujjat allaqachon bekor qilingan."),
        ReturnEditFailure.VersionConflict => VersionConflictText(),
        ReturnEditFailure.NegativeStock => Tr.T("Не хватает остатка на складе — сервер не провёл изменение. Проверьте остаток товара.",
            "Кампада калдык жетишпейт — сервер өзгөртүүнү өткөргөн жок. Товардын калдыгын текшериңиз.",
            "Not enough stock — the server did not apply the change. Check the product stock.",
            "Stok yetersiz — sunucu değişikliği işlemedi. Ürün stokunu kontrol edin.",
            "Omborda qoldiq yetarli emas — server o'zgarishni o'tkazmadi. Mahsulot qoldig'ini tekshiring."),
        ReturnEditFailure.ExceedsReturnable => Tr.T("Количество больше, чем можно вернуть по документу-основанию.", "Саны негизги документ боюнча кайтарууга мүмкүн болгондон көп.",
            "The quantity exceeds what can be returned under the source document.", "Miktar, kaynak belgeye göre iade edilebilecek miktarı aşıyor.", "Miqdor asosiy hujjat bo'yicha qaytarish mumkin bo'lganidan ko'p."),
        ReturnEditFailure.RefundExceedsPaid => Tr.T("Сумма возврата больше оплаченной по чеку.", "Кайтаруу суммасы чек боюнча төлөнгөндөн көп.", "The refund exceeds the amount paid for the receipt.",
            "İade tutarı fişte ödenenden fazla.", "Qaytarish summasi chek bo'yicha to'langanidan ko'p."),
        ReturnEditFailure.Connection => Tr.T("Нет связи с сервером. Ничего не изменено — повторите, когда связь появится.", "Сервер менен байланыш жок. Эч нерсе өзгөргөн жок — байланыш пайда болгондо кайталаңыз.",
            "No connection to the server. Nothing was changed — try again when the connection is back.", "Sunucuyla bağlantı yok. Hiçbir şey değişmedi — bağlantı gelince tekrar deneyin.",
            "Server bilan aloqa yo'q. Hech narsa o'zgarmadi — aloqa paydo bo'lganda qayta urining."),
        // 2026-11-11 (реальный контракт Умара)
        ReturnEditFailure.Blocked => BlockReasonText(e.Code) ?? Tr.T("Сервер запретил изменение документа.", "Сервер документти өзгөртүүгө тыюу салды.", "The server forbade changing the document.",
            "Sunucu belgenin değiştirilmesini yasakladı.", "Server hujjatni o'zgartirishni taqiqladi."),
        ReturnEditFailure.VersionRequired => Tr.T("Сервер не получил версию документа. Обновите список и откройте документ заново — ничего не изменено.",
            "Сервер документтин версиясын алган жок. Тизмени жаңыртып, документти кайра ачыңыз — эч нерсе өзгөргөн жок.",
            "The server did not receive the document version. Refresh the list and open the document again — nothing was changed.",
            "Sunucu belge sürümünü almadı. Listeyi yenileyip belgeyi yeniden açın — hiçbir şey değişmedi.",
            "Server hujjat versiyasini olmadi. Ro'yxatni yangilab, hujjatni qayta oching — hech narsa o'zgarmadi."),
        ReturnEditFailure.Validation => Tr.T("Сервер не принял данные — проверьте количество, цену и причину. ", "Сервер маалыматты кабыл алган жок — санын, баасын жана себебин текшериңиз. ",
            "The server did not accept the data — check the quantity, price and reason. ", "Sunucu verileri kabul etmedi — miktarı, fiyatı ve nedeni kontrol edin. ",
            "Server ma'lumotlarni qabul qilmadi — miqdor, narx va sababni tekshiring. ") + e.Message,
        ReturnEditFailure.IdempotencyKeyRequired => Tr.T("Сервер требует ключ повтора запроса — обновите кассу до последней версии. Ничего не изменено.",
            "Сервер сурамды кайталоо ачкычын талап кылат — кассаны акыркы версияга жаңыртыңыз. Эч нерсе өзгөргөн жок.",
            "The server requires a request retry key — update the till to the latest version. Nothing was changed.",
            "Sunucu istek tekrar anahtarı istiyor — kasayı son sürüme güncelleyin. Hiçbir şey değişmedi.",
            "Server so'rovni takrorlash kalitini talab qiladi — kassani oxirgi versiyaga yangilang. Hech narsa o'zgarmadi."),
        ReturnEditFailure.DebtAlreadyPaid => BlockReasonText("debt_already_paid")!,
        ReturnEditFailure.PeriodClosed => BlockReasonText("period_closed")!,
        ReturnEditFailure.NewLinesNotAllowed => Tr.T("Добавлять новые строки в возврат нельзя — можно только менять количество и цену существующих строк или убирать их.",
            "Кайтарууга жаңы саптарды кошууга болбойт — бар саптардын санын жана баасын гана өзгөртүүгө же алып салууга болот.",
            "New lines cannot be added to the return — you can only change the quantity and price of existing lines or remove them.",
            "İadeye yeni satır eklenemez — yalnızca mevcut satırların miktarını ve fiyatını değiştirebilir veya kaldırabilirsiniz.",
            "Qaytarishga yangi qatorlar qo'shib bo'lmaydi — faqat mavjud qatorlarning miqdori va narxini o'zgartirish yoki olib tashlash mumkin."),
        _ =>Tr.T("Сервер отказал: ", "Сервер четке какты: ", "The server refused: ", "Sunucu reddetti: ", "Server rad etdi: ") + e.Message,
    };

    public static string VersionConflictText() => Tr.T("Документ изменён другим сотрудником. Обновите список и откройте документ заново — ничего не сохранено.",
        "Документти башка кызматкер өзгөрттү. Тизмени жаңыртып, документти кайра ачыңыз — эч нерсе сакталган жок.",
        "The document was changed by another employee. Refresh the list and open the document again — nothing was saved.",
        "Belge başka bir çalışan tarafından değiştirildi. Listeyi yenileyip belgeyi yeniden açın — hiçbir şey kaydedilmedi.",
        "Hujjatni boshqa xodim o'zgartirdi. Ro'yxatni yangilab, hujjatni qayta oching — hech narsa saqlanmadi.");

    /// <summary>2026-11-11: почему свежая карточка не даёт править/аннулировать — по edit_block_reason; без причины — «уже аннулирован» или «срок истёк» (как раньше).</summary>
    public static string NotEditableText(ReturnEditState s) =>
        s.IsVoided ? BlockReasonText("already_voided")!
        : BlockReasonText(s.BlockReason) ?? Text(new ReturnEditException(ReturnEditFailure.WindowExpired, "edit_window_expired", ""));

    /// <summary>2026-11-11 (реальный контракт Умара): понятный текст по коду edit_block_reason (и тем же кодам отказа 403/409); null — кода нет.
    /// Незнакомый код — общий текст с кодом сервера (чтобы техподдержка поняла).</summary>
    public static string? BlockReasonText(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            "" => null,
            "legacy_return_not_supported" => Tr.T("Возврат создан до включения правки — изменить или аннулировать его нельзя.",
                "Кайтаруу оңдоо киргизилгенге чейин түзүлгөн — аны өзгөртүүгө же жокко чыгарууга болбойт.",
                "The return was created before editing was enabled — it cannot be changed or voided.",
                "İade, düzenleme açılmadan önce oluşturuldu — değiştirilemez veya iptal edilemez.",
                "Qaytarish tahrirlash yoqilishidan oldin yaratilgan — uni o'zgartirib yoki bekor qilib bo'lmaydi."),
            "debt_sale_not_supported" => Tr.T("Чек в долг — правка и аннулирование возврата не поддерживаются.",
                "Насыя чек — кайтарууну оңдоо жана жокко чыгаруу колдоого алынбайт.",
                "A debt (credit) receipt — editing and voiding the return are not supported.",
                "Veresiye fiş — iadenin düzenlenmesi ve iptali desteklenmiyor.",
                "Qarzga chek — qaytarishni tahrirlash va bekor qilish qo'llab-quvvatlanmaydi."),
            "agent_sale_not_supported" => Tr.T("Продажа агента — правка и аннулирование возврата не поддерживаются.",
                "Агенттин сатуусу — кайтарууну оңдоо жана жокко чыгаруу колдоого алынбайт.",
                "An agent sale — editing and voiding the return are not supported.",
                "Temsilci satışı — iadenin düzenlenmesi ve iptali desteklenmiyor.",
                "Agent sotuvi — qaytarishni tahrirlash va bekor qilish qo'llab-quvvatlanmaydi."),
            "has_later_documents" => Tr.T("После этого был другой возврат по чеку — изменить или аннулировать этот возврат нельзя.",
                "Мындан кийин чек боюнча башка кайтаруу болгон — бул кайтарууну өзгөртүүгө же жокко чыгарууга болбойт.",
                "There was another return on this receipt after this one — this return cannot be changed or voided.",
                "Bundan sonra bu fişte başka bir iade yapıldı — bu iade değiştirilemez veya iptal edilemez.",
                "Bundan keyin chek bo'yicha boshqa qaytarish bo'lgan — bu qaytarishni o'zgartirib yoki bekor qilib bo'lmaydi."),
            "shift_closed" => Tr.T("Смена закрыта — возврат с деньгами закрытой смены изменить или аннулировать нельзя.",
                "Смена жабылды — жабык сменанын акчасы менен кайтарууну өзгөртүүгө же жокко чыгарууга болбойт.",
                "The shift is closed — a return with money from a closed shift cannot be changed or voided.",
                "Vardiya kapalı — kapalı vardiyanın parasıyla yapılan iade değiştirilemez veya iptal edilemez.",
                "Smena yopilgan — yopiq smena puli bilan qaytarishni o'zgartirib yoki bekor qilib bo'lmaydi."),
            "edit_window_expired" => Tr.T("Прошло 24 часа — изменить или аннулировать возврат уже нельзя (по времени сервера).",
                "24 саат өттү — кайтарууну өзгөртүүгө же жокко чыгарууга мындан ары болбойт (сервердин убактысы боюнча).",
                "24 hours have passed — the return can no longer be changed or voided (server time).",
                "24 saat geçti — iade artık değiştirilemez veya iptal edilemez (sunucu saati).",
                "24 soat o'tdi — qaytarishni endi o'zgartirib yoki bekor qilib bo'lmaydi (server vaqti bo'yicha)."),
            "already_voided" => Tr.T("Уже аннулирован.", "Мурунтан жокко чыгарылган.", "Already voided.", "Zaten iptal edildi.", "Allaqachon bekor qilingan."),
            "permission_denied" or "forbidden" => Tr.T("Нет прав на это действие. Нужны права на возвраты и закупки — обратитесь к владельцу.",
                "Бул аракетке укук жок. Кайтарууларга жана сатып алууларга укук керек — ээсине кайрылыңыз.",
                "No rights for this action. Rights to returns and purchases are required — ask the owner.",
                "Bu işlem için yetki yok. İadeler ve satın alma yetkisi gerekli — sahibine başvurun.",
                "Bu amalga huquq yo'q. Qaytarishlar va xaridlarga huquq kerak — egasiga murojaat qiling."),
            "period_closed" => Tr.T("Период закрыт — документ этого периода изменить или аннулировать нельзя.",
                "Мезгил жабылды — бул мезгилдин документин өзгөртүүгө же жокко чыгарууга болбойт.",
                "The period is closed — a document of this period cannot be changed or voided.",
                "Dönem kapalı — bu dönemin belgesi değiştirilemez veya iptal edilemez.",
                "Davr yopilgan — bu davr hujjatini o'zgartirib yoki bekor qilib bo'lmaydi."),
            "debt_already_paid" => Tr.T("Долг поставщику по этому возврату уже погашен — изменить или аннулировать возврат нельзя.",
                "Бул кайтаруу боюнча жеткирүүчүгө карыз буга чейин төлөнгөн — кайтарууну өзгөртүүгө же жокко чыгарууга болбойт.",
                "The supplier debt for this return has already been settled — the return cannot be changed or voided.",
                "Bu iadeye ait tedarikçi borcu zaten kapatıldı — iade değiştirilemez veya iptal edilemez.",
                "Bu qaytarish bo'yicha yetkazib beruvchiga qarz allaqachon to'langan — qaytarishni o'zgartirib yoki bekor qilib bo'lmaydi."),
            _ => Tr.T($"Изменить нельзя (код сервера: {c}).", $"Өзгөртүүгө болбойт (сервердин коду: {c}).", $"Cannot be changed (server code: {c}).",
                $"Değiştirilemez (sunucu kodu: {c}).", $"O'zgartirib bo'lmaydi (server kodi: {c})."),
        };
    }

    /// <summary>Метка аннулированного документа («АННУЛИРОВАН») на языке кассы.</summary>
    public static string VoidedMark() => Tr.T("АННУЛИРОВАН", "ЖОККО ЧЫГАРЫЛДЫ", "VOIDED", "İPTAL EDİLDİ", "BEKOR QILINDI");
}

/// <summary>Итог проверки PIN руководителя. Token — значение для X-Confirm-Token (одноразовое, в журнал не пишется).</summary>
public sealed record ReturnConfirmResult(bool Ok, string? Token, string ApprovedBy, ReturnEditException? Error);

/// <summary>Версия документа из журнала аудита (GET …/returns/{id}/history/). Snapshot — документ целиком на этой версии (для просмотра исходной).</summary>
public sealed record ReturnHistoryEntry(int Version, string Action, DateTimeOffset? At, string User, string ApprovedBy, string Reason, double Total, JsonElement? Snapshot, string Diff)
{
    /// <summary>Строки документа на этой версии: товар, количество, единица, цена, скидка, сумма. Понимает оба типа (qty/purchase_price и quantity/price).</summary>
    public IReadOnlyList<(string Name, double Qty, string Unit, double Price, double Discount, double Total)> SnapshotLines()
    {
        var list = new List<(string, double, string, double, double, double)>();
        if (Snapshot is not { ValueKind: JsonValueKind.Object } s)
            return list;
        var items = s.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Array ? it
            : s.TryGetProperty("lines", out var ln) && ln.ValueKind == JsonValueKind.Array ? ln : default;
        if (items.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var i in items.EnumerateArray())
        {
            var name = ReturnEditState.Str(i, "product_name") is { Length: > 0 } pn ? pn : ReturnEditState.Str(i, "name");
            var qty = ReturnEditState.Str(i, "qty").Length > 0 ? ReturnEditState.Num(i, "qty") : ReturnEditState.Num(i, "quantity");
            var price = ReturnEditState.Str(i, "purchase_price").Length > 0 ? ReturnEditState.Num(i, "purchase_price") : ReturnEditState.Num(i, "price");
            var discount = ReturnEditState.Num(i, "discount");
            var total = ReturnEditState.Str(i, "line_total").Length > 0 ? ReturnEditState.Num(i, "line_total") : ReturnLineMath.LineTotal(qty, price, discount);
            list.Add((name, qty, ReturnEditState.Str(i, "unit"), price, discount, total));
        }
        return list;
    }

    public bool IsCreated => Action == "created";
    public bool IsVoided => Action == "voided";
}

/// <summary>2026-11-10: сумма строки возврата со скидкой: (кол-во × цена − скидка), до копейки от нуля, не меньше 0 (как SupplierReturnMath).</summary>
public static class ReturnLineMath
{
    public static double LineTotal(double qty, double price, double discount) =>
        (double)Math.Max(0m, Math.Round((decimal)Math.Max(0, qty) * (decimal)Math.Max(0, price) - (decimal)Math.Max(0, discount), 2, MidpointRounding.AwayFromZero));

    public static double Total(IEnumerable<(double Qty, double Price, double Discount)> lines) =>
        (double)lines.Sum(l => (decimal)LineTotal(l.Qty, l.Price, l.Discount));
}

/// <summary>Запросы правки/аннулирования возвратов (оба типа). Контракт — наше ТЗ серверу; 2026-11-11 сверен с реализацией Умара (боевой сервер).</summary>
public sealed class ReturnEditApi
{
    internal const string ConfirmTokenHeader = "X-Confirm-Token";
    private static readonly string[] TokenFields = ["confirm_token", "confirmation_token", "token"];
    private static readonly object LockGate = new();
    private static DateTime _pinLockedUntilUtc = DateTime.MinValue;

    private readonly NurMarketApiClient _api;

    public ReturnEditApi(NurMarketApiClient api) => _api = api;

    /// <summary>Секунд до конца блокировки PIN, о которой сказал сервер (429 pin_locked); 0 — можно вводить. Во время блокировки запросы не отправляются.</summary>
    public static int PinLockSecondsLeft
    {
        get
        {
            lock (LockGate)
            {
                var left = (_pinLockedUntilUtc - DateTime.UtcNow).TotalSeconds;
                return left > 0 ? (int)Math.Ceiling(left) : 0;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (LockGate)
            _pinLockedUntilUtc = DateTime.MinValue;
    }

    /// <summary>Карточка документа (GET …/returns/{id}/) — свежие version/can_edit по времени сервера перед правкой.</summary>
    public async Task<JsonElement> GetDocumentAsync(ReturnDocKind kind, string id, CancellationToken ct = default)
    {
        try
        {
            return await _api.RequestAsync(HttpMethod.Get, ReturnDocKinds.DocPath(kind, id), null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ReturnEditErrors.Classify(ex);
        }
    }

    public async Task<ReturnEditState> GetStateAsync(ReturnDocKind kind, string id, CancellationToken ct = default) =>
        ReturnEditState.Parse(await GetDocumentAsync(kind, id, ct).ConfigureAwait(false), DateTime.UtcNow);

    /// <summary>2026-11-11 (окно правки возврата от клиента): карточка чека GET pos/sales/{id}/ — строки чека (id = sale_item_id, quantity, returnable_qty, цена)
    /// для «максимум = в чеке − уже возвращено другими».</summary>
    public async Task<JsonElement> GetSaleAsync(string saleId, CancellationToken ct = default)
    {
        try
        {
            return await _api.RequestAsync(HttpMethod.Get, "api/main/pos/sales/" + Uri.EscapeDataString(saleId.Trim()) + "/", null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ReturnEditErrors.Classify(ex);
        }
    }

    /// <summary>PIN руководителя → одноразовый токен для ОДНОГО действия над ОДНИМ документом и версией (POST users/auth/pin/verify/ с областью действия).
    /// Ошибки PIN/срока не бросаются — возвращаются в <see cref="ReturnConfirmResult.Error"/>. PIN в журнал не пишется.</summary>
    public async Task<ReturnConfirmResult> ConfirmAsync(ReturnDocKind kind, string id, ReturnEditAction action, int version, string pin, CancellationToken ct = default)
    {
        var locked = PinLockSecondsLeft;
        if (locked > 0)
            return new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinLocked, ServerErrorCodes.PinLocked, "pin_locked (local)") { RetryAfterSeconds = locked });
        if (string.IsNullOrWhiteSpace(pin))
            return new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinInvalid, ServerErrorCodes.PinInvalid, "empty"));
        var body = new Dictionary<string, object?>
        {
            ["pin"] = pin.Trim(),
            ["scope"] = ReturnDocKinds.Scope(action),
            ["object_type"] = ReturnDocKinds.ObjectType(kind),
            ["object_id"] = id,
            ["version"] = version,
        };
        var what = $"{ReturnDocKinds.ObjectType(kind)} {id} ({ReturnDocKinds.Scope(action)}, v{version})";
        try
        {
            using var critical = ApiThrottle.BeginCritical();
            var r = await _api.RequestAsync(HttpMethod.Post, "api/users/auth/pin/verify/", body, null, ct, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            body["pin"] = null;
            if (r.ValueKind != JsonValueKind.Object)
                return new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.Server, "", "no_token"));
            if (r.TryGetProperty("valid", out var valid) && valid.ValueKind == JsonValueKind.False)
            {
                PosLogger.Log($"Возвраты: PIN руководителя для {what} — неверный.", "WARNING");
                return new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.PinInvalid, ServerErrorCodes.PinInvalid, "valid=false"));
            }
            // Ответ с access/refresh — это токены входа, а не подтверждение: их в X-Confirm-Token не подставляем (как у позднего чека).
            var sessionTokens = r.TryGetProperty("access", out _) || r.TryGetProperty("refresh", out _);
            foreach (var name in TokenFields)
            {
                if (name == "token" && sessionTokens)
                    continue;
                if (r.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } token)
                {
                    var approvedBy = ReturnEditState.Str(r, "approved_by_name");
                    PosLogger.Log($"Возвраты: PIN руководителя для {what} принят{(approvedBy.Length > 0 ? " (" + approvedBy + ")" : "")}.", "INFO");
                    return new ReturnConfirmResult(true, token, approvedBy, null);
                }
            }
            PosLogger.Log($"Возвраты: PIN для {what} — в ответе нет токена подтверждения (поля: {string.Join(", ", r.EnumerateObject().Select(p => p.Name))}).", "WARNING");
            return new ReturnConfirmResult(false, null, "", new ReturnEditException(ReturnEditFailure.Server, "", "no_token"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            body["pin"] = null;
            var e = ReturnEditErrors.Classify(ex);
            if (e.Failure == ReturnEditFailure.PinLocked)
            {
                var seconds = e.RetryAfterSeconds ?? 15 * 60;
                lock (LockGate)
                    _pinLockedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
            }
            PosLogger.Log($"Возвраты: PIN руководителя для {what} не подтверждён: {e.Failure} (код {e.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}).", "WARNING");
            return new ReturnConfirmResult(false, null, "", e);
        }
    }

    /// <summary>PATCH документа (полный новый состав) с Idempotency-Key, If-Match: "версия", X-Confirm-Token. Отказ — <see cref="ReturnEditException"/>.</summary>
    public async Task<JsonElement> PatchAsync(ReturnDocKind kind, string id, int version, IDictionary<string, object?> body, string confirmToken, string idempotencyKey, CancellationToken ct = default)
    {
        body["version"] = version;
        return await SendAsync(HttpMethod.Patch, ReturnDocKinds.DocPath(kind, id), version, body, confirmToken, idempotencyKey, ct, "правка").ConfigureAwait(false);
    }

    /// <summary>Аннулирование (soft delete) POST …/returns/{id}/void/ с причиной. Отказ — <see cref="ReturnEditException"/>.</summary>
    public async Task<JsonElement> VoidAsync(ReturnDocKind kind, string id, int version, string reason, string confirmToken, string idempotencyKey, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?> { ["version"] = version, ["reason"] = (reason ?? "").Trim() };
        return await SendAsync(HttpMethod.Post, ReturnDocKinds.DocPath(kind, id) + "void/", version, body, confirmToken, idempotencyKey, ct, "аннулирование").ConfigureAwait(false);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, int version, object body, string confirmToken, string idempotencyKey, CancellationToken ct, string what)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(confirmToken))
            throw new ArgumentException("Нужен токен подтверждения руководителя.", nameof(confirmToken));
        if (version <= 0)
            throw new ArgumentException("Версия документа обязательна.", nameof(version));
        var headers = new Dictionary<string, string>
        {
            ["Idempotency-Key"] = idempotencyKey.Trim(),
            ["If-Match"] = "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"",
            [ConfirmTokenHeader] = confirmToken.Trim(),
        };
        try
        {
            using var critical = ApiThrottle.BeginCritical(); // деньги и склад — быстрая полоса очереди
            var (data, _) = await _api.RequestWithHeadersAsync(method, path, body, headers, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            // 2026-11-11: повтор с тем же Idempotency-Key сервер отвечает тем же результатом и replayed:true — это успех, не второе проведение
            var replayed = ReturnEditState.Bool(data, "replayed") == true;
            PosLogger.Log($"Возвраты: {what} {path} (v{version}) — принято сервером{(replayed ? " (повтор запроса, replayed)" : "")}.", "INFO");
            return data;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var e = ReturnEditErrors.Classify(ex);
            PosLogger.Log($"Возвраты: {what} {path} (v{version}) не выполнено: {e.Failure} (код {e.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "—"}, {e.Code}).", "WARNING");
            throw e;
        }
    }

    /// <summary>История версий (журнал аудита): GET …/returns/{id}/history/. 403 — не руководитель (ReturnEditException PermissionDenied).</summary>
    public async Task<List<ReturnHistoryEntry>> GetHistoryAsync(ReturnDocKind kind, string id, CancellationToken ct = default)
    {
        JsonElement d;
        try
        {
            d = await _api.RequestAsync(HttpMethod.Get, ReturnDocKinds.DocPath(kind, id) + "history/", null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw ReturnEditErrors.Classify(ex);
        }
        return ParseHistory(d);
    }

    public static List<ReturnHistoryEntry> ParseHistory(JsonElement d)
    {
        var rows = d.ValueKind == JsonValueKind.Array ? d
            : d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r : default;
        var list = new List<ReturnHistoryEntry>();
        if (rows.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var x in rows.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object)
                continue;
            var user = ReturnEditState.Str(x, "user_name") is { Length: > 0 } u ? u
                : x.TryGetProperty("user", out var uo) && uo.ValueKind == JsonValueKind.Object ? ReturnEditState.Str(uo, "name") : ReturnEditState.Str(x, "user");
            JsonElement? snapshot = x.TryGetProperty("snapshot", out var s) && s.ValueKind == JsonValueKind.Object ? s.Clone() : null;
            var diff = x.TryGetProperty("diff", out var df) ? df.ValueKind == JsonValueKind.String ? df.GetString() ?? "" : df.ValueKind == JsonValueKind.Array ? df.GetRawText() : "" : "";
            list.Add(new ReturnHistoryEntry((int)ReturnEditState.Num(x, "version"), ReturnEditState.Str(x, "action").ToLowerInvariant(),
                ReturnEditState.Date(x, "at") ?? ReturnEditState.Date(x, "created_at"), user, ReturnEditState.Str(x, "approved_by_name"), ReturnEditState.Str(x, "reason"),
                ReturnEditState.Num(x, "total"), snapshot, diff));
        }
        return list.OrderBy(v => v.Version).ToList();
    }
}
