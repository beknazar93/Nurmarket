using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using static NurMarketKassa.AvaloniaHost.Views.Dialogs.ScaleUi;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-06 (просьба владельца «добавь настройки каждых весов, полноценные настройки весов»): окно «Настройки весов Штрих РС-200» по образцу ShtrikhScaleSettingsWindow.
/// Вкладки: Подключение / Общие / Штрих-код / Печать / Быстрый доступ и экран / Обмен файлами / Отчётность / Дополнительно. Каждый параметр — с подписью и подсказкой «параметр N»
/// (номера — руководство администратора, п. 6.3, и Паспорт версий весов).
///
/// У РС-200 нет команд по сети — только файлы в общей папке (в Паспорте версий 2.1.3.3/2.1.3.4 упомянут протокол драйвера на порту 22222, он проверяется отдельной кнопкой, только чтением).
/// Поэтому: ЧТЕНИЕ — из Config.txt (рядом с программой весов) или Export\Export.txt (если включён экспорт конфигурации, параметр 804) либо из файла, выбранного вручную (флешка);
/// ЗАПИСЬ — файлом импорта со строками «&lt;N номер;значение» ТОЛЬКО для изменённых параметров и ТОЛЬКО если на весах включён импорт конфигурации (параметр 814 = 1); иначе — готовый
/// файл для ручного «Импорт данных». Опасные параметры (номер весов, пути обмена, импорт/экспорт, режимы) — отдельное подтверждение; прочитанный Config.txt сохраняется копией в папке данных кассы.
/// На живых весах не проверено.
/// </summary>
public partial class ShtrikhPcSettingsWindow : Window
{
    private sealed class Editor
    {
        public required ShtrikhPcParam Param { get; init; }
        public required Control Control { get; init; }
        public required TextBlock Label { get; init; }
        public required Func<string> Get { get; init; }
        public required Action<string> Set { get; init; }
        public required string Title { get; init; }
    }

    private readonly Dictionary<int, Editor> _editors = new();
    private ShtrikhPcConfigData? _baseline;
    private string _baselineText = "";
    private string? _baselineSource;
    private DateTime? _baselineTime;
    private bool _loading;
    private bool _busy;

    private TextBox _folderBox = null!;
    private TextBox _loginBox = null!;
    private TextBox _passwordBox = null!;
    private TextBox _hostBox = null!;
    private NumericUpDown _portBox = null!;
    private TextBlock _probeText = null!;
    private Border _compareBorder = null!;
    private StackPanel _compareBody = null!;
    private TextBlock _unknownText = null!;
    private TextBlock _quickText = null!;
    private Button _sendButton = null!;
    private Button _restoreButton = null!;
    private Button _revertButton = null!;
    private Button _saveFileButton = null!;
    private Button _readButton = null!;

    public ShtrikhPcSettingsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => this.FitToKassaScreen();
        Title = L("Настройки весов Штрих РС-200", "Штрих РС-200 таразасынын жөндөөлөрү", "Shtrikh RS-200 scale settings", "Shtrikh RS-200 tartı ayarları", "Shtrix RS-200 tarozi sozlamalari");
        TitleText.Text = Title;
        IntroText.Text = L(
            "Настройки весов хранятся в файле Config.txt. Касса читает его с весов (или с флешки), показывает параметры понятными подписями и записывает ТОЛЬКО то, что вы изменили — файлом импорта (на весах должен быть включён «Импорт конфигурации», параметр 814). Сначала нажмите «Прочитать с весов».",
            "Тараза жөндөөлөрү Config.txt файлында сакталат. Касса аны таразадан (же флешкеден) окуйт, параметрлерди түшүнүктүү аталыштар менен көрсөтөт жана сиз өзгөрткөндү гана жазат — импорт файлы менен (таразада «Конфигурацияны импорттоо», 814-параметр күйгүзүлгөн болушу керек). Адегенде «Таразадан окуу» басыңыз.",
            "The scale settings live in the Config.txt file. The till reads it from the scale (or a USB stick), shows the parameters with clear labels and writes ONLY what you changed — as an import file (the scale must have “Import configuration” on, parameter 814). First press “Read from the scale”.",
            "Tartı ayarları Config.txt dosyasında durur. Kasa onu tartıdan (veya USB bellekten) okur, parametreleri anlaşılır etiketlerle gösterir ve YALNIZCA değiştirdiğinizi içe aktarma dosyasıyla yazar (tartıda «Yapılandırmayı içe aktar» açık olmalı, parametre 814). Önce «Tartıdan oku»ya basın.",
            "Tarozi sozlamalari Config.txt faylida saqlanadi. Kassa uni tarozidan (yoki fleshkadan) o‘qiydi, parametrlarni tushunarli nomlar bilan ko‘rsatadi va FAQAT o‘zgartirganingizni import fayli bilan yozadi (tarozida «Konfiguratsiyani import» yoqilgan bo‘lishi kerak, 814-parametr). Avval «Tarozidan o‘qish» ni bosing.");

