using System.Globalization;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services.Billing;

/// <summary>2026-11-19 (сервер «оплата подписки ELQR», ответ Умара 19.11, раздел 2): статус счёта на оплату подписки. Окончательные — Paid / Expired / Cancelled / Error.</summary>
public enum BillingStatus { Pending, Processing, Paid, Expired, Cancelled, Error, Unknown }

/// <summary>Тариф из GET users/billing/config/ → plans. Решать по <see cref="Code"/> (start / standard / custom); <see cref="Name"/> — если сервер дал.</summary>
/// 2026-11-21 (ответ Умара 21.11): PriceYear — price_year (год = 10 месяцев); name может быть объектом {ru,ky,en,tr,uz}.
public sealed record BillingPlanOption(string Code, string? Name, decimal PriceMonth, decimal? PriceYear = null);

/// <summary>GET users/billing/config/: оплата доступна (<see cref="Available"/>), режим test (учебный банк, без денег) или live, комиссия банка, сроки, текущий тариф и дата «подписка до».</summary>
public sealed record BillingConfig(bool Available, string Mode, decimal BankFee, IReadOnlyList<int> Months, string? CurrentPlan, string? CurrentPlanName,
    DateTimeOffset? ValidUntil, IReadOnlyList<BillingPlanOption> Plans, int QrTtlSeconds, IReadOnlyList<string>? Periods = null, int? YearPaidMonths = null)
{
    /// <summary>2026-11-21: сервер умеет годовую оплату (billing/config periods содержит "year" или у тарифа есть price_year): «Месяц / Год», год = 10 месяцев.</summary>
    public bool CanPayYear => Periods?.Contains("year", StringComparer.OrdinalIgnoreCase) == true || Plans.Any(p => p.PriceYear is > 0);

    /// <summary>Учебный банк: кнопка «Учебная оплата (тест)» возможна только здесь (и только владельцу/админу). В боевом режиме её нет вообще.</summary>
    public bool IsTest => string.Equals(Mode, "test", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Строка счёта («Подписка NurCRM, 3 мес.», «Скидка», «Комиссия банка — 30 сом») — показывается как есть.</summary>
public sealed record BillingLine(string Title, decimal Amount);

/// <summary>Счёт на оплату подписки. QR-картинка и ссылка банка — данные платежа: в журнал не пишутся (ToString их не содержит).</summary>
public sealed class BillingInvoice
{
    public string InvoiceId { get; init; } = "";
    public BillingStatus Status { get; init; } = BillingStatus.Unknown;
    public string StatusRaw { get; init; } = "";
    public bool TestMode { get; init; }
    public string? PlanCode { get; init; }
    public string? PlanName { get; init; }
    public int Months { get; init; }
    /// <summary>2026-11-21: "month" | "year" (счёт на год = 12 месяцев по цене 10).</summary>
    public string? Period { get; init; }
    public decimal? PriceMonth { get; init; }
    public decimal? Subtotal { get; init; }
    public decimal? Discount { get; init; }
    public decimal? BankFee { get; init; }
    public decimal Amount { get; init; }
    public IReadOnlyList<BillingLine> Lines { get; init; } = [];
    public string? QrImageBase64 { get; init; }
    public string? QrLink { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? PaidAt { get; init; }
    public DateTimeOffset? ValidUntil { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }

    public bool IsFinal => Status is BillingStatus.Paid or BillingStatus.Expired or BillingStatus.Cancelled or BillingStatus.Error;

    /// <summary>В журнал — только номер и статус (QR, ссылка, суммы не пишем).</summary>
    /// <summary>Копия счёта со статусом «истёк» — когда срок QR вышел, а сервер ещё не закрыл счёт (окно считает его просроченным у себя).</summary>
    public BillingInvoice AsExpired() => new()
    {
        InvoiceId = InvoiceId, Status = BillingStatus.Expired, StatusRaw = "expired", TestMode = TestMode, PlanCode = PlanCode, PlanName = PlanName, Months = Months, Period = Period,
        PriceMonth = PriceMonth, Subtotal = Subtotal, Discount = Discount, BankFee = BankFee, Amount = Amount, Lines = Lines, ExpiresAt = ExpiresAt, CreatedAt = CreatedAt,
    };

    public override string ToString() => $"BillingInvoice {InvoiceId} {StatusRaw}";
}

/// <summary>Разбор ответов сервера (internal — для тестов). Сервер присылает суммы и числом, и строкой «1500.00»; даты — ISO.</summary>
public static class BillingParser
{
    public static readonly int[] DefaultMonths = [1, 3, 6, 12];

    public static BillingStatus ParseStatus(string? raw) => (raw ?? "").Trim().ToLowerInvariant() switch
    {
        "pending" or "created" or "new" => BillingStatus.Pending,
        "processing" => BillingStatus.Processing,
        "paid" or "success" => BillingStatus.Paid,
        "expired" => BillingStatus.Expired,
        "cancelled" or "canceled" => BillingStatus.Cancelled,
        "error" or "failed" => BillingStatus.Error,
        _ => BillingStatus.Unknown,
    };

    public static BillingConfig ParseConfig(JsonElement root)
    {
        var months = new List<int>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("months", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var x in m.EnumerateArray())
                if (Int(x) is { } n && n > 0 && !months.Contains(n))
                    months.Add(n);
        if (months.Count == 0)
            months.AddRange(DefaultMonths);
        months.Sort();

        var plans = new List<BillingPlanOption>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("plans", out var p) && p.ValueKind == JsonValueKind.Array)
            foreach (var item in p.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                if (Str(item, "code") is { } code)
                    plans.Add(new BillingPlanOption(code, TariffApi.LocalName(item, "name") ?? TariffApi.LocalName(item, "plan_name"), Dec(item, "price_month") ?? Dec(item, "price") ?? 0m, Dec(item, "price_year")));

        var periods = new List<string>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("periods", out var pr) && pr.ValueKind == JsonValueKind.Array)
            periods.AddRange(pr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())).Select(x => x.GetString()!.Trim().ToLowerInvariant()));

        // current_plan — код строкой или объект {code, name}
        string? currentCode = null, currentName = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("current_plan", out var cp))
        {
            if (cp.ValueKind == JsonValueKind.String)
                currentCode = cp.GetString()?.Trim();
            else if (cp.ValueKind == JsonValueKind.Object)
            {
                currentCode = Str(cp, "code");
                currentName = Str(cp, "name");
            }
        }
        currentName ??= Str(root, "current_plan_name");

        return new BillingConfig(
            Available: root.ValueKind == JsonValueKind.Object && root.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.True,
            Mode: (Str(root, "mode") ?? "live").ToLowerInvariant(),
            BankFee: Dec(root, "bank_fee") ?? 30m,
            Months: months,
            CurrentPlan: string.IsNullOrWhiteSpace(currentCode) ? null : currentCode,
            CurrentPlanName: currentName,
            ValidUntil: Date(root, "valid_until"),
            Plans: plans,
            QrTtlSeconds: Int(root, "qr_ttl_seconds") ?? 1200,
            Periods: periods,
            YearPaidMonths: Int(root, "year_paid_months"));
    }

    public static BillingInvoice ParseInvoice(JsonElement root)
    {
        var lines = new List<BillingLine>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lines", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var item in l.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
            {
                var title = Str(item, "title") ?? Str(item, "name") ?? Str(item, "label");
                if (title is not null)
                    lines.Add(new BillingLine(title, Dec(item, "amount") ?? 0m));
            }

        var statusRaw = Str(root, "status") ?? "";
        return new BillingInvoice
        {
            InvoiceId = Str(root, "invoice_id") ?? Str(root, "id") ?? "",
            Status = ParseStatus(statusRaw),
            StatusRaw = statusRaw,
            TestMode = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("test_mode", out var tm) && tm.ValueKind == JsonValueKind.True,
            PlanCode = Str(root, "plan"),
            PlanName = Str(root, "plan_name"),
            Months = Int(root, "months") ?? 0,
            Period = Str(root, "period"),
            PriceMonth = Dec(root, "price_month"),
            Subtotal = Dec(root, "subtotal"),
            Discount = Dec(root, "discount"),
            BankFee = Dec(root, "bank_fee"),
            Amount = Dec(root, "amount") ?? 0m,
            Lines = lines,
            QrImageBase64 = Str(root, "qr_image"),
            QrLink = Str(root, "qr_link"),
            ExpiresAt = Date(root, "expires_at"),
            PaidAt = Date(root, "paid_at"),
            ValidUntil = Date(root, "valid_until"),
            Error = Str(root, "error"),
            CreatedAt = Date(root, "created_at"),
        };
    }

    /// <summary>История: массив счетов или страница {results:[…]}.</summary>
    public static IReadOnlyList<BillingInvoice> ParseInvoiceList(JsonElement root)
    {
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r
            : default;
        if (array.ValueKind != JsonValueKind.Array)
            return [];
        return array.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object).Select(ParseInvoice).ToList();
    }

    /// <summary>Картинка QR из base64 (допускается префикс «data:image/png;base64,»). Не base64 — null.</summary>
    public static byte[]? DecodeQr(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        var s = base64.Trim();
        var comma = s.IndexOf(',');
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            s = s[(comma + 1)..];
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    internal static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v))
            return null;
        var text = v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    internal static decimal? Dec(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s is not null && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static int? Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? Int(v) : null;

    private static int? Int(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n
        : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s
        : null;

    /// <summary>Дата или дата-время ISO; без часового пояса — как местное время.</summary>
    internal static DateTimeOffset? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        return s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
    }
}
