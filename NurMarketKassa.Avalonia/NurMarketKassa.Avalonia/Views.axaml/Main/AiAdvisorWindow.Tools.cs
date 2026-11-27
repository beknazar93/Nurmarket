using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-27, владелец: «Дай Нур-советнику доступы ко всем новым функциям и разделам, чтобы он мог открывать, изучать и получать данные от функций,
/// разделов, вкладок. Полный доступ». Инструменты советника (AdvisorToolDispatcher, Infrastructure) получают здесь всё из программы: права пользователя,
/// видимость пунктов меню, переход по разделам и данные разделов — ТОЛЬКО ЧТЕНИЕ, из тех же сервисов и кэшей, что сводка и окна разделов (сводка — кэш 60 с,
/// склад — каталог программы, чеки — ReceiptHistoryService, бот — ServerTelegramBotApi, ремонт — RepairOrderStore, рассылка — ClientMailingData…).
/// Под ответом — строка «Использовано: …» (какие разделы советник смотрел).</summary>
public sealed partial class AiAdvisorWindow
{
    /// <summary>«Действия с подтверждением» (черновик акции / заказа поставщику). Решение 27.11: по умолчанию ВЫКЛЮЧЕНЫ — инструмент не объявляется ИИ.</summary>
    internal const bool ToolActionsEnabled = false;

