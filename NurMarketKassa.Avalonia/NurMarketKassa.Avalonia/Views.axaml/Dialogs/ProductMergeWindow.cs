using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.ProductMerge;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Зависимости окна «Объединение товаров» (подменяются в проверках: каталог, сервер, права, подтверждения, файлы).</summary>
public sealed class ProductMergeDeps
{
    /// <summary>Товары для поиска групп — из локального каталога кассы.</summary>
    public Func<IReadOnlyList<MergeProduct>> Products { get; init; } = () => CatalogCacheService.Products.ToList().Select(MergeProduct.From).ToList();

    /// <summary>Сервер для слияния; null — нет входа/связи. Признаки (архив товаров, себестоимость по партиям) читаются здесь же.</summary>
    public Func<Task<IProductMergeServer?>> CreateServer { get; init; } = DefaultServerAsync;

    /// <summary>Объединять может только владелец или администратор (ActionGuard.IsSenior: они PIN не вводят — они и есть старшие).</summary>
    public Func<bool> CanMerge { get; init; } = () => ActionGuard.IsSenior();

    public Func<bool> IsOffline { get; init; } = () => OfflineModeHelper.SellLocally;

    public Func<string> Who { get; init; } = () => PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId ?? "—";

    public Func<string> Account { get; init; } = () => UserPreferences.Instance.LastDataAccountKey ?? "";

    /// <summary>Вопрос «да/нет»: окно, текст, заголовок, «да», «нет», опасное ли. Подменяется в проверках.</summary>
    public Func<Window, string, string, string, string, bool, Task<bool>> Confirm { get; init; } = (owner, text, title, yes, no, danger) =>
        PosDialogs.ConfirmYesNoModalAsync(owner, text, title, confirmText: yes, cancelText: no, danger: danger, enterCancels: true);

    /// <summary>После слияния: источники убрать из каталога кассы (как после архива в «Складе»), главные перечитать с сервера.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<string>, Task> AfterMerge { get; init; } = DefaultAfterMergeAsync;

    /// <summary>Сохранить план в CSV: окно, имя файла, запись по пути → итоговая подпись (путь или имя) или null при отмене.</summary>
    public Func<Window, string, Action<string>, Task<string?>> SaveCsv { get; init; } = DefaultSaveCsvAsync;

    private static async Task<IProductMergeServer?> DefaultServerAsync()
    {
        var catalog = App.AppHost?.Services.GetService(typeof(ICatalogApiService)) as ICatalogApiService;
        var client = App.AppHost?.Services.GetService(typeof(NurMarketApiClient)) as NurMarketApiClient;
        if (catalog is null || client is null || string.IsNullOrEmpty(client.AccessToken) || ProductActions.InventoryApi is not { } inventory)
            return null;
        var features = await ServerCapabilities.LoadAsync(client).ConfigureAwait(true);
        // 2026-11-21 (ответ Умара 21.11): серверное слияние POST main/products/merge/ есть. Признаки компании не угадываем — поддержку определяет проба dry_run (ProductMergeApi.Supported, на сессию);
        // старый сервер (404/405) — запасной ручной путь через карточки.
        return new MergeServerAdapter(catalog, inventory, features.Contains("product_archive"), features.Contains(ServerCapabilities.CostingFifo), new ProductMergeApi(client));
    }

    private static async Task DefaultAfterMergeAsync(IReadOnlyList<string> sourceIds, IReadOnlyList<string> mainIds)
    {
        foreach (var id in sourceIds)
        {
            try
            {
                if (CatalogCacheService.Products.ToList().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) is { } tile)
                    RemovedProductMemory.Remember(tile, archived: true); // аналитика прошлых периодов не теряет закупку, категорию и бренд
                LocalProductRepository.Instance.DeleteProduct(id);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Слияние товаров: источник {id} не убран из каталога кассы ({ex.Message}).", "WARNING");
            }
        }
        try
        {
            CatalogCacheService.LoadFromDatabase();
            CatalogCacheService.NotifyCatalogChanged();
            await CatalogCacheService.RefreshProductsAsync(mainIds).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Слияние товаров: каталог не обновлён ({ex.Message}).", "WARNING");
        }
    }

    private static async Task<string?> DefaultSaveCsvAsync(Window owner, string suggested, Action<string> write)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Tr.T("Сохранить план объединения", "Бириктирүү планын сактоо", "Save the merge plan", "Birleştirme planını kaydet", "Birlashtirish rejasini saqlash"),
            SuggestedFileName = suggested,
            DefaultExtension = "csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
        }).ConfigureAwait(true);
        if (file is null)
            return null;
        var path = ExportFileBridge.PathFor(file);
        write(path);
        return await ExportFileBridge.CompleteAsync(file, path).ConfigureAwait(true);
    }
}

