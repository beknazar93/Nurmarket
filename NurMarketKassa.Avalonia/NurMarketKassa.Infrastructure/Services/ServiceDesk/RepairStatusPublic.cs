using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Результат передачи заказа серверу.</summary>
public enum RepairSyncKind
{
    /// <summary>Принят (201 created / 200 / unchanged).</summary>
    Ok,
    /// <summary>403: у пользователя нет права repair.manage или у компании выключен модуль «Ремонт» — паузу на 10 минут.</summary>
    Forbidden,
    /// <summary>429: слишком часто — пауза (Retry-After клиент API не отдаёт, ждём минуту).</summary>
    RateLimited,
    /// <summary>404/405/501: сервер этого адреса не знает — пауза на полчаса.</summary>
    Missing,
    /// <summary>400/409/422: сервер отклонил именно этот заказ — не повторяем до перезапуска кассы.</summary>
    Rejected,
    /// <summary>Нет сети, таймаут, 5xx — заказ остаётся в очереди, повтор позже.</summary>
    Retry,
    /// <summary>2026-11-26 (этап 2): 409 number_conflict — наш номер уже занят другой квитанцией, сервер предлагает свободный (suggested_number / suggested_code).</summary>
    NumberConflict,
    /// <summary>2026-11-26 (этап 2): 409 stale_version — квитанцию уже изменили на другом устройстве; в ответе актуальное состояние (current).</summary>
    Stale,
}

/// <remarks>2026-11-26 (Умар 16:20): <paramref name="Notified"/> — поле notified ответа PUT при переводе заказа в «Готов»: sent | no_chat | deferred | failed (сервер сам пишет клиенту в Telegram-бота магазина, один раз на заказ); null — поля нет.</remarks>
/// <remarks>2026-11-26 (этап 2): <paramref name="Code"/>/<paramref name="Number"/> — номер и код «Р-0005», которые выдал сервер; <paramref name="UpdatedAt"/> — метка для base_updated_at; <paramref name="ServerId"/> — id на сервере;
/// <paramref name="SuggestedNumber"/>/<paramref name="SuggestedCode"/> — при number_conflict; <paramref name="CurrentJson"/> — current при stale_version; <paramref name="Http"/> — код ответа при отказе;
/// <paramref name="LegacyNumber"/> — принято только по-старому (с нашим номером): сервер ещё не умеет server_number.</remarks>
public sealed record RepairSyncResult(RepairSyncKind Kind, bool Created = false, bool Unchanged = false, string? ServerNumber = null, string? PublicUrl = null, string? Notified = null,
    string? Code = null, int? Number = null, string? UpdatedAt = null, string? ServerId = null, int? SuggestedNumber = null, string? SuggestedCode = null, string? CurrentJson = null, int? Http = null, bool LegacyNumber = false,
    string? ErrorCode = null);

/// <summary>2026-11-27 (ответ Умара 27.11, п. 3): один запрос к серверной квитанции (external_id нет): PATCH repair-orders/&lt;uuid&gt;/ (поля), POST …/status/, POST …/issue/. <paramref name="Applied"/> — какие
/// записи BaseKey (RepairContentKey) этот запрос переносит на сервер и их значения: после успеха они запоминаются, чтобы повтор после обрыва не слал уже принятое (иначе 409 bad_transition).</summary>
public sealed record RepairServerCall(string Method, string Suffix, Dictionary<string, object?> Body, IReadOnlyDictionary<string, string> Applied, string? IdempotencyKey = null);

/// <summary>2026-11-26 (сервер выкатил этап 1 «статус ремонта на витрине», ответ Умара 26.11): передача заказов ремонта серверу и ссылка на страницу статуса.
/// Контракт: PUT api/main/repair-orders/{external_id}/ (external_id = Id квитанции, 32 hex), тело — number, code, status (accepted|in_work|ready|issued|cancelled), device, client_name,
/// client_phone, accepted_at, due_date, ready_at, estimate, prepay, final_price + serial, problem, accessories, note; ответ 201 created:true, затем 200 (unchanged:true при том же составе), в ответе id, number
/// (серверный), code, public_url. Право — модуль «Ремонт» и repair.manage (иначе 403). Деньги сервер НЕ двигает: prepay и final_price — только цифры для показа.
/// Включается только признаком capabilities.features «repair_status_public» (есть, когда у компании включён модуль «Ремонт»); без него касса в сеть ничего не шлёт.
/// Заказ не теряется без сети: у каждой правки растёт Rev, у принятой сервером — SyncedRev; заказы с разными числами уходят повторно (окно «Ремонт и сервис» делает это при открытии,
/// после каждой правки и раз в 90 секунд). Серверу уходят и неисправность, серийный номер, комплектация, заметка — для программы владельца; в публичный ответ сервера они не попадают.</summary>
public static class RepairStatusPublic
{
    /// <summary>Признак сервера: публичная страница статуса ремонта и приём заказов ремонта от кассы.</summary>
    public const string Capability = "repair_status_public";

    internal const string ApiPath = "api/main/repair-orders/";

    public static bool Enabled => ServerCapabilities.Has(Capability);

    /// <summary>Статус для сервера — латиницей.</summary>
    public static string StatusCode(RepairStatus s) => s switch
    {
        RepairStatus.Accepted => "accepted",
        RepairStatus.InWork => "in_work",
        RepairStatus.Ready => "ready",
        RepairStatus.Issued => "issued",
        _ => "cancelled",
    };

    /// <summary>Наша ссылка на страницу статуса: /catalog/{slug}/repair?code=12 (номер квитанции; страница спрашивает ещё последние 4 цифры телефона). Берётся, только если сервер не прислал public_url.</summary>
    public static string? Url(string? slug, RepairOrder o) =>
        string.IsNullOrWhiteSpace(slug) ? null : ShowcaseApiService.CatalogUrl(slug) + "/repair?code=" + o.Number.ToString(CultureInfo.InvariantCulture);

