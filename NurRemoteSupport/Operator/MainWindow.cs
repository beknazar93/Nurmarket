using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NurRemoteSupport.Operator;

/// <summary>Главное окно оператора: «Ждут помощи» (кассы, нажавшие «Получить код», с именем кассира и описанием проблемы —
/// подключение одним PIN, обновление каждые 3 с, сигнал о новой кассе), «Идут сейчас», «По коду», «Журнал» (все сеансы:
/// кто, к какой кассе, сколько длилось, чем кончилось + заметки по кассе), «Заметки» (история обращений всех операторов).</summary>
internal sealed class MainWindow : Window
{
    private readonly Api _api;
    private readonly ContentControl _page = new();
    private readonly StackPanel _nav = new() { Spacing = 2 };
    private readonly ToastHost _toast = new();
    private string _current = "queue";
    private JsonElement? _sessions;
    private readonly HashSet<string> _knownPending = new();
    private bool _firstLoad = true;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _journalQuery = "";
    private string _notesQuery = "";

    public MainWindow(Api api)
    {
        _api = api;
        Width = 1280;
        Height = 820;
        MinWidth = 960;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Window;
        UpdateTitle(0);
        var side = BuildSide();
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        body.Children.Add(side);
        var right = new Border { Padding = new Thickness(26, 22), Child = _page };
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
        Content = new Panel { Children = { body, _toast } };
        Activated += (_, _) => Toast.Sink = _toast.Show;
        _timer.Tick += async (_, _) => await RefreshAsync();
        Opened += async (_, _) =>
        {
            Toast.Sink = _toast.Show;
            Show("queue");
            await RefreshAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateTitle(int waiting) =>
        Title = (waiting > 0 ? $"({waiting}) " : "") + "NurMarket " + L.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam") + " — " + _api.Name;

    // ------------------------------------------------------------------ меню

    private Control BuildSide()
    {
        var who = new StackPanel
        {
            Margin = new Thickness(16, 18, 16, 18),
            Children =
            {
                new TextBlock { Text = "NurMarket", FontSize = 20, FontWeight = FontWeight.ExtraBold, Foreground = Brushes.White },
                Ui.Pill(L.T("поддержка", "колдоо", "support", "destek", "yordam"), Ui.Accent, Ui.Navy),
                new TextBlock { Text = _api.Name, Foreground = Brushes.White, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = _api.Base.Host, Foreground = Ui.B("#9FB3C2"), FontSize = 12 },
            },
        };
        who.Children[1].HorizontalAlignment = HorizontalAlignment.Left;
        who.Children[1].Margin = new Thickness(0, 6, 0, 0);
        RenderNav();
        var logout = Ui.Btn(L.T("Выйти", "Чыгуу", "Sign out", "Çıkış", "Chiqish"), "nav", () =>
        {
            new LoginWindow().Show();
            Close();
        });
        logout.HorizontalAlignment = HorizontalAlignment.Stretch;
        logout.HorizontalContentAlignment = HorizontalAlignment.Left;
        var dock = new DockPanel { Background = Ui.Navy };
        DockPanel.SetDock(who, Dock.Top);
        DockPanel.SetDock(logout, Dock.Bottom);
        logout.Margin = new Thickness(8, 0, 8, 12);
        dock.Children.Add(who);
        dock.Children.Add(logout);
        dock.Children.Add(new ScrollViewer { Content = _nav, Margin = new Thickness(8, 0) });
        return dock;
    }

    private void RenderNav()
    {
        _nav.Children.Clear();
        var waiting = Pending().Count;
        foreach (var (id, text) in new[]
                 {
                     ("queue", L.T("Ждут помощи", "Жардам күтүшөт", "Waiting for help", "Yardım bekleyenler", "Yordam kutmoqda") + (waiting > 0 ? $"  ({waiting})" : "")),
                     ("active", L.T("Идут сейчас", "Азыр жүрүп жатат", "In progress", "Devam edenler", "Hozir davom etmoqda")),
                     ("code", L.T("Подключиться по коду", "Код менен туташуу", "Connect by code", "Kodla bağlan", "Kod bilan ulanish")),
                     ("journal", L.T("Журнал сеансов", "Сеанстар журналы", "Session log", "Oturum günlüğü", "Seanslar jurnali")),
                     ("notes", L.T("Заметки и обращения", "Эскертмелер жана кайрылуулар", "Notes and cases", "Notlar ve talepler", "Eslatmalar va murojaatlar")),
                 })
        {
            var key = id;
            var b = Ui.Btn(text, "nav", () => Show(key));
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (_current == key)
            {
                b.Resources["ButtonBackground"] = Ui.Accent;
                b.Resources["ButtonBackgroundPointerOver"] = Ui.Accent;
                b.Resources["ButtonForeground"] = Ui.Navy;
                b.Resources["ButtonForegroundPointerOver"] = Ui.Navy;
            }
            _nav.Children.Add(b);
        }
    }

    private void Show(string page)
    {
        _current = page;
        RenderNav();
        _page.Content = page switch
        {
            "queue" => QueuePage(),
            "active" => ActivePage(),
            "code" => CodePage(),
            "journal" => JournalPage(),
            "notes" => NotesPage(),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ данные

    private async Task RefreshAsync()
    {
        try
        {
            _sessions = await _api.SessionsAsync();
        }
        catch (Exception ex)
        {
            if (_current is "queue" or "active")
                _toast.Show(ex.Message, true);
            return;
        }
        var pending = Pending();
        UpdateTitle(pending.Count);
        var fresh = pending.Select(p => Str(p, "sessionId")).Where(id => !_knownPending.Contains(id)).ToList();
        if (!_firstLoad && fresh.Count > 0)
        {
            var first = pending.First(p => Str(p, "sessionId") == fresh[0]);
            _toast.Show(L.T("Новая касса ждёт помощи: ", "Жаңы касса жардам күтөт: ", "A till is waiting for help: ", "Yardım bekleyen kasa: ", "Yangi kassa yordam kutmoqda: ")
                        + Str(first, "company") + " · " + Str(first, "machine"), false);
            _ = Task.Run(() =>
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                        Console.Beep(880, 180);
                }
                catch
                {
                    // нет звука — не страшно
                }
            });
        }
        _firstLoad = false;
        _knownPending.Clear();
        foreach (var p in pending)
            _knownPending.Add(Str(p, "sessionId"));
        RenderNav();
        if (_current is "queue" or "active")
            Show(_current);
    }

    private List<JsonElement> Active() =>
        _sessions is { } s && s.TryGetProperty("active", out var a) ? a.EnumerateArray().ToList() : new();

    private List<JsonElement> Pending() => Active().Where(e => Str(e, "state") == "pending").ToList();

    private List<JsonElement> Recent() =>
        _sessions is { } s && s.TryGetProperty("recent", out var a) ? a.EnumerateArray().ToList() : new();

    public static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static DateTimeOffset? Date(JsonElement e, string k) =>
        e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    // ------------------------------------------------------------------ страницы

    private static Control Page(string title, string subtitle, Control body, Control? action = null)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        if (action != null)
        {
            DockPanel.SetDock(action, Dock.Right);
            head.Children.Add(action);
        }
        head.Children.Add(new StackPanel { Children = { Ui.H1(title), Ui.SoftText(subtitle) } });
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(body);
        return dock;
    }

    private Control QueuePage()
    {
        var list = new StackPanel { Spacing = 10 };
        var pending = Pending();
        if (_sessions is null)
            list.Children.Add(Ui.SoftText(L.T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
        else if (pending.Count == 0)
            list.Children.Add(Ui.Card(Ui.SoftText(L.T("Сейчас никто не ждёт. Касса появится здесь, как только кассир нажмёт «Получить код для поддержки» в разделе «Тех. поддержка».",
                "Азыр эч ким күтпөйт. Кассир «Колдоо үчүн код алуу» баскычын басканда касса бул жерде пайда болот.",
                "Nobody is waiting. A till appears here once the cashier presses “Get a code for support” in “Support”.",
                "Şu an bekleyen yok. Kasiyer «Destek için kod al»a bastığında kasa burada görünür.",
                "Hozir hech kim kutmayapti. Kassir «Yordam uchun kod olish»ni bosganda kassa shu yerda paydo bo'ladi."), 14)));
        foreach (var p in pending)
        {
            var row = p;
            var note = Str(p, "note");
            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    Ui.H2(Str(p, "company") is { Length: > 0 } c ? c : "—"),
                    Ui.Pill(L.T("ждёт ", "күтөт ", "waiting ", "bekliyor ", "kutmoqda ") + (Date(p, "created") is { } cr ? Ui.Ago(cr) : ""), Ui.WarnSoft, Ui.Warn),
                },
            });
            info.Children.Add(Ui.SoftText($"{Str(p, "machine")} · {L.T("кассир", "кассир", "cashier", "kasiyer", "kassir")}: {(Str(p, "user") is { Length: > 0 } u ? u : "—")} · NurMarket {Str(p, "version")}"));
            if (note.Length > 0)
                info.Children.Add(new Border
                {
                    Background = Ui.AccentSoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Margin = new Thickness(0, 4, 0, 0),
                    Child = new TextBlock { Text = "«" + note + "»", TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text },
                });
            var connect = Ui.BtnAsync(L.T("Подключиться", "Туташуу", "Connect", "Bağlan", "Ulanish"), "primary", () => ConnectFromQueueAsync(row));
            connect.VerticalAlignment = VerticalAlignment.Center;
            var history = Ui.BtnAsync(L.T("История кассы", "Кассанын тарыхы", "Till history", "Kasa geçmişi", "Kassa tarixi"), "secondary",
                () => ShowKassaHistoryAsync(Str(row, "company"), Str(row, "machine")));
            history.VerticalAlignment = VerticalAlignment.Center;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { history, connect } };
            var d = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            d.Children.Add(buttons);
            d.Children.Add(info);
            list.Children.Add(Ui.Card(d));
        }
        return Page(L.T("Ждут помощи", "Жардам күтүшөт", "Waiting for help", "Yardım bekleyenler", "Yordam kutmoqda"),
            L.T("Кассир называет только PIN (4 цифры). Подключение — только после «Разрешить» на кассе.", "Кассир PIN гана айтат (4 сан). Туташуу — кассадагы «Уруксат берүү» баскычынан кийин гана.",
                "The cashier tells you only the PIN (4 digits). You connect only after they press “Allow”.", "Kasiyer yalnızca PIN'i söyler (4 hane). Bağlantı, kasada «İzin ver»den sonra.",
                "Kassir faqat PIN aytadi (4 raqam). Ulanish — kassada «Ruxsat berish»dan keyin."),
            new ScrollViewer { Content = list },
            Ui.BtnAsync(L.T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"), "secondary", RefreshAsync));
    }

    private async Task ConnectFromQueueAsync(JsonElement row)
    {
        var pin = await Dialogs.AskAsync(this, L.T("PIN кассы", "Кассанын PIN'и", "Till PIN", "Kasa PIN'i", "Kassa PIN"),
            L.T($"Попросите кассира {Str(row, "company")} продиктовать PIN (4 цифры) с экрана «Тех. поддержка».",
                $"{Str(row, "company")} кассиринен «Тех. колдоо» экранындагы PIN'ди (4 сан) айтып берүүнү сураңыз.",
                $"Ask the cashier at {Str(row, "company")} to read the PIN (4 digits) from the “Support” screen.",
                $"{Str(row, "company")} kasiyerinden «Destek» ekranındaki PIN'i (4 hane) okumasını isteyin.",
                $"{Str(row, "company")} kassiridan «Texnik yordam» ekranidagi PIN'ni (4 raqam) aytishini so'rang."), "0000", digits: 4);
        if (pin is null)
            return;
        OpenSession(new SessionWindow(_api, sessionId: Str(row, "sessionId"), code: null, pin, Str(row, "company"), Str(row, "machine"), Str(row, "user"), Str(row, "note")));
    }

    private void OpenSession(SessionWindow w)
    {
        w.Show();
        w.Closed += async (_, _) => await RefreshAsync();
    }

    private Control ActivePage()
    {
        var list = new StackPanel { Spacing = 8 };
        var busy = Active().Where(e => Str(e, "state") != "pending").ToList();
        if (busy.Count == 0)
            list.Children.Add(Ui.Card(Ui.SoftText(L.T("Сейчас нет идущих сеансов.", "Азыр жүрүп жаткан сеанс жок.", "No sessions in progress.", "Devam eden oturum yok.", "Hozir seans yo'q."), 14)));
        foreach (var s in busy)
        {
            var state = Str(s, "state") == "active"
                ? Ui.Pill(L.T("идёт", "жүрүп жатат", "live", "canlı", "davom etmoqda"), Ui.SuccessSoft, Ui.Success)
                : Ui.Pill(L.T("ждёт «Разрешить»", "«Уруксат» күтөт", "awaiting consent", "izin bekliyor", "ruxsat kutmoqda"), Ui.WarnSoft, Ui.Warn);
            var since = Date(s, "start") ?? Date(s, "created");
            list.Children.Add(Ui.Card(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { Ui.H2(Str(s, "company")), state } },
                    Ui.SoftText($"{Str(s, "machine")} · {L.T("оператор", "оператор", "operator", "operatör", "operator")}: {Str(s, "operatorName")} · {(since is { } d ? Ui.Ago(d) : "")}"),
                },
            }));
        }
        return Page(L.T("Идут сейчас", "Азыр жүрүп жатат", "In progress", "Devam edenler", "Hozir davom etmoqda"),
            L.T("Кто из операторов к какой кассе подключён прямо сейчас.", "Кайсы оператор кайсы кассага туташкан.", "Which operator is connected to which till right now.",
                "Hangi operatör hangi kasaya bağlı.", "Qaysi operator qaysi kassaga ulangan."), new ScrollViewer { Content = list });
    }

    private Control CodePage()
    {
        var code = Ui.Input("482 915 736");
        code.FontSize = 22;
        var pin = Ui.Input("0000");
        pin.FontSize = 22;
        pin.MaxLength = 4;
        var go = Ui.Btn(L.T("Подключиться", "Туташуу", "Connect", "Bağlan", "Ulanish"), "primary", () =>
        {
            var c = new string((code.Text ?? "").Where(char.IsAsciiDigit).ToArray());
            var p = new string((pin.Text ?? "").Where(char.IsAsciiDigit).ToArray());
            if (c.Length != 9 || p.Length != 4)
            {
                _toast.Show(L.T("Код — 9 цифр, PIN — 4 цифры.", "Код — 9 сан, PIN — 4 сан.", "The code is 9 digits, the PIN is 4.", "Kod 9 hane, PIN 4 hane.", "Kod — 9 raqam, PIN — 4 raqam."), true);
                return;
            }
            OpenSession(new SessionWindow(_api, sessionId: null, code: c, p, "", "", "", ""));
        });
        go.Height = 46;
        var form = new StackPanel
        {
            Spacing = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                Ui.Field(L.T("Код кассы (9 цифр)", "Кассанын коду (9 сан)", "Till code (9 digits)", "Kasa kodu (9 hane)", "Kassa kodi (9 raqam)"), code),
                Ui.Field("PIN", pin),
                go,
            },
        };
        return Page(L.T("Подключиться по коду", "Код менен туташуу", "Connect by code", "Kodla bağlan", "Kod bilan ulanish"),
            L.T("Если кассир диктует код и PIN по телефону. Обычно удобнее «Ждут помощи» — там нужен только PIN.",
                "Кассир кодду жана PIN'ди телефон аркылуу айтса. Адатта «Жардам күтүшөт» ыңгайлуу — PIN гана керек.",
                "When the cashier reads the code and PIN over the phone. “Waiting for help” is usually easier — only the PIN is needed.",
                "Kasiyer kodu ve PIN'i telefonda okursa. Genelde «Yardım bekleyenler» daha kolay — yalnızca PIN gerekir.",
                "Kassir kod va PIN'ni telefonda aytsa. Odatda «Yordam kutmoqda» qulayroq — faqat PIN kerak."), Ui.Card(form, 22));
    }

    private Control JournalPage()
    {
        var search = Ui.Input(L.T("Поиск: компания, компьютер, кассир, оператор, проблема", "Издөө: компания, компьютер, кассир, оператор", "Search: company, computer, cashier, operator, issue",
            "Ara: şirket, bilgisayar, kasiyer, operatör, sorun", "Qidirish: kompaniya, kompyuter, kassir, operator, muammo"), _journalQuery);
        var list = new StackPanel();
        void Render()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim();
            _journalQuery = q;
            var rows = Recent().Where(e => q.Length == 0 || new[] { "company", "machine", "user", "operatorName", "operator", "note", "reason" }
                .Any(k => Str(e, k).Contains(q, StringComparison.CurrentCultureIgnoreCase))).Take(300).ToList();
            list.Children.Add(Row(true, L.T("Когда", "Качан", "When", "Ne zaman", "Qachon"), L.T("Касса", "Касса", "Till", "Kasa", "Kassa"),
                L.T("Оператор", "Оператор", "Operator", "Operatör", "Operator"), L.T("Длилось", "Узактыгы", "Duration", "Süre", "Davomiylik"), L.T("Итог", "Жыйынтык", "Result", "Sonuç", "Natija"), null));
            if (rows.Count == 0)
                list.Children.Add(new TextBlock { Text = L.T("Записей нет.", "Жазуу жок.", "No records.", "Kayıt yok.", "Yozuvlar yo'q."), Foreground = Ui.TextSoft, Margin = new Thickness(12) });
            foreach (var e in rows)
            {
                var item = e;
                var dur = e.TryGetProperty("durationSec", out var ds) && ds.TryGetInt64(out var sec) && sec > 0 ? TimeSpan.FromSeconds(sec).ToString(@"h\:mm\:ss") : "—";
                var when = Date(e, "end") ?? Date(e, "created");
                list.Children.Add(Row(false, when?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "", $"{Str(e, "company")} · {Str(e, "machine")}" + (Str(e, "note") is { Length: > 0 } n ? $" — «{n}»" : ""),
                    Str(e, "operatorName"), dur, ReasonText(Str(e, "reason")), () => _ = ShowKassaHistoryAsync(Str(item, "company"), Str(item, "machine"))));
            }
        }
        search.TextChanged += (_, _) => Render();
        Render();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(search);
        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(Ui.Card(new ScrollViewer { Content = list }, 0));
        return Page(L.T("Журнал сеансов", "Сеанстар журналы", "Session log", "Oturum günlüğü", "Seanslar jurnali"),
            L.T("Все подключения: кто, к какой кассе, сколько длилось, чем кончилось. Нажмите на строку — история этой кассы.",
                "Бардык туташуулар. Сапты басыңыз — ушул кассанын тарыхы.", "All connections: who, which till, how long, the result. Click a row for the till's history.",
                "Tüm bağlantılar. Satıra tıklayın — kasanın geçmişi.", "Barcha ulanishlar. Qatorni bosing — kassa tarixi."), dock,
            Ui.BtnAsync(L.T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"), "secondary", async () =>
            {
                await RefreshAsync();
                Show("journal");
            }));
    }

    private static string ReasonText(string reason) => reason switch
    {
        "operator" => L.T("завершил оператор", "оператор бүтүрдү", "ended by operator", "operatör bitirdi", "operator tugatdi"),
        "host" => L.T("завершил кассир", "кассир бүтүрдү", "ended by cashier", "kasiyer bitirdi", "kassir tugatdi"),
        "declined" => L.T("кассир отказал", "кассир баш тартты", "cashier declined", "kasiyer reddetti", "kassir rad etdi"),
        "consent-timeout" => L.T("нет ответа кассира", "кассир жооп берген жок", "no answer", "yanıt yok", "javob yo'q"),
        "locked" => L.T("неверный PIN ×5", "туура эмес PIN ×5", "wrong PIN ×5", "yanlış PIN ×5", "noto'g'ri PIN ×5"),
        "timeout" or "disconnect" => L.T("связь прервалась", "байланыш үзүлдү", "connection lost", "bağlantı koptu", "aloqa uzildi"),
        "" => "—",
        _ => reason,
    };

    private static Control Row(bool head, string a, string b, string c, string d, string e, Action? click)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("150,3*,1.3*,90,1.4*"), Margin = new Thickness(12, 0) };
        var cells = new[] { a, b, c, d, e };
        for (var i = 0; i < cells.Length; i++)
        {
            var t = new TextBlock
            {
                Text = cells[i], FontSize = 13, Foreground = head ? Ui.TextSoft : Ui.Text, FontWeight = head ? FontWeight.Bold : FontWeight.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 9, 10, 9),
            };
            Grid.SetColumn(t, i);
            g.Children.Add(t);
        }
        var border = new Border { BorderBrush = Ui.Border, BorderThickness = new Thickness(0, 0, 0, 1), Background = head ? Ui.Soft : Brushes.Transparent, Child = g };
        if (click == null)
            return border;
        var b2 = new Button { Content = border, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(0) };
        b2.Click += (_, _) => click();
        return b2;
    }

    private Control NotesPage()
    {
        var search = Ui.Input(L.T("Поиск по заметкам: компания, компьютер, текст, оператор", "Эскертмелерден издөө", "Search notes: company, computer, text, operator",
            "Notlarda ara", "Eslatmalardan qidirish"), _notesQuery);
        var list = new StackPanel { Spacing = 8 };
        List<JsonElement> notes = new();
        void Render()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim();
            _notesQuery = q;
            var rows = notes.Where(n => q.Length == 0 || new[] { "company", "machine", "text", "operatorName" }.Any(k => Str(n, k).Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
            if (rows.Count == 0)
                list.Children.Add(Ui.SoftText(L.T("Заметок нет.", "Эскертме жок.", "No notes.", "Not yok.", "Eslatmalar yo'q.")));
            foreach (var n in rows)
                list.Children.Add(NoteCard(n));
        }
        search.TextChanged += (_, _) => Render();
        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = await _api.NotesAsync(null, null, 300);
                Dispatcher.UIThread.Post(() =>
                {
                    notes = loaded;
                    Render();
                });
            }
            catch (Exception ex)
            {
                _toast.Show(ex.Message, true);
            }
        });
        var dock = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10), Children = { search } };
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(new ScrollViewer { Content = list });
        return Page(L.T("Заметки и обращения", "Эскертмелер жана кайрылуулар", "Notes and cases", "Notlar ve talepler", "Eslatmalar va murojaatlar"),
            L.T("Что случилось и что сделали — по каждой кассе, от всех операторов. Пишутся в окне сеанса, вкладка «Заметки».",
                "Ар бир касса боюнча эмне болду жана эмне жасалды — бардык операторлордон.", "What happened and what was done — per till, from all operators. Written in the session window, “Notes” tab.",
                "Her kasa için ne oldu ve ne yapıldı — tüm operatörlerden.", "Har bir kassa bo'yicha nima bo'ldi va nima qilindi — barcha operatorlardan."), dock);
    }

    public static Control NoteCard(JsonElement n) => Ui.Card(new StackPanel
    {
        Spacing = 4,
        Children =
        {
            Ui.SoftText($"{(Date(n, "created")?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "")} · {Str(n, "operatorName")} · {Str(n, "company")} · {Str(n, "machine")}", 12),
            new TextBlock { Text = Str(n, "text"), TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text },
        },
    }, 12);

    /// <summary>История кассы: прошлые заметки и сеансы этой кассы — в отдельном окне.</summary>
    private async Task ShowKassaHistoryAsync(string company, string machine)
    {
        var notes = await _api.NotesAsync(company, machine, 100);
        var sessions = Recent().Where(e => Str(e, "company") == company && Str(e, "machine") == machine).Take(50).ToList();
        var list = new StackPanel { Spacing = 8 };
        list.Children.Add(Ui.Caption(L.T("Заметки", "Эскертмелер", "Notes", "Notlar", "Eslatmalar")));
        if (notes.Count == 0)
            list.Children.Add(Ui.SoftText(L.T("Заметок по этой кассе ещё нет.", "Бул касса боюнча эскертме жок.", "No notes for this till yet.", "Bu kasa için not yok.", "Bu kassa bo'yicha eslatma yo'q.")));
        foreach (var n in notes)
            list.Children.Add(NoteCard(n));
        list.Children.Add(Ui.Caption(L.T("Сеансы", "Сеанстар", "Sessions", "Oturumlar", "Seanslar")));
        if (sessions.Count == 0)
            list.Children.Add(Ui.SoftText("—"));
        foreach (var s in sessions)
            list.Children.Add(Ui.SoftText($"{(Date(s, "end") ?? Date(s, "created"))?.ToLocalTime():dd.MM.yyyy HH:mm} · {Str(s, "operatorName")} · {ReasonText(Str(s, "reason"))}" +
                                          (Str(s, "note") is { Length: > 0 } nn ? $" · «{nn}»" : "")));
        var w = new Window
        {
            Title = $"{company} · {machine}", Width = 620, Height = 640, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Window,
            Content = new Border { Padding = new Thickness(18), Child = new ScrollViewer { Content = list } },
        };
        await w.ShowDialog(this);
    }
}

