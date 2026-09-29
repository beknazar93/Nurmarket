using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-09-28: «Весы с печатью этикеток» — марка плитками и поля только выбранной марки
/// (см. разметку). Адрес каждой марки хранится в своём поле настроек: Штрих-ПРИНТ —
/// ScaleNetworkIp/ScaleLanPort/ScaleLanPassword, Rongta — RongtaScaleIp/RongtaScalePort,
/// TM-30F — TmScaleIp/TmScalePort. Поля сохраняются при уходе с поля и перед каждой кнопкой.</summary>
public partial class LabelScaleSetupPanel : UserControl
{
    /// <summary>Панель в отдельном окне (из окна «Весы»): без кнопки «Отправить товары».</summary>
    public bool IsDialogMode { get; set; }

    /// <summary>Марку или адрес поменяли — окно «Весы» обновляет свою шапку.</summary>
    public event Action? SetupChanged;

    private string _brand = ScaleUi.BrandShtrikh;
    private bool _loading;

    /// <summary>Поля хоть раз заполнены из настроек. До этого SaveFields ничего не пишет — иначе
    /// пустые поля ещё не открытой панели затёрли бы сохранённые адреса.</summary>
    private bool _hasLoaded;

    public LabelScaleSetupPanel()
    {
        InitializeComponent();
    }

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private IBrush Brush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : fallback;

    private void Panel_Loaded(object? sender, RoutedEventArgs e) => LoadFromPreferences();

    /// <summary>Заполняет плитки и поля из настроек. Ничего не сохраняет.</summary>
    public void LoadFromPreferences()
    {
        _loading = true;
        try
        {
            var prefs = UserPreferences.Instance;
            _brand = ScaleUi.NormalizeBrand(prefs.ScaleBrand);
            BrandShtrikhTile.IsChecked = _brand == ScaleUi.BrandShtrikh;
            BrandRongtaTile.IsChecked = _brand == ScaleUi.BrandRongta;
            BrandTmTile.IsChecked = _brand == ScaleUi.BrandTm;
            BrandAiTile.IsChecked = _brand == ScaleUi.BrandAi;

            ShtrikhIpBox.Text = prefs.ScaleNetworkIp ?? "";
            ShtrikhPortBox.Text = prefs.ScaleLanPort.ToString(CultureInfo.InvariantCulture);
            ShtrikhPasswordBox.Text = prefs.ScaleLanPassword ?? "";
            ShtrikhRouteDirect.IsChecked = prefs.ShtrikhDirectLan;
            ShtrikhRouteServer.IsChecked = !prefs.ShtrikhDirectLan;

            RongtaIpBox.Text = ScaleUi.IpOf(ScaleUi.BrandRongta);
            RongtaPortBox.Text = prefs.RongtaScalePort.ToString(CultureInfo.InvariantCulture);
            var ownServer = string.Equals(prefs.RongtaDataSource, "server", StringComparison.OrdinalIgnoreCase);
            RongtaRouteServer.IsChecked = ownServer;
            RongtaRouteSite.IsChecked = !ownServer;
            RongtaServerPortBox.Text = prefs.RongtaServerPort.ToString(CultureInfo.InvariantCulture);

            TmIpBox.Text = prefs.TmScaleIp ?? "";
            TmPortBox.Text = prefs.TmScalePort.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
            _hasLoaded = true;
        }
        ApplyBrand();
    }

