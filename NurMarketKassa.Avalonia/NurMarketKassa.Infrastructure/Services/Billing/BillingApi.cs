using System.Net.Http;
using System.Text.Json;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services.Billing;

/// <summary>Что делает окно «Подписка» — интерфейс нужен, чтобы окно проверялось без сервера (заглушка в UiTests).</summary>
public interface IBillingApi
{
    Task<BillingConfig> GetConfigAsync(CancellationToken ct = default);
    /// <summary>POST users/billing/invoices/ {months, plan_code?} с заголовком Idempotency-Key: один ключ на попытку, повтор с тем же ключом сервер отдаёт тем же счётом.</summary>
    Task<BillingInvoice> CreateInvoiceAsync(int months, string? planCode, string idempotencyKey, CancellationToken ct = default);

    /// <summary>2026-11-21 (ответ Умара 21.11): period "year" — счёт на 12 месяцев по цене 10 (строка «Скидка (год = 10 месяцев)» приходит в lines). По умолчанию — прежний счёт по месяцам.</summary>
    Task<BillingInvoice> CreateInvoiceAsync(int months, string? planCode, string? period, string idempotencyKey, CancellationToken ct = default) =>
        CreateInvoiceAsync(months, planCode, idempotencyKey, ct);
    Task<BillingInvoice> GetInvoiceAsync(string invoiceId, CancellationToken ct = default);
    Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(string? status = null, CancellationToken ct = default);
    Task<BillingInvoice?> CancelInvoiceAsync(string invoiceId, CancellationToken ct = default);
    /// <summary>Только учебный банк (config.mode == "test"): result = success / processing / error / cancelled.</summary>
    Task<BillingInvoice?> TestPayAsync(string invoiceId, string result, CancellationToken ct = default);
}

/// <summary>Вид ошибки оплаты подписки (коды сервера: 409 billing_unavailable, 400 invalid / plan_unknown, 429 too_many_invoices, 502 bank_unavailable, 409 invoice_final).</summary>
public enum BillingErrorKind { Other, Unavailable, InvalidMonths, PlanUnknown, TooManyInvoices, BankUnavailable, InvoiceFinal, NoRights, SessionExpired, Network }

/// <summary>Разобранная ошибка: вид и (для 502) номер счёта со статусом error — повторить можно новым счётом.</summary>
public sealed record BillingError(BillingErrorKind Kind, string? InvoiceId);

/// <summary>2026-11-19 (ответ Умара 19.11, раздел 2; ТЗ «Оплата подписки клиентами через ELQR»): клиент API оплаты подписки магазином. Ключ банка живёт только на сервере NurCRM;
/// касса ходит на сервер: GET users/billing/config/, POST/GET users/billing/invoices/, POST …/{id}/cancel/ и …/{id}/test-pay/ (учебный банк). Права — владелец и администратор.</summary>
public sealed class BillingApi : IBillingApi
{
    private const string Root = "api/users/billing/";
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(40); // сервер ходит в банк за QR

    private readonly NurMarketApiClient _api;

    public BillingApi(NurMarketApiClient api) => _api = api;

    public async Task<BillingConfig> GetConfigAsync(CancellationToken ct = default) =>
        BillingParser.ParseConfig(await _api.RequestAsync(HttpMethod.Get, Root + "config/", null, null, ct).ConfigureAwait(false));

    public Task<BillingInvoice> CreateInvoiceAsync(int months, string? planCode, string idempotencyKey, CancellationToken ct = default) =>
        CreateInvoiceAsync(months, planCode, null, idempotencyKey, ct);

    public async Task<BillingInvoice> CreateInvoiceAsync(int months, string? planCode, string? period, string idempotencyKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("Idempotency-Key обязателен.", nameof(idempotencyKey));
        var body = new Dictionary<string, object?> { ["months"] = months };
        if (!string.IsNullOrWhiteSpace(planCode))
            body["plan_code"] = planCode.Trim();
        if (string.Equals(period, "year", StringComparison.OrdinalIgnoreCase))
            body["period"] = "year"; // 2026-11-21: годовой счёт (12 месяцев по цене 10); months остаётся 12
        var data = await _api.PostWithIdempotencyKeyAsync(Root + "invoices/", body, idempotencyKey, ct, CreateTimeout).ConfigureAwait(false);
        return BillingParser.ParseInvoice(data);
    }

