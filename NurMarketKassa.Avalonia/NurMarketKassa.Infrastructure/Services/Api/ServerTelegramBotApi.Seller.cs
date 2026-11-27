using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 1», docs/ТЗ/Умар — API бота-продавца Ф1 (26.11).md): настройки продавца (seller_config), «Диалоги бота» (взять / вернуть боту),
/// отчёт по продажам бота и «Проверить бота» глазами покупателя. Показываются в программе владельца только при признаках сервера bot_seller / bot_handoff / bot_sales_stats.</summary>
public interface IBotSellerApi
{
    /// <summary>null — сервер не отдаёт seller_config (старый сервер).</summary>
    Task<BotSellerConfig?> GetSellerConfigAsync(CancellationToken ct = default);

    /// <summary>PATCH только изменённых полей seller_config (<see cref="BotSellerConfig.DiffBody"/>); ошибку 400 — текстом через <see cref="ServerTelegramBotApi.DescribeFields"/>.</summary>
    Task<BotSellerConfig?> PatchSellerConfigAsync(IReadOnlyDictionary<string, object?> sellerConfigPart, CancellationToken ct = default);

    Task<BotDialogsPage> GetDialogsAsync(string? status, DateTime from, DateTime to, int page, CancellationToken ct = default);

    Task<BotDialogDetail?> GetDialogAsync(string chatId, CancellationToken ct = default);

    /// <summary>action: «take» — взять диалог у бота, «return» — вернуть боту (закрывает и заявки «перезвонить»).</summary>
    Task<BotDialogAction> DialogActionAsync(string chatId, string action, CancellationToken ct = default);

    /// <summary>2026-11-26 (ответ Умара, вечер): ответ покупателю из программы — сервер шлёт текст от бота, сам «берёт» диалог (бот молчит handoff_return_minutes) и пишет в «Чаты».
    /// Не дошло до Telegram — ApiException 502 (code telegram_error, detail).</summary>
    Task<BotDialogReply> ReplyToDialogAsync(string chatId, string text, CancellationToken ct = default);

    Task<BotSalesStats> GetSalesStatsAsync(DateTime from, DateTime to, CancellationToken ct = default);

    /// <summary>«Проверить бота»: ответ ИИ на вопрос так, как его увидит покупатель (as=customer). Заказ не создаётся, продавец не зовётся.</summary>
    Task<BotTestAnswer> TestAsCustomerAsync(string question, CancellationToken ct = default);
}

/// <summary>Часы живого продавца: AllDay — «круглосуточно» (сервер хранит {}); PerDay — сервер хранит расписание по дням (mon…sun), программа его не правит; иначе — одно «с–до» на все дни.</summary>
public sealed record BotSellerHours(bool AllDay, bool PerDay, string From, string To)
{
    public static BotSellerHours Default => new(false, false, "09:00", "20:00");
}