    /// <summary>Инструменты предлагаются ИИ для вопроса из 2+ слов (приветствие и «спасибо» идут без них — запрос короче и быстрее).</summary>
    internal static bool ShouldOfferTools(string? question) =>
        AdvisorCapabilities.MasterOn && (question ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    private static bool HasPermissionNow(string permission)
    {
        try
        {
            // Нет сервиса прав — не сеанс программы: ничего не разрешаем (безопасно).
            return App.AppHost?.Services.GetService<IPermissionService>()?.HasPermission(permission) ?? false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Диспетчер инструментов на один ответ (лимит вызовов и строка «Использовано» — на ответ).</summary>
    internal AdvisorToolDispatcher CreateToolDispatcher() => new(new AdvisorToolHost
    {
        HasPermission = HasPermissionNow,
        CapOn = AdvisorCapabilities.IsOn,
        InMenu = ProductActionPlan.MenuKeyVisible,
        Open = ProductActionPlan.OpenNavTarget is { } open ? (id, _) => open(id) : null,
        Data = ToolDataAsync,
        Marketplace = MarketplaceInfo,
        WhatsNew = WhatsNewInfo,
        Confirm = ConfirmToolActionAsync,
        Draft = PrepareToolDraftAsync,
        ActionsEnabled = ToolActionsEnabled,
    });

    // ── Строка «Использовано: …» ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Подпись под ответом: «Использовано: Продажи и финансы · 7 дней, Акции». Переносится по словам и не выходит за пузырь.</summary>
    internal static TextBlock BuildToolsUsedLine(IReadOnlyList<string> used)
    {
        var line = new TextBlock
        {
            Name = "AdvisorToolsUsed",
            Text = T("Использовано: ", "Колдонулду: ", "Used: ", "Kullanıldı: ", "Foydalanildi: ") + string.Join(", ", used),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 2),
        };
        Avalonia.Automation.AutomationProperties.SetName(line, line.Text);
        return line;
    }

    /// <summary>Добавить строку «Использовано: …» в конец пузыря ответа (после таблиц и кнопок).</summary>
    private void AddToolsUsedLine(SelectableTextBlock body, IReadOnlyList<string> used)
    {
        if (used.Count == 0 || body.Parent is not StackPanel content)
            return;
        var line = BuildToolsUsedLine(used);
        Use(line, TextBlock.ForegroundProperty, "BrushTextSoft");
        content.Children.Add(line);
    }

    // ── Действия с подтверждением (по умолчанию выключены) ───────────────────────────────────────────────────

    private async Task<bool> ConfirmToolActionAsync(string what, CancellationToken ct)
    {
        return await Dispatcher.UIThread.InvokeAsync(() => NurMarketKassa.AvaloniaHost.Services.PosDialogs.ConfirmYesNoModalAsync(this,
            T("Нур-советник предлагает: ", "Нур Кеңешчи сунуштайт: ", "Nur Advisor suggests: ", "Nur Danışman öneriyor: ", "Nur Maslahatchi taklif qiladi: ") + what + ".\n"
            + T("Ничего не проводится — будет только черновик. Выполнить?", "Эч нерсе өткөрүлбөйт — долбоор гана болот. Аткарылсынбы?", "Nothing is posted — only a draft. Do it?",
                "Hiçbir şey işlenmez — yalnızca taslak. Yapılsın mı?", "Hech narsa o'tkazilmaydi — faqat qoralama. Bajarilsinmi?"),
            T("Нур-советник", "Нур Кеңешчи", "Nur Advisor", "Nur Danışman", "Nur Maslahatchi"),
            T("Выполнить", "Аткаруу", "Do it", "Yap", "Bajarish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"))).ConfigureAwait(false);
    }

    private async Task<string> PrepareToolDraftAsync(string kind, CancellationToken ct)
    {
        await Dispatcher.UIThread.InvokeAsync(() => kind == "promo" ? AddPromoDraftAsync() : AddSupplierOrderDraftAsync()).ConfigureAwait(false);
        return "ЧЕРНОВИК ПОКАЗАН владельцу карточкой в чате. Ничего не проведено и не изменено — дальше решает владелец.";
    }

    // ── Маркетплейс и «что нового» ───────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<MarketplaceItem> MarketplaceItems()
    {
        try
        {
            // Каталог собирается без сети; значки и предпросмотры — ленивые функции, но на всякий случай — в потоке интерфейса.
            return Dispatcher.UIThread.CheckAccess()
                ? new LocalMarketplaceCatalogSource(_ => { }).GetItems()
                : Dispatcher.UIThread.Invoke(() => new LocalMarketplaceCatalogSource(_ => { }).GetItems());
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Нур-советник: каталог Маркетплейса не собран ({ex.GetType().Name}).", "WARNING");
            return Array.Empty<MarketplaceItem>();
        }
    }

    private static JsonObject ItemBrief(MarketplaceItem i) => new()
    {
        ["id"] = i.Id,
        ["title"] = i.Title,
        ["kind"] = i.KindLabel,
        ["status"] = i.TileStatusLine,
        ["line"] = i.BlurbText,
    };

    private static JsonObject ItemFull(MarketplaceItem i)
    {
        var o = ItemBrief(i);
        o["what"] = i.LongDescription.Length > 0 ? i.LongDescription : i.ShortDescription;
        if (i.Price is { } price)
            o["price"] = price.Label;
        if (i.HowTo.Count > 0)
            o["how_to_enable"] = new JsonArray(i.HowTo.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        if (i.Audience.Count > 0)
            o["for_whom"] = new JsonArray(i.Audience.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        if (i.Features.Count > 0)
            o["features"] = new JsonArray(i.Features.Take(6).Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
        if (i.WhatsNew.Length > 0)
            o["whats_new"] = i.WhatsNew;
        return o;
    }

    /// <summary>Описание функции Маркетплейса по слову раздела («Смета», «Ремонт», «Телеграм»).</summary>
    private static object? MarketplaceInfo(string hint)
    {
        var item = MarketplaceItems().FirstOrDefault(i => i.Title.Contains(hint, StringComparison.OrdinalIgnoreCase)) ?? MarketplaceItems().FirstOrDefault(i => i.Matches(hint));
        return item is null ? null : ItemFull(item);
    }

    private static object? WhatsNewInfo()
    {
        var items = MarketplaceItems();
        var fresh = items.Where(i => i.IsNew || i.WhatsNew.Length > 0).Take(15).Select(i => (JsonNode)ItemFull(i)).ToArray();
        return new JsonObject
        {
            ["new_in_marketplace"] = new JsonArray(fresh),
            ["sections_total"] = AdvisorSections.All.Count,
            ["recent_sections"] = new JsonArray(new[] { "promotions", "mailing", "repair", "telegrambot.offers", "telegrambot.bargain", "telegrambot.dialogs", "settings.onec", "estimate", "partsorders", "staffreport", "investments" }
                .Select(AdvisorSections.Find).Where(s => s is not null).Select(s => (JsonNode)new JsonObject { ["id"] = s!.Id, ["title"] = s.TitleRu, ["about"] = s.About }).ToArray()),
        };
    }

    // ── Данные разделов (только чтение) ─────────────────────────────────────────────────────────────────────

    private static AiAccess ToolAccess(AdvisorDataRequest r)
    {
        var settings = r.HasPermission(PosPermissions.ViewSettings);
        return new AiAccess(r.HasPermission(PosPermissions.ViewProducts), r.HasPermission(PosPermissions.ViewClients), settings, settings);
    }

    /// <summary>Строки текста сводки про закупку, наценку, себестоимость и прибыль — убрать, если пользователю они не видны.</summary>
    internal static string StripPurchaseLines(string text, bool purchaseVisible)
    {
        if (purchaseVisible || string.IsNullOrEmpty(text))
            return text;
        string[] words = { "закуп", "прибыл", "марж", "наценк", "себестоим", "purchase", "profit", "margin", "markup" };
        return string.Join("\n", text.Split('\n').Where(l => !words.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>Строки текста с искомым словом (заголовок — первая строка — остаётся); ничего не нашлось — весь текст.</summary>
    private static string FilterLines(string text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrEmpty(text))
            return text;
        var lines = text.Split('\n');
        var hit = lines.Skip(1).Where(l => SearchMatch.Matches(query, l)).ToList();
        return hit.Count == 0 ? text : lines[0] + "\n" + string.Join("\n", hit);
    }

    private static JsonObject Texts(AdvisorDataRequest r, params (string Name, string? Text)[] parts)
    {
        var o = new JsonObject();
        foreach (var (name, text) in parts)
            if (!string.IsNullOrWhiteSpace(text))
                o[name] = FilterLines(StripPurchaseLines(text!, r.PurchaseVisible), r.Query);
        return o;
    }

    private static async Task<IReadOnlyList<AiSection>> SummaryPartsAsync(AdvisorDataRequest r, SummaryParts parts) =>
        (await BuildSummaryAsync(ToolAccess(r), parts).ConfigureAwait(false)).Sections;

    private static string? PartText(IReadOnlyList<AiSection> sections, string key) =>
        string.Join("\n\n", sections.Where(s => s.Key == key).Select(s => s.Full)) is { Length: > 0 } t ? t : null;

    /// <summary>Сумма — общим форматтером программы («1 234,50 сом»).</summary>
    private static string Money(decimal v) => MoneyText.Som(v);

    /// <summary>Данные раздела для ИИ. Возвращает объект — диспетчер его очищает (секреты, телефоны, закупка) и ужимает.</summary>
    internal static async Task<object?> ToolDataAsync(AdvisorDataRequest r, CancellationToken ct)
    {
        switch (r.Area)
        {
            case "summary":
            {
                var s = await SummaryPartsAsync(r, SummaryParts.Periods).ConfigureAwait(false);
                return Texts(r, ("overview", PartText(s, "overview")), ("periods", PartText(s, "periods")));
            }
            case "sales":
                return await SalesDataAsync(r, ct).ConfigureAwait(false);
            case "shifts":
                return TelegramAiChat.ShiftArchiveProvider is { } shifts ? Texts(r, ("shifts", await shifts(ct).ConfigureAwait(false))) : null;
            case "staff":
            {
                var words = r.From == r.To && r.To == DateTime.Today ? "сегодня" : r.From == r.To && r.To == DateTime.Today.AddDays(-1) ? "вчера"
                    : (r.To - r.From).TotalDays <= 7 ? "неделя" : r.From.Month != DateTime.Today.Month && r.To.Month != DateTime.Today.Month ? "прошлый месяц" : "";
                return Texts(r, ("staff", await TelegramAiChat.StaffContextForAsync("сотрудники зарплата табель смены " + words, ct).ConfigureAwait(false)));
            }
            case "warehouse":
                return WarehouseData(r);
            case "expiry":
                return Texts(r, ("expiry", ProductExpiryIndex.ContextText(Math.Max(10, r.Top * 2))));
            case "purchases":
                return await PurchasesDataAsync(r, ct).ConfigureAwait(false);
            case "suppliers":
            {
                var svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
                var list = await svc.ListAsync(ct).ConfigureAwait(false);
                var debts = await SummaryPartsAsync(r, SummaryParts.Debts).ConfigureAwait(false);
                var rows = list.Where(x => r.Query is null || SearchMatch.Matches(r.Query, x.Name)).Take(r.Top)
                    .Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["company"] = x.Llc }).ToArray();
                var o = Texts(r, ("debts", PartText(debts, "debts")));
                o["suppliers_total"] = list.Count;
                o["suppliers"] = new JsonArray(rows);
                return o;
            }
            case "debts":
            {
                var s = await SummaryPartsAsync(r, SummaryParts.Debts | SummaryParts.Debtors).ConfigureAwait(false);
                return Texts(r, ("totals", PartText(s, "debts")), ("debtors", r.HasPermission(PosPermissions.ViewClients) ? PartText(s, "debtors") : null));
            }
            case "clients":
                return await ClientsDataAsync(r, ct).ConfigureAwait(false);
            case "analytics":
            {
                var s = await SummaryPartsAsync(r, SummaryParts.Analysis | SummaryParts.Periods).ConfigureAwait(false);
                return Texts(r, ("periods", PartText(s, "periods")), ("analysis", PartText(s, "analysis")), ("low_stock", PartText(s, "low_stock")));
            }
            case "abc":
            {
                // ABC построен по выручке и прибыли — без права на прибыль только выручка (строки с прибылью убираются).
                var s = await SummaryPartsAsync(r, SummaryParts.Abc).ConfigureAwait(false);
                return Texts(r, ("abc", PartText(s, "abc")));
            }
            case "bot":
            {
                var s = await SummaryPartsAsync(r, SummaryParts.Bot).ConfigureAwait(false);
                string? inquiries = null;
                try { inquiries = TelegramInquiryStore.ShortSummary(); }
                catch (Exception) { /* обращения не прочитаны — без них */ }
                return Texts(r, ("bot", PartText(s, "bot")), ("inquiries", inquiries));
            }
            case "bot_seller":
                return BotSellerUi.Api() is { } seller ? await seller.GetSellerConfigAsync(ct).ConfigureAwait(false) : null;
            case "bot_dialogs":
                return BotSellerUi.Api() is { } dialogs ? await dialogs.GetDialogsAsync(null, r.From, r.To, 1, ct).ConfigureAwait(false) : null;
            case "bot_sales":
                return BotSellerUi.Api() is { } stats ? await stats.GetSalesStatsAsync(r.From, r.To, ct).ConfigureAwait(false) : null;
            case "bot_bargain":
                return BotSellerUi.BargainApi() is { } bargain ? await bargain.GetBargainProductsAsync(r.Query, null, true, 1, ct).ConfigureAwait(false) : null;
            case "bot_offers":
                return BotSellerUi.BargainApi() is { } offers ? await offers.GetBargainOffersAsync(r.From, r.To, null, 1, ct).ConfigureAwait(false) : null;
            case "repair":
                return RepairData(r);
            case "mailing":
                return await MailingDataAsync(ct).ConfigureAwait(false);
            case "marketplace":
            {
                var items = MarketplaceItems();
                var picked = r.Query is { } q ? items.Where(i => i.Matches(q)).Take(r.Top).Select(i => (JsonNode)ItemFull(i)) : items.Select(i => (JsonNode)ItemBrief(i));
                return new JsonObject { ["total"] = items.Count, ["items"] = new JsonArray(picked.ToArray()) };
            }
            case "settings":
                return SettingsData(r);
            case "tariff":
                return TariffData();
            case "promotions":
                return await PromotionsDataAsync(r, ct).ConfigureAwait(false);
            default:
                return null;
        }
    }

    private static async Task<object?> SalesDataAsync(AdvisorDataRequest r, CancellationToken ct)
    {
        var s = await SummaryPartsAsync(r, SummaryParts.Periods).ConfigureAwait(false);
        var o = Texts(r, ("periods", PartText(s, "periods")));
        // Чеки за период — тот же запрос, что окно «Продажи» и вопрос «чеки за вчера» (ReceiptHistoryService); дольше месяца — только итоги сводки.
        if ((r.To - r.From).TotalDays > 31)
        {
            o["note"] = "чеки построчно — только за период до 31 дня; итоги выше";
            return o;
        }
        var all = await ReceiptHistoryService.LoadRangeAsync(r.From, r.To.AddDays(1), (r.To - r.From).TotalDays > 8 ? 15 : 10, ct).ConfigureAwait(false);
        var paid = all.Where(e => !ReceiptHistoryService.IsReturnedOrCanceled(e)).ToList();
        var back = all.Where(ReceiptHistoryService.IsReturnedOrCanceled).ToList();
        var total = paid.Sum(e => e.Total);
        o["receipts_count"] = paid.Count;
        o["revenue"] = Money(total);
        o["average_receipt"] = Money(paid.Count > 0 ? total / paid.Count : 0);
        o["returns_count"] = back.Count;
        o["returns_sum"] = Money(back.Sum(e => e.Total));
        o["by_payment"] = new JsonArray(paid.GroupBy(e => ReceiptHistoryService.PaymentLabel(e.PaymentMethod))
            .Select(g => (JsonNode)new JsonObject { ["method"] = g.Key, ["receipts"] = g.Count(), ["sum"] = Money(g.Sum(e => e.Total)) }).ToArray());
        if ((r.To - r.From).TotalDays >= 1)
            o["by_day"] = new JsonArray(paid.GroupBy(e => e.CreatedAt.Date).OrderBy(g => g.Key)
                .Select(g => (JsonNode)new JsonObject { ["day"] = g.Key.ToString("dd.MM"), ["receipts"] = g.Count(), ["sum"] = Money(g.Sum(e => e.Total)) }).ToArray());
        // Построчно — только если владелец разрешил «Чеки за период уходят в ИИ» (кассиры — кодами [С1]).
        if (AdvisorCapabilities.IsOn("priv.receipts"))
        {
            var rows = all.Where(e => r.Query is null || SearchMatch.Matches(r.Query, e.FirstItemName ?? "", e.ReceiptNumber))
                .Take(r.Top).Select(e => (JsonNode)new JsonObject
                {
                    ["no"] = e.ReceiptNumber,
                    ["time"] = e.CreatedAt.ToString("dd.MM HH:mm"),
                    ["cashier"] = AiPseudonyms.Staff(e.Cashier),
                    ["method"] = ReceiptHistoryService.PaymentLabel(e.PaymentMethod),
                    ["sum"] = Money(e.Total),
                    ["first_item"] = e.FirstItemName,
                    ["returned"] = ReceiptHistoryService.IsReturnedOrCanceled(e),
                }).ToArray();
            o["receipts"] = new JsonArray(rows);
        }
        return o;
    }

    private static object? WarehouseData(AdvisorDataRequest r)
    {
        List<NurMarketKassa.Models.Pos.CatalogProductTileVm> tiles;
        try
        {
            tiles = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        if (tiles.Count == 0)
            return null;
        var filter = r.Section.Id switch { "warehouse.out" => "out", "warehouse.low" => "low", _ => null };
        IEnumerable<NurMarketKassa.Models.Pos.CatalogProductTileVm> pick = tiles;
        if (r.Query is { } q)
            pick = pick.Where(t => SearchMatch.Matches(q, t.Title, t.Category ?? "", t.Barcode ?? ""));
        else if (filter == "out")
            pick = pick.Where(t => t.Quantity <= 0);
        else
            pick = pick.Where(t => t.Quantity > 0 && t.Quantity <= 5).OrderBy(t => t.Quantity);
        var o = new JsonObject
        {
            ["products_total"] = tiles.Count,
            ["out_of_stock"] = tiles.Count(t => t.Quantity <= 0),
            ["negative_stock"] = tiles.Count(t => t.Quantity < 0),
            ["stock_value_at_sale_price"] = Money((decimal)tiles.Where(t => t.Quantity > 0).Sum(t => t.Quantity * t.PriceValue)),
            ["purchase_value"] = Money((decimal)tiles.Where(t => t.Quantity > 0).Sum(t => t.Quantity * t.PurchasePrice)), // уберёт очистка без права на закупку
            ["list_kind"] = r.Query is not null ? "поиск: " + r.Query : filter == "out" ? "нет в наличии" : "заканчивается (остаток 1–5)",
            ["items"] = new JsonArray(pick.Take(r.Top).Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Title,
                ["category"] = t.Category,
                ["price"] = t.PriceValue,
                ["stock"] = t.Quantity,
                ["unit"] = t.Unit,
                ["barcode"] = t.Barcode,
                ["purchase_price"] = t.PurchasePrice,
            }).ToArray()),
        };
        return o;
    }

    private static async Task<object?> PurchasesDataAsync(AdvisorDataRequest r, CancellationToken ct)
    {
        var svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
        var (receipts, count, total, _) = await svc.GetReceiptsPageAsync(1, r.Top, null, r.From, r.To, ct).ConfigureAwait(false);
        return new JsonObject
        {
            ["receipts_total"] = count,
            ["purchase_total"] = Money((decimal)total), // сумма закупки — уберёт очистка без права на закупку
            ["receipts"] = new JsonArray(receipts.Where(x => r.Query is null || SearchMatch.Matches(r.Query, x.SupplierName, x.NumberDisplay, x.EsfNumber)).Take(r.Top).Select(x => (JsonNode)new JsonObject
            {
                ["number"] = x.NumberDisplay.Length > 0 ? x.NumberDisplay : x.ServerNumber,
                ["date"] = x.CreatedAt?.ToString("dd.MM.yyyy"),
                ["supplier"] = x.SupplierName,
                ["items_count"] = x.ItemsCount,
                ["purchase_sum"] = Money((decimal)x.Total),
                ["esf_number"] = x.EsfNumber,
                ["supplier_doc_number"] = x.SupplierDocNumber,
            }).ToArray()),
        };
    }

    private static async Task<object?> ClientsDataAsync(AdvisorDataRequest r, CancellationToken ct)
    {
        var snap = await ClientMailingData.LoadAsync(ct).ConfigureAwait(false);
        var list = snap.Contacts;
        IEnumerable<ClientMailing.Contact> pick = r.Query is { } q ? list.Where(c => SearchMatch.Matches(q, c.Name)) : list.OrderByDescending(c => c.Spent);
        return new JsonObject
        {
            ["clients_total"] = list.Count,
            ["with_debt"] = list.Count(c => c.Debt > 0),
            ["debt_total"] = Money((decimal)list.Where(c => c.Debt > 0).Sum(c => c.Debt)),
            ["list_kind"] = r.Query is not null ? "поиск: " + r.Query : "лучшие по сумме покупок",
            // Телефон не уходит в ИИ даже маской — только имя и цифры покупок.
            ["clients"] = new JsonArray(pick.Take(r.Top).Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["spent"] = Money((decimal)c.Spent),
                ["receipts"] = c.Receipts,
                ["debt"] = c.Debt > 0 ? Money((decimal)c.Debt) : null,
                ["last_purchase"] = c.LastPurchase?.ToString("dd.MM.yyyy"),
            }).ToArray()),
        };
    }

    private static async Task<object?> MailingDataAsync(CancellationToken ct)
    {
        var snap = await ClientMailingData.LoadAsync(ct).ConfigureAwait(false);
        var settings = ClientMailing.GetSettings();
        var dnd = ClientMailing.DoNotDisturb();
        var last = ClientMailing.LastMailed();
        var opened = ClientMailing.OpenedToday();
        // Только сегменты и число получателей — без телефонов и имён (рассылку отправляет владелец сам, по одному чату).
        return new JsonObject
        {
            ["clients_total"] = snap.Contacts.Count,
            ["opened_today"] = opened,
            ["segments"] = new JsonArray(ClientMailing.Segments.Select(seg =>
            {
                var plan = ClientMailing.Plan(seg, snap.Contacts, settings, dnd, last, DateTime.Now, opened);
                return (JsonNode)new JsonObject
                {
                    ["segment"] = ClientMailing.SegmentTitle(seg),
                    ["in_segment"] = plan.InSegment,
                    ["ready_to_send"] = plan.Items.Count(i => i.Skip == ClientMailing.SkipReason.None),
                    ["no_whatsapp"] = plan.NoPhone,
                };
            }).ToArray()),
        };
    }

    private static object? RepairData(AdvisorDataRequest r)
    {
        var orders = RepairOrderStore.Default.All();
        var today = DateTime.Today;
        IEnumerable<RepairOrder> pick = orders;
        if (r.Query is { } q)
            pick = pick.Where(o => SearchMatch.Matches(q, RepairRules.Code(o), o.Device, o.Fault, RepairRules.StatusName(o.Status)));
        return new JsonObject
        {
            ["orders_total"] = orders.Count,
            ["by_status"] = new JsonArray(orders.GroupBy(o => o.Status).Select(g => (JsonNode)new JsonObject { ["status"] = RepairRules.StatusName(g.Key), ["count"] = g.Count() }).ToArray()),
            ["overdue"] = orders.Count(o => RepairRules.IsOverdue(o, today)),
            ["prepay_total"] = Money(orders.Where(o => !RepairRules.IsFinal(o.Status)).Sum(o => RepairRules.PrepayToRefund(o))),
            // Клиент — только первая буква имени, телефон не уходит.
            ["orders"] = new JsonArray(pick.Take(r.Top).Select(o => (JsonNode)new JsonObject
            {
                ["code"] = RepairRules.Code(o),
                ["status"] = RepairRules.StatusName(o.Status),
                ["device"] = o.Device,
                ["fault"] = o.Fault.Length > 80 ? o.Fault[..80] + "…" : o.Fault,
                ["client"] = o.ClientName.Length > 0 ? o.ClientName[..1] + "." : "",
                ["accepted"] = o.AcceptedAt.ToString("dd.MM.yyyy"),
                ["due"] = o.DueDate?.ToString("dd.MM.yyyy"),
                ["estimate"] = Money(o.Estimate),
                ["prepay"] = Money(o.Prepay),
                ["overdue"] = RepairRules.IsOverdue(o, today),
            }).ToArray()),
        };
    }

    /// <summary>Настройки в безопасном виде: только флаги, числа и выборы (строк нет вовсе — в них бывают ключи, адреса, реквизиты), без полей-секретов.</summary>
    internal static object? SettingsData(AdvisorDataRequest r)
    {
        var filter = r.Query ?? r.Section.Id switch
        {
            "settings.onec" => "OneC",
            "settings.scales" => "Scale",
            "settings.print" => "Print",
            "settings.screen" => "Theme",
            _ => null,
        };
        if (filter is "1с" or "1c" or "1С")
            filter = "OneC";
        var rows = SafeSettings(UserPreferences.Instance, filter).Take(60).Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["value"] = p.Value }).ToArray();
        return new JsonObject { ["filter"] = filter, ["settings"] = new JsonArray(rows) };
    }