    public async Task<BillingInvoice> GetInvoiceAsync(string invoiceId, CancellationToken ct = default) =>
        BillingParser.ParseInvoice(await _api.RequestAsync(HttpMethod.Get, Root + "invoices/" + Uri.EscapeDataString(invoiceId) + "/", null, null, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false));

    public async Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(string? status = null, CancellationToken ct = default)
    {
        IReadOnlyDictionary<string, string>? query = string.IsNullOrWhiteSpace(status) ? null : new Dictionary<string, string> { ["status"] = status.Trim() };
        return BillingParser.ParseInvoiceList(await _api.RequestAsync(HttpMethod.Get, Root + "invoices/", null, query, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false));
    }

    public async Task<BillingInvoice?> CancelInvoiceAsync(string invoiceId, CancellationToken ct = default)
    {
        // Отмена идемпотентна: ключ по номеру счёта (общая защита денежных POST «invoice» тоже подставила бы свой).
        var data = await _api.PostWithIdempotencyKeyAsync(Root + "invoices/" + Uri.EscapeDataString(invoiceId) + "/cancel/", new Dictionary<string, object?>(), "billing-cancel-" + invoiceId, ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && BillingParser.Str(data, "status") is not null ? BillingParser.ParseInvoice(data) : null;
    }

    public async Task<BillingInvoice?> TestPayAsync(string invoiceId, string result, CancellationToken ct = default)
    {
        var data = await _api.PostWithIdempotencyKeyAsync(Root + "invoices/" + Uri.EscapeDataString(invoiceId) + "/test-pay/", new Dictionary<string, object?> { ["result"] = result },
            "billing-testpay-" + Guid.NewGuid().ToString("N"), ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && BillingParser.Str(data, "status") is not null ? BillingParser.ParseInvoice(data) : null;
    }

    // ── Ошибки ───────────────────────────────────────────────────────────────────────

    /// <summary>Вид ошибки по коду и статусу ответа; для 502 — номер счёта со статусом error (data.invoice_id или invoice_id).</summary>
    public static BillingError Classify(Exception ex)
    {
        if (ex is not ApiException api)
            return new BillingError(BillingErrorKind.Network, null);
        var code = ServerErrorCodes.Code(api);
        string? invoiceId = null;
        if (api.Payload is { ValueKind: JsonValueKind.Object } p)
        {
            invoiceId = BillingParser.Str(p, "invoice_id");
            if (invoiceId is null && p.TryGetProperty("data", out var d))
                invoiceId = BillingParser.Str(d, "invoice_id");
        }

        var kind = code switch
        {
            "billing_unavailable" => BillingErrorKind.Unavailable,
            "invalid" => BillingErrorKind.InvalidMonths,
            "plan_unknown" => BillingErrorKind.PlanUnknown,
            "too_many_invoices" => BillingErrorKind.TooManyInvoices,
            "bank_unavailable" => BillingErrorKind.BankUnavailable,
            "invoice_final" => BillingErrorKind.InvoiceFinal,
            _ => BillingErrorKind.Other,
        };
        if (kind == BillingErrorKind.Other)
            kind = api.StatusCode switch
            {
                401 => BillingErrorKind.SessionExpired,
                403 => BillingErrorKind.NoRights,
                429 => BillingErrorKind.TooManyInvoices,
                502 => BillingErrorKind.BankUnavailable,
                _ => BillingErrorKind.Other,
            };
        return new BillingError(kind, invoiceId);
    }

    /// <summary>Понятный текст ошибки на языке интерфейса. Нет связи — отдельная формулировка; прочее — ответ сервера.</summary>
    public static string Describe(Exception ex)
    {
        var e = Classify(ex);
        return e.Kind switch
        {
            BillingErrorKind.Unavailable => NotConnectedText(),
            BillingErrorKind.InvalidMonths => Tr.T("Срок подписки выбран неверно — выберите 1, 3, 6 или 12 месяцев.", "Жазылуу мөөнөтү туура эмес тандалды — 1, 3, 6 же 12 ай тандаңыз.",
                "The subscription term is invalid — choose 1, 3, 6 or 12 months.", "Abonelik süresi geçersiz — 1, 3, 6 veya 12 ay seçin.", "Obuna muddati noto'g'ri tanlangan — 1, 3, 6 yoki 12 oy tanlang."),
            BillingErrorKind.PlanUnknown => Tr.T("Сервер не знает выбранный тариф — обновите окно и выберите заново.", "Сервер тандалган тарифти билбейт — терезени жаңыртып, кайра тандаңыз.",
                "The server does not know the chosen plan — refresh the window and choose again.", "Sunucu seçilen tarifeyi tanımıyor — pencereyi yenileyip tekrar seçin.", "Server tanlangan tarifni bilmaydi — oynani yangilab, qaytadan tanlang."),
            BillingErrorKind.TooManyInvoices => Tr.T("Слишком много счетов — подождите немного и повторите.", "Эсептер өтө көп — бир аз күтүп, кайра аракет кылыңыз.",
                "Too many invoices — wait a moment and try again.", "Çok fazla fatura — biraz bekleyip tekrar deneyin.", "Hisoblar juda ko'p — biroz kutib, qaytadan urinib ko'ring."),
            BillingErrorKind.BankUnavailable => Tr.T("Банк сейчас не отвечает. Деньги не списаны — попробуйте создать счёт ещё раз.", "Банк азыр жооп бербей жатат. Акча алынган жок — эсепти дагы бир жолу түзүп көрүңүз.",
                "The bank is not responding. No money was charged — try creating the invoice again.", "Banka şu an yanıt vermiyor. Para çekilmedi — faturayı tekrar oluşturmayı deneyin.", "Bank hozir javob bermayapti. Pul yechilmadi — hisobni yana bir bor yaratib ko'ring."),
            BillingErrorKind.InvoiceFinal => Tr.T("Этот счёт уже закрыт — создайте новый.", "Бул эсеп жабылган — жаңысын түзүңүз.", "This invoice is already closed — create a new one.",
                "Bu fatura zaten kapatıldı — yenisini oluşturun.", "Bu hisob allaqachon yopilgan — yangisini yarating."),
            BillingErrorKind.NoRights => Tr.T("Подписку оплачивает владелец или администратор компании.", "Жазылууну компаниянын ээси же администратору төлөйт.",
                "The subscription is paid by the company owner or administrator.", "Aboneliği şirket sahibi veya yönetici öder.", "Obunani kompaniya egasi yoki administrator to'laydi."),
            BillingErrorKind.SessionExpired => Tr.T("Срок входа истёк — войдите заново.", "Кирүү мөөнөтү бүттү — кайра кириңиз.", "Your sign-in has expired — please sign in again.",
                "Oturum süresi doldu — yeniden giriş yapın.", "Kirish muddati tugadi — qaytadan kiring."),
            BillingErrorKind.Network => ServerOutageMonitor.FriendlyError(ex),
            _ => ex is ApiException a ? a.Message : ServerOutageMonitor.FriendlyError(ex),
        };
    }

    /// <summary>Пояснение, когда оплата для компании не включена (409 billing_unavailable или available:false).</summary>
    public static string NotConnectedText() => Tr.T("Оплата подписки через приложение ещё не подключена для вашей компании.",
        "Жазылууну тиркеме аркылуу төлөө сиздин компания үчүн азырынча туташтырылган эмес.",
        "Paying for the subscription through the app is not yet connected for your company.",
        "Uygulama üzerinden abonelik ödemesi şirketiniz için henüz bağlanmadı.",
        "Obunani ilova orqali to'lash kompaniyangiz uchun hali ulanmagan.");
}