    private static string Money(decimal v) => RepairRules.Money(v).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>2026-11-26 (Умар 16:20): сервер сам написал клиенту «Ремонт готов» в Telegram-бота магазина (notified = sent) — кассиру писать не нужно.</summary>
    public static bool ServerNotifiedClient(RepairOrder o) => o.NotifiedStatus == "sent";

    /// <summary>2026-11-26: что сказал сервер, если клиента он не оповестил (no_chat / deferred / failed): подсказка кассиру; для sent и неизвестного — null.</summary>
    public static string? ServerNotifyHint(RepairOrder o) => o.NotifiedStatus switch
    {
        "no_chat" => Tr.T("Клиент не привязал номер к Telegram-боту магазина — напишите ему в WhatsApp.", "Кардар номерин дүкөндүн Telegram-ботуна байлаган жок — WhatsApp'ка жазыңыз.", "The customer has not linked the number to the shop's Telegram bot — write in WhatsApp.", "Müşteri numarasını mağazanın Telegram botuna bağlamadı — WhatsApp'tan yazın.", "Mijoz raqamini do'konning Telegram botiga ulamagan — WhatsApp'da yozing."),
        "deferred" => Tr.T("Сервер отложил сообщение клиенту в Telegram — отправит позже; можно сразу написать в WhatsApp.", "Сервер кардарга Telegram аркылуу билдирүүнү кийинкиге калтырды — кийин жөнөтөт; WhatsApp'ка дароо жазса болот.", "The server postponed the Telegram message to the customer — it will be sent later; you can write in WhatsApp right away.", "Sunucu müşteriye Telegram mesajını erteledi — sonra gönderecek; hemen WhatsApp'tan yazabilirsiniz.", "Server mijozga Telegram xabarini keyinga qoldirdi — keyin yuboradi; darhol WhatsApp'da yozish mumkin."),
        "failed" => Tr.T("Сервер не смог написать клиенту в Telegram — напишите ему в WhatsApp.", "Сервер кардарга Telegram аркылуу жаза алган жок — WhatsApp'ка жазыңыз.", "The server could not message the customer in Telegram — write in WhatsApp.", "Sunucu müşteriye Telegram'dan yazamadı — WhatsApp'tan yazın.", "Server mijozga Telegram orqali yoza olmadi — WhatsApp'da yozing."),
        _ => null,
    };

    /// <summary>Тело PUT точно по контракту сервера. Пустые необязательные поля (serial, problem, accessories, note) не шлём.
    /// 2026-11-26 (этап 2): у квитанции с временным номером своего number/code нет — шлём server_number: true, номер выдаёт сервер (<paramref name="legacyNumber"/> — старый сервер без этапа 2: шлём наш номер, как раньше);
    /// base_updated_at — метка последнего ответа сервера (защита от затирания чужой правки).</summary>
    public static Dictionary<string, object?> BuildPayload(RepairOrder o, bool legacyNumber = false)
    {
        var body = new Dictionary<string, object?>();
        if (o.TempNumber && !legacyNumber)
            body["server_number"] = true;
        else
        {
            body["number"] = o.Number;
            body["code"] = RepairRules.Code(o);
        }
        if (!string.IsNullOrWhiteSpace(o.UpdatedAt))
            body["base_updated_at"] = o.UpdatedAt;
        body["status"] = StatusCode(o.Status);
        body["device"] = o.Device;
        body["client_name"] = o.ClientName;
        body["client_phone"] = o.ClientPhone;
        body["accepted_at"] = o.AcceptedAt.ToString("s", CultureInfo.InvariantCulture);
        body["due_date"] = o.DueDate is { } d ? Day(d) : null;
        body["ready_at"] = o.ReadyAt is { } r ? r.ToString("s", CultureInfo.InvariantCulture) : null;
        body["estimate"] = Money(o.Estimate);
        body["prepay"] = Money(o.Prepay);
        body["final_price"] = o.Status is RepairStatus.Ready or RepairStatus.Issued ? Money(o.FinalPrice) : null;
        void Optional(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                body[key] = value.Trim();
        }
        Optional("serial", o.Serial);
        Optional("problem", o.Fault);
        Optional("accessories", o.Equipment);
        Optional("note", o.Note);
        return body;
    }

