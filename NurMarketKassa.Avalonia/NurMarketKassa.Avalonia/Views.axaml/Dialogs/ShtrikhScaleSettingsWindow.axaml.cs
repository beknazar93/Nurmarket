using System.Globalization;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using P = NurMarketKassa.Services.Hardware.ShtrikhPrintProtocol;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-09-28: «Настройки весов Штрих-ПРИНТ». Владелец попросил («вот эти настройки нужны
/// для штрих м … пройтись по программе полностью и создать настройки в нашей программе»)
/// перенести в кассу настройки тестовой программы Штрих-М «Тест драйвера весов
/// "ШТРИХ-ПРИНТ"». Вкладки повторяют её разделы, но с понятными подписями:
/// «Клавиатура» (клавиши быстрого доступа, функциональные клавиши, блокировка), «Состояние»,
/// «Система», «Товары», «Печать и этикетка», «Штрих-код», «Тексты».
///
/// Что НЕ перенесено и почему (подробно — в отчёте к задаче):
/// • эмуляция клавиатуры, ноль, тара, выбор товара, печать этикетки — нужен спецпароль,
///   который Штрих-М выдаёт под каждый заводской номер (раздел «Особенности» протокола);
/// • редактор координат/шрифтов формата этикетки (A0h..A7h) и загрузка картинок (C0h..C5h) —
///   для этого у Штрих-М есть отдельные программы «Редактор этикеток» и «Загрузчик»;
///   (2026-09-28: координаты/шрифты своих форматов A0h..A7h, символы валют C1h/C2h и курс
///   2Bh теперь есть — вкладки «Валюта» и «Макет этикетки», см. …Currency.cs; картинки C0h — нет);
/// • «Положение точки» (20h) и «Товары/сообщения» (D5h) — меняют смысл всех цен / требуют
///   очистки базы; показываются только для чтения;
/// • «Восстановить параметры» (17h) и параметры обмена (14h/15h) — по протоколу только RS-232;
/// • логические устройства (вкладка «ЛУ») — понятие драйвера, у кассы свой список весов.
///
/// Правило безопасности: любая команда с паролем — это попытка входа, весы после пяти
/// неудачных блокируют доступ. Поэтому при ошибке пароля любое чтение/запись сразу
/// останавливается, а «Записать» отправляет только изменённые после чтения параметры.
/// </summary>
public partial class ShtrikhScaleSettingsWindow : Window
{
    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private enum Kind { Toggle, Choice, Number, Text, Bits }

    /// <summary>Одна строка настройки: откуда читать, куда писать и чем показывать.</summary>
    private sealed class Param
    {
        public string Title = "";
        public byte GetCmd;
        public byte SetCmd;
        public Kind Kind;
        public int Width = 1;             // байт в команде (число/текст)
        public int? Index;                // номер строки/текста для 94h/99h
        public bool Inverted;             // «Свободная цена»: 0 — разрешена
        public long Min;
        public long Max = 255;
        public (long Value, string Label)[] Options = Array.Empty<(long, string)>();
        public Func<ShtrikhScaleStatus, long>? FromStatus;   // значение берётся из 11h
        public Control? Editor;
        public List<CheckBox>? BitBoxes;
        public object? Loaded;            // что прочитали с весов (long или string)
        public bool Unreadable;           // весы не отдают значение (код 120/121)
    }

    private readonly List<Param> _systemParams = new();
    private readonly List<Param> _goodsParams = new();
    private readonly List<Param> _printParams = new();
    private readonly List<Param> _barcodeParams = new();
    private readonly List<Param> _textParams = new();

    private ShtrikhScaleStatus? _status;
    private bool _busy;

    // Клавиатура.
    private NumericUpDown _hotkeyNumber = null!;
    private ComboBox _hotkeyFunction = null!;
    private TextBlock _hotkeyValueLabel = null!;
    private TextBox _hotkeyValue = null!;
    private TextBlock _hotkeyInfo = null!;
    private TextBlock _hotkeyList = null!;
    private readonly CheckBox[] _functionKeys = new CheckBox[5];
    private TextBlock _functionKeysHint = null!;
    private TextBlock _lockState = null!;
    private TextBlock _statusText = null!;
    private TextBlock _clockText = null!;
    private TextBox _urgentText = null!;

    // Префиксы ШК — одна команда чтения (76h) на три поля.
    private Param? _prefixWeight, _prefixPiece, _prefixTotal;

    public ShtrikhScaleSettingsWindow()
    {
        InitializeComponent();
        BuildHeader();
        BuildKeyboardTab();
        BuildStatusTab();
        BuildSystemTab();
        BuildGoodsTab();
        BuildPrintTab();
        BuildBarcodeTab();
        BuildTextsTab();
        BuildCurrencyAndLayoutTabs(); // 2026-09-28: «Валюта» и «Макет этикетки» — ShtrikhScaleSettingsWindow.Currency.cs
        ShowResult(L("Нажмите «Проверить связь», затем «Прочитать с весов» на нужной вкладке.",
            "«Байланышты текшерүү» баскычын, андан кийин керектүү өтмөктө «Таразадан окуу» баскычын басыңыз.",
            "Press “Check connection”, then “Read from scale” on the tab you need.",
            "“Bağlantıyı kontrol et”e, ardından ilgili sekmede “Tartıdan oku”ya basın.",
            "«Aloqani tekshirish»ni, so‘ng kerakli bo‘limda «Tarozidan o‘qish»ni bosing."), false);
    }

    private async void Window_Opened(object? sender, EventArgs e)
    {
        // 2026-09-29: 820×720 не помещалось на 1024×768 при 125–150 % — по экрану кассы (DialogScreenFit).
        this.FitToKassaScreen();
        // Сразу узнаём исполнение весов: от него зависят число клавиш и подписи функциональных
        // клавиш. Команда 11h без пароля — не расходует попытки входа.
        await RunAsync(async scale =>
        {
            await LoadStatusAsync(scale).ConfigureAwait(true);
            return L("Весы на связи.", "Тараза байланышта.", "The scale is connected.", "Tartı bağlı.", "Tarozi aloqada.");
        }, quietIfOffline: true).ConfigureAwait(true);
    }

    // =====================================================================================
    // Общие строительные блоки
    // =====================================================================================

    private void BuildHeader()
    {
        var prefs = UserPreferences.Instance;
        ConnectionText.Text = L("Весы: ", "Тараза: ", "Scale: ", "Tartı: ", "Tarozi: ")
            + $"{prefs.ScaleNetworkIp}:{prefs.ScaleLanPort.ToString(CultureInfo.InvariantCulture)}"
            + L(" · адрес, порт и пароль — в Настройки → Весы", " · дарек, порт жана сырсөз — Жөндөөлөр → Таразада",
                " · address, port and password are in Settings → Scales", " · adres, port ve şifre: Ayarlar → Tartı",
                " · manzil, port va parol — Sozlamalar → Tarozi");
        CheckButton.Content = L("Проверить связь", "Байланышты текшерүү", "Check connection", "Bağlantıyı kontrol et", "Aloqani tekshirish");
        IntroText.Text = L(
            "Здесь те же настройки, что в программе Штрих-М «Тест драйвера весов». Сначала «Прочитать с весов», поменяйте нужное и нажмите «Записать в весы» — отправятся только изменённые значения.",
            "Бул жерде Штрих-М «Таразалар драйверинин тести» программасындагы жөндөөлөр. Адегенде «Таразадан окуу», керектүүсүн өзгөртүп «Таразага жазуу» басыңыз — өзгөргөн маанилер гана жөнөтүлөт.",
            "These are the same settings as in the Shtrih-M “scale driver test” program. First “Read from scale”, change what you need and press “Write to scale” — only changed values are sent.",
            "Bunlar Shtrih-M “tartı sürücüsü testi” programındaki ayarlardır. Önce “Tartıdan oku”, gerekeni değiştirin ve “Tartıya yaz”a basın — yalnızca değişen değerler gönderilir.",
            "Bu yerda Shtrih-M «tarozi drayveri testi» dasturidagi sozlamalar. Avval «Tarozidan o‘qish», keraklisini o‘zgartiring va «Taroziga yozish»ni bosing — faqat o‘zgargan qiymatlar yuboriladi.");

        KeyboardTab.Header = L("Клавиатура", "Клавиатура", "Keyboard", "Klavye", "Klaviatura");
        StatusTab.Header = L("Состояние", "Абалы", "Status", "Durum", "Holat");
        SystemTab.Header = L("Система", "Система", "System", "Sistem", "Tizim");
        GoodsTab.Header = L("Товары", "Товарлар", "Goods", "Ürünler", "Tovarlar");
        PrintTab.Header = L("Печать и этикетка", "Басып чыгаруу жана этикетка", "Printing & label", "Baskı ve etiket", "Chop etish va yorliq");
        BarcodeTab.Header = L("Штрих-код", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod");
        TextsTab.Header = L("Тексты", "Тексттер", "Texts", "Metinler", "Matnlar");
    }

    private static Border Card(string title, string? hint, out StackPanel body)
    {
        body = new StackPanel { Spacing = 10 };
        var root = new StackPanel { Spacing = 10 };
        root.Children.Add(new TextBlock { Text = title, Classes = { "cardTitle" } });
        if (!string.IsNullOrWhiteSpace(hint))
            root.Children.Add(new TextBlock { Text = hint, Classes = { "hint" } });
        root.Children.Add(body);
        return new Border { Classes = { "card" }, Child = root };
    }

    private static Button MakeButton(string text, bool primary, EventHandler<RoutedEventArgs> onClick)
    {
        var button = new Button { Content = text, Classes = { primary ? "btn-primary" : "btn-ok" } };
        button.Click += onClick;
        return button;
    }

    private static WrapPanel ButtonRow(params Control[] buttons)
    {
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(0, 0, 8, 6);
            row.Children.Add(b);
        }
        return row;
    }

    private static Grid Row(string label, Control editor)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        grid.Children.Add(new TextBlock { Text = label, Classes = { "label" }, Margin = new Thickness(0, 0, 12, 0) });
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = editor is TextBox ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        grid.Children.Add(editor);
        return grid;
    }

    /// <summary>Строит редактор параметра и кладёт строку в панель.</summary>
    private void AddParam(StackPanel panel, List<Param> list, Param p)
    {
        switch (p.Kind)
        {
            case Kind.Toggle:
                var box = new CheckBox { Content = p.Title, IsEnabled = false };
                p.Editor = box;
                panel.Children.Add(box);
                break;
            case Kind.Choice:
                var combo = new ComboBox { MinWidth = 280, IsEnabled = false };
                foreach (var (_, label) in p.Options)
                    combo.Items.Add(label);
                p.Editor = combo;
                panel.Children.Add(Row(p.Title, combo));
                break;
            case Kind.Number:
                var number = new NumericUpDown
                {
                    Minimum = p.Min, Maximum = p.Max, Increment = 1, FormatString = "0",
                    MinWidth = 160, IsEnabled = false,
                };
                p.Editor = number;
                panel.Children.Add(Row(p.Title, number));
                break;
            case Kind.Text:
                var text = new TextBox { MaxLength = p.Width, IsEnabled = false, Watermark = L($"до {p.Width} символов", $"{p.Width} белгиге чейин", $"up to {p.Width} characters", $"en fazla {p.Width} karakter", $"{p.Width} belgigacha") };
                p.Editor = text;
                panel.Children.Add(Row(p.Title, text));
                break;
            case Kind.Bits:
                var bitsPanel = new WrapPanel { Orientation = Orientation.Horizontal };
                p.BitBoxes = new List<CheckBox>();
                foreach (var (bit, label) in p.Options)
                {
                    var b = new CheckBox { Content = label, IsEnabled = false, Margin = new Thickness(0, 0, 16, 4), Tag = bit };
                    p.BitBoxes.Add(b);
                    bitsPanel.Children.Add(b);
                }
                panel.Children.Add(new TextBlock { Text = p.Title, Classes = { "label" } });
                panel.Children.Add(bitsPanel);
                break;
        }
        list.Add(p);
    }

    private static void SetEditorValue(Param p, object value)
    {
        switch (p.Kind)
        {
            case Kind.Toggle when p.Editor is CheckBox box:
                box.IsChecked = Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
                box.IsEnabled = true;
                break;
            case Kind.Choice when p.Editor is ComboBox combo:
                var v = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                var index = Array.FindIndex(p.Options, o => o.Value == v);
                if (index < 0)
                {
                    // Значение вне известного списка (новая прошивка) — показываем как есть.
                    combo.Items.Add($"{v}");
                    p.Options = p.Options.Append((v, $"{v}")).ToArray();
                    index = p.Options.Length - 1;
                }
                combo.SelectedIndex = index;
                combo.IsEnabled = true;
                break;
            case Kind.Number when p.Editor is NumericUpDown number:
                number.Value = Math.Clamp(Convert.ToInt64(value, CultureInfo.InvariantCulture), p.Min, p.Max);
                number.IsEnabled = true;
                break;
            case Kind.Text when p.Editor is TextBox text:
                text.Text = value as string ?? "";
                text.IsEnabled = true;
                break;
            case Kind.Bits when p.BitBoxes is not null:
                var mask = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                foreach (var b in p.BitBoxes)
                {
                    b.IsChecked = (mask & (1L << Convert.ToInt32(b.Tag, CultureInfo.InvariantCulture))) != 0;
                    b.IsEnabled = true;
                }
                break;
        }
    }

    private static object? GetEditorValue(Param p) => p.Kind switch
    {
        Kind.Toggle => p.Editor is CheckBox { IsChecked: true } ? 1L : 0L,
        Kind.Choice => p.Editor is ComboBox { SelectedIndex: >= 0 } c ? p.Options[c.SelectedIndex].Value : null,
        Kind.Number => p.Editor is NumericUpDown { Value: { } d } ? (long)d : null,
        Kind.Text => (p.Editor as TextBox)?.Text ?? "",
        Kind.Bits => p.BitBoxes?.Where(b => b.IsChecked == true)
            .Aggregate(0L, (acc, b) => acc | (1L << Convert.ToInt32(b.Tag, CultureInfo.InvariantCulture))),
        _ => null,
    };

    private void AddReadWriteButtons(StackPanel panel, List<Param> list, Func<ShtrikhPrintLanScaleService, Task>? extraRead = null)
    {
        var read = MakeButton(L("Прочитать с весов", "Таразадан окуу", "Read from scale", "Tartıdan oku", "Tarozidan o‘qish"), false,
            async (_, _) => await ReadParamsAsync(list, extraRead).ConfigureAwait(true));
        var write = MakeButton(L("Записать в весы", "Таразага жазуу", "Write to scale", "Tartıya yaz", "Taroziga yozish"), true,
            async (_, _) => await WriteParamsAsync(list).ConfigureAwait(true));
        var reset = MakeButton(L("Вернуть прочитанное", "Окулганды кайтаруу", "Revert to read values", "Okunan değerlere dön", "O‘qilganiga qaytarish"), false,
            (_, _) =>
            {
                foreach (var p in list.Where(p => p.Loaded is not null))
                    SetEditorValue(p, p.Loaded!);
            });
        panel.Children.Add(ButtonRow(read, write, reset));
    }

    // =====================================================================================
    // Связь
    // =====================================================================================

    private static ShtrikhPrintLanScaleService CreateScale()
    {
        var prefs = UserPreferences.Instance;
        return new ShtrikhPrintLanScaleService(prefs.ScaleNetworkIp ?? "", prefs.ScaleLanPort, prefs.ScaleLanPassword);
    }

    /// <summary>Выполняет операцию с весами: блокирует кнопки, показывает ответ весов,
    /// переводит ошибку пароля в понятный текст.</summary>
    private async Task RunAsync(Func<ShtrikhPrintLanScaleService, Task<string>> action, bool quietIfOffline = false)
    {
        if (_busy)
            return;
        _busy = true;
        Tabs.IsEnabled = false;
        CheckButton.IsEnabled = false;
        ShowResult(L("Обмен с весами…", "Тараза менен алмашуу…", "Talking to the scale…", "Tartı ile iletişim…", "Tarozi bilan almashuv…"), false);
        try
        {
            using var scale = CreateScale();
            var message = await Task.Run(() => action(scale)).ConfigureAwait(true);
            ShowResult("0: " + message, false);
        }
        catch (ShtrikhScaleException ex) when (ex.IsPasswordError)
        {
            ShowResult($"{ex.ErrorCode}: " + L(
                "Весы не приняли пароль. Проверьте пароль в Настройки → Весы (обычно 0030). Обмен остановлен, чтобы весы не заблокировали доступ.",
                "Тараза сырсөздү кабыл алган жок. Жөндөөлөр → Таразадан сырсөздү текшериңиз (адатта 0030). Тараза бөгөттөп калбашы үчүн алмашуу токтотулду.",
                "The scale rejected the password. Check it in Settings → Scales (usually 0030). Stopped so the scale does not lock access.",
                "Tartı şifreyi kabul etmedi. Ayarlar → Tartı'da kontrol edin (genelde 0030). Tartı erişimi kilitlemesin diye durduruldu.",
                "Tarozi parolni qabul qilmadi. Sozlamalar → Tarozida tekshiring (odatda 0030). Tarozi bloklamasligi uchun to‘xtatildi.")
                + (ex.ErrorCode == 170 ? " " + L("Сейчас доступ уже заблокирован: выключите и включите весы.", "Азыр кирүү бөгөттөлгөн: таразаны өчүрүп күйгүзүңүз.", "Access is already locked: power-cycle the scale.", "Erişim kilitli: tartıyı kapatıp açın.", "Kirish bloklangan: tarozini o‘chirib yoqing.") : ""), true);
        }
        catch (ShtrikhScaleException ex)
        {
            var offline = ex.ErrorCode == 0;
            if (offline && quietIfOffline)
                ShowResult("-1: " + L("Нет связи с весами. Проверьте IP-адрес и порт в Настройки → Весы.", "Тараза менен байланыш жок. IP-даректи жана портту Жөндөөлөр → Таразадан текшериңиз.", "No connection to the scale. Check the IP address and port in Settings → Scales.", "Tartı ile bağlantı yok. IP ve portu Ayarlar → Tartı'da kontrol edin.", "Tarozi bilan aloqa yo‘q. IP va portni Sozlamalar → Tarozida tekshiring."), true);
            else
                ShowResult((offline ? "-1: " : $"{ex.ErrorCode}: ") + ex.Message, true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Настройки весов Штрих-ПРИНТ: {ex}", "SCALES");
            ShowResult(ex.Message, true);
        }
        finally
        {
            _busy = false;
            Tabs.IsEnabled = true;
            CheckButton.IsEnabled = true;
        }
    }

    private void ShowResult(string text, bool isError)
    {
        ResultText.Text = text;
        ResultBorder.BorderBrush = ThemeBrush(isError ? "BrushWarning" : "BrushBorder", Brushes.Gray);
        ResultBorder.Background = ThemeBrush(isError ? "BrushWarningSoft" : "BrushSurfaceSubtle", Brushes.Transparent);
    }

    private IBrush ThemeBrush(string key, IBrush fallback) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush ? brush : fallback;

    private async Task LoadStatusAsync(ShtrikhPrintLanScaleService scale)
    {
        var status = await scale.GetStatusAsync().ConfigureAwait(false);
        _status = status;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ApplyStatus(status));
    }

    private async void Check_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(async scale =>
        {
            var info = await scale.TestConnectionAsync(beep: true).ConfigureAwait(false);
            await LoadStatusAsync(scale).ConfigureAwait(false);
            return L("Весы на связи: ", "Тараза байланышта: ", "Scale connected: ", "Tartı bağlı: ", "Tarozi aloqada: ") + info;
        }).ConfigureAwait(true);

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    // =====================================================================================
    // Чтение / запись списков параметров
    // =====================================================================================

    private async Task ReadParamsAsync(List<Param> list, Func<ShtrikhPrintLanScaleService, Task>? extraRead)
    {
        await RunAsync(async scale =>
        {
            var errors = new List<string>();
            ShtrikhScaleStatus? status = null;
            if (list.Any(p => p.FromStatus is not null))
            {
                status = await scale.GetStatusAsync().ConfigureAwait(false);
                _status = status;
            }

            foreach (var p in list)
            {
                if (p == _prefixPiece || p == _prefixTotal)
                    continue; // прочитаны вместе с весовым (одна команда 76h)
                try
                {
                    object value;
                    if (p.FromStatus is not null)
                        value = p.FromStatus(status!);
                    else if (p == _prefixWeight)
                    {
                        var prefixes = await scale.GetBarcodePrefixesAsync().ConfigureAwait(false);
                        _prefixPiece!.Loaded = (long)prefixes.Piece;
                        _prefixTotal!.Loaded = (long)prefixes.Total;
                        value = (long)prefixes.Weight;
                    }
                    else if (p.Kind == Kind.Text)
                        value = await scale.GetTextParamAsync(p.GetCmd, p.Width, p.Index).ConfigureAwait(false);
                    else if (p.Width > 1)
                        value = await scale.GetIntParamAsync(p.GetCmd, p.Width).ConfigureAwait(false);
                    else
                    {
                        long raw = await scale.GetByteParamAsync(p.GetCmd).ConfigureAwait(false);
                        value = p.Inverted ? (raw == 0 ? 1L : 0L) : raw;
                    }
                    p.Loaded = value;
                    p.Unreadable = false;
                }
                catch (ShtrikhScaleException ex) when (ex.IsPasswordError || ex.ErrorCode == 0)
                {
                    throw; // пароль или связь — дальше идти нельзя
                }
                catch (ShtrikhScaleException ex)
                {
                    // Команда не поддерживается этой моделью/прошивкой (120/121/123) — поле
                    // остаётся доступным для записи «с нуля», но честно помечается.
                    p.Loaded = p.Kind == Kind.Text ? "" : (object)0L;
                    p.Unreadable = true;
                    errors.Add($"{p.Title}: {ex.ErrorCode} {P.DescribeError(ex.ErrorCode)}");
                }
            }

            if (extraRead is not null)
                await extraRead(scale).ConfigureAwait(false);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var p in list.Where(p => p.Loaded is not null))
                    SetEditorValue(p, p.Loaded!);
                if (status is not null)
                    ApplyStatus(status);
            });

            return errors.Count == 0
                ? L("Прочитано.", "Окулду.", "Read.", "Okundu.", "O‘qildi.")
                : L("Прочитано, но не всё: ", "Окулду, бирок баары эмес: ", "Read, but not everything: ", "Okundu, ama hepsi değil: ", "O‘qildi, lekin hammasi emas: ") + string.Join("; ", errors);
        }).ConfigureAwait(true);
    }

    private async Task WriteParamsAsync(List<Param> list)
    {
        if (list.All(p => p.Loaded is null))
        {
            ShowResult(L("Сначала нажмите «Прочитать с весов» — без этого можно затереть настройки весов пустыми значениями.",
                "Адегенде «Таразадан окуу» басыңыз — антпесе тараза жөндөөлөрү бош маанилер менен өчүп калышы мүмкүн.",
                "Press “Read from scale” first — otherwise the scale settings could be overwritten with empty values.",
                "Önce “Tartıdan oku”ya basın — aksi halde tartı ayarları boş değerlerle silinebilir.",
                "Avval «Tarozidan o‘qish»ni bosing — aks holda tarozi sozlamalari bo‘sh qiymatlar bilan o‘chib ketishi mumkin."), true);
            return;
        }

        // Значения из редакторов снимаем в UI-потоке до ухода в фон.
        var changes = list.Where(p => p.Loaded is not null)
            .Select(p => (Param: p, Value: GetEditorValue(p)))
            .Where(x => x.Value is not null && !Equals(Normalize(x.Value), Normalize(x.Param.Loaded)))
            .ToList();

        if (changes.Count == 0)
        {
            ShowResult(L("Изменений нет — в весы ничего не отправлено.", "Өзгөртүү жок — таразага эч нерсе жөнөтүлгөн жок.", "No changes — nothing was sent to the scale.", "Değişiklik yok — tartıya bir şey gönderilmedi.", "O‘zgarish yo‘q — tarozga hech narsa yuborilmadi."), false);
            return;
        }

        await RunAsync(async scale =>
        {
            var written = 0;
            var errors = new List<string>();
            foreach (var (p, value) in changes)
            {
                try
                {
                    if (p == _prefixWeight || p == _prefixPiece || p == _prefixTotal)
                    {
                        var type = p == _prefixWeight ? 0 : p == _prefixPiece ? 1 : 2;
                        await scale.SetBarcodePrefixAsync(type, (int)(long)value!).ConfigureAwait(false);
                    }
                    else if (p.Kind == Kind.Text)
                        await scale.SetTextParamAsync(p.SetCmd, (string)value!, p.Width, p.Index).ConfigureAwait(false);
                    else if (p.Width > 1)
                        await scale.SetIntParamAsync(p.SetCmd, (long)value!, p.Width).ConfigureAwait(false);
                    else
                    {
                        var v = (long)value!;
                        if (p.Inverted)
                            v = v == 0 ? 1 : 0;
                        await scale.SetByteParamAsync(p.SetCmd, (int)v).ConfigureAwait(false);
                    }
                    p.Loaded = value;
                    written++;
                }
                catch (ShtrikhScaleException ex) when (ex.IsPasswordError || ex.ErrorCode == 0)
                {
                    throw;
                }
                catch (ShtrikhScaleException ex)
                {
                    errors.Add($"{p.Title}: {ex.ErrorCode} {P.DescribeError(ex.ErrorCode)}");
                }
            }

            return (errors.Count == 0 ? "" : string.Join("; ", errors) + ". ")
                + L($"Записано параметров: {written}.", $"Жазылган параметрлер: {written}.", $"Parameters written: {written}.", $"Yazılan parametre: {written}.", $"Yozilgan parametrlar: {written}.");
        }).ConfigureAwait(true);
    }

    private static object? Normalize(object? v) => v switch
    {
        string s => s.TrimEnd(),
        null => null,
        _ => Convert.ToInt64(v, CultureInfo.InvariantCulture),
    };

    // =====================================================================================
    // Вкладка «Клавиатура»
    // =====================================================================================

    private enum ValueKind { None, Price, Plu, Code, Number }

    /// <summary>Функции клавиш быстрого доступа — Приложение 7 протокола (коды функций) в
    /// порядке, удобном кассиру: цена и товар — первыми. Max — диапазон значения.</summary>
    private static (byte Code, string Name, ValueKind Kind, long Max)[] HotkeyFunctions() => new[]
    {
        (P.HotkeyPrice, L("Установить цену", "Бааны коюу", "Set price", "Fiyat ayarla", "Narx o‘rnatish"), ValueKind.Price, 999999L),
        (P.HotkeyPluNumber, L("Выбрать товар (по номеру ПЛУ)", "Товарды тандоо (ПЛУ номуру боюнча)", "Select item (by PLU number)", "Ürün seç (PLU numarası)", "Tovar tanlash (PLU raqami bo‘yicha)"), ValueKind.Plu, 65535L),
        (P.HotkeyProductCode, L("Выбрать товар по коду", "Товарды код боюнча тандоо", "Select item by code", "Ürünü koda göre seç", "Tovarni kod bo‘yicha tanlash"), ValueKind.Code, 999999L),
        ((byte)0x0E, L("Ввод номера ПЛУ для выбора товара", "Товар тандоо үчүн ПЛУ номурун киргизүү", "Enter PLU number to select item", "Ürün seçmek için PLU no. girişi", "Tovar tanlash uchun PLU raqamini kiritish"), ValueKind.None, 0L),
        ((byte)0x0F, L("Ввод кода товара для выбора товара", "Товар тандоо үчүн товар кодун киргизүү", "Enter item code to select item", "Ürün seçmek için ürün kodu girişi", "Tovar tanlash uchun tovar kodini kiritish"), ValueKind.None, 0L),
        ((byte)0x09, L("Ввод фиксированной массы", "Туруктуу массаны киргизүү", "Enter fixed weight", "Sabit ağırlık girişi", "Qat’iy massani kiritish"), ValueKind.None, 0L),
        ((byte)0x0A, L("Ввод срока годности", "Жарактуулук мөөнөтүн киргизүү", "Enter shelf life", "Raf ömrü girişi", "Yaroqlilik muddatini kiritish"), ValueKind.None, 0L),
        ((byte)0x0B, L("Ввод даты реализации", "Сатуу датасын киргизүү", "Enter sell-by date", "Satış tarihi girişi", "Sotish sanasini kiritish"), ValueKind.None, 0L),
        ((byte)0x0D, L("Ввод даты изготовления", "Даярдалган датаны киргизүү", "Enter production date", "Üretim tarihi girişi", "Ishlab chiqarilgan sanani kiritish"), ValueKind.None, 0L),
        ((byte)0x0C, L("Ввод произвольного штрих-кода", "Эркин штрих-кодду киргизүү", "Enter custom barcode", "Serbest barkod girişi", "Ixtiyoriy shtrix-kodni kiritish"), ValueKind.None, 0L),
        ((byte)0x04, L("Ввод времени", "Убакытты киргизүү", "Enter time", "Saat girişi", "Vaqtni kiritish"), ValueKind.None, 0L),
        ((byte)0x05, L("Ввод даты", "Датаны киргизүү", "Enter date", "Tarih girişi", "Sanani kiritish"), ValueKind.None, 0L),
        ((byte)0x06, L("Ввод порога автопечати", "Автобасуу чегин киргизүү", "Enter auto-print threshold", "Otomatik baskı eşiği girişi", "Avtochop chegarasini kiritish"), ValueKind.None, 0L),
        ((byte)0x07, L("Ввод курса валюты", "Валюта курсун киргизүү", "Enter exchange rate", "Döviz kuru girişi", "Valyuta kursini kiritish"), ValueKind.None, 0L),
        ((byte)0x03, L("Вкл/выкл валютный эквивалент (0/1)", "Валюталык эквивалентти күйгүзүү/өчүрүү (0/1)", "Currency equivalent on/off (0/1)", "Döviz karşılığı aç/kapat (0/1)", "Valyuta ekvivalentini yoqish/o‘chirish (0/1)"), ValueKind.Number, 1L),
        ((byte)0x29, L("Фасовка (0 — выкл, 1 — вкл)", "Таңгактоо (0 — өчүк, 1 — күйүк)", "Packing mode (0 off, 1 on)", "Paketleme (0 kapalı, 1 açık)", "Qadoqlash (0 — o‘chiq, 1 — yoqiq)"), ValueKind.Number, 1L),
        ((byte)0x27, L("Режим печати (0 нет, 1 да, 2 авто)", "Басуу режими (0 жок, 1 ооба, 2 авто)", "Print mode (0 no, 1 yes, 2 auto)", "Baskı modu (0 yok, 1 evet, 2 oto)", "Chop rejimi (0 yo‘q, 1 ha, 2 avto)"), ValueKind.Number, 2L),
        ((byte)0x71, L("Формат этикетки (0–14)", "Этикетка форматы (0–14)", "Label format (0–14)", "Etiket formatı (0–14)", "Yorliq formati (0–14)"), ValueKind.Number, 14L),
        ((byte)0x47, L("Смещение печати (0–15)", "Басуунун жылышы (0–15)", "Print offset (0–15)", "Baskı kayması (0–15)", "Chop siljishi (0–15)"), ValueKind.Number, 15L),
        ((byte)0x49, L("Контраст (0–15)", "Контраст (0–15)", "Contrast (0–15)", "Kontrast (0–15)", "Kontrast (0–15)"), ValueKind.Number, 15L),
        ((byte)0x79, L("Печать по П+ (0/1)", "П+ боюнча басуу (0/1)", "Print on P+ (0/1)", "P+ ile baskı (0/1)", "P+ bo‘yicha chop (0/1)"), ValueKind.Number, 1L),
        ((byte)0x8B, L("Печать по выбору ПЛУ (0/1)", "ПЛУ тандаганда басуу (0/1)", "Print on PLU selection (0/1)", "PLU seçiminde baskı (0/1)", "PLU tanlanganda chop (0/1)"), ValueKind.Number, 1L),
        ((byte)0x7B, L("Тип печати (0 этикетки, 1 лента)", "Басуу түрү (0 этикетка, 1 лента)", "Print type (0 labels, 1 tape)", "Baskı türü (0 etiket, 1 rulo)", "Chop turi (0 yorliq, 1 lenta)"), ValueKind.Number, 1L),
        ((byte)0x7D, L("Датчик этикетки (0 нет, 1 да, 2 выборочно)", "Этикетка датчиги (0 жок, 1 ооба, 2 тандап)", "Label sensor (0 off, 1 on, 2 selective)", "Etiket sensörü (0 kapalı, 1 açık, 2 seçmeli)", "Yorliq datchigi (0 yo‘q, 1 ha, 2 tanlab)"), ValueKind.Number, 2L),
        ((byte)0x85, L("Сброс ПЛУ после печати (0/1)", "Басуудан кийин ПЛУну түшүрүү (0/1)", "Reset PLU after print (0/1)", "Baskı sonrası PLU sıfırla (0/1)", "Chopdan keyin PLUni tashlash (0/1)"), ValueKind.Number, 1L),
        (P.HotkeyNone, L("Не выполнять никаких функций", "Эч кандай функция аткарбоо", "No function", "İşlev yok", "Hech qanday funksiya yo‘q"), ValueKind.None, 0L),
    };

    private (byte Code, string Name, ValueKind Kind, long Max)[] _hotkeyFunctions = Array.Empty<(byte, string, ValueKind, long)>();

    private void BuildKeyboardTab()
    {
        _hotkeyFunctions = HotkeyFunctions();

        // --- Клавиши быстрого доступа
        var card = Card(
            L("Клавиши быстрого доступа", "Тез жетүү баскычтары", "Quick-access keys", "Hızlı erişim tuşları", "Tezkor tugmalar"),
            L("Кнопки на весах, на которые можно «повесить» цену, товар или функцию. Укажите номер клавиши (на ШТРИХ-ПРИНТ: 1–45, с «РЕГ» — 46–90), выберите, что она делает, и нажмите «Записать клавишу».",
              "Таразадагы баскычтар — аларга баа, товар же функция байланышат. Баскычтын номурун көрсөтүп (ШТРИХ-ПРИНТте 1–45, «РЕГ» менен 46–90), эмне кыларын тандап, «Баскычты жазуу» басыңыз.",
              "Buttons on the scale that can be bound to a price, an item or a function. Enter the key number (ShTRIH-PRINT: 1–45, with “REG” 46–90), choose what it does and press “Write key”.",
              "Tartıdaki tuşlara fiyat, ürün veya işlev atanabilir. Tuş numarasını girin (ŞTRİH-PRİNT: 1–45, “REG” ile 46–90), ne yapacağını seçin ve “Tuşu yaz”a basın.",
              "Tarozidagi tugmalarga narx, tovar yoki funksiya biriktiriladi. Tugma raqamini kiriting (ShTRIX-PRINT: 1–45, «REG» bilan 46–90), vazifasini tanlang va «Tugmani yozish»ni bosing."),
            out var body);

        _hotkeyNumber = new NumericUpDown { Minimum = 1, Maximum = 255, Value = 1, Increment = 1, FormatString = "0", MinWidth = 140 };
        body.Children.Add(Row(L("Номер клавиши", "Баскычтын номуру", "Key number", "Tuş numarası", "Tugma raqami"), _hotkeyNumber));

        _hotkeyFunction = new ComboBox { MinWidth = 360 };
        foreach (var f in _hotkeyFunctions)
            _hotkeyFunction.Items.Add(f.Name);
        _hotkeyFunction.SelectedIndex = 0;
        _hotkeyFunction.SelectionChanged += (_, _) => UpdateHotkeyValueLabel();
        body.Children.Add(Row(L("Что делает клавиша", "Баскыч эмне кылат", "What the key does", "Tuşun işlevi", "Tugma vazifasi"), _hotkeyFunction));

        _hotkeyValueLabel = new TextBlock { Classes = { "label" }, Margin = new Thickness(0, 0, 12, 0) };
        _hotkeyValue = new TextBox { Text = "0", MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var valueRow = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        valueRow.Children.Add(_hotkeyValueLabel);
        Grid.SetColumn(_hotkeyValue, 1);
        valueRow.Children.Add(_hotkeyValue);
        body.Children.Add(valueRow);
        UpdateHotkeyValueLabel();

        body.Children.Add(ButtonRow(
            MakeButton(L("Прочитать клавишу", "Баскычты окуу", "Read key", "Tuşu oku", "Tugmani o‘qish"), false, async (_, _) => await ReadHotkeyAsync().ConfigureAwait(true)),
            MakeButton(L("Записать клавишу", "Баскычты жазуу", "Write key", "Tuşu yaz", "Tugmani yozish"), true, async (_, _) => await WriteHotkeyAsync().ConfigureAwait(true)),
            MakeButton(L("Показать все клавиши", "Бардык баскычтарды көрсөтүү", "Show all keys", "Tüm tuşları göster", "Barcha tugmalarni ko‘rsatish"), false, async (_, _) => await ReadAllHotkeysAsync().ConfigureAwait(true)),
            MakeButton(L("Сбросить поля", "Талааларды тазалоо", "Reset fields", "Alanları sıfırla", "Maydonlarni tozalash"), false, (_, _) =>
            {
                // Как «По умолчанию» в тестовой программе: первоначальные значения полей.
                _hotkeyNumber.Value = 1;
                _hotkeyFunction.SelectedIndex = 0;
                _hotkeyValue.Text = "0";
            })));

        _hotkeyInfo = new TextBlock { Classes = { "hint" } };
        _hotkeyList = new TextBlock { Classes = { "hint" }, FontFamily = new FontFamily("Consolas, Segoe UI") };
        body.Children.Add(_hotkeyInfo);
        body.Children.Add(_hotkeyList);
        KeyboardPanel.Children.Add(card);

        // --- Функциональные клавиши
        var fkCard = Card(
            L("Функциональные клавиши", "Функционалдык баскычтар", "Function keys", "İşlev tuşları", "Funksional tugmalar"),
            L("Снимите галочку, чтобы клавиша на весах перестала работать (весы ответят двойным писком).",
              "Баскыч иштебей калышы үчүн белгини алып салыңыз (тараза эки жолу чыйылдайт).",
              "Untick to disable a key on the scale (the scale will double-beep).",
              "Tartıdaki tuşu kapatmak için işareti kaldırın (tartı çift bip verir).",
              "Tugmani o‘chirish uchun belgini olib tashlang (tarozi ikki marta signal beradi)."),
            out var fkBody);
        for (var i = 0; i < _functionKeys.Length; i++)
        {
            _functionKeys[i] = new CheckBox { IsChecked = true };
            fkBody.Children.Add(_functionKeys[i]);
        }
        _functionKeysHint = new TextBlock { Classes = { "hint" } };
        fkBody.Children.Add(_functionKeysHint);
        UpdateFunctionKeyLabels();
        fkBody.Children.Add(ButtonRow(
            MakeButton(L("Прочитать с весов", "Таразадан окуу", "Read from scale", "Tartıdan oku", "Tarozidan o‘qish"), false, async (_, _) => await ReadFunctionKeysAsync().ConfigureAwait(true)),
            MakeButton(L("Записать в весы", "Таразага жазуу", "Write to scale", "Tartıya yaz", "Taroziga yozish"), true, async (_, _) => await WriteFunctionKeysAsync().ConfigureAwait(true))));
        KeyboardPanel.Children.Add(fkCard);

        // --- Блокировка клавиатуры
        var lockCard = Card(
            L("Блокировка клавиатуры", "Клавиатураны бөгөттөө", "Keyboard lock", "Klavye kilidi", "Klaviaturani bloklash"),
            L("Временно запрещает нажатия на весах — например, пока загружаются товары.",
              "Таразадагы басууларды убактылуу тыят — мисалы, товарлар жүктөлүп жатканда.",
              "Temporarily blocks key presses on the scale — for example while goods are being loaded.",
              "Tartıdaki tuş basımlarını geçici olarak engeller — örneğin ürünler yüklenirken.",
              "Tarozidagi bosishlarni vaqtincha taqiqlaydi — masalan, tovarlar yuklanayotganda."),
            out var lockBody);
        _lockState = new TextBlock { Classes = { "label" } };
        lockBody.Children.Add(_lockState);
        lockBody.Children.Add(ButtonRow(
            MakeButton(L("Заблокировать", "Бөгөттөө", "Lock", "Kilitle", "Bloklash"), true, async (_, _) => await SetLockAsync(true).ConfigureAwait(true)),
            MakeButton(L("Разблокировать", "Бөгөттөн чыгаруу", "Unlock", "Kilidi aç", "Blokdan chiqarish"), false, async (_, _) => await SetLockAsync(false).ConfigureAwait(true))));
        KeyboardPanel.Children.Add(lockCard);

        // --- Эмуляция клавиатуры — честно объясняем, почему кнопки нет.
        var emuCard = Card(
            L("Эмуляция нажатий", "Басууларды эмуляциялоо", "Key emulation", "Tuş emülasyonu", "Tugma emulyatsiyasi"),
            L("Нажимать клавиши весов с компьютера (как «Эмуляция клавиатуры» в программе Штрих-М) можно только со специальным паролем, который Штрих-М выдаёт под заводской номер весов. Поэтому в кассе этой кнопки нет.",
              "Компьютерден тараза баскычтарын басуу (Штрих-Мдеги «Клавиатура эмуляциясы») Штрих-М тараза номуруна берген атайын сырсөз менен гана болот. Ошондуктан кассада бул баскыч жок.",
              "Pressing scale keys from the PC (“keyboard emulation” in the Shtrih-M program) needs a special password issued by Shtrih-M per scale serial number, so the till has no such button.",
              "Tartı tuşlarına bilgisayardan basmak (Shtrih-M programındaki “klavye emülasyonu”) yalnızca Shtrih-M'in seri numarasına verdiği özel şifreyle mümkündür; bu yüzden kasada bu düğme yok.",
              "Kompyuterdan tarozi tugmalarini bosish (Shtrih-M dasturidagi «klaviatura emulyatsiyasi») faqat Shtrih-M zavod raqamiga beradigan maxsus parol bilan mumkin, shuning uchun kassada bu tugma yo‘q."),
            out _);
        KeyboardPanel.Children.Add(emuCard);
    }

    private (byte Code, string Name, ValueKind Kind, long Max) SelectedFunction =>
        _hotkeyFunctions[Math.Max(0, _hotkeyFunction.SelectedIndex)];

    private void UpdateHotkeyValueLabel()
    {
        var f = SelectedFunction;
        _hotkeyValue.IsVisible = f.Kind != ValueKind.None;
        _hotkeyValueLabel.IsVisible = f.Kind != ValueKind.None;
        _hotkeyValueLabel.Text = f.Kind switch
        {
            ValueKind.Price => L("Цена, сом", "Баасы, сом", "Price, som", "Fiyat, som", "Narx, so‘m"),
            ValueKind.Plu => L("Номер ПЛУ", "ПЛУ номуру", "PLU number", "PLU numarası", "PLU raqami"),
            ValueKind.Code => L("Код товара", "Товардын коду", "Item code", "Ürün kodu", "Tovar kodi"),
            _ => L("Значение", "Мааниси", "Value", "Değer", "Qiymat"),
        };
    }

    private int DecimalDigits => _status?.DecimalPointDigits ?? 2;

    private string DescribeHotkey(ShtrikhHotkey key)
    {
        var index = Array.FindIndex(_hotkeyFunctions, f => f.Code == key.FunctionCode);
        var name = index >= 0 ? _hotkeyFunctions[index].Name : L("функция", "функция", "function", "işlev", "funksiya") + $" {key.FunctionCode:X2}h";
        var kind = index >= 0 ? _hotkeyFunctions[index].Kind : ValueKind.Number;
        var value = kind switch
        {
            ValueKind.Price => FormatPrice(key.Value) + L(" сом", " сом", " som", " som", " so‘m"),
            ValueKind.None => "",
            _ => key.Value.ToString(CultureInfo.InvariantCulture),
        };
        return value.Length == 0 ? name : $"{name}: {value}";
    }

    private string FormatPrice(long mde) =>
        DecimalDigits >= 2 ? (mde / 100m).ToString("0.00", CultureInfo.CurrentCulture) : mde.ToString(CultureInfo.CurrentCulture);

    private bool TryGetHotkeyValue(out long value, out string error)
    {
        var f = SelectedFunction;
        var text = (_hotkeyValue.Text ?? "").Trim().Replace(',', '.');
        value = 0;
        error = "";
        if (f.Kind == ValueKind.None)
            return true;
        if (f.Kind == ValueKind.Price)
        {
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price < 0)
            {
                error = L("Введите цену числом, например 125,50.", "Бааны сан менен жазыңыз, мисалы 125,50.", "Enter the price as a number, e.g. 125.50.", "Fiyatı sayı olarak girin, ör. 125,50.", "Narxni son bilan kiriting, masalan 125,50.");
                return false;
            }
            value = P.PriceToMde(price, DecimalDigits);
            return true;
        }
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0 || value > f.Max
            || (f.Kind is ValueKind.Plu or ValueKind.Code && value < 1))
        {
            error = L($"Значение должно быть от {(f.Kind is ValueKind.Plu or ValueKind.Code ? 1 : 0)} до {f.Max}.",
                $"Маани {(f.Kind is ValueKind.Plu or ValueKind.Code ? 1 : 0)} менен {f.Max} ортосунда болушу керек.",
                $"The value must be from {(f.Kind is ValueKind.Plu or ValueKind.Code ? 1 : 0)} to {f.Max}.",
                $"Değer {(f.Kind is ValueKind.Plu or ValueKind.Code ? 1 : 0)} ile {f.Max} arasında olmalı.",
                $"Qiymat {(f.Kind is ValueKind.Plu or ValueKind.Code ? 1 : 0)} dan {f.Max} gacha bo‘lishi kerak.");
            return false;
        }
        return true;
    }

    private async Task ReadHotkeyAsync()
    {
        var key = (int)(_hotkeyNumber.Value ?? 1);
        await RunAsync(async scale =>
        {
            var hk = await scale.GetHotkeyAsync(key).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var index = Array.FindIndex(_hotkeyFunctions, f => f.Code == hk.FunctionCode);
                if (index >= 0)
                    _hotkeyFunction.SelectedIndex = index;
                _hotkeyValue.Text = index >= 0 && _hotkeyFunctions[index].Kind == ValueKind.Price
                    ? FormatPrice(hk.Value)
                    : hk.Value.ToString(CultureInfo.InvariantCulture);
                _hotkeyInfo.Text = L("Клавиша ", "Баскыч ", "Key ", "Tuş ", "Tugma ") + $"{key}: " + DescribeHotkey(hk);
            });
            return L("Клавиша прочитана.", "Баскыч окулду.", "Key read.", "Tuş okundu.", "Tugma o‘qildi.");
        }).ConfigureAwait(true);
    }

    private async Task WriteHotkeyAsync()
    {
        if (!TryGetHotkeyValue(out var value, out var error))
        {
            ShowResult(error, true);
            return;
        }
        var key = (int)(_hotkeyNumber.Value ?? 1);
        var f = SelectedFunction;
        await RunAsync(async scale =>
        {
            await scale.SetHotkeyAsync(key, f.Code, value).ConfigureAwait(false);
            var check = await scale.GetHotkeyAsync(key).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                _hotkeyInfo.Text = L("Клавиша ", "Баскыч ", "Key ", "Tuş ", "Tugma ") + $"{key}: " + DescribeHotkey(check));
            return L("Клавиша записана.", "Баскыч жазылды.", "Key written.", "Tuş yazıldı.", "Tugma yozildi.");
        }).ConfigureAwait(true);
    }

    private async Task ReadAllHotkeysAsync()
    {
        await RunAsync(async scale =>
        {
            if (_status is null)
                await LoadStatusAsync(scale).ConfigureAwait(false);
            var count = _status?.HotkeyCount is > 0 and var c ? c : 90;
            var sb = new StringBuilder();
            var busy = 0;
            for (var key = 1; key <= count; key++)
            {
                var hk = await scale.GetHotkeyAsync(key).ConfigureAwait(false);
                if (hk.FunctionCode == P.HotkeyNone)
                    continue;
                busy++;
                sb.Append(key.ToString(CultureInfo.InvariantCulture).PadLeft(3)).Append(". ").AppendLine(DescribeHotkey(hk));
            }
            var text = busy == 0
                ? L("Ни одна клавиша не запрограммирована.", "Бир да баскыч программаланган эмес.", "No key is programmed.", "Hiçbir tuş programlanmamış.", "Birorta tugma dasturlanmagan.")
                : sb.ToString().TrimEnd();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _hotkeyList.Text = text);
            return L($"Прочитано клавиш: {count}, занято: {busy}.", $"Окулган баскычтар: {count}, бош эмес: {busy}.", $"Keys read: {count}, programmed: {busy}.", $"Okunan tuş: {count}, programlı: {busy}.", $"O‘qilgan tugmalar: {count}, band: {busy}.");
        }).ConfigureAwait(true);
    }

    private bool IsVariantC => _status?.HardwareVariant == 1;

    /// <summary>Биты B2h/B3h. У ШТРИХ-ПРИНТ С раскладка другая: бит 0 — клавиша &gt;T&lt;,
    /// бит 1 — клавиша &gt;0&lt; (для неё 0 — ВКЛЮЧЕНА, 1 — выключена).</summary>
    private void UpdateFunctionKeyLabels()
    {
        if (IsVariantC)
        {
            _functionKeys[0].Content = L("клавиша >T< (тара)", ">T< баскычы (тара)", ">T< key (tare)", ">T< tuşu (dara)", ">T< tugmasi (tara)");
            _functionKeys[1].Content = L("клавиша >0< (ноль)", ">0< баскычы (нөл)", ">0< key (zero)", ">0< tuşu (sıfır)", ">0< tugmasi (nol)");
            for (var i = 2; i < _functionKeys.Length; i++)
                _functionKeys[i].IsVisible = false;
            _functionKeysHint.Text = L("Весы ШТРИХ-ПРИНТ С: отключаются только клавиши тары и нуля.", "ШТРИХ-ПРИНТ С таразасы: тара жана нөл баскычтары гана өчүрүлөт.", "ShTRIH-PRINT S scale: only tare and zero keys can be disabled.", "ŞTRİH-PRİNT S: yalnızca dara ve sıfır tuşları kapatılabilir.", "ShTRIX-PRINT S: faqat tara va nol tugmalari o‘chiriladi.");
            return;
        }
        _functionKeys[0].Content = L("клавиша Дата/Время", "Дата/Убакыт баскычы", "Date/Time key", "Tarih/Saat tuşu", "Sana/Vaqt tugmasi");
        _functionKeys[1].Content = L("клавиша Курс/Экв", "Курс/Экв баскычы", "Rate/Equiv key", "Kur/Karşılık tuşu", "Kurs/Ekv tugmasi");
        _functionKeys[2].Content = L("клавиша Авто/Фасовка", "Авто/Таңгактоо баскычы", "Auto/Packing key", "Oto/Paketleme tuşu", "Avto/Qadoqlash tugmasi");
        _functionKeys[3].Content = L("клавиша * (запись)", "* баскычы (жазуу)", "* key (record)", "* tuşu (kayıt)", "* tugmasi (yozish)");
        _functionKeys[4].Content = L("клавиши +, −, P и =", "+, −, P жана = баскычтары", "+, −, P and = keys", "+, −, P ve = tuşları", "+, −, P va = tugmalari");
        foreach (var box in _functionKeys)
            box.IsVisible = true;
        _functionKeysHint.Text = L("На весах версии 4.5 клавиш Дата/Время, Курс/Экв и Авто/Фасовка нет — их галочки ни на что не влияют.",
            "4.5 версиясындагы таразада Дата/Убакыт, Курс/Экв жана Авто/Таңгактоо баскычтары жок — алардын белгилери эч нерсеге таасир этпейт.",
            "Scales with firmware 4.5 have no Date/Time, Rate/Equiv and Auto/Packing keys — those ticks have no effect.",
            "4.5 sürümlü tartılarda Tarih/Saat, Kur/Karşılık ve Oto/Paketleme tuşları yoktur — bu işaretler etkisizdir.",
            "4.5 versiyali tarozilarda Sana/Vaqt, Kurs/Ekv va Avto/Qadoqlash tugmalari yo‘q — ularning belgilari ta’sir qilmaydi.");
    }

    private async Task ReadFunctionKeysAsync()
    {
        await RunAsync(async scale =>
        {
            if (_status is null)
                await LoadStatusAsync(scale).ConfigureAwait(false);
            var mask = await scale.GetByteParamAsync(P.CmdGetFunctionKeys).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                UpdateFunctionKeyLabels();
                for (var i = 0; i < _functionKeys.Length; i++)
                    _functionKeys[i].IsChecked = (mask & (1 << i)) != 0;
                if (IsVariantC)
                    _functionKeys[1].IsChecked = (mask & 0x02) == 0; // >0<: 0 — включена
            });
            return L("Настройка функциональных клавиш прочитана.", "Функционалдык баскычтардын жөндөөсү окулду.", "Function key settings read.", "İşlev tuşu ayarı okundu.", "Funksional tugmalar sozlamasi o‘qildi.");
        }).ConfigureAwait(true);
    }

    private async Task WriteFunctionKeysAsync()
    {
        var mask = 0;
        for (var i = 0; i < _functionKeys.Length; i++)
            if (_functionKeys[i].IsVisible && _functionKeys[i].IsChecked == true)
                mask |= 1 << i;
        if (IsVariantC)
            mask = (_functionKeys[0].IsChecked == true ? 0x01 : 0) | (_functionKeys[1].IsChecked == true ? 0 : 0x02);

        await RunAsync(async scale =>
        {
            await scale.SetByteParamAsync(P.CmdSetFunctionKeys, mask).ConfigureAwait(false);
            return L("Настройка функциональных клавиш записана.", "Функционалдык баскычтардын жөндөөсү жазылды.", "Function key settings written.", "İşlev tuşu ayarı yazıldı.", "Funksional tugmalar sozlamasi yozildi.");
        }).ConfigureAwait(true);
    }

    private async Task SetLockAsync(bool locked)
    {
        await RunAsync(async scale =>
        {
            await scale.SetKeyboardLockedAsync(locked).ConfigureAwait(false);
            await LoadStatusAsync(scale).ConfigureAwait(false);
            return locked
                ? L("Клавиатура весов заблокирована.", "Тараза клавиатурасы бөгөттөлдү.", "Scale keyboard locked.", "Tartı klavyesi kilitlendi.", "Tarozi klaviaturasi bloklandi.")
                : L("Клавиатура весов разблокирована.", "Тараза клавиатурасы бөгөттөн чыгарылды.", "Scale keyboard unlocked.", "Tartı klavyesinin kilidi açıldı.", "Tarozi klaviaturasi blokdan chiqarildi.");
        }).ConfigureAwait(true);
    }

    // =====================================================================================
    // Вкладка «Состояние»
    // =====================================================================================

    private void BuildStatusTab()
    {
        var card = Card(L("Состояние весов", "Тараза абалы", "Scale status", "Tartı durumu", "Tarozi holati"),
            L("То же, что «Запрос состояния» в программе Штрих-М. Команда без пароля.", "Штрих-М программасындагы «Абалды суроо» сыяктуу. Сырсөзсүз команда.", "Same as “Status request” in the Shtrih-M program. No password needed.", "Shtrih-M programındaki “Durum sorgusu” ile aynı. Şifre gerekmez.", "Shtrih-M dasturidagi «Holat so‘rovi» bilan bir xil. Parol kerak emas."),
            out var body);
        _statusText = new TextBlock { Classes = { "label" }, FontFamily = new FontFamily("Consolas, Segoe UI"), FontSize = 13 };
        body.Children.Add(_statusText);
        body.Children.Add(ButtonRow(
            MakeButton(L("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"), true, async (_, _) => await RunAsync(async scale =>
            {
                await LoadStatusAsync(scale).ConfigureAwait(false);
                return L("Состояние получено.", "Абалы алынды.", "Status received.", "Durum alındı.", "Holat olindi.");
            }).ConfigureAwait(true)),
            MakeButton(L("Узнать вес", "Салмакты билүү", "Get weight", "Ağırlığı al", "Vaznni bilish"), false, async (_, _) => await RunAsync(async scale =>
            {
                var grams = await scale.GetWeightGramsAsync().ConfigureAwait(false);
                return L("Вес: ", "Салмак: ", "Weight: ", "Ağırlık: ", "Vazn: ") + (grams / 1000m).ToString("0.000", CultureInfo.CurrentCulture) + L(" кг", " кг", " kg", " kg", " kg");
            }).ConfigureAwait(true)),
            MakeButton(L("Состояние принтера", "Принтердин абалы", "Printer status", "Yazıcı durumu", "Printer holati"), false, async (_, _) => await RunAsync(async scale =>
            {
                var state = await scale.GetPrinterStateAsync().ConfigureAwait(false);
                return DescribePrinter(state);
            }).ConfigureAwait(true))));
        StatusPanel.Children.Add(card);
    }

    private static string YesNo(bool v) => v ? L("да", "ооба", "yes", "evet", "ha") : L("нет", "жок", "no", "hayır", "yo‘q");

    private static string DescribePrinter(byte state) =>
        L("Бумага: ", "Кагаз: ", "Paper: ", "Kağıt: ", "Qog‘oz: ") + YesNo((state & 0x01) != 0)
        + L("; этикетка не снята: ", "; этикетка алынган жок: ", "; label not taken: ", "; etiket alınmadı: ", "; yorliq olinmagan: ") + YesNo((state & 0x02) != 0)
        + L("; головка открыта: ", "; башчасы ачык: ", "; head open: ", "; kafa açık: ", "; kallak ochiq: ") + YesNo((state & 0x08) != 0);

    private void ApplyStatus(ShtrikhScaleStatus s)
    {
        var sb = new StringBuilder();
        void Line(string name, string value) => sb.Append(name.PadRight(26)).AppendLine(value);
        Line(L("Исполнение", "Аткарылышы", "Model", "Model", "Model"), s.HardwareVariantName);
        Line(L("Версия ПО", "ПО версиясы", "Firmware", "Yazılım", "Dastur versiyasi"), s.SoftwareVersion);
        Line(L("Номер весов", "Тараза номуру", "Scale number", "Tartı no.", "Tarozi raqami"), s.ScaleNumber.ToString(CultureInfo.InvariantCulture));
        Line(L("Товаров в таблице (макс.)", "Таблицадагы товарлар (макс.)", "Goods table size", "Ürün tablosu", "Tovarlar jadvali"), s.ProductTableSize.ToString(CultureInfo.InvariantCulture));
        Line(L("Сообщений / строк", "Билдирүүлөр / саптар", "Messages / lines", "Mesaj / satır", "Xabarlar / qatorlar"), $"{s.MessageTableSize} / {s.MessageLineCount}");
        Line(L("Клавиш быстрого доступа", "Тез жетүү баскычтары", "Quick-access keys", "Hızlı tuş sayısı", "Tezkor tugmalar"), s.HotkeyCount.ToString(CultureInfo.InvariantCulture));
        Line(L("Наибольший предел", "Эң жогорку чек", "Max load", "Maks. yük", "Eng katta chegara"), $"{s.MaxWeightKg} " + L("кг", "кг", "kg", "kg", "kg"));
        Line(L("Часы весов", "Тараза сааты", "Scale clock", "Tartı saati", "Tarozi soati"), s.ScaleClock?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? L("сбой часов", "саат бузулган", "clock error", "saat hatası", "soat nosoz"));
        Line(L("Цены на весах", "Таразадагы баалар", "Prices on scale", "Tartıdaki fiyatlar", "Tarozidagi narxlar"), s.DecimalPointDigits >= 2 ? L("с тыйынами (2 знака)", "тыйындар менен (2 белги)", "with tyiyn (2 decimals)", "kuruşlu (2 hane)", "tiyin bilan (2 belgi)") : L("целые сомы", "бүтүн сом", "whole som", "tam som", "butun so‘m"));
        Line(L("Вес / тара", "Салмак / тара", "Weight / tare", "Ağırlık / dara", "Vazn / tara"), $"{s.WeightGrams} / {s.TareGrams} " + L("г", "г", "g", "g", "g"));
        Line(L("Вес зафиксирован", "Салмак бекитилди", "Weight stable", "Ağırlık sabit", "Vazn barqaror"), YesNo(s.WeightFixed));
        Line(L("Перегрузка", "Ашыкча жүк", "Overload", "Aşırı yük", "Ortiqcha yuk"), YesNo(s.Overloaded));
        Line(L("Выбран ПЛУ", "Тандалган ПЛУ", "Selected PLU", "Seçili PLU", "Tanlangan PLU"), s.SelectedPlu == 0 ? "—" : s.SelectedPlu.ToString(CultureInfo.InvariantCulture));
        Line(L("Принтер", "Принтер", "Printer", "Yazıcı", "Printer"), DescribePrinter(s.PrinterState));
        Line(L("Клавиатура заблокирована", "Клавиатура бөгөттөлгөн", "Keyboard locked", "Klavye kilitli", "Klaviatura bloklangan"), YesNo(s.KeyboardLocked));
        Line(L("Режим весов", "Тараза режими", "Scale mode", "Tartı modu", "Tarozi rejimi"), s.DescribeBusyReason());
        _statusText.Text = sb.ToString().TrimEnd();

        _lockState.Text = s.KeyboardLocked
            ? L("Сейчас клавиатура весов ЗАБЛОКИРОВАНА.", "Азыр тараза клавиатурасы БӨГӨТТӨЛГӨН.", "The scale keyboard is LOCKED now.", "Tartı klavyesi şu an KİLİTLİ.", "Hozir tarozi klaviaturasi BLOKLANGAN.")
            : L("Сейчас клавиатура весов работает.", "Азыр тараза клавиатурасы иштейт.", "The scale keyboard is working now.", "Tartı klavyesi şu an çalışıyor.", "Hozir tarozi klaviaturasi ishlayapti.");

        if (s.HotkeyCount > 0)
            _hotkeyNumber.Maximum = s.HotkeyCount;
        UpdateFunctionKeyLabels();
        if (_clockText is not null)
            _clockText.Text = L("Часы весов: ", "Тараза сааты: ", "Scale clock: ", "Tartı saati: ", "Tarozi soati: ")
                + (s.ScaleClock?.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture) ?? "—")
                + L(" · компьютер: ", " · компьютер: ", " · PC: ", " · bilgisayar: ", " · kompyuter: ")
                + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // =====================================================================================
    // Вкладка «Система»
    // =====================================================================================

    private void BuildSystemTab()
    {
        var clock = Card(L("Дата и время", "Дата жана убакыт", "Date and time", "Tarih ve saat", "Sana va vaqt"),
            L("Дата и время печатаются на этикетке. Кнопка ставит на весах время этого компьютера.", "Дата жана убакыт этикеткага басылат. Баскыч таразага ушул компьютердин убактысын коёт.", "Date and time are printed on the label. The button sets this computer's time on the scale.", "Tarih ve saat etikete basılır. Düğme bu bilgisayarın saatini tartıya yazar.", "Sana va vaqt yorliqqa chop etiladi. Tugma tarozga shu kompyuter vaqtini o‘rnatadi."),
            out var clockBody);
        _clockText = new TextBlock { Classes = { "label" } };
        clockBody.Children.Add(_clockText);
        clockBody.Children.Add(ButtonRow(MakeButton(L("Синхронизировать с компьютером", "Компьютер менен шайкештештирүү", "Sync with this computer", "Bilgisayarla eşitle", "Kompyuter bilan moslashtirish"), true,
            async (_, _) => await RunAsync(async scale =>
            {
                await scale.SetClockAsync(DateTime.Now).ConfigureAwait(false);
                await LoadStatusAsync(scale).ConfigureAwait(false);
                return L("Часы весов установлены.", "Тараза сааты коюлду.", "Scale clock set.", "Tartı saati ayarlandı.", "Tarozi soati o‘rnatildi.");
            }).ConfigureAwait(true))));
        SystemPanel.Children.Add(clock);

        var card = Card(L("Системные настройки", "Системалык жөндөөлөр", "System settings", "Sistem ayarları", "Tizim sozlamalari"),
            L("Текущие значения весы отдают в «Запросе состояния», поэтому чтение здесь — без лишних попыток пароля.", "Учурдагы маанилерди тараза «Абалды суроодо» берет, ошондуктан окуу ашыкча сырсөз аракетисиз.", "The scale reports current values in its status request, so reading costs no password attempts.", "Tartı mevcut değerleri durum sorgusunda verir; okuma şifre denemesi harcamaz.", "Joriy qiymatlarni tarozi holat so‘rovida beradi, shuning uchun o‘qish parol urinishini sarflamaydi."),
            out var body);
        AddParam(body, _systemParams, new Param
        {
            Title = L("Номер весов (1–99)", "Тараза номуру (1–99)", "Scale number (1–99)", "Tartı numarası (1–99)", "Tarozi raqami (1–99)"),
            SetCmd = P.CmdSetScaleNumber, Kind = Kind.Number, Min = 1, Max = 99, FromStatus = s => s.ScaleNumber,
        });
        AddParam(body, _systemParams, new Param
        {
            Title = L("Режим печати", "Басуу режими", "Print mode", "Baskı modu", "Chop rejimi"),
            SetCmd = P.CmdSetPrintMode, Kind = Kind.Choice, FromStatus = s => s.PrintMode,
            Options = new (long, string)[]
            {
                (0, L("печать выключена", "басуу өчүк", "printing off", "baskı kapalı", "chop o‘chiq")),
                (1, L("печать разрешена", "басууга уруксат", "printing on", "baskı açık", "chop yoqiq")),
                (2, L("автопечать", "автобасуу", "auto-print", "otomatik baskı", "avtochop")),
            },
        });
        AddParam(body, _systemParams, new Param
        {
            Title = L("Порог автопечати, г (0 — выкл)", "Автобасуу чеги, г (0 — өчүк)", "Auto-print threshold, g (0 off)", "Otomatik baskı eşiği, g (0 kapalı)", "Avtochop chegarasi, g (0 — o‘chiq)"),
            SetCmd = P.CmdSetAutoPrintThreshold, Kind = Kind.Number, Width = 2, Min = 0, Max = 65535, FromStatus = s => s.AutoPrintThresholdGrams,
        });
        AddParam(body, _systemParams, new Param
        {
            Title = L("Звук клавиш", "Баскычтардын үнү", "Key sound", "Tuş sesi", "Tugma ovozi"),
            SetCmd = P.CmdSetSound, Kind = Kind.Toggle, FromStatus = s => s.SoundOn ? 1 : 0,
        });
        AddParam(body, _systemParams, new Param
        {
            Title = L("Режим фасовки", "Таңгактоо режими", "Packing mode", "Paketleme modu", "Qadoqlash rejimi"),
            SetCmd = P.CmdSetPackaging, Kind = Kind.Toggle, FromStatus = s => s.PackagingOn ? 1 : 0,
        });
        AddParam(body, _systemParams, new Param
        {
            Title = L("Формат времени", "Убакыт форматы", "Time format", "Saat biçimi", "Vaqt formati"),
            SetCmd = P.CmdSetTimeFormat, Kind = Kind.Choice, FromStatus = s => s.TimeFormat,
            Options = new (long, string)[] { (0, L("12-часовой", "12 сааттык", "12-hour", "12 saat", "12 soatlik")), (1, L("24-часовой", "24 сааттык", "24-hour", "24 saat", "24 soatlik")) },
        });
        AddParam(body, _systemParams, new Param
        {
            Title = L("Формат даты", "Дата форматы", "Date format", "Tarih biçimi", "Sana formati"),
            SetCmd = P.CmdSetDateFormat, Kind = Kind.Choice, FromStatus = s => s.DateFormat,
            Options = new (long, string)[] { (0, L("ДД ММ ГГ", "КК АА ЖЖ", "DD MM YY", "GG AA YY", "KK OO YY")), (1, L("ГГ ММ ДД", "ЖЖ АА КК", "YY MM DD", "YY AA GG", "YY OO KK")), (2, L("ММ ДД ГГ", "АА КК ЖЖ", "MM DD YY", "AA GG YY", "OO KK YY")) },
        });
        AddParam(body, _systemParams, new Param
        {
            Title = L("Вызов товара на весах", "Таразада товарды чакыруу", "Item lookup on scale", "Tartıda ürün çağırma", "Tarozida tovarni chaqirish"),
            GetCmd = P.CmdGetPluAccess, SetCmd = P.CmdSetPluAccess, Kind = Kind.Choice,
            Options = new (long, string)[] { (0, L("по номеру ПЛУ", "ПЛУ номуру боюнча", "by PLU number", "PLU numarasıyla", "PLU raqami bo‘yicha")), (1, L("по коду товара", "товардын коду боюнча", "by item code", "ürün koduyla", "tovar kodi bo‘yicha")) },
        });
        AddReadWriteButtons(body, _systemParams);
        SystemPanel.Children.Add(card);
    }

    // =====================================================================================
    // Вкладка «Товары» («Работа с товарами» в программе Штрих-М)
    // =====================================================================================

    private void BuildGoodsTab()
    {
        var card = Card(L("Работа с товарами", "Товарлар менен иштөө", "Working with goods", "Ürünlerle çalışma", "Tovarlar bilan ishlash"),
            L("Эти параметры влияют только на работу продавца с клавиатуры весов.", "Бул параметрлер сатуучунун тараза клавиатурасы менен иштөөсүнө гана таасир этет.", "These parameters only affect how the seller works with the scale keyboard.", "Bu parametreler yalnızca satıcının tartı klavyesiyle çalışmasını etkiler.", "Bu parametrlar faqat sotuvchining tarozi klaviaturasi bilan ishlashiga ta’sir qiladi."),
            out var body);
        AddParam(body, _goodsParams, new Param { Title = L("Разрешить запись цены ПЛУ с весов", "Таразадан ПЛУ баасын жазууга уруксат", "Allow saving PLU price from the scale", "Tartıdan PLU fiyatı kaydetmeye izin ver", "Tarozidan PLU narxini yozishga ruxsat"), GetCmd = P.CmdGetWritePluPrice, SetCmd = P.CmdSetWritePluPrice, Kind = Kind.Toggle });
        AddParam(body, _goodsParams, new Param { Title = L("Разрешить менять цену выбранного товара", "Тандалган товардын баасын өзгөртүүгө уруксат", "Allow changing the price of a selected item", "Seçili ürünün fiyatını değiştirmeye izin ver", "Tanlangan tovar narxini o‘zgartirishga ruxsat"), GetCmd = P.CmdGetChangePluPrice, SetCmd = P.CmdSetChangePluPrice, Kind = Kind.Toggle });
        AddParam(body, _goodsParams, new Param { Title = L("Разрешить свободную цену (без товара)", "Эркин баага уруксат (товарсыз)", "Allow free price (no item)", "Serbest fiyata izin ver (ürünsüz)", "Erkin narxga ruxsat (tovarsiz)"), GetCmd = P.CmdGetFreePrice, SetCmd = P.CmdSetFreePrice, Kind = Kind.Toggle, Inverted = true });
        AddParam(body, _goodsParams, new Param { Title = L("Вести учёт продаж по ПЛУ", "ПЛУ боюнча сатуу эсебин жүргүзүү", "Keep sales totals per PLU", "PLU bazında satış kaydı tut", "PLU bo‘yicha savdo hisobini yuritish"), GetCmd = P.CmdGetRecordKeeping, SetCmd = P.CmdSetRecordKeeping, Kind = Kind.Toggle });
        AddParam(body, _goodsParams, new Param { Title = L("Сбрасывать товар после печати", "Басуудан кийин товарды түшүрүү", "Reset item after printing", "Baskıdan sonra ürünü sıfırla", "Chopdan keyin tovarni tashlash"), GetCmd = P.CmdGetResetPluAfterPrint, SetCmd = P.CmdSetResetPluAfterPrint, Kind = Kind.Toggle });
        AddParam(body, _goodsParams, new Param
        {
            Title = L("Сброс товара по таймеру", "Таймер боюнча товарды түшүрүү", "Reset item by timer", "Zamanlayıcıyla ürün sıfırlama", "Taymer bo‘yicha tovarni tashlash"),
            GetCmd = P.CmdGetResetPluByTimer, SetCmd = P.CmdSetResetPluByTimer, Kind = Kind.Choice,
            Options = new (long, string)[]
            {
                (0, L("выключен", "өчүк", "off", "kapalı", "o‘chiq")), (1, L("через 5 с", "5 сек кийин", "after 5 s", "5 sn sonra", "5 s dan keyin")),
                (2, L("через 15 с", "15 сек кийин", "after 15 s", "15 sn sonra", "15 s dan keyin")), (3, L("через 30 с", "30 сек кийин", "after 30 s", "30 sn sonra", "30 s dan keyin")),
                (4, L("через 45 с", "45 сек кийин", "after 45 s", "45 sn sonra", "45 s dan keyin")), (5, L("через 1 мин", "1 мүн кийин", "after 1 min", "1 dk sonra", "1 daq dan keyin")),
                (6, L("через 2 мин", "2 мүн кийин", "after 2 min", "2 dk sonra", "2 daq dan keyin")), (7, L("через 3 мин", "3 мүн кийин", "after 3 min", "3 dk sonra", "3 daq dan keyin")),
            },
        });
        AddParam(body, _goodsParams, new Param
        {
            Title = L("Поле «Групповой код» товара — это", "Товардын «Топтук коду» талаасы — бул", "The item's “group code” field is", "Ürünün “grup kodu” alanı", "Tovarning «guruh kodi» maydoni — bu"),
            GetCmd = P.CmdGetGroupCodeUse, SetCmd = P.CmdSetGroupCodeUse, Kind = Kind.Choice,
            Options = new (long, string)[] { (0, L("групповой код", "топтук код", "group code", "grup kodu", "guruh kodi")), (1, L("дата изготовления", "даярдалган дата", "production date", "üretim tarihi", "ishlab chiqarilgan sana")) },
        });
        AddReadWriteButtons(body, _goodsParams);
        GoodsPanel.Children.Add(card);
    }

    // =====================================================================================
    // Вкладка «Печать и этикетка»
    // =====================================================================================

    private static (long, string)[] LabelFormats() => new (long, string)[]
    {
        // Список — по «Руководству администратора ШТРИХ-ПРИНТ 4.5» (1.2.2) и тестовой программе.
        (0, "58×30, " + L("ШК", "ШК", "barcode", "barkod", "ShK")), (1, "58×40, " + L("ШК", "ШК", "barcode", "barkod", "ShK")),
        (2, "58×50, " + L("ШК", "ШК", "barcode", "barkod", "ShK")), (3, "58×60, " + L("ШК", "ШК", "barcode", "barkod", "ShK")),
        (4, "58×40, " + L("ШК, с разметкой", "ШК, белгилөө менен", "barcode, pre-printed", "barkod, baskılı", "ShK, belgili")),
        (5, "58×60, " + L("ШК, с разметкой", "ШК, белгилөө менен", "barcode, pre-printed", "barkod, baskılı", "ShK, belgili")),
        (6, "58×80, " + L("ШК", "ШК", "barcode", "barkod", "ShK")), (7, "58×90, " + L("ШК", "ШК", "barcode", "barkod", "ShK")),
        (8, "58×100, " + L("ШК", "ШК", "barcode", "barkod", "ShK")), (9, "58×120, " + L("ШК", "ШК", "barcode", "barkod", "ShK")),
        (10, L("Формат 1 (свой)", "Формат 1 (өзүңүздүкү)", "Format 1 (custom)", "Format 1 (özel)", "Format 1 (o‘z)")),
        (11, L("Формат 2 (свой)", "Формат 2 (өзүңүздүкү)", "Format 2 (custom)", "Format 2 (özel)", "Format 2 (o‘z)")),
        (12, L("Формат 3 (свой)", "Формат 3 (өзүңүздүкү)", "Format 3 (custom)", "Format 3 (özel)", "Format 3 (o‘z)")),
        (13, L("Формат 4 (свой)", "Формат 4 (өзүңүздүкү)", "Format 4 (custom)", "Format 4 (özel)", "Format 4 (o‘z)")),
        (14, L("Формат 5 (свой)", "Формат 5 (өзүңүздүкү)", "Format 5 (custom)", "Format 5 (özel)", "Format 5 (o‘z)")),
    };

    private static (long, string)[] Steps0To15(string normal)
    {
        // 0 и 8 — «норма»; 1..7 — меньше/вверх, 9..15 — больше/вниз (единица 0,125 мм для смещения).
        var list = new List<(long, string)> { (8, normal) };
        for (var v = 1; v <= 7; v++)
            list.Add((v, $"−{8 - v}"));
        for (var v = 9; v <= 15; v++)
            list.Add((v, $"+{v - 8}"));
        list.Add((0, normal + " (0)"));
        return list.ToArray();
    }

    private void BuildPrintTab()
    {
        var card = Card(L("Этикетка", "Этикетка", "Label", "Etiket", "Yorliq"), null, out var body);
        AddParam(body, _printParams, new Param { Title = L("Формат этикетки", "Этикетка форматы", "Label format", "Etiket formatı", "Yorliq formati"), GetCmd = P.CmdGetLabelFormat, SetCmd = P.CmdSetLabelFormat, Kind = Kind.Choice, Options = LabelFormats() });
        AddParam(body, _printParams, new Param
        {
            Title = L("Строк в названии товара", "Товардын аталышындагы саптар", "Lines in item name", "Ürün adı satır sayısı", "Tovar nomidagi qatorlar"),
            GetCmd = P.CmdGetNameLines, SetCmd = P.CmdSetNameLines, Kind = Kind.Choice,
            Options = new (long, string)[] { (0, L("не печатать", "баспоо", "do not print", "basma", "chop etmaslik")), (1, "1"), (2, "2") },
        });
        AddParam(body, _printParams, new Param
        {
            Title = L("Что печатать на этикетке", "Этикеткага эмне басылат", "What to print on the label", "Etikete basılacaklar", "Yorliqqa nima chop etiladi"),
            GetCmd = P.CmdGetPrintableFields, SetCmd = P.CmdSetPrintableFields, Kind = Kind.Bits,
            Options = new (long, string)[]
            {
                (0, L("дата", "дата", "date", "tarih", "sana")), (1, L("время", "убакыт", "time", "saat", "vaqt")),
                (2, L("номер этикетки", "этикетка номуру", "label number", "etiket no.", "yorliq raqami")), (3, L("номер весов", "тараза номуру", "scale number", "tartı no.", "tarozi raqami")),
                (4, L("тара", "тара", "tare", "dara", "tara")), (5, L("срок годности", "жарактуулук мөөнөтү", "shelf life", "raf ömrü", "yaroqlilik muddati")),
                (6, L("цена и стоимость", "баа жана наркы", "price and total", "fiyat ve tutar", "narx va qiymat")), (7, L("знаки валют", "валюта белгилери", "currency signs", "para birimi işareti", "valyuta belgilari")),
            },
        });
        AddReadWriteButtons(body, _printParams);
        PrintPanel.Children.Add(card);

        var printer = Card(L("Принтер весов", "Тараза принтери", "Scale printer", "Tartı yazıcısı", "Tarozi printeri"), null, out var pBody);
        var printerParams = new List<Param>();
        AddParam(pBody, printerParams, new Param { Title = L("Печать на непрерывной ленте (не на этикетках)", "Үзгүлтүксүз лентага басуу (этикеткага эмес)", "Print on continuous tape (not labels)", "Sürekli ruloya baskı (etiket değil)", "Uzluksiz lentaga chop (yorliqqa emas)"), GetCmd = P.CmdGetPrintType, SetCmd = P.CmdSetPrintType, Kind = Kind.Toggle });
        AddParam(pBody, printerParams, new Param { Title = L("Развернуть печать на 180°", "Басууну 180° буруу", "Rotate print 180°", "Baskıyı 180° döndür", "Chopni 180° burish"), GetCmd = P.CmdGetTurnPrint, SetCmd = P.CmdSetTurnPrint, Kind = Kind.Toggle });
        AddParam(pBody, printerParams, new Param
        {
            Title = L("Датчик снятой этикетки", "Алынган этикетка датчиги", "Taken-label sensor", "Alınan etiket sensörü", "Olingan yorliq datchigi"),
            GetCmd = P.CmdGetLabelSensor, SetCmd = P.CmdSetLabelSensor, Kind = Kind.Choice,
            Options = new (long, string)[] { (0, L("не проверять", "текшербөө", "do not check", "kontrol etme", "tekshirmaslik")), (1, L("проверять", "текшерүү", "check", "kontrol et", "tekshirish")), (2, L("выборочно", "тандап", "selectively", "seçmeli", "tanlab")) },
        });
        AddParam(pBody, printerParams, new Param { Title = L("Смещение печати (шаг 0,125 мм)", "Басуунун жылышы (кадамы 0,125 мм)", "Print offset (0.125 mm steps)", "Baskı kayması (0,125 mm adım)", "Chop siljishi (0,125 mm qadam)"), GetCmd = P.CmdGetPrintOffset, SetCmd = P.CmdSetPrintOffset, Kind = Kind.Choice, Options = Steps0To15(L("без смещения", "жылышсыз", "no offset", "kaymasız", "siljishsiz")) });
        AddParam(pBody, printerParams, new Param { Title = L("Контраст печати", "Басуу контрасты", "Print contrast", "Baskı kontrastı", "Chop kontrasti"), GetCmd = P.CmdGetContrast, SetCmd = P.CmdSetContrast, Kind = Kind.Choice, Options = Steps0To15(L("норма", "норма", "normal", "normal", "me’yor")) });
        AddParam(pBody, printerParams, new Param { Title = L("Печатать при нажатии П+ (в сумматор)", "П+ басылганда басуу (сумматорго)", "Print on P+ (add to total)", "P+ ile baskı (toplama)", "P+ bosilganda chop (summatorga)"), GetCmd = P.CmdGetPrintByPPlus, SetCmd = P.CmdSetPrintByPPlus, Kind = Kind.Toggle });
        AddParam(pBody, printerParams, new Param { Title = L("Печатать сразу при выборе товара", "Товар тандалганда дароо басуу", "Print as soon as an item is selected", "Ürün seçilince hemen bas", "Tovar tanlanishi bilan chop"), GetCmd = P.CmdGetPrintOnPluSelect, SetCmd = P.CmdSetPrintOnPluSelect, Kind = Kind.Toggle });
        AddReadWriteButtons(pBody, printerParams);
        pBody.Children.Add(ButtonRow(
            MakeButton(L("Промотать ленту", "Лентаны айлантуу", "Feed", "Şerit ilerlet", "Lentani aylantirish"), false, async (_, _) => await RunAsync(async scale =>
            {
                await scale.FeedAsync().ConfigureAwait(false);
                return L("Лента промотана.", "Лента айлантылды.", "Fed.", "Şerit ilerletildi.", "Lenta aylantirildi.");
            }).ConfigureAwait(true)),
            MakeButton(L("Печать тестовой этикетки", "Сыноо этикеткасын басуу", "Print test label", "Test etiketi bas", "Sinov yorlig‘ini chop etish"), false, async (_, _) => await RunAsync(async scale =>
            {
                await scale.PrintTestLabelAsync().ConfigureAwait(false);
                return L("Тестовая этикетка напечатана.", "Сыноо этикеткасы басылды.", "Test label printed.", "Test etiketi basıldı.", "Sinov yorlig‘i chop etildi.");
            }).ConfigureAwait(true))));
        PrintPanel.Children.Add(printer);
    }

    // =====================================================================================
    // Вкладка «Штрих-код»
    // =====================================================================================

    private void BuildBarcodeTab()
    {
        var card = Card(L("Весовой штрих-код на этикетке", "Этикеткадагы салмак штрих-коду", "Weight barcode on the label", "Etiketteki tartı barkodu", "Yorliqdagi vazn shtrix-kodi"),
            L("Эти настройки должны совпадать с настройкой весового штрих-кода в кассе — иначе касса не узнает товар с этикетки. Буквы: П — префикс, Т — код товара, С — стоимость, В — масса, З — номер предприятия GS1, К/к — контрольная цифра.",
              "Бул жөндөөлөр кассадагы салмак штрих-кодунун жөндөөсү менен дал келиши керек — антпесе касса этикеткадан товарды тааныбайт. Тамгалар: П — префикс, Т — товар коду, С — наркы, В — масса, З — GS1 ишкана номуру, К/к — текшерүү цифрасы.",
              "These must match the weight-barcode setting in the till, otherwise the till won't recognise items from labels. Letters: П prefix, Т item code, С total price, В weight, З GS1 company number, К/к check digit.",
              "Bunlar kasadaki tartı barkodu ayarıyla aynı olmalı, yoksa kasa etiketten ürünü tanımaz. Harfler: П önek, Т ürün kodu, С tutar, В ağırlık, З GS1 firma no., К/к kontrol hanesi.",
              "Bular kassadagi vazn shtrix-kodi sozlamasi bilan mos bo‘lishi kerak, aks holda kassa yorliqdan tovarni tanimaydi. Harflar: П — prefiks, Т — tovar kodi, С — qiymat, В — massa, З — GS1 korxona raqami, К/к — nazorat raqami."),
            out var body);
        var structures = new (long, string)[]
        {
            (0, L("не печатать штрих-код", "штрих-код баспоо", "no barcode", "barkod yok", "shtrix-kod yo‘q")),
            (1, "ППТТТТТкССССК"), (2, "ППТТТТкСССССК"), (3, "ППТТТТТТССССК"), (4, "ППТТТТТСССССК"), (5, "ППТТТТССССССК"),
            (6, "ППТТТТТТВВВВК"), (7, "ППТТТТТВВВВВК"), (8, "ППТТТТВВВВВВК"), (9, "ПТТТТТТВВВВВК"),
            (10, "ПППЗЗЗЗЗЗЗТТК"), (11, "ПППЗЗЗЗЗЗТТТК"), (12, "ПППЗЗЗЗЗТТТТК"), (13, "ПППЗЗЗЗТТТТТК"), (14, "ПППЗЗЗТТТТТТК"),
        };
        AddParam(body, _barcodeParams, new Param { Title = L("Структура штрих-кода", "Штрих-коддун түзүлүшү", "Barcode structure", "Barkod yapısı", "Shtrix-kod tuzilmasi"), GetCmd = P.CmdGetBarcodeStructure, SetCmd = P.CmdSetBarcodeStructure, Kind = Kind.Choice, Options = structures });
        AddParam(body, _barcodeParams, new Param
        {
            Title = L("Что ставить в префикс (П)", "Префикске (П) эмне коюлат", "What goes into the prefix (П)", "Öneke (П) ne yazılır", "Prefiksga (П) nima qo‘yiladi"),
            GetCmd = P.CmdGetBarcodePrefixType, SetCmd = P.CmdSetBarcodePrefixType, Kind = Kind.Choice,
            Options = new (long, string)[]
            {
                (0, L("номер весов", "тараза номуру", "scale number", "tartı numarası", "tarozi raqami")),
                (1, L("групповой код товара", "товардын топтук коду", "item group code", "ürün grup kodu", "tovar guruh kodi")),
                (2, L("весовой / штучный префикс", "салмак / даана префикси", "weight / piece prefix", "tartılı / adet öneki", "vazn / dona prefiksi")),
                (3, L("префикс GS1", "GS1 префикси", "GS1 prefix", "GS1 öneki", "GS1 prefiksi")),
            },
        });
        _prefixWeight = new Param { Title = L("Префикс весового товара (0–99)", "Салмактуу товардын префикси (0–99)", "Weighed item prefix (0–99)", "Tartılı ürün öneki (0–99)", "Tortiladigan tovar prefiksi (0–99)"), Kind = Kind.Number, Min = 0, Max = 99 };
        _prefixPiece = new Param { Title = L("Префикс штучного товара (0–99)", "Даана товардын префикси (0–99)", "Piece item prefix (0–99)", "Adetli ürün öneki (0–99)", "Donali tovar prefiksi (0–99)"), Kind = Kind.Number, Min = 0, Max = 99 };
        _prefixTotal = new Param { Title = L("Префикс итоговой этикетки (0 — запрещена)", "Жыйынтык этикетка префикси (0 — тыюу)", "Total label prefix (0 = disabled)", "Toplam etiket öneki (0 = kapalı)", "Yakuniy yorliq prefiksi (0 — taqiqlangan)"), Kind = Kind.Number, Min = 0, Max = 99 };
        AddParam(body, _barcodeParams, _prefixWeight);
        AddParam(body, _barcodeParams, _prefixPiece);
        AddParam(body, _barcodeParams, _prefixTotal);
        AddParam(body, _barcodeParams, new Param { Title = L("Префикс GS1 (0–999)", "GS1 префикси (0–999)", "GS1 prefix (0–999)", "GS1 öneki (0–999)", "GS1 prefiksi (0–999)"), GetCmd = P.CmdGetGs1Prefix, SetCmd = P.CmdSetGs1Prefix, Kind = Kind.Number, Width = 2, Min = 0, Max = 999 });
        AddParam(body, _barcodeParams, new Param { Title = L("Номер предприятия GS1", "GS1 ишкана номуру", "GS1 company number", "GS1 firma numarası", "GS1 korxona raqami"), GetCmd = P.CmdGetGs1PlantNumber, SetCmd = P.CmdSetGs1PlantNumber, Kind = Kind.Number, Width = 4, Min = 0, Max = 9999999 });
        AddParam(body, _barcodeParams, new Param
        {
            Title = L("Дополнение EAN-5", "EAN-5 толуктоосу", "EAN-5 add-on", "EAN-5 eki", "EAN-5 qo‘shimchasi"),
            GetCmd = P.CmdGetUseEan5, SetCmd = P.CmdSetUseEan5, Kind = Kind.Choice,
            Options = new (long, string)[] { (0, L("нет", "жок", "none", "yok", "yo‘q")), (1, L("срок годности", "жарактуулук мөөнөтү", "expiry date", "son kullanma", "yaroqlilik muddati")), (2, L("масса в граммах", "грамм менен масса", "weight in grams", "gram cinsinden ağırlık", "grammdagi massa")) },
        });
        AddReadWriteButtons(body, _barcodeParams);
        BarcodePanel.Children.Add(card);

        BuildBarcodePreviewCard();
        foreach (var p in _barcodeParams)
        {
            switch (p.Editor)
            {
                case ComboBox c: c.SelectionChanged += (_, _) => UpdateBarcodePreview(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => UpdateBarcodePreview(); break;
            }
        }
    }

    // =====================================================================================
    // Пример весового штрих-кода и сверка с кассой (просьба владельца «2000001003923 —
    // добавь возможность редактировать штрих-код при отправке на весы»)
    // =====================================================================================

    private NumericUpDown _sampleCode = null!;
    private NumericUpDown _sampleGrams = null!;
    private TextBlock _sampleBarcode = null!;
    private TextBlock _sampleVerdict = null!;
    private TextBlock _companyFormatText = null!;
    private decimal _samplePriceSom = 100m;
    private string _sampleName = "";

    /// <summary>Открыть окно сразу на вкладке «Штрих-код» с примером для товара из окна выгрузки.</summary>
    public void ShowBarcodeTabFor(string productName, long codeInBarcode, decimal priceSom)
    {
        _sampleName = productName ?? "";
        _samplePriceSom = priceSom > 0 ? priceSom : 100m;
        _sampleCode.Value = Math.Clamp(codeInBarcode, 1, 999999);
        Tabs.SelectedItem = BarcodeTab;
        UpdateBarcodePreview();
    }

    private void BuildBarcodePreviewCard()
    {
        var card = Card(L("Какой штрих-код напечатают весы", "Тараза кандай штрих-код басат", "Which barcode the scale will print", "Tartı hangi barkodu basacak", "Tarozi qanday shtrix-kod chop etadi"),
            L("Пример собирается из настроек выше. Касса проверяет его тем же разбором, что и при скане этикетки.",
              "Мисал жогорудагы жөндөөлөрдөн түзүлөт. Касса аны этикетканы сканерлегендей эле текшерет.",
              "The example is built from the settings above and checked by the same parser the till uses when scanning a label.",
              "Örnek yukarıdaki ayarlardan oluşturulur ve kasa etiketi tararken kullandığı ayrıştırıcıyla kontrol eder.",
              "Namuna yuqoridagi sozlamalardan tuziladi va kassa yorliqni skanerlagandagi tahlil bilan tekshiriladi."),
            out var body);

        _companyFormatText = new TextBlock { Classes = { "hint" } };
        body.Children.Add(_companyFormatText);

        _sampleCode = new NumericUpDown { Minimum = 1, Maximum = 999999, Value = 1, Increment = 1, FormatString = "0", MinWidth = 160 };
        _sampleCode.ValueChanged += (_, _) => UpdateBarcodePreview();
        body.Children.Add(Row(L("Код товара в штрих-коде", "Штрих-коддогу товар коду", "Item code in the barcode", "Barkoddaki ürün kodu", "Shtrix-koddagi tovar kodi"), _sampleCode));

        _sampleGrams = new NumericUpDown { Minimum = 1, Maximum = 99999, Value = 392, Increment = 1, FormatString = "0", MinWidth = 160 };
        _sampleGrams.ValueChanged += (_, _) => UpdateBarcodePreview();
        body.Children.Add(Row(L("Масса для примера, г", "Мисал үчүн масса, г", "Sample weight, g", "Örnek ağırlık, g", "Namuna uchun massa, g"), _sampleGrams));

        _sampleBarcode = new TextBlock { FontSize = 22, FontWeight = FontWeight.Bold, FontFamily = new FontFamily("Consolas, Segoe UI"), Foreground = ThemeBrush("BrushText", Brushes.Black) };
        body.Children.Add(_sampleBarcode);
        _sampleVerdict = new TextBlock { Classes = { "label" } };
        body.Children.Add(_sampleVerdict);

        body.Children.Add(ButtonRow(MakeButton(L("Подобрать под кассу", "Кассага ылайыктоо", "Match the till", "Kasaya uydur", "Kassaga moslash"), false, (_, _) => MatchBarcodeToKassa())));
        BarcodePanel.Children.Add(card);
        UpdateBarcodePreview();
    }

    private long? EditorLong(Param? p) => p is null ? null : GetEditorValue(p) is long v ? v : null;

    private Param? BarcodeParam(byte getCmd) => _barcodeParams.FirstOrDefault(p => p.GetCmd == getCmd && p.GetCmd != 0);

    /// <summary>Пересобирает пример ШК и вердикт «касса прочитает / не прочитает».</summary>
    private void UpdateBarcodePreview()
    {
        if (_sampleBarcode is null)
            return;

        var layout = NurMarketKassa.Core.Application.WeightBarcodeParser.Layout;
        var mode = NurMarketKassa.Core.Application.WeightBarcodeParser.Mode;
        var amountUnit = NurMarketKassa.Core.Application.WeightBarcodeParser.AmountUnit;
        var isCode = string.Equals(layout, "code", StringComparison.OrdinalIgnoreCase);
        _companyFormatText.Text = L("Настройка компании (сайт → Весы → Настройки): раскладка ", "Компаниянын жөндөөсү (сайт → Таразалар → Жөндөөлөр): ", "Company setting (website → Scales → Settings): layout ", "Şirket ayarı (site → Tartılar → Ayarlar): düzen ", "Kompaniya sozlamasi (sayt → Tarozilar → Sozlamalar): ")
            + (isCode ? L("«по коду» 2+6+4+1", "«код боюнча» 2+6+4+1", "“by code” 2+6+4+1", "“koda göre” 2+6+4+1", "«kod bo‘yicha» 2+6+4+1") : L("«по PLU» 2+5+5+1", "«PLU боюнча» 2+5+5+1", "“by PLU” 2+5+5+1", "“PLU’ya göre” 2+5+5+1", "«PLU bo‘yicha» 2+5+5+1"))
            + L(", режим ", ", режим ", ", mode ", ", mod ", ", rejim ") + mode
            + L(". Касса ждёт на весах структуру ", ". Касса таразада күтөт: түзүлүш ", ". The till expects scale structure ", ". Kasa tartıda şu yapıyı bekler: ", ". Kassa tarozida kutadi: tuzilma ")
            + $"{ShtrikhBarcodeFormat.RecommendedStructure(layout)} ({ShtrikhBarcodeFormat.Structures[ShtrikhBarcodeFormat.RecommendedStructure(layout)]})"
            + L(" и префикс 20–29.", " жана префикс 20–29.", " and prefix 20–29.", " ve önek 20–29.", " va prefiks 20–29.");

        var structure = EditorLong(BarcodeParam(P.CmdGetBarcodeStructure));
        var prefixType = EditorLong(BarcodeParam(P.CmdGetBarcodePrefixType));
        if (structure is null || prefixType is null)
        {
            _sampleBarcode.Text = "—";
            _sampleVerdict.Text = L("Нажмите «Прочитать с весов» выше — пример строится по настройкам весов.", "Жогорудагы «Таразадан окуу» басыңыз — мисал тараза жөндөөлөрү боюнча түзүлөт.", "Press “Read from scale” above — the example is built from the scale settings.", "Yukarıda “Tartıdan oku”ya basın — örnek tartı ayarlarından oluşturulur.", "Yuqorida «Tarozidan o‘qish»ni bosing — namuna tarozi sozlamalaridan tuziladi.");
            return;
        }

        var weightPrefix = (int)(EditorLong(_prefixWeight) ?? 20);
        var gs1Prefix = (int)(EditorLong(BarcodeParam(P.CmdGetGs1Prefix)) ?? 0);
        var gs1Plant = EditorLong(BarcodeParam(P.CmdGetGs1PlantNumber)) ?? 0;
        var prefix = ShtrikhBarcodeFormat.EffectivePrefix((int)prefixType, _status?.ScaleNumber ?? 1, weightPrefix, gs1Prefix);
        var code = (long)(_sampleCode.Value ?? 1);
        var grams = (int)(_sampleGrams.Value ?? 392);
        var costMde = P.PriceToMde(_samplePriceSom * grams / 1000m, DecimalDigits);

        var sample = ShtrikhBarcodeFormat.BuildSample((int)structure, prefix ?? 20, code, grams, costMde, gs1Prefix, gs1Plant);
        _sampleBarcode.Text = sample ?? L("штрих-код не печатается", "штрих-код басылбайт", "no barcode is printed", "barkod basılmaz", "shtrix-kod chop etilmaydi");

        var problems = ShtrikhBarcodeFormat.CheckCompatibility((int)structure, prefix, layout, mode, amountUnit, DecimalDigits);
        var max = ShtrikhBarcodeFormat.MaxProductCode((int)structure);
        if (max > 0 && code > max)
            problems.Add(L($"код {code} не помещается в штрих-код (максимум {max}) — весы напечатают только последние цифры", $"{code} коду штрих-кодго батпайт (эң көбү {max}) — тараза акыркы цифраларды гана басат", $"code {code} does not fit (max {max}) — the scale prints only the last digits", $"{code} kodu sığmaz (en fazla {max}) — tartı yalnızca son haneleri basar", $"{code} kodi sig‘maydi (ko‘pi bilan {max}) — tarozi faqat oxirgi raqamlarni chop etadi"));

        if (problems.Count == 0 && sample is not null && !sample.Contains('?')
            && NurMarketKassa.Core.Application.WeightBarcodeParser.TryParse(sample, out var parsed))
        {
            var product = LocalCartService.FindByEmbeddedCode(parsed.ProductCode);
            var what = product?.Title ?? (_sampleName.Length > 0 ? _sampleName : L("товар с кодом ", "коду бар товар ", "item with code ", "kodlu ürün ", "kodli tovar ") + parsed.ProductCode.TrimStart('0'));
            _sampleVerdict.Text = "✓ " + L("Касса прочитает: ", "Касса окуйт: ", "The till reads: ", "Kasa okur: ", "Kassa o‘qiydi: ") + what + " · "
                + (parsed.Kind == NurMarketKassa.Core.Domain.WeightBarcodeValueKind.Weight
                    ? parsed.Value.ToString("0.000", CultureInfo.CurrentCulture) + L(" кг", " кг", " kg", " kg", " kg")
                    : parsed.Value.ToString("0.00", CultureInfo.CurrentCulture) + L(" сом", " сом", " som", " som", " so‘m"))
                + (product is null ? L(" (товара с таким кодом в каталоге кассы нет)", " (мындай коддуу товар кассанын каталогунда жок)", " (no item with this code in the till catalog)", " (kasa kataloğunda bu kodla ürün yok)", " (kassa katalogida bunday kodli tovar yo‘q)") : "");
            _sampleVerdict.Foreground = ThemeBrush("BrushSuccess", Brushes.Green);
        }
        else
        {
            if (problems.Count == 0)
                problems.Add(L("касса не смогла разобрать этот штрих-код", "касса бул штрих-кодду окуй алган жок", "the till could not parse this barcode", "kasa bu barkodu çözemedi", "kassa bu shtrix-kodni o‘qiy olmadi"));
            _sampleVerdict.Text = "⚠ " + L("Касса НЕ узнает товар: ", "Касса товарды ТААНЫБАЙТ: ", "The till will NOT recognise the item: ", "Kasa ürünü TANIMAZ: ", "Kassa tovarni TANIMAYDI: ") + string.Join("; ", problems) + ".";
            _sampleVerdict.Foreground = ThemeBrush("BrushWarning", Brushes.DarkOrange);
        }
    }

    /// <summary>«Подобрать под кассу»: ставит в поля структуру и префикс, которые касса разбирает
    /// при текущей настройке компании. В весы ничего не уходит, пока не нажата «Записать в весы».</summary>
    private void MatchBarcodeToKassa()
    {
        var structureParam = BarcodeParam(P.CmdGetBarcodeStructure);
        var prefixTypeParam = BarcodeParam(P.CmdGetBarcodePrefixType);
        if (structureParam?.Loaded is null || prefixTypeParam?.Loaded is null)
        {
            ShowResult(L("Сначала «Прочитать с весов».", "Адегенде «Таразадан окуу».", "“Read from scale” first.", "Önce “Tartıdan oku”.", "Avval «Tarozidan o‘qish»."), true);
            return;
        }

        var mode = NurMarketKassa.Core.Application.WeightBarcodeParser.Mode;
        var byWeight = !string.Equals(mode, "amount", StringComparison.OrdinalIgnoreCase);
        SetEditorValue(structureParam, (long)ShtrikhBarcodeFormat.RecommendedStructure(NurMarketKassa.Core.Application.WeightBarcodeParser.Layout, byWeight));
        SetEditorValue(prefixTypeParam, 2L); // весовой / штучный префикс
        var weightPrefix = EditorLong(_prefixWeight) ?? 20;
        // Режим «авто»: 25 — это «сумма», для массы нужен любой другой из 20–29.
        if (weightPrefix is < 20 or > 29 || (byWeight && weightPrefix == 25 && !string.Equals(mode, "weight", StringComparison.OrdinalIgnoreCase)))
            SetEditorValue(_prefixWeight!, byWeight ? 20L : 25L);
        UpdateBarcodePreview();
        ShowResult(L("Поля подобраны под кассу. Проверьте пример и нажмите «Записать в весы».", "Талаалар кассага ылайыкталды. Мисалды текшерип «Таразага жазуу» басыңыз.", "Fields now match the till. Check the example and press “Write to scale”.", "Alanlar kasaya uyduruldu. Örneği kontrol edip “Tartıya yaz”a basın.", "Maydonlar kassaga moslandi. Namunani tekshirib «Taroziga yozish»ni bosing."), false);
    }

    // =====================================================================================
    // Вкладка «Тексты» («Заголовки/Реклама»)
    // =====================================================================================

    private void BuildTextsTab()
    {
        var card = Card(L("Тексты на этикетке и дисплее", "Этикеткадагы жана дисплейдеги тексттер", "Texts on label and display", "Etiket ve ekran metinleri", "Yorliq va displeydagi matnlar"),
            L("Название магазина печатается внизу этикетки, рекламная строка бежит на дисплее весов, когда ими не пользуются 5 минут.", "Дүкөндүн аты этикетканын ылдыйына басылат, жарнама сабы тараза 5 мүнөт колдонулбаганда дисплейде жүрөт.", "The shop name is printed at the bottom of the label; the advert line scrolls on the scale display after 5 idle minutes.", "Mağaza adı etiketin altına basılır; reklam satırı 5 dk boşta kalınca ekranda kayar.", "Do‘kon nomi yorliq pastida chop etiladi, reklama qatori tarozidan 5 daqiqa foydalanilmasa displeyda yuradi."),
            out var body);
        AddParam(body, _textParams, new Param { Title = L("Название магазина, строка 1", "Дүкөндүн аты, 1-сап", "Shop name, line 1", "Mağaza adı, satır 1", "Do‘kon nomi, 1-qator"), GetCmd = P.CmdGetShopName, SetCmd = P.CmdSetShopName, Kind = Kind.Text, Width = P.TitleFieldLength, Index = 1 });
        AddParam(body, _textParams, new Param { Title = L("Название магазина, строка 2", "Дүкөндүн аты, 2-сап", "Shop name, line 2", "Mağaza adı, satır 2", "Do‘kon nomi, 2-qator"), GetCmd = P.CmdGetShopName, SetCmd = P.CmdSetShopName, Kind = Kind.Text, Width = P.TitleFieldLength, Index = 2 });
        AddParam(body, _textParams, new Param { Title = L("Заголовок этикетки (если товар не выбран)", "Этикетка аталышы (товар тандалбаса)", "Label title (no item selected)", "Etiket başlığı (ürün seçilmezse)", "Yorliq sarlavhasi (tovar tanlanmasa)"), GetCmd = P.CmdGetLabelTitle, SetCmd = P.CmdSetLabelTitle, Kind = Kind.Text, Width = P.TitleFieldLength });
        AddParam(body, _textParams, new Param { Title = L("Заголовок итоговой этикетки", "Жыйынтык этикетканын аталышы", "Total label title", "Toplam etiket başlığı", "Yakuniy yorliq sarlavhasi"), GetCmd = P.CmdGetTotalLabelTitle, SetCmd = P.CmdSetTotalLabelTitle, Kind = Kind.Text, Width = P.TitleFieldLength });
        AddParam(body, _textParams, new Param { Title = L("Рекламная строка на дисплее", "Дисплейдеги жарнама сабы", "Advert line on display", "Ekrandaki reklam satırı", "Displeydagi reklama qatori"), GetCmd = P.CmdGetAdvert, SetCmd = P.CmdSetAdvert, Kind = Kind.Text, Width = P.AdvertFieldLength });
        for (var i = 1; i <= 5; i++)
        {
            AddParam(body, _textParams, new Param
            {
                Title = L($"Свой текст {i} (для своих форматов)", $"Өз текст {i} (өз форматтар үчүн)", $"Custom text {i} (custom formats)", $"Özel metin {i} (özel formatlar)", $"O‘z matn {i} (o‘z formatlar uchun)"),
                GetCmd = P.CmdGetUserText, SetCmd = P.CmdSetUserText, Kind = Kind.Text, Width = P.UserTextFieldLength, Index = i,
            });
        }
        AddReadWriteButtons(body, _textParams);
        TextsPanel.Children.Add(card);

        var urgent = Card(L("Срочное сообщение продавцу", "Сатуучуга шашылыш билдирүү", "Urgent message to the seller", "Satıcıya acil mesaj", "Sotuvchiga shoshilinch xabar"),
            L("Появится на дисплее весов и исчезнет после нажатия любой клавиши.", "Тараза дисплейинде чыгып, каалаган баскыч басылганда жоголот.", "Shows on the scale display until any key is pressed.", "Herhangi bir tuşa basılana kadar ekranda görünür.", "Tarozi displeyida chiqadi va istalgan tugma bosilganda yo‘qoladi."),
            out var uBody);
        _urgentText = new TextBox { MaxLength = P.AdvertFieldLength, Watermark = L("до 22 символов", "22 белгиге чейин", "up to 22 characters", "en fazla 22 karakter", "22 belgigacha") };
        uBody.Children.Add(_urgentText);
        uBody.Children.Add(ButtonRow(MakeButton(L("Показать на весах", "Таразада көрсөтүү", "Show on scale", "Tartıda göster", "Tarozida ko‘rsatish"), true, async (_, _) =>
        {
            var text = _urgentText.Text ?? "";
            await RunAsync(async scale =>
            {
                await scale.ShowUrgentMessageAsync(text).ConfigureAwait(false);
                return L("Сообщение показано на весах.", "Билдирүү таразада көрсөтүлдү.", "Message shown on the scale.", "Mesaj tartıda gösterildi.", "Xabar tarozida ko‘rsatildi.");
            }).ConfigureAwait(true);
        })));
        TextsPanel.Children.Add(urgent);
    }
}
