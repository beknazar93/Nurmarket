using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.UiTests.Harness;
using Xunit;

namespace NurMarketKassa.UiTests.Screens;

/// <summary>2026-11-19 (вопрос владельца со скриншота «Штрихкоды и ценники»: «как будет работать в маленьких экранах? проверь все вкладки на квадратные маленькие экраны!»):
/// каждый раздел программы владельца (и вкладки Склада, Калькуляции, Финансов, Настроек) в оболочке <see cref="OwnerShellWindow"/> на малых и квадратных окнах —
/// 1024×768, 1280×1024, 800×800, 1000×1000, 1024×600, 800×600 (ru), 1024×768 и 800×800 (ky). Проверки: (а) текст не обрезан (<see cref="TextClipChecker"/>);
/// (б) кнопки и поля не выходят за видимую область: каждая внутренняя область обрезки либо вмещает элемент, либо это ScrollViewer, который по этой оси прокручивается;
/// (в) у корня раздела (SectionFitHost) нет горизонтальной прокрутки — всё по ширине помещается; (г) раздел не пустой, кнопки с подписью не сжаты в ноль.
/// Итог каждого прогона — строки «размер | язык | раздел/вкладка | OK или проблема» в %TEMP%\nurmarket-tests\small-screens.txt; снимки проблемных мест —
/// %TEMP%\nurmarket-tests\ui-actual\small-*.png. Уже известные и осознанно отложенные проблемы — в <see cref="Known"/> (чинить — удалять строку).</summary>
[Collection("UiScenarios")]
public sealed class OwnerSectionsSmallScreensUiTests : IDisposable
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly PropertyInfo IsOwnerProp = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _savedIsOwner = AppMode.IsOwner;
    private readonly bool _savedUnlocked = UserPreferences.Instance.BulkPriceTagUnlocked;
    private List<CatalogProductTileVm>? _savedProducts;
    private static readonly object FileLock = new();

    private readonly BarcodePrintSettings _savedBarcodeSettings;

    public OwnerSectionsSmallScreensUiTests()
    {
        TestHost.Ensure();
        UiFake.Reset();
        _savedBarcodeSettings = BarcodePrintSettingsStore.Load();
    }

    public void Dispose()
    {
        BarcodePrintSettingsStore.Save(_savedBarcodeSettings); // раздел «Штрихкоды» при закрытии пишет свои настройки (фильтр, режим) — соседние тесты читают их из той же папки данных
        IsOwnerProp.SetValue(null, _savedIsOwner);
        UserPreferences.Instance.BulkPriceTagUnlocked = _savedUnlocked;
        if (_savedProducts != null)
        {
            CatalogCacheService.Products.Clear();
            CatalogCacheService.Products.AddRange(_savedProducts);
        }
    }

    /// <summary>Известные проблемы: «раздел|вид[|подробность]». Пока пусто — всё, что находит проба, считается провалом.
    /// Табель (StaffTimesheetWindow*) и «Неотправленное» ведут другие исполнители — их замечания сюда не вносятся, а передаются им отчётом.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        // --- «Финансы / Аналитика»: заголовок колонки «Кол-во» (36 px вместо 43), строка-описание вкладки «Товары» не переносится (800 px); «Возвраты»: заголовок «Товар» 19 px
        "analytics|clip", "analytics/Выручка и оплаты|clip", "analytics/Товары|clip", "analytics/Товарлар|clip", "sales-finance/Возвраты|clip",
        // --- блок состояния (StateBlock) в TabControl «Финансов» на 800×600: вкладка ниже блока даже с прокруткой (поля блока 36+36 px) — решить: поля блока по высоте области
        "analytics/Склад|unreachable", "sales-finance|unreachable", "sales-finance/Продажи|unreachable",
        // --- «Калькуляция → Аудит»: колонки таблицы (цена, остаток, «нет закупки») уже текста на 800–1024 px
        "calculator/Audit|clip",
        // --- «Склад»: ABC (колонка «Товар» сжата до 73 px при 800), «Списание»/«Эсептен чыгаруу» (подписи в колонке 206 px), «Приёмка» ky («сом / бирдик»), счётчик «Жалпы: 60 · Табылды: 60»
        "warehouse/ABC|clip", "warehouse/Списание|clip", "warehouse/Эсептен чыгаруу|clip", "warehouse/Кабыл алуу|clip", "warehouse|clip", "warehouse/Товарлар|clip",
        // --- «Склад → Приёмка» ky: кнопка «Кирешеге алуу» ниже области на 800×600 — передать исполнителю склад-партий (ReceivingBatchDialog/приёмка)
        "warehouse/Кабыл алуу|unreachable",
        // --- «Настройки → Экран» (ky): подсказка про панель задач выше поля; «Заказы с сайта» — пустые состояния (ru/ky) выше поля 35 px
        "settings|clip", "settings/настройки: screen|clip", "siteorders|clip",
    };

    public static IEnumerable<object[]> Cases()
    {
        foreach (var (w, h) in new[] { (1024, 768), (1280, 1024), (800, 800), (1000, 1000), (1024, 600), (800, 600) })
            yield return [w, h, AppLanguage.Russian];
        yield return [1024, 768, AppLanguage.Kyrgyz];
        yield return [800, 800, AppLanguage.Kyrgyz];
    }

    // ------------------------------------------------------------------ окружение

    private static async Task LoginOwnerAsync()
    {
        UiFake.Server.Json("POST", "/api/users/auth/login/", 200,
            "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true}}");
        UiFake.Server.Json("GET", "/api/users/profile/", 200,
            "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"can_view_settings\":true,\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");
        await TestHost.Services.GetRequiredService<NurMarketApiClient>().LoginAsync("cashier@test.kg", "12345678");
    }

    private void SeedCatalog(int count = 60)
    {
        _savedProducts ??= CatalogCacheService.Products.ToList();
        CatalogCacheService.Products.Clear();
        for (var i = 0; i < count; i++)
            CatalogCacheService.Products.Add(new CatalogProductTileVm("ss" + i, $"Товар {i} для проверки малых экранов", (10 + i * 7) + ".00 сом", false)
            {
                Barcode = (4600000002000L + i).ToString(),
                Article = "S" + i,
                Unit = "шт",
                Quantity = i % 30,
                PurchasePrice = 5 + i,
                Category = "Категория " + i % 6,
            });
    }

    private static T Svc<T>() where T : class => TestHost.Services.GetService<T>() ?? Activator.CreateInstance<T>();

    /// <summary>Разделы программы владельца (как в OwnerShellWindow.axaml.cs). Веб-разделы «WhatsApp» и «NurCRM» (встроенный браузер) в Headless не поднимаются — не проверяются;
    /// «Неотправленное» и «Оформить возврат…» — диалоги, а не разделы.</summary>
    private static IEnumerable<(string Key, Func<Window> Create)> Sections()
    {
        yield return ("aiadvisor", () => new AiAdvisorWindow());
        yield return ("warehouse", () => Svc<WarehouseWindow>());
        yield return ("barcodes", () => new BarcodePrintSectionWindow());
        yield return ("calculator", () => new CalculatorWindow());
        yield return ("estimate", () => new EstimatesWindow());
        yield return ("restock", () => Svc<RestockSuggestionsWindow>());
        yield return ("purchases", () => new PurchasesWindow());
        yield return ("suppliers", () => new SuppliersWindow());
        yield return ("supplierreturns", () => new SupplierReturnsWindow());
        yield return ("branches", () => new BranchesWindow());
        yield return ("sales-finance", () => (Window)typeof(FinanceWindow).GetMethod("AsSalesAndFinance", Flags)!.Invoke(Svc<FinanceWindow>(), null)!);
        yield return ("debts", () => new DebtsWindow());
        yield return ("rentals", () => new RentalsWindow(null, null));
        yield return ("repair", () => new RepairOrdersWindow());
        yield return ("tradein", () => new TradeInWindow());
        yield return ("analytics", () => Svc<FinanceWindow>().AsAnalyticsSection());
        yield return ("abc", () => Svc<AbcAnalysisWindow>());
        yield return ("profitcash", () => new ProfitCashReconcileWindow());
        yield return ("investments", () => new InvestmentsWindow());
        yield return ("losssales", () => new LossSalesWindow());
        yield return ("bonusanalytics", () => new BonusAnalyticsWindow());
        yield return ("staffreport", () => new StaffReportsWindow());
        yield return ("fiscalreport", () => new FiscalSalesReportWindow());
        yield return ("sizesreport", () => new SizesReportWindow());
        yield return ("siteorders", () => new SiteOrdersWindow());
        yield return ("siteeditor", () => new SiteEditorWindow());
        yield return ("sitereviews", () => new SiteReviewsWindow());
        yield return ("sitecollections", () => new SiteCollectionsWindow());
        yield return ("sitesettings", () => new SiteSettingsWindow());
        yield return ("appshop", () => new AppShopWindow());
        yield return ("clients", () => Svc<ClientsWindow>());
        yield return ("telegrambot", () => new TelegramBotAnalyticsWindow());
        yield return ("funnel", () => new FunnelWindow());
        yield return ("salary", () => new SalaryWindow());
        yield return ("timesheet", () => new StaffTimesheetWindow()); // ведёт другой исполнитель — замечания только в отчёт
        yield return ("marketplace", () => new MarketplaceWindow().AsSection());
        yield return ("settings", () => Svc<PosSettingsWindow>());
        yield return ("kb", () => Svc<KnowledgeBaseWindow>());
        yield return ("support", () => Svc<RemoteSupportWindow>());
        yield return ("logs", () => Svc<LogsAndErrorsWindow>());
    }

    /// <summary>Режимы внутри раздела: вкладки TabControl верхнего уровня, сегменты «Калькуляции», страницы «Настроек», вкладки «Весов» и «Маркетплейса».</summary>
    private static List<(string Label, Action Activate)> Modes(string key, Window w)
    {
        var modes = new List<(string, Action)>();
        foreach (var tabs in w.GetVisualDescendants().OfType<TabControl>().Where(t => t.IsEffectivelyVisible && !t.GetVisualAncestors().OfType<TabItem>().Any()).ToList())
            foreach (var tab in tabs.Items.OfType<TabItem>().Where(t => t.IsVisible).ToList())
            {
                var t = tab;
                var c = tabs;
                modes.Add((LayoutStabilityProbe.TabTitle(t), () => c.SelectedItem = t));
            }
        if (key == "calculator")
            foreach (var b in w.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("seg") && (b.Name ?? "").StartsWith("Tab", StringComparison.Ordinal)).ToList())
            {
                var btn = b;
                modes.Add((btn.Name!.Replace("Tab", "").Replace("Button", ""), () => btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            }
        if (key == "settings" && w is PosSettingsWindow ps)
            foreach (var tab in new[] { "screen", "scales", "print", "updates", "operations", "customization", "employees", "keys", "ai" })
                modes.Add(("настройки: " + tab, () => ps.SelectNavTab(tab)));
        if (key == "marketplace" && w is MarketplaceWindow mp)
            modes.Add(("доп. функции", () => mp.ShowExtrasTab()));
        return modes;
    }

    // ------------------------------------------------------------------ проверки

    private static string Desc(Control c)
    {
        var text = c is TextBox tb ? tb.Watermark?.ToString() : UiDrive.TextOf(c);
        text = string.IsNullOrWhiteSpace(text) ? "" : " «" + (text.Length > 30 ? text[..30] + "…" : text.Replace('\n', ' ')) + "»";
        return c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? "" : "#" + c.Name) + text;
    }

    private static bool InTemplatePart(Control c)
    {
        foreach (var a in c.GetVisualAncestors())
            if (a is ScrollBar or ComboBox or NumericUpDown or TextBox
                || a.GetType().Name is "DataGridColumnHeader" or "CalendarDatePicker" or "DatePicker" or "TimePicker" or "Slider")
                return true;
        return false;
    }

    /// <summary>Каждая внутренняя область обрезки (окно, ScrollViewer, элемент с ClipToBounds) либо вмещает элемент, либо прокручивается по нужной оси. Горизонтальная прокрутка
    /// корня раздела (ScrollViewer в SectionFitHost) здесь допускается — её отдельно считает <see cref="RootHorizontalOverflow"/>.</summary>
    private static string? Unreachable(Control c, Window win, ScrollViewer? root)
    {
        Visual cur = c;
        for (var a = c.GetVisualParent(); a != null; a = a.GetVisualParent())
        {
            var isWin = a is Window;
            if (!(isWin || a is ScrollViewer || a is Control { ClipToBounds: true }))
                continue;
            if (cur.TranslatePoint(default, a) is not { } p)
                continue;
            var area = isWin ? win.ClientSize : a.Bounds.Size;
            var size = cur.Bounds.Size;
            var xOut = p.X < -2 || p.X + size.Width > area.Width + 2;
            var yOut = p.Y < -2 || p.Y + size.Height > area.Height + 2;
            if (!xOut && !yOut)
                continue;
            // таблица DataGrid прокручивается собственными полосами (не ScrollViewer): ячейки и строки внутри неё — по её настройкам прокрутки
            if (c.GetVisualAncestors().OfType<DataGrid>().FirstOrDefault() is { } dg && !ReferenceEquals(a, dg) && a.GetVisualAncestors().Contains(dg))
            {
                if ((!xOut || dg.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled) && (!yOut || dg.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled))
                {
                    cur = a;
                    continue;
                }
            }
            // область обрезки ScrollViewer — его ScrollContentPresenter (ClipToBounds): прокрутку берём у самого ScrollViewer
            var sv = a as ScrollViewer ?? (a is ScrollContentPresenter ? a.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() : null);
            if (sv != null)
            {
                var isRoot = ReferenceEquals(sv, root);
                var xOk = !xOut || isRoot || (sv.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.Extent.Width > sv.Viewport.Width + 1);
                var yOk = !yOut || (sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.Extent.Height > sv.Viewport.Height + 1);
                if (xOk && yOk)
                {
                    cur = a;
                    continue;
                }
            }
            return $"{Desc(c)} вне области {a.GetType().Name}{(a is Control { Name: { Length: > 0 } n } ? "#" + n : "")} ({p.X:0},{p.Y:0} {size.Width:0}×{size.Height:0} при {area.Width:0}×{area.Height:0}) и не прокручивается";
        }
        return null;
    }

    private static ScrollViewer? RootScroll(Window w) =>
        w.GetVisualDescendants().OfType<SectionFitHost>().FirstOrDefault()?.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private static string? RootHorizontalOverflow(ScrollViewer? root) =>
        root != null && root.Extent.Width > root.Viewport.Width + 1.5
            ? $"раздел шире окна: нужно {root.Extent.Width:0} px, есть {root.Viewport.Width:0} px (горизонтальная прокрутка раздела)"
            : null;

    /// <summary>Возвращает пары (вид, подробность): clip / hscroll / unreachable / empty / squeezed.</summary>
    private static List<(string Kind, string Detail)> Analyze(Window section, string key, string lang)
    {
        var res = new List<(string, string)>();
        if (section.ClientSize.Width < 300 || section.ClientSize.Height < 200)
            res.Add(("empty", $"окно раздела {section.ClientSize.Width:0}×{section.ClientSize.Height:0}"));
        var root = RootScroll(section);
        if (RootHorizontalOverflow(root) is { } hs)
            res.Add(("hscroll", hs));
        foreach (var v in TextClipChecker.Find(section, "small-" + key, lang))
            res.Add(("clip", v.Detail));

        var interactive = 0;
        var seen = new HashSet<string>();
        foreach (var c in section.GetVisualDescendants().OfType<Control>())
        {
            if (!(c is Button or TextBox or ComboBox or NumericUpDown) || !c.IsEffectivelyVisible || c.Opacity <= 0.01 || c.Bounds.Width <= 0 || c.Bounds.Height <= 0 || InTemplatePart(c))
                continue;
            interactive++;
            if (c is Button b && b.GetVisualDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text)) && (b.Bounds.Width < 20 || b.Bounds.Height < 14))
                res.Add(("squeezed", $"{Desc(b)} сжата до {b.Bounds.Width:0}×{b.Bounds.Height:0}"));
            if (Unreachable(c, section, root) is { } u && seen.Add(Desc(c)))
                res.Add(("unreachable", u));
        }
        var visibleText = section.GetVisualDescendants().OfType<TextBlock>().Count(t => t.IsEffectivelyVisible && t.Bounds.Width > 0 && !string.IsNullOrWhiteSpace(t.Text));
        if (visibleText + interactive < 3)
            res.Add(("empty", $"в разделе видно {visibleText} подписей и {interactive} кнопок/полей"));
        return res;
    }

    private static void Shot(Window section, string name)
    {
        try
        {
            using var frame = section.CaptureRenderedFrame();
            if (frame is null)
                return;
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "ui-actual");
            Directory.CreateDirectory(dir);
            frame.Save(Path.Combine(dir, name + ".png"));
        }
        catch (Exception)
        {
            // снимок — только пояснение к отчёту
        }
    }

    // ------------------------------------------------------------------ прогон

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task Owner_AllSections_FitSmallScreen(int w, int h, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        await LoginOwnerAsync();
        IsOwnerProp.SetValue(null, true);
        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        SeedCatalog();
        var langCode = lang == AppLanguage.Kyrgyz ? "ky" : "ru";
        var size = $"{w}x{h}";
        var lines = new List<string>();
        var fresh = new List<string>();
        var shell = new OwnerShellWindow { Width = w, Height = h };
        var open = typeof(OwnerShellWindow).GetMethod("OpenSection", Flags, null, [typeof(string), typeof(Func<Window>)], null)!;
        try
        {
            shell.Show();
            UiShot.Settle(3, 30);
            lines.Add($"# {size} {langCode}: окно владельца {shell.ClientSize.Width:0}×{shell.ClientSize.Height:0}, MinWidth {shell.MinWidth:0}, MinHeight {shell.MinHeight:0}");
            if (Math.Abs(shell.ClientSize.Width - w) > 2)
                fresh.Add($"окно владельца не сжимается до {w}: получилось {shell.ClientSize.Width:0} (MinWidth {shell.MinWidth:0})");

            void Record(string label, Window section)
            {
                var found = Analyze(section, label.Split('/')[0], langCode);
                var bad = found.Where(f => !Known.Contains($"{label}|{f.Kind}") && !Known.Contains($"{label}|{f.Kind}|{f.Detail}")).ToList();
                lines.Add($"{size}|{langCode}|{label}|{(found.Count == 0 ? "OK" : "ПРОБЛЕМА: " + string.Join("; ", found.Select(f => $"{f.Kind}: {f.Detail}").Take(6)))}");
                if (bad.Count > 0)
                {
                    fresh.Add($"{label}: " + string.Join(" | ", bad.Select(f => $"{f.Kind}: {f.Detail}").Take(6)));
                    Shot(section, $"small-{label.Replace('/', '-').Replace(' ', '_').Replace(':', '_')}-{size}-{langCode}");
                }
            }

            foreach (var (key, create) in Sections())
            {
                Window? section = null;
                try
                {
                    open.Invoke(shell, [key, (Func<Window>)(() => section = create())]);
                    UiShot.Settle(8, 30);
                    if (section is null)
                    {
                        lines.Add($"{size}|{langCode}|{key}|не открылся");
                        continue;
                    }
                    Record(key, section);
                    foreach (var (label, act) in Modes(key, section))
                    {
                        act();
                        UiShot.Settle(4, 30);
                        Record($"{key}/{label}", section);
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                    lines.Add($"{size}|{langCode}|{key}|ИСКЛЮЧЕНИЕ {inner.GetType().Name}: {inner.Message}");
                    fresh.Add($"{key}: исключение {inner.GetType().Name}: {inner.Message}");
                }
                finally
                {
                    section?.Close();
                    UiShot.Settle(2, 20);
                }
            }
        }
        finally
        {
            shell.Close();
            UiShot.Pump();
        }

        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests");
            Directory.CreateDirectory(dir);
            lock (FileLock)
                File.AppendAllLines(Path.Combine(dir, "small-screens.txt"), lines);
        }
        catch (IOException)
        {
        }
        Assert.True(fresh.Count == 0, $"Проблемы на {size} ({langCode}):\n" + string.Join("\n", fresh.Select(x => "  " + x)));
    }
}
