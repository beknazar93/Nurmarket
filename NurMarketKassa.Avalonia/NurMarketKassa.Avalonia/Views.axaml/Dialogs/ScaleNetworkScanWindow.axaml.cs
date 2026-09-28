using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using static NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-28: «Поиск весов в сети» (просьба владельца «анализ ip адресов добавь чтобы узнать
/// ip адрес подключенных весов»). Перебирает локальную подсеть выбранного адаптера этого
/// компьютера (ScaleNetworkScanner): ping + ARP-таблица + проверка портов весов. У строки —
/// «Использовать для…»: адрес записывается в настройки нужной марки (Штрих-М / Rongta / TM-30F).
/// Сканирование идёт вне UI-потока, окно можно закрыть или остановить поиск в любой момент.
/// </summary>
public partial class ScaleNetworkScanWindow : Window
{
    // 2026-09-28: добавлена колонка «Как найдено» (ping / ARP / широковещание / временный адрес / через роутер).
    private const string ColumnsSpec = "115,130,*,70,185,165,150,150";

    private readonly string? _preferredBrand;
    private IReadOnlyList<LocalSubnet> _subnets = Array.Empty<LocalSubnet>();
    private CancellationTokenSource? _cts;

    /// <summary>Хоть один адрес записан в настройки — вызывающему окну стоит перечитать поля.</summary>
    public bool AnyAddressApplied { get; private set; }

    public ScaleNetworkScanWindow() : this(null)
    {
    }

    public ScaleNetworkScanWindow(string? preferredBrand)
    {
        _preferredBrand = preferredBrand;
        InitializeComponent();

        Title = L("Поиск весов в сети", "Тармактан тараза издөө", "Find scales on the network", "Ağda tartı ara", "Tarmoqda tarozi qidirish");
        TitleText.Text = Title;
        IntroText.Text = L(
            "Касса проверит все адреса локальной сети этого компьютера: кто отвечает на ping, кто есть в ARP-таблице (весы часто молчат на ping, но всегда видны по ARP), открыт ли порт весов. Ничего в устройства не записывается. Весы должны быть включены и подключены к той же сети.",
            "Касса бул компьютердин жергиликтүү тармагындагы бардык даректерди текшерет: ким ping'ге жооп берет, ким ARP-таблицада (тараза көбүнчө ping'ге жооп бербейт, бирок ARP'те дайыма көрүнөт), тараза порту ачыкпы. Түзмөктөргө эч нерсе жазылбайт. Тараза күйүк жана ошол эле тармакка туташкан болушу керек.",
            "The till checks every address of this computer's local network: who answers ping, who is in the ARP table (scales often ignore ping but always show up in ARP), whether a scale port is open. Nothing is written to any device. The scale must be on and in the same network.",
            "Kasa bu bilgisayarın yerel ağındaki tüm adresleri kontrol eder: kim ping'e yanıt veriyor, kim ARP tablosunda (tartılar çoğu zaman ping'e yanıt vermez ama ARP'de her zaman görünür), tartı portu açık mı. Cihazlara hiçbir şey yazılmaz. Tartı açık ve aynı ağda olmalı.",
            "Kassa bu kompyuterning mahalliy tarmog‘idagi barcha manzillarni tekshiradi: kim ping'ga javob beradi, kim ARP jadvalida (tarozilar ko‘pincha ping'ga javob bermaydi, lekin ARP'da doim ko‘rinadi), tarozi porti ochiqmi. Qurilmalarga hech narsa yozilmaydi. Tarozi yoqilgan va shu tarmoqda bo‘lishi kerak.");
        SubnetLabel.Text = L("Сеть:", "Тармак:", "Network:", "Ağ:", "Tarmoq:");
        StartButton.Content = StartText;
        BuildHeader();
        InitForeignPanel(); // 2026-09-28: чужие подсети и временный адрес — ScaleNetworkScanWindow.Subnets.cs
        LoadSubnets();
        ShowResult(L("Нажмите «Начать поиск». Обычно это занимает 10–30 секунд.", "«Издөөнү баштоо» басыңыз. Адатта 10–30 секунд созулат.", "Press “Start search”. It usually takes 10–30 seconds.", "“Aramayı başlat”a basın. Genellikle 10–30 saniye sürer.", "«Qidiruvni boshlash»ni bosing. Odatda 10–30 soniya davom etadi."), false);
        Closing += (_, _) => _cts?.Cancel();
    }

    private static string StartText => L("Начать поиск", "Издөөнү баштоо", "Start search", "Aramayı başlat", "Qidiruvni boshlash");
    private static string StopText => L("Остановить", "Токтотуу", "Stop", "Durdur", "To‘xtatish");

    private void LoadSubnets()
    {
        _subnets = ScaleNetworkScanner.GetLocalSubnets();
        // 2026-09-28: пункты списка строит FillTargets — после подсетей ПК идут «чужие» подсети.
        FillTargets();
        if (_subnets.Count > 0)
        {
            SubnetBox.SelectedIndex = 0;
            SubnetBox.SelectionChanged += (_, _) =>
            {
                UpdateSubnetNote();
                UpdateForeignPanel();
            };
            UpdateSubnetNote();
            UpdateForeignPanel();
        }
        else
        {
            StartButton.IsEnabled = false;
            ShowResult(L("У компьютера нет активного сетевого подключения IPv4 — искать негде. Подключите кабель или Wi-Fi.", "Компьютерде IPv4 активдүү тармак туташуусу жок — издей турган жер жок. Кабелди же Wi-Fi'ды туташтырыңыз.", "This computer has no active IPv4 network connection. Connect a cable or Wi-Fi.", "Bu bilgisayarda etkin IPv4 bağlantısı yok. Kablo veya Wi-Fi bağlayın.", "Kompyuterda faol IPv4 ulanish yo‘q. Kabel yoki Wi-Fi ulang."), true);
        }
    }

    private LocalSubnet? SelectedSubnet => SelectedTarget?.Local; // 2026-09-28: список теперь шире подсетей ПК

    private void UpdateSubnetNote()
    {
        var s = SelectedSubnet;
        var notes = new List<string>();
        if (s is { Narrowed: true })
        {
            notes.Add(L($"Сеть шире /24 (/{s.PrefixLength}) — проверяем только 254 адреса вокруг этого компьютера ({s.RangeText}). Если весы в другой «сотне», поменяйте им адрес или введите его вручную.",
                $"Тармак /24'төн кенен (/{s.PrefixLength}) — бул компьютердин айланасындагы 254 дарек гана текшерилет ({s.RangeText}). Тараза башка «жүздүктө» болсо, дарегин өзгөртүңүз же колго жазыңыз.",
                $"The network is wider than /24 (/{s.PrefixLength}) — only the 254 addresses around this computer are checked ({s.RangeText}). If the scale is elsewhere, change its address or enter it manually.",
                $"Ağ /24'ten geniş (/{s.PrefixLength}) — yalnızca bu bilgisayarın çevresindeki 254 adres kontrol edilir ({s.RangeText}). Tartı başka yerdeyse adresini değiştirin veya elle girin.",
                $"Tarmoq /24 dan keng (/{s.PrefixLength}) — faqat shu kompyuter atrofidagi 254 manzil tekshiriladi ({s.RangeText}). Tarozi boshqa joyda bo‘lsa, manzilini o‘zgartiring yoki qo‘lda kiriting."));
        }
        if (s is not null && s.LocalAddress.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        {
            notes.Add(L("Адрес 169.254.x.x — компьютер не получил адрес от роутера (кабель напрямую к весам?). Весы с DHCP в такой сети адреса тоже не получат.",
                "169.254.x.x дареги — компьютер роутерден дарек алган жок (кабель түз таразагабы?). DHCP'лүү тараза мындай тармакта дарек албайт.",
                "Address 169.254.x.x — the computer got no address from a router (cable straight to the scale?). A DHCP scale will not get an address either.",
                "169.254.x.x adresi — bilgisayar yönlendiriciden adres alamadı (kablo doğrudan tartıya mı?). DHCP'li tartı da adres alamaz.",
                "169.254.x.x manzili — kompyuter routerdan manzil olmadi (kabel to‘g‘ridan-to‘g‘ri taroziga?). DHCP'li tarozi ham manzil ololmaydi."));
        }
        SubnetNote.Text = string.Join(" ", notes);
        SubnetNote.IsVisible = notes.Count > 0;
    }

    private void BuildHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnsSpec) };
        string[] titles =
        {
            L("IP-адрес", "IP-дарек", "IP address", "IP adresi", "IP manzil"),
            "MAC",
            L("Производитель / имя", "Өндүрүүчү / аты", "Vendor / name", "Üretici / ad", "Ishlab chiqaruvchi / nomi"),
            "Ping",
            L("Порты весов", "Тараза порттору", "Scale ports", "Tartı portları", "Tarozi portlari"),
            L("Что это", "Бул эмне", "What it is", "Bu ne", "Bu nima"),
            L("Как найдено", "Кантип табылды", "How found", "Nasıl bulundu", "Qanday topildi"), // 2026-09-28
            "",
        };
        for (var i = 0; i < titles.Length; i++)
        {
            var t = new TextBlock { Text = titles[i], FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = ThemeBrush(this, "BrushTextSoft", Brushes.Gray), Margin = new Thickness(0, 0, 8, 0) };
            Grid.SetColumn(t, i);
            grid.Children.Add(t);
        }
        HeaderHost.Child = grid;
    }

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            return;
        }

        // 2026-09-28: кроме подсетей ПК — «чужие» подсети (RunScanAsync в ScaleNetworkScanWindow.Subnets.cs).
        var target = SelectedTarget;
        if (target is null || target.Kind == TargetKind.Separator)
            return;
        var subnetText = target.Local?.RangeText ?? target.Preset?.Range.Label ?? (target.Kind == TargetKind.Custom ? CustomRangeBox.Text : "typical subnets");

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        StartButton.Content = StopText;
        SubnetBox.IsEnabled = false;
        ForeignPanel.IsEnabled = false;
        Progress.IsVisible = true;
        Progress.Value = 0;
        RowsPanel.Children.Clear();
        ShowResult(L("Идёт поиск…", "Издөө жүрүүдө…", "Searching…", "Aranıyor…", "Qidirilmoqda…"), false);

        var progress = new Progress<ScaleScanProgress>(p => Dispatcher.UIThread.Post(() => ShowProgress(p)));
        var shtrikhPort = UserPreferences.Instance.ScaleLanPort;
        var started = DateTime.UtcNow;
        try
        {
            var devices = await RunScanAsync(target, shtrikhPort, progress, ct).ConfigureAwait(true);
            if (devices is null)
                return; // ошибка ввода или отказ в подтверждении — сообщение уже на экране
            ShowDevices(devices);
            var scales = devices.Count(d => d.Guess != ScaleDeviceGuess.Unknown);
            var seconds = (int)(DateTime.UtcNow - started).TotalSeconds;
            ShowResult(L($"Готово за {seconds} с: найдено устройств — {devices.Count}, похожих на весы — {scales}.",
                         $"{seconds} секундда даяр: түзмөктөр — {devices.Count}, таразага окшошу — {scales}.",
                         $"Done in {seconds} s: {devices.Count} devices found, {scales} look like scales.",
                         $"{seconds} sn'de bitti: {devices.Count} cihaz bulundu, {scales} tanesi tartıya benziyor.",
                         $"{seconds} soniyada tayyor: {devices.Count} ta qurilma topildi, {scales} tasi taroziga o‘xshaydi.")
                       + (scales == 0
                           ? " " + L("Весы не опознаны по протоколу — посмотрите устройства без имени (MAC, ARP), это могут быть весы с закрытым протоколом (TM-30F) или весы в режиме клиента.",
                                     "Тараза протокол боюнча таанылган жок — атсыз түзмөктөрдү караңыз (MAC, ARP), булар жабык протоколдуу тараза (TM-30F) же клиент режиминдеги тараза болушу мүмкүн.",
                                     "No scale was identified by protocol — look at nameless devices (MAC, ARP); they may be scales with a closed protocol (TM-30F) or in client mode.",
                                     "Protokolle tanınan tartı yok — adsız cihazlara bakın (MAC, ARP); kapalı protokollü (TM-30F) veya istemci modundaki tartı olabilir.",
                                     "Protokol bo‘yicha tarozi aniqlanmadi — nomsiz qurilmalarni ko‘ring (MAC, ARP), bular yopiq protokolli (TM-30F) yoki mijoz rejimidagi tarozi bo‘lishi mumkin.")
                           : "")
                       + (ScanNotesText is { } notes ? "\n" + notes : ""), false); // 2026-09-28: временный адрес, широковещание
            PosLogger.Log($"Поиск весов в сети {subnetText}: устройств {devices.Count}, похожих на весы {scales}", "SCALES");
        }
        catch (OperationCanceledException)
        {
            ShowResult(L("Поиск остановлен.", "Издөө токтотулду.", "Search stopped.", "Arama durduruldu.", "Qidiruv to‘xtatildi.")
                       + (ScanNotesText is { } notes ? "\n" + notes : ""), false); // 2026-09-28: итог по временному адресу виден и после остановки
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поиск весов в сети: {ex}", "SCALES");
            ShowResult(L("Ошибка поиска: ", "Издөө катасы: ", "Search error: ", "Arama hatası: ", "Qidiruv xatosi: ") + ex.Message, true);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            StartButton.Content = StartText;
            SubnetBox.IsEnabled = true;
            ForeignPanel.IsEnabled = true;
            Progress.IsVisible = false;
            ProgressText.Text = "";
        }
    }

    private void ShowProgress(ScaleScanProgress p)
    {
        var stage = p.Stage switch
        {
            ScaleScanStage.Ping => L("Проверяю адреса (ping)", "Даректер текшерилүүдө (ping)", "Checking addresses (ping)", "Adresler kontrol ediliyor (ping)", "Manzillar tekshirilmoqda (ping)"),
            ScaleScanStage.Arp => L("Читаю ARP-таблицу", "ARP-таблица окулууда", "Reading the ARP table", "ARP tablosu okunuyor", "ARP jadvali o‘qilmoqda"),
            ScaleScanStage.Probe => L("Проверяю порты весов и имена", "Тараза порттору жана аттары текшерилүүдө", "Checking scale ports and names", "Tartı portları ve adlar kontrol ediliyor", "Tarozi portlari va nomlari tekshirilmoqda"),
            _ => L("Готово", "Даяр", "Done", "Bitti", "Tayyor"),
        };
        var text = p.Total > 0 ? $"{stage}: {p.Done} / {p.Total}" : stage;
        ReportProgressPrefix(ref text); // 2026-09-28: «[2/7] 192.168.1.0/24 · …» при нескольких подсетях
        ProgressText.Text = text;
        // Шкала: ping — 0–70 %, ARP — 70 %, проверки — 70–100 %.
        Progress.Value = p.Stage switch
        {
            ScaleScanStage.Ping => p.Total > 0 ? 70.0 * p.Done / p.Total : 0,
            ScaleScanStage.Arp => 70,
            ScaleScanStage.Probe => p.Total > 0 ? 70 + 30.0 * p.Done / p.Total : 70,
            _ => 100,
        };
    }

    private void ShowDevices(IReadOnlyList<ScaleNetworkDevice> devices)
    {
        RowsPanel.Children.Clear();
        if (devices.Count == 0)
        {
            RowsPanel.Children.Add(new TextBlock { Text = L("Никого не нашли.", "Эч ким табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi."), Classes = { "hint" }, Margin = new Thickness(0, 8) });
            return;
        }
        // 2026-09-28: для весов из чужой подсети — готовый свободный адрес в сети компьютера.
        var suggest = devices.Any(d => d.OutsideLocalNetworks) ? SuggestAddressForScale(devices) : null;
        foreach (var d in devices)
            RowsPanel.Children.Add(BuildRow(d, suggest));
    }

    private Control BuildRow(ScaleNetworkDevice d, string? suggest = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ColumnsSpec), Margin = new Thickness(0, 6) };
        var mono = new FontFamily("Consolas, Segoe UI");

        void Cell(int column, string text, bool bold = false, FontFamily? font = null, IBrush? brush = null)
        {
            var t = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = brush ?? ThemeBrush(this, "BrushText", Brushes.Black),
            };
            if (font is not null)
                t.FontFamily = font;
            Grid.SetColumn(t, column);
            grid.Children.Add(t);
        }

        Cell(0, d.Ip, bold: true, font: mono);
        Cell(1, d.Mac ?? "—", font: mono);

        var who = new List<string>();
        if (d.IsGateway)
            who.Add(L("роутер (шлюз)", "роутер (шлюз)", "router (gateway)", "yönlendirici (ağ geçidi)", "router (shlyuz)"));
        who.Add(d.Vendor ?? (d.IsRandomMac
            ? L("случайный MAC (телефон/ноутбук)", "кокустук MAC (телефон/ноутбук)", "random MAC (phone/laptop)", "rastgele MAC (telefon/dizüstü)", "tasodifiy MAC (telefon/noutbuk)")
            : "—"));
        if (!string.IsNullOrWhiteSpace(d.HostName))
            who.Add(d.HostName!);
        Cell(2, string.Join(" · ", who));

        Cell(3, d.PingReplied
            ? $"{d.RoundtripMs?.ToString(CultureInfo.InvariantCulture) ?? "?"} " + L("мс", "мс", "ms", "ms", "ms")
            : d.InArpTable || !d.FoundBy.HasFlag(ScaleFoundBy.Broadcast) // 2026-09-28: найденные только широковещанием в ARP нет
                ? L("нет (ARP)", "жок (ARP)", "no (ARP)", "yok (ARP)", "yo‘q (ARP)")
                : "—");

        string Mark(bool? open) => open == true ? "✓" : "—";
        var ports = $"TCP 5001 {Mark(d.Tcp5001Open)} · TCP {ScaleNetworkScanner.TmServerPort} {Mark(d.TmPortOpen)} · "
                    + L("Штрих", "Штрих", "Shtrih", "Shtrih", "Shtrix") + $" UDP {UserPreferences.Instance.ScaleLanPort} {(d.ShtrikhInfo is null ? "—" : "✓")}";
        Cell(4, ports, font: mono);

        var (guessText, guessBrush) = d.Guess switch
        {
            ScaleDeviceGuess.Shtrikh => (L("Весы Штрих-ПРИНТ", "Штрих-ПРИНТ таразасы", "Shtrih-PRINT scale", "Shtrih-PRINT tartı", "Shtrix-PRINT tarozi") + (d.ShtrikhInfo is { Length: > 0 } ? $" ({d.ShtrikhInfo})" : ""), ThemeBrush(this, "BrushSuccess", Brushes.Green)),
            ScaleDeviceGuess.TmJhScale => (L("Похоже на TM-30F / Dahua (порт 4001)", "TM-30F / Dahua окшойт (4001 порт)", "Looks like TM-30F / Dahua (port 4001)", "TM-30F / Dahua'ya benziyor (port 4001)", "TM-30F / Dahua'ga o‘xshaydi (4001 port)"), ThemeBrush(this, "BrushSuccess", Brushes.Green)),
            ScaleDeviceGuess.Rongta => (L("Возможно Rongta (открыт 5001)", "Rongta болушу мүмкүн (5001 ачык)", "Possibly Rongta (5001 open)", "Rongta olabilir (5001 açık)", "Rongta bo‘lishi mumkin (5001 ochiq)"), ThemeBrush(this, "BrushWarning", Brushes.DarkOrange)),
            _ => (L("неизвестное устройство", "белгисиз түзмөк", "unknown device", "bilinmeyen cihaz", "noma’lum qurilma"), ThemeBrush(this, "BrushTextSoft", Brushes.Gray)),
        };
        Cell(5, guessText, bold: d.Guess != ScaleDeviceGuess.Unknown, brush: guessBrush);
        Cell(6, FoundByText(d), brush: ThemeBrush(this, "BrushTextSoft", Brushes.Gray)); // 2026-09-28: «Как найдено»

        var use = new Button
        {
            Content = L("Использовать для…", "Колдонуу…", "Use for…", "Şunun için kullan…", "Foydalanish…"),
            Classes = { d.Guess == ScaleDeviceGuess.Unknown ? "btn-ok" : "btn-primary" },
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(10, 5),
        };
        var menu = new MenuFlyout();
        // Марка, из окна которой открыт поиск, — первой строкой меню.
        foreach (var brand in new[] { BrandShtrikh, BrandRongta, BrandTm }.OrderBy(b => b == _preferredBrand ? 0 : 1))
        {
            var item = new MenuItem { Header = BrandTitle(brand) };
            var b = brand;
            item.Click += (_, _) => ApplyAddress(d.Ip, b);
            menu.Items.Add(item);
        }
        // 2026-09-28: весы в чужой подсети — сразу записать адрес, который им дадут в сети компьютера.
        if (d.OutsideLocalNetworks && suggest is not null)
        {
            menu.Items.Add(new Separator());
            foreach (var brand in new[] { BrandShtrikh, BrandRongta, BrandTm }.OrderBy(b => b == _preferredBrand ? 0 : 1))
            {
                var b = brand;
                var item = new MenuItem
                {
                    Header = L($"{suggest} (новый адрес весов, после смены на весах) → {BrandTitle(brand)}",
                               $"{suggest} (таразанын жаңы дареги, таразада алмаштыргандан кийин) → {BrandTitle(brand)}",
                               $"{suggest} (the scale's new address, after changing it on the scale) → {BrandTitle(brand)}",
                               $"{suggest} (tartının yeni adresi, tartıda değiştirdikten sonra) → {BrandTitle(brand)}",
                               $"{suggest} (tarozining yangi manzili, tarozida o‘zgartirgandan keyin) → {BrandTitle(brand)}"),
                };
                item.Click += (_, _) => ApplyAddress(suggest, b);
                menu.Items.Add(item);
            }
        }
        use.Flyout = menu;
        Grid.SetColumn(use, 7);
        grid.Children.Add(use);

        var row = new StackPanel
        {
            Children =
            {
                grid,
            },
        };
        if (d.OutsideLocalNetworks)
        {
            row.Children.Add(new TextBlock
            {
                Text = "⚠ " + OutsideHint(d, suggest),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6),
                Foreground = ThemeBrush(this, "BrushWarning", Brushes.DarkOrange),
            });
        }
        row.Children.Add(new Border { Height = 1, Background = ThemeBrush(this, "BrushBorder", Brushes.LightGray) });
        return row;
    }

    private void ApplyAddress(string ip, string brand)
    {
        ApplyIpToBrand(brand, ip);
        AnyAddressApplied = true;
        PosLogger.Log($"Поиск весов: адрес {ip} записан для марки {brand}", "SCALES");
        ShowResult(L($"Адрес {ip} записан в настройки весов {BrandTitle(brand)}.",
                     $"{ip} дареги {BrandTitle(brand)} таразасынын жөндөөлөрүнө жазылды.",
                     $"Address {ip} saved to the {BrandTitle(brand)} scale settings.",
                     $"{ip} adresi {BrandTitle(brand)} tartı ayarlarına kaydedildi.",
                     $"{ip} manzili {BrandTitle(brand)} tarozi sozlamalariga yozildi."), false);
    }

    private void ShowResult(string text, bool isError)
    {
        ResultText.Text = text;
        ResultBorder.BorderBrush = ThemeBrush(this, isError ? "BrushWarning" : "BrushBorder", Brushes.Gray);
        ResultBorder.Background = ThemeBrush(this, isError ? "BrushWarningSoft" : "BrushSurfaceSubtle", Brushes.Transparent);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
