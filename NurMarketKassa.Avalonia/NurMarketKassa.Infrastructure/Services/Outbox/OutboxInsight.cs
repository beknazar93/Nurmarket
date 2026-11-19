using System.Globalization;

namespace NurMarketKassa.Services;

// 2026-11-14 (пакет Л-6 «Офлайн»): подсказки к экрану «Неотправленное» — возраст записи, срок сервера (7 суток), «что будет, если не отправить», сводка с деньгами
// и совет при долгом офлайне. Только чистая логика и тексты: ничего не читает и не меняет (источники и сервис очереди остаются прежними).

/// <summary>Положение записи относительно срока приёма сервера (офлайн-чек и офлайн-приход — до 7 суток с настоящей датой, ответ Умара 13.11, П0-2).</summary>
public enum OutboxDeadlineLevel
{
    /// <summary>Срока нет (вид записи не зависит от него) или ещё далеко.</summary>
    None,
    /// <summary>Подходит к 7 суткам: пока можно отправить обычным путём.</summary>
    Soon,
    /// <summary>Больше 7 суток: сервер сам не примет — нужен владелец (PIN) или ручное проведение.</summary>
    Expired,
    /// <summary>Больше 90 суток: только вручную на сайте.</summary>
    TooLate,
}

/// <summary>Сводка по списку «Неотправленного» для шапки окна.</summary>
public sealed record OutboxSummary(int Count, int Errors, decimal SalesMoney, int SalesCount, DateTimeOffset? Oldest, int Soon, int Expired);

/// <summary>Совет при долгом офлайне: Level 0 — ничего не показывать, 1 — предупреждение, 2 — срочно.</summary>
public sealed record LongOfflineAdvice(int Level, string Text);

public static class OutboxInsight
{
    /// <summary>Предел сервера для офлайн-чека и офлайн-прихода с настоящей датой (max_days).</summary>
    public const int ServerMaxDays = 7;

    /// <summary>Предел «позднего» чека с владельцем (late_max_days).</summary>
    public const int ServerLateMaxDays = 90;

    /// <summary>С какого возраста предупреждаем «скоро 7 суток» (остаётся 2 суток — как «за 2 дня до конца срока» в плане Л-6).</summary>
    public const int WarnFromDays = ServerMaxDays - 2;

    /// <summary>Сколько часов офлайна считать «долгим» (плашка с советом).</summary>
    public const int LongOfflineHours = 4;

