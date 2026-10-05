using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.Api;

/// <summary>Настройки витрины компании — поля GET/PATCH /api/users/settings/company/, которые
/// касаются сайта: адрес витрины (slug → https://nurcrm.kg/catalog/{slug}) и номер WhatsApp, на
/// который витрина отправляет заказ (phones_howcase — так, с опечаткой, поле называется на сервере).
/// Название компании — только для показа: меняется не здесь (правило владельца — обновление
/// программы и её разделы не трогают название и адрес магазина).</summary>
public sealed record ShowcaseSettings(string CompanyName, string Slug, string ShowcasePhone);

/// <summary>Строка заказа (items[] у /api/main/orders/): товар, количество, цена и сумма сервера.</summary>
public sealed record SiteOrderItem(string ProductId, string? Name, double Quantity, double Price, double Total);

/// <summary>Заказ NurCRM (GET /api/main/orders/). Поля — по схеме сервера (Order); доставка, адрес
/// и комментарий в схеме сейчас НЕТ — читаются, только если сервер когда-нибудь начнёт их отдавать.</summary>
public sealed class SiteOrder
{
    public string Id { get; init; } = "";
    public string OrderNumber { get; init; } = "";
    public string CustomerName { get; init; } = "";
    public string Phone { get; init; } = "";
    public string Department { get; init; } = "";
    public string Status { get; init; } = "";
    public string DateOrdered { get; init; } = "";
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public double Total { get; init; }
    public double TotalQuantity { get; init; }
    public IReadOnlyList<SiteOrderItem> Items { get; init; } = Array.Empty<SiteOrderItem>();
    /// <summary>2026-10-05: заказ витрины — остаток зарезервирован (списан) под заказ; продажа кассы, если заказ пробит.</summary>
    public bool StockReserved { get; init; }
    public string? SaleId { get; init; }
    public string? Delivery { get; init; }
    public string? Address { get; init; }
    public string? Comment { get; init; }
    public string? Source { get; init; }

