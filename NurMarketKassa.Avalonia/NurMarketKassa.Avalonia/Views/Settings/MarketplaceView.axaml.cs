using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class MarketplaceView : UserControl
{
    public MarketplaceView()
    {
        InitializeComponent();
        // 2026-10-23: витрина как в Play Market (MarketplaceStoreView) читает единый каталог (Services/MarketplaceCatalog.cs).
        // Действия плиток идут сюда, в Run — внутри та же логика, что была в карточках доп. функций.
        _inlineHost.Child = ExtrasWrapPanel;
        ExtrasWrapPanel.Children.CollectionChanged += (_, _) =>
        {
            ApplyInlineVisibility();
            ScheduleStoreReload();
        };
        Store.InlineControlProvider = ProvideInlineControl;
        Store.Initialize(new LocalMarketplaceCatalogSource(Run));
        // 2026-10-30 (стресс-тест «Управление и Помощь»): витрина собиралась при открытии трижды (Initialize, Store.Reload в конце
        // RebuildThemedCards и отложенная пересборка по событию прежних карточек) — ≈1,2 с «зависания» окна. Каталог от прежних карточек
        // не зависит (ProvideInlineControl = null), поэтому здесь достаточно первой сборки.
        _storeReloadPending = true;
        RebuildThemedCards(reload: false);
        _storeReloadPending = false;
        _themeBuiltFor = Application.Current?.ActualThemeVariant;
        // 2026-10-21: карточки красятся кистями темы в момент сборки — при смене светлой/тёмной (кнопка «Тема» у владельца,
        // редактор тем) пересобираем их, иначе остаются старые цвета. Post — AccentThemeService.Apply кладёт новые кисти
        // уже после смены варианта (App.ApplyTheme).
        // 2026-10-30: при открытии окна событие приходит само (вид получил вариант темы окна) — витрина уже собрана в нём; пересборка — только
        // если вариант (светлый/тёмный) действительно сменился.
        ActualThemeVariantChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (Equals(ActualThemeVariant, _themeBuiltFor))
                return;
            _themeBuiltFor = ActualThemeVariant;
            RebuildThemedCards();
        });

        // "Список" новых тем/доп. услуг — это, по сути, новая версия кассы (Velopack/GitHub
        // Releases): отдельного каталога на сервере нет, все карточки зашиты в код. Поэтому
        // "обновить список при открытии Маркетплейса" (2026-09-05, по просьбе пользователя)
        // реализовано как немедленная проверка обновлений с баннером — так пользователь
        // реально видит, что есть более новая версия с новыми функциями/темами.
        _ = CheckForUpdatesAsync();

        // 2026-10-31, ТЗ Умара «функции маркетплейса — с сервера»: при открытии Маркетплейса — свежий список функций, включённых
        // компании на сервере; пришло изменение (включили/выключили) — витрина перечитывается. Без связи/с ошибкой — как было, по ключам.
        AttachedToVisualTree += (_, _) => ServerAddons.Changed += OnServerAddonsChanged;
        DetachedFromVisualTree += (_, _) => ServerAddons.Changed -= OnServerAddonsChanged;
        _ = ServerAddons.RefreshAsync(force: true);
        InitWebPurchases(); // 2026-11-06: «Открыть Маркетплейс на сайте», «Обновить покупки», темы с сайта (MarketplaceView.WebPurchases.cs)
    }

    private void OnServerAddonsChanged() => Dispatcher.UIThread.Post(ScheduleStoreReload);

    // 2026-10-23: хранилище прежних карточек доп. функций. На экране они не показываются (витрина рисует каталог), но
    // обработчики активации по-прежнему обновляют их, а любое изменение списка перечитывает каталог (ScheduleStoreReload).
    // 2026-10-27: видимых прежних карточек не осталось (голосовое управление убрано).
    private readonly WrapPanel ExtrasWrapPanel = new();
    private readonly Border _inlineHost = new();
    private bool _storeReloadPending;
    private Avalonia.Styling.ThemeVariant? _themeBuiltFor;

    private void ScheduleStoreReload()
    {
        if (_storeReloadPending)
            return;
        _storeReloadPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _storeReloadPending = false;
            Store.Reload();
        }, DispatcherPriority.Background);
    }

    /// <summary>2026-10-27: из прежних карточек больше не видна ни одна (единственной была «Голосовое управление» — убрана).</summary>
    private void ApplyInlineVisibility()
    {
        foreach (var child in ExtrasWrapPanel.Children)
            child.IsVisible = false;
    }

    private Control? ProvideInlineControl(string id) => null;

    /// <summary>Команды действий каталога (см. LocalMarketplaceCatalogSource): существующая логика кассы, без изменений.</summary>
    private void Run(string command)
    {
        try
        {
            var e = new RoutedEventArgs();
            var colon = command.IndexOf(':');
            var verb = colon < 0 ? command : command[..colon];
            var id = colon < 0 ? "" : command[(colon + 1)..];
            switch (verb)
            {
                case "unlock":
                    switch (id)
                    {
                        case "loyalty": LoyaltyUnlockButton_Click(this, e); break;
                        case "timesheet": StaffTimesheetUnlockButton_Click(this, e); break;
                        case "scales": ScalesUnlockButton_Click(this, e); break;
                        case "analytics": WarehouseAnalyticsUnlockButton_Click(this, e); break;
                        case "pricetag": PriceTagEditorUnlockButton_Click(this, e); break;
                        case "bulktag": BulkPriceTagUnlockButton_Click(this, e); break;
                        case "labeleditor": LabelEditorUnlockButton_Click(this, e); break;
                        case "export": AnalyticsExportUnlockButton_Click(this, e); break;
                        case "telegram": TelegramBotUnlockButton_Click(this, e); break;
                        case "shiftstats": ShiftAnalyticsUnlockButton_Click(this, e); break;
                        case "language": LanguagePackUnlockButton_Click(this, e); break;
                        default:
                            if (id.StartsWith("pack:", StringComparison.Ordinal))
                            {
                                var slug = id["pack:".Length..];
                                var title = StandardPacks().FirstOrDefault(p => p.Slug == slug).Title ?? slug;
                                UnlockPack(slug, title);
                            }
                            break;
                    }
                    break;
                case "open":
                    OpenFeature(id);
                    break;
                case "request":
                    _ = RequestAddonAsync(id);
                    break;
                case "web":
                    OpenMarketplaceSite(string.IsNullOrEmpty(id) ? null : id); // 2026-11-06: «Открыть Маркетплейс на сайте» / «Купить на сайте» (web:kassa-loyalty)
                    break;
                case "servertheme":
                    _ = RunServerThemeAsync(id); // 2026-11-06: темы с сайта — install/apply/remove/buy:<код>
                    break;
                case "theme":
                    // 2026-10-27 (аудит редакторов, В-8): свою плохо читаемую тему молча не применяем — «Всё равно применить?».
                    if (CustomThemeStore.Find(id) is { } customToApply && !ThemeReadability.ConfirmApply(TopLevel.GetTopLevel(this) as Window, customToApply))
                        break;
                    App.ApplyAccentTheme(id);
                    // Предпросмотры рисуются под текущий светлый/тёмный вариант, а отметка — на выбранной теме.
                    RebuildThemedCards();
                    break;
                case "layout":
                    App.ApplyMainLayoutMode(id);
                    RefreshLayoutModeCard();
                    Store.Reload();
                    break;
                case "themeeditor":
                    // 2026-10-27 (аудит редакторов, В-7): «Изменить в редакторе» на карточке своей темы открывает именно эту тему (themeeditor:<id>).
                    OpenThemeEditor(string.IsNullOrEmpty(id) ? null : id);
                    break;
                case "sitetheme":
                    // 2026-10-27 (ТЗ «Сайт», С-3): тема сайта — открыть «Редактор сайта»; тема ждёт подтверждения там (SiteThemeCatalog.PendingThemeCode).
                    if (ProductActionPlan.OpenSection is null)
                        break;
                    SiteThemeCatalog.PendingThemeCode = id;
                    if (SiteEditorWindow.Current is { IsReady: true } openEditor)
                        _ = openEditor.ApplyPendingThemeAsync();
                    ProductActionPlan.OpenSection("siteeditor");
                    break;
                case "customerdisplay":
                    OpenCustomerDisplayEditor_Click(this, e);
                    break;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: действие «{command}» не выполнено: {ex}", "WARNING");
        }
    }

    private bool _requestBusy;

    /// <summary>2026-10-31, ТЗ Умара: «Подключить» — заявка в поддержку (POST api/support/addon-requests/, как на сайте). Оплата пока через
    /// поддержку: перезвонят, примут оплату и включат функцию на сервере (она появится здесь после следующей загрузки).</summary>
    private async Task RequestAddonAsync(string slug)
    {
        if (_requestBusy)
            return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var item = Store.FindItem(slug);
        var title = item?.Title ?? slug;
        _requestBusy = true;
        try
        {
            var dlg = new SimpleInputDialog(title,
                Tr.T("Ваш телефон для связи (не меньше 9 цифр), например +996 555 123 456:", "Байланыш үчүн телефонуңуз (кеминде 9 сан), мисалы +996 555 123 456:",
                     "Your phone number for contact (at least 9 digits), e.g. +996 555 123 456:", "İletişim için telefonunuz (en az 9 hane), örn. +996 555 123 456:",
                     "Aloqa uchun telefon raqamingiz (kamida 9 ta raqam), masalan +996 555 123 456:"),
                optional: false,
                confirmText: Tr.T("Отправить заявку", "Өтүнмөнү жөнөтүү", "Send request", "Talebi gönder", "Arizani yuborish"));
            if (PosDialogHost.Show(dlg, owner) != true)
                return;
            var phone = dlg.Result.Trim();
            if (!ServerAddons.PhoneLooksValid(phone))
            {
                AddonRequestAlert(owner, title, PhoneTooShortText(), PosAlertKind.Error);
                return;
            }

            // Цена — по-русски, как её увидит поддержка (на сайте то же: «1 500 сом разово»).
            var price = item?.Price is { Amount: { } amount } p
                ? $"{amount:N0} сом" + (p.Period == MarketplacePricePeriod.PerMonth ? "/мес" : " разово") + (p.MonthlyFee is { } fee ? $" + {fee:N0} сом/мес" : "")
                : "";
            var result = await ServerAddons.SubmitRequestAsync(slug, title, price, phone).ConfigureAwait(true);
            switch (result.Status)
            {
                case ServerAddons.RequestStatus.Sent:
                    AddonRequestAlert(owner, title,
                        Tr.T("Заявка отправлена. Поддержка перезвонит, примет оплату и включит функцию — она появится здесь сама.",
                             "Өтүнмө жөнөтүлдү. Колдоо кызматы чалып, төлөмдү кабыл алат жана функцияны иштетет — ал бул жерде өзү пайда болот.",
                             "Request sent. Support will call you, take the payment and turn the feature on — it will appear here by itself.",
                             "Talep gönderildi. Destek sizi arayacak, ödemeyi alacak ve özelliği açacak — burada kendiliğinden görünecek.",
                             "Ariza yuborildi. Qo'llab-quvvatlash qo'ng'iroq qiladi, to'lovni qabul qiladi va funksiyani yoqadi — u shu yerda o'zi paydo bo'ladi."),
                        PosAlertKind.Success);
                    break;
                case ServerAddons.RequestStatus.PhoneTooShort:
                    AddonRequestAlert(owner, title, PhoneTooShortText(), PosAlertKind.Error);
                    break;
                case ServerAddons.RequestStatus.TooMany:
                    AddonRequestAlert(owner, title,
                        Tr.T("Слишком много заявок за короткое время. Подождите несколько минут и попробуйте снова.", "Кыска убакытта өтө көп өтүнмө. Бир нече мүнөт күтүп, кайра аракет кылыңыз.",
                             "Too many requests in a short time. Wait a few minutes and try again.", "Kısa sürede çok fazla talep. Birkaç dakika bekleyip tekrar deneyin.", "Qisqa vaqtda juda ko'p ariza. Bir necha daqiqa kutib, qayta urinib ko'ring."),
                        PosAlertKind.Warning);
                    break;
                case ServerAddons.RequestStatus.Unavailable:
                    var tel = string.IsNullOrWhiteSpace(result.SupportPhone) ? "" : result.SupportPhone.Trim();
                    AddonRequestAlert(owner, title,
                        Tr.T("Заявки сейчас не принимаются. Позвоните в поддержку: " + (tel.Length > 0 ? tel : "номер на сайте nurcrm.kg"),
                             "Азыр өтүнмөлөр кабыл алынбайт. Колдоо кызматына чалыңыз: " + (tel.Length > 0 ? tel : "номери nurcrm.kg сайтында"),
                             "Requests are not accepted right now. Call support: " + (tel.Length > 0 ? tel : "the number is on nurcrm.kg"),
                             "Talepler şu an kabul edilmiyor. Destek hattını arayın: " + (tel.Length > 0 ? tel : "numara nurcrm.kg sitesinde"),
                             "Hozir arizalar qabul qilinmaydi. Qo'llab-quvvatlashga qo'ng'iroq qiling: " + (tel.Length > 0 ? tel : "raqam nurcrm.kg saytida")),
                        PosAlertKind.Warning);
                    break;
                case ServerAddons.RequestStatus.NoConnection:
                    AddonRequestAlert(owner, title,
                        Tr.T("Сервер NurCRM недоступен. Проверьте интернет и повторите — заявка не отправлена.", "NurCRM сервери жеткиликсиз. Интернетти текшерип, кайталаңыз — өтүнмө жөнөтүлгөн жок.", // 2026-11-01 (В-08): единая фраза о сбое сервера
                             "The NurCRM server is unavailable. Check the internet and retry — the request was not sent.", "NurCRM sunucusu kullanılamıyor. İnterneti kontrol edip tekrar deneyin — talep gönderilmedi.", "NurCRM serveri mavjud emas. Internetni tekshirib, qayta urinib ko'ring — ariza yuborilmadi."),
                        PosAlertKind.Warning);
                    break;
                default:
                    AddonRequestAlert(owner, title,
                        Tr.T("Заявку не удалось отправить. Попробуйте позже или позвоните в поддержку.", "Өтүнмөнү жөнөтүү мүмкүн болгон жок. Кийинчерээк аракет кылыңыз же колдоо кызматына чалыңыз.",
                             "The request could not be sent. Try later or call support.", "Talep gönderilemedi. Daha sonra deneyin veya destek hattını arayın.", "Arizani yuborib bo'lmadi. Keyinroq urinib ko'ring yoki qo'llab-quvvatlashga qo'ng'iroq qiling."),
                        PosAlertKind.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: заявка на подключение «{slug}» не отправлена: {ex.Message}", "WARNING");
        }
        finally
        {
            _requestBusy = false;
        }
    }

    /// <summary>Сообщение о заявке. Стенд проверок подменяет его (без реального окна); в программе всегда PosAlertDialog.</summary>
    internal static Action<Window?, string, string, PosAlertKind> AddonRequestAlert = (owner, title, text, kind) => PosAlertDialog.Show(owner, title, text, kind);

    private static string PhoneTooShortText() =>
        Tr.T("Телефон слишком короткий — введите не меньше 9 цифр.", "Телефон өтө кыска — кеминде 9 сан киргизиңиз.",
             "The phone number is too short — enter at least 9 digits.", "Telefon çok kısa — en az 9 hane girin.", "Telefon raqami juda qisqa — kamida 9 ta raqam kiriting.");

    /// <summary>«Открыть» разблокированной функции — те же действия, что были на кнопках карточек доп. функций.</summary>
    private void OpenFeature(string id)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        switch (id)
        {
            case "timesheet":
                StaffTimesheetWindow.Open(owner);
                break;
            case "analytics":
                // Программа владельца (2026-09-27): аналитика склада — вкладка «Склад» раздела «Аналитика».
                // В кассе и на тарифе «Старт» — отдельное окно склада, как было.
                if (owner is { Owner: OwnerShellWindow shell } && shell.OpenAnalyticsStock())
                    return;
                var analyticsWindow = App.GetRequiredService<WarehouseWindow>();
                analyticsWindow.AnalyticsOnly = true;
                analyticsWindow.Show(owner);
                break;
            case "pricetag":
                App.GetRequiredService<WarehouseWindow>().Show(owner);
                break;
            case "bulktag":
                _ = BulkPriceTagPrintDialog.Open(owner);
                break;
            case "labeleditor":
                _ = new LabelTemplateEditorDialog().ShowDialog(owner);
                break;
            case "export":
                PosAlertDialog.Show(owner,
                    Tr.T("Где выгрузить", "Кайдан чыгарса болот", "Where to export", "Nereden aktarılır", "Qayerda eksport qilinadi"),
                    Tr.T("Откройте «Отчёты», выберите период и нажмите «Excel» или «Word» в верхней строке.",
                         "«Отчёттор» бөлүмүн ачып, мезгилди тандап, жогорку саптагы «Excel» же «Word» баскычын басыңыз.", "Open “Reports”, select a period and click “Excel” or “Word” in the top bar.", "«Raporlar»ı açın, dönemi seçin ve üst satırdaki «Excel» veya «Word» düğmesine basın.", "«Hisobotlar»ni oching, davrni tanlang va yuqori qatordagi «Excel» yoki «Word» tugmasini bosing."),
                    PosAlertKind.Info);
                break;
            case "telegram":
                _ = new NurMarketKassa.AvaloniaHost.Views.TelegramBotSetupWindow().ShowDialog(owner);
                break;
            case "shiftstats":
                PosAlertDialog.Show(owner,
                    Tr.T("Где смотреть", "Кайдан көрсө болот", "Where to look", "Nereye bakmalı", "Qayerda ko'rish mumkin"),
                    Tr.T("Итоги открываются из меню кассы: «Смены» → выбрать смену. Те же цифры печатаются в X/Z-отчёте и приходят в Telegram при закрытии смены.",
                         "Жыйынтыктар касса менюсунан ачылат: «Смена» → сменаны тандаңыз. Ошол эле сандар X/Z-отчётто басылат жана смена жабылганда Telegram'га келет.", "Totals open from the till menu: “Shifts” → select a shift. The same figures are printed in the X/Z report and sent to Telegram when the shift is closed.", "Özet kasa menüsünden açılır: «Vardiyalar» → vardiyayı seçin. Aynı rakamlar X/Z raporunda yazdırılır ve vardiya kapanınca Telegram'a gelir.", "Yakunlar kassa menyusidan ochiladi: «Smenalar» → smenani tanlang. Xuddi shu raqamlar X/Z-hisobotda chop etiladi va smena yopilganda Telegram'ga keladi."),
                    PosAlertKind.Info);
                break;
        }
    }

    /// <summary>Открывает витрину на фильтре «Функции» — используется, когда пользователь переходит сюда из другой страницы
    /// настроек (см. ScreenSettingsView → "Открыть в Маркетплейсе"). Имя прежнее: на него ссылаются другие окна.</summary>
    public void ShowExtrasTab() => Store.SetFilter(MarketplaceFilter.Features);

    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var updateService = App.GetRequiredService<IAppUpdateService>();
            var result = await updateService.CheckAsync().ConfigureAwait(true);
            if (!result.IsConfigured || !result.IsUpdateAvailable || string.IsNullOrWhiteSpace(result.LatestVersion))
                return;

            // 2026-09-08: Velopack иногда считает установленной версию из своих внутренних
            // данных (locator), а не из реально запущенной сборки — на этой машине это привело
            // к тому, что баннер предлагал "обновиться" на ту же версию, что уже установлена и
            // работает (сборка НЕ через настоящий Velopack-инсталлятор/автообновление, а
            // локальным копированием файлов при разработке — версия из локатора не обновилась).
            // Экран "Обновления" в Настройках показывает версию из самой сборки
            // (Assembly.GetExecutingAssembly()) — она всегда достоверна. Сверяем с ней здесь тоже,
            // чтобы Маркетплейс не предлагал "обновление" на уже запущенную версию.
            // 2026-09-21, живой баг (скриншот владельца): проверялось только ТОЧНОЕ равенство
            // версий — если запущена версия НОВЕЕ последнего опубликованного релиза (например,
            // локальная тестовая сборка 1.16.49 при опубликованном на GitHub 1.16.45), баннер
            // всё равно предлагал "обновиться" — фактически откатиться на старую версию, стерев
            // все более новые локальные правки. Сравниваем версии по-настоящему (>=), а не только
            // на точное совпадение.
            var runningVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (runningVersion != null
                && System.Version.TryParse(result.LatestVersion, out var latestVersion)
                && runningVersion >= latestVersion)
            {
                return;
            }

            UpdateBannerTitle.Text = Tr.T(
                $"Доступно обновление {result.LatestVersion} — новые темы и функции!",
                $"{result.LatestVersion} жаңыртуусу жеткиликтүү — жаңы темалар жана функциялар!", $"Update {result.LatestVersion} is available — new themes and features!", $"Güncelleme mevcut: {result.LatestVersion} — yeni temalar ve özellikler!", $"Yangi versiya mavjud: {result.LatestVersion} — yangi mavzular va funksiyalar!");

            var notes = await updateService.GetReleaseNotesAsync(result.LatestVersion, System.Threading.CancellationToken.None)
                .ConfigureAwait(true);
            // Описание релиза — разметка на пяти языках; в баннер — первые пункты «Нового»
            // на языке программы обычным текстом (2026-09-28).
            notes = NurMarketKassa.Services.ReleaseNotesText.Plain(notes, maxItems: 4);
            UpdateBannerNotes.Text = string.IsNullOrWhiteSpace(notes)
                ? Tr.T("Нажмите «Обновить», чтобы получить последнюю версию.", "Акыркы версияны алуу үчүн «Жаңыртуу» баскычын басыңыз.", "Click “Update” to get the latest version.", "Son sürümü almak için «Güncelle»ye basın.", "Oxirgi versiyani olish uchun «Yangilash» tugmasini bosing.")
                : notes.Trim();

            UpdateBanner.IsVisible = true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Marketplace update check failed: {ex}", "UPDATE");
        }
    }

    private async void UpdateNow_Click(object? sender, RoutedEventArgs e)
    {
        var updateService = App.GetRequiredService<IAppUpdateService>();
        UpdateNowButton.IsEnabled = false;
        UpdateProgressBar.IsVisible = true;
        UpdateProgressBar.Value = 0;
        UpdateBannerNotes.Text = Tr.T("Скачивание обновления… 0%", "Жаңыртуу жүктөлүүдө… 0%", "Downloading update… 0%", "Güncelleme indiriliyor… %0", "Yangilanish yuklab olinmoqda… 0%");

        try
        {
            await updateService.DownloadAsync(percent =>
            {
                // DownloadAsync репортит прогресс с фонового потока Velopack.
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateProgressBar.Value = percent;
                    UpdateBannerNotes.Text = Tr.T($"Скачивание обновления… {percent}%", $"Жаңыртуу жүктөлүүдө… {percent}%",
                        $"Downloading update… {percent}%", $"Güncelleme indiriliyor… %{percent}", $"Yangilanish yuklab olinmoqda… {percent}%");
                });
            }).ConfigureAwait(true);

            UpdateBannerNotes.Text = Tr.T("Обновление скачано. Касса сейчас перезапустится…", "Жаңыртуу жүктөлдү. Касса азыр кайра ачылат…", "Update downloaded. The till will now restart…", "Güncelleme indirildi. Kasa şimdi yeniden başlatılacak…", "Yangilanish yuklab olindi. Kassa hozir qayta ishga tushadi…");
            await Task.Delay(1200).ConfigureAwait(true);

            // Не возвращает управление — Velopack завершает процесс изнутри.
            updateService.ApplyUpdateAndRestart();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Marketplace update download/apply failed: {ex}", "UPDATE");
            UpdateBannerNotes.Text = Tr.T($"Не удалось обновить: {ex.Message}", $"Жаңыртуу мүмкүн болгон жок: {ex.Message}",
                $"Could not update: {ex.Message}", $"Güncellenemedi: {ex.Message}", $"Yangilab bo'lmadi: {ex.Message}");
            UpdateProgressBar.IsVisible = false;
            UpdateNowButton.IsEnabled = true;
        }
    }

    /// <summary>Фильтр «Виды кассы» (2026-09-28) — программно, например из карточки «1С».</summary>
    public void ShowLayoutsTab() => Store.SetFilter(MarketplaceFilter.Layouts);

    // ------------------------------------------------------------------ экран покупателя (2026-09-28)

    private async void OpenCustomerDisplayEditor_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;
            await new CustomerDisplayEditorWindow().ShowDialog<bool?>(owner).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор экрана покупателя не открылся: {ex}", "WARNING");
        }
        finally
        {
            Store.Reload(); // 2026-10-23: описание и предпросмотр экрана покупателя на странице — в нынешнем виде
        }
    }

    /// <summary>2026-10-21: id темы, которая реально включена: сохранённая, если её цвета (фон окна, акцент, текст) сейчас в
    /// программе; иначе — встроенная тема с такими цветами; не нашлась или сохранена своя тема (у неё бывают свои цвета
    /// поверх основы) — сохранённая (пакет 3, п. 138).</summary>
    internal static string AppliedThemeId(string saved, IEnumerable<string> builtInIds)
    {
        if (Application.Current is not { } app || CustomThemeStore.Find(saved) is not null)
            return saved;
        Color? Live(string key) => app.Resources.TryGetValue(key, out var value) && value is ISolidColorBrush brush ? brush.Color : null;
        if (Live("BrushWindow") is not { } window || Live("BrushAccent") is not { } accent || Live("BrushText") is not { } text)
            return saved;
        var dark = app.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        bool Shown(string id) =>
            AccentThemeService.GetPreview(id, dark) is { } pv
            && Color.Parse(pv.Window) == window && Color.Parse(pv.Accent) == accent && Color.Parse(pv.Text) == text;
        if (Shown(saved))
            return saved;
        return builtInIds.FirstOrDefault(Shown) ?? saved;
    }

    /// <summary>«Редактор тем» (2026-09-28). Окно модальное: после закрытия галерея
    /// перестраивается — там могли появиться, измениться или исчезнуть свои темы.</summary>
    private void OpenThemeEditor_Click(object? sender, RoutedEventArgs e) => OpenThemeEditor(null);

    private async void OpenThemeEditor(string? themeId)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;
            var editor = new ThemeEditorWindow();
            if (!string.IsNullOrEmpty(themeId))
                editor.OpenTheme(themeId);
            await editor.ShowDialog<bool?>(owner).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор тем не открылся: {ex}", "WARNING");
        }
        finally
        {
            RebuildThemedCards(); // 2026-10-23: свои темы могли появиться/измениться/исчезнуть
        }
    }

    /// <summary>2026-10-21: галерея тем и карточки «Доп. функций» — в порядке конструктора (порядок карточек в ряду).</summary>
    private void RebuildThemedCards(bool reload = true)
    {
        RefreshLoyaltyCard();
        RefreshWarehouseAnalyticsCard();
        RefreshPriceTagEditorCard();
        RefreshLabelEditorCard();
        RefreshLayoutModeCard();
        RefreshLanguagePackCard();
        RefreshStaffTimesheetCard();
        RefreshTelegramBotCard();
        RefreshShiftAnalyticsCard();
        RefreshAnalyticsExportCard();
        RefreshScalesCard();
        RefreshStandardPackCards();
        if (reload)
            Store.Reload(); // 2026-10-23: темы и виды кассы рисуются цветами текущего варианта темы
    }

    /// <summary>Кисть из ресурсов приложения — карточки Маркетплейса собираются в коде, а там
    /// DynamicResource недоступен, поэтому значение берётся напрямую с запасным вариантом.</summary>
    private IBrush ThemeBrush(string key, IBrush fallback)
    {
        // 2026-10-21: карточки собираются в конструкторе, когда вид ещё не в окне — поиск от this не доходил до ресурсов
        // приложения и отдавал запасной цвет (белый низ карточек тем в тёмной теме). Вне дерева ищем в Application,
        // и всегда с вариантом темы — кисти Warning/Success… лежат в словарях Light/Dark.
        var found = this.TryFindResource(key, ActualThemeVariant, out var value)
                    || (Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out value));
        return found && value is IBrush brush ? brush : fallback;
    }

    /// <summary>Проверка серийного номера — офлайн, нет сервера лицензий. Два вида принимаемых
    /// ключей: постоянный (свой на каждую функцию, см. LicenseKeys.IsPermanentSerial) открывает
    /// её навсегда, единый мастер-ключ (LicenseKeys.MasterTestSerial) — любую, но на 15 минут
    /// (LicenseKeys.ExpireIfDue). isPermanent говорит вызывающему коду, какой случай сработал.</summary>
    private static bool TryValidateSerial(string serial, string featureSlug, out bool isPermanent)
    {
        if (LicenseKeys.IsPermanentSerial(featureSlug, serial))
        {
            isPermanent = true;
            return true;
        }

        isPermanent = false;
        return LicenseKeys.IsMasterSerial(serial);
    }

    // 2026-10-27: карточка «Голосовое управление» (активация, загрузка моделей, переключатель, проверка) убрана — есть ИИ-ассистент.
}
