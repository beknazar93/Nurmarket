using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>Раздел "Аккаунт" в Настройках — тариф, срок действия подписки NurCRM и
/// подключённые доп. услуги (GET /api/users/company/, см. CompanyInfoService).</summary>
public partial class AccountView : UserControl
{
    public AccountView()
    {
        InitializeComponent();
        RenderCompany(CompanyInfoService.LastCompany);
        _ = LoadAsync();
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>2026-09-13, по просьбе владельца — перенос текущего онлайн-каталога в
    /// автономный (офлайн) режим на этом же ПК (см. MigrateToOfflineDialog). При успехе диалог
    /// уже подготовил (активировал ключ, создал локальный аккаунт, пометил каталог как
    /// сохраняемый) — здесь только обычный выход из NurCRM-сессии, тем же путём, что и кнопка
    /// "Выйти" ниже, чтобы кассир попал на экран входа и вошёл уже автономно.</summary>
    private async void MigrateOfflineButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new MigrateToOfflineDialog();
        PosDialogHost.Show(dialog, owner);
        if (!dialog.Completed)
            return;

        var settingsWindow = owner;
        settingsWindow?.Close();

        var bridge = NurMarketKassa.AvaloniaHost.App.GetRequiredService<MainWindowHostBridge>();
        if (bridge.Window is { } mainWindow)
            await mainWindow.LogoutAsync().ConfigureAwait(true);
    }

    /// <summary>Раздел "Выйти" был отдельным пунктом главного меню — по просьбе пользователя
    /// перенесён сюда, в Аккаунт, и убран из меню (см. SideMenuView). Сама логика выхода
    /// (закрытие смены, переход на экран входа) остаётся в MainWindow.LogoutAsync — здесь
    /// только вызов через bridge, т.к. этот UserControl не владеет окном MainWindow напрямую.</summary>
    private async void LogoutButton_Click(object? sender, RoutedEventArgs e)
    {
        var settingsWindow = TopLevel.GetTopLevel(this) as Window;
        settingsWindow?.Close();

        var bridge = NurMarketKassa.AvaloniaHost.App.GetRequiredService<MainWindowHostBridge>();
        if (bridge.Window is { } mainWindow)
        {
            await mainWindow.LogoutAsync().ConfigureAwait(true);
            return;
        }

        // 2026-09-29, владелец: «выйти из учётной записи» в программе владельца — теперь только
        // здесь (внизу меню вместо него «Выйти на рабочий стол»). Окна кассы у владельца нет —
        // раньше эта кнопка у него ничего не делала.
#if NURANDROID
        // 2026-10-04, Android-касса: окна там — слои, время жизни своё (NurMarketKassa.AndroidDesktopLifetime).
        if (Avalonia.Application.Current?.ApplicationLifetime is NurMarketKassa.IClassicDesktopStyleApplicationLifetime desktop
#else
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
#endif
            && desktop.MainWindow is OwnerShellWindow ownerShell)
            await ownerShell.SignOutAsync().ConfigureAwait(true);
    }

    /// <summary>Карточка «Вход в кассу» — из того, что касса уже знает: ответ сервера на вход
    /// (логин, имя, роль) и текущая сессия (касса, смена). Сеть не нужна.</summary>
    private void RenderLogin()
    {
        var session = App.GetRequiredService<NurMarketKassa.Ui.Shared.IAppSession>();
        var user = App.GetRequiredService<NurMarketApiClient>().UserPayload;

        string? Read(string name) =>
            user.ValueKind == System.Text.Json.JsonValueKind.Object
            && user.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim()
                : null;

        var lastLogin = UserPreferences.Instance.LastLoginEmail;
        LoginText.Text = Read("email") ?? (string.IsNullOrWhiteSpace(lastLogin) ? "—" : lastLogin);
        CashierNameText.Text = string.IsNullOrWhiteSpace(session.CurrentUserDisplayName) ? "—" : session.CurrentUserDisplayName;
        RoleText.Text = Read("role_display") ?? Read("role") ?? "—";
        CashboxText.Text = string.IsNullOrWhiteSpace(session.PosCashboxDisplayName) ? "—" : session.PosCashboxDisplayName;
        ShiftText.Text = string.IsNullOrWhiteSpace(session.ActiveShiftId)
            ? Tr.T("не открыта", "ачылган эмес", "not open", "açık değil", "ochilmagan")
            : Tr.T("открыта", "ачык", "open", "açık", "ochiq") + " · №" + session.ActiveShiftId![..Math.Min(8, session.ActiveShiftId.Length)].ToUpperInvariant();
    }