/// <summary>Настройки бота-продавца (telegram-bot/settings/ → seller_config). «Торг» (bargain) — этап Ф2: сервер его пока только хранит, на ответы он не влияет — в программе не показывается и не меняется.</summary>
public sealed record BotSellerConfig(
    bool SellerEnabled, bool OrdersEnabled, string Tone, string Greeting, string Signature, IReadOnlyList<string> ForbiddenWords, string Language,
    bool HandoffEnabled, BotSellerHours Hours, string OffHoursReply, int HandoffReturnMinutes, long ConfirmThreshold,
    string Rules, string Delivery, string Payment, string WorkHours, BotBargainConfig? Bargain = null)
{
    public static readonly string[] Tones = ["official", "friendly", "short"];
    public static readonly string[] Languages = ["auto", "ru", "ky"];
    public const int MaxForbiddenWords = 30;

    /// <summary>Только изменённые относительно <paramref name="before"/> поля — для частичного PATCH. Пусто — менять нечего.
    /// Часы по дням (PerDay) без явной правки не отправляются: расписание по дням не затирается одним «с–до».</summary>
    public Dictionary<string, object?> DiffBody(BotSellerConfig before)
    {
        var d = new Dictionary<string, object?>();
        if (SellerEnabled != before.SellerEnabled) d["seller_enabled"] = SellerEnabled;
        if (OrdersEnabled != before.OrdersEnabled) d["orders_enabled"] = OrdersEnabled;
        if (Tone != before.Tone) d["tone"] = Tone;
        if (Greeting != before.Greeting) d["greeting"] = Greeting;
        if (Signature != before.Signature) d["signature"] = Signature;
        if (!ForbiddenWords.SequenceEqual(before.ForbiddenWords)) d["forbidden_words"] = ForbiddenWords.ToList();
        if (Language != before.Language) d["language"] = Language;
        if (HandoffEnabled != before.HandoffEnabled) d["handoff_enabled"] = HandoffEnabled;
        if (Hours != before.Hours)
            d["human_hours"] = Hours.AllDay ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["from"] = Hours.From, ["to"] = Hours.To };
        if (OffHoursReply != before.OffHoursReply) d["off_hours_reply"] = OffHoursReply;
        if (HandoffReturnMinutes != before.HandoffReturnMinutes) d["handoff_return_minutes"] = HandoffReturnMinutes;
        if (ConfirmThreshold != before.ConfirmThreshold) d["confirm_threshold"] = ConfirmThreshold;
        var k = new Dictionary<string, object?>();
        if (Rules != before.Rules) k["rules"] = Rules;
        if (Delivery != before.Delivery) k["delivery"] = Delivery;
        if (Payment != before.Payment) k["payment"] = Payment;
        if (WorkHours != before.WorkHours) k["work_hours"] = WorkHours;
        if (k.Count > 0) d["knowledge"] = k;
        // 2026-11-26 (торг, Ф2): блок «Торг» — только изменённые поля seller_config.bargain; сервер без блока (null) его не получает.
        if (Bargain is { } nb && before.Bargain is { } ob && nb.DiffBody(ob) is { Count: > 0 } b) d["bargain"] = b;
        return d;
    }

    /// <summary>«слово1, слово2» / по строкам → список без пустых и повторов (сервер принимает до <see cref="MaxForbiddenWords"/>).</summary>
    public static List<string> SplitWords(string? text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
}

public sealed record BotHandoff(string Reason, string Status, string Question, string Phone, string Note, string TakenBy, DateTimeOffset? CreatedAt, DateTimeOffset? FirstAnswerAt);

/// <summary>Статус диалога: bot — отвечает бот, needs_human — ждёт продавца, human — у продавца, callback — перезвонить (живёт 3 дня).</summary>
public sealed record BotDialog(string ChatId, string Name, string Username, int MessagesCount, int OrdersCount, DateTimeOffset? LastAt, string LastText, string Status, BotHandoff? Handoff);

public sealed record BotDialogsPage(int? Count, bool HasNext, IReadOnlyList<BotDialog> Rows);

public sealed record BotDialogMessage(DateTimeOffset? At, string Text, string Reply, bool IsVoice, string? OrderNumber);

public sealed record BotDialogOrder(string Number, decimal Total, string Status, DateTimeOffset? CreatedAt);

public sealed record BotDialogDetail(BotDialog Dialog, IReadOnlyList<BotDialogMessage> Messages, IReadOnlyList<BotDialogOrder> Orders);

public sealed record BotDialogAction(string Status, bool Changed);

/// <summary>Ответ сервера на «ответить покупателю»: Status — статус диалога после ответа (human), Sent — сообщение ушло в Telegram.</summary>
public sealed record BotDialogReply(string Status, bool Sent);

/// <summary>Максимум знаков в ответе покупателю (сервер).</summary>
public static class BotDialogLimits
{
    public const int ReplyMaxLength = 4000;

