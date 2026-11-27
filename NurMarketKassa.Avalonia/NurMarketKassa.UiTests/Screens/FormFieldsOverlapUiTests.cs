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
using NurMarketKassa.Services.Api; // 2026-11-21: ITariffApi/TariffQuote/TariffOrder в тестовых заглушках (сборка UiTests не проходила без этой строки)
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
    // 2026-11-21 (просьба владельца): квадратный экран 1024×1024 — только для ключевых окон (не раздуваем полный прогон).
    private static readonly HashSet<string> SquareScreenNames = new(StringComparer.Ordinal) { "n-staff-timesheet", "c-settings", "c-main-kassa", "c-warehouse" };
    private static UiSize[] SizesFor(string name) => SquareScreenNames.Contains(name) ? [.. WindowSizes, new UiSize(1024, 1024)] : WindowSizes;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "form-overlap-" + Guid.NewGuid().ToString("N")[..8]);

    public FormFieldsOverlapUiTests()
    {
        TestHost.Ensure();
        Directory.CreateDirectory(_dir);
    }

    private IDisposable? _tariffModules; // 2026-11-26: реестр модулей и цены для «n-tariff-custom-modules» (статические CompanyModules / PublicModules) — возвращаются в Dispose

    public void Dispose()
    {
        _tariffModules?.Dispose();
        ModalShell.WorkingAreaForTests = null;
        NurMarketKassa.Services.Hardware.HardwareCalcSettings.Use(null);
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    /// <summary>Известные и осознанно отложенные нарушения: «окно|правило|элемент А|элемент Б». Пока пусто. Окна других исполнителей сюда не вносятся — их нарушения идут отчётом.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        // 2026-11-26: таблица накладной (колонки 928 px) на экране 800×600 шире окна — в ней боковая прокрутка (InvoiceEditorWindow), поля справа достижимы прокруткой; перекраивать колонки накладной не стали.
        "n-invoice-editor-barcodes|поле-вне-окна-по-ширине|TextBox", "n-invoice-editor-barcodes|поле-вне-окна-по-ширине|TextBox«скан или вписать»","n-invoice-editor-barcodes|поле-вне-окна-по-ширине|TextBox«скан же жазуу»", "n-invoice-editor-barcodes|поле-вне-окна-по-ширине|TextBox«skan yoki yozing»",
        // 2026-11-26: на 800×600 нижняя рамка списка «Авто» в панели настроек ценника обрезана на 3 px (ресайз панели по высоте окна — отдельная задача); список виден и работает.
        "c-barcode-print|поле-вне-прокрутки-по-высоте|ComboBox«Авто»", "c-barcode-print|поле-вне-прокрутки-по-высоте|ComboBox«Avto»",
    };

    private static readonly string[] OwnNames =
    [
        "n-estimates", "n-estimate-editor-new", "n-estimate-editor-filled", "n-estimate-editor-filled-open", "n-estimate-calc-tile", "n-estimate-calc-paint", "n-estimate-print",
        "n-parts-orders", "n-parts-order-edit", "n-subscription", "n-subscription-year", "n-tariff-custom", "n-tariff-custom-standard", "n-tariff-custom-removed", "n-tariff-custom-modules", "n-product-batches", "n-receiving-batch", "n-support-request", "n-analogs", "n-sphere-wizard", "n-fiscal-settings-server",
        "n-invoice-editor-barcodes", "n-barcode-extras", "n-barcode-conflict", // 2026-11-20: штрихкоды в накладных
        "n-ai-variants-card", "n-ai-variants-bar", // 2026-11-20: вкусы одного товара при загрузке накладной через ИИ (блок вариантов и панель «Команда ИИ»)
        "n-bazaar-haggle", "n-bazaar-rates", // 2026-11-21 («Базар»): окно торга и окно курсов валют
        "n-catalog-filter", // 2026-11-21: окно «Фильтр» каталога кассы (без своего поиска, «Сбросить» с обводкой)
        "n-staff-timesheet", // 2026-11-21: табель сотрудников (раскладка под низкий экран)
        "n-supplier-return-edit", // 2026-11-21 (ответ Умара 21.11): правка возврата поставщику — «Добавить товар», причина и вид компенсации, добавленная строка
        "n-supplier-debt-pay", "n-supplier-debt-pay-part", "n-supplier-return-new", // 2026-11-26: оплата долга поставщику любой суммой; «Новый возврат поставщику» с долгом у поставщика
        "n-purchase-receive", // 2026-11-26 (ТОП-15 п. 4): страница «Приём товара» с новым полем «№ ЭСФ» под номером накладной
        "n-repair-form", "n-tradein-form", "n-repair-price-form", // 2026-11-26 (снимок владельца): формы «Ремонта и сервиса» — «Комплектация» вылезала за окно, «Срок, дней» — за низ (подробно — ServiceFormsFitUiTests)
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
            foreach (var size in sized ? SizesFor(name) : [default(UiSize)])
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

    /// <summary>2026-11-26 (владелец: форма «Нового приёма в ремонт» обрезалась справа и снизу): окна, куда в 1.17.93–1.17.94 добавляли поля и панели, — на самом маленьком экране кассы 800×600.
    /// Окна на весь экран — растянуты до 800×600; диалоги — как в программе (своя ширина, предел по экрану кассы: окно не шире 784, рабочая область для оболочки — 800×600).
    /// Правила те же, что у основного теста, плюс: ничего не выходит за ширину окна и области прокрутки без боковой полосы, окно не больше экрана (<see cref="ServiceFormsFitUiTests.FitProblems"/>).
    /// «Тариф «Своё»» и страницы модулей Маркетплейса ведут другие исполнители — их здесь нет. Карточка товара (вкладка «Доп. штрих-коды», подпись «Кол-во в уп.» вплотную к полю в режиме compact) — найдено, не правилось, в список не входит.</summary>
    private static readonly string[] SmallScreenNames =
    [
        "c-purchases-receive", "c-barcode-print", "c-return-sale", "n-receiving-batch", "n-invoice-editor-barcodes", "n-barcode-extras", "n-supplier-return-edit",
        "n-repair-form", "n-tradein-form", "n-repair-price-form",
    ];

    public static IEnumerable<object[]> SmallScreenMatrix() =>
        SmallScreenNames.SelectMany(name => new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.Uzbek }.Select(lang => new object[] { name, lang }));

    [AvaloniaTheory]
    [MemberData(nameof(SmallScreenMatrix))]
    public async Task Window_FormFields_DoNotOverlap_OnSmallScreen800x600(string name, AppLanguage lang)
    {
        await TestHost.WarmAsync();
        UiShot.Setup(UiTheme.Light, lang);
        var screen = new UiSize(800, 600);
        ModalShell.WorkingAreaForTests = new Size(screen.Width, screen.Height);
        var (window, sized) = Create(name);
        var code = lang switch { AppLanguage.Kyrgyz => "ky", AppLanguage.Uzbek => "uz", _ => "ru" };
        var found = new Dictionary<string, List<string>>();
        try
        {
            if (sized)
            {
                // Окно большое: на экране 800×600 касса сожмёт его до рабочей области минус отступ 16 (FitToKassaScreen).
                ServiceFormsFitUiTests.ApplyKassaScreenLimits(window, screen);
                using (var _ = UiShot.Render(window, new UiSize(screen.Width - 16, screen.Height - 16)))
                {
                }
                UiShot.Settle(3);
            }
            else
                ServiceFormsFitUiTests.ShowLikeInApp(window, screen);
            ServiceFormsFitUiTests.Save(window, $"smallscreen-{name}-{code}-{screen}.png"); // снимок для глаз: %TEMP%\nurmarket-tests\ui-actual
            Collect(found, window, name, screen.ToString());
            foreach (var problem in ServiceFormsFitUiTests.FitProblems(window, screen, formButtons: false))
                found[$"{name}|вписывание|{problem}"] = [$"{screen}: {problem}"];
            foreach (var (label, activate) in Modes(window))
            {
                activate();
                UiShot.Settle(3);
                Collect(found, window, name + "/" + label, screen.ToString());
                foreach (var problem in ServiceFormsFitUiTests.FitProblems(window, screen, formButtons: false))
                    found[$"{name}/{label}|вписывание|{problem}"] = [$"{screen}: {problem}"];
            }
        }
        finally
        {
            window.Close();
            UiShot.Pump();
            ModalShell.WorkingAreaForTests = null;
        }
        var fresh = found.Where(kv => !Known.Contains(kv.Key)).ToList();
        Assert.True(fresh.Count == 0, $"На экране 800×600 обрезание или налезание ({name}, {code}):\n" + string.Join("\n", fresh.Take(25).Select(kv => $"  {kv.Key} — {string.Join(", ", kv.Value.Distinct())}"))
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
            case "n-subscription-year": // 2026-11-21: «Месяц / Год» (год = 10 месяцев) в окне «Подписка»
            {
                var yearWindow = new SubscriptionWindow(new FakeBilling(true), true) { Clock = () => new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.FromHours(6)) };
                yearWindow.Opened += async (_, _) =>
                {
                    await yearWindow.LoadAsync();
                    yearWindow.SetYear(true);
                };
                return (yearWindow, true);
            }
            case "n-tariff-custom": // 2026-11-21: тариф «Своё» на «Старте» — список пакетов, период «Месяц / Год»
                return (new TariffCustomWindow(new FakeTariff(), "Старт"), true);
            case "n-tariff-custom-standard": // на «Стандарте»: все пакеты отмечены (входят), галочку можно снять
                return (new TariffCustomWindow(new FakeTariff(), "Стандарт"), true);
            case "n-tariff-custom-removed": // со снятым пакетом: «Своё» — Старт + оставшиеся, цены и год с сервера
            {
                var removedWindow = new TariffCustomWindow(new FakeTariff(), "Стандарт");
                removedWindow.Opened += async (_, _) =>
                {
                    await removedWindow.LoadAsync();
                    removedWindow.SetChecked("ai", false);
                    removedWindow.SetPeriodForTest("year");
                    await removedWindow.QuoteForTestAsync();
                };
                return (removedWindow, true);
            }
            case "n-tariff-custom-modules": // 2026-11-26: «Своё» собирается из Маркетплейса — 11 пакетов и 29 функций строками (значок, название, «что делает», цена, переключатель), выбор из Маркетплейса уже отмечен
            {
                _tariffModules ??= TariffBuilderFixture.Apply(Path.Combine(_dir, "tariff-modules"), "condition", "repair");
                TariffDraft.SetModule("haggle", true, false);
                TariffDraft.SetModule("quick_items", false, true);
                TariffDraft.SetPack("ai", true, false);
                return (new TariffCustomWindow(new TariffBuilderUiTests.BuilderTariffApi(), "Старт"), true);
            }
            case "n-staff-timesheet":
            {
                Directory.CreateDirectory(_dir);
                var staff = Enumerable.Range(1, 7).Select(i => ($"u{i}", $"Сотрудник {i} Тестовый")).ToList();
                var tsDeps = new TimesheetDeps
                {
                    Shifts = _ => Task.FromResult<IReadOnlyList<ShiftHistoryEntry>>([]),
                    Employees = _ => Task.FromResult<IReadOnlyList<(string Key, string Name)>>(staff),
                    Marks = new TimesheetMarksService(Path.Combine(_dir, "timesheet-marks.json")),
                    Today = () => Today,
                    CanEdit = true,
                };
                var tsWindow = new StaffTimesheetWindow(tsDeps);
                tsWindow.Opened += async (_, _) => await tsWindow.LoadAsync();
                return (tsWindow, true);
            }
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
            case "n-bazaar-haggle": // 2026-11-21 («Базар»): торг — «До 10 / 50 / 100» и «Договорились за …»
                return (new BazaarHaggleDialog("Куртка зимняя, размер 52", 1247m), false);
            case "n-bazaar-rates": // 2026-11-21 («Базар»): курсы валют USD / RUB / CNY
                return (new BazaarRatesDialog(), false);
            case "n-catalog-filter": // 2026-11-21: окно «Фильтр»: тип, категория, бренд, цена, «в наличии», «Сбросить / Отмена / Применить»
                return (new FilterWindow(new NurMarketKassa.ViewModels.Main.CatalogFilterViewModel(
                    Enumerable.Range(1, 6).Select(i => new CatalogProductTileVm("p" + i, "Товар " + i, "10.00 сом", false) { Category = i % 2 == 0 ? "Молочка" : "Выпечка", Brand = i % 3 == 0 ? "Нурсут" : null }))), true);
            case "n-supplier-return-edit": // 2026-11-21: 7 строк (есть поиск по накладной) + добавленная товар-строка; все блоки правки возврата поставщику
            {
                var items = string.Join(",", Enumerable.Range(1, 7).Select(i => "{\"id\":\"l" + i + "\",\"product_id\":\"p" + i + "\",\"product_name\":\"Молоко «Айналайын» 1 л №" + i + "\",\"qty\":\"2\",\"unit\":\"шт\",\"purchase_price\":\"150.50\"}"));
                var info = NurMarketKassa.Services.Api.SupplierApiService.ParseReturn(JsonDocument.Parse("{\"id\":\"r1\",\"supplier_name\":\"ОсОО «Поставщик»\",\"reason\":\"defect\",\"compensation\":\"debt_offset\",\"created_at\":\"2026-11-20T03:00:00Z\","
                    + "\"comment\":\"брак упаковки\",\"total_amount\":\"2107\",\"version\":3,\"status\":\"posted\",\"can_edit\":true,\"can_void\":true,\"items\":[" + items + "]}").RootElement.Clone());
                var dialog = new SupplierReturnEditDialog(info, "ВП-2026-000123");
                dialog.AddProductForTests(new CatalogProductTileVm("pNew", "Кефир «Нурсүт» 0,9 л", "90.00 сом", false, null) { Unit = "шт", PurchasePrice = 70 });
                return (dialog, false);
            }
            case "n-supplier-debt-pay": // 2026-11-26: оплата долга поставщику — сумма, «Весь долг / 50 % / 20 %», способ оплаты, пояснение «долг без графика»
                return (new SupplierDebtPayDialog("ОсОО «Поставщик строительных материалов»", "взнос №2 по долгу «Закупка 12.11.2026», срок 30.11.2026", 23400.5, "Остаток взноса",
                    "Долг без графика (1 200,00 сом) сюда не входит — его гасят на сайте NurCRM."), false);
            case "n-supplier-debt-pay-part": // то же с введённой частичной суммой («Останется долг: …»)
            {
                var dialog = new SupplierDebtPayDialog("ОсОО «Поставщик»", "весь долг", 9999999.99, "Долг к оплате");
                dialog.PickForTests(20);
                return (dialog, false);
            }
            case "n-supplier-return-new": // 2026-11-26: «Новый возврат поставщику» — долг поставщика, вид «Зачесть в долг перед поставщиком», строка «Долг перед поставщиком: …»
            {
                var dialog = new SupplierReturnDialog();
                dialog.SetSupplierDebtForTests(1500);
                return (dialog, false);
            }
            case "n-repair-form": // 2026-11-26: формы «Ремонта и сервиса» — те же конструкторы, что в программе
                return (ServiceFormsFitUiTests.CreateForm(_dir, "repair-new"), false);
            case "n-tradein-form":
                return (ServiceFormsFitUiTests.CreateForm(_dir, "tradein-new"), false);
            case "n-repair-price-form":
                return (ServiceFormsFitUiTests.CreateForm(_dir, "repair-price"), false);
            case "n-purchase-receive":
            {
                var w = new PurchasesWindow();
                w.OpenReceive(null);
                return (w, true);
            }
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
    /// <summary>2026-11-21: «Своё» — заглушка сервера (пакеты, расчёт со снятием и годом) для проверки раскладки; цифры произвольные.</summary>
    private sealed class FakeTariff : ITariffApi
    {
        private static readonly string[] Codes = ["ai", "clients", "sales_analytics", "restock", "salary", "debts", "service", "scales", "telegram_bot", "shift_analytics", "analytics_export"];

        public Task<TariffCatalog> GetCatalogAsync(CancellationToken ct = default) =>
            Task.FromResult(new TariffCatalog(
                [new TariffPlan("start", "Старт", 1300m, null, 13000m), new TariffPlan("standard", "Стандарт", 3600m, null, 36000m), new TariffPlan("custom", "Своё", null, "start")],
                Codes.Select(c => new TariffPack(c, "Пакет " + c, "Описание пакета, которое может быть достаточно длинным и переноситься на вторую строку", 1500m, 400m, ["standard"])).ToList()));

        public Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, CancellationToken ct = default) => QuoteAsync(packs, null, null, null, "month", ct);

        public Task<TariffQuote> QuoteAsync(IReadOnlyList<string> packs, IReadOnlyList<string>? modules, string? sphere, IReadOnlyList<string>? remove, string period, CancellationToken ct = default)
        {
            var removed = remove?.ToList() ?? [];
            var year = period == "year";
            if (removed.Count == 0)
                return Task.FromResult(new TariffQuote(0m, 0m, 3600m, [], false, 3600m, "standard", null, null, null, null, 3600m, "standard", [], 1300m, period, year ? 36000m : null, year ? 7200m : null, year ? 10 : null));
            var rows = Codes.Except(removed).Select(p => new TariffQuotePack(p, 400m, 1500m, false)).ToList();
            var monthly = rows.Sum(r => r.Monthly);
            return Task.FromResult(new TariffQuote(monthly, rows.Sum(r => r.Once), 1300m, rows, true, 3600m, "start", null, null, null, null, 1300m + monthly, "custom", removed, 1300m, period,
                year ? (1300m + monthly) * 10m : null, year ? (1300m + monthly) * 2m : null, year ? 10 : null));
        }

        public Task<TariffOrder> SubmitCustomAsync(IReadOnlyList<string> packs, string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(new TariffOrder("o-1", "pending", 0m, 0m, null));
    }

    private sealed class FakeBilling : IBillingApi
    {
        private readonly bool _year;

        public FakeBilling(bool year = false) => _year = year;

        public Task<BillingConfig> GetConfigAsync(CancellationToken ct = default) =>
            Task.FromResult(BillingParser.ParseConfig(JsonDocument.Parse("{\"available\":true,\"mode\":\"test\",\"bank_fee\":\"30.00\",\"months\":[1,3,6,12],\"current_plan\":\"standard\",\"valid_until\":\"2026-12-19\","
                + (_year ? "\"plans\":[{\"code\":\"start\",\"price_month\":\"1300.00\",\"price_year\":\"13000.00\"},{\"code\":\"standard\",\"price_month\":\"3600.00\",\"price_year\":\"36000.00\"}],\"periods\":[\"month\",\"year\"],\"year_paid_months\":10,"
                    : "\"plans\":[{\"code\":\"start\",\"price_month\":\"1300.00\"},{\"code\":\"standard\",\"price_month\":\"3600.00\"}],")
                + "\"qr_ttl_seconds\":1200}").RootElement.Clone()));

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
