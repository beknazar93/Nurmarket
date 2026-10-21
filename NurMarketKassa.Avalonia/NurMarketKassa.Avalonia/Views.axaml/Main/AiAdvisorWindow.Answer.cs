using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-21, владелец: «доведи Нур Советника: дизайн 10, удобство 10, бизнес-логика 10». Ответ советника:
/// этапы на экране (О3), плитки цифр, которые считает программа (И5), «откуда цифра» с кнопкой «Открыть раздел» (идея 2),
/// кнопки по смыслу — «В Excel», «Заказать у поставщика», «Сделать акцию», «Напомнить должникам» (И4), понятные ошибки на 5 языках
/// с «Повторить» (О8), права пользователя (О4), проверка перед «Выполнить» с вторым подтверждением (И10).</summary>
public sealed partial class AiAdvisorWindow
{
    // ── Права (О4) ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Права текущего пользователя — те же флаги, что разделы программы: «Склад», «Клиенты», «Настройки» (зарплата и бот).</summary>
    private static AiAccess CurrentAccess()
    {
        var permissions = App.AppHost?.Services.GetService<IPermissionService>();
        bool Has(string p)
        {
            try
            {
                return permissions?.HasPermission(p) ?? true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        var settings = Has(PosPermissions.ViewSettings);
        return new AiAccess(Has(PosPermissions.ViewProducts), Has(PosPermissions.ViewClients), settings, settings);
    }

    private static string NoAccessText(string what, string siteRight) =>
        T($"У вас нет доступа к {what}. Доступ даёт владелец на сайте NurCRM: Сотрудники → сотрудник → доступ «{siteRight}».",
            $"Сизде {what} уруксат жок. Уруксатты ээси NurCRM сайтында берет: Кызматкерлер → кызматкер → «{siteRight}» уруксаты.",
            $"You don't have access to {what}. The owner grants it on the NurCRM website: Employees → employee → “{siteRight}” access.",
            $"{what} erişiminiz yok. Erişimi işletme sahibi NurCRM sitesinde verir: Çalışanlar → çalışan → «{siteRight}» erişimi.",
            $"Sizda {what} ruxsat yo'q. Ruxsatni ega NurCRM saytida beradi: Xodimlar → xodim → «{siteRight}» ruxsati.");

    /// <summary>Вопрос о том, к чему у пользователя нет доступа, — ответ без ИИ (данные в ИИ даже не уходят).</summary>
    private static string? DeniedAnswer(string question, AiAccess access)
    {
        var q = question.ToLowerInvariant();
        if (!access.Staff && TelegramAiChat.LooksLikeStaffQuestion(question)
            && new[] { "зарплат", "зп", "оклад", "айлык", "табел", "сотрудник", "кызматкер", "персонал", "salary", "maaş", "maosh", "смен" }.Any(q.Contains))
            return NoAccessText(T("зарплатам, табелю и сотрудникам", "айлыктарга, табелге жана кызматкерлерге", "salaries, timesheets and staff", "maaşlara, puantaja ve çalışanlara",
                "maoshlar, tabel va xodimlarga"), "Настройки");
        if (!access.Clients && (new[] { "телефон", "номер", "контакт", "адрес", "whatsapp", "ватсап", "напомн" }.Any(q.Contains)
                                && new[] { "клиент", "должник", "покупател", "кардар", "карыздар", "client", "debtor" }.Any(q.Contains)
                                || new[] { "кто должен", "кто из клиентов", "ким карыз", "ким бизге", "список должник", "должников" }.Any(q.Contains)))
            return NoAccessText(T("клиентам и должникам", "кардарларга жана карыздарларга", "customers and debtors", "müşterilere ve borçlulara", "mijozlar va qarzdorlarga"), "Клиенты");
        return null;
    }

    /// <summary>2026-10-21 (О5): «выведи телефоны и адреса клиентов» — таблицей прямо из программы, в ИИ эти данные не уходят.</summary>
    private static async Task<string?> TryLocalClientsListAsync(string question, AiAccess access, CancellationToken ct)
    {
        var q = question.ToLowerInvariant();
        if (!access.Clients || !TelegramAiChat.LooksLikeListRequest(question)
            || !new[] { "телефон", "номер", "контакт", "адрес" }.Any(q.Contains)
            || !new[] { "клиент", "покупател", "кардар", "client" }.Any(q.Contains))
            return null;
        var api = App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>();
        var rows = new List<(string Name, string Phone, string Address)>();
        for (var page = 1; page <= 20; page++)
        {
            var (items, hasNext) = await api.GetClientsPageAsync(page, null, ct).ConfigureAwait(true);
            foreach (var c in items)
            {
                string S(string n) => c.ValueKind == System.Text.Json.JsonValueKind.Object && c.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                var name = S("full_name") is { Length: > 0 } fn ? fn : S("name");
                rows.Add((name.Length > 0 ? name : "—", S("phone"), S("address")));
            }
            if (!hasNext)
                break;
        }
        PosLogger.Log($"ИИ-советник: список клиентов ({rows.Count}) показан из программы, без ИИ.", "INFO");
        static string Cell(string v) => string.IsNullOrWhiteSpace(v) ? "—" : v.Replace("|", "/").Trim();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(T($"Клиенты магазина — {rows.Count} (из программы, в ИИ эти данные не отправлялись):",
            $"Дүкөндүн кардарлары — {rows.Count} (программадан, бул маалымат ИИге жөнөтүлгөн жок):",
            $"Shop customers — {rows.Count} (from the program, not sent to the AI):",
            $"Mağaza müşterileri — {rows.Count} (programdan, yapay zekâya gönderilmedi):",
            $"Do'kon mijozlari — {rows.Count} (dasturdan, SIga yuborilmadi):"));
        sb.AppendLine();
        sb.AppendLine("| № | " + T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz") + " | " + T("Телефон", "Телефон", "Phone", "Telefon", "Telefon") + " | "
                      + T("Адрес", "Дарек", "Address", "Adres", "Manzil") + " |");
        sb.AppendLine("|---:|---|---|---|");
        var i = 0;
        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Take(500))
            sb.AppendLine($"| {++i} | {Cell(r.Name)} | {Cell(r.Phone)} | {Cell(r.Address)} |");
        if (rows.Count > 500)
            sb.AppendLine(T($"(показаны 500 из {rows.Count} — весь список в разделе «Клиенты»)", $"(500 / {rows.Count} — толук тизме «Кардарлар» бөлүмүндө)",
                $"(500 of {rows.Count} shown — the full list is in “Customers”)", $"({rows.Count} kişiden 500'ü — tam liste «Müşteriler» bölümünde)", $"({rows.Count} dan 500 tasi — to'liq ro'yxat «Mijozlar» bo'limida)"));
        return sb.ToString().TrimEnd();
    }

    /// <summary>2026-10-21 (О1, О6): какие разделы данных нужны вопросу целиком (список склада, все должники, ABC, сроки).</summary>
    private static ISet<string> ForceSections(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        var list = TelegramAiChat.LooksLikeListRequest(question ?? "") || new[] { "все ", "всех", "всё", "баары", "бардык", "all " }.Any(q.Contains);
        var set = new HashSet<string>();
        bool Has(params string[] w) => w.Any(q.Contains);
        if (Has("abc", "авс", "группы a", "группа a", "группе a", "группы а", "группа а"))
            set.Add("abc");
        if (Has("заканч", "закончил", "заказ", "пополн", "түгөн", "kамы", "азай", "running out", "tüken", "tugay"))
            set.Add("low_stock");
        if (TelegramAiChat.LooksLikeExpiryQuestion(question ?? ""))
            set.Add("expiry");
        if (list && Has("долж", "долг", "карыз", "debt", "qarz", "borç"))
            set.Add("debtors");
        if (list && Has("склад", "товар", "остат", "кампа", "калдык", "stock", "ombor", "depo"))
            set.Add("warehouse");
        if (Has("теряю", "залеж", "не продают", "мёртв", "мертв", "акци", "скидк", "жоготуп", "арзандат"))
            set.Add("analysis");
        return set;
    }

    // ── Этапы ответа (О3) ───────────────────────────────────────────────────────────────────────────────────────

    private static string[] StageTexts => new[]
    {
        T("Собираю данные магазина", "Дүкөндүн маалыматын чогултуп жатам", "Collecting shop data", "Mağaza verileri toplanıyor", "Do'kon ma'lumotlari yig'ilmoqda"),
        T("Спрашиваю ИИ", "ИИден сурап жатам", "Asking the AI", "Yapay zekâya soruluyor", "SIdan so'ralmoqda"),
        T("Готовлю ответ", "Жоопту даярдап жатам", "Preparing the answer", "Yanıt hazırlanıyor", "Javob tayyorlanmoqda"),
    };

    /// <summary>Пузырь «Думаю…» с тремя этапами и секундами: ① Собираю данные → ② Спрашиваю ИИ → ③ Готовлю ответ.</summary>
    private (SelectableTextBlock Body, Action<int> SetStage) AddStageBubble()
    {
        var body = AddBubble(T("Думаю…", "Ойлонуп жатам…", "Thinking…", "Düşünüyorum…", "O'ylayapman…"), fromOwner: false);
        if (body.Parent is not StackPanel content)
            return (body, _ => { });
        body.IsVisible = false;
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Tag = "stages" };
        var pills = new List<(Border Pill, TextBlock Mark, TextBlock Label)>();
        for (var i = 0; i < StageTexts.Length; i++)
        {
            var mark = new TextBlock { Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = StageTexts[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var pill = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 5), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { mark, label } },
            };
            pills.Add((pill, mark, label));
            row.Children.Add(pill);
        }
        var seconds = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 6) };
        Use(seconds, TextBlock.ForegroundProperty, "BrushTextSoft");
        row.Children.Add(seconds);
        content.Children.Insert(0, row);
        var started = DateTime.UtcNow;
        var current = 0;
        void Paint()
        {
            for (var i = 0; i < pills.Count; i++)
            {
                var (pill, mark, label) = pills[i];
                var done = i < current;
                var active = i == current;
                mark.Text = done ? "✓" : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Use(pill, Border.BackgroundProperty, active ? "BrushPanelSoft" : "BrushPanel");
                Use(pill, Border.BorderBrushProperty, active ? "BrushAccentStrong" : "BrushBorder");
                Use(mark, TextBlock.ForegroundProperty, done ? "BrushSuccess" : active ? "BrushText" : "BrushTextSoft");
                Use(label, TextBlock.ForegroundProperty, active || done ? "BrushText" : "BrushTextSoft");
                label.FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal;
                Avalonia.Automation.AutomationProperties.SetName(pill, label.Text + (done ? " ✓" : active ? " …" : ""));
            }
        }
        Paint();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (body.Text != T("Думаю…", "Ойлонуп жатам…", "Thinking…", "Düşünüyorum…", "O'ylayapman…") || !content.Children.Contains(row))
            {
                timer.Stop();
                content.Children.Remove(row);
                return;
            }
            var s = (int)(DateTime.UtcNow - started).TotalSeconds;
            seconds.Text = s >= 1 ? T($"{s} с", $"{s} сек", $"{s} s", $"{s} sn", $"{s} s") : "";
        };
        timer.Start();
        return (body, stage =>
        {
            current = Math.Clamp(stage - 1, 0, pills.Count - 1);
            Paint();
        });
    }

    // ── Понятные ошибки (О8) ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ошибка ИИ или сети → понятный текст на 5 языках (технический текст — только в журнал).</summary>
    private static string FriendlyErrorText(string? error)
    {
        var e = (error ?? "").ToLowerInvariant();
        if (e.Contains("timeout") || e.Contains("вовремя") || e.Contains("cancel") || e.Contains("отмен"))
            return T("Ответ не пришёл за 30 секунд — ИИ сейчас медленный. Повторить?", "Жооп 30 секундда келген жок — ИИ азыр жай. Кайталайлыбы?",
                "No answer within 30 seconds — the AI is slow right now. Try again?", "30 saniyede yanıt gelmedi — yapay zekâ şu an yavaş. Tekrar denensin mi?",
                "30 soniyada javob kelmadi — SI hozir sekin. Qayta urinilsinmi?");
        if (e.Contains("лимит") || e.Contains("429"))
            return T("Лимит запросов к ИИ на сейчас исчерпан — повторите через минуту.", "ИИге суроолордун лимити азырынча бүттү — бир мүнөттөн кийин кайталаңыз.",
                "The AI request limit is used up for now — try again in a minute.", "Yapay zekâ istek sınırı şimdilik doldu — bir dakika sonra tekrar deneyin.",
                "SIga so'rovlar limiti hozircha tugadi — bir daqiqadan keyin qayta urining.");
        if (e.Contains("ключ") || e.Contains("api key"))
            return T("Ключ ИИ не подходит — проверьте его в ⚙ «Модели ИИ».", "ИИ ачкычы туура эмес — аны ⚙ «ИИ моделдери» ичинен текшериңиз.",
                "The AI key doesn't work — check it in ⚙ “AI models”.", "Yapay zekâ anahtarı çalışmıyor — ⚙ «Yapay zekâ modelleri»nden kontrol edin.",
                "SI kaliti ishlamayapti — uni ⚙ «SI modellari»da tekshiring.");
        if (e.Contains("связ") || e.Contains("network") || e.Contains("http") || e.Contains("ssl"))
            return T("Нет связи с ИИ — проверьте интернет и повторите.", "ИИ менен байланыш жок — интернетти текшерип, кайталаңыз.",
                "No connection to the AI — check the internet and try again.", "Yapay zekâya bağlantı yok — interneti kontrol edip tekrar deneyin.",
                "SI bilan aloqa yo'q — internetni tekshirib, qayta urining.");
        if (e.Contains("перегруж") || e.Contains("503") || e.Contains("модел"))
            return T("ИИ сейчас перегружен — повторите через минуту.", "ИИ азыр ашыкча жүктөлгөн — бир мүнөттөн кийин кайталаңыз.",
                "The AI is overloaded right now — try again in a minute.", "Yapay zekâ şu an aşırı yüklü — bir dakika sonra tekrar deneyin.",
                "SI hozir band — bir daqiqadan keyin qayta urining.");
        return T("ИИ сейчас недоступен — повторите вопрос чуть позже.", "ИИ азыр жеткиликсиз — суроону бир аздан кийин кайталаңыз.",
            "The AI is unavailable right now — ask again a bit later.", "Yapay zekâ şu an kullanılamıyor — biraz sonra tekrar sorun.",
            "SI hozir mavjud emas — savolni birozdan keyin qayta bering.");
    }

    /// <summary>Пузырь ошибки: понятный текст и кнопка «Повторить» (тот же вопрос).</summary>
    private void ShowFriendlyError(SelectableTextBlock body, string? error, string question)
    {
        PosLogger.Log($"ИИ-советник: не ответил ({error ?? "нет ответа"}).", "WARNING");
        body.Text = FriendlyErrorText(error);
        if (body.Parent is not StackPanel content)
            return;
        var retry = MorningReportCard.SmallButton("↻ " + T("Повторить", "Кайталоо", "Try again", "Tekrar dene", "Qayta urinish"),
            T("Повторить вопрос", "Суроону кайталоо", "Ask again", "Soruyu tekrarla", "Savolni qaytarish"));
        retry.HorizontalAlignment = HorizontalAlignment.Left;
        retry.Click += async (_, _) =>
        {
            retry.IsEnabled = false;
            await SendAsync(question).ConfigureAwait(true);
        };
        content.Children.Insert(content.Children.IndexOf(body) + 1, retry);
    }

    // ── Таблицы, списки, итоги ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Строка итога таблицы на любом из 5 языков.</summary>
    private static readonly string[] TotalWords = { "Итого", "Итог", "Всего", "Жыйынтык", "Бардыгы", "Жалпы", "Total", "Toplam", "Jami", "Итого:" };

    private static bool HasTable(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n').Any(l => TableSeparator.IsMatch(l));

    /// <summary>2026-10-21 (О6): товары из действий ИИ — таблицей (вместо карточки действий на просьбу «выведи список»).</summary>
    private static string ListFromSteps(IReadOnlyList<ProductActionPlan.Step> steps)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("| № | " + T("Товар", "Товар", "Product", "Ürün", "Mahsulot") + " | " + T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq") + " | "
                      + T("Цена", "Баасы", "Price", "Fiyat", "Narx") + " | " + T("Предложено", "Сунушталды", "Suggested", "Önerilen", "Taklif") + " |");
        sb.AppendLine("|---:|---|---:|---:|---|");
        var i = 0;
        foreach (var s in steps.DistinctBy(s => s.Product.Id + s.Op))
            sb.AppendLine($"| {++i} | {s.Product.Title.Replace("|", "/")} | {s.Product.Quantity:0.###} | {s.Product.PriceValue:0.##} | {ProductActionPlan.Describe(s).Replace("|", "/")} |");
        sb.Append($"| {T("Итого", "Жыйынтык", "Total", "Toplam", "Jami")} | {i} | | | |");
        return sb.ToString();
    }

    // ── Под ответом: плитки цифр, «откуда цифра», кнопки по смыслу ──────────────────────────────────────────────

    private enum Topic { None, Revenue, Debts, Warehouse, LowStock, Promo, Abc, Expiry, Staff, Receipts }

    private static HashSet<Topic> Topics(string question)
    {
        var q = (question ?? "").ToLowerInvariant();
        var set = new HashSet<Topic>();
        bool Has(params string[] w) => w.Any(q.Contains);
        if (Has("выручк", "прибыл", "продаж", "продал", "оборот", "недел", "вчера", "сегодн", "месяц", "вчерашн", "сравни", "сатуу", "киреше", "пайда", "кечээ", "жума", "бүгүн",
                "revenue", "profit", "sales", "week", "yesterday", "ciro", "satış", "kâr", "savdo", "tushum", "foyda"))
            set.Add(Topic.Revenue);
        if (Has("долж", "долг", "карыз", "должен", "debt", "borç", "qarz"))
            set.Add(Topic.Debts);
        if (Has("склад", "остат", "товаров на", "кампа", "калдык", "закуп", "stock", "warehouse", "ombor", "depo"))
            set.Add(Topic.Warehouse);
        if (Has("заказ", "заканч", "закончил", "пополн", "түгөн", "order", "running out", "sipariş", "buyurtma"))
            set.Add(Topic.LowStock);
        if (Has("акци", "скидк", "теряю", "залеж", "не продают", "арзандат", "жоготуп", "promo", "discount", "kampanya", "aksiya", "chegirma"))
            set.Add(Topic.Promo);
        if (Has("abc", "авс"))
            set.Add(Topic.Abc);
        if (TelegramAiChat.LooksLikeExpiryQuestion(question ?? ""))
            set.Add(Topic.Expiry);
        if (TelegramAiChat.LooksLikeStaffQuestion(question ?? "") && Has("зарплат", "айлык", "табел", "сотрудник", "кызматкер", "salary", "maaş", "maosh"))
            set.Add(Topic.Staff);
        if (Has("чек", "receipt", "fiş", "chek"))
            set.Add(Topic.Receipts);
        // 2026-10-21 (дизайн, круг 3, живая проверка): «зарплата с начала месяца» — не вопрос о выручке (плитки выручки были лишними).
        if (set.Contains(Topic.Staff))
            set.Remove(Topic.Revenue);
        return set;
    }

    private static readonly Dictionary<Topic, string[]> TopicSections = new()
    {
        [Topic.Revenue] = new[] { "period_question", "periods", "overview" },
        [Topic.Debts] = new[] { "debts", "debtors" },
        [Topic.Warehouse] = new[] { "warehouse_totals", "question_products", "warehouse" },
        [Topic.LowStock] = new[] { "low_stock" },
        [Topic.Promo] = new[] { "analysis" },
        [Topic.Abc] = new[] { "abc" },
        [Topic.Expiry] = new[] { "expiry" },
        [Topic.Staff] = new[] { "staff" },
        [Topic.Receipts] = new[] { "receipts" },
    };

    /// <summary>Под ответом: плитки цифр программы, «Откуда цифры» с «Открыть раздел», кнопки «В Excel», «Заказать у поставщика»,
    /// «Сделать акцию», «Напомнить должникам». localOnly — ответ собран программой без ИИ.</summary>
    private void AddAnswerExtras(SelectableTextBlock body, string question, string answerText, IReadOnlyList<AiSection> included, bool localOnly, bool fallback = false)
    {
        if (body.Parent is not StackPanel content)
            return;
        var topics = Topics(question);
        var access = CurrentAccess();
        var at = content.Children.IndexOf(body) + 1;
        // Таблицы ответа рисуются сразу за body (RenderTables) — плитки ставим ПЕРЕД текстом ответа, остальное — после таблиц.
        if (!localOnly && BuildAnswerTiles(topics, access, question) is { } tiles)
            content.Children.Insert(Math.Max(0, content.Children.IndexOf(body)), tiles);
        var actionsRow = content.Children.OfType<StackPanel>().LastOrDefault(p => p.Orientation == Orientation.Horizontal);
        var insertAt = actionsRow is not null ? content.Children.IndexOf(actionsRow) : content.Children.Count;

        // «Откуда цифры» — разделы данных по теме вопроса, которые реально ушли в ИИ.
        var keys = topics.SelectMany(t => TopicSections.TryGetValue(t, out var k) ? k : Array.Empty<string>()).ToHashSet();
        // Один раздел — одна подпись (самая подробная: «Сводка (сервер NurCRM), периоды на 01:40», а не два раза «Сводка»).
        var sources = included.Where(s => keys.Contains(s.Key) && s.SourceLabel is not null)
            .GroupBy(s => s.SourceSection ?? s.SourceLabel).Select(g => g.OrderByDescending(s => s.SourceLabel!.Length).First()).Take(3).ToList();
        if (sources.Count > 0 || fallback || localOnly)
        {
            var src = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            var label = new TextBlock
            {
                Text = localOnly
                    ? T("Откуда: данные программы, без ИИ", "Кайдан: программанын маалыматы, ИИсиз", "Source: program data, no AI", "Kaynak: program verileri, yapay zekâ yok", "Manba: dastur ma'lumotlari, SIsiz")
                    : T("Откуда цифры: ", "Сандар кайдан: ", "Where the numbers come from: ", "Rakamların kaynağı: ", "Raqamlar manbai: ")
                      + string.Join(" · ", sources.Select(s => s.SourceLabel)) + (fallback ? T(" · ответил запасной ИИ", " · запастагы ИИ жооп берди", " · answered by the backup AI", " · yedek yapay zekâ yanıtladı", " · zaxira SI javob berdi") : ""),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 4), MaxWidth = 560,
            };
            Use(label, TextBlock.ForegroundProperty, "BrushTextSoft");
            src.Children.Add(label);
            foreach (var s in sources.Where(s => s.SourceSection is not null).DistinctBy(s => s.SourceSection).Take(2))
            {
                var key = s.SourceSection!;
                var open = MorningReportCard.SmallButton(T("Открыть «", "«", "Open “", "«", "«") + SectionTitle(key) + T("» ›", "» ачуу ›", "” ›", "» aç ›", "» ochish ›"),
                    T("Открыть раздел ", "Бөлүмдү ачуу: ", "Open section ", "Bölümü aç: ", "Bo'limni ochish: ") + SectionTitle(key));
                open.MinHeight = 28;
                open.FontSize = 12;
                open.Margin = new Thickness(0, 0, 6, 4);
                open.Click += (_, _) => OpenSectionSafe(key);
                src.Children.Add(open);
            }
            content.Children.Insert(insertAt++, src);
        }

        // Кнопки по смыслу (И4).
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        void Add(string text, string name, Func<Task> action)
        {
            var b = MorningReportCard.SmallButton(text, name);
            b.Margin = new Thickness(0, 0, 8, 6);
            b.Click += async (_, _) =>
            {
                try
                {
                    await action().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"ИИ-советник: кнопка «{name}» не сработала ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                    AddBubble(T("Не получилось: ", "Болбой калды: ", "Failed: ", "Olmadı: ", "Bo'lmadi: ") + FriendlyErrorText(ex.Message), fromOwner: false);
                }
            };
            buttons.Children.Add(b);
        }
        if (HasTable(answerText))
            Add("⬇ " + T("В Excel", "Excel'ге", "To Excel", "Excel'e", "Excel'ga"), T("Сохранить таблицу в Excel", "Таблицаны Excel'ге сактоо", "Save the table to Excel", "Tabloyu Excel'e kaydet", "Jadvalni Excel'ga saqlash"),
                () => ExportTablesAsync(question, answerText));
        if (access.Products && topics.Contains(Topic.LowStock))
            Add("🧾 " + T("Заказать у поставщика", "Жеткирүүчүдөн заказ кылуу", "Order from supplier", "Tedarikçiden sipariş", "Yetkazib beruvchidan buyurtma"),
                T("Подготовить черновик заказа поставщику", "Жеткирүүчүгө заказдын долбоорун даярдоо", "Prepare a supplier order draft", "Tedarikçi sipariş taslağı hazırla", "Yetkazib beruvchiga buyurtma qoralamasini tayyorlash"),
                AddSupplierOrderDraftAsync);
        if (access.Products && topics.Contains(Topic.Promo))
            Add("🏷 " + T("Сделать акцию", "Акция жасоо", "Make a promotion", "Kampanya yap", "Aksiya qilish"),
                T("Подготовить черновик акции", "Акциянын долбоорун даярдоо", "Prepare a promotion draft", "Kampanya taslağı hazırla", "Aksiya qoralamasini tayyorlash"),
                AddPromoDraftAsync);
        if (access.Clients && topics.Contains(Topic.Debts))
            Add("✉ " + T("Напомнить должникам", "Карыздарга эскертүү", "Remind debtors", "Borçlulara hatırlat", "Qarzdorlarga eslatish"),
                T("Напоминания должникам по расписанию", "Карыздарга эскертүүлөр", "Debt reminders", "Borç hatırlatmaları", "Qarz eslatmalari"),
                () => { OpenDebtReminders(); return Task.CompletedTask; });
        // «Копировать» / «Озвучить» — в той же строке, после кнопок по смыслу (одна строка действий под ответом).
        if (actionsRow is not null)
        {
            foreach (var child in actionsRow.Children.ToList())
            {
                actionsRow.Children.Remove(child);
                if (child is Control c)
                {
                    c.Margin = new Thickness(0, 0, 4, 6);
                    c.VerticalAlignment = VerticalAlignment.Center;
                    buttons.Children.Add(c);
                }
            }
        }
        if (buttons.Children.Count > 0)
            content.Children.Insert(insertAt, buttons);
    }

    /// <summary>2026-10-21 (дизайн, круг 2): пузырь вопроса владельца — мягкий акцент в светлой теме; в тёмной BrushAccentSoft
    /// оливковый («грязный» на снимках) — там нейтральная панель. Тема меняется на ходу — пузырь перекрашивается.</summary>
    private void UseOwnerBubble(Border bubble)
    {
        void Apply() => Use(bubble, Border.BackgroundProperty,
            (bubble.ActualThemeVariant ?? ActualThemeVariant) == Avalonia.Styling.ThemeVariant.Dark ? "BrushPanelSoft" : "BrushAccentSoft");
        Apply();
        bubble.ActualThemeVariantChanged += (_, _) => Apply();
    }

    /// <summary>2026-10-21 (И9): на пустом экране — «Пора напомнить N должникам» с кнопкой (ничего не отправляется само).</summary>
    private Control? DebtReminderBanner()
    {
        if (!CurrentAccess().Clients)
            return null;
        string? due;
        try
        {
            due = DebtReminderPlanner.DueText();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: напоминания должникам не проверены ({ex.Message}).", "WARNING");
            return null;
        }
        if (string.IsNullOrWhiteSpace(due))
            return null;
        var text = new TextBlock { Text = "✉ " + due, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Use(text, TextBlock.ForegroundProperty, "BrushText");
        var open = MorningReportCard.SmallButton(T("Проверить список", "Тизмени текшерүү", "Review the list", "Listeyi kontrol et", "Ro'yxatni tekshirish"),
            T("Открыть напоминания должникам", "Карыздарга эскертүүлөрдү ачуу", "Open debt reminders", "Borç hatırlatmalarını aç", "Qarz eslatmalarini ochish"));
        open.Click += (_, _) => OpenDebtReminders();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { text } };
        Grid.SetColumn(open, 1);
        open.Margin = new Thickness(10, 0, 0, 0);
        grid.Children.Add(open);
        var box = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), BorderThickness = new Thickness(1), Child = grid, Margin = new Thickness(0, 6, 0, 0) };
        Use(box, Border.BackgroundProperty, "BrushPanel");
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        return box;
    }

    private static string SectionTitle(string key) => key switch
    {
        "overview" => T("Сводка", "Жыйынтык", "Overview", "Özet", "Umumiy ko'rinish"),
        "debts" => T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari"),
        "warehouse" => T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"),
        "restock" => T("Пополнение и сроки", "Толуктоо жана мөөнөттөр", "Restock & expiry", "Stok yenileme ve SKT", "To'ldirish va muddatlar"),
        "abc" => T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC tahlili"),
        "analytics" => T("Аналитика", "Аналитика", "Analytics", "Analitik", "Tahlil"),
        "salary" => T("Зарплата", "Айлык", "Salary", "Maaş", "Maosh"),
        "sales" => T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"),
        "telegrambot" => T("Телеграм-бот", "Телеграм-бот", "Telegram bot", "Telegram botu", "Telegram bot"),
        _ => key,
    };

    private void OpenSectionSafe(string key)
    {
        try
        {
            if (key == "overview" && ProductActionPlan.NavigateByWords?.Invoke("открой сводку", true) is not null)
                return;
            ProductActionPlan.OpenSection?.Invoke(key);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: раздел «{key}» не открыт ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Плитки цифр под вопрос — посчитаны программой (OwnerPeriodStats, OwnerShopFacts), не ИИ.</summary>
    private static Control? BuildAnswerTiles(HashSet<Topic> topics, AiAccess access, string question)
    {
        var tiles = new List<OwnerMorningReport.Tile>();
        var stats = OwnerPeriodStats.Cached;
        string Money(double v) => OwnerPeriodStats.Money(v);
        if (topics.Contains(Topic.Revenue) && stats is not null)
        {
            if (stats.Get("yesterday") is { } y)
            {
                var deltas = new List<OwnerMorningReport.Delta>();
                if (stats.Compare("yesterday_vs_day_before") is { } c1)
                    deltas.Add(new OwnerMorningReport.Delta(c1.RevenueDeltaPercent, T("к позавчера", "мурдагы күнгө карата", "vs the day before", "önceki güne göre", "oldingi kunga nisbatan")));
                if (stats.Compare("yesterday_vs_last_week") is { } c2)
                    deltas.Add(new OwnerMorningReport.Delta(c2.RevenueDeltaPercent, T("к тому же дню прошлой недели", "өткөн жуманын ошол күнүнө карата", "vs same day last week", "geçen haftanın aynı gününe göre", "o'tgan haftaning shu kuniga nisbatan")));
                tiles.Add(new OwnerMorningReport.Tile("y", T($"Выручка вчера, {y.From:dd.MM}", $"Кечээки киреше, {y.From:dd.MM}", $"Revenue yesterday, {y.From:dd.MM}", $"Dünkü ciro, {y.From:dd.MM}", $"Kechagi tushum, {y.From:dd.MM}"),
                    Money(y.Revenue), deltas, T($"чеков {y.Checks:0}, прибыль {Money(y.Profit)}", $"чек {y.Checks:0}, пайда {Money(y.Profit)}", $"{y.Checks:0} receipts, profit {Money(y.Profit)}",
                        $"{y.Checks:0} fiş, kâr {Money(y.Profit)}", $"{y.Checks:0} chek, foyda {Money(y.Profit)}"), false, "overview"));
            }
            if (stats.Compare("week_vs_prev") is { } w)
                tiles.Add(new OwnerMorningReport.Tile("w", T($"7 дней, {w.Current.From:dd.MM}–{w.Current.To:dd.MM}", $"7 күн, {w.Current.From:dd.MM}–{w.Current.To:dd.MM}", $"7 days, {w.Current.From:dd.MM}–{w.Current.To:dd.MM}",
                        $"7 gün, {w.Current.From:dd.MM}–{w.Current.To:dd.MM}", $"7 kun, {w.Current.From:dd.MM}–{w.Current.To:dd.MM}"), Money(w.Current.Revenue),
                    new[] { new OwnerMorningReport.Delta(w.RevenueDeltaPercent, T("к прошлым 7 дням", "мурунку 7 күнгө карата", "vs previous 7 days", "önceki 7 güne göre", "oldingi 7 kunga nisbatan")) },
                    T($"прибыль {Money(w.Current.Profit)}", $"пайда {Money(w.Current.Profit)}", $"profit {Money(w.Current.Profit)}", $"kâr {Money(w.Current.Profit)}", $"foyda {Money(w.Current.Profit)}"), false, "overview"));
            if (stats.Compare("month_vs_prev") is { } m)
                tiles.Add(new OwnerMorningReport.Tile("m", T("Месяц", "Ай", "Month", "Ay", "Oy") + $" {m.Current.From:dd.MM}–{m.Current.To:dd.MM}", Money(m.Current.Revenue),
                    new[] { new OwnerMorningReport.Delta(m.RevenueDeltaPercent, T("к тем же дням прошлого месяца", "өткөн айдын ошол күндөрүнө карата", "vs same days last month", "geçen ayın aynı günlerine göre", "o'tgan oyning shu kunlariga nisbatan")) },
                    null, false, "overview"));
        }
        if (topics.Contains(Topic.Debts) && OwnerShopFacts.CachedExact() is { } exact)
        {
            var total = exact.Sum(d => d.Total);
            var overdue = exact.SelectMany(d => d.Sales).Where(s => (DateTime.Today - s.At.Date).TotalDays > 30).Sum(s => s.Left);
            tiles.Add(new OwnerMorningReport.Tile("d", T("Вам должны", "Сизге карыз", "You are owed", "Size borçlu", "Sizga qarz"), Money(total), Array.Empty<OwnerMorningReport.Delta>(),
                T($"клиентов {exact.Count(d => d.Total > 0.005)}, чеков {exact.Sum(d => d.Sales.Count)}", $"кардар {exact.Count(d => d.Total > 0.005)}, чек {exact.Sum(d => d.Sales.Count)}",
                    $"{exact.Count(d => d.Total > 0.005)} customers, {exact.Sum(d => d.Sales.Count)} receipts", $"{exact.Count(d => d.Total > 0.005)} müşteri, {exact.Sum(d => d.Sales.Count)} fiş",
                    $"{exact.Count(d => d.Total > 0.005)} mijoz, {exact.Sum(d => d.Sales.Count)} chek"), false, "debts"));
            tiles.Add(new OwnerMorningReport.Tile("o", T("Просрочка > 30 дней", "30 күндөн ашык", "Overdue > 30 days", "30 günden fazla gecikme", "30 kundan ortiq"), Money(overdue),
                Array.Empty<OwnerMorningReport.Delta>(), null, overdue > 0.005, "debts"));
        }
        // Вопрос про конкретный товар («остаток Макфы») — плитки всего склада не нужны.
        if (topics.Contains(Topic.Warehouse) && access.Products && ProductActionPlan.Mentioned(question, 1).Count == 0)
        {
            var all = OwnerShopFacts.Tiles().Where(t => !t.IsService).ToList();
            if (all.Count > 0)
            {
                var inStock = all.Where(t => t.Quantity > 0).ToList();
                tiles.Add(new OwnerMorningReport.Tile("s1", T("Товаров на складе", "Кампадагы товарлар", "Products in stock", "Depodaki ürünler", "Ombordagi mahsulotlar"),
                    $"{inStock.Count} / {all.Count}", Array.Empty<OwnerMorningReport.Delta>(),
                    T($"нет в наличии {all.Count - inStock.Count}", $"жок {all.Count - inStock.Count}", $"{all.Count - inStock.Count} out of stock", $"{all.Count - inStock.Count} stokta yok", $"{all.Count - inStock.Count} ta yo'q"), false, "warehouse"));
                tiles.Add(new OwnerMorningReport.Tile("s2", T("Остаток по закупке", "Сатып алуу баасы боюнча калдык", "Stock at cost", "Alış fiyatıyla stok", "Xarid narxida qoldiq"),
                    Money(inStock.Sum(t => t.Quantity * t.PurchasePrice)), Array.Empty<OwnerMorningReport.Delta>(), null, false, "warehouse"));
            }
        }
        if (tiles.Count == 0)
            return null;
        var grid = new Views.Controls.KpiTileGrid { MinTileWidth = 190, Spacing = 8, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var t in tiles.Take(4))
            grid.Children.Add(MorningReportCard.BuildTile(t));
        return grid;
    }

    // ── Выручка за названный день или период (И5): считает программа, будущая дата — без ИИ ───────────────────────

    private static readonly System.Text.RegularExpressions.Regex ExplicitDate = new(
        @"\b\d{1,2}[./]\d{1,2}([./]\d{2,4})?\b|\b\d{1,2}\s+(январ|феврал|март|апрел|ма[йя]|июн|июл|август|сентябр|октябр|ноябр|декабр)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Вопрос о продажах за будущую дату («выручка 25 октября») — ответ без ИИ: данных ещё нет.</summary>
    private static string? FutureDateAnswer(string question)
    {
        if (!Topics(question).Contains(Topic.Revenue) && !OwnerSalesData.LooksLikeQuestion(question))
            return null;
        if (!ExplicitDate.IsMatch(question.ToLowerInvariant()))
            return null;
        var (from, _, label) = OwnerSalesData.Period(question);
        if (from.Date <= DateTime.Today)
            return null;
        return T($"{label} — это будущая дата: продаж за неё ещё нет. Могу показать сегодня, вчера, неделю или месяц.",
            $"{label} — бул келечектеги күн: ал үчүн сатуу али жок. Бүгүнкү, кечээки, жумалык же айлык маалыматты көрсөтө алам.",
            $"{label} is a future date — there are no sales for it yet. I can show today, yesterday, the week or the month.",
            $"{label} ileri bir tarih — henüz satış yok. Bugünü, dünü, haftayı veya ayı gösterebilirim.",
            $"{label} — bu kelajakdagi sana: unga hali sotuv yo'q. Bugun, kecha, hafta yoki oyni ko'rsata olaman.");
    }

    /// <summary>Выручка за день или период из вопроса («выручка 6.10», «12 октября») — тот же отчёт сервера, что «Сводка».</summary>
    private static async Task<AiSection?> PeriodRevenueSectionAsync(string question, CancellationToken ct)
    {
        if (!Topics(question).Contains(Topic.Revenue) || !ExplicitDate.IsMatch(question.ToLowerInvariant()))
            return null;
        var (from, toEx, label) = OwnerSalesData.Period(question);
        if (from.Date > DateTime.Today)
            return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var report = await App.SalesApi.MarketSalesReportAsync(from, toEx.AddDays(-1), cts.Token).ConfigureAwait(true);
            if (report.ValueKind != System.Text.Json.JsonValueKind.Object || !report.TryGetProperty("cards", out var cards))
                return null;
            double N(string n) => cards.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble()
                : cards.TryGetProperty(n, out var vs) && vs.ValueKind == System.Text.Json.JsonValueKind.String && double.TryParse(vs.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            var text = $"ВЫРУЧКА ЗА {label} (посчитала программа по отчёту сервера NurCRM, как «Сводка»): выручка {OwnerPeriodStats.Money(N("revenue"))}, чеков {N("transactions"):0}, "
                       + $"средний чек {OwnerPeriodStats.Money(N("avg_check"))}, валовая прибыль {OwnerPeriodStats.Money(N("gross_profit"))}.";
            return new AiSection("period_question", "Выручка за период вопроса", 1, text, SourceSection: "overview", SourceLabel: $"Сводка (сервер NurCRM), {label}");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            PosLogger.Log($"ИИ-советник: выручка за {label} не получена ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    // ── «В Excel» ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Таблицы ответа — в .xlsx (тот же построитель, что выгрузка продаж: ExcelWorkbookBuilder).</summary>
    private async Task ExportTablesAsync(string question, string answer)
    {
        var tables = new List<(string[] Header, List<string[]> Rows)>();
        var lines = answer.Replace("\r\n", "\n").Split('\n');
        static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        for (var i = 0; i + 1 < lines.Length; i++)
        {
            if (!TableLine.IsMatch(lines[i]) || !TableSeparator.IsMatch(lines[i + 1]))
                continue;
            var header = Cells(lines[i]);
            var rows = new List<string[]>();
            i += 2;
            while (i < lines.Length && TableLine.IsMatch(lines[i]))
                rows.Add(Cells(lines[i++]));
            tables.Add((header, rows));
        }
        if (tables.Count == 0)
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = T("Сохранить таблицу в Excel", "Таблицаны Excel'ге сактоо", "Save the table to Excel", "Tabloyu Excel'e kaydet", "Jadvalni Excel'ga saqlash"),
            SuggestedFileName = T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi") + $" {DateTime.Now:yyyy-MM-dd HH-mm}.xlsx",
            FileTypeChoices = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Excel") { Patterns = new[] { "*.xlsx" } } },
        }).ConfigureAwait(true);
        var path = file?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;
        var shop = UserPreferences.Instance.StoreName;
        await Task.Run(() =>
        {
            var meta = ReportMeta.Create(T("Нур Советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"), shop, question.Length > 90 ? question[..90] + "…" : question);
            using var book = new ExcelWorkbookBuilder(path!, meta);
            var n = 0;
            foreach (var (header, rows) in tables)
            {
                n++;
                var numeric = header.Select((_, c) => rows.Count > 0 && rows.All(r => c >= r.Length || r[c].Length == 0 || ParseNumber(r[c]) is not null)).ToArray();
                var columns = header.Select((h, c) => new XlColumn(h.Length > 0 ? h : "—", numeric[c] && c > 0 ? XlKind.Quantity : XlKind.Text)).ToList();
                var sheet = book.AddSheet(T("Таблица", "Таблица", "Table", "Tablo", "Jadval") + " " + n, T("Ответ Нур Советника", "Нур Кеңешчинин жообу", "Nur Advisor answer", "Nur Danışman yanıtı", "Nur Maslahatchi javobi"));
                sheet.AddTable(columns, rows.Select(r => header.Select((_, c) => c < r.Length
                    ? numeric[c] && c > 0 && ParseNumber(r[c]) is { } v ? (object?)v : r[c]
                    : null).ToArray()), totals: false);
            }
            book.Save();
        }).ConfigureAwait(true);
        PosLogger.Log($"ИИ-советник: таблица ответа сохранена в Excel ({tables.Count} шт.).", "INFO");
        AddBubble(T($"✓ Сохранено: {path}", $"✓ Сакталды: {path}", $"✓ Saved: {path}", $"✓ Kaydedildi: {path}", $"✓ Saqlandi: {path}"), fromOwner: false);
    }

    private static double? ParseNumber(string cell)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(cell, @"(сом|шт\.?|ч|кг|%|som|so'm)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace(",", ".").Replace("−", "-").Trim();
        return t.Length > 0 && double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    // ── И4: черновик заказа поставщику и черновик акции ─────────────────────────────────────────────────────────

    /// <summary>Черновик заказа: что закончилось и заканчивается (на 14 дней продаж), поставщик — из последней закупки товара,
    /// текст для WhatsApp/Telegram поставщику (копировать) или черновик закупки (окно закупки — без проведения).</summary>
    private async Task AddSupplierOrderDraftAsync()
    {
        var status = AddBubble(T("Готовлю черновик заказа…", "Заказдын долбоорун даярдап жатам…", "Preparing the order draft…", "Sipariş taslağı hazırlanıyor…", "Buyurtma qoralamasi tayyorlanmoqda…"), fromOwner: false);
        var rows = await OwnerShopFacts.StockRowsAsync(CancellationToken.None).ConfigureAwait(true);
        var need = OwnerShopFacts.SoldOut(rows, 20).Concat(OwnerShopFacts.RunningLow(rows, 20)).DistinctBy(r => r.Tile.Id).Where(r => r.OrderFor14 > 0).Take(25).ToList();
        if (need.Count == 0)
        {
            status.Text = T("Срочно заказывать нечего: закончившихся и заканчивающихся популярных товаров нет.", "Шашылыш заказ кылууга эч нерсе жок.",
                "Nothing urgent to order.", "Acil sipariş edilecek bir şey yok.", "Zudlik bilan buyurtma qiladigan narsa yo'q.");
            return;
        }
        // Поставщик — из истории закупок товара (не больше 4 запросов одновременно).
        var gate = new SemaphoreSlim(4);
        var suppliers = await Task.WhenAll(need.Select(async r =>
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var history = await PurchaseReceivingService.Instance.LoadHistoryAsync(r.Tile.Id).ConfigureAwait(false);
                return history.Select(h => h.SupplierName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
            }
            catch (Exception)
            {
                return "";
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(true);
        var none = T("Поставщик не указан", "Жеткирүүчү көрсөтүлгөн эмес", "Supplier not set", "Tedarikçi belirtilmemiş", "Yetkazib beruvchi ko'rsatilmagan");
        var groups = need.Select((r, i) => (Row: r, Supplier: suppliers[i].Length > 0 ? suppliers[i] : none)).GroupBy(x => x.Supplier).ToList();
        status.Text = T($"Черновик заказа — {need.Count} товаров, {groups.Count} поставщик(ов). Количество — на 14 дней продаж; ничего не отправлено и не проведено.",
            $"Заказдын долбоору — {need.Count} товар, {groups.Count} жеткирүүчү. Саны — 14 күндүк сатууга; эч нерсе жөнөтүлгөн жана өткөрүлгөн жок.",
            $"Order draft — {need.Count} products, {groups.Count} supplier(s). Quantities for 14 days of sales; nothing sent or posted.",
            $"Sipariş taslağı — {need.Count} ürün, {groups.Count} tedarikçi. Miktarlar 14 günlük satış için; hiçbir şey gönderilmedi.",
            $"Buyurtma qoralamasi — {need.Count} mahsulot, {groups.Count} yetkazib beruvchi. Miqdor 14 kunlik savdoga; hech narsa yuborilmadi.");
        foreach (var g in groups)
        {
            var text = T($"Здравствуйте! Заказ от магазина «{UserPreferences.Instance.StoreName}»:", $"Саламатсызбы! «{UserPreferences.Instance.StoreName}» дүкөнүнөн заказ:",
                $"Hello! Order from “{UserPreferences.Instance.StoreName}”:", $"Merhaba! «{UserPreferences.Instance.StoreName}» mağazasından sipariş:",
                $"Assalomu alaykum! «{UserPreferences.Instance.StoreName}» do'konidan buyurtma:") + "\n"
                + string.Join("\n", g.Select((x, i) => $"{i + 1}. {x.Row.Name} — {x.Row.OrderFor14:0} {(string.IsNullOrWhiteSpace(x.Row.Tile.Unit) ? "шт." : x.Row.Tile.Unit)}"))
                + "\n" + T("Спасибо!", "Рахмат!", "Thank you!", "Teşekkürler!", "Rahmat!");
            var titleRow = new TextBlock { Text = "🧾 " + g.Key, FontSize = 14.5, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
            Use(titleRow, TextBlock.ForegroundProperty, "BrushText");
            var body = new SelectableTextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
            Use(body, TextBlock.ForegroundProperty, "BrushText");
            var copy = MorningReportCard.SmallButton(T("Копировать текст", "Текстти көчүрүү", "Copy text", "Metni kopyala", "Matnni nusxalash"),
                T("Копировать текст заказа", "Заказдын текстин көчүрүү", "Copy the order text", "Sipariş metnini kopyala", "Buyurtma matnini nusxalash"));
            copy.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(text).ConfigureAwait(true);
                copy.Content = "✓ " + T("Скопировано", "Көчүрүлдү", "Copied", "Kopyalandı", "Nusxalandi");
            };
            var draftButton = MorningReportCard.SmallButton(T("Черновик закупки…", "Сатып алуунун долбоору…", "Purchase draft…", "Alım taslağı…", "Xarid qoralamasi…"),
                T("Открыть черновик закупки (без проведения)", "Сатып алуунун долбоорун ачуу", "Open a purchase draft (not posted)", "Alım taslağını aç (işlenmez)", "Xarid qoralamasini ochish"));
            var groupRows = g.ToList();
            var supplierName = g.Key == none ? null : g.Key;
            draftButton.Click += async (_, _) =>
            {
                var steps = groupRows.Select(x => new ProductActionPlan.Step("receive", x.Row.Tile, x.Row.OrderFor14, null, null,
                    x.Row.Cost > 0 ? x.Row.Cost : null, null, ExpectedStock: x.Row.Tile.Quantity)).ToList();
                var draft = InvoiceEditorWindow.PurchaseDraft.FromSteps(steps, supplierName, null);
                await new InvoiceEditorWindow(draft).ShowDialog<string?>(this).ConfigureAwait(true);
            };
            var card = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10), MaxWidth = 680, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new StackPanel { Spacing = 8, Children = { titleRow, body, new WrapPanel { Children = { copy, draftButton } } } },
            };
            copy.Margin = new Thickness(0, 0, 8, 0);
            Use(card, Border.BackgroundProperty, "BrushPanel");
            Use(card, Border.BorderBrushProperty, "BrushBorder");
            _messages.Children.Add(card);
        }
        PosLogger.Log($"ИИ-советник: черновик заказа поставщикам — {need.Count} товаров, {groups.Count} поставщиков (ничего не отправлено).", "INFO");
        SaveCurrentChat();
        ScrollToEnd();
    }

    /// <summary>Черновик акции: залежавшиеся (30 дней без продаж) и затоваренные товары, скидка 15–20 %, но цена не ниже закупки + 5 %;
    /// «Подготовить смену цен» — карточка действий с проверкой И10 и подтверждением.</summary>
    private async Task AddPromoDraftAsync()
    {
        var rows = await OwnerShopFacts.StockRowsAsync(CancellationToken.None).ConfigureAwait(true);
        var candidates = OwnerShopFacts.Dead(rows, 30).Concat(rows.Where(r => r.PerDay > 0 && r.Stock / r.PerDay > 60).OrderByDescending(r => r.Frozen))
            .DistinctBy(r => r.Tile.Id)
            .Where(r => r.Price > 0 && r.Cost > 0 && r.Markup is > 10)
            .Take(8).ToList();
        if (candidates.Count == 0)
        {
            AddBubble(T("Для акции подходящих товаров нет: залежавшихся с запасом наценки не нашлось.", "Акция үчүн ылайыктуу товар жок.",
                "No products fit a promotion: no slow movers with enough markup.", "Kampanyaya uygun ürün yok.", "Aksiya uchun mos mahsulot yo'q."), fromOwner: false);
            return;
        }
        var until = DateTime.Today.AddDays(14);
        var steps = candidates.Select(r =>
        {
            var discount = r.Sold30 <= 0 ? 0.20 : 0.15;
            var min = Math.Ceiling(r.Cost * 1.05);
            var price = Math.Max(min, Math.Floor(r.Price * (1 - discount)));
            return new ProductActionPlan.Step("set_price", r.Tile, price, null, T($"акция до {until:dd.MM}", $"акция {until:dd.MM} чейин", $"promo until {until:dd.MM}", $"{until:dd.MM} tarihine kadar kampanya", $"aksiya {until:dd.MM} gacha"));
        }).Where(s => s.Qty < s.Product.PriceValue - 0.5).ToList();
        AddBubble(T($"Черновик акции до {until:dd.MM}: скидка 15–20 % на залежавшиеся товары, цена не ниже закупки + 5 %. Проверьте и подтвердите — после акции верните цены "
                    + "(напишите «верни цены после акции»).",
            $"{until:dd.MM} чейинки акциянын долбоору: жатып калган товарларга 15–20 % арзандатуу, баа сатып алуу + 5 %дан төмөн эмес. Текшерип ырастаңыз.",
            $"Promotion draft until {until:dd.MM}: 15–20% off slow movers, never below cost + 5%. Check and confirm — restore prices after the promo.",
            $"{until:dd.MM} tarihine kadar kampanya taslağı: bekleyen ürünlerde %15–20 indirim, alış + %5'in altında değil. Kontrol edip onaylayın.",
            $"{until:dd.MM} gacha aksiya qoralamasi: turib qolgan mahsulotlarga 15–20 % chegirma, narx xarid + 5 % dan past emas. Tekshirib tasdiqlang."), fromOwner: false);
        AddProductActionsCard(steps, T("Сделать акцию", "Акция жасоо", "Make a promotion", "Kampanya yap", "Aksiya qilish"));
    }

    // ── И9: напоминания должникам ───────────────────────────────────────────────────────────────────────────────

    private void OpenDebtReminders()
    {
        if (!CurrentAccess().Clients)
        {
            AddBubble(NoAccessText(T("клиентам и должникам", "кардарларга жана карыздарларга", "customers and debtors", "müşterilere ve borçlulara", "mijozlar va qarzdorlarga"), "Клиенты"), fromOwner: false);
            return;
        }
        DebtReminderWindow.Open(this);
    }

    // ── И10: проверка перед «Выполнить» ─────────────────────────────────────────────────────────────────────────

    /// <summary>Предупреждения к действиям с товарами — с цифрами («цена 1 сом ниже закупки 160»).</summary>
    internal static List<string> ProductActionWarnings(IReadOnlyList<ProductActionPlan.Step> steps)
    {
        var list = new List<string>();
        string Q(double v) => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        var som = T("сом", "сом", "som", "som", "so'm");
        foreach (var s in steps)
        {
            var name = "«" + s.Product.Title + "»";
            var cost = s.Purchase ?? s.Product.PurchasePrice;
            double? price = s.Op switch { "set_price" => s.Qty, "receive" or "create" => s.Price, _ => null };
            if (s.Op == "set_purchase" && s.Qty is { } newCost && s.Product.PriceValue > 0 && newCost > s.Product.PriceValue)
                list.Add(T($"{name}: закупка {Q(newCost)} {som} выше цены продажи {Q(s.Product.PriceValue)} {som} — наценка станет минусовой",
                    $"{name}: сатып алуу {Q(newCost)} {som} сатуу баасынан {Q(s.Product.PriceValue)} {som} жогору — үстөк минус болот",
                    $"{name}: cost {Q(newCost)} {som} is above the sale price {Q(s.Product.PriceValue)} {som} — negative markup",
                    $"{name}: alış {Q(newCost)} {som}, satış fiyatı {Q(s.Product.PriceValue)} {som} üstünde — kâr payı eksi olur",
                    $"{name}: xarid {Q(newCost)} {som} sotuv narxi {Q(s.Product.PriceValue)} {som} dan yuqori — ustama manfiy bo'ladi"));
            if (price is { } p)
            {
                if (p <= 0)
                    list.Add(T($"{name}: цена {Q(p)} {som} — товар будет бесплатным", $"{name}: баа {Q(p)} {som} — товар бекер болот", $"{name}: price {Q(p)} {som} — the product becomes free",
                        $"{name}: fiyat {Q(p)} {som} — ürün bedava olur", $"{name}: narx {Q(p)} {som} — mahsulot bepul bo'ladi"));
                else if (cost > 0 && p < cost)
                    list.Add(T($"{name}: цена {Q(p)} {som} ниже закупки {Q(cost)} {som} — каждая продажа в убыток ({Q(p - cost)} {som})",
                        $"{name}: баа {Q(p)} {som} сатып алуудан {Q(cost)} {som} төмөн — ар бир сатуу зыян ({Q(p - cost)} {som})",
                        $"{name}: price {Q(p)} {som} is below cost {Q(cost)} {som} — every sale loses {Q(cost - p)} {som}",
                        $"{name}: fiyat {Q(p)} {som}, alış {Q(cost)} {som} altında — her satış zarar ({Q(p - cost)} {som})",
                        $"{name}: narx {Q(p)} {som} xarid {Q(cost)} {som} dan past — har bir sotuv zarar ({Q(p - cost)} {som})"));
                else if (cost > 0 && (p - cost) / cost * 100 > 500)
                    list.Add(T($"{name}: наценка {(p - cost) / cost * 100:0} % (цена {Q(p)} при закупке {Q(cost)}) — больше 500 %, нет ли лишнего нуля?",
                        $"{name}: үстөк {(p - cost) / cost * 100:0} % (баа {Q(p)}, сатып алуу {Q(cost)}) — 500 %дан ашык, ашыкча нөл жокпу?",
                        $"{name}: markup {(p - cost) / cost * 100:0}% (price {Q(p)}, cost {Q(cost)}) — over 500%, an extra zero?",
                        $"{name}: kâr payı %{(p - cost) / cost * 100:0} (fiyat {Q(p)}, alış {Q(cost)}) — %500'den fazla, fazladan sıfır mı?",
                        $"{name}: ustama {(p - cost) / cost * 100:0} % (narx {Q(p)}, xarid {Q(cost)}) — 500 % dan ortiq, ortiqcha nol yo'qmi?"));
            }
            if (s.Op == "stock_out" && s.Qty is { } q)
            {
                var stock = s.ExpectedStock ?? s.Product.Quantity;
                if (q > stock + 1e-9)
                    list.Add(T($"{name}: списание {Q(q)} больше остатка {Q(stock)} — остаток уйдёт в минус ({Q(stock - q)})",
                        $"{name}: чыгаруу {Q(q)} калдыктан {Q(stock)} көп — калдык минуска кетет ({Q(stock - q)})",
                        $"{name}: write-off {Q(q)} exceeds stock {Q(stock)} — stock goes negative ({Q(stock - q)})",
                        $"{name}: {Q(q)} düşüm stoktan {Q(stock)} fazla — stok eksiye düşer ({Q(stock - q)})",
                        $"{name}: hisobdan chiqarish {Q(q)} qoldiqdan {Q(stock)} ko'p — qoldiq manfiy bo'ladi ({Q(stock - q)})"));
                else if (stock > 0 && q > stock * 0.5)
                    list.Add(T($"{name}: списание {Q(q)} из {Q(stock)} — больше половины остатка",
                        $"{name}: {Q(stock)} ичинен {Q(q)} чыгаруу — калдыктын жарымынан көбү",
                        $"{name}: writing off {Q(q)} of {Q(stock)} — more than half the stock",
                        $"{name}: {Q(stock)} içinden {Q(q)} düşüm — stoğun yarısından fazlası",
                        $"{name}: {Q(stock)} dan {Q(q)} hisobdan chiqarish — qoldiqning yarmidan ko'pi"));
            }
            if (s.Op == "stock_set" && s.Qty is < 0)
                list.Add(T($"{name}: остаток {Q(s.Qty.Value)} — минусовой", $"{name}: калдык {Q(s.Qty.Value)} — минус", $"{name}: stock {Q(s.Qty.Value)} — negative",
                    $"{name}: stok {Q(s.Qty.Value)} — eksi", $"{name}: qoldiq {Q(s.Qty.Value)} — manfiy"));
        }
        var products = steps.Select(s => s.Product.Id).Distinct().Count();
        if (products > 20)
            list.Insert(0, T($"Массовое изменение: {products} товаров сразу — проверьте список целиком.", $"Массалык өзгөртүү: бир эле учурда {products} товар — тизмени толук текшериңиз.",
                $"Bulk change: {products} products at once — check the whole list.", $"Toplu değişiklik: aynı anda {products} ürün — listenin tamamını kontrol edin.",
                $"Ommaviy o'zgartirish: bir vaqtda {products} ta mahsulot — ro'yxatni to'liq tekshiring."));
        return list;
    }

    private Control WarningsPanel(IReadOnlyList<string> warnings)
    {
        var stack = new StackPanel { Spacing = 4 };
        var head = new TextBlock { Text = "⚠ " + T("Проверьте перед выполнением:", "Аткаруудан мурун текшериңиз:", "Check before applying:", "Uygulamadan önce kontrol edin:", "Bajarishdan oldin tekshiring:"),
            FontSize = 13.5, FontWeight = FontWeight.SemiBold };
        Use(head, TextBlock.ForegroundProperty, "BrushDanger");
        stack.Children.Add(head);
        foreach (var w in warnings.Take(12))
        {
            var line = new TextBlock { Text = "• " + w, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Use(line, TextBlock.ForegroundProperty, "BrushText");
            stack.Children.Add(line);
        }
        if (warnings.Count > 12)
        {
            var more = new TextBlock { Text = T($"… и ещё {warnings.Count - 12}", $"… дагы {warnings.Count - 12}", $"… and {warnings.Count - 12} more", $"… ve {warnings.Count - 12} tane daha", $"… va yana {warnings.Count - 12}"), FontSize = 12.5 };
            Use(more, TextBlock.ForegroundProperty, "BrushTextSoft");
            stack.Children.Add(more);
        }
        var box = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 2), Child = stack };
        Use(box, Border.BorderBrushProperty, "BrushDanger");
        Use(box, Border.BackgroundProperty, "BrushPanelSoft");
        Avalonia.Automation.AutomationProperties.SetName(box, head.Text + " " + string.Join("; ", warnings));
        return box;
    }

    /// <summary>Второе подтверждение прямо в карточке: «Да, всё верно — выполнить» / «Отмена».</summary>
    private void ShowSecondConfirm(Border card, IReadOnlyList<string> warnings, Action onYes, Control? hideWhileAsking = null)
    {
        if (card.Child is not StackPanel panel || panel.Children.OfType<Border>().Any(b => Equals(b.Tag, "confirm2")))
            return;
        // Пока спрашиваем второй раз — первые кнопки скрыты (одна главная кнопка на экране).
        if (hideWhileAsking is not null)
            hideWhileAsking.IsVisible = false;
        var text = new TextBlock
        {
            Text = T($"Вы уверены? Предупреждений: {warnings.Count}. Первое: {warnings[0]}", $"Ишенесизби? Эскертүүлөр: {warnings.Count}. Биринчиси: {warnings[0]}",
                $"Are you sure? Warnings: {warnings.Count}. First: {warnings[0]}", $"Emin misiniz? Uyarı: {warnings.Count}. İlki: {warnings[0]}",
                $"Ishonchingiz komilmi? Ogohlantirishlar: {warnings.Count}. Birinchisi: {warnings[0]}"),
            FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        Use(text, TextBlock.ForegroundProperty, "BrushText");
        var yes = new Button
        {
            Content = T("Да, всё верно — выполнить", "Ооба, баары туура — аткаруу", "Yes, it's correct — apply", "Evet, doğru — uygula", "Ha, hammasi to'g'ri — bajarish"),
            Height = 38, Padding = new Thickness(16, 0), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(0), FontWeight = FontWeight.SemiBold,
            VerticalContentAlignment = VerticalAlignment.Center, Foreground = Brushes.White,
        };
        Use(yes, Button.BackgroundProperty, "BrushDanger");
        Avalonia.Automation.AutomationProperties.SetName(yes, T("Да, выполнить несмотря на предупреждения", "Ооба, эскертүүлөргө карабай аткаруу", "Yes, apply despite the warnings",
            "Evet, uyarılara rağmen uygula", "Ha, ogohlantirishlarga qaramay bajarish"));
        var no = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        no.Height = 38;
        var box = new Border
        {
            Tag = "confirm2", CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1.5),
            Child = new StackPanel { Spacing = 8, Children = { text, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { yes, no } } } },
        };
        Use(box, Border.BorderBrushProperty, "BrushDanger");
        Use(box, Border.BackgroundProperty, "BrushPanel");
        yes.Click += (_, _) =>
        {
            panel.Children.Remove(box);
            PosLogger.Log($"ИИ-советник: второе подтверждение при предупреждениях ({warnings.Count}) — владелец подтвердил.", "INFO");
            onYes();
        };
        no.Click += (_, _) =>
        {
            panel.Children.Remove(box);
            if (hideWhileAsking is not null)
                hideWhileAsking.IsVisible = true;
        };
        panel.Children.Add(box);
        ScrollToEnd();
    }
}
