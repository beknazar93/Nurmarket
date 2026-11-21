using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>Курсы валют компании (ответ GET main/currencies/): сколько сомов за единицу валюты.</summary>
public sealed record CurrencyRates(IReadOnlyDictionary<string, decimal> Rates, IReadOnlyList<string> Enabled, string? Version, DateTimeOffset? UpdatedAt)
{
    public static readonly CurrencyRates Empty = new(new Dictionary<string, decimal>(), Array.Empty<string>(), null, null);
}

/// <summary>Оплата наличными в иностранной валюте: принято <see cref="AmountCurrency"/> по курсу <see cref="Rate"/> = <see cref="AmountSom"/> сомов; <see cref="SomExtra"/> — добавлено сомами.</summary>
public sealed record FxPayment(string Currency, decimal AmountCurrency, decimal Rate, decimal AmountSom, decimal SomExtra = 0m)
{
    /// <summary>Всего принято наличными в сомах: валюта по курсу + сомы.</summary>
    public decimal TotalSom => AmountSom + SomExtra;
}

/// <summary>Результат проверки ввода «Принято в валюте».</summary>
public enum FxInputResult
{
    Ok,
    /// <summary>Курса нет — кнопка недоступна, оплата в этой валюте невозможна (ТЗ: «курс 0 или пустой — кнопка недоступна»).</summary>
    NoRate,
    /// <summary>Сумма не число или не больше нуля.</summary>
    BadAmount,
    /// <summary>Валюта не из списка принимаемых.</summary>
    UnknownCurrency,
}

/// <summary>
/// 2026-11-21 (сфера «Базар», модуль <c>currencies</c>, ТЗ Контейнер-базар 2.6, ответ сервера 20.11 В.19): курсы и оплата в валюте.
/// Курсы — <c>GET / PUT main/currencies/</c> (меняет владелец / администратор; <c>If-Match: version</c>; 409 <c>version_conflict</c>). Оплата — строка <c>payment.payments[]</c> тела pos/checkout/:
/// <c>{method, amount (СОМЫ), currency, amount_currency, rate}</c>; <c>amount</c> считается как <c>amount_currency × rate</c> (сервер: расхождение больше 1 сома — 400 <c>currency_amount_mismatch</c>;
/// неизвестная валюта — <c>currency_unknown</c>; нет курса — <c>currency_rate_required</c>). Сдача — в сомах (обычная логика окна оплаты). Принятая валюта лежит в снимке чека
/// (поле <c>fx_payment</c>) — поэтому она доходит и до офлайн-очереди, и до повтора неясной оплаты. Курсы хранятся в файле <c>currency-rates.json</c> (папка данных) — работают без связи.
/// </summary>
public static class BazaarCurrencies
{
    /// <summary>Валюты, которые «Базар» принимает в оплату (задание владельца: USD, RUB, CNY). Показываются только те, у которых есть курс.</summary>
    public static readonly IReadOnlyList<string> PayCurrencies = new[] { "USD", "RUB", "CNY" };

    /// <summary>Валюты, которые знает сервер (ответ В.19).</summary>
    public static readonly IReadOnlyList<string> KnownCurrencies = new[] { "USD", "EUR", "RUB", "CNY", "KZT", "UZS", "TRY" };

    /// <summary>Поле снимка чека с принятой валютой.</summary>
    public const string CartField = "fx_payment";

    private static readonly object Gate = new();
    private static CurrencyRates? _rates;

    /// <summary>Только для проверок: другой файл вместо настоящего.</summary>
    public static string? FileOverride { get; set; }