    private async Task LoadAsync()
    {
        try
        {
            RenderLogin();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"AccountView login card failed: {ex.Message}", "WARNING");
        }

        RefreshButton.IsEnabled = false;
        LoadingOrErrorText.IsVisible = true;
        LoadingOrErrorText.Text = Tr.T("Загрузка...", "Жүктөлүүдө...", "Loading...", "Yükleniyor...", "Yuklanmoqda...");
        try
        {
            var status = await CompanyInfoService.RefreshAsync(App.AuthApi).ConfigureAwait(true);
            RenderCompany(CompanyInfoService.LastCompany);
            LoadingOrErrorText.IsVisible = status is null && CompanyInfoService.LastCompany is null;
            if (LoadingOrErrorText.IsVisible)
                LoadingOrErrorText.Text = Tr.T(
                    "Не удалось загрузить данные аккаунта — нет связи с сервером.",
                    "Аккаунт маалыматын жүктөө мүмкүн болгон жок — сервер менен байланыш жок.", "Couldn't load account data — no connection to the server.", "Hesap verileri yüklenemedi — sunucuyla bağlantı yok.", "Hisob ma'lumotlarini yuklab bo'lmadi — server bilan aloqa yo'q.");
        }
        catch (Exception ex)
        {
            LoadingOrErrorText.IsVisible = true;
            LoadingOrErrorText.Text = Tr.T("Ошибка: ", "Ката: ", "Error: ", "Hata: ", "Xato: ") + ex.Message;
            PosLogger.Log($"AccountView load failed: {ex}", "WARNING");
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void RenderCompany(CompanyDto? company)
    {
        if (company is null)
        {
            CompanyNameText.Text = Tr.T("—", "—", "—", "—", "—");
            CompanyInnText.Text = "";
            PlanNameText.Text = Tr.T("—", "—", "—", "—", "—");
            PlanPriceText.Text = "";
            ExpiryDatesText.Text = "";
            ExpiryStatusBadge.IsVisible = false;
            ServicesList.Items.Clear();
            return;
        }

        CompanyNameText.Text = string.IsNullOrWhiteSpace(company.Name) ? Tr.T("Без названия", "Аталышы жок", "Untitled", "Adsız", "Nomsiz") : company.Name;
        CompanyInnText.Text = string.IsNullOrWhiteSpace(company.Inn) ? "" : $"{Tr.T("ИНН", "ИНН", "TIN", "VKN", "STIR")}: {company.Inn}";

        PlanNameText.Text = string.IsNullOrWhiteSpace(company.SubscriptionPlanName)
            ? Tr.T("Не определён", "Аныкталган эмес", "Not defined", "Tanımlanmamış", "Aniqlanmagan")
            : company.SubscriptionPlanName;
        PlanPriceText.Text = string.IsNullOrWhiteSpace(company.SubscriptionPlanPrice)
            ? ""
            : $"{company.SubscriptionPlanPrice} {Tr.T("сом / мес.", "сом / ай", "som / mo.", "som / ay", "so'm / oy")}";

        RenderExpiry(company.StartDate, company.EndDate);
        RenderServices(company);
        RenderPlanCompare(company.SubscriptionPlanName);
    }

    /// <summary>2026-10-05, владелец: «добавь в тарифе Старт описание Стандарта и сравнение в аккаунте». Строки — те же
    /// ограничения, что в программе (TariffGate, меню кассы и программы владельца).</summary>
    private void RenderPlanCompare(string? planName)
    {
        var isStart = string.Equals(planName?.Trim(), TariffGate.StartPlanName, StringComparison.OrdinalIgnoreCase);
        PlanCompareCard.IsVisible = isStart;
        PlanCompareHost.Children.Clear();
        if (!isStart)
            return;

        var text = ThemeBrush("BrushText", Brushes.Black);
        var soft = ThemeBrush("BrushTextSoft", Brushes.Gray);
        var ok = ThemeBrush("BrushSuccess", Brushes.Green);
        PlanCompareHost.Children.Add(new TextBlock
        {
            Text = Tr.T("Тариф «Стандарт» — всё для управления магазином", "«Стандарт» тарифи — дүкөндү башкаруу үчүн баары",
                "The “Standard” plan — everything to run the shop", "«Standart» tarifesi — mağazayı yönetmek için her şey", "«Standart» tarifi — do'konni boshqarish uchun hammasi"),
            FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = text, TextWrapping = TextWrapping.Wrap,
        });
        PlanCompareHost.Children.Add(new TextBlock
        {
            Text = Tr.T("Сейчас у вас «Старт»: касса — продажи, чеки, возвраты, склад, печать. «Стандарт» добавляет клиентов, аналитику, финансы, "
                        + "зарплату, ИИ-советника и Telegram-бота — чтобы видеть, где магазин зарабатывает и где теряет.",
                "Азыр сизде «Старт»: касса — сатуу, чектер, кайтаруу, кампа, басып чыгаруу. «Стандарт» кардарларды, аналитиканы, каржыны, "
                + "эмгек акыны, ИИ-кеңешчини жана Telegram-ботту кошот — дүкөн кайда тапканын жана кайда жоготконун көрүү үчүн.",
                "You are on “Start”: the till — sales, receipts, returns, warehouse, printing. “Standard” adds customers, analytics, finance, "
                + "salaries, the AI advisor and the Telegram bot — to see where the shop earns and where it loses.",
                "Şu an «Start» tarifesindesiniz: kasa — satış, fişler, iadeler, depo, yazdırma. «Standart» müşterileri, analizleri, finansı, "
                + "maaşları, yapay zekâ danışmanını ve Telegram botunu ekler — mağazanın nerede kazandığını ve nerede kaybettiğini görmek için.",
                "Hozir sizda «Start»: kassa — sotuv, cheklar, qaytarish, ombor, chop etish. «Standart» mijozlar, analitika, moliya, "
                + "ish haqi, SI maslahatchi va Telegram-botni qo'shadi — do'kon qayerda topayotganini va qayerda yo'qotayotganini ko'rish uchun."),
            FontSize = 13, Foreground = soft, TextWrapping = TextWrapping.Wrap,
        });

        var paid = Tr.T($"+{TariffGate.PackMonthlyFee} сом/мес", $"+{TariffGate.PackMonthlyFee} сом/ай", $"+{TariffGate.PackMonthlyFee} som/mo",
            $"+{TariffGate.PackMonthlyFee} som/ay", $"+{TariffGate.PackMonthlyFee} so'm/oy");
        var rows = new (string Feature, string Start, string Standard)[]
        {
            (Tr.T("Продажи, чеки, возвраты, печать", "Сатуу, чектер, кайтаруу, басып чыгаруу", "Sales, receipts, returns, printing", "Satış, fişler, iadeler, yazdırma", "Sotuv, cheklar, qaytarish, chop etish"), "✓", "✓"),
            (Tr.T("Склад, товары, калькуляция", "Кампа, товарлар, калькуляция", "Warehouse, products, pricing", "Depo, ürünler, hesaplama", "Ombor, mahsulotlar, kalkulyatsiya"), "✓", "✓"),
            (Tr.T("Весы на кассе, дисплей покупателя", "Кассадагы тараза, сатып алуучунун дисплейи", "Scales at the till, customer display", "Kasadaki terazi, müşteri ekranı", "Kassadagi tarozi, xaridor displeyi"), "✓", "✓"),
            (Tr.T("Отложенные чеки, оплата долгов", "Кийинкиге калтырылган чектер, карыз төлөө", "Parked receipts, debt payments", "Bekletilen fişler, borç ödemeleri", "Kechiktirilgan cheklar, qarz to'lovi"), paid, "✓"),
            (Tr.T("Клиенты, воронка продаж, WhatsApp", "Кардарлар, сатуу воронкасы, WhatsApp", "Customers, sales funnel, WhatsApp", "Müşteriler, satış hunisi, WhatsApp", "Mijozlar, savdo voronkasi, WhatsApp"), paid, "✓"),
            (Tr.T("Продажи, финансы, аналитика, ABC, прибыль, продажи в убыток, план продаж", "Сатуулар, каржы, аналитика, ABC, пайда, зыян менен сатуулар, сатуу планы",
                "Sales, finance, analytics, ABC, profit, sales at a loss, sales plan", "Satışlar, finans, analiz, ABC, kâr, zararına satışlar, satış planı",
                "Sotuvlar, moliya, analitika, ABC, foyda, zarariga sotuvlar, sotuv rejasi"), paid, "✓"),
            (Tr.T("Пополнение и сроки годности", "Толуктоо жана жарактуулук мөөнөттөрү", "Restock and expiry dates", "Stok yenileme ve son kullanma tarihleri", "To'ldirish va yaroqlilik muddatlari"), paid, "✓"),
            (Tr.T("Зарплата сотрудников", "Кызматкерлердин эмгек акысы", "Staff salaries", "Personel maaşları", "Xodimlar ish haqi"), paid, "✓"),
            (Tr.T("ИИ-советник, ИИ в боте и голосовом управлении", "ИИ-кеңешчи, боттогу жана үн менен башкаруудагы ИИ", "AI advisor, AI in the bot and voice control",
                "Yapay zekâ danışmanı, bot ve sesli kontroldeki yapay zekâ", "SI maslahatchi, botdagi va ovozli boshqaruvdagi SI"), paid, "✓"),
            (Tr.T("Telegram-бот владельца", "Ээсинин Telegram-боту", "Owner's Telegram bot", "İşletme sahibinin Telegram botu", "Egasining Telegram-boti"), paid, "✓"),
            (Tr.T("Расширенные итоги смены, выгрузка в Excel и Word", "Сменанын кеңейтилген жыйынтыктары, Excel жана Word'ко чыгаруу",
                "Extended shift totals, export to Excel and Word", "Genişletilmiş vardiya sonuçları, Excel ve Word'e aktarma", "Smenaning kengaytirilgan yakunlari, Excel va Word'ga eksport"), paid, "✓"),
            (Tr.T("Отправка товаров на весы по сети", "Товарларды таразага тармак аркылуу жөнөтүү", "Sending products to scales over the network",
                "Ürünleri tartılara ağ üzerinden gönderme", "Mahsulotlarni taroziga tarmoq orqali yuborish"), paid, "✓"),
            (Tr.T("NurCRM в программе, база знаний, тех. поддержка", "Программадагы NurCRM, билим базасы, тех колдоо", "NurCRM in the app, knowledge base, support",
                "Programda NurCRM, bilgi bankası, destek", "Dasturdagi NurCRM, bilimlar bazasi, texnik yordam"), paid, "✓"),
            (Tr.T("Сотрудники", "Кызматкерлер", "Employees", "Personel", "Xodimlar"),
                Tr.T("до 3", "3кө чейин", "up to 3", "en fazla 3", "3 tagacha"), Tr.T("больше", "көбүрөөк", "more", "daha fazla", "ko'proq")),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Avalonia.Thickness(0, 4, 0, 0) };
        void Cell(string value, int row, int col, bool head = false, IBrush? brush = null)
        {
            var cell = new TextBlock
            {
                Text = value, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                FontWeight = head ? FontWeight.SemiBold : FontWeight.Normal,
                Foreground = brush ?? (head ? text : soft),
                HorizontalAlignment = col == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
                MinWidth = col == 0 ? 0 : 110,
                TextAlignment = col == 0 ? TextAlignment.Left : TextAlignment.Center,
                Margin = new Avalonia.Thickness(col == 0 ? 0 : 8, 3, 0, 3),
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            grid.Children.Add(cell);
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(Tr.T("Возможность", "Мүмкүнчүлүк", "Feature", "Özellik", "Imkoniyat"), 0, 0, head: true);
        Cell(Tr.T("Старт", "Старт", "Start", "Start", "Start"), 0, 1, head: true);
        Cell(Tr.T("Стандарт", "Стандарт", "Standard", "Standart", "Standart"), 0, 2, head: true);
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell(rows[i].Feature, i + 1, 0, brush: text);
            Cell(rows[i].Start, i + 1, 1);
            Cell(rows[i].Standard, i + 1, 2, brush: rows[i].Standard == "✓" ? ok : text);
        }
        PlanCompareHost.Children.Add(grid);

        PlanCompareHost.Children.Add(new TextBlock
        {
            Text = Tr.T($"На «Старте» любую функцию «Стандарта» можно подключить отдельно в Маркетплейс → Доп. функции: активация {TariffGate.PackActivationFee} сом "
                        + $"и абонплата {TariffGate.PackMonthlyFee} сом в месяц за каждую. Перейти на «Стандарт» целиком можно через NurCRM.",
                $"«Старт» тарифинде «Стандарттын» каалаган функциясын Маркетплейс → Кошумча функциялар бөлүмүндө өзүнчө туташтырса болот: активдештирүү {TariffGate.PackActivationFee} сом "
                + $"жана ар бири үчүн айына {TariffGate.PackMonthlyFee} сом абонтөлөм. «Стандартка» толугу менен NurCRM аркылуу өтсө болот.",
                $"On “Start”, any “Standard” feature can be connected separately in Marketplace → Extras: activation {TariffGate.PackActivationFee} som "
                + $"and {TariffGate.PackMonthlyFee} som a month for each. You can switch to “Standard” entirely through NurCRM.",
                $"«Start» tarifesinde herhangi bir «Standart» özelliği Marketplace → Ek özellikler bölümünden ayrıca bağlanabilir: etkinleştirme {TariffGate.PackActivationFee} som "
                + $"ve her biri için ayda {TariffGate.PackMonthlyFee} som. «Standart»a tamamen NurCRM üzerinden geçebilirsiniz.",
                $"«Start» tarifida «Standart»ning istalgan funksiyasini Marketpleys → Qo'shimcha funksiyalar bo'limida alohida ulash mumkin: faollashtirish {TariffGate.PackActivationFee} so'm "
                + $"va har biri uchun oyiga {TariffGate.PackMonthlyFee} so'm. «Standart»ga to'liq NurCRM orqali o'tish mumkin."),
            FontSize = 12, Foreground = soft, TextWrapping = TextWrapping.Wrap,
        });
    }

    private void RenderExpiry(string? startDateRaw, string? endDateRaw)
    {
        var start = TryParseDate(startDateRaw);
        var end = TryParseDate(endDateRaw);

        ExpiryDatesText.Text = start is null && end is null
            ? Tr.T("Даты не указаны.", "Даталар көрсөтүлгөн эмес.", "No dates specified.", "Tarihler belirtilmedi.", "Sanalar ko'rsatilmagan.")
            : Tr.T(
                $"С {FormatDate(start)} по {FormatDate(end)}",
                $"{FormatDate(start)} — {FormatDate(end)}", $"From {FormatDate(start)} to {FormatDate(end)}", $"{FormatDate(start)} – {FormatDate(end)}", $"{FormatDate(start)} — {FormatDate(end)}");

        if (end is null)
        {
            ExpiryStatusBadge.IsVisible = false;
            return;
        }

        var remaining = end.Value - DateTimeOffset.Now;
        string statusText;
        string bgKey, borderKey, fgKey;
        if (remaining <= TimeSpan.Zero)
        {
            statusText = Tr.T("Подписка истекла", "Жазылуу мөөнөтү бүттү", "Subscription expired", "Abonelik sona erdi", "Obuna muddati tugadi");
            (bgKey, borderKey, fgKey) = ("BrushDangerSoft", "BrushDanger", "BrushDanger");
        }
        else if (remaining.TotalDays <= 3)
        {
            var days = (int)Math.Ceiling(remaining.TotalDays);
            statusText = Tr.T($"Истекает через {days} дн.", $"{days} күндөн кийин бүтөт",
                $"Expires in {days} days", $"{days} gün içinde sona erer", $"{days} kundan keyin tugaydi");
            (bgKey, borderKey, fgKey) = ("BrushWarningSoft", "BrushWarning", "BrushWarning");
        }
        else
        {
            statusText = Tr.T("Активна", "Активдүү", "Active", "Aktif", "Faol");
            (bgKey, borderKey, fgKey) = ("BrushSuccessSoft", "BrushSuccess", "BrushSuccess");
        }

        ExpiryStatusBadge.IsVisible = true;
        ExpiryStatusBadge.Background = ThemeBrush(bgKey, Brushes.LightGray);
        ExpiryStatusBadge.BorderBrush = ThemeBrush(borderKey, Brushes.Gray);
        ExpiryStatusText.Text = statusText;
        ExpiryStatusText.Foreground = ThemeBrush(fgKey, Brushes.Black);
    }

    private void RenderServices(CompanyDto company)
    {
        ServicesList.Items.Clear();
        var services = new (string LabelRu, string LabelKy, bool Enabled)[]
        {
            (Tr.T("Документы", "Документтер", "Documents", "Belgeler", "Hujjatlar"), "", company.CanViewDocuments),
            ("WhatsApp", "", company.CanViewWhatsapp),
            ("Instagram", "", company.CanViewInstagram),
            ("Telegram", "", company.CanViewTelegram),
            (Tr.T("Витрина (showcase)", "Витрина (showcase)", "Showcase", "Vitrin (showcase)", "Vitrina (showcase)"), "", company.CanViewShowcase),
        };

        foreach (var (label, _, enabled) in services)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = enabled ? "✅" : "⬜",
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                Opacity = enabled ? 1.0 : 0.55,
                VerticalAlignment = VerticalAlignment.Center,
            });
            ServicesList.Items.Add(row);
        }
    }

    private static DateTimeOffset? TryParseDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;

    private static string FormatDate(DateTimeOffset? dt) =>
        dt?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "—";

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Avalonia.Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is IBrush brush
            ? brush
            : fallback;
}