/// <summary>Короткие сообщения внизу окна (3–5 с).</summary>
internal sealed class ToastHost : Border
{
    private readonly TextBlock _text = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
    private CancellationTokenSource? _hide;

    public ToastHost()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 0, 24);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(16, 10);
        IsVisible = false;
        Child = _text;
    }

    public async void Show(string text, bool error)
    {
        _hide?.Cancel();
        var cts = new CancellationTokenSource();
        _hide = cts;
        Background = error ? Ui.Danger : Ui.Navy;
        _text.Text = text;
        IsVisible = true;
        try
        {
            await Task.Delay(error ? 6000 : 4000, cts.Token);
            IsVisible = false;
        }
        catch (TaskCanceledException)
        {
            // пришло следующее сообщение
        }
    }
}

/// <summary>Простые диалоги: ввод короткого значения (PIN).</summary>
internal static class Dialogs
{
    public static async Task<string?> AskAsync(Window owner, string title, string text, string watermark, int digits = 0)
    {
        var input = Ui.Input(watermark);
        input.FontSize = 24;
        if (digits > 0)
            input.MaxLength = digits;
        string? result = null;
        var w = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Window,
        };
        void Ok()
        {
            var v = (input.Text ?? "").Trim();
            if (digits > 0)
                v = new string(v.Where(char.IsAsciiDigit).ToArray());
            if (digits > 0 && v.Length != digits)
                return;
            result = v;
            w.Close();
        }
        input.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                Ok();
        };
        var ok = Ui.Btn(L.T("Подключиться", "Туташуу", "Connect", "Bağlan", "Ulanish"), "primary", Ok);
        var cancel = Ui.Btn(L.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), "secondary", () => w.Close());
        w.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Ui.H2(title), Ui.SoftText(text, 14), input,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        w.Opened += (_, _) => input.Focus();
        await w.ShowDialog(owner);
        return result;
    }
}
