using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services; // 2026-11-20: IEstimateCatalog в FakeCatalog
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Billing;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-20 (жёсткое замечание владельца по окну «Смета»: поля шапки налезали друг на друга и на подписи): проверка «поля формы не налезают» для всех окон и диалогов —
/// каталог экранов, окна вне каталога и окна, сделанные 19–20.11 (Смета и её диалоги, Заказы под клиента, Подписка, Партии, Срок и партия, Поддержка, Аналоги, мастер сферы…),
/// а также все вкладки TabControl, страницы «Настроек» и вкладки карточки товара. Размеры: окна — 1024×600, 1366×768, 1024×768; диалоги — по содержимому; языки ru, ky, uz.
/// Правила по визуальному дереву (скрытые элементы IsVisible=false и Opacity=0 не считаются):
/// (а) два видимых поля ввода (TextBox, ComboBox, NumericUpDown, DatePicker, CalendarDatePicker, AutoCompleteBox) не перекрываются больше чем на 2 px;
/// (б) подпись (TextBlock над полем или слева от него) не врезается в рамку поля и не стоит вплотную (зазор меньше 2 px);
/// (в) поле и кнопка не перекрываются; (г) поле целиком в окне и в своём ScrollViewer (вниз при прокрутке можно, вбок — нет); (д) ширина поля не меньше 48 px.
/// Порог не ослабляется; ложные срабатывания — только в <see cref="Known"/> с комментарием. Тариф «Своё» (TariffCustomWindow) ведёт другой исполнитель — в список не входит.</summary>
[Collection("UiScenarios")]
public sealed class FormFieldsOverlapUiTests : IDisposable
{
    private const double Overlap = 2;
    private const double MinFieldWidth = 48;
    private static readonly DateTime Today = new(2026, 11, 20);
    private static readonly UiSize[] WindowSizes = [new(1024, 600), new(1366, 768), new(1024, 768)];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "form-overlap-" + Guid.NewGuid().ToString("N")[..8]);

    public FormFieldsOverlapUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        ModalShell.WorkingAreaForTests = null;
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(null);
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    /// <summary>Известные и осознанно отложенные нарушения: «окно|правило|элемент А|элемент Б». Пока пусто. Окна других исполнителей сюда не вносятся — их нарушения идут отчётом.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
    };

    private static readonly string[] OwnNames =
    [
        "n-estimates", "n-estimate-editor-new", "n-estimate-editor-filled", "n-estimate-editor-filled-open", "n-estimate-calc-tile", "n-estimate-calc-paint", "n-estimate-print",
        "n-parts-orders", "n-parts-order-edit", "n-subscription", "n-product-batches", "n-receiving-batch", "n-support-request", "n-analogs", "n-sphere-wizard", "n-fiscal-settings-server",
        "n-invoice-editor-barcodes", "n-barcode-extras", "n-barcode-conflict", // 2026-11-20: штрихкоды в накладных
        "n-ai-variants-card", "n-ai-variants-bar", // 2026-11-20: вкусы одного товара при загрузке накладной через ИИ (блок вариантов и панель «Команда ИИ»)
    ];

    public static IEnumerable<object[]> Matrix()
    {
        var names = ScreenCatalog.All.Select(s => "c-" + s.Name)
            .Concat(TextNotClippedTests.ExtraWindows.Keys)
            .Concat(OwnNames);
        foreach (var name in names)
        foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek })
            yield return [name, lang];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Matrix))]
    public async Task Window_FormFields_DoNotOverlap(string name, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var (window, sized) = Create(name);
        var code = lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };
        var found = new Dictionary<string, List<string>>();
        try
        {
            foreach (var size in sized ? WindowSizes : [default(UiSize)])
            {
                using (var _ = UiShot.Render(window, sized ? size : null))
                {
                }
                UiShot.Settle(3);
                Collect(found, window, name, sized ? size.ToString() : "по содержимому");
                foreach (var (label, activate) in Modes(window))
                {
                    activate();
                    UiShot.Settle(3);
                    Collect(found, window, name + "/" + label, sized ? size.ToString() : "по содержимому");
                }
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
        var fresh = found.Where(kv => !Known.Contains(kv.Key)).ToList();
        Assert.True(fresh.Count == 0, $"Поля форм налезают ({name}, {code}):\n" + string.Join("\n", fresh.Take(25).Select(kv => $"  {kv.Key} — {string.Join(", ", kv.Value.Distinct())}"))
            + (fresh.Count > 25 ? $"\n  … и ещё {fresh.Count - 25}" : ""));
    }

    /// <summary>Проверка самой проверки: на искусственном окне с заведомыми ошибками (два поля друг на друге, подпись вплотную к рамке, кнопка поверх поля, поле за краем окна, узкое поле)
    /// проба обязана находить каждый вид нарушения — иначе тест «всё чисто» ничего не стоит.</summary>
    [AvaloniaFact]
    public async Task Checker_FindsEveryKindOfViolation_InSyntheticWindow()
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, AppLanguage.Russian);
        var canvas = new Canvas();
        void Put(Control c, double x, double y, double w = double.NaN, double h = double.NaN)
        {
            Canvas.SetLeft(c, x);
            Canvas.SetTop(c, y);
            if (!double.IsNaN(w))
                c.Width = w;
            if (!double.IsNaN(h))
                c.Height = h;
            canvas.Children.Add(c);
        }
        Put(new TextBox { Name = "A" }, 10, 40, 200, 40);
        Put(new TextBox { Name = "B" }, 150, 40, 200, 40);                       // поля перекрываются на 60 px
        Put(new TextBlock { Text = "Подпись", FontSize = 12.5, Height = 18 }, 400, 22);     // низ подписи 40 — вплотную к полю C (верх 40)
        Put(new TextBox { Name = "C" }, 400, 40, 200, 40);
        Put(new Button { Name = "Btn", Content = "Кнопка" }, 20, 60, 100, 40);   // кнопка поверх поля A
        Put(new TextBox { Name = "D" }, 640, 150, 120, 40);                      // за правым краем окна (ширина 700)
        Put(new TextBox { Name = "E", MinWidth = 0 }, 10, 200, 30, 40);                        // схлопнутое поле
        var window = new Window { Content = canvas, Width = 700, Height = 300 };
        try
        {
            using var _ = UiShot.Render(window);
            var rules = FindViolations(window, "synthetic").Select(v => v.Key.Split('|')[1]).ToHashSet();
            Assert.Contains("поля-перекрытие", rules);
            Assert.Contains("подпись-вплотную", rules);
            Assert.Contains("поле-кнопка", rules);
            Assert.Contains("поле-вне-окна-по-ширине", rules);
            Assert.Contains("поле-узкое", rules);
        }
        finally
        {
            window.Close();
            UiShot.Pump();
        }
    }

    // ------------------------------------------------------------------ окна

    private (Window Window, bool Sized) Create(string name)
    {
        if (name.StartsWith("c-", StringComparison.Ordinal))
        {
            var screen = ScreenCatalog.Get(name[2..]);
            return (screen.Create(), screen.Kind == ScreenKind.Window);
        }
        if (TextNotClippedTests.ExtraWindows.TryGetValue(name, out var extra))
            return (extra(), true);
        var (deps, store) = EstimateDeps_();
        switch (name)
        {
            case "n-estimates":
                Filled(store);
                return (new EstimatesWindow(deps), true);
            case "n-estimate-editor-new":
                return (new EstimateEditorWindow(deps, store.Create()), true);
            case "n-estimate-editor-filled":
                return (new EstimateEditorWindow(deps, Filled(store)), true);
            case "n-estimate-editor-filled-open":
            {
                var w = new EstimateEditorWindow(deps, Filled(store));
                w.Opened += (_, _) => w.GetVisualDescendants().OfType<Expander>().First(e => e.Name == "EstimateHeaderExpander").IsExpanded = true;
                return (w, true);
            }
            case "n-estimate-calc-tile":
                NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(NurMarketKassa.Services.Hardware.HardwareCalcSettings.CreateDefault());
                return (new EstimateCalcDialog(deps, store.Create(), _ => true, EstimateMaterial.Tile), true);
            case "n-estimate-calc-paint":
                NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(NurMarketKassa.Services.Hardware.HardwareCalcSettings.CreateDefault());
                return (new EstimateCalcDialog(deps, store.Create(), _ => true, EstimateMaterial.Paint), true);
            case "n-estimate-print":
                return (new EstimatePrintWindow(deps, Filled(store)), true);
            case "n-parts-orders":
            {
                var pd = PartsDeps();
                SeedOrder(pd.Store);
                return (new PartsOrdersWindow(pd), true);
            }
            case "n-parts-order-edit":
                return (new PartsOrderEditDialog(new PartsOrder { ClientName = "СТО Асан", Phone = "0700 123 456", Prepayment = 1000m, DueDate = new DateTime(2026, 11, 25), Supplier = "Автозапчасти Азия" }), false);
            case "n-subscription":
                return (new SubscriptionWindow(new FakeBilling(), true) { Clock = () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(6)) }, true);
            case "n-product-batches":
                return (new ProductBatchesWindow("Йогурт «Нурсүт» клубника 200 г", "шт", true, _ => Task.FromResult(new List<StockBatch>())), false);
            case "n-receiving-batch":
                return (new ReceivingBatchDialog(new ReceivingLineVm { ProductName = "Йогурт «Нурсүт» клубника 200 г" }), false);
            case "n-support-request":
                return (new SupportRequestWindow(null, "меню", null), false);
            case "n-analogs":
                return (new AnalogsWindow(new CatalogProductTileVm("1", "Фильтр масляный Mann-Filter W 712/94", "520.00 сом", false, null) { Article = "W 712/94", Quantity = 5, Unit = "шт" },
                    new List<CatalogProductTileVm>(), _ => { }), false);
            case "n-sphere-wizard":
                return (new MarketSphereChoiceWindow(), false);
            case "n-fiscal-settings-server": // 2026-11-20: «Налоги и ККМ», режим server со сведениями о ККМ с сервера
            {
                var card = new NurMarketKassa.AvaloniaHost.Views.Settings.FiscalSettingsCard { ForceVisible = true, SkipServerRead = true };
                card.ShowForPreview(new NurMarketKassa.Services.Fiscal.FiscalCapabilities(true, NurMarketKassa.Services.Fiscal.FiscalMode.Server, true, "simplified", false, new[] { 0m, 12m }, 2m,
                    new NurMarketKassa.Services.Fiscal.FiscalKkmInfo("EK00044889", "0000000318879", "0000000002470706", "2029-11-18", "01102201510234", "2.0", true, "57")));
                return (new Window { Content = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Children = { card } } } }, true);
            }
            case "n-invoice-editor-barcodes": // 2026-11-20: «Закупка по накладной» — колонка «Штрихкод» с «Сканировать» и «+N»
                return (InvoiceBarcodeUiTests.CreateEditorForLayout(), true);
            case "n-barcode-extras":
                return (InvoiceBarcodeUiTests.CreateExtrasForLayout(), false);
            case "n-ai-variants-card":
                return (InvoiceVariantsUiTests.CreateCardForLayout(), true);
            case "n-ai-variants-bar":
                return (InvoiceVariantsUiTests.CreateAdvisorForLayout(), true);
            case "n-barcode-conflict":
                return (InvoiceBarcodeUiTests.CreateConflictForLayout(), false);
            default:
                throw new InvalidOperationException("Неизвестное окно " + name);
        }
    }

    private sealed class FakeCatalog : IEstimateCatalog
    {
        private readonly Dictionary<string, EstimateCatalogInfo> _items = new()
        {
            ["p1"] = new("p1", "Краска акриловая белая 10 л", "банка", 3200m, 3000m, 2500m, 20m, false),
            ["p2"] = new("p2", "Плитка керамическая 30х60 1,44 м2", "уп", 1800m, 1700m, 1400m, 40m, false),
        };

        public List<EstimateCatalogInfo> Search(string query, int max = 8) =>
            _items.Values.Where(i => i.Id == query || i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(max).ToList();

        public EstimateCatalogInfo? Find(string id) => _items.GetValueOrDefault(id);

        public EstimateCatalogInfo Get(string id) => _items[id];
    }

    private readonly FakeCatalog _catalog = new();

    private (EstimateDeps Deps, EstimateStore Store) EstimateDeps_()
    {
        var store = new EstimateStore(Path.Combine(_dir, "estimates-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => Today.AddHours(10));
        var deps = new EstimateDeps
        {
            Store = store,
            Catalog = _catalog,
            Till = null,
            PrintRibbon = _ => Task.FromResult<string?>(null),
            OpenWhatsApp = (_, _) => { },
            CopyText = _ => Task.CompletedTask,
            Confirm = (_, _) => Task.FromResult(true),
            CanSeeCost = () => true,
            CanEditNorms = () => true,
            Today = () => Today,
            Buyers = () => Task.FromResult(new List<ClientDocsUi.Buyer>()),
            Cashier = () => "Тест",
        };
        return (deps, store);
    }

    private EstimateDoc Filled(EstimateStore store)
    {
        var doc = store.Create(d =>
        {
            d.Title = "Ремонт комнаты 18 м² на ул. Ленина";
            d.ClientName = "Бакыт Асанов";
            d.Phone = "0700 123 456";
            d.Address = "ул. Ленина 5, кв. 12";
            d.DiscountPercent = 5m;
        });
        doc.Sections.Add(new EstimateSection { Name = "Стены" });
        var paint = new EstimateLine { Qty = 2m, Unit = "банка", SectionId = doc.Sections[0].Id };
        EstimateRules.FillFromCatalog(paint, _catalog.Get("p1"), EstimateLevel.Retail);
        var work = new EstimateLine { Name = "Работа: укладка плитки и покраска стен", Unit = "усл.", Qty = 1m, Price = 15000m, ReservePercent = 0m };
        doc.Lines.AddRange([paint, work]);
        store.Save(doc);
        return doc;
    }

    private PartsOrderDeps PartsDeps() => new()
    {
        Store = new PartsOrderStore(Path.Combine(_dir, "orders-" + Guid.NewGuid().ToString("N")[..6] + ".json"), () => new DateTime(2026, 11, 20, 10, 0, 0)),
        SellAsync = _ => Task.FromResult(2),
        AskPin = () => Task.FromResult(false),
        OpenUrl = _ => true,
        ShopName = () => "Автодом",
        StartTariff = () => true,
    };

    private static void SeedOrder(PartsOrderStore store) => store.Save(new PartsOrder
    {
        ClientName = "СТО Асан",
        Phone = "0700 123 456",
        Prepayment = 1000m,
        Status = PartsOrderStatus.New,
        DueDate = new DateTime(2026, 11, 25),
        Supplier = "Автозапчасти Азия",
        Lines = new List<PartsOrderLine> { new() { Title = "Фильтр масляный Toyota 90915-YZZJ2", Qty = 2m, Price = 450m } },
    });

    /// <summary>Минимальная заглушка оплаты подписки: только настройка тарифов и сроков (окно выбора), счетов нет.</summary>
    private sealed class FakeBilling : IBillingApi
    {
        public Task<BillingConfig> GetConfigAsync(CancellationToken ct = default) =>
            Task.FromResult(BillingParser.ParseConfig(JsonDocument.Parse("{\"available\":true,\"mode\":\"test\",\"bank_fee\":\"30.00\",\"months\":[1,3,6,12],\"current_plan\":\"standard\",\"valid_until\":\"2026-12-19\","
                + "\"plans\":[{\"code\":\"start\",\"price_month\":\"1300.00\"},{\"code\":\"standard\",\"price_month\":\"3600.00\"}],\"qr_ttl_seconds\":1200}").RootElement.Clone()));

        public Task<BillingInvoice> CreateInvoiceAsync(int months, string? planCode, string idempotencyKey, CancellationToken ct = default) => throw new HttpRequestException("нет связи");

        public Task<BillingInvoice> GetInvoiceAsync(string invoiceId, CancellationToken ct = default) => throw new HttpRequestException("нет связи");

        public Task<IReadOnlyList<BillingInvoice>> ListInvoicesAsync(string? status = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BillingInvoice>>([]);

        public Task<BillingInvoice?> CancelInvoiceAsync(string invoiceId, CancellationToken ct = default) => Task.FromResult<BillingInvoice?>(null);

        public Task<BillingInvoice?> TestPayAsync(string invoiceId, string result, CancellationToken ct = default) => Task.FromResult<BillingInvoice?>(null);
    }

    /// <summary>Режимы внутри окна: вкладки TabControl верхнего уровня (в том числе «Товар» в карточке товара) и страницы «Настроек».</summary>
    private static List<(string Label, Action Activate)> Modes(Window w)
    {
        var modes = new List<(string, Action)>();
        foreach (var tabs in w.GetVisualDescendants().OfType<TabControl>().Where(t => t.IsEffectivelyVisible && !t.GetVisualAncestors().OfType<TabItem>().Any()).ToList())
            foreach (var tab in tabs.Items.OfType<TabItem>().Where(t => t.IsVisible).ToList())
            {
                var t = tab;
                var c = tabs;
                modes.Add((LayoutStabilityProbe.TabTitle(t), () => c.SelectedItem = t));
            }
        if (w is PosSettingsWindow ps)
            foreach (var tab in new[] { "screen", "scales", "print", "updates", "operations", "customization", "employees", "keys", "ai" })
                modes.Add(("настройки: " + tab, () => ps.SelectNavTab(tab)));
        return modes;
    }

    // ------------------------------------------------------------------ проверка

    private static void Collect(Dictionary<string, List<string>> found, Window window, string scope, string size)
    {
        foreach (var (key, detail) in FindViolations(window, scope))
        {
            if (!found.TryGetValue(key, out var list))
                found[key] = list = [];
            list.Add($"{size}: {detail}");
        }
    }

    private static bool IsField(Visual v) => v is TextBox or ComboBox or NumericUpDown or DatePicker or CalendarDatePicker or AutoCompleteBox;

    private static bool Shown(Visual v)
    {
        for (Visual? cur = v; cur is not null; cur = cur.GetVisualParent())
            if (!cur.IsVisible || cur.Opacity <= 0.01)
                return false;
        return v.Bounds.Width > 0 && v.Bounds.Height > 0;
    }

    private static Rect? RectIn(Visual v, Visual root) =>
        v.TranslatePoint(default, root) is { } p ? new Rect(p, v.Bounds.Size) : null;

    /// <summary>Видимая часть элемента: пересечение с областями прокрутки и обрезки предков (прокрутка вниз и обрезка — не «налезание»). null — элемент целиком скрыт обрезкой.</summary>
    private static Rect? VisibleRect(Visual v, Visual root)
    {
        if (RectIn(v, root) is not { } r)
            return null;
        foreach (var a in v.GetVisualAncestors())
        {
            if (a is not (ScrollViewer or Control { ClipToBounds: true }) || ReferenceEquals(a, root))
                continue;
            if (RectIn(a, root) is not { } ar)
                continue;
            r = r.Intersect(ar);
            if (r.Width <= 0 || r.Height <= 0)
                return null;
        }
        return r;
    }

    private static string Desc(Control c)
    {
        var text = c switch
        {
            TextBox tb => string.IsNullOrEmpty(tb.Watermark?.ToString()) ? Avalonia.Automation.AutomationProperties.GetName(tb) : tb.Watermark?.ToString(),
            TextBlock t => t.Text,
            _ => Avalonia.Automation.AutomationProperties.GetName(c) is { Length: > 0 } a ? a : UiDrive.TextOf(c),
        };
        text = string.IsNullOrWhiteSpace(text) ? "" : "«" + (text.Length > 28 ? text[..28] + "…" : text.Replace('\n', ' ')) + "»";
        return c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : "#" + c.Name) + text;
    }

    internal static IEnumerable<(string Key, string Detail)> FindViolations(Window window, string scope) // 2026-11-20: internal — им же проверяется мастер «Массовая загрузка»
    {
        var all = window.GetVisualDescendants().OfType<Control>().Where(Shown).ToList();
        var fields = all.Where(c => IsField(c) && !c.GetVisualAncestors().Any(IsField)).ToList();
        var fullRects = new Dictionary<Control, Rect>();
        var rects = new Dictionary<Control, Rect>();
        foreach (var f in fields)
            if (RectIn(f, window) is { } full && VisibleRect(f, window) is { } vis)
            {
                fullRects[f] = full;
                rects[f] = vis;
            }
        fields = fields.Where(rects.ContainsKey).ToList();
        string K(string rule, Control a, Control? b = null) => $"{scope}|{rule}|{Desc(a)}" + (b is null ? "" : "|" + Desc(b));

        // (а) поля друг на друге
        for (var i = 0; i < fields.Count; i++)
        for (var j = i + 1; j < fields.Count; j++)
        {
            var inter = rects[fields[i]].Intersect(rects[fields[j]]);
            if (inter.Width > Overlap && inter.Height > Overlap)
                yield return (K("поля-перекрытие", fields[i], fields[j]), $"{Desc(fields[i])} и {Desc(fields[j])} перекрываются на {inter.Width:0}×{inter.Height:0} px");
        }

        // (б) подписи
        var labels = all.OfType<TextBlock>().Where(t => !string.IsNullOrWhiteSpace(t.Text) && !t.Classes.Contains("mdl2")
            && !t.GetVisualAncestors().Any(a => IsField(a) || a is Button or ToggleButton or TabItem or ListBoxItem or ComboBoxItem or MenuItem or Expander)).ToList();
        foreach (var l in labels)
        {
            if (VisibleRect(l, window) is not { } lr)
                continue;
            foreach (var f in fields)
            {
                var fr = rects[f];
                var inter = lr.Intersect(fr);
                if (inter.Width > Overlap && inter.Height > Overlap)
                {
                    if (fr.Inflate(1).Contains(lr))
                        continue; // текст целиком внутри поля (суффикс «сом», подсказка поверх) — не подпись
                    yield return (K("подпись-врезается", l, f), $"подпись {Desc(l)} налезает на рамку поля {Desc(f)} ({inter.Width:0}×{inter.Height:0} px)");
                    continue;
                }
                var hOverlap = Math.Min(lr.Right, fr.Right) - Math.Max(lr.Left, fr.Left);
                var vOverlap = Math.Min(lr.Bottom, fr.Bottom) - Math.Max(lr.Top, fr.Top);
                var above = fr.Top - lr.Bottom;
                if (hOverlap > 4 && lr.Top < fr.Top && lr.Height < 40 && above >= -Overlap && above < 2)
                    yield return (K("подпись-вплотную", l, f), $"подпись {Desc(l)} вплотную к полю {Desc(f)}: зазор {above:0.#} px");
                var left = fr.Left - lr.Right;
                if (vOverlap > 4 && lr.Left < fr.Left && left >= -Overlap && left < 2)
                    yield return (K("подпись-вплотную-слева", l, f), $"подпись {Desc(l)} вплотную слева от поля {Desc(f)}: зазор {left:0.#} px");
            }
        }

        // (в) поле и кнопка
        var buttons = all.Where(c => c is Button or ToggleButton && !c.GetVisualAncestors().Any(IsField) && !c.GetVisualDescendants().Any(IsField)).ToList();
        foreach (var b in buttons)
        {
            if (VisibleRect(b, window) is not { } br)
                continue;
            foreach (var f in fields)
            {
                var inter = br.Intersect(rects[f]);
                if (inter.Width > Overlap && inter.Height > Overlap)
                    yield return (K("поле-кнопка", f, b), $"поле {Desc(f)} и кнопка {Desc(b)} перекрываются на {inter.Width:0}×{inter.Height:0} px");
            }
        }

        // (г) поле в окне и в своём ScrollViewer; (д) ширина
        var client = window.ClientSize;
        foreach (var f in fields)
        {
            var r = fullRects[f];
            var scrolls = f.GetVisualAncestors().OfType<ScrollViewer>().ToList();
            if (r.Left < -Overlap || r.Right > client.Width + Overlap)
                yield return (K("поле-вне-окна-по-ширине", f), $"поле {Desc(f)} выходит за окно по горизонтали: {r.Left:0}…{r.Right:0} при ширине окна {client.Width:0}");
            if ((r.Top < -Overlap || r.Bottom > client.Height + Overlap) && !scrolls.Any(s => s.Extent.Height > s.Viewport.Height + 1))
                yield return (K("поле-вне-окна-по-высоте", f), $"поле {Desc(f)} выходит за окно по вертикали: {r.Top:0}…{r.Bottom:0} при высоте окна {client.Height:0}");
            var scrolledInsideH = false;
            var scrolledInsideV = false; // поле лежит во вложенной прокрутке — внешняя область его не «теряет»: оно прокручивается внутри
            foreach (var s in scrolls)
            {
                if (RectIn(f, s) is not { } sr)
                    continue;
                var canH = s.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && s.Extent.Width > s.Viewport.Width + 1;
                var canV = s.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && s.Extent.Height > s.Viewport.Height + 1;
                var skipH = scrolledInsideH;
                var skipV = scrolledInsideV;
                scrolledInsideH |= canH;
                scrolledInsideV |= canV;
                canH |= skipH;
                canV |= skipV;
                if (!canH && (sr.Left < -Overlap || sr.Right > s.Bounds.Width + Overlap))
                    yield return (K("поле-вне-прокрутки-по-ширине", f), $"поле {Desc(f)} выходит из области прокрутки по горизонтали: {sr.Left:0}…{sr.Right:0} при ширине {s.Bounds.Width:0}");
                if (!canV && (sr.Top < -Overlap || sr.Bottom > s.Bounds.Height + Overlap))
                    yield return (K("поле-вне-прокрутки-по-высоте", f), $"поле {Desc(f)} выходит из области прокрутки по вертикали: {sr.Top:0}…{sr.Bottom:0} при высоте {s.Bounds.Height:0}");
            }
            if (r.Width < MinFieldWidth)
                yield return (K("поле-узкое", f), $"поле {Desc(f)} схлопнуто: ширина {r.Width:0.#} px (нужно не меньше {MinFieldWidth})");
        }
    }
}
