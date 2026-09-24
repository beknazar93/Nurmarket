using System.Collections.ObjectModel;
using Avalonia.Controls;
using NurMarketKassa.Services;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class OperationsSettingsView : UserControl
{
    private ObservableCollection<BankQrSetting> _bankSettings = new();

    // Ни у одного банка нет логотипа-по-умолчанию (2026-09-05: раньше "Элкарт" был
    // единственным исключением со своей встроенной картинкой Assets/Elkart-logo.png, но
    // пользователь попросил убрать все "мини-логотипы" из системы) — показывается общая
    // векторная иконка (см. XAML, Border/TextBlock с IsVisible="{Binding HasNoLogo}",
    // Segoe MDL2 Assets glyph), пока кассир сам не загрузит свой логотип через
    // ChangeLogo_Click — тогда путь сохраняется в UserPreferences.BankLogoPaths и переживает
    // перезапуск кассы.
    // Показываем только три основных банка. Полный список из двадцати шести — в диалоге
    // «Добавить банк»: владелец выбирает оттуда свои, и они появляются в этом списке.
    // Вываливать все 26 сразу нельзя — владелец жаловался, что список слишком длинный,
    // а магазин работает с одним-двумя банками.
    private string[] _banks => KyrgyzBanks.DefaultVisible;

    public OperationsSettingsView()
    {
        InitializeComponent();
    }

    // ── Сфера магазина ───────────────────────────────────────────────────────────────

    private void RefreshSphereUi()
    {
        SphereTitle.Text = Tr.T("Сфера магазина", "Дүкөндүн тармагы", "Store type", "Mağaza türü", "Do'kon turi");
        SphereHint.Text = Tr.T(
            "Под что настроена касса. Меняется в любой момент, на продажи и отчёты не влияет.",
            "Касса эмнеге ыңгайлаштырылган. Каалаган убакта өзгөрөт, сатууларга жана отчётторго таасир этпейт.",
            "What the POS is set up for. Can be changed at any time; sales and reports are not affected.",
            "Kasanın neye göre ayarlandığı. İstediğiniz zaman değişir, satışları ve raporları etkilemez.",
            "Kassa nimaga moslangan. Istalgan vaqtda o'zgartiriladi, sotuv va hisobotlarga ta'sir qilmaydi.");
        SphereGroceryRadio.Content = Tr.T("Продуктовый", "Азык-түлүк", "Grocery", "Market (gıda)", "Oziq-ovqat");
        SphereClothingRadio.Content = Tr.T("Одежда и похожие", "Кийим жана ушул сыяктуу", "Clothing and similar", "Giyim ve benzeri", "Kiyim va shunga o'xshash");
        SphereServicesRadio.Content = Tr.T("Услуги", "Кызматтар", "Services", "Hizmetler", "Xizmatlar");

        var sphere = UserPreferences.Instance.MarketSphere;
        SphereGroceryRadio.IsChecked = sphere == MarketSpheres.Grocery;
        SphereClothingRadio.IsChecked = sphere == MarketSpheres.Clothing;
        SphereServicesRadio.IsChecked = sphere == MarketSpheres.Services;
        SphereDescription.Text = sphere switch
        {
            MarketSpheres.Clothing => Tr.T(
                "При оплате появляется блок «Консультант»: кто помог покупателю, и процент ему с этой продажи — как на сайте.",
                "Төлөөдө «Консультант» блогу пайда болот: сатып алуучуга ким жардам берди жана ага бул сатуудан пайыз — сайттагыдай.",
                "The payment window gets a Consultant block: who helped the customer and their percentage of the sale, as on the website.",
                "Ödemede «Danışman» bölümü çıkar: müşteriye kim yardım etti ve bu satıştan ona yüzde — sitedeki gibi.",
                "To'lovda «Maslahatchi» bloki paydo bo'ladi: xaridorga kim yordam berdi va unga bu sotuvdan foiz — saytdagidek."),
            MarketSpheres.Services => Tr.T(
                "Каталог открывается на вкладке «Услуги». Услуги продаются без остатка: касса не просит пополнить склад и не показывает у них количество.",
                "Каталог «Кызматтар» өтмөгүндө ачылат. Кызматтар калдыксыз сатылат: касса кампаны толуктоону сурабайт жана алардын санын көрсөтпөйт.",
                "The catalogue opens on the Services tab. Services are sold without stock: the POS does not ask to restock them and does not show their quantity.",
                "Katalog «Hizmetler» sekmesinde açılır. Hizmetler stoksuz satılır: kasa stok eklemeyi istemez ve miktarlarını göstermez.",
                "Katalog «Xizmatlar» bo'limida ochiladi. Xizmatlar qoldiqsiz sotiladi: kassa omborni to'ldirishni so'ramaydi va ularning sonini ko'rsatmaydi."),
            _ => Tr.T(
                "Обычная касса магазина, как было.",
                "Дүкөндүн кадимки кассасы, мурункудай.",
                "A regular store POS, as before.",
                "Her zamanki mağaza kasası.",
                "Oddiy do'kon kassasi, avvalgidek."),
        };
    }

    private void Sphere_Click(object? sender, RoutedEventArgs e)
    {
        var sphere = SphereClothingRadio.IsChecked == true ? MarketSpheres.Clothing
            : SphereServicesRadio.IsChecked == true ? MarketSpheres.Services
            : MarketSpheres.Grocery;
        MarketSpheres.Set(sphere);
        RefreshSphereUi();
    }

    public void LoadBankQrSettings()
    {
        RefreshSphereUi();
        _bankSettings = new ObservableCollection<BankQrSetting>();
        var prefs = UserPreferences.Instance;

        // Обработчик сравнивает значение перед записью, поэтому эта установка не вызывает
        // лишнего сохранения настроек.
        DynamicQrCheck.IsChecked = prefs.DynamicPaymentQrEnabled;

        TelegramTokenBox.Text = prefs.TelegramBotToken ?? "";
        TelegramSummaryCheck.IsChecked = prefs.TelegramShiftSummaryEnabled;
        TelegramCommandsCheck.IsChecked = prefs.TelegramCommandsEnabled;
        OwnerPhoneBox.Text = prefs.OwnerPhone ?? "";
        UpdateTelegramStatus();

        void AddBankRow(string bank, bool isCustom)
        {
            string? qrPath = prefs.BankQrPaths?.TryGetValue(bank, out var qr) == true ? qr : null;
            string? logoPath = prefs.BankLogoPaths?.TryGetValue(bank, out var customLogo) == true
                ? customLogo
                : null;

            // Пустой список отмеченных означает «владелец ещё не выбирал» — отмечаем те же
            // три банка, что показывались до обновления, чтобы касса не изменилась сама собой.
            var visible = prefs.VisibleBankNames is { Count: > 0 }
                ? prefs.VisibleBankNames
                : KyrgyzBanks.DefaultVisible.ToList();

            var row = new BankQrSetting
            {
                BankName = bank,
                LogoPath = logoPath,
                QrCodePath = qrPath,
                IsCustom = isCustom,
                ShowAtCheckout = visible.Contains(bank, StringComparer.OrdinalIgnoreCase),
            };
            row.PropertyChanged += BankRow_PropertyChanged;
            _bankSettings.Add(row);
        }

        foreach (var bank in _banks)
            AddBankRow(bank, isCustom: false);
        foreach (var bank in prefs.CustomBankNames)
            AddBankRow(bank, isCustom: true);

        BankQrItemsControl.ItemsSource = _bankSettings;
    }

    private void UpdateTelegramStatus()
    {
        var prefs = UserPreferences.Instance;
        if (string.IsNullOrWhiteSpace(prefs.TelegramChatId))
        {
            TelegramStatusText.Text = "Получатель не определён.";
            return;
        }

        TelegramStatusText.Text = string.IsNullOrWhiteSpace(prefs.TelegramChatTitle)
            ? $"Получатель определён (чат {prefs.TelegramChatId})."
            : $"Получатель: {prefs.TelegramChatTitle}.";
    }

    private void SaveTelegramToken()
    {
        var prefs = UserPreferences.Instance;
        var token = (TelegramTokenBox.Text ?? "").Trim();
        if (prefs.TelegramBotToken == token)
            return;
        prefs.TelegramBotToken = token;
        prefs.SaveToDisk();
    }

    /// <summary>Узнаёт chat_id владельца по последним сообщениям боту. Владелец должен сначала
    /// нажать «Старт» у своего бота — иначе Telegram нам просто нечего показать.</summary>
    private async void TelegramDetect_Click(object? sender, RoutedEventArgs e)
    {
        SaveTelegramToken();
        TelegramDetectButton.IsEnabled = false;
        TelegramStatusText.Text = "Спрашиваю Telegram…";
        try
        {
            var (chatId, name, error) = await TelegramBotService
                .TryDetectChatIdAsync(UserPreferences.Instance.TelegramBotToken ?? "")
                .ConfigureAwait(true);

            if (error is not null || chatId is null)
            {
                TelegramStatusText.Text = error ?? "Не удалось определить получателя.";
                return;
            }

            var prefs = UserPreferences.Instance;
            prefs.TelegramChatId = chatId;
            prefs.TelegramChatTitle = name;
            prefs.SaveToDisk();
            UpdateTelegramStatus();
        }
        finally
        {
            TelegramDetectButton.IsEnabled = true;
        }
    }

    private async void TelegramTest_Click(object? sender, RoutedEventArgs e)
    {
        SaveTelegramToken();
        TelegramTestButton.IsEnabled = false;
        TelegramStatusText.Text = "Отправляю…";
        try
        {
            var shop = UserPreferences.Instance.StoreName;
            var error = await TelegramBotService
                .SendAsync($"<b>{shop}</b>\n\nПробное сообщение из кассы. Если вы его видите — бот настроен верно.")
                .ConfigureAwait(true);
            TelegramStatusText.Text = error ?? "Отправлено — проверьте Telegram.";
        }
        finally
        {
            TelegramTestButton.IsEnabled = true;
        }
    }

    private void TelegramSummary_Changed(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var enabled = TelegramSummaryCheck.IsChecked == true;
        if (prefs.TelegramShiftSummaryEnabled == enabled)
            return;
        prefs.TelegramShiftSummaryEnabled = enabled;
        prefs.SaveToDisk();
    }

    /// <summary>Открывает мастер подключения бота. После закрытия перечитываем поля: мастер
    /// мог сохранить токен, получателя и переключатели, и настройки не должны показывать
    /// устаревшие значения.</summary>
    private async void TelegramWizard_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = new TelegramBotSetupWindow();

        if (owner != null)
            await window.ShowDialog(owner).ConfigureAwait(true);
        else
            window.Show();

        var prefs = UserPreferences.Instance;
        TelegramTokenBox.Text = prefs.TelegramBotToken ?? "";
        TelegramSummaryCheck.IsChecked = prefs.TelegramShiftSummaryEnabled;
        TelegramCommandsCheck.IsChecked = prefs.TelegramCommandsEnabled;
        UpdateTelegramStatus();
    }

    private void TelegramCommands_Changed(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var enabled = TelegramCommandsCheck.IsChecked == true;
        if (prefs.TelegramCommandsEnabled == enabled)
            return;

        prefs.TelegramCommandsEnabled = enabled;
        prefs.SaveToDisk();

        // Опрос поднимаем (или гасим) сразу, не дожидаясь перезапуска кассы: владелец только
        // что включил функцию и ждёт, что бот начнёт отвечать прямо сейчас.
        App.GetRequiredService<MainWindowHostBridge>().Window?.StartTelegramBot();
    }

    /// <summary>Ручная рассылка напоминаний. Уходит только подписавшимся на бота — Telegram не
    /// позволяет написать человеку по номеру телефона (см. TelegramBotPollingService).</summary>
    private async void TelegramDebtReminders_Click(object? sender, RoutedEventArgs e)
    {
        if (!TelegramBotService.IsConfigured)
        {
            TelegramStatusText.Text = "Сначала подключите бота: вставьте токен и определите получателя.";
            return;
        }

        TelegramDebtRemindersButton.IsEnabled = false;
        TelegramStatusText.Text = "Рассылаю напоминания…";
        try
        {
            var bot = new TelegramBotPollingService(
                App.GetRequiredService<NurMarketKassa.Services.Api.ISalesApiService>(),
                App.GetRequiredService<NurMarketKassa.Services.Api.IClientsApiService>());

            var sent = await bot.SendDebtRemindersAsync().ConfigureAwait(true);
            TelegramStatusText.Text = sent > 0
                ? $"Отправлено напоминаний: {sent}."
                : "Некому отправлять: должники не подписаны на бота. Список со ссылками WhatsApp пришлёт команда /dolgi.";
        }
        catch (Exception ex)
        {
            TelegramStatusText.Text = "Не удалось разослать напоминания: " + ex.Message;
        }
        finally
        {
            TelegramDebtRemindersButton.IsEnabled = true;
        }
    }

    private void OwnerPhone_LostFocus(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var phone = (OwnerPhoneBox.Text ?? "").Trim();
        if (prefs.OwnerPhone == phone)
            return;
        prefs.OwnerPhone = phone;
        prefs.SaveToDisk();
    }

    /// <summary>Переключатель «QR с суммой чека». По умолчанию выключен: формула контрольной
    /// суммы ELQR сверена с настоящими QR MBank, но приложения других банков на таком коде не
    /// проверялись — включать осознанно, после пробного сканирования.</summary>
    private void DynamicQr_Changed(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        var enabled = DynamicQrCheck.IsChecked == true;
        if (prefs.DynamicPaymentQrEnabled == enabled)
            return;
        prefs.DynamicPaymentQrEnabled = enabled;
        prefs.SaveToDisk();
    }


    /// <summary>Сохраняет отметки «показывать при оплате». Пишем весь список целиком, а не
    /// по одной записи: так в настройках не остаётся банков, которые владелец уже снял.</summary>
    private void BankRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BankQrSetting.ShowAtCheckout) || _bankSettings is null)
            return;

        var prefs = UserPreferences.Instance;
        prefs.VisibleBankNames = _bankSettings
            .Where(x => x.ShowAtCheckout)
            .Select(x => x.BankName)
            .ToList();
        prefs.SaveToDisk();
    }

    private void AddBank_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;

        var name = AddBankDialog.Show(owner, KyrgyzBanks.All
            .Where(x => !_banks.Contains(x, StringComparer.OrdinalIgnoreCase)
                && !UserPreferences.Instance.CustomBankNames.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList());
        if (string.IsNullOrWhiteSpace(name))
            return;

        var prefs = UserPreferences.Instance;
        bool alreadyExists = _banks.Contains(name, StringComparer.OrdinalIgnoreCase)
            || prefs.CustomBankNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (alreadyExists)
        {
            PosMessageBox.Show(
                "Банк с таким названием уже есть в списке.",
                "Новый банк",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        prefs.CustomBankNames.Add(name);
        if (!prefs.VisibleBankNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            // Банк добавляют, чтобы им платить. Заставлять после этого искать галочку —
            // лишний шаг, который владелец пропустит и решит, что добавление не сработало.
            if (prefs.VisibleBankNames.Count == 0)
                prefs.VisibleBankNames = KyrgyzBanks.DefaultVisible.ToList();
            prefs.VisibleBankNames.Add(name);
        }
        prefs.SaveToDisk();
        LoadBankQrSettings();
    }

    private void RemoveBank_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting } || !setting.IsCustom)
            return;

        if (PosMessageBox.Show(
                $"Удалить банк {setting.BankName} из списка вместе с загруженным QR-кодом?",
                "Удаление банка",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var prefs = UserPreferences.Instance;
        string? previousQrPath = setting.QrCodePath;
        prefs.CustomBankNames.RemoveAll(b => string.Equals(b, setting.BankName, StringComparison.OrdinalIgnoreCase));
        prefs.BankQrPaths?.Remove(setting.BankName);
        prefs.BankLogoPaths?.Remove(setting.BankName);
        prefs.SaveToDisk();
        DeleteManagedQrFile(previousQrPath);
        LoadBankQrSettings();
    }

    private async void ChangeLogo_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting })
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Выберите логотип для {setting.BankName}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Изображения") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
            return;

        setting.LogoPath = path;

        var prefs = UserPreferences.Instance;
        prefs.BankLogoPaths ??= new Dictionary<string, string>();
        prefs.BankLogoPaths[setting.BankName] = path;
        prefs.SaveToDisk();
    }

    private async void LoadQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not BankQrSetting setting)
            return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
            return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = $"Выберите QR-код для банка {setting.BankName}",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Изображения") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } }
            }
        });

        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path))
            return;

        if (topLevel is Window owner)
            await OpenQrEditorAsync(owner, setting, path);
    }

    private async void EditQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting }
            || string.IsNullOrWhiteSpace(setting.QrCodePath)
            || !File.Exists(setting.QrCodePath)
            || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        await OpenQrEditorAsync(owner, setting, setting.QrCodePath);
    }

    private void RemoveQrCode_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BankQrSetting setting })
            return;

        if (PosMessageBox.Show(
                $"Убрать QR-код банка {setting.BankName}?",
                "Удаление QR-кода",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        string? previousPath = setting.QrCodePath;
        setting.QrCodePath = null;
        SaveBankQrSettings();
        DeleteManagedQrFile(previousPath);
    }

    private async Task OpenQrEditorAsync(Window owner, BankQrSetting setting, string sourcePath)
    {
        string? previousPath = setting.QrCodePath;
        var dialog = new QrCropDialog(sourcePath, setting.BankName);
        string? editedPath = await dialog.ShowDialog<string?>(owner);
        if (string.IsNullOrWhiteSpace(editedPath))
            return;

        setting.QrCodePath = editedPath;
        SaveBankQrSettings();

        if (!string.Equals(previousPath, editedPath, StringComparison.OrdinalIgnoreCase))
            DeleteManagedQrFile(previousPath);
    }

    private static void DeleteManagedQrFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            string managedDirectory = Path.GetFullPath(QrCropDialog.GetManagedQrDirectory())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            if (candidate.StartsWith(managedDirectory, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                File.Delete(candidate);
        }
        catch
        {
            // The preference is already removed; failure to clean an old managed copy is harmless.
        }
    }

    private void SaveBankQrSettings()
    {
        var prefs = UserPreferences.Instance;
        prefs.BankQrPaths ??= new Dictionary<string, string>();
        prefs.BankQrPaths.Clear();
        foreach (var bs in _bankSettings)
        {
            if (!string.IsNullOrEmpty(bs.QrCodePath))
                prefs.BankQrPaths[bs.BankName] = bs.QrCodePath;
        }

        prefs.SaveToDisk();

        // Экран покупателя кеширует InformationImagePath до следующего события
        // (продажа/статус оплаты). Без явного пинка новый QR не появится там,
        // пока не пройдёт следующая продажа.
        _ = App.GetRequiredService<AvaloniaCustomerDisplayService>().ApplySettingsAsync();
    }
}
