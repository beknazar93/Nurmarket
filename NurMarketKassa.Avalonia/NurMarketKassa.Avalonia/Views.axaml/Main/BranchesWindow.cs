using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-06, владелец: «на сайте есть филиалы — изучи и добавь в нашу админку тоже». Раздел программы владельца
/// «Филиалы» — то же, что раздел «Филиалы» сайта NurCRM, и те же данные:
/// • список филиалов (GET api/users/branches/) с поиском; новый филиал и правка — название, код, адрес, телефон, почта,
///   часовой пояс, «активен» (POST/PATCH); включить и выключить; удалить — как на сайте, сначала проверка: с открытыми
///   сменами нельзя, с товарами, сотрудниками и перемещениями — только после вопроса. Как на сайте — не больше 3 филиалов;
/// • «Перемещения» — товар между главным складом и филиалами (GET/POST api/main/branch-transfers/): список с периодом,
///   направлением и поиском, состав накладной, «Отменить» (POST …/{id}/cancel/ с причиной — остатки вернутся),
///   «Повторить» и «Новое перемещение»: откуда, куда, товары с остатком отправителя, «Всё», комментарий.
/// Остатки меняет сервер — сразу, как и при перемещении с сайта.</summary>
public sealed class BranchesWindow : Window, IOwnerSection
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    internal const int MaxBranches = 3;

    public sealed record Branch(string Id, string Name, string Code, string Address, string Phone, string Email, string Timezone, bool IsActive);

    private readonly NurMarketApiClient _api;
    private readonly Grid _root = new() { Margin = new Thickness(24, 16, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold };
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    // 2026-10-20, этап О9б: + строка плиток на обеих вкладках (KpiTileGrid).
    private readonly Grid _branchesPage = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };
    private readonly Grid _transfersPage = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), IsVisible = false };

    // Филиалы
    private readonly TextBox _branchSearch;
    // 2026-10-20, этап О9б: строка состояния — «Загружаю…» и ошибка обычным текстом; итоги — плитками.
    private readonly TextBlock _branchSummary = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid _branchKpi = new() { MinTileWidth = 170, Spacing = 8, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14), IsVisible = false };
    private readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid _transferKpi = new() { MinTileWidth = 190, Spacing = 8, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14), IsVisible = false };
    private readonly StackPanel _branchList = new() { Spacing = 10 };
    private List<Branch> _branches = new();
    private readonly Dictionary<string, string> _branchStats = new();
    private int _branchGen;

    // Перемещения
    private readonly ComboBox _transferBranch = new() { MinWidth = 220, MinHeight = 40 };
    private readonly StackPanel _directionChips = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly WrapPanel _transferFilters = new();
    private readonly TextBox _transferSearch;
    private readonly TextBlock _transferSummary = new() { FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
    private readonly StackPanel _transferList = new() { Spacing = 10 };
    private readonly Dictionary<string, JsonElement> _transferDetails = new();
    private readonly HashSet<string> _expanded = new();
    private string _direction = "all";
    private string _period = "month";
    private string _status = "";
    private int _page = 1;
    private int _transferGen;
    private bool _transfersLoaded;
    private bool _syncingBranchCombo;

    internal static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    internal static string MainWarehouse => T("Главный склад", "Башкы кампа", "Main warehouse", "Ana depo", "Asosiy ombor");

    /// <summary>Часовые пояса — те же, что в списке сайта.</summary>
    internal static (string Id, string Name)[] Timezones => new[]
    {
        ("Asia/Bishkek", T("Азия/Бишкек (UTC+6)", "Азия/Бишкек (UTC+6)", "Asia/Bishkek (UTC+6)", "Asya/Bişkek (UTC+6)", "Osiyo/Bishkek (UTC+6)")),
        ("Asia/Almaty", T("Азия/Алматы (UTC+6)", "Азия/Алматы (UTC+6)", "Asia/Almaty (UTC+6)", "Asya/Almatı (UTC+6)", "Osiyo/Olmaota (UTC+6)")),
        ("Asia/Tashkent", T("Азия/Ташкент (UTC+5)", "Азия/Ташкент (UTC+5)", "Asia/Tashkent (UTC+5)", "Asya/Taşkent (UTC+5)", "Osiyo/Toshkent (UTC+5)")),
        ("Europe/Moscow", T("Европа/Москва (UTC+3)", "Европа/Москва (UTC+3)", "Europe/Moscow (UTC+3)", "Avrupa/Moskova (UTC+3)", "Yevropa/Moskva (UTC+3)")),
        ("Asia/Dubai", T("Азия/Дубай (UTC+4)", "Азия/Дубай (UTC+4)", "Asia/Dubai (UTC+4)", "Asya/Dubai (UTC+4)", "Osiyo/Dubay (UTC+4)")),
        ("UTC", "UTC (UTC+0)"),
    };

    internal static string TimezoneName(string id) => Timezones.FirstOrDefault(x => x.Id == id).Name ?? id;

    public BranchesWindow()
    {
        _api = App.GetRequiredService<NurMarketApiClient>();
        Title = T("Филиалы", "Филиалдар", "Branches", "Şubeler", "Filiallar");
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        _title.Text = Title;
        Use(_title, TextBlock.ForegroundProperty, "BrushText");
        var hint = new TextBlock
        {
            Text = T("Филиалы и перемещение товара между главным складом и филиалами — общие с сайтом NurCRM. Остатки меняются сразу.",
                "Филиалдар жана товарды башкы кампа менен филиалдардын ортосунда жылдыруу — NurCRM сайты менен жалпы. Калдыктар дароо өзгөрөт.",
                "Branches and moving goods between the main warehouse and branches are shared with the NurCRM website. Stock changes right away.",
                "Şubeler ve ana depo ile şubeler arasındaki ürün transferleri NurCRM sitesiyle ortaktır. Stok hemen değişir.",
                "Filiallar va mahsulotni asosiy ombor bilan filiallar o'rtasida ko'chirish — NurCRM sayti bilan umumiy. Qoldiqlar darhol o'zgaradi."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        // ── страница «Филиалы»
        var branchTools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        _branchSearch = UiKit.Input(this, T("Поиск: название, адрес, телефон, почта…", "Издөө: аталышы, дареги, телефону, почтасы…", "Search: name, address, phone, email…",
            "Ara: ad, adres, telefon, e-posta…", "Qidirish: nomi, manzil, telefon, pochta…"), 40);
        _branchSearch.TextChanged += (_, _) => RenderBranches();
        branchTools.Children.Add(_branchSearch);
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Margin = new Thickness(8, 0, 0, 0);
        refresh.Click += async (_, _) => await LoadBranchesAsync().ConfigureAwait(true);
        Grid.SetColumn(refresh, 1);
        branchTools.Children.Add(refresh);
        var create = UiKit.Primary(this, T("＋ Новый филиал", "＋ Жаңы филиал", "＋ New branch", "＋ Yeni şube", "＋ Yangi filial"));
        create.Margin = new Thickness(8, 0, 0, 0);
        create.Click += async (_, _) => await EditBranchAsync(null).ConfigureAwait(true);
        Grid.SetColumn(create, 2);
        branchTools.Children.Add(create);
        branchTools.Classes.Add("no-reflow");
        Use(_branchSummary, TextBlock.ForegroundProperty, "BrushTextSoft");
        var branchScroll = new ScrollViewer { Content = _branchList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(_branchKpi, 1);
        Grid.SetRow(_branchSummary, 2);
        Grid.SetRow(branchScroll, 3);
        _branchesPage.Children.Add(branchTools);
        _branchesPage.Children.Add(_branchKpi);
        _branchesPage.Children.Add(_branchSummary);
        _branchesPage.Children.Add(branchScroll);

        // ── страница «Перемещения»
        var filterRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _transferBranch.Margin = new Thickness(0, 0, 8, 8);
        _transferBranch.SelectionChanged += async (_, _) =>
        {
            if (_syncingBranchCombo)
                return;
            _direction = "all";
            BuildTransferFilters();
            await LoadTransfersAsync(reset: true).ConfigureAwait(true);
        };
        filterRow.Children.Add(_transferBranch);
        // 2026-10-20, этап О9б: «Все / Входящие / Исходящие» — сегменты в рамке (рамка скрыта вместе с ними).
        var directionSegment = UiKit.Segment(_directionChips);
        directionSegment.Margin = new Thickness(0, 0, 8, 8);
        directionSegment.Bind(Visual.IsVisibleProperty, _directionChips.GetObservable(Visual.IsVisibleProperty));
        filterRow.Children.Add(directionSegment);
        var transferTools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        _transferSearch = UiKit.Input(this, T("Поиск: номер, комментарий…", "Издөө: номер, комментарий…", "Search: number, comment…", "Ara: numara, yorum…", "Qidirish: raqam, izoh…"), 40);
        var searchTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        searchTimer.Tick += async (_, _) =>
        {
            searchTimer.Stop();
            await LoadTransfersAsync(reset: true).ConfigureAwait(true);
        };
        _transferSearch.TextChanged += (_, _) =>
        {
            searchTimer.Stop();
            searchTimer.Start();
        };
        transferTools.Children.Add(_transferSearch);
        var refreshTransfers = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refreshTransfers.Margin = new Thickness(8, 0, 0, 0);
        refreshTransfers.Click += async (_, _) => await LoadTransfersAsync(reset: true).ConfigureAwait(true);
        Grid.SetColumn(refreshTransfers, 1);
        transferTools.Children.Add(refreshTransfers);
        var newTransfer = UiKit.Primary(this, T("＋ Новое перемещение", "＋ Жаңы жылдыруу", "＋ New transfer", "＋ Yeni transfer", "＋ Yangi ko'chirish"));
        newTransfer.Margin = new Thickness(8, 0, 0, 0);
        newTransfer.Click += async (_, _) => await NewTransferAsync(null).ConfigureAwait(true);
        Grid.SetColumn(newTransfer, 2);
        transferTools.Children.Add(newTransfer);
        transferTools.Classes.Add("no-reflow");
        Use(_transferSummary, TextBlock.ForegroundProperty, "BrushTextSoft");
        var transferScroll = new ScrollViewer { Content = _transferList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        // 2026-10-20, этап О9б: склад, направление, период и статус — одной строкой (на узком окне переносятся), а не тремя.
        _transferFilters.Margin = new Thickness(0, 0, 0, 0);
        filterRow.Children.Add(_transferFilters);
        Grid.SetRow(transferTools, 2);
        var listHost = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        listHost.Children.Add(_transferKpi);
        Grid.SetRow(_transferSummary, 1);
        listHost.Children.Add(_transferSummary);
        Grid.SetRow(transferScroll, 2);
        listHost.Children.Add(transferScroll);
        Grid.SetRow(listHost, 3);
        _transfersPage.Children.Add(filterRow);
        _transfersPage.Children.Add(transferTools);
        _transfersPage.Children.Add(listHost);

        // 2026-10-20, этап О9б (правило 2): вкладки «Филиалы / Перемещения» — сегменты в рамке, как в «Складе».
        var tabsSegment = UiKit.Segment(_tabs);
        tabsSegment.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(hint, 1);
        Grid.SetRow(tabsSegment, 2);
        Grid.SetRow(_branchesPage, 3);
        Grid.SetRow(_transfersPage, 3);
        _root.Children.Add(_title);
        _root.Children.Add(hint);
        _root.Children.Add(tabsSegment);
        _root.Children.Add(_branchesPage);
        _root.Children.Add(_transfersPage);
        Content = _root;

        // Узкий экран (телефон): кнопки — строкой под поиском.
        NarrowLayout.Attach(this, 900, narrow =>
        {
            foreach (var tools in new[] { branchTools, transferTools })
            {
                tools.ColumnDefinitions = new ColumnDefinitions(narrow ? "*,*" : "*,Auto,Auto");
                tools.RowDefinitions = narrow ? new RowDefinitions("Auto,8,Auto") : new RowDefinitions();
                var search = tools.Children[0];
                var b1 = tools.Children[1];
                var b2 = tools.Children[2];
                Grid.SetColumnSpan(search, narrow ? 2 : 1);
                Grid.SetRow(b1, narrow ? 2 : 0);
                Grid.SetColumn(b1, narrow ? 0 : 1);
                Grid.SetRow(b2, narrow ? 2 : 0);
                Grid.SetColumn(b2, narrow ? 1 : 2);
                ((Button)b1).Margin = new Thickness(narrow ? 0 : 8, 0, narrow ? 4 : 0, 0);
                ((Button)b2).Margin = new Thickness(narrow ? 4 : 8, 0, 0, 0);
                ((Button)b1).HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
                ((Button)b2).HorizontalAlignment = narrow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            }
        });

        BuildTabs("branches");
        BuildTransferFilters();
        _branchSummary.Text = T("Загружаю филиалы…", "Филиалдар жүктөлүүдө…", "Loading branches…", "Şubeler yükleniyor…", "Filiallar yuklanmoqda…");
        Opened += async (_, _) => await LoadBranchesAsync().ConfigureAwait(true);
    }

    public void AsOwnerSection()
    {
        _title.IsVisible = false;
        _root.Margin = OwnerSectionLayout.Margin;
    }

    private void BuildTabs(string active)
    {
        _tabs.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("branches", T("Филиалы", "Филиалдар", "Branches", "Şubeler", "Filiallar")),
                     ("transfers", T("Перемещения", "Жылдыруулар", "Transfers", "Transferler", "Ko'chirishlar")),
                 })
        {
            var chip = UiKit.Seg(text, key == active); // 2026-10-20, этап О9б: сегмент
            chip.Click += async (_, _) => await ShowTabAsync(key).ConfigureAwait(true);
            _tabs.Children.Add(chip);
        }
    }

    private async Task ShowTabAsync(string key)
    {
        BuildTabs(key);
        _branchesPage.IsVisible = key == "branches";
        _transfersPage.IsVisible = key == "transfers";
        if (key == "transfers" && !_transfersLoaded)
            await LoadTransfersAsync(reset: true).ConfigureAwait(true);
    }

    // ───────────────────────────────────────────── филиалы

    internal static async Task<List<Branch>> LoadBranchListAsync(NurMarketApiClient api)
    {
        var data = await api.RequestAsync(HttpMethod.Get, "api/users/branches/", null, new Dictionary<string, string> { ["page_size"] = "100" },
            CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        return Rows(data).Select(x => new Branch(Str(x, "id"), Str(x, "name"), Str(x, "code"), Str(x, "address"), Str(x, "phone"), Str(x, "email"),
                Str(x, "timezone") is { Length: > 0 } tz ? tz : "Asia/Bishkek",
                !x.TryGetProperty("is_active", out var a) || a.ValueKind != JsonValueKind.False))
            .Where(x => x.Id.Length > 0).ToList();
    }

    private async Task LoadBranchesAsync()
    {
        var gen = ++_branchGen;
        try
        {
            var list = await LoadBranchListAsync(_api).ConfigureAwait(true);
            if (gen != _branchGen)
                return;
            _branches = list;
            _branchStats.Clear();
            RenderBranches();
            FillTransferBranchCombo();
            PosLogger.Log($"Филиалы: {list.Count} шт.", "INFO");
            foreach (var b in list)
                _ = LoadStatsAsync(b, gen);
        }
        catch (Exception ex)
        {
            if (gen != _branchGen)
                return;
            _branchSummary.IsVisible = true;
            _branchSummary.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
            PosLogger.Log($"Филиалы: не загружены ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Что привязано к филиалу — те же 4 запроса, что сайт делает перед удалением.</summary>
    private async Task<(int OpenShifts, int Products, int Employees, int Transfers)> CountLinksAsync(string branchId)
    {
        async Task<int> Count(string path, Dictionary<string, string> q)
        {
            try
            {
                q["branch"] = branchId;
                q["page_size"] = "1";
                var data = await _api.RequestAsync(HttpMethod.Get, path, null, q, CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
                if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.TryGetInt32(out var n))
                    return n;
                return data.ValueKind == JsonValueKind.Array ? data.GetArrayLength() : 0;
            }
            catch
            {
                return 0;
            }
        }

        var shifts = Count("api/construction/shifts/", new Dictionary<string, string> { ["status"] = "open" });
        var products = Count("api/main/products/list/", new Dictionary<string, string>());
        var employees = Count("api/users/employees/", new Dictionary<string, string>());
        var transfers = Count("api/main/branch-transfers/", new Dictionary<string, string>());
        return (await shifts.ConfigureAwait(true), await products.ConfigureAwait(true), await employees.ConfigureAwait(true), await transfers.ConfigureAwait(true));
    }

    private async Task LoadStatsAsync(Branch branch, int gen)
    {
        var (shifts, products, employees, transfers) = await CountLinksAsync(branch.Id).ConfigureAwait(true);
        if (gen != _branchGen)
            return;
        _branchStats[branch.Id] = T($"Товаров: {products} · Сотрудников: {employees} · Открытых смен: {shifts} · Перемещений: {transfers}",
            $"Товарлар: {products} · Кызматкерлер: {employees} · Ачык сменалар: {shifts} · Жылдыруулар: {transfers}",
            $"Products: {products} · Employees: {employees} · Open shifts: {shifts} · Transfers: {transfers}",
            $"Ürünler: {products} · Çalışanlar: {employees} · Açık vardiyalar: {shifts} · Transferler: {transfers}",
            $"Mahsulotlar: {products} · Xodimlar: {employees} · Ochiq smenalar: {shifts} · Ko'chirishlar: {transfers}");
        RenderBranches();
    }

    private void RenderBranches()
    {
        _branchList.Children.Clear();
        var active = _branches.Count(b => b.IsActive);
        // 2026-10-20, этап О9б: итоги — плитками «Филиалов N из 3 · Активных · Выключено»; пусто — подсказкой по центру списка.
        _branchSummary.IsVisible = false;
        _branchKpi.Children.Clear();
        _branchKpi.IsVisible = _branches.Count > 0;
        if (_branches.Count > 0)
        {
            _branchKpi.Children.Add(UiKit.Kpi(T("Филиалов", "Филиалдар", "Branches", "Şubeler", "Filiallar"),
                T($"{_branches.Count} из {MaxBranches}", $"{_branches.Count} / {MaxBranches}", $"{_branches.Count} of {MaxBranches}", $"{_branches.Count} / {MaxBranches}", $"{_branches.Count} / {MaxBranches}")));
            _branchKpi.Children.Add(UiKit.Kpi(T("Активных", "Активдүү", "Active", "Aktif", "Faol"), active.ToString(CultureInfo.InvariantCulture)));
            _branchKpi.Children.Add(UiKit.Kpi(T("Выключено", "Өчүрүлгөн", "Disabled", "Kapalı", "O'chirilgan"), (_branches.Count - active).ToString(CultureInfo.InvariantCulture)));
        }
        else
        {
            _branchList.Children.Add(SectionCards.Empty(this, T("Филиалов пока нет. Нажмите «＋ Новый филиал» — потом товар можно будет перемещать с главного склада в филиал (вкладка «Перемещения»). Можно до 3 филиалов.",
                "Азырынча филиал жок. «＋ Жаңы филиал» басыңыз — анан товарды башкы кампадан филиалга жылдырса болот («Жылдыруулар» өтмөгү). 3 филиалга чейин болот.",
                "No branches yet. Press “＋ New branch” — then you can move goods from the main warehouse to it (the “Transfers” tab). Up to 3 branches.",
                "Henüz şube yok. «＋ Yeni şube»ye basın — sonra ürünleri ana depodan şubeye taşıyabilirsiniz («Transferler» sekmesi). En fazla 3 şube.",
                "Hozircha filial yo'q. «＋ Yangi filial»ni bosing — keyin mahsulotni asosiy ombordan filialga ko'chirish mumkin («Ko'chirishlar» bo'limi). 3 tagacha filial.")));
            return;
        }
        var query = (_branchSearch.Text ?? "").Trim();
        var shown = _branches.Where(b => query.Length == 0
            || new[] { b.Name, b.Code, b.Address, b.Phone, b.Email }.Any(x => x.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToList();
        if (_branches.Count > 0 && shown.Count == 0)
        {
            _branchList.Children.Add(SectionCards.Empty(this, T("Ничего не найдено. Ищется по названию, коду, адресу, телефону и почте.", "Эч нерсе табылган жок. Аталышы, коду, дареги, телефону жана почтасы боюнча изделет.",
                "Nothing found. The search looks at name, code, address, phone and email.", "Hiçbir şey bulunamadı. Ad, kod, adres, telefon ve e-postaya göre aranır.",
                "Hech narsa topilmadi. Nomi, kodi, manzili, telefoni va pochtasi bo'yicha qidiriladi.")));
            return;
        }
        foreach (var b in shown)
            _branchList.Children.Add(BuildBranchCard(b));
    }

    private Control BuildBranchCard(Branch b)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var body = new StackPanel { Spacing = 6 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var name = new TextBlock { Text = b.Name.Length > 0 ? b.Name : "—", FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(name);
        head.Children.Add(Pill(b.IsActive ? T("Активен", "Активдүү", "Active", "Aktif", "Faol") : T("Выключен", "Өчүрүлгөн", "Disabled", "Kapalı", "O'chirilgan"),
            b.IsActive ? "BrushSuccessSoft" : "BrushWarningSoft", b.IsActive ? "BrushSuccess" : "BrushWarning"));
        body.Children.Add(head);
        // 2026-10-20, этап О9б: карточка плотнее — адрес строкой, телефон и почта одной строкой, пояс и код — одной.
        if (b.Address.Length > 0)
        {
            var address = new TextBlock { Text = b.Address, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
            Use(address, TextBlock.ForegroundProperty, "BrushText");
            body.Children.Add(address);
        }
        if (string.Join("  ·  ", new[] { b.Phone, b.Email }.Where(x => x.Length > 0)) is { Length: > 0 } contacts)
            body.Children.Add(Hint(contacts));
        body.Children.Add(Hint(T("Часовой пояс: ", "Убакыт алкагы: ", "Time zone: ", "Saat dilimi: ", "Vaqt mintaqasi: ") + TimezoneName(b.Timezone)
                               + (b.Code.Length > 0 ? T(" · код: ", " · коду: ", " · code: ", " · kod: ", " · kod: ") + b.Code : "")));
        body.Children.Add(Hint(_branchStats.TryGetValue(b.Id, out var stats) ? stats : T("Считаю товары и сотрудников…", "Товарлар жана кызматкерлер эсептелүүдө…",
            "Counting products and employees…", "Ürünler ve çalışanlar sayılıyor…", "Mahsulotlar va xodimlar sanalmoqda…")));

        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        Button Small(string text, Func<Task> action)
        {
            var btn = UiKit.Ghost(this, text);
            btn.Height = 38;
            btn.Margin = new Thickness(0, 0, 8, 6);
            btn.Click += async (_, _) => await action().ConfigureAwait(true);
            buttons.Children.Add(btn);
            return btn;
        }

        Small(T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"), () => EditBranchAsync(b));
        Small(b.IsActive ? T("Выключить", "Өчүрүү", "Disable", "Kapat", "O'chirish") : T("Включить", "Күйгүзүү", "Enable", "Aç", "Yoqish"), () => ToggleBranchAsync(b));
        Small(T("Перемещения →", "Жылдыруулар →", "Transfers →", "Transferler →", "Ko'chirishlar →"), async () =>
        {
            SelectTransferBranch(b.Id);
            _direction = "all";
            BuildTransferFilters();
            BuildTabs("transfers");
            _branchesPage.IsVisible = false;
            _transfersPage.IsVisible = true;
            await LoadTransfersAsync(reset: true).ConfigureAwait(true);
        });
        var delete = Small(T("Удалить", "Өчүрүү (жок кылуу)", "Delete", "Sil", "O'chirib tashlash"), () => DeleteBranchAsync(b));
        delete.Classes.Add("danger"); // 2026-10-20, этап О9б: красная обводка стилем sec-outline.danger (и при наведении), как «Отменить перемещение»
        body.Children.Add(buttons);
        card.Child = body;
        return card;
    }

    private async Task EditBranchAsync(Branch? branch)
    {
        if (branch == null && _branches.Count >= MaxBranches)
        {
            await InfoAsync(T($"Можно создать максимум {MaxBranches} филиала.", $"Эң көп {MaxBranches} филиал түзсө болот.", $"You can create at most {MaxBranches} branches.",
                $"En fazla {MaxBranches} şube oluşturabilirsiniz.", $"Ko'pi bilan {MaxBranches} ta filial yaratish mumkin.")).ConfigureAwait(true);
            return;
        }
        var dialog = new BranchEditDialog(branch);
        if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
            await LoadBranchesAsync().ConfigureAwait(true);
    }

    private async Task ToggleBranchAsync(Branch b)
    {
        if (b.IsActive && !await ConfirmAsync(T($"Выключить филиал «{b.Name}»? Он останется в списке, его можно включить снова.",
                $"«{b.Name}» филиалын өчүрөсүзбү? Ал тизмеде калат, кайра күйгүзсө болот.",
                $"Disable the branch \"{b.Name}\"? It stays in the list and can be enabled again.",
                $"\"{b.Name}\" şubesi kapatılsın mı? Listede kalır, tekrar açılabilir.",
                $"«{b.Name}» filiali o'chirilsinmi? U ro'yxatda qoladi, qayta yoqish mumkin."),
                // 2026-10-22 (модалки, пакет 4): глагол и красная кнопка вместо «Да/Нет».
                T("Выключить филиал", "Филиалды өчүрүү", "Disable branch", "Şubeyi kapat", "Filialni o'chirish"), danger: true).ConfigureAwait(true))
            return;
        try
        {
            await _api.RequestAsync(new HttpMethod("PATCH"), $"api/users/branches/{Uri.EscapeDataString(b.Id)}/",
                new Dictionary<string, object?> { ["is_active"] = !b.IsActive }, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            PosLogger.Log($"Филиалы: «{b.Name}» {(b.IsActive ? "выключен" : "включён")}.", "INFO");
            await LoadBranchesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await InfoAsync(T("Не удалось изменить статус: ", "Статусту өзгөртүү мүмкүн болгон жок: ", "Couldn't change the status: ", "Durum değiştirilemedi: ", "Holatni o'zgartirib bo'lmadi: ")
                            + ServerTelegramBotApi.DescribeFields(ex)).ConfigureAwait(true);
        }
    }

    private async Task DeleteBranchAsync(Branch b)
    {
        var (shifts, products, employees, transfers) = await CountLinksAsync(b.Id).ConfigureAwait(true);
        if (shifts > 0)
        {
            await InfoAsync(T($"Удаление невозможно: в филиале есть открытые смены ({shifts}). Закройте их и повторите.",
                $"Өчүрүүгө болбойт: филиалда ачык сменалар бар ({shifts}). Аларды жаап, кайталаңыз.",
                $"Can't delete: the branch has open shifts ({shifts}). Close them and try again.",
                $"Silinemez: şubede açık vardiyalar var ({shifts}). Kapatıp tekrar deneyin.",
                $"O'chirib bo'lmaydi: filialda ochiq smenalar bor ({shifts}). Ularni yopib, qayta urinib ko'ring.")).ConfigureAwait(true);
            return;
        }
        var links = new List<string>();
        if (products > 0)
            links.Add(T($"товаров: {products}", $"товарлар: {products}", $"products: {products}", $"ürünler: {products}", $"mahsulotlar: {products}"));
        if (employees > 0)
            links.Add(T($"сотрудников: {employees}", $"кызматкерлер: {employees}", $"employees: {employees}", $"çalışanlar: {employees}", $"xodimlar: {employees}"));
        if (transfers > 0)
            links.Add(T($"перемещений: {transfers}", $"жылдыруулар: {transfers}", $"transfers: {transfers}", $"transferler: {transfers}", $"ko'chirishlar: {transfers}"));
        var question = links.Count > 0
            ? T($"К филиалу «{b.Name}» привязано: {string.Join(", ", links)}. Удалить филиал всё равно?",
                $"«{b.Name}» филиалына байланышкан: {string.Join(", ", links)}. Баары бир өчүрөсүзбү?",
                $"Linked to the branch \"{b.Name}\": {string.Join(", ", links)}. Delete the branch anyway?",
                $"\"{b.Name}\" şubesine bağlı: {string.Join(", ", links)}. Şube yine de silinsin mi?",
                $"«{b.Name}» filialiga bog'langan: {string.Join(", ", links)}. Baribir o'chirilsinmi?")
            : T($"Удалить филиал «{b.Name}»?", $"«{b.Name}» филиалын өчүрөсүзбү?", $"Delete the branch \"{b.Name}\"?", $"\"{b.Name}\" şubesi silinsin mi?", $"«{b.Name}» filiali o'chirilsinmi?");
        // 2026-10-22 (модалки, пакет 4): «Удалить филиал» красной вместо «Да».
        if (!await ConfirmAsync(question, T("Удалить филиал", "Филиалды өчүрүү", "Delete branch", "Şubeyi sil", "Filialni o'chirish"), danger: true).ConfigureAwait(true))
            return;
        try
        {
            await _api.RequestAsync(HttpMethod.Delete, $"api/users/branches/{Uri.EscapeDataString(b.Id)}/", null, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            PosLogger.Log($"Филиалы: «{b.Name}» удалён.", "INFO");
            await LoadBranchesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await InfoAsync(T("Не удалось удалить: ", "Өчүрүү мүмкүн болгон жок: ", "Couldn't delete: ", "Silinemedi: ", "O'chirib bo'lmadi: ") + ServerTelegramBotApi.DescribeFields(ex)).ConfigureAwait(true);
        }
    }

    // ───────────────────────────────────────────── перемещения

    private void FillTransferBranchCombo()
    {
        var selected = SelectedTransferBranchId();
        _syncingBranchCombo = true;
        _transferBranch.ItemsSource = new[] { T("Все склады", "Бардык кампалар", "All warehouses", "Tüm depolar", "Barcha omborlar") }
            .Concat(_branches.Select(b => b.Name)).ToList();
        var index = selected == null ? 0 : _branches.FindIndex(b => b.Id == selected) + 1;
        _transferBranch.SelectedIndex = Math.Max(0, index);
        _syncingBranchCombo = false;
    }

    private void SelectTransferBranch(string id)
    {
        _syncingBranchCombo = true;
        _transferBranch.SelectedIndex = Math.Max(0, _branches.FindIndex(b => b.Id == id) + 1);
        _syncingBranchCombo = false;
    }

    private string? SelectedTransferBranchId() =>
        _transferBranch.SelectedIndex > 0 && _transferBranch.SelectedIndex - 1 < _branches.Count ? _branches[_transferBranch.SelectedIndex - 1].Id : null;

    private void BuildTransferFilters()
    {
        _directionChips.Children.Clear();
        _directionChips.IsVisible = SelectedTransferBranchId() != null;
        foreach (var (key, text) in new[]
                 {
                     ("all", T("Все", "Баары", "All", "Tümü", "Hammasi")), ("in", T("Входящие", "Келгендер", "Incoming", "Gelen", "Kiruvchi")),
                     ("out", T("Исходящие", "Кеткендер", "Outgoing", "Giden", "Chiquvchi")),
                 })
        {
            var chip = UiKit.Seg(text, key == _direction); // 2026-10-20, этап О9б: сегменты вместо чипов
            chip.Click += async (_, _) =>
            {
                _direction = key;
                BuildTransferFilters();
                await LoadTransfersAsync(reset: true).ConfigureAwait(true);
            };
            _directionChips.Children.Add(chip);
        }

        // 2026-10-20, этап О9б (правило 2): период и статус — две группы сегментов (выбранный тёмно-синий), а не 7 одинаковых чипов.
        _transferFilters.Children.Clear();
        var periods = new StackPanel { Orientation = Orientation.Horizontal };
        var statuses = new StackPanel { Orientation = Orientation.Horizontal };
        // 2026-10-20, проверка фильтров: даты периода рядом с его сегментами («01.10–20.10»).
        foreach (var group in new[] { UiKit.WithPeriodLabel(UiKit.Segment(periods), _transferPeriodLabel), UiKit.Segment(statuses) })
        {
            group.Margin = new Thickness(0, 0, 8, 8);
            _transferFilters.Children.Add(group);
        }
        _transferPeriodLabel.Text = TransferPeriodFrom() is { } periodFrom ? PeriodRange.Label(periodFrom, DateTime.Today) : "";
        foreach (var (key, text) in new[]
                 {
                     ("week", T("Неделя", "Апта", "Week", "Hafta", "Hafta")), ("month", T("Месяц", "Ай", "Month", "Ay", "Oy")),
                     ("quarter", T("3 месяца", "3 ай", "3 months", "3 ay", "3 oy")), ("all", T("Всё время", "Бардык убакыт", "All time", "Tüm zamanlar", "Butun davr")),
                 })
        {
            var chip = UiKit.Seg(text, key == _period);
            chip.Click += async (_, _) =>
            {
                _period = key;
                BuildTransferFilters();
                await LoadTransfersAsync(reset: true).ConfigureAwait(true);
            };
            periods.Children.Add(chip);
        }
        foreach (var (key, text) in new[]
                 {
                     ("", T("Любой статус", "Каалаган статус", "Any status", "Her durum", "Har qanday holat")),
                     ("completed", T("Проведено", "Өткөрүлдү", "Completed", "Tamamlandı", "O'tkazildi")),
                     ("cancelled", T("Отменено", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi")),
                 })
        {
            var chip = UiKit.Seg(text, key == _status);
            chip.Click += async (_, _) =>
            {
                _status = key;
                BuildTransferFilters();
                await LoadTransfersAsync(reset: true).ConfigureAwait(true);
            };
            statuses.Children.Add(chip);
        }
    }

    // 2026-10-20, проверка фильтров: одни правила периода во всех окнах (PeriodRange) — «Неделя» = 7 дней включая сегодня (было 8:
    // сегодня−7), «Месяц» — с 1-го числа (было сегодня−30), «3 месяца» — 90 дней включая сегодня (было 91).
    private readonly TextBlock _transferPeriodLabel = new();

    private DateTime? TransferPeriodFrom() => _period switch
    {
        "week" => PeriodRange.WeekFrom(DateTime.Today),
        "month" => PeriodRange.MonthFrom(DateTime.Today),
        "quarter" => DateTime.Today.AddDays(-89),
        _ => (DateTime?)null,
    };

    private async Task LoadTransfersAsync(bool reset)
    {
        var gen = ++_transferGen;
        if (reset)
        {
            _page = 1;
            _expanded.Clear();
        }
        var query = new Dictionary<string, string> { ["page"] = _page.ToString(CultureInfo.InvariantCulture) };
        if (SelectedTransferBranchId() is { } branch)
        {
            // Как на сайте: у выбранного филиала — «Входящие» (to_branch), «Исходящие» (from_branch) или все его (branch).
            query[_direction switch { "in" => "to_branch", "out" => "from_branch", _ => "branch" }] = branch;
        }
        var from = TransferPeriodFrom();
        if (from is { } f)
        {
            query["date_from"] = f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            query["date_to"] = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        if (_status.Length > 0)
            query["status"] = _status;
        if ((_transferSearch.Text ?? "").Trim() is { Length: > 0 } search)
            query["search"] = search;
        if (reset)
        {
            _transferSummary.IsVisible = true;
            _transferSummary.Text = T("Загружаю перемещения…", "Жылдыруулар жүктөлүүдө…", "Loading transfers…", "Transferler yükleniyor…", "Ko'chirishlar yuklanmoqda…");
        }
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, "api/main/branch-transfers/", null, query, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            if (gen != _transferGen)
                return;
            _transfersLoaded = true;
            var rows = Rows(data).ToList();
            var count = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : rows.Count;
            var hasNext = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String;
            if (reset)
                _transferList.Children.Clear();
            else if (_transferList.Children.Count > 0 && _transferList.Children[^1] is Button { Tag: "more" } moreButton)
                _transferList.Children.Remove(moreButton);
            _transferSummary.IsVisible = false;
            if (count == 0)
            {
                // 2026-10-20, этап О9б: пусто — подсказкой по центру списка, плиток нет.
                _transferKpi.IsVisible = false;
                _transferList.Children.Add(SectionCards.Empty(this, T("Перемещений за этот период нет. Чтобы перенести товар с главного склада в филиал или между филиалами, нажмите «＋ Новое перемещение». Прошлые — «Всё время».",
                    "Бул мезгилде жылдыруу жок. Товарды башкы кампадан филиалга же филиалдардын ортосунда жылдыруу үчүн «＋ Жаңы жылдыруу» басыңыз. Мурункулары — «Бардык убакыт».",
                    "No transfers for this period. To move goods from the main warehouse to a branch or between branches, press “＋ New transfer”. Older ones — “All time”.",
                    "Bu dönemde transfer yok. Ürünleri ana depodan şubeye veya şubeler arasında taşımak için «＋ Yeni transfer»e basın. Eskiler — «Tüm zamanlar».",
                    "Bu davrda ko'chirishlar yo'q. Mahsulotni asosiy ombordan filialga yoki filiallar o'rtasida ko'chirish uchun «＋ Yangi ko'chirish»ni bosing. Oldingilari — «Butun davr».")));
                return;
            }
            foreach (var row in rows)
                _transferList.Children.Add(BuildTransferCard(row));
            var completed = rows.Where(r => Str(r, "status") == "completed").ToList();
            // 2026-10-20, этап О9б: итоги — плитками (суммы не обрезаются); «на этой странице» — как и раньше, по загруженным строкам.
            _transferKpi.Children.Clear();
            _transferKpi.Children.Add(UiKit.Kpi(T("Перемещений", "Жылдыруулар", "Transfers", "Transferler", "Ko'chirishlar"), count.ToString(CultureInfo.InvariantCulture)));
            _transferKpi.Children.Add(UiKit.Kpi(T("Проведено на этой странице", "Бул баракта өткөрүлдү", "Completed on this page", "Bu sayfada tamamlanan", "Bu sahifada o'tkazildi"),
                T($"{Qty(completed.Sum(r => Num(r, "total_quantity")))} ед.", $"{Qty(completed.Sum(r => Num(r, "total_quantity")))} бирдик", $"{Qty(completed.Sum(r => Num(r, "total_quantity")))} units",
                    $"{Qty(completed.Sum(r => Num(r, "total_quantity")))} birim", $"{Qty(completed.Sum(r => Num(r, "total_quantity")))} birlik")));
            _transferKpi.Children.Add(UiKit.Kpi(T("На сумму", "Суммасы", "Amount", "Tutar", "Summa"), Money(completed.Sum(r => Num(r, "total_amount")))));
            _transferKpi.IsVisible = true;
            if (hasNext)
            {
                var more = UiKit.Ghost(this, T("Показать ещё", "Дагы көрсөтүү", "Show more", "Daha fazla göster", "Yana ko'rsatish"));
                more.Tag = "more";
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Click += async (_, _) =>
                {
                    _page++;
                    await LoadTransfersAsync(reset: false).ConfigureAwait(true);
                };
                _transferList.Children.Add(more);
            }
        }
        catch (Exception ex)
        {
            if (gen != _transferGen)
                return;
            // 2026-10-20: проверка фильтров — при ошибке под новым фильтром оставались перемещения прошлого.
            if (reset)
                _transferList.Children.Clear();
            _transferKpi.IsVisible = false;
            _transferSummary.IsVisible = true;
            _transferSummary.Text = T("Не удалось загрузить: ", "Жүктөө мүмкүн болгон жок: ", "Couldn't load: ", "Yüklenemedi: ", "Yuklab bo'lmadi: ") + ServerTelegramBotApi.DescribeFields(ex);
            PosLogger.Log($"Перемещения филиалов: не загружены ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>Склад перемещения: филиал ({id, name}) или главный склад (null).</summary>
    internal static string Route(JsonElement row, string side) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(side, out var s) && s.ValueKind == JsonValueKind.Object && Str(s, "name") is { Length: > 0 } name ? name : MainWarehouse;

    private Control BuildTransferCard(JsonElement row)
    {
        var id = Str(row, "id");
        var cancelled = Str(row, "status") == "cancelled";
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12), Cursor = new Cursor(StandardCursorType.Hand) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var body = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = new StackPanel { Spacing = 3 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var number = new TextBlock { Text = Str(row, "number") is { Length: > 0 } nm ? nm : "—", FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Use(number, TextBlock.ForegroundProperty, "BrushText");
        titleRow.Children.Add(number);
        // 2026-10-20, этап О9б (правило 4): «Отменено» — нейтральной плашкой (красный — только долг и убыток).
        titleRow.Children.Add(cancelled
            ? Pill(T("Отменено", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"), "BrushPanelSoft", "BrushTextSoft")
            : Pill(T("Проведено", "Өткөрүлдү", "Completed", "Tamamlandı", "O'tkazildi"), "BrushSuccessSoft", "BrushSuccess"));
        left.Children.Add(titleRow);
        var direction = SelectedTransferBranchId() is { } selected
            ? row.TryGetProperty("to_branch", out var to) && Str(to, "id") == selected ? "⬇ " : "⬆ "
            : "";
        var route = new TextBlock { Text = direction + Route(row, "from_branch") + "  →  " + Route(row, "to_branch"), FontSize = 14, TextWrapping = TextWrapping.Wrap };
        Use(route, TextBlock.ForegroundProperty, "BrushText");
        left.Children.Add(route);
        var date = DateTime.TryParse(Str(row, "date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("dd.MM.yyyy", Ru) : Str(row, "date");
        var positions = (int)Num(row, "items_count");
        left.Children.Add(Hint(string.Join(" · ", new[]
        {
            date,
            T($"{positions} поз.", $"{positions} позиция", $"{positions} items", $"{positions} kalem", $"{positions} pozitsiya"),
            T($"{Qty(Num(row, "total_quantity"))} ед.", $"{Qty(Num(row, "total_quantity"))} бирдик", $"{Qty(Num(row, "total_quantity"))} units", $"{Qty(Num(row, "total_quantity"))} birim", $"{Qty(Num(row, "total_quantity"))} birlik"),
        }.Where(x => x.Length > 0))));
        if (Str(row, "comment") is { Length: > 0 } comment)
            left.Children.Add(Hint("«" + comment + "»"));
        if (cancelled)
            left.Children.Add(Hint(T("Отменено", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi")
                                   + (DateTime.TryParse(Str(row, "cancelled_at"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ca) ? " " + ca.ToString("dd.MM.yyyy HH:mm", Ru) : "")
                                   + (Str(row, "cancel_reason") is { Length: > 0 } reason ? ": " + reason : "")));
        head.Children.Add(left);
        var sum = new TextBlock { Text = Money(Num(row, "total_amount")), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 0, 0) };
        sum.Classes.Add("num");
        Use(sum, TextBlock.ForegroundProperty, cancelled ? "BrushTextSoft" : "BrushText");
        Grid.SetColumn(sum, 1);
        head.Children.Add(sum);
        body.Children.Add(head);

        var expanded = _expanded.Contains(id);
        // 2026-10-20, этап О9б: цветом текста (был жёлтый на белом), состав — таблицей с ровными числами.
        var more = SectionCards.Toggle(this, expanded ? T("Скрыть товары ▴", "Товарларды жашыруу ▴", "Hide products ▴", "Ürünleri gizle ▴", "Mahsulotlarni yashirish ▴")
            : T("Товары, отмена, повтор ▾", "Товарлар, жокко чыгаруу, кайталоо ▾", "Products, cancel, repeat ▾", "Ürünler, iptal, tekrar ▾", "Mahsulotlar, bekor qilish, takrorlash ▾"));
        body.Children.Add(more);
        if (expanded)
        {
            if (_transferDetails.TryGetValue(id, out var detail))
            {
                if (detail.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    var table = new List<(string, string, string, string)>();
                    foreach (var it in items.EnumerateArray())
                    {
                        var unit = Str(it, "unit") is { Length: > 0 } u ? u : T("шт", "даана", "pcs", "adet", "dona");
                        table.Add((Str(it, "name") + (Str(it, "barcode") is { Length: > 0 } bc ? "  ·  " + bc : ""), $"{Qty(Num(it, "quantity"))} {unit}", Money(Num(it, "price")), Money(Num(it, "amount"))));
                    }
                    body.Children.Add(SectionCards.LinesTable(this, table, T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
                        T("Цена", "Баасы", "Price", "Fiyat", "Narx"), T("Сумма", "Суммасы", "Amount", "Tutar", "Summa")));
                }
                var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                var repeat = UiKit.Ghost(this, T("↻ Повторить", "↻ Кайталоо", "↻ Repeat", "↻ Tekrarla", "↻ Takrorlash"));
                repeat.Height = 38;
                repeat.Margin = new Thickness(0, 0, 8, 0);
                repeat.Click += async (_, _) => await NewTransferAsync(detail).ConfigureAwait(true);
                actions.Children.Add(repeat);
                if (!cancelled)
                {
                    var cancel = UiKit.Ghost(this, T("Отменить перемещение", "Жылдырууну жокко чыгаруу", "Cancel the transfer", "Transferi iptal et", "Ko'chirishni bekor qilish"));
                    cancel.Height = 38;
                    cancel.Classes.Add("danger"); // 2026-10-18, ревью О3: красная обводка стилем sec-outline.danger (и при наведении)
                    cancel.Click += async (_, _) => await CancelTransferAsync(row).ConfigureAwait(true);
                    actions.Children.Add(cancel);
                }
                body.Children.Add(actions);
            }
            else
            {
                body.Children.Add(Hint(T("Загружаю состав…", "Курамы жүктөлүүдө…", "Loading items…", "Kalemler yükleniyor…", "Tarkibi yuklanmoqda…")));
                _ = LoadDetailAsync(id, card, row);
            }
        }
        card.Child = body;
        card.PointerPressed += (_, e) =>
        {
            // Нажатие на кнопку внутри карточки не сворачивает её.
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) != null)
                return;
            if (!_expanded.Remove(id))
                _expanded.Add(id);
            Replace(card, row);
        };
        return card;
    }

    private void Replace(Control card, JsonElement row)
    {
        var index = _transferList.Children.IndexOf(card);
        if (index >= 0)
            _transferList.Children[index] = BuildTransferCard(row);
    }

    private async Task LoadDetailAsync(string id, Control card, JsonElement row)
    {
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Get, $"api/main/branch-transfers/{Uri.EscapeDataString(id)}/", null, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            _transferDetails[id] = data.Clone();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Перемещение {id}: состав не загружен ({ex.Message}).", "WARNING");
            _expanded.Remove(id);
        }
        Replace(card, row);
    }

    private async Task CancelTransferAsync(JsonElement row)
    {
        var id = Str(row, "id");
        var reason = await AskReasonAsync(T($"Отменить перемещение {Str(row, "number")}? Товар вернётся: {Route(row, "to_branch")} → {Route(row, "from_branch")}.",
            $"{Str(row, "number")} жылдыруусун жокко чыгарасызбы? Товар кайтып келет: {Route(row, "to_branch")} → {Route(row, "from_branch")}.",
            $"Cancel the transfer {Str(row, "number")}? The goods will go back: {Route(row, "to_branch")} → {Route(row, "from_branch")}.",
            $"{Str(row, "number")} transferi iptal edilsin mi? Ürünler geri döner: {Route(row, "to_branch")} → {Route(row, "from_branch")}.",
            $"{Str(row, "number")} ko'chirish bekor qilinsinmi? Mahsulot qaytadi: {Route(row, "to_branch")} → {Route(row, "from_branch")}."),
            // 2026-10-22 (модалки, пакет 4): «Отменить перемещение» красной, рядом «Назад» (не «Отмена» — чтобы не путать).
            T("Отменить перемещение", "Жылдырууну жокко чыгаруу", "Cancel transfer", "Transferi iptal et", "Ko'chirishni bekor qilish"),
            T("Назад", "Артка", "Back", "Geri", "Orqaga")).ConfigureAwait(true);
        if (reason == null)
            return;
        try
        {
            var body = new Dictionary<string, object?>();
            if (reason.Length > 0)
                body["reason"] = reason;
            await _api.RequestAsync(HttpMethod.Post, $"api/main/branch-transfers/{Uri.EscapeDataString(id)}/cancel/", body, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            PosLogger.Log($"Перемещение {Str(row, "number")} отменено.", "INFO");
            _transferDetails.Remove(id);
            await LoadTransfersAsync(reset: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await InfoAsync(T("Не удалось отменить: ", "Жокко чыгаруу мүмкүн болгон жок: ", "Couldn't cancel: ", "İptal edilemedi: ", "Bekor qilib bo'lmadi: ") + ServerTelegramBotApi.DescribeFields(ex)).ConfigureAwait(true);
        }
    }

    private async Task NewTransferAsync(JsonElement? repeatOf)
    {
        if (_branches.Count == 0)
        {
            await InfoAsync(T("Сначала создайте филиал на вкладке «Филиалы».", "Адегенде «Филиалдар» өтмөгүндө филиал түзүңүз.", "Create a branch on the \"Branches\" tab first.",
                "Önce \"Şubeler\" sekmesinde bir şube oluşturun.", "Avval «Filiallar» bo'limida filial yarating.")).ConfigureAwait(true);
            return;
        }
        var dialog = new BranchTransferDialog(_branches, repeatOf, SelectedTransferBranchId());
        if (await dialog.ShowDialog<bool>(this).ConfigureAwait(true))
        {
            await ShowTabAsync("transfers").ConfigureAwait(true);
            await LoadTransfersAsync(reset: true).ConfigureAwait(true);
        }
    }

    // ───────────────────────────────────────────── общее

    private Border Pill(string text, string background, string foreground)
    {
        var label = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold };
        Use(label, TextBlock.ForegroundProperty, foreground);
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 3), Child = label, VerticalAlignment = VerticalAlignment.Center };
        Use(pill, Border.BackgroundProperty, background);
        return pill;
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    // 2026-10-22 (модалки, пакет 4): кнопки — глаголы («Удалить филиал», «Выключить филиал», «Отменить перемещение» —
    // красные) вместо «Да/Нет/OK»; у опасного вопроса Enter не нажимает главную, фокус — на «Отмена».
    private async Task<bool> ConfirmAsync(string text, string confirmText, bool danger) =>
        await AskAsync(text, withNo: true, reasonBox: null, confirmText, null, danger).ConfigureAwait(true) != null;

    private Task InfoAsync(string text) => AskAsync(text, withNo: false, reasonBox: null);

    /// <summary>Вопрос с полем «причина». null — «Назад»/✕/Esc, иначе текст причины (может быть пустым). Действие — опасное (красная).</summary>
    private Task<string?> AskReasonAsync(string text, string confirmText, string cancelText) =>
        AskAsync(text, withNo: true, reasonBox: UiKit.Input(this, T("Причина (необязательно)", "Себеби (милдеттүү эмес)", "Reason (optional)", "Neden (isteğe bağlı)", "Sabab (ixtiyoriy)"), 44),
            confirmText, cancelText, danger: true);

    private async Task<string?> AskAsync(string text, bool withNo, TextBox? reasonBox, string? confirmText = null, string? cancelText = null, bool danger = false)
    {
        var tcs = new TaskCompletionSource<string?>();
        // 2026-10-20, этап О9б: вопрос — в общей оболочке ModalShell (✕ и Esc — как «Нет»); ответы и порядок — прежние.
        var dialog = new Window { Title = Title };
        SectionCards.AsModal(dialog, 512);
        var panel = new StackPanel { Spacing = 14 };
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        Use(label, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(label);
        if (reasonBox != null)
        {
            reasonBox.MaxLength = 500;
            panel.Children.Add(reasonBox);
        }
        var row = SectionCards.Buttons();
        var yesText = withNo
            ? confirmText ?? T("Продолжить", "Улантуу", "Continue", "Devam", "Davom etish")
            : T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli");
        var yes = danger ? SectionCards.Danger(yesText) : SectionCards.Primary(yesText, isDefault: true);
        if (!withNo)
            yes.IsCancel = true; // сообщение: Enter, Esc и ✕ закрывают его
        yes.Click += (_, _) => { tcs.TrySetResult((reasonBox?.Text ?? "").Trim()); dialog.Close(); };
        Button? no = null;
        if (withNo)
        {
            no = SectionCards.Cancel(cancelText ?? T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
            no.Click += (_, _) => { tcs.TrySetResult(null); dialog.Close(); };
            row.Children.Add(no);
        }
        dialog.Closed += (_, _) => tcs.TrySetResult(withNo ? null : "");
        row.Children.Add(yes);
        var shell = SectionCards.Shell(Title, null, panel, row);
        shell.InitialFocus = reasonBox != null ? reasonBox : danger && no != null ? no : yes;
        dialog.Content = shell;
        await dialog.ShowDialog(this).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

    internal static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        var rows = data.ValueKind == JsonValueKind.Array ? data : data.ValueKind == JsonValueKind.Object && data.TryGetProperty("results", out var r) ? r : default;
        return rows.ValueKind == JsonValueKind.Array ? rows.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }

    internal static string Money(double v) => v.ToString("N2", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    internal static string Qty(double v) => v.ToString("0.###", Ru);

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : "";

    internal static double Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>Новый филиал или правка: те же поля, что в форме сайта. Результат ShowDialog — true, если сохранено на сервере.</summary>
public sealed class BranchEditDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);
    private static readonly Regex CodePattern = new("^[-a-zA-Z0-9_]*$");

    private readonly NurMarketApiClient _api;
    private readonly BranchesWindow.Branch? _branch;
    private readonly TextBox _name;
    private readonly TextBox _code;
    private readonly TextBox _address;
    private readonly TextBox _phone;
    private readonly TextBox _email;
    // 2026-10-22 (модалки, пакет 4): 44 px — касание пальцем (было 40).
    private readonly ComboBox _timezone = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
    private readonly CheckBox _active = new() { IsChecked = true };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;

    public BranchEditDialog(BranchesWindow.Branch? branch)
    {
        _api = App.GetRequiredService<NurMarketApiClient>();
        _branch = branch;
        Title = branch == null
            ? T("Новый филиал", "Жаңы филиал", "New branch", "Yeni şube", "Yangi filial")
            : T("Изменить филиал", "Филиалды өзгөртүү", "Edit branch", "Şubeyi düzenle", "Filialni o'zgartirish");
        // 2026-10-20, этап О9б: окно в общей оболочке ModalShell (заголовок и ✕ сверху, кнопки снизу на месте). Поля — прежние.
        // 2026-10-22 (модалки, пакет 4): M 672 (было 592), поля 44 px, Enter — «Сохранить», фокус — в названии;
        // пока идёт запрос, окно занято (IsBusy): ✕/Esc/«Отмена» выключены — не закрыть посреди сохранения.
        SectionCards.AsModal(this, 672);

        var panel = new StackPanel { Spacing = 6 };

        TextBox Field(string label, string watermark, string value, int max)
        {
            panel.Children.Add(UiKit.Label(this, label));
            var box = UiKit.Input(this, watermark, 44);
            box.Text = value;
            box.MaxLength = max;
            panel.Children.Add(box);
            return box;
        }

        _name = Field(T("Название *", "Аталышы *", "Name *", "Ad *", "Nomi *"), T("Например: Филиал на Ахунбаева", "Мисалы: Ахунбаевдеги филиал", "E.g. Akhunbaev street branch", "Örn.: Ahunbaev şubesi", "Masalan: Axunboyev filiali"), branch?.Name ?? "", 128);
        _code = Field(T("Код филиала (латиница, цифры, «-», «_»)", "Филиалдын коду (латын тамгалары, сандар, «-», «_»)", "Branch code (latin letters, digits, \"-\", \"_\")",
            "Şube kodu (Latin harfler, rakamlar, \"-\", \"_\")", "Filial kodi (lotin harflari, raqamlar, «-», «_»)"), "osh, bishkek-2, online", branch?.Code ?? "", 64);
        _address = Field(T("Адрес", "Дареги", "Address", "Adres", "Manzil"), "", branch?.Address ?? "", 255);
        _phone = Field(T("Телефон", "Телефону", "Phone", "Telefon", "Telefon"), "+996 …", branch?.Phone ?? "", 32);
        _email = Field(T("Почта филиала", "Филиалдын почтасы", "Branch email", "Şube e-postası", "Filial pochtasi"), "branch@example.com", branch?.Email ?? "", 254);

        panel.Children.Add(UiKit.Label(this, T("Часовой пояс", "Убакыт алкагы", "Time zone", "Saat dilimi", "Vaqt mintaqasi")));
        _timezone.ItemsSource = BranchesWindow.Timezones.Select(x => x.Name).ToList();
        var tz = Array.FindIndex(BranchesWindow.Timezones, x => x.Id == (branch?.Timezone ?? "Asia/Bishkek"));
        _timezone.SelectedIndex = tz >= 0 ? tz : 0;
        panel.Children.Add(_timezone);

        _active.IsChecked = branch?.IsActive ?? true;
        _active.Content = T("Филиал активен", "Филиал активдүү", "Branch is active", "Şube aktif", "Filial faol");
        _active.Margin = new Thickness(0, 8, 0, 0);
        Use(_active, ForegroundProperty, "BrushText");
        panel.Children.Add(_active);

        var errorBox = SectionCards.ErrorBox(this, _error);
        errorBox.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(errorBox);

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: true);
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        var hint = new TextBlock
        {
            Text = T("* — обязательно. Филиал общий с сайтом NurCRM.", "* — милдеттүү. Филиал NurCRM сайты менен жалпы.", "* — required. The branch is shared with the NurCRM website.",
                "* — zorunlu. Şube NurCRM sitesiyle ortaktır.", "* — majburiy. Filial NurCRM sayti bilan umumiy."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, MinWidth = 140,
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        _shell = SectionCards.Shell(Title, null, panel, SectionCards.Buttons(cancel, _save), hint);
        _shell.InitialFocus = _name;
        Content = _shell;
    }

    private readonly Views.Dialogs.ModalShell _shell;

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = text.Length > 0;
    }

    private async Task SaveAsync()
    {
        if (_shell.IsBusy)
            return;
        var name = (_name.Text ?? "").Trim();
        var code = (_code.Text ?? "").Trim();
        var email = (_email.Text ?? "").Trim();
        if (name.Length == 0)
        {
            ShowError(T("Введите название филиала.", "Филиалдын аталышын жазыңыз.", "Enter the branch name.", "Şube adını girin.", "Filial nomini kiriting."));
            return;
        }
        if (!CodePattern.IsMatch(code))
        {
            ShowError(T("Код — только латинские буквы, цифры, «-» и «_».", "Код — латын тамгалары, сандар, «-» жана «_» гана.", "The code may only contain latin letters, digits, \"-\" and \"_\".",
                "Kod yalnızca Latin harfleri, rakamlar, \"-\" ve \"_\" içerebilir.", "Kod faqat lotin harflari, raqamlar, «-» va «_» dan iborat bo'lishi kerak."));
            return;
        }
        if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
        {
            ShowError(T("Почта указана неверно.", "Почта туура эмес.", "The email is not valid.", "E-posta geçersiz.", "Pochta noto'g'ri."));
            return;
        }
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["address"] = (_address.Text ?? "").Trim(),
            ["phone"] = (_phone.Text ?? "").Trim(),
            ["email"] = email.Length > 0 ? email : null,
            ["timezone"] = BranchesWindow.Timezones[Math.Max(0, _timezone.SelectedIndex)].Id,
            ["is_active"] = _active.IsChecked == true,
        };
        // Код необязателен: пустой при создании — не передаём (сервер придумает сам), при правке — стираем.
        if (code.Length > 0)
            body["code"] = code;
        else if (_branch != null)
            body["code"] = null;

        _save.IsEnabled = false;
        _shell.IsBusy = true;
        ShowError("");
        try
        {
            if (_branch == null)
                await _api.RequestAsync(HttpMethod.Post, "api/users/branches/", body, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            else
                await _api.RequestAsync(new HttpMethod("PATCH"), $"api/users/branches/{Uri.EscapeDataString(_branch.Id)}/", body, null, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            PosLogger.Log($"Филиалы: «{name}» {(_branch == null ? "создан" : "изменён")}.", "INFO");
            _shell.IsBusy = false;
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError(T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
            PosLogger.Log($"Филиалы: «{name}» не сохранён ({ex.Message}).", "WARNING");
        }
        finally
        {
            _shell.IsBusy = false;
            _save.IsEnabled = true;
        }
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>«Новое перемещение»: откуда и куда (главный склад или активный филиал), дата, товары с остатком отправителя
/// (поиск по складу отправителя, как на сайте), количество и «Всё», комментарий. Результат ShowDialog — true, если
/// перемещение проведено на сервере (POST api/main/branch-transfers/).</summary>
public sealed class BranchTransferDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private sealed class Line
    {
        public string ProductId = "";
        public string Name = "";
        public string Unit = "";
        public double Price;
        public double? Available;
        public TextBox Qty = null!;
    }

    private readonly NurMarketApiClient _api;
    private readonly List<(string? Id, string Name)> _places;
    private readonly ComboBox _from = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
    private readonly ComboBox _to = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
    private readonly TextBox _date;
    private readonly TextBox _search;
    private readonly StackPanel _results = new() { Spacing = 4 };
    private readonly StackPanel _lines = new() { Spacing = 6 };
    private readonly TextBox _comment;
    private readonly TextBlock _total = new() { FontSize = 15, FontWeight = FontWeight.Bold };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;
    private readonly Views.Dialogs.ModalShell _shell;
    private readonly List<Line> _rows = new();
    private int _searchGen;
    private int _lastFromIndex;

    public BranchTransferDialog(IReadOnlyList<BranchesWindow.Branch> branches, JsonElement? repeatOf, string? preferredBranch)
    {
        _api = App.GetRequiredService<NurMarketApiClient>();
        Title = T("Новое перемещение", "Жаңы жылдыруу", "New transfer", "Yeni transfer", "Yangi ko'chirish");
        // 2026-10-20, этап О9б: окно в общей оболочке ModalShell — итог и «Провести перемещение» внизу всегда видны,
        // содержимое прокручивается между шапкой и низом. Поля, поиск и проверки — прежние.
        // 2026-10-22 (модалки, пакет 4): L 912 (было 792), поля и кнопки строк 44 px; пока идёт запрос — окно занято (IsBusy).
        SectionCards.AsModal(this, 912);

        // Только активные филиалы — как на сайте («Отправитель/получатель неактивен»).
        _places = new List<(string? Id, string Name)> { (null, BranchesWindow.MainWarehouse) };
        _places.AddRange(branches.Where(b => b.IsActive).Select(b => ((string?)b.Id, b.Name)));

        var panel = new StackPanel { Spacing = 8 };

        var route = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,*") };
        var fromBox = new StackPanel { Spacing = 4 };
        fromBox.Children.Add(UiKit.Label(this, T("Откуда", "Кайдан", "From", "Nereden", "Qayerdan")));
        fromBox.Children.Add(_from);
        route.Children.Add(fromBox);
        var swap = UiKit.Ghost(this, "⇄");
        swap.Height = 44;
        swap.MinWidth = 44;
        swap.Margin = new Thickness(8, 22, 8, 0);
        swap.VerticalAlignment = VerticalAlignment.Top;
        ToolTip.SetTip(swap, T("Поменять местами", "Ордун алмаштыруу", "Swap", "Yer değiştir", "Almashtirish"));
        swap.Click += (_, _) => (_from.SelectedIndex, _to.SelectedIndex) = (_to.SelectedIndex, _from.SelectedIndex);
        Grid.SetColumn(swap, 1);
        route.Children.Add(swap);
        var toBox = new StackPanel { Spacing = 4 };
        toBox.Children.Add(UiKit.Label(this, T("Куда", "Кайда", "To", "Nereye", "Qayerga")));
        toBox.Children.Add(_to);
        Grid.SetColumn(toBox, 2);
        route.Children.Add(toBox);
        panel.Children.Add(route);
        _from.ItemsSource = _places.Select(x => x.Name).ToList();
        _to.ItemsSource = _places.Select(x => x.Name).ToList();

        panel.Children.Add(UiKit.Label(this, T("Дата документа (ГГГГ-ММ-ДД)", "Документтин датасы (ЖЖЖЖ-АА-КК)", "Document date (YYYY-MM-DD)", "Belge tarihi (YYYY-AA-GG)", "Hujjat sanasi (YYYY-OO-KK)")));
        _date = UiKit.Input(this, "2026-10-06", 44);
        _date.Text = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        _date.MaxWidth = 220;
        _date.HorizontalAlignment = HorizontalAlignment.Left;
        panel.Children.Add(_date);

        panel.Children.Add(UiKit.Label(this, T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar")));
        _search = UiKit.Input(this, T("Найти товар на складе отправителя: название или штрихкод", "Жөнөтүүчүнүн кампасынан товар издөө: аталышы же штрихкод",
            "Find a product in the sender's stock: name or barcode", "Gönderenin deposunda ürün ara: ad veya barkod", "Jo'natuvchi omboridan mahsulot qidirish: nomi yoki shtrix-kod"), 44);
        var searchTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        searchTimer.Tick += async (_, _) =>
        {
            searchTimer.Stop();
            await SearchAsync().ConfigureAwait(true);
        };
        _search.TextChanged += (_, _) =>
        {
            searchTimer.Stop();
            searchTimer.Start();
        };
        panel.Children.Add(_search);
        panel.Children.Add(_results);
        // 2026-10-20, этап О9б: добавленные товары — в рамке (видна, когда строки есть).
        var linesGroup = SectionCards.Group(this, _lines);
        linesGroup.IsVisible = false;
        _lines.Children.CollectionChanged += (_, _) => linesGroup.IsVisible = _lines.Children.Count > 0;
        panel.Children.Add(linesGroup);

        _comment = UiKit.Input(this, T("Комментарий (необязательно)", "Комментарий (милдеттүү эмес)", "Comment (optional)", "Yorum (isteğe bağlı)", "Izoh (ixtiyoriy)"), 44);
        _comment.MaxLength = 500;
        _comment.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(_comment);
        // 2026-10-20, этап О9б: итог — слева на строке кнопок, ошибка — красной плашкой.
        _total.FontSize = 14;
        _total.TextWrapping = TextWrapping.Wrap;
        _total.MinWidth = 140;
        Use(_total, TextBlock.ForegroundProperty, "BrushText");
        var errorBox = SectionCards.ErrorBox(this, _error);
        errorBox.Margin = new Thickness(0, 6, 0, 0);
        panel.Children.Add(errorBox);

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _save = SectionCards.Primary(T("Провести перемещение", "Жылдырууну өткөрүү", "Make the transfer", "Transferi gerçekleştir", "Ko'chirishni o'tkazish"));
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        Content = _shell = SectionCards.Shell(Title,
            T("Остатки изменятся сразу — у отправителя уменьшатся, у получателя прибавятся.", "Калдыктар дароо өзгөрөт — жөнөтүүчүдө азаят, алуучуда көбөйөт.",
                "Stock changes right away — down at the sender, up at the receiver.", "Stok hemen değişir — gönderende azalır, alıcıda artar.",
                "Qoldiqlar darhol o'zgaradi — jo'natuvchida kamayadi, qabul qiluvchida ko'payadi."),
            panel, SectionCards.Buttons(cancel, _save), _total);

        // Откуда/куда: по умолчанию главный склад → выбранный в списке филиал (или первый активный).
        var fromIndex = 0;
        var toIndex = preferredBranch != null ? Math.Max(1, _places.FindIndex(x => x.Id == preferredBranch)) : 1;
        if (repeatOf is { } source)
        {
            fromIndex = PlaceIndex(source.TryGetProperty("from_branch", out var f) ? f : default);
            toIndex = PlaceIndex(source.TryGetProperty("to_branch", out var t) ? t : default);
            if (BranchesWindow.Str(source, "comment") is { Length: > 0 } comment)
                _comment.Text = comment;
        }
        _from.SelectedIndex = fromIndex;
        _to.SelectedIndex = Math.Min(toIndex, _places.Count - 1);
        _lastFromIndex = _from.SelectedIndex;
        _from.SelectionChanged += (_, _) =>
        {
            // Товары и остатки — со склада отправителя: другой отправитель — список заново.
            if (_from.SelectedIndex == _lastFromIndex)
                return;
            _lastFromIndex = _from.SelectedIndex;
            _rows.Clear();
            _lines.Children.Clear();
            _results.Children.Clear();
            UpdateTotal();
            _ = SearchAsync();
        };
        if (repeatOf is { } src && src.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in items.EnumerateArray())
                AddLine(BranchesWindow.Str(it, "product"), BranchesWindow.Str(it, "name"), BranchesWindow.Str(it, "unit"), BranchesWindow.Num(it, "price"), null,
                    BranchesWindow.Num(it, "quantity"));
            Opened += async (_, _) => await RefreshAvailableAsync().ConfigureAwait(true);
        }
        UpdateTotal();
    }

    private int PlaceIndex(JsonElement side)
    {
        var id = side.ValueKind == JsonValueKind.Object ? BranchesWindow.Str(side, "id") : "";
        var index = id.Length == 0 ? 0 : _places.FindIndex(x => x.Id == id);
        return index >= 0 ? index : 0;
    }

    private string? FromId => _from.SelectedIndex >= 0 && _from.SelectedIndex < _places.Count ? _places[_from.SelectedIndex].Id : null;

    private string? ToId => _to.SelectedIndex >= 0 && _to.SelectedIndex < _places.Count ? _places[_to.SelectedIndex].Id : null;

    private async Task<List<JsonElement>> ProductsAsync(string? search, int pageSize)
    {
        // Склад отправителя: филиал — branch=<id>, главный склад — branch=main (проверено 06.10: остатки разные).
        var query = new Dictionary<string, string> { ["branch"] = FromId ?? "main", ["page_size"] = pageSize.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(search))
            query["search"] = search.Trim();
        var data = await _api.RequestAsync(HttpMethod.Get, "api/main/products/list/", null, query, CancellationToken.None, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        return BranchesWindow.Rows(data).Select(x => x.Clone()).ToList();
    }

    private async Task SearchAsync()
    {
        var gen = ++_searchGen;
        var text = (_search.Text ?? "").Trim();
        _results.Children.Clear();
        if (text.Length == 0)
            return;
        try
        {
            var products = await ProductsAsync(text, 20).ConfigureAwait(true);
            if (gen != _searchGen)
                return;
            _results.Children.Clear();
            if (products.Count == 0)
            {
                _results.Children.Add(Hint(T("На складе отправителя такого товара нет.", "Жөнөтүүчүнүн кампасында мындай товар жок.", "No such product in the sender's stock.",
                    "Gönderenin deposunda böyle bir ürün yok.", "Jo'natuvchi omborida bunday mahsulot yo'q.")));
                return;
            }
            foreach (var p in products.Where(p => BranchesWindow.Str(p, "kind") is "" or "product"))
            {
                var id = BranchesWindow.Str(p, "id");
                var qty = BranchesWindow.Num(p, "quantity");
                var unit = BranchesWindow.Str(p, "unit");
                var button = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8),
                    MinHeight = 44, // 2026-10-22 (модалки, пакет 4): строка найденного товара — 44 px
                    CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Focusable = false, IsEnabled = qty > 0 && _rows.All(r => r.ProductId != id),
                    Content = new TextBlock
                    {
                        Text = $"{BranchesWindow.Str(p, "name")}  ·  {T("есть", "бар", "in stock", "stokta", "bor")}: {BranchesWindow.Qty(qty)} {(unit.Length > 0 ? unit : T("шт", "даана", "pcs", "adet", "dona"))}"
                               + (BranchesWindow.Str(p, "barcode") is { Length: > 0 } bc ? "  ·  " + bc : ""),
                        TextWrapping = TextWrapping.Wrap, FontSize = 13.5,
                    },
                };
                Use(button, Button.BackgroundProperty, "BrushPanel");
                Use(button, Button.BorderBrushProperty, "BrushBorder");
                Use(button, Button.ForegroundProperty, "BrushText");
                button.Click += (_, _) =>
                {
                    // Накладная сервера считает по закупочной цене (проверено 06.10: «Патроны» 28 сом при цене продажи 40).
                    AddLine(id, BranchesWindow.Str(p, "name"), unit,
                        BranchesWindow.Num(p, "purchase_price") is > 0 and var cost ? cost : BranchesWindow.Num(p, "price"), qty, 1);
                    _search.Text = "";
                    _results.Children.Clear();
                };
                _results.Children.Add(button);
            }
        }
        catch (Exception ex)
        {
            if (gen != _searchGen)
                return;
            _results.Children.Clear();
            _results.Children.Add(Hint(T("Поиск не удался: ", "Издөө ишке ашкан жок: ", "Search failed: ", "Arama başarısız: ", "Qidiruv amalga oshmadi: ") + ServerTelegramBotApi.DescribeFields(ex)));
        }
    }

    /// <summary>«Повторить»: остатки отправителя для строк прошлой накладной (по названию — id тот же склад).</summary>
    private async Task RefreshAvailableAsync()
    {
        foreach (var row in _rows.ToList())
        {
            try
            {
                var found = (await ProductsAsync(row.Name, 20).ConfigureAwait(true)).FirstOrDefault(p => BranchesWindow.Str(p, "id") == row.ProductId);
                row.Available = found.ValueKind == JsonValueKind.Object ? BranchesWindow.Num(found, "quantity") : 0;
            }
            catch
            {
                row.Available = null;
            }
        }
        RenderLines();
    }

    private void AddLine(string productId, string name, string unit, double price, double? available, double qty)
    {
        if (productId.Length == 0 || _rows.Any(r => r.ProductId == productId))
            return;
        var box = UiKit.Input(this, "0", 44);
        box.Width = 110;
        box.Text = BranchesWindow.Qty(available is { } a && qty > a ? a : qty);
        box.TextChanged += (_, _) => UpdateTotal();
        _rows.Add(new Line { ProductId = productId, Name = name, Unit = unit, Price = price, Available = available, Qty = box });
        RenderLines();
    }

    private void RenderLines()
    {
        _lines.Children.Clear();
        foreach (var r in _rows)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = r.Name.Length > 0 ? r.Name : "—", FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
            Use(title, TextBlock.ForegroundProperty, "BrushText");
            info.Children.Add(title);
            var unit = r.Unit.Length > 0 ? r.Unit : T("шт", "даана", "pcs", "adet", "dona");
            info.Children.Add(Hint((r.Available is { } a
                ? T($"есть: {BranchesWindow.Qty(a)} {unit}", $"бар: {BranchesWindow.Qty(a)} {unit}", $"in stock: {BranchesWindow.Qty(a)} {unit}", $"stokta: {BranchesWindow.Qty(a)} {unit}", $"bor: {BranchesWindow.Qty(a)} {unit}")
                : T("остаток проверит сервер", "калдыкты сервер текшерет", "the server will check stock", "stoku sunucu kontrol eder", "qoldiqni server tekshiradi")) + " · " + BranchesWindow.Money(r.Price)));
            row.Children.Add(info);
            r.Qty.Margin = new Thickness(8, 0, 0, 0);
            (r.Qty.Parent as Panel)?.Children.Remove(r.Qty);
            Grid.SetColumn(r.Qty, 1);
            row.Children.Add(r.Qty);
            var all = UiKit.Ghost(this, T("Всё", "Баары", "All", "Tümü", "Hammasi"));
            all.Height = 44;
            all.Margin = new Thickness(6, 0, 0, 0);
            all.IsEnabled = r.Available is > 0;
            ToolTip.SetTip(all, T("Переместить весь остаток", "Бүт калдыкты жылдыруу", "Move the whole stock", "Tüm stoku taşı", "Butun qoldiqni ko'chirish"));
            var line = r;
            all.Click += (_, _) => line.Qty.Text = BranchesWindow.Qty(line.Available ?? 0);
            Grid.SetColumn(all, 2);
            row.Children.Add(all);
            var remove = UiKit.Ghost(this, "✕");
            remove.Height = 44;
            remove.MinWidth = 44;
            remove.Margin = new Thickness(6, 0, 0, 0);
            ToolTip.SetTip(remove, T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"));
            remove.Click += (_, _) =>
            {
                _rows.Remove(line);
                RenderLines();
            };
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            _lines.Children.Add(row);
        }
        UpdateTotal();
    }

    private static double ParseQty(string? text) =>
        double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    private void UpdateTotal()
    {
        var qty = _rows.Sum(r => ParseQty(r.Qty.Text) is var q && q > 0 ? q : 0);
        var sum = _rows.Sum(r => (ParseQty(r.Qty.Text) is var q && q > 0 ? q : 0) * r.Price);
        _total.Text = _rows.Count == 0
            ? T("Добавьте товары: найдите их по названию или штрихкоду.", "Товар кошуңуз: аталышы же штрихкоду боюнча табыңыз.", "Add products: find them by name or barcode.",
                "Ürün ekleyin: ad veya barkodla bulun.", "Mahsulot qo'shing: nomi yoki shtrix-kodi bo'yicha toping.")
            : T($"Итого: {_rows.Count} поз., {BranchesWindow.Qty(qty)} ед. на {BranchesWindow.Money(sum)}", $"Жыйынтык: {_rows.Count} позиция, {BranchesWindow.Qty(qty)} бирдик, {BranchesWindow.Money(sum)}",
                $"Total: {_rows.Count} items, {BranchesWindow.Qty(qty)} units for {BranchesWindow.Money(sum)}", $"Toplam: {_rows.Count} kalem, {BranchesWindow.Qty(qty)} birim, {BranchesWindow.Money(sum)}",
                $"Jami: {_rows.Count} pozitsiya, {BranchesWindow.Qty(qty)} birlik, {BranchesWindow.Money(sum)}");
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = text.Length > 0;
    }

    private async Task SaveAsync()
    {
        if (_from.SelectedIndex == _to.SelectedIndex)
        {
            ShowError(T("Отправитель и получатель должны отличаться.", "Жөнөтүүчү менен алуучу ар башка болушу керек.", "The sender and the receiver must be different.",
                "Gönderen ve alıcı farklı olmalı.", "Jo'natuvchi va qabul qiluvchi har xil bo'lishi kerak."));
            return;
        }
        if (!DateTime.TryParseExact((_date.Text ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            ShowError(T("Укажите дату в виде 2026-10-06.", "Датаны 2026-10-06 түрүндө жазыңыз.", "Enter the date as 2026-10-06.", "Tarihi 2026-10-06 biçiminde girin.", "Sanani 2026-10-06 ko'rinishida kiriting."));
            return;
        }
        if (_rows.Count == 0)
        {
            ShowError(T("Добавьте товары.", "Товар кошуңуз.", "Add products.", "Ürün ekleyin.", "Mahsulot qo'shing."));
            return;
        }
        var bad = _rows.Where(r => ParseQty(r.Qty.Text) is var q && (double.IsNaN(q) || q <= 0 || (r.Available is { } a && q > a + 1e-9))).Select(r => r.Name).ToList();
        if (bad.Count > 0)
        {
            ShowError(T("Исправьте количество (больше нуля и не больше остатка): ", "Санын оңдоңуз (нөлдөн көп жана калдыктан ашпасын): ", "Fix the quantity (above zero and not above stock): ",
                "Miktarı düzeltin (sıfırdan büyük ve stoktan fazla olmayan): ", "Miqdorni to'g'rilang (noldan ko'p va qoldiqdan oshmasin): ") + string.Join(", ", bad));
            return;
        }
        ShowError("");
        var qtyTotal = _rows.Sum(r => ParseQty(r.Qty.Text));
        var fromName = _places[_from.SelectedIndex].Name;
        var toName = _places[_to.SelectedIndex].Name;
        if (!await ConfirmAsync(T($"Провести перемещение: {fromName} → {toName}, {_rows.Count} поз., {BranchesWindow.Qty(qtyTotal)} ед.? Остатки изменятся сразу.",
                $"Жылдырууну өткөрөсүзбү: {fromName} → {toName}, {_rows.Count} позиция, {BranchesWindow.Qty(qtyTotal)} бирдик? Калдыктар дароо өзгөрөт.",
                $"Make the transfer: {fromName} → {toName}, {_rows.Count} items, {BranchesWindow.Qty(qtyTotal)} units? Stock changes right away.",
                $"Transfer yapılsın mı: {fromName} → {toName}, {_rows.Count} kalem, {BranchesWindow.Qty(qtyTotal)} birim? Stok hemen değişir.",
                $"Ko'chirish o'tkazilsinmi: {fromName} → {toName}, {_rows.Count} pozitsiya, {BranchesWindow.Qty(qtyTotal)} birlik? Qoldiqlar darhol o'zgaradi.")).ConfigureAwait(true))
            return;

        // Как у сайта: главный склад — null, количество — число.
        var body = new Dictionary<string, object?>
        {
            ["from_branch"] = FromId,
            ["to_branch"] = ToId,
            ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["items"] = _rows.Select(r => new Dictionary<string, object?> { ["product"] = r.ProductId, ["quantity"] = Math.Round(ParseQty(r.Qty.Text), 3) }).ToList(),
        };
        if ((_comment.Text ?? "").Trim() is { Length: > 0 } comment)
            body["comment"] = comment;
        if (!_shell.TryBeginBusy())
            return;
        _save.IsEnabled = false;
        try
        {
            var data = await _api.RequestAsync(HttpMethod.Post, "api/main/branch-transfers/", body, null, CancellationToken.None, TimeSpan.FromSeconds(45)).ConfigureAwait(true);
            PosLogger.Log($"Перемещение {BranchesWindow.Str(data, "number")}: {fromName} → {toName}, {_rows.Count} поз., {qtyTotal:0.###} ед.", "INFO");
            _shell.EndBusy();
            Close(true);
        }
        catch (Exception ex)
        {
            ShowError(T("Не проведено: ", "Өткөрүлгөн жок: ", "Not done: ", "Gerçekleştirilmedi: ", "O'tkazilmadi: ") + ServerTelegramBotApi.DescribeFields(ex));
            PosLogger.Log($"Перемещение не проведено ({ex.Message}).", "WARNING");
        }
        finally
        {
            _shell.EndBusy();
            _save.IsEnabled = true;
        }
    }

    private async Task<bool> ConfirmAsync(string text)
    {
        var tcs = new TaskCompletionSource<bool>();
        // 2026-10-20, этап О9б: подтверждение — в общей оболочке ModalShell (✕ и Esc — как «Нет»).
        var dialog = new Window { Title = Title };
        SectionCards.AsModal(dialog, 512);
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        Use(label, TextBlock.ForegroundProperty, "BrushText");
        // 2026-10-22 (модалки, пакет 4): «Отмена» вместо «Нет», Enter — «Провести», фокус на ней.
        var yes = SectionCards.Primary(T("Провести", "Өткөрүү", "Do it", "Gerçekleştir", "O'tkazish"), isDefault: true);
        var no = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        yes.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);
        var shell = SectionCards.Shell(Title, null, label, SectionCards.Buttons(no, yes));
        shell.InitialFocus = yes;
        dialog.Content = shell;
        await dialog.ShowDialog(this).ConfigureAwait(true);
        return await tcs.Task.ConfigureAwait(true);
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
