using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Состояние чека относительно «позднего проведения по PIN владельца».</summary>
public enum LateAcceptState
{
    /// <summary>Чек не про это (не отклонён «старше 7 суток», уже проведён, автономный).</summary>
    NotApplicable,

    /// <summary>Старше 7 суток, не старше 90, сервер ЯВНО отказал и отправка гарантированно не начиналась — можно провести по PIN владельца.</summary>
    Allowed,

    /// <summary>2026-11-01 (ревью): отправка чека могла начаться (старый путь cart/checkout, пакет, потерян ответ) — на сервере он, возможно, УЖЕ есть. Повторная отправка создала бы вторую продажу: только сверка в «Чеках» на сервере и «Закрыть без отправки».</summary>
    MaybePosted,

    /// <summary>Старше 90 суток — сервер не примет ни при каком PIN: только «Закрыть без отправки».</summary>
    TooOld,
}

// 2026-11-06 (аудит №5, Л5-06): Unavailable — проверить PIN НЕЛЬЗЯ из-за отсутствия связи/тайм-аута (единственный случай, когда закрытие смены с недостачей и ревизию
// не блокируем); Error — сервер ответил отказом/ошибкой (4xx, «нет прав», 5xx, непонятный ответ) — это ОТКАЗ, обхода нет.
public enum LatePinStatus { Ok, Wrong, Locked, Error, Unavailable }

// 2026-11-19: ExpiresInSeconds — срок токена (expires_in, сервер даёт 300 с); TokenRejected — сервер не принял подтверждение (токен просрочен/нет прав): нужно ввести PIN заново;
// SentWithWarnings — чек принят, но сервер предупредил (ниже закупки / ушло в минус) — текст в OutboxSentNotes.
public sealed record LatePinResult(LatePinStatus Status, string? Token, string Message, int? ExpiresInSeconds = null);

public sealed record LateAcceptResult(bool Ok, string Message, bool TokenRejected = false, bool SentWithWarnings = false);

/// <summary>2026-11-19: итог пакетной отправки одним подтверждением. Remaining — сколько чеков не тронуто (кончился срок токена или он отклонён).</summary>
public sealed record LateAcceptBatchResult(int Sent, int Failed, int Remaining, bool TokenExpired, string Message);

/// <summary>
/// 2026-11-01 (К-06, продолжение): чек из офлайн-очереди старше 7 суток сервер не принимает сам (400 offline_too_old, SyncService.QuickReplay.cs), он остаётся
/// «Требует проверки». Здесь — явное проведение владельцем: PIN → X-Confirm-Token → тот же запрос pos/checkout/ с НАСТОЯЩЕЙ датой продажи (sold_at из
/// даты чека) и "accept_late": true, тем же ключом идемпотентности (повтор не задвоит продажу). Контракт — ответ Умара 31.10 (c8958b4), раздел 2.
/// Кнопка в «Нестандартных чеках» включается признаком UserPreferences.AcceptLateEnabled (по умолчанию false) — пока Умар не подтвердит поле токена.
/// </summary>
public sealed partial class SyncService
{
    /// <summary>late_max_days из ответа сервера (offline_too_old.data): старше — не проводится никак. Постоянная до подтверждения Умаром, что значение не меняется на компанию.</summary>
    internal static readonly TimeSpan LateAcceptMaxAge = TimeSpan.FromDays(90);

    private const int LatePinMaxFailures = 3;
    private const int LatePinLockSeconds = 30;
    private static readonly object LatePinGate = new();
    private static int _latePinFailures;
    private static DateTime _latePinLockedUntilUtc = DateTime.MinValue;

    /// <summary>Кнопка «Провести по PIN владельца» вообще разрешена (признак настроек).</summary>
    // 2026-11-19: включается, когда сервер сообщил признак offline_accept_late (ответ Умара 19.11); ручной AcceptLateEnabled в user-settings.json оставлен для стенда.
    public static bool LateAcceptEnabled => UserPreferences.Instance.AcceptLateEnabled || ServerCapabilities.Has(ServerCapabilities.OfflineAcceptLate);

