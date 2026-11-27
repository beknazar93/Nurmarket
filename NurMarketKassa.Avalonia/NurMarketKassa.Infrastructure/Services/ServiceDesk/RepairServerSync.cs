using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>2026-11-26 (Умар, этап 2 «ремонт: синхронизация», docs/ТЗ/Умар — ремонт этап 2 синхронизация (26.11).md): строка квитанции из GET api/main/repair-orders/ (и поле current ответа 409 stale_version).
/// Имена полей читаем и «как у PUT» (client_name, prepay), и «как у чтения» (customer_name, prepaid): сервер в ТЗ назвал второй вариант. Поле, которого в строке нет, квитанцию не меняет.
/// Деньги разбираются как decimal из текста JSON (не через double) и округляются общим <see cref="RepairRules.Money"/> — копейки не теряются.</summary>
public sealed class RepairServerRow
{
    public JsonElement Raw { get; private init; }
    public string Id { get; private init; } = "";
    public string ExternalId { get; private init; } = "";
    public string Source { get; private init; } = "";
    public string Code { get; private init; } = "";
    public int? Number { get; private init; }
    public string UpdatedAt { get; private init; } = "";
    public RepairStatus? Status { get; private init; }

    /// <summary>Номер: цифры из кода «Р-0005» (на этапе 1 поле number было внутренним номером сервера, а не нашим), иначе поле number.</summary>
    public int? NumberValue => NumberFromCode(Code) ?? (Number is > 0 ? Number : null);

    public static int? NumberFromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return null;
        var digits = new string(code.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    public static RepairStatus? ParseStatus(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "accepted" => RepairStatus.Accepted,
        "in_work" => RepairStatus.InWork,
        "ready" => RepairStatus.Ready,
        "issued" => RepairStatus.Issued,
        "cancelled" or "canceled" => RepairStatus.Cancelled,
        _ => null,
    };

    private static string? Str(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var v))
                return v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), JsonValueKind.Null => "", _ => null };
        return null;
    }

    /// <summary>Сумма: число или строка «1500.00»; null — поля нет; пустое/null в JSON — 0.</summary>
    private static decimal? Money(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (!e.TryGetProperty(name, out var v))
                continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
                return RepairRules.Money(d);
            if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString()?.Replace(" ", "").Replace(" ", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var s))
                return RepairRules.Money(s);
            // 2026-11-26 (стресс-тест ремонта): непустой мусор («abc», объект, true) — поле пропускаем, а не обнуляем: раньше «итог» или «смета» квитанции становились 0 сом.
            if (v.ValueKind is JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())))
                return 0m;
            continue;
        }
        return null;
    }

    private static DateTime? Time(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t.LocalDateTime : null;

    public static RepairServerRow? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        var id = Str(e, "id")?.Trim() ?? "";
        var ext = Str(e, "external_id")?.Trim() ?? "";
        if (id.Length == 0 && ext.Length == 0)
            return null;
        int? number = null;
        if (e.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var num))
            number = num;
        return new RepairServerRow
        {
            Raw = e.Clone(), Id = id, ExternalId = ext, Source = Str(e, "source")?.Trim().ToLowerInvariant() ?? "",
            Code = Str(e, "code")?.Trim() ?? "", Number = number, UpdatedAt = Str(e, "updated_at")?.Trim() ?? "",
            Status = ParseStatus(Str(e, "status")),
        };
    }

    /// <summary>Строка новее той, что касса уже знает: время сравниваем; если не разобрать — отличие считаем обновлением.</summary>
    public static bool IsNewer(string? rowUpdatedAt, string? have)
    {
        if (string.IsNullOrWhiteSpace(have))
            return true;
        if (string.IsNullOrWhiteSpace(rowUpdatedAt))
            return false;
        if (DateTimeOffset.TryParse(rowUpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a)
            && DateTimeOffset.TryParse(have, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var b))
            return a > b;
        return !string.Equals(rowUpdatedAt, have, StringComparison.Ordinal);
    }

    /// <summary>Перенести в квитанцию то, что есть в строке. <paramref name="newOrder"/> — новая квитанция (берём и время приёма, и деньги предоплаты); иначе время и предоплату/возвраты оставляем кассе:
    /// предоплата — это деньги, которые касса сама внесла в смену, сервер их не двигает; время приёма сервер мог сдвинуть часовым поясом.</summary>
    public void Apply(RepairOrder o, bool newOrder)
    {
        var e = Raw;
        if (Status is { } st)
            o.Status = st;
        if (Str(e, "device") is { } device) o.Device = device.Trim();
        if (Str(e, "customer_name", "client_name") is { } name) o.ClientName = name.Trim();
        if (Str(e, "customer_phone", "client_phone") is { } phone) o.ClientPhone = phone.Trim();
        if (Str(e, "serial") is { } serial) o.Serial = ImeiValidator.Normalize(serial);
        if (Str(e, "problem", "fault") is { } fault) o.Fault = fault.Trim();
        if (Str(e, "accessories", "equipment") is { } eq) o.Equipment = eq.Trim();
        if (Str(e, "note") is { } note) o.Note = note.Trim();
        if (Money(e, "estimate", "estimated_price") is { } est) o.Estimate = est; // 2026-11-26 (живая проверка NBS): чтение отдаёт estimated_price, PUT принимает estimate
        if (Money(e, "final_price") is { } fin) o.FinalPrice = fin;
        // 2026-11-27 (ответ Умара 27.11, п. 3): у серверной квитанции (создана в программе владельца/мобилке, меняется через repair-orders/<uuid>/…) предоплату и возвраты ведёт сервер — берём его цифры всегда
        if ((newOrder || o.ServerOnly) && Money(e, "prepaid", "prepay") is { } pre) o.Prepay = pre;
        if (o.ServerOnly && Money(e, "refunded") is { } refunded) o.Refunded = refunded;
        if (e.TryGetProperty("due_date", out var due))
            o.DueDate = due.ValueKind == JsonValueKind.String && Time(due.GetString()) is { } dd ? dd.Date : null;
        if (newOrder)
        {
            if (Time(Str(e, "accepted_at")) is { } at) o.AcceptedAt = at;
            if (Time(Str(e, "ready_at")) is { } ra) o.ReadyAt = ra;
        }
        if (Str(e, "public_url") is { Length: > 0 } url && RepairStatusPublic.IsSafePublicUrl(url))
            o.PublicUrl = url;
        if (Str(e, "notified")?.Trim().ToLowerInvariant() is { Length: > 0 } notified)
            o.NotifiedStatus = notified;
        if (!string.IsNullOrEmpty(UpdatedAt))
            o.UpdatedAt = UpdatedAt;
    }
}

