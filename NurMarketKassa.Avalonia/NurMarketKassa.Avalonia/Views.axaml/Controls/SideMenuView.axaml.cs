using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Interfaces;
using NurMarketKassa.Services;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.Main.Controls;

/// <summary>Меню ☰ кассы. 2026-10-19, владелец: «так же и в кассе сделай старое/новое для выбора» — два вида (KassaMenuStyles):
/// «Старое» (OldMenuRoot, как в выпуске 1.17.58) и «Новое» (NewMenuRoot).
/// 2026-10-20, владелец о «Новом» по макету KassaMenus («это вообще кошмар что за дизайн??? исправь, изучи дизайны бургер-меню
/// красивые и удобные»): «Новое» переделано — выезжающая слева панель 400 (на окне ≤ 1100 — 360) с шапкой смены, поиском
/// действий (Ctrl+K), плитками быстрых действий и группами со значками; «Система» сворачивается.
/// 2026-10-21 (меню ☰, жалоба владельца): «не удобно и не красиво меню» — «Новое» третий раз: вся панель — сетка крупных плиток
/// 3 в ряд по группам («Продажи», «Смена и деньги», «Товары», «Работа», «Отчёты», «Экран», «Настройки и помощь»), в шапке смены — три
/// кнопки (смена, сменить кассира, выйти), тёмная тема — переключатель, счётчики — бейджами в углу плитки, одна прокрутка.
/// Пункты, права, тариф, скрытые разделы и команды — общие (SideMenuViewModel); здесь только вид: какой виден, подписи на языке
/// кассы, видимость ячеек (права + поиск), выбор с клавиатуры, счётчики и защита сканера (скан при открытом меню закрывает меню
/// и уходит в чек, поле поиска не ворует сканы).</summary>
public partial class SideMenuView : UserControl
{
    /// <summary>2026-10-20: меню «Новое» открыто и само разбирает клавиши (↑↓, Tab, Enter, Esc, Ctrl+K) — окно кассы в это время не
    /// двигает рамку каталога и не жмёт «Оплату» по Enter (MainWindow.OnArrowKeyTunnel / OnPlainHotkeyTunnel).</summary>
    internal static bool CapturesKeyboard { get; private set; }

    private Panel? _host;
    private SideMenuViewModel? _vm;
    private TopLevel? _top;
    private IBarcodeInputService? _scanner;
    private readonly List<MenuCell> _cells = new();
    private bool _isOpen;
    private string _query = "";
    private Button? _kb;
    private bool _refreshQueued;
    private readonly List<IDisposable> _heroBindings = new();
    private CancellationTokenSource? _animCts;

    // Первая буква с клавиатуры, пока поле поиска без фокуса: ждём, не сканер ли это (см. OnTopTextInput).
    private readonly DispatcherTimer _pendingTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _pending = "";
    private long _pendingTick;

    // Скорость набора в поле поиска: сканер печатает быстрее 50 мс на знак (см. IsScanEnterInSearch).
    private long _lastSearchKeyTick;
    private int _searchFastRun;

    private sealed record MenuCell(Panel Cell, Button Button, bool Fixed, bool SearchOnly, string Synonyms);

