using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NurMarketKassa.Services.Fiscal;

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): режим фискализации компании — <c>capabilities.fiscal.mode</c>.
/// Off — как сегодня (чек «Товарный», нефискальный); Kassa — чеки пробивает касса (eKassa), реквизиты пишет на сервер; Server — пробивает сервер, касса НЕ пробивает
/// (запись реквизитов с кассы отвечает 409 fiscal_mode_server).</summary>
public enum FiscalMode { Off, Kassa, Server }

/// <summary>2026-11-14: fiscal_status чека/возврата на сервере: none (старые чеки), pending, accepted, failed. Для записи на сервер годятся только pending/accepted/failed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiscalStatus { None, Pending, Accepted, Failed }

/// <summary>2026-11-14: что пробиваем — продажу или возврат.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FiscalDocKind { Sale, Return }

public static class FiscalStatusNames
{
    public static FiscalStatus Parse(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "pending" => FiscalStatus.Pending,
        "accepted" => FiscalStatus.Accepted,
        "failed" => FiscalStatus.Failed,
        _ => FiscalStatus.None, // none, пусто или неизвестное значение — «не пробит»
    };

    /// <summary>Значение для сервера (нижний регистр: pending|accepted|failed|none).</summary>
    public static string ToApi(FiscalStatus status) => status.ToString().ToLowerInvariant();

    /// <summary>Короткая надпись для кассира и владельца: «Пробит / Не пробит / Ошибка / Ожидает».</summary>
    public static string Label(FiscalStatus status) => status switch
    {
        FiscalStatus.Accepted => Tr.T("Пробит", "Бектилди", "Fiscalized", "Fiskal fiş kesildi", "Urildi"),
        FiscalStatus.Failed => Tr.T("Ошибка", "Ката", "Error", "Hata", "Xato"),
        FiscalStatus.Pending => Tr.T("Ожидает", "Күтүүдө", "Pending", "Bekliyor", "Kutilmoqda"),
        _ => Tr.T("Не пробит", "Бектилген эмес", "Not fiscalized", "Fiş kesilmedi", "Urilmagan"),
    };
}

