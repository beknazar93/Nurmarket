using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services.Fiscal;

/// <summary>Исход записи фискальных реквизитов на сервер.</summary>
public enum FiscalWriteOutcome
{
    /// <summary>200/201 — записано.</summary>
    Ok,
    /// <summary>200 с replayed:true — те же реквизиты уже записаны (повтор безопасен).</summary>
    Replayed,
    /// <summary>409 fiscal_already_set — у accepted другие реквизиты (в <see cref="FiscalWriteResult.Current"/> то, что на сервере). Требует внимания человека.</summary>
    AlreadySet,
    /// <summary>409 sale_not_completed — продажа ещё не проведена на сервере (чек в очереди): повторить позже.</summary>
    SaleNotCompleted,
    /// <summary>409 return_voided — возврат аннулирован, писать некуда.</summary>
    ReturnVoided,
    /// <summary>409 fiscal_mode_server — пробивает сервер, касса не пишет.</summary>
    ModeServer,
    /// <summary>409 fiscal_locked.</summary>
    Locked,
    /// <summary>400 validation_error.</summary>
    ValidationError,
    /// <summary>404 — продажи/возврата нет.</summary>
    NotFound,
    /// <summary>Нет связи, таймаут, 5xx, 429 — повторить позже.</summary>
    Unavailable,
    /// <summary>Другая ошибка сервера (401/403/прочие 4xx).</summary>
    Rejected,
}

public sealed record FiscalWriteResult(FiscalWriteOutcome Outcome, string Message, FiscalRequisites? Current = null, string? Fields = null, int? HttpStatus = null)
{
    /// <summary>Реквизиты на сервере — дело сделано (записано или уже было).</summary>
    public bool IsDone => Outcome is FiscalWriteOutcome.Ok or FiscalWriteOutcome.Replayed;

    /// <summary>Имеет смысл повторить позже без участия человека.</summary>
    public bool RetryLater => Outcome is FiscalWriteOutcome.Unavailable or FiscalWriteOutcome.SaleNotCompleted;
}

/// <summary>Строка отчёта «Продажи без фискального чека».</summary>
public sealed record FiscalReportRow(string Id, string Number, DateTime? CreatedAt, decimal Total, string Cashier, string PaymentMethod, FiscalStatus Status, string? Error);

public sealed record FiscalReportPage(IReadOnlyList<FiscalReportRow> Rows, int Count, int Page, int PageSize, bool HasNext);

/// <summary>Форма «Налоги и ККМ» (PUT settings/fiscal/): режим в форме НЕ меняется — его определяет сервер (capabilities.fiscal.mode).</summary>
public sealed record FiscalSettingsForm(bool FiscalEnabled, string TaxRegime, bool VatPayer, IReadOnlyList<decimal> VatRates, decimal? NspRate);

/// <summary>2026-11-14 (П0-1, ответ Умара 13.11 вечер): клиент сервера NurCRM для фискализации — запись реквизитов, настройки «Налоги и ККМ», отчёт «Продажи без фискального чека».
/// Сюда НЕ входит связь с eKassa (её нет без документации). Всё идёт через общий <see cref="NurMarketApiClient"/> (токен, ограничитель, журнал сбоев).
/// Пути, не указанные в ответе Умара явно, вынесены в константы и перечислены в отчёте «Фискализация — что готово в кассе» (Ф2, Ф7).</summary>
public static class FiscalApi
{
    /// <summary>POST api/main/pos/sales/{id}/fiscal/ — запись реквизитов продажи (ответ Умара, П0-1).</summary>
    public static string SalePath(string saleId) => "api/main/pos/sales/" + Uri.EscapeDataString(saleId.Trim()) + "/fiscal/";

    /// <summary>POST api/main/pos/returns/{id}/fiscal/ — запись реквизитов возврата.</summary>
    public static string ReturnPath(string returnId) => "api/main/pos/returns/" + Uri.EscapeDataString(returnId.Trim()) + "/fiscal/";

