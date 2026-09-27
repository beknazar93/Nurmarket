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
        BuildThemeGallery();
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

        // "Список" новых тем/доп. услуг — это, по сути, новая версия кассы (Velopack/GitHub
        // Releases): отдельного каталога на сервере нет, все карточки зашиты в код. Поэтому
        // "обновить список при открытии Маркетплейса" (2026-09-05, по просьбе пользователя)
        // реализовано как немедленная проверка обновлений с баннером — так пользователь
        // реально видит, что есть более новая версия с новыми функциями/темами.
        _ = CheckForUpdatesAsync();
    }

    /// <summary>Переключает вкладку "Доп. функции" программно — используется, когда пользователь
    /// переходит сюда из другой страницы настроек (см. ScreenSettingsView → "Открыть в Маркетплейсе").</summary>
    public void ShowExtrasTab() => ExtrasTab_Click(this, new RoutedEventArgs());

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

    private void ThemesTab_Click(object? sender, RoutedEventArgs e)
    {
        ThemesPanel.IsVisible = true;
        ExtrasPanel.IsVisible = false;
        ThemesTabButton.Classes.Remove("btn-secondary");
        ThemesTabButton.Classes.Add("btn-primary");
        ExtrasTabButton.Classes.Remove("btn-primary");
        ExtrasTabButton.Classes.Add("btn-secondary");
    }

    private void ExtrasTab_Click(object? sender, RoutedEventArgs e)
    {
        ThemesPanel.IsVisible = false;
        ExtrasPanel.IsVisible = true;
        ExtrasTabButton.Classes.Remove("btn-secondary");
        ExtrasTabButton.Classes.Add("btn-primary");
        ThemesTabButton.Classes.Remove("btn-primary");
        ThemesTabButton.Classes.Add("btn-secondary");
    }

    /// <summary>Галерея тем. Карточки собираются в коде, а не через ItemsControl+Binding:
    /// каждая рисует собственный предпросмотр цветами САМОЙ темы (фон окна, плитка товара,
    /// кнопка), а не цветами текущей. Словами разницу между шестью оформлениями не передать,
    /// а перебирать их вживую — каждый раз перекрашивать весь экран.</summary>
    private void BuildThemeGallery()
    {
        ThemeGalleryPanel.Items.Clear();

        var prefs = UserPreferences.Instance;
        var current = AccentThemeService.Normalize(prefs.AccentTheme);
        var dark = prefs.DarkTheme;

        // Встроенные темы, за ними — свои (редактор тем, 2026-09-28) с пометкой «Своя».
        var entries = AccentThemeService.AvailableThemes
            .Select(t => (t.Id, t.Label, t.Icon, t.Description, IsCustom: false))
            .Concat(CustomThemeStore.All.Select(t => (t.Id, Label: t.Name, Icon: "",
                Description: Tr.T("Своя тема. Изменить — кнопка «Редактор тем».", "Өз темаңыз. Өзгөртүү — «Темалар редактору» баскычы.",
                    "Your own theme. To change it, use the “Theme editor” button.", "Kendi temanız. Değiştirmek için «Tema düzenleyici» düğmesi.",
                    "O'z mavzuingiz. O'zgartirish — «Mavzu muharriri» tugmasi."), IsCustom: true)))
            .ToList();

        foreach (var theme in entries)
        {
            bool isActive = string.Equals(theme.Id, current, System.StringComparison.OrdinalIgnoreCase);
            var preview = AccentThemeService.GetPreview(theme.Id, dark);

            var card = new Button
            {
                Tag = theme.Id,
                Width = 250,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(0),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Background = ThemeBrush("BrushPanel", Brushes.White),
                BorderBrush = isActive
                    ? ThemeBrush("BrushAccent", Brushes.Goldenrod)
                    : ThemeBrush("BrushBorder", Brushes.LightGray),
                BorderThickness = new Thickness(isActive ? 2 : 1),
                CornerRadius = new CornerRadius(12),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };

            var rows = new StackPanel { Spacing = 0 };

            // --- предпросмотр: кусок кассы в миниатюре, цветами этой темы
            if (preview is { } pv)
            {
                var windowBrush = new SolidColorBrush(Color.Parse(pv.Window));
                var tileBrush = new SolidColorBrush(Color.Parse(pv.Tile));
                var accentBrush = new SolidColorBrush(Color.Parse(pv.Accent));
                var textBrush = new SolidColorBrush(Color.Parse(pv.Text));

                var tiles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                for (var i = 0; i < 3; i++)
                    tiles.Children.Add(new Border
                    {
                        Width = 42,
                        Height = 30,
                        Background = tileBrush,
                        BorderBrush = new SolidColorBrush(Color.Parse(pv.TileBorder)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(4),
                        Child = new Border
                        {
                            Height = 4,
                            Width = 22,
                            Margin = new Thickness(6, 0, 0, 6),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Bottom,
                            CornerRadius = new CornerRadius(2),
                            Background = accentBrush,
                        },
                    });

                var payButton = new Border
                {
                    Height = 18,
                    Width = 138,
                    Margin = new Thickness(0, 8, 0, 0),
                    CornerRadius = new CornerRadius(4),
                    Background = accentBrush,
                };

                rows.Children.Add(new Border
                {
                    Background = windowBrush,
                    Padding = new Thickness(12, 12, 12, 10),
                    CornerRadius = new CornerRadius(11, 11, 0, 0),
                    Child = new StackPanel
                    {
                        Spacing = 0,
                        Children =
                        {
                            tiles,
                            payButton,
                            new TextBlock
                            {
                                Text = Tr.T("Каталог и кнопка оплаты", "Каталог жана төлөм баскычы",
                                            "Catalog and pay button", "Katalog ve ödeme düğmesi",
                                            "Katalog va to'lov tugmasi"),
                                FontSize = 9,
                                Margin = new Thickness(0, 7, 0, 0),
                                Foreground = textBrush,
                                Opacity = 0.65,
                            },
                        },
                    },
                });
            }

            // --- название и состояние
            var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };

            titleRow.Children.Add(new TextBlock
            {
                Text = theme.Icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            });

            var nameText = new TextBlock
            {
                Text = theme.Label,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("BrushText", Brushes.Black),
            };
            Grid.SetColumn(nameText, 1);
            titleRow.Children.Add(nameText);

            if (theme.IsCustom && !isActive)
            {
                var customBadge = new Border
                {
                    Background = ThemeBrush("BrushPanelSoft", Brushes.LightGray),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = Tr.T("Своя", "Өзүңүздүкү", "Custom", "Özel", "O'zingizniki"),
                        FontSize = 10,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeBrush("BrushTextMuted", Brushes.Gray),
                    },
                };
                Grid.SetColumn(customBadge, 2);
                titleRow.Children.Add(customBadge);
            }

            if (isActive)
            {
                var badge = new Border
                {
                    Background = ThemeBrush("BrushAccentSoft", Brushes.LightGoldenrodYellow),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = Tr.T("Выбрана", "Тандалган", "Selected", "Seçili", "Tanlangan"),
                        FontSize = 10,
                        FontWeight = FontWeight.SemiBold,
                        Foreground = ThemeBrush("BrushAccentStrong", Brushes.DarkGoldenrod),
                    },
                };
                Grid.SetColumn(badge, 2);
                titleRow.Children.Add(badge);
            }

            var descText = new TextBlock
            {
                Text = theme.Description,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
            };

            rows.Children.Add(new Border
            {
                Padding = new Thickness(12, 10, 12, 12),
                Child = new StackPanel { Spacing = 0, Children = { titleRow, descText } },
            });

            card.Content = rows;
            card.Click += ThemeCard_Click;
            ThemeGalleryPanel.Items.Add(card);
        }

        ThemeGalleryPanel.Items.Add(BuildNewCustomThemeCard());
    }

    /// <summary>Последняя карточка галереи — вход в редактор: создать свою тему.</summary>
    private Button BuildNewCustomThemeCard()
    {
        var card = new Button
        {
            Width = 250,
            MinHeight = 150,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(12),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Background = ThemeBrush("BrushPanel", Brushes.White),
            BorderBrush = ThemeBrush("BrushBorderStrong", Brushes.Gray),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new PathIcon
                    {
                        Data = Application.Current?.TryFindResource("IconPalette", out var icon) == true ? icon as Geometry : null,
                        Width = 28,
                        Height = 28,
                        Foreground = ThemeBrush("BrushAccentStrong", Brushes.Goldenrod),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = Tr.T("+ Своя тема", "+ Өз темаңыз", "+ Custom theme", "+ Özel tema", "+ O'z mavzuingiz"),
                        FontSize = 14,
                        FontWeight = FontWeight.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = ThemeBrush("BrushText", Brushes.Black),
                    },
                    new TextBlock
                    {
                        Text = Tr.T("Цвета, скругление и шрифт на основе любой темы",
                            "Каалаган темага негизделген түстөр, тегеректик жана шрифт",
                            "Colors, rounding and font based on any theme",
                            "Herhangi bir temaya dayalı renkler, yuvarlaklık ve yazı tipi",
                            "Istalgan mavzu asosidagi ranglar, yumaloqlik va shrift"),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        Foreground = ThemeBrush("BrushTextSoft", Brushes.Gray),
                    },
                },
            },
        };
        card.Click += OpenThemeEditor_Click;
        return card;
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
            BuildThemeGallery();
        }
    }

    private void ThemeCard_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string themeId })
            return;

        App.ApplyAccentTheme(themeId);

        // Предпросмотры рисуются под текущий светлый/тёмный вариант, а рамка отмечает
        // выбранную — после смены темы пересобираем галерею целиком.
        BuildThemeGallery();
    }

    /// <summary>Кисть из ресурсов приложения — карточки Маркетплейса собираются в коде, а там
    /// DynamicResource недоступен, поэтому значение берётся напрямую с запасным вариантом.</summary>
    private IBrush ThemeBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, out var value) && value is IBrush brush
            ? brush
            : fallback;

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
        var installedRu = VoiceModelDownloadService.IsInstalled(VoiceModelLanguage.Russian);
        var installedKy = VoiceModelDownloadService.IsInstalled(VoiceModelLanguage.Kyrgyz);
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
            Width = 330,
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
            Width = 330,
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
            Width = 330,
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
                Content = Tr.T("🔒 Активировать — 1500 сом", "🔒 Активдештирүү — 1500 сом", "🔒 Activate — 1500 som", "🔒 Etkinleştir — 1500 som", "🔒 Faollashtirish — 1500 so'm"),
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
            Width = 330,
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
            Width = 330,
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
            onUnlockClick: AnalyticsExportUnlockButton_Click);

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
            onUnlockClick: TelegramBotUnlockButton_Click);

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
            onUnlockClick: ShiftAnalyticsUnlockButton_Click);

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
            Width = 330,
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
        };

        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(headerRow);
        content.Children.Add(BuildOneCLayoutPreview(isOneC));
        content.Children.Add(descText);
        content.Children.Add(hintText);
        content.Children.Add(toggleButton);

        _layoutModeCard = new Border
        {
            Classes = { "SettingsCard" },
            Width = 330,
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

    private Border BuildPriceTagFeatureCard(string title, string description, string openButtonLabel, Func<System.Threading.Tasks.Task> openAction, bool unlocked, EventHandler<RoutedEventArgs> onUnlockClick)
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
                Content = Tr.T("🔒 Активировать — 1500 сом", "🔒 Активдештирүү — 1500 сом", "🔒 Activate — 1500 som", "🔒 Etkinleştir — 1500 som", "🔒 Faollashtirish — 1500 so'm"),
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
            Width = 330,
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