    /// <summary>Показывает поля выбранной марки, подписи и состояние связи.</summary>
    private void ApplyBrand()
    {
        ShtrikhFields.IsVisible = _brand == ScaleUi.BrandShtrikh;
        RongtaFields.IsVisible = _brand == ScaleUi.BrandRongta;
        TmFields.IsVisible = _brand == ScaleUi.BrandTm;
        AiFields.IsVisible = _brand == ScaleUi.BrandAi;

        ConnectionTitle.Text = _brand == ScaleUi.BrandAi
            ? ScaleUi.LabelBrandTitle(_brand)
            : L("Подключение: ", "Туташуу: ", "Connection: ", "Bağlantı: ", "Ulanish: ") + ScaleUi.LabelBrandTitle(_brand);

        var hasNetwork = _brand != ScaleUi.BrandAi;
        CheckButton.IsVisible = hasNetwork;
        FindButton.IsVisible = hasNetwork;
        DeviceSettingsButton.IsVisible = hasNetwork;
        SendButton.IsVisible = !IsDialogMode;
        SendButton.Content = _brand == ScaleUi.BrandAi
            ? L("Подготовить файл →", "Файл даярдоо →", "Prepare the file →", "Dosyayı hazırla →", "Faylni tayyorlash →")
            : L("Отправить товары →", "Товарларды жөнөтүү →", "Send goods →", "Ürünleri gönder →", "Tovarlarni yuborish →");

        ShtrikhRouteHint.Text = ShtrikhRouteDirect.IsChecked == true
            ? L("Касса сама отправляет товары на весы по сети (UDP), без сервера. Нужны адрес, порт и пароль из системного меню весов.",
                "Касса товарларды таразага тармак аркылуу (UDP) өзү жөнөтөт, серверсиз. Таразанын системалык менюсундагы дарек, порт жана сырсөз керек.",
                "The till sends goods to the scale itself over the network (UDP), without the server. It needs the address, port and password from the scale's system menu.",
                "Kasa ürünleri tartıya ağ üzerinden (UDP) sunucusuz kendisi gönderir. Tartının sistem menüsündeki adres, port ve şifre gerekir.",
                "Kassa tovarlarni taroziga tarmoq orqali (UDP) serversiz o‘zi yuboradi. Tarozining tizim menyusidagi manzil, port va parol kerak.")
            : L("Сервер NurCRM сам передаёт товары на весы (проверенный путь). Адрес здесь нужен только для «Проверить связь».",
                "NurCRM сервери товарларды таразага өзү берет (текшерилген жол). Бул жердеги дарек «Байланышты текшерүү» үчүн гана керек.",
                "The NurCRM server passes the goods to the scale itself (the proven path). The address here is only for “Check connection”.",
                "NurCRM sunucusu ürünleri tartıya kendisi iletir (denenmiş yol). Buradaki adres yalnızca «Bağlantıyı kontrol et» içindir.",
                "NurCRM serveri tovarlarni taroziga o‘zi uzatadi (sinalgan yo‘l). Bu yerdagi manzil faqat «Aloqani tekshirish» uchun kerak.");

        var ownServer = RongtaRouteServer.IsChecked == true;
        RongtaServerPortRow.IsVisible = ownServer;
        RongtaRouteHint.Text = ownServer
            ? L("Список строится из каталога кассы; касса ждёт подключения программы весов на этот порт и сама «нажимает» в RLS1000 загрузку (если RLS1000 нет — запустите загрузку в программе весов сами, есть 90 секунд).",
                "Тизме кассанын каталогунан түзүлөт; касса тараза программасынын ушул портко туташуусун күтөт жана RLS1000'де жүктөөнү өзү «басат» (RLS1000 жок болсо — тараза программасында жүктөөнү өзүңүз баштаңыз, 90 секунд бар).",
                "The list is built from the till catalog; the till waits for the scale software to connect to this port and “presses” download in RLS1000 itself (without RLS1000, start the upload in the scale software yourself, you have 90 seconds).",
                "Liste kasa kataloğundan oluşturulur; kasa tartı programının bu porta bağlanmasını bekler ve RLS1000'de yüklemeye kendisi «basar» (RLS1000 yoksa yüklemeyi tartı programında kendiniz başlatın, 90 saniyeniz var).",
                "Ro‘yxat kassa katalogidan tuziladi; kassa tarozi dasturining shu portga ulanishini kutadi va RLS1000 da yuklashni o‘zi «bosadi» (RLS1000 bo‘lmasa — yuklashni tarozi dasturida o‘zingiz boshlang, 90 soniya bor).")
            : L("Касса скачивает файл .txp с сайта NurCRM (как вкладка «Rongta» на сайте) и запускает загрузку в RLS1000. На весы уходит весь список весовых товаров.",
                "Касса NurCRM сайтынан .txp файлын жүктөйт (сайттагы «Rongta» өтмөгүндөй) жана RLS1000'де жүктөөнү баштайт. Таразага бардык салмактуу товарлардын тизмеси кетет.",
                "The till downloads the .txp file from the NurCRM website (like the “Rongta” tab there) and starts the upload in RLS1000. The whole list of weighed goods goes to the scale.",
                "Kasa NurCRM sitesinden .txp dosyasını indirir (sitedeki «Rongta» sekmesi gibi) ve RLS1000'de yüklemeyi başlatır. Tartıya tüm tartılı ürün listesi gider.",
                "Kassa NurCRM saytidan .txp faylini yuklab oladi (saytdagi «Rongta» bo‘limi kabi) va RLS1000 da yuklashni boshlaydi. Taroziga barcha vaznli tovarlar ro‘yxati ketadi.");

        UpdateIpHint();
        UpdateStatus();
    }

