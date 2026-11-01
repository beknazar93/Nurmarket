using System.Diagnostics;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-28, BE-11: досылка офлайн-чека одним запросом POST api/main/pos/checkout/ с
/// Idempotency-Key = id записи очереди. Старая досылка (sales/start → позиции → checkout)
/// защищалась от двойного чека сверкой корзины и при «не удалось проверить» откладывала чек;
/// с ключом повтор безопасен сам по себе: проведённую продажу сервер вернёт ещё раз (200,
/// "replayed": true), а не создаст вторую.
/// </summary>
public sealed partial class SyncService
{
    /// <summary>Handled=false — чек идёт старым путём (быстрый выключен, чек ему не подходит или
    /// сервер отказал по существу 4xx — значит, продажи с этим ключом нет). Ошибки связи и 5xx
    /// пробрасываются как есть: SyncBatchAsync оставит чек в очереди, следующий цикл пошлёт тот же ключ.</summary>
    private async Task<(bool Handled, string? SaleId)> TryQuickReplayAsync(OfflineSaleEntry entry, CancellationToken ct)
    {
        // Чек, который старый путь уже начал проводить (есть корзина/отметка отправки), доводит
        // старый путь — у него своя сверка по корзине.
        if (entry.CheckoutCompleted
            || entry.CheckoutSubmittedAt is not null
            || !string.IsNullOrWhiteSpace(entry.SyncCartId))
            return (false, null);

        // Если чек уже уходил этим адресом (ответ мог потеряться), дослать его можно только им же —
        // даже при выключенном в настройках быстром пути.
        if (!entry.QuickCheckoutAttempted && !UserPreferences.Instance.QuickCheckoutEnabled)
            return (false, null);

        if (_sales is not IPosQuickCheckoutApi quickApi)
            return (false, null);

        // 2026-10-06: сбор тела вынесен в BuildQuickReplayBody — им же пользуется пакетная досылка (SyncService.BatchReplay.cs).
        var body = BuildQuickReplayBody(entry);
        if (body == null)
            return (false, null);

        // 2026-10-29 (аварийный аудит, п. 3): ключ чека мог уйти на сервер раньше (оплата упала по таймауту/500 или прошлая досылка не получила ответа).
        var keyMaybeSent = entry.QuickCheckoutAttempted;

        // Отметка ДО запроса и переживает перезапуск: следующий цикл пошлёт тот же ключ.
        OfflinePendingSalesStore.Update(entry.Id, e => e.QuickCheckoutAttempted = true);

        var sw = Stopwatch.StartNew();
        JsonElement response = default;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                response = await quickApi.PosQuickCheckoutAsync(body, entry.Id, TimeSpan.FromSeconds(45), ct)
                    .ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // SyncBatchAsync ловит именно TaskCanceledException: чек остаётся в очереди.
                throw new TaskCanceledException("Таймаут досылки чека.");
            }
            catch (ApiException ex) when (ex.StatusCode == 500 && attempt == 1)
            {
                // Один повтор тем же ключом: прошедшую продажу он вернёт как повтор (200).
                PosLogger.Log($"OFFLINE replay quick: чек {entry.Id} — 500, повтор тем же ключом.", "OFFLINE");
            }
            // 2026-10-04: 409/423/425 — «запрос с этим ключом ещё выполняется» (первая отправка ещё
            // обрабатывается сервером): это не отказ по существу, старым путём идти нельзя — он создал
            // бы вторую продажу. Такие ответы, как и 5xx, кроме 500, уходят в SyncBatchAsync — чек
            // остаётся в очереди и досылается тем же ключом.
            // 2026-11-01 (К-06, аудит «10 из 10»): сервер отказал «чек старше 7 суток» (400 offline_too_old / offline_created_at). Раньше такой чек уходил
            // старым путём БЕЗ даты продажи, и CRM записывала его сегодняшним числом (16 чеков 31.10): выручка не в тот день и не в ту смену, Z-отчёт
            // сегодняшней смены искажён. Теперь чек не проводится «сегодняшним» молча: он остаётся в кассе со статусом «Требует проверки» и понятной
            // причиной, владельцу уходит оповещение. Продажи с этим ключом на сервере нет (отказ до записи), потерь и дублей нет.
            catch (ApiException ex) when (IsOfflineTooOld(ex, entry))
            {
                PosLogger.Log($"OFFLINE replay quick: чек {entry.Id} от {entry.CreatedAt:yyyy-MM-dd HH:mm} старше срока сервера — {ex.Message}. На проверку (без даты не проводим).", "WARNING");
                OfflinePendingSalesStore.Update(entry.Id, e => { e.ReviewNeeded = true; e.TooOldForServer = true; }); // 2026-11-01: признак для кнопки «Провести по PIN владельца»
                throw new ReplayDeferredException(TooOldReviewText(entry)) { NeedsReview = true };
            }
            // 2026-10-29 (аварийный аудит, п. 3): второй 500 подряд и 4xx на ключ, который МОГ уже дойти до сервера (keyMaybeSent), больше не
            // отправляют чек старым путём без ключа — сервер мог записать продажу до сбоя, и старый путь создал бы дубль. Исключение уходит
            // в SyncBatchAsync: 500 — чек остаётся в очереди и досылается тем же ключом, 4xx — «Некорректные чеки» (решает человек).
            // Старый путь остаётся только для чека, чей ключ ещё ни разу не отправлялся.
            catch (ApiException ex) when (keyMaybeSent == false
                                          && ex.StatusCode is >= 400 and < 500
                                          && !PosCheckoutService.IsQuickTransientStatus(ex.StatusCode))
            {
                // Продажи с этим ключом нет (иначе был бы 200-повтор) — пробуем старым путём.
                PosLogger.Log(
                    $"OFFLINE replay quick: чек {entry.Id} — отказ {ex.StatusCode}: {ex.Message} — старый путь.",
                    "OFFLINE");
                return (false, null);
            }
        }

        var saleId = CheckoutResponseHelper.TrySaleId(response);
        var replayed = response.ValueKind == JsonValueKind.Object
                       && response.TryGetProperty("replayed", out var rp) && rp.ValueKind == JsonValueKind.True;
        OfflinePendingSalesStore.Update(entry.Id, e =>
        {
            e.CheckoutCompleted = true;
            if (!string.IsNullOrWhiteSpace(saleId))
                e.SyncedSaleId = saleId;
        });
        PosLogger.Log(
            $"OFFLINE replay quick: чек {entry.Id} проведён за {sw.ElapsedMilliseconds} мс, продажа {saleId ?? "—"}, replayed={replayed}.",
            "OFFLINE");

        await ResyncStockAfterReplayAsync(entry, ct).ConfigureAwait(false);
        return (true, saleId);
    }

    /// <summary>2026-11-01 (К-06): максимальный возраст офлайн-продажи, которую сервер принимает без подтверждения владельца (max_days в ответе Умара 31.10).</summary>
    internal static readonly TimeSpan ServerOfflineMaxAge = TimeSpan.FromDays(7);

    /// <summary>2026-11-01 (К-06): отказ «продажа старше 7 суток» (400, code offline_too_old; у старой версии сервера — текст про offline_created_at).</summary>
    internal static bool IsOfflineTooOld(ApiException ex, OfflineSaleEntry entry)
    {
        if (ex.StatusCode != 400)
            return false;
        string text;
        try
        {
            text = ((ex.Payload is { } p ? p.GetRawText() : "") + " " + ex.Message).ToLowerInvariant();
        }
        catch (InvalidOperationException)
        {
            text = ex.Message.ToLowerInvariant();
        }

        // 2026-11-01 (приёмка, НИЗКАЯ-5): код offline_too_old — однозначно; слабый признак offline_created_at (старая версия сервера, ошибка поля) — только если чек и вправду старше ~6 суток.
        return text.Contains("offline_too_old")
               || (text.Contains("offline_created_at") && DateTimeOffset.Now - entry.CreatedAt > ServerOfflineMaxAge - TimeSpan.FromDays(1));
    }

    /// <summary>2026-11-01 (К-06): причина для кассира и владельца — чек «Требует проверки», а не проведённый сегодняшним числом.</summary>
    private static string TooOldReviewText(OfflineSaleEntry entry)
    {
        var date = entry.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
        return Tr.T(
            $"Требует проверки: чек от {date} старше 7 суток, сервер не принимает такие продажи автоматически. Провести его «сегодняшним числом» нельзя — выручка попала бы не в тот день и не в ту смену. Чек и деньги сохранены. Проведите продажу на сайте с нужной датой (владелец/администратор) и закройте этот чек кнопкой «Закрыть без отправки».",
            $"Текшерүү керек: {date} чеги 7 суткадан эски, сервер мындай сатууларды автоматтык кабыл албайт. Аны «бүгүнкү күн менен» өткөрүүгө болбойт — түшүм башка күнгө жана башка сменага түшөт. Чек жана акча сакталды. Сатууну сайтта керектүү датасы менен өткөрүңүз (ээси/администратор) жана бул чекти «Жөнөтпөй жабуу» баскычы менен жабыңыз.",
            $"Needs review: the receipt from {date} is older than 7 days and the server does not accept such sales automatically. It cannot be posted “with today's date” — the revenue would land on the wrong day and in the wrong shift. The receipt and the money are kept. Post the sale on the website with the right date (owner/administrator) and close this receipt with “Close without sending”.",
            $"Kontrol gerekli: {date} tarihli fiş 7 günden eski, sunucu bu tür satışları otomatik kabul etmiyor. «Bugünün tarihiyle» işlenemez — gelir yanlış güne ve yanlış vardiyaya düşer. Fiş ve para saklandı. Satışı sitede doğru tarihle işleyin (sahip/yönetici) ve bu fişi «Göndermeden kapat» ile kapatın.",
            $"Tekshirish kerak: {date} dagi chek 7 kundan eski, server bunday sotuvlarni avtomatik qabul qilmaydi. Uni «bugungi sana bilan» o'tkazib bo'lmaydi — tushum boshqa kunga va boshqa smenaga tushadi. Chek va pul saqlandi. Sotuvni saytda kerakli sana bilan o'tkazing (egasi/administrator) va bu chekni «Yubormasdan yopish» tugmasi bilan yoping.");
    }

    /// <summary>2026-11-01 (К-06): чек, который быстрый путь не взял, старше срока сервера — старым путём (он не передаёт sold_at) не проводим.</summary>
    private static bool IsTooOldForDatelessReplay(OfflineSaleEntry entry) =>
        DateTimeOffset.Now - entry.CreatedAt > ServerOfflineMaxAge
        && entry.CheckoutSubmittedAt is null
        && string.IsNullOrWhiteSpace(entry.SyncCartId);

    /// <summary>Тело pos/checkout/ для чека очереди — для одиночной и пакетной досылки. null — чек этому пути не подходит.</summary>
    internal static System.Text.Json.Nodes.JsonObject? BuildQuickReplayBody(OfflineSaleEntry entry)
    {
        // Смена, в которой чек пробит (см. комментарий в SubmitReplayCheckoutAsync): «offline-…»
        // сервер не знает — тогда без смены, сервер возьмёт открытую, как и старый путь.
        var shiftId = !string.IsNullOrWhiteSpace(entry.ShiftId)
                      && !entry.ShiftId!.StartsWith("offline-", StringComparison.OrdinalIgnoreCase)
            ? entry.ShiftId
            : null;

        var body = QuickCheckoutBody.TryBuild(
            entry.CartJson,
            entry.PaymentMethod,
            entry.CashReceived,
            nonCashReceived: null,
            clientId: entry.ClientId, // 2026-10-27 (Н-6): клиент офлайн-чека не теряется при досылке
            shiftId,
            entry.ConsultantId,
            entry.ConsultantCommissionEnabled,
            entry.ConsultantCommissionPercent,
            printReceipt: false,
            out var unsupported);
        if (body == null)
        {
            PosLogger.Log($"OFFLINE replay quick: чек {entry.Id} не подходит ({unsupported}) — старый путь.", "OFFLINE");
            return null;
        }

        // 2026-10-05, ТЗ ч.12, п. 2.4 (сервер выложил 05.10): время продажи и касса. Сервер относит чек к смене этой кассы,
        // открытой в момент продажи, даже если она уже закрыта (раньше — 400 «Смена не открыта», деньги получены, а продажи
        // на сервере нет). Проверено 05.10: sold_at в закрытой смене → 201 в ту смену, повтор тем же ключом → 200 replayed.
        body["sold_at"] = entry.CreatedAt.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
        if (Guid.TryParse(entry.CashboxId, out _))
            body["cashbox"] = entry.CashboxId!.Trim();
        return body;
    }
}