/// <summary>2026-11-14: фискальные реквизиты чека/возврата — контракт совпадает с сервером (поля fiscal_* в pos/sales и pos/returns, тело POST …/fiscal/).
/// Сервер принимает pending/accepted/failed; для accepted обязателен <see cref="DocNumber"/>.</summary>
public sealed record FiscalRequisites(
    FiscalStatus Status = FiscalStatus.None,
    string? DocNumber = null,
    string? MemoryNumber = null,
    string? KkmRegNumber = null,
    string? Sign = null,
    string? QrUrl = null,
    DateTimeOffset? At = null,
    string? KkmShiftId = null,
    string? Error = null)
{
    /// <summary>Длина текста ошибки в запросе: сервер, скорее всего, ограничивает fiscal_error (предел неизвестен — Ф6 в отчёте). Лишнее обрезается, а не ломает запись.</summary>
    public const int MaxErrorLength = 500;

    public static FiscalRequisites None { get; } = new();

    /// <summary>Чек пробит — менять и удалять его нельзя (сервер: 409 fiscal_locked).</summary>
    [JsonIgnore]
    public bool IsLocked => Status == FiscalStatus.Accepted;

    /// <summary>Есть что показать (статус не «none» или есть хотя бы один реквизит).</summary>
    [JsonIgnore]
    public bool HasData => Status != FiscalStatus.None || !string.IsNullOrWhiteSpace(DocNumber) || !string.IsNullOrWhiteSpace(Error);

    /// <summary>Разбор полей fiscal_* из объекта продажи/возврата сервера. Нет полей — <see cref="None"/> (старый чек, старый сервер).</summary>
    public static FiscalRequisites Parse(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return None;
        var status = FiscalStatusNames.Parse(Str(obj, "fiscal_status"));
        // 2026-11-19: режим server — сервер кладёт итог eKassa в объект ekassa {status: ok|error|pending, fd_number…}; поля fiscal_* могут быть пусты. Не показываем «Не пробит» у пробитого чека.
        var ekassaDoc = (string?)null;
        if (status == FiscalStatus.None && obj.TryGetProperty("ekassa", out var ek) && ek.ValueKind == JsonValueKind.Object)
        {
            status = FromEkassaStatus(Str(ek, "status"));
            ekassaDoc = Str(ek, "fd_number");
        }
        var parsed = new FiscalRequisites(
            status,
            Str(obj, "fiscal_doc_number") ?? ekassaDoc,
            Str(obj, "fiscal_memory_number"),
            Str(obj, "kkm_reg_number"),
            Str(obj, "fiscal_sign"),
            Str(obj, "fiscal_qr_url"),
            Str(obj, "fiscal_at") is { } at && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when) ? when : null,
            Str(obj, "kkm_shift_id"),
            Str(obj, "fiscal_error"));
        return parsed;
    }

    /// <summary>Статус eKassa сервера (ok / error / pending) → наш статус. Для фискального возврата (ekassa_fiscal.returns) тот же словарь.</summary>
    public static FiscalStatus FromEkassaStatus(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "ok" or "accepted" or "done" => FiscalStatus.Accepted,
        "error" or "failed" => FiscalStatus.Failed,
        "pending" or "queued" or "processing" => FiscalStatus.Pending,
        _ => FiscalStatus.None,
    };

    /// <summary>Проверка перед отправкой на сервер: статус pending/accepted/failed, у accepted — номер фискального документа. null — всё верно, иначе причина.</summary>
    public string? Validate()
    {
        if (Status == FiscalStatus.None)
            return "fiscal_status: none нельзя записать на сервер";
        if (Status == FiscalStatus.Accepted && string.IsNullOrWhiteSpace(DocNumber))
            return "fiscal_doc_number обязателен для accepted";
        return null;
    }

    /// <summary>Тело POST …/fiscal/: статус всегда, остальные поля — только заполненные (пустые не шлём, чтобы не затирать). Время — ISO 8601.</summary>
    public Dictionary<string, object?> ToRequestBody()
    {
        var body = new Dictionary<string, object?> { ["fiscal_status"] = FiscalStatusNames.ToApi(Status) };
        Put(body, "fiscal_doc_number", DocNumber);
        Put(body, "fiscal_memory_number", MemoryNumber);
        Put(body, "kkm_reg_number", KkmRegNumber);
        Put(body, "fiscal_sign", Sign);
        Put(body, "fiscal_qr_url", QrUrl);
        if (At is { } at)
            body["fiscal_at"] = at.ToString("O", CultureInfo.InvariantCulture);
        Put(body, "kkm_shift_id", KkmShiftId);
        if (!string.IsNullOrWhiteSpace(Error))
        {
            var error = Error!.Trim();
            body["fiscal_error"] = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        }
        return body;
    }

    /// <summary>Те же реквизиты, что у другой записи (для 200 replayed / 409 fiscal_already_set: сервер сравнивает «те же» поля).</summary>
    public bool SameAs(FiscalRequisites? other) =>
        other is not null && Status == other.Status
        && string.Equals(DocNumber ?? "", other.DocNumber ?? "", StringComparison.Ordinal)
        && string.Equals(MemoryNumber ?? "", other.MemoryNumber ?? "", StringComparison.Ordinal)
        && string.Equals(KkmRegNumber ?? "", other.KkmRegNumber ?? "", StringComparison.Ordinal)
        && string.Equals(Sign ?? "", other.Sign ?? "", StringComparison.Ordinal);

    private static void Put(Dictionary<string, object?> body, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            body[key] = value!.Trim();
    }

    private static string? Str(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(), // номер ФД может прийти числом
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
    }
}