    /// <summary>2026-11-26 (стресс-тест): ответ покупателю точно НЕ ушёл — сервер отказал до отправки (4xx, кроме 408) или Telegram не принял (502 code telegram_error).
    /// Иначе (таймаут, обрыв связи, 500/503/504, 502 без telegram_error) исход неизвестен: сообщение могло дойти, повтор того же текста — только с подтверждением.</summary>
    public static bool ReplyFailedForSure(Exception ex) => ex switch
    {
        ApiException { StatusCode: >= 400 and < 500 and not 408 } => true,
        ApiException { StatusCode: 502, Payload: { ValueKind: JsonValueKind.Object } p } => p.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() == "telegram_error",
        _ => false,
    };
}

public sealed record BotUnknownQuestion(DateTimeOffset? At, string ChatId, string Text, string Name = "", string? ClientId = null);

/// <summary>Отчёт по продажам бота (telegram-bot/sales-stats/). Суммы — decimal (сервер отдаёт строкой «0.00»).</summary>
public sealed record BotSalesStats(
    int Dialogs, int Messages, int People,
    int OrdersCount, decimal OrdersTotal, int OrdersDone, decimal OrdersDoneTotal, int OrdersCanceled, decimal AvgCheck,
    double ConversionPercent, int FunnelHandoffs,
    int HandoffsTotal, IReadOnlyDictionary<string, int> HandoffsByReason, int Callbacks, int Answered, int Unanswered, int WaitingNow, int? AvgAnswerSeconds, int? MedianAnswerSeconds,
    IReadOnlyList<BotUnknownQuestion> UnknownQuestions, BotBargainStats? Bargain = null);

/// <summary>Черновик заказа из «Проверить бота» (order_draft): все поля могут отсутствовать. Заказ при проверке не создаётся.</summary>
public sealed record BotOrderDraft(string Name, string Phone, IReadOnlyList<BotOrderDraftItem> Items, string Comment);

public sealed record BotOrderDraftItem(string Title, string VariantId, string Size, string Color, string Qty);

/// <summary>Ответ «Проверить бота». Handoff — причина, по которой бот позвал бы продавца (null — не позвал); OrderDraft — заказ, который бот оформил бы (null — не оформил); CatalogMatches — сколько товаров каталога бот нашёл.</summary>
public sealed record BotTestAnswer(string Answer, string Model, string? Handoff, BotOrderDraft? OrderDraft, int CatalogMatches, BotBargainNote? Bargain = null);

/// <summary>Ответ бота приходит в разметке Telegram (&lt;b&gt;…&lt;/b&gt;) — на экране показываем чистый текст.</summary>
public static class BotSellerText
{
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    public static string Plain(string? html) => WebUtility.HtmlDecode(Tags.Replace(html ?? "", "")).Trim();
}

