using System.Net.NetworkInformation;
using Avalonia;
using Avalonia.Controls;
using NurMarketKassa.Services.Hardware;
using System.Threading;
using System.Globalization;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class ScaleSettingsView : UserControl
{
    private const string BrandShtrikh = "shtrikh";
    private const string BrandRongta = "rongta";

    public event EventHandler? SaveRequested;

    public ScaleSettingsView()
    {
        InitializeComponent();
    }

    private void ScaleSettingsView_Loaded(object? sender, RoutedEventArgs e)
    {
        LoadScaleBrand();
        PluScaleIpBox.Text = UserPreferences.Instance.ScaleNetworkIp;

        RefreshPluCardVisibility();
    }

    /// <summary>Карточка отправки PLU на сетевые весы скрыта целиком на тарифе «Старт», пока
    /// доп. услуга не куплена (см. TariffGate.CanUseScales) — вызывается и при повторном открытии
    /// окна настроек на случай, если кассир только что купил её в Маркетплейсе, не закрывая это
    /// окно (тот же приём, что и RefreshPaidFeatureVisibility в WarehouseWindow).</summary>
    public void RefreshPluCardVisibility() => PluCard.IsVisible = TariffGate.CanUseScales;

    private const string BrandAi = "ai";

    private void PluBrandRadio_Click(object? sender, RoutedEventArgs e)
    {
        // Выбор модели дублируется здесь и в окне «Весы» — обе точки пишут в одну и ту же
        // настройку ScaleBrand. 2026-09-22: добавив «AI весы» только в окно, я оставил это
        // место без новой модели, и владелец справедливо спросил, куда она делась.
        UserPreferences.Instance.ScaleBrand =
            PluBrandRongtaRadio.IsChecked == true ? BrandRongta
            : PluBrandAiRadio.IsChecked == true ? BrandAi
            : BrandShtrikh;
        UserPreferences.Instance.SaveToDisk();
    }

    /// <summary>Отмечает модель, сохранённую в настройках. Без этого экран всегда открывался
    /// на «Штрих-М», чем бы владелец ни пользовался.</summary>
    public void LoadScaleBrand()
    {
        var brand = UserPreferences.Instance.ScaleBrand;
        PluBrandRongtaRadio.IsChecked = brand == BrandRongta;
        PluBrandAiRadio.IsChecked = brand == BrandAi;
        PluBrandShtrikhRadio.IsChecked = brand != BrandRongta && brand != BrandAi;
    }

    /// <summary>По просьбе владельца (2026-09-19: "где настройка и ввод ip чтобы узнать
    /// статус подключение весов по лан") — по образцу уже существующей "Проверить весы" для
    /// COM-весов выше. Это ЧИСТО диагностика (обычный ICMP ping) — сама отправка PLU этот IP
    /// не использует: у Штрих-М канал серверный, у Rongta — через RLS1000 (см.
    /// RongtaScaleAutomationService). Порт весов не задокументирован нигде, поэтому TCP-connect
    /// к конкретному порту не делаем — ping достаточен, чтобы понять "весы в сети или нет".</summary>
    private async void PluCheckConnection_Click(object? sender, RoutedEventArgs e)
    {
        var ip = (PluScaleIpBox.Text ?? "").Trim();
        if (ip.Length == 0)
        {
            ShowPluIpAlert(Tr.T("Введите IP-адрес весов.", "Таразанын IP-дарегин киргизиңиз.",
                "Enter the scale's IP address.", "Tartının IP adresini girin.", "Tarozining IP manzilini kiriting."), true);
            return;
        }

        UserPreferences.Instance.ScaleNetworkIp = ip;
        UserPreferences.Instance.SaveToDisk();

        PluCheckConnectionButton.IsEnabled = false;
        ShowPluIpAlert(Tr.T("Проверка…", "Текшерилүүдө…", "Checking…", "Kontrol ediliyor…", "Tekshirilmoqda…"), false);
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(ip, 2000).ConfigureAwait(true);
            if (reply.Status == IPStatus.Success)
            {
                ShowPluIpAlert(Tr.T(
                    $"Весы доступны в сети ({reply.RoundtripTime} мс).",
                    $"Тараза тармакта жеткиликтүү ({reply.RoundtripTime} мс).",
                    $"The scale is reachable on the network ({reply.RoundtripTime} ms).",
                    $"Tartı ağda erişilebilir ({reply.RoundtripTime} ms).",
                    $"Tarozi tarmoqda mavjud ({reply.RoundtripTime} ms)."), false);
            }
            else
            {
                ShowPluIpAlert(Tr.T(
                    $"Весы не отвечают ({reply.Status}). Проверьте IP и что весы включены и в той же сети.",
                    $"Тараза жооп бербейт ({reply.Status}). IP жана тараза күйгүзүлгөнүн текшериңиз.",
                    $"The scale is not responding ({reply.Status}). Check the IP and that the scale is powered on and on the same network.",
                    $"Tartı yanıt vermiyor ({reply.Status}). IP'yi ve tartının açık olduğunu kontrol edin.",
                    $"Tarozi javob bermayapti ({reply.Status}). IP va tarozi yoqilganini tekshiring."), true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Scale IP ping failed: {ex.Message}", "SCALES");
            ShowPluIpAlert(Tr.T("Ошибка проверки: ", "Текшерүү катасы: ", "Check error: ", "Kontrol hatası: ", "Tekshirish xatosi: ") + ex.Message, true);
        }
        finally
        {
            PluCheckConnectionButton.IsEnabled = true;
        }
    }

    private void ShowPluIpAlert(string message, bool isError)
    {
        PluIpAlert.IsVisible = true;
        PluIpAlertText.Text = message;
        PluIpAlert.Background = ThemeBrush(isError ? "BrushWarningSoft" : "BrushSuccessSoft", Brushes.LightGoldenrodYellow);
        PluIpAlert.BorderBrush = ThemeBrush(isError ? "BrushWarning" : "BrushSuccess", Brushes.Goldenrod);
    }

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : fallback;

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);

    private void OpenScalesPlu_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = App.GetRequiredService<ScalesPluWindow>();
        window.Show(owner);
    }

    /// <summary>Заполняет поля сетевых весов. Вызывается вместе с остальной загрузкой
    /// настроек экрана.</summary>
    public void LoadLanScaleSettings()
    {
        var prefs = UserPreferences.Instance;
        LanScaleIpBox.Text = prefs.ScaleNetworkIp ?? "";
        LanScalePortBox.Text = prefs.ScaleLanPort.ToString(CultureInfo.InvariantCulture);
        LanScalePasswordBox.Text = prefs.ScaleLanPassword ?? "";
    }

    /// <summary>Сохраняем по уходу с поля, а не по кнопке: владелец правит адрес и сразу
    /// жмёт «Проверить связь», и настройки уже должны быть записаны.</summary>
    private void LanScale_LostFocus(object? sender, RoutedEventArgs e) => SaveLanScaleSettings();

    private void SaveLanScaleSettings()
    {
        var prefs = UserPreferences.Instance;
        prefs.ScaleNetworkIp = (LanScaleIpBox.Text ?? "").Trim();

        if (int.TryParse((LanScalePortBox.Text ?? "").Trim(), out var port) && port is > 0 and <= 65535)
            prefs.ScaleLanPort = port;

        // Пустое поле не затирает сохранённый пароль: пустой пароль весы не примут, а
        // случайно очищенное поле молча сломало бы выгрузку.
        var password = (LanScalePasswordBox.Text ?? "").Trim();
        if (password.Length > 0)
            prefs.ScaleLanPassword = password;

        prefs.SaveToDisk();
    }

    /// <summary>Открывает то же окно настроек подключения, что и «Весы» → «Напрямую по
    /// кабелю». Одно место правки на всю программу: раньше адрес и пароль можно было менять
    /// в двух разных местах, и они расходились.</summary>
    private async void OpenLanScaleDialog_Click(object? sender, RoutedEventArgs e)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleConnectionDialog();

        if (owner != null)
            await dialog.ShowDialog(owner).ConfigureAwait(true);
        else
            dialog.Show();

        LoadLanScaleSettings();
    }

    /// <summary>«Проверить связь» — опознаёт весы и подаёт гудок. Намеренно использует только
    /// команды БЕЗ пароля, поэтому отвечает даже тогда, когда весы заблокировали доступ из-за
    /// неудачных попыток: владелец видит, что связь есть, а дело именно в пароле.</summary>
    private async void TestLanScale_Click(object? sender, RoutedEventArgs e)
    {
        SaveLanScaleSettings();
        TestLanScaleButton.IsEnabled = false;
        LanScaleAlert.IsVisible = true;
        LanScaleAlertText.Text = "Проверяю связь с весами…";

        try
        {
            var prefs = UserPreferences.Instance;
            using var scale = new ShtrikhPrintLanScaleService(
                prefs.ScaleNetworkIp ?? "", prefs.ScaleLanPort, prefs.ScaleLanPassword);

            var info = await scale.TestConnectionAsync(beep: true, CancellationToken.None).ConfigureAwait(true);
            LanScaleAlertText.Text = "Весы на связи: " + info;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Проверка связи с сетевыми весами: {ex}", "SCALES");
            LanScaleAlertText.Text = ex.Message;
        }
        finally
        {
            TestLanScaleButton.IsEnabled = true;
        }
    }
}