    public static LateAcceptState GetLateAcceptState(OfflineSaleEntry entry)
    {
        if (entry.IsAutonomous || entry.CheckoutCompleted
            || !string.Equals(entry.Status, OfflineSaleEntry.Failed, StringComparison.OrdinalIgnoreCase))
            return LateAcceptState.NotApplicable;
        var age = DateTimeOffset.Now - entry.CreatedAt;
        // Отметка TooOldForServer ставится при отказе сервера; у чеков, отклонённых до этой правки, признака нет — берём «Требует проверки» старше 7 суток (не «в долг»).
        var candidate = entry.TooOldForServer || (entry.ReviewNeeded && !entry.OfflineDebt && age > ServerOfflineMaxAge);
        if (!candidate)
            return LateAcceptState.NotApplicable;
        // 2026-11-01 (ревью): любой признак «отправка начиналась» (старый путь: CheckoutSubmittedAt/SyncCartId; пакет) — чек мог уже пройти на сервере под другим ключом.
        // Без отметки TooOldForServer (явный отказ сервера по ключу чека) отказ не подтверждён — тоже «возможно проведён».
        if (entry.CheckoutSubmittedAt is not null
            || !string.IsNullOrWhiteSpace(entry.SyncCartId)
            || entry.BatchCheckoutAttempted
            || !entry.TooOldForServer)
            return LateAcceptState.MaybePosted;
        return age > LateAcceptMaxAge ? LateAcceptState.TooOld : LateAcceptState.Allowed;
    }

    /// <summary>Только для тестов: сбросить локальную паузу и счётчик неверных PIN (общие на программу).</summary>
    internal static void ResetLatePinLockForTests()
    {
        lock (LatePinGate)
        {
            _latePinFailures = 0;
            _latePinLockedUntilUtc = DateTime.MinValue;
        }
    }

    /// <summary>Секунд до конца паузы после 3 неверных PIN (0 — можно вводить).</summary>
    public static int LatePinLockSecondsLeft
    {
        get
        {
            lock (LatePinGate)
            {
                var left = (_latePinLockedUntilUtc - DateTime.UtcNow).TotalSeconds;
                return left > 0 ? (int)Math.Ceiling(left) : 0;
            }
        }
    }

    /// <summary>2026-11-06 (аудит №5, Л5-06): сбой именно связи (нет сети, тайм-аут), а не ответ сервера. 4xx/401/403/5xx сюда НЕ входят — это отказ, а не «нет связи».</summary>
    public static bool IsConnectivityFailure(Exception ex) => ex switch
    {
        HttpRequestException or TimeoutException or System.Net.Sockets.SocketException => true,
        OperationCanceledException => true, // тайм-аут запроса (отмену вызывающим перехватывает фильтр выше)
        ApiException api => api.StatusCode is null or 408 or 504,
        _ => ex.InnerException is not null && IsConnectivityFailure(ex.InnerException),
    };

