using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-26 (ответ Умара 26.11, «Бот-продавец, этап 1»): общие мелочи окон «Бот-продавец», «Диалоги бота», «Отчёт по продажам бота» и «Проверить бота» — цвета из темы, карточка,
/// подпись над полем, тексты причин передачи и статусов. Окна собраны в коде, как «Журнал сообщений бота».</summary>
internal static class BotSellerUi
{
    public static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static void Theme(Control target, AvaloniaProperty property, string key) =>
        target.Bind(property, target.GetResourceObservable(key));

    public static TextBlock Soft(string text, double size = 12.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        Theme(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    public static TextBlock Heading(string text, double size = 15)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Theme(t, TextBlock.ForegroundProperty, "BrushText");
        return t;
    }

    public static Border Card(Control child, double padding = 14)
    {
        var card = new Border { Padding = new Thickness(padding, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = child };
        Theme(card, Border.BackgroundProperty, "BrushPanel");
        Theme(card, Border.BorderBrushProperty, "BrushBorder");
        return card;
    }

    /// <summary>Подпись над полем (зазор 4 px — подпись не налезает на рамку), под полем — необязательная пояснялка.</summary>
    public static StackPanel Labeled(string label, Control field, string? hint = null)
    {
        var box = new StackPanel { Spacing = 4 };
        var l = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Theme(l, TextBlock.ForegroundProperty, "BrushText");
        box.Children.Add(l);
        box.Children.Add(field);
        if (!string.IsNullOrWhiteSpace(hint))
            box.Children.Add(Soft(hint!, 12));
        Avalonia.Automation.AutomationProperties.SetName(field, label);
        return box;
    }

    public static Button Btn(string text, bool primary = false)
    {
        var b = new Button { Content = text, Classes = { primary ? "sec-primary" : "sec-outline" } };
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    public static IBotBargainApi? BargainApi() => App.AppHost?.Services.GetService(typeof(ServerTelegramBotApi)) as IBotBargainApi;

    /// <summary>Сумма для показа: «1 234,50 сом»; нет значения — «—».</summary>
    public static string Som(decimal? value) => value is { } v ? MoneyText.Som(v) : "—";

    /// <summary>Проценты для показа: «7,5 %».</summary>
    public static string Percent(decimal? value) => value is { } v ? MoneyText.Weight((double)v, "0.##") + " %" : "—";

    /// <summary>Решение движка торга (bargain/test/, bargain/offers/): fixed, counter, accept, decline; неизвестное слово сервера — как есть.</summary>
    public static string DecisionText(string decision) => decision switch
    {
        "accept" => T("Договорились", "Келишилди", "Deal", "Anlaştık", "Kelishildi"),
        "counter" => T("Встречная цена", "Каршы баа", "Counter price", "Karşı fiyat", "Qarshi narx"),
        "decline" => T("Лучшая цена", "Эң мыкты баа", "Best price", "En iyi fiyat", "Eng yaxshi narx"),
        "fixed" => T("Цена фиксированная", "Баасы туруктуу", "Fixed price", "Sabit fiyat", "Narx qat'iy"),
        _ => decision,
    };

    /// <summary>Поле правил торга для сообщений об ошибках 400 («Минимальная цена: Не выше цены товара.»).</summary>
    public static string BargainFieldLabel(string field) => field switch
    {
        "min_price" => T("Минимальная цена для торга", "Соодалашуунун минималдуу баасы", "Minimum price for bargaining", "Pazarlık için en düşük fiyat", "Savdolashish uchun minimal narx"),
        "max_discount_percent" => T("Макс. скидка, %", "Макс. арзандатуу, %", "Max. discount, %", "Maks. indirim, %", "Maks. chegirma, %"),
        "allowed" => T("Торг разрешён", "Соодалашууга уруксат", "Bargaining allowed", "Pazarlığa izin var", "Savdolashishga ruxsat"),
        "seller_config" => T("Бот-продавец", "Бот-сатуучу", "Seller bot", "Satıcı bot", "Bot-sotuvchi"),
        _ => field,
    };

    public static string BargainFailure(Exception ex) => ex is ApiException { StatusCode: 403 or 404 } ? Failure(ex) : BotBargainText.Describe(ex, BargainFieldLabel);

    /// <summary>«100,50» / «100.5» → число; пусто или не число — null.</summary>
    public static decimal? ParseAmount(string? text) =>
        decimal.TryParse((text ?? "").Replace(" ", "").Replace(" ", "").Replace(',', '.').Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static IBotSellerApi? Api() => App.AppHost?.Services.GetService(typeof(ServerTelegramBotApi)) as IBotSellerApi;

    public static string PeriodLabel(int days) => days switch
    {
        7 => T("7 дней", "7 күн", "7 days", "7 gün", "7 kun"),
        90 => T("90 дней", "90 күн", "90 days", "90 gün", "90 kun"),
        _ => T("30 дней", "30 күн", "30 days", "30 gün", "30 kun"),
    };

    public static string StatusText(string status) => status switch
    {
        "bot" => T("Отвечает бот", "Бот жооп берет", "Bot replies", "Bot yanıtlıyor", "Bot javob beradi"),
        "needs_human" => T("Ждёт продавца", "Сатуучуну күтүүдө", "Waiting for the seller", "Satıcıyı bekliyor", "Sotuvchini kutmoqda"),
        "human" => T("У продавца", "Сатуучуда", "With the seller", "Satıcıda", "Sotuvchida"),
        "callback" => T("Перезвонить", "Чалуу керек", "Call back", "Geri aranacak", "Qayta qo'ng'iroq qilish"),
        _ => status,
    };

    public static string ReasonText(string reason) => reason switch
    {
        "human" => T("попросил продавца", "сатуучуну суранды", "asked for a seller", "satıcı istedi", "sotuvchini so'radi"),
        "unknown" => T("бот не знал ответа", "бот жоопту билген жок", "the bot did not know", "bot cevabı bilmiyordu", "bot javobni bilmadi"),
        "angry" => T("недоволен", "нааразы", "unhappy", "memnun değil", "norozi"),
        "big_order" => T("крупный заказ", "чоң заказ", "large order", "büyük sipariş", "katta buyurtma"),
        _ => reason,
    };

    public static string Seconds(int? seconds)
    {
        if (seconds is not { } s)
            return "—";
        if (s < 60)
            return s.ToString(CultureInfo.InvariantCulture) + " " + T("с", "с", "s", "sn", "s");
        var minutes = s / 60;
        return minutes.ToString(CultureInfo.InvariantCulture) + " " + T("мин", "мин", "min", "dk", "daq") + (s % 60 > 0 ? " " + (s % 60).ToString(CultureInfo.InvariantCulture) + " " + T("с", "с", "s", "sn", "s") : "");
    }

    /// <summary>Черновик заказа из «Проверить бота» читаемо: имя, телефон, позиции (размер, цвет, количество), комментарий. Пустые поля пропускаются.</summary>
    public static string DraftText(BotOrderDraft d)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(d.Name))
            lines.Add(T("Имя: ", "Аты: ", "Name: ", "Ad: ", "Ism: ") + d.Name);
        if (!string.IsNullOrWhiteSpace(d.Phone))
            lines.Add(T("Телефон: ", "Телефон: ", "Phone: ", "Telefon: ", "Telefon: ") + d.Phone);
        foreach (var i in d.Items)
        {
            var title = string.IsNullOrWhiteSpace(i.Title) ? T("товар", "товар", "item", "ürün", "mahsulot") : i.Title;
            var details = string.Join(", ", new[]
            {
                string.IsNullOrWhiteSpace(i.Size) ? "" : T("размер ", "өлчөмү ", "size ", "beden ", "o'lcham ") + i.Size,
                string.IsNullOrWhiteSpace(i.Color) ? "" : T("цвет ", "түсү ", "color ", "renk ", "rang ") + i.Color,
            }.Where(x => x.Length > 0));
            lines.Add("• " + title + (details.Length > 0 ? " (" + details + ")" : "") + (string.IsNullOrWhiteSpace(i.Qty) ? "" : " × " + i.Qty));
        }
        if (!string.IsNullOrWhiteSpace(d.Comment))
            lines.Add(T("Комментарий: ", "Комментарий: ", "Comment: ", "Yorum: ", "Izoh: ") + d.Comment);
        return string.Join("\n", lines);
    }

    /// <summary>2026-11-26 (стресс-тест): очень длинный текст покупателя (4000 знаков, без пробелов) в строке списка — первые <paramref name="max"/> знаков с «…»; полный текст — в «Переписке».
    /// Суррогатная пара эмодзи не разрезается.</summary>
    public static string Clip(string? text, int max = 300)
    {
        var s = (text ?? "").Trim();
        if (s.Length <= max)
            return s;
        var cut = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
        return s[..cut].TrimEnd() + "…";
    }

    public static string When(DateTimeOffset? at) => at is { } a ? a.LocalDateTime.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture) : "";

    /// <summary>Имя покупателя в Telegram: «Имя (@логин)»; без имени — «Покупатель».</summary>
    public static string Person(BotDialog d)
    {
        var name = string.IsNullOrWhiteSpace(d.Name) ? T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor") : d.Name;
        return string.IsNullOrWhiteSpace(d.Username) ? name : $"{name} (@{d.Username.TrimStart('@')})";
    }

    /// <summary>Текст ошибки сервера для строки состояния: 403 — права, 404 — сервер не умеет, остальное — как у остальных окон бота.</summary>
    public static string Failure(Exception ex) => ex is ApiException { StatusCode: 403 }
        ? T("Это доступно только владельцу магазина.", "Бул дүкөндүн ээсине гана жеткиликтүү.", "This is available to the shop owner only.", "Bu yalnızca mağaza sahibine açıktır.", "Bu faqat do'kon egasiga ochiq.")
        : ex is ApiException { StatusCode: 404 }
            ? T("Сервер пока не поддерживает это.", "Сервер муну азырынча колдобойт.", "The server does not support this yet.", "Sunucu bunu henüz desteklemiyor.", "Server buni hali qo'llamaydi.")
            : ServerTelegramBotApi.DescribeFields(ex);
}