/// <summary>2026-11-26: «что знает сервер о квитанции» в виде словаря нормализованных значений. Нужен, чтобы при stale_version отличить настоящую правку на другом устройстве
/// от простой смены updated_at (сервер сам дописал notified и т. п.): если поля в ответе совпадают с тем, что касса отправила/прочитала в прошлый раз, — повторяем PUT с новой меткой, не беспокоя кассира.</summary>
public static class RepairContentKey
{
    private static string M(decimal v) => RepairRules.Money(v).ToString("0.00", CultureInfo.InvariantCulture);

    public static Dictionary<string, string> Of(RepairOrder o)
    {
        var final = o.Status is RepairStatus.Ready or RepairStatus.Issued;
        return new Dictionary<string, string>
        {
            ["status"] = RepairStatusPublic.StatusCode(o.Status),
            ["device"] = o.Device.Trim(), ["name"] = o.ClientName.Trim(), ["phone"] = o.ClientPhone.Trim(),
            ["estimate"] = M(o.Estimate), ["prepay"] = M(o.Prepay), ["final"] = final ? M(o.FinalPrice) : "",
            ["due"] = o.DueDate is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
            ["serial"] = ImeiValidator.Normalize(o.Serial), ["fault"] = o.Fault.Trim(), ["equipment"] = o.Equipment.Trim(), ["note"] = o.Note.Trim(),
        };
    }

