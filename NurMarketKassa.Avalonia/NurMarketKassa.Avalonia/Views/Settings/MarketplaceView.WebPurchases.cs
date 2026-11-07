using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

// 2026-11-06 (просьба владельца: «Маркетплейс должен открываться с нашего сайта, и оттуда при покупке в программу скачиваются дополнительные функции и темы»).
// Эта часть окна «Маркетплейс»:
//  • «Открыть Маркетплейс на сайте» — страница Маркетплейса веб-админки в браузере (адрес — UserPreferences.MarketplaceWebUrl; токена в адресе нет, вход на сайте делает сам клиент);
//  • «Обновить покупки» и автоматическая проверка, когда окно снова становится активным после возвращения из браузера: немедленный запрос ServerAddons (не чаще раза в 20 секунд),
//    анимированная строка «Проверяем покупки…», итог «Подключено новых функций: N» / «Новых покупок не найдено»;
//  • темы и виды кассы с сайта (MarketplaceServerThemes): «Купить на сайте» / «Установить» / «Применить» / «Удалить»; купленные темы ставятся сами (настройка «Устанавливать купленные темы автоматически»).
// Функции открываются без перезапуска: ServerAddons.Changed → витрина перечитывается, TariffGate.PacksChanged → меню владельца пересобирается.
public partial class MarketplaceView
{
    private TextBlock _purchaseStatus = null!;
    private ProgressBar _purchaseProgress = null!;
    private Button _openSiteButton = null!;
    private Button _refreshPurchasesButton = null!;
    private CheckBox _autoInstallCheck = null!;
    private DispatcherTimer? _dotsTimer;
    private DispatcherTimer? _statusClearTimer;
    private int _dots;
    private bool _purchaseBusy;
    private bool _themeBusy;
    private bool _openedSite;
    private DateTime _openedSiteAtUtc = DateTime.MinValue;
    private Window? _hookedWindow;

    /// <summary>После ухода на сайт возвращение в программу проверяет покупки в течение этого срока.</summary>
    private static readonly TimeSpan SiteReturnWindow = TimeSpan.FromHours(2);