    /// <summary>Проверка PIN владельца на сервере. 3 неверных подряд — пауза 30 с. PIN нигде не пишется (ни в журнал, ни на диск).</summary>
    public async Task<LatePinResult> VerifyOwnerPinForLateAsync(string pin, CancellationToken ct = default)
    {
        var locked = LatePinLockSecondsLeft;
        if (locked > 0)
            return new LatePinResult(LatePinStatus.Locked, null, LatePinLockedText(locked));
        if (_sales is not IPosLateAcceptApi api)
            return new LatePinResult(LatePinStatus.Error, null, NoApiText());

        OwnerPinResult result;
        try
        {
            result = await api.VerifyOwnerPinAsync(pin, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (IsConnectivityFailure(ex))
        {
            // 2026-11-06 (аудит №5, Л5-06): только настоящий обрыв связи/тайм-аут = «PIN не проверен — нет связи». Остальные ошибки сервера — отказ (см. ниже).
            return new LatePinResult(LatePinStatus.Unavailable, null, NoConnectionText());
        }
        catch (ApiException ex) when (ex.StatusCode == 429)
        {
            // 2026-11-01: сервер сам блокирует проверку PIN на 15 минут после 5 ошибок подряд (проверка бэкенда 01.11).
            // 2026-11-05 (сервер ceba67c): 429 pin_locked + retry_after (секунды, заголовок Retry-After) — «повторите через N мин»; до конца блокировки новых запросов не шлём.
            PosLogger.Log("OFFLINE late-accept: сервер заблокировал проверку PIN (429).", "WARNING");
            if (ServerErrorCodes.RetryAfterSeconds(ex) is { } retryAfter)
            {
                lock (LatePinGate)
                    _latePinLockedUntilUtc = DateTime.UtcNow.AddSeconds(retryAfter);
                return new LatePinResult(LatePinStatus.Locked, null, ServerErrorCodes.PinLockedText(retryAfter));
            }
            return new LatePinResult(LatePinStatus.Locked, null, Tr.T(
                "Сервер временно заблокировал проверку PIN (слишком много ошибок). Подождите около 15 минут и повторите.",
                "Сервер PIN текшерүүнү убактылуу бөгөттөдү (өтө көп ката). Болжолдуу 15 мүнөт күтүп, кайталаңыз.",
                "The server temporarily blocked PIN checks (too many errors). Wait about 15 minutes and try again.",
                "Sunucu PIN kontrolünü geçici olarak engelledi (çok fazla hata). Yaklaşık 15 dakika bekleyip tekrar deneyin.",
                "Server PIN tekshiruvini vaqtincha bloklab qo'ydi (xatolar juda ko'p). Taxminan 15 daqiqa kutib, qayta urining."));
        }
        catch (ApiException ex)
        {
            PosLogger.Log($"OFFLINE late-accept: проверка PIN владельца — ошибка сервера {ex.StatusCode}.", "WARNING");
            return new LatePinResult(LatePinStatus.Error, null, Tr.T(
                $"Сервер не смог проверить PIN (код {ex.StatusCode}). Повторите позже.",
                $"Сервер PIN текшере алган жок (коду {ex.StatusCode}). Кийинчерээк кайталаңыз.",
                $"The server could not check the PIN (code {ex.StatusCode}). Try again later.",
                $"Sunucu PIN'i kontrol edemedi (kod {ex.StatusCode}). Daha sonra tekrar deneyin.",
                $"Server PIN ni tekshira olmadi (kod {ex.StatusCode}). Keyinroq qayta urining."));
        }

        if (result.Unauthorized)
            return new LatePinResult(LatePinStatus.Error, null, Tr.T(
                "Нет прав на проверку PIN или сессия истекла. Войдите в кассу заново (владелец/администратор) и повторите. Попытка PIN не потрачена.",
                "PIN текшерүүгө укук жок же сессия бүттү. Кассага кайра кириңиз (ээси/администратор) жана кайталаңыз. PIN аракети сарпталган жок.",
                "No rights to check the PIN or the session expired. Sign in again (owner/administrator) and retry. The PIN attempt was not used.",
                "PIN kontrolü için yetki yok veya oturum bitti. Kasaya yeniden girin (sahip/yönetici) ve tekrar deneyin. PIN hakkı harcanmadı.",
                "PIN ni tekshirish huquqi yo'q yoki sessiya tugagan. Kassaga qayta kiring (egasi/administrator) va qayta urining. PIN urinishi sarflanmadi."));

        // 2026-11-19 (ответ Умара 19.11): pin_not_set — PIN владельца/админа не задан. Отказ (обхода нет), попытка не тратится.
        if (result.NotSet)
            return new LatePinResult(LatePinStatus.Error, null, ServerErrorCodes.PinNotSetText());

        if (result.Accepted && !string.IsNullOrWhiteSpace(result.Token))
        {
            lock (LatePinGate)
                _latePinFailures = 0;
            return new LatePinResult(LatePinStatus.Ok, result.Token, "", result.ExpiresIn);
        }

        if (result.Rejected)
        {
            int left;
            lock (LatePinGate)
            {
                _latePinFailures++;
                if (_latePinFailures >= LatePinMaxFailures)
                {
                    _latePinFailures = 0;
                    _latePinLockedUntilUtc = DateTime.UtcNow.AddSeconds(LatePinLockSeconds);
                }
                left = LatePinMaxFailures - _latePinFailures;
            }
            PosLogger.Log("OFFLINE late-accept: неверный PIN владельца.", "WARNING");
            var lockNow = LatePinLockSecondsLeft;
            if (lockNow > 0)
                return new LatePinResult(LatePinStatus.Locked, null, LatePinLockedText(lockNow));
            // 2026-11-05: сервер сказал, сколько попыток осталось (attempts_left), — показываем его число, а не своё.
            if (result.AttemptsLeft is { } serverLeft)
                return new LatePinResult(LatePinStatus.Wrong, null, ServerErrorCodes.PinInvalidText(serverLeft));
            return new LatePinResult(LatePinStatus.Wrong, null, Tr.T(
                $"Неверный PIN. Осталось попыток: {left}.",
                $"PIN туура эмес. Калган аракет: {left}.",
                $"Incorrect PIN. Attempts left: {left}.",
                $"Yanlış PIN. Kalan deneme: {left}.",
                $"PIN noto'g'ri. Qolgan urinishlar: {left}."));
        }

        // PIN принят, но токен в ответе не найден (формат ответа не подтверждён Умаром) — в журнал только имена полей, без значений.
        PosLogger.Log($"OFFLINE late-accept: PIN не подтверждён: явного токена подтверждения в ответе нет (токен сессии не используется) ({result.Error}). Уточнить у Умара поле токена в ответе auth/pin/verify/.", "WARNING");
        return new LatePinResult(LatePinStatus.Error, null, Tr.T(
            "Сервер не подтвердил PIN (нет токена подтверждения в ответе). Чек не проведён. Обратитесь к разработчику (нужно уточнить формат ответа сервера).",
            "Сервер PIN ди ырастаган жок (жоопто ырастоо токени жок). Чек өткөрүлгөн жок. Өнүктүрүүчүгө кайрылыңыз (сервердин жооп форматын тактоо керек).",
            "The server did not confirm the PIN (no confirmation token in the reply). The receipt was not posted. Contact the developer (the server reply format must be clarified).",
            "Sunucu PIN'i onaylamadı (yanıtta onay belirteci yok). Fiş işlenmedi. Geliştiriciye başvurun (sunucu yanıt biçimi netleştirilmeli).",
            "Server PIN ni tasdiqlamadi (javobda tasdiqlash tokeni yo'q). Chek o'tkazilmadi. Dasturchiga murojaat qiling (server javob formatini aniqlash kerak)."));
    }

    /// <summary>Провести чек по подтверждению владельца: настоящая дата продажи (sold_at), accept_late, тот же ключ идемпотентности.
    /// Успех — чек убран из очереди. Отказ — понятное сообщение, чек остаётся «Требует проверки».</summary>
    /// <param name="confirmToken">2026-11-19: null — вошёл владелец/админ, сервер принимает accept_late без X-Confirm-Token (ответ Умара 19.11, п. 3); если он всё же откажет — TokenRejected, касса спросит PIN.</param>
    public async Task<LateAcceptResult> SendLateAcceptedAsync(string entryId, string? confirmToken, string? approvedBy, CancellationToken ct = default)
    {
        if (!LateAcceptEnabled)
            return new LateAcceptResult(false, NoApiText());
        var entry = OfflinePendingSalesStore.TryGetById(entryId);
        if (entry is null)
            return new LateAcceptResult(false, Tr.T("Чек не найден в очереди.", "Чек кезектен табылган жок.", "The receipt was not found in the queue.", "Fiş kuyrukta bulunamadı.", "Chek navbatda topilmadi."));
        var state = GetLateAcceptState(entry);
        if (state == LateAcceptState.TooOld)
            return new LateAcceptResult(false, TooOldSupportText()); // 2026-11-19: «Старше 90 суток — отправить нельзя, обратитесь в поддержку»
        if (state == LateAcceptState.MaybePosted)
            return new LateAcceptResult(false, MaybePostedText());
        if (state != LateAcceptState.Allowed)
            return new LateAcceptResult(false, Tr.T("Этот чек нельзя провести по PIN владельца (он уже проведён или не ждёт проверки).", "Бул чекти ээсинин PIN коду менен өткөрүүгө болбойт (өткөрүлгөн же текшерүүнү күтпөйт).", "This receipt cannot be posted with the owner PIN (already posted or not awaiting review).", "Bu fiş sahip PIN'iyle işlenemez (zaten işlendi veya kontrol beklemiyor).", "Bu chekni egasining PIN i bilan o'tkazib bo'lmaydi (allaqachon o'tkazilgan yoki tekshiruvni kutmayapti)."));
        if (entry.BatchCheckoutAttempted)
            return new LateAcceptResult(false, Tr.T("Чек уходил пакетом, и сервер ещё не дал по нему ответа. Подождите следующей синхронизации и повторите.", "Чек пакет менен кеткен, сервер дагы жооп берген жок. Кийинки шайкештирүүнү күтүп, кайталаңыз.", "The receipt was sent in a batch and the server has not answered yet. Wait for the next sync and retry.", "Fiş toplu gönderildi, sunucu henüz yanıt vermedi. Sonraki senkronizasyonu bekleyip tekrar deneyin.", "Chek paket bilan yuborilgan, server hali javob bermadi. Keyingi sinxronlashni kutib, qayta urining."));
        if (_sales is not IPosLateAcceptApi api)
            return new LateAcceptResult(false, NoApiText());

        var body = BuildQuickReplayBody(entry);
        if (body == null)
            return new LateAcceptResult(false, Tr.T("Этот чек нельзя отправить быстрым способом (нестандартный состав). Проведите его на сайте.", "Бул чекти тез ыкма менен жөнөтүүгө болбойт (стандарттуу эмес курам). Сайтта өткөрүңүз.", "This receipt cannot be sent the fast way (non-standard content). Post it on the website.", "Bu fiş hızlı yöntemle gönderilemez (standart dışı içerik). Sitede işleyin.", "Bu chekni tez usulda yuborib bo'lmaydi (nostandart tarkib). Saytda o'tkazing."));
        // sold_at — настоящая дата продажи (BuildQuickReplayBody); accept_late — явное согласие владельца.
        // TODO(Умар): имя поля accept_late и X-Confirm-Token взяты из ответа 31.10, раздел 2; подтвердить на живом запросе (тест NBS).
        body["accept_late"] = true;

        OfflinePendingSalesStore.Update(entry.Id, e => e.QuickCheckoutAttempted = true);
        JsonElement response;
        try
        {
            response = await api.PosLateCheckoutAsync(body, entry.Id, confirmToken, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // Ответ мог не дойти: чек остаётся, повтор тем же ключом безопасен (PIN понадобится заново — токен живёт 5 минут).
            PosLogger.Log($"OFFLINE late-accept: чек {entry.Id} — нет ответа ({ex.GetType().Name}), чек остаётся в «Требует проверки».", "WARNING");
            return new LateAcceptResult(false, NoConnectionText());
        }
        catch (ApiException ex)
        {
            var text = DescribeLateRefusal(ex, entry);
            // 2026-11-19: 401/403 или повторный offline_too_old у чека моложе 90 суток = сервер не принял подтверждение (токен просрочен, чужой, нет прав) — спросить PIN заново.
            var tokenRejected = ex.StatusCode is 401 or 403 || (IsOfflineTooOld(ex, entry) && DateTimeOffset.Now - entry.CreatedAt <= LateAcceptMaxAge);
            PosLogger.Log($"OFFLINE late-accept: чек {entry.Id} — отказ сервера {ex.StatusCode}: {text}", "WARNING");
            OfflinePendingSalesStore.Update(entry.Id, e => { e.LastError = tokenRejected ? NeedsConfirmText(entry) : text; e.ReviewNeeded = true; });
            return new LateAcceptResult(false, text, TokenRejected: tokenRejected);
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
            $"OFFLINE late-accept: чек {entry.Id} от {entry.CreatedAt:yyyy-MM-dd HH:mm} проведён по PIN владельца ({approvedBy ?? "—"}), продажа {saleId ?? "—"}, replayed={replayed}.",
            "OFFLINE");
        // 2026-11-19 (ответ Умара 19.11, п. 4): чек ниже закупки / с нулевым остатком проводится; warnings below_cost / negative_stock + stock_negative — в журнал и в «Неотправленное».
        var withWarnings = false;
        if (!replayed)
        {
            CheckoutWarnings.HandleReplayResponse(entry.Id, saleId, response);
            withWarnings = OutboxSentNotes.HasNote(entry.Id);
        }
        await ResyncStockAfterReplayAsync(entry, ct).ConfigureAwait(false);
        OfflinePendingSalesStore.MarkSynced(entry.Id, saleId);
        OfflinePendingSalesStore.RemoveSynced(entry.Id);
        Interlocked.Increment(ref _replayedCount);
        Lan.LanJournal.PublishUploaded(entry.Id, saleId);
        UpdateStatusText();
        RaiseStateChanged();
        var date = entry.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
        return new LateAcceptResult(true, Tr.T(
            $"Чек проведён с датой продажи {date}.",
            $"Чек сатуу датасы {date} менен өткөрүлдү.",
            $"The receipt was posted with the sale date {date}.",
            $"Fiş {date} satış tarihiyle işlendi.",
            $"Chek {date} sotuv sanasi bilan o'tkazildi."), SentWithWarnings: withWarnings);
    }

    /// <summary>2026-11-19: несколько чеков одним подтверждением (токен живёт 5 минут — expires_in). Идёт в переданном порядке (от старых к новым); когда срок токена на исходе (или сервер его отклонил) — останавливается:
    /// оставшиеся чеки не тронуты и ждут нового подтверждения. confirmToken == null (вошёл владелец/админ) — срок не ограничен.</summary>
    public async Task<LateAcceptBatchResult> SendLateAcceptedBatchAsync(IReadOnlyList<string> entryIds, string? confirmToken, int? expiresInSeconds, string? approvedBy, CancellationToken ct = default)
    {
        var issuedAt = DateTime.UtcNow;
        var ttl = TimeSpan.FromSeconds(Math.Clamp(expiresInSeconds ?? 300, 30, 900));
        int sent = 0, failed = 0, index = 0;
        string? lastFailure = null;
        var expired = false;
        for (; index < entryIds.Count; index++)
        {
            // Запас 15 с: запрос идёт до 45 с — не начинаем чек, если токен вот-вот истечёт.
            if (confirmToken is not null && DateTime.UtcNow - issuedAt > ttl - TimeSpan.FromSeconds(15))
            {
                expired = true;
                break;
            }
            ct.ThrowIfCancellationRequested();
            var r = await SendLateAcceptedAsync(entryIds[index], confirmToken, approvedBy, ct).ConfigureAwait(false);
            if (r.Ok)
            {
                sent++;
                continue;
            }
            failed++;
            lastFailure = r.Message;
            if (r.TokenRejected)
            {
                expired = true;
                index++;
                break;
            }
            if (IsConnectivityText(r.Message))
            {
                index++;
                break; // связи нет — остальные не мучаем
            }
        }
        var remaining = Math.Max(0, entryIds.Count - index);
        var message = BatchSummaryText(sent, failed, remaining, expired, lastFailure);
        PosLogger.Log($"OFFLINE late-accept: пакет — отправлено {sent}, отказов {failed}, не тронуто {remaining}{(expired ? ", подтверждение истекло/отклонено" : "")}.", "OFFLINE");
        return new LateAcceptBatchResult(sent, failed, remaining, expired, message);
    }

    private static bool IsConnectivityText(string message) => string.Equals(message, NoConnectionText(), StringComparison.Ordinal);

    private static string BatchSummaryText(int sent, int failed, int remaining, bool expired, string? lastFailure)
    {
        var head = Tr.T($"Отправлено: {sent}.", $"Жөнөтүлдү: {sent}.", $"Sent: {sent}.", $"Gönderildi: {sent}.", $"Yuborildi: {sent}.");
        if (failed == 0 && remaining == 0)
            return head;
        var parts = new List<string> { head };
        if (failed > 0)
            parts.Add(Tr.T($"Не отправлено: {failed}.", $"Жөнөтүлгөн жок: {failed}.", $"Not sent: {failed}.", $"Gönderilmedi: {failed}.", $"Yuborilmadi: {failed}."));
        if (remaining > 0)
            parts.Add(expired
                ? Tr.T($"Ещё {remaining} чек. ждут: подтверждение действует 5 минут — нажмите «Подтвердить отправку…» и введите PIN ещё раз.",
                    $"Дагы {remaining} чек күтөт: ырастоо 5 мүнөт иштейт — «Жөнөтүүнү ырастоо…» басып, PIN кодду кайра киргизиңиз.",
                    $"{remaining} more receipt(s) are waiting: the confirmation lasts 5 minutes — press “Confirm sending…” and enter the PIN again.",
                    $"{remaining} fiş daha bekliyor: onay 5 dakika geçerli — «Göndermeyi onayla…»ya basıp PIN'i yeniden girin.",
                    $"Yana {remaining} ta chek kutmoqda: tasdiqlash 5 daqiqa amal qiladi — «Yuborishni tasdiqlash…»ni bosib, PIN ni qayta kiriting.")
                : Tr.T($"Ещё {remaining} чек. не тронуты.", $"Дагы {remaining} чек тийилген жок.", $"{remaining} more receipt(s) were not touched.", $"{remaining} fiş daha dokunulmadı.", $"Yana {remaining} ta chek tegilmadi."));
        if (!string.IsNullOrWhiteSpace(lastFailure))
            parts.Add(lastFailure!);
        return string.Join(" ", parts);
    }

    /// <summary>2026-11-19: статус старого чека, пока ждёт подтверждения (ответ Умара 19.11, п. 3): «Старше 7 суток — нужно подтверждение».</summary>
    public static string NeedsConfirmText(OfflineSaleEntry entry)
    {
        var date = entry.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm");
        return Tr.T($"Старше 7 суток — нужно подтверждение (чек от {date}). Нажмите «Подтвердить отправку…» и введите PIN владельца или администратора. Чек и деньги сохранены.",
            $"7 суткадан эски — ырастоо керек ({date} чеги). «Жөнөтүүнү ырастоо…» басып, ээсинин же администратордун PIN кодун киргизиңиз. Чек жана акча сакталды.",
            $"Older than 7 days — confirmation needed (receipt from {date}). Press “Confirm sending…” and enter the owner's or administrator's PIN. The receipt and the money are kept.",
            $"7 günden eski — onay gerekli ({date} tarihli fiş). «Göndermeyi onayla…»ya basıp sahibin veya yöneticinin PIN'ini girin. Fiş ve para saklandı.",
            $"7 kundan eski — tasdiqlash kerak ({date} dagi chek). «Yuborishni tasdiqlash…»ni bosib, egasi yoki administrator PIN kodini kiriting. Chek va pul saqlandi.");
    }

    /// <summary>2026-11-19: старше 90 суток — отправить нельзя ни с каким PIN.</summary>
    public static string TooOldSupportText() => Tr.T(
        "Старше 90 суток — отправить нельзя, обратитесь в поддержку. Чек и деньги сохранены в кассе.",
        "90 суткадан эски — жөнөтүүгө болбойт, колдоого кайрылыңыз. Чек жана акча кассада сакталды.",
        "Older than 90 days — it cannot be sent, contact support. The receipt and the money are kept in the till.",
        "90 günden eski — gönderilemez, destekle iletişime geçin. Fiş ve para kasada saklandı.",
        "90 kundan eski — yuborib bo'lmaydi, qo'llab-quvvatlashga murojaat qiling. Chek va pul kassada saqlandi.");

    /// <summary>Понятная причина отказа сервера при позднем проведении (старше 90 суток, смена закрыта, нет прав, ключ ещё выполняется…).</summary>
    private static string DescribeLateRefusal(ApiException ex, OfflineSaleEntry entry)
    {
        string raw;
        try
        {
            raw = ((ex.Payload is { } p ? p.GetRawText() : "") + " " + ex.Message).ToLowerInvariant();
        }
        catch (InvalidOperationException)
        {
            raw = ex.Message.ToLowerInvariant();
        }

        if (ex.StatusCode is 408 or 409 or 423 or 425 or >= 500)
            return Tr.T("Сервер сейчас не смог обработать чек (запрос ещё выполняется или сервер занят). Подождите минуту и повторите — продажа не задвоится.",
                "Сервер чекти азыр иштете албады (сурам дагы аткарылууда же сервер бош эмес). Бир мүнөт күтүп, кайталаңыз — сатуу эки жолу чыкпайт.",
                "The server could not process the receipt right now (the request is still running or the server is busy). Wait a minute and retry — the sale will not be duplicated.",
                "Sunucu fişi şu an işleyemedi (istek hâlâ sürüyor veya sunucu meşgul). Bir dakika bekleyip tekrar deneyin — satış çiftlenmez.",
                "Server chekni hozir qayta ishlay olmadi (so'rov hali bajarilmoqda yoki server band). Bir daqiqa kutib, qayta urining — sotuv ikkilanmaydi.");
        // 2026-11-19: offline_too_old при accept_late — либо чек старше 90 суток (late_max_days), либо сервер не принял подтверждение (чек моложе 90 суток).
        if (raw.Contains("offline_too_old") || raw.Contains("late_max_days"))
            return DateTimeOffset.Now - entry.CreatedAt > LateAcceptMaxAge ? TooOldSupportText() : NeedsConfirmText(entry);
        if (raw.Contains("shift") || raw.Contains("смен"))
            return Tr.T("Сервер отказал: смена этого чека закрыта или не найдена. Проведите продажу на сайте с нужной датой и закройте чек кнопкой «Закрыть без отправки».",
                "Сервер четке какты: бул чектин сменасы жабык же табылган жок. Сатууну сайтта керектүү датасы менен өткөрүп, чекти «Жөнөтпөй жабуу» менен жабыңыз.",
                "The server refused: this receipt's shift is closed or not found. Post the sale on the website with the right date and close the receipt with “Close without sending”.",
                "Sunucu reddetti: bu fişin vardiyası kapalı veya bulunamadı. Satışı sitede doğru tarihle işleyin ve fişi «Göndermeden kapat» ile kapatın.",
                "Server rad etdi: bu chekning smenasi yopiq yoki topilmadi. Sotuvni saytda kerakli sana bilan o'tkazing va chekni «Yubormasdan yopish» bilan yoping.");
        if (ex.StatusCode is 401 or 403)
            return Tr.T("Сервер не принял подтверждение владельца (токен просрочен — он живёт 5 минут — или нет прав). Нажмите кнопку ещё раз и введите PIN заново.",
                "Сервер ээсинин ырастоосун кабыл алган жок (токендин мөөнөтү өттү — ал 5 мүнөт жашайт — же укук жок). Баскычты кайра басып, PIN кодду кайра киргизиңиз.",
                "The server did not accept the owner's confirmation (the token expired — it lives 5 minutes — or no rights). Press the button again and re-enter the PIN.",
                "Sunucu sahip onayını kabul etmedi (belirtecin süresi doldu — 5 dakika geçerli — veya yetki yok). Düğmeye tekrar basıp PIN'i yeniden girin.",
                "Server egasining tasdig'ini qabul qilmadi (token muddati tugadi — u 5 daqiqa yashaydi — yoki huquq yo'q). Tugmani qayta bosib, PIN ni qayta kiriting.");
        return Tr.T($"Сервер отказал (код {ex.StatusCode}): {ex.Message}. Чек сохранён в кассе.",
            $"Сервер четке какты (коду {ex.StatusCode}): {ex.Message}. Чек кассада сакталды.",
            $"The server refused (code {ex.StatusCode}): {ex.Message}. The receipt is kept in the till.",
            $"Sunucu reddetti (kod {ex.StatusCode}): {ex.Message}. Fiş kasada saklandı.",
            $"Server rad etdi (kod {ex.StatusCode}): {ex.Message}. Chek kassada saqlandi.");
    }

    /// <summary>2026-11-01 (ревью): чек, который мог уже уйти на сервер.</summary>
    public static string MaybePostedText() => Tr.T(
        "Возможно, чек уже проведён на сервере (его отправка начиналась, ответ мог потеряться). Сверьте по «Чекам» на сервере: если продажа есть — закройте чек кнопкой «Закрыть без отправки»; если нет — проведите её на сайте с нужной датой. По PIN повторно не отправляется, чтобы не задвоить продажу.",
        "Чек серверде өткөрүлүп калган болушу мүмкүн (жөнөтүү башталган, жооп жоголгон болушу ыктымал). Серверде «Чектерден» салыштырыңыз: сатуу бар болсо — чекти «Жөнөтпөй жабуу» менен жабыңыз; жок болсо — сайтта керектүү датасы менен өткөрүңүз. Сатуу эки жолу чыкпашы үчүн PIN менен кайра жөнөтүлбөйт.",
        "The receipt may already be posted on the server (sending had started, the reply may have been lost). Check the server's “Receipts”: if the sale is there, close the receipt with “Close without sending”; if not, post it on the website with the right date. It is not resent by PIN so the sale is not duplicated.",
        "Fiş sunucuda zaten işlenmiş olabilir (gönderim başlamıştı, yanıt kaybolmuş olabilir). Sunucudaki «Fişler»den kontrol edin: satış varsa fişi «Göndermeden kapat» ile kapatın; yoksa sitede doğru tarihle işleyin. Satış çiftlenmesin diye PIN ile yeniden gönderilmez.",
        "Chek serverda allaqachon o'tkazilgan bo'lishi mumkin (yuborish boshlangan, javob yo'qolgan bo'lishi mumkin). Serverdagi «Cheklar»dan solishtiring: sotuv bor bo'lsa — chekni «Yubormasdan yopish» bilan yoping; yo'q bo'lsa — saytda kerakli sana bilan o'tkazing. Sotuv ikkilanmasligi uchun PIN bilan qayta yuborilmaydi.");

    private static string LatePinLockedText(int seconds) => Tr.T(
        $"Слишком много неверных PIN. Подождите {seconds} с и повторите.",
        $"PIN өтө көп жолу туура эмес киргизилди. {seconds} с күтүп, кайталаңыз.",
        $"Too many incorrect PINs. Wait {seconds} s and try again.",
        $"Çok fazla yanlış PIN. {seconds} sn bekleyip tekrar deneyin.",
        $"PIN juda ko'p marta noto'g'ri kiritildi. {seconds} s kutib, qayta urining.");

    private static string NoConnectionText() => Tr.T(
        "Нет связи с сервером. Чек остался в кассе, ничего не потеряно — повторите, когда связь появится (PIN понадобится заново).",
        "Сервер менен байланыш жок. Чек кассада калды, эч нерсе жоголгон жок — байланыш пайда болгондо кайталаңыз (PIN кайра керек болот).",
        "No connection to the server. The receipt stays in the till, nothing is lost — retry when the connection is back (the PIN will be needed again).",
        "Sunucuyla bağlantı yok. Fiş kasada kaldı, hiçbir şey kaybolmadı — bağlantı gelince tekrar deneyin (PIN yeniden gerekir).",
        "Server bilan aloqa yo'q. Chek kassada qoldi, hech narsa yo'qolmadi — aloqa paydo bo'lganda qayta urining (PIN qayta kerak bo'ladi).");

    private static string NoApiText() => Tr.T(
        "Проведение по PIN владельца сейчас недоступно (функция выключена или сервер её не поддерживает).",
        "Ээсинин PIN коду менен өткөрүү азыр жеткиликсиз (функция өчүрүлгөн же сервер колдобойт).",
        "Posting with the owner PIN is unavailable now (the feature is off or the server does not support it).",
        "Sahip PIN'iyle işleme şu an kullanılamıyor (özellik kapalı veya sunucu desteklemiyor).",
        "Egasining PIN i bilan o'tkazish hozir mavjud emas (funksiya o'chirilgan yoki server qo'llamaydi).");
}
