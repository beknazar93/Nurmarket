using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-13: локальное хранилище квитанций «Ремонта и сервиса». Сервер ремонт пока не умеет (features.repair = false), поэтому данные — файл
/// repair-orders.json в %LOCALAPPDATA%\NurMarketKassa (схема pos_local.db не меняется), общий для кассы и программы владельца на одном ПК; запись — под
/// межпроцессным замком AtomicFile.Update; файл — данные компании (CompanyStoreRegistry). Между компьютерами НЕ синхронизируется (до серверного ТЗ).
/// Деньги стор не трогает: предоплата, возврат и оплата работ проводятся кассой существующими путями, здесь только записываются суммы.</summary>
public sealed class RepairOrderStore
{
    public const string FileName = "repair-orders.json";
    private const int MaxLog = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Func<DateTime> _now;

    public RepairOrderStore(string path, Func<DateTime>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTime.Now);
    }

    public static RepairOrderStore Default => new(Path.Combine(AppPaths.KassaLocal, FileName));

    public RepairData Load() => Parse(AtomicFile.ReadAllText(_path));

    private static RepairData Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new RepairData();
        try
        {
            var d = JsonSerializer.Deserialize<RepairData>(text, Json) ?? new RepairData();
            d.Orders ??= new(); // 2026-11-26 (стресс-тест): "Orders": null в файле — не падение в каждом методе
            d.Orders.RemoveAll(o => o is null);
            return d;
        }
        catch (JsonException) { return new RepairData(); }
    }

    private void Mutate(Func<RepairData, bool> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.Update(_path, text =>
        {
            var data = Parse(text);
            // 2026-11-26 (стресс-тест ремонта): файл не читается (испорчен, чужой формат) — перед перезаписью кладём копию рядом (.bad-время), иначе все квитанции молча терялись бы.
            if (!string.IsNullOrWhiteSpace(text) && data.Orders.Count == 0 && !LooksEmpty(text))
                BackupUnreadable(text);
            return change(data) ? JsonSerializer.Serialize(data, Json) : null;
        });
    }

    /// <summary>Пустой по смыслу файл: разобрался и в нём просто нет квитанций (не повод для копии).</summary>
    private static bool LooksEmpty(string text)
    {
        try { return JsonSerializer.Deserialize<RepairData>(text, Json) is not null; }
        catch (JsonException) { return false; }
        catch (NotSupportedException) { return false; }
    }

    private void BackupUnreadable(string text)
    {
        try
        {
            var bad = _path + ".bad-" + _now().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            if (!File.Exists(bad))
                File.WriteAllText(bad, text);
            PosLogger.Log($"Ремонт: файл квитанций не читается — копия сохранена ({Path.GetFileName(bad)}).", "WARNING");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Ремонт: файл квитанций не читается, копию сохранить не удалось ({ex.GetType().Name}).", "WARNING");
        }
    }

    public List<RepairOrder> All() => Load().Orders.OrderByDescending(o => o.Number).ToList();

    public RepairOrder? Find(string id) => Load().Orders.FirstOrDefault(o => o.Id == id);

    /// <summary>Новая квитанция: присваивает номер и время, статус «Принят». Возвращает сохранённую копию.</summary>
    /// <summary>2026-11-26 (этап 2): <paramref name="serverNumber"/> — номер временный, настоящий выдаст сервер при отправке (по умолчанию — когда у сервера есть «Ремонт» и он не отвечал по-старому).</summary>
    public RepairOrder Create(RepairOrder draft, string by, bool? serverNumber = null)
    {
        RepairOrder? saved = null;
        Mutate(d =>
        {
            d.LastNumber = Math.Max(d.LastNumber, d.Orders.Count == 0 ? 0 : d.Orders.Max(o => o.Number)) + 1;
            draft.Number = d.LastNumber;
            draft.AcceptedAt = _now();
            draft.AcceptedBy = by ?? "";
            draft.Status = RepairStatus.Accepted;
            draft.Serial = ImeiValidator.Normalize(draft.Serial);
            draft.Estimate = RepairRules.Money(draft.Estimate);
            draft.Prepay = RepairRules.Money(draft.Prepay);
            draft.Log.Add(new RepairLogEntry { At = draft.AcceptedAt, By = by ?? "", Action = "accept" });
            draft.Rev = 1;
            draft.SyncedRev = -1;
            draft.TempNumber = serverNumber ?? (RepairStatusPublic.Enabled && !RepairStatusPublic.LegacyNumbers);
            d.Orders.Add(draft);
            saved = draft;
            return true;
        });
        return saved!;
    }

    /// <summary>Правка квитанции под замком. false — квитанции нет или правка вернула false (ничего не меняли).</summary>
    public bool Update(string id, Func<RepairOrder, bool> change, string by, string action)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || !change(o))
                return false;
            o.Log.Add(new RepairLogEntry { At = _now(), By = by ?? "", Action = action });
            if (o.Log.Count > MaxLog)
                o.Log.RemoveRange(0, o.Log.Count - MaxLog);
            o.Rev++; // 2026-11-26: правка — заказ снова ждёт отправки серверу (RepairStatusPublic)
            done = true;
            return true;
        });
        return done;
    }

    /// <summary>2026-11-26: сервер принял заказ. Отметка «отправлено» ставится, только если за время запроса заказ не менялся (rev тот же); номер и адрес страницы статуса
    /// запоминаем всегда. В журнал заказа не пишется.</summary>
    public void MarkSynced(string id, int rev, string? serverNumber, string? publicUrl, string? notified = null)
    {
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            return o is not null && ApplyCommon(o, rev, serverNumber, publicUrl, notified);
        });
    }

    private bool ApplyCommon(RepairOrder o, int rev, string? serverNumber, string? publicUrl, string? notified)
    {
        var changed = false;
        if (o.Rev == rev && o.SyncedRev != rev) { o.SyncedRev = rev; changed = true; }
        if (!string.IsNullOrWhiteSpace(serverNumber) && o.ServerNumber != serverNumber) { o.ServerNumber = serverNumber!; changed = true; }
        if (!string.IsNullOrWhiteSpace(publicUrl) && o.PublicUrl != publicUrl) { o.PublicUrl = publicUrl!; changed = true; }
        // 2026-11-26 (Умар 16:20): ответ про «Ремонт готов» в Telegram; пустого ответа не затираем (сервер пишет клиенту один раз на заказ). sent — клиент уже оповещён, отметка «Клиенту написали».
        if (!string.IsNullOrWhiteSpace(notified) && o.NotifiedStatus != notified) { o.NotifiedStatus = notified!; changed = true; }
        if (notified == "sent" && o.NotifiedAt is null) { o.NotifiedAt = _now(); changed = true; }
        return changed;
    }

    /// <summary>2026-11-26 (этап 2): итог успешного PUT: отметка «отправлено» (если заказ не менялся), метка updated_at (для base_updated_at), id на сервере, хэш содержимого (BaseKey);
    /// номер: если мы просили сервер выдать его (<paramref name="askedServerNumber"/>) — берём number/code из ответа и снимаем «временный»; если ответа с номером нет (старый сервер) — остаётся наш.</summary>
    public void ApplySyncResult(string id, int rev, RepairSyncResult r, string? baseKey = null, bool askedServerNumber = false)
    {
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null)
                return false;
            var changed = ApplyCommon(o, rev, r.ServerNumber, r.PublicUrl, r.Notified);
            if (!string.IsNullOrWhiteSpace(r.ServerId) && o.ServerId != r.ServerId) { o.ServerId = r.ServerId!; changed = true; }
            if (!string.IsNullOrWhiteSpace(r.UpdatedAt) && o.UpdatedAt != r.UpdatedAt) { o.UpdatedAt = r.UpdatedAt!; changed = true; }
            if (!string.IsNullOrWhiteSpace(baseKey) && o.BaseKey != baseKey) { o.BaseKey = baseKey!; changed = true; }
            if (o.TempNumber)
            {
                if (askedServerNumber && r.Number is > 0)
                    AdoptCore(d, o, r.Number!.Value, r.Code, rememberOld: false);
                o.TempNumber = false;
                changed = true;
            }
            return changed;
        });
    }

    /// <summary>Сменить номер квитанции на тот, что предложил сервер (number_conflict): старый код запоминается как «напечатанный», если квитанцию уже печатали.</summary>
    public bool AdoptNumber(string id, int number, string? code)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || number <= 0)
                return false;
            done = AdoptCore(d, o, number, code, rememberOld: true);
            return done;
        });
        return done;
    }

    private static bool AdoptCore(RepairData d, RepairOrder o, int number, string? code, bool rememberOld)
    {
        var newCode = string.IsNullOrWhiteSpace(code) || code!.Trim() == RepairRules.Code(number) ? "" : code.Trim();
        var changed = o.TempNumber;
        if (o.Number != number || o.ServerCode != newCode)
        {
            var old = RepairRules.Code(o);
            o.Number = number;
            o.ServerCode = newCode;
            if (rememberOld && string.IsNullOrEmpty(o.PrintedCode))
                o.PrintedCode = old;
            changed = true;
        }
        o.TempNumber = false;
        d.LastNumber = Math.Max(d.LastNumber, number);
        return changed;
    }

    /// <summary>Квитанция напечатана с этим кодом (если он потом сменится на серверный, карточка покажет оба). В журнал и в очередь отправки не попадает.</summary>
    public void MarkPrinted(string id, string code)
    {
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || o.PrintedCode == code)
                return false;
            o.PrintedCode = code;
            return true;
        });
    }

    /// <summary>2026-11-27 (серверная квитанция): запрос принят сервером — его записи переносятся в BaseKey (что сервер знает), метка updated_at — из ответа. Повтор после обрыва шлёт только остаток.</summary>
    public void ApplyServerStep(string id, IReadOnlyDictionary<string, string> applied, string? updatedAt)
    {
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null)
                return false;
            Dictionary<string, string> key;
            try { key = string.IsNullOrWhiteSpace(o.BaseKey) ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(o.BaseKey) ?? new(); }
            catch (JsonException) { key = new(); }
            foreach (var (k, v) in applied)
                key[k] = v;
            var text = JsonSerializer.Serialize(key);
            var changed = o.BaseKey != text;
            o.BaseKey = text;
            if (!string.IsNullOrWhiteSpace(updatedAt) && o.UpdatedAt != updatedAt) { o.UpdatedAt = updatedAt!; changed = true; }
            return changed;
        });
    }

    /// <summary>2026-11-27 (серверная квитанция): сервер отказал (bad_transition, payment_missing…) — квитанция приводится к строке сервера. Деньги кассы не пропадают: если локально «выдан» отменён сервером,
    /// оплата работ возвращается из «оплачено при выдаче» обратно в «ждёт оплаты чека» (иначе кассир взял бы деньги второй раз). Предоплата и возвраты — как на сервере (Apply для серверной квитанции).
    /// В журнал заказа — «server-rejected:код».</summary>
    public bool RevertToServerRow(string id, RepairServerRow row, string reason)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null)
                return false;
            if (o.Status == RepairStatus.Issued && row.Status is { } st && st != RepairStatus.Issued && o.PaidAtIssue > 0)
            {
                o.PaymentInCart = o.PaidAtIssue;
                o.PaidAtIssue = 0;
            }
            if (o.Status == RepairStatus.Issued && row.Status != RepairStatus.Issued)
            {
                o.IssuedAt = null;
                o.IssuedBy = "";
            }
            row.Apply(o, newOrder: false);
            o.BaseKey = RepairContentKey.Serialize(RepairContentKey.Of(row));
            o.SyncedRev = o.Rev;
            o.Log.Add(new RepairLogEntry { At = _now(), By = "", Action = "server-rejected:" + reason });
            if (o.Log.Count > MaxLog)
                o.Log.RemoveRange(0, o.Log.Count - MaxLog);
            done = true;
            return true;
        });
        return done;
    }

    /// <summary>2026-11-27 (серверная квитанция): предоплата принята сервером (POST …/prepay/ — движение в смене и запись в квитанции одной транзакцией). Предоплата — из строки ответа, иначе старая + сумма.
    /// Rev не растёт: отправлять больше нечего.</summary>
    public bool RecordServerPrepay(string id, decimal amount, RepairServerRow? row, string by)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null)
                return false;
            var before = o.Prepay;
            if (row is not null)
                row.Apply(o, newOrder: false);
            if (row is null || o.Prepay == before)
                o.Prepay = RepairRules.Money(before + amount);
            if (row is not null)
            {
                // 2026-11-27: строка ответа — то, что теперь знает сервер; поля, которых в ответе нет, остаются из прежнего BaseKey (иначе они выглядели бы «изменёнными» и ушли бы в PATCH)
                Dictionary<string, string> key;
                try { key = string.IsNullOrWhiteSpace(o.BaseKey) ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(o.BaseKey) ?? new(); }
                catch (JsonException) { key = new(); }
                foreach (var (k, v) in RepairContentKey.Of(row))
                    key[k] = v;
                o.BaseKey = JsonSerializer.Serialize(key);
                if (!string.IsNullOrEmpty(row.UpdatedAt))
                    o.UpdatedAt = row.UpdatedAt;
            }
            o.Log.Add(new RepairLogEntry { At = _now(), By = by ?? "", Action = "prepay" });
            if (o.Log.Count > MaxLog)
                o.Log.RemoveRange(0, o.Log.Count - MaxLog);
            done = true;
            return true;
        });
        return done;
    }

    /// <summary>stale_version: сервер уже знает более позднюю версию, но по содержимому ничего не менялось — берём его метку и отправим ещё раз.</summary>
    public void AdoptUpdatedAt(string id, string updatedAt)
    {
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || o.UpdatedAt == updatedAt)
                return false;
            o.UpdatedAt = updatedAt;
            return true;
        });
    }

    /// <summary>stale_version: запомнить актуальное состояние с сервера; заказ не отправляется, пока кассир не решит (<see cref="KeepMineOverServer"/> / <see cref="TakeServerVersion"/>).</summary>
    public void MarkConflict(string id, string currentJson)
    {
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || string.IsNullOrWhiteSpace(currentJson) || o.ConflictJson == currentJson)
                return false;
            o.ConflictJson = currentJson;
            return true;
        });
    }

    private static RepairServerRow? ConflictRow(RepairOrder o)
    {
        try { return RepairServerRow.Parse(JsonDocument.Parse(o.ConflictJson).RootElement); }
        catch (JsonException) { return null; }
    }

    /// <summary>«Применить мои правки поверх»: берём метку сервера как основу и отправляем свои данные заново (чужая правка будет заменена).</summary>
    public bool KeepMineOverServer(string id, string by)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || !o.HasConflict)
                return false;
            if (ConflictRow(o) is { UpdatedAt.Length: > 0 } row)
                o.UpdatedAt = row.UpdatedAt;
            o.ConflictJson = "";
            if (o.SyncedRev == o.Rev)
                o.Rev++; // правок «в очереди» нет, а отправить надо — ставим в очередь
            o.Log.Add(new RepairLogEntry { At = _now(), By = by ?? "", Action = "keep-mine" });
            done = true;
            return true;
        });
        return done;
    }

    /// <summary>«Взять с сервера»: свои неотправленные правки отбрасываются, квитанция становится такой, как на сервере (деньги кассы — предоплата, возврат, оплата в чеке — остаются).</summary>
    public bool TakeServerVersion(string id, string by)
    {
        var done = false;
        Mutate(d =>
        {
            var o = d.Orders.FirstOrDefault(x => x.Id == id);
            if (o is null || !o.HasConflict)
                return false;
            if (ConflictRow(o) is { } row)
            {
                row.Apply(o, newOrder: false);
                o.BaseKey = RepairContentKey.Serialize(RepairContentKey.Of(row));
                if (row.NumberValue is > 0 and var num)
                    AdoptCore(d, o, num, row.Code, rememberOld: !o.TempNumber);
            }
            o.ConflictJson = "";
            o.SyncedRev = o.Rev;
            o.Log.Add(new RepairLogEntry { At = _now(), By = by ?? "", Action = "take-server" });
            done = true;
            return true;
        });
        return done;
    }

    /// <summary>Слить строки, прочитанные с сервера (GET repair-orders), в файл; <paramref name="nextSince"/> — новый курсор. Без дублей: ключ — external_id (= Id квитанции кассы), иначе id сервера.
    /// Квитанции, созданные на сервере (программа владельца, мобилка; external_id нет), добавляются как «только чтение» (Source = server). Своя квитанция с неотправленными правками или с конфликтом не затирается:
    /// о расхождении скажет сервер (stale_version). Возвращает, сколько квитанций добавлено или изменено.</summary>
    public int MergeServerRows(IReadOnlyList<RepairServerRow> rows, string? nextSince)
    {
        var changed = 0;
        Mutate(d =>
        {
            var dirty = false;
            foreach (var row in rows)
            {
                var o = (row.ExternalId.Length > 0 ? d.Orders.FirstOrDefault(x => x.Id == row.ExternalId) : null)
                    ?? (row.Id.Length > 0 ? d.Orders.FirstOrDefault(x => x.ServerId == row.Id) : null)
                    ?? (row.ExternalId.Length == 0 && row.Id.Length > 0 ? d.Orders.FirstOrDefault(x => x.Id == "srv-" + row.Id) : null);
                if (o is null)
                {
                    var fresh = new RepairOrder
                    {
                        Id = row.ExternalId.Length > 0 ? row.ExternalId : "srv-" + row.Id, ServerId = row.Id, Source = row.ExternalId.Length > 0 ? "kassa" : "server",
                        AcceptedAt = _now(), Rev = 1, SyncedRev = 1,
                    };
                    row.Apply(fresh, newOrder: true);
                    fresh.Number = row.NumberValue is > 0 ? row.NumberValue.Value : d.LastNumber + 1;
                    fresh.ServerCode = !string.IsNullOrWhiteSpace(row.Code) && row.Code != RepairRules.Code(fresh.Number) ? row.Code : "";
                    d.LastNumber = Math.Max(d.LastNumber, fresh.Number);
                    fresh.BaseKey = RepairContentKey.Serialize(RepairContentKey.Of(row));
                    fresh.Log.Add(new RepairLogEntry { At = _now(), By = "", Action = "server-sync" });
                    d.Orders.Add(fresh);
                    changed++;
                    dirty = true;
                    continue;
                }
                if (row.Id.Length > 0 && o.ServerId != row.Id) { o.ServerId = row.Id; dirty = true; }
                if (o.SyncPending || o.HasConflict || !RepairServerRow.IsNewer(row.UpdatedAt, o.UpdatedAt))
                    continue;
                var before = o.Status;
                row.Apply(o, newOrder: false);
                if (o.Status == RepairStatus.Ready && o.ReadyAt is null) o.ReadyAt = _now();
                if (o.Status == RepairStatus.Issued && o.IssuedAt is null) o.IssuedAt = _now();
                if (row.NumberValue is > 0 and var num && (num != o.Number || o.TempNumber || (!string.IsNullOrWhiteSpace(row.Code) && row.Code != RepairRules.Code(o))))
                    AdoptCore(d, o, num, row.Code, rememberOld: !o.TempNumber);
                o.TempNumber = false;
                o.BaseKey = RepairContentKey.Serialize(RepairContentKey.Of(row));
                o.SyncedRev = o.Rev;
                if (before != o.Status)
                    o.Log.Add(new RepairLogEntry { At = _now(), By = "", Action = "server-status-" + RepairStatusPublic.StatusCode(o.Status) });
                changed++;
                dirty = true;
            }
            if (!string.IsNullOrWhiteSpace(nextSince) && d.NextSince != nextSince)
            {
                d.NextSince = nextSince!;
                dirty = true;
            }
            return dirty;
        });
        return changed;
    }

    /// <summary>Перевод статуса «принят → в работе → готов» (итог работ задаётся при «Готов»). Выдача и отмена — отдельные методы (с деньгами).</summary>
    public bool Move(string id, RepairStatus to, string by, decimal? finalPrice = null)
    {
        if (to is RepairStatus.Issued or RepairStatus.Cancelled)
            return false;
        return Update(id, o =>
        {
            if (!RepairRules.CanMove(o.Status, to))
                return false;
            if (to == RepairStatus.Ready)
            {
                o.FinalPrice = RepairRules.Money(finalPrice ?? (o.FinalPrice > 0 ? o.FinalPrice : o.Estimate));
                o.ReadyAt = _now();
            }
            o.Status = to;
            return true;
        }, by, "to-" + to.ToString().ToLowerInvariant());
    }

    /// <summary>Итог работ можно поправить, пока квитанция «Готов» и оплата ещё не ушла в чек.</summary>
    public bool SetFinalPrice(string id, decimal finalPrice, string by) => Update(id, o =>
    {
        if (o.Status != RepairStatus.Ready || o.PaymentInCart > 0 || finalPrice < 0)
            return false;
        o.FinalPrice = RepairRules.Money(finalPrice);
        return true;
    }, by, "final-price");

    /// <summary>Строка оплаты работ добавлена в чек кассы (ждёт «чек оплачен»).</summary>
    public bool MarkPaymentInCart(string id, decimal amount, string by) => Update(id, o =>
    {
        if (o.Status != RepairStatus.Ready || amount <= 0)
            return false;
        o.PaymentInCart = RepairRules.Money(amount);
        return true;
    }, by, "payment-in-cart");

    /// <summary>Строку убрали из чека / чек не оплатили — снова можно добавить оплату.</summary>
    public bool CancelPaymentInCart(string id, string by) => Update(id, o =>
    {
        if (o.PaymentInCart <= 0)
            return false;
        o.PaymentInCart = 0;
        return true;
    }, by, "payment-cancelled");

    /// <summary>Запись возврата клиенту из кассы (отмена или переплата). Сумму проводит касса изъятием, здесь только запись.</summary>
    public bool RecordRefund(string id, decimal amount, string by) => Update(id, o =>
    {
        if (amount <= 0 || RepairRules.Money(o.Refunded + amount) > RepairRules.Money(o.Prepay))
            return false;
        o.Refunded = RepairRules.Money(o.Refunded + amount);
        return true;
    }, by, "refund");

    /// <summary>Выдача: только из «Готов»; доплата (если есть) уже проведена продажей услуги и подтверждена («чек оплачен»); переплата предоплаты возвращена клиенту.</summary>
    public bool Issue(string id, string by) => Update(id, o =>
    {
        if (o.Status != RepairStatus.Ready)
            return false;
        var due = RepairRules.AmountDue(o.FinalPrice, o.Prepay);
        if (due > 0 && o.PaymentInCart + 0.001m < due)
            return false;
        if (o.Refunded + 0.001m < RepairRules.Overpaid(o.FinalPrice, o.Prepay))
            return false;
        o.PaidAtIssue = due > 0 ? RepairRules.Money(o.PaymentInCart) : 0m;
        o.PaymentInCart = 0;
        o.IssuedAt = _now();
        o.IssuedBy = by ?? "";
        o.Status = RepairStatus.Issued;
        return true;
    }, by, "issue");

    /// <summary>Отмена приёма. Нельзя, пока не возвращена предоплата или в чеке висит строка оплаты.</summary>
    public bool Cancel(string id, string by) => Update(id, o =>
    {
        if (!RepairRules.CanMove(o.Status, RepairStatus.Cancelled) || RepairRules.PrepayToRefund(o) > 0 || o.PaymentInCart > 0)
            return false;
        o.Status = RepairStatus.Cancelled;
        return true;
    }, by, "cancel");

    public bool MarkNotified(string id, string by) => Update(id, o => { o.NotifiedAt = _now(); return true; }, by, "notified");

    /// <summary>Поиск по номеру, клиенту, телефону, устройству, серийному номеру (хвост номера тоже).</summary>
    public static IEnumerable<RepairOrder> Filter(IEnumerable<RepairOrder> all, string? query, RepairStatus? status)
    {
        var q = (query ?? "").Trim();
        var qs = ImeiValidator.Normalize(q);
        var digits = q.TrimStart('Р', 'р', 'P', 'p', '-').TrimStart('0');
        return all.Where(o => status is null || o.Status == status).Where(o =>
            q.Length == 0
            || RepairRules.Code(o).Contains(q, StringComparison.OrdinalIgnoreCase)
            || o.PrintedCode.Contains(q, StringComparison.OrdinalIgnoreCase) // 2026-11-26: на бумаге у клиента может быть прежний (временный) код
            || (digits.Length > 0 && o.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) == digits)
            || o.ClientName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || o.ClientPhone.Contains(q, StringComparison.OrdinalIgnoreCase)
            || o.Device.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (qs.Length >= 4 && o.Serial.Contains(qs, StringComparison.OrdinalIgnoreCase)));
    }
}
