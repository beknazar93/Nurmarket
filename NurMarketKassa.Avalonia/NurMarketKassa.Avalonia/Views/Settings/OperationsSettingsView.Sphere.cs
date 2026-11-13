using System.Net.Http;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>
/// 2026-09-28: вид магазина уходит на сервер (PATCH /api/users/company/ {market_sphere}, BE-18),
/// и все кассы компании берут его при входе (MarketSphereSync). Раньше вид выбирали вручную на
/// каждом компьютере. Менять вид компании может только владелец: у кассира сервер отвечает 403 —
/// тогда вид остаётся только в этой кассе, и мы честно об этом пишем.
/// </summary>
public partial class OperationsSettingsView
{
    private int _sphereSaveVersion;

    private async Task SaveSphereOnServerAsync(string sphere)
    {
        var version = ++_sphereSaveVersion;
        var normalized = MarketSpheres.Normalize(sphere);

        if (MarketSphereSync.ServerSphere == normalized)
        {
            ShowSphereServerStatus(Tr.T(
                "Этот вид уже задан для всех касс компании.",
                "Бул түр компаниянын бардык кассалары үчүн мурунтан эле коюлган.",
                "This store type is already set for all of the company's tills.",
                "Bu mağaza türü şirketin tüm kasaları için zaten ayarlı.",
                "Bu tur kompaniyaning barcha kassalari uchun allaqachon o'rnatilgan."));
            return;
        }

        ShowSphereServerStatus(Tr.T(
            "Сохраняю вид магазина для всех касс…",
            "Дүкөндүн түрү бардык кассалар үчүн сакталууда…",
            "Saving the store type for all tills…",
            "Mağaza türü tüm kasalar için kaydediliyor…",
            "Do'kon turi barcha kassalar uchun saqlanmoqda…"));

        string message;
        try
        {
            await App.GetRequiredService<ClientDebtsApiService>()
                .SetCompanyMarketSphereAsync(normalized)
                .ConfigureAwait(true);
            MarketSphereSync.NoteSavedOnServer(normalized);
            PosLogger.Log($"Вид магазина сохранён на сервере: {normalized}.", "API");
            message = Tr.T(
                "Сохранено для всех касс компании: они перестроятся при следующем входе.",
                "Компаниянын бардык кассалары үчүн сакталды: кийинки киргенде алар өзгөрөт.",
                "Saved for all of the company's tills: they will switch at their next sign-in.",
                "Şirketin tüm kasaları için kaydedildi: bir sonraki girişte değişecekler.",
                "Kompaniyaning barcha kassalari uchun saqlandi: keyingi kirishda ular o'zgaradi.");
        }
        catch (ApiException ex) when (ex.StatusCode == 403)
        {
            PosLogger.Log("Вид магазина на сервере не изменён: нет прав владельца (403).", "API");
            message = Tr.T(
                "Вид изменён только в этой кассе: для всех касс его может менять только владелец." +
                (MarketSphereSync.ServerSphere is null ? "" : " При следующем входе касса вернётся к виду, заданному владельцем."),
                "Түр ушул кассада гана өзгөрдү: бардык кассалар үчүн аны ээси гана өзгөртө алат." +
                (MarketSphereSync.ServerSphere is null ? "" : " Кийинки киргенде касса ээси койгон түргө кайтат."),
                "Changed on this till only: only the owner can change it for all tills." +
                (MarketSphereSync.ServerSphere is null ? "" : " At the next sign-in the till will return to the type set by the owner."),
                "Yalnızca bu kasada değişti: tüm kasalar için yalnızca sahibi değiştirebilir." +
                (MarketSphereSync.ServerSphere is null ? "" : " Bir sonraki girişte kasa, sahibin belirlediği türe döner."),
                "Faqat shu kassada o'zgardi: barcha kassalar uchun uni faqat egasi o'zgartira oladi." +
                (MarketSphereSync.ServerSphere is null ? "" : " Keyingi kirishda kassa egasi belgilagan turga qaytadi."));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            PosLogger.Log($"Вид магазина на сервер не сохранён: {ex.Message}", "WARNING");
            message = Tr.T(
                "Вид изменён в этой кассе, но на сервер не сохранён (нет связи или ошибка сервера). Выберите его ещё раз, когда появится интернет.",
                "Түр ушул кассада өзгөрдү, бирок серверге сакталган жок (байланыш жок же сервер катасы). Интернет пайда болгондо аны кайра тандаңыз.",
                "Changed on this till, but not saved to the server (no connection or a server error). Select it again once you are back online.",
                "Bu kasada değişti ama sunucuya kaydedilemedi (bağlantı yok veya sunucu hatası). İnternet geldiğinde tekrar seçin.",
                "Shu kassada o'zgardi, lekin serverga saqlanmadi (aloqa yo'q yoki server xatosi). Internet paydo bo'lganda uni qayta tanlang.");
        }

        // Пока шёл запрос, могли выбрать другой вид — тогда этот ответ уже не актуален.
        if (version == _sphereSaveVersion)
            ShowSphereServerStatus(message);
    }