/// <summary>
/// 2026-11-21 (просьба владельца «исправь в нашем складе уже загруженных товаров»): окно «Объединение товаров» — вкусы и цвета одного товара, загруженные отдельными карточками
/// («Асу», «Асу клубника», «Асу лимон»), сводятся в один товар с доп. штрихкодами. Слева — группы-кандидаты (поиск, фильтр, отметки для массового слияния), справа — состав группы
/// (главный, «в слиянии», названия вариантов) и «Предпросмотр»: что станет с кодами, остатком, закупкой, ценой и источниками. «Объединять не надо» запоминает отказ.
/// Само слияние — <see cref="ProductMergeRunner"/> (бэкап до первой правки, журнал, откат); окно только показывает и спрашивает. Без архива на сервере, без связи или без прав —
/// режим «только подготовка»: группы и CSV доступны, «Объединить» выключена с понятной причиной. Касса: Склад → «Дубли и варианты»; программа владельца — тот же Склад.
/// </summary>
public sealed class ProductMergeWindow : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private const int MaxRows = 200;

    private readonly ProductMergeDeps _deps;
    private readonly ModalShell _shell = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new();
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly StackPanel _detail = new() { Spacing = 10 };
    private readonly TextBlock _status = new();
    private readonly TextBlock _banner = new();
    private readonly Button _mergeChecked;
    private readonly Button _csv;
    private readonly Button _journal;
    private readonly Button _close;
    private List<MergeGroup> _groups = [];
    private readonly Dictionary<string, MergeDraft> _drafts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _checked = new(StringComparer.Ordinal);
    private readonly HashSet<string> _merged = new(StringComparer.Ordinal);
    private HashSet<string> _declined = new(StringComparer.Ordinal);
    private string? _selectedId;
    private IProductMergeServer? _server;
    private string _modeProblem = "";
    private bool _loading;

    public ProductMergeWindow() : this(null) { }

    public ProductMergeWindow(ProductMergeDeps? deps)
    {
        _deps = deps ?? new ProductMergeDeps();
        Title = T("Объединение товаров", "Товарларды бириктирүү", "Merge products", "Ürünleri birleştirme", "Mahsulotlarni birlashtirish");
        SectionCards.AsModal(this, 1040);
        SizeToContent = SizeToContent.Manual;
        Height = 600;
        Opened += async (_, _) =>
        {
            this.FitToKassaScreen();
            await ReloadAsync().ConfigureAwait(true);
        };

        _mergeChecked = ServiceDeskUi.Action("MergeCheckedButton", "", () => _ = MergeCheckedAsync(), primary: true);
        _mergeChecked.Margin = new Thickness(0);
        _csv = ServiceDeskUi.Action("MergeCsvButton", T("План в CSV", "План CSV", "Plan to CSV", "Planı CSV", "Reja CSV"), () => _ = ExportCsvAsync());
        _csv.Margin = new Thickness(0, 0, 8, 0);
        _journal = ServiceDeskUi.Action("MergeJournalButton", T("Журнал и откат", "Журнал жана кайтаруу", "Journal and rollback", "Günlük ve geri alma", "Jurnal va qaytarish"), () => _ = OpenJournalAsync());
        _journal.Margin = new Thickness(0, 0, 8, 0);
        _close = ServiceDeskUi.Action("MergeCloseButton", T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), Close);
        _close.IsCancel = true;
        _close.Margin = new Thickness(0, 0, 8, 0);
        BuildUi();
        UpdateMergeButton();
    }

    public static async Task OpenAsync(Window owner, ProductMergeDeps? deps = null)
    {
        var w = new ProductMergeWindow(deps);
        await w.ShowDialog(owner).ConfigureAwait(true);
    }

    // ───────────── наружу для проверок ─────────────

    public IReadOnlyList<MergeGroup> Groups => _groups;

    public string? SelectedId => _selectedId;

    public string StatusMessage => _status.Text ?? "";

    public string BannerMessage => _banner.Text ?? "";

    public bool CanMergeNow => _modeProblem.Length == 0;

    public Task ReloadForTests() => ReloadAsync();

    public void Select(string? groupId)
    {
        _selectedId = groupId;
        RenderList();
        RenderDetail();
    }

    public void SetChecked(string groupId, bool value)
    {
        if (value)
            _checked.Add(groupId);
        else
            _checked.Remove(groupId);
        RenderList();
    }

    // ───────────── построение ─────────────

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;
        _shell.ContentScroll = ScrollBarVisibility.Disabled;

        _search.Name = "MergeSearch";
        _search.Height = 40;
        _search.Watermark = T("Название или штрихкод", "Аталышы же штрихкод", "Name or barcode", "Ad veya barkod", "Nomi yoki shtrix-kod");
        _search.TextChanged += (_, _) => { RenderList(); };
        Style(_search);
        _filter.Name = "MergeFilter";
        _filter.Height = 40;
        _filter.HorizontalAlignment = HorizontalAlignment.Stretch;
        _filter.ItemsSource = new[]
        {
            T("Все группы", "Бардык топтор", "All groups", "Tüm gruplar", "Barcha guruhlar"),
            T("Только уверенные", "Ишенимдүүлөр гана", "Confident only", "Yalnızca güvenilir", "Faqat ishonchli"),
            T("Цены разные", "Баалары ар башка", "Prices differ", "Fiyatlar farklı", "Narxlar har xil"),
            T("Скрытые (отказы)", "Жашырылган (баш тартуу)", "Hidden (declined)", "Gizli (reddedilen)", "Yashirilgan (rad etilgan)"),
        };
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => RenderList();

        var markRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Margin = new Thickness(0, 0, 0, 8) };
        var mark = ServiceDeskUi.Action("MergeMarkConfident", T("Отметить уверенные", "Ишенимдүүлөрдү белгилөө", "Tick confident", "Güvenilirleri işaretle", "Ishonchlilarni belgilash"), MarkConfident);
        var unmark = ServiceDeskUi.Action("MergeUnmarkAll", T("Снять отметки", "Белгилерди алуу", "Clear ticks", "İşaretleri kaldır", "Belgilarni olib tashlash"), () => { _checked.Clear(); RenderList(); });
        mark.Margin = new Thickness(0, 0, 4, 0);
        unmark.Margin = new Thickness(4, 0, 0, 0);
        mark.HorizontalAlignment = HorizontalAlignment.Stretch;
        unmark.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(mark, 0);
        Grid.SetColumn(unmark, 1);
        markRow.Children.Add(mark);
        markRow.Children.Add(unmark);

        var tools = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(_search);
        tools.Children.Add(_filter);
        tools.Children.Add(markRow);
        var left = new DockPanel { Width = 320, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(new ScrollViewer { Name = "MergeListScroll", Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        _status.Name = "MergeStatusText";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12.5;
        ServiceDeskUi.Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _banner.Name = "MergeBannerText";
        _banner.TextWrapping = TextWrapping.Wrap;
        _banner.FontSize = 12.5;
        _banner.FontWeight = FontWeight.SemiBold;
        ServiceDeskUi.Use(_banner, TextBlock.ForegroundProperty, "BrushWarning");
        var top = new StackPanel { Spacing = 4, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(_banner);
        top.Children.Add(_status);
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top);
        right.Children.Add(new ScrollViewer { Name = "MergeDetailScroll", Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        var body = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        body.Children.Add(left);
        body.Children.Add(right);
        _shell.Content = body;

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var leftButtons = new StackPanel { Orientation = Orientation.Horizontal };
        leftButtons.Children.Add(_journal);
        leftButtons.Children.Add(_csv);
        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        rightButtons.Children.Add(_close);
        rightButtons.Children.Add(_mergeChecked);
        Grid.SetColumn(leftButtons, 0);
        Grid.SetColumn(rightButtons, 2);
        footer.Children.Add(leftButtons);
        footer.Children.Add(rightButtons);
        _shell.Footer = footer;
        Content = _shell;
    }

    private static void Style(TextBox box)
    {
        ServiceDeskUi.Use(box, BackgroundProperty, "BrushInput");
        ServiceDeskUi.Use(box, ForegroundProperty, "BrushText");
        ServiceDeskUi.Use(box, BorderBrushProperty, "BrushInputBorder");
    }

    // ───────────── загрузка ─────────────

    private async Task ReloadAsync()
    {
        if (_loading)
            return;
        _loading = true;
        _status.Text = T("Ищу дубли и варианты в каталоге…", "Каталогдон кайталанмаларды жана варианттарды издеп жатам…", "Looking for duplicates and variants in the catalog…", "Katalogda tekrarlar ve varyantlar aranıyor…", "Katalogda takrorlar va variantlar qidirilmoqda…");
        try
        {
            var products = _deps.Products().Where(p => !_merged.Contains(p.Id)).ToList();
            _declined = MergeStore.Declined();
            _groups = await Task.Run(() => MergeCandidateFinder.Find(products)).ConfigureAwait(true);
            _drafts.Clear();
            var live = _groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
            _checked.RemoveWhere(id => !live.Contains(id));
            _checks.Clear(); // 2026-11-21: проверки сервера (dry_run) устаревают после слияний
            foreach (var stale in _blocked.Keys.Where(k => !live.Contains(k)).ToList())
                _blocked.Remove(stale);
            if (_selectedId is not null && !live.Contains(_selectedId))
                _selectedId = null;
            await RefreshModeAsync().ConfigureAwait(true);
            var visible = _groups.Count(g => !_declined.Contains(g.Id));
            _status.Text = _groups.Count == 0
                ? T($"Просмотрено товаров: {products.Count}. Дублей и вариантов не найдено.", $"Каралган товарлар: {products.Count}. Кайталанмалар жана варианттар табылган жок.", $"Products checked: {products.Count}. No duplicates or variants found.", $"Taranan ürün: {products.Count}. Tekrar veya varyant bulunamadı.", $"Ko'rilgan mahsulotlar: {products.Count}. Takror yoki variant topilmadi.")
                : T($"Просмотрено товаров: {products.Count}. Найдено групп: {visible}. Выберите группу слева, проверьте «Предпросмотр» и объедините.", $"Каралган товарлар: {products.Count}. Табылган топтор: {visible}. Сол жактан топту тандап, «Алдын ала көрүүнү» текшерип, бириктириңиз.",
                    $"Products checked: {products.Count}. Groups found: {visible}. Pick a group on the left, check the preview and merge.", $"Taranan ürün: {products.Count}. Bulunan grup: {visible}. Soldan grubu seçin, önizlemeyi kontrol edip birleştirin.", $"Ko'rilgan mahsulotlar: {products.Count}. Topilgan guruhlar: {visible}. Chapdan guruhni tanlang, oldindan ko'rishni tekshirib, birlashtiring.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Слияние товаров: поиск групп не удался ({ex.Message}).", "WARNING");
            _status.Text = T("Не удалось просмотреть каталог: " + ex.Message, "Каталогду карап чыгуу мүмкүн болгон жок: " + ex.Message, "Could not scan the catalog: " + ex.Message, "Katalog taranamadı: " + ex.Message, "Katalogni ko'rib bo'lmadi: " + ex.Message);
        }
        finally
        {
            _loading = false;
        }
        RenderList();
        RenderDetail();
    }

    /// <summary>Что мешает «Объединить» прямо сейчас: нет прав, нет связи, у сервера нет архива. Пусто — можно.</summary>
    private async Task RefreshModeAsync()
    {
        _server = null;
        if (!_deps.CanMerge())
            _modeProblem = MergeTexts.NoRightsReason();
        else if (_deps.IsOffline())
            _modeProblem = MergeTexts.OfflineReason();
        else
        {
            try { _server = await _deps.CreateServer().ConfigureAwait(true); }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                PosLogger.Log($"Слияние товаров: сервер не подготовлен ({ex.Message}).", "WARNING");
            }
            _modeProblem = _server is null ? MergeTexts.OfflineReason() : !_server.CanArchive && _server.ServerMergeSupported != true ? MergeTexts.PrepareOnlyReason() : "";
        }
        UpdateBanner();
        UpdateMergeButton();
    }

    /// <summary>Верхняя строка: что мешает объединять, а при старом сервере (404/405 на пробе) — «ручной режим».</summary>
    private void UpdateBanner()
    {
        var text = _modeProblem.Length > 0 ? _modeProblem : _server?.ServerMergeSupported == false ? MergeTexts.ManualModeBanner() : "";
        _banner.Text = text;
        _banner.IsVisible = text.Length > 0;
    }

    // ───────────── проверка на сервере (dry_run) ─────────────

    private readonly Dictionary<string, MergeCheck> _checks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _blocked = new(StringComparer.Ordinal);
    private Task _preflight = Task.CompletedTask;

    /// <summary>Для проверок: дождаться проверки выбранной группы на сервере.</summary>
    public Task PreflightDone => _preflight;

    private static string CheckKey(MergePlan p) => p.Main!.Id + ">" + string.Join(",", p.SourceIds.OrderBy(x => x, StringComparer.Ordinal));

    /// <summary>2026-11-21: при выборе группы — dry_run на сервере: заранее видно, объединит ли сервер («не объединит: есть варианты размер/цвет, упаковки, разные единицы, партии»).
    /// «Не объединит» — группа помечена в списке, «Объединить» выключена. Старый сервер (404/405) — баннер «ручной режим». Ничего на сервере не меняется.</summary>
    private async Task PreflightAsync(MergeGroup g, MergePlan plan, TextBlock text, Button mergeOne)
    {
        if (_server is null || _modeProblem.Length > 0 || !plan.CanRun || plan.Main is null)
            return;
        var key = CheckKey(plan);
        if (!_checks.TryGetValue(key, out var check))
        {
            text.Text = MergeTexts.ServerCheckPending();
            text.IsVisible = true;
            check = await new ProductMergeRunner(_server, _deps.Who(), _deps.Account()).CheckAsync(plan).ConfigureAwait(true);
            if (check.Kind != MergeCheckKind.Error)
                _checks[key] = check;
        }
        var wasBlocked = _blocked.ContainsKey(g.Id);
        if (check.Kind == MergeCheckKind.Blocked)
            _blocked[g.Id] = check.Reasons;
        else if (check.Kind != MergeCheckKind.Error)
            _blocked.Remove(g.Id);
        UpdateBanner();
        if (_blocked.ContainsKey(g.Id) != wasBlocked)
            RenderList();
        if (Selected?.Id != g.Id)
            return;
        if (check.Kind is MergeCheckKind.Server or MergeCheckKind.Manual)
        {
            // группа раньше помечалась «не объединит», а теперь можно — вернуть кнопку и цвет
            ServiceDeskUi.Use(text, TextBlock.ForegroundProperty, "BrushTextSoft");
            mergeOne.IsEnabled = plan.CanRun && _modeProblem.Length == 0;
            ToolTip.SetTip(mergeOne, null);
        }
        switch (check.Kind)
        {
            case MergeCheckKind.Server:
                text.Text = MergeTexts.ServerCheckOk();
                text.IsVisible = true;
                break;
            case MergeCheckKind.Blocked:
                text.Text = "⚠ " + MergeTexts.ServerCheckBlocked(check.Reasons);
                text.IsVisible = true;
                ServiceDeskUi.Use(text, TextBlock.ForegroundProperty, "BrushDanger");
                mergeOne.IsEnabled = false;
                ToolTip.SetTip(mergeOne, MergeTexts.ServerCheckBlocked(check.Reasons));
                break;
            case MergeCheckKind.Error:
                text.Text = check.Message;
                text.IsVisible = true;
                break;
            default:
                text.IsVisible = false; // ручной режим — виден в баннере
                break;
        }
    }

    // ───────────── список групп ─────────────

    private IEnumerable<MergeGroup> Visible()
    {
        var q = (_search.Text ?? "").Trim();
        var f = _filter.SelectedIndex;
        foreach (var g in _groups)
        {
            var declined = _declined.Contains(g.Id);
            if (f == 3 ? !declined : declined)
                continue;
            if (f == 1 && (g.Confidence != MergeConfidence.High || g.PricesDiffer))
                continue;
            if (f == 2 && !g.PricesDiffer)
                continue;
            if (q.Length > 0 && !(g.BaseTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                  || g.Members.Any(m => m.Product.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || m.Product.AllCodes().Any(c => c.Contains(q, StringComparison.OrdinalIgnoreCase)))))
                continue;
            yield return g;
        }
    }

    private void MarkConfident()
    {
        foreach (var g in Visible().Where(g => g.Confidence == MergeConfidence.High && !g.PricesDiffer && g.MergeableCount >= 2))
            _checked.Add(g.Id);
        RenderList();
    }

    private MergeDraft DraftOf(MergeGroup g)
    {
        if (!_drafts.TryGetValue(g.Id, out var d))
            _drafts[g.Id] = d = MergePlanner.DefaultDraft(g);
        return d;
    }

    private void RenderList()
    {
        _list.Children.Clear();
        var rows = Visible().ToList();
        if (rows.Count == 0)
            _list.Children.Add(ServiceDeskUi.Text(_groups.Count == 0
                ? T("Групп нет. Дубли и варианты появятся, когда в каталоге будут товары вроде «Асу», «Асу клубника», «Асу лимон».", "Топтор жок. «Асу», «Асу кулпунай», «Асу лимон» сыяктуу товарлар болгондо кайталанмалар жана варианттар пайда болот.",
                    "No groups. Duplicates and variants appear when the catalog has products like “Asu”, “Asu strawberry”, “Asu lemon”.", "Grup yok. Katalogda «Asu», «Asu çilek», «Asu limon» gibi ürünler olunca tekrarlar ve varyantlar görünür.", "Guruh yo'q. Katalogda «Asu», «Asu qulupnay», «Asu limon» kabi mahsulotlar bo'lsa, takror va variantlar paydo bo'ladi.")
                : T("Ничего не найдено.", "Эч нерсе табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi."), 13, brush: "BrushTextSoft"));
        foreach (var g in rows.Take(MaxRows))
            _list.Children.Add(BuildRow(g));
        if (rows.Count > MaxRows)
            _list.Children.Add(ServiceDeskUi.Text(T($"Показано {MaxRows} из {rows.Count}. Уточните поиск.", $"{rows.Count} ичинен {MaxRows} көрсөтүлдү. Издөөнү тактаңыз.", $"Showing {MaxRows} of {rows.Count}. Narrow the search.", $"{rows.Count} içinden {MaxRows} gösteriliyor. Aramayı daraltın.", $"{rows.Count} dan {MaxRows} ko'rsatildi. Qidiruvni aniqlashtiring."), 12, brush: "BrushTextSoft"));
        UpdateMergeButton();
    }

    private Control BuildRow(MergeGroup g)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var check = new CheckBox { Name = "MergeGroupCheck_" + g.Id, IsChecked = _checked.Contains(g.Id), VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, Margin = new Thickness(0, 0, 6, 0) };
        Avalonia.Automation.AutomationProperties.SetName(check, T("Отметить для объединения: ", "Бириктирүү үчүн белгилөө: ", "Tick for merge: ", "Birleştirme için işaretle: ", "Birlashtirish uchun belgilash: ") + g.BaseTitle);
        var id = g.Id;
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == true)
                _checked.Add(id);
            else
                _checked.Remove(id);
            UpdateMergeButton();
        };
        var line1 = ServiceDeskUi.Text(g.BaseTitle, 14, true, wrap: false);
        line1.TextTrimming = TextTrimming.CharacterEllipsis;
        var line2 = ServiceDeskUi.Text(T($"Товаров: {g.Members.Count} · ", $"Товар: {g.Members.Count} · ", $"Products: {g.Members.Count} · ", $"Ürün: {g.Members.Count} · ", $"Mahsulot: {g.Members.Count} · ")
            + MergeTexts.Confidence(g.Confidence) + $" {g.Score}%", 12, brush: "BrushTextSoft", wrap: false);
        line2.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(line1);
        panel.Children.Add(line2);
        if (g.PricesDiffer)
            panel.Children.Add(ServiceDeskUi.Chip(T("цены разные", "баалары ар башка", "prices differ", "fiyatlar farklı", "narxlar har xil"), false));
        if (_blocked.ContainsKey(g.Id))
            panel.Children.Add(ServiceDeskUi.Chip(MergeTexts.ServerCheckBlockedChip(), false));
        var b = new Button { Name = "MergeGroupRow_" + g.Id, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8) };
        ServiceDeskUi.Use(b, BackgroundProperty, g.Id == _selectedId ? "BrushAccentSoft" : "BrushPanelSoft");
        ServiceDeskUi.Use(b, ForegroundProperty, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(b, g.BaseTitle + " " + MergeTexts.Confidence(g.Confidence));
        b.Click += (_, _) => Select(id);
        Grid.SetColumn(check, 0);
        Grid.SetColumn(b, 1);
        grid.Children.Add(check);
        grid.Children.Add(b);
        return grid;
    }

    private void UpdateMergeButton()
    {
        var n = _checked.Count(id => _groups.Any(g => g.Id == id));
        _mergeChecked.Content = T($"Объединить выбранные группы ({n})", $"Тандалган топторду бириктирүү ({n})", $"Merge selected groups ({n})", $"Seçili grupları birleştir ({n})", $"Tanlangan guruhlarni birlashtirish ({n})");
        Avalonia.Automation.AutomationProperties.SetName(_mergeChecked, _mergeChecked.Content as string);
        _mergeChecked.IsEnabled = n > 0 && _modeProblem.Length == 0;
        if (_modeProblem.Length > 0)
            ToolTip.SetTip(_mergeChecked, _modeProblem);
        else
            ToolTip.SetTip(_mergeChecked, null);
    }

    // ───────────── состав группы и предпросмотр ─────────────

    private MergeGroup? Selected => _groups.FirstOrDefault(g => g.Id == _selectedId);

    private void RenderDetail()
    {
        _detail.Children.Clear();
        if (Selected is not { } g)
        {
            _detail.Children.Add(ServiceDeskUi.Text(T("Выберите группу слева: здесь будет состав и «Предпросмотр».", "Сол жактан топту тандаңыз: бул жерде курамы жана «Алдын ала көрүү» болот.", "Pick a group on the left: its composition and the preview appear here.", "Soldan bir grup seçin: bileşimi ve önizleme burada görünür.", "Chapdan guruhni tanlang: tarkibi va oldindan ko'rish shu yerda bo'ladi."), 13, brush: "BrushTextSoft"));
            return;
        }
        var draft = DraftOf(g);
        var plan = MergePlanner.Build(g, draft);

        // заголовок группы
        var head = new StackPanel { Spacing = 4 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        titleRow.Children.Add(ServiceDeskUi.Text(g.BaseTitle, 17, true, wrap: false));
        titleRow.Children.Add(ServiceDeskUi.Chip(MergeTexts.Confidence(g.Confidence) + $" {g.Score}%", g.Confidence == MergeConfidence.High));
        head.Children.Add(titleRow);
        head.Children.Add(ServiceDeskUi.Text(MergeTexts.Reason(g), 12.5, brush: "BrushTextSoft"));
        foreach (var w in plan.Warnings)
        {
            var warn = ServiceDeskUi.Text("⚠ " + MergeTexts.Warning(w), 12.5, brush: "BrushWarning");
            head.Children.Add(warn);
        }
        _detail.Children.Add(head);
        // действия — сразу под заголовком группы: на 1024×600 «Предпросмотр» ниже, и кнопки не должны уезжать под прокрутку
        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        var mergeOne = ServiceDeskUi.Action("MergeOneButton", T("Объединить эту группу", "Бул топту бириктирүү", "Merge this group", "Bu grubu birleştir", "Bu guruhni birlashtirish"), () => _ = MergeOneAsync(g), primary: true);
        mergeOne.IsEnabled = plan.CanRun && _modeProblem.Length == 0;
        if (!mergeOne.IsEnabled)
            ToolTip.SetTip(mergeOne, _modeProblem.Length > 0 ? _modeProblem : MergeTexts.Problem(plan.Problem));
        actions.Children.Add(mergeOne);
        var declined = _declined.Contains(g.Id);
        actions.Children.Add(ServiceDeskUi.Action("MergeDeclineButton", declined
            ? T("Вернуть в список", "Тизмеге кайтаруу", "Return to the list", "Listeye geri döndür", "Ro'yxatga qaytarish")
            : T("Объединять не надо", "Бириктирүүнүн кереги жок", "Don't merge", "Birleştirme", "Birlashtirish kerak emas"), () => Decline(g, !declined)));
        _detail.Children.Add(actions);
        // 2026-11-21: проверка на сервере (dry_run): объединит ли сервер эту группу; «не объединит» выключает кнопку и называет причины
        var serverCheck = ServiceDeskUi.Text("", 12.5, brush: "BrushTextSoft");
        serverCheck.Name = "MergeServerCheck";
        serverCheck.IsVisible = false;
        _detail.Children.Add(serverCheck);
        if (_blocked.TryGetValue(g.Id, out var knownReasons))
        {
            serverCheck.Text = "⚠ " + MergeTexts.ServerCheckBlocked(knownReasons);
            serverCheck.IsVisible = true;
            ServiceDeskUi.Use(serverCheck, TextBlock.ForegroundProperty, "BrushDanger");
            mergeOne.IsEnabled = false;
            ToolTip.SetTip(mergeOne, MergeTexts.ServerCheckBlocked(knownReasons));
        }
        _preflight = PreflightAsync(g, plan, serverCheck, mergeOne);
        var previewHost = new StackPanel { Name = "MergePreview" };
        _detail.Children.Add(BuildMembers(g, draft, plan, previewHost));

        // название главного
        var renameHost = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };
        var renameLabel = ServiceDeskUi.Text(T("Название главного товара после объединения", "Бириктирилгенден кийин башкы товардын аталышы", "Main product name after the merge", "Birleştirmeden sonra ana ürün adı", "Birlashtirishdan keyin asosiy mahsulot nomi"), 12.5, brush: "BrushTextSoft");
        renameLabel.Margin = new Thickness(0, 0, 0, 4);
        var rename = new TextBox { Name = "MergeMainTitle", Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch, Text = draft.NewMainTitle ?? plan.Main?.Title ?? "", MaxLength = 200 };
        Style(rename);
        Grid.SetRow(rename, 1);
        renameHost.Children.Add(renameLabel);
        renameHost.Children.Add(rename);
        rename.TextChanged += (_, _) =>
        {
            var text = (rename.Text ?? "").Trim();
            draft.NewMainTitle = plan.Main is { } m && string.Equals(text, m.Title.Trim(), StringComparison.Ordinal) ? null : text;
            FillPreview(previewHost, g, draft);
        };
        if (plan.Main is not null)
            _detail.Children.Add(renameHost);

        FillPreview(previewHost, g, draft);
        _detail.Children.Add(previewHost);

    }

    private Control BuildMembers(MergeGroup g, MergeDraft draft, MergePlan plan, StackPanel previewHost)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("52,52,*,140,104,64,56") };
        var headers = new[]
        {
            T("Главн.", "Башкы", "Main", "Ana", "Asosiy"), T("Вкл.", "Кир.", "Incl.", "Dahil", "Kirit."), T("Товар", "Товар", "Product", "Ürün", "Mahsulot"),
            T("Название варианта", "Вариант аталышы", "Variant name", "Varyant adı", "Variant nomi"), T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"), T("Цена", "Баасы", "Price", "Fiyat", "Narx"), T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq"),
        };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < headers.Length; c++)
        {
            var h = ServiceDeskUi.Text(headers[c], 12, brush: "BrushTextSoft", wrap: false);
            h.Margin = new Thickness(c == 2 || c == 3 || c == 4 ? 6 : 0, 0, c >= 5 ? 6 : 0, 6);
            h.TextTrimming = TextTrimming.CharacterEllipsis;
            if (c >= 5)
                h.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }
        var row = 0;
        foreach (var m in g.Members)
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var isMain = m.Product.Id == draft.MainId;
            var blockers = isMain ? m.Blockers.Where(b => b != MergeBlocker.OtherUnit).ToList() : MergePlanner.EffectiveBlockers(m, plan.Main ?? m.Product).ToList();
            var included = !isMain && blockers.Count == 0 && !draft.Excluded.Contains(m.Product.Id);

            var radio = new RadioButton { Name = "MergeMain_" + m.Product.Id, GroupName = "MergeMainOf" + g.Id, IsChecked = isMain, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0, IsEnabled = m.Blockers.Count == 0 || !m.Blockers.Any(b => b != MergeBlocker.OtherUnit) };
            Avalonia.Automation.AutomationProperties.SetName(radio, T("Главный: ", "Башкы: ", "Main: ", "Ana: ", "Asosiy: ") + m.Product.Title);
            var pid = m.Product.Id;
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true && draft.MainId != pid)
                {
                    MergePlanner.SetMain(g, draft, pid);
                    RenderDetail();
                }
            };
            var check = new CheckBox { Name = "MergeInclude_" + m.Product.Id, IsChecked = isMain || included, IsEnabled = !isMain && blockers.Count == 0, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            Avalonia.Automation.AutomationProperties.SetName(check, T("В слиянии: ", "Бириктирүүдө: ", "In the merge: ", "Birleşimde: ", "Birlashuvda: ") + m.Product.Title);
            check.IsCheckedChanged += (_, _) =>
            {
                if (check.IsChecked == true)
                    draft.Excluded.Remove(pid);
                else
                    draft.Excluded.Add(pid);
                RenderDetail();
            };

            var name = new StackPanel { Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(ServiceDeskUi.Text(m.Product.Title, 13, isMain));
            if (blockers.Count > 0)
                name.Children.Add(ServiceDeskUi.Text(string.Join("; ", blockers.Select(MergeTexts.Blocker)), 11.5, brush: "BrushDanger"));

            Control variant;
            if (isMain)
                variant = ServiceDeskUi.Text(T("основной товар", "негизги товар", "main product", "ana ürün", "asosiy mahsulot"), 12, brush: "BrushTextSoft", wrap: false);
            else
            {
                var box = new TextBox { Name = "MergeVariant_" + m.Product.Id, Height = 36, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center, IsEnabled = included, MaxLength = 80,
                    Text = draft.VariantNames.TryGetValue(m.Product.Id, out var vn) ? vn : m.VariantName };
                Style(box);
                box.TextChanged += (_, _) =>
                {
                    draft.VariantNames[pid] = box.Text ?? "";
                    FillPreview(previewHost, g, draft);
                };
                variant = box;
            }
            variant.Margin = new Thickness(6, 4, 6, 4);
            variant.VerticalAlignment = VerticalAlignment.Center;

            var code = ServiceDeskUi.Text(m.Product.Barcode ?? "—", 12.5, wrap: false);
            code.Margin = new Thickness(6, 0, 6, 0);
            code.VerticalAlignment = VerticalAlignment.Center;
            code.TextTrimming = TextTrimming.CharacterEllipsis;
            var price = ServiceDeskUi.Text(MergeTexts.Money(m.Product.Price), 12.5, wrap: false, brush: plan.Main is { } mm && !MergeCandidateFinder.SamePrice(m.Product.Price, mm.Price) ? "BrushWarning" : "BrushText");
            price.HorizontalAlignment = HorizontalAlignment.Right;
            price.VerticalAlignment = VerticalAlignment.Center;
            price.Margin = new Thickness(0, 0, 6, 0);
            var stock = ServiceDeskUi.Text(MergeTexts.Qty(m.Product.Quantity), 12.5, wrap: false);
            stock.HorizontalAlignment = HorizontalAlignment.Right;
            stock.VerticalAlignment = VerticalAlignment.Center;
            stock.Margin = new Thickness(0, 0, 6, 0);

            var cells = new Control[] { radio, check, name, variant, code, price, stock };
            for (var c = 0; c < cells.Length; c++)
            {
                Grid.SetRow(cells[c], row);
                Grid.SetColumn(cells[c], c);
                if (c < 2)
                    cells[c].HorizontalAlignment = HorizontalAlignment.Left;
                grid.Children.Add(cells[c]);
            }
        }
        var host = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8), Child = grid };
        ServiceDeskUi.Use(host, Border.BorderBrushProperty, "BrushBorder");
        ServiceDeskUi.Use(host, Border.BackgroundProperty, "BrushPanel");
        return host;
    }

    private void FillPreview(StackPanel host, MergeGroup g, MergeDraft draft)
    {
        host.Children.Clear();
        var plan = MergePlanner.Build(g, draft);
        var box = new StackPanel { Spacing = 4 };
        box.Children.Add(ServiceDeskUi.Text(T("Предпросмотр: что получится", "Алдын ала көрүү: эмне болот", "Preview: what you get", "Önizleme: ne olacak", "Oldindan ko'rish: nima bo'ladi"), 13.5, true));
        if (plan.Main is null || !plan.CanRun)
            box.Children.Add(ServiceDeskUi.Text(MergeTexts.Problem(plan.Problem), 12.5, brush: "BrushDanger"));
        if (plan.Main is { } main)
        {
            var newTitle = plan.NewMainTitle ?? main.Title;
            box.Children.Add(ServiceDeskUi.Text(T($"Главный: «{newTitle}», основной штрихкод {main.Barcode ?? "—"} остаётся.", $"Башкы: «{newTitle}», негизги штрихкод {main.Barcode ?? "—"} калат.", $"Main: “{newTitle}”, the main barcode {main.Barcode ?? "—"} stays.", $"Ana: «{newTitle}», ana barkod {main.Barcode ?? "—"} kalır.", $"Asosiy: «{newTitle}», asosiy shtrix-kod {main.Barcode ?? "—"} qoladi."), 12.5));
            if (plan.Variants.Count > 0)
            {
                var added = plan.Variants.SelectMany(v => v.CodesToAdd).ToList();
                var shown = string.Join("; ", added.Take(6).Select(a => a.Barcode + (string.IsNullOrWhiteSpace(a.Name) ? "" : " — " + a.Name)));
                var more = added.Count > 6 ? $" +{added.Count - 6}" : "";
                box.Children.Add(ServiceDeskUi.Text(T($"Доп. штрихкоды главного: было {main.AltCodes.Count}, станет {plan.FinalAltCodes.Count}. Новые: {shown}{more}.", $"Башкынын кошумча штрихкоддору: болчу {main.AltCodes.Count}, болот {plan.FinalAltCodes.Count}. Жаңылары: {shown}{more}.",
                    $"Extra barcodes of the main: {main.AltCodes.Count} now, {plan.FinalAltCodes.Count} after. New: {shown}{more}.", $"Ana ürünün ek barkodları: {main.AltCodes.Count} idi, {plan.FinalAltCodes.Count} olacak. Yeni: {shown}{more}.", $"Asosiyning qo'shimcha shtrix-kodlari: {main.AltCodes.Count} edi, {plan.FinalAltCodes.Count} bo'ladi. Yangilari: {shown}{more}."), 12.5));
                box.Children.Add(ServiceDeskUi.Text(MergeTexts.StockLine(plan), 12.5));
                box.Children.Add(ServiceDeskUi.Text(MergeTexts.PurchaseLine(plan), 12.5));
                box.Children.Add(ServiceDeskUi.Text(T($"Цена главного: {MergeTexts.Money(main.Price)} — не меняется.", $"Башкынын баасы: {MergeTexts.Money(main.Price)} — өзгөрбөйт.", $"Main price: {MergeTexts.Money(main.Price)} — unchanged.", $"Ana fiyat: {MergeTexts.Money(main.Price)} — değişmez.", $"Asosiy narx: {MergeTexts.Money(main.Price)} — o'zgarmaydi."), 12.5));
                box.Children.Add(ServiceDeskUi.Text(MergeTexts.SourcesLine() + ": " + string.Join(", ", plan.Variants.Select(v => "«" + v.Source.Title + "»")) + ".", 12.5));
            }
            foreach (var (product, blockers) in plan.Blocked)
                box.Children.Add(ServiceDeskUi.Text(T($"Не войдёт: «{product.Title}» — ", $"Кирбейт: «{product.Title}» — ", $"Not included: “{product.Title}” — ", $"Dahil değil: «{product.Title}» — ", $"Kirmaydi: «{product.Title}» — ") + string.Join("; ", blockers.Select(MergeTexts.Blocker)) + ".", 12.5, brush: "BrushDanger"));
        }
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), Child = box };
        ServiceDeskUi.Use(border, Border.BackgroundProperty, "BrushPanelSoft");
        host.Children.Add(border);
    }

    private void Decline(MergeGroup g, bool decline)
    {
        try
        {
            MergeStore.SetDeclined(g.Id, decline);
            _checked.Remove(g.Id);
            if (decline)
                _declined.Add(g.Id);
            else
                _declined.Remove(g.Id);
            _selectedId = null;
            _status.Text = decline
                ? T($"Группа «{g.BaseTitle}» скрыта: больше не предлагается. Вернуть — фильтр «Скрытые».", $"«{g.BaseTitle}» тобу жашырылды: мындан ары сунушталбайт. Кайтаруу — «Жашырылган» чыпкасы.", $"The group “{g.BaseTitle}” is hidden and will not be offered again. Restore it with the “Hidden” filter.", $"«{g.BaseTitle}» grubu gizlendi: bir daha önerilmez. Geri almak için «Gizli» filtresi.", $"«{g.BaseTitle}» guruhi yashirildi: boshqa taklif qilinmaydi. Qaytarish — «Yashirilgan» filtri.")
                : T($"Группа «{g.BaseTitle}» снова в списке.", $"«{g.BaseTitle}» тобу кайра тизмеде.", $"The group “{g.BaseTitle}” is back in the list.", $"«{g.BaseTitle}» grubu yine listede.", $"«{g.BaseTitle}» guruhi yana ro'yxatda.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = T("Не удалось запомнить отказ: " + ex.Message, "Баш тартууну эстеп калуу мүмкүн болгон жок: " + ex.Message, "Could not remember the choice: " + ex.Message, "Seçim kaydedilemedi: " + ex.Message, "Tanlovni eslab qolib bo'lmadi: " + ex.Message);
        }
        RenderList();
        RenderDetail();
    }

    // ───────────── слияние ─────────────

    private Task MergeOneAsync(MergeGroup g) => RunMergesAsync([g]);

    private Task MergeCheckedAsync() => RunMergesAsync(_groups.Where(g => _checked.Contains(g.Id)).ToList());

    private async Task RunMergesAsync(IReadOnlyList<MergeGroup> groups)
    {
        if (groups.Count == 0 || _modeProblem.Length > 0 || _server is null)
            return;
        var plans = groups.Select(g => MergePlanner.Build(g, DraftOf(g))).ToList();
        // 2026-11-21: группы, которые сервер уже назвал «не объединит» (dry_run), в слияние не берём — их причины остаются в списке
        var runnable = plans.Where(p => p.CanRun && !_blocked.ContainsKey(p.Group.Id)).ToList();
        if (runnable.Count == 0)
        {
            var firstBlocked = plans.FirstOrDefault(p => p.CanRun && _blocked.ContainsKey(p.Group.Id));
            _status.Text = firstBlocked is not null ? MergeTexts.ServerBlocked(firstBlocked.Main!.Title, _blocked[firstBlocked.Group.Id]) : MergeTexts.Problem(plans[0].Problem);
            return;
        }
        var sources = runnable.Sum(p => p.Variants.Count);
        var stock = runnable.Sum(p => p.Variants.Sum(v => v.Stock));
        var priceNote = runnable.Any(p => p.Warnings.Contains(MergeWarning.PricesDiffer))
            ? "\n\n" + T("В части групп цены разные: варианты станут продаваться по цене главного товара.", "Айрым топтордо баалар ар башка: варианттар башкы товардын баасы менен сатылат.", "Some groups have different prices: variants will sell at the main product's price.", "Bazı gruplarda fiyatlar farklı: varyantlar ana ürünün fiyatıyla satılır.", "Ba'zi guruhlarda narxlar har xil: variantlar asosiy mahsulot narxida sotiladi.")
            : "";
        var text = T($"Объединить групп: {runnable.Count}. Товаров-источников: {sources}, их остаток {MergeTexts.Qty(stock)} перейдёт главным товарам; источники уйдут в архив, история продаж останется.\n\nПеред слиянием в папку данных пишется бэкап исходных карточек; отменить можно кнопкой «Журнал и откат».",
            $"Бириктирилүүчү топтор: {runnable.Count}. Булак товарлар: {sources}, алардын калдыгы {MergeTexts.Qty(stock)} башкы товарларга өтөт; булактар архивге кетет, сатуулар тарыхы калат.\n\nБириктирүүдөн мурун баштапкы карточкалардын резерви дайындар папкасына жазылат; «Журнал жана кайтаруу» баскычы менен кайтарса болот.",
            $"Groups to merge: {runnable.Count}. Source products: {sources}; their stock of {MergeTexts.Qty(stock)} moves to the main products; the sources go to the archive and the sales history stays.\n\nBefore merging, a backup of the original cards is written to the data folder; you can undo with “Journal and rollback”.",
            $"Birleştirilecek grup: {runnable.Count}. Kaynak ürün: {sources}; stokları {MergeTexts.Qty(stock)} ana ürünlere geçer; kaynaklar arşive gider, satış geçmişi kalır.\n\nBirleştirmeden önce orijinal kartların yedeği veri klasörüne yazılır; «Günlük ve geri alma» ile geri alabilirsiniz.",
            $"Birlashtiriladigan guruhlar: {runnable.Count}. Manba mahsulotlar: {sources}; ularning {MergeTexts.Qty(stock)} qoldig'i asosiy mahsulotlarga o'tadi; manbalar arxivga ketadi, sotuvlar tarixi qoladi.\n\nBirlashtirishdan oldin asl kartalar zaxirasi ma'lumotlar papkasiga yoziladi; «Jurnal va qaytarish» bilan qaytarish mumkin.") + priceNote;
        if (!await _deps.Confirm(this, text, T("Объединение товаров", "Товарларды бириктирүү", "Merge products", "Ürünleri birleştirme", "Mahsulotlarni birlashtirish"),
                T($"Объединить ({runnable.Count})", $"Бириктирүү ({runnable.Count})", $"Merge ({runnable.Count})", $"Birleştir ({runnable.Count})", $"Birlashtirish ({runnable.Count})"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), false).ConfigureAwait(true))
            return;

        var runner = new ProductMergeRunner(_server, _deps.Who(), _deps.Account());
        var doneSources = new List<string>();
        var doneMains = new List<string>();
        var lines = new List<string>();
        var ok = 0;
        await _shell.RunBusyAsync(async () =>
        {
            var index = 0;
            foreach (var plan in runnable)
            {
                index++;
                var label = $"[{index}/{runnable.Count}] «{plan.Main!.Title}»";
                MergeRunResult result;
                try
                {
                    result = await runner.RunAsync(plan, CancellationToken.None, msg => _status.Text = label + ": " + msg).ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    PosLogger.Log($"Слияние товаров: группа «{plan.Main!.Title}» прервана ({ex.GetType().Name}: {ex.Message}).", "ERROR");
                    lines.Add($"«{plan.Main!.Title}»: {ex.Message}");
                    break;
                }
                if (result.Entry is { } entry)
                {
                    foreach (var s in entry.Sources.Where(x => x.State == "done"))
                    {
                        doneSources.Add(s.Id);
                        _merged.Add(s.Id);
                    }
                    if (entry.Sources.Any(x => x.State == "done"))
                        doneMains.Add(plan.Main!.Id);
                }
                if (result.Ok)
                    ok++;
                else if (result.Status == "blocked")
                {
                    _blocked[plan.Group.Id] = result.Reasons ?? [];
                    lines.Add(result.Message);
                }
                else
                    lines.Add($"«{plan.Main!.Title}»: {result.Message}");
                if (result.Status == "check")
                    break; // исход неизвестен — дальше не идём, пока владелец не проверит остатки
            }
            if (doneSources.Count > 0)
                await _deps.AfterMerge(doneSources, doneMains).ConfigureAwait(true);
        }).ConfigureAwait(true);

        var summary = T($"Объединено групп: {ok} из {runnable.Count}. Перенесено товаров: {doneSources.Count}.", $"Бириктирилген топтор: {ok}/{runnable.Count}. Которулган товар: {doneSources.Count}.", $"Groups merged: {ok} of {runnable.Count}. Products moved: {doneSources.Count}.", $"Birleştirilen grup: {ok}/{runnable.Count}. Taşınan ürün: {doneSources.Count}.", $"Birlashtirilgan guruhlar: {ok}/{runnable.Count}. Ko'chirilgan mahsulotlar: {doneSources.Count}.")
            + (lines.Count > 0 ? " " + string.Join(" ", lines) : "");
        _checked.Clear();
        await ReloadAsync().ConfigureAwait(true);
        _status.Text = summary;
    }

    // ───────────── CSV и журнал ─────────────

    private async Task ExportCsvAsync()
    {
        var plans = Visible().Select(g => MergePlanner.Build(g, DraftOf(g))).Where(p => p.Main is not null && p.Variants.Count > 0).ToList();
        if (plans.Count == 0)
        {
            _status.Text = T("Нечего выгружать: в списке нет групп.", "Чыгарууга эч нерсе жок: тизмеде топтор жок.", "Nothing to export: the list has no groups.", "Dışa aktarılacak bir şey yok: listede grup yok.", "Chiqariladigan narsa yo'q: ro'yxatda guruh yo'q.");
            return;
        }
        try
        {
            var saved = await _deps.SaveCsv(this, "merge-plan-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv", path => MergeStore.ExportPlanCsv(plans, path)).ConfigureAwait(true);
            if (saved is not null)
                _status.Text = T($"План сохранён: {saved} (групп: {plans.Count}).", $"План сакталды: {saved} (топтор: {plans.Count}).", $"Plan saved: {saved} (groups: {plans.Count}).", $"Plan kaydedildi: {saved} (grup: {plans.Count}).", $"Reja saqlandi: {saved} (guruhlar: {plans.Count}).");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Слияние товаров: план не сохранён ({ex.Message}).", "WARNING");
            _status.Text = T("Не удалось сохранить план: " + ex.Message, "Планды сактоо мүмкүн болгон жок: " + ex.Message, "Could not save the plan: " + ex.Message, "Plan kaydedilemedi: " + ex.Message, "Rejani saqlab bo'lmadi: " + ex.Message);
        }
    }

    private async Task OpenJournalAsync()
    {
        var w = new ProductMergeJournalWindow(_deps, _server, _modeProblem);
        await w.ShowDialog(this).ConfigureAwait(true);
        if (w.RolledBackAny)
            await ReloadAfterRollbackAsync(w.RestoredIds).ConfigureAwait(true);
    }

    private async Task ReloadAfterRollbackAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
            _merged.Remove(id);
        try { await CatalogCacheService.RefreshProductsAsync(ids).ConfigureAwait(true); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { PosLogger.Log($"Слияние товаров: каталог после отката не обновлён ({ex.Message}).", "WARNING"); }
        await ReloadAsync().ConfigureAwait(true);
    }
}