    /// <summary>Возраст простыми словами: «5 мин», «3 ч 20 мин», «2 дн. 4 ч».</summary>
    public static string AgeText(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var age = now - createdAt;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalMinutes < 1)
            return Tr.T("только что", "жаңы эле", "just now", "az önce", "hozirgina");
        if (age.TotalHours < 1)
            return Tr.T($"{(int)age.TotalMinutes} мин", $"{(int)age.TotalMinutes} мүн", $"{(int)age.TotalMinutes} min", $"{(int)age.TotalMinutes} dk", $"{(int)age.TotalMinutes} daq");
        if (age.TotalDays < 1)
        {
            var h = (int)age.TotalHours;
            var m = age.Minutes;
            return Tr.T($"{h} ч {m} мин", $"{h} саат {m} мүн", $"{h} h {m} min", $"{h} sa {m} dk", $"{h} soat {m} daq");
        }
        var d = (int)age.TotalDays;
        var hh = age.Hours;
        return Tr.T($"{d} дн. {hh} ч", $"{d} күн {hh} саат", $"{d} d {hh} h", $"{d} gün {hh} sa", $"{d} kun {hh} soat");
    }

    /// <summary>Зависит ли срок приёма сервера от вида записи: чек продажи и приход от поставщика — да (7 суток), остальное сервер по дате не отсекает.</summary>
    public static bool HasServerDeadline(OutboxKind kind) => kind is OutboxKind.Sale or OutboxKind.Receiving;

    public static OutboxDeadlineLevel Deadline(OutboxItem item, DateTimeOffset now)
    {
        if (!HasServerDeadline(item.Kind))
            return OutboxDeadlineLevel.None;
        var days = (now - item.CreatedAt).TotalDays;
        if (days >= ServerLateMaxDays)
            return OutboxDeadlineLevel.TooLate;
        if (days >= ServerMaxDays)
            return OutboxDeadlineLevel.Expired;
        return days >= WarnFromDays ? OutboxDeadlineLevel.Soon : OutboxDeadlineLevel.None;
    }

    /// <summary>Строка о сроке с понятным действием; null — сказать нечего.</summary>
    public static string? DeadlineText(OutboxItem item, DateTimeOffset now)
    {
        var level = Deadline(item, now);
        var sale = item.Kind == OutboxKind.Sale;
        var left = Math.Max(0, (int)Math.Ceiling(ServerMaxDays - (now - item.CreatedAt).TotalDays));
        switch (level)
        {
            case OutboxDeadlineLevel.Soon:
                return sale
                    ? Tr.T($"Чеку почти {ServerMaxDays} суток (осталось ~{left} дн.): после этого сервер сам его не примет. Отправьте сейчас — «Отправить сейчас» или восстановите связь.",
                        $"Чекке дээрлик {ServerMaxDays} суткага жакын (~{left} күн калды): андан кийин сервер аны өзү кабыл албайт. Азыр жөнөтүңүз — «Азыр жөнөтүү» же байланышты калыбына келтириңиз.",
                        $"The receipt is almost {ServerMaxDays} days old (~{left} d left): after that the server will not take it by itself. Send it now — “Send now” or restore the connection.",
                        $"Fiş neredeyse {ServerMaxDays} günlük (~{left} gün kaldı): sonra sunucu bunu kendiliğinden kabul etmez. Şimdi gönderin — «Şimdi gönder» veya bağlantıyı kurun.",
                        $"Chek deyarli {ServerMaxDays} kunlik (~{left} kun qoldi): undan keyin server uni o'zi qabul qilmaydi. Hozir yuboring — «Hozir yuborish» yoki aloqani tiklang.")
                    : Tr.T($"Приходу почти {ServerMaxDays} суток (осталось ~{left} дн.): потом сервер сам его не примет. Отправьте сейчас.",
                        $"Кирүүгө дээрлик {ServerMaxDays} суткага жакын (~{left} күн калды): кийин сервер аны өзү кабыл албайт. Азыр жөнөтүңүз.",
                        $"The delivery is almost {ServerMaxDays} days old (~{left} d left): after that the server will not take it by itself. Send it now.",
                        $"Mal kabulü neredeyse {ServerMaxDays} günlük (~{left} gün kaldı): sonra sunucu kendiliğinden kabul etmez. Şimdi gönderin.",
                        $"Kirim deyarli {ServerMaxDays} kunlik (~{left} kun qoldi): keyin server uni o'zi qabul qilmaydi. Hozir yuboring.");
            case OutboxDeadlineLevel.Expired:
                return sale
                    ? Tr.T($"Не принято по сроку: чеку больше {ServerMaxDays} суток, сервер автоматически его не берёт. Что делать: владелец проводит чек по PIN — кнопка «Некорректные чеки» → «Провести по PIN владельца» (дата останется настоящей). Чек и деньги сохранены.",
                        $"Мөөнөтү боюнча кабыл алынган жок: чекке {ServerMaxDays} суткадан ашты, сервер аны автоматтык албайт. Эмне кылуу: ээси чекти PIN менен өткөрөт — «Туура эмес чектер» → «Ээсинин PIN-коду менен өткөрүү» (дата чыныгы бойдон калат). Чек жана акча сакталды.",
                        $"Not accepted by the deadline: the receipt is over {ServerMaxDays} days old, the server does not take it automatically. What to do: the owner posts it with a PIN — “Irregular receipts” → “Process with owner PIN” (the real date is kept). The receipt and the money are saved.",
                        $"Süresi nedeniyle kabul edilmedi: fiş {ServerMaxDays} günden eski, sunucu bunu otomatik almaz. Ne yapmalı: sahip PIN ile işler — «Hatalı fişler» → «Sahip PIN'iyle işle» (tarih gerçek kalır). Fiş ve para saklandı.",
                        $"Muddati bo'yicha qabul qilinmadi: chek {ServerMaxDays} kundan eski, server uni avtomatik olmaydi. Nima qilish kerak: egasi chekni PIN bilan o'tkazadi — «Noto'g'ri cheklar» → «Egasining PIN-kodi bilan o'tkazish» (sana haqiqiy qoladi). Chek va pul saqlandi.")
                    : ReceivingServerMessages.Humanize("offline_too_old");
            case OutboxDeadlineLevel.TooLate:
                return Tr.T($"Больше {ServerLateMaxDays} суток: сервер не принимает даже с PIN. Проведите вручную на сайте с настоящей датой (владелец/администратор) и уберите запись из очереди.",
                    $"{ServerLateMaxDays} суткадан ашты: сервер PIN менен да кабыл албайт. Сайтта чыныгы датасы менен кол менен өткөрүп (ээси/администратор), жазууну кезектен алып салыңыз.",
                    $"Over {ServerLateMaxDays} days: the server does not accept it even with a PIN. Post it by hand on the website with the real date (owner/administrator) and remove the item from the queue.",
                    $"{ServerLateMaxDays} günden fazla: sunucu PIN ile bile kabul etmez. Siteden gerçek tarihle elle işleyin (sahip/yönetici) ve kaydı kuyruktan kaldırın.",
                    $"{ServerLateMaxDays} kundan ortiq: server PIN bilan ham qabul qilmaydi. Saytda haqiqiy sana bilan qo'lda o'tkazing (egasi/administrator) va yozuvni navbatdan olib tashlang.");
            default:
                return null;
        }
    }

    /// <summary>«Что будет, если не отправить» — коротко, по виду записи.</summary>
    public static string IfNotSent(OutboxKind kind) => kind switch
    {
        OutboxKind.Sale => Tr.T("Если не отправить: продажа не попадёт в отчёты и кассу сервера, остаток на сервере не уменьшится, а через 7 суток сервер сам чек не примет.",
            "Жөнөтпөсөңүз: сатуу сервердин отчетторуна жана кассасына түшпөйт, серверде калдык азаймайт, 7 суткадан кийин сервер чекти өзү кабыл албайт.",
            "If not sent: the sale will not reach the server's reports and till, server stock will not decrease, and after 7 days the server will not take the receipt by itself.",
            "Gönderilmezse: satış sunucunun raporlarına ve kasasına girmez, sunucudaki stok azalmaz, 7 gün sonra sunucu fişi kendiliğinden kabul etmez.",
            "Yubormasangiz: sotuv serverning hisobotlari va kassasiga tushmaydi, serverda qoldiq kamaymaydi, 7 kundan keyin server chekni o'zi qabul qilmaydi."),
        OutboxKind.Return => Tr.T("Если не отправить: возврат не отразится на сервере — деньги выданы, а остаток и отчёты прежние.",
            "Жөнөтпөсөңүз: кайтаруу серверде чагылбайт — акча берилди, ал эми калдык жана отчеттор мурдагыдай.",
            "If not sent: the return will not show on the server — the money is paid out, but stock and reports stay the same.",
            "Gönderilmezse: iade sunucuda görünmez — para verildi ama stok ve raporlar aynı kalır.",
            "Yubormasangiz: qaytarish serverda ko'rinmaydi — pul berildi, qoldiq va hisobotlar o'zgarmaydi."),
        OutboxKind.DebtPayment => Tr.T("Если не отправить: долг клиента на сервере останется прежним, хотя деньги получены.",
            "Жөнөтпөсөңүз: акча алынганына карабай серверде кардардын карызы мурдагыдай калат.",
            "If not sent: the client's debt on the server stays the same although the money was received.",
            "Gönderilmezse: para alındığı halde müşterinin borcu sunucuda aynı kalır.",
            "Yubormasangiz: pul olinganiga qaramay serverda mijozning qarzi o'zgarmaydi."),
        OutboxKind.Receiving => Tr.T("Если не отправить: товар не прибавится на сервере, долг поставщику не запишется; через 7 суток сервер приход сам не примет.",
            "Жөнөтпөсөңүз: товар серверде кошулбайт, жеткирүүчүгө карыз жазылбайт; 7 суткадан кийин сервер кирүүнү өзү кабыл албайт.",
            "If not sent: the goods will not be added on the server and the supplier debt will not be recorded; after 7 days the server will not take the delivery by itself.",
            "Gönderilmezse: ürünler sunucuda eklenmez, tedarikçi borcu yazılmaz; 7 gün sonra sunucu mal kabulünü kendiliğinden almaz.",
            "Yubormasangiz: tovar serverda qo'shilmaydi, yetkazib beruvchiga qarz yozilmaydi; 7 kundan keyin server kirimni o'zi qabul qilmaydi."),
        OutboxKind.NewProduct => Tr.T("Если не отправить: товар останется только в этой кассе — на сайте, в мобилке и на других кассах его не будет.",
            "Жөнөтпөсөңүз: товар ушул кассада гана калат — сайтта, мобилкада жана башка кассаларда болбойт.",
            "If not sent: the product stays only in this till — it will not be on the website, in the mobile app or on other tills.",
            "Gönderilmezse: ürün yalnızca bu kasada kalır — sitede, mobilde ve diğer kasalarda olmaz.",
            "Yubormasangiz: tovar faqat shu kassada qoladi — saytda, mobilda va boshqa kassalarda bo'lmaydi."),
        OutboxKind.ShiftClose => Tr.T("Если не отправить: смена на сервере останется открытой, итог дня не сойдётся.",
            "Жөнөтпөсөңүз: смена серверде ачык калат, күндүн жыйынтыгы дал келбейт.",
            "If not sent: the shift stays open on the server and the day's total will not match.",
            "Gönderilmezse: vardiya sunucuda açık kalır, gün sonucu tutmaz.",
            "Yubormasangiz: smena serverda ochiq qoladi, kun yakuni mos kelmaydi."),
        OutboxKind.CashOperation => Tr.T("Если не отправить: внесение/изъятие не попадёт в кассу смены на сервере — остаток денег в отчёте будет неверным.",
            "Жөнөтпөсөңүз: киргизүү/алуу серверде смена кассасына түшпөйт — отчеттогу акча калдыгы туура болбойт.",
            "If not sent: the cash in/out will not reach the shift cash on the server — the money balance in the report will be wrong.",
            "Gönderilmezse: para girişi/çıkışı sunucudaki vardiya kasasına girmez — rapordaki para bakiyesi yanlış olur.",
            "Yubormasangiz: kiritish/olish serverdagi smena kassasiga tushmaydi — hisobotdagi pul qoldig'i noto'g'ri bo'ladi."),
        OutboxKind.StockWriteOff => Tr.T("Если не отправить: остаток на сервере не уменьшится — на сайте и других кассах товар будет числиться на складе.",
            "Жөнөтпөсөңүз: серверде калдык азаймайт — сайтта жана башка кассаларда товар кампада бар деп эсептелет.",
            "If not sent: the server stock will not decrease — the website and other tills will still show the goods in stock.",
            "Gönderilmezse: sunucudaki stok azalmaz — sitede ve diğer kasalarda ürün stokta görünür.",
            "Yubormasangiz: serverda qoldiq kamaymaydi — saytda va boshqa kassalarda tovar omborda turadi."),
        OutboxKind.StockTransfer => Tr.T("Если не отправить: товар на сервере не переедет между складами/филиалами — остатки там и там будут неверными.",
            "Жөнөтпөсөңүз: товар серверде кампалар/филиалдар арасында жылбайт — калдыктар эки жерде тең туура болбойт.",
            "If not sent: the goods will not move between warehouses/branches on the server — stock will be wrong in both places.",
            "Gönderilmezse: ürünler sunucuda depolar/şubeler arasında taşınmaz — stoklar iki yerde de yanlış olur.",
            "Yubormasangiz: tovar serverda omborlar/filiallar orasida ko'chmaydi — qoldiqlar ikkala joyda ham noto'g'ri bo'ladi."),
        OutboxKind.Revision => Tr.T("Если не отправить: пересчёт склада не попадёт на сервер — остатки останутся прежними. Посчитанное хранится на этой кассе.",
            "Жөнөтпөсөңүз: кампаны кайра эсептөө серверге түшпөйт — калдыктар мурдагыдай калат. Эсептелгени ушул кассада сакталат.",
            "If not sent: the stocktake will not reach the server — stock stays as it was. The counted data is kept on this till.",
            "Gönderilmezse: sayım sunucuya ulaşmaz — stok aynı kalır. Sayılanlar bu kasada saklanır.",
            "Yubormasangiz: ombor qayta sanog'i serverga tushmaydi — qoldiqlar o'zgarmaydi. Sanalgani shu kassada saqlanadi."),
        _ => Tr.T("Если не отправить: запись останется только на этой кассе.", "Жөнөтпөсөңүз: жазуу ушул кассада гана калат.",
            "If not sent: the item stays only on this till.", "Gönderilmezse: kayıt yalnızca bu kasada kalır.", "Yubormasangiz: yozuv faqat shu kassada qoladi."),
    };

    /// <summary>Причина ожидания: что сказал источник; иначе — по состоянию связи.</summary>
    public static string WhyWaiting(OutboxItem item, bool noConnection)
    {
        if (!string.IsNullOrWhiteSpace(item.Reason))
            return item.Reason!;
        if (item.Status == OutboxStatus.Sending)
            return Tr.T("Отправляется прямо сейчас.", "Дал азыр жөнөтүлүүдө.", "Being sent right now.", "Şu anda gönderiliyor.", "Hozir yuborilmoqda.");
        return noConnection
            ? Tr.T("Нет связи с сервером — уйдёт сама, когда связь появится.", "Сервер менен байланыш жок — байланыш пайда болгондо өзү кетет.", "No connection to the server — it will go by itself when the connection returns.",
                "Sunucuyla bağlantı yok — bağlantı gelince kendiliğinden gider.", "Server bilan aloqa yo'q — aloqa paydo bo'lganda o'zi ketadi.")
            : Tr.T("Ждёт своей очереди — уйдёт автоматически в ближайшую минуту.", "Кезегин күтүүдө — жакынкы мүнөттө автоматтык жөнөтүлөт.", "Waiting its turn — it will go automatically within a minute.",
                "Sırasını bekliyor — bir dakika içinde otomatik gider.", "Navbatini kutmoqda — bir daqiqa ichida avtomatik ketadi.");
    }

    public static OutboxSummary Summarize(IReadOnlyList<OutboxItem> items, DateTimeOffset now)
    {
        decimal money = 0;
        int sales = 0, soon = 0, expired = 0;
        DateTimeOffset? oldest = null;
        foreach (var i in items)
        {
            if (i.Kind == OutboxKind.Sale)
            {
                sales++;
                money += i.Amount ?? 0m;
            }
            if (oldest is null || i.CreatedAt < oldest)
                oldest = i.CreatedAt;
            switch (Deadline(i, now))
            {
                case OutboxDeadlineLevel.Soon: soon++; break;
                case OutboxDeadlineLevel.Expired or OutboxDeadlineLevel.TooLate: expired++; break;
            }
        }
        return new OutboxSummary(items.Count, items.Count(i => i.Status == OutboxStatus.Error), money, sales, oldest, soon, expired);
    }

    /// <summary>Строка «В очереди чеков: N на сумму X; самая старая запись — Y» и предупреждения о сроке. Пусто — выводить нечего.</summary>
    public static string SummaryText(OutboxSummary s, DateTimeOffset now, Func<decimal, string> money)
    {
        if (s.Count == 0)
            return "";
        var parts = new List<string>();
        if (s.SalesCount > 0)
            parts.Add(Tr.T($"Чеков в очереди: {s.SalesCount} на {money(s.SalesMoney)}", $"Кезекте чек: {s.SalesCount}, {money(s.SalesMoney)}", $"Receipts queued: {s.SalesCount} worth {money(s.SalesMoney)}",
                $"Kuyrukta fiş: {s.SalesCount}, {money(s.SalesMoney)}", $"Navbatda chek: {s.SalesCount}, {money(s.SalesMoney)}"));
        if (s.Oldest is { } o)
            parts.Add(Tr.T("самая старая запись — ", "эң эски жазуу — ", "oldest item — ", "en eski kayıt — ", "eng eski yozuv — ") + AgeText(o, now));
        return string.Join("; ", parts);
    }

    /// <summary>Предупреждение шапки: сколько записей подходят к 7 суткам или уже не приняты по сроку. null — всё в порядке.</summary>
    public static string? DeadlineBanner(OutboxSummary s)
    {
        if (s.Expired > 0)
            return Tr.T($"Не принято по сроку: {s.Expired} зап. старше {ServerMaxDays} суток — сервер сам их не берёт (см. действие в карточке).",
                $"Мөөнөтү боюнча кабыл алынган жок: {ServerMaxDays} суткадан эски {s.Expired} жазуу — сервер аларды өзү албайт (карточкадагы аракетти караңыз).",
                $"Not accepted by the deadline: {s.Expired} item(s) older than {ServerMaxDays} days — the server does not take them by itself (see the action on the card).",
                $"Süresi nedeniyle kabul edilmedi: {ServerMaxDays} günden eski {s.Expired} kayıt — sunucu bunları kendiliğinden almaz (karttaki işleme bakın).",
                $"Muddati bo'yicha qabul qilinmadi: {ServerMaxDays} kundan eski {s.Expired} ta yozuv — server ularni o'zi olmaydi (kartochkadagi amalga qarang).");
        if (s.Soon > 0)
            return Tr.T($"Скоро {ServerMaxDays} суток: {s.Soon} зап. подходят к пределу сервера. Отправьте их сейчас («Отправить всё»).",
                $"Жакында {ServerMaxDays} сутка: {s.Soon} жазуу сервердин чегине жакындады. Азыр жөнөтүңүз («Баарын жөнөтүү»).",
                $"{ServerMaxDays}-day limit is close: {s.Soon} item(s) near the server limit. Send them now (“Send all”).",
                $"{ServerMaxDays} günlük sınır yakın: {s.Soon} kayıt sunucu sınırına yaklaştı. Şimdi gönderin («Hepsini gönder»).",
                $"{ServerMaxDays} kunlik chegara yaqin: {s.Soon} ta yozuv server chegarasiga yaqinlashdi. Hozir yuboring («Hammasini yuborish»).");
        return null;
    }

    /// <summary>Совет при долгом офлайне: нет связи дольше <paramref name="thresholdHours"/> часов или есть неотправленное старше этого порога. outageSince — с какого момента сервер не отвечает (null — связь есть).</summary>
    public static LongOfflineAdvice EvaluateLongOffline(DateTimeOffset? outageSince, OutboxSummary s, DateTimeOffset now, int thresholdHours = LongOfflineHours)
    {
        var offlineFor = outageSince is { } since ? now - since : TimeSpan.Zero;
        var threshold = TimeSpan.FromHours(Math.Max(1, thresholdHours));
        var queueOld = s.Oldest is { } o && s.Count > 0 && now - o > threshold;
        var offlineLong = outageSince is not null && offlineFor > threshold;
        if (!offlineLong && !queueOld)
            return new LongOfflineAdvice(0, "");

        var urgent = s.Expired > 0 || s.Soon > 0 || offlineFor > TimeSpan.FromDays(ServerMaxDays - 2);
        var hours = (int)Math.Max(offlineFor.TotalHours, s.Oldest is { } old ? (now - old).TotalHours : 0);
        string head = offlineLong
            ? Tr.T($"Нет связи уже {hours} ч.", $"Байланыш жок болгонуна {hours} саат.", $"No connection for {hours} h.", $"{hours} saattir bağlantı yok.", $"{hours} soatdan beri aloqa yo'q.")
            : Tr.T($"Есть неотправленное старше {LongOfflineHours} ч ({s.Count} зап.).", $"{LongOfflineHours} сааттан эски жөнөтүлбөгөн жазуулар бар ({s.Count}).", $"Unsent items older than {LongOfflineHours} h ({s.Count}).",
                $"{LongOfflineHours} saatten eski gönderilmemiş kayıt var ({s.Count}).", $"{LongOfflineHours} soatdan eski yuborilmagan yozuvlar bor ({s.Count}).");
        var advice = Tr.T(
            " Продажи сохраняются на этой кассе и уйдут сами. Проверьте интернет/роутер; не очищайте данные кассы и не переустанавливайте программу. Если связи не будет — запишите чеки на бумаге и не позже 7 суток отправьте очередь (после — только через владельца).",
            " Сатуулар ушул кассада сакталат жана өзү жөнөтүлөт. Интернетти/роутерди текшериңиз; касса маалыматтарын тазалабаңыз жана программаны кайра орнотпоңуз. Байланыш болбосо — чектерди кагазга жазыңыз жана 7 суткадан кечикпей кезекти жөнөтүңүз (кийин — ээси аркылуу гана).",
            " Sales are saved on this till and will go by themselves. Check the internet/router; do not clear the till data or reinstall the program. If there is no connection — note receipts on paper and send the queue within 7 days (after that — only through the owner).",
            " Satışlar bu kasada saklanır ve kendiliğinden gider. İnterneti/modemi kontrol edin; kasa verilerini silmeyin, programı yeniden kurmayın. Bağlantı gelmezse — fişleri kâğıda yazın ve kuyruğu en geç 7 gün içinde gönderin (sonra yalnızca sahip aracılığıyla).",
            " Sotuvlar shu kassada saqlanadi va o'zi ketadi. Internet/routerni tekshiring; kassa ma'lumotlarini tozalamang va dasturni qayta o'rnatmang. Aloqa bo'lmasa — cheklarni qog'ozga yozing va navbatni 7 kundan kechiktirmay yuboring (keyin — faqat egasi orqali).");
        return new LongOfflineAdvice(urgent ? 2 : 1, head + advice);
    }

    /// <summary>Деньги в одну строку для текстов без UI (тесты и журнал).</summary>
    public static string Plain(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
}
