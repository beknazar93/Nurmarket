using Avalonia.Controls;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-30, владелец: «Нур Советник: в настройках поставить тумблер (находить штрихкоды) и прочие возможности, чтобы можно
/// было выключать и включать какую-то часть». Окно советника: проверки тумблеров (<see cref="AdvisorCapabilities"/>), скрытие кнопок и
/// карточек выключенного, живое обновление при смене настроек (облако или соседнее окно), локальный ответ «что за товар по штрихкоду»
/// (справочник NurCRM, без нейросети). Выключенное не попадает в инструкцию ИИ и данные (AiContextBudget, TelegramAiChat) и не
/// исполняется (ProductActionPlan) — здесь только вид и вежливые сообщения.</summary>
public sealed partial class AiAdvisorWindow
{
    private static bool Cap(string id) => AdvisorCapabilities.IsOn(id);

    private Button? _attachButton;
    private string? _capsShown;

    /// <summary>«Эта возможность выключена владельцем (Настройки → Операции → Нур Советник)» — в ленту.</summary>
    private void SayDisabled(string? id = null) => AddBubble(AdvisorCapabilities.BlockedText(id), fromOwner: false);

    private static string CapsSignature() => string.Concat(AdvisorCapabilities.All.Select(c => AdvisorCapabilities.IsOn(c.Id) ? '1' : '0'));

    /// <summary>Привести вид окна к тумблерам: кнопки голоса и скрепки, готовые вопросы, приветствие (утренний отчёт, «Пора напомнить»),
    /// строка ввода при выключенном советнике. Вызывается при открытии и при любой смене настроек.</summary>
    private void ApplyCapabilities()
    {
        var on = AdvisorCapabilities.MasterOn;
        if (_mic is not null)
            _mic.IsVisible = on && Cap("voice.ask");
        if (_talk is not null)
            _talk.IsVisible = on && Cap("voice.live");
        if (_attachButton is not null)
            _attachButton.IsVisible = on && Cap("inv.read");
        _quick.IsVisible = on && Cap("pro.hints");
        // Звонок идёт — панель разговора управляет строкой ввода сама.
        if (_composer is not null && _live is null)
            _composer.IsVisible = on;
        RefreshKeyCard();
        var signature = CapsSignature();
        if (_capsShown is not null && _capsShown != signature && _welcome is not null && _messages.Children.Contains(_welcome))
        {
            _messages.Children.Remove(_welcome);
            _welcome = null;
            ShowWelcome();
        }
        _capsShown = signature;
    }

    /// <summary>Подписка на смену настроек (с любого потока). Отписывается при закрытии окна.</summary>
    private void WatchCapabilities()
    {
        Action onChanged = () => Dispatcher.UIThread.Post(ApplyCapabilities);
        AdvisorCapabilities.Changed += onChanged;
        SettingsCloudSync.Applied += onChanged;
        Closed += (_, _) =>
        {
            AdvisorCapabilities.Changed -= onChanged;
            SettingsCloudSync.Applied -= onChanged;
        };
        ApplyCapabilities();
    }

    // ── Списки по складу: «найди товары с 0 остатком» ─────────────────────────────────────────────────────────────

    /// <summary>true — запрос распознан и обработан здесь (ответ в ленте), false — это не список склада. Каталог в ИИ не уходит.</summary>
    private async Task<bool> TryStockQueryAsync(string question, AiAccess access)
    {
        if (AdvisorStockQuery.Parse(question) is not { } parsed)
            return false;
        if (!Cap("read.stocklists"))
        {
            SayDisabled("read.stocklists");
            return true;
        }
        if (!access.Products)
        {
            AddBubble(NoAccessText(T("складу", "кампага", "the warehouse", "depoya", "omborga"), "Склад"), fromOwner: false);
            return true;
        }
        return await ShowStockListAsync(parsed, question).ConfigureAwait(true);
    }