    public bool IsNew => string.Equals(Status, ShowcaseApiService.StatusNew, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 2026-09-29, просьба владельца: «заказы с сайта тоже должны падать в админку. Настройки сайта тоже».
///
/// Что есть у NurCRM (проверено 29.09 по схеме /redoc/, коду сайта и живыми GET на тестовой NBS):
/// • Витрина — публичная страница https://nurcrm.kg/catalog/{slug}; товары она берёт из
///   GET /api/main/public/companies/{slug}/showcase/ (все товары компании).
/// • Кнопка заказа на витрине НЕ создаёт заказ на сервере: сайт собирает текст («Заказ с витрины…»)
///   и открывает WhatsApp на номер phones_howcase (wa.me). Поэтому заказы витрины сейчас видит только
///   этот WhatsApp — ни программа, ни сайт их не получают.
/// • Единственные заказы на сервере — /api/main/orders/ (на сайте раздел «Закупки», /crm/zakaz):
///   номер, покупатель, телефон, отдел, дата, статус new / pending / completed, строки товаров.
///   Их программа владельца и показывает; сменить можно только статус (PATCH {status}).
/// • Вебхука о заказах нет (события: sale.*, shift.*, stock.low, company.updated, appointment.created),
///   поэтому новые заказы узнаются опросом — не чаще раза в полминуты и через общий темп <see cref="ApiThrottle"/>.
///
/// Транспорт — NurMarketApiClient (Bearer, обновление токена, branch=…). Ошибки сервера уходят
/// вызывающему как <see cref="ApiException"/> — окно само решает, что показать.
/// </summary>
public sealed class ShowcaseApiService
{
    public const string StatusNew = "new";
    public const string StatusPending = "pending";
    public const string StatusCompleted = "completed";
    // 2026-10-05: статусы заказов витрины (GET/PATCH /api/main/showcase/orders/, ТЗ часть 3, 6.12).
    public const string StatusAccepted = "accepted";
    public const string StatusReady = "ready";
    public const string StatusDone = "done";
    public const string StatusCanceled = "canceled";

    /// <summary>«В работе»: принят или готов (и старое «В процессе»).</summary>
    public static bool IsInProgress(string status) => status is StatusAccepted or StatusReady or StatusPending;

    /// <summary>Закрыт: выдан или отменён (и старое «Завершён»).</summary>
    public static bool IsFinished(string status) => status is StatusDone or StatusCanceled or StatusCompleted;

    private const string OrdersPath = "api/main/showcase/orders/";

    public const int SlugMinLength = 3;
    public const int SlugMaxLength = 50;
    public const int PhoneMaxLength = 60;

    /// <summary>Сайт, на котором открывается витрина (app.nurcrm.kg/catalog/… отвечает 404).</summary>
    public const string PublicSiteBase = "https://nurcrm.kg";

    private const int MaxOrderPages = 20;

    private readonly NurMarketApiClient _api;

    public ShowcaseApiService(NurMarketApiClient api) => _api = api;

    /// <summary>Сколько новых заказов (status=new) было в последнем полученном списке. Шлётся после
    /// каждого удачного списка — значок у пункта меню «Заказы с сайта».</summary>
    public static event Action<int>? NewOrdersCountChanged;

    public static int? LastNewOrdersCount { get; private set; }

    /// <summary>2026-09-30, решение владельца: список /api/main/orders/ («Закупки» на сайте) НЕ показываем.
    /// 2026-10-05, владелец «да» на «заказы с сайта прямо с сервера»: NurCRM хранит заказы витрины (с сайта и из
    /// Telegram-бота) — /api/main/showcase/orders/ (ТЗ часть 3, 6.12). Список включён и читает их.</summary>
    public static readonly bool OrdersListEnabled = true;

    /// <summary>Число новых заказов изменилось без нового списка (владелец сменил статус в карточке).</summary>
    public static void PublishNewOrdersCount(int count)
    {
        LastNewOrdersCount = count;
        NewOrdersCountChanged?.Invoke(count);
    }

    public static string CatalogUrl(string slug) => $"{PublicSiteBase}/catalog/{Uri.EscapeDataString(slug.Trim())}";

    /// <summary>Правило сайта NurCRM (Settings → ссылка на витрину): на «Старте» в секторе «Магазин»
    /// витрина — платная доп. услуга «Онлайн витрина» (can_view_showcase у компании); на остальных
    /// тарифах владельцу она доступна всегда.</summary>
    public static bool IsShowcaseConnected =>
        !TariffGate.IsStartTariff || CompanyInfoService.LastCompany?.CanViewShowcase == true;

    // ── Настройки витрины ───────────────────────────────────────────────────────────

    public async Task<ShowcaseSettings> GetSettingsAsync(CancellationToken ct = default)
    {
        var data = await GetRetryingAsync("api/users/settings/company/", null, ct).ConfigureAwait(false);
        return ParseSettings(data);
    }

    /// <summary>GET /api/users/company/check-slug/?slug=… → {available, message}. Свой текущий адрес
    /// сервер считает свободным.</summary>
    public async Task<(bool Available, string? Message)> CheckSlugAsync(string slug, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, "api/users/company/check-slug/", null,
            new Dictionary<string, string> { ["slug"] = slug.Trim() }, ct).ConfigureAwait(false);
        var available = data.ValueKind == JsonValueKind.Object
                        && data.TryGetProperty("available", out var a)
                        && a.ValueKind == JsonValueKind.True;
        return (available, Str(data, "message"));
    }

    /// <summary>PATCH /api/users/settings/company/ — ТОЛЬКО изменённые поля (null — поле не трогаем).
    /// Название, адрес и остальные настройки компании не отправляются никогда.</summary>
    public async Task<ShowcaseSettings> SaveSettingsAsync(string? newSlug, string? newShowcasePhone, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>();
        if (newSlug != null)
            body["slug"] = newSlug.Trim();
        if (newShowcasePhone != null)
            body["phones_howcase"] = newShowcasePhone.Trim().Length == 0 ? null : newShowcasePhone.Trim();
        if (body.Count == 0)
            return await GetSettingsAsync(ct).ConfigureAwait(false);

        var data = await _api.RequestAsync(HttpMethod.Patch, "api/users/settings/company/", body, null, ct).ConfigureAwait(false);
        PosLogger.Log($"Настройки сайта сохранены: {string.Join(", ", body.Keys)}", "SHOWCASE");
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("slug", out _)
            ? ParseSettings(data)
            : await GetSettingsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Сколько товаров видно на витрине (публичный список, page_size=1 — нужен только count).
    /// null — сервер не ответил списком.</summary>
    public async Task<int?> CountShowcaseProductsAsync(string slug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return null;
        var data = await GetRetryingAsync($"api/main/public/companies/{Uri.EscapeDataString(slug.Trim())}/showcase/",
            new Dictionary<string, string> { ["page_size"] = "1" }, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)
            ? n
            : null;
    }

    // ── Заказ из телеграм-бота ─────────────────────────────────────────────────────

    private static string? _cachedSlug;

    /// <summary>2026-10-01, решение владельца «обращения → заказы с сайта»: покупатель подтвердил заказ
    /// в боте — создаём заказ витрины тем же публичным адресом, что и сайт
    /// (POST /api/main/public/companies/{slug}/orders/, самовывоз). Цену и сумму считает сервер по
    /// своему каталогу — бот цену не передаёт. Ответ: номер заказа и сумма сервера.</summary>
    public async Task<(string Id, string Number, decimal Total)> CreateBotOrderAsync(
        string customerName, string phone, IReadOnlyList<(string ProductId, double Qty)> items, string? comment,
        CancellationToken ct = default)
    {
        _cachedSlug ??= (await GetSettingsAsync(ct).ConfigureAwait(false)).Slug;
        if (string.IsNullOrWhiteSpace(_cachedSlug))
            throw new InvalidOperationException("у компании не задан адрес витрины (slug)");

        var body = new Dictionary<string, object?>
        {
            ["customer"] = new Dictionary<string, object?> { ["name"] = customerName, ["phone"] = phone },
            ["items"] = items.Select(i => new Dictionary<string, object?>
            {
                ["product"] = i.ProductId,
                ["qty"] = i.Qty.ToString("0.###", CultureInfo.InvariantCulture),
            }).ToList(),
            ["delivery"] = new Dictionary<string, object?> { ["type"] = "pickup" },
            ["comment"] = string.IsNullOrWhiteSpace(comment) ? "Заказ через телеграм-бота" : "Телеграм-бот: " + comment,
        };

        var data = await _api.RequestAsync(HttpMethod.Post,
            $"api/main/public/companies/{Uri.EscapeDataString(_cachedSlug.Trim())}/orders/", body, null, ct).ConfigureAwait(false);

        string Read(string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())
            : "";
        decimal.TryParse(Read("total"), NumberStyles.Any, CultureInfo.InvariantCulture, out var total);
        return (Read("id"), Read("number"), total);
    }

    // ── Заказы ──────────────────────────────────────────────────────────────────────

    /// <summary>Все заказы компании (страницы до конца, не больше 20), новые сверху.</summary>
    public async Task<IReadOnlyList<SiteOrder>> ListOrdersAsync(CancellationToken ct = default)
    {
        var result = new List<SiteOrder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxOrderPages; page++)
        {
            var query = page > 1 ? new Dictionary<string, string> { ["page"] = page.ToString(CultureInfo.InvariantCulture) } : null;
            var data = await GetRetryingAsync(OrdersPath, query, ct).ConfigureAwait(false);
            var rows = data.ValueKind == JsonValueKind.Array
                ? data
                : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array
                    ? r
                    : default;
            if (rows.ValueKind != JsonValueKind.Array)
                break;

            var added = 0;
            foreach (var row in rows.EnumerateArray())
            {
                var order = ParseOrder(row);
                if (order.Id.Length == 0 || !seen.Add(order.Id))
                    continue;
                result.Add(order);
                added++;
            }

            // Как у списка продаж: страница за последней может повторять её — стоп, если нового нет.
            var hasNext = data.ValueKind == JsonValueKind.Object
                          && data.TryGetProperty("next", out var next)
                          && next.ValueKind == JsonValueKind.String;
            if (!hasNext || added == 0)
                break;
        }

        var sorted = result
            .OrderByDescending(o => o.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(o => o.DateOrdered, StringComparer.Ordinal)
            .ToList();

        PublishNewOrdersCount(sorted.Count(o => o.IsNew));
        return sorted;
    }

    public async Task<SiteOrder?> GetOrderAsync(string id, CancellationToken ct = default)
    {
        var data = await GetRetryingAsync($"{OrdersPath}{Uri.EscapeDataString(id.Trim())}/", null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object ? ParseOrder(data) : null;
    }

    /// <summary>PATCH /api/main/showcase/orders/{id}/ {"status": …}: new → accepted → ready → done, или canceled.
    /// Остаток резервируется при создании заказа: canceled — резерв вернётся на остаток; done без продажи кассы —
    /// резерв становится окончательным списанием. Из done/canceled перевести нельзя.</summary>
    public async Task<SiteOrder> SetOrderStatusAsync(string id, string status, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Patch, $"{OrdersPath}{Uri.EscapeDataString(id.Trim())}/",
            new Dictionary<string, string> { ["status"] = status }, null, ct).ConfigureAwait(false);
        PosLogger.Log($"Заказ {id}: статус → {status}", "SHOWCASE");
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty("id", out _)
            ? ParseOrder(data)
            : (await GetOrderAsync(id, ct).ConfigureAwait(false)) ?? throw new ApiException("Order not found", 404);
    }

    // ── Проверка полей (как на сайте NurCRM) ────────────────────────────────────────

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
        // Кыргызские буквы — на сайте их нет в таблице, и они просто пропадали из адреса.
        ['ң'] = "n", ['ө'] = "o", ['ү'] = "u",
    };

    private static readonly Regex SlugFormat = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <summary>Как поле slug на сайте: строчные, кириллица латиницей, пробелы/_/. → дефис, прочее
    /// убирается, двойной дефис схлопывается, не длиннее 50. Дефисы по краям не срезаются, чтобы
    /// можно было набирать слово за словом (срезает <see cref="TrimSlug"/> перед проверкой).</summary>
    public static string NormalizeSlugInput(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var sb = new StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
            sb.Append(Translit.TryGetValue(ch, out var lat) ? lat : ch.ToString());
        var s = Regex.Replace(sb.ToString(), @"[\s_.]+", "-");
        s = Regex.Replace(s, "[^a-z0-9-]", "");
        s = Regex.Replace(s, "-{2,}", "-");
        return s.Length > SlugMaxLength ? s[..SlugMaxLength] : s;
    }

    public static string TrimSlug(string? text) => NormalizeSlugInput(text).Trim('-');

    /// <summary>null — адрес подходит; иначе причина на языке программы.</summary>
    public static string? ValidateSlug(string? slug)
    {
        var s = slug ?? "";
        if (s.Length == 0)
            return Tr.T("Введите адрес витрины", "Витринанын дарегин жазыңыз", "Enter the showcase address", "Vitrin adresini girin", "Vitrina manzilini kiriting");
        if (Regex.IsMatch(s, "[^a-z0-9-]"))
            return Tr.T("Только строчные латинские буквы, цифры и дефис", "Кичине латын тамгалары, сандар жана дефис гана", "Only lowercase Latin letters, digits and hyphens",
                "Yalnızca küçük Latin harfleri, rakamlar ve kısa çizgi", "Faqat kichik lotin harflari, raqamlar va chiziqcha");
        if (s.StartsWith('-') || s.EndsWith('-'))
            return Tr.T("Дефис не может быть в начале или в конце", "Дефис башында же аягында болбойт", "A hyphen can't be at the start or the end",
                "Kısa çizgi başta veya sonda olamaz", "Chiziqcha boshida yoki oxirida bo'lmaydi");
        if (s.Contains("--", StringComparison.Ordinal))
            return Tr.T("Два дефиса подряд нельзя", "Эки дефис катар болбойт", "Two hyphens in a row are not allowed", "Art arda iki kısa çizgi kullanılamaz", "Ketma-ket ikki chiziqcha mumkin emas");
        if (s.Length < SlugMinLength)
            return Tr.T($"Слишком коротко: нужно от {SlugMinLength} символов", $"Өтө кыска: {SlugMinLength} белгиден кем эмес", $"Too short: at least {SlugMinLength} characters",
                $"Çok kısa: en az {SlugMinLength} karakter", $"Juda qisqa: kamida {SlugMinLength} ta belgi");
        if (s.Length > SlugMaxLength)
            return Tr.T($"Слишком длинно: не больше {SlugMaxLength} символов", $"Өтө узун: {SlugMaxLength} белгиден ашпасын", $"Too long: at most {SlugMaxLength} characters",
                $"Çok uzun: en fazla {SlugMaxLength} karakter", $"Juda uzun: ko'pi bilan {SlugMaxLength} ta belgi");
        return SlugFormat.IsMatch(s)
            ? null
            : Tr.T("Недопустимый адрес", "Жараксыз дарек", "Invalid address", "Geçersiz adres", "Noto'g'ri manzil");
    }

    /// <summary>Итог проверки номера WhatsApp витрины. Error — сохранять нельзя; Warning — можно, но
    /// покупатели, скорее всего, не дозвонятся; Suggested — номер с кодом страны 996.</summary>
    public sealed record PhoneCheck(string? Error, string? Warning, string? Suggested);

    /// <summary>Сайт берёт из поля все цифры подряд и открывает wa.me/&lt;цифры&gt;. Поэтому: один номер,
    /// 9–15 цифр, и с кодом страны — иначе WhatsApp откроет чужой или несуществующий номер.</summary>
    public static PhoneCheck CheckShowcasePhone(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0)
            return new PhoneCheck(null,
                Tr.T("Без номера кнопка заказа на витрине не работает: сайту некуда отправить заказ.",
                    "Номерсиз витринадагы заказ баскычы иштебейт: сайт заказды эч жакка жөнөтө албайт.",
                    "Without a number, the order button on the showcase doesn't work: the site has nowhere to send the order.",
                    "Numara olmadan vitrindeki sipariş düğmesi çalışmaz: site siparişi gönderecek bir yer bulamaz.",
                    "Raqamsiz vitrinadagi buyurtma tugmasi ishlamaydi: sayt buyurtmani yuboradigan joy yo'q."),
                null);
        if (t.Length > PhoneMaxLength)
            return new PhoneCheck(Tr.T($"Не длиннее {PhoneMaxLength} символов", $"{PhoneMaxLength} белгиден ашпасын", $"At most {PhoneMaxLength} characters",
                $"En fazla {PhoneMaxLength} karakter", $"Ko'pi bilan {PhoneMaxLength} ta belgi"), null, null);
        if (Regex.IsMatch(t, @"[^\d\s+\-()]"))
        {
            // Несколько номеров через запятую/точку с запятой: сайт склеил бы их в одну строку цифр.
            var groups = Regex.Matches(t, @"\d[\d\s\-()]*\d").Select(m => Regex.Replace(m.Value, @"\D", "")).Count(g => g.Length >= 9);
            return new PhoneCheck(groups > 1
                    ? Tr.T("Укажите один номер: витрина отправляет заказ в один WhatsApp.",
                        "Бир гана номер жазыңыз: витрина заказды бир WhatsApp'ка жөнөтөт.",
                        "Enter one number: the showcase sends the order to a single WhatsApp.",
                        "Tek bir numara girin: vitrin siparişi tek bir WhatsApp'a gönderir.",
                        "Bitta raqam kiriting: vitrina buyurtmani bitta WhatsApp'ga yuboradi.")
                    : Tr.T("В номере только цифры, пробелы, «+», «-» и скобки.",
                        "Номерде сандар, боштуктар, «+», «-» жана кашаалар гана болот.",
                        "The number may contain only digits, spaces, “+”, “-” and brackets.",
                        "Numarada yalnızca rakam, boşluk, «+», «-» ve parantez olabilir.",
                        "Raqamda faqat raqamlar, bo'shliqlar, «+», «-» va qavslar bo'ladi."),
                null, null);
        }

        var digits = Regex.Replace(t, @"\D", "");
        if (digits.Length < 9)
            return new PhoneCheck(Tr.T("Номер слишком короткий", "Номер өтө кыска", "The number is too short", "Numara çok kısa", "Raqam juda qisqa"), null, null);
        if (digits.Length > 15)
            return new PhoneCheck(Tr.T("Номер слишком длинный", "Номер өтө узун", "The number is too long", "Numara çok uzun", "Raqam juda uzun"), null, null);

        string? suggested = null;
        if (digits.Length == 9)
            suggested = "996" + digits;
        else if (digits.Length == 10 && digits[0] == '0')
            suggested = "996" + digits[1..];
        if (suggested != null)
            return new PhoneCheck(null,
                Tr.T($"Номер без кода страны: WhatsApp откроет неверный номер. Нужно так: {suggested}.",
                    $"Номер өлкөнүн кодусуз: WhatsApp туура эмес номерди ачат. Мындай болушу керек: {suggested}.",
                    $"The number has no country code: WhatsApp will open the wrong number. It should be: {suggested}.",
                    $"Numarada ülke kodu yok: WhatsApp yanlış numarayı açar. Şöyle olmalı: {suggested}.",
                    $"Raqamda mamlakat kodi yo'q: WhatsApp noto'g'ri raqamni ochadi. Bunday bo'lishi kerak: {suggested}."),
                suggested);
        return new PhoneCheck(null, null, null);
    }