/// <summary>2026-11-20 (режим server: ККМ зарегистрирована на сервере, в кассе её нет): сведения о ККМ, если сервер их отдаёт. Адрес запроса и имена полей Умар ещё не подтвердил
/// (вопрос отправлен 20.11), поэтому разбор ТОЛЬКО терпимый: берём ненулевые поля с известными именами (kkm_*, serial, reg_number, fm_*, ofd_inn…) из блока fiscal
/// и из вложенного объекта kkm / ekassa / kassa; ничего не выдумываем — нет полей, нет и карточки.</summary>
public sealed record FiscalKkmInfo(
    string? Serial = null,
    string? RegNumber = null,
    string? FmNumber = null,
    string? FmValidUntil = null,
    string? OfdInn = null,
    string? Version = null,
    bool? ShiftOpen = null,
    string? LastReceipt = null)
{
    /// <summary>Есть хотя бы одно сведение.</summary>
    [JsonIgnore]
    public bool HasData => Serial is not null || RegNumber is not null || FmNumber is not null || FmValidUntil is not null || OfdInn is not null
        || Version is not null || ShiftOpen is not null || LastReceipt is not null;

    private static readonly string[] Nested = { "kkm", "ekassa", "kassa", "cash_register", "kkm_info" };

    /// <summary>Разбор блока fiscal (или ответа settings/fiscal/): null — сведений о ККМ нет.</summary>
    public static FiscalKkmInfo? Parse(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return null;
        var sources = new List<JsonElement> { block };
        foreach (var name in Nested)
            if (block.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object)
                sources.Add(inner);
        string? Pick(params string[] keys)
        {
            foreach (var src in sources)
                foreach (var key in keys)
                    if (Text(src, key) is { } v)
                        return v;
            return null;
        }
        var shift = (bool?)null;
        foreach (var src in sources)
        {
            foreach (var key in new[] { "shift_open", "shift_opened", "kkm_shift_open", "is_shift_open" })
                if (src.TryGetProperty(key, out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    shift ??= b.ValueKind == JsonValueKind.True;
            if (shift is null && src.TryGetProperty("shift", out var sh) && sh.ValueKind == JsonValueKind.Object && Text(sh, "status") is { } st)
                shift = st.Equals("open", StringComparison.OrdinalIgnoreCase) ? true : st.Equals("closed", StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        var info = new FiscalKkmInfo(
            Pick("kkm_serial", "kkm_serial_number", "serial_number", "serial", "kkm_zn"),
            Pick("kkm_reg_number", "reg_number", "registration_number", "kkm_rn"),
            Pick("fm_number", "fiscal_memory_number", "kkm_fm_number", "fm_serial", "fm"),
            Pick("fm_valid_until", "fm_expires_at", "fm_expiry", "fm_end_date", "kkm_fm_valid_until"),
            Pick("ofd_inn", "kkm_ofd_inn"),
            Pick("kkm_version", "version"),
            shift,
            Pick("last_receipt_number", "last_fiscal_receipt", "last_receipt"));
        return info.HasData ? info : null;
    }

    /// <summary>Строки для карточки: ключ (имя элемента), подпись, значение. Только заполненные.</summary>
    public IEnumerable<(string Key, string Label, string Value)> Rows()
    {
        if (Serial is not null)
            yield return ("Serial", Tr.T("ЗН ККМ", "ККМ зав. №", "Serial no.", "Seri no", "KKM zav. №"), Serial);
        if (RegNumber is not null)
            yield return ("Reg", Tr.T("РН ККМ", "ККМ рег. №", "Reg. no.", "Kayıt no", "KKM ro'yx. №"), RegNumber);
        if (FmNumber is not null)
            yield return ("Fm", Tr.T("ФМ", "ФМ", "Fiscal memory", "Mali bellek", "FM"), FmNumber);
        if (FmValidUntil is not null)
            yield return ("FmUntil", Tr.T("Срок ФМ", "ФМ мөөнөтү", "Memory valid until", "Bellek süresi", "FM muddati"), FormatDate(FmValidUntil));
        if (OfdInn is not null)
            yield return ("Ofd", Tr.T("ОФД (ИНН)", "ОФД (ИНН)", "OFD (TIN)", "OFD (VKN)", "OFD (STIR)"), OfdInn);
        if (Version is not null)
            yield return ("Version", Tr.T("Версия", "Версия", "Version", "Sürüm", "Versiya"), Version);
        if (ShiftOpen is { } open)
            yield return ("Shift", Tr.T("Смена", "Смена", "Shift", "Vardiya", "Smena"), open
                ? Tr.T("открыта", "ачык", "open", "açık", "ochiq")
                : Tr.T("закрыта", "жабык", "closed", "kapalı", "yopiq"));
        if (LastReceipt is not null)
            yield return ("LastReceipt", Tr.T("Последний чек", "Акыркы чек", "Last receipt", "Son fiş", "Oxirgi chek"), LastReceipt);
    }

    /// <summary>Дата «2029-11-18» или ISO → «18.11.2029»; не дата — как пришло.</summary>
    internal static string FormatDate(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : value;

    private static string? Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
    }

    /// <summary>Для записи на диск (последний известный блок, как остальные поля fiscal-settings.json): имена те же, что разбирает <see cref="Parse"/>.</summary>
    internal Dictionary<string, object?> ToBlock()
    {
        var d = new Dictionary<string, object?>();
        if (Serial is not null) d["kkm_serial"] = Serial;
        if (RegNumber is not null) d["kkm_reg_number"] = RegNumber;
        if (FmNumber is not null) d["fm_number"] = FmNumber;
        if (FmValidUntil is not null) d["fm_valid_until"] = FmValidUntil;
        if (OfdInn is not null) d["ofd_inn"] = OfdInn;
        if (Version is not null) d["kkm_version"] = Version;
        if (ShiftOpen is { } s) d["shift_open"] = s;
        if (LastReceipt is not null) d["last_receipt_number"] = LastReceipt;
        return d;
    }
}

/// <summary>2026-11-14: блок <c>capabilities.fiscal</c> — {mode: kassa|off|server, fiscal_enabled, tax_regime, vat_payer, vat_rates:["0.00","12.00"], nsp_rate:"2.00"}.
/// <see cref="FeatureAvailable"/> — в <c>features</c> есть <c>fiscal_kassa</c> (адреса fiscal/ работают). Нет блока или признака — режим Off.
/// <see cref="Kkm"/> (2026-11-20) — сведения о ККМ, если сервер их отдал (null — не отдаёт).</summary>
public sealed record FiscalCapabilities(
    bool FeatureAvailable = false,
    FiscalMode Mode = FiscalMode.Off,
    bool FiscalEnabled = false,
    string TaxRegime = "",
    bool VatPayer = false,
    IReadOnlyList<decimal>? VatRates = null,
    decimal? NspRate = null,
    FiscalKkmInfo? Kkm = null)
{
    public static FiscalCapabilities Off { get; } = new();

    /// <summary>Ставки НДС компании (пустой список — не заданы).</summary>
    [JsonIgnore]
    public IReadOnlyList<decimal> Rates => VatRates ?? Array.Empty<decimal>();

    /// <summary>Фискальные надписи и поля показываются: сервер знает фискализацию и режим не «выключено».</summary>
    [JsonIgnore]
    // 2026-11-19 (сверка с работой eKassa «режим server»): режим server сервер называет сам (блок fiscal), признак fiscal_kassa для него не обязателен — иначе печать фискального
    // чека сервера (FiscalServerReceipt) зависела бы от имени признака, которого сервер может и не присылать. Режим kassa (пробивает касса, наши fiscal/-адреса) по-прежнему требует fiscal_kassa.
    public bool ShowsFiscalUi => Mode != FiscalMode.Off && (FeatureAvailable || Mode == FiscalMode.Server);

    /// <summary>Пробивает сервер (mode=server): касса не пробивает, печатает фискальный текст сервера.</summary>
    [JsonIgnore]
    public bool ServerFiscalizes => Mode == FiscalMode.Server;

    /// <summary>Касса сама должна пробивать чек: режим kassa, фискализация включена и сервер знает адреса fiscal/. Режим server и off — касса НЕ пробивает.</summary>
    [JsonIgnore]
    public bool KassaMustFiscalize => FeatureAvailable && Mode == FiscalMode.Kassa && FiscalEnabled;

    public static FiscalMode ParseMode(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
    {
        "kassa" => FiscalMode.Kassa,
        "server" => FiscalMode.Server,
        _ => FiscalMode.Off, // off, пусто и неизвестное значение — как сегодня
    };

    public static string ModeToApi(FiscalMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>Разбор ответа GET api/main/capabilities/. <paramref name="features"/> — уже разобранный набор признаков (<c>ServerCapabilities.Parse</c>).</summary>
    public static FiscalCapabilities Parse(JsonElement root, IReadOnlySet<string> features)
    {
        var available = features.Contains(FeatureName);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fiscal", out var block))
            return new FiscalCapabilities(available);
        return ParseBlock(block, available);
    }

    /// <summary>Разбор самого блока fiscal (и ответа GET settings/fiscal/ — те же поля).</summary>
    public static FiscalCapabilities ParseBlock(JsonElement block, bool featureAvailable)
    {
        if (block.ValueKind != JsonValueKind.Object)
            return new FiscalCapabilities(featureAvailable);
        var rates = new List<decimal>();
        if (block.TryGetProperty("vat_rates", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var x in arr.EnumerateArray())
                if (ReadDecimal(x) is { } r && !rates.Contains(r))
                    rates.Add(r);
        return new FiscalCapabilities(
            featureAvailable,
            block.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? ParseMode(m.GetString()) : FiscalMode.Off,
            block.TryGetProperty("fiscal_enabled", out var en) && en.ValueKind == JsonValueKind.True,
            block.TryGetProperty("tax_regime", out var tr) && tr.ValueKind == JsonValueKind.String ? (tr.GetString() ?? "").Trim() : "",
            block.TryGetProperty("vat_payer", out var vp) && vp.ValueKind == JsonValueKind.True,
            rates,
            block.TryGetProperty("nsp_rate", out var nsp) ? ReadDecimal(nsp) : null,
            FiscalKkmInfo.Parse(block));
    }

    public const string FeatureName = "fiscal_kassa";

    internal static decimal? ReadDecimal(JsonElement x) => x.ValueKind switch
    {
        JsonValueKind.String when decimal.TryParse(x.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        JsonValueKind.Number when x.TryGetDecimal(out var d) => d,
        _ => null,
    };
}
