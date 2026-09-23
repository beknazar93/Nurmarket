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
            TokenStatus.Text = $"Бот подключён: @{_botUsername}";
        }

        if (!string.IsNullOrWhiteSpace(prefs.TelegramChatId))
        {
            TestButton.IsEnabled = true;
            DetectStatus.Text = string.IsNullOrWhiteSpace(prefs.TelegramChatTitle)
                ? $"Получатель определён (чат {prefs.TelegramChatId})."
                : $"Получатель: {prefs.TelegramChatTitle}.";
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
            TokenStatus.Text = "Вставьте токен из чата с BotFather.";
            return;
        }

        CheckTokenButton.IsEnabled = false;
        TokenStatus.Text = "Проверяю токен…";
        try
        {
            var (username, title, error) = await TelegramBotService.GetBotInfoAsync(token).ConfigureAwait(true);
            if (error != null || string.IsNullOrWhiteSpace(username))
            {
                TokenStatus.Text = error ?? "Telegram не принял токен.";
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
                ? $"Токен верный. Бот: @{username}"
                : $"Токен верный. Бот: {title} (@{username})";
        }
        finally
        {
            CheckTokenButton.IsEnabled = true;
        }
    }

    private async void Detect_Click(object? sender, RoutedEventArgs e)
    {
        DetectButton.IsEnabled = false;
        DetectStatus.Text = "Спрашиваю Telegram…";
        try
        {
            var (chatId, name, error) = await TelegramBotService
                .TryDetectChatIdAsync(UserPreferences.Instance.TelegramBotToken ?? "")
                .ConfigureAwait(true);

            if (error != null || chatId == null)
            {
                DetectStatus.Text = error ?? "Не удалось определить получателя.";
                return;
            }

            var prefs = UserPreferences.Instance;
            prefs.TelegramChatId = chatId;
            prefs.TelegramChatTitle = name;
            prefs.SaveToDisk();

            TestButton.IsEnabled = true;
            DetectStatus.Text = string.IsNullOrWhiteSpace(name)
                ? $"Готово: сводки пойдут в чат {chatId}."
                : $"Готово: сводки пойдут в чат «{name}».";

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
        TestStatus.Text = "Отправляю…";
        try
        {
            var shop = UserPreferences.Instance.StoreName;
            var error = await TelegramBotService
                .SendAsync($"<b>{shop}</b>\n\nПробное сообщение из кассы. Если вы его видите — бот подключён.\n\n"
                           + "Попробуйте команду /segodnya.")
                .ConfigureAwait(true);
            TestStatus.Text = error ?? "Отправлено — проверьте Telegram.";
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
