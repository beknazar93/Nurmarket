using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-01 (просьба владельца: «принтеры и по LAN соединение тоже добавь»): блок «Сеть (LAN)» для принтера чеков и принтера этикеток —
/// поля «IP-адрес или имя» и «Порт (9100)», «Проверить связь» (только подключение, без печати) и «Найти в сети» (порт 9100 в подсети компьютера,
/// только по нажатию, с ходом и остановкой). Адрес проверяется (IPv4 или имя, порт 1–65535); пароли и секреты не запрашиваются.
/// Сохранение делает окно настроек: у принтера чеков — сразу в поле порта (<see cref="AddressApplied"/>), у принтера этикеток — по «Использовать» (<see cref="UseRequested"/>).</summary>
public sealed class NetworkPrinterPanel : StackPanel
{
    private readonly TextBox _host;
    private readonly TextBox _port;
    private readonly Button _test;
    private readonly Button _find;
    private readonly Button? _use;
    private readonly ComboBox _found;
    private readonly TextBlock _status;
    private CancellationTokenSource? _scanCts;
    private bool _syncing;

    /// <summary>Адрес изменён и верен (режим принтера чеков: сразу в поле порта).</summary>
    public event EventHandler? AddressApplied;

    /// <summary>Нажато «Использовать» с верным адресом (режим принтера этикеток).</summary>
    public event EventHandler<string>? UseRequested;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public NetworkPrinterPanel(bool showUseButton)
    {
        Spacing = 6;
        var caption = new TextBlock { Text = T("Сеть (LAN / Wi-Fi), порт 9100", "Тармак (LAN / Wi-Fi), порт 9100", "Network (LAN / Wi-Fi), port 9100", "Ağ (LAN / Wi-Fi), port 9100", "Tarmoq (LAN / Wi-Fi), port 9100"), FontSize = 12, FontWeight = FontWeight.SemiBold };
        caption.Classes.Add("FieldLabel");

        _host = new TextBox { Watermark = T("IP-адрес или имя, напр. 192.168.1.50", "IP-дарек же ат, мис. 192.168.1.50", "IP address or name, e.g. 192.168.1.50", "IP adresi veya ad, ör. 192.168.1.50", "IP-manzil yoki nom, masalan 192.168.1.50"), Height = 44, MaxLength = 253 };
        _host.Classes.Add("ModernTextBox");
        _port = new TextBox { Text = NetworkPrinterAddress.DefaultPort.ToString(), Watermark = "9100", Height = 44, Width = 96, MaxLength = 5 };
        _port.Classes.Add("ModernTextBox");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto") };
        row.Children.Add(_host);
        Grid.SetColumn(_port, 2);
        row.Children.Add(_port);

        _test = MakeButton(T("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish"));
        _find = MakeButton(T("Найти в сети", "Тармактан табуу", "Find on the network", "Ağda bul", "Tarmoqdan topish"));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_test);
        buttons.Children.Add(_find);
        if (showUseButton)
        {
            _use = MakeButton(T("Использовать этот принтер", "Ушул принтерди колдонуу", "Use this printer", "Bu yazıcıyı kullan", "Shu printerdan foydalanish"));
            buttons.Children.Add(_use);
        }