    /// <summary>Номер для ссылки wa.me — только цифры (как делает витрина).</summary>
    public static string WhatsAppDigits(string? phone) => Regex.Replace(phone ?? "", @"\D", "");

    // ── Разбор ответов ──────────────────────────────────────────────────────────────

    private static ShowcaseSettings ParseSettings(JsonElement data) =>
        new(Str(data, "name") ?? "", Str(data, "slug") ?? "", Str(data, "phones_howcase") ?? "");

    internal static SiteOrder ParseOrder(JsonElement row)
    {
        var items = new List<SiteOrderItem>();
        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("items", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in list.EnumerateArray())
            {
                var product = it.ValueKind == JsonValueKind.Object && it.TryGetProperty("product", out var p) && p.ValueKind == JsonValueKind.Object
                    ? Str(p, "id") ?? ""
                    : Str(it, "product") ?? "";
                var qty = Num(it, "quantity") ?? Num(it, "qty") ?? 0;
                var price = Num(it, "price") ?? 0;
                // Заказ витрины: размер и цвет варианта — к названию.
                var name = FirstStr(it, "product_name", "name", "title");
                var variant = string.Join(", ", new[] { Str(it, "variant_size"), Str(it, "variant_color") }.Where(v => !string.IsNullOrWhiteSpace(v)));
                if (name is not null && variant.Length > 0)
                    name += $" ({variant})";
                items.Add(new SiteOrderItem(
                    product,
                    name,
                    qty,
                    price,
                    Num(it, "total") ?? qty * price));
            }
        }

