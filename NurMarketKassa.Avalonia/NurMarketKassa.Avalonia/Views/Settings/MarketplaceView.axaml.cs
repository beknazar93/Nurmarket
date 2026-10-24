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
        RebuildThemedCards();
        // 2026-10-21: карточки красятся кистями темы в момент сборки — при смене светлой/тёмной (кнопка «Тема» у владельца,
        // редактор тем) пересобираем их, иначе остаются старые цвета. Post — AccentThemeService.Apply кладёт новые кисти
        // уже после смены варианта (App.ApplyTheme).
        ActualThemeVariantChanged += (_, _) => Dispatcher.UIThread.Post(RebuildThemedCards);

        // "Список" новых тем/доп. услуг — это, по сути, новая версия кассы (Velopack/GitHub
        // Releases): отдельного каталога на сервере нет, все карточки зашиты в код. Поэтому
        // "обновить список при открытии Маркетплейса" (2026-09-05, по просьбе пользователя)
        // реализовано как немедленная проверка обновлений с баннером — так пользователь
        // реально видит, что есть более новая версия с новыми функциями/темами.
        _ = CheckForUpdatesAsync();
    }

    // 2026-10-23: хранилище прежних карточек доп. функций. На экране они не показываются (витрина рисует каталог), но
    // обработчики активации по-прежнему обновляют их, а любое изменение списка перечитывает каталог (ScheduleStoreReload).
    // Единственная карточка, которую видно, — «Голосовое управление» (загрузка моделей) на странице функции.
    private readonly WrapPanel ExtrasWrapPanel = new();
    private readonly Border _inlineHost = new();
    private bool _storeReloadPending;

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

    /// <summary>Из прежних карточек видна только «Голосовое управление» (кнопки загрузки моделей, переключатель, проверка);
    /// шапка и описание скрыты — они уже есть на странице.</summary>
    private void ApplyInlineVisibility()
    {
        foreach (var child in ExtrasWrapPanel.Children)
        {
            var show = ReferenceEquals(child, _voiceControlCard);
            child.IsVisible = show;
            if (show && child is Border { Child: Panel panel } card)
            {
                card.Width = double.NaN;
                card.Margin = default;
                for (var i = 0; i < Math.Min(2, panel.Children.Count); i++)
                    panel.Children[i].IsVisible = false;
            }
        }
    }

    private Control? ProvideInlineControl(string id)
    {
        if (id != "voice" || !UserPreferences.Instance.VoiceControlUnlocked)
            return null;
        // Блок мог остаться в прежней странице, пока строится новая.
        if (_inlineHost.Parent is Panel old)
            old.Children.Remove(_inlineHost);
        ApplyInlineVisibility();
        return _inlineHost;
    }

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
                        case "voice": VoiceUnlockButton_Click(this, e); break;
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
                case "theme":
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
                    OpenThemeEditor_Click(this, e);
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
    private async void OpenThemeEditor_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not Window owner)
                return;
            var editor = new ThemeEditorWindow();
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
    private void RebuildThemedCards()
    {
        RefreshLoyaltyCard();
        RefreshVoiceControlCard();
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

    private Border? _voiceControlCard;

    /// <summary>Пересобирает карточку "Голосовое управление" в "Доп. функциях" под текущее
    /// состояние (заблокировано / разблокировано-не-скачано / установлено) — тот же приём,
    /// что и BuildThemeGallery для тем, только для одной карточки, а не набора.</summary>
    private void RefreshVoiceControlCard()
    {
        if (_voiceControlCard is not null)
            ExtrasWrapPanel.Children.Remove(_voiceControlCard);

        _voiceControlCard = BuildVoiceControlCard();
        ExtrasWrapPanel.Children.Add(_voiceControlCard);
    }

    private Border BuildVoiceControlCard()
    {
        var prefs = UserPreferences.Instance;
        var unlocked = prefs.VoiceControlUnlocked;
        // 2026-10-24 (аудит, круг 8, С-9): у владельца голос не работает — смотрим модели КАССЫ этого ПК, а не своей папки.
        var ownerView = AppMode.IsOwner;
        var installedRu = ownerView ? VoiceModelDownloadService.IsInstalledInKassa(VoiceModelLanguage.Russian) : VoiceModelDownloadService.IsInstalled(VoiceModelLanguage.Russian);
        var installedKy = ownerView ? VoiceModelDownloadService.IsInstalledInKassa(VoiceModelLanguage.Kyrgyz) : VoiceModelDownloadService.IsInstalled(VoiceModelLanguage.Kyrgyz);
        var installed = installedRu || installedKy;

        var titleText = new TextBlock
        {
            Text = Tr.T("🎙 Голосовое управление", "🎙 Үн менен башкаруу", "🎙 Voice control", "🎙 Sesli kontrol", "🎙 Ovozli boshqaruv"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var (badgeText, badgeBrushKey) = (unlocked, installed) switch
        {
            (false, _) => (Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"), "BrushWarning"),
            (true, false) => (Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi"), "BrushAccent"),
            (true, true) => (Tr.T("✓ Установлено", "✓ Орнотулду", "✓ Installed", "✓ Kuruldu", "✓ O'rnatildi"), "BrushAccent"),
        };

        var badge = new Border
        {
            Background = ThemeBrush($"{badgeBrushKey}Soft", Brushes.Transparent),
            BorderBrush = ThemeBrush(badgeBrushKey, Brushes.Orange),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = badgeText,
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush(badgeBrushKey, Brushes.DarkOrange),
            },
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "Кассир говорит «касса» и название товара — программа сама находит и добавляет его в чек. Работает офлайн. Пакет распознавания речи (~113 МБ) скачивается отдельно, чтобы не утяжелять базовую кассу.",
                "Кассир «касса» деп, андан кийин товардын атын айтат — программа аны өзү таап, чекке кошот. Офлайн иштейт. Кепти таануу пакети (~113 МБ) негизги кассаны оордотпош үчүн өзүнчө жүктөлөт.", "The cashier says “kassa” and the product name — the program finds it and adds it to the receipt. Works offline. The speech recognition pack (~113 MB) is downloaded separately to keep the base till lightweight.", "Kasiyer «kassa» deyip ürün adını söyler — program ürünü kendisi bulur ve fişe ekler. Çevrimdışı çalışır. Ses tanıma paketi (~113 MB), temel kasayı ağırlaştırmamak için ayrıca indirilir.", "Kassir «kassa» so'zini va mahsulot nomini aytadi — dastur mahsulotni o'zi topib, chekka qo'shadi. Oflayn ishlaydi. Asosiy kassani og'irlashtirmaslik uchun nutqni tanish paketi (~113 MB) alohida yuklab olinadi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);

        if (!unlocked)
        {
            var unlockButton = new Button
            {
                Classes = { "btn-secondary" },
                Content = Tr.T("🔒 Активировать — 2000 сом", "🔒 Активдештирүү — 2000 сом", "🔒 Activate — 2000 som", "🔒 Etkinleştir — 2000 som", "🔒 Faollashtirish — 2000 so'm"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            unlockButton.Click += VoiceUnlockButton_Click;
            content.Children.Add(unlockButton);
        }
        else if (ownerView)
        {
            // 2026-10-24 (аудит, круг 8, С-9): у владельца — только статус моделей кассы, без скачивания, переключателя и проверки.
            foreach (var (isInstalled, ruOk, ruNo) in new[]
            {
                (installedRu,
                    Tr.T("Русская модель установлена в кассе этого компьютера", "Орус модели бул компьютердин кассасында орнотулган", "The Russian model is installed in this computer's till", "Rusça model bu bilgisayarın kasasında kurulu", "Rus modeli shu kompyuterning kassasida o'rnatilgan"),
                    Tr.T("Русская модель не установлена — скачайте в кассе: Маркетплейс → Голосовое управление", "Орус модели орнотулган эмес — кассадан жүктөп алыңыз: Маркетплейс → Үн менен башкаруу", "The Russian model is not installed — download it in the till: Marketplace → Voice control", "Rusça model kurulu değil — kasadan indirin: Pazar yeri → Sesli kontrol", "Rus modeli o'rnatilmagan — kassada yuklab oling: Marketpleys → Ovozli boshqaruv")),
                (installedKy,
                    Tr.T("Кыргызская модель установлена в кассе этого компьютера", "Кыргыз модели бул компьютердин кассасында орнотулган", "The Kyrgyz model is installed in this computer's till", "Kırgızca model bu bilgisayarın kasasında kurulu", "Qirg'iz modeli shu kompyuterning kassasida o'rnatilgan"),
                    Tr.T("Кыргызская модель не установлена — скачайте в кассе: Маркетплейс → Голосовое управление", "Кыргыз модели орнотулган эмес — кассадан жүктөп алыңыз: Маркетплейс → Үн менен башкаруу", "The Kyrgyz model is not installed — download it in the till: Marketplace → Voice control", "Kırgızca model kurulu değil — kasadan indirin: Pazar yeri → Sesli kontrol", "Qirg'iz modeli o'rnatilmagan — kassada yuklab oling: Marketpleys → Ovozli boshqaruv")),
            })
            {
                content.Children.Add(new TextBlock
                {
                    Text = (isInstalled ? "✓ " : "") + (isInstalled ? ruOk : ruNo),
                    FontSize = 12,
                    Foreground = ThemeBrush(isInstalled ? "BrushAccent" : "BrushTextSoft", Brushes.Gray),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
        }
        else
        {
            // Обе языковые модели можно скачать независимо — ту, что ещё не установлена,
            // всегда предлагаем (не только когда не установлена ни одна), чтобы можно было
            // добавить второй язык в любой момент, не переактивируя доп. услугу заново.
            if (!installedRu)
                content.Children.Add(BuildVoiceModelDownloadRow(
                    VoiceModelLanguage.Russian, Tr.T("⬇ Скачать русскую модель (~113 МБ)", "⬇ Орусча моделди жүктөп алуу (~113 МБ)", "⬇ Download the Russian model (~113 MB)", "⬇ Rusça modeli indir (~113 MB)", "⬇ Rus modelini yuklab olish (~113 MB)")));
            if (!installedKy)
                content.Children.Add(BuildVoiceModelDownloadRow(
                    VoiceModelLanguage.Kyrgyz, Tr.T("⬇ Скачать кыргызскую модель (~60 МБ)", "⬇ Кыргызча моделди жүктөп алуу (~60 МБ)", "⬇ Download the Kyrgyz model (~60 MB)", "⬇ Kırgızca modeli indir (~60 MB)", "⬇ Qirg'iz modelini yuklab olish (~60 MB)")));

            if (installed)
            {
                var enableCheck = new CheckBox
                {
                    Classes = { "ToggleSwitch" },
                    Height = 40,
                    Content = new TextBlock { Text = Tr.T("Включить голосовое управление", "Үн менен башкарууну күйгүзүү", "Enable voice control", "Sesli kontrolü etkinleştir", "Ovozli boshqaruvni yoqish"), FontSize = 12 },
                    IsChecked = prefs.VoiceControlEnabled,
                };
                enableCheck.Click += VoiceEnabledCheck_Click;

                var testButton = new Button
                {
                    Classes = { "btn-secondary" },
                    Content = Tr.T("🎙 Проверить и обучение", "🎙 Текшерүү жана үйрөтүү", "🎙 Test and train", "🎙 Test et ve eğit", "🎙 Tekshirish va o'rgatish"),
                    Height = 32,
                    Padding = new Thickness(14, 4),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                testButton.Click += (_, _) => VoiceControlTestWindow.Open(TopLevel.GetTopLevel(this) as Window);

                content.Children.Add(enableCheck);
                content.Children.Add(testButton);
            }
        }

        return new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };
    }

    private void VoiceUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Голосовое управление", "Үн менен башкаруу", "Voice control", "Sesli kontrol", "Ovozli boshqaruv"));
        if (serial == null)
            return; // отменено

        if (!TryValidateSerial(serial, "voice", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.VoiceControlUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("voice");

        PosAlertDialog.Show(
            owner,
            Tr.T("Доп. услуга разблокирована", "Кошумча кызмат ачылды", "Add-on unlocked", "Ek hizmet kilidi açıldı", "Qo'shimcha xizmat ochildi"),
            Tr.T(
                "Серийный номер принят — теперь можно скачать пакет распознавания речи.",
                "Сериялык номер кабыл алынды — эми кепти таануу пакетин жүктөп алсаңыз болот.", "Serial number accepted — you can now download the speech recognition pack.", "Seri numarası kabul edildi — artık ses tanıma paketini indirebilirsiniz.", "Seriya raqami qabul qilindi — endi nutqni tanish paketini yuklab olish mumkin."),
            PosAlertKind.Success);

        RefreshVoiceControlCard();
    }

    /// <summary>Одна строка "скачать модель X" в карточке — кнопка + прогресс-бар + статус,
    /// та же структура для любого языка, отличается только текст кнопки и передаваемый
    /// VoiceModelLanguage.</summary>
    private StackPanel BuildVoiceModelDownloadRow(VoiceModelLanguage language, string buttonLabel)
    {
        var downloadButton = new Button
        {
            Classes = { "btn-primary" },
            Content = buttonLabel,
            Height = 32,
            Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var progressBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6,
            IsVisible = false,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var statusText = new TextBlock
        {
            FontSize = 11,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            IsVisible = false,
        };
        downloadButton.Click += async (_, _) =>
            await StartVoiceModelDownloadAsync(language, downloadButton, progressBar, statusText).ConfigureAwait(true);

        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(downloadButton);
        row.Children.Add(progressBar);
        row.Children.Add(statusText);
        return row;
    }

    private async System.Threading.Tasks.Task StartVoiceModelDownloadAsync(
        VoiceModelLanguage language, Button button, ProgressBar progressBar, TextBlock statusText)
    {
        button.IsEnabled = false;
        progressBar.IsVisible = true;
        statusText.IsVisible = true;
        statusText.Text = Tr.T("Загрузка… 0%", "Жүктөлүүдө… 0%", "Downloading… 0%", "İndiriliyor… %0", "Yuklanmoqda… 0%");

        var progress = new Progress<double>(percent =>
        {
            progressBar.Value = percent;
            statusText.Text = percent < 100
                ? Tr.T($"Загрузка… {percent:F0}%", $"Жүктөлүүдө… {percent:F0}%",
                    $"Downloading… {percent:F0}%", $"İndiriliyor… %{percent:F0}", $"Yuklanmoqda… {percent:F0}%")
                : Tr.T("Распаковка…", "Архив ачылууда…", "Unpacking…", "Paket açılıyor…", "Arxivdan chiqarilmoqda…");
        });

        var (ok, error) = await VoiceModelDownloadService.DownloadAndInstallAsync(language, progress).ConfigureAwait(true);

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (!ok)
        {
            button.IsEnabled = true;
            // Показываем НАСТОЯЩУЮ причину. Раньше на любую ошибку писали «Проверьте интернет»,
            // и владелец искал проблему в связи, хотя файла просто нет на сервере.
            statusText.Text = error ?? Tr.T("Загрузка отменена.", "Жүктөө токтотулду.",
                "Download canceled.", "İndirme iptal edildi.", "Yuklash bekor qilindi.");
            return;
        }

        PosAlertDialog.Show(
            owner,
            Tr.T("Пакет установлен", "Пакет орнотулду", "Package installed", "Paket kuruldu", "Paket o'rnatildi"),
            Tr.T(
                "Голосовое управление скачано — теперь включите его переключателем.",
                "Үн менен башкаруу жүктөлдү — эми аны которгуч менен күйгүзүңүз.", "Voice control downloaded — now turn it on with the switch.", "Sesli kontrol indirildi — şimdi anahtarı açarak etkinleştirin.", "Ovozli boshqaruv yuklab olindi — endi uni almashtirgich bilan yoqing."),
            PosAlertKind.Success);

        RefreshVoiceControlCard();
    }

    private void VoiceEnabledCheck_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check)
            return;

        UserPreferences.Instance.VoiceControlEnabled = check.IsChecked == true;
        UserPreferences.Instance.SaveToDisk();

        try
        {
            App.GetRequiredService<IVoiceControlService>().Start();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Голосовое управление: не удалось применить настройку: {ex}", "VOICE");
        }
    }

    private Border? _loyaltyCard;

    /// <summary>Программа лояльности стала платной доп. услугой (2026-09-05, по прямому
    /// требованию пользователя: "убери из Настройки → Операции... сделай платной... при покупке
    /// только включи") — раньше был свободный тумблер в Настройки → Операции (см. историю в
    /// OperationsSettingsView), теперь и тумблера, и процента начисления в UI больше нет вообще:
    /// покупка серийника СРАЗУ включает LoyaltyEnabled, без отдельного шага "включить". Процент
    /// начисления (UserPreferences.LoyaltyEarnPercent) остался в преференсах со старым значением
    /// (по умолчанию 5%), но менять его через интерфейс сейчас негде — пользователь не просил
    /// это сохранить, если понадобится отдельная настройка процента, её можно будет вернуть сюда
    /// же, в карточку.</summary>
    private void RefreshLoyaltyCard()
    {
        if (_loyaltyCard is not null)
            ExtrasWrapPanel.Children.Remove(_loyaltyCard);

        _loyaltyCard = BuildLoyaltyCard();
        ExtrasWrapPanel.Children.Add(_loyaltyCard);
    }

    private Border BuildLoyaltyCard()
    {
        var unlocked = UserPreferences.Instance.LoyaltyEnabled;

        var titleText = new TextBlock
        {
            Text = Tr.T("🎁 Программа лояльности", "🎁 Лоялдуулук программасы", "🎁 Loyalty program", "🎁 Sadakat programı", "🎁 Sodiqlik dasturi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeKey = unlocked ? "BrushAccent" : "BrushWarning";
        var badge = new Border
        {
            Background = ThemeBrush($"{badgeKey}Soft", Brushes.Transparent),
            BorderBrush = ThemeBrush(badgeKey, Brushes.Orange),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = unlocked ? Tr.T("✓ Включено", "✓ Күйгүзүлгөн", "✓ Enabled", "✓ Etkin", "✓ Yoqilgan") : Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush(badgeKey, Brushes.DarkOrange),
            },
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "Бонусные баллы клиентам за покупки — начисление и списание при следующей оплате.",
                "Клиенттерге сатып алуулары үчүн бонус упайлар: топтолот жана кийинки төлөмдө чегерилет.", "Bonus points for customer purchases — earned at payment and redeemed on the next one.", "Alışveriş yapan müşterilere bonus puan: puanlar ödemede kazanılır ve sonraki alışverişte harcanır.", "Xaridlar uchun mijozlarga bonus ballar: ball yig'ish va keyingi to'lovda ularni ishlatish."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);

        if (!unlocked)
        {
            var unlockButton = new Button
            {
                Classes = { "btn-secondary" },
                Content = Tr.T("🔒 Активировать — 1500 сом", "🔒 Активдештирүү — 1500 сом", "🔒 Activate — 1500 som", "🔒 Etkinleştir — 1500 som", "🔒 Faollashtirish — 1500 so'm"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            unlockButton.Click += LoyaltyUnlockButton_Click;
            content.Children.Add(unlockButton);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = Tr.T("Работает автоматически на кассе — отдельно ничего открывать не нужно.",
                    "Кассада автоматтык түрдө иштейт — өзүнчө эч нерсе ачуунун кереги жок.", "Works automatically at the till — nothing to open separately.", "Kasada otomatik çalışır — ayrıca bir şey açmanıza gerek yok.", "Kassada avtomatik ishlaydi — alohida hech narsani ochish shart emas."),
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };
    }

    private void LoyaltyUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Программа лояльности", "Лоялдуулук программасы", "Loyalty program", "Sadakat programı", "Sodiqlik dasturi"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "loyalty", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        // "При покупке только включи" — без отдельного шага, покупка сразу включает бонусы.
        UserPreferences.Instance.LoyaltyEnabled = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("loyalty");
        RefreshLoyaltyCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Программа лояльности активирована", "Лоялдуулук программасы иштетилди", "Loyalty program activated", "Sadakat programı etkinleştirildi", "Sodiqlik dasturi faollashtirildi"),
            Tr.T(
                $"Бонусные баллы включены и начисляются автоматически при оплате — {UserPreferences.Instance.LoyaltyEarnPercent:0.#}% от суммы покупки.",
                $"Бонус упайлар күйгүзүлдү жана төлөмдө автоматтык түрдө эсептелет — сатып алуу суммасынын {UserPreferences.Instance.LoyaltyEarnPercent:0.#}% өлчөмүндө.", $"Bonus points are on and earned automatically at payment — {UserPreferences.Instance.LoyaltyEarnPercent:0.#}% of the purchase amount.", $"Bonus puanlar etkin: her ödemede alışveriş tutarı üzerinden %{UserPreferences.Instance.LoyaltyEarnPercent:0.#} otomatik olarak kazanılır.", $"Bonus ballar yoqildi va to'lovda avtomatik hisoblanadi — xarid summasidan {UserPreferences.Instance.LoyaltyEarnPercent:0.#}%."),
            PosAlertKind.Success);
    }

    private Border? _staffTimesheetCard;

    /// <summary>Замена статичной заглушки "Учёт сотрудников" ("Скоро", extra3) — первая часть
    /// описания ("Табель, смены") реализована как StaffTimesheetWindow, чистый отчёт по уже
    /// существующим данным о сменах. Вторая часть описания ("мотивация персонала") сознательно
    /// НЕ реализована — расчёт премий/бонусов требует конкретных правил начисления, которых
    /// пользователь пока не задавал (2026-09-05, обсуждалось явно — начали именно с табеля).
    /// Тот же приём разблокировки, что и у BuildWarehouseAnalyticsCard.</summary>
    private void RefreshStaffTimesheetCard()
    {
        if (_staffTimesheetCard is not null)
            ExtrasWrapPanel.Children.Remove(_staffTimesheetCard);

        _staffTimesheetCard = BuildStaffTimesheetCard();
        ExtrasWrapPanel.Children.Add(_staffTimesheetCard);
    }

    private Border BuildStaffTimesheetCard()
    {
        var unlocked = UserPreferences.Instance.StaffTimesheetUnlocked;

        var titleText = new TextBlock
        {
            Text = Tr.T("🧑‍💼 Учёт сотрудников", "🧑‍💼 Кызматкерлерди эсепке алуу", "🧑‍💼 Staff timesheet", "🧑‍💼 Personel takibi", "🧑‍💼 Xodimlar hisobi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeKey = unlocked ? "BrushAccent" : "BrushWarning";
        var badge = new Border
        {
            Background = ThemeBrush($"{badgeKey}Soft", Brushes.Transparent),
            BorderBrush = ThemeBrush(badgeKey, Brushes.Orange),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = unlocked ? Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi") : Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush(badgeKey, Brushes.DarkOrange),
            },
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "Табель по кассирам: сколько смен, сколько часов отработано и какая выручка за выбранный период — по уже имеющимся данным о сменах.",
                "Кассирлер боюнча табель: тандалган мезгилде канча смена, канча саат иштелгени жана канча түшүм болгону — сменалар боюнча бар маалыматтардан.", "Timesheet by cashier: number of shifts, hours worked and revenue for the selected period — based on existing shift data.", "Kasiyer çizelgesi: seçilen dönemde kaç vardiya, kaç saat çalışıldığı ve ne kadar ciro yapıldığı — mevcut vardiya verilerine göre.", "Kassirlar bo'yicha tabel: tanlangan davrda nechta smena, necha soat ishlangani va qancha tushum bo'lgani — mavjud smena ma'lumotlari asosida."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);

        if (!unlocked)
        {
            var unlockButton = new Button
            {
                Classes = { "btn-secondary" },
                Content = Tr.T("🔒 Активировать — 1500 сом", "🔒 Активдештирүү — 1500 сом", "🔒 Activate — 1500 som", "🔒 Etkinleştir — 1500 som", "🔒 Faollashtirish — 1500 so'm"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            unlockButton.Click += StaffTimesheetUnlockButton_Click;
            content.Children.Add(unlockButton);
        }
        else
        {
            var openButton = new Button
            {
                Classes = { "btn-primary" },
                Content = Tr.T("🧑‍💼 Открыть табель", "🧑‍💼 Табелди ачуу", "🧑‍💼 Open timesheet", "🧑‍💼 Çizelgeyi aç", "🧑‍💼 Tabelni ochish"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            openButton.Click += (_, _) => StaffTimesheetWindow.Open(TopLevel.GetTopLevel(this) as Window);
            content.Children.Add(openButton);
        }

        return new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };
    }

    private void StaffTimesheetUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Учёт сотрудников", "Кызматкерлерди эсепке алуу", "Staff timesheet", "Personel takibi", "Xodimlar hisobi"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "timesheet", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.StaffTimesheetUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("timesheet");
        RefreshStaffTimesheetCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Учёт сотрудников активирован", "Кызматкерлерди эсепке алуу иштетилди", "Staff timesheet activated", "Personel takibi etkinleştirildi", "Xodimlar hisobi faollashtirildi"),
            Tr.T(
                "Табель по кассирам доступен по кнопке «Открыть табель» прямо здесь, в Доп. функциях.",
                "Кассирлер боюнча табелди ушул жерден — Кошумча функциялардагы «Табелди ачуу» баскычы аркылуу ачса болот.", "The cashier timesheet opens with the “Open timesheet” button right here in Extras.", "Kasiyer çizelgesi burada, Ek özellikler bölümündeki «Çizelgeyi aç» düğmesiyle açılır.", "Kassirlar tabelini shu yerning o'zida, Qo'shimcha funksiyalar bo'limidagi «Tabelni ochish» tugmasi bilan ochish mumkin."),
            PosAlertKind.Success);
    }

    private Border? _scalesCard;

    /// <summary>«Весы» (отправка PLU на сетевые весы Штрих-М/Rongta) — платная доп. услуга, но
    /// ТОЛЬКО на тарифе «Старт» (2026-09-21, по просьбе владельца: "весы надо добавить в доп
    /// услуги в тарифе старт"); на «Стандарт» и выше эта функция бесплатна как раньше, поэтому
    /// карточка там не показывается вообще — продавать нечего. См. TariffGate.CanUseScales,
    /// разблокированная карточка прячет соответствующий блок на вкладке Настройки → Весы
    /// (ScaleSettingsView.RefreshPluCardVisibility).</summary>
    private void RefreshScalesCard()
    {
        if (_scalesCard is not null)
        {
            ExtrasWrapPanel.Children.Remove(_scalesCard);
            _scalesCard = null;
        }

        if (!NurMarketKassa.Services.TariffGate.IsStartTariff)
            return;

        _scalesCard = BuildScalesCard();
        ExtrasWrapPanel.Children.Add(_scalesCard);
    }

    private Border BuildScalesCard()
    {
        var unlocked = UserPreferences.Instance.ScalesUnlocked;

        var titleText = new TextBlock
        {
            Text = Tr.T("⚖ Весы", "⚖ Тараза", "⚖ Scales", "⚖ Tartı", "⚖ Tarozi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeKey = unlocked ? "BrushAccent" : "BrushWarning";
        var badge = new Border
        {
            Background = ThemeBrush($"{badgeKey}Soft", Brushes.Transparent),
            BorderBrush = ThemeBrush(badgeKey, Brushes.Orange),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = unlocked ? Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi") : Tr.T("🔒 Платно на тарифе «Старт»", "🔒 «Старт» тарифинде акылуу", "🔒 Paid on the “Start” plan", "🔒 «Start» tarifesinde ücretli", "🔒 «Start» tarifida pullik"),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush(badgeKey, Brushes.DarkOrange),
            },
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "Отправка весовых товаров с PLU-кодами на сетевые весы (Штрих-М, Rongta) — Настройки → Весы. Базовое взвешивание на кассе (COM-довес) сюда не входит и остаётся бесплатным.",
                "PLU коддору бар салмактуу товарларды тармактык таразаларга жөнөтүү (Штрих-М, Rongta) — Жөндөөлөр → Тараза. Кассадагы негизги тартуу (COM-тараза) буга кирбейт жана акысыз бойдон калат.", "Sends weighed products with PLU codes to network scales (Shtrikh-M, Rongta) — Settings → Scales. Basic weighing at the till (COM scale) isn't included and stays free.", "Tartılı ürünleri PLU kodlarıyla ağ tartılarına (Shtrikh-M, Rongta) gönderme — Ayarlar → Tartılar. Kasadaki temel tartım (COM üzerinden) buna dahil değildir ve ücretsiz kalır.", "PLU kodli vaznli mahsulotlarni tarmoq tarozilariga (Shtrih-M, Rongta) yuborish — Sozlamalar → Tarozilar. Kassadagi oddiy tortish (COM orqali) bunga kirmaydi va bepulligicha qoladi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);

        if (!unlocked)
        {
            var unlockButton = new Button
            {
                Classes = { "btn-secondary" },
                Content = TariffGate.PackPriceLabel, // 2026-10-05: функция «Стандарта» на «Старте» — 1500 сом + 400 сом/мес
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            unlockButton.Click += ScalesUnlockButton_Click;
            content.Children.Add(unlockButton);
        }
        else
        {
            content.Children.Add(new TextBlock
            {
                Text = Tr.T("Открыто на вкладке Настройки → Весы.", "Жөндөөлөр → Тараза бетинде ачык.",
                    "Available on the Settings → Scales tab.", "Ayarlar → Tartılar sekmesinde açık.", "Sozlamalar → Tarozilar bo'limida ochiq."),
                FontSize = 11,
                FontStyle = FontStyle.Italic,
                Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        return new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };
    }

    private void ScalesUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Весы", "Тараза", "Scales", "Tartı", "Tarozi"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "scales", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.ScalesUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("scales");
        RefreshScalesCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Доп. услуга разблокирована", "Кошумча кызмат ачылды", "Add-on unlocked", "Ek hizmet kilidi açıldı", "Qo'shimcha xizmat ochildi"),
            Tr.T(
                "Отправка на весы теперь доступна на вкладке Настройки → Весы.",
                "Таразага жөнөтүү эми Жөндөөлөр → Тараза бетинде жеткиликтүү.", "Sending to scales is now available in Settings → Scales.", "Tartıya gönderme artık Ayarlar → Tartılar sekmesinde kullanılabilir.", "Tarozilarga yuborish endi Sozlamalar → Tarozilar bo'limida mavjud."),
            PosAlertKind.Success);
    }

    private Border? _warehouseAnalyticsCard;

    /// <summary>Замена прежней статичной карточки-заглушки "Расширенная аналитика" ("Скоро") —
    /// теперь это настоящая, рабочая доп. услуга: аналитика склада (KPI, топ по стоимости,
    /// разбивка по категориям — вкладка WarehouseWindow.AnalyticsTabItem) уже существует и
    /// работает, просто закрыта серийным номером (см. WarehouseWindow.RefreshAnalyticsLock).
    /// Эта карточка — просто вход в неё, тот же приём, что и BuildVoiceControlCard, но без шага
    /// скачивания: разблокировать/открыть, состояний всего два.</summary>
    private void RefreshWarehouseAnalyticsCard()
    {
        if (_warehouseAnalyticsCard is not null)
            ExtrasWrapPanel.Children.Remove(_warehouseAnalyticsCard);

        _warehouseAnalyticsCard = BuildWarehouseAnalyticsCard();
        ExtrasWrapPanel.Children.Add(_warehouseAnalyticsCard);
    }

    private Border BuildWarehouseAnalyticsCard()
    {
        var unlocked = UserPreferences.Instance.WarehouseAnalyticsUnlocked;

        var titleText = new TextBlock
        {
            Text = Tr.T("📊 Аналитика склада", "📊 Складдын аналитикасы", "📊 Warehouse analytics", "📊 Depo analitiği", "📊 Ombor analitikasi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeKey = unlocked ? "BrushAccent" : "BrushWarning";
        var badge = new Border
        {
            Background = ThemeBrush($"{badgeKey}Soft", Brushes.Transparent),
            BorderBrush = ThemeBrush(badgeKey, Brushes.Orange),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = unlocked ? Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi") : Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush(badgeKey, Brushes.DarkOrange),
            },
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "Показатели по остаткам и продажам, топ товаров по стоимости и разбивка по категориям — вкладка «Аналитика» в окне склада.",
                "Калдыктар жана сатуулар боюнча көрсөткүчтөр, наркы боюнча топ товарлар жана категориялар боюнча бөлүштүрүү — склад терезесиндеги «Аналитика» өтмөгү.", "Stock and sales metrics, top products by value and a category breakdown — the “Analytics” tab in the warehouse window.", "Stok ve satış göstergeleri, değere göre en iyi ürünler ve kategori dağılımı — depo penceresindeki «Analiz» sekmesi.", "Qoldiq va sotuv ko'rsatkichlari, qiymati bo'yicha top mahsulotlar va kategoriyalar kesimi — ombor oynasidagi «Analitika» bo'limi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);

        if (!unlocked)
        {
            var unlockButton = new Button
            {
                Classes = { "btn-secondary" },
                Content = Tr.T("🔒 Активировать — 1500 сом", "🔒 Активдештирүү — 1500 сом", "🔒 Activate — 1500 som", "🔒 Etkinleştir — 1500 som", "🔒 Faollashtirish — 1500 so'm"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            unlockButton.Click += WarehouseAnalyticsUnlockButton_Click;
            content.Children.Add(unlockButton);
        }
        else
        {
            var openButton = new Button
            {
                Classes = { "btn-primary" },
                Content = Tr.T("📊 Открыть аналитику склада", "📊 Склад аналитикасын ачуу", "📊 Open warehouse analytics", "📊 Depo analitiğini aç", "📊 Ombor analitikasini ochish"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            openButton.Click += (_, _) =>
            {
                // Программа владельца (2026-09-27): аналитика склада — вкладка «Склад» раздела
                // «Аналитика». В кассе и на тарифе «Старт» — отдельное окно склада, как было.
                if (TopLevel.GetTopLevel(this) is Window { Owner: OwnerShellWindow shell } && shell.OpenAnalyticsStock())
                    return;

                var warehouseWindow = App.GetRequiredService<WarehouseWindow>();
                warehouseWindow.AnalyticsOnly = true;
                warehouseWindow.Show(TopLevel.GetTopLevel(this) as Window);
            };
            content.Children.Add(openButton);
        }

        return new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };
    }

    private void WarehouseAnalyticsUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Аналитика склада", "Складдын аналитикасы", "Warehouse analytics", "Depo analitiği", "Ombor analitikasi"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "analytics", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.WarehouseAnalyticsUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("analytics");
        RefreshWarehouseAnalyticsCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Аналитика склада активирована", "Складдын аналитикасы иштетилди", "Warehouse analytics activated", "Depo analitiği etkinleştirildi", "Ombor analitikasi faollashtirildi"),
            Tr.T(
                "Откройте окно склада — появится вкладка «Аналитика» с показателями по остаткам, продажам и категориям.",
                "Склад терезесин ачыңыз — калдыктар, сатуулар жана категориялар боюнча көрсөткүчтөрү бар «Аналитика» өтмөгү пайда болот.", "Open the warehouse window — it now has an “Analytics” tab with stock, sales and category metrics.", "Depo penceresini açın — stok, satış ve kategori göstergelerini içeren «Analiz» sekmesi görünecek.", "Ombor oynasini oching — qoldiqlar, sotuvlar va kategoriyalar bo'yicha ko'rsatkichlari bilan «Analitika» bo'limi paydo bo'ladi."),
            PosAlertKind.Success);
    }

    private Border? _priceTagEditorCard;
    private Border? _bulkPriceTagCard;

    /// <summary>Тот же приём, что и BuildWarehouseAnalyticsCard — карточка-вход в уже
    /// реализованную и закрытую серийником функцию (см. WarehouseWindow.
    /// EnsurePriceTagEditorUnlocked, PriceTagPrintDialog/BulkPriceTagPrintDialog), а не
    /// отдельная копия логики разблокировки. Раньше эта доп. услуга была закрыта серийником
    /// ТОЛЬКО в момент клика по кнопке "Ценник"/"Массовая печать ценников" в складе — в
    /// Маркетплейсе её не было видно вообще, из-за чего казалось, что её "не перенесли". Две
    /// отдельные карточки (редактор + массовая печать) с двумя НЕЗАВИСИМЫМИ флагами —
    /// PriceTagEditorUnlocked и BulkPriceTagUnlocked (2026-09-04, по явной просьбе пользователя:
    /// раньше обе карточки включали один и тот же флаг, из-за чего активация одной сразу
    /// открывала и вторую — теперь активировать нужно каждую отдельно).</summary>
    private void RefreshPriceTagEditorCard()
    {
        if (_priceTagEditorCard is not null)
            ExtrasWrapPanel.Children.Remove(_priceTagEditorCard);
        if (_bulkPriceTagCard is not null)
            ExtrasWrapPanel.Children.Remove(_bulkPriceTagCard);

        _priceTagEditorCard = BuildPriceTagFeatureCard(
            title: Tr.T("💲 Редактор ценников", "💲 Баа белгиси редактору", "💲 Price tag editor", "💲 Fiyat etiketi düzenleyici", "💲 Narx yorliqlari muharriri"),
            description: Tr.T(
                "6 готовых шаблонов ценника (простой, со штрих-кодом, акционные, подробный, с QR-кодом) для одного товара — вкладка «Склад» → «Товары» → «Ценник».",
                "Бир товар үчүн баа белгисинин 6 даяр шаблону (жөнөкөй, штрих-код менен, акциялык, толук, QR-код менен) — «Кампа» → «Товарлар» → «Баа белгиси».", "6 ready-made price tag templates (simple, with barcode, promo, detailed, with QR code) for a single product — “Warehouse” → “Products” → “Price tag”.", "Tek ürün için 6 hazır fiyat etiketi şablonu (basit, barkodlu, promosyonlu, detaylı, QR kodlu) — «Depo» → «Ürünler» → «Fiyat etiketi».", "Bitta mahsulot uchun 6 ta tayyor narx yorlig'i shabloni (oddiy, shtrix-kodli, aksiya, batafsil, QR-kodli) — «Ombor» → «Mahsulotlar» → «Narx yorlig'i»."),
            openButtonLabel: Tr.T("💲 Открыть склад", "💲 Складды ачуу", "💲 Open warehouse", "💲 Depoyu aç", "💲 Omborni ochish"),
            openAction: () =>
            {
                var warehouseWindow = App.GetRequiredService<WarehouseWindow>();
                warehouseWindow.Show(TopLevel.GetTopLevel(this) as Window);
                return System.Threading.Tasks.Task.CompletedTask;
            },
            unlocked: UserPreferences.Instance.PriceTagEditorUnlocked,
            onUnlockClick: PriceTagEditorUnlockButton_Click);
        _bulkPriceTagCard = BuildPriceTagFeatureCard(
            title: Tr.T("🏷 Массовая печать ценников", "🏷 Баа белгилерин топтоп басып чыгаруу", "🏷 Bulk price tag printing", "🏷 Toplu fiyat etiketi baskısı", "🏷 Narx yorliqlarini ommaviy chop etish"),
            description: Tr.T(
                "Печать ценников сразу для нескольких товаров за один проход — на термопринтер или обычный A4, вместо печати по одному — вкладка «Склад» → «Товары».",
                "Бир нече товардын баа белгилерин бир жолу басып чыгаруу — термопринтерге же кадимки A4 барагына, бирден басып отурбай — «Кампа» → «Товарлар» өтмөгү.", "Print price tags for several products in one go — on a thermal printer or plain A4 — instead of one at a time. “Warehouse” → “Products”.", "Birden fazla ürünün fiyat etiketini tek seferde yazdırma — termal yazıcıya veya normal A4'e, tek tek yazdırmak yerine — «Depo» → «Ürünler».", "Bir nechta mahsulotning narx yorliqlarini bir yo'la chop etish — termoprinterda yoki oddiy A4 printerda, birma-bir emas — «Ombor» → «Mahsulotlar»."),
            openButtonLabel: Tr.T("🏷 Открыть массовую печать", "🏷 Топтоп басууну ачуу", "🏷 Open bulk printing", "🏷 Toplu baskıyı aç", "🏷 Ommaviy chop etishni ochish"),
            openAction: () => BulkPriceTagPrintDialog.Open(TopLevel.GetTopLevel(this) as Window),
            unlocked: UserPreferences.Instance.BulkPriceTagUnlocked,
            onUnlockClick: BulkPriceTagUnlockButton_Click);

        ExtrasWrapPanel.Children.Add(_priceTagEditorCard);
        ExtrasWrapPanel.Children.Add(_bulkPriceTagCard);
    }

    private Border? _analyticsExportCard;

    /// <summary>«Выгрузка аналитики» (2026-09-22): отчёт по продажам и складу в Excel и Word
    /// с графиками. Файлы пишутся без Excel и Word на компьютере — на кассе офиса обычно нет.</summary>
    private void RefreshAnalyticsExportCard()
    {
        if (_analyticsExportCard is not null)
            ExtrasWrapPanel.Children.Remove(_analyticsExportCard);

        _analyticsExportCard = BuildPriceTagFeatureCard(
            title: Tr.T("Выгрузка аналитики в Excel и Word", "Аналитиканы Excel жана Word файлына чыгаруу",
                        "Analytics export to Excel and Word", "Analitiği Excel ve Word'e aktarma",
                        "Tahlilni Excel va Word'ga eksport qilish"),
            description: Tr.T(
                "Отчёт по продажам и складу за выбранный период: выручка, чеки, средний чек, скидки, возвраты, топ товаров и что пора заказать. С графиками — выручка по дням, топ товаров, структура за период. Excel и Word не нужны: файл собирается самой кассой.",
                "Тандалган мезгил боюнча сатуу жана кампа отчёту: түшүм, чектер, орточо чек, арзандатуулар, кайтаруулар, топ товарлар жана эмнеге буйрутма берүү керектиги. Графиктери менен — күндөр боюнча түшүм, топ товарлар, мезгилдин түзүмү. Excel жана Word керек эмес: файлды касса өзү түзөт.", "Sales and stock report for the selected period: revenue, receipts, average receipt, discounts, returns, top products and what to reorder. With charts: revenue by day, top products, period breakdown. No Excel or Word needed — the till builds the file itself.", "Seçilen dönem için satış ve depo raporu: ciro, fişler, ortalama fiş, indirimler, iadeler, en çok satanlar ve sipariş edilmesi gerekenler. Grafiklerle — günlük ciro, en çok satanlar, dönemin yapısı. Excel ve Word gerekmez: dosyayı kasa kendisi oluşturur.", "Tanlangan davr bo'yicha sotuv va ombor hisoboti: tushum, cheklar, o'rtacha chek, chegirmalar, qaytarishlar, top mahsulotlar va nimani buyurtma qilish vaqti kelgani. Grafiklar bilan — kunlar bo'yicha tushum, top mahsulotlar, davr tarkibi. Excel va Word shart emas: faylni kassaning o'zi tayyorlaydi."),
            openButtonLabel: Tr.T("Где выгрузить", "Кайдан чыгарса болот", "Where to export",
                                  "Nereden aktarılır", "Qayerda eksport qilinadi"),
            openAction: () =>
            {
                PosAlertDialog.Show(
                    TopLevel.GetTopLevel(this) as Window,
                    Tr.T("Где выгрузить", "Кайдан чыгарса болот", "Where to export", "Nereden aktarılır", "Qayerda eksport qilinadi"),
                    Tr.T("Откройте «Отчёты», выберите период и нажмите «Excel» или «Word» в верхней строке.",
                         "«Отчёттор» бөлүмүн ачып, мезгилди тандап, жогорку саптагы «Excel» же «Word» баскычын басыңыз.", "Open “Reports”, select a period and click “Excel” or “Word” in the top bar.", "«Raporlar»ı açın, dönemi seçin ve üst satırdaki «Excel» veya «Word» düğmesine basın.", "«Hisobotlar»ni oching, davrni tanlang va yuqori qatordagi «Excel» yoki «Word» tugmasini bosing."),
                    PosAlertKind.Info);
                return System.Threading.Tasks.Task.CompletedTask;
            },
            unlocked: TariffGate.CanUseAnalyticsExport,
            onUnlockClick: AnalyticsExportUnlockButton_Click,
            priceLabel: TariffGate.PackPriceLabel);

        ExtrasWrapPanel.Children.Add(_analyticsExportCard);
    }

    private void AnalyticsExportUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var name = Tr.T("Выгрузка аналитики", "Аналитиканы чыгаруу", "Analytics export",
                        "Analitik aktarımı", "Tahlilni eksport qilish");
        var serial = SerialActivationDialog.Show(owner, name);
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "export", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number",
                     "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.",
                     "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.",
                     "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.AnalyticsExportUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("export");
        RefreshAnalyticsExportCard();

        PosAlertDialog.Show(owner, name,
            Tr.T("Функция активирована.", "Функция иштетилди.", "Feature activated.",
                 "Özellik etkinleştirildi.", "Funksiya faollashtirildi."),
            PosAlertKind.Success);
    }

    // 2026-10-05, владелец: «всё, что есть на тарифе «Стандарт», можно подключить и на «Старт», но абонплата за каждую услугу
    // добавляется по 400 сом + подключение (активация) 1500 сом». Карточки пакетов — только на «Старте» (на «Стандарте» всё
    // уже входит в тариф). Подключение — серийным ключом, как остальные доп. функции (LicenseKeys, slug = TariffGate.Packs).
    private readonly List<Border> _packCards = new();

    internal static IEnumerable<(string Slug, string Title, string Description)> StandardPacks()
    {
        yield return (TariffGate.Packs.Ai,
            Tr.T("ИИ-советник и ИИ в боте", "ИИ-кеңешчи жана боттогу ИИ", "AI advisor and the bot's AI", "Yapay zekâ danışmanı ve bottaki yapay zekâ", "SI maslahatchi va botdagi SI"),
            Tr.T("Советы по продажам и закупкам в программе владельца, ИИ в Telegram-боте и голосовом управлении, поиск фото товаров и поиск в интернете.",
                "Ээсинин программасында сатуу жана сатып алуу боюнча кеңештер, Telegram-боттогу жана үн менен башкаруудагы ИИ, товардын сүрөтүн жана интернеттен издөө.",
                "Sales and purchasing advice in the owner app, AI in the Telegram bot and voice control, product photo search and web search.",
                "Sahip programında satış ve satın alma tavsiyeleri, Telegram botunda ve sesli kontrolde yapay zekâ, ürün fotoğrafı ve internet araması.",
                "Egasi dasturida sotuv va xarid bo'yicha maslahatlar, Telegram-botda va ovozli boshqaruvda SI, mahsulot rasmi va internetda qidirish."));
        yield return (TariffGate.Packs.Clients,
            Tr.T("Клиенты, воронка и WhatsApp", "Кардарлар, воронка жана WhatsApp", "Customers, funnel and WhatsApp", "Müşteriler, huni ve WhatsApp", "Mijozlar, voronka va WhatsApp"),
            Tr.T("База клиентов с долгами и скидками, воронка продаж, переписка в WhatsApp из программы.",
                "Карыздары жана арзандатуулары менен кардарлардын базасы, сатуу воронкасы, программадан WhatsApp'та жазышуу.",
                "A customer base with debts and discounts, a sales funnel, WhatsApp chats from the app.",
                "Borç ve indirimli müşteri tabanı, satış hunisi, programdan WhatsApp yazışmaları.",
                "Qarz va chegirmalar bilan mijozlar bazasi, savdo voronkasi, dasturdan WhatsApp yozishmalari."));
        yield return (TariffGate.Packs.SalesAnalytics,
            Tr.T("Продажи и аналитика", "Сатуулар жана аналитика", "Sales and analytics", "Satışlar ve analiz", "Sotuvlar va analitika"),
            Tr.T("Продажи, финансы, аналитика, ABC-анализ, прибыль и деньги, продажи в убыток, план продаж.",
                "Сатуулар, каржы, аналитика, ABC-анализ, пайда жана акча, зыян менен сатуулар, сатуу планы.",
                "Sales, finance, analytics, ABC analysis, profit and cash, sales at a loss, sales plan.",
                "Satışlar, finans, analiz, ABC analizi, kâr ve nakit, zararına satışlar, satış planı.",
                "Sotuvlar, moliya, analitika, ABC tahlil, foyda va pul, zarariga sotuvlar, sotuv rejasi."));
        yield return (TariffGate.Packs.Restock,
            Tr.T("Пополнение и сроки годности", "Толуктоо жана жарактуулук мөөнөттөрү", "Restock and expiry dates", "Stok yenileme ve son kullanma tarihleri", "To'ldirish va yaroqlilik muddatlari"),
            Tr.T("Что пора заказать и у каких товаров заканчивается срок годности.", "Эмнеге буйрутма берүү керек жана кайсы товарлардын мөөнөтү бүтүп баратат.",
                "What to reorder and which products are about to expire.", "Neyin sipariş edilmesi gerektiği ve hangi ürünlerin son kullanma tarihinin dolmak üzere olduğu.",
                "Nimani buyurtma qilish kerak va qaysi mahsulotlarning muddati tugayapti."));
        yield return (TariffGate.Packs.Salary,
            Tr.T("Зарплата сотрудников", "Кызматкерлердин эмгек акысы", "Staff salaries", "Personel maaşları", "Xodimlar ish haqi"),
            Tr.T("Расчёт зарплаты сотрудников по продажам и сменам.", "Кызматкерлердин эмгек акысын сатуу жана сменалар боюнча эсептөө.",
                "Staff salary calculation by sales and shifts.", "Satış ve vardiyalara göre personel maaşı hesaplama.", "Xodimlar ish haqini sotuv va smenalar bo'yicha hisoblash."));
        yield return (TariffGate.Packs.Debts,
            Tr.T("Долги и отложенные чеки", "Карыздар жана кийинкиге калтырылган чектер", "Debts and parked receipts", "Borçlar ve bekletilen fişler", "Qarzlar va kechiktirilgan cheklar"),
            Tr.T("Оплата долгов покупателей на кассе и отложенные чеки.", "Кассада сатып алуучулардын карызын төлөө жана кийинкиге калтырылган чектер.",
                "Customer debt payments at the till and parked receipts.", "Kasada müşteri borç ödemeleri ve bekletilen fişler.", "Kassada xaridorlar qarzini to'lash va kechiktirilgan cheklar."));
        yield return (TariffGate.Packs.Service,
            Tr.T("NurCRM, база знаний и поддержка", "NurCRM, билим базасы жана колдоо", "NurCRM, knowledge base and support", "NurCRM, bilgi bankası ve destek", "NurCRM, bilimlar bazasi va yordam"),
            Tr.T("Сайт NurCRM внутри программы, база знаний, удалённая тех. поддержка и журнал ошибок.",
                "Программанын ичинде NurCRM сайты, билим базасы, алыстан тех колдоо жана каталар журналы.",
                "The NurCRM site inside the app, a knowledge base, remote support and an error log.",
                "Program içinde NurCRM sitesi, bilgi bankası, uzaktan destek ve hata günlüğü.",
                "Dastur ichida NurCRM sayti, bilimlar bazasi, masofaviy texnik yordam va xatolar jurnali."));
    }

    private void RefreshStandardPackCards()
    {
        foreach (var card in _packCards)
            ExtrasWrapPanel.Children.Remove(card);
        _packCards.Clear();
        if (!TariffGate.IsStartTariff)
            return;

        foreach (var (slug, title, description) in StandardPacks())
        {
            var card = BuildPriceTagFeatureCard(title, description,
                openButtonLabel: Tr.T("✓ Подключено — разделы в меню", "✓ Туташтырылды — бөлүмдөр менюда", "✓ Connected — sections are in the menu",
                    "✓ Bağlandı — bölümler menüde", "✓ Ulandi — bo'limlar menyuda"),
                openAction: () => System.Threading.Tasks.Task.CompletedTask,
                unlocked: TariffGate.HasPack(slug),
                onUnlockClick: (_, _) => UnlockPack(slug, title),
                priceLabel: TariffGate.PackPriceLabel);
            _packCards.Add(card);
            ExtrasWrapPanel.Children.Add(card);
        }
    }

    private void UnlockPack(string slug, string title)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, title);
        if (serial == null)
            return;
        _ = UnlockPackAsync(owner, slug, title, serial);
    }

    /// <summary>2026-10-05, ТЗ ч.13, п. 2.3: ключ — сначала на сервер (POST users/company/features/activate/): там функция
    /// включается всей компании, на всех устройствах. Сервер ключа не знает или нет связи — проверка ключа кассой, как раньше.</summary>
    private async Task UnlockPackAsync(Window? owner, string slug, string title, string serial)
    {
        try
        {
            var data = await App.GetRequiredService<NurMarketKassa.Services.NurMarketApiClient>().RequestAsync(System.Net.Http.HttpMethod.Post,
                "api/users/company/features/activate/", new Dictionary<string, object?> { ["key"] = serial.Trim() }, null,
                CancellationToken.None, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            var code = data.ValueKind == System.Text.Json.JsonValueKind.Object && data.TryGetProperty("code", out var c) ? c.GetString() : null;
            PosLogger.Log($"Маркетплейс: ключ принят сервером — функция компании {code}.", "INFO");
            await CompanyInfoService.RefreshAsync(App.AuthApi).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Маркетплейс: сервер не включил функцию по ключу ({ex.Message}) — проверка ключа кассой.", "WARNING");
        }
        if (TariffGate.HasPack(slug) && !UserPreferences.Instance.UnlockedPacks.Contains(slug, StringComparer.OrdinalIgnoreCase))
        {
            // Включено сервером для всей компании.
            TariffGate.RaisePacksChanged();
            RefreshStandardPackCards();
            PosAlertDialog.Show(owner, title,
                Tr.T("Функция подключена для всей компании — на всех кассах и в программе владельца.", "Функция бүт компанияга туташтырылды — бардык кассаларда жана ээсинин программасында.",
                     "Feature connected for the whole company — on every till and in the owner app.", "Özellik tüm şirket için bağlandı — tüm kasalarda ve sahip programında.",
                     "Funksiya butun kompaniya uchun ulandi — barcha kassalarda va egasi dasturida."),
                PosAlertKind.Success);
            return;
        }

        if (!TryValidateSerial(serial, slug, out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number",
                     "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.",
                     "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.",
                     "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        var prefs = UserPreferences.Instance;
        if (!prefs.UnlockedPacks.Contains(slug, StringComparer.OrdinalIgnoreCase))
            prefs.UnlockedPacks.Add(slug);
        if (isPermanent)
            prefs.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor(slug);
        PosLogger.Log($"Маркетплейс: подключён пакет «Стандарта» {slug} ({(isPermanent ? "постоянный ключ" : "тестовый доступ")}).", "INFO");
        TariffGate.RaisePacksChanged();
        RefreshStandardPackCards();

        PosAlertDialog.Show(owner, title,
            Tr.T("Функция подключена. Разделы появились в меню.", "Функция туташтырылды. Бөлүмдөр менюда пайда болду.", "Feature connected. The sections are now in the menu.",
                 "Özellik bağlandı. Bölümler menüde göründü.", "Funksiya ulandi. Bo'limlar menyuda paydo bo'ldi."),
            PosAlertKind.Success);
    }

    private Border? _telegramBotCard;

    /// <summary>«Телеграм-бот владельца» (2026-09-22). Правило владельца: новые функции входят
    /// в «Стандарт», а на «Старт» покупаются здесь. Поэтому на «Стандарт» карточка показывает
    /// функцию уже открытой — платить второй раз за то, что входит в тариф, нельзя.</summary>
    private void RefreshTelegramBotCard()
    {
        if (_telegramBotCard is not null)
            ExtrasWrapPanel.Children.Remove(_telegramBotCard);

        _telegramBotCard = BuildPriceTagFeatureCard(
            title: Tr.T("Телеграм-бот владельца", "Ээсинин телеграм-боту", "Owner's Telegram bot",
                        "Sahibin Telegram botu", "Egasi uchun Telegram-bot"),
            description: Tr.T(
                "Сводка по закрытой смене приходит вам в Telegram. Бот отвечает на команды: выручка за день и неделю, топ товаров, что пора заказать, что заканчивается, список должников со ссылками для напоминания.",
                "Жабылган смена боюнча жыйынтык сизге Telegram'га келет. Бот буйруктарга жооп берет: күндүк жана жумалык түшүм, топ товарлар, эмнеге буйрутма берүү керек, эмне түгөнүп баратат, эскертүү шилтемелери менен карызкорлордун тизмеси.", "You get a summary of each closed shift in Telegram. The bot answers commands: revenue for the day and week, top products, what to reorder, what's running low, and a list of debtors with reminder links.", "Kapanan vardiyanın özeti size Telegram'dan gelir. Bot şu komutlara yanıt verir: günlük ve haftalık ciro, en çok satanlar, sipariş edilmesi gerekenler, tükenmek üzere olanlar, hatırlatma bağlantılı borçlu listesi.", "Yopilgan smena bo'yicha hisobot sizga Telegram'da keladi. Bot buyruqlarga javob beradi: kunlik va haftalik tushum, top mahsulotlar, nimani buyurtma qilish kerak, nima tugayapti, eslatma havolalari bilan qarzdorlar ro'yxati."),
            openButtonLabel: Tr.T("Настроить бота", "Ботту жөндөө", "Set up the bot",
                                  "Botu ayarla", "Botni sozlash"),
            openAction: () =>
            {
                var dialog = new NurMarketKassa.AvaloniaHost.Views.TelegramBotSetupWindow();
                return dialog.ShowDialog(TopLevel.GetTopLevel(this) as Window);
            },
            unlocked: TariffGate.CanUseTelegramBot,
            onUnlockClick: TelegramBotUnlockButton_Click,
            priceLabel: TariffGate.PackPriceLabel);

        ExtrasWrapPanel.Children.Add(_telegramBotCard);
    }

    private void TelegramBotUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var name = Tr.T("Телеграм-бот владельца", "Ээсинин телеграм-боту", "Owner's Telegram bot",
                        "Sahibin Telegram botu", "Egasi uchun Telegram-bot");
        var serial = SerialActivationDialog.Show(owner, name);
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "telegram", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number",
                     "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.",
                     "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.",
                     "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.TelegramBotUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("telegram");
        RefreshTelegramBotCard();

        PosAlertDialog.Show(owner, name,
            Tr.T("Функция активирована.", "Функция иштетилди.", "Feature activated.",
                 "Özellik etkinleştirildi.", "Funksiya faollashtirildi."),
            PosAlertKind.Success);
    }

    private Border? _shiftAnalyticsCard;

    /// <summary>«Расширенные итоги смены» (2026-09-22): возвраты, списания, расход, оплата
    /// долгов и скидки в окне смены, X/Z-отчёте и сводке в Telegram.</summary>
    private void RefreshShiftAnalyticsCard()
    {
        if (_shiftAnalyticsCard is not null)
            ExtrasWrapPanel.Children.Remove(_shiftAnalyticsCard);

        _shiftAnalyticsCard = BuildPriceTagFeatureCard(
            title: Tr.T("Расширенные итоги смены", "Кеңейтилген смена жыйынтыктары",
                        "Extended shift totals", "Genişletilmiş vardiya toplamları",
                        "Kengaytirilgan smena yakunlari"),
            description: Tr.T(
                "Возвраты, списания, расход, оплата долгов и скидки (в том числе оплаченные бонусами) — в окне смены, в X/Z-отчёте и в сводке закрытия смены. Считаются самой кассой, поэтому доступны и без интернета.",
                "Кайтаруулар, эсептен чыгаруулар, чыгымдар, карыз төлөмдөрү жана арзандатуулар (бонус менен төлөнгөндөрү да) — смена терезесинде, X/Z-отчётто жана смена жабылгандагы жыйынтыкта. Аларды касса өзү эсептейт, ошондуктан интернетсиз да көрүнөт.", "Returns, write-offs, expenses, debt payments and discounts (including those paid with points) — in the shift window, the X/Z report and the shift closing summary. Calculated by the till itself, so they're available offline too.", "İadeler, stok düşümleri, giderler, borç ödemeleri ve indirimler (bonusla ödenenler dahil) — vardiya penceresinde, X/Z raporunda ve vardiya kapanış özetinde. Kasa bunları kendisi hesaplar, bu yüzden internetsiz de kullanılabilir.", "Qaytarishlar, hisobdan chiqarishlar, xarajatlar, qarz to'lovlari va chegirmalar (jumladan, bonuslar bilan to'langanlari) — smena oynasida, X/Z-hisobotda va smena yopilishi hisobotida. Kassaning o'zi hisoblaydi, shuning uchun internetsiz ham ishlaydi."),
            openButtonLabel: Tr.T("Открыть смены", "Сменаларды көрүү", "View shifts",
                                  "Vardiyaları aç", "Smenalar bo'limini ochish"),
            openAction: () =>
            {
                PosAlertDialog.Show(
                    TopLevel.GetTopLevel(this) as Window,
                    Tr.T("Где смотреть", "Кайдан көрсө болот", "Where to look",
                         "Nereye bakmalı", "Qayerda ko'rish mumkin"),
                    Tr.T("Итоги открываются из меню кассы: «Смены» → выбрать смену. Те же цифры печатаются в X/Z-отчёте и приходят в Telegram при закрытии смены.",
                         "Жыйынтыктар касса менюсунан ачылат: «Смена» → сменаны тандаңыз. Ошол эле сандар X/Z-отчётто басылат жана смена жабылганда Telegram'га келет.", "Totals open from the till menu: “Shifts” → select a shift. The same figures are printed in the X/Z report and sent to Telegram when the shift is closed.", "Özet kasa menüsünden açılır: «Vardiyalar» → vardiyayı seçin. Aynı rakamlar X/Z raporunda yazdırılır ve vardiya kapanınca Telegram'a gelir.", "Yakunlar kassa menyusidan ochiladi: «Smenalar» → smenani tanlang. Xuddi shu raqamlar X/Z-hisobotda chop etiladi va smena yopilganda Telegram'ga keladi."),
                    PosAlertKind.Info);
                return System.Threading.Tasks.Task.CompletedTask;
            },
            unlocked: TariffGate.CanUseShiftAnalytics,
            onUnlockClick: ShiftAnalyticsUnlockButton_Click,
            priceLabel: TariffGate.PackPriceLabel);

        ExtrasWrapPanel.Children.Add(_shiftAnalyticsCard);
    }

    private void ShiftAnalyticsUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var name = Tr.T("Расширенные итоги смены", "Кеңейтилген смена жыйынтыктары",
                        "Extended shift totals", "Genişletilmiş vardiya toplamları",
                        "Kengaytirilgan smena yakunlari");
        var serial = SerialActivationDialog.Show(owner, name);
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "shiftstats", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number",
                     "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.",
                     "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.",
                     "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.ShiftAnalyticsUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("shiftstats");
        RefreshShiftAnalyticsCard();

        PosAlertDialog.Show(owner, name,
            Tr.T("Функция активирована.", "Функция иштетилди.", "Feature activated.",
                 "Özellik etkinleştirildi.", "Funksiya faollashtirildi."),
            PosAlertKind.Success);
    }

    private Border? _labelEditorCard;

    /// <summary>Отдельный, независимый от «Склада» вход в редактор шаблона этикетки
    /// (2026-09-06, по просьбе пользователя — раньше редактор открывался только изнутри
    /// диалога печати этикетки для конкретного товара, Склад → строка товара → «Печать» →
    /// «Редактор этикетки»). 2026-09-07: раньше был бесплатным, по явной просьбе пользователя
    /// стал платным — тот же паттерн, что и у редактора/массовой печати ценников выше.</summary>
    private void RefreshLabelEditorCard()
    {
        if (_labelEditorCard is not null)
            ExtrasWrapPanel.Children.Remove(_labelEditorCard);

        _labelEditorCard = BuildPriceTagFeatureCard(
            title: Tr.T("🎨 Редактор этикетки", "🎨 Этикетка редактору", "🎨 Label editor", "🎨 Etiket düzenleyici", "🎨 Yorliq muharriri"),
            description: Tr.T(
                "Расположение штрих-кода, названия, цены, артикула, единицы измерения и магазина на этикетке — с перетаскиванием и настройкой каждого элемента.",
                "Штрих-коддун, аталыштын, баанын, артикулдун, өлчөм бирдигинин жана дүкөндүн этикеткадагы жайгашуусу — ар бир элементти сүйрөп жылдырууга жана жөндөөгө болот.", "Place the barcode, name, price, SKU, unit and store name on the label — drag and configure each element.", "Etiketteki barkod, ad, fiyat, stok kodu, ölçü birimi ve mağaza adının yerleşimi — her öğe sürüklenip ayrı ayrı ayarlanabilir.", "Yorliqdagi shtrix-kod, nom, narx, artikul, o'lchov birligi va do'kon nomining joylashuvi — har bir elementni sudrab ko'chirish va sozlash imkoni bilan."),
            openButtonLabel: Tr.T("🎨 Открыть редактор", "🎨 Редакторду ачуу", "🎨 Open editor", "🎨 Düzenleyiciyi aç", "🎨 Muharrirni ochish"),
            openAction: () =>
            {
                var editor = new LabelTemplateEditorDialog();
                return editor.ShowDialog(TopLevel.GetTopLevel(this) as Window);
            },
            unlocked: UserPreferences.Instance.LabelEditorUnlocked,
            onUnlockClick: LabelEditorUnlockButton_Click);

        ExtrasWrapPanel.Children.Add(_labelEditorCard);
    }

    private void LabelEditorUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Редактор этикетки", "Этикетка редактору", "Label editor", "Etiket düzenleyici", "Yorliq muharriri"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "labeleditor", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.LabelEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("labeleditor");
        RefreshLabelEditorCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Редактор этикетки активирован", "Этикетка редактору иштетилди", "Label editor activated", "Etiket düzenleyici etkinleştirildi", "Yorliq muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступен редактор этикеток — Маркетплейс → Доп. функции или Склад → Товары.",
                "Эми этикетка редактору жеткиликтүү — Маркетплейс → Кошумча функциялар же Кампа → Товарлар.", "The label editor is now available — Marketplace → Extras or Warehouse → Products.", "Etiket düzenleyici artık kullanılabilir — Mağaza → Ek özellikler veya Depo → Ürünler.", "Endi yorliq muharriri mavjud — Bozor → Qo'shimcha funksiyalar yoki Ombor → Mahsulotlar."),
            PosAlertKind.Success);
    }

    private Border? _languagePackCard;

    /// <summary>Карточка «Языковой пакет» (2026-09-07, по решению владельца): английский, турецкий
    /// и узбекский интерфейс стали платным дополнением, по умолчанию доступны русский и кыргызский.
    /// Словари встроены в кассу, поэтому активация серийным номером мгновенная (см. LanguagePackGate);
    /// платные языки появляются в Настройки → Экран сразу после активации.</summary>
    private void RefreshLanguagePackCard()
    {
        if (_languagePackCard is not null)
            ExtrasWrapPanel.Children.Remove(_languagePackCard);

        var unlocked = LanguagePackGate.IsUnlocked;

        var titleText = new TextBlock
        {
            Text = Tr.T("🌐 Языковой пакет", "🌐 Тил пакети", "🌐 Language pack", "🌐 Dil paketi", "🌐 Til paketi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badge = MakeLayoutBadge(
            unlocked ? Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi") : Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"),
            unlocked ? "BrushAccent" : "BrushWarning");

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "English, Türkçe, O'zbekcha — дополнительные языки интерфейса кассы. По умолчанию доступны русский и кыргызский. После активации языки появятся в Настройки → Экран.",
                "English, Türkçe, O'zbekcha — кассанын кошумча интерфейс тилдери. Демейки боюнча орусча жана кыргызча жеткиликтүү. Активациядан кийин тилдер Жөндөөлөр → Экран бөлүмүндө пайда болот.", "English, Türkçe, O'zbekcha — additional interface languages for the till. Russian and Kyrgyz are available by default. After activation, the languages appear in Settings → Screen.", "English, Türkçe, O'zbekcha — kasa arayüzü için ek diller. Varsayılan olarak Rusça ve Kırgızca kullanılabilir. Etkinleştirmeden sonra diller Ayarlar → Ekran bölümünde görünür.", "English, Türkçe, O'zbekcha — kassa interfeysining qo'shimcha tillari. Standart holatda rus va qirg'iz tillari mavjud. Faollashtirilgach, tillar Sozlamalar → Ekran bo'limida paydo bo'ladi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var button = new Button
        {
            Classes = { unlocked ? "btn-secondary" : "btn-primary" },
            Content = unlocked
                ? Tr.T("✓ Активировано", "✓ Активдештирилди", "✓ Activated", "✓ Etkinleştirildi", "✓ Faollashtirildi")
                : Tr.T("🔒 Активировать (серийный номер)", "🔒 Активдештирүү (сериялык номер)", "🔒 Activate (serial number)", "🔒 Etkinleştir (seri numarası)", "🔒 Faollashtirish (seriya raqami)"),
            Height = 34,
            Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsEnabled = !unlocked,
        };
        if (!unlocked)
            button.Click += LanguagePackUnlockButton_Click;

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);
        content.Children.Add(button);

        _languagePackCard = new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };

        ExtrasWrapPanel.Children.Add(_languagePackCard);
    }

    private void LanguagePackUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Языковой пакет", "Тил пакети", "Language pack", "Dil paketi", "Til paketi"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "language", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.LanguagePackUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("language");

        PosAlertDialog.Show(
            owner,
            Tr.T("Языковой пакет активирован", "Тил пакети активдештирилди", "Language pack activated", "Dil paketi etkinleştirildi", "Til paketi faollashtirildi"),
            Tr.T("English, Türkçe и O'zbekcha теперь доступны в Настройки → Экран.",
                 "English, Türkçe жана O'zbekcha эми Жөндөөлөр → Экран бөлүмүндө жеткиликтүү.", "English, Türkçe and O'zbekcha are now available in Settings → Screen.", "English, Türkçe ve O'zbekcha artık Ayarlar → Ekran bölümünde kullanılabilir.", "English, Türkçe va O'zbekcha endi Sozlamalar → Ekran bo'limida mavjud."),
            PosAlertKind.Success);
        RefreshLanguagePackCard();
    }

    private Border? _layoutModeCard;

    /// <summary>Карточка-переключатель альтернативной раскладки главного экрана кассы "1С-стиль"
    /// (2026-09-06) — бесплатная, тот же UserPreferences.MainLayoutMode/App.ApplyMainLayoutMode,
    /// что и переключатель в Настройки → Экран (см. ScreenSettingsView) — просто ещё один вход
    /// сразу из Маркетплейса, без серийника.</summary>
    /// <summary>Карточка «Интерфейс кассира 1С» в «Доп. функциях» (2026-09-07, переработана):
    /// вместо длинного абзаца — миниатюра макета (строка сканера, крупная активная строка чека,
    /// список позиций, сводка с кнопкой «Оплата», ряд быстрых кнопок), бейджи «Бесплатно» и
    /// состояние, короткое описание и одна кнопка-переключатель. Это живой переключатель, а не
    /// paywall (см. App.ApplyMainLayoutMode / MainWindow.RefreshLayoutMode), поэтому не через
    /// BuildPriceTagFeatureCard.</summary>
    private void RefreshLayoutModeCard()
    {
        if (_layoutModeCard is not null)
            ExtrasWrapPanel.Children.Remove(_layoutModeCard);

        var isOneC = string.Equals(UserPreferences.Instance.MainLayoutMode, "onec", StringComparison.OrdinalIgnoreCase);

        var titleText = new TextBlock
        {
            Text = Tr.T("Интерфейс кассира 1С", "1С кассир интерфейси", "1C cashier interface", "1C kasiyer arayüzü", "1C kassir interfeysi"),
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badges = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badges.Children.Add(MakeLayoutBadge(Tr.T("Бесплатно", "Акысыз", "Free", "Ücretsiz", "Bepul"), "BrushSuccess"));
        badges.Children.Add(isOneC
            ? MakeLayoutBadge(Tr.T("✓ Включено", "✓ Күйгүзүлгөн", "✓ Enabled", "✓ Etkin", "✓ Yoqilgan"), "BrushAccent")
            : MakeLayoutBadge(Tr.T("Выключено", "Өчүрүлгөн", "Off", "Kapalı", "O'chirilgan"), "BrushBorder", mutedText: true));

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badges, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badges);

        var descText = new TextBlock
        {
            Text = Tr.T(
                "Крупная активная строка чека, список позиций и сводка с кнопкой «Оплата» справа — как в 1С «Рабочее место кассира». Товар добавляется только сканером.",
                "Чектин чоң активдүү сабы, позициялар тизмеси жана оң жакта «Төлөм» баскычы бар жыйынтык — 1С «Кассирдин жумуш орду» сыяктуу. Товар сканер менен гана кошулат.", "A large active receipt line, the item list and a summary with the “Payment” button on the right — like the 1C “Cashier workplace”. Products are added by scanner only.", "Büyük etkin fiş satırı, kalem listesi ve sağda «Ödeme» düğmeli özet — 1C'deki «Kasiyer çalışma yeri» gibi. Ürün yalnızca barkod okuyucuyla eklenir.", "Chekning katta faol qatori, pozitsiyalar ro'yxati va o'ngda «To'lash» tugmasi bilan yakuniy ma'lumot — 1C «Kassir ish joyi»dagidek. Mahsulot faqat skaner orqali qo'shiladi."),
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var hintText = new TextBlock
        {
            Text = Tr.T("Переключается мгновенно, без перезапуска. Также: Настройки → Экран.",
                        "Дароо которулат, кайра ачуунун кереги жок. Ошондой эле: Жөндөөлөр → Экран.", "Switches instantly, no restart needed. Also in Settings → Screen.", "Yeniden başlatma gerekmeden anında geçiş yapılır. Ayrıca: Ayarlar → Ekran.", "Qayta ishga tushirmasdan darhol almashadi. Shuningdek: Sozlamalar → Ekran."),
            FontSize = 11,
            Foreground = ThemeBrush("BrushTextMuted", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var toggleButton = new Button
        {
            Classes = { isOneC ? "btn-secondary" : "btn-primary" },
            Content = isOneC
                ? Tr.T("Вернуть обычный интерфейс", "Кадимки интерфейске кайтуу", "Switch back to the standard interface", "Standart arayüze dön", "Oddiy interfeysga qaytish")
                : Tr.T("Включить интерфейс 1С", "1С интерфейсин күйгүзүү", "Enable the 1C interface", "1C arayüzünü etkinleştir", "1C interfeysini yoqish"),
            Height = 34,
            Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        toggleButton.Click += (_, _) =>
        {
            App.ApplyMainLayoutMode(isOneC ? "standard" : "onec");
            RefreshLayoutModeCard();
            Store.Reload();
        };

        // 2026-09-28: видов кассы теперь шесть — отсюда одна ссылка на вкладку «Виды кассы».
        var allLayoutsButton = new Button
        {
            Classes = { "btn-secondary" },
            Content = Tr.T("Все виды кассы", "Кассанын бардык түрлөрү", "All till layouts", "Tüm kasa görünümleri", "Kassaning barcha ko'rinishlari"), // 2026-10-19, О7: без «→»
            Height = 34,
            Padding = new Thickness(14, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        allLayoutsButton.Click += (_, _) => ShowLayoutsTab();

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(headerRow);
        content.Children.Add(BuildOneCLayoutPreview(isOneC));
        content.Children.Add(descText);
        content.Children.Add(hintText);
        content.Children.Add(toggleButton);
        content.Children.Add(allLayoutsButton);

        _layoutModeCard = new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };

        ExtrasWrapPanel.Children.Add(_layoutModeCard);
    }

    private Border MakeLayoutBadge(string text, string brushKey, bool mutedText = false) => new()
    {
        Background = ThemeBrush($"{brushKey}Soft", Brushes.Transparent),
        BorderBrush = ThemeBrush(brushKey, Brushes.Gray),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(8, 3),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = ThemeBrush(mutedText ? "BrushTextSoft" : brushKey, Brushes.Gray),
        },
    };

    /// <summary>Миниатюра макета «1С» — те же цвета, что у настоящего OneCLayoutView (кремовая
    /// строка сканера, мятная активная строка, тан-подсветка выбранной позиции, зелёные пилюли
    /// быстрых действий, оранжевая «Оплата»), чтобы карточка показывала, ЧТО именно включится,
    /// а не описывала это словами. Цвета фиксированные: это макет светлой раскладки, не тема.</summary>
    private static Border BuildOneCLayoutPreview(bool active)
    {
        static Border Block(string hex, double height, double radius = 3) => new()
        {
            Background = new SolidColorBrush(Color.Parse(hex)),
            Height = height,
            CornerRadius = new CornerRadius(radius),
        };

        var scanner = Block("#FFF8EC", 14, 4);
        scanner.Child = new Border
        {
            Background = Brushes.White,
            Height = 8,
            Margin = new Thickness(3, 3, 70, 3),
            CornerRadius = new CornerRadius(2),
        };

        var receipt = new StackPanel { Spacing = 3 };
        receipt.Children.Add(Block("#E4F5E6", 22, 4));
        receipt.Children.Add(Block("#FDECC8", 7));
        receipt.Children.Add(Block("#E5E7EB", 7));
        receipt.Children.Add(Block("#E5E7EB", 7));
        receipt.Children.Add(Block("#E5E7EB", 7));

        var summary = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        var summaryLines = new[] { Block("#E5E7EB", 6), Block("#E5E7EB", 6), Block("#CBD5E1", 9) };
        for (var i = 0; i < summaryLines.Length; i++)
        {
            summaryLines[i].Margin = new Thickness(0, 0, 0, 4);
            Grid.SetRow(summaryLines[i], i);
            summary.Children.Add(summaryLines[i]);
        }
        var pay = Block("#F97316", 14, 4);
        Grid.SetRow(pay, 4);
        summary.Children.Add(pay);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,60") };
        Grid.SetColumn(summary, 1);
        body.Children.Add(receipt);
        body.Children.Add(summary);

        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (var i = 0; i < 5; i++)
        {
            var pill = Block("#DCF3DE", 9, 4);
            pill.Width = 36;
            pills.Children.Add(pill);
        }

        var stack = new StackPanel { Spacing = 5 };
        stack.Children.Add(scanner);
        stack.Children.Add(body);
        stack.Children.Add(pills);

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F8FAFC")),
            BorderBrush = new SolidColorBrush(Color.Parse(active ? "#F97316" : "#E2E8F0")),
            BorderThickness = new Thickness(active ? 1.5 : 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8),
            Child = stack,
        };
    }

    private Border BuildPriceTagFeatureCard(string title, string description, string openButtonLabel, Func<System.Threading.Tasks.Task> openAction, bool unlocked, EventHandler<RoutedEventArgs> onUnlockClick,
        string? priceLabel = null)
    {
        var titleText = new TextBlock
        {
            Text = title,
            Classes = { "SettingsCardTitle" },
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badgeKey = unlocked ? "BrushAccent" : "BrushWarning";
        var badge = new Border
        {
            Background = ThemeBrush($"{badgeKey}Soft", Brushes.Transparent),
            BorderBrush = ThemeBrush(badgeKey, Brushes.Orange),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = unlocked ? Tr.T("✓ Разблокировано", "✓ Ачылды", "✓ Unlocked", "✓ Kilidi açıldı", "✓ Ochildi") : Tr.T("🔒 Платно", "🔒 Акылуу", "🔒 Paid", "🔒 Ücretli", "🔒 Pullik"),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = ThemeBrush(badgeKey, Brushes.DarkOrange),
            },
        };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(badge, 1);
        headerRow.Children.Add(titleText);
        headerRow.Children.Add(badge);

        var descText = new TextBlock
        {
            Text = description,
            Classes = { "SettingsCardBody" },
            FontSize = 12,
            Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
        };

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(headerRow);
        content.Children.Add(descText);

        if (!unlocked)
        {
            var unlockButton = new Button
            {
                Classes = { "btn-secondary" },
                Content = priceLabel ?? Tr.T("🔒 Активировать — 1500 сом", "🔒 Активдештирүү — 1500 сом", "🔒 Activate — 1500 som", "🔒 Etkinleştir — 1500 som", "🔒 Faollashtirish — 1500 so'm"),
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            unlockButton.Click += onUnlockClick;
            content.Children.Add(unlockButton);
        }
        else
        {
            var openButton = new Button
            {
                Classes = { "btn-primary" },
                Content = openButtonLabel,
                Height = 32,
                Padding = new Thickness(14, 4),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Center,
            };
            openButton.Click += async (_, _) => await openAction().ConfigureAwait(true);
            content.Children.Add(openButton);
        }

        return new Border
        {
            Classes = { "SettingsCard" },
            Width = 390, // 2026-10-19, О7: 3 карточки в ряд на 1366, 2 — на 1024 (было 330 — 2 в ряд и пустое поле)
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 10, 10),
            Child = content,
        };
    }

    private void PriceTagEditorUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Редактор ценников", "Баа белгиси редактору", "Price tag editor", "Fiyat etiketi düzenleyici", "Narx yorliqlari muharriri"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "pricetag", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.PriceTagEditorUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("pricetag");
        RefreshPriceTagEditorCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Редактор ценников активирован", "Баа белгиси редактору иштетилди", "Price tag editor activated", "Fiyat etiketi düzenleyici etkinleştirildi", "Narx yorliqlari muharriri faollashtirildi"),
            Tr.T(
                "Теперь доступна печать ценника для одного товара: Склад → Товары → кнопка «💲 Ценник» на карточке товара.",
                "Эми бир товардын баа белгисин басып чыгарса болот: Кампа → Товарлар → товар карточкасындагы «💲 Баа белгиси» баскычы.", "Single-product price tag printing is now available: Warehouse → Products → the “💲 Price tag” button on the product card.", "Artık tek ürün için fiyat etiketi yazdırabilirsiniz: Depo → Ürünler → ürün kartındaki «💲 Fiyat etiketi» düğmesi.", "Endi bitta mahsulotning narx yorlig'ini chop etish mumkin: Ombor → Mahsulotlar → mahsulot kartasidagi «💲 Narx yorlig'i» tugmasi."),
            PosAlertKind.Success);
    }

    /// <summary>Независимая от PriceTagEditorUnlockButton_Click доп. услуга (2026-09-04, см.
    /// UserPreferences.BulkPriceTagUnlocked) — тот же серийник (единый мастер-ключ), но своя
    /// активация и свой флаг, чтобы включение одной карточки не включало вторую.</summary>
    private void BulkPriceTagUnlockButton_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var serial = SerialActivationDialog.Show(owner, Tr.T("Массовая печать ценников", "Баа белгилерин топтоп басып чыгаруу", "Bulk price tag printing", "Toplu fiyat etiketi baskısı", "Narx yorliqlarini ommaviy chop etish"));
        if (serial == null)
            return;

        if (!TryValidateSerial(serial, "bulktag", out var isPermanent))
        {
            PosAlertDialog.Show(
                owner,
                Tr.T("Неверный серийный номер", "Сериялык номер туура эмес", "Invalid serial number", "Geçersiz seri numarası", "Seriya raqami noto'g'ri"),
                Tr.T("Проверьте номер и попробуйте снова.", "Номерди текшерип, кайра аракет кылыңыз.", "Check the number and try again.", "Numarayı kontrol edip tekrar deneyin.", "Raqamni tekshirib, qaytadan urinib ko'ring."),
                PosAlertKind.Error);
            return;
        }

        UserPreferences.Instance.BulkPriceTagUnlocked = true;
        if (isPermanent)
            UserPreferences.Instance.SaveToDisk();
        else
            LicenseKeys.ActivateMasterAccessFor("bulktag");
        RefreshPriceTagEditorCard();

        PosAlertDialog.Show(
            owner,
            Tr.T("Массовая печать ценников активирована", "Баа белгилерин топтоп басып чыгаруу иштетилди", "Bulk price tag printing activated", "Toplu fiyat etiketi baskısı etkinleştirildi", "Narx yorliqlarini ommaviy chop etish faollashtirildi"),
            Tr.T(
                "Теперь доступна кнопка «🏷 Массовая печать ценников» в Склад → Товары — выберите нужные товары и распечатайте ценники сразу для всех.",
                "Эми Кампа → Товарлар бетинде «🏷 Баа белгилерин топтоп басып чыгаруу» баскычы бар — керектүү товарларды тандап, баарынын баа белгилерин бир жолу басып чыгарыңыз.", "The “🏷 Bulk price tag printing” button is now available in Warehouse → Products — select the products you need and print price tags for all of them at once.", "Depo → Ürünler bölümünde artık «🏷 Toplu fiyat etiketi yazdır» düğmesi var — ürünleri seçip hepsinin fiyat etiketini tek seferde yazdırın.", "Endi Ombor → Mahsulotlar bo'limida «🏷 Narx yorliqlarini ommaviy chop etish» tugmasi bor — kerakli mahsulotlarni tanlang va hammasining narx yorlig'ini bir yo'la chop eting."),
            PosAlertKind.Success);
    }
}
