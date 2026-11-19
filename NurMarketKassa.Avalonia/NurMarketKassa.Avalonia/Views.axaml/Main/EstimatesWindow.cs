using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-11-19 («Смета» — калькулятор для строймаркета, ТЗ 19.11.2026): список смет. Слева — поиск, фильтр по статусу («Черновик / Отправлена / Принята») и сметы (номер, объект, клиент, итог, статус,
/// «Цены могли измениться» после срока); справа — карточка выбранной сметы и действия: «Открыть», «Дублировать», «Удалить» (с подтверждением). Данные — только локально (<see cref="EstimateStore"/>), на других
/// кассах смета не появляется (подсказка под списком). Модуль доступен в кассе (меню ☰ и «Ещё») и в программе владельца (Товары → Смета); одно окно — один экземпляр (WindowSingleton).
/// В программе владельца у карточки видны себестоимость и маржа; в кассе — только с правом на закупку. Esc закрывает окно.
/// </summary>
public sealed class EstimatesWindow : Window, IOwnerSection
{
    private readonly EstimateDeps _deps;
    private readonly ModalShell _shell = new();
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _detail = new();
    private readonly TextBlock _status = new();
    private string? _selectedId;
    private bool _ownerSection;

    public EstimatesWindow() : this(null) { }

    public EstimatesWindow(EstimateDeps? deps)
    {
        _deps = deps ?? new EstimateDeps();
        Title = EstimateUi.Title;
        Width = 1040;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        this[!BackgroundProperty] = this.GetResourceObservable("DialogWindowBackground").ToBinding();
        this[!TransparencyLevelHintProperty] = this.GetResourceObservable("DialogTransparencyHint").ToBinding();
        Opened += (_, _) => { if (!_ownerSection) this.FitToKassaScreen(); };
        BuildUi();
        Reload();
    }

    public static void Open(Window? owner) => WindowSingleton.ShowOnce(owner, () => new EstimatesWindow());

    public void AsOwnerSection()
    {
        _ownerSection = true;
        _shell.ShowClose = false;
        _shell.Footer = null;
        _shell.Subtitle = null;
        _shell.Margin = OwnerSectionLayout.Margin;
        _shell.ScreenHeightShare = 1.0;
        _shell.VerticalAlignment = VerticalAlignment.Stretch;
        _shell.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find<Control>("PART_FooterGrid")?.Parent is Control footerBand)
                footerBand.IsVisible = false;
        };
    }

    public string StatusMessage => _status.Text ?? "";

    public string? SelectedId => _selectedId;

    private void BuildUi()
    {
        _shell.Title = Title;
        _shell.Margin = new Thickness(16);
        _shell.ScreenHeightShare = 1.0;

        var newButton = ServiceDeskUi.Action("EstimateNew", EstimateUi.T("＋ Новая смета", "＋ Жаңы смета", "＋ New estimate", "＋ Yeni keşif", "＋ Yangi smeta"), () => _ = NewEstimateAsync(), primary: true);
        _search.Name = "EstimateSearch";
        _search.Watermark = EstimateUi.T("Номер, объект, клиент, телефон", "Номер, объект, кардар, телефон", "Number, site, customer, phone", "Numara, şantiye, müşteri, telefon", "Raqam, obyekt, mijoz, telefon");
        _search.Height = 40;
        _search.TextChanged += (_, _) => Reload();
        ServiceDeskUi.Use(_search, BackgroundProperty, "BrushInput");
        ServiceDeskUi.Use(_search, ForegroundProperty, "BrushText");
        ServiceDeskUi.Use(_search, BorderBrushProperty, "BrushInputBorder");
        _filter.Name = "EstimateFilter";
        _filter.Height = 40;
        _filter.ItemsSource = new[]
        {
            EstimateUi.T("Все", "Баары", "All", "Tümü", "Hammasi"), EstimateUi.StatusName(EstimateStatus.Draft), EstimateUi.StatusName(EstimateStatus.Sent), EstimateUi.StatusName(EstimateStatus.Accepted),
        };
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) => Reload();

        var tools = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(newButton);
        tools.Children.Add(_search);
        tools.Children.Add(_filter);
        var left = new DockPanel { Width = 330, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(tools, Dock.Top);
        left.Children.Add(tools);
        left.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        _status.Name = "EstimateListStatus";
        _status.TextWrapping = TextWrapping.Wrap;
        _status.FontSize = 12.5;
        _status.Margin = new Thickness(0, 6, 0, 0);
        _status.Text = EstimateUi.LocalHint;
        ServiceDeskUi.Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        var right = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        right.Children.Add(_status);
        right.Children.Add(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });

        var grid = new DockPanel();
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left);
        grid.Children.Add(right);
        _shell.Content = grid;

        var close = new Button { Name = "EstimateListClose", Content = EstimateUi.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), MinWidth = 120, IsCancel = true };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();
        _shell.Footer = close;
        Content = _shell;
    }

    private EstimateStatus? FilterStatus() => _filter.SelectedIndex switch { 1 => EstimateStatus.Draft, 2 => EstimateStatus.Sent, 3 => EstimateStatus.Accepted, _ => null };

    private static bool Matches(EstimateDoc e, string? query)
    {
        var q = (query ?? "").Trim();
        return q.Length == 0 || SearchMatch.Matches(q, e.Title, e.ClientName, e.Phone, e.Address, EstimateRules.Code(e.Number));
    }

    public void Reload()
    {
        var all = _deps.Store.All();
        var status = FilterStatus();
        var rows = all.Where(e => (status is null || e.Status == status) && Matches(e, _search.Text)).ToList();
        _list.Children.Clear();
        if (rows.Count == 0)
            _list.Children.Add(ServiceDeskUi.Text(all.Count == 0
                ? EstimateUi.T("Смет пока нет. Нажмите «＋ Новая смета».", "Азырынча смета жок. «＋ Жаңы смета» басыңыз.", "No estimates yet. Press “＋ New estimate”.", "Henüz keşif yok. «＋ Yeni keşif»e basın.", "Hozircha smeta yo'q. «＋ Yangi smeta» ni bosing.")
                : EstimateUi.T("Ничего не найдено.", "Эч нерсе табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi."), 13, brush: "BrushTextSoft"));
        foreach (var e in rows)
            _list.Children.Add(BuildRow(e));
        ShowDetail(all.FirstOrDefault(e => e.Id == _selectedId));
    }

    private Control BuildRow(EstimateDoc e)
    {
        var line1 = ServiceDeskUi.Text($"{EstimateRules.Code(e.Number)} · {(string.IsNullOrWhiteSpace(e.Title) ? EstimateUi.T("без названия", "аталышы жок", "untitled", "adsız", "nomsiz") : e.Title)}", 14, true, wrap: false);
        line1.TextTrimming = TextTrimming.CharacterEllipsis;
        var line2 = ServiceDeskUi.Text($"{(string.IsNullOrWhiteSpace(e.ClientName) ? "—" : e.ClientName)} · {MoneyText.Som(EstimateRules.Total(e))} · {EstimateUi.Day(e.CreatedAt)}", 12, brush: "BrushTextSoft", wrap: false);
        line2.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(line1);
        panel.Children.Add(line2);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(ServiceDeskUi.Chip(EstimateUi.StatusName(e.Status), e.Status == EstimateStatus.Accepted));
        if (EstimateRules.IsExpired(e, _deps.Today()))
            chips.Children.Add(ServiceDeskUi.Chip(EstimateUi.T("Цены могли измениться", "Баалар өзгөрүшү мүмкүн", "Prices may have changed", "Fiyatlar değişmiş olabilir", "Narxlar o'zgargan bo'lishi mumkin"), true));
        panel.Children.Add(chips);
        var b = new Button { Name = "Row_" + e.Number, Content = panel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(10) };
        ServiceDeskUi.Use(b, BackgroundProperty, e.Id == _selectedId ? "BrushAccentSoft" : "BrushPanelSoft");
        ServiceDeskUi.Use(b, ForegroundProperty, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(b, $"{EstimateRules.Code(e.Number)} {e.Title} {EstimateUi.StatusName(e.Status)}");
        var id = e.Id;
        b.Click += (_, _) => Select(id);
        b.DoubleTapped += (_, _) => _ = OpenEditorAsync(id);
        return b;
    }

    public void Select(string? id)
    {
        _selectedId = id;
        Reload();
    }

    private void Say(string text) => _status.Text = text;

    private void ShowDetail(EstimateDoc? e)
    {
        _detail.Children.Clear();
        if (e is null)
        {
            _detail.Children.Add(ServiceDeskUi.Text(EstimateUi.T("Выберите смету слева или составьте новую.", "Сол жактан смета тандаңыз же жаңысын түзүңүз.", "Pick an estimate on the left or create a new one.", "Soldan bir keşif seçin veya yenisini oluşturun.", "Chapdan smetani tanlang yoki yangisini tuzing."), 14, brush: "BrushTextSoft"));
            return;
        }
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(ServiceDeskUi.Text($"{EstimateUi.Title} {EstimateRules.Code(e.Number)}", 18, true, wrap: false));
        head.Children.Add(ServiceDeskUi.Chip(EstimateUi.StatusName(e.Status), e.Status == EstimateStatus.Accepted));
        _detail.Children.Add(head);
        if (EstimateRules.IsExpired(e, _deps.Today()))
            _detail.Children.Add(ServiceDeskUi.Text(EstimateUi.T("Срок сметы прошёл — цены могли измениться. Откройте смету и нажмите «Пересчитать цены».", "Сметанын мөөнөтү өттү — баалар өзгөрүшү мүмкүн. Сметаны ачып «Баалар кайра эсептөө» басыңыз.", "The estimate has expired — prices may have changed. Open it and press “Recalculate prices”.", "Keşfin süresi doldu — fiyatlar değişmiş olabilir. Açıp «Fiyatları yeniden hesapla»ya basın.", "Smeta muddati o'tdi — narxlar o'zgargan bo'lishi mumkin. Uni ochib «Narxlarni qayta hisoblash» ni bosing."), 13, true, "BrushDanger"));

        var id = e.Id;
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        actions.Children.Add(ServiceDeskUi.Action("EstimateOpen", EstimateUi.T("Открыть", "Ачуу", "Open", "Aç", "Ochish"), () => _ = OpenEditorAsync(id), primary: true));
        actions.Children.Add(ServiceDeskUi.Action("EstimateDuplicate", EstimateUi.T("Дублировать", "Көчүрмөсүн түзүү", "Duplicate", "Çoğalt", "Nusxa yaratish"), () => Duplicate(id)));
        actions.Children.Add(ServiceDeskUi.Action("EstimateDelete", EstimateUi.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), () => _ = DeleteAsync(id), danger: true));
        _detail.Children.Add(actions);

        var facts = new WrapPanel { Orientation = Orientation.Horizontal };
        void Fact(string label, string? value)
        {
            var host = new StackPanel { Width = 282, Margin = new Thickness(0, 0, 10, 0) };
            ServiceDeskUi.AddFact(host, label, value);
            if (host.Children.Count > 0)
                facts.Children.Add(host);
        }
        Fact(EstimateUi.T("Объект", "Объект", "Site", "Şantiye", "Obyekt"), e.Title);
        Fact(EstimateUi.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz"), string.Join(", ", new[] { e.ClientName, e.Phone }.Where(x => !string.IsNullOrWhiteSpace(x))));
        Fact(EstimateUi.T("Адрес объекта", "Объекттин дареги", "Site address", "Şantiye adresi", "Obyekt manzili"), e.Address);
        Fact(EstimateUi.T("Действует до", "Чейин жарактуу", "Valid until", "Geçerlilik", "Amal qiladi"), EstimateUi.Day(e.ValidUntil));
        Fact(EstimateUi.T("Цены на", "Баалар", "Prices as of", "Fiyat tarihi", "Narxlar sanasi"), EstimateUi.Day(e.PricesAt));
        Fact(EstimateUi.T("Уровень цены", "Баа деңгээли", "Price level", "Fiyat düzeyi", "Narx darajasi"), e.Level == EstimateLevel.Foreman ? EstimateUi.T("Прораб (опт)", "Прораб (дүң)", "Foreman (bulk)", "Usta (toptan)", "Prorab (ulgurji)") : EstimateUi.T("Розница", "Чекене", "Retail", "Perakende", "Chakana"));
        Fact(EstimateUi.T("Позиций", "Позиция", "Lines", "Kalem", "Pozitsiya"), e.Lines.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Fact(EstimateUi.T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami"), MoneyText.Som(EstimateRules.Total(e)));
        if (_deps.CanSeeCost())
            Fact(EstimateUi.T("Себестоимость и маржа", "Өздүк нарк жана маржа", "Cost and margin", "Maliyet ve marj", "Tannarx va marja"), EstimateText.InternalSummary(e));
        Fact(EstimateUi.T("Примечание", "Эскертүү", "Note", "Not", "Izoh"), e.Note);
        _detail.Children.Add(facts);
    }

    // ───────────────────────────── действия ─────────────────────────────

    /// <summary>Новая смета и сразу редактор.</summary>
    public async Task NewEstimateAsync()
    {
        var doc = _deps.Store.Create();
        _selectedId = doc.Id;
        await OpenEditorAsync(doc.Id).ConfigureAwait(true);
    }

    public async Task OpenEditorAsync(string id)
    {
        var doc = _deps.Store.Find(id);
        if (doc is null)
            return;
        _selectedId = id;
        var editor = new EstimateEditorWindow(_deps, doc);
        await editor.ShowDialog<bool>(this).ConfigureAwait(true);
        // Пустую смету, которую так и не начали заполнять, не копим в списке.
        var after = _deps.Store.Find(id);
        if (after is { Lines.Count: 0, Sections.Count: 0 } && string.IsNullOrWhiteSpace(after.Title) && string.IsNullOrWhiteSpace(after.ClientName) && string.IsNullOrWhiteSpace(after.Note))
        {
            _deps.Store.Delete(id);
            _selectedId = null;
        }
        Reload();
        if (editor.AddedToCart && _deps.Till is not null)
            Close();
    }

    public EstimateDoc? Duplicate(string id)
    {
        var copy = _deps.Store.Duplicate(id, EstimateUi.T("(копия)", "(көчүрмө)", "(copy)", "(kopya)", "(nusxa)"));
        if (copy is null)
            return null;
        _selectedId = copy.Id;
        Say(EstimateUi.T("Копия создана: ", "Көчүрмө түзүлдү: ", "Copy created: ", "Kopya oluşturuldu: ", "Nusxa yaratildi: ") + EstimateRules.Code(copy.Number));
        Reload();
        return copy;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        var doc = _deps.Store.Find(id);
        if (doc is null)
            return false;
        var question = EstimateUi.T($"Удалить смету {EstimateRules.Code(doc.Number)} «{doc.Title}»? Вернуть её будет нельзя.", $"{EstimateRules.Code(doc.Number)} «{doc.Title}» сметасын өчүрөсүзбү? Кайтаруу мүмкүн эмес.", $"Delete estimate {EstimateRules.Code(doc.Number)} “{doc.Title}”? It cannot be restored.", $"{EstimateRules.Code(doc.Number)} «{doc.Title}» keşfi silinsin mi? Geri alınamaz.", $"{EstimateRules.Code(doc.Number)} «{doc.Title}» smetasi o'chirilsinmi? Uni qaytarib bo'lmaydi.");
        var ok = _deps.Confirm is { } confirm
            ? await confirm(question, EstimateUi.Title).ConfigureAwait(true)
            : await PosDialogs.ConfirmYesNoModalAsync(this, question, EstimateUi.Title, confirmText: EstimateUi.T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), danger: true).ConfigureAwait(true);
        if (!ok)
            return false;
        var done = _deps.Store.Delete(id);
        if (_selectedId == id)
            _selectedId = null;
        Reload();
        return done;
    }
}