    /// <summary>2026-10-30 (приёмка 1.17.66): список по разобранному запросу; неясная фраза («нулевой» без «остаток / цена», «до 100») —
    /// вопрос «Вы имеете в виду: …?» с кнопками, выбор кнопкой запускает тот же показ уже по выбранному смыслу.</summary>
    private async Task<bool> ShowStockListAsync(AdvisorStockQuery.Parsed parsed, string question)
    {
        AdvisorStockQuery.Result? result;
        try
        {
            var showMoney = WarehouseWindow.RevisionShowPurchase;
            result = await Task.Run(() => AdvisorStockQuery.Run(parsed, OwnerShopFacts.Tiles(), showMoney)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"ИИ-советник: список склада не построен ({ex.GetType().Name}: {ex.Message}) — отвечает ИИ.", "WARNING");
            return false;
        }
        if (result is null)
            return false;
        var body = AddBubble(result.Text, fromOwner: false);
        if (result.Options is { Length: > 1 } options)
        {
            if (body.Parent is StackPanel ask)
            {
                var choices = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
                foreach (var option in options)
                {
                    var label = AdvisorStockQuery.Label(option);
                    var button = MorningReportCard.SmallButton(label.Length > 0 ? char.ToUpper(label[0]) + label[1..] : label, label);
                    button.Margin = new Avalonia.Thickness(0, 0, 8, 6);
                    button.Click += async (_, _) =>
                    {
                        if (!choices.IsEnabled)
                            return; // два клика подряд — один список
                        choices.IsEnabled = false;
                        try { await ShowStockListAsync(option, question).ConfigureAwait(true); }
                        catch (Exception ex)
                        {
                            PosLogger.Log($"ИИ-советник: список по выбранному смыслу не построен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                        }
                    };
                    choices.Children.Add(button);
                }
                ask.Children.Add(choices);
            }
            PosLogger.Log($"ИИ-советник: фраза неоднозначна ({string.Join(" / ", options.Select(o => o.Kind))}) — уточняем у владельца, без ИИ.", "INFO");
            return true;
        }
        PosLogger.Log($"ИИ-советник: список склада «{result.Title}» ({result.Total}) показан программой, без ИИ.", "INFO");
        if (result.NeedsMoney || result.Total == 0)
            return true;
        AddAnswerExtras(body, question, result.Text, Array.Empty<AiSection>(), localOnly: true);
        if (body.Parent is not StackPanel content)
            return true;
        var row = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 2, 0, 0) };
        void Add(string text, string name, Func<Task> action)
        {
            var b = MorningReportCard.SmallButton(text, name);
            b.Margin = new Avalonia.Thickness(0, 0, 8, 6);
            b.Click += async (_, _) =>
            {
                try { await action().ConfigureAwait(true); }
                catch (Exception ex)
                {
                    PosLogger.Log($"ИИ-советник: кнопка «{name}» не сработала ({ex.GetType().Name}: {ex.Message}).", "WARNING");
                    AddBubble(T("Не получилось: ", "Болбой калды: ", "Failed: ", "Olmadı: ", "Bo'lmadi: ") + FriendlyErrorText(ex.Message), fromOwner: false);
                }
            };
            row.Children.Add(b);
        }
        if (Cap("nav.open") && ProductActionPlan.OpenNavTarget is { } open && AdvisorNavRouter.ById(result.NavId) is { } target)
            Add(T($"Показать все на складе ({result.Total})", $"Кампада баарын көрсөтүү ({result.Total})", $"Show all in the warehouse ({result.Total})", $"Depoda hepsini göster ({result.Total})", $"Omborda hammasini ko'rsatish ({result.Total})"),
                T("Открыть склад", "Кампаны ачуу", "Open the warehouse", "Depoyu aç", "Omborni ochish"),
                async () => AddBubble((await AdvisorTurn.OpenAsync(target, open, null, Array.Empty<string>()).ConfigureAwait(true)).Text, fromOwner: false));
        if (result.Total > AdvisorStockQuery.Shown)
            Add("⬇ " + T($"Весь список в Excel ({Math.Min(result.Total, 5000)})", $"Толук тизме Excel'ге ({Math.Min(result.Total, 5000)})", $"Full list to Excel ({Math.Min(result.Total, 5000)})", $"Tüm liste Excel'e ({Math.Min(result.Total, 5000)})", $"To'liq ro'yxat Excel'ga ({Math.Min(result.Total, 5000)})"),
                T("Сохранить полный список в Excel", "Толук тизмени Excel'ге сактоо", "Save the full list to Excel", "Tam listeyi Excel'e kaydet", "To'liq ro'yxatni Excel'ga saqlash"),
                () => ExportTablesAsync(question, result.FullTable));
        if (result.CanOrder && Cap("pro.hints"))
            Add("🧾 " + T("Создать заказ поставщику", "Жеткирүүчүгө заказ түзүү", "Create a supplier order", "Tedarikçiye sipariş oluştur", "Yetkazib beruvchiga buyurtma yaratish"),
                T("Подготовить черновик заказа поставщику", "Жеткирүүчүгө заказдын долбоорун даярдоо", "Prepare a supplier order draft", "Tedarikçi sipariş taslağı hazırla", "Yetkazib beruvchiga buyurtma qoralamasini tayyorlash"),
                AddSupplierOrderDraftAsync);
        if (row.Children.Count > 0)
            content.Children.Add(row);
        return true;
    }

    // ── Штрихкоды: «что за товар 4600…», «штрихкод Марса» ─────────────────────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex BarcodeDigits = new(@"(?<![\d])\d{8,14}(?![\d])", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] BarcodeWords = { "штрихкод", "штрих-код", "штрих код", "баркод", "barcode", "barkod", "shtrix", "что за товар", "какой товар", "кандай товар", "чей код" };

    private static bool MentionsBarcode(string q) => BarcodeWords.Any(q.Contains);