    private TextBox? CurrentIpBox => _brand switch
    {
        ScaleUi.BrandRongta => RongtaIpBox,
        ScaleUi.BrandTm => TmIpBox,
        ScaleUi.BrandAi => null,
        _ => ShtrikhIpBox,
    };

    /// <summary>Подсказка под адресом: опечатка, чужая сеть и т. п. (ScaleUi.CheckScaleIp).</summary>
    private void UpdateIpHint()
    {
        var box = CurrentIpBox;
        if (box is null)
        {
            IpHintBorder.IsVisible = false;
            return;
        }
        var (level, text) = ScaleUi.CheckScaleIp(box.Text);
        IpHintBorder.IsVisible = level != ScaleIpLevel.None;
        if (level == ScaleIpLevel.None)
            return;
        var (bg, border, mark) = level switch
        {
            ScaleIpLevel.Ok => ("BrushSuccessSoft", "BrushSuccess", "✓ "),
            ScaleIpLevel.Warning => ("BrushWarningSoft", "BrushWarning", "⚠ "),
            _ => ("BrushDangerSoft", "BrushDanger", "✗ "),
        };
        IpHintBorder.Background = Brush(bg, Brushes.LightYellow);
        IpHintBorder.BorderBrush = Brush(border, Brushes.Goldenrod);
        IpHintText.Text = mark + text;
    }

    /// <summary>Строка состояния: адрес/способ и последняя проверка связи.</summary>
    private void UpdateStatus()
    {
        if (_brand == ScaleUi.BrandAi)
        {
            StatusBorder.IsVisible = false;
            return;
        }
        StatusBorder.IsVisible = true;
        var state = ScaleUi.LastCheckOf(_brand);
        StatusText.Text = ScaleUi.AddressLine(_brand) + Environment.NewLine + ScaleUi.LastCheckText(_brand);
        StatusBorder.Background = Brush(state is null ? "BrushSurfaceSubtle" : state.Ok ? "BrushSuccessSoft" : "BrushWarningSoft", Brushes.WhiteSmoke);
        StatusBorder.BorderBrush = Brush(state is null ? "BrushBorder" : state.Ok ? "BrushSuccess" : "BrushWarning", Brushes.LightGray);
    }

    // ------------------------------------------------------------------ сохранение

    private void Field_LostFocus(object? sender, RoutedEventArgs e) => SaveFields();

