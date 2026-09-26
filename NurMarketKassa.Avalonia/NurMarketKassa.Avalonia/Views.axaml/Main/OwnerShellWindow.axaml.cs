using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Главное окно программы владельца «NurMarket Владелец» (2026-09-26, разделение программ,
/// см. <see cref="AppMode"/>). Разделы слева — те же окна, что раньше открывались из меню кассы
/// (склад, продажи, финансы, зарплата, ABC, клиенты, CRM…), с теми же проверками прав и тарифа;
/// раздел занимает правую часть этого окна (см. «разделы в окне» ниже).
/// Справа — сводка за день/неделю/месяц с сервера NurCRM: показатели со сравнением с прошлым
/// периодом, выручка по дням, способы оплаты, последние продажи и лучшие товары. Обновляется
/// каждые 20 секунд: продажа, пробитая на кассе, появляется здесь без отдельной синхронизации.</summary>
public partial class OwnerShellWindow : Window, IMainShell
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(20);
    private const int RecentRows = 8;

    // Цвета способов оплаты. Цветом выделена только точка/полоса, подпись — обычным цветом текста
    // темы, поэтому читается одинаково в светлой и тёмной теме.
    private static readonly Dictionary<string, Color> PaymentColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cash"] = Color.Parse("#16A34A"),
        ["transfer"] = Color.Parse("#3B82F6"),
        ["card"] = Color.Parse("#3B82F6"),
        ["mbank"] = Color.Parse("#06B6D4"),
        ["mixed"] = Color.Parse("#8B5CF6"),
        ["split"] = Color.Parse("#8B5CF6"),
        ["debt"] = Color.Parse("#F59E0B"),
    };

    private static readonly Color OtherPaymentColor = Color.Parse("#94A3B8");

    private readonly DispatcherTimer _timer;
    private readonly CancellationTokenSource _cts = new();
    private string _period = "today";
    private bool _refreshing;
    private bool _loggingOut;
    private DateTime? _lastSuccess;

    // Прошлый период не меняется — берём его отчёт один раз на период (и на новый день).
    private string? _compareKey;
    private JsonElement? _compareCards;

    public OwnerShellWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        Opened += async (_, _) =>
        {
            await RefreshAsync().ConfigureAwait(true);
            _timer.Start();
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _cts.Cancel();
            Tr.LanguageChanged -= OnLanguageChanged;
        };
        Tr.LanguageChanged += OnLanguageChanged;
        UseBrush(LiveDot, Shape.FillProperty, "BrushSuccess");

        Closing += (_, _) => CloseAllSections();
        PositionChanged += (_, _) => SyncSectionBounds();
        // Не сразу, а после разметки: размер области раздела меняется раньше, чем её родитель
        // встаёт на новое место (Avalonia ставит Bounds родителю после детей), и при сворачивании
        // меню раздел ложился по старому краю — поверх меню (скриншот владельца 2026-09-26).
        SectionHost.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
                Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
        };

        ApplyTexts();
    }

    /// <summary>То же, что кассе при входе: компания, раздельные данные аккаунтов и проверка
    /// подписки. Без смены, каталога продаж и оборудования — это касса.</summary>
    public async Task<bool> InitializeApplicationAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(Tr.T("Загрузка профиля…", "Профиль жүктөлүүдө…", "Loading profile…", "Profil yükleniyor…", "Profil yuklanmoqda…"));
        try
        {
            var subscription = await CompanyInfoService.RefreshAsync(App.AuthApi, cancellationToken).ConfigureAwait(true);
            AccountDataIsolation.SwitchTo(CompanyInfoService.LastCompany?.Id);
            if (subscription is { IsExpired: true })
            {
                try
                {
                    PosAlertDialog.Show(null,
                        Tr.T("Подписка не оплачена", "Жазылуу төлөнгөн эмес", "Subscription not paid", "Abonelik ödenmedi", "Obuna to'lanmagan"),
                        Tr.T($"Срок действия компании истёк ({subscription.EndDate:dd.MM.yyyy}). Пожалуйста, оплатите!",
                            $"Компаниянын мөөнөтү бүттү ({subscription.EndDate:dd.MM.yyyy}). Сураныч, төлөңүз!",
                            $"The company subscription expired ({subscription.EndDate:dd.MM.yyyy}). Please pay.",
                            $"Şirket aboneliği sona erdi ({subscription.EndDate:dd.MM.yyyy}). Lütfen ödeyin.",
                            $"Kompaniya obunasi tugadi ({subscription.EndDate:dd.MM.yyyy}). Iltimos, to'lang."),
                        PosAlertKind.Error,
                        Tr.T("Оплатить", "Төлөө", "Pay", "Öde", "To'lash"));
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Owner app: subscription alert failed: {ex.Message}", "WARNING");
                }

                OpenUrl("https://www.nurcrm.kg");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: company profile unavailable: {ex.Message}", "WARNING");
        }

        ApplyTexts();
        return true;
    }

    public void PlaceOnPrimaryScreen() => WindowState = WindowState.Maximized;

    private void OnLanguageChanged() => Dispatcher.UIThread.Post(() =>
    {
        ApplyTexts();
        _compareKey = null;
        _ = RefreshAsync();
    });

    // ------------------------------------------------------------------ тексты и разделы

    private static CultureInfo UiCulture
    {
        get
        {
            try
            {
                return CultureInfo.GetCultureInfo(Tr.T("ru-RU", "ky-KG", "en-US", "tr-TR", "uz-Latn-UZ"));
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.GetCultureInfo("ru-RU");
            }
        }
    }

    private void ApplyTexts()
    {
        Title = Tr.T("NurMarket Владелец", "NurMarket Ээси", "NurMarket Owner", "NurMarket Sahibi", "NurMarket Egasi");
        OwnerBadgeText.Text = Tr.T("Владелец", "Ээси", "Owner", "Sahibi", "Egasi");

        var company = CompanyInfoService.LastCompany;
        CompanyNameText.Text = string.IsNullOrWhiteSpace(company?.Name) ? "NurCRM" : company!.Name;
        var plan = TariffGate.CurrentPlanName;
        var end = CompanyInfoService.GetCachedSubscriptionStatus()?.EndDate;
        var tariff = string.IsNullOrWhiteSpace(plan) ? "" : Tr.T($"Тариф «{plan}»", $"Тариф «{plan}»", $"{plan} plan", $"{plan} tarifesi", $"«{plan}» tarifi");
        if (end is { } e && e.Year > 2000)
            tariff += (tariff.Length > 0 ? " · " : "") + Tr.T($"до {e:dd.MM.yyyy}", $"{e:dd.MM.yyyy} чейин", $"until {e:dd.MM.yyyy}", $"{e:dd.MM.yyyy} tarihine kadar", $"{e:dd.MM.yyyy} gacha");
        TariffText.Text = tariff;
        TariffText.IsVisible = tariff.Length > 0;

        var name = PosApp.CurrentUserDisplayName ?? "";
        UserNameText.Text = string.IsNullOrWhiteSpace(name) ? Tr.T("Пользователь", "Колдонуучу", "User", "Kullanıcı", "Foydalanuvchi") : name;
        UserRoleText.Text = Tr.T("Вход через NurCRM", "NurCRM аркылуу кирүү", "Signed in via NurCRM", "NurCRM ile giriş", "NurCRM orqali kirish");
        AvatarText.Text = Initials(name);
        ToolTip.SetTip(ThemeButton, Tr.T("Светлая / тёмная тема", "Жарык / караңгы тема", "Light / dark theme", "Açık / koyu tema", "Yorug' / qorong'i mavzu"));
        ToolTip.SetTip(LogoutButton, Tr.T("Выйти из учётной записи", "Эсептик жазуудан чыгуу", "Sign out", "Oturumu kapat", "Hisobdan chiqish"));
        ToolTip.SetTip(RefreshButton, Tr.T("Обновить сейчас", "Азыр жаңыртуу", "Refresh now", "Şimdi yenile", "Hozir yangilash"));
        ToolTip.SetTip(CollapseButton, Tr.T("Свернуть / развернуть меню", "Менюну жыйноо / ачуу", "Collapse / expand menu", "Menüyü daralt / genişlet", "Menyuni yig'ish / yoyish"));
        UpdateThemeIcon();

        DashboardTitle.Text = Tr.T("Сводка", "Жыйынтык", "Overview", "Özet", "Umumiy ko'rinish");
        SectionLoadingText.Text = Tr.T("Открываем раздел…", "Бөлүм ачылууда…", "Opening…", "Açılıyor…", "Ochilmoqda…");
        var culture = UiCulture;
        var today = DateTime.Today.ToString("dddd, d MMMM yyyy", culture);
        DateText.Text = today.Length > 0 ? char.ToUpper(today[0], culture) + today[1..] : today;
        TodayButton.Content = Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun");
        WeekButton.Content = Tr.T("Неделя", "Жума", "Week", "Hafta", "Hafta");
        MonthButton.Content = Tr.T("Месяц", "Ай", "Month", "Ay", "Oy");

        RevenueLabel.Text = Tr.T("Выручка", "Киреше", "Revenue", "Ciro", "Tushum");
        ChecksLabel.Text = Tr.T("Чеки", "Чектер", "Receipts", "Fişler", "Cheklar");
        AvgLabel.Text = Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek");
        ProfitLabel.Text = Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda");

        ChartTitle.Text = _period == "month"
            ? Tr.T("Выручка по дням месяца", "Айдын күндөрү боюнча киреше", "Revenue by day this month", "Bu ay günlük ciro", "Oy kunlari bo'yicha tushum")
            : Tr.T("Выручка за 7 дней", "7 күндүк киреше", "Revenue, last 7 days", "Son 7 gün ciro", "7 kunlik tushum");
        ChartEmptyText.Text = Tr.T("Продаж за эти дни нет", "Бул күндөрү сатуу жок", "No sales on these days", "Bu günlerde satış yok", "Bu kunlarda sotuv yo'q");
        PaymentsTitle.Text = Tr.T("Способы оплаты", "Төлөм ыкмалары", "Payment methods", "Ödeme yöntemleri", "To'lov usullari");
        PaymentsEmptyText.Text = Tr.T("Оплат пока нет", "Азырынча төлөм жок", "No payments yet", "Henüz ödeme yok", "Hozircha to'lov yo'q");
        ReturnsLabel.Text = Tr.T("Возвраты", "Кайтаруулар", "Returns", "İadeler", "Qaytarishlar");
        RecentTitle.Text = Tr.T("Последние продажи", "Акыркы сатуулар", "Latest sales", "Son satışlar", "So'nggi sotuvlar");
        AllSalesText.Text = Tr.T("Все продажи", "Бардык сатуулар", "All sales", "Tüm satışlar", "Barcha sotuvlar");
        RecentEmptyText.Text = Tr.T("За этот период продаж нет", "Бул мезгилде сатуу жок", "No sales in this period", "Bu dönemde satış yok", "Bu davrda sotuv yo'q");
        TopTitle.Text = Tr.T("Лучшие товары", "Мыкты товарлар", "Top products", "En çok satanlar", "Eng yaxshi mahsulotlar");
        TopEmptyText.Text = Tr.T("Пока нечего показать", "Азырынча көрсөтө турган эч нерсе жок", "Nothing to show yet", "Henüz gösterilecek bir şey yok", "Hozircha ko'rsatadigan narsa yo'q");

        BuildNavigation();
        if (_activeSection != null)
            SectionTitleText.Text = TitleFor(_activeSection);
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var letters = string.Concat(parts.Take(2).Select(p => char.ToUpperInvariant(p[0])));
        return letters.Length > 0 ? letters : "N";
    }

    /// <summary>Разделы по группам, с иконками. Проверки — те же, что у меню кассы
    /// (SideMenuViewModel / MainWindow.Navigate*): права роли и тариф «Старт» (на нём доступны
    /// склад и настройки, как на сайте). Недоступные разделы не показываются — как в кассе.</summary>
    private void BuildNavigation()
    {
        NavPanel.Children.Clear();
        _navButtons.Clear();
        var isStart = TariffGate.IsStartTariff;
        var pendingGroup = (string?)null;

        var collapsed = UserPreferences.Instance.OwnerSidebarCollapsed;
        ApplySidebarLayout(collapsed);

        void Group(string title) => pendingGroup = title;

        void Add(string key, string iconKey, string text, bool visible, Action open)
        {
            _navTitles[key] = text;
            if (!visible)
                return;

            if (pendingGroup != null)
            {
                // В свёрнутом меню вместо подписи группы — тонкая черта.
                NavPanel.Children.Add(collapsed
                    ? new Border { Height = 1, Margin = new Thickness(8, 10), Background = Brushes.Transparent, Classes = { "navGroupLine" } }
                    : new TextBlock { Text = pendingGroup.ToUpper(UiCulture), Classes = { "navGroup" } });
                pendingGroup = null;
            }

            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            if (this.TryFindResource(iconKey, out var icon) && icon is Geometry geometry)
                content.Children.Add(new Path { Data = geometry, Classes = { "navIcon" } });
            var label = new TextBlock
            {
                Text = text,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(label, 1);
            content.Children.Add(label);

            var button = new Button { Content = content, Classes = { "nav" } };
            ToolTip.SetTip(button, text);
            if (collapsed)
            {
                label.IsVisible = false;
                button.Padding = new Thickness(0);
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
            }
            _navButtons[key] = button;
            button.Click += (_, _) =>
            {
                try
                {
                    open();
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Owner app: section failed to open: {ex}", "ERROR");
                    PosMessageBox.Show(this, ex.Message, Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            };
            NavPanel.Children.Add(button);
        }

        Add("overview", "HomeIcon", Tr.T("Сводка", "Жыйынтык", "Overview", "Özet", "Umumiy ko'rinish"), true,
            () => ShowSection(null));

        Group(Tr.T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar"));
        Add("warehouse", "WarehouseIcon", Tr.T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"), true,
            () => { if (Authorize(PosPermissions.ViewProcurement)) OpenSection("warehouse", () => App.GetRequiredService<WarehouseWindow>()); });
        Add("calculator", "CalculatorIcon", Tr.T("Калькуляция", "Калькуляция", "Pricing calculator", "Hesaplama", "Kalkulyatsiya"), true,
            () => OpenSection("calculator", () => new CalculatorWindow()));
        Add("restock", "RestockIcon", Tr.T("Пополнение и сроки", "Толуктоо жана мөөнөттөр", "Restock & expiry", "Stok ve SKT", "To'ldirish va muddatlar"), !isStart,
            () => OpenSection("restock", () => App.GetRequiredService<RestockSuggestionsWindow>()));

        Group(Tr.T("Продажи и деньги", "Сатуу жана акча", "Sales & money", "Satış ve para", "Sotuv va pul"));
        Add("sales", "SalesIcon", Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar"), !isStart,
            () => { if (Authorize(PosPermissions.ViewSales)) OpenSection("sales", () => App.GetRequiredService<SalesWindow>()); });
        Add("finance", "FinanceIcon", Tr.T("Финансы", "Каржы", "Finance", "Finans", "Moliya"), !isStart,
            () => OpenSection("finance", () => App.GetRequiredService<FinanceWindow>()));
        Add("abc", "AbcIcon", Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC-tahlil"), !isStart,
            () => { if (Authorize(PosPermissions.ViewSales)) OpenSection("abc", () => App.GetRequiredService<AbcAnalysisWindow>()); });

        Group(Tr.T("Люди", "Адамдар", "People", "Kişiler", "Odamlar"));
        Add("clients", "ClientsIcon", Tr.T("Клиенты", "Кардарлар", "Customers", "Müşteriler", "Mijozlar"), TariffGate.CanViewClients,
            () => { if (Authorize(PosPermissions.ViewSales)) OpenSection("clients", () => App.GetRequiredService<ClientsWindow>()); });
        Add("salary", "SalaryIcon", Tr.T("Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi"), !isStart,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("salary", () => new SalaryWindow()); });

        Group(Tr.T("Сервис", "Кызмат", "Service", "Hizmet", "Xizmat"));
        Add("crm", "CrmIcon", "NurCRM", !isStart,
            () => OpenSection("crm", () => App.GetRequiredService<CrmWebViewWindow>()));
        Add("marketplace", "MarketplaceIcon", Tr.T("Маркетплейс", "Маркетплейс", "Marketplace", "Pazar yeri", "Marketpleys"), true,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("marketplace", () => new MarketplaceWindow()); });
        Add("settings", "SettingsIcon", Tr.T("Настройки", "Жөндөөлөр", "Settings", "Ayarlar", "Sozlamalar"), true,
            () => { if (Authorize(PosPermissions.ViewSettings)) OpenSection("settings", () => App.GetRequiredService<PosSettingsWindow>()); });

        Group(Tr.T("Помощь", "Жардам", "Help", "Yardım", "Yordam"));
        Add("kb", "KnowledgeBaseIcon", Tr.T("База знаний", "Билим базасы", "Knowledge base", "Bilgi bankası", "Bilimlar bazasi"), !isStart,
            () => OpenSection("kb", () => App.GetRequiredService<KnowledgeBaseWindow>()));
        Add("support", "RemoteSupportIcon", Tr.T("Тех. поддержка", "Техколдоо", "Support", "Destek", "Texnik yordam"), !isStart,
            () => OpenSection("support", () => App.GetRequiredService<RemoteSupportWindow>()));
        Add("logs", "ErrorLogIcon", Tr.T("Журнал ошибок", "Каталар журналы", "Error log", "Hata günlüğü", "Xatolar jurnali"), !isStart,
            () => OpenSection("logs", () => App.GetRequiredService<LogsAndErrorsWindow>()));

        // Как в меню кассы: закрыть программу и выйти на рабочий стол (вход при этом сохраняется).
        Group(Tr.T("Система", "Система", "System", "Sistem", "Tizim"));
        Add("exit", "PowerIcon", Tr.T("Выйти на рабочий стол", "Иш столуна чыгуу", "Exit to desktop", "Masaüstüne çık", "Ish stoliga chiqish"), true,
            () =>
            {
                App.ExitWithoutLoginRedirect = true;
                Close();
            });

        UpdateNavHighlight();
    }

    // ------------------------------------------------------------------ свёрнутое меню

    private void Collapse_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        prefs.OwnerSidebarCollapsed = !prefs.OwnerSidebarCollapsed;
        prefs.SaveToDisk();
        BuildNavigation();
        Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
    }

    /// <summary>Свёрнутое меню — только иконки (подпись всплывает подсказкой), без карточки
    /// компании и имени пользователя; правая часть окна и открытый раздел занимают освободившееся
    /// место (SectionHost меняет размер — SyncSectionBounds двигает раздел).</summary>
    private void ApplySidebarLayout(bool collapsed)
    {
        RootGrid.ColumnDefinitions[0].Width = new GridLength(collapsed ? 72 : 252);
        LogoImage.IsVisible = !collapsed;
        LogoTextPanel.IsVisible = !collapsed;
        LogoGrid.Margin = collapsed ? new Thickness(0, 18, 0, 14) : new Thickness(18, 18, 12, 14);
        LogoGrid.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        CompanyCard.IsVisible = !collapsed;
        UserInfoPanel.IsVisible = !collapsed;
        ThemeButton.IsVisible = !collapsed;
        LogoutButton.IsVisible = !collapsed;
        UserFooter.Padding = collapsed ? new Thickness(16, 12) : new Thickness(14, 12);
        ToolTip.SetTip(AvatarText, collapsed ? UserNameText.Text : null);
    }

    // ------------------------------------------------------------------ разделы в окне

    // 2026-09-26, просьба владельца: разделы не отдельными окнами поверх, а «целое окно меняется»
    // при выборе пункта меню. Разделы — большие окна кассы (склад, финансы…) со своими диалогами,
    // сканером, Esc и загрузкой при открытии; переписывать их в панели — большой риск для кассы,
    // где они тоже работают. Поэтому окно раздела остаётся окном, но без рамки и принадлежит этому
    // окну: оно лежит ровно поверх правой части, двигается и меняет размер вместе с ним. Слева
    // остаётся меню, сверху — узкая полоса с названием раздела и кнопками окна. Раздел, открытый
    // раньше, при переходе прячется, а не закрывается: вернуться в него можно сразу, как было.

    private sealed class Section
    {
        public required string Key { get; init; }
        public required Window Window { get; init; }
    }

    private readonly List<Section> _sections = new();
    private readonly Dictionary<string, Button> _navButtons = new();
    private readonly Dictionary<string, string> _navTitles = new();
    private Section? _activeSection;
    private bool _positioningSection;
    private bool _closingAllSections;

    private string TitleFor(Section section) =>
        _navTitles.TryGetValue(section.Key, out var title) ? title : section.Window.Title ?? section.Key;

    private void OpenSection(string key, Func<Window> create)
    {
        var existing = _sections.FirstOrDefault(s => s.Key == key);
        if (existing != null)
        {
            ShowSection(existing);
            return;
        }

        var window = create();
        PrepareEmbeddedWindow(window);
        var section = new Section { Key = key, Window = window };
        window.Closed += (_, _) => OnSectionClosed(section);
        _sections.Add(section);
        PosLogger.Log($"Owner app: раздел «{TitleFor(section)}» открыт.", "UI");
        ShowSection(section);
    }

    /// <summary>Окно раздела без рамки, значка в панели задач и ограничений размера — его место
    /// и размер задаёт SyncSectionBounds. Свои кнопки «свернуть/развернуть» у разделов с
    /// нарисованной шапкой прячем: окно сворачивается и разворачивается целиком, кнопками сверху.</summary>
    private void PrepareEmbeddedWindow(Window window)
    {
        window.SystemDecorations = SystemDecorations.None;
        window.ExtendClientAreaToDecorationsHint = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.SizeToContent = SizeToContent.Manual;
        window.CanResize = false;
        window.ShowInTaskbar = false;
        window.Topmost = false;
        window.MinWidth = 0;
        window.MinHeight = 0;
        window.MaxWidth = double.PositiveInfinity;
        window.MaxHeight = double.PositiveInfinity;
        window.WindowState = WindowState.Normal;

        window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var button in window.GetVisualDescendants().OfType<Button>())
            {
                var classes = button.Classes;
                if ((classes.Contains("WindowControlButton") && !classes.Contains("WindowCloseButton"))
                    || (classes.Contains("caption") && !classes.Contains("close")))
                    button.IsVisible = false;
            }

            SyncSectionBounds();
        }, DispatcherPriority.Loaded);

        // Раздел сам себя не двигает и не разворачивает: перетаскивание за его шапку или двойной
        // щелчок возвращаем на место.
        window.PositionChanged += (_, _) =>
        {
            if (!_positioningSection && ReferenceEquals(_activeSection?.Window, window))
                Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Background);
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty || _positioningSection || window.WindowState == WindowState.Normal)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (WindowState != WindowState.Minimized && ReferenceEquals(_activeSection?.Window, window))
                {
                    window.WindowState = WindowState.Normal;
                    SyncSectionBounds();
                }
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>null — сводка. Иначе раздел занимает правую часть окна, шапка сжимается до
    /// полосы с названием раздела и кнопками окна.</summary>
    private void ShowSection(Section? section)
    {
        _activeSection = section;
        foreach (var other in _sections)
        {
            if (!ReferenceEquals(other, section) && other.Window.IsVisible)
                other.Window.Hide();
        }

        var overview = section == null;
        OverviewScroll.IsVisible = overview;
        OverviewTools.IsVisible = overview;
        TitlePanel.IsVisible = overview;
        SectionTitleText.IsVisible = !overview;
        SectionHost.IsVisible = !overview;
        HeaderGrid.Margin = overview ? new Thickness(28, 12, 10, 4) : new Thickness(24, 4, 10, 4);
        if (section != null)
            SectionTitleText.Text = TitleFor(section);
        UpdateNavHighlight();
        if (section == null)
            return;

        // Область раздела только что стала видимой — размеры у неё появятся после разметки.
        UpdateLayout();
        SyncSectionBounds();
        if (!section.Window.IsVisible)
            section.Window.Show(this);
        section.Window.Activate();
        Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
    }

    private void UpdateNavHighlight()
    {
        var activeKey = _activeSection?.Key ?? "overview";
        foreach (var (key, button) in _navButtons)
            button.Classes.Set("active", key == activeKey);
    }

    /// <summary>Кладёт окно открытого раздела ровно на правую часть окна.</summary>
    private void SyncSectionBounds()
    {
        var section = _activeSection;
        if (section == null || !IsVisible || WindowState == WindowState.Minimized || !SectionHost.IsVisible)
            return;

        var size = SectionHost.Bounds.Size;
        if (size.Width < 50 || size.Height < 50)
            return;

        var topLeft = SectionHost.PointToScreen(new Point(0, 0));
        var window = section.Window;
        _positioningSection = true;
        try
        {
            if (window.WindowState != WindowState.Normal)
                window.WindowState = WindowState.Normal;
            if (window.Position != topLeft)
                window.Position = topLeft;
            if (Math.Abs(window.Width - size.Width) > 0.5)
                window.Width = size.Width;
            if (Math.Abs(window.Height - size.Height) > 0.5)
                window.Height = size.Height;
        }
        finally
        {
            _positioningSection = false;
        }
    }

    /// <summary>Раздел закрылся сам (своя кнопка «закрыть», Esc) — возвращаемся к сводке.</summary>
    private void OnSectionClosed(Section section)
    {
        if (!_sections.Remove(section) || _closingAllSections || !ReferenceEquals(_activeSection, section))
            return;
        ShowSection(null);
    }

    private void CloseAllSections()
    {
        _closingAllSections = true;
        foreach (var section in _sections.ToList())
        {
            try
            {
                section.Window.Close();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Owner app: section close failed: {ex.Message}", "WARNING");
            }
        }
    }

    private bool Authorize(string permission)
    {
        if (App.GetRequiredService<IPermissionService>().HasPermission(permission))
            return true;
        PosLogger.Log($"Owner app: permission denied: {permission}", "WARNING");
        PosMessageBox.Show(this,
            Tr.T("Недостаточно прав для этого раздела.", "Бул бөлүм үчүн укук жетишсиз.", "Not enough rights for this section.",
                "Bu bölüm için yetki yetersiz.", "Bu bo'lim uchun huquq yetarli emas."),
            Title ?? "", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        return false;
    }

    // ------------------------------------------------------------------ сводка

    private (DateTime From, DateTime To) CurrentRange()
    {
        var today = DateTime.Today;
        return _period switch
        {
            // Неделя — с понедельника, месяц — с 1-го числа, как в «Продажах» кассы и на сайте.
            "week" => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),
            "month" => (new DateTime(today.Year, today.Month, 1), today),
            _ => (today, today),
        };
    }

    /// <summary>С чем сравнивать: вчера; те же дни прошлой недели; те же числа прошлого месяца.</summary>
    private (DateTime From, DateTime To) PreviousRange(DateTime from, DateTime to)
    {
        var days = (to - from).Days;
        switch (_period)
        {
            case "week":
                return (from.AddDays(-7), from.AddDays(-7 + days));
            case "month":
            {
                var prevFrom = from.AddMonths(-1);
                var lastDay = DateTime.DaysInMonth(prevFrom.Year, prevFrom.Month) - 1;
                return (prevFrom, prevFrom.AddDays(Math.Min(days, lastDay)));
            }
            default:
                return (from.AddDays(-1), to.AddDays(-1));
        }
    }

    private string CompareHint() => _period switch
    {
        "week" => Tr.T("к прошлой неделе", "өткөн жумага", "vs last week", "geçen haftaya göre", "o'tgan haftaga"),
        "month" => Tr.T("к прошлому месяцу", "өткөн айга", "vs last month", "geçen aya göre", "o'tgan oyga"),
        _ => Tr.T("к вчера", "кечээкиге", "vs yesterday", "düne göre", "kechagiga"),
    };

    private async Task RefreshAsync()
    {
        if (_refreshing || _loggingOut || _cts.IsCancellationRequested)
            return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var (from, to) = CurrentRange();
            var ct = _cts.Token;

            var report = await App.SalesApi.MarketSalesReportAsync(from, to, ct).ConfigureAwait(true);

            var (prevFrom, prevTo) = PreviousRange(from, to);
            var compareKey = $"{_period}:{prevFrom:yyyyMMdd}:{prevTo:yyyyMMdd}";
            if (_compareKey != compareKey)
            {
                var previous = await App.SalesApi.MarketSalesReportAsync(prevFrom, prevTo, ct).ConfigureAwait(true);
                _compareCards = previous.ValueKind == JsonValueKind.Object && previous.TryGetProperty("cards", out var pc)
                    ? pc.Clone()
                    : null;
                _compareKey = compareKey;
            }

            // График: для «сегодня» и «недели» — последние 7 дней, для месяца — дни месяца (они уже
            // есть в отчёте за период).
            var chartSource = report;
            var (chartFrom, chartTo) = (from, to);
            if (_period != "month")
            {
                (chartFrom, chartTo) = (DateTime.Today.AddDays(-6), DateTime.Today);
                chartSource = await App.SalesApi.MarketSalesReportAsync(chartFrom, chartTo, ct).ConfigureAwait(true);
            }

            var rows = await App.SalesApi.PosSalesListAsync(1, RecentRows, null, ct, dateFrom: from, dateToExclusive: to.AddDays(1))
                .ConfigureAwait(true);

            ApplyCards(report);
            ApplyChart(chartSource, chartFrom, chartTo);
            ApplyPayments(report);
            ApplyTopProducts(report);
            ApplyRecent(rows);

            _lastSuccess = DateTime.Now;
            UseBrush(LiveDot, Shape.FillProperty, "BrushSuccess");
            UpdatedText.Text = Tr.T($"Обновлено в {DateTime.Now:HH:mm}", $"{DateTime.Now:HH:mm} жаңыртылды", $"Updated at {DateTime.Now:HH:mm}",
                $"{DateTime.Now:HH:mm} güncellendi", $"{DateTime.Now:HH:mm} da yangilandi");
            ToolTip.SetTip(UpdatedText, Tr.T($"Обновляется само каждые {RefreshInterval.TotalSeconds:0} с",
                $"Ар {RefreshInterval.TotalSeconds:0} с сайын өзү жаңырат", $"Refreshes every {RefreshInterval.TotalSeconds:0} s",
                $"Her {RefreshInterval.TotalSeconds:0} sn yenilenir", $"Har {RefreshInterval.TotalSeconds:0} s da yangilanadi"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: overview refresh failed: {ex.Message}", "WARNING");
            UseBrush(LiveDot, Shape.FillProperty, "BrushWarning");
            UpdatedText.Text = _lastSuccess is { } at
                ? Tr.T($"Нет связи · данные на {at:HH:mm}", $"Байланыш жок · {at:HH:mm} маалыматы", $"Offline · data as of {at:HH:mm}",
                    $"Bağlantı yok · {at:HH:mm} verileri", $"Aloqa yo'q · {at:HH:mm} ma'lumotlari")
                : Tr.T("Нет связи с сервером", "Сервер менен байланыш жок", "No connection to the server", "Sunucuyla bağlantı yok", "Server bilan aloqa yo'q");
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void ApplyCards(JsonElement report)
    {
        if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("cards", out var cards))
            return;

        var prev = _compareCards;
        var hint = CompareHint();

        SetMoney(RevenueValue, Num(cards, "revenue"));
        SetDelta(RevenueDeltaPill, RevenueDelta, RevenueDeltaHint, Num(cards, "revenue"), prev is { } p1 ? Num(p1, "revenue") : null, hint);

        ChecksValue.Inlines = null;
        ChecksValue.Text = Num(cards, "transactions").ToString("N0", UiCulture);
        SetDelta(ChecksDeltaPill, ChecksDelta, ChecksDeltaHint, Num(cards, "transactions"), prev is { } p2 ? Num(p2, "transactions") : null, hint);

        SetMoney(AvgValue, Num(cards, "avg_check"));
        SetDelta(AvgDeltaPill, AvgDelta, AvgDeltaHint, Num(cards, "avg_check"), prev is { } p3 ? Num(p3, "avg_check") : null, hint);

        SetMoney(ProfitValue, Num(cards, "gross_profit"));
        var margin = Num(cards, "margin_percent");
        var profitHint = margin != 0
            ? Tr.T($"маржа {margin:0.#}%", $"маржа {margin:0.#}%", $"margin {margin:0.#}%", $"marj %{margin:0.#}", $"marja {margin:0.#}%")
            : hint;
        SetDelta(ProfitDeltaPill, ProfitDelta, ProfitDeltaHint, Num(cards, "gross_profit"), prev is { } p4 ? Num(p4, "gross_profit") : null, profitHint);
    }

    /// <summary>Сумма крупно, «сом» мелко и приглушённо.</summary>
    private void SetMoney(TextBlock target, double value)
    {
        var currency = new Run(" " + Som()) { FontSize = 15, FontWeight = FontWeight.Medium };
        UseBrush(currency, TextElement.ForegroundProperty, "BrushTextSoft");
        target.Text = null;
        target.Inlines = new InlineCollection { new Run(Amount(value)), currency };
    }

    private static void SetDelta(Border pill, TextBlock text, TextBlock hint, double current, double? previous, string hintText)
    {
        pill.Classes.Set("up", false);
        pill.Classes.Set("down", false);
        hint.Text = hintText;

        if (previous is not { } prev || prev <= 0)
        {
            text.Text = "—";
            return;
        }

        var change = (current - prev) / prev * 100;
        if (Math.Abs(change) < 0.5)
        {
            text.Text = "0%";
            return;
        }

        var up = change > 0;
        pill.Classes.Set(up ? "up" : "down", true);
        var abs = Math.Abs(change);
        text.Text = (up ? "▲ " : "▼ ") + (abs >= 100 ? abs.ToString("0", CultureInfo.InvariantCulture) : abs.ToString("0.#", UiCulture)) + "%";
    }

    private void ApplyChart(JsonElement report, DateTime from, DateTime to)
    {
        ChartBars.Children.Clear();
        ChartBars.ColumnDefinitions.Clear();
        ChartBars.RowDefinitions = new RowDefinitions("*,Auto");

        var byDay = new Dictionary<DateTime, double>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("charts", out var charts)
            && charts.TryGetProperty("sales_dynamics", out var dynamics)
            && dynamics.ValueKind == JsonValueKind.Array)
        {
            foreach (var point in dynamics.EnumerateArray())
            {
                if (DateTime.TryParseExact(Str(point, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    byDay[day.Date] = Num(point, "value");
            }
        }

        var days = new List<DateTime>();
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            days.Add(d);

        var values = days.Select(d => byDay.TryGetValue(d, out var v) ? v : 0).ToList();
        var max = values.Count == 0 ? 0 : values.Max();
        var total = values.Sum();
        ChartTotalText.Text = Tr.T($"Итого: {Amount(total)} {Som()}", $"Бардыгы: {Amount(total)} {Som()}", $"Total: {Amount(total)} {Som()}",
            $"Toplam: {Amount(total)} {Som()}", $"Jami: {Amount(total)} {Som()}");
        ChartEmptyText.IsVisible = max <= 0;
        ChartBars.IsVisible = max > 0;
        if (max <= 0)
            return;

        var culture = UiCulture;
        var few = days.Count <= 10;
        const double barArea = 150;
        for (var i = 0; i < days.Count; i++)
        {
            ChartBars.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            var day = days[i];
            var value = values[i];
            var isToday = day == DateTime.Today;

            var column = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            if (few && value > 0)
            {
                var valueText = new TextBlock
                {
                    Text = Compact(value),
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 5),
                    FontWeight = isToday ? FontWeight.SemiBold : FontWeight.Normal,
                };
                UseBrush(valueText, TextBlock.ForegroundProperty, isToday ? "BrushText" : "BrushTextSoft");
                column.Children.Add(valueText);
            }

            var bar = new Border
            {
                Height = value > 0 ? Math.Max(4, barArea * value / max) : 2,
                CornerRadius = new CornerRadius(6, 6, 2, 2),
                Margin = new Thickness(few ? 10 : 2, 0),
                Opacity = isToday ? 1 : 0.55,
            };
            UseBrush(bar, Border.BackgroundProperty, value > 0 ? "BrushAccent" : "BrushBorder");
            ToolTip.SetTip(bar, $"{day.ToString("ddd, d MMMM", culture)} — {Amount(value)} {Som()}");
            column.Children.Add(bar);
            Grid.SetColumn(column, i);
            ChartBars.Children.Add(column);

            var label = new TextBlock
            {
                Text = few ? $"{day.ToString("ddd", culture)}\n{day.Day}" : day.Day.ToString(CultureInfo.InvariantCulture),
                FontSize = few ? 11.5 : 10.5,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
                FontWeight = isToday ? FontWeight.Bold : FontWeight.Normal,
            };
            UseBrush(label, TextBlock.ForegroundProperty, isToday ? "BrushText" : "BrushTextSoft");
            Grid.SetColumn(label, i);
            Grid.SetRow(label, 1);
            ChartBars.Children.Add(label);
        }
    }

    private void ApplyPayments(JsonElement report)
    {
        PaymentsBar.Children.Clear();
        PaymentsBar.ColumnDefinitions.Clear();
        PaymentsLegend.Children.Clear();

        var methods = new List<(string Method, double Count, double Total)>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("charts", out var charts)
            && charts.TryGetProperty("payment_methods", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in list.EnumerateArray())
            {
                var total = Num(m, "total");
                if (total > 0)
                    methods.Add((Str(m, "method"), Num(m, "count"), total));
            }
        }

        methods.Sort((a, b) => b.Total.CompareTo(a.Total));
        var sum = methods.Sum(m => m.Total);
        PaymentsCountText.Text = sum > 0 ? $"{Amount(sum)} {Som()}" : "";
        PaymentsEmptyText.IsVisible = methods.Count == 0;

        for (var i = 0; i < methods.Count; i++)
        {
            var (method, count, total) = methods[i];
            var color = PaymentColors.TryGetValue(method, out var c) ? c : OtherPaymentColor;
            var share = sum > 0 ? total / sum : 0;

            PaymentsBar.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(share, 0.004), GridUnitType.Star));
            var segment = new Border
            {
                Background = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, i < methods.Count - 1 ? 2 : 0, 0),
            };
            Grid.SetColumn(segment, i);
            PaymentsBar.Children.Add(segment);

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
            row.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });

            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var nameRun = new Run(PaymentLabel(method)) { FontSize = 13.5 };
            UseBrush(nameRun, TextElement.ForegroundProperty, "BrushText");
            var countRun = new Run("  " + ChecksWord(count)) { FontSize = 12 };
            UseBrush(countRun, TextElement.ForegroundProperty, "BrushTextSoft");
            name.Inlines = new InlineCollection { nameRun, countRun };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var amount = new TextBlock { Text = $"{Amount(total)} {Som()}", FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            UseBrush(amount, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(amount, 2);
            row.Children.Add(amount);

            var percent = new TextBlock
            {
                Text = (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                FontSize = 12.5,
                MinWidth = 44,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            UseBrush(percent, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(percent, 3);
            row.Children.Add(percent);

            PaymentsLegend.Children.Add(row);
        }

        // Возвраты — как на сайте: «Документы» → «Возврат продажи».
        double returnsCount = 0, returnsSum = 0;
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("tables", out var tables)
            && tables.TryGetProperty("documents", out var docs)
            && docs.ValueKind == JsonValueKind.Array)
        {
            foreach (var doc in docs.EnumerateArray())
            {
                if (string.Equals(Str(doc, "name"), "Возврат продажи", StringComparison.OrdinalIgnoreCase))
                {
                    returnsCount = Num(doc, "count");
                    returnsSum = Num(doc, "sum");
                }
            }
        }

        ReturnsValue.Text = returnsCount > 0 ? $"{returnsCount:0} · {Amount(returnsSum)} {Som()}" : "0";
    }

    private void ApplyTopProducts(JsonElement report)
    {
        TopList.Children.Clear();
        var items = new List<(string Name, double Sold, double Revenue)>();
        if (report.ValueKind == JsonValueKind.Object
            && report.TryGetProperty("tables", out var tables)
            && tables.TryGetProperty("top_products", out var top)
            && top.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in top.EnumerateArray())
                items.Add((Str(p, "name"), Num(p, "sold"), Num(p, "revenue")));
        }

        items = items.Where(i => i.Revenue > 0).OrderByDescending(i => i.Revenue).Take(5).ToList();
        TopEmptyText.IsVisible = items.Count == 0;
        var max = items.Count == 0 ? 0 : items.Max(i => i.Revenue);

        for (var i = 0; i < items.Count; i++)
        {
            var (name, sold, revenue) = items[i];
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            var rank = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
            UseBrush(rank, Border.BackgroundProperty, i == 0 ? "BrushAccent" : "BrushInputAlt");
            var rankText = new TextBlock
            {
                Text = (i + 1).ToString(CultureInfo.InvariantCulture),
                FontSize = 12.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            UseBrush(rankText, TextBlock.ForegroundProperty, i == 0 ? "BrushAccentForeground" : "BrushText");
            rank.Child = rankText;
            row.Children.Add(rank);

            var middle = new StackPanel { Margin = new Thickness(12, 0, 12, 0), Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = name, FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            UseBrush(title, TextBlock.ForegroundProperty, "BrushText");
            middle.Children.Add(title);

            var share = max > 0 ? revenue / max : 0;
            var track = new Border { Height = 5, CornerRadius = new CornerRadius(3), ClipToBounds = true };
            UseBrush(track, Border.BackgroundProperty, "BrushInputAlt");
            var fillGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(Math.Max(share, 0.01), GridUnitType.Star),
                    new ColumnDefinition(Math.Max(1 - share, 0.0001), GridUnitType.Star),
                },
            };
            var fill = new Border { CornerRadius = new CornerRadius(3) };
            UseBrush(fill, Border.BackgroundProperty, "BrushAccent");
            fillGrid.Children.Add(fill);
            track.Child = fillGrid;
            middle.Children.Add(track);
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            var revenueText = new TextBlock { Text = $"{Amount(revenue)} {Som()}", FontSize = 13, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            UseBrush(revenueText, TextBlock.ForegroundProperty, "BrushText");
            var soldText = new TextBlock
            {
                Text = Tr.T($"продано {Qty(sold)}", $"сатылды {Qty(sold)}", $"sold {Qty(sold)}", $"satılan {Qty(sold)}", $"sotildi {Qty(sold)}"),
                FontSize = 11.5,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            UseBrush(soldText, TextBlock.ForegroundProperty, "BrushTextSoft");
            right.Children.Add(revenueText);
            right.Children.Add(soldText);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            TopList.Children.Add(row);
        }
    }

    private void ApplyRecent(List<JsonElement> rows)
    {
        RecentList.Children.Clear();
        var shown = rows.Take(RecentRows).ToList();
        RecentEmptyText.IsVisible = shown.Count == 0;

        for (var i = 0; i < shown.Count; i++)
        {
            var sale = shown[i];
            var canceled = string.Equals(Str(sale, "status"), "canceled", StringComparison.OrdinalIgnoreCase);
            var method = Str(sale, "payment_method");

            var line = new Border { Padding = new Thickness(0, 10), BorderThickness = new Thickness(0, 0, 0, i < shown.Count - 1 ? 1 : 0) };
            UseBrush(line, Border.BorderBrushProperty, "BrushBorder");
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,Auto,Auto") };

            var created = Str(sale, "created_at");
            var time = DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
                ? dto.LocalDateTime.ToString(dto.LocalDateTime.Date == DateTime.Today ? "HH:mm" : "dd.MM", CultureInfo.InvariantCulture)
                : "";
            var timeText = new TextBlock { Text = time, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            UseBrush(timeText, TextBlock.ForegroundProperty, "BrushTextSoft");
            row.Children.Add(timeText);

            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
            var itemName = Str(sale, "first_item_name");
            var title = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(itemName) ? Tr.T("Продажа", "Сатуу", "Sale", "Satış", "Sotuv") : itemName,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            UseBrush(title, TextBlock.ForegroundProperty, "BrushText");
            middle.Children.Add(title);
            var who = string.Join(" · ", new[] { Str(sale, "user_display"), Str(sale, "cashbox_name") }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (who.Length > 0)
            {
                var sub = new TextBlock { Text = who, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                UseBrush(sub, TextBlock.ForegroundProperty, "BrushTextSoft");
                middle.Children.Add(sub);
            }
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var pill = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 3), Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            UseBrush(pill, Border.BackgroundProperty, canceled ? "BrushDangerSoft" : "BrushInputAlt");
            var pillContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var dotColor = canceled ? Color.Parse("#EF4444") : PaymentColors.TryGetValue(method, out var pc) ? pc : OtherPaymentColor;
            pillContent.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(dotColor), VerticalAlignment = VerticalAlignment.Center });
            var pillText = new TextBlock
            {
                Text = canceled ? Tr.T("Возврат", "Кайтаруу", "Returned", "İade", "Qaytarilgan") : PaymentLabel(method),
                FontSize = 12,
                FontWeight = FontWeight.Medium,
                VerticalAlignment = VerticalAlignment.Center,
            };
            UseBrush(pillText, TextBlock.ForegroundProperty, canceled ? "BrushDanger" : "BrushText");
            pillContent.Children.Add(pillText);
            pill.Child = pillContent;
            Grid.SetColumn(pill, 2);
            row.Children.Add(pill);

            var amount = new TextBlock
            {
                Text = $"{Amount(Num(sale, "total"))} {Som()}",
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                MinWidth = 110,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextDecorations = canceled ? TextDecorations.Strikethrough : null,
            };
            UseBrush(amount, TextBlock.ForegroundProperty, canceled ? "BrushTextSoft" : "BrushText");
            Grid.SetColumn(amount, 3);
            row.Children.Add(amount);

            line.Child = row;
            RecentList.Children.Add(line);
        }
    }

    private static string PaymentLabel(string method) => (method ?? "").Trim().ToLowerInvariant() switch
    {
        "cash" => Tr.T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
        "transfer" or "card" => Tr.T("Безналичные", "Накталай эмес", "Non-cash", "Nakitsiz", "Naqdsiz"),
        "mbank" => "MBank",
        "mixed" or "split" => Tr.T("Смешанная", "Аралаш", "Mixed", "Karma", "Aralash"),
        "debt" => Tr.T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
        "" => "—",
        var other => other,
    };

    private static string ChecksWord(double count)
    {
        var n = (long)Math.Round(count);
        var ru = (n % 10 == 1 && n % 100 != 11) ? "чек"
            : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? "чека"
            : "чеков";
        return Tr.T($"{n} {ru}", $"{n} чек", n == 1 ? "1 receipt" : $"{n} receipts", $"{n} fiş", $"{n} ta chek");
    }

    private static string Som() => Tr.T("сом", "сом", "som", "som", "so'm");

    /// <summary>Сумма без «,00» у целых: 414 634 и 414 634,07.</summary>
    private static string Amount(double value)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        return Math.Abs(value - Math.Round(value)) < 0.005 ? value.ToString("N0", ru) : value.ToString("N2", ru);
    }

    private static string Qty(double value) =>
        Math.Abs(value - Math.Round(value)) < 0.0005
            ? value.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"))
            : value.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));

    /// <summary>Короткая подпись над столбиком: 415 тыс, 1,2 млн.</summary>
    private static string Compact(double value)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (value >= 1_000_000)
            return (value / 1_000_000).ToString("0.#", ru) + " " + Tr.T("млн", "млн", "M", "Mn", "mln");
        if (value >= 10_000)
            return (value / 1_000).ToString("0", ru) + " " + Tr.T("тыс", "миң", "K", "B", "ming");
        return value.ToString("N0", ru);
    }

    private static double Num(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        };
    }

    private static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Цвет из темы с подпиской: при смене светлой/тёмной темы элементы, собранные в
    /// коде, перекрашиваются сами, как DynamicResource в разметке.</summary>
    private void UseBrush(AvaloniaObject target, AvaloniaProperty property, string resourceKey) =>
        target.Bind(property, this.GetResourceObservable(resourceKey));

    // ------------------------------------------------------------------ шапка окна

    private void DragArea_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        BeginMoveDrag(e);
    }

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty)
        {
            UpdateWindowChrome();
            Dispatcher.UIThread.Post(SyncSectionBounds, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Развёрнутое окно без системной рамки Windows сдвигает за край экрана на ширину
    /// рамки — отступаем на столько же. Значок кнопки: «развернуть» или «восстановить».</summary>
    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootGrid.Margin = maximized ? OffScreenMargin : default;
        MaximizeIconPath.Data = Geometry.Parse(maximized
            ? "M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z"
            : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
    }

    // ------------------------------------------------------------------ кнопки

    private void Period_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string period } || period == _period)
            return;
        _period = period;
        foreach (var b in new[] { TodayButton, WeekButton, MonthButton })
            b.Classes.Set("active", ReferenceEquals(b, sender));
        ApplyTexts();
        _ = RefreshAsync();
    }

    private void Refresh_Click(object? sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void AllSales_Click(object? sender, RoutedEventArgs e)
    {
        if (!TariffGate.IsStartTariff && Authorize(PosPermissions.ViewSales))
            OpenSection("sales", () => App.GetRequiredService<SalesWindow>());
    }

    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var newIsDark = !prefs.DarkTheme;
        prefs.DarkTheme = newIsDark;
        App.ApplyTheme(newIsDark);
        prefs.SaveToDisk();
        UpdateThemeIcon();
    }

    private void UpdateThemeIcon()
    {
        if (this.TryFindResource(UserPreferences.Instance.DarkTheme ? "SunIcon" : "MoonIcon", out var icon) && icon is Geometry geometry)
            ThemeIconPath.Data = geometry;
    }

    /// <summary>Выход из учётной записи — как в кассе (MainWindow.NavigateToLoginAsync): стираем
    /// сессию и сохранённый вход, показываем окно входа.</summary>
    private async void Logout_Click(object? sender, RoutedEventArgs e)
    {
        _loggingOut = true;
        _timer.Stop();
        try
        {
            App.AuthApi.ClearSession();
            await App.GetRequiredService<IAuthSessionManager>().ClearSessionAsync().ConfigureAwait(true);
            OfflineAuthSessionStore.Clear();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: logout cleanup failed: {ex.Message}", "WARNING");
        }

        CloseAllSections();
        var login = App.GetRequiredService<LoginWindow>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = login;
        login.Show();
        Close();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: failed to open {url}: {ex.Message}", "WARNING");
        }
    }
}