    private void InitWebPurchases()
    {
        BuildWebBar();
        AttachedToVisualTree += (_, _) =>
        {
            HookWindowActivation();
            MarketplaceThemesClient.Shared.Changed += OnServerThemesChanged;
            ServerThemePreview.Loaded += OnServerThemesChanged;
            CustomThemeStore.Changed += OnServerThemesChanged;
            _ = QuietThemesRefreshAsync();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            UnhookWindowActivation();
            MarketplaceThemesClient.Shared.Changed -= OnServerThemesChanged;
            ServerThemePreview.Loaded -= OnServerThemesChanged;
            CustomThemeStore.Changed -= OnServerThemesChanged;
            StopCheckingAnimation();
            _statusClearTimer?.Stop();
        };
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private void OnServerThemesChanged() => Dispatcher.UIThread.Post(() =>
    {
        _autoInstallCheck.IsVisible = MarketplaceThemesClient.Shared.IsAvailable; // переключатель нужен, только когда сервер отдаёт темы
        ScheduleStoreReload();
    });

    // ------------------------------------------------------------------ панель

    private void BuildWebBar()
    {
        _openSiteButton = new Button
        {
            Content = T("Открыть Маркетплейс на сайте", "Маркетплейсти сайттан ачуу", "Open the Marketplace on the website", "Marketplace'i web sitesinde aç", "Marketpleysni saytda ochish"),
            Padding = new Thickness(16, 7), FontSize = 13, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        _openSiteButton.Classes.Add("btn-primary");
        _openSiteButton.Click += (_, _) => OpenMarketplaceSite(null);

        _refreshPurchasesButton = new Button
        {
            Content = T("Обновить покупки", "Сатып алууларды жаңыртуу", "Refresh purchases", "Satın alımları yenile", "Xaridlarni yangilash"),
            Padding = new Thickness(16, 7), FontSize = 13, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        _refreshPurchasesButton.Classes.Add("btn-secondary");
        _refreshPurchasesButton.Click += (_, _) => _ = RefreshPurchasesAsync(auto: false);

        _purchaseStatus = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 560 };
        _purchaseStatus.Bind(TextBlock.ForegroundProperty, _purchaseStatus.GetResourceObservable("BrushTextMuted"));
        _purchaseProgress = new ProgressBar { IsIndeterminate = true, Height = 4, Width = 120, IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };

        // Устанавливать купленные темы автоматически (по умолчанию да); показывается, когда сервер отдаёт темы (признак marketplace_themes).
        _autoInstallCheck = new CheckBox
        {
            Content = T("Устанавливать купленные темы автоматически", "Сатып алынган темаларды автоматтык орнотуу", "Install purchased themes automatically", "Satın alınan temaları otomatik yükle", "Sotib olingan mavzularni avtomatik o'rnatish"),
            IsChecked = UserPreferences.Instance.AutoInstallPurchasedThemes, IsVisible = MarketplaceThemesClient.Shared.IsAvailable,
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
        };
        _autoInstallCheck.IsCheckedChanged += (_, _) =>
        {
            var on = _autoInstallCheck.IsChecked == true;
            if (UserPreferences.Instance.AutoInstallPurchasedThemes == on)
                return;
            UserPreferences.Instance.AutoInstallPurchasedThemes = on;
            UserPreferences.Instance.SaveToDisk();
        };

        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_openSiteButton);
        row.Children.Add(_refreshPurchasesButton);
        row.Children.Add(_autoInstallCheck);
        row.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { _purchaseProgress, _purchaseStatus } });
        WebBarHost.Child = row;
    }

    // ------------------------------------------------------------------ открыть сайт

    /// <summary>Открыть Маркетплейс на сайте (общая страница; с <paramref name="item"/> — карточка функции или темы: kassa-loyalty, theme-sunny).</summary>
    private void OpenMarketplaceSite(string? item)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var url = MarketplaceWebLink.ForSettings(item);
        if (url is null)
        {
            PosAlertDialog.Show(owner, T("Маркетплейс на сайте", "Сайттагы Маркетплейс", "Marketplace on the website", "Sitedeki Marketplace", "Saytdagi Marketpleys"),
                T("Адрес Маркетплейса на сайте указан неверно (нужен адрес https://…). Исправьте его в настройках или обратитесь в поддержку.", "Сайттагы Маркетплейстин дареги туура эмес (https://… керек). Жөндөөлөрдөн оңдоңуз же колдоого кайрылыңыз.",
                    "The Marketplace website address is invalid (an https://… address is required). Fix it in the settings or contact support.", "Web sitesindeki Marketplace adresi geçersiz (https://… gerekir). Ayarlardan düzeltin veya destekle iletişime geçin.", "Saytdagi Marketpleys manzili noto'g'ri (https://… kerak). Sozlamalarda tuzating yoki yordam xizmatiga murojaat qiling."),
                PosAlertKind.Warning);
            return;
        }
        OpenSiteUrl(owner, url);
    }

    private void OpenSiteUrl(Window? owner, string url)
    {
        _openedSite = true;
        _openedSiteAtUtc = DateTime.UtcNow;
        if (!PlatformLauncher.OpenUrl(url))
        {
            PosAlertDialog.Show(owner, T("Маркетплейс на сайте", "Сайттагы Маркетплейс", "Marketplace on the website", "Sitedeki Marketplace", "Saytdagi Marketpleys"),
                T("Не удалось открыть браузер. Откройте адрес вручную: ", "Браузерди ачуу мүмкүн болгон жок. Дарегин кол менен ачыңыз: ", "Could not open the browser. Open the address manually: ", "Tarayıcı açılamadı. Adresi elle açın: ", "Brauzerni ochib bo'lmadi. Manzilni qo'lda oching: ") + url,
                PosAlertKind.Warning);
            return;
        }
        ShowPurchaseStatus(T("Сайт открыт. Оплатите там и вернитесь сюда — покупка подхватится сама (или нажмите «Обновить покупки»).",
            "Сайт ачылды. Ал жерде төлөп, бул жерге кайтыңыз — сатып алуу өзү табылат (же «Сатып алууларды жаңыртуу» баскычын басыңыз).",
            "The website is open. Pay there and come back — the purchase is picked up automatically (or press “Refresh purchases”).",
            "Site açıldı. Orada ödeyin ve buraya dönün — satın alma kendiliğinden alınır (veya «Satın alımları yenile»ye basın).",
            "Sayt ochildi. U yerda to'lang va bu yerga qayting — xarid o'zi topiladi (yoki «Xaridlarni yangilash»ni bosing)."), clearAfter: TimeSpan.FromSeconds(40));
    }

    // ------------------------------------------------------------------ возвращение из браузера

    private void HookWindowActivation()
    {
        if (TopLevel.GetTopLevel(this) is not Window window || ReferenceEquals(window, _hookedWindow))
            return;
        UnhookWindowActivation();
        window.Activated += OnWindowActivated;
        _hookedWindow = window;
    }

    private void UnhookWindowActivation()
    {
        if (_hookedWindow is { } window)
            window.Activated -= OnWindowActivated;
        _hookedWindow = null;
    }

    /// <summary>Окно снова активно: если мы уводили пользователя на сайт — сразу проверяем покупки (не ждём 15 минут).</summary>
    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (!_openedSite || _purchaseBusy || DateTime.UtcNow - _openedSiteAtUtc > SiteReturnWindow)
            return;
        _ = RefreshPurchasesAsync(auto: true);
    }

    // ------------------------------------------------------------------ проверка покупок

    private async Task RefreshPurchasesAsync(bool auto)
    {
        if (_purchaseBusy)
            return;
        _purchaseBusy = true;
        _refreshPurchasesButton.IsEnabled = false;
        StartCheckingAnimation();
        var started = DateTime.UtcNow;
        var message = "";
        var worked = false; // получили свежие данные хотя бы от одного источника
        try
        {
            var addons = await ServerAddons.RefreshDetailedAsync(force: true).ConfigureAwait(true);
            var themes = await CheckThemesAsync().ConfigureAwait(true);
            var parts = new List<string>();

            var featureNames = addons.NewSlugs.Select(s => Store.FindItem(s)?.Title ?? s).ToList();
            if (featureNames.Count > 0)
                parts.Add(T($"Подключено новых функций: {featureNames.Count} ({string.Join(", ", featureNames)}).", $"Жаңы функциялар туташтырылды: {featureNames.Count} ({string.Join(", ", featureNames)}).",
                    $"New features connected: {featureNames.Count} ({string.Join(", ", featureNames)}).", $"Bağlanan yeni özellik: {featureNames.Count} ({string.Join(", ", featureNames)}).", $"Yangi funksiyalar ulandi: {featureNames.Count} ({string.Join(", ", featureNames)})."));
            if (themes.NewTitles.Count > 0)
                parts.Add(T($"Куплено новых тем: {themes.NewTitles.Count} ({string.Join(", ", themes.NewTitles)}).", $"Жаңы темалар сатып алынды: {themes.NewTitles.Count} ({string.Join(", ", themes.NewTitles)}).",
                    $"New themes purchased: {themes.NewTitles.Count} ({string.Join(", ", themes.NewTitles)}).", $"Satın alınan yeni tema: {themes.NewTitles.Count} ({string.Join(", ", themes.NewTitles)}).", $"Yangi mavzular sotib olindi: {themes.NewTitles.Count} ({string.Join(", ", themes.NewTitles)})."));
            if (themes.Installed > 0)
                parts.Add(T($"Установлено тем: {themes.Installed}.", $"Орнотулган темалар: {themes.Installed}.", $"Themes installed: {themes.Installed}.", $"Yüklenen tema: {themes.Installed}.", $"O'rnatilgan mavzular: {themes.Installed}."));
            parts.AddRange(themes.Problems);

            worked = addons.Status == ServerAddons.PurchaseCheckStatus.Checked || themes.Catalog?.Status == MarketplaceCatalogStatus.Loaded;
            var throttled = addons.Status == ServerAddons.PurchaseCheckStatus.Throttled && themes.Catalog?.Status is null or MarketplaceCatalogStatus.Throttled or MarketplaceCatalogStatus.NotSupported;
            if (parts.Count == 0)
            {
                if (worked)
                    parts.Add(T("Новых покупок не найдено.", "Жаңы сатып алуулар табылган жок.", "No new purchases found.", "Yeni satın alma bulunamadı.", "Yangi xaridlar topilmadi."));
                else if (throttled)
                    parts.Add(T("Покупки только что проверены — повторить можно через несколько секунд.", "Сатып алуулар жаңы эле текшерилди — бир нече секундадан кийин кайталаса болот.",
                        "Purchases were just checked — you can retry in a few seconds.", "Satın alımlar az önce kontrol edildi — birkaç saniye sonra tekrar deneyebilirsiniz.", "Xaridlar hozirgina tekshirildi — bir necha soniyadan keyin qayta urinib ko'rish mumkin."));
                else
                    parts.Add(T("Не удалось проверить покупки: нет связи с сервером NurCRM. Работаем по сохранённым данным.", "Сатып алууларды текшерүү мүмкүн болгон жок: NurCRM серверине байланыш жок. Сакталган маалымат боюнча иштейбиз.",
                        "Could not check purchases: no connection to the NurCRM server. Working from saved data.", "Satın alımlar kontrol edilemedi: NurCRM sunucusuna bağlantı yok. Kayıtlı verilerle çalışıyoruz.", "Xaridlarni tekshirib bo'lmadi: NurCRM serveriga ulanish yo'q. Saqlangan ma'lumotlar bo'yicha ishlaymiz."));
            }
            message = string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: проверка покупок не удалась: {ex.Message}", "WARNING");
            message = T("Не удалось проверить покупки. Попробуйте позже.", "Сатып алууларды текшерүү мүмкүн болгон жок. Кийинчерээк аракет кылыңыз.", "Could not check purchases. Try later.", "Satın alımlar kontrol edilemedi. Daha sonra deneyin.", "Xaridlarni tekshirib bo'lmadi. Keyinroq urinib ko'ring.");
        }
        finally
        {
            // Строка «Проверяем покупки…» видна не короче 0,7 с — иначе при быстром ответе анимация мелькает и непонятно, что проверка была.
            var left = TimeSpan.FromMilliseconds(700) - (DateTime.UtcNow - started);
            if (left > TimeSpan.Zero)
                await Task.Delay(left).ConfigureAwait(true);
            StopCheckingAnimation();
            _purchaseBusy = false;
            _refreshPurchasesButton.IsEnabled = true;
        }

        if (worked && _openedSite)
            _openedSite = false; // вернулись и проверили — до следующего похода на сайт автопроверка не нужна
        ShowPurchaseStatus(message, clearAfter: TimeSpan.FromSeconds(auto ? 25 : 20));
        ScheduleStoreReload();
    }

    private sealed record ThemesCheck(MarketplaceCatalogResult? Catalog, List<string> NewTitles, int Installed, List<string> Problems);

    /// <summary>Темы с сайта: свежий каталог, названия новых покупок, автоустановка купленного. Без признака marketplace_themes — пусто, без ошибок.</summary>
    private async Task<ThemesCheck> CheckThemesAsync()
    {
        var client = MarketplaceThemesClient.Shared;
        var titles = new List<string>();
        var problems = new List<string>();
        var installed = 0;
        MarketplaceCatalogResult? catalog = null;
        try
        {
            catalog = await client.RefreshAsync(force: true).ConfigureAwait(true);
            if (catalog.Status == MarketplaceCatalogStatus.NotSupported)
                return new ThemesCheck(catalog, titles, 0, problems);
            foreach (var code in catalog.NewlyEntitled ?? [])
                titles.Add(client.Find(code)?.Name.Get() is { Length: > 0 } n ? n : code);
            if (UserPreferences.Instance.AutoInstallPurchasedThemes && client.Entries.Count > 0)
            {
                var done = await MarketplaceServerThemes.Installer.AutoInstallEntitledAsync().ConfigureAwait(true);
                foreach (var (entry, result) in done)
                {
                    if (result.Ok)
                        installed++;
                    else
                        problems.Add($"«{(entry.Name.Get() is { Length: > 0 } t ? t : entry.Code)}»: {ThemeInstallProblem(result.Status)}");
                }
                if (installed > 0)
                    RebuildThemedCards();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: темы с сайта не проверены: {ex.Message}", "WARNING");
        }
        return new ThemesCheck(catalog, titles, installed, problems);
    }

    /// <summary>Тихая проверка при открытии окна (без сообщений): свежий каталог и автоустановка купленного.</summary>
    private async Task QuietThemesRefreshAsync()
    {
        try
        {
            var client = MarketplaceThemesClient.Shared;
            var catalog = await client.RefreshAsync(force: false).ConfigureAwait(true);
            if (catalog.Status == MarketplaceCatalogStatus.NotSupported || client.Entries.Count == 0 || !UserPreferences.Instance.AutoInstallPurchasedThemes)
                return;
            var done = await MarketplaceServerThemes.Installer.AutoInstallEntitledAsync().ConfigureAwait(true);
            var count = done.Count(d => d.Result.Ok);
            if (count > 0)
            {
                RebuildThemedCards();
                ShowPurchaseStatus(T($"Установлено купленных тем: {count}.", $"Сатып алынган темалар орнотулду: {count}.", $"Purchased themes installed: {count}.", $"Satın alınan temalar yüklendi: {count}.", $"Sotib olingan mavzular o'rnatildi: {count}."), clearAfter: TimeSpan.FromSeconds(20));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: темы с сайта при открытии не проверены: {ex.Message}", "WARNING");
        }
    }

    // ------------------------------------------------------------------ строка состояния

    private void StartCheckingAnimation()
    {
        _statusClearTimer?.Stop();
        _dots = 0;
        _purchaseProgress.IsVisible = true;
        SetCheckingText();
        _dotsTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _dotsTimer.Tick -= OnDotsTick;
        _dotsTimer.Tick += OnDotsTick;
        _dotsTimer.Start();
    }

    private void OnDotsTick(object? sender, EventArgs e)
    {
        _dots = (_dots + 1) % 4;
        SetCheckingText();
    }

    private void SetCheckingText() =>
        _purchaseStatus.Text = T("Проверяем покупки", "Сатып алууларды текшерүүдөбүз", "Checking purchases", "Satın alımlar kontrol ediliyor", "Xaridlar tekshirilmoqda") + new string('.', _dots);

    private void StopCheckingAnimation()
    {
        _dotsTimer?.Stop();
        _purchaseProgress.IsVisible = false;
    }

    private void ShowPurchaseStatus(string text, TimeSpan clearAfter)
    {
        _purchaseStatus.Text = text;
        _statusClearTimer ??= new DispatcherTimer();
        _statusClearTimer.Stop();
        _statusClearTimer.Interval = clearAfter;
        _statusClearTimer.Tick -= OnStatusClear;
        _statusClearTimer.Tick += OnStatusClear;
        _statusClearTimer.Start();
    }

    private void OnStatusClear(object? sender, EventArgs e)
    {
        _statusClearTimer?.Stop();
        if (!_purchaseBusy)
            _purchaseStatus.Text = "";
    }

    // ------------------------------------------------------------------ действия карточек тем с сайта

    /// <summary>Команда карточки: install / apply / remove / buy : код темы.</summary>
    private async Task RunServerThemeAsync(string arg)
    {
        try
        {
            var colon = arg.IndexOf(':');
            if (colon <= 0)
                return;
            var action = arg[..colon];
            var code = arg[(colon + 1)..];
            var owner = TopLevel.GetTopLevel(this) as Window;
            var entry = MarketplaceThemesClient.Shared.Find(code);
            if (entry is null)
            {
                PosAlertDialog.Show(owner, T("Тема с сайта", "Сайттан тема", "Website theme", "Siteden tema", "Saytdan mavzu"),
                    T("Темы нет в каталоге сайта — обновите покупки.", "Тема сайттын каталогунда жок — сатып алууларды жаңыртыңыз.", "The theme is not in the website catalog — refresh purchases.", "Tema sitenin kataloğunda yok — satın alımları yenileyin.", "Mavzu sayt katalogida yo'q — xaridlarni yangilang."),
                    PosAlertKind.Warning);
                return;
            }
            var title = entry.Name.Get() is { Length: > 0 } n ? n : entry.Code;
            switch (action)
            {
                case "buy":
                    if (MarketplaceWebLink.ForTheme(entry, UserPreferences.Instance.MarketplaceWebUrl) is { } url)
                        OpenSiteUrl(owner, url);
                    else
                        OpenMarketplaceSite(entry.Code);
                    break;
                case "install":
                    await InstallServerThemeAsync(owner, entry, title).ConfigureAwait(true);
                    break;
                case "apply":
                    if (MarketplaceServerThemes.Installer.CustomIdOf(code) is { } customId)
                    {
                        if (CustomThemeStore.Find(customId) is { } def && !ThemeReadability.ConfirmApply(owner, def))
                            break;
                        App.ApplyAccentTheme(customId);
                        RebuildThemedCards();
                    }
                    else
                        await InstallServerThemeAsync(owner, entry, title).ConfigureAwait(true); // тему убрали в «Редакторе тем» — ставим заново
                    break;
                case "remove":
                    var sure = await PosDialogs.ConfirmYesNoModalAsync(owner,
                        T($"Убрать тему «{title}» с этой кассы? Покупка на сайте остаётся — тему можно установить снова.", $"«{title}» теманы бул кассадан алып салаасызбы? Сайттагы сатып алуу калат — теманы кайра орнотсо болот.",
                            $"Remove the theme “{title}” from this till? The purchase on the website stays — you can install it again.", $"«{title}» temasını bu kasadan kaldırın mı? Sitedeki satın alma kalır — temayı yeniden yükleyebilirsiniz.", $"«{title}» mavzusi bu kassadan olib tashlansinmi? Saytdagi xarid qoladi — mavzuni qayta o'rnatish mumkin."),
                        T("Удалить тему", "Теманы өчүрүү", "Remove theme", "Temayı kaldır", "Mavzuni o'chirish"),
                        confirmText: T("Удалить", "Өчүрүү", "Remove", "Kaldır", "O'chirish"), danger: true).ConfigureAwait(true);
                    if (!sure)
                        break;
                    if (MarketplaceServerThemes.Installer.CustomIdOf(code) is { } installedId
                        && string.Equals(AccentThemeService.Normalize(UserPreferences.Instance.AccentTheme), installedId, StringComparison.OrdinalIgnoreCase))
                        App.ApplyAccentTheme("classic"); // выбранную тему убираем — возвращаем «Классическую», а не оставляем программу без темы
                    MarketplaceServerThemes.Installer.Remove(code);
                    RebuildThemedCards();
                    break;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: действие с темой «{arg}» не выполнено: {ex}", "WARNING");
        }
    }

    private async Task InstallServerThemeAsync(Window? owner, MarketplaceThemeEntry entry, string title)
    {
        if (_themeBusy)
            return;
        _themeBusy = true;
        try
        {
            ShowPurchaseStatus(T($"Скачиваем тему «{title}»…", $"«{title}» темасын жүктөп жатабыз…", $"Downloading the theme “{title}”…", $"«{title}» teması indiriliyor…", $"«{title}» mavzusi yuklanmoqda…"), TimeSpan.FromSeconds(60));
            var result = await MarketplaceServerThemes.Installer.InstallAsync(entry).ConfigureAwait(true);
            _statusClearTimer?.Stop();
            _purchaseStatus.Text = "";
            if (result.Ok)
            {
                RebuildThemedCards();
                PosAlertDialog.Show(owner, title,
                    result.Status == ThemeInstallStatus.Updated
                        ? T("Тема обновлена. Если она выбрана — цвета уже новые.", "Тема жаңыртылды. Тандалган болсо — түстөр жаңы.", "The theme is updated. If it is selected, the colours are already new.", "Tema güncellendi. Seçiliyse renkler zaten yeni.", "Mavzu yangilandi. Tanlangan bo'lsa — ranglar allaqachon yangi.")
                        : T("Тема установлена. Нажмите «Применить тему».", "Тема орнотулду. «Теманы колдонуу» баскычын басыңыз.", "The theme is installed. Press “Apply theme”.", "Tema yüklendi. «Temayı uygula»ya basın.", "Mavzu o'rnatildi. «Mavzuni qo'llash»ni bosing."),
                    PosAlertKind.Success);
                return;
            }
            if (result.Status == ThemeInstallStatus.NotPurchased)
            {
                // Сервер не нашёл покупку: каталог устарел — обновляем его, карточка станет «Купить на сайте».
                _ = MarketplaceThemesClient.Shared.RefreshAsync(force: true);
                PosAlertDialog.Show(owner, title, ThemeInstallProblem(result.Status), PosAlertKind.Warning);
                return;
            }
            PosAlertDialog.Show(owner, title, ThemeInstallProblem(result.Status), PosAlertKind.Error);
        }
        finally
        {
            _themeBusy = false;
        }
    }

    private static string ThemeInstallProblem(ThemeInstallStatus status) => status switch
    {
        ThemeInstallStatus.NotPurchased => T("Тема не куплена. Нажмите «Купить на сайте».", "Тема сатып алынган эмес. «Сайттан сатып алуу» баскычын басыңыз.", "The theme is not purchased. Press “Buy on the website”.", "Tema satın alınmamış. «Web sitesinden satın al»a basın.", "Mavzu sotib olinmagan. «Saytdan sotib olish»ni bosing."),
        ThemeInstallStatus.NotFound => T("Пакет темы на сервере не найден.", "Теманын пакети серверде табылган жок.", "The theme package was not found on the server.", "Tema paketi sunucuda bulunamadı.", "Mavzu paketi serverda topilmadi."),
        ThemeInstallStatus.HashMismatch or ThemeInstallStatus.SizeMismatch or ThemeInstallStatus.NoHash => T("Пакет темы повреждён или не прошёл проверку — тема не установлена. Попробуйте позже или сообщите в поддержку.", "Теманын пакети бузулган же текшерүүдөн өткөн жок — тема орнотулган жок. Кийинчерээк аракет кылыңыз же колдоого билдириңиз.",
            "The theme package is damaged or failed the check — the theme was not installed. Try later or tell support.", "Tema paketi bozuk veya doğrulamayı geçemedi — tema yüklenmedi. Daha sonra deneyin veya desteğe bildirin.", "Mavzu paketi buzilgan yoki tekshiruvdan o'tmadi — mavzu o'rnatilmadi. Keyinroq urinib ko'ring yoki yordam xizmatiga xabar bering."),
        ThemeInstallStatus.TooLarge => T("Пакет темы слишком большой (больше 5 МБ) — тема не установлена.", "Теманын пакети өтө чоң (5 МБдан ашык) — тема орнотулган жок.", "The theme package is too large (over 5 MB) — the theme was not installed.", "Tema paketi çok büyük (5 MB'tan fazla) — tema yüklenmedi.", "Mavzu paketi juda katta (5 MB dan ortiq) — mavzu o'rnatilmadi."),
        ThemeInstallStatus.BadPackage => T("Пакет темы не читается — тема не установлена. Сообщите в поддержку.", "Теманын пакети окулбайт — тема орнотулган жок. Колдоого билдириңиз.", "The theme package cannot be read — the theme was not installed. Tell support.", "Tema paketi okunamıyor — tema yüklenmedi. Desteğe bildirin.", "Mavzu paketi o'qilmaydi — mavzu o'rnatilmadi. Yordam xizmatiga xabar bering."),
        ThemeInstallStatus.NeedsNewerApp => T("Теме нужна более новая версия программы — обновите программу.", "Темага программанын жаңыраак версиясы керек — программаны жаңыртыңыз.", "The theme needs a newer version of the program — update the program.", "Tema için programın daha yeni sürümü gerekir — programı güncelleyin.", "Mavzu uchun dasturning yangiroq versiyasi kerak — dasturni yangilang."),
        ThemeInstallStatus.Unsupported => T("Этот вид пакета эта версия программы пока не ставит.", "Пакеттин бул түрүн программанын бул версиясы азырынча орнотпойт.", "This version of the program cannot install this kind of package yet.", "Programın bu sürümü bu paket türünü henüz yükleyemez.", "Dasturning bu versiyasi paketning bu turini hozircha o'rnatmaydi."),
        ThemeInstallStatus.NoConnection or ThemeInstallStatus.NotSignedIn => T("Сервер NurCRM недоступен. Проверьте интернет и повторите.", "NurCRM сервери жеткиликсиз. Интернетти текшерип, кайталаңыз.", "The NurCRM server is unavailable. Check the internet and retry.", "NurCRM sunucusu kullanılamıyor. İnterneti kontrol edip tekrar deneyin.", "NurCRM serveri mavjud emas. Internetni tekshirib, qayta urinib ko'ring."),
        _ => T("Тему установить не удалось. Попробуйте позже.", "Теманы орнотуу мүмкүн болгон жок. Кийинчерээк аракет кылыңыз.", "The theme could not be installed. Try later.", "Tema yüklenemedi. Daha sonra deneyin.", "Mavzuni o'rnatib bo'lmadi. Keyinroq urinib ko'ring."),
    };
}