    internal static IEnumerable<(string Name, JsonNode? Value)> SafeSettings(object prefs, string? filter)
    {
        foreach (var p in prefs.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead)
                continue;
            var type = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (!(type == typeof(bool) || type == typeof(int) || type == typeof(long) || type == typeof(double) || type == typeof(decimal) || type.IsEnum))
                continue;
            if (AdvisorRedactor.IsSecretField(p.Name) || AdvisorRedactor.IsSoftSecretField(p.Name) || AdvisorRedactor.IsPhoneField(p.Name)
                || p.Name.Contains("Pin", StringComparison.Ordinal) || p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase))
                continue;
            if (filter is not null && !p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            object? value;
            try
            {
                value = p.GetValue(prefs);
            }
            catch (Exception)
            {
                continue;
            }
            yield return (p.Name, value is null ? null : type.IsEnum ? JsonValue.Create(value.ToString()) : JsonSerializer.SerializeToNode(value, type));
        }
    }

    private static object? TariffData()
    {
        var o = new JsonObject
        {
            ["plan"] = TariffGate.CurrentPlanName,
            ["server_plan"] = ServerCapabilities.Plan?.Code,
            ["subscription_until"] = CompanyInfoService.GetCachedSubscriptionStatus()?.EndDate is { } end && end.Year > 2000 ? end.ToString("dd.MM.yyyy") : null,
            ["start_tariff"] = TariffGate.IsStartTariff,
        };
        var packs = new JsonObject();
        foreach (var p in typeof(TariffGate).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(bool) && p.Name.StartsWith("Can", StringComparison.Ordinal)))
        {
            try { packs[p.Name] = (bool)p.GetValue(null)!; }
            catch (Exception) { /* признак не прочитан — пропускаем */ }
        }
        o["available"] = packs;
        return o;
    }

    private static async Task<object?> PromotionsDataAsync(AdvisorDataRequest r, CancellationToken ct)
    {
        var rules = PromotionRules.ActiveRules();
        Dictionary<string, string> names;
        try
        {
            names = CatalogCacheService.Products.Where(t => t.Id is { Length: > 0 }).GroupBy(t => t.Id!).ToDictionary(g => g.Key, g => g.First().Title, StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            names = new Dictionary<string, string>();
        }
        var rows = rules.Select(p => (Id: p.Key, Name: names.TryGetValue(p.Key, out var n) ? n : "", Rules: p.Value))
            .Where(x => r.Query is null || SearchMatch.Matches(r.Query, x.Name))
            .Take(r.Top)
            .Select(x =>
            {
                JsonNode? parsed;
                try { parsed = JsonNode.Parse(x.Rules); }
                catch (JsonException) { parsed = null; }
                return (JsonNode)new JsonObject { ["product"] = x.Name.Length > 0 ? x.Name : "товар " + x.Id, ["rules"] = parsed };
            }).ToArray();
        var o = new JsonObject
        {
            ["products_with_card_rules"] = rules.Count,
            ["card_rules_note"] = "акции из карточки товара (скидка % при сумме строки от min_amount, не больше promo_quantity штук)",
            ["card_rules"] = new JsonArray(rows),
        };
        // Акции v2 (раздел «Акции»): список и отчёт за период — тот же API, что окно PromotionsWindow; только чтение (без предпросмотра и изменений).
        if (PromotionsWindow.CanOffer())
        {
            var api = new PromotionsApi(App.GetRequiredService<NurMarketApiClient>());
            try { o["promotions"] = AdvisorRedactor.ToNode(await api.ListAsync(ct: ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { o["promotions_error"] = "список акций не получен"; }
            try { o["report"] = AdvisorRedactor.ToNode(await api.ReportAsync(r.From, r.To, ct: ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { o["report_error"] = "отчёт по акциям не получен"; }
        }
        return o;
    }
}