    private void IpBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loading)
            return;
        UpdateIpHint();
    }

    /// <summary>Пишет поля ВСЕХ марок в их собственные настройки (не смешивая адреса) и сохраняет
    /// файл. Пустой пароль Штрих-ПРИНТ не затирает сохранённый (пустой весы не примут).</summary>
    public void SaveFields()
    {
        if (_loading || !_hasLoaded)
            return;
        var prefs = UserPreferences.Instance;

        prefs.ScaleNetworkIp = (ShtrikhIpBox.Text ?? "").Trim();
        if (TryPort(ShtrikhPortBox.Text, out var shtrikhPort))
            prefs.ScaleLanPort = shtrikhPort;
        var password = (ShtrikhPasswordBox.Text ?? "").Trim();
        if (password.Length > 0)
            prefs.ScaleLanPassword = password;
        prefs.ShtrikhDirectLan = ShtrikhRouteDirect.IsChecked == true;

        var rongtaIp = (RongtaIpBox.Text ?? "").Trim();
        // Rongta раньше делила адрес со Штрихом (IpOf подставляет его): не пишем подставленное
        // значение обратно, пока его не поменяли.
        if (!string.IsNullOrEmpty(prefs.RongtaScaleIp) || !string.Equals(rongtaIp, prefs.ScaleNetworkIp ?? "", StringComparison.Ordinal))
            prefs.RongtaScaleIp = rongtaIp;
        if (TryPort(RongtaPortBox.Text, out var rongtaPort))
            prefs.RongtaScalePort = rongtaPort;
        prefs.RongtaDataSource = RongtaRouteServer.IsChecked == true ? "server" : "site";
        if (TryPort(RongtaServerPortBox.Text, out var serverPort))
            prefs.RongtaServerPort = serverPort;

        prefs.TmScaleIp = (TmIpBox.Text ?? "").Trim();
        if (TryPort(TmPortBox.Text, out var tmPort))
            prefs.TmScalePort = tmPort;

        prefs.SaveToDisk();
        UpdateStatus();
        SetupChanged?.Invoke();
    }

    private static bool TryPort(string? text, out int port) =>
        int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;

    // ------------------------------------------------------------------ марка и способ

    private void BrandTile_Click(object? sender, RoutedEventArgs e)
    {
        var brand = sender == BrandRongtaTile ? ScaleUi.BrandRongta
            : sender == BrandTmTile ? ScaleUi.BrandTm
            : sender == BrandAiTile ? ScaleUi.BrandAi
            : ScaleUi.BrandShtrikh;
        SaveFields();
        _brand = brand;
        UserPreferences.Instance.ScaleBrand = brand;
        UserPreferences.Instance.SaveToDisk();
        // 2026-09-28 (просьба владельца): у TM-30F выбран формат с суммой без веса — префикс весов
        // сразу получает правило «сумма» (Настройки → Весы → «Штрих-код: вес / сумма»).
        if (brand == ScaleUi.BrandTm)
            ScaleBarcodeRules.EnsureTmAmountRule();
        ApplyBrand();
        SetupChanged?.Invoke();
    }

    private void Route_Click(object? sender, RoutedEventArgs e)
    {
        SaveFields();
        ApplyBrand();
    }

    // ------------------------------------------------------------------ кнопки

    private async void Check_Click(object? sender, RoutedEventArgs e)
    {
        SaveFields();
        CheckButton.IsEnabled = false;
        StatusBorder.IsVisible = true;
        StatusText.Text = ScaleUi.AddressLine(_brand) + Environment.NewLine
            + L("Проверяю связь с весами…", "Тараза менен байланыш текшерилүүдө…", "Checking the connection to the scale…", "Tartı bağlantısı kontrol ediliyor…", "Tarozi bilan aloqa tekshirilmoqda…");
        try
        {
            await ScaleUi.CheckConnectionAsync(_brand).ConfigureAwait(true);
        }
        finally
        {
            CheckButton.IsEnabled = true;
            UpdateStatus();
            SetupChanged?.Invoke();
        }
    }

    private async void Find_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        SaveFields();
        if (await ScaleUi.OpenScanAsync(owner, _brand).ConfigureAwait(true))
            LoadFromPreferences();
        SetupChanged?.Invoke();
    }

    private async void DeviceSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return;
        SaveFields();
        await ScaleUi.OpenBrandSettingsAsync(owner, _brand).ConfigureAwait(true);
        // Окна марок сами пишут адрес/порт/марку — перечитываем.
        LoadFromPreferences();
        SetupChanged?.Invoke();
    }

    private void Send_Click(object? sender, RoutedEventArgs e)
    {
        SaveFields();
        var owner = TopLevel.GetTopLevel(this) as Window;
        var window = App.GetRequiredService<ScalesPluWindow>();
        if (owner is not null)
            window.Show(owner);
        else
            window.Show();
    }
}
