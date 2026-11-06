namespace NurMarketKassa.Services;

// 2026-11-06, просьба владельца: «История неотправленных документов — чеков, накладных, приходов — и в ней возможность ручной отправки».
// Единое окно «Неотправленное» читает записи из ВСЕХ очередей кассы через эти типы. Ничего не хранит и не меняет форматы файлов:
// каждый источник (IOutboxSource) только показывает свои записи и для отправки вызывает ТОТ ЖЕ путь, что и автоматика, с ТЕМ ЖЕ ключом повтора.

/// <summary>Вид записи в «Неотправленном».</summary>
public enum OutboxKind
{
    /// <summary>Чек продажи (очередь офлайн-чеков).</summary>
    Sale,
    /// <summary>Возврат, оформленный без связи.</summary>
    Return,
    /// <summary>Оплата долга, принятая без связи.</summary>
    DebtPayment,
    /// <summary>Приход (накладная) в фоне.</summary>
    Receiving,
    /// <summary>Новый товар, заведённый без связи.</summary>
    NewProduct,
    /// <summary>Закрытие смены, не принятое сервером.</summary>
    ShiftClose,
    /// <summary>Внесение или изъятие денег кассы.</summary>
    CashOperation,
}

/// <summary>Состояние записи: ждёт связи/очереди, сейчас отправляется, отказ по существу.</summary>
public enum OutboxStatus
{
    Waiting,
    Sending,
    Error,
}

/// <summary>Строка подробностей записи (позиция чека/прихода).</summary>
public sealed record OutboxLine(string Name, string Quantity, string Amount, string? Note = null);

/// <summary>Одна запись «Неотправленного». Только чтение — изменения идут через источник.</summary>
public sealed class OutboxItem
{
    /// <summary>Уникальный ключ «источник:id» — по нему замок «отправляется» и действия окна.</summary>
    public string Key => Source + ":" + Id;

    public string Source { get; init; } = "";
    public string Id { get; init; } = "";
    public OutboxKind Kind { get; init; }

    /// <summary>Номер чека / название товара / поставщик — что кассир узнает глазами.</summary>
    public string Title { get; init; } = "";

    /// <summary>Вторая строка: кассир, клиент, поставщик.</summary>
    public string? Subtitle { get; init; }

    public decimal? Amount { get; init; }
    public int? LinesCount { get; init; }

    /// <summary>ИСХОДНОЕ время создания записи (не время отправки): по нему сортировка и порядок отправки.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    public OutboxStatus Status { get; init; }

    /// <summary>Причина последней ошибки/ожидания простым языком (текст сервера, если он отказал по существу).</summary>
    public string? Reason { get; init; }