    private void ShowSphereServerStatus(string text)
    {
        SphereServerStatus.Text = text;
        SphereServerStatus.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    // ── 2026-11-08, владелец: «Вид магазина меняется только через техподдержку» (MarketSphereLock) ──────────────

    /// <summary>Плашка с замком, текущий вид, серые кнопки. Под замком кнопки неактивны, но выбранная показывает текущий вид.</summary>
    private void RefreshSphereLockUi()
    {
        var canChange = MarketSphereLock.CanChangeNow;
        foreach (var radio in new[] { SphereGroceryRadio, SphereClothingRadio, SphereServicesRadio }.Concat(_extraSphereRadios.Values))
            radio.IsEnabled = canChange;

        SphereLockText.Text = !canChange
            ? LockedText
            : MarketSphereLock.SupportSessionActive
                ? Tr.T("Оператор поддержки подключён — вид магазина можно переключить.",
                    "Колдоо кызматынын оператору туташты — дүкөндүн түрүн которсо болот.",
                    "A support operator is connected — the store type can be switched.",
                    "Destek operatörü bağlı — mağaza türü değiştirilebilir.",
                    "Qo'llab-quvvatlash operatori ulangan — do'kon turini almashtirish mumkin.")
                : MarketSphereLock.UnlockedBySupport
                    ? Tr.T("Техподдержка разрешила сменить вид магазина на этой кассе.",
                        "Техколдоо бул кассада дүкөндүн түрүн алмаштырууга уруксат берди.",
                        "Support has allowed changing the store type on this till.",
                        "Teknik destek bu kasada mağaza türünü değiştirmeye izin verdi.",
                        "Texnik yordam shu kassada do'kon turini o'zgartirishga ruxsat berdi.")
                    : Tr.T("Выберите вид магазина. Выбор делается один раз — потом вид меняется только через техподдержку.",
                        "Дүкөндүн түрүн тандаңыз. Тандоо бир жолу жасалат — андан кийин түр техколдоо аркылуу гана өзгөрөт.",
                        "Choose the store type. You choose it once — after that it can only be changed through support.",
                        "Mağaza türünü seçin. Seçim bir kez yapılır — sonra tür yalnızca teknik destek aracılığıyla değişir.",
                        "Do'kon turini tanlang. Tanlov bir marta qilinadi — keyin tur faqat texnik yordam orqali o'zgaradi.");

        SphereCurrentText.Text = Tr.T("Сейчас: ", "Азыр: ", "Current: ", "Şu an: ", "Hozir: ") + MarketSpheres.Profile.Title.Get();

        SphereSupportRow.IsVisible = !canChange;
        SphereSupportButton.Content = Tr.T("Написать в поддержку", "Колдоо кызматына жазуу", "Message support", "Desteğe yaz", "Yordamga yozish");
        SphereSupportPhone.Text = Tr.T("Телефон и WhatsApp: ", "Телефон жана WhatsApp: ", "Phone and WhatsApp: ", "Telefon ve WhatsApp: ", "Telefon va WhatsApp: ")
                                  + MarketSphereLock.SupportPhone;
    }

    /// <summary>Надпись над серыми кнопками (её же проверяет тест снимка).</summary>
    public static string LockedText => Tr.T(
        "Вид магазина меняется только через техподдержку.",
        "Дүкөндүн түрү техколдоо аркылуу гана өзгөрөт.",
        "The store type can only be changed through support.",
        "Mağaza türü yalnızca teknik destek aracılığıyla değiştirilir.",
        "Do'kon turi faqat texnik yordam orqali o'zgartiriladi.");

    /// <summary>«Написать в поддержку» — WhatsApp поддержки с готовым текстом (тот же номер, что в окне «Поддержка»).</summary>
    private void SphereSupport_Click(object? sender, RoutedEventArgs e)
    {
        var company = CompanyInfoService.LastCompany?.Name;
        var text = Tr.T("Здравствуйте! Хочу сменить вид магазина. ", "Саламатсызбы! Дүкөндүн түрүн алмаштыргым келет. ", "Hello! I would like to change the store type. ",
                       "Merhaba! Mağaza türünü değiştirmek istiyorum. ", "Assalomu alaykum! Do'kon turini o'zgartirmoqchiman. ")
                   + (string.IsNullOrWhiteSpace(company) ? "" : Tr.T("Компания: ", "Компания: ", "Company: ", "Şirket: ", "Kompaniya: ") + company + ". ")
                   + Tr.T("Сейчас: ", "Азыр: ", "Current: ", "Şu an: ", "Hozir: ") + MarketSpheres.Profile.Title.Get() + ".";
        var digits = new string(MarketSphereLock.SupportPhone.Where(char.IsDigit).ToArray());
        if (!PlatformLauncher.OpenUrl("https://wa.me/" + digits + "?text=" + Uri.EscapeDataString(text)))
            PosLogger.Log("Вид магазина: WhatsApp поддержки не открылся.", "WARNING");
    }

    // ── 2026-11-12 (ХМ14, ТЗ хозмаг): плитки сфер из каталога и размер магазина «Хозтовары» ──────────────

    private readonly Dictionary<string, Avalonia.Controls.RadioButton> _extraSphereRadios = new();

    /// <summary>Кнопки сфер каталога сверх трёх базовых (<see cref="SphereCatalog.ChoiceList"/>): создаются один раз, потом только отмечаются.</summary>
    private void RefreshExtraSphereRadios(string currentProfileId)
    {
        foreach (var profile in SphereCatalog.ChoiceList())
        {
            if (profile.Id is MarketSpheres.Grocery or MarketSpheres.Clothing or MarketSpheres.Services)
                continue;
            if (!_extraSphereRadios.TryGetValue(profile.Id, out var radio))
            {
                radio = new Avalonia.Controls.RadioButton { GroupName = "MarketSphere", Margin = new Avalonia.Thickness(0, 0, 8, 8), Tag = profile.Id };
                radio.Classes.Add("DatePill");
                radio.Classes.Add("SphereLockable");
                radio.Click += Sphere_Click;
                _extraSphereRadios[profile.Id] = radio;
                SphereRadioPanel.Children.Add(radio);
            }
            radio.Content = profile.Icon + " " + profile.ShortTitle.Get();
            radio.IsChecked = profile.Id == currentProfileId;
        }
    }

    /// <summary>Id точной сферы, чья кнопка отмечена, или null (отмечена одна из трёх базовых).</summary>
    private string? SelectedExtraSphere() =>
        _extraSphereRadios.FirstOrDefault(kv => kv.Value.IsChecked == true).Key;

    /// <summary>Сфера выбрана в Настройках. Новый магазин (первый выбор) получает «скан коробки» включённым (В-Х4), перешедший — выключенным.</summary>
    private static void OnSphereChosen(string sphere, bool firstChoice)
    {
        if (sphere == MarketSpheres.Hardware && firstChoice)
            SphereSettings.SetHardwareChoice(SphereSettings.GetHardwareSize(), packScan: true);
    }

    private bool _hardwareUiBusy;

    private void RefreshHardwareUi()
    {
        var isHardware = MarketSpheres.IsHardware;
        HardwarePanel.IsVisible = isHardware;
        if (!isHardware)
            return;
        _hardwareUiBusy = true;
        try
        {
            HardwareSizeTitle.Text = Dialogs.HardwareChoiceTexts.SizeStepTitle;
            HardwareShopRadio.Content = Dialogs.HardwareChoiceTexts.SizeName(HardwareSize.Shop);
            HardwareMarketRadio.Content = Dialogs.HardwareChoiceTexts.SizeName(HardwareSize.Market);
            var size = SphereSettings.GetHardwareSize();
            HardwareShopRadio.IsChecked = size == HardwareSize.Shop;
            HardwareMarketRadio.IsChecked = size == HardwareSize.Market;
            HardwareSizeHint.Text = Dialogs.HardwareChoiceTexts.SizeDescription(size);
            HardwarePackScanText.Text = Dialogs.HardwareChoiceTexts.PackScanText;
            HardwarePackScanCheck.IsChecked = SphereSettings.GetHardwarePackScan();
            HardwareChangesText.Text = Dialogs.HardwareChoiceTexts.WhatChangesText();
            // Нормы калькулятора правит владелец или администратор (по роли из профиля); кассиру кнопка не показывается.
            HardwareNormsButton.IsVisible = CanSeeAiKeys;
            HardwareNormsButton.Content = Tr.T("Нормы расхода калькулятора…", "Калькулятордун сарптоо нормалары…", "Calculator consumption rates…", "Hesaplayıcı tüketim normları…", "Kalkulyator sarf normalari…");
        }
        finally
        {
            _hardwareUiBusy = false;
        }
    }

    private async void HardwareNorms_Click(object? sender, RoutedEventArgs e)
    {
        if (!CanSeeAiKeys || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        await new HardwareCalcNormsView().ShowDialog(owner).ConfigureAwait(true);
    }

    private void HardwareSize_Click(object? sender, RoutedEventArgs e)
    {
        if (_hardwareUiBusy)
            return;
        var size = HardwareMarketRadio.IsChecked == true ? HardwareSize.Market : HardwareSize.Shop;
        if (!SphereSettings.SetHardwareChoice(size))
            PosLogger.Log("Размер магазина «Хозтовары» не записан: настройки сфер не помещаются в лимит.", "WARNING");
        RefreshHardwareUi();
    }

    private void HardwarePackScan_Changed(object? sender, RoutedEventArgs e)
    {
        if (_hardwareUiBusy)
            return;
        if (!SphereSettings.SetHardwareChoice(SphereSettings.GetHardwareSize(), HardwarePackScanCheck.IsChecked == true))
            PosLogger.Log("«Скан коробки» не записан: настройки сфер не помещаются в лимит.", "WARNING");
        RefreshHardwareUi();
    }

    private void OnSphereLockChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (this.IsAttachedToVisualTree())
            RefreshSphereUi();
    });

    private void SphereLock_Attached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        MarketSphereLock.Changed += OnSphereLockChanged;
        MarketSpheres.Changed += OnSphereLockChanged; // вид назначили сверху, пока страница открыта
    }

    private void SphereLock_Detached(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        MarketSphereLock.Changed -= OnSphereLockChanged;
        MarketSpheres.Changed -= OnSphereLockChanged;
    }
}
