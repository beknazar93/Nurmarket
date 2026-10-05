using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-09-30, владелец: «добавь ИИ, чтобы бот отвечал и общался».
///
/// Разговорная нейросеть для бота владельца — Google Gemini по БЕСПЛАТНОМУ ключу
/// (aistudio.google.com → Get API key; карта не нужна). Точные вопросы (выручка, должники, цена
/// товара) по-прежнему считает сама касса (TelegramAssistant); сюда приходит только разговор:
/// приветствия, «почему упала выручка», «как поднять продажи», непонятные фразы. Нейросеть
/// получает короткую сводку магазина (выручка, топ, что заканчивается, найденные товары) и
/// отвечает по ней, не выдумывая цифр.
///
/// Честно: на бесплатном уровне Google может использовать запросы для улучшения своих продуктов —
/// это написано владельцу в настройках рядом с полем ключа. Без ключа ничего никуда не уходит.
/// Имена моделей Google меняет — пробуем несколько по очереди и запоминаем ту, что ответила.
/// </summary>
public static class TelegramAiChat
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    // 2026-09-30: проверено на ключе владельца — «лёгкие» модели отвечают за 1,5–2,5 с, gemini-flash-latest и
    // gemini-3.5-flash «думают» 4–9 с и бывают перегружены (503); gemini-2.5-flash-lite этому ключу недоступна (404).
    private static readonly string[] Models = { "gemini-3.5-flash-lite", "gemini-flash-lite-latest", "gemini-3.1-flash-lite", "gemini-flash-latest", "gemini-3.5-flash" };

    private static string? _workingModel;

    /// <summary>Последние реплики по каждому чату — чтобы бот помнил, о чём только что говорили.</summary>
    private static readonly Dictionary<string, List<(string Role, string Text)>> History = new();

    private const int HistoryTurns = 8;

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(UserPreferences.Instance.TelegramAiKey);

    private const string SystemPrompt =
        "Ты — ИИ-помощник владельца магазина в Кыргызстане и общаешься с ним в Telegram. "
        + "Отвечай коротко (до 6–8 предложений), дружелюбно и по делу, на языке собеседника: по-русски или по-кыргызски. "
        + "Цифры магазина бери ТОЛЬКО из сводки ниже; если нужных данных в ней нет — честно скажи об этом и подскажи команду бота: "
        + "/segodnya — выручка сегодня, /nedelya — за неделю, /top — лучшие товары, /abc — ABC-анализ, /zakaz — что заказать, "
        + "/ostatki — что заканчивается, /dolgi — должники. Никогда не выдумывай суммы, остатки и цены. "
        + "Можно давать общие советы по торговле, выкладке, закупкам и работе с покупателями. Валюта — сом. "
        + ListRules;

    /// <summary>2026-09-30, владелец: «списки в боте некрасивые, всё смешано». Единые правила
    /// оформления для обоих режимов — нейросеть пишет список в одном и том же простом виде,
    /// а ToTelegramHtml переводит его в разметку Telegram.</summary>
    private const string ListRules =
        " ОФОРМЛЕНИЕ (Telegram, не Markdown): не используй таблицы, решётки (#), звёздочки для курсива. "
        + "Список — каждый пункт С НОВОЙ СТРОКИ и начинается с «• », например: «• Кока-Кола 1л — 85 сом». "
        + "Не больше 10–15 пунктов; если товары разного вида — сначала короткий заголовок группы отдельной строкой "
        + "(например «Напитки:»), под ним пункты. Между группами — пустая строка. "
        + "Название товара можно выделить **жирным**. Перед списком и после — не больше одной короткой фразы.";

    /// <summary>2026-10-05, владелец: «в десктопе открой чат с ИИ для владельца, чтобы владелец советовался с ним —
    /// специальную вкладку». Раздел «ИИ-советник» программы владельца (AiAdvisorWindow): та же сводка магазина,
    /// что у бота, свой разговор, вместо команд бота — разделы программы; ответ простым текстом.</summary>
    /// <param name="serverSummary">Цифры «Сводки» программы владельца (отчёт сервера NurCRM). Есть — выручка берётся
    /// из них, а не из локальной истории продаж (она бывает неполной или с повторами: живой случай 05.10 —
    /// 1 353 572 сом за 7 дней по локальной истории против 886 049 сом на сервере).</param>
    public static Task<(string? Answer, string? Error)> AskOwnerAppAsync(string question, string? serverSummary, CancellationToken ct) =>
        AskCoreAsync(OwnerAppHistoryKey, question,
            OwnerAppPrompt + "\n\nСВОДКА МАГАЗИНА на " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ":\n"
            + (string.IsNullOrWhiteSpace(serverSummary) ? "" : serverSummary.Trim() + "\n")
            + BuildShopContext(question, localRevenue: string.IsNullOrWhiteSpace(serverSummary)), ct, raw: true, webSearch: true);

    /// <summary>Источник из интернета, на который опирался ответ (поиск Google в Gemini).</summary>
    public sealed record WebSource(string Title, string Uri);

    /// <summary>2026-10-05, владелец: «включи поиск по интернету для ИИ». Источники последнего ответа с поиском
    /// (ИИ-советник показывает их ссылками под ответом). Пусто — ИИ ответил без поиска.</summary>
    public static IReadOnlyList<WebSource> LastWebSources { get; private set; } = Array.Empty<WebSource>();

    /// <summary>2026-10-05, проверка на ключе владельца: обычные запросы — 200, с поиском Google — 429 (у бесплатного ключа
    /// Gemini квоты на поиск нет). До этого времени поиск не пробуем, отвечаем без него.</summary>
    private static DateTime _webSearchBlockedUntilUtc = DateTime.MinValue;

    /// <summary>Поиск в интернете сейчас недоступен для ключа (последний запрос с поиском получил 429).</summary>
    public static bool WebSearchUnavailable => DateTime.UtcNow < _webSearchBlockedUntilUtc;

    /// <summary>Один вопрос с поиском Google, без истории (поиск фото товара в интернете — ProductPhotoFinder).</summary>
    public static async Task<(string? Answer, IReadOnlyList<WebSource> Sources, string? Error)> AskWebAsync(string system, string question, CancellationToken ct)
    {
        var key = UserPreferences.Instance.TelegramAiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (null, Array.Empty<WebSource>(), "ключ ИИ не задан");
        if (WebSearchUnavailable)
            return (null, Array.Empty<WebSource>(), "поиск в интернете недоступен для этого ключа ИИ");
        var (answer, error, sources) = await GenerateCoreAsync(key!, system, new List<(string, string)>(), question, ct,
            webSearch: true, fallbackWithoutSearch: false).ConfigureAwait(false);
        return (answer, sources, error);
    }

    /// <summary>«Новый разговор» в разделе «ИИ-советник».</summary>
    public static void ResetOwnerAppHistory()
    {
        lock (History)
            History.Remove(OwnerAppHistoryKey);
    }

    private const string OwnerAppHistoryKey = "ownerapp";

    private const string OwnerAppPrompt =
        "Ты — ИИ-советник владельца магазина в Кыргызстане, встроенный в программу NurMarket (раздел «ИИ-советник»). "
        + "Владелец советуется с тобой о своём магазине. Отвечай по делу и дружелюбно, до 8–10 предложений, на языке собеседника "
        + "(русский, кыргызский, английский, турецкий или узбекский). "
        + "Цифры магазина бери ТОЛЬКО из сводки ниже; если нужных данных в ней нет — честно скажи об этом и подскажи раздел программы: "
        + "«Продажи», «Аналитика», «ABC-анализ», «Пополнение и сроки», «Прибыль и деньги», «Клиенты» (долги), «Склад». "
        + "В сводке есть итоги долгов и список должников: каждый должник обозначен кодом вида [Д1], [Д2]. Называя должника, "
        + "пиши его код в квадратных скобках ровно так, как в сводке, — программа сама покажет владельцу вместо кода имя и телефон. "
        + "Никогда не пиши, что имена или телефоны скрыты, недоступны или конфиденциальны — владелец видит их вместо кодов. "
        + "Просят список должников — перечисли ВСЕХ из сводки, каждого с новой строки: «• [Д1] — 1 000 сом, чеков: 2, долг с 01.09.2026», "
        + "в конце — итог. "
        + "В сводке есть «АНАЛИЗ ДЛЯ АКЦИЙ»: товары без продаж, затоваренные, с падающими продажами, с низкой наценкой, растущие, "
        + "клиенты и заказы через бота. Когда спрашивают про акции, проблемные товары или «что делать со складом» — предлагай "
        + "конкретные акции на конкретные товары: какой товар, какая скидка или комплект, на какой срок и почему; соблюдай правила "
        + "акций из анализа (не ниже закупки + 5 %, на товары с низкой наценкой — без скидки). "
        + "Фото товаров программа ищет и ставит сама: если просят загрузить или найти фото — скажи нажать «Найди фото для товаров без фото». "
        // 2026-10-05, владелец: «включи поиск по интернету для ИИ».
        + "У тебя есть поиск Google: используй его, когда нужны сведения извне — цены у конкурентов и поставщиков, новинки, "
        + "законы и налоги Кыргызстана, курсы валют, праздники и сезонный спрос, как продавать тот или иной товар. Цифры своего магазина — "
        + "только из сводки, не из интернета. Найденное в интернете называй как найденное и не выдумывай. "
        // 2026-10-05, владелец: «добавь возможность ИИ управлять ботом» (ответ «1 да»). Сам ИИ ничего не меняет:
        // он пишет строку БОТ: {...}, программа показывает её владельцу карточкой и применяет только по «Применить».
        + "Ты можешь включать и выключать функции Телеграм-бота магазина. Состояние бота — в сводке (раздел «БОТ»). "
        + "Если владелец просит включить или выключить функцию бота, коротко скажи, что изменится, и ПОСЛЕДНЕЙ строкой ответа "
        + "напиши ровно: БОТ: {\"ключ\": true/false} — только ключи shift_summary_enabled (сводка смены владельцу), "
        + "commands_enabled (команды владельца в боте), ai_enabled (ИИ в боте), consultant_enabled (ИИ-консультант для покупателей), "
        + "voice_replies_enabled (ответы голосом). Программа покажет владельцу кнопку «Применить» — до неё ничего не меняется, "
        + "поэтому не пиши, что уже включил. В таком ответе — одна-две фразы о том, что изменится, без других цифр магазина. "
        // 2026-10-05, ТЗ часть 11: свои ответы бота (команда /adres, ответ на слово «доставка») — тоже через подтверждение.
        + "Свои ответы бота: если в сводке «СЦЕНАРИИ БОТА» НЕ написано «сервер пока не поддерживает» и владелец просит добавить "
        + "команду или ответ бота (адрес, доставка, график работы, оплата), составь короткий вежливый ответ покупателю и ПОСЛЕДНЕЙ строкой "
        + "напиши ровно: СЦЕНАРИЙ: {\"kind\": \"command\" или \"keywords\", \"command\": \"латиница_без_слеша\" (для command), "
        + "\"keywords\": [\"слово\", …] (для keywords, по-русски и по-кыргызски), \"title\": \"название\", \"reply_text\": \"ответ, можно теги <b> <i>\", "
        + "\"audience\": \"customers\"} — одной строкой JSON. Не выдумывай адрес, телефон и цены — бери их у владельца или из сводки; "
        + "если данных нет — спроси. Если сервер не поддерживает сценарии — скажи, что они появятся после обновления сервера. "
        + "В сводке есть ABC-анализ за 30 дней (срезы по выручке, прибыли, количеству, категориям, брендам и складу, группы A/B/C) — "
        + "по нему советуй, что нельзя допускать до нуля (A), что держать в меньшем запасе или выводить (C), где товар в A по выручке, "
        + "но в C по прибыли (мало наценки). "
        + "В сводке есть и склад: все товары с категорией, ценой продажи, закупкой и остатком — по нему считай наценку, "
        + "замороженные в остатках деньги, что закончилось и что заказать. "
        + "Никогда не выдумывай суммы, остатки и цены. Давай конкретные советы: что заказать, что продвигать, где теряются деньги, "
        + "как поднять продажи, как работать с покупателями и выкладкой. Валюта — сом. "
        + "Оформление — простой текст: без таблиц, решёток (#) и звёздочек; список — каждый пункт с новой строки и начинается с «• », "
        + "не больше 10–15 пунктов.";

    /// <summary>2026-10-05: ответ нейросети для окна программы (не Telegram): единые «• », без разметки Markdown.</summary>
    public static string ToPlainText(string text)
    {
        var s = text.Replace("\r\n", "\n");
        s = Regex.Replace(s, @"(?<=\S)[ \t]+[\*•][ \t]+(?=\S)", "\n• ");
        s = Regex.Replace(s, @"(?m)^[ \t]*[\*\-\+•][ \t]+", "• ");
        s = Regex.Replace(s, @"(?m)^[ \t]*#{1,6}[ \t]*", "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s;
    }

    /// <summary>Ответ на реплику владельца. Error — понятная владельцу причина, если не вышло.</summary>
    public static Task<(string? Answer, string? Error)> AskAsync(string chatId, string question, CancellationToken ct) =>
        AskCoreAsync("owner:" + chatId, question,
            SystemPrompt + "\n\nСВОДКА МАГАЗИНА на " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + ":\n" + BuildShopContext(question), ct);

    /// <summary>2026-09-30, решение владельца «консультант для всех»: любой, кто пишет боту, общается
    /// с ИИ-продавцом. Сводка — ТОЛЬКО каталог (товары, цены, есть ли в наличии) и контакты магазина:
    /// выручки, долгов и данных других покупателей в ней нет вовсе, поэтому ИИ не может их выдать даже
    /// по просьбе. Не больше <see cref="CustomerLimitPerHour"/> вопросов в час от одного человека —
    /// чтобы посторонние не выбрали бесплатный лимит Google.</summary>
    public static async Task<(string? Answer, string? Error)> AskCustomerAsync(string chatId, string question, CancellationToken ct)
    {
        lock (CustomerRequests)
        {
            if (!CustomerRequests.TryGetValue(chatId, out var times))
                CustomerRequests[chatId] = times = new Queue<DateTime>();
            while (times.Count > 0 && times.Peek() < DateTime.UtcNow.AddHours(-1))
                times.Dequeue();
            if (times.Count >= CustomerLimitPerHour)
                return ("Вы задали много вопросов подряд 🙂 Давайте продолжим через час — или позвоните в магазин.", null);
            times.Enqueue(DateTime.UtcNow);
        }

        var prefs = UserPreferences.Instance;
        var system = CustomerPrompt
            .Replace("{shop}", prefs.StoreName)
            + "\n\nМАГАЗИН: " + prefs.StoreName
            + (string.IsNullOrWhiteSpace(prefs.StoreAddress) ? "" : "\nАдрес: " + prefs.StoreAddress)
            + (string.IsNullOrWhiteSpace(prefs.OwnerPhone) ? "" : "\nТелефон магазина: " + prefs.OwnerPhone)
            + (MarketSpheres.IsClothing || MarketSpheres.IsServices
                ? "\nПРОКАТ: магазин даёт вещи напрокат с залогом (деньги или паспорт). Если спрашивают про прокат или аренду — "
                  + "скажи, что это можно оформить в магазине, цена и срок — у продавца; назови вещи из каталога в наличии, размеры и цвета, если они указаны."
                : "")
            + "\n\nКАТАЛОГ (цена и наличие):\n" + TelegramAssistant.CustomerCatalogContext(question, EarlierCustomerText(chatId))
            + CustomerProfileBlock(chatId);
        var (answer, error) = await AskCoreAsync("client:" + chatId, question, system, ct, raw: true).ConfigureAwait(false);
        if (answer == null)
            return (null, error);
        answer = await ProcessOrderAsync(chatId, answer, ct).ConfigureAwait(false);
        return (ToTelegramHtml(answer), null);
    }

    /// <summary>2026-10-01, решение владельца «обращения → заказы с сайта». Создание заказа витрины —
    /// ставит программа (ShowcaseApiService.CreateBotOrderAsync); null — заказы из бота не подключены.</summary>
    public static Func<string, string, IReadOnlyList<(string ProductId, double Qty)>, string?, CancellationToken,
        Task<(string Id, string Number, decimal Total)>>? OrderCreator { get; set; }

    private static readonly Regex OrderLine = new(@"(?m)^[ \t]*ЗАКАЗ:[ \t]*(\{.*\})[ \t]*$", RegexOptions.Compiled);

    /// <summary>Нейросеть дописывает строку «ЗАКАЗ: {…}», только когда покупатель подтвердил заказ и
    /// назвал телефон. Строку убираем из ответа, товары сверяем с каталогом, заказ создаём на сервере
    /// (цену считает сервер) и сообщаем покупателю номер и сумму, а владельцу — в его чат.</summary>
    private static async Task<string> ProcessOrderAsync(string chatId, string answer, CancellationToken ct)
    {
        var match = OrderLine.Match(answer);
        if (!match.Success)
            return answer;
        var text = OrderLine.Replace(answer, "").Trim();

        string name, phone, comment;
        var items = new List<(string ProductId, double Qty)>();
        var titles = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;
            name = root.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? "" : "";
            phone = root.TryGetProperty("phone", out var ph) ? ph.GetString()?.Trim() ?? "" : "";
            comment = root.TryGetProperty("comment", out var c) ? c.GetString()?.Trim() ?? "" : "";
            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var it in arr.EnumerateArray())
                {
                    var title = it.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                    double qty = 1;
                    if (it.TryGetProperty("qty", out var q))
                    {
                        if (q.ValueKind == JsonValueKind.Number)
                            qty = q.GetDouble();
                        else if (q.ValueKind == JsonValueKind.String
                                 && double.TryParse(q.GetString()?.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                                     System.Globalization.CultureInfo.InvariantCulture, out var qs))
                            qty = qs;
                    }
                    var product = TelegramAssistant.FindProductByTitle(title);
                    if (product != null && qty > 0)
                    {
                        items.Add((product.Id, qty));
                        titles.Add($"{product.Title} × {qty:0.###}");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            PosLogger.Log($"Заказ из бота: не разобран ({ex.Message}).", "TELEGRAM");
            return text;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (items.Count == 0 || digits.Length < 9)
        {
            PosLogger.Log($"Заказ из бота не оформлен: товаров {items.Count}, телефон {(digits.Length < 9 ? "не указан" : "есть")}.", "TELEGRAM");
            return text + "\n\nЧтобы оформить заказ, напишите, пожалуйста, товары из нашего каталога и номер телефона.";
        }

        // Реквизиты запоминаем сразу — в следующий раз бот только попросит их подтвердить.
        if (!string.IsNullOrWhiteSpace(name))
            TelegramInquiryStore.SaveProfile(chatId, name, phone);

        if (OrderCreator == null)
            return text + "\n\nЗаказ передан продавцу — вам перезвонят.";

        try
        {
            var (_, number, total) = await OrderCreator(string.IsNullOrWhiteSpace(name) ? "Покупатель из Telegram" : name,
                phone, items, comment, ct).ConfigureAwait(false);
            TelegramInquiryStore.MarkLastOrder(chatId, number);
            PosLogger.Log($"Заказ из бота №{number} оформлен: {items.Count} поз., {total:0.##} сом.", "TELEGRAM");
            _ = TelegramBotService.SendAsync(
                $"🛒 <b>Новый заказ из бота №{Esc(number)}</b>\n{Esc(name)} · {Esc(phone)}\n"
                + string.Join("\n", titles.Select(t => "• " + Esc(t)))
                + $"\nСумма: {total:N2} сом" + (string.IsNullOrWhiteSpace(comment) ? "" : $"\nКомментарий: {Esc(comment)}"));
            return text + $"\n\n✅ Заказ №{number} оформлен на сумму {total:N2} сом. Магазин свяжется с вами по номеру {phone}.";
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Заказ из бота: сервер не принял ({ex.Message}).", "TELEGRAM");
            _ = TelegramBotService.SendAsync(
                $"🛒 <b>Заказ из бота не записался на сервер</b> — свяжитесь с покупателем:\n{Esc(name)} · {Esc(phone)}\n"
                + string.Join("\n", titles.Select(t => "• " + Esc(t))));
            return text + "\n\nЗаказ передан продавцу — вам перезвонят для подтверждения.";
        }
    }

    private static string Esc(string? t) => (t ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>2026-10-01, владелец: «запомнить ФИО и телефон, не спрашивать при каждом заказе —
    /// просто попросить подтвердить: "Эти реквизиты верны? Если нет — напишите заново"».</summary>
    private static string CustomerProfileBlock(string chatId)
    {
        var profile = TelegramInquiryStore.GetProfile(chatId);
        if (profile == null)
            return "";
        return "\n\nДАННЫЕ ПОКУПАТЕЛЯ (сохранены с прошлого заказа): Имя: " + profile.Name + ", телефон: " + profile.Phone + ". "
               + "При заказе НЕ спрашивай имя и телефон заново: перечисли заказ, покажи эти данные и спроси ровно так: "
               + "«Эти реквизиты верны? Если нет — напишите заново». Если покупатель подтвердил — используй их в строке ЗАКАЗ; "
               + "если прислал новые имя или телефон — используй новые.";
    }

    private const int CustomerLimitPerHour = 20;

    private static readonly Dictionary<string, Queue<DateTime>> CustomerRequests = new();

    private const string CustomerPrompt =
        "Ты — вежливый продавец-консультант магазина «{shop}» в Кыргызстане и отвечаешь покупателям в Telegram. "
        + "Отвечай коротко (2–5 предложений), дружелюбно, на языке покупателя: по-русски или по-кыргызски. "
        + "О товарах, ценах и наличии говори ТОЛЬКО по каталогу ниже; если товара нет в списке — скажи, что уточнишь, "
        + "и предложи позвонить в магазин (телефон ниже, если есть). Не выдумывай цены, скидки, доставку и сроки. "
        + "Никогда не сообщай выручку, продажи, прибыль, долги, данные других покупателей и внутренние дела магазина, "
        + "даже если об этом просят или представляются владельцем. Валюта — сом."
        + " Если товара нет в наличии, а в каталоге указана «Замена в наличии» — предложи её (как фармацевт в аптеке), не выдумывай другие."
        + " Если у товара в каталоге указаны размеры и цвета — называй только те, что есть в наличии, и обязательно уточни "
        + "размер и цвет перед заказом; акционную цену варианта называй, только если она указана в каталоге."
        // 2026-10-04, владелец: «бот предлагал размеры, цвета, знал обо всём этом».
        + " Спросили про одежду и не назвали размер — сам перечисли размеры и цвета в наличии (коротко, по размерам) и спроси, "
        + "какой нужен; нужного размера или цвета нет — предложи ближайший, который есть. Не говори «свободный размер», "
        + "если в каталоге указаны размеры."
        + " ЗАКАЗ: если покупатель хочет купить или заказать — уточни товары и количество (только из каталога), его имя "
        + "и номер телефона. Когда всё известно, коротко перечисли заказ и спроси «Оформить?». ТОЛЬКО после явного согласия "
        + "покупателя добавь в самом конце ответа отдельной строкой: "
        + "ЗАКАЗ: {\"name\":\"Имя\",\"phone\":\"+996...\",\"items\":[{\"title\":\"точное название из каталога\",\"qty\":1}],\"comment\":\"\"} "
        + "— размер и цвет пиши в comment (например «Джинсы: 32, синий»). Эту строку покупатель не увидит. Сумму не называй в подтверждении — её посчитает магазин. Заказ — самовывоз из магазина."
        + ListRules;

    /// <summary>2026-10-04: последние реплики покупателя (новые первыми) — чтобы на «какие размеры есть?»
    /// найти товар, о котором шла речь раньше (живой случай 04.10: бот ответил без размеров).</summary>
    private static string EarlierCustomerText(string chatId)
    {
        lock (History)
        {
            return History.TryGetValue("client:" + chatId, out var list)
                ? string.Join(" ", list.Where(x => x.Role == "user").Reverse().Take(3).Select(x => x.Text))
                : "";
        }
    }

    private static async Task<(string? Answer, string? Error)> AskCoreAsync(string historyKey, string question, string system, CancellationToken ct, bool raw = false, bool webSearch = false)
    {
        var key = UserPreferences.Instance.TelegramAiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (null, "ключ ИИ не задан");

        List<(string Role, string Text)> history;
        lock (History)
        {
            if (!History.TryGetValue(historyKey, out history!))
                History[historyKey] = history = new List<(string, string)>();
            history = history.ToList();
        }

        var chatId = historyKey;
        var (answer, error, sources) = await GenerateCoreAsync(key!, system, history, question, ct, webSearch).ConfigureAwait(false);
        if (webSearch)
            LastWebSources = sources;
        if (answer == null)
            return (null, error);

        lock (History)
        {
            var list = History[chatId];
            list.Add(("user", question));
            list.Add(("model", answer));
            while (list.Count > HistoryTurns * 2)
                list.RemoveAt(0);
        }

        return (raw ? answer : ToTelegramHtml(answer), null);
    }

    /// <summary>2026-10-05: один вопрос без истории разговора (голосовое управление кассы — VoiceAi).</summary>
    public static async Task<(string? Answer, string? Error)> AskOnceAsync(string system, string question, CancellationToken ct)
    {
        var key = UserPreferences.Instance.TelegramAiKey;
        if (string.IsNullOrWhiteSpace(key))
            return (null, "ключ ИИ не задан");
        return await GenerateAsync(key!, system, new List<(string, string)>(), question, ct).ConfigureAwait(false);
    }

    /// <summary>Проверка ключа из настроек: короткий запрос без данных магазина.</summary>
    public static async Task<(bool Ok, string Message)> TestKeyAsync(string key, CancellationToken ct)
    {
        var (answer, error) = await GenerateAsync(key.Trim(), "Отвечай одним коротким предложением.",
            new List<(string, string)>(), "Скажи по-русски, что ты на связи.", ct).ConfigureAwait(false);
        return answer != null ? (true, $"{answer.Trim()} ({_workingModel})") : (false, error ?? "нет ответа");
    }

    private static async Task<(string? Answer, string? Error)> GenerateAsync(
        string key, string system, List<(string Role, string Text)> history, string question, CancellationToken ct)
    {
        var (answer, error, _) = await GenerateCoreAsync(key, system, history, question, ct, webSearch: false).ConfigureAwait(false);
        return (answer, error);
    }

    /// <summary>2026-10-05, владелец: «включи поиск по интернету для ИИ» — webSearch добавляет инструмент google_search
    /// (Gemini сам решает, когда искать). Модель без поддержки поиска (400 про tool) — тот же запрос без поиска.</summary>
    private static async Task<(string? Answer, string? Error, IReadOnlyList<WebSource> Sources)> GenerateCoreAsync(
        string key, string system, List<(string Role, string Text)> history, string question, CancellationToken ct, bool webSearch,
        bool fallbackWithoutSearch = true)
    {
        if (webSearch && WebSearchUnavailable && fallbackWithoutSearch)
            webSearch = false;
        var contents = new JsonArray();
        foreach (var (role, text) in history)
            contents.Add(new JsonObject { ["role"] = role, ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) });
        contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = question }) });

        string Body(bool search)
        {
            var b = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                ["contents"] = contents.DeepClone(),
                ["generationConfig"] = new JsonObject { ["temperature"] = 0.5, ["maxOutputTokens"] = search ? 1200 : 700 },
            };
            if (search)
                b["tools"] = new JsonArray(new JsonObject { ["google_search"] = new JsonObject() });
            return b.ToJsonString();
        }
        var body = Body(webSearch);
        var none = (IReadOnlyList<WebSource>)Array.Empty<WebSource>();

        var models = (_workingModel is { } known ? new[] { known }.Concat(Models.Where(m => m != known)) : Models).ToList();
        string? lastError = null;
        for (var mi = 0; mi < models.Count; mi++)
        {
            var model = models[mi];
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
                request.Headers.Add("x-goog-api-key", key);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var text = ReadText(json);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _workingModel = model;
                        return (text, null, webSearch ? ReadSources(json) : none);
                    }

                    lastError = "нейросеть вернула пустой ответ";
                    continue;
                }

                var message = ReadError(json);
                if (webSearch && response.StatusCode == (HttpStatusCode)429)
                {
                    // Поиск Google не входит в квоту ключа — полчаса не пробуем; ответ без поиска (если можно).
                    _webSearchBlockedUntilUtc = DateTime.UtcNow.AddMinutes(30);
                    PosLogger.Log($"ИИ: поиск в интернете недоступен для ключа (429 у {model}) — {(fallbackWithoutSearch ? "отвечаю без поиска" : "пропускаю")}.", "TELEGRAM");
                    if (!fallbackWithoutSearch)
                        return (null, "поиск в интернете недоступен для этого ключа ИИ", none);
                    webSearch = false;
                    body = Body(false);
                    mi--;
                    continue;
                }
                if (webSearch && response.StatusCode == HttpStatusCode.BadRequest
                    && (message.Contains("google_search", StringComparison.OrdinalIgnoreCase) || message.Contains("tool", StringComparison.OrdinalIgnoreCase)
                        || message.Contains("grounding", StringComparison.OrdinalIgnoreCase)))
                {
                    PosLogger.Log($"ИИ: модель {model} не умеет поиск в интернете — отвечаю без поиска ({message}).", "TELEGRAM");
                    webSearch = false;
                    body = Body(false);
                    mi--;
                    continue;
                }
                // 2026-09-30 (живой случай): 503 «This model is currently experiencing high demand» —
                // модель перегружена; раньше бот сразу сдавался. Теперь — следующая модель по списку.
                if ((int)response.StatusCode >= 500)
                {
                    lastError = "нейросеть Google сейчас перегружена — попробуйте через минуту";
                    if (_workingModel == model)
                        _workingModel = null;
                    continue;
                }

                // Модель не найдена или недоступна этому ключу — пробуем следующую.
                if (response.StatusCode is HttpStatusCode.NotFound
                    || (response.StatusCode == HttpStatusCode.BadRequest && message.Contains("model", StringComparison.OrdinalIgnoreCase) && !message.Contains("API key", StringComparison.OrdinalIgnoreCase))
                    || (response.StatusCode == HttpStatusCode.Forbidden && message.Contains("model", StringComparison.OrdinalIgnoreCase)))
                {
                    lastError = $"модель {model} недоступна";
                    continue;
                }

                if (response.StatusCode == (HttpStatusCode)429)
                    return (null, "исчерпан бесплатный лимит запросов Google — попробуйте через минуту", none);
                if (message.Contains("API key", StringComparison.OrdinalIgnoreCase) || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                    return (null, "ключ Google не подходит — проверьте его в настройках бота", none);
                return (null, $"ошибка Google {(int)response.StatusCode}: {message}", none);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, "Google не ответил вовремя", none);
            }
            catch (HttpRequestException ex)
            {
                return (null, "нет связи с Google: " + ex.Message, none);
            }
        }

        return (null, lastError ?? "нет подходящей модели", none);
    }

    /// <summary>Источники ответа с поиском: candidates[0].groundingMetadata.groundingChunks[].web (uri, title).</summary>
    private static IReadOnlyList<WebSource> ReadSources(string json)
    {
        var list = new List<WebSource>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("groundingMetadata", out var meta)
                && meta.TryGetProperty("groundingChunks", out var chunks) && chunks.ValueKind == JsonValueKind.Array)
            {
                foreach (var chunk in chunks.EnumerateArray())
                {
                    if (chunk.TryGetProperty("web", out var web) && web.TryGetProperty("uri", out var uri) && uri.GetString() is { Length: > 0 } u)
                        list.Add(new WebSource(web.TryGetProperty("title", out var t) ? t.GetString() ?? u : u, u));
                }
            }
        }
        catch (JsonException)
        {
            // без источников
        }
        return list.DistinctBy(x => x.Uri).Take(8).ToList();
    }

    private static string? ReadText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                return null;
            if (!candidates[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                return null;
            var sb = new StringBuilder();
            foreach (var part in parts.EnumerateArray())
            {
                // «Размышления» модели (thought) владельцу не показываем.
                if (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True)
                    continue;
                if (part.TryGetProperty("text", out var text))
                    sb.Append(text.GetString());
            }

            return sb.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ReadError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                return message.GetString() ?? "";
        }
        catch (JsonException)
        {
        }

        return json.Length > 200 ? json[..200] : json;
    }

    /// <summary>Сводка для нейросети: те же отчёты, что шлёт бот, без HTML-разметки.</summary>
    private static string BuildShopContext(string question, bool localRevenue = true)
    {
        var sb = new StringBuilder();
        void Add(Func<string> build)
        {
            try
            {
                sb.AppendLine(StripHtml(build()));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"ИИ-помощник: часть сводки не собрана ({ex.Message}).", "TELEGRAM");
            }
        }

        // 2026-10-05: «ИИ-советник» программы владельца передаёт выручку с сервера — локальную не добавляем.
        if (localRevenue)
        {
            Add(() => TelegramReportBuilder.BuildRevenue(1, "Выручка сегодня"));
            Add(() => TelegramReportBuilder.BuildRevenue(7, "Выручка за 7 дней"));
            Add(() => TelegramReportBuilder.BuildTopProducts(7, 8));
        }
        Add(() => TelegramReportBuilder.BuildLowStock(take: 10));
        Add(TelegramInquiryStore.ShortSummary);
        if (TelegramAssistant.RentalContext(question) is { } rentals)
            sb.AppendLine(rentals);
        if (TelegramAssistant.ProductContext(question) is { } products)
            sb.AppendLine("Товары из вопроса:\n" + products);

        var text = sb.ToString();
        return text.Length > 6000 ? text[..6000] : text;
    }

    private static string StripHtml(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));

    /// <summary>Markdown нейросети → HTML Telegram: экранируем, **жирный** → &lt;b&gt;, «* » → «• ».</summary>
    private static string ToTelegramHtml(string text)
    {
        var s = text.Replace("\r\n", "\n");
        // Пункты, слепленные в одну строку («Есть: * Кола * Фанта», «…сом • Сахар…»), — каждый с новой строки.
        s = Regex.Replace(s, @"(?<=\S)[ \t]+[\*•][ \t]+(?=\S)", "\n• ");
        // Маркеры списка в начале строки (*, -, +, •, с отступом) — единый «• ».
        s = Regex.Replace(s, @"(?m)^[ \t]*[\*\-\+•][ \t]+", "• ");
        // Заголовки Markdown «### Напитки» → «Напитки».
        s = Regex.Replace(s, @"(?m)^[ \t]*#{1,6}[ \t]*", "");
        s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");
        s = Regex.Replace(s, @"__(.+?)__", "<b>$1</b>");
        // Одиночные звёздочки (курсив Markdown) и обратные кавычки — убираем.
        s = s.Replace("*", "").Replace("`", "");
        // Строка-заголовок группы («Напитки:») — жирным.
        s = Regex.Replace(s, @"(?m)^(?!• )([^\n<]{2,40}):[ \t]*$", "<b>$1:</b>");
        // Не больше одной пустой строки подряд.
        s = Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
        return s.Length > 3800 ? s[..3800] + "…" : s;
    }
}
