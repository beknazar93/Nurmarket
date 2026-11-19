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

/// <summary>2026-11-14: блок <c>capabilities.fiscal</c> — {mode: kassa|off|server, fiscal_enabled, tax_regime, vat_payer, vat_rates:["0.00","12.00"], nsp_rate:"2.00"}.
/// <see cref="FeatureAvailable"/> — в <c>features</c> есть <c>fiscal_kassa</c> (адреса fiscal/ работают). Нет блока или признака — режим Off.</summary>
public sealed record FiscalCapabilities(
    bool FeatureAvailable = false,
    FiscalMode Mode = FiscalMode.Off,
    bool FiscalEnabled = false,
    string TaxRegime = "",
    bool VatPayer = false,
    IReadOnlyList<decimal>? VatRates = null,
    decimal? NspRate = null)
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
            block.TryGetProperty("nsp_rate", out var nsp) ? ReadDecimal(nsp) : null);
    }

    public const string FeatureName = "fiscal_kassa";

    internal static decimal? ReadDecimal(JsonElement x) => x.ValueKind switch
    {
        JsonValueKind.String when decimal.TryParse(x.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        JsonValueKind.Number when x.TryGetDecimal(out var d) => d,
        _ => null,
    };
}