    /// <summary>Подмена для тестов: клиент API вместо <see cref="ServerLoyalty.ApiProvider"/>.</summary>
    public static Func<NurMarketApiClient?>? ApiOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.Roaming, AppMode.DataFolderName, "currency-rates.json");

    // ───────────── чистая логика ─────────────

    /// <summary>Сомов за валюту: <c>amount_currency × rate</c>, до копейки (как проверяет сервер).</summary>
    public static decimal ToSom(decimal amountCurrency, decimal rate) =>
        Math.Round(amountCurrency * rate, 2, MidpointRounding.AwayFromZero);

    /// <summary>Разбор ввода: запятая / точка, пробелы; не число или ≤ 0 — null.</summary>
    public static decimal? ParseAmount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var clean = text.Replace(" ", "").Replace(" ", "").Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0m && v < 1_000_000_000m ? v : null;
    }

    /// <summary>Принятая валюта из ввода кассира: курс берётся из курсов компании. <paramref name="somExtra"/> — добавлено сомами (необязательно).</summary>
    public static FxInputResult TryBuild(CurrencyRates rates, string? currency, string? amountText, string? somExtraText, out FxPayment? payment)
    {
        payment = null;
        var code = (currency ?? "").Trim().ToUpperInvariant();
        if (!KnownCurrencies.Contains(code))
            return FxInputResult.UnknownCurrency;
        if (!rates.Rates.TryGetValue(code, out var rate) || rate <= 0m)
            return FxInputResult.NoRate;
        if (ParseAmount(amountText) is not { } amount)
            return FxInputResult.BadAmount;
        var extra = ParseAmount(somExtraText) ?? 0m;
        payment = new FxPayment(code, amount, rate, ToSom(amount, rate), extra);
        return FxInputResult.Ok;
    }

    /// <summary>Строка <c>payments[]</c> для валютной части оплаты (сервер: <c>amount</c> — сомы).</summary>
    public static JsonObject ToPaymentEntry(FxPayment p) => new()
    {
        ["method"] = "cash",
        ["amount"] = p.AmountSom.ToString("0.00", CultureInfo.InvariantCulture),
        ["currency"] = p.Currency,
        ["amount_currency"] = p.AmountCurrency.ToString("0.##", CultureInfo.InvariantCulture),
        ["rate"] = p.Rate.ToString("0.####", CultureInfo.InvariantCulture),
    };

    /// <summary>Все строки <c>payment.payments[]</c>: валюта и (если добавлены) сомы наличными.</summary>
    public static JsonArray ToPayments(FxPayment p)
    {
        var arr = new JsonArray { ToPaymentEntry(p) };
        if (p.SomExtra > 0m)
            arr.Add(new JsonObject { ["method"] = "cash", ["amount"] = p.SomExtra.ToString("0.00", CultureInfo.InvariantCulture) });
        return arr;
    }

    /// <summary>В снимок чека (<see cref="CartField"/>) и обратно: валюта, сумма в валюте, курс, сомы.</summary>
    public static JsonObject ToCartJson(FxPayment p) => new()
    {
        ["currency"] = p.Currency,
        ["amount_currency"] = p.AmountCurrency.ToString("0.##", CultureInfo.InvariantCulture),
        ["rate"] = p.Rate.ToString("0.####", CultureInfo.InvariantCulture),
        ["amount_som"] = p.AmountSom.ToString("0.00", CultureInfo.InvariantCulture),
        ["som_extra"] = p.SomExtra.ToString("0.00", CultureInfo.InvariantCulture),
    };

    public static FxPayment? FromCart(JsonElement cartRoot)
    {
        if (cartRoot.ValueKind != JsonValueKind.Object || !cartRoot.TryGetProperty(CartField, out var f) || f.ValueKind != JsonValueKind.Object)
            return null;
        decimal? Dec(string name) => f.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
        var code = f.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(code) || Dec("amount_currency") is not > 0m || Dec("rate") is not > 0m)
            return null;
        var amount = Dec("amount_currency")!.Value;
        var rate = Dec("rate")!.Value;
        return new FxPayment(code.Trim().ToUpperInvariant(), amount, rate, ToSom(amount, rate), Dec("som_extra") ?? 0m);
    }

    /// <summary>Строка курса для чека и экрана: «Принято $20 по курсу 87,5».</summary>
    public static string ReceiptLine(FxPayment p) =>
        $"{Tr.T("Принято", "Алынды", "Received", "Alındı", "Qabul qilindi")} {p.AmountCurrency.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"))} {p.Currency} "
        + $"{Tr.T("по курсу", "курс боюнча", "at rate", "kurla", "kurs bo'yicha")} {p.Rate.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"))}";

    /// <summary>Принимаемые валюты кассы: из USD / RUB / CNY — те, у которых есть курс (и, если сервер прислал список <c>enabled</c>, включённые).</summary>
    public static IReadOnlyList<string> Accepted(CurrencyRates rates) =>
        PayCurrencies.Where(c => rates.Rates.TryGetValue(c, out var r) && r > 0m && (rates.Enabled.Count == 0 || rates.Enabled.Contains(c))).ToList();

    /// <summary>Разбор ответа GET / PUT main/currencies/: <c>{base, rates:{USD:"87.5"}, enabled:[…], version, updated_at}</c>.</summary>
    public static CurrencyRates Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
            return CurrencyRates.Empty;
        var rates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetProperty("rates", out var r) && r.ValueKind == JsonValueKind.Object)
            foreach (var p in r.EnumerateObject())
            {
                decimal? d = p.Value.ValueKind switch
                {
                    JsonValueKind.Number when p.Value.TryGetDecimal(out var n) => n,
                    JsonValueKind.String when decimal.TryParse(p.Value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
                    _ => null,
                };
                if (d is > 0m)
                    rates[p.Name.ToUpperInvariant()] = d.Value;
            }
        var enabled = new List<string>();
        if (data.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } code)
                    enabled.Add(code.ToUpperInvariant());
        string? version = data.TryGetProperty("version", out var v) ? v.ValueKind switch { JsonValueKind.String => v.GetString(), JsonValueKind.Number => v.GetRawText(), _ => null } : null;
        DateTimeOffset? updated = data.TryGetProperty("updated_at", out var u) && u.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(u.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
        return new CurrencyRates(rates, enabled, version, updated);
    }

    // ───────────── хранение ─────────────

    /// <summary>Последние известные курсы (из файла кассы; пока сервер не отвечал — пусто).</summary>
    public static CurrencyRates Current
    {
        get
        {
            lock (Gate)
            {
                if (_rates is not null)
                    return _rates;
                try
                {
                    if (File.Exists(FilePath) && AtomicFile.ReadAllText(FilePath) is { Length: > 0 } text)
                    {
                        using var doc = JsonDocument.Parse(text);
                        return _rates = Parse(doc.RootElement.Clone());
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    PosLogger.Log($"Курсы валют: файл не прочитан ({ex.Message}).", "WARNING");
                }
                return _rates = CurrencyRates.Empty;
            }
        }
    }

    private static void Remember(JsonElement data, CurrencyRates parsed)
    {
        lock (Gate)
        {
            _rates = parsed;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                AtomicFile.WriteAllText(FilePath, data.GetRawText());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                PosLogger.Log($"Курсы валют: не удалось сохранить на диск: {ex.Message}", "WARNING");
            }
        }
    }

    /// <summary>Только для проверок: забыть память (файл не трогает).</summary>
    public static void ResetForTests()
    {
        lock (Gate)
            _rates = null;
    }

    // ───────────── сервер ─────────────

    /// <summary>GET main/currencies/ → курсы в память и на диск. Нет связи / сервер не ответил — остаются прежние (null в результате).</summary>
    public static async Task<CurrencyRates?> RefreshAsync(CancellationToken ct = default)
    {
        var api = (ApiOverride ?? ServerLoyalty.ApiProvider)?.Invoke();
        if (api is null || string.IsNullOrEmpty(api.AccessToken))
            return null;
        try
        {
            var data = await api.RequestAsync(HttpMethod.Get, "api/main/currencies/", null, null, ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var parsed = Parse(data);
            Remember(data, parsed);
            return parsed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Курсы валют: сервер не ответил ({ex.Message}) — остаются прежние.", "WARNING");
            return null;
        }
    }

    private static DateTime _lastAttempt = DateTime.MinValue;

    /// <summary>Не чаще раза в 5 минут тихо перечитать курсы в фоне (модуль включён, связь есть). Оплату не задерживает: она берёт курсы, что уже есть.</summary>
    public static void EnsureFresh()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _lastAttempt < TimeSpan.FromMinutes(5))
                return;
            _lastAttempt = DateTime.UtcNow;
        }
        _ = Task.Run(() => RefreshAsync());
    }

    /// <summary>PUT main/currencies/ (владелец / администратор). Пустой курс — валюта убирается. Ошибка — текст для кассира; успех — null.</summary>
    public static async Task<(CurrencyRates? Rates, string? Error)> SaveAsync(IReadOnlyDictionary<string, string> rates, CancellationToken ct = default)
    {
        var api = (ApiOverride ?? ServerLoyalty.ApiProvider)?.Invoke();
        if (api is null || string.IsNullOrEmpty(api.AccessToken))
            return (null, NoConnectionText);
        var body = new JsonObject();
        var ratesNode = new JsonObject();
        foreach (var (code, value) in rates)
        {
            var c = code.Trim().ToUpperInvariant();
            if (!KnownCurrencies.Contains(c))
                continue;
            ratesNode[c] = string.IsNullOrWhiteSpace(value) ? "" : (ParseAmount(value) is { } d ? d.ToString("0.####", CultureInfo.InvariantCulture) : "");
        }
        body["rates"] = ratesNode;
        var headers = Current.Version is { Length: > 0 } ver ? new Dictionary<string, string> { ["If-Match"] = ver } : null;
        try
        {
            var data = await api.RequestAsync(HttpMethod.Put, "api/main/currencies/", body, null, ct, TimeSpan.FromSeconds(15), headers).ConfigureAwait(false);
            var parsed = Parse(data);
            Remember(data, parsed);
            return (parsed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var text = ex.Message ?? "";
            var code = ServerErrorCodes.Code(ex);
            var status = (ex as ApiException)?.StatusCode;
            if (code == "version_conflict" || text.Contains("version_conflict", StringComparison.OrdinalIgnoreCase))
            {
                await RefreshAsync(ct).ConfigureAwait(false);
                return (null, Tr.T("Курсы только что изменили на другой кассе. Показаны новые — проверьте и сохраните ещё раз.",
                    "Курстарды дагы бир кассада өзгөртүштү. Жаңылары көрсөтүлдү — текшерип, кайра сактаңыз.",
                    "The rates were just changed on another till. The new ones are shown — check and save again.",
                    "Kurlar az önce başka bir kasada değiştirildi. Yenileri gösteriliyor — kontrol edip tekrar kaydedin.",
                    "Kurslar hozirgina boshqa kassada o'zgartirildi. Yangilari ko'rsatildi — tekshirib, qayta saqlang."));
            }
            if (code == "permission_denied" || status == 403 || text.Contains("permission_denied", StringComparison.OrdinalIgnoreCase))
                return (null, Tr.T("Курсы валют может менять только владелец или администратор.", "Валюта курстарын ээси же администратор гана өзгөртө алат.",
                    "Only the owner or an administrator can change exchange rates.", "Döviz kurlarını yalnızca sahip veya yönetici değiştirebilir.", "Valyuta kurslarini faqat egasi yoki administrator o'zgartira oladi."));
            PosLogger.Log($"Курсы валют: не сохранились ({ex.Message}).", "WARNING");
            return (null, NoConnectionText);
        }
    }

    private static string NoConnectionText => Tr.T("Курсы не сохранились: нет связи с сервером. Повторите, когда связь появится.",
        "Курстар сакталган жок: сервер менен байланыш жок. Байланыш пайда болгондо кайталаңыз.",
        "The rates were not saved: no connection to the server. Try again when it is back.",
        "Kurlar kaydedilmedi: sunucuyla bağlantı yok. Bağlantı gelince tekrar deneyin.",
        "Kurslar saqlanmadi: server bilan aloqa yo'q. Aloqa tiklanganda qayta urinib ko'ring.");

    /// <summary>Текст ошибки сервера по коду оплаты в валюте (<c>currency_amount_mismatch</c>, <c>currency_unknown</c>, <c>currency_rate_required</c>); null — не наш код.</summary>
    public static string? ServerErrorText(string? code) => code switch
    {
        "currency_amount_mismatch" => Tr.T("Сумма в сомах не сходится с суммой в валюте по курсу. Обновите курсы и повторите оплату.",
            "Сомдогу сумма валютадагы сумма менен курс боюнча дал келген жок. Курстарды жаңыртып, төлөмдү кайталаңыз.",
            "The som amount does not match the foreign amount at the rate. Refresh the rates and pay again.",
            "Som tutarı, döviz tutarıyla kura göre uyuşmuyor. Kurları yenileyip ödemeyi tekrarlayın.",
            "So'mdagi summa valyutadagi summa bilan kurs bo'yicha mos kelmadi. Kurslarni yangilab, to'lovni qaytaring."),
        "currency_unknown" => Tr.T("Эта валюта серверу не известна. Выберите другую валюту или оплату в сомах.",
            "Бул валютаны сервер билбейт. Башка валютаны же сом менен төлөмдү тандаңыз.",
            "The server does not know this currency. Choose another currency or pay in som.",
            "Bu para birimini sunucu tanımıyor. Başka bir para birimi seçin veya som ile ödeyin.",
            "Bu valyutani server bilmaydi. Boshqa valyutani yoki so'mda to'lovni tanlang."),
        "currency_rate_required" => Tr.T("Для этой валюты не задан курс. Укажите курс в окне «Курсы валют».",
            "Бул валюта үчүн курс коюлган эмес. «Валюта курстары» терезесинде курсту көрсөтүңүз.",
            "No rate is set for this currency. Set it in the “Exchange rates” window.",
            "Bu para birimi için kur girilmemiş. «Döviz kurları» penceresinde kuru girin.",
            "Bu valyuta uchun kurs berilmagan. «Valyuta kurslari» oynasida kursni kiriting."),
        _ => null,
    };
}
