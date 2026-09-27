using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// Мастер подключения телеграм-бота: от создания у @BotFather до пробного сообщения.
///
/// Зачем отдельное окно, а не поля в настройках: без объяснения порядка действий владелец
/// упирался в то, что токен вставлен, а сводка не приходит — потому что Telegram не даёт боту
/// написать первым, и сначала нужно самому нажать «Старт» у своего бота. Здесь шаги идут по
/// порядку и каждый следующий включается только после того, как предыдущий действительно
/// выполнен.
///
/// Создать бота из программы нельзя в принципе: @BotFather — обычный чат в Telegram, API для
/// регистрации ботов у него нет ни у кого. Поэтому первый шаг открывает чат, а дальше касса
/// делает всё сама.
/// </summary>
public partial class TelegramBotSetupWindow : Window
{
    private string? _botUsername;

    public TelegramBotSetupWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        var prefs = UserPreferences.Instance;
        TokenBox.Text = prefs.TelegramBotToken ?? "";
        SummaryCheck.IsChecked = prefs.TelegramShiftSummaryEnabled;
        CommandsCheck.IsChecked = prefs.TelegramCommandsEnabled;

        // Бот уже подключён — открываем окно сразу в «рабочем» состоянии, чтобы владелец мог
        // проверить связь или переключить настройки, не проходя шаги заново.
        _botUsername = prefs.TelegramBotUsername;
        if (!string.IsNullOrWhiteSpace(_botUsername))
        {
            OpenMyBotButton.IsEnabled = true;
            DetectButton.IsEnabled = true;
            TokenStatus.Text = Tr.T($"Бот подключён: @{_botUsername}", $"Бот туташтырылды: @{_botUsername}", $"Bot connected: @{_botUsername}", $"Bot bağlandı: @{_botUsername}", $"Bot ulandi: @{_botUsername}");
        }

