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

// 2026-11-06: В15 — часть класса MarketplaceView (разбор огромного файла на partial-файлы), логика не менялась; остальное — MarketplaceView.axaml.cs
public partial class MarketplaceView
{
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
                FontSize = 12,
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
                FontSize = 12,
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
                FontSize = 12,
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
                FontSize = 12,
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
                FontSize = 12,
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
                FontSize = 12,
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
}
