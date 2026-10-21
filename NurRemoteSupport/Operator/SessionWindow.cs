using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace NurRemoteSupport.Operator;

/// <summary>Окно сеанса с кассой: экран (вписать / 1:1 / на весь экран), мышь и клавиатура, «только просмотр», качество,
/// комбинации клавиш; справа — чат с кассиром, сведения о кассе, диагностика, журнал кассы (и zip), файлы на кассу, буфер
/// обмена, заметки по кассе (история обращений). Подключение — только после «Разрешить» кассира.</summary>
internal sealed class SessionWindow : Window
{
    private readonly Api _api;
    private readonly string? _sessionId;
    private readonly string? _code;
    private string _pin;
    private string _company, _machine;
    private readonly string _user, _note;
    private SessionClient? _client;
    private string _serverSessionId = "";
    private bool _live;
    private bool _viewOnly;
    private bool _fit = true;

    // экран
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly Border _screen = new() { Background = Brushes.Black, Focusable = true, ClipToBounds = true };
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Path _cursor = new()
    {
        Data = Geometry.Parse("M0,0 L0,16 L4.5,12 L7.5,19 L10,18 L7,11 L12.5,11 Z"), Fill = Brushes.White, Stroke = Brushes.Black, StrokeThickness = 1, IsVisible = false,
    };
    private readonly Border _status = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Ui.Panel, CornerRadius = new CornerRadius(14), Padding = new Thickness(22, 16), MaxWidth = 520 };
    private readonly TextBlock _statusText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Ui.Text, TextAlignment = TextAlignment.Center };
    private WriteableBitmap? _bitmap;
    private int _cx = -1, _cy = -1;

    // шапка
    private readonly TextBlock _title = new() { FontSize = 17, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Text };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Foreground = Ui.TextSoft };
    private readonly TextBlock _stats = new() { FontSize = 11, Foreground = Ui.TextSoft, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly ToastHost _toast = new();

    // инструменты
    private readonly StackPanel _chatList = new() { Spacing = 6 };
    private readonly ScrollViewer _chatScroll = new();
    private readonly TextBox _info = Mono();
    private readonly TextBox _logs = Mono();
    private readonly StackPanel _diag = new() { Spacing = 6 };
    private readonly StackPanel _files = new() { Spacing = 6 };
    private readonly TextBox _clip = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, Watermark = L.T("Текст для буфера кассы или ввода", "Кассанын буфери үчүн текст", "Text for the till clipboard or typing", "Kasa panosu için metin", "Kassa buferi uchun matn") };
    private readonly StackPanel _notes = new() { Spacing = 8 };
    private readonly TextBox _noteInput = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Watermark = L.T("Что случилось и что сделали…", "Эмне болду жана эмне жасалды…", "What happened and what was done…", "Ne oldu ve ne yapıldı…", "Nima bo'ldi va nima qilindi…") };
    private readonly TabControl _tabs = new();
    private TabItem? _chatTab;

    // ввод
    private readonly HashSet<string> _keysDown = new();
    private long _lastMove;
    private (int X, int Y)? _pendingMove;

    public SessionWindow(Api api, string? sessionId, string? code, string pin, string company, string machine, string user, string note)
    {
        _api = api;
        _sessionId = sessionId;
        _code = code;
        _pin = pin;
        _company = company;
        _machine = machine;
        _user = user;
        _note = note;
        Width = 1500;
        Height = 920;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Window;
        UpdateHeader(L.T("подключаюсь…", "туташып жатам…", "connecting…", "bağlanıyor…", "ulanmoqda…"));
        Content = new Panel { Children = { Build(), _toast } };
        Activated += (_, _) => Toast.Sink = _toast.Show;
        Opened += async (_, _) => await ConnectAsync();
        Closing += (_, _) => { _ = _client?.DisposeAsync().AsTask(); };
    }

    private static TextBox Mono() => new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas, Courier New, monospace"), FontSize = 12,
        Background = Ui.Soft,
    };

    private void UpdateHeader(string state)
    {
        Title = $"{(_company.Length > 0 ? _company : L.T("Касса", "Касса", "Till", "Kasa", "Kassa"))} · {_machine} — NurMarket {L.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam")}";
        _title.Text = $"{(_company.Length > 0 ? _company : L.T("Касса", "Касса", "Till", "Kasa", "Kassa"))}{(_machine.Length > 0 ? " · " + _machine : "")}";
        _subtitle.Text = (_user.Length > 0 ? L.T("кассир: ", "кассир: ", "cashier: ", "kasiyer: ", "kassir: ") + _user + " · " : "") + state + (_note.Length > 0 ? $" · «{_note}»" : "");
    }

    private void SetStatus(string? text)
    {
        _statusText.Text = text ?? "";
        _status.IsVisible = text != null;
    }

    // ------------------------------------------------------------------ разметка

    private Control Build()
    {
        // Шапка
        var viewOnly = new ToggleSwitch { OnContent = L.T("Только просмотр", "Көрүү гана", "View only", "Yalnızca izle", "Faqat ko'rish"), OffContent = L.T("Управление", "Башкаруу", "Control", "Kontrol", "Boshqaruv"), VerticalAlignment = VerticalAlignment.Center };
        viewOnly.IsCheckedChanged += async (_, _) =>
        {
            _viewOnly = viewOnly.IsChecked == true;
            ReleaseKeys();
            if (_live && _client != null)
                await SafeSend(new { type = "view-only", value = _viewOnly });
        };
        var quality = new ComboBox { VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
        foreach (var (q, name) in new[] { (30, L.T("Качество: низкое", "Сапат: төмөн", "Quality: low", "Kalite: düşük", "Sifat: past")), (60, L.T("Качество: среднее", "Сапат: орто", "Quality: medium", "Kalite: orta", "Sifat: o'rta")), (85, L.T("Качество: высокое", "Сапат: жогору", "Quality: high", "Kalite: yüksek", "Sifat: yuqori")) })
            quality.Items.Add(new ComboBoxItem { Content = name, Tag = q });
        quality.SelectedIndex = Settings.Current.Quality <= 40 ? 0 : Settings.Current.Quality >= 80 ? 2 : 1;
        quality.SelectionChanged += async (_, _) =>
        {
            if (quality.SelectedItem is ComboBoxItem { Tag: int q })
            {
                Settings.Current.Quality = q;
                Settings.Current.Save();
                if (_live)
                    await SafeSend(new { type = "quality", value = q });
            }
        };
        var keys = Ui.Btn(L.T("Клавиши ▾", "Баскычтар ▾", "Keys ▾", "Tuşlar ▾", "Tugmalar ▾"));
        var menu = new MenuFlyout();
        foreach (var (title, combo) in new[]
                 {
                     ("Ctrl+Shift+Esc — " + L.T("диспетчер задач", "тапшырмалар диспетчери", "task manager", "görev yöneticisi", "vazifalar dispetcheri"), new[] { "ControlLeft", "ShiftLeft", "Escape" }),
                     ("Alt+Tab", new[] { "AltLeft", "Tab" }),
                     ("Win", new[] { "MetaLeft" }),
                     ("Win+R — " + L.T("выполнить", "аткаруу", "run", "çalıştır", "bajarish"), new[] { "MetaLeft", "KeyR" }),
                     ("Win+D — " + L.T("рабочий стол", "иш столу", "desktop", "masaüstü", "ish stoli"), new[] { "MetaLeft", "KeyD" }),
                     ("Win+E — " + L.T("проводник", "өткөргүч", "explorer", "gezgin", "provodnik"), new[] { "MetaLeft", "KeyE" }),
                     ("Alt+F4", new[] { "AltLeft", "F4" }),
                     ("Ctrl+C", new[] { "ControlLeft", "KeyC" }),
                     ("Ctrl+V", new[] { "ControlLeft", "KeyV" }),
                     ("Ctrl+A", new[] { "ControlLeft", "KeyA" }),
                     ("Esc", new[] { "Escape" }),
                     ("F5", new[] { "F5" }),
                     ("PrintScreen", new[] { "PrintScreen" }),
                 })
        {
            var c = combo;
            menu.Items.Add(new MenuItem { Header = title, Command = new Relay(async () => await SendComboAsync(c)) });
        }
        keys.Flyout = menu;
        var fit = Ui.Btn(L.T("1:1", "1:1", "1:1", "1:1", "1:1"));
        fit.Click += (_, _) =>
        {
            _fit = !_fit;
            fit.Content = _fit ? "1:1" : L.T("Вписать", "Батыруу", "Fit", "Sığdır", "Sig'dirish");
            ApplyFit();
        };
        var full = Ui.Btn(L.T("На весь экран", "Толук экран", "Full screen", "Tam ekran", "To'liq ekran"));
        full.Click += (_, _) => WindowState = WindowState == WindowState.FullScreen ? WindowState.Maximized : WindowState.FullScreen;
        var end = Ui.BtnAsync(L.T("Завершить", "Аяктоо", "End", "Bitir", "Tugatish"), "danger", async () =>
        {
            if (_client != null)
                await _client.EndAsync();
            Close();
        });
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _stats, viewOnly, quality, keys, fit, full, end } };
        var head = new DockPanel { Background = Ui.Panel };
        DockPanel.SetDock(tools, Dock.Right);
        head.Children.Add(tools);
        head.Children.Add(new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } });
        var headBorder = new Border { Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 10), Child = head };

        // Экран
        _scroll.Content = _image;
        _overlay.Children.Add(_cursor);
        _status.Child = _statusText;
        _screen.Child = new Panel { Children = { _scroll, _overlay, _status } };
        AttachInput();

        // Инструменты справа
        _chatTab = Tab(L.T("Чат", "Чат", "Chat", "Sohbet", "Chat"), ChatPanel());
        _tabs.Items.Add(_chatTab);
        _tabs.Items.Add(Tab(L.T("Касса", "Касса", "Till", "Kasa", "Kassa"), InfoPanel()));
        _tabs.Items.Add(Tab(L.T("Диагностика", "Диагностика", "Diagnostics", "Tanılama", "Diagnostika"), DiagPanel()));
        _tabs.Items.Add(Tab(L.T("Журнал", "Журнал", "Log", "Günlük", "Jurnal"), LogsPanel()));
        _tabs.Items.Add(Tab(L.T("Файлы", "Файлдар", "Files", "Dosyalar", "Fayllar"), FilesPanel()));
        _tabs.Items.Add(Tab(L.T("Буфер", "Буфер", "Clipboard", "Pano", "Bufer"), ClipPanel()));
        _tabs.Items.Add(Tab(L.T("Заметки", "Эскертмелер", "Notes", "Notlar", "Eslatmalar"), NotesPanel()));
        var side = new Border { Background = Ui.Panel, BorderBrush = Ui.Border, BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(10, 6), Child = _tabs };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,440") };
        body.Children.Add(_screen);
        Grid.SetColumn(side, 1);
        body.Children.Add(side);
        var root = new DockPanel();
        DockPanel.SetDock(headBorder, Dock.Top);
        root.Children.Add(headBorder);
        root.Children.Add(body);
        return root;
    }

    private static TabItem Tab(string header, Control content) => new() { Header = new TextBlock { Text = header, FontSize = 12.5, FontWeight = FontWeight.SemiBold }, Content = content, Padding = new Thickness(7, 2), MinHeight = 34 };

    private void ApplyFit()
    {
        _image.Stretch = _fit ? Stretch.Uniform : Stretch.None;
        var vis = _fit ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = vis;
        _scroll.VerticalScrollBarVisibility = vis;
        Dispatcher.UIThread.Post(PlaceCursor, DispatcherPriority.Background);
    }

    private Control ChatPanel()
    {
        _chatScroll.Content = _chatList;
        var input = new TextBox { Watermark = L.T("Сообщение кассиру…", "Кассирге билдирүү…", "Message to the cashier…", "Kasiyere mesaj…", "Kassirga xabar…"), AcceptsReturn = false, TextWrapping = TextWrapping.Wrap };
        async Task Send()
        {
            var text = (input.Text ?? "").Trim();
            if (text.Length == 0 || !_live)
                return;
            input.Text = "";
            await SafeSend(new { type = "chat", text });
            AddChat(true, text);
        }
        input.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Send();
            }
        };
        var send = Ui.BtnAsync(L.T("Отправить", "Жөнөтүү", "Send", "Gönder", "Yuborish"), "primary", Send);
        var quick = new WrapPanel();
        foreach (var phrase in new[]
                 {
                     L.T("Здравствуйте! Я подключился, сейчас посмотрю.", "Саламатсызбы! Туташтым, азыр карайм.", "Hello! I'm connected, taking a look now.", "Merhaba! Bağlandım, şimdi bakıyorum.", "Assalomu alaykum! Ulandim, hozir ko'raman."),
                     L.T("Пожалуйста, не трогайте мышь пару минут.", "Сураныч, бир нече мүнөт чычканга тийбеңиз.", "Please don't touch the mouse for a couple of minutes.", "Lütfen birkaç dakika fareye dokunmayın.", "Iltimos, bir necha daqiqa sichqonchaga tegmang."),
                     L.T("Готово, проверьте, пожалуйста.", "Даяр, текшерип көрүңүз.", "Done, please check.", "Tamam, lütfen kontrol edin.", "Tayyor, tekshirib ko'ring."),
                 })
        {
            var text = phrase;
            var b = Ui.Btn(text.Length > 26 ? text[..26] + "…" : text, "secondary", () => input.Text = text);
            b.FontSize = 11;
            b.Padding = new Thickness(8, 4);
            b.Margin = new Thickness(0, 0, 4, 4);
            ToolTip.SetTip(b, text);
            quick.Children.Add(b);
        }
        var bottom = new StackPanel { Spacing = 6, Children = { quick, new DockPanel { Children = { send, input } } } };
        DockPanel.SetDock(send, Dock.Right);
        send.Margin = new Thickness(6, 0, 0, 0);
        var dock = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(_chatScroll);
        return dock;
    }

    private void AddChat(bool mine, string text)
    {
        _chatList.Children.Add(new Border
        {
            Background = mine ? Ui.AccentSoft : Ui.InfoSoft, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6), MaxWidth = 320,
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text },
                    new TextBlock { Text = (mine ? L.T("вы", "сиз", "you", "siz", "siz") : L.T("кассир", "кассир", "cashier", "kasiyer", "kassir")) + " · " + DateTime.Now.ToString("HH:mm"), FontSize = 10, Foreground = Ui.TextSoft },
                },
            },
        });
        _chatScroll.ScrollToEnd();
    }

    private Control InfoPanel() => ToolPanel(_info, Ui.BtnAsync(L.T("Обновить сведения", "Маалыматты жаңыртуу", "Refresh info", "Bilgiyi yenile", "Ma'lumotni yangilash"), "secondary",
        () => SafeSend(new { type = "info-request" })));

    private Control DiagPanel()
    {
        var run = Ui.BtnAsync(L.T("Проверить кассу", "Кассаны текшерүү", "Check the till", "Kasayı kontrol et", "Kassani tekshirish"), "primary", async () =>
        {
            _diag.Children.Clear();
            _diag.Children.Add(Ui.SoftText(L.T("Проверяю… (до 10 с)", "Текшерип жатам…", "Checking… (up to 10 s)", "Kontrol ediliyor…", "Tekshirilmoqda…")));
            await SafeSend(new { type = "diag-request" });
        });
        var dock = new DockPanel();
        DockPanel.SetDock(run, Dock.Top);
        run.Margin = new Thickness(0, 6, 0, 8);
        dock.Children.Add(run);
        dock.Children.Add(new ScrollViewer { Content = _diag });
        return dock;
    }

    private Control LogsPanel()
    {
        var b300 = Ui.BtnAsync(L.T("300 строк", "300 сап", "300 lines", "300 satır", "300 qator"), "secondary", () => SafeSend(new { type = "logs-request", lines = 300 }));
        var b2000 = Ui.BtnAsync(L.T("2000 строк", "2000 сап", "2000 lines", "2000 satır", "2000 qator"), "secondary", () => SafeSend(new { type = "logs-request", lines = 2000 }));
        var zip = Ui.BtnAsync(L.T("Скачать все (zip)", "Баарын жүктөө (zip)", "Download all (zip)", "Tümünü indir (zip)", "Hammasini yuklash (zip)"), "primary", async () =>
        {
            await SafeSend(new { type = "logs-zip-request" });
            _toast.Show(L.T("Касса собирает журналы — файл сохранится в «Загрузки\\NurMarketSupport».", "Касса журналдарды чогултууда.", "The till is packing its logs — the file goes to “Downloads\\NurMarketSupport”.",
                "Kasa günlükleri topluyor.", "Kassa jurnallarni yig'moqda."), false);
        });
        var bar = new WrapPanel { Margin = new Thickness(0, 6, 0, 8), Children = { b300, b2000, zip } };
        foreach (var c in bar.Children)
            c.Margin = new Thickness(0, 0, 6, 6);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(_logs);
        return dock;
    }

    private Control FilesPanel()
    {
        var progress = new ProgressBar { Minimum = 0, Maximum = 1, Height = 6, IsVisible = false };
        var send = Ui.BtnAsync(L.T("Отправить файл кассиру…", "Кассирге файл жөнөтүү…", "Send a file to the cashier…", "Kasiyere dosya gönder…", "Kassirga fayl yuborish…"), "primary", async () =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, Title = L.T("Файл для кассы", "Касса үчүн файл", "File for the till", "Kasa için dosya", "Kassa uchun fayl") });
            var path = picked.FirstOrDefault()?.TryGetLocalPath();
            if (path == null || _client == null || !_live)
                return;
            progress.IsVisible = true;
            progress.Value = 0;
            try
            {
                await _client.SendFileAsync(path, new Progress<double>(v => progress.Value = v));
                AddFileLine("→ " + System.IO.Path.GetFileName(path) + " — " + L.T("отправлен", "жөнөтүлдү", "sent", "gönderildi", "yuborildi"));
            }
            finally
            {
                progress.IsVisible = false;
            }
        });
        var open = Ui.Btn(L.T("Папка полученных файлов", "Алынган файлдар папкасы", "Received files folder", "Alınan dosyalar klasörü", "Olingan fayllar papkasi"), "secondary", () => OpenFolder(DownloadDir()));
        var top = new StackPanel
        {
            Spacing = 6, Margin = new Thickness(0, 6, 0, 8),
            Children =
            {
                Ui.SoftText(L.T("Файл попадёт на кассу в «Загрузки\\NurMarket поддержка», кассир увидит уведомление. До 100 МБ. Сам файл не запускается.",
                    "Файл кассадагы «Жүктөлүүлөр\\NurMarket поддержка» папкасына түшөт. 100 МБга чейин.", "The file lands in “Downloads\\NurMarket поддержка” on the till; the cashier is notified. Up to 100 MB. It is not run.",
                    "Dosya kasada «İndirilenler\\NurMarket поддержка» klasörüne gider. 100 MB'a kadar.", "Fayl kassada «Yuklanmalar\\NurMarket поддержка» papkasiga tushadi. 100 MB gacha.")),
                send, progress, open,
            },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = _files });
        return dock;
    }

    private void AddFileLine(string text) => _files.Children.Insert(0, new TextBlock { Text = DateTime.Now.ToString("HH:mm") + "  " + text, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text });

    private Control ClipPanel()
    {
        var set = Ui.BtnAsync(L.T("Положить в буфер кассы", "Кассанын буферине салуу", "Put into the till clipboard", "Kasa panosuna koy", "Kassa buferiga qo'yish"), "primary", async () =>
        {
            await SafeSend(new { type = "clipboard-set", text = _clip.Text ?? "" });
            _toast.Show(L.T("Готово — на кассе можно вставить Ctrl+V.", "Даяр — кассада Ctrl+V.", "Done — paste on the till with Ctrl+V.", "Tamam — kasada Ctrl+V.", "Tayyor — kassada Ctrl+V."), false);
        });
        var type = Ui.BtnAsync(L.T("Напечатать на кассе", "Кассада терүү", "Type on the till", "Kasada yaz", "Kassada yozish"), "secondary",
            () => SafeSend(new { type = "text", text = _clip.Text ?? "" }));
        ToolTip.SetTip(type, L.T("Текст печатается туда, где сейчас курсор на кассе (кириллица тоже).", "Текст кассадагы курсор турган жерге терилет.", "Typed where the cursor is on the till (Cyrillic too).",
            "İmlecin olduğu yere yazılır.", "Kursor turgan joyga yoziladi."));
        var get = Ui.BtnAsync(L.T("Взять буфер кассы", "Кассанын буферин алуу", "Get the till clipboard", "Kasa panosunu al", "Kassa buferini olish"), "secondary",
            () => SafeSend(new { type = "clipboard-get" }));
        var mine = Ui.Btn(L.T("Скопировать себе", "Өзүмө көчүрүү", "Copy to my clipboard", "Kendime kopyala", "O'zimga nusxalash"), "ghost", async () =>
        {
            if (Clipboard != null)
                await Clipboard.SetTextAsync(_clip.Text ?? "");
        });
        var bar = new WrapPanel { Children = { set, type, get, mine } };
        foreach (var c in bar.Children)
            c.Margin = new Thickness(0, 0, 6, 6);
        return new StackPanel { Spacing = 8, Margin = new Thickness(0, 6, 0, 0), Children = { _clip, bar } };
    }

    private Control NotesPanel()
    {
        var save = Ui.BtnAsync(L.T("Сохранить заметку", "Эскертмени сактоо", "Save note", "Notu kaydet", "Eslatmani saqlash"), "primary", async () =>
        {
            var text = (_noteInput.Text ?? "").Trim();
            if (text.Length == 0)
                return;
            await _api.AddNoteAsync(_company, _machine, _serverSessionId, text);
            _noteInput.Text = "";
            _toast.Show(L.T("Заметка сохранена — её увидят все операторы.", "Эскертме сакталды.", "Note saved — all operators will see it.", "Not kaydedildi.", "Eslatma saqlandi."), false);
            await LoadNotesAsync();
        });
        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 8), Children = { _noteInput, save, Ui.Caption(L.T("Прошлые обращения этой кассы", "Бул кассанын мурунку кайрылуулары", "Earlier cases of this till", "Bu kasanın önceki talepleri", "Bu kassaning oldingi murojaatlari")) } };
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = _notes });
        return dock;
    }

    private async Task LoadNotesAsync()
    {
        try
        {
            var list = await _api.NotesAsync(_company, _machine, 50);
            _notes.Children.Clear();
            if (list.Count == 0)
                _notes.Children.Add(Ui.SoftText(L.T("Раньше по этой кассе заметок не было.", "Мурун бул касса боюнча эскертме болгон эмес.", "No earlier notes for this till.", "Bu kasa için önceki not yok.", "Bu kassa bo'yicha oldin eslatma bo'lmagan.")));
            foreach (var n in list)
                _notes.Children.Add(MainWindow.NoteCard(n));
        }
        catch (Exception ex)
        {
            _notes.Children.Clear();
            _notes.Children.Add(Ui.SoftText(ex.Message));
        }
    }

    private static Control ToolPanel(Control content, Control button)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(button, Dock.Top);
        button.Margin = new Thickness(0, 6, 0, 8);
        dock.Children.Add(button);
        dock.Children.Add(content);
        return dock;
    }

    // ------------------------------------------------------------------ соединение

    private async Task ConnectAsync()
    {
        SetStatus(L.T("Подключаюсь к серверу поддержки…", "Колдоо серверине туташып жатам…", "Connecting to the support server…", "Destek sunucusuna bağlanılıyor…", "Yordam serveriga ulanmoqda…"));
        var c = new SessionClient(_api.OperatorWs, _api.Token);
        _client = c;
        c.Error += (code, left) => Dispatcher.UIThread.Post(async () => await OnErrorAsync(code, left));
        c.WaitingConsent += () => Dispatcher.UIThread.Post(() =>
        {
            UpdateHeader(L.T("ждём «Разрешить» на кассе", "кассада «Уруксат берүү» күтүлүүдө", "waiting for “Allow” on the till", "kasada «İzin ver» bekleniyor", "kassada «Ruxsat berish» kutilmoqda"));
            SetStatus(L.T("PIN верный. Ждём, когда кассир нажмёт «Разрешить» (до 60 секунд)…", "PIN туура. Кассир «Уруксат берүү» басканын күтөбүз…",
                "The PIN is correct. Waiting for the cashier to press “Allow” (up to 60 seconds)…", "PIN doğru. Kasiyerin «İzin ver»e basması bekleniyor…",
                "PIN to'g'ri. Kassir «Ruxsat berish»ni bosishini kutyapmiz…"));
        });
        c.Started += m => Dispatcher.UIThread.Post(() => OnStarted(m));
        c.ScreenSize += (w, h) => Dispatcher.UIThread.Post(() =>
        {
            _bitmap = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            _image.Source = _bitmap;
        });
        c.FrameReady += (x, y, w, h, frame) => Dispatcher.UIThread.Post(() => DrawFrame(x, y, w, h, frame), DispatcherPriority.Render);
        c.Cursor += (x, y) => Dispatcher.UIThread.Post(() =>
        {
            _cx = x;
            _cy = y;
            PlaceCursor();
        });
        c.Chat += text => Dispatcher.UIThread.Post(() =>
        {
            AddChat(false, text);
            if (_tabs.SelectedItem != _chatTab)
                _toast.Show(L.T("Кассир: ", "Кассир: ", "Cashier: ", "Kasiyer: ", "Kassir: ") + text, false);
        });
        c.Info += text => Dispatcher.UIThread.Post(() => _info.Text = text);
        c.Logs += text => Dispatcher.UIThread.Post(() =>
        {
            _logs.Text = text;
            _logs.CaretIndex = text.Length;
        });
        c.Diag += items => Dispatcher.UIThread.Post(() => RenderDiag(items));
        c.Clipboard += text => Dispatcher.UIThread.Post(() =>
        {
            _clip.Text = text;
            _toast.Show(L.T("Буфер кассы получен.", "Кассанын буфери алынды.", "Got the till clipboard.", "Kasa panosu alındı.", "Kassa buferi olindi."), false);
        });
        c.FileFromHost += (name, bytes) => Dispatcher.UIThread.Post(() => SaveFromHost(name, bytes));
        c.FileStatus += (_, status, detail) => Dispatcher.UIThread.Post(() =>
            AddFileLine(status == "saved"
                ? "✓ " + L.T("касса сохранила: ", "касса сактады: ", "saved on the till: ", "kasa kaydetti: ", "kassa saqladi: ") + detail
                : "✗ " + L.T("касса не приняла файл: ", "касса файлды кабыл алган жок: ", "the till rejected the file: ", "kasa dosyayı kabul etmedi: ", "kassa faylni qabul qilmadi: ") + detail));
        c.Stats += (fps, kbps) => Dispatcher.UIThread.Post(() => _stats.Text = $"{fps:0} {L.T("к/с", "к/с", "fps", "fps", "k/s")} · {kbps / 1000:0.0} {L.T("Мбит/с", "Мбит/с", "Mbit/s", "Mbit/s", "Mbit/s")}");
        c.Ended += reason => Dispatcher.UIThread.Post(() => OnEnded(reason));
        try
        {
            await c.ConnectAsync();
            await Join();
        }
        catch (Exception ex)
        {
            SetStatus(L.T("Нет связи с сервером поддержки: ", "Колдоо сервери менен байланыш жок: ", "No connection to the support server: ", "Destek sunucusuna bağlantı yok: ", "Server bilan aloqa yo'q: ") + ex.Message);
        }
    }

    private Task Join() => _sessionId != null ? _client!.JoinBySessionAsync(_sessionId, _pin) : _client!.JoinByCodeAsync(_code!, _pin);

    private async Task OnErrorAsync(string code, int? left)
    {
        if (code == "bad-pin")
        {
            var pin = await Dialogs.AskAsync(this, L.T("Неверный PIN", "PIN туура эмес", "Wrong PIN", "Yanlış PIN", "Noto'g'ri PIN"),
                L.T($"Попросите кассира назвать PIN ещё раз. Осталось попыток: {left}.", $"Кассирден PIN'ди кайра сураңыз. Калган аракет: {left}.", $"Ask the cashier for the PIN again. Attempts left: {left}.",
                    $"Kasiyerden PIN'i tekrar isteyin. Kalan deneme: {left}.", $"Kassirdan PIN'ni qayta so'rang. Qolgan urinish: {left}."), "0000", digits: 4);
            if (pin == null)
            {
                Close();
                return;
            }
            _pin = pin;
            await Join();
            return;
        }
        SetStatus(code switch
        {
            "no-session" => L.T("Такой кассы нет или код уже использован. Попросите кассира получить новый код.", "Мындай касса жок же код колдонулган.", "No such till, or the code was already used. Ask for a new code.", "Böyle kasa yok veya kod kullanıldı.", "Bunday kassa yo'q yoki kod ishlatilgan."),
            "locked" => L.T("5 раз неверный PIN — касса заблокировала код. Кассиру нужно получить новый.", "PIN 5 жолу туура эмес — код бөгөттөлдү.", "Wrong PIN 5 times — the code is locked. The cashier needs a new one.", "5 kez yanlış PIN — kod kilitlendi.", "PIN 5 marta noto'g'ri — kod bloklandi."),
            "busy" => L.T("К этой кассе уже подключается другой оператор.", "Бул кассага башка оператор туташууда.", "Another operator is already connecting to this till.", "Bu kasaya başka operatör bağlanıyor.", "Bu kassaga boshqa operator ulanmoqda."),
            "declined" => L.T("Кассир отказал в доступе (или не ответил).", "Кассир уруксат берген жок.", "The cashier declined (or did not answer).", "Kasiyer reddetti.", "Kassir rad etdi."),
            "expired" => L.T("Код устарел (15 минут). Попросите новый.", "Код эскирди (15 мүнөт).", "The code expired (15 minutes). Ask for a new one.", "Kodun süresi doldu.", "Kod eskirdi."),
            "rate-limited" => L.T("Слишком много попыток — подождите минуту.", "Өтө көп аракет — бир мүнөт күтүңүз.", "Too many attempts — wait a minute.", "Çok fazla deneme — bir dakika bekleyin.", "Juda ko'p urinish — bir daqiqa kuting."),
            _ => L.T("Сервер ответил: ", "Сервер жооп берди: ", "The server replied: ", "Sunucu yanıtı: ", "Server javobi: ") + code,
        });
    }

    private void OnStarted(JsonElement m)
    {
        _live = true;
        _serverSessionId = MainWindow.Str(m, "sessionId");
        if (m.TryGetProperty("host", out var h))
        {
            if (MainWindow.Str(h, "company") is { Length: > 0 } company)
                _company = company;
            if (MainWindow.Str(h, "machine") is { Length: > 0 } machine)
                _machine = machine;
        }
        UpdateHeader(L.T("сеанс идёт", "сеанс жүрүп жатат", "session live", "oturum canlı", "seans davom etmoqda") + (h.ValueKind == JsonValueKind.Object ? " · NurMarket " + MainWindow.Str(h, "version") : ""));
        SetStatus(null);
        _screen.Focus();
        _ = SafeSend(new { type = "quality", value = Settings.Current.Quality });
        _ = SafeSend(new { type = "info-request" });
        _ = SafeSend(new { type = "diag-request" });
        _ = LoadNotesAsync();
    }

    private void OnEnded(string reason)
    {
        _live = false;
        ReleaseKeys();
        UpdateHeader(L.T("сеанс завершён", "сеанс аяктады", "session ended", "oturum bitti", "seans tugadi"));
        if (!IsVisible)
            return;
        SetStatus(L.T("Сеанс завершён", "Сеанс аяктады", "The session has ended", "Oturum sona erdi", "Seans tugadi") + reason switch
        {
            "host" => L.T(": кассир нажал «Завершить».", ": кассир «Аяктоо» басты.", ": the cashier pressed “End”.", ": kasiyer «Bitir»e bastı.", ": kassir «Tugatish»ni bosdi."),
            "operator" => ".",
            "timeout" or "disconnect" => L.T(": связь прервалась.", ": байланыш үзүлдү.", ": the connection was lost.", ": bağlantı koptu.", ": aloqa uzildi."),
            "declined" => L.T(": кассир отказал.", ": кассир баш тартты.", ": the cashier declined.", ": kasiyer reddetti.", ": kassir rad etdi."),
            "consent-timeout" => L.T(": кассир не ответил.", ": кассир жооп берген жок.", ": no answer from the cashier.", ": kasiyer yanıt vermedi.", ": kassir javob bermadi."),
            _ => $" ({reason}).",
        } + "\n" + L.T("Запишите итог во вкладке «Заметки».", "Жыйынтыкты «Эскертмелер» өтмөгүнө жазыңыз.", "Write the outcome in the “Notes” tab.", "Sonucu «Notlar» sekmesine yazın.", "Natijani «Eslatmalar» bo'limiga yozing."));
        if (_tabs.Items.Count > 6)
            _tabs.SelectedIndex = 6;
    }

    private async Task SafeSend(object o)
    {
        if (_client == null || !_client.IsOpen)
            return;
        try
        {
            await _client.SendAsync(o);
        }
        catch (Exception ex)
        {
            _toast.Show(ex.Message, true);
        }
    }

    private void RenderDiag(List<(string Name, bool? Ok, string Detail)> items)
    {
        _diag.Children.Clear();
        foreach (var (name, ok, detail) in items)
        {
            var (mark, fg, bg) = ok switch
            {
                true => ("✓", Ui.Success, Ui.SuccessSoft),
                false => ("✗", Ui.Danger, Ui.DangerSoft),
                _ => ("•", Ui.TextMid, Ui.Soft),
            };
            _diag.Children.Add(new Border
            {
                Background = bg, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7),
                Child = new DockPanel
                {
                    Children =
                    {
                        new TextBlock { Text = mark, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = fg, Width = 22, [DockPanel.DockProperty] = Dock.Left },
                        new StackPanel { Children = { new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, Foreground = Ui.Text }, new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Foreground = Ui.TextMid, FontSize = 12 } } },
                    },
                },
            });
        }
    }

    private static string DownloadDir() => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "NurMarketSupport");

    private void SaveFromHost(string name, byte[] bytes)
    {
        try
        {
            var dir = DownloadDir();
            Directory.CreateDirectory(dir);
            var safe = string.Concat(System.IO.Path.GetFileName(name).Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var path = System.IO.Path.Combine(dir, safe.Length > 0 ? safe : "file");
            File.WriteAllBytes(path, bytes);
            AddFileLine("← " + safe + $" ({bytes.Length / 1024} КБ) — " + L.T("сохранён", "сакталды", "saved", "kaydedildi", "saqlandi"));
            _toast.Show(L.T("Файл с кассы сохранён: ", "Кассадан файл сакталды: ", "File from the till saved: ", "Kasadan dosya kaydedildi: ", "Kassadan fayl saqlandi: ") + path, false);
        }
        catch (Exception ex)
        {
            _toast.Show(ex.Message, true);
        }
    }

    private static void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // проводник недоступен
        }
    }

    // ------------------------------------------------------------------ экран

    private void DrawFrame(int x, int y, int w, int h, uint frame)
    {
        if (_bitmap is { } bmp && _client is { } c)
        {
            using (var fb = bmp.Lock())
            {
                lock (c.FrameLock)
                {
                    if (c.W == bmp.PixelSize.Width && c.H == bmp.PixelSize.Height)
                    {
                        for (var row = y; row < y + h; row++)
                            System.Runtime.InteropServices.Marshal.Copy(c.Frame, (row * c.W + x) * 4, fb.Address + row * fb.RowBytes + x * 4, w * 4);
                    }
                }
            }
            _image.InvalidateVisual();
        }
        _ = SafeSend(new { type = "ack", frame });
    }

    /// <summary>Курсор кассы (на снимке экрана его нет) — стрелка поверх картинки.</summary>
    private void PlaceCursor()
    {
        if (_client is not { W: > 0 } c || _cx < 0 || _image.Bounds.Width <= 0)
        {
            _cursor.IsVisible = false;
            return;
        }
        var origin = _image.TranslatePoint(new Point(0, 0), _overlay) ?? new Point(0, 0);
        var sx = _image.Bounds.Width / c.W;
        var sy = _image.Bounds.Height / c.H;
        Canvas.SetLeft(_cursor, origin.X + _cx * sx);
        Canvas.SetTop(_cursor, origin.Y + _cy * sy);
        _cursor.IsVisible = true;
    }

    private (int X, int Y)? HostPoint(PointerEventArgs e)
    {
        if (_client is not { W: > 0 } c || _image.Bounds.Width <= 0 || _image.Bounds.Height <= 0)
            return null;
        var p = e.GetPosition(_image);
        var x = (int)(p.X * c.W / _image.Bounds.Width);
        var y = (int)(p.Y * c.H / _image.Bounds.Height);
        if (x < 0 || y < 0 || x >= c.W || y >= c.H)
            return null;
        return (x, y);
    }

    private bool CanControl => _live && !_viewOnly;

    private void AttachInput()
    {
        _image.PointerMoved += (_, e) =>
        {
            if (!CanControl || HostPoint(e) is not { } p)
                return;
            // Не чаще ~60 раз в секунду; последнее положение не теряется.
            _pendingMove = p;
            var now = Environment.TickCount64;
            if (now - _lastMove < 16)
                return;
            _lastMove = now;
            FlushMove();
        };
        _image.PointerPressed += (_, e) =>
        {
            _screen.Focus();
            if (!CanControl || HostPoint(e) is not { } p)
                return;
            FlushMove();
            var button = e.GetCurrentPoint(_image).Properties.PointerUpdateKind switch
            {
                PointerUpdateKind.RightButtonPressed => 2,
                PointerUpdateKind.MiddleButtonPressed => 1,
                _ => 0,
            };
            _ = SafeSend(new { type = "mouse", action = "down", x = p.X, y = p.Y, button });
            e.Handled = true;
        };
        _image.PointerReleased += (_, e) =>
        {
            if (!CanControl || HostPoint(e) is not { } p)
                return;
            var button = e.InitialPressMouseButton switch { MouseButton.Right => 2, MouseButton.Middle => 1, _ => 0 };
            _ = SafeSend(new { type = "mouse", action = "up", x = p.X, y = p.Y, button });
            e.Handled = true;
        };
        _image.PointerWheelChanged += (_, e) =>
        {
            if (!CanControl || HostPoint(e) is not { } p)
                return;
            _ = SafeSend(new { type = "mouse", action = "wheel", x = p.X, y = p.Y, delta = (int)Math.Round(e.Delta.Y * 120) });
            e.Handled = true;
        };
        _image.LayoutUpdated += (_, _) => PlaceCursor();
        _screen.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!CanControl)
                return;
            e.Handled = true;
            if (Code(e) is not { } code)
                return;
            _keysDown.Add(code);
            _ = SafeSend(new { type = "key", action = "down", code, key = e.KeySymbol ?? "" });
        }, RoutingStrategies.Tunnel);
        _screen.AddHandler(KeyUpEvent, (_, e) =>
        {
            if (!CanControl)
                return;
            e.Handled = true;
            if (Code(e) is not { } code)
                return;
            _keysDown.Remove(code);
            _ = SafeSend(new { type = "key", action = "up", code, key = e.KeySymbol ?? "" });
        }, RoutingStrategies.Tunnel);
        _screen.LostFocus += (_, _) => ReleaseKeys();
        Deactivated += (_, _) => ReleaseKeys();
    }

    private void FlushMove()
    {
        if (_pendingMove is not { } p)
            return;
        _pendingMove = null;
        _ = SafeSend(new { type = "mouse", action = "move", x = p.X, y = p.Y });
    }

    /// <summary>Физическая клавиша Avalonia → KeyboardEvent.code (что понимает касса: KeyA, Digit1, Numpad1, ArrowLeft…).</summary>
    private static string? Code(KeyEventArgs e)
    {
        var name = e.PhysicalKey.ToString();
        if (e.PhysicalKey == PhysicalKey.None)
            return null;
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0]))
            return "Key" + name;
        if (name.StartsWith("NumPad", StringComparison.Ordinal))
            return "Numpad" + name[6..];
        return name;
    }

    /// <summary>Потеря фокуса или «только просмотр» — отпустить на кассе всё, что держали нажатым.</summary>
    private void ReleaseKeys()
    {
        if (_keysDown.Count == 0)
            return;
        foreach (var code in _keysDown.ToList())
            _ = SafeSend(new { type = "key", action = "up", code, key = "" });
        _keysDown.Clear();
    }

    private async Task SendComboAsync(string[] combo)
    {
        if (!CanControl)
        {
            _toast.Show(L.T("Включено «Только просмотр» или сеанс не идёт.", "«Көрүү гана» күйгүзүлгөн.", "View only is on, or there is no live session.", "Yalnızca izle açık.", "«Faqat ko'rish» yoqilgan."), true);
            return;
        }
        foreach (var code in combo)
            await SafeSend(new { type = "key", action = "down", code, key = "" });
        await Task.Delay(60);
        foreach (var code in combo.Reverse())
            await SafeSend(new { type = "key", action = "up", code, key = "" });
        _screen.Focus();
    }
}

/// <summary>Команда для пунктов меню.</summary>
internal sealed class Relay(Func<Task> run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public async void Execute(object? parameter) => await run();
}