    public SideMenuView()
    {
        InitializeComponent();
        CollectCells();
        InitNewBadges();
        // 2026-10-22 (меню, сворачиваемые разделы): заголовки групп нового вида — кнопки сворачивания (SideMenuView.Groups.cs).
        InitGroups();
        _pendingTimer.Tick += OnPendingTimer;
        NewMenuRoot.AddHandler(Button.ClickEvent, OnNewMenuClick, RoutingStrategies.Bubble);
        MenuSearchBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                OnQueryChanged();
        };
        // 2026-10-21 (меню ☰, жалоба владельца): ширина плиток — от ширины содержимого (все сетки групп, не одна «Быстрые действия»).
        NewMenuContent.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                UpdateTileWidth();
        };
        ActualThemeVariantChanged += (_, _) => ApplyHeroColors();

        AttachedToVisualTree += (_, _) =>
        {
            KassaMenuStyles.Changed -= OnStyleChanged;
            KassaMenuStyles.Changed += OnStyleChanged;
            SettingsCloudSync.Applied -= OnStyleChanged;
            SettingsCloudSync.Applied += OnStyleChanged;
            Tr.LanguageChanged -= OnLanguageChanged;
            Tr.LanguageChanged += OnLanguageChanged;
            // Ширина нового вида — по окну: панель меню (родитель) растянута на всё окно.
            if (Parent is Panel host && !ReferenceEquals(host, _host))
            {
                if (_host is not null)
                    _host.PropertyChanged -= OnHostPropertyChanged;
                _host = host;
                _host.PropertyChanged += OnHostPropertyChanged;
            }
            AttachTopLevel(TopLevel.GetTopLevel(this));
            AttachScanner();
            ApplyTexts();
            ApplyHeroColors();
            ApplyStyle();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            KassaMenuStyles.Changed -= OnStyleChanged;
            SettingsCloudSync.Applied -= OnStyleChanged;
            Tr.LanguageChanged -= OnLanguageChanged;
            AttachTopLevel(null);
            if (_scanner is not null)
                _scanner.BarcodeScanned -= OnBarcodeScanned;
            _scanner = null;
            _pendingTimer.Stop();
            SetOpen(false);
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
                _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm = DataContext as SideMenuViewModel;
            if (_vm is not null)
                _vm.PropertyChanged += OnVmPropertyChanged;
            UpdateTitle();
            QueueRefresh();
        };
    }

    /// <summary>2026-10-27, аудит Н-18: значок «Новое» (Border.NewBadge) у пункта старого вида меню гаснет после первого открытия этого
    /// пункта и больше не появляется (ключ пункта — Tag="new:…" у кнопки, запоминание — MenuNewBadgeStore).</summary>
    private void InitNewBadges()
    {
        foreach (var badge in this.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("NewBadge")).ToList())
        {
            var button = badge.GetLogicalAncestors().OfType<Button>().FirstOrDefault();
            if (button?.Tag is not string { Length: > 0 } key || !key.StartsWith("new:", StringComparison.Ordinal))
                continue;
            if (MenuNewBadgeStore.IsSeen(key))
            {
                badge.IsVisible = false;
                continue;
            }
            button.Click += (_, _) =>
            {
                if (!badge.IsVisible)
                    return;
                badge.IsVisible = false;
                MenuNewBadgeStore.MarkSeen(key);
            };
        }
    }

    private MainWindowViewModel? MainVm => _top?.DataContext as MainWindowViewModel;

    private bool IsNewStyle => KassaMenuStyles.Current == KassaMenuStyles.New;

    private void OnStyleChanged() => Dispatcher.UIThread.Post(ApplyStyle);

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(ApplyTexts);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SideMenuViewModel.CashboxTitle) or nameof(SideMenuViewModel.CashierName) or null or "")
            UpdateTitle();
        if (e.PropertyName is nameof(SideMenuViewModel.ShiftBalanceText) or null or "")
            UpdateBalanceSize();
        QueueRefresh();
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
            UpdateWidth();
        else if (e.Property == IsVisibleProperty)
            SetOpen(_host?.IsVisible == true);
    }

    /// <summary>Какой вид виден (настройка KassaMenuStyle; меняется сразу — окно выбора, Настройки → Экран, облако).</summary>
    private void ApplyStyle()
    {
        var isNew = IsNewStyle;
        OldMenuRoot.IsVisible = !isNew;
        NewMenuRoot.IsVisible = isNew;
        UpdateWidth();
        CapturesKeyboard = _isOpen && isNew;
    }

    private void ApplyTexts()
    {
        var styleText = Tr.T("Вид меню", "Менюнун көрүнүшү", "Menu layout", "Menü görünümü", "Menyu ko'rinishi");
        OldMenuStyleText.Text = styleText;
        NewMenuStyleText.Text = styleText;
        AutomationProperties.SetName(OldMenuStyleButton, styleText);
        AutomationProperties.SetName(NewMenuStyleButton, styleText);
        // 2026-10-21 (меню ☰, жалоба владельца): группы плиток по смыслу; «Система» — «Настройки и помощь» (видна сразу).
        NewCaptionSales.Text = Tr.T("Продажи", "Сатуу", "Sales", "Satış", "Sotuv");
        NewCaptionMoney.Text = Tr.T("Смена и деньги", "Смена жана акча", "Shift and money", "Vardiya ve para", "Smena va pul");
        NewCaptionGoods.Text = Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Tovarlar");
        NewCaptionWork.Text = Tr.T("Работа", "Иш", "Work", "İş", "Ish");
        NewCaptionReports.Text = Tr.T("Отчёты", "Отчёттор", "Reports", "Raporlar", "Hisobotlar");
        NewCaptionScreen.Text = Tr.T("Экран", "Экран", "Screen", "Ekran", "Ekran");
        NewCaptionSystem.Text = Tr.T("Настройки и помощь", "Жөндөөлөр жана жардам", "Settings and help", "Ayarlar ve yardım", "Sozlamalar va yordam");
        NewInTillCaption.Text = Tr.T("В кассе", "Кассада", "In the till", "Kasada", "Kassada");
        NewExitText.Text = Tr.T("Выйти", "Чыгуу", "Exit", "Çıkış", "Chiqish");
        // 2026-10-22 (закупки как в вебе): «Закупки» — в обоих видах меню.
        var purchases = Tr.T("Закупки", "Сатып алуулар", "Purchases", "Satın alma", "Xaridlar");
        NewPurchasesText.Text = purchases;
        OldPurchasesText.Text = purchases;
        AutomationProperties.SetName(OldPurchasesButton, purchases);
        NewDeferredCartsText.Text = Tr.T("Отложенные чеки", "Калтырылган чектер", "Held receipts", "Bekletilen fişler", "Kechiktirilgan cheklar");
        // 2026-10-20 (живой аудит кассы, К16): X-отчёт — цифры открытой смены без закрытия.
        var xReport = Tr.T("X-отчёт (без закрытия смены)", "X-отчёт (сменаны жаппай)", "X-report (shift stays open)", "X raporu (vardiya açık kalır)", "X-hisobot (smenani yopmasdan)");
        OldXReportText.Text = xReport;
        // 2026-10-21 (меню ☰, жалоба владельца): на плитке — коротко «X-отчёт», пояснение мелкой подписью.
        NewXReportText.Text = Tr.T("X-отчёт", "X-отчёт", "X-report", "X raporu", "X-hisobot");
        NewXReportHint.Text = Tr.T("без закрытия смены", "сменаны жаппай", "shift stays open", "vardiya açık kalır", "smenani yopmasdan");
        NewCustomerDisplayText.Text = Tr.T("Экран покупателя", "Сатып алуучунун экраны", "Customer display", "Müşteri ekranı", "Xaridor ekrani");
        NewKeyboardText.Text = Tr.T("Клавиатура Windows", "Windows клавиатурасы", "Windows keyboard", "Windows klavyesi", "Windows klaviaturasi");
        MenuSearchBox.Watermark = Tr.T("Найти действие…", "Аракетти табуу…", "Find an action…", "İşlem bul…", "Amalni topish…");
        NewNothingFound.Text = Tr.T("Ничего не найдено. Попробуйте другое слово: «чек», «возврат», «склад»…",
            "Эч нерсе табылган жок. Башка сөз менен издеңиз: «чек», «кайтаруу», «кампа»…",
            "Nothing found. Try another word: “receipt”, “return”, “stock”…",
            "Hiçbir şey bulunamadı. Başka bir kelime deneyin: «fiş», «iade», «depo»…",
            "Hech narsa topilmadi. Boshqa so'z bilan qidiring: «chek», «qaytarish», «ombor»…");
        AutomationProperties.SetName(MenuSearchBox, Tr.T("Найти действие (Ctrl+K)", "Аракетти табуу (Ctrl+K)", "Find an action (Ctrl+K)", "İşlem bul (Ctrl+K)", "Amalni topish (Ctrl+K)"));
        var clear = Tr.T("Очистить поиск", "Издөөнү тазалоо", "Clear search", "Aramayı temizle", "Qidiruvni tozalash");
        ToolTip.SetTip(SearchClearButton, clear);
        AutomationProperties.SetName(SearchClearButton, clear);
        var version = typeof(SideMenuView).Assembly.GetName().Version;
        NewVersionText.Text = version is null
            ? "NurMarket"
            : $"NurMarket · {Tr.T("версия", "версия", "version", "sürüm", "versiya")} {version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        var close = Tr.T("Закрыть меню (Esc)", "Менюну жабуу (Esc)", "Close the menu (Esc)", "Menüyü kapat (Esc)", "Menyuni yopish (Esc)");
        ToolTip.SetTip(NewMenuCloseButton, close);
        AutomationProperties.SetName(NewMenuCloseButton, close);
        UpdateTitle();
        UpdateDynamicRows();
        UpdateGroupHeaders(); // 2026-10-22 (меню, сворачиваемые разделы): подписи заголовков на языке кассы
        QueueRefresh();
    }

    /// <summary>2026-10-21 (меню ☰, жалоба владельца): имена для экранного диктора и автоматизации (UIA) — у плиток содержимое не
    /// строка, поэтому имя — видимые подписи плитки («Некорректные чеки, 4»; «Экран покупателя, Ctrl+M»).</summary>
    private void UpdateAutomationNames()
    {
        foreach (var c in _cells)
        {
            if (ReferenceEquals(c.Button, MiTheme))
                continue; // имя с состоянием «включена/выключена» — UpdateDynamicRows
            var parts = c.Button.GetLogicalDescendants().OfType<TextBlock>()
                .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text) && t.GetLogicalAncestors().OfType<Control>()
                    .TakeWhile(a => !ReferenceEquals(a, c.Button)).All(a => a.IsVisible))
                .Select(t => t.Text!.Trim()).Distinct().ToList();
            if (parts.Count > 0)
                AutomationProperties.SetName(c.Button, string.Join(", ", parts));
        }
    }

    /// <summary>Шапка нового вида — «NBS · Основная касса» (компания и касса), инициалы кассира.</summary>
    private void UpdateTitle()
    {
        if (NewMenuTitle is null)
            return;
        var cashbox = _vm?.CashboxTitle ?? "";
        var company = CompanyInfoService.LastCompany?.Name;
        NewMenuTitle.Text = string.IsNullOrWhiteSpace(company) || string.Equals(company, cashbox, StringComparison.OrdinalIgnoreCase)
            ? cashbox
            : company + " · " + cashbox;
        ToolTip.SetTip(NewMenuTitle, NewMenuTitle.Text);
        var words = (_vm?.CashierName ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0])).Take(2).ToArray();
        NewInitials.Text = words.Length == 0 ? "•" : string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
    }

    /// <summary>«В кассе 347 581,85 сом» — крупно; если вместо суммы слова («скрыто (слепой пересчёт)») — помельче.</summary>
    private void UpdateBalanceSize()
    {
        var text = _vm?.ShiftBalanceText ?? "";
        NewBalanceText.FontSize = text.Any(char.IsDigit) ? 26 : 16;
    }

    /// <summary>Подписи, которые зависят от состояния кассы: тема (светлая/тёмная), клавиша экрана покупателя.</summary>
    private void UpdateDynamicRows()
    {
        var dark = UserPreferences.Instance.DarkTheme;
        // 2026-10-21 (меню ☰, жалоба владельца): тёмная тема — переключатель: подпись постоянная, положение бегунка — состояние.
        NewThemeText.Text = Tr.T("Тёмная тема", "Караңгы тема", "Dark theme", "Koyu tema", "Qorong'i mavzu");
        ThemeSwitchTrack.Classes.Set("on", dark);
        AutomationProperties.SetName(MiTheme, NewThemeText.Text + ", " + (dark
            ? Tr.T("включена", "күйгүзүлгөн", "on", "açık", "yoqilgan")
            : Tr.T("выключена", "өчүрүлгөн", "off", "kapalı", "o'chirilgan")));
        var gesture = "";
        try
        {
            // Пустая клавиша — без подписи (Display вернул бы «не назначено»).
            var raw = new PosHotkeyService().GetGesture(PosHotkeyAction.ToggleCustomerDisplay);
            gesture = string.IsNullOrWhiteSpace(raw) ? "" : PosHotkeyService.Display(raw);
        }
        catch
        {
            // клавиши не прочитались — подсказки просто нет
        }
        CustomerDisplayKeyText.Text = gesture;
        CustomerDisplayKeyText.IsVisible = !string.IsNullOrWhiteSpace(gesture);
        UpdateBalanceSize();
    }

    // ── ячейки: права (IsVisible кнопки) + поиск ──

    private void CollectCells()
    {
        foreach (var cell in NewMenuRoot.GetLogicalDescendants().OfType<Panel>().Where(p => p.Classes.Contains("km-cell")))
        {
            if (cell.Children.FirstOrDefault() is not Button button)
                continue;
            _cells.Add(new MenuCell(cell, button, cell.Classes.Contains("km-fixed"), cell.Classes.Contains("km-searchonly"),
                Synonyms.TryGetValue(button.Name ?? "", out var syn) ? syn : ""));
            button.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty || e.Property == IsEnabledProperty)
                    QueueRefresh();
            };
        }
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            RefreshCells();
        }, DispatcherPriority.Background);
    }

    /// <summary>Что видно: ячейка — если пункт разрешён (IsVisible кнопки — те же привязки прав) и подходит под поиск; группа —
    /// если в ней есть видимая ячейка; «Система» свёрнута, пока не раскрыта (или пока идёт поиск).</summary>
    private void RefreshCells()
    {
        var searching = _query.Length > 0;
        // Поиск — только результаты: шапка смены уходит, пока в поле есть текст (её действия тоже находятся поиском).
        NewHero.IsVisible = !searching;
        NewSearchPanel.Margin = new Thickness(0, searching ? 0 : 12, 0, 0);
        var shiftOpen = _vm?.IsShiftOpen == true;
        Grid.SetColumn(NewShiftPill, shiftOpen ? 1 : 0);
        NewShiftPill.HorizontalAlignment = shiftOpen ? Avalonia.Layout.HorizontalAlignment.Right : Avalonia.Layout.HorizontalAlignment.Left;
        var tokens = searching ? Normalize(_query).Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        var anyMatch = false;
        foreach (var c in _cells)
        {
            bool show;
            if (c.Fixed)
                show = true;
            else
            {
                var match = !searching || Matches(c, tokens);
                show = c.SearchOnly ? searching && match : match;
                if (searching && match && c.Button.IsVisible)
                    anyMatch = true;
            }
            c.Cell.IsVisible = show && c.Button.IsVisible;
        }

        bool AnyVisible(Control group) => _cells.Any(c => !c.Fixed && c.Cell.IsVisible && group.IsLogicalAncestorOf(c.Cell));

        // 2026-10-21 (меню ☰, жалоба владельца): группа (подпись + сетка плиток) видна, если в ней есть видимая плитка;
        // «Система» больше не сворачивается (IsSystemSectionExpanded — только у «Старого» вида).
        foreach (var group in NewMenuContent.Children.OfType<StackPanel>().Where(p => p.Classes.Contains("km-group-box")))
            group.IsVisible = AnyVisible(group);
        // 2026-10-22 (меню, сворачиваемые разделы): свёрнутые группы — только заголовок; при поиске найденное раскрыто.
        ApplyGroupCollapse(searching, animate: false);

        NewNothingFound.IsVisible = searching && !anyMatch;
        SearchKeyChip.IsVisible = !searching;
        SearchClearButton.IsVisible = searching;

        if (_kb is not null && !IsNavigable(_kb))
            SetKb(null);
        // Первый найденный подсвечен (его откроет Enter) — после раскладки, когда известны места пунктов.
        if (searching && _kb is null)
            Dispatcher.UIThread.Post(() =>
            {
                if (_query.Length > 0 && _kb is null)
                    SetKb(NavButtons().FirstOrDefault(b => !IsHeroOrFoot(b) && !IsGroupHeader(b)), bringIntoView: false);
            }, DispatcherPriority.Loaded);
    }

    private bool Matches(MenuCell c, string[] tokens)
    {
        var labels = string.Join(" ", c.Button.GetLogicalDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text));
        var hay = Normalize(labels + " " + c.Synonyms);
        return tokens.All(t => hay.Contains(t, StringComparison.Ordinal));
    }

    private static string Normalize(string s) => s.ToLowerInvariant().Replace('ё', 'е').Replace('ң', 'н').Replace('ө', 'о').Replace('ү', 'у')
        .Replace("'", "").Replace("ʻ", "").Replace("’", "");

    /// <summary>Синонимы поиска на 5 языках (кассир пишет «чек», «долг», «настрой»… на любом из них).</summary>
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["MiReceipts"] = "чек чеки история копия печать повтор receipt receipts history check копиясы чектер тарых fiş fis geçmiş chek tarix nusxa",
        ["MiReturn"] = "возврат вернуть refund return кайтаруу кайтар iade qaytar",
        ["MiPayDebt"] = "долг долги должник оплата погасить debt карыз qarz borç borc",
        ["MiRental"] = "прокат аренда rent rental ижара kira ijara prokat",
        ["MiClients"] = "клиент клиенты покупатель customer client кардар müşteri musteri mijoz xaridor",
        ["MiDeferredCarts"] = "отлож отложенные корзина пауза hold held deferred калтыр кийинкиге bekle kechiktir savat",
        ["MiWarehouse"] = "склад товар товары остаток stock warehouse inventory кампа товар depo stok ombor tovar",
        // 2026-10-22 (закупки как в вебе): «закупки», «приход», «поставщик», «накладная» — на 5 языках.
        ["MiPurchases"] = "закупка закупки приход приемка приёмка поставщик поставщики накладная заказ поставщику purchase purchases supplier receiving procurement сатып алуу жеткирүүчү кириш satın alma tedarikçi mal kabul xarid yetkazib beruvchi kirim",
        ["MiRestock"] = "пополнение сроки срок годности закупка restock expiry толуктоо мөөнөт stok son kullanma toldirish muddat",
        ["MiTimesheet"] = "табель сотрудник сотрудники смены timesheet staff кызматкер personel xodim",
        ["MiXReport"] = "x-отчёт x отчет отчёт смены итоги смены касса x-report report shift totals отчёт смена rapor vardiya hisobot smena",
        ["MiIrregular"] = "некорректные ошибка ошибки очередь отправка чек чеки irregular failed error туура эмес чек hatalı fiş notogri chek",
        ["MiRemovals"] = "удалено удаление удалить из чека журнал removed deleted чектен алынган silinen fiş chekdan olib tashlangan",
        ["MiSalesFinance"] = "продажи финансы выручка отчёт отчет sales finance revenue report сатуу каржы отчёт satış satis finans rapor sotuv moliya hisobot",
        ["MiAbc"] = "abc авс анализ analysis анализ analiz tahlil отчёт report",
        ["MiSalary"] = "зарплата оклад выплата salary payroll эмгек акы maaş maas ish haqi",
        ["MiCustomerDisplay"] = "экран покупателя монитор дисплей второй экран customer display monitor сатып алуучу экран müşteri ekranı ekran xaridor",
        ["MiKeyboard"] = "клавиатура экранная osk keyboard on-screen клавиатурасы klavye klaviatura",
        ["MiTheme"] = "тема тёмная темная светлая ночь цвет theme dark light тема караңгы ачык tema koyu açık mavzu qorongi yorug",
        ["NewMenuStyleButton"] = "вид меню меню старое новое menu layout style меню көрүнүш menü görünüm menyu korinish",
        ["MiHotkeys"] = "горячие клавиши сочетание hotkeys shortcuts keys ыкчам баскыч kısayol kisayol tezkor tugma",
        ["MiSettings"] = "настрой настройки параметры принтер печать settings options printer жөндөө ayar sozlama",
        ["MiMarketplace"] = "маркетплейс магазин функции тема вид кассы marketplace store shop маркет mağaza dokon",
        ["MiCrm"] = "crm nurcrm срм заявки сайт",
        ["MiLogs"] = "логи лог ошибки журнал log logs errors лог ката hata xato jurnal",
        ["MiSupport"] = "поддержка помощь техподдержка удалённая support help remote колдоо жардам destek yardım yordam",
        ["MiKnowledgeBase"] = "база знаний справка инструкция помощь faq knowledge help билим bilgi bilim yordam",
        ["MiCashOperation"] = "внесение изъятие деньги касса наличные cash in out акча салуу алуу nakit kirim chiqim pul",
        ["MiFindCloseShift"] = "закрыть смену смена close shift сменаны жабуу vardiya kapat smena yopish",
        ["MiFindOpenShift"] = "открыть смену смена open shift сменаны ачуу vardiya aç smena ochish",
        ["MiFindSwitchCashier"] = "сменить кассира кассир пересменка switch cashier кассирди алмаштыруу kasiyer değiştir kassir almashtirish",
        ["MiFindExit"] = "выйти выход рабочий стол закрыть программу exit quit desktop чыгуу çıkış chiqish",
    };

    // ── открытие / закрытие ──

    private void SetOpen(bool open)
    {
        if (_isOpen == open)
            return;
        _isOpen = open;
        CapturesKeyboard = open && IsNewStyle;
        if (open)
        {
            UpdateTitle(); // меню открыли — название компании и кассы свежие
            if (!IsNewStyle)
                return;
            _pending = "";
            _pendingTimer.Stop();
            MenuSearchBox.Text = "";
            _query = "";
            SetKb(null);
            UpdateDynamicRows();
            UpdateWidth();
            RefreshCells();
            UpdateAutomationNames();
            NewMenuScroll.Offset = default;
            _ = RefreshCountsAsync();
            PlayOpenAnimation();
        }
        else
        {
            _animCts?.Cancel();
            _pending = "";
            _pendingTimer.Stop();
            SetKb(null);
            // Кнопки меню фокус не берут; поле поиска — да: закрытое меню не оставляет фокус в скрытом поле (сканер!).
            ReleaseFocus();
            if (MenuSearchBox.Text is { Length: > 0 })
                MenuSearchBox.Text = "";
        }
    }

    /// <summary>Сдвиг слева + прозрачность, 150 мс; затемнение под меню проявляется так же.</summary>
    private async void PlayOpenAnimation()
    {
        _animCts?.Cancel();
        var cts = _animCts = new CancellationTokenSource();
        try
        {
            var slide = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0d), new Setter(TranslateTransform.XProperty, -28d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d), new Setter(TranslateTransform.XProperty, 0d) } },
                },
            };
            var tasks = new List<Task> { slide.RunAsync(NewMenuRoot, cts.Token) };
            if (_host?.Children.FirstOrDefault() is Control backdrop && !ReferenceEquals(backdrop, this))
            {
                var fade = new Animation
                {
                    Duration = TimeSpan.FromMilliseconds(150),
                    Children =
                    {
                        new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(OpacityProperty, 0d) } },
                        new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(OpacityProperty, 1d) } },
                    },
                };
                tasks.Add(fade.RunAsync(backdrop, cts.Token));
            }
            await Task.WhenAll(tasks).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // меню закрыли раньше конца — без анимации значения базовые (видно, на месте)
        }
    }

    /// <summary>Ширина: 440, на окне ≤ 1100 — 408 (3 плитки в ряд по ~126: «Windows клавиатурасы» без разрыва слова), на совсем узком (телефон) — по окну с полосой
    /// затемнения справа. 2026-10-21 (меню ☰, жалоба владельца): было 400 / 360 — плиткам с подписью 14 тесно.</summary>
    private void UpdateWidth()
    {
        if (!NewMenuRoot.IsVisible)
            return;
        var available = _host?.Bounds.Width ?? 0;
        if (available <= 0)
            available = 1366;
        var width = available <= 1100 ? 408 : 440;
        NewMenuRoot.Width = Math.Max(280, Math.Min(width, available - 48));
        UpdateTileWidth();
    }

    /// <summary>Плитки — ровно 3 в ряд: ширина ячейки — треть ряда (не WrapPanel.ItemWidth: с ним скрытая плитка оставляла дыру),
    /// скрытые не занимают места, ряд уплотняется.</summary>
    /// 2026-10-21 (меню ☰, жалоба владельца): так — во всех сетках групп (WrapPanel.km-grid).
    private void UpdateTileWidth()
    {
        var w = NewMenuContent.Bounds.Width;
        if (w <= 0)
            w = NewMenuRoot.Width - NewMenuContent.Margin.Left - NewMenuContent.Margin.Right;
        var item = Math.Floor((w + 8) / 3); // сетка шире содержимого на 8 (Margin -4,0): поля плиток по 4 у краёв
        if (item <= 0)
            return;
        foreach (var grid in NewMenuContent.GetLogicalDescendants().OfType<WrapPanel>().Where(p => p.Classes.Contains("km-grid")))
            foreach (var cell in grid.Children.OfType<Control>())
            {
                if (double.IsNaN(cell.Width) || Math.Abs(cell.Width - item) > 0.5)
                    cell.Width = item;
            }
    }

    // ── шапка: тёмный цвет темы, светлый текст ──

    /// <summary>Светлая тема: фон шапки — цвет текста темы (тёмно-синий у NurMarket, графит, коричневый у «Кыргыз»), текст —
    /// цвет панели. Тёмная: фон — мягкая панель темы, текст — цвет текста. Только кисти темы, меняются вместе с ней.</summary>
    private void ApplyHeroColors()
    {
        foreach (var b in _heroBindings)
            b.Dispose();
        _heroBindings.Clear();
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var bgKey = dark ? "BrushPanelSoft" : "BrushText";
        var fgKey = dark ? "BrushText" : "BrushPanel";
        void Bind(AvaloniaObject target, AvaloniaProperty property, string key) =>
            _heroBindings.Add(target.Bind(property, this.GetResourceObservable(key)));

        Bind(NewHero, Border.BackgroundProperty, bgKey);
        foreach (var tb in NewHero.GetLogicalDescendants().OfType<TextBlock>())
            Bind(tb, TextBlock.ForegroundProperty, MiOpenShift.IsLogicalAncestorOf(tb) ? bgKey : fgKey);
        foreach (var fill in NewHero.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("hero-fill")))
            Bind(fill, Border.BackgroundProperty, fgKey);
        foreach (var btn in NewHero.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("km-hero")))
        {
            Bind(btn, ForegroundProperty, fgKey);
            Bind(btn, BorderBrushProperty, fgKey);
        }
        Bind(MiOpenShift, BackgroundProperty, fgKey);
        Bind(MiOpenShift, ForegroundProperty, bgKey);
        // 2026-10-21 (меню ☰, жалоба владельца): значки кнопок шапки — цветом надписи (у «Открыть смену» — цветом шапки).
        foreach (var path in NewHero.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Path>().Where(p => p.Classes.Contains("hero-ic")))
            Bind(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, MiOpenShift.IsLogicalAncestorOf(path) ? bgKey : fgKey);
        Bind(NewCloseGlyph, Avalonia.Controls.Shapes.Shape.StrokeProperty, fgKey);
        Bind(NewShiftClosedDot, Avalonia.Controls.Shapes.Shape.FillProperty, fgKey);
    }

    // ── счётчики ──

    /// <summary>Счётчики из тех же журналов, что показывают окна: «Некорректные чеки» (продажи при нулевом остатке + неотправленные
    /// чеки очереди), «Отложенные чеки», «Удалено из чека» (сегодня, своя компания). Читаются в фоне — меню открывается сразу.</summary>
    private async Task RefreshCountsAsync()
    {
        var wantIrregular = MiIrregular.IsVisible;
        var wantRemovals = MiRemovals.IsVisible;
        var companyId = CompanyInfoService.LastCompany?.Id;
        try
        {
            var counts = await Task.Run(() =>
            {
                int irregular = 0, deferred = 0, removals = 0;
                try
                {
                    if (wantIrregular)
                        irregular = IrregularReceiptsStore.LoadAll().Count
                                    + OfflinePendingSalesStore.LoadAll().Count(s => !string.Equals(s.Status, OfflineSaleEntry.Synced, StringComparison.OrdinalIgnoreCase));
                }
                catch
                {
                    // журнал не прочитан — без счётчика
                }
                try
                {
                    deferred = DeferredCartsStore.LoadAll().Count;
                }
                catch
                {
                    // без счётчика
                }
                try
                {
                    if (wantRemovals)
                    {
                        var today = DateTime.Today;
                        removals = CartRemovalLogStore.ReadAll().Count(r => r.At.LocalDateTime >= today
                            && (string.IsNullOrWhiteSpace(r.CompanyId)
                                || (!string.IsNullOrWhiteSpace(companyId) && string.Equals(r.CompanyId, companyId, StringComparison.OrdinalIgnoreCase))));
                    }
                }
                catch
                {
                    // без счётчика
                }
                return (irregular, deferred, removals);
            }).ConfigureAwait(true);
            ApplyCounts(counts.irregular, counts.deferred, counts.removals);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Kassa: счётчики меню ☰ не прочитаны: {ex.Message}", "WARNING");
        }
    }

    private void ApplyCounts(int irregular, int deferred, int removals)
    {
        static void Set(Border badge, int n)
        {
            badge.Classes.Set("shown", n > 0);
            if (badge.Child is TextBlock t)
                t.Text = n > 99 ? "99+" : n.ToString();
        }
        Set(IrregularCount, irregular);
        Set(DeferredCartsCount, deferred);
        Set(RemovalsCount, removals);
        UpdateAutomationNames();
        UpdateGroupHeaders(); // 2026-10-22 (меню, сворачиваемые разделы): бейджи свёрнутых групп
    }

    // ── клавиатура и сканер ──

    private void AttachTopLevel(TopLevel? top)
    {
        if (ReferenceEquals(top, _top))
            return;
        if (_top is not null)
        {
            _top.RemoveHandler(KeyDownEvent, OnTopKeyDown);
            _top.RemoveHandler(TextInputEvent, OnTopTextInput);
        }
        _top = top;
        if (_top is not null)
        {
            _top.AddHandler(KeyDownEvent, OnTopKeyDown, RoutingStrategies.Tunnel);
            _top.AddHandler(TextInputEvent, OnTopTextInput, RoutingStrategies.Tunnel);
        }
    }

    private void AttachScanner()
    {
        if (_scanner is not null)
            return;
        try
        {
            _scanner = App.GetRequiredService<IBarcodeInputService>();
            _scanner.BarcodeScanned += OnBarcodeScanned;
        }
        catch
        {
            _scanner = null; // предпросмотр/без служб — меню работает без сканера
        }
    }

    private AvaloniaKeyboardWedgeBarcodeService? Wedge => _scanner as AvaloniaKeyboardWedgeBarcodeService;

    /// <summary>Скан при открытом меню (как у меню «Ещё», MainWindow.OnPopupScanKey): код уже ушёл в чек — меню закрывается,
    /// поле поиска очищается.</summary>
    private void OnBarcodeScanned(string code)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnBarcodeScanned(code));
            return;
        }
        if (!_isOpen || !IsNewStyle)
            return;
        if (_top is WindowBase { IsActive: false })
            return;
        _pending = "";
        _pendingTimer.Stop();
        MenuSearchBox.Text = "";
        ReleaseFocus();
        _vm?.CloseMenuCommand.Execute(null);
    }

    private bool SearchFocused => _top?.FocusManager?.GetFocusedElement() is { } f && ReferenceEquals(f, MenuSearchBox);

    private void OnTopKeyDown(object? sender, KeyEventArgs e)
    {
        if (!_isOpen || !IsNewStyle || e.Handled)
            return;
        var mods = e.KeyModifiers & ~KeyModifiers.Shift;
        var inSearch = SearchFocused;

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            ReleaseFocus();
            _vm?.CloseMenuCommand.Execute(null);
            return;
        }
        if (e.Key == Key.K && mods == KeyModifiers.Control)
        {
            e.Handled = true;
            FocusSearch("");
            return;
        }
        if (mods != KeyModifiers.None)
            return;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Tab when e.KeyModifiers == KeyModifiers.None:
                e.Handled = true;
                MoveKb(+1);
                return;
            case Key.Up:
            case Key.Tab when e.KeyModifiers == KeyModifiers.Shift:
                e.Handled = true;
                MoveKb(-1);
                return;
            case Key.Enter:
                OnEnter(e, inSearch);
                return;
        }

        // 2026-10-22 (меню, сворачиваемые разделы): Пробел на выбранном заголовке группы — свернуть/раскрыть.
        if (HandleGroupSpace(e, inSearch))
            return;
        if (inSearch)
            FeedScannerFromSearch(e);
    }

    /// <summary>Enter: второй Enter сканера (CR+LF) — съедаем; код сканера, набранный в поле поиска, — сканеру (товар в чек, меню
    /// закрывается); иначе — выбранный пункт или первый найденный.</summary>
    private void OnEnter(KeyEventArgs e, bool inSearch)
    {
        if (Wedge is { ScanJustCompleted: true })
        {
            e.Handled = true;
            return;
        }
        if (inSearch)
        {
            if (IsScanEnterInSearch() && _scanner is not null)
            {
                _scanner.ProcessKeyDown(e); // готовый код → BarcodeScanned → OnBarcodeScanned закрывает меню
                e.Handled = true;
                return;
            }
            Wedge?.DiscardBuffer();
        }
        else if (Wedge is { HasCompleteScan: true })
        {
            return; // скан дописывается — пусть его заберёт сканер (MainWindow)
        }
        else if (_pending.Length > 0)
        {
            // Набрали букву и сразу Enter — сначала в поиск, потом первый найденный.
            var text = _pending;
            _pending = "";
            _pendingTimer.Stop();
            Wedge?.DiscardBuffer();
            FocusSearch(text);
            OnQueryChanged();
        }

        e.Handled = true;
        var target = _kb ?? (_query.Length > 0 ? NavButtons().FirstOrDefault(b => !IsHeroOrFoot(b) && !IsGroupHeader(b)) : null);
        if (target is not null)
            Execute(target);
    }

    /// <summary>Пока поле поиска с фокусом, окно кассы клавиши сканеру не передаёт (поле ввода) — передаём сами, но символы
    /// остаются в поле (набор кассира не страдает). Готовый ли это скан, решает IsScanEnterInSearch.</summary>
    private void FeedScannerFromSearch(KeyEventArgs e)
    {
        var now = Environment.TickCount64;
        _searchFastRun = now - _lastSearchKeyTick is >= 0 and < 50 ? _searchFastRun + 1 : 0;
        _lastSearchKeyTick = now;
        if (_scanner is null)
            return;
        var handled = e.Handled;
        _scanner.ProcessKeyDown(e);
        e.Handled = handled;
    }

    /// <summary>Скан в поле поиска: ≥ 4 знака подряд быстрее 50 мс и Enter сразу за ними (человек так не печатает).</summary>
    private bool IsScanEnterInSearch() =>
        _searchFastRun >= 3
        && Environment.TickCount64 - _lastSearchKeyTick is >= 0 and < 150
        && Wedge is { HasCompleteScan: true };

    /// <summary>Первая буква с клавиатуры, пока поле поиска без фокуса: ждём 250 мс — если это сканер, код закончится Enter'ом и
    /// уйдёт в чек (OnBarcodeScanned), а буква — нет; если человек — фокус в поле поиска, набранное — в поле. Цифры — сканеру.</summary>
    private void OnTopTextInput(object? sender, TextInputEventArgs e)
    {
        if (!_isOpen || !IsNewStyle || string.IsNullOrEmpty(e.Text))
            return;
        if (_top?.FocusManager?.GetFocusedElement() is TextBox)
            return;
        if (_pending.Length == 0 && !e.Text.Any(char.IsLetter))
            return;
        _pending += e.Text;
        _pendingTick = Environment.TickCount64;
        if (!_pendingTimer.IsEnabled)
            _pendingTimer.Start();
    }

    private void OnPendingTimer(object? sender, EventArgs e)
    {
        // Знаки ещё сыплются быстро — это сканер: ждём его Enter.
        if (Environment.TickCount64 - _pendingTick < 90)
            return;
        _pendingTimer.Stop();
        var text = _pending;
        _pending = "";
        if (!_isOpen || text.Length == 0 || SearchFocused)
            return;
        Wedge?.DiscardBuffer();
        FocusSearch(text);
    }

    private void FocusSearch(string text)
    {
        if (text.Length > 0)
            MenuSearchBox.Text = text;
        MenuSearchBox.Focus();
        MenuSearchBox.CaretIndex = MenuSearchBox.Text?.Length ?? 0;
        _searchFastRun = 0;
    }

    private void OnQueryChanged()
    {
        var q = (MenuSearchBox.Text ?? "").Trim();
        if (q == _query)
            return;
        _query = q;
        SetKb(null);
        RefreshCells();
        if (_query.Length > 0)
            NewMenuScroll.Offset = default;
    }

    private void SearchClear_Click(object? sender, RoutedEventArgs e)
    {
        MenuSearchBox.Text = "";
        ReleaseFocus();
    }

    // ── выбор с клавиатуры ──

    private static bool IsHeroOrFoot(Button b) => b.Classes.Contains("km-hero") || b.Classes.Contains("km-hero-primary") || b.Classes.Contains("km-foot");

    private bool IsNavigable(Button b) => b.IsEffectivelyVisible && b.IsEffectivelyEnabled;

    /// <summary>Пункты по порядку на экране: шапка, найденное, плитки (слева направо), строки, низ.</summary>
    private List<Button> NavButtons() =>
        _cells.Select(c => c.Button).Concat(GroupHeaders()).Where(IsNavigable)
            .Select(b => (b, p: b.TranslatePoint(default, NewMenuRoot) ?? default))
            .OrderBy(x => Math.Round(x.p.Y / 8)).ThenBy(x => x.p.X)
            .Select(x => x.b).ToList();

    private void MoveKb(int step)
    {
        var list = NavButtons();
        if (list.Count == 0)
            return;
        // 2026-10-21 (меню ☰, жалоба владельца): первое ↓/Tab — на первую плитку («История чеков»), а не на ✕ шапки.
        if (_kb is null && step > 0 && list.FirstOrDefault(b => b.Classes.Contains("km-tile")) is { } firstTile)
        {
            SetKb(firstTile);
            return;
        }
        var i = _kb is null ? -1 : list.IndexOf(_kb);
        i = i < 0 ? (step > 0 ? 0 : list.Count - 1) : (i + step + list.Count) % list.Count;
        SetKb(list[i]);
    }

    private void SetKb(Button? button, bool bringIntoView = true)
    {
        if (ReferenceEquals(button, _kb))
            return;
        _kb?.Classes.Remove("km-kb");
        _kb = button;
        if (_kb is null)
            return;
        _kb.Classes.Add("km-kb");
        if (bringIntoView)
            _kb.BringIntoView();
    }

    /// <summary>Нажать пункт с клавиатуры: те же команды/обработчики, что у щелчка.</summary>
    private void Execute(Button button)
    {
        ReleaseFocus();
        if (button.Command is { } command)
        {
            if (command.CanExecute(button.CommandParameter))
                command.Execute(button.CommandParameter);
        }
        else
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Щелчок по пункту (до выполнения команды): фокус из поля поиска — окну кассы, иначе после закрытия меню и окна
    /// раздела фокус вернулся бы в скрытое поле и сканер перестал бы работать.</summary>
    private void OnNewMenuClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { } b && b.Classes.Contains("km-act"))
            ReleaseFocus();
    }

    private void ReleaseFocus()
    {
        if (_top?.FocusManager?.GetFocusedElement() is not Visual focused || !this.IsVisualAncestorOf(focused))
            return;
        if (_top is WindowBase { IsActive: false })
        {
            _top.FocusManager?.ClearFocus();
            return;
        }
        if (!_top.Focus())
            _top.FocusManager?.ClearFocus();
    }

    // ── пункты, которых нет в SideMenuViewModel: те же команды, что у кнопок шапки кассы и раскладок ──

    private void CloseMenu() => _vm?.CloseMenuCommand.Execute(null);

    private void RunToolbar(Func<MainWindowViewModel, System.Windows.Input.ICommand> pick)
    {
        var main = MainVm;
        CloseMenu();
        if (main is null)
            return;
        var command = pick(main);
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void OpenShift_Click(object? sender, RoutedEventArgs e) => RunToolbar(m => m.Toolbar.OpenShiftCommand);

    private void CloseShift_Click(object? sender, RoutedEventArgs e) => RunToolbar(m => m.Toolbar.CloseShiftCommand);

    private void DeferredCarts_Click(object? sender, RoutedEventArgs e) => RunToolbar(m => m.Basket.OpenDeferredCartsCommand);

    /// <summary>2026-10-20 (живой аудит кассы, К16): «X-отчёт» — окно «Просмотр смены» по открытой смене (MainWindow.ShowXReport).</summary>
    private void XReport_Click(object? sender, RoutedEventArgs e)
    {
        CloseMenu();
        (_top as NurMarketKassa.AvaloniaHost.Views.MainKassir.MainWindow)?.ShowXReport();
    }

    private void CustomerDisplay_Click(object? sender, RoutedEventArgs e) => RunToolbar(m => m.Toolbar.CheckCustomerDisplayCommand);

    private void Keyboard_Click(object? sender, RoutedEventArgs e) => RunToolbar(m => m.Toolbar.ToggleKeyboardCommand);

    /// <summary>2026-10-21 (меню ☰, жалоба владельца): «Тёмная тема» — переключатель: меню не закрывается, бегунок и вся касса
    /// меняются сразу (та же команда, что у кнопки «Тема» в шапке кассы).</summary>
    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        var command = MainVm?.Toolbar.ToggleThemeCommand;
        if (command is null || !command.CanExecute(null))
            return;
        command.Execute(null);
        Dispatcher.UIThread.Post(UpdateDynamicRows);
    }

    /// <summary>«Горячие клавиши» — то же окно, что у кнопки «Горячие» в шапке кассы.</summary>
    private async void Hotkeys_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            CloseMenu();
            // 2026-10-27, аудит С-17: окно «Горячие клавиши» — только с правом «Настройки» (как и кнопка «Горячие» в шапке).
            if (TopLevel.GetTopLevel(this) is MainKassir.MainWindow mainWindow && !mainWindow.AuthorizeSettings())
                return;
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                await new HotkeySettingsWindow(new PosHotkeyService()).ShowDialog<bool>(owner).ConfigureAwait(true);
                owner.Focus();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Kassa: окно «Горячие клавиши» из меню не открылось: {ex.Message}", "WARNING");
        }
    }

    /// <summary>«Вид меню»: меню закрывается, поверх кассы — окно с двумя карточками (старое / новое), меняется сразу.</summary>
    private async void MenuStyle_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _vm?.CloseMenuCommand.Execute(null);
            if (TopLevel.GetTopLevel(this) is Window owner)
                await new OwnerMenuStyleDialog(KassaMenuStyles.Catalog, 672, 278).ShowDialog(owner).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Kassa: окно «Вид меню» не открылось: {ex.Message}", "WARNING");
        }
    }
}