    /// <summary>2026-10-30: вопрос про штрихкод отвечает программа без нейросети. По номеру — ваш склад, затем справочник NurCRM
    /// (тумблер «bc.directory», 128 тыс. товаров, работает без интернета); по названию — штрихкоды найденных товаров вашего склада.
    /// null — это не вопрос про штрихкод или ответа нет (дальше отвечает ИИ).</summary>
    private static async Task<string?> TryBarcodeAnswerAsync(string question, AiAccess access, CancellationToken ct)
    {
        var q = (question ?? "").ToLowerInvariant();
        if (!MentionsBarcode(q) || ProductActionPlan.LooksLikeAction(q.Replace("штрихкод", "").Replace("штрих-код", "").Replace("штрих код", "").Replace("баркод", "")))
            return null;
        var codes = BarcodeDigits.Matches(q).Select(m => m.Value).Distinct().Take(3).ToList();
        if (codes.Count > 0)
        {
            var lines = new List<string>();
            foreach (var code in codes)
            {
                ct.ThrowIfCancellationRequested();
                var own = access.Products && ProductActions.Find(code) is { } p && string.Equals((p.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase) ? p : null;
                if (own is not null)
                {
                    lines.Add(T($"{code} — у вас на складе: «{own.Title}», цена {own.PriceLine}, остаток {own.Quantity:0.###}.",
                        $"{code} — сиздин кампада: «{own.Title}», баасы {own.PriceLine}, калдыгы {own.Quantity:0.###}.",
                        $"{code} — in your warehouse: “{own.Title}”, price {own.PriceLine}, stock {own.Quantity:0.###}.",
                        $"{code} — deponuzda: «{own.Title}», fiyat {own.PriceLine}, stok {own.Quantity:0.###}.",
                        $"{code} — omboringizda: «{own.Title}», narxi {own.PriceLine}, qoldiq {own.Quantity:0.###}."));
                    continue;
                }
                if (!Cap("bc.directory"))
                    return null; // справочник выключен — дальше отвечает ИИ по вашему складу
                var info = await Task.Run(() => NurMarketKassa.Services.GlobalProducts.GlobalProductCatalog.Instance.Find(code), ct).ConfigureAwait(true);
                if (info is not null)
                    lines.Add(T($"{code} — в справочнике NurCRM: «{info.Name}»" + (info.Brand.Length > 0 ? $", бренд {info.Brand}" : "") + (info.Category.Length > 0 ? $", категория {info.Category}" : "") + ". На вашем складе такого нет.",
                        $"{code} — NurCRM справочнигинде: «{info.Name}»" + (info.Brand.Length > 0 ? $", бренд {info.Brand}" : "") + (info.Category.Length > 0 ? $", категория {info.Category}" : "") + ". Сиздин кампада мындай жок.",
                        $"{code} — in the NurCRM directory: “{info.Name}”" + (info.Brand.Length > 0 ? $", brand {info.Brand}" : "") + (info.Category.Length > 0 ? $", category {info.Category}" : "") + ". Not in your warehouse.",
                        $"{code} — NurCRM rehberinde: «{info.Name}»" + (info.Brand.Length > 0 ? $", marka {info.Brand}" : "") + (info.Category.Length > 0 ? $", kategori {info.Category}" : "") + ". Deponuzda yok.",
                        $"{code} — NurCRM ma'lumotnomasida: «{info.Name}»" + (info.Brand.Length > 0 ? $", brend {info.Brand}" : "") + (info.Category.Length > 0 ? $", kategoriya {info.Category}" : "") + ". Omboringizda yo'q."));
                else
                    lines.Add(T($"{code} — не нашёл ни на вашем складе, ни в справочнике NurCRM.", $"{code} — сиздин кампадан да, NurCRM справочнигинен да таппадым.",
                        $"{code} — found neither in your warehouse nor in the NurCRM directory.", $"{code} — ne deponuzda ne de NurCRM rehberinde buldum.", $"{code} — na omboringizdan, na NurCRM ma'lumotnomasidan topolmadim."));
            }
            PosLogger.Log($"ИИ-советник: штрихкод ({codes.Count}) определён программой по складу и справочнику, без ИИ.", "INFO");
            return string.Join("\n", lines);
        }
        // По названию → штрихкод: только товары вашего склада (справочник устроен по штрихкоду, по названию в нём не ищут).
        if (!access.Products)
            return null;
        var found = ProductActionPlan.Mentioned(question ?? "", 5);
        if (found.Count == 0)
            return null;
        PosLogger.Log($"ИИ-советник: штрихкоды {found.Count} товаров показаны из каталога программы, без ИИ.", "INFO");
        return T("Штрихкоды (из вашего каталога):\n", "Штрихкоддор (сиздин каталогдон):\n", "Barcodes (from your catalog):\n", "Barkodlar (kataloğunuzdan):\n", "Shtrix-kodlar (katalogingizdan):\n")
               + string.Join("\n", found.Select(p => "• " + p.Title + " — " + (string.IsNullOrWhiteSpace(p.Barcode) ? T("штрихкода нет", "штрихкоду жок", "no barcode", "barkod yok", "shtrix-kod yo'q") : p.Barcode)));
    }
}