        _found = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44, IsVisible = false, PlaceholderText = T("Найденные принтеры (порт 9100)", "Табылган принтерлер (порт 9100)", "Found printers (port 9100)", "Bulunan yazıcılar (port 9100)", "Topilgan printerlar (port 9100)") };
        _found.Classes.Add("TouchComboBox");
        _status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };

        Children.Add(caption);
        Children.Add(row);
        Children.Add(buttons);
        Children.Add(_found);
        Children.Add(_status);

        _test.Click += async (_, _) => await TestAsync();
        _find.Click += async (_, _) => await FindAsync();
        if (_use != null)
            _use.Click += (_, _) =>
            {
                if (TryGetAddress(out var address))
                    UseRequested?.Invoke(this, address);
            };
        _host.TextChanged += (_, _) => OnEdited();
        _port.TextChanged += (_, _) => OnEdited();
        _found.SelectionChanged += (_, _) =>
        {
            if (_found.SelectedItem is string address)
            {
                SetAddress(address);
                OnEdited();
            }
        };
        DetachedFromVisualTree += (_, _) => _scanCts?.Cancel();
    }

    private static Button MakeButton(string text)
    {
        var b = new Button { Content = text, MinHeight = 44, Padding = new Thickness(14, 0) };
        b.Classes.Add("PrintGoldButton");
        return b;
    }

    /// <summary>Заполняет поля из порта; не сетевой порт — поля не меняются.</summary>
    public void SetAddress(string? port)
    {
        if (!NetworkPrinterAddress.IsNetworkPort(port) || !NetworkPrinterAddress.TryParse(port, out var host, out var tcpPort, out _))
            return;
        if (string.Equals(CurrentAddress, NetworkPrinterAddress.Format(host, tcpPort), StringComparison.OrdinalIgnoreCase))
            return; // поля уже показывают этот адрес — не трогаем (иначе при вводе прыгает курсор и меняется регистр)
        _syncing = true;
        try
        {
            _host.Text = host;
            _port.Text = tcpPort.ToString();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Верный адрес «TCP:host:port» из полей; ошибка — под полями.</summary>
    public bool TryGetAddress(out string address)
    {
        address = "";
        var host = (_host.Text ?? "").Trim();
        var portText = string.IsNullOrWhiteSpace(_port.Text) ? NetworkPrinterAddress.DefaultPort.ToString() : _port.Text!.Trim();
        if (!int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            ShowError(T("Порт должен быть числом от 1 до 65535.", "Порт 1ден 65535ке чейинки сан болушу керек.", "The port must be a number from 1 to 65535.", "Port 1 ile 65535 arasında bir sayı olmalı.", "Port 1 dan 65535 gacha son bo'lishi kerak."));
            return false;
        }
        if (!NetworkPrinterAddress.IsValidHost(host))
        {
            ShowError(host.Length == 0
                ? T("Укажите IP-адрес или имя принтера.", "Принтердин IP-дарегин же атын көрсөтүңүз.", "Enter the printer's IP address or name.", "Yazıcının IP adresini veya adını girin.", "Printerning IP-manzili yoki nomini kiriting.")
                : T("Неверный адрес: нужен IP (например 192.168.1.50) или имя.", "Дарек туура эмес: IP (мисалы 192.168.1.50) же ат керек.", "Invalid address: an IP (e.g. 192.168.1.50) or a name is required.", "Geçersiz adres: IP (ör. 192.168.1.50) veya ad gerekli.", "Manzil noto'g'ri: IP (masalan 192.168.1.50) yoki nom kerak."));
            return false;
        }
        address = NetworkPrinterAddress.Format(host, port);
        return true;
    }

    private void ShowError(string text)
    {
        _status.Text = text;
        _status.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
    }

    private void OnEdited()
    {
        if (_syncing)
            return;
        _status.Text = "";
        if (_use == null && !string.IsNullOrWhiteSpace(_host.Text) && TryGetAddress(out _))
            AddressApplied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Текущий верный адрес (без сообщения об ошибке) или null.</summary>
    public string? CurrentAddress
    {
        get
        {
            var host = (_host.Text ?? "").Trim();
            if (!NetworkPrinterAddress.IsValidHost(host) || !int.TryParse(string.IsNullOrWhiteSpace(_port.Text) ? "9100" : _port.Text!.Trim(), out var port) || port is < 1 or > 65535)
                return null;
            return NetworkPrinterAddress.Format(host, port);
        }
    }

    private async Task TestAsync()
    {
        if (!TryGetAddress(out var address))
            return;
        _test.IsEnabled = false;
        _status.ClearValue(TextBlock.ForegroundProperty);
        _status.Text = T("Проверяю связь…", "Байланышты текшерип жатам…", "Checking the connection…", "Bağlantı kontrol ediliyor…", "Aloqa tekshirilmoqda…");
        try
        {
            var result = await Task.Run(() => PrinterPortService.ProbePort(address)); // не на потоке интерфейса, таймаут 2 с; идёт печать на этот адрес — «Занят», без подключения
            _status.Text = result.Message;
            _status.Foreground = new SolidColorBrush(result.IsAvailable ? Color.FromRgb(0x16, 0xA3, 0x4A) : Color.FromRgb(0xDC, 0x26, 0x26));
        }
        finally
        {
            _test.IsEnabled = true;
        }
    }

    private async Task FindAsync()
    {
        if (_scanCts != null)
        {
            _scanCts.Cancel(); // кнопка «Остановить»
            return;
        }
        var total = NetworkPrinterScanner.HostCount();
        if (total == 0)
        {
            ShowError(T("Не найдена локальная сеть этого компьютера.", "Бул компьютердин жергиликтүү тармагы табылган жок.", "This computer's local network was not found.", "Bu bilgisayarın yerel ağı bulunamadı.", "Bu kompyuterning mahalliy tarmog'i topilmadi."));
            return;
        }
        _scanCts = new CancellationTokenSource();
        var cts = _scanCts;
        _find.Content = T("Остановить", "Токтотуу", "Stop", "Durdur", "To'xtatish");
        _status.ClearValue(TextBlock.ForegroundProperty);
        _status.Text = T($"Проверяю порт 9100 у {total} адресов вашей сети…", $"Тармагыңыздын {total} дарегинде 9100 портун текшерип жатам…", $"Checking port 9100 on {total} addresses of your network…", $"Ağınızdaki {total} adreste 9100 portu kontrol ediliyor…", $"Tarmog'ingizdagi {total} ta manzilda 9100 port tekshirilmoqda…");
        var progress = new Progress<NetworkPrinterScanner.Progress>(p =>
            _status.Text = T($"Поиск… проверено {p.Done} из {p.Total}, найдено {p.Found}", $"Издөө… {p.Done}/{p.Total} текшерилди, табылды {p.Found}", $"Searching… checked {p.Done} of {p.Total}, found {p.Found}", $"Aranıyor… {p.Done}/{p.Total} kontrol edildi, bulunan {p.Found}", $"Qidirilmoqda… {p.Done}/{p.Total} tekshirildi, topildi {p.Found}"));
        try
        {
            var found = await Task.Run(() => NetworkPrinterScanner.ScanAsync(progress, cts.Token));
            _found.ItemsSource = found;
            _found.IsVisible = found.Count > 0;
            _status.Text = found.Count == 0
                ? T("Устройств с открытым портом 9100 не найдено. Проверьте питание принтера и сеть или введите адрес вручную.", "9100 порту ачык түзмөктөр табылган жок. Принтердин кубатын жана тармакты текшериңиз же даректи кол менен киргизиңиз.", "No devices with open port 9100 were found. Check the printer's power and the network, or enter the address manually.", "9100 portu açık cihaz bulunamadı. Yazıcının gücünü ve ağı kontrol edin veya adresi elle girin.", "9100 port ochiq qurilmalar topilmadi. Printer quvvati va tarmoqni tekshiring yoki manzilni qo'lda kiriting.")
                : T($"Найдено: {found.Count}. Выберите принтер из списка.", $"Табылды: {found.Count}. Тизмеден принтерди тандаңыз.", $"Found: {found.Count}. Choose a printer from the list.", $"Bulunan: {found.Count}. Listeden yazıcı seçin.", $"Topildi: {found.Count}. Ro'yxatdan printerni tanlang.");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Network printer scan failed: {ex.GetType().Name}", "WARNING");
            ShowError(T("Поиск в сети не удался.", "Тармактан издөө ишке ашкан жок.", "The network search failed.", "Ağ araması başarısız oldu.", "Tarmoqdan qidirish amalga oshmadi."));
        }
        finally
        {
            cts.Dispose();
            _scanCts = null;
            _find.Content = T("Найти в сети", "Тармактан табуу", "Find on the network", "Ağda bul", "Tarmoqdan topish");
        }
    }
}
