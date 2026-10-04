using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// «Тех поддержка NurCRM» (этап 4 бэклога «Доработки») — запускает СТАНДАРТНЫЙ клиент AnyDesk
/// (устанавливать/поддерживать собственный протокол удалённого рабочего стола с нуля внутри
/// кассы — отдельная и очень рискованная для боевой POS-системы задача, пользователь явно
/// выбрал переиспользовать готовый проверенный инструмент вместо этого). Заранее настроенного
/// ID техподдержки NurMarket здесь нет — для этого нужен отдельный бизнес-аккаунт AnyDesk с
/// собственной сборкой клиента, это вне рамок правки кода; сейчас открывается ОБЫЧНЫЙ AnyDesk,
/// который сам генерирует одноразовый адрес подключения — кассир называет его оператору по
/// телефону/WhatsApp.
/// </summary>
public partial class RemoteSupportWindow : Window, IOwnerSection
{
    private static readonly string[] KnownAnyDeskPaths =
    {
        // В комплекте с самой кассой (см. AnyDesk\AnyDesk.exe в проекте, CopyToOutputDirectory —
        // копируется рядом с NurMarketKassa.Avalonia.exe при публикации) — проверяется первым,
        // так что установленная отдельно копия AnyDesk не обязательна.
        Path.Combine(AppContext.BaseDirectory, "AnyDesk", "AnyDesk.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AnyDesk", "AnyDesk.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "AnyDesk", "AnyDesk.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AnyDesk", "AnyDesk.exe"),
    };

    private const string AnyDeskDownloadUrl = "https://anydesk.com/en/downloads/windows";

    public RemoteSupportWindow()
    {
        InitializeComponent();
        RefreshStatus();
        // 2026-10-05: на Android AnyDesk отсюда не запустить — главная кнопка окна «Скопировать информацию об устройстве».
        if (OperatingSystem.IsAndroid())
        {
            LaunchButton.IsVisible = false;
            DownloadButton.IsVisible = false;
            CopyDeviceInfoButton.Classes.Set("SecondaryActionButton", false);
            CopyDeviceInfoButton.Classes.Set("PrimaryActionButton", true);
            StatusText.Text = Tr.T(
                "Нажмите «Скопировать информацию об устройстве» и отправьте текст в поддержку NurMarket (Telegram, WhatsApp) — по нему программу подстроят под ваш аппарат. Удалённый доступ на Android — приложение AnyDesk из Google Play.",
                "«Түзмөк жөнүндө маалыматты көчүрүү» баскычын басып, текстти NurMarket колдоосуна жибериңиз (Telegram, WhatsApp) — ал боюнча программа аппаратыңызга ылайыкталат. Android'де алыстан кирүү — Google Play'деги AnyDesk колдонмосу.",
                "Tap “Copy device information” and send the text to NurMarket support (Telegram, WhatsApp) — it helps adapt the app to your device. Remote access on Android: the AnyDesk app from Google Play.",
                "«Cihaz bilgilerini kopyala»ya dokunun ve metni NurMarket desteğine gönderin (Telegram, WhatsApp) — program cihazınıza göre uyarlanır. Android'de uzaktan erişim: Google Play'deki AnyDesk uygulaması.",
                "«Qurilma ma'lumotlarini nusxalash»ni bosing va matnni NurMarket qo'llab-quvvatlash xizmatiga yuboring (Telegram, WhatsApp) — dastur qurilmangizga moslashtiriladi. Android'da masofaviy kirish — Google Play'dagi AnyDesk ilovasi.");
        }
    }

    /// <summary>2026-10-05, владелец: «скопировать информацию об устройстве — в буфер обмена».</summary>
    private async void CopyDeviceInfo_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var text = DeviceInfoReport.Build(this);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                throw new InvalidOperationException("буфер обмена недоступен");
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
            PosLogger.Log("Поддержка: информация об устройстве скопирована:\n" + text, "INFO");
            StatusText.Text = Tr.T(
                "Скопировано. Вставьте текст в чат поддержки NurMarket (долгое нажатие → «Вставить»).",
                "Көчүрүлдү. Текстти NurMarket колдоо чатына чаптаңыз (узак басуу → «Чаптоо»).",
                "Copied. Paste the text into the NurMarket support chat (long press → “Paste”).",
                "Kopyalandı. Metni NurMarket destek sohbetine yapıştırın (uzun basın → «Yapıştır»).",
                "Nusxalandi. Matnni NurMarket qo'llab-quvvatlash chatiga joylashtiring (uzoq bosing → «Joylashtirish»).");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поддержка: информацию об устройстве скопировать не удалось: {ex.Message}", "WARNING");
            StatusText.Text = Tr.T(
                $"Скопировать не удалось: {ex.Message}", $"Көчүрүү мүмкүн болгон жок: {ex.Message}",
                $"Could not copy: {ex.Message}", $"Kopyalanamadı: {ex.Message}", $"Nusxalab bo'lmadi: {ex.Message}");
        }
    }

    private static string? FindInstalledAnyDesk()
    {
        foreach (var path in KnownAnyDeskPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private void RefreshStatus()
    {
        var found = FindInstalledAnyDesk();
        if (found != null)
        {
            StatusText.Text = Tr.T(
                "AnyDesk найден на этом компьютере.",
                "AnyDesk бул компьютерде табылды.", "AnyDesk was found on this computer.", "AnyDesk bu bilgisayarda bulundu.", "AnyDesk shu kompyuterda topildi.");
            DownloadButton.IsVisible = false;
        }
        else
        {
            StatusText.Text = Tr.T(
                "AnyDesk не установлен. Нажмите «Запустить AnyDesk» — если он ещё не скачан, откроется страница загрузки.",
                "AnyDesk орнотулган эмес. «AnyDesk'ти иштетүү» баскычын басыңыз — эгер ал али жүктөлө элек болсо, жүктөө барагы ачылат.", "AnyDesk is not installed. Click “Launch AnyDesk” — if it has not been downloaded yet, the download page will open.", "AnyDesk yüklü değil. «AnyDesk'i başlat» düğmesine basın — henüz indirilmediyse indirme sayfası açılır.", "AnyDesk o'rnatilmagan. «AnyDesk'ni ishga tushirish» tugmasini bosing — agar u hali yuklab olinmagan bo'lsa, yuklab olish sahifasi ochiladi.");
            DownloadButton.IsVisible = true;
        }
    }

    private void LaunchButton_Click(object? sender, RoutedEventArgs e)
    {
        var path = FindInstalledAnyDesk();
        if (path == null)
        {
            OpenDownloadPage();
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            StatusText.Text = Tr.T(
                "AnyDesk запущен. Назовите оператору адрес (ID), который появится в его окне.",
                "AnyDesk иштетилди. Анын терезесинде чыккан даректи (ID) операторго айтыңыз.", "AnyDesk is running. Tell the support agent the address (ID) shown in its window.", "AnyDesk başlatıldı. Penceresinde görünecek adresi (ID) destek görevlisine bildirin.", "AnyDesk ishga tushdi. Uning oynasida paydo bo'ladigan manzilni (ID) operatorga ayting.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось запустить AnyDesk: {ex}", "WARNING");
            StatusText.Text = Tr.T(
                $"Не удалось запустить AnyDesk: {ex.Message}",
                $"AnyDesk иштетилген жок: {ex.Message}", $"Could not launch AnyDesk: {ex.Message}", $"AnyDesk başlatılamadı: {ex.Message}", $"AnyDesk'ni ishga tushirib bo'lmadi: {ex.Message}");
            DownloadButton.IsVisible = true;
        }
    }

    private void DownloadButton_Click(object? sender, RoutedEventArgs e) => OpenDownloadPage();

    private void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = AnyDeskDownloadUrl, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось открыть страницу загрузки AnyDesk: {ex}", "WARNING");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): значок с названием и
    /// «Закрыть» убраны — название над разделом. Окно рассчитано на 520 px; растянутое на весь
    /// раздел, оно разнесло бы текст и кнопку по краям экрана, поэтому содержимое остаётся колонкой
    /// привычной ширины у левого края, как текст остальных разделов.</summary>
    public void AsOwnerSection()
    {
        TitlePanel.IsVisible = false;
        CloseButton.IsVisible = false;
        IntroText.Margin = new Avalonia.Thickness(0);
        LaunchButton.Margin = new Avalonia.Thickness(0, 16, 0, 14);
        // Без белой подложки: в кассе она заполняла всё маленькое окно, а колонкой посреди
        // раздела выглядела бы карточкой, прилипшей к тексту.
        RootGrid.Background = Avalonia.Media.Brushes.Transparent;
        RootGrid.Margin = OwnerSectionLayout.Margin;
        RootGrid.MaxWidth = 600;
        RootGrid.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        RootGrid.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
    }
}
