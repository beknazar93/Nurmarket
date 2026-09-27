using System.Globalization;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// Настройки прямого подключения весов: адрес, порт, пароль и проверка связи.
///
/// Раньше эти поля жили прямо в строке окна «Весы», рядом с «Начальный PLU» и галочкой
/// «Напрямую по кабелю». В одну строку они не помещались: на окне 900 px пароль и кнопка
/// «Проверить» уезжали за правый край и были недоступны вовсе. Отдельное окно решает это
/// по существу, а не подгонкой ширины: полям есть где встать, к каждому есть подпись и
/// пояснение, а проверка связи показывает ответ весов прямо здесь.
/// </summary>
public partial class ScaleConnectionDialog : Window
{
    public ScaleConnectionDialog()
    {
        InitializeComponent();
        LoadFromPreferences();
    }

    private void LoadFromPreferences()
    {
        var prefs = UserPreferences.Instance;
        IpBox.Text = prefs.ScaleNetworkIp ?? "";
        PortBox.Text = prefs.ScaleLanPort.ToString(CultureInfo.InvariantCulture);
        PasswordBox.Text = prefs.ScaleLanPassword ?? "";
    }

    /// <summary>Проверка связи намеренно использует команды БЕЗ пароля — весы отвечают на неё
    /// даже когда заблокировали доступ из-за неудачных попыток входа. Так видно, что связь
    /// есть, а дело именно в пароле.</summary>
    private async void Test_Click(object? sender, RoutedEventArgs e)
    {
        if (!TrySave(out var error))
        {
            ShowStatus(error!);
            return;
        }

        TestButton.IsEnabled = false;
        ShowStatus(Tr.T("Проверяю связь с весами…", "Тараза менен байланыш текшерилүүдө…", "Checking the connection to the scale…", "Tartı bağlantısı kontrol ediliyor…", "Tarozi bilan aloqa tekshirilmoqda…"));
        try
        {
            var prefs = UserPreferences.Instance;
            using var scale = new ShtrikhPrintLanScaleService(
                prefs.ScaleNetworkIp ?? "", prefs.ScaleLanPort, prefs.ScaleLanPassword);

            var info = await scale.TestConnectionAsync(beep: true, CancellationToken.None).ConfigureAwait(true);
            ShowStatus(Tr.T("Весы на связи: ", "Тараза байланышта: ", "Scale connected: ", "Tartı bağlı: ", "Tarozi aloqada: ") + info);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Проверка связи с весами из диалога: {ex}", "SCALES");
            ShowStatus(ex.Message);
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!TrySave(out var error))
        {
            ShowStatus(error!);
            return;
        }

        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

    /// <summary>Проверяет введённое и сохраняет. Пустой пароль не затирает сохранённый: пустой
    /// пароль весы не примут, а случайно очищенное поле молча сломало бы выгрузку.</summary>
    private bool TrySave(out string? error)
    {
        error = null;
        var prefs = UserPreferences.Instance;

        var ip = (IpBox.Text ?? "").Trim();
        if (ip.Length == 0)
        {
            error = Tr.T(
                "Укажите IP-адрес весов — он есть в их системном меню.",
                "Таразанын IP-дарегин көрсөтүңүз — ал таразанын системалык менюсунда бар.",
                "Enter the scale's IP address — you'll find it in the scale's system menu.",
                "Tartının IP adresini girin — tartının sistem menüsünde bulunur.",
                "Tarozining IP manzilini kiriting — u tarozining tizim menyusida bor.");
            return false;
        }

        var portText = (PortBox.Text ?? "").Trim();
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is <= 0 or > 65535)
        {
            error = Tr.T(
                "Порт должен быть числом от 1 до 65535. Обычно 1111.",
                "Порт 1ден 65535ке чейинки сан болушу керек. Адатта 1111.",
                "The port must be a number from 1 to 65535. Usually 1111.",
                "Port 1 ile 65535 arasında bir sayı olmalıdır. Genellikle 1111.",
                "Port 1 dan 65535 gacha bo'lgan son bo'lishi kerak. Odatda 1111.");
            return false;
        }

        var password = (PasswordBox.Text ?? "").Trim();
        if (password.Length > 0 && (password.Length != 4 || !password.All(char.IsDigit)))
        {
            error = Tr.T(
                "Пароль весов — ровно 4 цифры. Обычно 0030.",
                "Таразанын сырсөзү — так 4 цифра. Адатта 0030.",
                "The scale password is exactly 4 digits. Usually 0030.",
                "Tartı şifresi tam 4 hanelidir. Genellikle 0030.",
                "Tarozi paroli — aniq 4 ta raqam. Odatda 0030.");
            return false;
        }

        prefs.ScaleNetworkIp = ip;
        prefs.ScaleLanPort = port;
        if (password.Length > 0)
            prefs.ScaleLanPassword = password;
        prefs.SaveToDisk();
        return true;
    }

    private void ShowStatus(string text)
    {
        StatusPanel.IsVisible = true;
        StatusText.Text = text;
    }
}