    /// <summary>Только поля, которые есть в строке сервера.</summary>
    public static Dictionary<string, string> Of(RepairServerRow row)
    {
        var tmp = new RepairOrder();
        row.Apply(tmp, newOrder: true);
        var all = Of(tmp);
        var e = row.Raw;
        bool Has(params string[] names) => names.Any(n => e.TryGetProperty(n, out _));
        var keep = new Dictionary<string, string>();
        void Add(string key, params string[] names) { if (Has(names)) keep[key] = all[key]; }
        if (row.Status is not null) keep["status"] = all["status"];
        Add("device", "device"); Add("name", "customer_name", "client_name"); Add("phone", "customer_phone", "client_phone");
        Add("estimate", "estimate", "estimated_price"); Add("prepay", "prepaid", "prepay"); Add("final", "final_price"); Add("due", "due_date");
        Add("serial", "serial"); Add("fault", "problem", "fault"); Add("equipment", "accessories", "equipment"); Add("note", "note");
        if (keep.ContainsKey("final") && row.Status is not (RepairStatus.Ready or RepairStatus.Issued))
            keep["final"] = "";
        return keep;
    }

    public static string Serialize(Dictionary<string, string> key) => JsonSerializer.Serialize(key);

    /// <summary>Содержимое на сервере не менялось с тех пор, как касса его знала (<paramref name="baseKey"/>): все поля строки совпадают с запомненными. Нет запомненного — не уверены, значит «менялось».</summary>
    public static bool ServerUnchanged(string? baseKey, RepairServerRow current)
    {
        if (string.IsNullOrWhiteSpace(baseKey))
            return false;
        Dictionary<string, string>? known;
        try { known = JsonSerializer.Deserialize<Dictionary<string, string>>(baseKey); }
        catch (JsonException) { return false; }
        if (known is null)
            return false;
        var now = Of(current);
        // prepay намеренно не сравниваем: это деньги кассы, сервер их не двигает, расхождение не считается чужой правкой.
        return now.Where(p => p.Key != "prepay").All(p => known.TryGetValue(p.Key, out var k) && k == p.Value);
    }
}

/// <summary>Страница ответа GET api/main/repair-orders/.</summary>
public sealed record RepairPage(IReadOnlyList<RepairServerRow> Rows, string? NextSince, bool HasMore);

/// <summary>2026-11-26 (Умар, этап 2): чтение изменений с сервера — GET api/main/repair-orders/?updated_since=&amp;page_size=200: при открытии окна «Ремонт» и раз в 1–2 минуты, пока оно открыто;
/// курсор next_since хранится в файле квитанций (то есть на компанию); has_more — следующая страница. Чужие квитанции (с другой кассы, из программы владельца, мобилки) попадают в файл
/// без дублей (RepairOrderStore.MergeServerRows); свои квитанции с неотправленными правками не затираются — их защищает base_updated_at / stale_version.</summary>
public static class RepairServerSync
{
    public const string FirstSince = "2000-01-01T00:00:00Z";
    private const int PageSize = 200;
    private const int MaxPagesPerRun = 25;
    private static readonly SemaphoreSlim PullGate = new(1, 1);