    public int Attempts { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>Кто создал (имя кассира). Пусто — запись общая: видна всем, чужой её считать нельзя.</summary>
    public string? Owner { get; init; }

    public IReadOnlyList<OutboxLine> Lines { get; init; } = Array.Empty<OutboxLine>();

    /// <summary>Способ оплаты и т. п. (для чека).</summary>
    public string? Details { get; init; }

    /// <summary>Подсказка, что делать дальше (например «проведите по PIN владельца в «Некорректных чеках»»).</summary>
    public string? Hint { get; init; }

    // ── что можно сделать (решает источник; окно и сервис это не переопределяют, а только сужают) ──
    public bool CanSend { get; init; } = true;
    public bool CanRetry { get; init; }
    public bool CanRemove { get; init; }
    public bool CanTakeBack { get; init; }
    public bool CanCopy { get; init; } = true;
    public bool CanSupport { get; init; }

    /// <summary>Подпись вида на языке интерфейса.</summary>
    public string KindLabel => OutboxText.KindLabel(Kind);
}

/// <summary>Кто смотрит окно. Кассир видит и отправляет свои записи, владелец/админ — все и может убирать.</summary>
public sealed record OutboxAccess(bool IsAdmin, string? UserName)
{
    /// <summary>Запись видна: админу — любая; остальным — свои и общие (без владельца).</summary>
    public bool CanSee(OutboxItem item) =>
        IsAdmin || string.IsNullOrWhiteSpace(item.Owner)
        || string.Equals(item.Owner.Trim(), (UserName ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Источник записей одной очереди. Реализация обязана отправлять тем же путём и тем же ключом, что автоматика.</summary>
public interface IOutboxSource
{
    string Name { get; }

    /// <summary>Текущие записи (копии). Не бросает: ошибка чтения — пустой список и строка в журнал.</summary>
    IReadOnlyList<OutboxItem> Snapshot();

    /// <summary>Форсировать отправку этой записи «сейчас» (тот же путь, что у автоматики). Возвращается, когда попытка закончилась или время вышло.</summary>
    Task SendAsync(string id, CancellationToken ct);

    /// <summary>«Повторить» для записи с ошибкой: вернуть в очередь тем же ключом (без отправки). false — нельзя.</summary>
    bool Retry(string id);

    /// <summary>«Убрать из очереди» (только для записей, где это допустимо). false — нельзя.</summary>
    bool Remove(string id, string who);

    /// <summary>Данные записи текстом — для «Скопировать» и «Отправить в поддержку».</summary>
    string? ExportText(string id);

    /// <summary>Подписка на изменения очереди (для живого обновления окна). Dispose отписывает.</summary>
    IDisposable Subscribe(Action changed);
}

/// <summary>Источник, который умеет отправить сразу несколько записей одним проходом (очередь чеков на тысячи записей: «Отправить всё» не должно делать по проходу на чек). Порядок внутри прохода
/// определяет сам источник (по времени создания), ключи повтора у записей те же.</summary>
public interface IOutboxBatchSource : IOutboxSource
{
    Task SendManyAsync(IReadOnlyList<string> ids, CancellationToken ct);
}

/// <summary>Чем закончилась попытка ручной отправки.</summary>
public enum OutboxSendKind
{
    /// <summary>Запись ушла на сервер и исчезла из очереди.</summary>
    Sent,
    /// <summary>Запись осталась в очереди (нет связи у сервера, ждёт предыдущих, идёт повтор).</summary>
    Queued,
    /// <summary>Сервер отказал по существу — статус «Ошибка», причина в сообщении.</summary>
    Rejected,
    /// <summary>Эту запись уже отправляют (автоматика или двойное нажатие) — ничего не сделано.</summary>
    AlreadySending,
    /// <summary>Нет связи с сервером — запись осталась в очереди.</summary>
    NoConnection,
    /// <summary>Сессия недействительна — нужен повторный вход.</summary>
    SessionExpired,
    /// <summary>Запись чужая — кассиру нельзя.</summary>
    Forbidden,
    /// <summary>Записи уже нет (ушла сама).</summary>
    NotFound,
}

/// <summary>Итог ручной отправки одной записи.</summary>
public sealed record OutboxSendResult(OutboxSendKind Kind, string Message, OutboxItem? Item = null)
{
    public bool IsSent => Kind == OutboxSendKind.Sent;
}

/// <summary>Итог «Отправить всё».</summary>
public sealed record OutboxBatchResult(int Sent, int Queued, int Rejected, OutboxSendKind? StoppedBy, string Message);

/// <summary>Итог «Убрать из очереди».</summary>
public sealed record OutboxRemoveResult(bool Done, string Message);

/// <summary>Тексты окна на 5 языках (общие для сервиса и окна).</summary>
public static class OutboxText
{
    public static string Title => Tr.T("Неотправленное", "Жөнөтүлбөгөндөр", "Unsent", "Gönderilmeyenler", "Yuborilmaganlar");

    public static string KindLabel(OutboxKind kind) => kind switch
    {
        OutboxKind.Sale => Tr.T("Чек продажи", "Сатуу чеги", "Sales receipt", "Satış fişi", "Sotuv cheki"),
        OutboxKind.Return => Tr.T("Возврат", "Кайтаруу", "Return", "İade", "Qaytarish"),
        OutboxKind.DebtPayment => Tr.T("Оплата долга", "Карыз төлөмү", "Debt payment", "Borç ödemesi", "Qarz to'lovi"),
        OutboxKind.Receiving => Tr.T("Приход (накладная)", "Кирүү (накладной)", "Receiving (invoice)", "Mal kabul (irsaliye)", "Kirim (nakladnoy)"),
        OutboxKind.NewProduct => Tr.T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi tovar"),
        OutboxKind.ShiftClose => Tr.T("Закрытие смены", "Сменаны жабуу", "Shift closing", "Vardiya kapatma", "Smenani yopish"),
        OutboxKind.CashOperation => Tr.T("Внесение/изъятие", "Киргизүү/алуу", "Cash in/out", "Para girişi/çıkışı", "Kiritish/olish"),
        _ => Tr.T("Другое", "Башка", "Other", "Diğer", "Boshqa"),
    };

    public static string StatusLabel(OutboxStatus status) => status switch
    {
        OutboxStatus.Sending => Tr.T("Отправляется", "Жөнөтүлүүдө", "Sending", "Gönderiliyor", "Yuborilmoqda"),
        OutboxStatus.Error => Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
        _ => Tr.T("Ждёт связи", "Байланышты күтөт", "Waiting for connection", "Bağlantı bekliyor", "Aloqani kutmoqda"),
    };

    /// <summary>2026-11-06 (В-2): внутренние коды состояния записи (retry_wait, pending_sync, posted…) простыми словами — для «Подробнее» и «Скопировать данные».
    /// Неизвестный код возвращается как есть.</summary>
    public static string RawStatus(string? raw)
    {
        switch ((raw ?? "").Trim().Replace("_", "").ToLowerInvariant())
        {
            case "": return "—";
            case "queued" or "pending" or "pendingsync" or "waiting":
                return Tr.T("ждёт отправки", "жөнөтүүнү күтөт", "waiting to be sent", "gönderilmeyi bekliyor", "yuborilishini kutmoqda");
            case "sending" or "syncing" or "finishing":
                return Tr.T("отправляется", "жөнөтүлүүдө", "being sent", "gönderiliyor", "yuborilmoqda");
            case "retrywait":
                return Tr.T("ждёт повторной попытки", "кайра аракетти күтөт", "waiting for a retry", "yeniden denemeyi bekliyor", "qayta urinishni kutmoqda");
            case "done" or "synced" or "posted":
                return Tr.T("принято сервером", "сервер кабыл алды", "accepted by the server", "sunucu kabul etti", "server qabul qildi");
            case "partial":
                return Tr.T("принято частично", "жарым-жартылай кабыл алынды", "partly accepted", "kısmen kabul edildi", "qisman qabul qilindi");
            case "failed" or "rejected":
                return Tr.T("сервер отказал", "сервер баш тартты", "refused by the server", "sunucu reddetti", "server rad etdi");
            case "needscheck" or "check":
                return Tr.T("нужна проверка", "текшерүү керек", "needs checking", "kontrol gerekiyor", "tekshirish kerak");
            case "unknown":
                return Tr.T("ответ сервера не получен", "сервердин жообу алынган жок", "no answer from the server", "sunucudan yanıt alınamadı", "serverdan javob olinmadi");
            default: return raw!;
        }
    }

    /// <summary>2026-11-06 (Б-7): текст пустого списка. hiddenByFilter — записи есть, но выбранный фильтр их скрыл: «всё на сервере» тогда была бы неправдой.</summary>
    public static string EmptyList(bool hiddenByFilter) => hiddenByFilter
        ? Tr.T("По этому фильтру ничего нет.", "Бул чыпка боюнча эч нерсе жок.", "Nothing matches this filter.", "Bu filtreyle eşleşen bir şey yok.", "Bu filtr bo'yicha hech narsa yo'q.")
        : Tr.T("Неотправленного нет — всё на сервере.", "Жөнөтүлбөгөн жазуу жок — баары серверде.", "Nothing is unsent — everything is on the server.",
            "Gönderilmemiş bir şey yok — her şey sunucuda.", "Yuborilmagan narsa yo'q — hammasi serverda.");

    public static string NoConnection => Tr.T("Нет связи с сервером — запись осталась в очереди.", "Сервер менен байланыш жок — жазуу кезекте калды.",
        "No connection to the server — the item stayed in the queue.", "Sunucuyla bağlantı yok — kayıt kuyrukta kaldı.", "Server bilan aloqa yo'q — yozuv navbatda qoldi.");

    public static string SessionExpired => Tr.T("Войдите снова — сессия недействительна. Записи сохранены и уйдут после входа.", "Кайра кириңиз — сессия жараксыз. Жазуулар сакталды, кирген соң жөнөтүлөт.",
        "Sign in again — the session is no longer valid. The items are kept and will be sent after sign-in.", "Tekrar giriş yapın — oturum geçersiz. Kayıtlar saklandı, girişten sonra gönderilir.", "Qayta kiring — sessiya yaroqsiz. Yozuvlar saqlandi, kirgach yuboriladi.");

    public static string AlreadySending => Tr.T("Эта запись уже отправляется.", "Бул жазуу мурунтан эле жөнөтүлүүдө.", "This item is already being sent.", "Bu kayıt zaten gönderiliyor.", "Bu yozuv allaqachon yuborilmoqda.");

    public static string Forbidden => Tr.T("Эта запись создана другим сотрудником — отправить её может владелец или администратор.", "Бул жазууну башка кызматкер түзгөн — аны ээси же администратор жөнөтө алат.",
        "This item was created by another employee — only the owner or an administrator can send it.", "Bu kayıt başka bir çalışan tarafından oluşturuldu — yalnızca sahip veya yönetici gönderebilir.", "Bu yozuvni boshqa xodim yaratgan — uni faqat egasi yoki administrator yuborishi mumkin.");

    public static string NotFound => Tr.T("Записи уже нет в очереди — она ушла на сервер.", "Жазуу кезекте жок — серверге жөнөп кеткен.", "The item is no longer in the queue — it has been sent.", "Kayıt artık kuyrukta değil — gönderildi.", "Yozuv endi navbatda yo'q — serverga yuborilgan.");

    public static string SentOk => Tr.T("Отправлено.", "Жөнөтүлдү.", "Sent.", "Gönderildi.", "Yuborildi.");

    public static string StillQueued => Tr.T("Запись осталась в очереди и уйдёт сама.", "Жазуу кезекте калды, өзү жөнөтүлөт.", "The item stayed in the queue and will go by itself.", "Kayıt kuyrukta kaldı, kendiliğinden gider.", "Yozuv navbatda qoldi, o'zi ketadi.");

    public static string RemoveForbiddenSale => Tr.T("Чек продажи убрать нельзя — это деньги. Скопируйте данные чека или отправьте их в поддержку.", "Сатуу чегин алып салууга болбойт — бул акча. Чектин маалыматтарын көчүрүңүз же колдоого жөнөтүңүз.",
        "A sales receipt cannot be removed — it is money. Copy the receipt data or send it to support.", "Satış fişi kaldırılamaz — bu paradır. Fiş verilerini kopyalayın veya desteğe gönderin.", "Sotuv chekini olib tashlab bo'lmaydi — bu pul. Chek ma'lumotlarini nusxalang yoki qo'llab-quvvatlashga yuboring.");

    public static string RemoveForbiddenRole => Tr.T("Убирать записи из очереди может только владелец или администратор.", "Жазууларды кезектен алып салууну ээси же администратор гана жасай алат.",
        "Only the owner or an administrator can remove items from the queue.", "Kuyruktan kayıt kaldırmayı yalnızca sahip veya yönetici yapabilir.", "Yozuvlarni navbatdan faqat egasi yoki administrator olib tashlay oladi.");

    public static string RemoveConfirm => Tr.T("Эти данные не попадут на сервер. Убрать?", "Бул маалыматтар серверге түшпөйт. Алып салалыбы?", "This data will not reach the server. Remove?", "Bu veriler sunucuya ulaşmayacak. Kaldırılsın mı?", "Bu ma'lumotlar serverga tushmaydi. Olib tashlansinmi?");

    public static string Removed => Tr.T("Запись убрана из очереди.", "Жазуу кезектен алынды.", "The item was removed from the queue.", "Kayıt kuyruktan kaldırıldı.", "Yozuv navbatdan olib tashlandi.");

    public static string RemoveFailed => Tr.T("Убрать не получилось — запись изменилась. Обновите список.", "Алып салуу мүмкүн болгон жок — жазуу өзгөрдү. Тизмени жаңыртыңыз.", "Could not remove — the item has changed. Refresh the list.", "Kaldırılamadı — kayıt değişti. Listeyi yenileyin.", "Olib tashlab bo'lmadi — yozuv o'zgargan. Ro'yxatni yangilang.");
}