public sealed partial class ServerTelegramBotApi : IBotSellerApi
{
    public async Task<BotSellerConfig?> GetSellerConfigAsync(CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "settings/", null, null, ct).ConfigureAwait(false);
        return ParseSellerConfig(data);
    }

    public async Task<BotSellerConfig?> PatchSellerConfigAsync(IReadOnlyDictionary<string, object?> sellerConfigPart, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Patch, Root + "settings/", new Dictionary<string, object?> { ["seller_config"] = sellerConfigPart }, null, ct).ConfigureAwait(false);
        return ParseSellerConfig(data);
    }

    public async Task<BotDialogsPage> GetDialogsAsync(string? status, DateTime from, DateTime to, int page, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["page_size"] = "30",
        };
        if (!string.IsNullOrWhiteSpace(status))
            query["status"] = status!;
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "dialogs/", null, query, ct).ConfigureAwait(false);
        var hasNext = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.String;
        int? count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : null;
        return new BotDialogsPage(count, hasNext, Rows(data).Select(ParseDialog).ToList());
    }

    public async Task<BotDialogDetail?> GetDialogAsync(string chatId, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Get, Root + "dialogs/" + Uri.EscapeDataString(chatId) + "/", null, null, ct).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object)
            return null;
        var messages = new List<BotDialogMessage>();
        if (data.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
            foreach (var m in ms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)) // 2026-11-26 (стресс-тест): null в списке — пропуск
                messages.Add(new BotDialogMessage(Date(m, "at"), Str(m, "text") ?? "", Str(m, "reply") ?? "", Bool(m, "is_voice"), Str(m, "order_number")));
        var orders = new List<BotDialogOrder>();
        if (data.TryGetProperty("orders", out var os) && os.ValueKind == JsonValueKind.Array)
            foreach (var o in os.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                orders.Add(new BotDialogOrder(Str(o, "number") ?? "", Dec(o, "total"), Str(o, "status") ?? "", Date(o, "created_at")));
        return new BotDialogDetail(ParseDialog(data), messages, orders);
    }

    public async Task<BotDialogAction> DialogActionAsync(string chatId, string action, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Post, Root + "dialogs/" + Uri.EscapeDataString(chatId) + "/", new Dictionary<string, object?> { ["action"] = action }, null, ct).ConfigureAwait(false);
        return new BotDialogAction(Str(data, "status") ?? "", Bool(data, "changed"));
    }

    public async Task<BotDialogReply> ReplyToDialogAsync(string chatId, string text, CancellationToken ct = default)
    {
        var data = await _api.RequestAsync(HttpMethod.Post, Root + "dialogs/" + Uri.EscapeDataString(chatId) + "/", new Dictionary<string, object?> { ["action"] = "reply", ["text"] = text }, null, ct).ConfigureAwait(false);
        return new BotDialogReply(Str(data, "status") ?? "", Bool(data, "sent"));
    }

    public async Task<BotSalesStats> GetSalesStatsAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string>
        {
            ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["to"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var d = await _api.RequestAsync(HttpMethod.Get, Root + "sales-stats/", null, query, ct).ConfigureAwait(false);
        var orders = Obj(d, "orders");
        var funnel = Obj(d, "funnel");
        var handoffs = Obj(d, "handoffs");
        var byReason = new Dictionary<string, int>();
        if (handoffs.TryGetProperty("by_reason", out var br) && br.ValueKind == JsonValueKind.Object)
            foreach (var p in br.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var cnt))
                    byReason[p.Name] = cnt;
        var unknown = new List<BotUnknownQuestion>();
        if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("unknown_questions", out var uq) && uq.ValueKind == JsonValueKind.Array)
            foreach (var q in uq.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                unknown.Add(new BotUnknownQuestion(Date(q, "at"), Str(q, "chat_id") ?? "", Str(q, "text") ?? "", Str(q, "name") ?? "", Str(q, "client_id")));
        return new BotSalesStats(Int(d, "dialogs"), Int(d, "messages"), Int(d, "people"),
            Int(orders, "count"), Dec(orders, "total"), Int(orders, "done"), Dec(orders, "done_total"), Int(orders, "canceled"), Dec(orders, "avg_check"),
            Num(d, "conversion_percent"), Int(funnel, "handoffs"),
            Int(handoffs, "total"), byReason, Int(handoffs, "callbacks"), Int(handoffs, "answered"), Int(handoffs, "unanswered"), Int(handoffs, "waiting_now"),
            NullableInt(handoffs, "avg_answer_seconds"), NullableInt(handoffs, "median_answer_seconds"), unknown, ParseBargainStats(d));
    }

    public async Task<BotTestAnswer> TestAsCustomerAsync(string question, CancellationToken ct = default)
    {
        var d = await _api.RequestAsync(HttpMethod.Post, Root + "test-ai/", new Dictionary<string, object?> { ["question"] = question, ["as"] = "customer" }, null, ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var matches = d.ValueKind == JsonValueKind.Object && d.TryGetProperty("catalog_matches", out var cm) && cm.ValueKind == JsonValueKind.Array ? cm.GetArrayLength() : 0;
        return new BotTestAnswer(BotSellerText.Plain(Str(d, "answer")), Str(d, "model") ?? "", Str(d, "handoff"), ParseDraft(d), matches, ParseBargainNote(d));
    }

    private static BotBargainNote? ParseBargainNote(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("bargain", out var b) || b.ValueKind != JsonValueKind.Object)
            return null;
        var text = BotSellerText.Plain(Str(b, "text"));
        return text.Length == 0 ? null : new BotBargainNote(text, b.TryGetProperty("tag", out var tag) ? DecN(tag, "offer") : null);
    }

    private static BotOrderDraft? ParseDraft(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("order_draft", out var od) || od.ValueKind != JsonValueKind.Object)
            return null;
        var items = new List<BotOrderDraftItem>();
        if (od.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var i in arr.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                items.Add(new BotOrderDraftItem(Str(i, "title") ?? "", Str(i, "variant_id") ?? "", Str(i, "size") ?? "", Str(i, "color") ?? "", Str(i, "qty") ?? ""));
        return new BotOrderDraft(Str(od, "name") ?? "", Str(od, "phone") ?? "", items, Str(od, "comment") ?? "");
    }

    internal static BotSellerConfig? ParseSellerConfig(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || !d.TryGetProperty("seller_config", out var s) || s.ValueKind != JsonValueKind.Object)
            return null;
        var words = new List<string>();
        if (s.TryGetProperty("forbidden_words", out var fw) && fw.ValueKind == JsonValueKind.Array)
            words.AddRange(fw.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
        var knowledge = Obj(s, "knowledge");
        return new BotSellerConfig(
            Bool(s, "seller_enabled"),
            !s.TryGetProperty("orders_enabled", out var oe) || oe.ValueKind != JsonValueKind.False,
            Str(s, "tone") ?? "friendly", Str(s, "greeting") ?? "", Str(s, "signature") ?? "", words, Str(s, "language") ?? "auto",
            !s.TryGetProperty("handoff_enabled", out var he) || he.ValueKind != JsonValueKind.False,
            ParseHours(s), Str(s, "off_hours_reply") ?? "",
            Int(s, "handoff_return_minutes") is > 0 and var minutes ? minutes : 120,
            s.TryGetProperty("confirm_threshold", out var thr) && thr.ValueKind == JsonValueKind.Number && thr.TryGetInt64(out var th) ? th : 30000,
            Str(knowledge, "rules") ?? "", Str(knowledge, "delivery") ?? "", Str(knowledge, "payment") ?? "", Str(knowledge, "work_hours") ?? "", ParseBargainConfig(s));
    }

    private static BotSellerHours ParseHours(JsonElement s)
    {
        if (!s.TryGetProperty("human_hours", out var h) || h.ValueKind != JsonValueKind.Object)
            return BotSellerHours.Default;
        if (!h.EnumerateObject().Any())
            return new BotSellerHours(true, false, "09:00", "20:00");
        if (Str(h, "from") is { Length: > 0 } from && Str(h, "to") is { Length: > 0 } to)
            return new BotSellerHours(false, false, from, to);
        return new BotSellerHours(false, true, "", "");
    }

    private static BotDialog ParseDialog(JsonElement r)
    {
        BotHandoff? handoff = null;
        if (r.TryGetProperty("handoff", out var h) && h.ValueKind == JsonValueKind.Object)
            handoff = new BotHandoff(Str(h, "reason") ?? "", Str(h, "status") ?? "", Str(h, "question") ?? "", Str(h, "phone") ?? "", Str(h, "note") ?? "", Str(h, "taken_by") ?? "",
                Date(h, "created_at"), Date(h, "first_answer_at"));
        return new BotDialog(Str(r, "chat_id") ?? "", Str(r, "name") ?? "", Str(r, "username") ?? "", Int(r, "messages_count"), Int(r, "orders_count"),
            Date(r, "last_at"), Str(r, "last_text") ?? "", Str(r, "status") ?? "bot", handoff);
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>Вложенный объект или пустой объект (чтобы Str / Int по отсутствующему блоку не падали).</summary>
    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : EmptyObject;

    private static int? NullableInt(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    /// <summary>Сумма от сервера — строкой «1200.00» или числом; в decimal без округления.</summary>
    private static decimal Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetDecimal(out var n) ? n : 0m,
                JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) => s,
                _ => 0m,
            }
            : 0m;
}