    /// <summary>Разбор страницы: строки лежат в массиве results (иначе items / orders / data) или ответ сам массив; next_since и has_more — как в ТЗ.</summary>
    public static RepairPage ParsePage(JsonElement d)
    {
        var rows = new List<RepairServerRow>();
        JsonElement? list = d.ValueKind == JsonValueKind.Array ? d : null;
        if (list is null && d.ValueKind == JsonValueKind.Object)
            foreach (var name in new[] { "results", "items", "orders", "data" })
                if (d.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array) { list = arr; break; }
        if (list is { } l)
            foreach (var item in l.EnumerateArray())
                if (RepairServerRow.Parse(item) is { } row)
                    rows.Add(row);
        string? next = null;
        var more = false;
        if (d.ValueKind == JsonValueKind.Object)
        {
            if (d.TryGetProperty("next_since", out var ns) && ns.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ns.GetString()))
                next = ns.GetString();
            more = d.TryGetProperty("has_more", out var hm) && hm.ValueKind == JsonValueKind.True;
        }
        return new RepairPage(rows, next, more);
    }

    /// <summary>Прочитать всё новое и слить в файл. <paramref name="fetch"/> — запрос GET (в бою — клиент API). Возвращает, сколько квитанций добавлено или обновлено. Не бросает: ошибка сети — 0 (повтор в следующий раз), 403/404 — пауза.</summary>
    public static async Task<int> PullAsync(RepairOrderStore store, Func<Dictionary<string, string>, CancellationToken, Task<JsonElement>> fetch, CancellationToken ct = default)
    {
        if (!await PullGate.WaitAsync(0, ct).ConfigureAwait(false))
            return 0;
        var changed = 0;
        try
        {
            var since = store.Load().NextSince;
            if (string.IsNullOrWhiteSpace(since))
                since = FirstSince;
            var pageNo = 1;
            for (var i = 0; i < MaxPagesPerRun; i++)
            {
                var query = new Dictionary<string, string> { ["updated_since"] = since, ["page_size"] = PageSize.ToString(CultureInfo.InvariantCulture) };
                if (pageNo > 1)
                    query["page"] = pageNo.ToString(CultureInfo.InvariantCulture);
                var page = ParsePage(await fetch(query, ct).ConfigureAwait(false));
                var next = page.NextSince ?? page.Rows.Select(r => r.UpdatedAt).Where(u => u.Length > 0).OrderBy(u => u, StringComparer.Ordinal).LastOrDefault();
                if (string.IsNullOrWhiteSpace(next) || !RepairServerRow.IsNewer(next, since))
                    next = since; // курсор не отодвигаем назад
                changed += store.MergeServerRows(page.Rows, next);
                if (!page.HasMore)
                    break;
                if (next != since) { since = next; pageNo = 1; }
                else pageNo++; // курсор не сдвинулся — читаем следующую страницу того же запроса
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            var kind = RepairStatusPublic.Classify(ex);
            if (kind is RepairSyncKind.Forbidden or RepairSyncKind.Missing or RepairSyncKind.RateLimited)
                RepairStatusPublic.Pause(kind);
            var code = ex is ApiException { StatusCode: { } s } ? $"HTTP {s}" : ex.GetType().Name;
            PosLogger.Log($"Ремонт: изменения с сервера не прочитаны ({code}), повтор позже.", "WARNING");
            // 2026-11-26 (живая проверка NBS): сервер отвечает 400 validation_error на курсор не в ISO 8601 — испорченный курсор иначе навсегда останавливал чтение; читаем всё заново (без дублей — MergeServerRows).
            if (ex is ApiException { StatusCode: 400, Payload: { ValueKind: JsonValueKind.Object } p } && p.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("updated_since", out _))
            {
                try { store.MergeServerRows(Array.Empty<RepairServerRow>(), FirstSince); }
                catch (Exception rx) when (rx is IOException or UnauthorizedAccessException) { PosLogger.Log($"Ремонт: курсор не сброшен ({rx.GetType().Name}).", "WARNING"); }
            }
        }
        finally
        {
            PullGate.Release();
        }
        return changed;
    }

    /// <summary>Чтение в бою: клиент API текущей учётной записи; без признака сервера, при паузе (403/404/429) или без входа — ничего.</summary>
    public static Task<int> PullAsync(RepairOrderStore store, CancellationToken ct = default)
    {
        if (!RepairStatusPublic.Enabled || RepairStatusPublic.IsPaused || RepairStatusPublic.Api() is not { } api)
            return Task.FromResult(0);
        return PullAsync(store, async (q, token) => await api.RequestAsync(System.Net.Http.HttpMethod.Get, RepairStatusPublic.ApiPath, null, q, token, TimeSpan.FromSeconds(20)).ConfigureAwait(false), ct);
    }

    /// <summary>В фоне; <paramref name="done"/> получает число обновлённых квитанций (окно перечитывает список).</summary>
    public static void PullInBackground(RepairOrderStore store, Action<int>? done = null)
    {
        if (!RepairStatusPublic.Enabled)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var n = await PullAsync(store).ConfigureAwait(false);
                if (n > 0)
                    done?.Invoke(n);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Ремонт: чтение изменений не удалось ({ex.GetType().Name}).", "WARNING");
            }
        });
    }
}