    /// <summary>Разбор ответа PUT: created / unchanged, серверный номер и адрес страницы статуса (public_url принимаем только https и только свой домен nurcrm.kg).</summary>
    public static RepairSyncResult ParseResponse(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object)
            return new RepairSyncResult(RepairSyncKind.Ok);
        bool Flag(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        string? Str(string name) => d.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null }
            : null;
        var url = Str("public_url");
        if (!IsSafePublicUrl(url))
            url = null;
        var notified = Str("notified")?.Trim().ToLowerInvariant();
        // 2026-11-26 (этап 2): номер и код, которые выдал сервер, метка updated_at (для base_updated_at) и id на сервере.
        int? number = d.TryGetProperty("number", out var nv) && nv.ValueKind == JsonValueKind.Number && nv.TryGetInt32(out var n) && n > 0 ? n : null;
        var code = Str("code")?.Trim();
        var updated = Str("updated_at")?.Trim();
        var id = Str("id")?.Trim();
        return new RepairSyncResult(RepairSyncKind.Ok, Flag("created"), Flag("unchanged"), Str("number"), url, string.IsNullOrEmpty(notified) ? null : notified,
            string.IsNullOrEmpty(code) ? null : code, RepairServerRow.NumberFromCode(code) ?? number, string.IsNullOrEmpty(updated) ? null : updated, string.IsNullOrEmpty(id) ? null : id);
    }

    /// <summary>2026-11-26 (этап 2): разбор отказа PUT. 409 number_conflict → предложенный номер; 409 stale_version → актуальное состояние (current); остальное — как <see cref="Classify"/>.</summary>
    public static RepairSyncResult ParseError(Exception ex)
    {
        var http = ex is ApiException { StatusCode: { } s } ? s : (int?)null;
        if (ex is ApiException { StatusCode: 409, Payload: { ValueKind: JsonValueKind.Object } p })
        {
            var code = p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (code == "number_conflict")
            {
                var sn = p.TryGetProperty("suggested_number", out var snv) && snv.ValueKind == JsonValueKind.Number && snv.TryGetInt32(out var n) && n > 0 ? n : (int?)null;
                var sc = p.TryGetProperty("suggested_code", out var scv) && scv.ValueKind == JsonValueKind.String ? scv.GetString()?.Trim() : null;
                return new RepairSyncResult(RepairSyncKind.NumberConflict, SuggestedNumber: sn ?? RepairServerRow.NumberFromCode(sc), SuggestedCode: string.IsNullOrEmpty(sc) ? null : sc, Http: http);
            }
            if (code == "stale_version" && p.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Object)
                return new RepairSyncResult(RepairSyncKind.Stale, CurrentJson: cur.GetRawText(), Http: http);
        }
        // 2026-11-27: код отказа сервера (bad_transition, prepay_not_refunded, payment_missing, overpay_not_refunded…) — для сообщения кассиру и журнала
        string? errorCode = null;
        if (ex is ApiException { Payload: { ValueKind: JsonValueKind.Object } body } && body.TryGetProperty("code", out var ec) && ec.ValueKind == JsonValueKind.String)
            errorCode = ec.GetString();
        return new RepairSyncResult(Classify(ex), Http: http, ErrorCode: errorCode);
    }

    // ───────────────────────── серверные квитанции (без external_id), ответ Умара 27.11 ─────────────────────────

    /// <summary>2026-11-27 (ответ Умара 27.11, п. 3): серверную квитанцию (создана в программе владельца или мобилке, external_id нет) касса меняет ОБЫЧНЫМИ адресами repair-orders/&lt;uuid&gt;/ (PATCH),
    /// …/status/, …/prepay/, …/issue/ — не PUT по external_id (он только для квитанций, созданных кассой).</summary>
    public static string ServerPath(RepairOrder o) => ApiPath + Uri.EscapeDataString(o.ServerId) + "/";

    private static readonly string[] EditableKeys = ["device", "name", "phone", "serial", "fault", "equipment", "note", "estimate", "due"];

    /// <summary>Что надо отправить серверу по серверной квитанции: разница между тем, что касса знает сейчас, и тем, что было на сервере (<see cref="RepairOrder.BaseKey"/>), в порядке PATCH полей → status → issue.
    /// Форма тел взята из ТЗ кассы 13.11 и ответа Умара 27.11 (в ТЗ 27.11 тела не расписаны): PATCH — только изменённые поля (имя и телефон — оба варианта названий, как в чтении и в PUT), status — {to, final_price},
    /// issue — без тела. Деньги — decimal через <see cref="RepairRules.Money"/>. НЕ ПРОВЕРЕНО ВЖИВУЮ (нет тестового аккаунта с модулем «Ремонт» в этом сеансе).
    /// Квитанция без сохранённого BaseKey: шлём все поля и текущий статус.</summary>
    public static List<RepairServerCall> PlanServerCalls(RepairOrder o)
    {
        var now = RepairContentKey.Of(o);
        Dictionary<string, string> known = new();
        if (!string.IsNullOrWhiteSpace(o.BaseKey))
        {
            try { known = JsonSerializer.Deserialize<Dictionary<string, string>>(o.BaseKey) ?? new(); }
            catch (JsonException) { known = new(); }
        }
        string Was(string key) => known.TryGetValue(key, out var v) ? v : "";
        var hadBase = known.Count > 0;
        var calls = new List<RepairServerCall>();

        var changed = EditableKeys.Where(k => !hadBase ? now[k].Length > 0 : Was(k) != now[k]).ToList();
        var statusChanged = !hadBase || Was("status") != now["status"];
        // Итог работ у «Готов» можно поправить без смены статуса (кнопка «Изменить стоимость»): уходит в PATCH.
        var finalChanged = hadBase && !statusChanged && o.Status == RepairStatus.Ready && Was("final") != now["final"];
        if (changed.Count > 0 || finalChanged)
        {
            var body = new Dictionary<string, object?>();
            var applied = new Dictionary<string, string>();
            foreach (var key in changed)
            {
                applied[key] = now[key];
                switch (key)
                {
                    case "device": body["device"] = o.Device; break;
                    case "name": body["client_name"] = o.ClientName; body["customer_name"] = o.ClientName; break;
                    case "phone": body["client_phone"] = o.ClientPhone; body["customer_phone"] = o.ClientPhone; break;
                    case "serial": body["serial"] = string.IsNullOrWhiteSpace(o.Serial) ? null : o.Serial.Trim(); break;
                    case "fault": body["problem"] = o.Fault; body["fault"] = o.Fault; break;
                    case "equipment": body["accessories"] = o.Equipment; body["equipment"] = o.Equipment; break;
                    case "note": body["note"] = o.Note; break;
                    case "estimate": body["estimate"] = Money(o.Estimate); break;
                    case "due": body["due_date"] = o.DueDate is { } d ? Day(d) : null; break;
                }
            }
            if (finalChanged)
            {
                body["final_price"] = Money(o.FinalPrice);
                applied["final"] = now["final"];
            }
            calls.Add(new RepairServerCall("PATCH", "", body, applied));
        }

        if (statusChanged)
        {
            var from = hadBase ? ParseStatusKey(Was("status")) : null;
            if (o.Status == RepairStatus.Issued)
            {
                // выдача возможна только из «Готов»: если сервер ещё не знает, что квитанция готова (правки накопились без связи) — сначала «готов»
                if (from != RepairStatus.Ready)
                    calls.Add(new RepairServerCall("POST", "status/", new() { ["to"] = "ready", ["final_price"] = Money(o.FinalPrice) },
                        new Dictionary<string, string> { ["status"] = "ready", ["final"] = now["final"] }));
                calls.Add(new RepairServerCall("POST", "issue/", new(), new Dictionary<string, string> { ["status"] = now["status"], ["final"] = now["final"] },
                    IdempotencyKey: "repair-issue-" + o.ServerId));
            }
            else
            {
                var to = new Dictionary<string, object?> { ["to"] = StatusCode(o.Status) };
                if (o.Status == RepairStatus.Ready)
                    to["final_price"] = Money(o.FinalPrice);
                calls.Add(new RepairServerCall("POST", "status/", to, new Dictionary<string, string> { ["status"] = now["status"], ["final"] = now["final"] }));
            }
        }
        return calls;
    }

    private static RepairStatus? ParseStatusKey(string s) => RepairServerRow.ParseStatus(s);

    /// <summary>Выполнить запросы <see cref="PlanServerCalls"/> по порядку. Метка base_updated_at — из последнего ответа (первый запрос — метка квитанции). Каждый принятый запрос сразу записывается в BaseKey
    /// (<paramref name="store"/>.ApplyServerStep), поэтому после обрыва повтор шлёт только остаток. Не бросает: итог — <see cref="RepairSyncResult"/> (stale_version, отказ с ErrorCode, сеть).</summary>
    public static async Task<RepairSyncResult> SendServerOrderAsync(NurMarketApiClient api, RepairOrderStore store, RepairOrder o, CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(o.ServerId))
            return new RepairSyncResult(RepairSyncKind.Rejected, ErrorCode: "no_server_id");
        var calls = PlanServerCalls(o);
        var updated = o.UpdatedAt;
        RepairSyncResult last = new(RepairSyncKind.Ok, Unchanged: true);
        try
        {
            foreach (var call in calls)
            {
                var body = new Dictionary<string, object?>(call.Body);
                if (!string.IsNullOrWhiteSpace(updated))
                    body["base_updated_at"] = updated; // защита от затирания чужой правки — как у PUT
                var headers = call.IdempotencyKey is null ? null : new Dictionary<string, string> { ["Idempotency-Key"] = call.IdempotencyKey };
                var method = call.Method == "PATCH" ? HttpMethod.Patch : HttpMethod.Post;
                var d = await api.RequestAsync(method, ServerPath(o) + call.Suffix, body, null, ct, timeout ?? TimeSpan.FromSeconds(20), headers).ConfigureAwait(false);
                last = ParseResponse(d);
                if (!string.IsNullOrWhiteSpace(last.UpdatedAt))
                    updated = last.UpdatedAt;
                try { store.ApplyServerStep(o.Id, call.Applied, updated); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PosLogger.Log($"Ремонт {RepairRules.Code(o)}: шаг синхронизации не записан ({ex.GetType().Name}).", "WARNING");
                }
            }
            return last with { Unchanged = calls.Count == 0 };
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // квитанцию на сервере удалили — это отказ по ней одной, а не «сервер не знает адрес»: общую паузу ремонта не ставим
            PosLogger.Log($"Ремонт {RepairRules.Code(o)}: серверной квитанции больше нет ({ex.StatusCode}).", "WARNING");
            return new RepairSyncResult(RepairSyncKind.Rejected, Http: 404, ErrorCode: "not_found");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FailResult(ex, o);
        }
    }

    /// <summary>2026-11-27: предоплата по серверной квитанции — POST repair-orders/&lt;uuid&gt;/prepay/ {amount, shift}; сервер сам проводит внесение в смену и пишет предоплату в квитанцию одной транзакцией
    /// (вариант Б ТЗ 13.11). Ключ повтора — свой на каждое действие кассира (повтор после обрыва уходит с тем же). НЕ ПРОВЕРЕНО ВЖИВУЮ. Возвращает итог и строку квитанции из ответа (если сервер её вернул).</summary>
    public static async Task<(RepairSyncResult Result, RepairServerRow? Row)> PrepayServerAsync(NurMarketApiClient api, RepairOrder o, decimal amount, string? shiftId, string idempotencyKey,
        CancellationToken ct = default, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(o.ServerId) || amount <= 0m)
            return (new RepairSyncResult(RepairSyncKind.Rejected, ErrorCode: "bad_request"), null);
        try
        {
            var body = new Dictionary<string, object?> { ["amount"] = Money(amount) };
            if (!string.IsNullOrWhiteSpace(shiftId))
                body["shift"] = shiftId.Trim();
            var d = await api.RequestAsync(HttpMethod.Post, ServerPath(o) + "prepay/", body, null, ct, timeout ?? TimeSpan.FromSeconds(30),
                new Dictionary<string, string> { ["Idempotency-Key"] = idempotencyKey }).ConfigureAwait(false);
            return (ParseResponse(d), RepairServerRow.Parse(d));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (FailResult(ex, o), null);
        }
    }

    /// <summary>Актуальная строка серверной квитанции (GET repair-orders/&lt;uuid&gt;/) — чтобы после отказа сервера привести квитанцию в кассе к тому, что на сервере.</summary>
    public static async Task<RepairServerRow?> LoadServerRowAsync(NurMarketApiClient api, RepairOrder o, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(o.ServerId))
            return null;
        try
        {
            return RepairServerRow.Parse(await api.RequestAsync(HttpMethod.Get, ServerPath(o), null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Ремонт {RepairRules.Code(o)}: актуальная строка с сервера не получена ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    /// <summary>Сообщение кассиру по коду отказа сервера; null — код не из известных.</summary>
    public static string? RejectionText(string? errorCode) => errorCode switch
    {
        "bad_transition" => Tr.T("Сервер не принял смену статуса: такой переход не разрешён. Квитанция приведена к состоянию на сервере.", "Сервер статусту алмаштырууну кабыл алган жок: мындай өтүүгө болбойт. Квитанция сервердеги абалга келтирилди.", "The server rejected the status change: this transition is not allowed. The receipt was restored to the server state.", "Sunucu durum değişikliğini kabul etmedi: bu geçişe izin verilmiyor. Makbuz sunucudaki duruma getirildi.", "Server holat o'zgarishini qabul qilmadi: bunday o'tishga ruxsat yo'q. Kvitansiya serverdagi holatga keltirildi."),
        "prepay_not_refunded" => Tr.T("Сервер не принял: предоплата не возвращена. Верните её в программе владельца. Квитанция приведена к состоянию на сервере.", "Сервер кабыл алган жок: алдын ала төлөм кайтарылган эмес. Аны ээсинин программасында кайтарыңыз. Квитанция сервердеги абалга келтирилди.", "The server rejected it: the prepayment has not been refunded. Refund it in the owner program. The receipt was restored to the server state.", "Sunucu kabul etmedi: ön ödeme iade edilmedi. Sahip programında iade edin. Makbuz sunucudaki duruma getirildi.", "Server qabul qilmadi: oldindan to'lov qaytarilmagan. Uni egasi dasturida qaytaring. Kvitansiya serverdagi holatga keltirildi."),
        "payment_missing" => Tr.T("Сервер не принял выдачу: он не видит оплату работ по этой квитанции. Квитанция приведена к состоянию на сервере, оплата в чеке сохранена.", "Сервер берүүнү кабыл алган жок: ал бул квитанция боюнча иштин төлөмүн көрбөйт. Квитанция сервердеги абалга келтирилди, чектеги төлөм сакталды.", "The server rejected the hand-over: it does not see a payment for the work on this receipt. The receipt was restored to the server state, the payment in the receipt is kept.", "Sunucu teslimi kabul etmedi: bu makbuz için iş ödemesini görmüyor. Makbuz sunucudaki duruma getirildi, fişteki ödeme korundu.", "Server topshirishni qabul qilmadi: u bu kvitansiya bo'yicha ish to'lovini ko'rmayapti. Kvitansiya serverdagi holatga keltirildi, chekdagi to'lov saqlandi."),
        "overpay_not_refunded" => Tr.T("Сервер не принял выдачу: переплата предоплаты не возвращена. Верните её в программе владельца. Квитанция приведена к состоянию на сервере.", "Сервер берүүнү кабыл алган жок: алдын ала төлөмдүн ашыкчасы кайтарылган эмес. Аны ээсинин программасында кайтарыңыз. Квитанция сервердеги абалга келтирилди.", "The server rejected the hand-over: the prepayment overpayment has not been refunded. Refund it in the owner program. The receipt was restored to the server state.", "Sunucu teslimi kabul etmedi: ön ödeme fazlası iade edilmedi. Sahip programında iade edin. Makbuz sunucudaki duruma getirildi.", "Server topshirishni qabul qilmadi: oldindan to'lov ortig'i qaytarilmagan. Uni egasi dasturida qaytaring. Kvitansiya serverdagi holatga keltirildi."),
        _ => null,
    };

    /// <summary>Общий разбор ошибки отправки: результат, паузы при 403/404/429 и запись в журнал (без телефона и имени клиента).</summary>
    private static RepairSyncResult FailResult(Exception ex, RepairOrder o)
    {
        var result = ParseError(ex);
        var kind = result.Kind;
        if (kind is RepairSyncKind.Forbidden or RepairSyncKind.Missing or RepairSyncKind.RateLimited)
            Pause(kind);
        var code = ex is ApiException { StatusCode: { } s } ? $"HTTP {s}" : ex.GetType().Name;
        if (kind == RepairSyncKind.Forbidden)
            WarnOnce("403", "Статус ремонта: сервер ответил 403 — нужен модуль «Ремонт» и право repair.manage; заказы остаются в очереди, повтор через 10 минут.");
        else if (kind == RepairSyncKind.Missing)
            WarnOnce("404", $"Статус ремонта: сервер не знает адрес repair-orders ({code}); повтор через 30 минут.");
        else if (kind == RepairSyncKind.NumberConflict)
            PosLogger.Log($"Статус ремонта {RepairRules.Code(o)}: номер занят на сервере, предложен {result.SuggestedCode ?? result.SuggestedNumber?.ToString(CultureInfo.InvariantCulture)}.", "WARNING");
        else if (kind == RepairSyncKind.Stale)
            PosLogger.Log($"Статус ремонта {RepairRules.Code(o)}: квитанцию изменили на другом устройстве (stale_version).", "WARNING");
        else if (kind == RepairSyncKind.Rejected)
            PosLogger.Log($"Статус ремонта {RepairRules.Code(o)}: сервер отклонил ({code}{(result.ErrorCode is { } ec ? ", " + ec : "")}).", "WARNING");
        else
            PosLogger.Log($"Статус ремонта {RepairRules.Code(o)}: не передан ({code}), останется в очереди.", "WARNING");
        return result;
    }

    public static bool IsSafePublicUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo)
        && (u.Host.Equals("nurcrm.kg", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".nurcrm.kg", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ошибку запроса — в вид результата: что делать дальше (пауза, повтор, отказ).</summary>
    public static RepairSyncKind Classify(Exception ex) => ex switch
    {
        ApiException { StatusCode: 403 } => RepairSyncKind.Forbidden,
        ApiException { StatusCode: 429 } => RepairSyncKind.RateLimited,
        ApiException { StatusCode: 404 or 405 or 501 } => RepairSyncKind.Missing,
        ApiException { StatusCode: 400 or 409 or 422 } => RepairSyncKind.Rejected,
        _ => RepairSyncKind.Retry,
    };

    /// <summary>Строки для печатной квитанции приёма: ссылка на статус на витрине (если сервер умеет и адрес известен) либо честная подсказка «позвоните в магазин, назовите код».
    /// Ничего не печатается, если нет ни ссылки, ни телефона магазина.</summary>
    public static IReadOnlyList<string> ReceiptLines(RepairOrder o, string? statusUrl, string? shopPhone, int width)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(statusUrl))
        {
            lines.AddRange(ReceiptLineLayout.WrapLeft(Tr.T("Статус ремонта на сайте:", "Оңдоонун абалы сайтта:", "Repair status online:", "Tamir durumu sitede:", "Ta'mir holati saytda:"), width));
            lines.AddRange(ReceiptLineLayout.WrapLeft(statusUrl!.Replace("https://", "", StringComparison.Ordinal), width));
            lines.AddRange(ReceiptLineLayout.WrapLeft(Tr.T($"Код {RepairRules.Code(o)} и последние 4 цифры телефона.", $"Код {RepairRules.Code(o)} жана телефондун акыркы 4 саны.", $"Code {RepairRules.Code(o)} and the last 4 digits of your phone.", $"Kod {RepairRules.Code(o)} ve telefonun son 4 hanesi.", $"Kod {RepairRules.Code(o)} va telefonning oxirgi 4 raqami."), width));
        }
        else if (!string.IsNullOrWhiteSpace(shopPhone))
        {
            lines.AddRange(ReceiptLineLayout.WrapLeft(Tr.T($"Узнать статус: позвоните {shopPhone!.Trim()} и назовите код {RepairRules.Code(o)}.", $"Абалын билүү: {shopPhone!.Trim()} номерине чалып, {RepairRules.Code(o)} кодун айтыңыз.", $"To check the status, call {shopPhone!.Trim()} and give the code {RepairRules.Code(o)}.", $"Durumu öğrenmek için {shopPhone!.Trim()} numarasını arayıp {RepairRules.Code(o)} kodunu söyleyin.", $"Holatini bilish uchun {shopPhone!.Trim()} ga qo'ng'iroq qilib, {RepairRules.Code(o)} kodini ayting."), width));
        }
        return lines;
    }

    // ───────────────────────── передача серверу ─────────────────────────

    private static readonly SemaphoreSlim FlushGate = new(1, 1);
    private static DateTime _pausedUntilUtc = DateTime.MinValue;
    /// <summary>2026-11-26 (этап 2): сервер не умеет server_number (ответил 400 на заказ без нашего номера, а со своим номером принял) — до перезапуска кассы шлём номер сами, как на этапе 1.</summary>
    private static volatile bool _legacyNumbers;
    internal static bool LegacyNumbers => _legacyNumbers;
    internal static bool IsPaused => DateTime.UtcNow < _pausedUntilUtc;
    private static readonly HashSet<string> RejectedRevs = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Warned = new(StringComparer.Ordinal);
    private const int MaxPerFlush = 20;
    private static readonly TimeSpan HistoryLimit = TimeSpan.FromDays(180);

    /// <summary>Только для проверок: снять паузу и забытые отказы.</summary>
    internal static void ResetForTests()
    {
        _pausedUntilUtc = DateTime.MinValue;
        _legacyNumbers = false;
        lock (RejectedRevs)
        {
            RejectedRevs.Clear();
            Warned.Clear();
        }
    }

    private static void WarnOnce(string key, string text)
    {
        bool first;
        lock (RejectedRevs)
            first = Warned.Add(key);
        if (first)
            PosLogger.Log(text, "WARNING");
    }

    internal static void Pause(RepairSyncKind kind)
    {
        var span = kind switch
        {
            RepairSyncKind.Forbidden => TimeSpan.FromMinutes(10),
            RepairSyncKind.Missing => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromMinutes(1),
        };
        _pausedUntilUtc = DateTime.UtcNow + span;
    }

    // 2026-11-27: public — его вызывает окно «Ремонт» (RepairOrdersWindow.Prepay) из проекта приложения, internal туда не виден (сборка падала CS0117)
    public static NurMarketApiClient? Api() =>
        ServerLoyalty.ApiProvider?.Invoke() is { } api && !string.IsNullOrEmpty(api.AccessToken) ? api : null;

    /// <summary>PUT одного заказа. Не бросает: ошибка — в <see cref="RepairSyncResult.Kind"/>; паузы при 403/404/429 ставит сама.
    /// 2026-11-26 (этап 2): 409 number_conflict и stale_version разбираются (<see cref="ParseError"/>) и паузу не ставят.</summary>
    public static async Task<RepairSyncResult> SendAsync(NurMarketApiClient api, RepairOrder o, CancellationToken ct = default, TimeSpan? timeout = null, bool legacyNumber = false)
    {
        try
        {
            var d = await api.RequestAsync(HttpMethod.Put, ApiPath + Uri.EscapeDataString(o.Id) + "/", BuildPayload(o, legacyNumber), null, ct, timeout ?? TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return ParseResponse(d);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FailResult(ex, o); // 2026-11-27: общий разбор (телефон и имя в журнал не пишем — только код и тип ошибки)
        }
    }

    /// <summary>Передать один заказ и записать итог в файл заказов (серверный номер, public_url, updated_at, отметка «отправлено», если заказ за это время не менялся).
    /// 2026-11-26 (этап 2): номер выдаёт сервер (server_number); старый сервер (400 на заказ без номера) — повтор с нашим номером; number_conflict — берём suggested и повторяем;
    /// stale_version — если на сервере содержимое то же, что касса знала, берём новую метку и повторяем, иначе сохраняем конфликт для окна («мои правки поверх» / «взять с сервера»).
    /// <paramref name="send"/> — подмена отправки для проверок.</summary>
    /// <param name="loadCurrent">2026-11-27: актуальная строка серверной квитанции после отказа сервера (по умолчанию GET repair-orders/&lt;uuid&gt;/); подмена — для проверок.</param>
    public static async Task<RepairSyncResult> SyncOrderAsync(RepairOrderStore store, RepairOrder o, CancellationToken ct = default, TimeSpan? timeout = null, Func<RepairOrder, bool, Task<RepairSyncResult>>? send = null,
        Func<RepairOrder, Task<RepairServerRow?>>? loadCurrent = null)
    {
        if (send is null)
        {
            if (!Enabled || Api() is not { } api)
                return new RepairSyncResult(RepairSyncKind.Retry);
            // 2026-11-27 (ответ Умара 27.11): серверная квитанция (external_id нет) — обычные адреса repair-orders/<uuid>/ (PATCH, status, issue), квитанция кассы — PUT по external_id
            send = (order, legacyMode) => order.ServerOnly ? SendServerOrderAsync(api, store, order, ct, timeout) : SendAsync(api, order, ct, timeout, legacyMode);
            loadCurrent ??= order => LoadServerRowAsync(api, order, ct);
        }
        var legacy = _legacyNumbers;
        var adoptedOnce = false;
        var staleOnce = false;
        RepairSyncResult r = new(RepairSyncKind.Retry);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            r = await send(o, legacy).ConfigureAwait(false);
            if (r.Kind == RepairSyncKind.Ok)
            {
                if (legacy && o.TempNumber)
                    _legacyNumbers = true; // сервер принял заказ только с нашим номером — этап 2 ещё не выкачен
                try { store.ApplySyncResult(o.Id, o.Rev, r, RepairContentKey.Serialize(RepairContentKey.Of(o)), askedServerNumber: o.TempNumber && !legacy); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PosLogger.Log($"Статус ремонта {RepairRules.Code(o)}: отметка «отправлено» не записана ({ex.GetType().Name}).", "WARNING");
                }
                return legacy && o.TempNumber ? r with { LegacyNumber = true } : r;
            }
            if (r.Kind == RepairSyncKind.Rejected && o.TempNumber && !legacy && r.Http is 400 or 422)
            {
                legacy = true; // старый сервер: server_number не знает — пробуем с нашим номером
                continue;
            }
            if (r.Kind == RepairSyncKind.NumberConflict && !adoptedOnce && (r.SuggestedNumber ?? 0) > 0 && (!o.TempNumber || legacy))
            {
                adoptedOnce = true;
                store.AdoptNumber(o.Id, r.SuggestedNumber!.Value, r.SuggestedCode);
                o = store.Find(o.Id) ?? o;
                continue;
            }
            if (r.Kind == RepairSyncKind.Stale)
            {
                RepairServerRow? current = null;
                try { current = RepairServerRow.Parse(JsonDocument.Parse(r.CurrentJson ?? "{}").RootElement); }
                catch (JsonException) { /* нечитаемый current — ниже станет конфликтом */ }
                if (!staleOnce && current is not null && current.UpdatedAt.Length > 0 && RepairContentKey.ServerUnchanged(o.BaseKey, current))
                {
                    staleOnce = true;
                    store.AdoptUpdatedAt(o.Id, current.UpdatedAt);
                    o = store.Find(o.Id) ?? o;
                    continue;
                }
                try { store.MarkConflict(o.Id, r.CurrentJson ?? ""); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PosLogger.Log($"Статус ремонта {RepairRules.Code(o)}: конфликт не записан ({ex.GetType().Name}).", "WARNING"); }
                return r;
            }
            break;
        }
        // 2026-11-27: сервер отказал по серверной квитанции (bad_transition, prepay_not_refunded, payment_missing…) — квитанция в кассе приводится к тому, что на сервере (деньги кассы — оплата в чеке — сохраняются),
        // иначе в кассе «выдано», а на сервере «готов». Причина — в журнале заказа (server-rejected:код) и в сообщении окна.
        if (o.ServerOnly && r.Kind == RepairSyncKind.Rejected && loadCurrent is not null)
        {
            try
            {
                if (await loadCurrent(o).ConfigureAwait(false) is { } row)
                    store.RevertToServerRow(o.Id, row, r.ErrorCode ?? ("http-" + (r.Http?.ToString(CultureInfo.InvariantCulture) ?? "?")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PosLogger.Log($"Статус ремонта {RepairRules.Code(o)}: квитанция не приведена к серверной ({ex.GetType().Name}).", "WARNING");
            }
        }
        if (r.Kind == RepairSyncKind.Rejected || r.Kind == RepairSyncKind.NumberConflict)
        {
            lock (RejectedRevs)
                RejectedRevs.Add(o.Id + ":" + o.Rev);
        }
        return r;
    }

    /// <summary>Отправить все заказы, которые ждут: не больше 20 за раз, по порядку, останавливаясь на паузе (403/404/429) или потере сети. Свежие и прежние (до 180 суток) заказы; закрытые давно — не трогаем.</summary>
    public static async Task<int> FlushAsync(RepairOrderStore store, CancellationToken ct = default)
    {
        if (!Enabled || DateTime.UtcNow < _pausedUntilUtc || Api() is null)
            return 0;
        if (!await FlushGate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0;
        var sent = 0;
        try
        {
            var now = DateTime.Now;
            var queue = store.All().Where(o => o.SyncPending && (!RepairRules.IsFinal(o.Status) || now - o.AcceptedAt < HistoryLimit)).OrderBy(o => o.Number).Take(MaxPerFlush).ToList();
            foreach (var o in queue)
            {
                bool skip;
                lock (RejectedRevs)
                    skip = RejectedRevs.Contains(o.Id + ":" + o.Rev);
                if (skip)
                    continue;
                var r = await SyncOrderAsync(store, o, ct).ConfigureAwait(false);
                if (r.Kind == RepairSyncKind.Ok)
                    sent++;
                else if (r.Kind is not (RepairSyncKind.Rejected or RepairSyncKind.NumberConflict or RepairSyncKind.Stale))
                    break; // пауза или нет сети — остальные уйдут в следующий раз
            }
        }
        finally
        {
            FlushGate.Release();
        }
        return sent;
    }

    /// <summary>Запустить отправку очереди в фоне (после правки заказа, при открытии окна и по таймеру). Выключено без признака сервера.</summary>
    public static void FlushInBackground(RepairOrderStore store, Action<int>? done = null)
    {
        if (!Enabled)
            return;
        _ = Task.Run(async () =>
        {
            try { done?.Invoke(await FlushAsync(store).ConfigureAwait(false)); } // 2026-11-26: done — окно перечитывает заказы, если сервер что-то принял (пометка «отправлено в Telegram»)
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Статус ремонта: очередь не отправлена ({ex.GetType().Name}).", "WARNING");
            }
        });
    }

    private static string? _slug;
    private static string _slugAccount = "";

    /// <summary>Адрес страницы статуса для квитанции. Только при признаке сервера (иначе null без запросов в сеть). Сначала пробуем передать заказ (до 5 секунд) — сервер вернёт свой public_url;
    /// нет ответа или public_url — наша ссылка по адресу витрины (slug читается один раз на аккаунт).</summary>
    public static async Task<string?> UrlForReceiptAsync(RepairOrderStore store, RepairOrder o, CancellationToken ct = default)
    {
        if (!Enabled || Api() is not { } api)
            return null;
        try
        {
            var url = o.PublicUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                if (DateTime.UtcNow < _pausedUntilUtc)
                    return null; // недавно сервер отказал или просил подождать — ссылку не обещаем
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var r = await SyncOrderAsync(store, o, cts.Token, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                // Сервер отказал (нет права/модуля, не знает адрес, отклонил заказ) — заказа на сервере не будет, ссылку на квитанции не печатаем (останется «позвоните в магазин»).
                // Нет сети (Retry, RateLimited) — заказ уйдёт из очереди позже, ссылка заработает.
                if (r.Kind is RepairSyncKind.Forbidden or RepairSyncKind.Missing or RepairSyncKind.Rejected or RepairSyncKind.NumberConflict or RepairSyncKind.Stale)
                    return null;
                url = r.PublicUrl;
                o = store.Find(o.Id) ?? o; // 2026-11-26 (этап 2): номер мог смениться на серверный
            }
            if (!string.IsNullOrWhiteSpace(url))
                return url;
            // 2026-11-26 (этап 2): номер ещё временный (сервер не ответил) — нашу ссылку по номеру не печатаем: после замены номера она вела бы на чужую квитанцию.
            if (o.TempNumber)
                return null;
            var account = ServerCapabilities.AccountKey();
            if (_slug is null || _slugAccount != account)
            {
                _slug = (await new ShowcaseApiService(api).GetSettingsAsync(ct).ConfigureAwait(false)).Slug;
                _slugAccount = account;
            }
            return Url(_slug, o);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            PosLogger.Log($"Статус ремонта: адрес страницы не получен ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }
}

/// <summary>2026-11-26: сообщение клиенту «ремонт готов» в Telegram — только по кнопке кассира и только тому, кто сам привязал свой номер к боту магазина
/// («Чеки и бонусы в Telegram»: нажал «Старт» и поделился номером). Это тот же путь, что у электронного чека, — сервера не нужно. Нет привязки — кнопки нет, остаётся WhatsApp.</summary>
public static class RepairTelegramNotify
{
    public static bool CanSend(RepairOrder o) =>
        TelegramReceiptDelivery.IsAvailable && TelegramReceiptLinks.ChatsForPhone(o.ClientPhone).Count > 0;

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Sent — дошло хотя бы в один чат; NotLinked — номер не привязан; Off — функция/бот выключены; Error — Telegram не принял.</summary>
    public static async Task<TelegramReceiptSendResult> SendReadyAsync(RepairOrder o, CancellationToken ct = default)
    {
        if (!TelegramReceiptDelivery.IsAvailable)
            return TelegramReceiptSendResult.Off;
        var chats = TelegramReceiptLinks.ChatsForPhone(o.ClientPhone);
        if (chats.Count == 0)
            return TelegramReceiptSendResult.NotLinked;
        var message = "🔧 " + Escape(RepairRules.ReadyMessage(o));
        var ok = false;
        foreach (var chat in chats)
        {
            var error = await TelegramBotService.SendToAsync(chat, message, ct).ConfigureAwait(false);
            if (error is null)
                ok = true;
            else if (TelegramBotService.IsBlocked(chat))
                TelegramReceiptLinks.Unlink(chat);
            else
                PosLogger.Log($"Ремонт: сообщение «готово» в Telegram не ушло ({error}).", "WARNING");
        }
        return ok ? TelegramReceiptSendResult.Sent : TelegramReceiptSendResult.Error;
    }
}
