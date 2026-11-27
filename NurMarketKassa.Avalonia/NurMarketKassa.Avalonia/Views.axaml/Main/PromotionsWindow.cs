using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-27 (ответ Умара «Конструктор акций Ф1», ТЗ «конструктор акций» раздел 1): раздел «Акции» программы владельца. Список акций по вкладкам
/// (Активные / Запланированные / Черновики / На паузе / Завершённые) с видом понятными словами, периодом, днями и часами, статусом и «сработала: N чеков, скидка X»
/// (promotions/report/), действия у строки (Запустить, Пауза / Возобновить, Завершить, Изменить, Удалить черновик) и мастер «Новая акция / Изменить акцию»
/// из 6 шагов (<c>PromotionsWindow.Wizard.cs</c>, <c>PromotionsWindow.Preview.cs</c>). Виден только при признаке сервера promotions_v2 и роли владелец/админ
/// (<see cref="PromotionsAccess"/>). Расчёт акций в кассе ведёт другой исполнитель (PromotionsV2/*) — здесь только управление акциями.</summary>
public sealed partial class PromotionsWindow : Window, IOwnerSection
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Для тестов: свой API (фейковый сервер) вместо настоящего.</summary>
    public static Func<IPromotionsApi>? ApiFactoryForTests { get; set; }
    /// <summary>Для тестов: ответ на вопрос «Завершить / Удалить / Выйти из мастера» вместо модального окна.</summary>
    public static Func<string, Task<bool>>? ConfirmForTests { get; set; }
    /// <summary>Для тестов: роль «владелец» (галочка «Разрешить продажу ниже закупки»).</summary>
    public static bool? IsOwnerForTests { get; set; }
    /// <summary>Для тестов: «сейчас» (вкладка «Запланированные» зависит от времени).</summary>
    public static Func<DateTimeOffset>? NowForTests { get; set; }

    private static DateTimeOffset Now => NowForTests?.Invoke() ?? DateTimeOffset.Now;

    private enum Tab { Active, Scheduled, Draft, Paused, Finished }

    private readonly IPromotionsApi _api;
    private readonly bool _isOwner;
    private readonly CancellationTokenSource _cts = new();
    private readonly Grid _root;

    // ── Список ──
    private readonly Grid _listView = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
    private readonly WrapPanel _tabsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<Tab, Button> _tabButtons = new();
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly Button _refresh;
    private readonly Button _newButton;

    private List<Promotion> _all = new();
    private Dictionary<string, PromotionReportRow> _report = new(StringComparer.OrdinalIgnoreCase);
    private Tab _tab = Tab.Active;
    private bool _loaded;
    private bool _busy;
    private int _loadGen;

    public PromotionsWindow() : this(null, null)
    {
    }

    public PromotionsWindow(IPromotionsApi? api, bool? isOwner)
    {
        Title = T("Акции", "Акциялар", "Promotions", "Kampanyalar", "Aksiyalar");
        Width = 1100;
        Height = 760;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        _api = api ?? ApiFactoryForTests?.Invoke() ?? new PromotionsApi(App.GetRequiredService<NurMarketApiClient>(), App.GetRequiredService<ICatalogApiService>());
        _isOwner = isOwner ?? IsOwnerForTests ?? DetectOwner();

        // ── Верх: заголовок раздела, «Обновить», «Новая акция» ──
        _refresh = Small(UiKit.Ghost(this, T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash")));
        AutomationProperties.SetName(_refresh, (string)_refresh.Content!);
        _refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        _newButton = Small(UiKit.Primary(this, T("Новая акция", "Жаңы акция", "New promotion", "Yeni kampanya", "Yangi aksiya")));
        AutomationProperties.SetName(_newButton, (string)_newButton.Content!);
        _newButton.Click += (_, _) => OpenWizard(null);
        var intro = new TextBlock
        {
            Text = T("Скидки, которые касса считает сама: «Купи 2 — получи 1», процент, сумма или цена по акции — на товары, категории и бренды, по дням и часам.",
                "Касса өзү эсептеген арзандатуулар: «2 сатып ал — 1 ал», пайыз, сумма же акциялык баа — товарларга, категорияларга жана бренддерге, күн жана саат боюнча.",
                "Discounts the till calculates itself: “Buy 2, get 1”, percent, amount or promo price — on products, categories and brands, by days and hours.",
                "Kasanın kendisi hesapladığı indirimler: «2 al 1 kazan», yüzde, tutar veya kampanya fiyatı — ürünlere, kategorilere ve markalara, günlere ve saatlere göre.",
                "Kassa o'zi hisoblaydigan chegirmalar: «2 ta ol — 1 ta ol», foiz, summa yoki aksiya narxi — mahsulotlar, toifalar va brendlarga, kun va soat bo'yicha."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Use(intro, TextBlock.ForegroundProperty, "BrushTextSoft");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_newButton);
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(intro);
        Grid.SetColumn(buttons, 1);
        top.Children.Add(buttons);

        // ── Вкладки ──
        foreach (var tab in new[] { Tab.Active, Tab.Scheduled, Tab.Draft, Tab.Paused, Tab.Finished })
        {
            var t = tab;
            var b = UiKit.Seg(TabTitle(tab), tab == _tab);
            b.Margin = new Thickness(0, 0, 0, 2);
            b.Click += (_, _) =>
            {
                _tab = t;
                RenderList();
            };
            _tabButtons[tab] = b;
            _tabsPanel.Children.Add(b);
        }
        var tabs = UiKit.Segment(_tabsPanel);
        tabs.HorizontalAlignment = HorizontalAlignment.Left;

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        AddRow(_listView, top, 0);
        AddRow(_listView, tabs, 1);
        AddRow(_listView, _status, 2);
        AddRow(_listView, scroll, 3);

        BuildWizardFrame();

        _root = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        _root.Children.Add(_listView);
        _root.Children.Add(_wizardView);
        _wizardView.IsVisible = false;
        Content = _root;
        NurMarketKassa.AvaloniaHost.Services.WindowChrome.Apply(this);

        Closed += (_, _) => _cts.Cancel();
        Opened += async (_, _) =>
        {
            if (!_loaded)
                await LoadAsync().ConfigureAwait(true);
        };
        SetStatus(T("Загружаю акции…", "Акцияларды жүктөп жатам…", "Loading promotions…", "Kampanyalar yükleniyor…", "Aksiyalar yuklanmoqda…"), "BrushTextSoft");
    }

    /// <summary>Вызывается программой владельца один раз — сразу после создания окна, до показа.</summary>
    public void AsOwnerSection() => _root.Margin = OwnerSectionLayout.Margin;

    /// <summary>Для тестов: открыть мастер (новая акция — null) и показать шаг; вид и «все товары / выбранные» — для проверки раскладки каждого варианта.</summary>
    public void OpenWizardForTests(Promotion? existing) => OpenWizard(existing);

    public void ShowStepForTests(int step) => GoToStep(step, validate: false);

    public void SetKindForTests(string kind)
    {
        if (_draft.KindLocked)
            return;
        ReadParams(out _, out _);
        _draft.Kind = kind;
        RefreshKindUi();
    }

    public void SetScopeAllForTests(bool all) => SetScopeAll(all);

    public int CurrentStepForTests => _step;

    public string WizardStatusForTests => _wizardStatus.Text ?? "";

    public bool WizardOpenForTests => _wizardView.IsVisible;

    /// <summary>Мастер открыт — несохранённые правки есть (оболочка не закроет спрятанный раздел ради памяти).</summary>
    public bool? HasUnsavedChanges => _wizardView.IsVisible;

    /// <summary>Пункт меню «Акции»: признак сервера promotions_v2, роль владелец/админ, не автономный вход.</summary>
    public static bool CanOffer()
    {
        try
        {
            if (App.GetRequiredService<NurMarketKassa.Core.Contracts.IAutonomousAuthService>().IsCurrentSessionAutonomous)
                return false;
            return PromotionsAccess.ShouldOffer(PromotionsAccess.FeatureOn(),
                PermissionService.IsOwnerOrAdmin(App.GetRequiredService<IAuthApiService>().UserPayload));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool DetectOwner()
    {
        try
        {
            return PromotionsAccess.IsOwner(App.GetRequiredService<IAuthApiService>().UserPayload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static string TabTitle(Tab tab) => tab switch
    {
        Tab.Active => T("Активные", "Активдүү", "Active", "Aktif", "Faol"),
        Tab.Scheduled => T("Запланированные", "Пландалган", "Scheduled", "Planlanan", "Rejalashtirilgan"),
        Tab.Draft => T("Черновики", "Каралама", "Drafts", "Taslaklar", "Qoralamalar"),
        Tab.Paused => T("На паузе", "Тыныгууда", "Paused", "Duraklatılan", "To'xtatilgan"),
        _ => T("Завершённые", "Аяктагандар", "Finished", "Bitenler", "Yakunlangan"),
    };

    private static Tab TabOf(Promotion p, DateTimeOffset now) => p.Status switch
    {
        Promotion.StatusDraft => Tab.Draft,
        Promotion.StatusPaused => Tab.Paused,
        Promotion.StatusFinished => Tab.Finished,
        _ => p.IsScheduled(now) ? Tab.Scheduled : Tab.Active,
    };

    // ── Загрузка ──────────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        var gen = ++_loadGen;
        _refresh.IsEnabled = false;
        SetStatus(T("Загружаю акции…", "Акцияларды жүктөп жатам…", "Loading promotions…", "Kampanyalar yükleniyor…", "Aksiyalar yuklanmoqda…"), "BrushTextSoft");
        try
        {
            var list = await _api.ListAsync(ct: _cts.Token).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            _all = list.Items.ToList();
            _loaded = true;
            SetStatus("", "BrushTextSoft");
            if (_tab == Tab.Active && !_all.Any(p => TabOf(p, Now) == Tab.Active))
                _tab = _all.Select(p => TabOf(p, Now)).OrderBy(t => t).Cast<Tab?>().FirstOrDefault() ?? Tab.Active; // открыть вкладку, где что-то есть
            RenderList();
            _ = LoadReportAsync(gen);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (gen != _loadGen)
                return;
            PosLogger.Log($"Акции: список не загружен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetStatus(PromotionErrors.Describe(ex).Text, "BrushDanger");
            RenderList();
        }
        finally
        {
            if (gen == _loadGen)
                _refresh.IsEnabled = true;
        }
    }

    /// <summary>«Сработала: N чеков, скидка X» — один запрос отчёта с самой ранней даты начала (не раньше года назад; без дат — 30 дней) до сегодня.</summary>
    private async Task LoadReportAsync(int gen)
    {
        var today = Now.ToOffset(PromotionsApi.CompanyOffset).Date;
        var from = _all.Where(p => p.StartsAt != null).Select(p => PromotionsApi.Local(p.StartsAt!.Value).Date).DefaultIfEmpty(today.AddDays(-30)).Min();
        if (from > today.AddDays(-30) && !_all.Any(p => p.StartsAt != null))
            from = today.AddDays(-30);
        if (from < today.AddDays(-365))
            from = today.AddDays(-365);
        if (from > today)
            from = today;
        try
        {
            var rows = await _api.ReportAsync(from, today, null, _cts.Token).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            _report = rows.Where(r => r.PromotionId.Length > 0).GroupBy(r => r.PromotionId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            RenderList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Акции: отчёт promotions/report/ не загружен ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    // ── Список ────────────────────────────────────────────────────────────────────

    private void RenderList()
    {
        var now = Now;
        foreach (var (tab, button) in _tabButtons)
        {
            var count = _all.Count(p => TabOf(p, now) == tab);
            var text = TabTitle(tab) + (count > 0 ? " (" + count + ")" : "");
            button.Content = text;
            AutomationProperties.SetName(button, text);
            UiKit.SetActive(button, tab == _tab);
            button.Classes.Set("active", tab == _tab);
        }

        _list.Children.Clear();
        if (!_loaded)
            return;
        if (_all.Count == 0)
        {
            _list.Children.Add(EmptyState());
            return;
        }
        var shown = _all.Where(p => TabOf(p, now) == _tab)
            .OrderByDescending(p => p.Priority).ThenByDescending(p => p.UpdatedAt ?? p.CreatedAt ?? DateTimeOffset.MinValue).ToList();
        if (shown.Count == 0)
        {
            _list.Children.Add(Note(T("В этой вкладке пусто.", "Бул өтмөктө бош.", "Nothing in this tab.", "Bu sekmede bir şey yok.", "Bu yorliqda hech narsa yo'q.")));
            return;
        }
        foreach (var p in shown)
            _list.Children.Add(Row(p, now));
    }

    private Control EmptyState()
    {
        var title = new TextBlock
        {
            Text = T("Акций пока нет", "Азырынча акциялар жок", "No promotions yet", "Henüz kampanya yok", "Hozircha aksiyalar yo'q"),
            FontSize = 17, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var text = Note(T("Создайте первую: например, «Купи 2 — получи 1» на воду или −15 % на молочку по утрам. Касса посчитает скидку сама, а здесь будет видно, сколько чеков она дала.",
            "Биринчисин түзүңүз: мисалы, сууга «2 сатып ал — 1 ал» же эртең менен сүт азыктарына −15 %. Касса арзандатууну өзү эсептейт, бул жерде канча чек бергени көрүнөт.",
            "Create the first one: e.g. “Buy 2, get 1” on water or −15 % on dairy in the mornings. The till calculates the discount itself, and here you'll see how many receipts it gave.",
            "İlkini oluşturun: örneğin suya «2 al 1 kazan» veya sabahları süt ürünlerine −%15. Kasa indirimi kendisi hesaplar, burada kaç fiş verdiği görünür.",
            "Birinchisini yarating: masalan, suvga «2 ta ol — 1 ta ol» yoki ertalab sut mahsulotlariga −15 %. Kassa chegirmani o'zi hisoblaydi, bu yerda nechta chek berganini ko'rasiz."));
        text.Margin = new Thickness(0, 8, 0, 14);
        var create = UiKit.Primary(this, T("Новая акция", "Жаңы акция", "New promotion", "Yeni kampanya", "Yangi aksiya"));
        create.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetName(create, (string)create.Content! + " (" + T("пустой список", "бош тизме", "empty list", "boş liste", "bo'sh ro'yxat") + ")");
        create.Click += (_, _) => OpenWizard(null);
        return new StackPanel { MaxWidth = 520, Margin = new Thickness(16, 48, 16, 0), HorizontalAlignment = HorizontalAlignment.Center, Children = { title, text, create } };
    }

    private Control Row(Promotion p, DateTimeOffset now)
    {
        var title = new TextBlock { Text = p.Title.Length > 0 ? p.Title : T("(без названия)", "(аталышы жок)", "(untitled)", "(adsız)", "(nomsiz)"), FontSize = 15.5, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var kind = new TextBlock { Text = PromotionText.KindLabel(p), FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        Use(kind, TextBlock.ForegroundProperty, "BrushAccentText");
        var when = Note2(PromotionText.Period(p) + " · " + PromotionText.DaysHours(p));
        var usage = Note2(_report.TryGetValue(p.Id, out var r) ? PromotionText.Usage(r) : p.IsDraft ? "" : PromotionText.Usage(null));
        usage.IsVisible = !string.IsNullOrEmpty(usage.Text);
        var left = new StackPanel { Spacing = 2, Children = { title, kind, when, usage } };
        if (p.Description.Length > 0)
            left.Children.Insert(1, Note2(p.Description));

        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 4), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
        var pillText = new TextBlock { Text = PromotionText.StatusLabel(p, now), FontSize = 12.5, FontWeight = FontWeight.SemiBold };
        var (bg, fg) = p.Status switch
        {
            Promotion.StatusActive when p.IsScheduled(now) => ("BrushAccentSoft", "BrushAccentText"),
            Promotion.StatusActive => ("BrushSuccessSoft", "BrushSuccess"),
            Promotion.StatusPaused => ("BrushWarningSoft", "BrushWarning"),
            _ => ("BrushSurfaceSubtle", "BrushTextSoft"),
        };
        Use(pill, Border.BackgroundProperty, bg);
        Use(pillText, TextBlock.ForegroundProperty, fg);
        pill.Child = pillText;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(left);
        Grid.SetColumn(pill, 1);
        head.Children.Add(pill);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        void Act(Button b, Func<Task> run)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            AutomationProperties.SetName(b, (string)b.Content! + ": " + p.Title);
            b.Click += async (_, _) =>
            {
                if (_busy)
                    return;
                await run().ConfigureAwait(true);
            };
            actions.Children.Add(b);
        }
        if (p.IsDraft)
            Act(Small(UiKit.Primary(this, T("Запустить", "Иштетүү", "Start", "Başlat", "Boshlash"))), () => ChangeStatusAsync(p, "start"));
        if (p.IsActive)
            Act(Small(UiKit.Ghost(this, T("Пауза", "Тыныгуу", "Pause", "Duraklat", "To'xtatish"))), () => ChangeStatusAsync(p, "pause"));
        if (p.IsPaused)
            Act(Small(UiKit.Primary(this, T("Возобновить", "Улантуу", "Resume", "Devam ettir", "Davom ettirish"))), () => ChangeStatusAsync(p, "resume"));
        if (!p.IsFinished)
            Act(Small(UiKit.Ghost(this, T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"))), () => OpenForEditAsync(p));
        if (p.IsActive || p.IsPaused)
            Act(Small(UiKit.Ghost(this, T("Завершить", "Аяктоо", "Finish", "Bitir", "Yakunlash"))), () => ChangeStatusAsync(p, "finish"));
        if (p.IsDraft)
            Act(Small(UiKit.Ghost(this, T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"))), () => DeleteAsync(p));

        var stack = new StackPanel { Children = { head } };
        if (actions.Children.Count > 0)
            stack.Children.Add(actions);
        var card = Card(stack);
        card.Tag = p.Id;
        return card;
    }

    // ── Действия у строки ─────────────────────────────────────────────────────────

    private async Task ChangeStatusAsync(Promotion p, string action)
    {
        if (action == "finish")
        {
            var ok = await ConfirmAsync(T($"Завершить акцию «{p.Title}»? Касса перестанет давать по ней скидку. Запустить её снова будет нельзя — только создать новую. Чеки и отчёт останутся.",
                    $"«{p.Title}» акциясын аяктайсызбы? Касса ал боюнча арзандатуу бербей калат. Кайра иштетүүгө болбойт — жаңысын гана түзүүгө болот. Чектер жана отчёт калат.",
                    $"Finish the promotion “{p.Title}”? The till stops giving its discount. It can't be started again — only a new one can be created. Receipts and the report stay.",
                    $"«{p.Title}» kampanyası bitirilsin mi? Kasa bu indirimi vermeyi bırakır. Yeniden başlatılamaz — yalnızca yenisi oluşturulabilir. Fişler ve rapor kalır.",
                    $"«{p.Title}» aksiyasi yakunlansinmi? Kassa u bo'yicha chegirma bermay qo'yadi. Uni qayta boshlab bo'lmaydi — faqat yangisini yaratish mumkin. Cheklar va hisobot qoladi."),
                T("Завершить", "Аяктоо", "Finish", "Bitir", "Yakunlash"), danger: true).ConfigureAwait(true);
            if (!ok)
                return;
        }
        _busy = true;
        try
        {
            await _api.ChangeStatusAsync(p.Id, action, _cts.Token).ConfigureAwait(true);
            PosLogger.Log($"Акции: «{p.Title}» ({p.Id}) — {action}.", "INFO");
            _busy = false;
            await LoadAsync().ConfigureAwait(true);
            SetStatus(action switch
            {
                "start" => T($"Акция «{p.Title}» запущена.", $"«{p.Title}» акциясы иштетилди.", $"Promotion “{p.Title}” started.", $"«{p.Title}» kampanyası başlatıldı.", $"«{p.Title}» aksiyasi boshlandi."),
                "pause" => T($"Акция «{p.Title}» на паузе — касса не даёт по ней скидку, пока вы не нажмёте «Возобновить».", $"«{p.Title}» тыныгууда — «Улантуу» баскычын баскыча касса арзандатуу бербейт.",
                    $"“{p.Title}” is paused — the till gives no discount until you press “Resume”.", $"«{p.Title}» duraklatıldı — «Devam ettir»e basana kadar kasa indirim vermez.", $"«{p.Title}» to'xtatildi — «Davom ettirish»ni bosmaguningizcha kassa chegirma bermaydi."),
                "resume" => T($"Акция «{p.Title}» снова действует.", $"«{p.Title}» кайра иштеп жатат.", $"“{p.Title}” is active again.", $"«{p.Title}» yeniden aktif.", $"«{p.Title}» yana amalda."),
                _ => T($"Акция «{p.Title}» завершена.", $"«{p.Title}» акциясы аяктады.", $"Promotion “{p.Title}” finished.", $"«{p.Title}» kampanyası bitti.", $"«{p.Title}» aksiyasi yakunlandi."),
            }, "BrushSuccess");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Акции: {action} «{p.Title}» не выполнено ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetStatus(PromotionErrors.Describe(ex).Text, "BrushDanger");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteAsync(Promotion p)
    {
        var ok = await ConfirmAsync(T($"Удалить черновик «{p.Title}»? Его нельзя будет вернуть.", $"«{p.Title}» караламасын өчүрөсүзбү? Аны кайтарууга болбойт.",
                $"Delete the draft “{p.Title}”? It can't be restored.", $"«{p.Title}» taslağı silinsin mi? Geri alınamaz.", $"«{p.Title}» qoralamasi o'chirilsinmi? Uni qaytarib bo'lmaydi."),
            T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), danger: true).ConfigureAwait(true);
        if (!ok)
            return;
        _busy = true;
        try
        {
            await _api.DeleteAsync(p.Id, _cts.Token).ConfigureAwait(true);
            PosLogger.Log($"Акции: черновик «{p.Title}» ({p.Id}) удалён.", "INFO");
            _busy = false;
            await LoadAsync().ConfigureAwait(true);
            SetStatus(T($"Черновик «{p.Title}» удалён.", $"«{p.Title}» караламасы өчүрүлдү.", $"Draft “{p.Title}” deleted.", $"«{p.Title}» taslağı silindi.", $"«{p.Title}» qoralamasi o'chirildi."), "BrushSuccess");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Акции: черновик «{p.Title}» не удалён ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            SetStatus(PromotionErrors.Describe(ex).Text, "BrushDanger");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task OpenForEditAsync(Promotion p)
    {
        _busy = true;
        try
        {
            // Свежая карточка (version, used_total) — список мог устареть.
            var fresh = await _api.GetAsync(p.Id, _cts.Token).ConfigureAwait(true);
            OpenWizard(fresh.Id.Length > 0 ? fresh : p);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Акции: карточка «{p.Title}» не загружена ({ex.GetType().Name}: {ex.Message}) — открываю по списку.", "WARNING");
            OpenWizard(p);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<bool> ConfirmAsync(string message, string confirmText, bool danger)
    {
        if (ConfirmForTests is { } hook)
            return await hook(message).ConfigureAwait(true);
        var answer = await PosMessageBox.ShowModalAsync(this, message, T("Акции", "Акциялар", "Promotions", "Kampanyalar", "Aksiyalar"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question, confirmText: confirmText, danger: danger).ConfigureAwait(true);
        return answer == System.Windows.MessageBoxResult.Yes;
    }

    // ── Помощники ─────────────────────────────────────────────────────────────────

    private Border Card(Control child)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12), BorderThickness = new Thickness(1), Child = child };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        return card;
    }

    private TextBlock Note(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16, 24, 16, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private TextBlock Note2(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private static Button Small(Button b)
    {
        b.Height = 44;
        b.FontSize = 13.5;
        return b;
    }

    private static void AddRow(Grid grid, Control child, int row)
    {
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private void SetStatus(string text, string brush)
    {
        _status.Text = text;
        _status.IsVisible = text.Length > 0;
        Use(_status, TextBlock.ForegroundProperty, brush);
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, (target is Control own ? own : this).GetResourceObservable(key));
}