    /// <summary>GET/PUT настроек «Налоги и ККМ». В ответе Умара написано «settings/fiscal/» без префикса; по соседним настройкам компании принят api/main/… (подтвердить, Ф7).</summary>
    public const string SettingsPath = "api/main/settings/fiscal/";

    public const string ReportPath = "api/main/reports/sales-without-fiscal/";

    /// <summary>Предел периода отчёта на сервере: 92 дня.</summary>
    public const int MaxReportDays = 92;

    // ───────────── запись реквизитов ─────────────

    public static async Task<FiscalWriteResult> WriteAsync(NurMarketApiClient api, FiscalDocKind kind, string docId, FiscalRequisites requisites, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(docId))
            return new FiscalWriteResult(FiscalWriteOutcome.NotFound, "id чека не задан");
        if (requisites.Validate() is { } problem)
            return new FiscalWriteResult(FiscalWriteOutcome.ValidationError, problem, Fields: problem);
        var path = kind == FiscalDocKind.Sale ? SalePath(docId) : ReturnPath(docId);
        try
        {
            var response = await api.RequestAsync(HttpMethod.Post, path, requisites.ToRequestBody(), null, ct).ConfigureAwait(false);
            var replayed = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("replayed", out var r) && r.ValueKind == JsonValueKind.True;
            return new FiscalWriteResult(replayed ? FiscalWriteOutcome.Replayed : FiscalWriteOutcome.Ok, "", HttpStatus: 200);
        }
        catch (ApiException ex)
        {
            return FromError(ex);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or JsonException)
        {
            return new FiscalWriteResult(FiscalWriteOutcome.Unavailable, ServerOutageMonitor.UserText(ex));
        }
    }

    /// <summary>Разбор ответа-ошибки сервера на запись реквизитов (код в <c>code</c>).</summary>
    internal static FiscalWriteResult FromError(ApiException ex)
    {
        var status = ex.StatusCode;
        var code = FiscalErrors.CodeOf(ex.Payload);
        if (status == 409)
        {
            switch (code)
            {
                case FiscalErrors.AlreadySet:
                    var current = ex.Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("current", out var cur) ? FiscalRequisites.Parse(cur) : null;
                    return new FiscalWriteResult(FiscalWriteOutcome.AlreadySet, FiscalErrors.AlreadySetText, current, HttpStatus: 409);
                case FiscalErrors.SaleNotCompleted:
                    return new FiscalWriteResult(FiscalWriteOutcome.SaleNotCompleted, FiscalErrors.SaleNotCompletedText, HttpStatus: 409);
                case FiscalErrors.ReturnVoided:
                    return new FiscalWriteResult(FiscalWriteOutcome.ReturnVoided, FiscalErrors.ReturnVoidedText, HttpStatus: 409);
                case FiscalErrors.ModeServer:
                    return new FiscalWriteResult(FiscalWriteOutcome.ModeServer, FiscalErrors.ModeServerText, HttpStatus: 409);
                case FiscalErrors.Locked:
                    return new FiscalWriteResult(FiscalWriteOutcome.Locked, FiscalErrors.LockedSaleText, HttpStatus: 409);
            }
            return new FiscalWriteResult(FiscalWriteOutcome.Rejected, ex.Message, HttpStatus: 409);
        }
        if (status == 400)
        {
            var fields = FiscalErrors.FieldsText(ex.Payload);
            return new FiscalWriteResult(FiscalWriteOutcome.ValidationError, FiscalErrors.ValidationText(fields), Fields: fields, HttpStatus: 400);
        }
        if (status == 404)
            return new FiscalWriteResult(FiscalWriteOutcome.NotFound, ex.Message, HttpStatus: 404);
        if (status is null or 429 or 408 or >= 500)
            return new FiscalWriteResult(FiscalWriteOutcome.Unavailable, ex.Message, HttpStatus: status);
        return new FiscalWriteResult(FiscalWriteOutcome.Rejected, ex.Message, HttpStatus: status);
    }

    // ───────────── настройки «Налоги и ККМ» ─────────────

    /// <summary>GET settings/fiscal/ — настройки как их видит сервер (режим, включено, режим налогообложения, плательщик НДС, ставки, НсП).</summary>
    public static async Task<FiscalCapabilities> GetSettingsAsync(NurMarketApiClient api, CancellationToken ct = default)
    {
        var data = await api.RequestAsync(HttpMethod.Get, SettingsPath, null, null, ct).ConfigureAwait(false);
        return FiscalCapabilities.ParseBlock(data, FiscalSettings.Current.FeatureAvailable);
    }

    /// <summary>Проверка формы до отправки: null — верно, иначе понятная причина на языке интерфейса.</summary>
    public static string? ValidateForm(FiscalSettingsForm form)
    {
        if (form.TaxRegime.Trim().Length > 32)
            return Tr.T("Режим налогообложения — не длиннее 32 знаков.", "Салык режими — 32 белгиден узун эмес.", "Tax regime — at most 32 characters.", "Vergi rejimi — en çok 32 karakter.", "Soliq rejimi — 32 belgidan oshmasin.");
        foreach (var rate in form.VatRates)
            if (rate < 0m || rate > 100m)
                return Tr.T("Ставка НДС — от 0 до 100 процентов.", "ЧКС ставкасы — 0дөн 100 пайызга чейин.", "VAT rate — from 0 to 100 percent.", "KDV oranı — yüzde 0 ile 100 arası.", "QQS stavkasi — 0 dan 100 foizgacha.");
        if (form.NspRate is { } nsp && (nsp < 0m || nsp > 100m))
            return Tr.T("Ставка НсП — от 0 до 100 процентов.", "СтН ставкасы — 0дөн 100 пайызга чейин.", "Sales tax rate — from 0 to 100 percent.", "Satış vergisi oranı — yüzde 0 ile 100 arası.", "Savdo solig'i stavkasi — 0 dan 100 foizgacha.");
        return null;
    }

    /// <summary>Тело PUT settings/fiscal/: fiscal_enabled, tax_regime, vat_payer, vat_rates (строки «12.00»), nsp_rate (строка или null).</summary>
    public static Dictionary<string, object?> SettingsBody(FiscalSettingsForm form) => new()
    {
        ["fiscal_enabled"] = form.FiscalEnabled,
        ["tax_regime"] = form.TaxRegime.Trim(),
        ["vat_payer"] = form.VatPayer,
        ["vat_rates"] = form.VatRates.Select(r => r.ToString("0.00", CultureInfo.InvariantCulture)).ToArray(),
        ["nsp_rate"] = form.NspRate?.ToString("0.00", CultureInfo.InvariantCulture),
    };

    /// <summary>PUT settings/fiscal/ (только владелец и администратор — иначе сервер ответит 403). Вызывается ТОЛЬКО по кнопке «Сохранить». После успеха режим обновляется.</summary>
    public static async Task<FiscalCapabilities> PutSettingsAsync(NurMarketApiClient api, FiscalSettingsForm form, CancellationToken ct = default)
    {
        if (ValidateForm(form) is { } problem)
            throw new ApiException(problem, 400);
        var data = await api.RequestAsync(HttpMethod.Put, SettingsPath, SettingsBody(form), null, ct).ConfigureAwait(false);
        var saved = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("fiscal_enabled", out _)
            ? FiscalCapabilities.ParseBlock(data, FiscalSettings.Current.FeatureAvailable)
            : FiscalSettings.Current with { FiscalEnabled = form.FiscalEnabled, TaxRegime = form.TaxRegime.Trim(), VatPayer = form.VatPayer, VatRates = form.VatRates.ToList(), NspRate = form.NspRate };
        // режим в ответе PUT может не прийти — тогда остаётся прежний (его определяет сервер)
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("mode", out _))
            saved = saved with { Mode = FiscalSettings.Current.Mode };
        FiscalSettings.SetCurrent(saved with { FeatureAvailable = FiscalSettings.Current.FeatureAvailable });
        return saved;
    }

    // ───────────── отчёт «Продажи без фискального чека» ─────────────

    /// <summary>Проверка периода: «с» не позже «по», не больше 92 дней включительно. null — верно, иначе причина.</summary>
    public static string? ValidateReportPeriod(DateTime from, DateTime to)
    {
        if (to.Date < from.Date)
            return Tr.T("Дата «с» позже даты «по».", "«Башталышы» күнү «аягынан» кийин.", "The “from” date is after the “to” date.", "Başlangıç tarihi bitiş tarihinden sonra.", "«Dan» sanasi «gacha» sanasidan keyin.");
        if ((to.Date - from.Date).TotalDays + 1 > MaxReportDays)
            return Tr.T($"Период — не больше {MaxReportDays} дней.", $"Мезгил — {MaxReportDays} күндөн ашпасын.", $"The period is at most {MaxReportDays} days.", $"Dönem en çok {MaxReportDays} gün olabilir.", $"Davr — {MaxReportDays} kundan oshmasin.");
        return null;
    }

    public static Dictionary<string, string> ReportQuery(DateTime from, DateTime to, int page, int pageSize) => new()
    {
        ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["page"] = Math.Max(1, page).ToString(CultureInfo.InvariantCulture),
        ["page_size"] = Math.Clamp(pageSize, 1, 500).ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>GET reports/sales-without-fiscal/?from&amp;to&amp;page&amp;page_size. Ответ — страницы {count, next, results:[…]} или простой массив. Ошибки (400 период, 403, сеть) — исключением: окно показывает StateBlock.</summary>
    public static async Task<FiscalReportPage> GetSalesWithoutFiscalAsync(NurMarketApiClient api, DateTime from, DateTime to, int page, int pageSize, CancellationToken ct = default)
    {
        if (ValidateReportPeriod(from, to) is { } problem)
            throw new ApiException(problem, 400);
        var data = await api.RequestAsync(HttpMethod.Get, ReportPath, null, ReportQuery(from, to, page, pageSize), ct).ConfigureAwait(false);
        return ParseReport(data, page, pageSize);
    }

    internal static FiscalReportPage ParseReport(JsonElement data, int page, int pageSize)
    {
        var rows = new List<FiscalReportRow>();
        var list = data.ValueKind == JsonValueKind.Array ? data
            : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array ? res
            : default;
        if (list.ValueKind == JsonValueKind.Array)
            foreach (var row in list.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Object && ParseRow(row) is { } parsed)
                    rows.Add(parsed);
        var count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : rows.Count;
        var hasNext = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(next.GetString());
        return new FiscalReportPage(rows, count, page, pageSize, hasNext);
    }

    private static FiscalReportRow? ParseRow(JsonElement row)
    {
        var id = Text(row, "id");
        if (id.Length == 0)
            return null;
        var created = Text(row, "created_at") is { Length: > 0 } at && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
            ? dt.ToLocalTime() : (DateTime?)null;
        var total = row.TryGetProperty("total", out var t) ? FiscalCapabilities.ReadDecimal(t) ?? 0m
            : row.TryGetProperty("amount", out var a) ? FiscalCapabilities.ReadDecimal(a) ?? 0m : 0m;
        var cashier = Text(row, "cashier_name");
        if (cashier.Length == 0)
            cashier = Text(row, "cashier_display");
        if (cashier.Length == 0)
            cashier = Text(row, "cashier");
        var method = Text(row, "payment_method");
        var error = Text(row, "fiscal_error");
        return new FiscalReportRow(id, Text(row, "number"), created, total, cashier, method, FiscalStatusNames.Parse(Text(row, "fiscal_status")), error.Length == 0 ? null : error);
    }

    private static string Text(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
            return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => (v.GetString() ?? "").Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        };
    }
}