        return new SiteOrder
        {
            Id = Str(row, "id") ?? "",
            OrderNumber = Str(row, "order_number") ?? Str(row, "number") ?? "",
            CustomerName = Str(row, "customer_name") ?? "",
            Phone = Str(row, "phone") ?? Str(row, "customer_phone") ?? "",
            Department = Str(row, "department") ?? "",
            Status = (Str(row, "status") ?? StatusNew).ToLowerInvariant(),
            DateOrdered = Str(row, "date_ordered") ?? "",
            CreatedAt = Date(row, "created_at"),
            UpdatedAt = Date(row, "updated_at"),
            Total = Num(row, "total") ?? items.Sum(i => i.Total),
            TotalQuantity = Num(row, "total_quantity") ?? items.Sum(i => i.Quantity),
            Items = items,
            Delivery = FirstStr(row, "delivery_type", "delivery_method", "receive_method", "delivery"),
            Address = FirstStr(row, "delivery_address", "address"),
            Comment = FirstStr(row, "comment", "customer_comment", "note", "notes"),
            Source = FirstStr(row, "source", "channel"),
            StockReserved = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("stock_reserved", out var reserved) && reserved.ValueKind == JsonValueKind.True,
            SaleId = Str(row, "sale"),
        };
    }

    /// <summary>GET с повтором при 429 «слишком частые запросы» (у NurMarketApiClient своего повтора
    /// нет): пауза, которую назвал сервер, не дольше 20 с, до двух повторов.</summary>
    private async Task<JsonElement> GetRetryingAsync(string path, IReadOnlyDictionary<string, string>? query, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await _api.RequestAsync(HttpMethod.Get, path, null, query, ct).ConfigureAwait(false);
            }
            catch (ApiException e) when (e.StatusCode == 429 && attempt < 2)
            {
                var match = Regex.Match(e.Message ?? "", @"(\d+)\s*(?:sec|сек)", RegexOptions.IgnoreCase);
                var seconds = match.Success && int.TryParse(match.Groups[1].Value, out var n) ? n : 3;
                PosLogger.Log($"Витрина: сервер попросил паузу {seconds} с (429), повтор {attempt + 1}/2: {path}", "API");
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 20)), ct).ConfigureAwait(false);
            }
        }
    }

    private static string? Str(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static string? FirstStr(JsonElement e, params string[] keys) =>
        keys.Select(k => Str(e, k)).FirstOrDefault(v => v != null);

    private static double? Num(JsonElement e, string key) =>
        Str(e, key) is { } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTimeOffset? Date(JsonElement e, string key) =>
        Str(e, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