        BuildActionBar();
        Tabs.Items.Add(MakeTab(L("Подключение", "Туташуу", "Connection", "Bağlantı", "Ulanish"), BuildConnectionTab()));
        Tabs.Items.Add(MakeTab(L("Общие", "Жалпы", "General", "Genel", "Umumiy"), BuildParamTab(ShtrikhPcTab.General)));
        Tabs.Items.Add(MakeTab(L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), BuildBarcodeTab()));
        Tabs.Items.Add(MakeTab(L("Печать", "Басып чыгаруу", "Printing", "Yazdırma", "Chop etish"), BuildParamTab(ShtrikhPcTab.Print)));
        Tabs.Items.Add(MakeTab(L("Быстрый доступ и экран", "Тез жеткиликтүүлүк жана экран", "Quick access and screen", "Hızlı erişim ve ekran", "Tez kirish va ekran"), BuildQuickTab()));
        Tabs.Items.Add(MakeTab(L("Обмен файлами", "Файл алмашуу", "File exchange", "Dosya değişimi", "Fayl almashuvi"), BuildExchangeTab()));
        Tabs.Items.Add(MakeTab(L("Отчётность", "Отчёттуулук", "Reports", "Raporlar", "Hisobotlar"), BuildParamTab(ShtrikhPcTab.Reports)));
        Tabs.Items.Add(MakeTab(L("Дополнительно", "Кошумча", "Advanced", "Gelişmiş", "Qo‘shimcha"), BuildAdvancedTab()));
        UpdateHeader();
        SetEditorsEnabled(false);
        UpdateState();
        ShowResult(L("Нажмите «Прочитать с весов» (или «Открыть Config.txt…» с флешки), чтобы увидеть настройки.", "Жөндөөлөрдү көрүү үчүн «Таразадан окуу» (же флешкедеги «Config.txt ачуу…») басыңыз.", "Press “Read from the scale” (or “Open Config.txt…” from a USB stick) to see the settings.", "Ayarları görmek için «Tartıdan oku»ya (veya USB bellekten «Config.txt aç…»a) basın.", "Sozlamalarni ko‘rish uchun «Tarozidan o‘qish» ni (yoki fleshkadan «Config.txt ochish…» ni) bosing."), false);
    }

    /// <summary>2026-11-07: открыть окно сразу на вкладке «Штрих-код» (как у окон Rongta / TM-30F).</summary>
    public void ShowBarcodeTab() => Tabs.SelectedIndex = 2;

    private static TabItem MakeTab(string header, Control content) => new() { Header = header, Content = new ScrollViewer { Content = content } };

    // =====================================================================================
    // Панель действий
    // =====================================================================================

    private void BuildActionBar()
    {
        _readButton = MakeButton(L("Прочитать с весов", "Таразадан окуу", "Read from the scale", "Tartıdan oku", "Tarozidan o‘qish"), false, async (_, _) => await ReadFromScalesAsync().ConfigureAwait(true));
        var open = MakeButton(L("Открыть Config.txt…", "Config.txt ачуу…", "Open Config.txt…", "Config.txt aç…", "Config.txt ochish…"), false, async (_, _) => await ReadFromFileAsync().ConfigureAwait(true));
        _sendButton = MakeButton(L("Отправить изменения в весы", "Өзгөртүүлөрдү таразага жөнөтүү", "Send the changes to the scale", "Değişiklikleri tartıya gönder", "O‘zgarishlarni taroziga yuborish"), true, async (_, _) => await SendAsync().ConfigureAwait(true));
        _saveFileButton = MakeButton(L("Сохранить файл настроек…", "Жөндөөлөр файлын сактоо…", "Save the settings file…", "Ayar dosyasını kaydet…", "Sozlamalar faylini saqlash…"), false, async (_, _) => await SaveFileAsync().ConfigureAwait(true));
        _revertButton = MakeButton(L("Отменить правки в окне", "Терезедеги оңдоолорду жокко чыгаруу", "Discard edits in the window", "Penceredeki düzenlemeleri iptal et", "Oynadagi tahrirlarni bekor qilish"), false, (_, _) => RevertEdits());
        _restoreButton = MakeButton(L("Вернуть на весах прочитанное", "Таразада окулганды кайтаруу", "Restore what was read on the scale", "Tartıda okunanı geri yükle", "Tarozida o‘qilganni qaytarish"), false, async (_, _) => await RestoreAsync().ConfigureAwait(true));
        var compare = MakeButton(L("Сверка с кассой", "Кассa менен салыштыруу", "Compare with the till", "Kasa ile karşılaştır", "Kassa bilan solishtirish"), false, (_, _) => RunCompare(true));
        foreach (var b in new Control[] { _readButton, open, _sendButton, _saveFileButton, _revertButton, _restoreButton, compare })
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            ActionBar.Children.Add(b);
        }
    }

    private void UpdateHeader()
    {
        var folder = ShtrikhPcSettings.Current.Folder;
        ConnectionText.Text = L("Весы: папка обмена ", "Тараза: алмашуу папкасы ", "Scale: exchange folder ", "Tartı: değişim klasörü ", "Tarozi: almashuv papkasi") + (folder.Length > 0 ? folder : "—");
    }

    private void UpdateState()
    {
        if (_baseline is null)
        {
            StateText.Text = L("Настройки ещё не прочитаны — поля недоступны.", "Жөндөөлөр али окула элек — талаалар жеткиликсиз.", "Settings have not been read yet — the fields are locked.", "Ayarlar henüz okunmadı — alanlar kilitli.", "Sozlamalar hali o‘qilmagan — maydonlar yopiq.");
        }
        else
        {
            var changes = ComputeChanges();
            StateText.Text = L($"Прочитано: {_baselineSource ?? "—"}" + (_baselineTime is { } t ? $" ({t:dd.MM.yyyy HH:mm})" : "") + $" · параметров: {_baseline.Count} · изменено в окне: {changes.Count} · кнопок быстрого доступа назначено: {ShtrikhPcConfig.AssignedQuickKeys(_baseline)}",
                $"Окулду: {_baselineSource ?? "—"}" + (_baselineTime is { } t2 ? $" ({t2:dd.MM.yyyy HH:mm})" : "") + $" · параметрлер: {_baseline.Count} · терезеде өзгөртүлгөн: {changes.Count} · дайындалган тез баскычтар: {ShtrikhPcConfig.AssignedQuickKeys(_baseline)}",
                $"Read: {_baselineSource ?? "—"}" + (_baselineTime is { } t3 ? $" ({t3:dd.MM.yyyy HH:mm})" : "") + $" · parameters: {_baseline.Count} · changed in the window: {changes.Count} · quick-access keys assigned: {ShtrikhPcConfig.AssignedQuickKeys(_baseline)}",
                $"Okundu: {_baselineSource ?? "—"}" + (_baselineTime is { } t4 ? $" ({t4:dd.MM.yyyy HH:mm})" : "") + $" · parametre: {_baseline.Count} · pencerede değişen: {changes.Count} · atanan hızlı erişim tuşu: {ShtrikhPcConfig.AssignedQuickKeys(_baseline)}",
                $"O‘qildi: {_baselineSource ?? "—"}" + (_baselineTime is { } t5 ? $" ({t5:dd.MM.yyyy HH:mm})" : "") + $" · parametrlar: {_baseline.Count} · oynada o‘zgargan: {changes.Count} · belgilangan tez tugmalar: {ShtrikhPcConfig.AssignedQuickKeys(_baseline)}");
        }
        var has = _baseline is not null;
        _sendButton.IsEnabled = has && !_busy;
        _saveFileButton.IsEnabled = has && !_busy;
        _revertButton.IsEnabled = has && !_busy;
        _restoreButton.IsEnabled = has && !_busy && ShtrikhPcConfigBackup.Latest() is not null;
        _readButton.IsEnabled = !_busy;
    }

    private void SetEditorsEnabled(bool enabled)
    {
        foreach (var e in _editors.Values)
            e.Control.IsEnabled = enabled && !e.Param.ReadOnly;
    }

    // =====================================================================================
    // Подключение
    // =====================================================================================

    private Control BuildConnectionTab()
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        var s = ShtrikhPcSettings.Current;

        var card = Card(L("Папка весов (общая папка Shared)", "Таразанын папкасы (Shared жалпы папкасы)", "Scale folder (the shared folder Shared)", "Tartı klasörü (Shared paylaşım klasörü)", "Tarozi papkasi (Shared umumiy papkasi)"),
            L("Обмен идёт файлами: касса кладёт файлы в общую папку весов и читает оттуда настройки. Пример: \\\\192.168.0.200\\Shared. Если папка закрыта паролем Windows — впишите логин и пароль пользователя весов.",
              "Алмашуу файлдар менен жүрөт: касса таразанын жалпы папкасына файл салат жана ал жактан жөндөөлөрдү окуйт. Мисал: \\\\192.168.0.200\\Shared. Папка Windows сырсөзү менен жабык болсо — таразанын колдонуучусунун логинин жана сырсөзүн жазыңыз.",
              "Exchange is by files: the till puts files into the scale's shared folder and reads the settings from there. Example: \\\\192.168.0.200\\Shared. If the folder is protected by a Windows password — enter the scale user's login and password.",
              "Değişim dosyalarla yapılır: kasa dosyaları tartının paylaşım klasörüne koyar ve ayarları oradan okur. Örnek: \\\\192.168.0.200\\Shared. Klasör Windows şifresiyle korunuyorsa tartı kullanıcısının adını ve şifresini girin.",
              "Almashuv fayllar orqali: kassa fayllarni tarozining umumiy papkasiga qo‘yadi va sozlamalarni o‘sha yerdan o‘qiydi. Misol: \\\\192.168.0.200\\Shared. Papka Windows paroli bilan yopiq bo‘lsa — tarozi foydalanuvchisining login va parolini yozing."),
            out var body);
        _folderBox = new TextBox { Text = s.Folder, Watermark = ShtrikhPcSettings.ExampleFolder, MinWidth = 320 };
        _loginBox = new TextBox { Text = s.Login, Watermark = ShtrikhPcSettings.ExampleLogin, MinWidth = 320 };
        _passwordBox = new TextBox { Text = s.Password, PasswordChar = '●', MinWidth = 320 };
        foreach (var box in new[] { _folderBox, _loginBox, _passwordBox })
            box.LostFocus += (_, _) => SaveConnection();
        body.Children.Add(Row(L("Папка весов", "Таразанын папкасы", "Scale folder", "Tartı klasörü", "Tarozi papkasi"), _folderBox));
        body.Children.Add(Row(L("Логин Windows весов", "Таразанын Windows логини", "Scale Windows login", "Tartı Windows kullanıcı adı", "Tarozi Windows logini"), _loginBox));
        body.Children.Add(Row(L("Пароль", "Сырсөз", "Password", "Şifre", "Parol"), _passwordBox));
        body.Children.Add(ButtonRow(
            MakeButton(L("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish"), false, async (_, _) => await CheckFolderAsync().ConfigureAwait(true)),
            MakeButton(L("Инструкция по подключению", "Туташуу боюнча нускама", "Connection guide", "Bağlantı kılavuzu", "Ulanish bo‘yicha qo‘llanma"), false, async (_, _) => await new ScaleGuideWindow(BrandShtrikhPc).ShowDialog(this).ConfigureAwait(true))));
        panel.Children.Add(card);

        var protocol = Card(L("Сетевой протокол весов (только проверка)", "Таразанын тармак протоколу (текшерүү гана)", "Scale network protocol (check only)", "Tartının ağ protokolü (yalnızca kontrol)", "Tarozining tarmoq protokoli (faqat tekshirish)"),
            L("В Паспорте версий весов (2.1.3.3, 2.1.3.4) есть «загрузка через драйвер» и команды «запрос типа устройства» и «установка даты/времени»; программа весов слушает порт 22222. Кнопка отправляет ТОЛЬКО запрос типа устройства (по TCP, затем по UDP, до 3 секунд) и ничего не записывает: ни товары, ни время, ни настройки. Пароль не подбирается.",
              "Таразанын версиялар паспортунда (2.1.3.3, 2.1.3.4) «драйвер аркылуу жүктөө» жана «түзмөк түрүн суроо» менен «дата/убакытты коюу» буйруктары бар; таразанын программасы 22222 портун угат. Баскыч ТЕК түзмөк түрүн сурайт (TCP, анан UDP, 3 секундга чейин) жана эч нерсе жазбайт: товарларды да, убакытты да, жөндөөлөрдү да. Сырсөз терилбейт.",
              "The scale version passport (2.1.3.3, 2.1.3.4) has “loading through the driver” and the commands “get device type” and “set date/time”; the scale program listens on port 22222. The button sends ONLY the device type request (TCP, then UDP, up to 3 seconds) and writes nothing: no goods, no time, no settings. The password is not guessed.",
              "Tartı sürüm pasaportunda (2.1.3.3, 2.1.3.4) «sürücü ile yükleme» ve «cihaz türü sorgusu» ile «tarih/saat ayarı» komutları var; tartı programı 22222 portunu dinler. Düğme YALNIZCA cihaz türü sorgusunu gönderir (TCP, sonra UDP, en çok 3 saniye) ve hiçbir şey yazmaz: ürün, saat, ayar yok. Şifre denenmez.",
              "Tarozi versiyalar pasportida (2.1.3.3, 2.1.3.4) «drayver orqali yuklash» hamda «qurilma turini so‘rash» va «sana/vaqtni o‘rnatish» buyruqlari bor; tarozi dasturi 22222 portni tinglaydi. Tugma FAQAT qurilma turini so‘raydi (TCP, so‘ng UDP, 3 soniyagacha) va hech narsa yozmaydi: tovar ham, vaqt ham, sozlama ham. Parol tanlanmaydi."),
            out var pBody);
        _hostBox = new TextBox { Text = s.ProtocolHost, Watermark = L("пусто — IP из папки \\\\IP\\Shared", "бош — \\\\IP\\Shared папкасынан IP", "empty — the IP from \\\\IP\\Shared", "boş — \\\\IP\\Shared'den IP", "bo‘sh — \\\\IP\\Shared dagi IP"), MinWidth = 320 };
        _portBox = new NumericUpDown { Minimum = 1, Maximum = 65535, Increment = 1, FormatString = "0", Value = s.ProtocolPort, MinWidth = 140 };
        _hostBox.LostFocus += (_, _) => SaveConnection();
        _portBox.ValueChanged += (_, _) => SaveConnection();
        pBody.Children.Add(Row(L("IP весов", "Таразанын IP'си", "Scale IP", "Tartı IP'si", "Tarozi IP'si"), _hostBox));
        pBody.Children.Add(Row(L("Порт протокола", "Протокол порту", "Protocol port", "Protokol portu", "Protokol porti"), _portBox));
        _probeText = Text("", "hint");
        pBody.Children.Add(ButtonRow(MakeButton(L("Проверить протокол обмена (порт 22222)", "Алмашуу протоколун текшерүү (22222 порту)", "Check the exchange protocol (port 22222)", "Değişim protokolünü kontrol et (port 22222)", "Almashuv protokolini tekshirish (22222 port)"), false, async (_, _) => await ProbeAsync().ConfigureAwait(true))));
        pBody.Children.Add(_probeText);
        panel.Children.Add(protocol);
        return panel;
    }

    private void SaveConnection()
    {
        SaveKassa(s =>
        {
            s.Folder = (_folderBox.Text ?? "").Trim();
            s.Login = (_loginBox.Text ?? "").Trim();
            s.Password = _passwordBox.Text ?? "";
            s.ProtocolHost = (_hostBox.Text ?? "").Trim();
            s.ProtocolPort = (int)(_portBox.Value ?? ShtrikhPcSettings.DefaultProtocolPort);
        });
        UpdateHeader();
    }

    private static ShtrikhPcSettings SaveKassa(Action<ShtrikhPcSettings> mutate)
    {
        var s = ShtrikhPcSettings.Current;
        mutate(s);
        s.Normalize();
        ShtrikhPcSettings.Save(s);
        UserPreferences.Instance.SaveToDisk();
        return s;
    }

    private async Task CheckFolderAsync()
    {
        SaveConnection();
        var s = ShtrikhPcSettings.Current;
        ShowResult(L("Проверка связи…", "Байланыш текшерилүүдө…", "Checking the connection…", "Bağlantı kontrol ediliyor…", "Aloqa tekshirilmoqda…"), false);
        var (status, detail) = await ShtrikhPcImportWriter.CheckFolderAsync(s).ConfigureAwait(true);
        ShowResult(PcFolderCheckText(status, detail, s.Folder), status != ShtrikhPcFolderCheck.Ok);
    }

    private async Task ProbeAsync()
    {
        SaveConnection();
        var s = ShtrikhPcSettings.Current;
        var host = ShtrikhPcProtocolProbe.ResolveHost(s);
        if (host is null)
        {
            _probeText.Text = L("Не задан IP весов: впишите его выше или укажите папку вида \\\\192.168.0.200\\Shared.", "Таразанын IP'си коюлган эмес: жогоруга жазыңыз же \\\\192.168.0.200\\Shared түрүндөгү папканы көрсөтүңүз.", "The scale IP is not set: enter it above or give a folder like \\\\192.168.0.200\\Shared.", "Tartı IP'si girilmemiş: yukarı yazın veya \\\\192.168.0.200\\Shared gibi bir klasör verin.", "Tarozi IP'si kiritilmagan: yuqoriga yozing yoki \\\\192.168.0.200\\Shared kabi papka ko‘rsating.");
            return;
        }
        _probeText.Text = L($"Проверка {host}:{s.ProtocolPort}…", $"{host}:{s.ProtocolPort} текшерилүүдө…", $"Checking {host}:{s.ProtocolPort}…", $"{host}:{s.ProtocolPort} kontrol ediliyor…", $"{host}:{s.ProtocolPort} tekshirilmoqda…");
        var result = await ShtrikhPcProtocolProbe.ProbeAsync(host, s.ProtocolPort).ConfigureAwait(true);
        _probeText.Text = result.Message;
        ShowResult(result.Message, !result.Answered);
        PosLogger.Log($"Весы РС-200: проверка протокола {host}:{s.ProtocolPort} (только чтение): {(result.Answered ? "ответили по " + result.Transport : "нет ответа")}; {result.Message}", "SCALES");
    }

    // =====================================================================================
    // Вкладки с параметрами
    // =====================================================================================

    private StackPanel BuildParamTab(ShtrikhPcTab tab, IEnumerable<Control>? top = null, IEnumerable<Control>? bottom = null)
    {
        var panel = new StackPanel { Spacing = 12, Margin = new Thickness(0, 10, 8, 10) };
        if (top is not null)
            foreach (var c in top)
                panel.Children.Add(c);
        var groups = new List<(string Title, List<ShtrikhPcParam> Items)>();
        foreach (var p in ShtrikhPcConfig.Catalog.Where(p => p.Tab == tab))
        {
            var title = p.Group?.Invoke() ?? "";
            var group = groups.FirstOrDefault(g => g.Title == title);
            if (group.Items is null)
            {
                group = (title, new List<ShtrikhPcParam>());
                groups.Add(group);
            }
            group.Items.Add(p);
        }
        foreach (var (title, items) in groups)
        {
            var card = Card(title, null, out var body);
            foreach (var p in items)
                body.Children.Add(BuildParamRow(p));
            panel.Children.Add(card);
        }
        if (bottom is not null)
            foreach (var c in bottom)
                panel.Children.Add(c);
        return panel;
    }

    private Control BuildParamRow(ShtrikhPcParam p)
    {
        var title = p.Title();
        var label = new TextBlock { Text = title, Classes = { "label" }, Margin = new Thickness(0, 0, 12, 0) };
        Control control;
        Func<string> get;
        Action<string> set;
        switch (p.Kind)
        {
            case ShtrikhPcParamKind.Bool:
            {
                var cb = new CheckBox();
                cb.IsCheckedChanged += (_, _) => OnEdited(p.Number);
                control = cb;
                get = () => cb.IsChecked == true ? "1" : "0";
                set = v => cb.IsChecked = v.Trim() == "1";
                break;
            }
            case ShtrikhPcParamKind.Choice:
            {
                var combo = new ComboBox { MinWidth = 280 };
                foreach (var item in p.Choices?.Invoke() ?? Array.Empty<string>())
                    combo.Items.Add(item);
                combo.SelectionChanged += (_, _) => OnEdited(p.Number);
                control = combo;
                get = () => Math.Max(0, combo.SelectedIndex).ToString(CultureInfo.InvariantCulture);
                set = v => combo.SelectedIndex = int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < combo.ItemCount ? i : -1;
                break;
            }
            case ShtrikhPcParamKind.Int:
            {
                var nud = new NumericUpDown { Minimum = p.Min, Maximum = p.Max, Increment = 1, FormatString = "0", MinWidth = 160 };
                nud.ValueChanged += (_, _) => OnEdited(p.Number);
                control = nud;
                get = () => ((int)(nud.Value ?? p.Min)).ToString(CultureInfo.InvariantCulture);
                set = v => nud.Value = int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, p.Min, p.Max) : p.Min;
                break;
            }
            default:
            {
                var tb = new TextBox { MinWidth = 320 };
                tb.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBox.TextProperty)
                        OnEdited(p.Number);
                };
                control = tb;
                get = () => tb.Text ?? "";
                set = v => tb.Text = v;
                break;
            }
        }
        _editors[p.Number] = new Editor { Param = p, Control = control, Label = label, Get = get, Set = set, Title = title };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };
        grid.Children.Add(label);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = control is TextBox ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        grid.Children.Add(control);

        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(grid);
        row.Children.Add(Text(HintFor(p), "hint"));
        return row;
    }

    private static string HintFor(ShtrikhPcParam p)
    {
        var def = p.Kind switch
        {
            ShtrikhPcParamKind.Bool => p.Default == "1" ? L("да", "ооба", "yes", "evet", "ha") : L("нет", "жок", "no", "hayır", "yo‘q"),
            ShtrikhPcParamKind.Choice => int.TryParse(p.Default, out var i) && p.Choices?.Invoke() is { } list && i >= 0 && i < list.Length ? list[i] : p.Default,
            _ => p.Default,
        };
        var range = p.Kind == ShtrikhPcParamKind.Int ? $"{p.Min}–{p.Max}" : "";
        var text = L($"Параметр {p.Number}", $"{p.Number}-параметр", $"Parameter {p.Number}", $"Parametre {p.Number}", $"{p.Number}-parametr");
        if (p.ReadOnly)
            text += " · " + L("только чтение", "окуу гана", "read only", "salt okunur", "faqat o‘qish");
        else if (def.Length > 0)
            text += " · " + L("по умолчанию: ", "демейки: ", "default: ", "varsayılan: ", "standart: ") + def;
        if (range.Length > 0 && !p.ReadOnly)
            text += " · " + L("диапазон ", "диапазон ", "range ", "aralık ", "oraliq ") + range;
        if (ShtrikhPcConfig.DangerReason(p.Number) is not null)
            text += " · ⚠ " + L("потребует подтверждения", "ырастоону талап кылат", "will need confirmation", "onay gerektirir", "tasdiqlashni talab qiladi");
        if (p.Note is not null)
            text += ". " + p.Note();
        return text;
    }

    private void OnEdited(int number)
    {
        if (_loading)
            return;
        if (_editors.TryGetValue(number, out var e) && _baseline is not null)
            MarkChanged(e);
        UpdateState();
    }

    private void MarkChanged(Editor e)
    {
        var baseline = _baseline;
        if (baseline is null)
            return;
        var diff = ShtrikhPcConfig.Diff(baseline, new Dictionary<int, string> { [e.Param.Number] = e.Get() });
        var changed = diff.Count > 0;
        e.Label.Text = (changed ? "● " : "") + e.Title;
        e.Label.FontWeight = changed ? FontWeight.Bold : FontWeight.Normal;
    }

    private List<ShtrikhPcChange> ComputeChanges()
    {
        if (_baseline is null)
            return new List<ShtrikhPcChange>();
        var edited = new Dictionary<int, string>();
        foreach (var e in _editors.Values.Where(e => !e.Param.ReadOnly))
            edited[e.Param.Number] = e.Get();
        return ShtrikhPcConfig.Diff(_baseline, edited);
    }

    /// <summary>Выставляет значения из прочитанной конфигурации в поля (нет на весах — значение по умолчанию из руководства).</summary>
    private void LoadEditors(ShtrikhPcConfigData data)
    {
        _loading = true;
        try
        {
            foreach (var e in _editors.Values)
                e.Set(data.Get(e.Param.Number) ?? e.Param.Default);
        }
        finally
        {
            _loading = false;
        }
        foreach (var e in _editors.Values)
            MarkChanged(e);
        var unknown = ShtrikhPcConfig.UnknownNumbers(data);
        _unknownText.Text = unknown.Count == 0
            ? L("Других параметров в файле нет.", "Файлда башка параметрлер жок.", "There are no other parameters in the file.", "Dosyada başka parametre yok.", "Faylda boshqa parametrlar yo‘q.")
            : string.Join("; ", unknown.Select(n => $"{n} = {data.Get(n)}"));
        _quickText.Text = L($"Назначено кнопок быстрого доступа (параметры 2001–2360): {ShtrikhPcConfig.AssignedQuickKeys(data)} из 360. Товары на кнопки касса назначает при выгрузке (галочка «Назначить кнопки быстрого доступа» в карточке весов); здесь они только показываются.",
            $"Тез жеткиликтүүлүк баскычтары (2001–2360 параметрлери): {ShtrikhPcConfig.AssignedQuickKeys(data)} / 360 дайындалган. Товарларды баскычтарга касса жүктөөдө дайындайт (таразанын карточкасындагы белги); бул жерде алар көрсөтүлөт гана.",
            $"Quick-access keys assigned (parameters 2001–2360): {ShtrikhPcConfig.AssignedQuickKeys(data)} of 360. The till assigns goods to the keys on upload (the “Assign quick-access buttons” box in the scale card); here they are only shown.",
            $"Atanan hızlı erişim tuşları (2001–2360 parametreleri): {ShtrikhPcConfig.AssignedQuickKeys(data)} / 360. Kasa ürünleri yüklerken tuşlara atar (tartı kartındaki kutu); burada yalnızca gösterilir.",
            $"Belgilangan tez kirish tugmalari (2001–2360 parametrlari): {ShtrikhPcConfig.AssignedQuickKeys(data)} / 360. Kassa tovarlarni yuklashda tugmalarga belgilaydi (tarozi kartasidagi belgi); bu yerda faqat ko‘rsatiladi.");
    }

    // =====================================================================================
    // Штрих-код, быстрый доступ, обмен, дополнительно
    // =====================================================================================

    private Control BuildBarcodeTab()
    {
        _compareBody = new StackPanel { Spacing = 4 };
        _compareBorder = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), Child = _compareBody };
        Themed(this, _compareBorder, Border.BorderBrushProperty, "BrushBorder", Brushes.Gray);
        Themed(this, _compareBorder, Border.BackgroundProperty, "BrushPanelSoft", Brushes.Transparent);
        _compareBody.Children.Add(Text(L("Сверка ещё не делалась. Прочитайте настройки весов и нажмите «Сверка с кассой».", "Салыштыруу али жасалган жок. Таразанын жөндөөлөрүн окуп, «Кассa менен салыштыруу» басыңыз.", "No comparison yet. Read the scale settings and press “Compare with the till”.", "Karşılaştırma henüz yapılmadı. Tartı ayarlarını okuyup «Kasa ile karşılaştır»a basın.", "Solishtirish hali qilinmagan. Tarozi sozlamalarini o‘qib, «Kassa bilan solishtirish» ni bosing."), "hint"));

        var compareCard = Card(L("Сверка параметров весов с кассой", "Таразанын параметрлерин касса менен салыштыруу", "Comparison of the scale parameters with the till", "Tartı parametrelerinin kasa ile karşılaştırılması", "Tarozi parametrlarini kassa bilan solishtirish"),
            L("Префикс, формат, число знаков кода, КС и знаки цены на весах должны совпадать с настройками кассы — иначе касса не узнает товар на этикетке или цены уедут в 10 и 100 раз. Зелёная рамка — всё совпало, жёлтая — есть расхождения.",
              "Таразадагы префикс, формат, коддун белгилеринин саны, КС жана баанын белгилери кассанын жөндөөлөрү менен дал келиши керек — болбосо касса этикеткадагы товарды танубайт же баалар 10 жана 100 эсе жылат. Жашыл алкак — баары дал келди, сары — айырмачылыктар бар.",
              "The prefix, format, code length, check digit and price decimals on the scale must match the till settings — otherwise the till will not recognise the item on the label or prices will be off by 10 and 100 times. Green frame — all matches, yellow — there are differences.",
              "Tartıdaki önek, biçim, kod uzunluğu, KS ve fiyat ondalığı kasa ayarlarıyla aynı olmalı — yoksa kasa etiketteki ürünü tanımaz veya fiyatlar 10 ve 100 kat kayar. Yeşil çerçeve — hepsi aynı, sarı — farklar var.",
              "Tarozidagi prefiks, format, kod uzunligi, KS va narx xonalari kassa sozlamalari bilan mos bo‘lishi kerak — aks holda kassa yorliqdagi tovarni tanimaydi yoki narxlar 10 va 100 baravar siljiydi. Yashil ramka — hammasi mos, sariq — farqlar bor."),
            out var cBody);
        cBody.Children.Add(_compareBorder);
        cBody.Children.Add(ButtonRow(
            MakeButton(L("Сверка с кассой", "Кассa менен салыштыруу", "Compare with the till", "Kasa ile karşılaştır", "Kassa bilan solishtirish"), false, (_, _) => RunCompare(true)),
            MakeButton(L("Подтянуть настройки кассы из весов", "Кассанын жөндөөлөрүн таразадан алуу", "Pull the till settings from the scale", "Kasa ayarlarını tartıdan al", "Kassa sozlamalarini tarozidan olish"), false, (_, _) => PullToKassa())));

        var kassa = ShtrikhPcSettings.Current;
        var example = ScaleBarcodeRules.ShtrikhPcExample(kassa, 123, 250m);
        var exampleCard = Card(L("Пример этикетки по настройкам кассы", "Кассанын жөндөөлөрү боюнча этикетка мисалы", "Label example by the till settings", "Kasa ayarlarına göre etiket örneği", "Kassa sozlamalari bo‘yicha yorliq namunasi"),
            L("У РС-200 нет команды «тестовая этикетка» по файловому обмену: пробную этикетку печатайте на самих весах. Здесь — как выглядел бы штрихкод и как его прочтёт касса.",
              "РС-200 де файл алмашуу аркылуу «сыноо этикеткасы» буйругу жок: сыноо этикеткасын таразанын өзүндө басыңыз. Бул жерде — штрих-код кандай болоорун жана касса аны кандай окуорун көрсөтөт.",
              "The RS-200 has no “test label” command over file exchange: print a trial label on the scale itself. Here — what the barcode would look like and how the till reads it.",
              "RS-200'de dosya değişimi ile «test etiketi» komutu yok: deneme etiketini tartının kendisinde basın. Burada — barkodun nasıl görüneceği ve kasanın nasıl okuyacağı.",
              "RS-200 da fayl almashuvi orqali «sinov yorlig‘i» buyrug‘i yo‘q: sinov yorlig‘ini tarozining o‘zida chop eting. Bu yerda — shtrix-kod qanday ko‘rinishi va kassa uni qanday o‘qishi."),
            out var eBody);
        eBody.Children.Add(Text(example is null ? "—" : example.Value.Barcode + "  →  " + example.Value.Reading, "label"));

        return BuildParamTab(ShtrikhPcTab.Barcode, new Control[] { compareCard }, new Control[] { exampleCard });
    }

    private Control BuildQuickTab()
    {
        _quickText = Text("", "hint");
        var quickCard = Card(L("Кнопки быстрого доступа", "Тез жеткиликтүүлүк баскычтары", "Quick-access keys", "Hızlı erişim tuşları", "Tez kirish tugmalari"), null, out var qBody);
        qBody.Children.Add(_quickText);
        return BuildParamTab(ShtrikhPcTab.Quick, new Control[] { quickCard });
    }

    private Control BuildExchangeTab()
    {
        var s = ShtrikhPcSettings.Current;
        var card = Card(L("Касса: файлы обмена", "Касса: алмашуу файлдары", "Till: exchange files", "Kasa: değişim dosyaları", "Kassa: almashuv fayllari"),
            L("Пути относительно папки весов. Должны совпадать с параметрами весов 801–803 ниже.", "Жолдор таразанын папкасына салыштырмалуу. Төмөнкү таразанын 801–803 параметрлери менен дал келиши керек.", "Paths relative to the scale folder. They must match the scale parameters 801–803 below.", "Yollar tartı klasörüne göredir. Aşağıdaki 801–803 tartı parametreleriyle aynı olmalı.", "Yo‘llar tarozi papkasiga nisbatan. Quyidagi tarozi 801–803 parametrlari bilan mos bo‘lishi kerak."), out var body);
        TextBox Field(string value, string watermark, Action<ShtrikhPcSettings, string> apply)
        {
            var box = new TextBox { Text = value, Watermark = watermark, MinWidth = 320 };
            box.LostFocus += (_, _) => SaveKassa(x => apply(x, (box.Text ?? "").Trim()));
            return box;
        }
        body.Children.Add(Row(L("Файл импорта", "Импорт файлы", "Import file", "İçe aktarma dosyası", "Import fayli"), Field(s.ImportFile, ShtrikhPcSettings.DefaultImportFile, (x, v) => x.ImportFile = v)));
        body.Children.Add(Row(L("Флаг-файл импорта", "Импорттун желек-файлы", "Import flag file", "İçe aktarma bayrak dosyası", "Import bayroq-fayli"), Field(s.FlagFile, ShtrikhPcSettings.DefaultFlagFile, (x, v) => x.FlagFile = v)));
        body.Children.Add(Row(L("Файл экспорта", "Экспорт файлы", "Export file", "Dışa aktarma dosyası", "Eksport fayli"), Field(s.ExportFile, ShtrikhPcSettings.DefaultExportFile, (x, v) => x.ExportFile = v)));
        return BuildParamTab(ShtrikhPcTab.Exchange, new Control[] { card });
    }

    private Control BuildAdvancedTab()
    {
        var s = ShtrikhPcSettings.Current;
        var card = Card(L("Касса: главный экран и флаг перезагрузки сценария", "Касса: башкы экран жана сценарийди кайра жүктөө желеги", "Till: main screen and the scenario reload flag", "Kasa: ana ekran ve senaryo yeniden yükleme bayrağı", "Kassa: bosh ekran va ssenariyni qayta yuklash bayrog‘i"),
            L("Главный экран весов строится из сценария самообслуживания. После загрузки товаров весы сами перечитывают его, если рядом лежит пустой флаг-файл с именем из параметра 954. Касса кладёт его после записи товаров, картинок и сценария и ПЕРЕД основным флагом импорта (в папку обмена и в папку сценариев). Пример весов владельца: 953 = 1, 954 = flag, 955 = 0, 956 = 60, 957 = 1.",
              "Таразанын башкы экраны өз алдынча тейлөө сценарийинен курулат. Товарлар жүктөлгөндөн кийин тараза аны өзү кайра окуйт, эгер жанында 954-параметрдеги аттагы бош желек-файл жатса. Касса аны товарларды, сүрөттөрдү жана сценарийди жазгандан кийин жана импорттун негизги желегинен МУРУН салат (алмашуу папкасына жана сценарийлер папкасына). Ээсинин таразасынын мисалы: 953 = 1, 954 = flag, 955 = 0, 956 = 60, 957 = 1.",
              "The scale's main screen is built from the self-service scenario. After goods are loaded the scale re-reads it by itself if an empty flag file named as in parameter 954 lies next to it. The till puts it after writing goods, pictures and the scenario and BEFORE the main import flag (into the exchange folder and the scenario folder). The owner's scale example: 953 = 1, 954 = flag, 955 = 0, 956 = 60, 957 = 1.",
              "Tartının ana ekranı self-servis senaryosundan kurulur. Ürünler yüklendikten sonra tartı, yanında 954 parametresindeki adla boş bir bayrak dosyası varsa onu kendisi yeniden okur. Kasa onu ürünleri, resimleri ve senaryoyu yazdıktan sonra ve ana içe aktarma bayrağından ÖNCE koyar (değişim klasörüne ve senaryo klasörüne). Sahibinin tartı örneği: 953 = 1, 954 = flag, 955 = 0, 956 = 60, 957 = 1.",
              "Tarozining bosh ekrani o‘zini-o‘zi xizmat ssenariysidan quriladi. Tovarlar yuklangach tarozi uni o‘zi qayta o‘qiydi, agar yonida 954-parametrdagi nomli bo‘sh bayroq-fayl yotgan bo‘lsa. Kassa uni tovarlar, rasmlar va ssenariy yozilgandan keyin va import asosiy bayrog‘idan OLDIN qo‘yadi (almashuv papkasiga va ssenariylar papkasiga). Egasi tarozisining namunasi: 953 = 1, 954 = flag, 955 = 0, 956 = 60, 957 = 1."),
            out var body);
        var update = new CheckBox { Content = L("Обновлять главный экран весов при выгрузке (сценарий)", "Жүктөөдө таразанын башкы экранын жаңыртуу (сценарий)", "Update the scale's main screen on upload (scenario)", "Yüklemede tartının ana ekranını güncelle (senaryo)", "Yuklashda tarozining bosh ekranini yangilash (ssenariy)"), IsChecked = s.UpdateScenario };
        update.IsCheckedChanged += (_, _) => SaveKassa(x => x.UpdateScenario = update.IsChecked == true);
        var scenarioFile = new TextBox { Text = s.ScenarioFile, Watermark = ShtrikhPcScenarioWriter.DefaultFileName, MinWidth = 220 };
        scenarioFile.LostFocus += (_, _) => SaveKassa(x => x.ScenarioFile = (scenarioFile.Text ?? "").Trim());
        var reload = new CheckBox { Content = L("Создавать флаг перезагрузки сценария", "Сценарийди кайра жүктөө желегин түзүү", "Create the scenario reload flag", "Senaryo yeniden yükleme bayrağını oluştur", "Ssenariyni qayta yuklash bayrog‘ini yaratish"), IsChecked = s.ScenarioReloadFlagEnabled };
        reload.IsCheckedChanged += (_, _) => SaveKassa(x => x.ScenarioReloadFlagEnabled = reload.IsChecked == true);
        var flagName = new TextBox { Text = s.ScenarioReloadFlag, Watermark = ShtrikhPcSettings.DefaultReloadFlag, MinWidth = 220 };
        flagName.LostFocus += (_, _) => SaveKassa(x => x.ScenarioReloadFlag = (flagName.Text ?? "").Trim());
        body.Children.Add(update);
        body.Children.Add(Row(L("Файл сценария", "Сценарий файлы", "Scenario file", "Senaryo dosyası", "Ssenariy fayli"), scenarioFile));
        body.Children.Add(reload);
        body.Children.Add(Row(L("Имя флага (параметр 954)", "Желек аты (954-параметр)", "Flag name (parameter 954)", "Bayrak adı (parametre 954)", "Bayroq nomi (954-parametr)"), flagName));

        _unknownText = Text("", "hint");
        var unknownCard = Card(L("Прочие параметры из файла (только чтение)", "Файлдагы башка параметрлер (окуу гана)", "Other parameters from the file (read only)", "Dosyadaki diğer parametreler (salt okunur)", "Fayldagi boshqa parametrlar (faqat o‘qish)"),
            L("Параметры, которых нет в руководстве, касса показывает, но не меняет.", "Колдонмодо жок параметрлерди касса көрсөтөт, бирок өзгөртпөйт.", "Parameters that are not in the manual are shown but not changed.", "Kılavuzda olmayan parametreler gösterilir ama değiştirilmez.", "Qo‘llanmada yo‘q parametrlar ko‘rsatiladi, lekin o‘zgartirilmaydi."), out var uBody);
        uBody.Children.Add(_unknownText);
        return BuildParamTab(ShtrikhPcTab.Advanced, new Control[] { card }, new Control[] { unknownCard });
    }

    // =====================================================================================
    // Чтение
    // =====================================================================================

    private async Task ReadFromScalesAsync()
    {
        SaveConnection();
        if (_busy)
            return;
        _busy = true;
        UpdateState();
        ShowResult(L("Чтение настроек весов…", "Таразанын жөндөөлөрү окулууда…", "Reading the scale settings…", "Tartı ayarları okunuyor…", "Tarozi sozlamalari o‘qilmoqda…"), false);
        try
        {
            var result = await ShtrikhPcConfigIo.ReadFromFolderAsync(ShtrikhPcSettings.Current).ConfigureAwait(true);
            AcceptRead(result);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private async Task ReadFromFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = L("Config.txt (или Export.txt) весов", "Таразанын Config.txt (же Export.txt) файлы", "The scale's Config.txt (or Export.txt)", "Tartının Config.txt (veya Export.txt) dosyası", "Tarozining Config.txt (yoki Export.txt) fayli"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("TXT") { Patterns = ["*.txt"] }, FilePickerFileTypes.All],
        }).ConfigureAwait(true);
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
            return;
        AcceptRead(ShtrikhPcConfigIo.ReadFromFile(path));
    }

    private void AcceptRead(ShtrikhPcConfigIoResult result)
    {
        if (result.Status != ShtrikhPcConfigIoStatus.Ok || result.Data is null)
        {
            ShowResult(IoStatusText(result), true);
            return;
        }
        _baseline = result.Data;
        _baselineText = result.Text ?? "";
        _baselineSource = result.Path;
        _baselineTime = result.FileTime;
        LoadEditors(_baseline);
        SetEditorsEnabled(true);
        UpdateState();
        PosLogger.Log($"Весы РС-200: прочитаны настройки ({_baseline.Count} параметров) из {result.Path}", "SCALES");
        var import = _baseline.GetInt(814, 0) == 1;
        RunCompare(false);
        ShowResult(L($"Прочитано параметров: {_baseline.Count} (файл {result.Path}).", $"Параметрлер окулду: {_baseline.Count} (файл {result.Path}).", $"Parameters read: {_baseline.Count} (file {result.Path}).", $"Okunan parametre: {_baseline.Count} (dosya {result.Path}).", $"O‘qilgan parametrlar: {_baseline.Count} (fayl {result.Path}).")
            + (import ? "" : " ⚠ " + ImportDisabledText()), !import);
    }

    private static string ImportDisabledText() => L(
        "На весах выключен «Импорт конфигурации» (параметр 814 = 0): файл настроек весы не применят. Включите: на весах «Система и печать» → «Конфигурация» → «База данных» → «Импорт» → галочка «Конфигурация». До этого кнопка «Сохранить файл настроек…» даст готовый файл для ручного «Импорт данных».",
        "Таразада «Конфигурацияны импорттоо» өчүк (814-параметр = 0): жөндөөлөр файлын тараза колдонбойт. Күйгүзүңүз: таразада «Система и печать» → «Конфигурация» → «База данных» → «Импорт» → «Конфигурация» белгиси. Ага чейин «Жөндөөлөр файлын сактоо…» кол менен «Импорт данных» үчүн даяр файл берет.",
        "“Import configuration” is off on the scale (parameter 814 = 0): the scale will not apply a settings file. Turn it on: on the scale “System and printing” → “Configuration” → “Database” → “Import” → the “Configuration” box. Until then the “Save the settings file…” button gives a ready file for a manual “Import data”.",
        "Tartıda «Yapılandırmayı içe aktar» kapalı (parametre 814 = 0): tartı ayar dosyasını uygulamaz. Açın: tartıda «Система и печать» → «Конфигурация» → «База данных» → «Импорт» → «Конфигурация» kutusu. O zamana kadar «Ayar dosyasını kaydet…» düğmesi elle «Импорт данных» için hazır dosya verir.",
        "Tarozida «Konfiguratsiyani import» o‘chiq (814-parametr = 0): tarozi sozlamalar faylini qo‘llamaydi. Yoqing: tarozida «Система и печать» → «Конфигурация» → «База данных» → «Импорт» → «Конфигурация» belgisi. Shunga qadar «Sozlamalar faylini saqlash…» tugmasi qo‘lda «Импорт данных» uchun tayyor fayl beradi.");

    private static string IoStatusText(ShtrikhPcConfigIoResult r) => r.Status switch
    {
        ShtrikhPcConfigIoStatus.NoFolder => L("Не задан путь к общей папке весов — впишите его на вкладке «Подключение» или откройте Config.txt с флешки.", "Таразанын жалпы папкасынын жолу коюлган эмес — «Туташуу» өтмөгүнө жазыңыз же флешкедеги Config.txt ачыңыз.", "The path to the scale's shared folder is not set — enter it on the “Connection” tab or open Config.txt from a USB stick.", "Tartının paylaşım klasör yolu ayarlanmamış — «Bağlantı» sekmesine yazın veya USB bellekten Config.txt açın.", "Tarozining umumiy papkasi yo‘li belgilanmagan — «Ulanish» varag‘iga yozing yoki fleshkadan Config.txt oching."),
        ShtrikhPcConfigIoStatus.FolderUnavailable => L("Папка весов не отвечает (3 с): весы выключены, не та сеть или папка не расшарена. Откройте Config.txt с флешки.", "Таразанын папкасы жооп бербейт (3 с): тараза өчүк, башка тармак же папка бөлүшүлгөн эмес. Флешкедеги Config.txt ачыңыз.", "The scale folder does not answer (3 s): the scale is off, on another network or the folder is not shared. Open Config.txt from a USB stick.", "Tartı klasörü yanıt vermiyor (3 sn): tartı kapalı, farklı ağda veya klasör paylaşılmamış. USB bellekten Config.txt açın.", "Tarozi papkasi javob bermayapti (3 s): tarozi o‘chiq, boshqa tarmoq yoki papka ulashilmagan. Fleshkadan Config.txt oching."),
        ShtrikhPcConfigIoStatus.NotFound => L("В папке весов нет Config.txt и Export\\Export.txt. Включите на весах «Экспорт конфигурации» (параметр 804) и нажмите «Экспорт данных» либо откройте Config.txt с флешки (кнопка «Открыть Config.txt…»).", "Таразанын папкасында Config.txt жана Export\\Export.txt жок. Таразада «Конфигурацияны экспорттоо» (804-параметр) күйгүзүп «Экспорт данных» басыңыз же флешкедеги Config.txt ачыңыз («Config.txt ачуу…» баскычы).", "The scale folder has neither Config.txt nor Export\\Export.txt. Turn on “Export configuration” on the scale (parameter 804) and press “Export data”, or open Config.txt from a USB stick (the “Open Config.txt…” button).", "Tartı klasöründe Config.txt de Export\\Export.txt de yok. Tartıda «Yapılandırmayı dışa aktar»ı (parametre 804) açıp «Экспорт данных»a basın veya USB bellekten Config.txt açın («Config.txt aç…» düğmesi).", "Tarozi papkasida Config.txt ham Export\\Export.txt yo‘q. Tarozida «Konfiguratsiyani eksport» ni (804-parametr) yoqib «Экспорт данных» ni bosing yoki fleshkadan Config.txt oching («Config.txt ochish…» tugmasi)."),
        ShtrikhPcConfigIoStatus.NoAccess => L($"Нет доступа к файлу ({r.Detail}): проверьте логин и пароль Windows весов.", $"Файлга жетүү жок ({r.Detail}): таразанын Windows логинин жана сырсөзүн текшериңиз.", $"No access to the file ({r.Detail}): check the scale's Windows login and password.", $"Dosyaya erişim yok ({r.Detail}): tartının Windows kullanıcı adını ve şifresini kontrol edin.", $"Faylga kirish yo‘q ({r.Detail}): tarozining Windows login va parolini tekshiring."),
        ShtrikhPcConfigIoStatus.NoConnection => L("Нет подключения к весам (кабель, IP, весы включены?).", "Таразага туташуу жок (кабель, IP, тараза күйүкпү?).", "No connection to the scale (cable, IP, is the scale on?).", "Tartıya bağlantı yok (kablo, IP, tartı açık mı?).", "Taroziga ulanish yo‘q (kabel, IP, tarozi yoqilganmi?)."),
        ShtrikhPcConfigIoStatus.BadCredentials => L("Неверный логин или пароль Windows весов.", "Таразанын Windows логини же сырсөзү туура эмес.", "Wrong Windows login or password of the scale.", "Tartının Windows kullanıcı adı veya şifresi yanlış.", "Tarozining Windows logini yoki paroli noto‘g‘ri."),
        ShtrikhPcConfigIoStatus.Unsupported => L("Сетевая папка весов (\\\\IP\\Shared) доступна только в Windows: на этом устройстве откройте Config.txt с флешки.", "Таразанын тармак папкасы (\\\\IP\\Shared) Windows та гана жеткиликтүү: бул түзмөктө флешкедеги Config.txt ачыңыз.", "The scale's network folder (\\\\IP\\Shared) is available only on Windows: on this device open Config.txt from a USB stick.", "Tartının ağ klasörüne (\\\\IP\\Shared) yalnızca Windows'ta erişilir: bu cihazda USB bellekten Config.txt açın.", "Tarozining tarmoq papkasi (\\\\IP\\Shared) faqat Windows da mavjud: bu qurilmada fleshkadan Config.txt oching."),
        ShtrikhPcConfigIoStatus.ImportDisabled => ImportDisabledText(),
        ShtrikhPcConfigIoStatus.Pending => L("Прежний файл импорта ещё не принят весами (в папке лежит флаг-файл). Подождите период «Обновление» или нажмите на весах «Импорт данных», затем повторите.", "Мурунку импорт файлын тараза азырынча кабыл алган жок (папкада желек-файл жатат). «Обновление» мөөнөтүн күтүңүз же таразада «Импорт данных» басыңыз, анан кайра жасаңыз.", "The previous import file has not been accepted by the scale yet (a flag file is in the folder). Wait for the “Update” period or press “Import data” on the scale, then repeat.", "Önceki içe aktarma dosyası tartı tarafından henüz kabul edilmedi (klasörde bayrak dosyası var). «Обновление» süresini bekleyin veya tartıda «Импорт данных»a basın, sonra tekrarlayın.", "Oldingi import faylini tarozi hali qabul qilmadi (papkada bayroq-fayl yotibdi). «Обновление» muddatini kuting yoki tarozida «Импорт данных» ni bosing, so‘ng takrorlang."),
        ShtrikhPcConfigIoStatus.NothingToChange => L("Нечего отправлять: в окне ничего не изменено.", "Жөнөтө турган эч нерсе жок: терезеде эч нерсе өзгөрткөн эмес.", "Nothing to send: nothing was changed in the window.", "Gönderilecek bir şey yok: pencerede hiçbir şey değişmedi.", "Yuboradigan narsa yo‘q: oynada hech narsa o‘zgartirilmadi."),
        _ => L("Не получилось: ", "Болгон жок: ", "Failed: ", "Olmadı: ", "Bo‘lmadi: ") + (r.Detail ?? r.Status.ToString()),
    };

    // =====================================================================================
    // Запись
    // =====================================================================================

    private string? ValidateAll(IReadOnlyList<ShtrikhPcChange> changes)
    {
        foreach (var c in changes)
            if (ShtrikhPcConfig.Find(c.Number) is { } p && ShtrikhPcConfig.Validate(p, c.New) is { } error)
                return error;
        return null;
    }

    /// <summary>Отдельное подтверждение для опасных параметров (номер весов, пути обмена, импорт/экспорт, режимы); для знаков цены (12) — ещё и предел цены и сверка с кассой.</summary>
    private async Task<bool> ConfirmDangerAsync(IReadOnlyList<ShtrikhPcChange> changes)
    {
        var lines = new List<string>();
        foreach (var c in changes)
        {
            var reason = ShtrikhPcConfig.DangerReason(c.Number)?.Invoke();
            if (c.Number == 12 && int.TryParse(c.New, out var decimals))
            {
                var max = ScalePriceLimits.FormatMax(ScalePriceLimits.ShtrikhPcMaxUnits, Math.Clamp(decimals, 0, 3));
                var kassa = ShtrikhPcSettings.Current.PriceDecimals;
                reason = L($"Знаки цены {c.Old} → {c.New}: предел цены станет {max} сом. В кассе сейчас {kassa} знаков — после того как весы применят настройку, нажмите «Подтянуть настройки кассы из весов», иначе цены на весах уедут в 10 или 100 раз.",
                    $"Баанын белгилери {c.Old} → {c.New}: баанын чеги {max} сом болот. Кассада азыр {kassa} белги — тараза жөндөөнү колдонгондон кийин «Кассанын жөндөөлөрүн таразадан алуу» басыңыз, болбосо таразадагы баалар 10 же 100 эсе жылат.",
                    $"Price decimals {c.Old} → {c.New}: the price limit becomes {max} som. The till has {kassa} decimals now — after the scale applies the setting press “Pull the till settings from the scale”, otherwise prices on the scale will be off by 10 or 100 times.",
                    $"Fiyat ondalığı {c.Old} → {c.New}: fiyat sınırı {max} som olur. Kasada şu an {kassa} hane var — tartı ayarı uyguladıktan sonra «Kasa ayarlarını tartıdan al»a basın, yoksa tartıdaki fiyatlar 10 veya 100 kat kayar.",
                    $"Narx xonalari {c.Old} → {c.New}: narx chegarasi {max} so‘m bo‘ladi. Kassada hozir {kassa} xona — tarozi sozlamani qo‘llagach «Kassa sozlamalarini tarozidan olish» ni bosing, aks holda tarozidagi narxlar 10 yoki 100 baravar siljiydi.");
            }
            if (reason is null)
                continue;
            var title = ShtrikhPcConfig.Find(c.Number)?.Title() ?? "";
            lines.Add($"• {c.Number} «{title}»: {c.Old} → {c.New}. {reason}");
        }
        if (lines.Count == 0)
            return true;
        return await PosDialogHost.ShowAsync(new PosConfirmDialog(
            L("Опасные параметры весов", "Таразанын коркунучтуу параметрлери", "Risky scale parameters", "Riskli tartı parametreleri", "Tarozining xavfli parametrlari"),
            string.Join("\n\n", lines),
            confirmText: L("Всё равно отправить", "Баары бир жөнөтүү", "Send anyway", "Yine de gönder", "Baribir yuborish"),
            accent: PosConfirmAccent.Danger), this).ConfigureAwait(true) == true;
    }

    private async Task SendAsync() => await WriteChangesAsync(ComputeChanges(), restoring: false).ConfigureAwait(true);

    private async Task WriteChangesAsync(List<ShtrikhPcChange> changes, bool restoring)
    {
        if (_baseline is null || _busy)
            return;
        if (changes.Count == 0)
        {
            ShowResult(IoStatusText(new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NothingToChange)), false);
            return;
        }
        if (ValidateAll(changes) is { } error)
        {
            ShowResult(error, true);
            return;
        }
        if (_baseline.GetInt(814, 0) != 1)
        {
            ShowResult(ImportDisabledText(), true);
            return;
        }
        if (!await ConfirmDangerAsync(changes).ConfigureAwait(true))
            return;
        _busy = true;
        UpdateState();
        try
        {
            // Резервная копия прочитанного Config.txt — перед записью (один раз на «прочтение»).
            string? backup = null;
            if (!restoring && _baselineText.Length > 0)
            {
                try
                {
                    backup = ShtrikhPcConfigBackup.Save(_baselineText, DateTime.Now);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    PosLogger.Log($"Весы РС-200: резервная копия Config.txt не сохранена ({ex.Message})", "SCALES");
                }
            }
            ShowResult(L("Запись настроек в папку весов…", "Жөндөөлөрдү таразанын папкасына жазуу…", "Writing the settings to the scale folder…", "Ayarlar tartı klasörüne yazılıyor…", "Sozlamalar tarozi papkasiga yozilmoqda…"), false);
            var result = await ShtrikhPcConfigIo.WriteAsync(changes, _baseline, ShtrikhPcSettings.Current).ConfigureAwait(true);
            if (result.Status != ShtrikhPcConfigIoStatus.Ok)
            {
                ShowResult(IoStatusText(result), true);
                return;
            }
            PosLogger.Log($"Весы РС-200: записаны параметры {string.Join(", ", changes.Select(c => c.Number + "=" + c.New))} в {result.Path}" + (backup is not null ? $"; копия {backup}" : ""), "SCALES");
            _baseline = ShtrikhPcConfig.Apply(_baseline, changes);
            LoadEditors(_baseline);
            var minutes = _baseline.GetInt(11, 0);
            ShowResult(L($"Записано параметров: {changes.Count} (файл {result.Path}). Весы применят их в течение {(minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : "нескольких")} мин. (параметр 11) или сразу — «Система и печать» → «Импорт данных»." + (backup is not null ? $" Копия прежнего Config.txt: {backup}." : ""),
                $"Жазылган параметрлер: {changes.Count} (файл {result.Path}). Тараза аларды {(minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : "бир нече")} мүнөттө (11-параметр) же дароо колдонот — «Система и печать» → «Импорт данных»." + (backup is not null ? $" Мурунку Config.txt көчүрмөсү: {backup}." : ""),
                $"Parameters written: {changes.Count} (file {result.Path}). The scale applies them within {(minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : "a few")} min (parameter 11) or right away — “System and printing” → “Import data”." + (backup is not null ? $" Copy of the previous Config.txt: {backup}." : ""),
                $"Yazılan parametre: {changes.Count} (dosya {result.Path}). Tartı bunları {(minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : "birkaç")} dk içinde (parametre 11) veya hemen uygular — «Система и печать» → «Импорт данных»." + (backup is not null ? $" Önceki Config.txt kopyası: {backup}." : ""),
                $"Yozilgan parametrlar: {changes.Count} (fayl {result.Path}). Tarozi ularni {(minutes > 0 ? minutes.ToString(CultureInfo.InvariantCulture) : "bir necha")} daqiqada (11-parametr) yoki darhol qo‘llaydi — «Система и печать» → «Импорт данных»." + (backup is not null ? $" Oldingi Config.txt nusxasi: {backup}." : "")), false);
            RunCompare(false);
        }
        finally
        {
            _busy = false;
            UpdateState();
        }
    }

    private async Task SaveFileAsync()
    {
        var changes = ComputeChanges();
        if (changes.Count == 0)
        {
            ShowResult(IoStatusText(new ShtrikhPcConfigIoResult(ShtrikhPcConfigIoStatus.NothingToChange)), false);
            return;
        }
        if (ValidateAll(changes) is { } error)
        {
            ShowResult(error, true);
            return;
        }
        if (!await ConfirmDangerAsync(changes).ConfigureAwait(true))
            return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = L("Файл настроек для весов (импорт)", "Таразанын жөндөөлөр файлы (импорт)", "Settings file for the scale (import)", "Tartı için ayar dosyası (içe aktarma)", "Tarozi uchun sozlamalar fayli (import)"),
            SuggestedFileName = "PCScale-config.txt",
            FileTypeChoices = [new FilePickerFileType("TXT") { Patterns = ["*.txt"] }],
        }).ConfigureAwait(true);
        var path = file?.TryGetLocalPath();
        if (path is null)
            return;
        var result = ShtrikhPcConfigIo.SaveToFile(changes, ShtrikhPcSettings.Current, path);
        if (result.Status != ShtrikhPcConfigIoStatus.Ok)
        {
            ShowResult(IoStatusText(result), true);
            return;
        }
        PosLogger.Log($"Весы РС-200: файл настроек ({changes.Count} параметров) сохранён в {path}", "SCALES");
        ShowResult(L($"Файл настроек сохранён: {path} (параметров: {changes.Count}). На весах включите импорт конфигурации (параметр 814): «Конфигурация» → «База данных» → «Импорт» → галочка «Конфигурация», затем «Импорт данных» с этим файлом.",
            $"Жөндөөлөр файлы сакталды: {path} (параметрлер: {changes.Count}). Таразада конфигурацияны импорттоону күйгүзүңүз (814-параметр): «Конфигурация» → «База данных» → «Импорт» → «Конфигурация» белгиси, анан ушул файл менен «Импорт данных».",
            $"Settings file saved: {path} (parameters: {changes.Count}). On the scale turn on the configuration import (parameter 814): “Configuration” → “Database” → “Import” → the “Configuration” box, then “Import data” with this file.",
            $"Ayar dosyası kaydedildi: {path} (parametre: {changes.Count}). Tartıda yapılandırma içe aktarmayı açın (parametre 814): «Конфигурация» → «База данных» → «Импорт» → «Конфигурация» kutusu, sonra bu dosyayla «Импорт данных».",
            $"Sozlamalar fayli saqlandi: {path} (parametrlar: {changes.Count}). Tarozida konfiguratsiyani importni yoqing (814-parametr): «Конфигурация» → «База данных» → «Импорт» → «Конфигурация» belgisi, so‘ng shu fayl bilan «Импорт данных»."), false);
    }

    private void RevertEdits()
    {
        if (_baseline is null)
            return;
        LoadEditors(_baseline);
        UpdateState();
        ShowResult(L("Правки в окне отменены: показано то, что прочитано с весов.", "Терезедеги оңдоолор жокко чыгарылды: таразадан окулганы көрсөтүлдү.", "Edits in the window were discarded: what was read from the scale is shown.", "Penceredeki düzenlemeler iptal edildi: tartıdan okunan gösteriliyor.", "Oynadagi tahrirlar bekor qilindi: tarozidan o‘qilgani ko‘rsatildi."), false);
    }

    /// <summary>«Вернуть на весах прочитанное»: сравнивает последнюю резервную копию Config.txt с тем, что сейчас на весах (по прочитанному и записанному), и записывает обратно расходящиеся параметры.</summary>
    private async Task RestoreAsync()
    {
        if (_baseline is null)
            return;
        var path = ShtrikhPcConfigBackup.Latest();
        if (path is null)
        {
            ShowResult(L("Резервной копии ещё нет: она создаётся перед первой записью настроек.", "Камдык көчүрмө али жок: ал жөндөөлөрдү биринчи жазуунун алдында түзүлөт.", "There is no backup yet: it is created before the first write of settings.", "Henüz yedek yok: ayarların ilk yazımından önce oluşturulur.", "Zaxira nusxa hali yo‘q: u sozlamalar birinchi yozilishidan oldin yaratiladi."), true);
            return;
        }
        var backup = ShtrikhPcConfig.ParseBytes(await File.ReadAllBytesAsync(path).ConfigureAwait(true));
        var changes = ShtrikhPcConfig.DiffToRestore(_baseline, backup);
        if (changes.Count == 0)
        {
            ShowResult(L("На весах уже то, что в резервной копии: возвращать нечего.", "Таразада камдык көчүрмөдөгү эле: кайтарууга эч нерсе жок.", "The scale already has what is in the backup: nothing to restore.", "Tartıda zaten yedekteki var: geri yüklenecek bir şey yok.", "Tarozida zaxira nusxadagi narsa allaqachon bor: qaytaradigan narsa yo‘q."), false);
            return;
        }
        await WriteChangesAsync(changes, restoring: true).ConfigureAwait(true);
    }

    // =====================================================================================
    // Сверка с кассой
    // =====================================================================================

    private void RunCompare(bool announce)
    {
        _compareBody.Children.Clear();
        if (_baseline is null)
        {
            _compareBody.Children.Add(Text(L("Сначала прочитайте настройки весов.", "Адегенде таразанын жөндөөлөрүн окуңуз.", "Read the scale settings first.", "Önce tartı ayarlarını okuyun.", "Avval tarozi sozlamalarini o‘qing."), "hint"));
            if (announce)
                ShowResult(L("Сначала прочитайте настройки весов.", "Адегенде таразанын жөндөөлөрүн окуңуз.", "Read the scale settings first.", "Önce tartı ayarlarını okuyun.", "Avval tarozi sozlamalarini o‘qing."), true);
            return;
        }
        var items = ShtrikhPcConfig.Compare(ShtrikhPcSettings.Current, _baseline);
        var ok = items.All(i => i.Ok);
        foreach (var item in items)
            _compareBody.Children.Add(Themed(this, Text((item.Ok ? "✓ " : "⚠ ") + item.Text, "label"), TextBlock.ForegroundProperty, item.Ok ? "BrushSuccess" : "BrushWarning", item.Ok ? Brushes.Green : Brushes.DarkOrange));
        Themed(this, _compareBorder, Border.BorderBrushProperty, ok ? "BrushSuccess" : "BrushWarning", ok ? Brushes.Green : Brushes.DarkOrange);
        Themed(this, _compareBorder, Border.BackgroundProperty, ok ? "BrushPanelSoft" : "BrushWarningSoft", Brushes.Transparent);
        if (announce)
            ShowResult(ok
                ? L("Сверка: настройки весов и кассы совпадают.", "Салыштыруу: таразанын жана кассанын жөндөөлөрү дал келет.", "Comparison: the scale and till settings match.", "Karşılaştırma: tartı ve kasa ayarları aynı.", "Solishtirish: tarozi va kassa sozlamalari mos.")
                : L("Сверка: есть расхождения — смотрите жёлтую рамку на вкладке «Штрих-код». Кнопка «Подтянуть настройки кассы из весов» выравнивает кассу по весам.", "Салыштыруу: айырмачылыктар бар — «Штрих-код» өтмөгүндөгү сары алкакты караңыз. «Кассанын жөндөөлөрүн таразадан алуу» баскычы кассаны таразага тууралайт.", "Comparison: there are differences — see the yellow frame on the “Barcode” tab. The “Pull the till settings from the scale” button aligns the till with the scale.", "Karşılaştırma: farklar var — «Barkod» sekmesindeki sarı çerçeveye bakın. «Kasa ayarlarını tartıdan al» düğmesi kasayı tartıya göre eşitler.", "Solishtirish: farqlar bor — «Shtrix-kod» varag‘idagi sariq ramkaga qarang. «Kassa sozlamalarini tarozidan olish» tugmasi kassani tarozi bo‘yicha tenglashtiradi."), !ok);
    }

    private void PullToKassa()
    {
        if (_baseline is null)
        {
            ShowResult(L("Сначала прочитайте настройки весов.", "Адегенде таразанын жөндөөлөрүн окуңуз.", "Read the scale settings first.", "Önce tartı ayarlarını okuyun.", "Avval tarozi sozlamalarini o‘qing."), true);
            return;
        }
        var s = ShtrikhPcSettings.Current;
        var changed = ShtrikhPcConfig.ApplyScaleToKassa(s, _baseline);
        ScaleBarcodeRules.ApplyShtrikhPc(s);
        UserPreferences.Instance.SaveToDisk();
        PosLogger.Log($"Весы РС-200: настройки кассы выровнены по весам, изменено полей: {changed}", "SCALES");
        RunCompare(false);
        ShowResult(changed == 0
            ? L("Касса уже совпадает с весами.", "Касса таразага эле дал келет.", "The till already matches the scale.", "Kasa zaten tartıyla aynı.", "Kassa allaqachon tarozi bilan mos.")
            : L($"Настройки кассы выровнены по весам: изменено полей {changed}.", $"Кассанын жөндөөлөрү таразага тууралалды: өзгөргөн талаалар {changed}.", $"The till settings were aligned with the scale: {changed} fields changed.", $"Kasa ayarları tartıya göre eşitlendi: değişen alan {changed}.", $"Kassa sozlamalari tarozi bo‘yicha tenglashtirildi: o‘zgargan maydonlar {changed}."), false);
    }

    // =====================================================================================

    private void ShowResult(string text, bool isError)
    {
        ResultText.Text = text;
        Themed(this, ResultBorder, Border.BorderBrushProperty, isError ? "BrushWarning" : "BrushBorder", Brushes.Gray);
        Themed(this, ResultBorder, Border.BackgroundProperty, isError ? "BrushWarningSoft" : "BrushPanelSoft", Brushes.Transparent);
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        SaveConnection();
        Close();
    }
}