        if (!string.IsNullOrWhiteSpace(prefs.TelegramChatId))
        {
            TestButton.IsEnabled = true;
            DetectStatus.Text = string.IsNullOrWhiteSpace(prefs.TelegramChatTitle)
                ? Tr.T($"Получатель определён (чат {prefs.TelegramChatId}).", $"Алуучу аныкталды (чат {prefs.TelegramChatId}).", $"Recipient detected (chat {prefs.TelegramChatId}).", $"Alıcı belirlendi (sohbet {prefs.TelegramChatId}).", $"Qabul qiluvchi aniqlandi (chat {prefs.TelegramChatId}).")
                : Tr.T($"Получатель: {prefs.TelegramChatTitle}.", $"Алуучу: {prefs.TelegramChatTitle}.", $"Recipient: {prefs.TelegramChatTitle}.", $"Alıcı: {prefs.TelegramChatTitle}.", $"Qabul qiluvchi: {prefs.TelegramChatTitle}.");
        }
    }

    private void OpenBotFather_Click(object? sender, RoutedEventArgs e) => OpenUrl("https://t.me/BotFather");

    private void OpenMyBot_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_botUsername))
            OpenUrl($"https://t.me/{_botUsername}");
    }

    private async void CopyNewBot_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard != null)
            await clipboard.SetTextAsync("/newbot").ConfigureAwait(true);
    }

    private async void PasteToken_Click(object? sender, RoutedEventArgs e)
    {
        var clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        var text = await clipboard.GetTextAsync().ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(text))
            TokenBox.Text = text.Trim();
    }

    /// <summary>Проверяет токен через getMe и запоминает его. Пока токен не подтверждён, шаги 3
    /// и 4 заблокированы: без рабочего токена они всё равно ничего не сделают, а кассир получил
    /// бы невнятную ошибку.</summary>
    private async void CheckToken_Click(object? sender, RoutedEventArgs e)
    {
        var token = (TokenBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            TokenStatus.Text = Tr.T("Вставьте токен из чата с BotFather.", "BotFather чатынан алынган токенди коюңуз.", "Paste the token from the chat with BotFather.", "BotFather sohbetindeki token'ı yapıştırın.", "BotFather bilan chatdagi tokenni qo'ying.");
            return;
        }

        CheckTokenButton.IsEnabled = false;
        TokenStatus.Text = Tr.T("Проверяю токен…", "Токен текшерилүүдө…", "Checking the token…", "Token kontrol ediliyor…", "Token tekshirilmoqda…");
        try
        {
            var (username, title, error) = await TelegramBotService.GetBotInfoAsync(token).ConfigureAwait(true);
            if (error != null || string.IsNullOrWhiteSpace(username))
            {
                TokenStatus.Text = error ?? Tr.T("Telegram не принял токен.", "Telegram токенди кабыл алган жок.", "Telegram rejected the token.", "Telegram token'ı kabul etmedi.", "Telegram tokenni qabul qilmadi.");
                OpenMyBotButton.IsEnabled = false;
                DetectButton.IsEnabled = false;
                return;
            }

            var prefs = UserPreferences.Instance;
            prefs.TelegramBotToken = token;
            prefs.TelegramBotUsername = username;
            prefs.SaveToDisk();

            _botUsername = username;
            OpenMyBotButton.IsEnabled = true;
            DetectButton.IsEnabled = true;
            TokenStatus.Text = string.IsNullOrWhiteSpace(title)
                ? Tr.T($"Токен верный. Бот: @{username}", $"Токен туура. Бот: @{username}", $"Token is valid. Bot: @{username}", $"Token geçerli. Bot: @{username}", $"Token to'g'ri. Bot: @{username}")
                : Tr.T($"Токен верный. Бот: {title} (@{username})", $"Токен туура. Бот: {title} (@{username})", $"Token is valid. Bot: {title} (@{username})", $"Token geçerli. Bot: {title} (@{username})", $"Token to'g'ri. Bot: {title} (@{username})");
        }
        finally
        {
            CheckTokenButton.IsEnabled = true;
        }
    }

    private async void Detect_Click(object? sender, RoutedEventArgs e)
    {
        DetectButton.IsEnabled = false;
        DetectStatus.Text = Tr.T("Спрашиваю Telegram…", "Telegram'дан суралууда…", "Asking Telegram…", "Telegram'a soruluyor…", "Telegram'dan so'ralmoqda…");
        try
        {
            var (chatId, name, error) = await TelegramBotService
                .TryDetectChatIdAsync(UserPreferences.Instance.TelegramBotToken ?? "")
                .ConfigureAwait(true);

            if (error != null || chatId == null)
            {
                DetectStatus.Text = error ?? Tr.T("Не удалось определить получателя.", "Алуучуну аныктоо мүмкүн болгон жок.", "Could not detect the recipient.", "Alıcı belirlenemedi.", "Qabul qiluvchini aniqlab bo'lmadi.");
                return;
            }

            var prefs = UserPreferences.Instance;
            prefs.TelegramChatId = chatId;
            prefs.TelegramChatTitle = name;
            prefs.SaveToDisk();

            TestButton.IsEnabled = true;
            DetectStatus.Text = string.IsNullOrWhiteSpace(name)
                ? Tr.T($"Готово: сводки пойдут в чат {chatId}.", $"Даяр: жыйынтыктар {chatId} чатына жөнөтүлөт.", $"Done: summaries will go to chat {chatId}.", $"Hazır: özetler {chatId} sohbetine gönderilecek.", $"Tayyor: hisobotlar {chatId} chatiga yuboriladi.")
                : Tr.T($"Готово: сводки пойдут в чат «{name}».", $"Даяр: жыйынтыктар «{name}» чатына жөнөтүлөт.", $"Done: summaries will go to the “{name}” chat.", $"Hazır: özetler «{name}» sohbetine gönderilecek.", $"Tayyor: hisobotlar «{name}» chatiga yuboriladi.");

            StartBotIfEnabled();
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestStatus.Text = Tr.T("Отправляю…", "Жөнөтүлүүдө…", "Sending…", "Gönderiliyor…", "Yuborilmoqda…");
        try
        {
            var shop = UserPreferences.Instance.StoreName;
            var error = await TelegramBotService
                .SendAsync($"<b>{shop}</b>\n\nПробное сообщение из кассы. Если вы его видите — бот подключён.\n\n"
                           + "Попробуйте команду /segodnya.")
                .ConfigureAwait(true);
            TestStatus.Text = error ?? Tr.T("Отправлено — проверьте Telegram.", "Жөнөтүлдү — Telegram'ды текшериңиз.", "Sent — check Telegram.", "Gönderildi — Telegram'ı kontrol edin.", "Yuborildi — Telegram'ni tekshiring.");
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Toggles_Changed(object? sender, RoutedEventArgs e)
    {
        // Loaded ещё не отработал — не перезаписываем настройки значениями по умолчанию.
        if (!IsLoaded)
            return;

        var prefs = UserPreferences.Instance;
        prefs.TelegramShiftSummaryEnabled = SummaryCheck.IsChecked == true;
        prefs.TelegramCommandsEnabled = CommandsCheck.IsChecked == true;
        prefs.SaveToDisk();
        StartBotIfEnabled();
    }

    private static void StartBotIfEnabled()
    {
        try
        {
            App.GetRequiredService<MainWindowHostBridge>().Window?.StartTelegramBot();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Телеграм-бот: перезапуск из мастера не удался ({ex.Message}).", "WARNING");
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось открыть ссылку {url}: {ex.Message}", "WARNING");
        }
    }
}
