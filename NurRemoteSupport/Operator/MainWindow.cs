using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NurRemoteSupport.Operator;

/// <summary>Главное окно оператора: «Ждут помощи» (кассы, нажавшие «Получить код», с именем кассира и описанием проблемы —
/// подключение одним PIN, обновление каждые 3 с, сигнал о новой кассе), «Идут сейчас», «По коду», «Журнал» (все сеансы:
/// кто, к какой кассе, сколько длилось, чем кончилось + заметки по кассе), «Заметки» (история обращений всех операторов).
/// 2026-10-27 (пакет О2): очередь обновляется на месте (прокрутка и фокус не сбрасываются), «По коду» понимает вставку
/// «Код: … PIN: …», «Выйти» завершает сеансы и отзывает вход, «Сменить пароль», тёмная тема.</summary>
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
    private bool _refreshing;
    private bool _leaving;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _journalQuery = "";
    private string _notesQuery = "";
    // очередь «Ждут помощи»: карточки по номеру сеанса, обновляются на месте
    private StackPanel? _queueList;
    private TextBlock? _queueEmpty;
    private readonly Dictionary<string, QueueCard> _queueCards = new();
    private ScrollViewer? _activeScroll;
    private ScrollViewer? _navScroll;

    public MainWindow(Api api)
    {
        _api = api;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Window;
        Ui.FitToScreen(this, 1280, 820, 960, 560);
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
        Title = (waiting > 0 ? $"({waiting}) " : "") + "NurCRM " + L.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam") + " — " + _api.Name;

    // ------------------------------------------------------------------ меню

    private Control BuildSide()
    {
        if (_navScroll != null)
            _navScroll.Content = null; // пересборка меню (смена темы): _nav переезжает в новую прокрутку
        var who = new StackPanel
        {
            Margin = new Thickness(16, 18, 16, 14),
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children =
                    {
                        new BrandMark { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = "NurCRM", FontSize = 20, FontWeight = FontWeight.ExtraBold, Foreground = Ui.NavyText, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                Ui.Pill(L.T("поддержка", "колдоо", "support", "destek", "yordam"), Ui.Accent, Ui.OnAccent),
                new TextBlock { Text = _api.Name, Foreground = Ui.NavyText, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = _api.Base.Host, Foreground = Ui.B("#AAAAAA"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        who.Children[1].HorizontalAlignment = HorizontalAlignment.Left;
        who.Children[1].Margin = new Thickness(0, 8, 0, 0);
        RenderNav();
        Button Nav(string text, Action click)
        {
            var b = Ui.Btn(text, "nav", click);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.HorizontalContentAlignment = HorizontalAlignment.Left;
            return b;
        }
        var theme = Nav(Ui.IsDark ? L.T("☀ Светлая тема", "☀ Жарык тема", "☀ Light theme", "☀ Açık tema", "☀ Yorug' mavzu") : L.T("☾ Тёмная тема", "☾ Караңгы тема", "☾ Dark theme", "☾ Koyu tema", "☾ Qorong'i mavzu"), () =>
        {
            Settings.Current.Dark = !Ui.IsDark;
            Settings.Current.Save();
            Ui.SetDark(Settings.Current.Dark);
            // подпись кнопки зависит от темы — меню пересобираем
            if (Content is Panel { Children: [Grid g, ..] } && g.Children.Count > 0)
            {
                g.Children.RemoveAt(0);
                g.Children.Insert(0, BuildSide());
            }
        });
        var password = Nav(L.T("Сменить пароль", "Сырсөздү алмаштыруу", "Change password", "Şifreyi değiştir", "Parolni almashtirish"), () => _ = ChangePasswordAsync());
        var logout = Nav(L.T("Выйти", "Чыгуу", "Sign out", "Çıkış", "Chiqish"), () => _ = LogoutAsync());
        var bottom = new StackPanel { Spacing = 2, Margin = new Thickness(8, 0, 8, 12), Children = { theme, password, logout } };
        var dock = new DockPanel { Background = Ui.Navy };
        DockPanel.SetDock(who, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(who);
        dock.Children.Add(bottom);
        _navScroll = new ScrollViewer { Content = _nav, Margin = new Thickness(8, 0) };
        dock.Children.Add(_navScroll);
        return dock;
    }

    /// <summary>«Выйти»: идущие сеансы завершаются (с подтверждением), вход отзывается на сервере, открывается экран входа.</summary>
    private async Task LogoutAsync()
    {
        if (_leaving)
            return;
        var open = App.Sessions.ToList();
        if (open.Count > 0)
        {
            var ok = await Dialogs.ConfirmAsync(this,
                L.T($"Завершить сеансов: {open.Count}?", $"Сеанстарды аяктоо: {open.Count}?", $"End {open.Count} session(s)?", $"{open.Count} oturum sonlandırılsın mı?", $"{open.Count} ta seans tugatilsinmi?"),
                L.T("Перед выходом идущие сеансы с кассами будут закрыты.", "Чыгуудан мурун кассалар менен жүрүп жаткан сеанстар жабылат.", "Sessions in progress will be closed before you sign out.",
                    "Çıkıştan önce devam eden oturumlar kapatılacak.", "Chiqishdan oldin davom etayotgan seanslar yopiladi."),
                L.T("Завершить и выйти", "Аяктоо жана чыгуу", "End and sign out", "Bitir ve çık", "Tugatish va chiqish"));
            if (!ok)
                return;
        }
        _leaving = true;
        foreach (var s in open)
            await s.EndAndCloseAsync();
        await _api.LogoutAsync();
        new LoginWindow().Show();
        Close();
    }

    private async Task ChangePasswordAsync()
    {
        var oldPw = new TextBox { PasswordChar = '•' }.A11y(L.T("Текущий пароль", "Азыркы сырсөз", "Current password", "Mevcut şifre", "Joriy parol"));
        var newPw = new TextBox { PasswordChar = '•' }.A11y(L.T("Новый пароль", "Жаңы сырсөз", "New password", "Yeni şifre", "Yangi parol"));
        var again = new TextBox { PasswordChar = '•' }.A11y(L.T("Новый пароль ещё раз", "Жаңы сырсөз дагы бир жолу", "New password again", "Yeni şifre tekrar", "Yangi parol yana"));
        var error = new TextBlock { Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var w = new Window { Title = L.T("Сменить пароль", "Сырсөздү алмаштыруу", "Change password", "Şifreyi değiştir", "Parolni almashtirish"), Width = 460, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Window };
        var busy = false;
        async Task Save()
        {
            if (busy)
                return;
            error.IsVisible = false;
            string? problem = null;
            if (string.IsNullOrEmpty(oldPw.Text))
                problem = L.T("Введите текущий пароль.", "Азыркы сырсөздү киргизиңиз.", "Enter the current password.", "Mevcut şifreyi girin.", "Joriy parolni kiriting.");
            else if ((newPw.Text ?? "").Length < 10)
                problem = L.T("Новый пароль — не меньше 10 символов.", "Жаңы сырсөз — кеминде 10 белги.", "The new password must be at least 10 characters.", "Yeni şifre en az 10 karakter olmalı.", "Yangi parol kamida 10 belgi bo'lishi kerak.");
            else if (newPw.Text != again.Text)
                problem = L.T("Новые пароли не совпадают.", "Жаңы сырсөздөр дал келбейт.", "The new passwords do not match.", "Yeni şifreler eşleşmiyor.", "Yangi parollar mos kelmaydi.");
            if (problem != null)
            {
                error.Text = problem;
                error.IsVisible = true;
                return;
            }
            busy = true;
            try
            {
                await _api.ChangePasswordAsync(oldPw.Text!, newPw.Text!);
                Toast.Show(L.T("Пароль изменён. Другие входы с этим логином закрыты.", "Сырсөз алмашты. Бул логин менен башка кирүүлөр жабылды.", "Password changed. Other sign-ins with this login were closed.",
                    "Şifre değişti. Bu girişin diğer oturumları kapatıldı.", "Parol almashdi. Bu login bilan boshqa kirishlar yopildi."));
                w.Close();
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
                error.IsVisible = true;
            }
            finally
            {
                busy = false;
            }
        }
        foreach (var box in new[] { oldPw, newPw, again })
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter)
                    await Save();
            };
        var warn = App.Sessions.Count > 0
            ? Ui.SoftText(L.T("Идущие сеансы закроются сервером в течение минуты — лучше сменить пароль между сеансами.", "Жүрүп жаткан сеанстар сервер тарабынан бир мүнөттө жабылат — сеанстардын ортосунда алмаштырган жакшы.",
                "Sessions in progress will be closed by the server within a minute — better change the password between sessions.", "Devam eden oturumlar sunucu tarafından bir dakika içinde kapatılır — şifreyi oturumlar arasında değiştirin.",
                "Davom etayotgan seanslar server tomonidan bir daqiqada yopiladi — parolni seanslar orasida almashtirgan ma'qul."))
            : null;
        var save = Ui.Btn(L.T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), "primary", () => _ = Save());
        var cancel = Ui.Btn(L.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), "secondary", () => w.Close());
        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Ui.H2(w.Title!),
                Ui.Field(L.T("Текущий пароль", "Азыркы сырсөз", "Current password", "Mevcut şifre", "Joriy parol"), oldPw),
                Ui.Field(L.T("Новый пароль (не меньше 10 символов)", "Жаңы сырсөз (кеминде 10 белги)", "New password (at least 10 characters)", "Yeni şifre (en az 10 karakter)", "Yangi parol (kamida 10 belgi)"), newPw),
                Ui.Field(L.T("Новый пароль ещё раз", "Жаңы сырсөз дагы бир жолу", "New password again", "Yeni şifre tekrar", "Yangi parol yana"), again),
            },
        };
        if (warn != null)
            form.Children.Add(warn);
        form.Children.Add(error);
        form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } });
        w.Content = new ScrollViewer { Content = new Border { Padding = new Thickness(22), Child = form } };
        w.Opened += (_, _) => oldPw.Focus();
        await w.ShowDialog(this);
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
                b.Resources["ButtonForeground"] = Ui.OnAccent;
                b.Resources["ButtonForegroundPointerOver"] = Ui.OnAccent;
            }
            _nav.Children.Add(b);
        }
    }

    private void Show(string page)
    {
        _current = page;
        RenderNav();
        _queueList = null;
        _queueCards.Clear();
        _activeScroll = null;
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
        // 2026-10-27: запросы не идут внахлёст — медленный сервер не копит очередь из опросов.
        if (_refreshing || _leaving)
            return;
        _refreshing = true;
        try
        {
            try
            {
                _sessions = await _api.SessionsAsync();
            }
            catch (Exception ex)
            {
                if (_current is "queue" or "active" && !_leaving)
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
            if (_current == "queue")
                SyncQueue();
            else if (_current == "active")
                RefreshActive();
        }
        finally
        {
            _refreshing = false;
        }
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

    /// <summary>Карточка кассы в очереди: создаётся один раз, при опросе обновляются только тексты (прокрутка и фокус не теряются).</summary>
    private sealed class QueueCard
    {
        public readonly Border View;
        public JsonElement Row;
        private readonly TextBlock _company = new() { FontSize = 16, FontWeight = FontWeight.Bold };
        private readonly TextBlock _waiting = new() { FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.Warn };
        private readonly TextBlock _line = new() { FontSize = 13, Foreground = Ui.TextSoft, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _note = new() { TextWrapping = TextWrapping.Wrap };
        private readonly Border _noteBox;

        public QueueCard(JsonElement row, Func<JsonElement, Task> connect, Func<JsonElement, Task> history)
        {
            Row = row;
            _noteBox = new Border { Background = Ui.AccentSoft, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Margin = new Thickness(0, 4, 0, 0), Child = _note };
            var pill = new Border { Background = Ui.WarnSoft, CornerRadius = new CornerRadius(999), Padding = new Thickness(10, 3), VerticalAlignment = VerticalAlignment.Center, Child = _waiting };
            var info = new StackPanel
            {
                Spacing = 4,
                Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _company, pill } }, _line, _noteBox },
            };
            var connectBtn = Ui.BtnAsync(L.T("Подключиться", "Туташуу", "Connect", "Bağlan", "Ulanish"), "primary", () => connect(Row));
            connectBtn.VerticalAlignment = VerticalAlignment.Center;
            var historyBtn = Ui.BtnAsync(L.T("История кассы", "Кассанын тарыхы", "Till history", "Kasa geçmişi", "Kassa tarixi"), "secondary", () => history(Row));
            historyBtn.VerticalAlignment = VerticalAlignment.Center;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { historyBtn, connectBtn } };
            var d = new DockPanel();
            DockPanel.SetDock(buttons, Dock.Right);
            d.Children.Add(buttons);
            d.Children.Add(info);
            View = Ui.Card(d);
            Update(row);
        }

        public void Update(JsonElement p)
        {
            Row = p;
            SetText(_company, Str(p, "company") is { Length: > 0 } c ? c : "—");
            SetText(_waiting, L.T("ждёт ", "күтөт ", "waiting ", "bekliyor ", "kutmoqda ") + (Date(p, "created") is { } cr ? Ui.Ago(cr) : ""));
            SetText(_line, $"{Str(p, "machine")} · {L.T("кассир", "кассир", "cashier", "kasiyer", "kassir")}: {(Str(p, "user") is { Length: > 0 } u ? u : "—")} · NurCRM {Str(p, "version")}");
            var note = Str(p, "note");
            SetText(_note, "«" + note + "»");
            _noteBox.IsVisible = note.Length > 0;
        }

        private static void SetText(TextBlock t, string v)
        {
            if (t.Text != v)
                t.Text = v;
        }
    }

    private Control QueuePage()
    {
        _queueList = new StackPanel { Spacing = 10 };
        _queueEmpty = new TextBlock();
        var emptyCard = Ui.Card(_queueEmpty, 14);
        emptyCard.Tag = "empty";
        _queueList.Children.Add(emptyCard);
        SyncQueue();
        return Page(L.T("Ждут помощи", "Жардам күтүшөт", "Waiting for help", "Yardım bekleyenler", "Yordam kutmoqda"),
            L.T("Кассир называет только PIN (4 цифры). Подключение — только после «Разрешить» на кассе.", "Кассир PIN гана айтат (4 сан). Туташуу — кассадагы «Уруксат берүү» баскычынан кийин гана.",
                "The cashier tells you only the PIN (4 digits). You connect only after they press “Allow”.", "Kasiyer yalnızca PIN'i söyler (4 hane). Bağlantı, kasada «İzin ver»den sonra.",
                "Kassir faqat PIN aytadi (4 raqam). Ulanish — kassada «Ruxsat berish»dan keyin."),
            new ScrollViewer { Content = _queueList },
            Ui.BtnAsync(L.T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"), "secondary", RefreshAsync));
    }

    /// <summary>Сверить карточки очереди с ответом сервера по номеру сеанса: новые добавить, ушедшие убрать, остальные обновить на месте.</summary>
    private void SyncQueue()
    {
        if (_queueList is null || _queueEmpty is null)
            return;
        var pending = Pending();
        var ids = pending.Select(p => Str(p, "sessionId")).ToHashSet();
        foreach (var gone in _queueCards.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _queueList.Children.Remove(_queueCards[gone].View);
            _queueCards.Remove(gone);
        }
        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            var id = Str(p, "sessionId");
            if (!_queueCards.TryGetValue(id, out var card))
            {
                card = new QueueCard(p, ConnectFromQueueAsync, r => ShowKassaHistoryAsync(Str(r, "company"), Str(r, "machine")));
                _queueCards[id] = card;
            }
            else
            {
                card.Update(p);
            }
            // порядок как у сервера (новые сверху); карточка переставляется только если стоит не на своём месте (индекс 0 занят служебной «пустой» карточкой)
            var want = i + 1;
            var at = _queueList.Children.IndexOf(card.View);
            if (at < 0)
                _queueList.Children.Insert(Math.Min(want, _queueList.Children.Count), card.View);
            else if (at != want)
            {
                _queueList.Children.RemoveAt(at);
                _queueList.Children.Insert(Math.Min(want, _queueList.Children.Count), card.View);
            }
        }
        var empty = _queueList.Children[0];
        empty.IsVisible = _sessions is null || pending.Count == 0;
        _queueEmpty.Text = _sessions is null
            ? L.T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")
            : L.T("Сейчас никто не ждёт. Касса появится здесь, как только кассир нажмёт «Получить код для поддержки» в разделе «Тех. поддержка».",
                "Азыр эч ким күтпөйт. Кассир «Колдоо үчүн код алуу» баскычын басканда касса бул жерде пайда болот.",
                "Nobody is waiting. A till appears here once the cashier presses “Get a code for support” in “Support”.",
                "Şu an bekleyen yok. Kasiyer «Destek için kod al»a bastığında kasa burada görünür.",
                "Hozir hech kim kutmayapti. Kassir «Yordam uchun kod olish»ni bosganda kassa shu yerda paydo bo'ladi.");
        _queueEmpty.Foreground = Ui.TextSoft;
        _queueEmpty.TextWrapping = TextWrapping.Wrap;
        _queueEmpty.FontSize = 14;
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
        _activeScroll = new ScrollViewer { Content = BuildActiveList() };
        return Page(L.T("Идут сейчас", "Азыр жүрүп жатат", "In progress", "Devam edenler", "Hozir davom etmoqda"),
            L.T("Кто из операторов к какой кассе подключён прямо сейчас.", "Кайсы оператор кайсы кассага туташкан.", "Which operator is connected to which till right now.",
                "Hangi operatör hangi kasaya bağlı.", "Qaysi operator qaysi kassaga ulangan."), _activeScroll);
    }

    /// <summary>Список «Идут сейчас» пересобирается при опросе, но прокрутка сохраняется.</summary>
    private void RefreshActive()
    {
        if (_activeScroll is null)
            return;
        var offset = _activeScroll.Offset;
        _activeScroll.Content = BuildActiveList();
        Dispatcher.UIThread.Post(() => _activeScroll.Offset = offset, DispatcherPriority.Background);
    }

    private Control BuildActiveList()
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
        return list;
    }

    // ------------------------------------------------------------------ «По коду»

    /// <summary>Разбор вставленного: «Код: 123 456 789 PIN: 5093», «123456789 5093», 13 цифр подряд, только код, только PIN.
    /// Всё нецифровое отбрасывается; 13 и больше цифр — первые 9 это код, последние 4 — PIN.</summary>
    internal static (string Code, string Pin) SplitPasted(string text, bool intoPin)
    {
        var digits = new string((text ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length >= 13)
            return (digits[..9], digits[^4..]);
        if (intoPin)
            return ("", digits.Length > 4 ? digits[^4..] : digits);
        return digits.Length > 9 ? (digits[..9], digits[9..]) : (digits, "");
    }

    private static string FormatCode(string digits) => digits.Length <= 3 ? digits : digits.Length <= 6 ? digits[..3] + " " + digits[3..] : digits[..3] + " " + digits[3..6] + " " + digits[6..];

    private Control CodePage()
    {
        var code = Ui.Input("482 915 736");
        code.FontSize = 22;
        var pin = Ui.Input("0000");
        pin.FontSize = 22;
        var error = new TextBlock { Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var guard = false;
        code.TextChanged += (_, _) =>
        {
            if (guard)
                return;
            guard = true;
            var (c, p) = SplitPasted(code.Text ?? "", intoPin: false);
            code.Text = FormatCode(c);
            code.CaretIndex = code.Text.Length;
            if (p.Length > 0)
                pin.Text = p;
            if (c.Length == 9)
                pin.Focus(); // после 9 цифр курсор сам переходит на PIN
            guard = false;
        };
        pin.TextChanged += (_, _) =>
        {
            if (guard)
                return;
            guard = true;
            var (c, p) = SplitPasted(pin.Text ?? "", intoPin: true);
            if (c.Length > 0)
            {
                code.Text = FormatCode(c);
            }
            if (pin.Text != p)
            {
                pin.Text = p;
                pin.CaretIndex = p.Length;
            }
            guard = false;
        };
        void Go()
        {
            error.IsVisible = false;
            var c = new string((code.Text ?? "").Where(char.IsAsciiDigit).ToArray());
            var p = new string((pin.Text ?? "").Where(char.IsAsciiDigit).ToArray());
            if (c.Length != 9 || p.Length != 4)
            {
                error.Text = L.T("Код — 9 цифр, PIN — 4 цифры.", "Код — 9 сан, PIN — 4 сан.", "The code is 9 digits, the PIN is 4.", "Kod 9 hane, PIN 4 hane.", "Kod — 9 raqam, PIN — 4 raqam.");
                error.IsVisible = true;
                (c.Length != 9 ? code : pin).Focus();
                return;
            }
            OpenSession(new SessionWindow(_api, sessionId: null, code: c, p, "", "", "", ""));
        }
        foreach (var box in new[] { code, pin })
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    Go();
                }
            };
        var go = Ui.Btn(L.T("Подключиться", "Туташуу", "Connect", "Bağlan", "Ulanish"), "primary", Go);
        go.Height = 48;
        var form = new StackPanel
        {
            Spacing = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                Ui.Field(L.T("Код кассы (9 цифр)", "Кассанын коду (9 сан)", "Till code (9 digits)", "Kasa kodu (9 hane)", "Kassa kodi (9 raqam)"), code),
                Ui.Field("PIN", pin),
                error,
                go,
                Ui.SoftText(L.T("Можно вставить всё сообщение кассира целиком: «Код: 123 456 789 PIN: 5093».", "Кассирдин билдирүүсүн бүт коюуга болот: «Код: 123 456 789 PIN: 5093».",
                    "You can paste the cashier's whole message: “Code: 123 456 789 PIN: 5093”.", "Kasiyerin mesajının tamamını yapıştırabilirsiniz: «Kod: 123 456 789 PIN: 5093».",
                    "Kassirning butun xabarini qo'yish mumkin: «Kod: 123 456 789 PIN: 5093».")),
            },
        };
        return Page(L.T("Подключиться по коду", "Код менен туташуу", "Connect by code", "Kodla bağlan", "Kod bilan ulanish"),
            L.T("Если кассир диктует код и PIN по телефону. Обычно удобнее «Ждут помощи» — там нужен только PIN.",
                "Кассир кодду жана PIN'ди телефон аркылуу айтса. Адатта «Жардам күтүшөт» ыңгайлуу — PIN гана керек.",
                "When the cashier reads the code and PIN over the phone. “Waiting for help” is usually easier — only the PIN is needed.",
                "Kasiyer kodu ve PIN'i telefonda okursa. Genelde «Yardım bekleyenler» daha kolay — yalnızca PIN gerekir.",
                "Kassir kod va PIN'ni telefonda aytsa. Odatda «Yordam kutmoqda» qulayroq — faqat PIN kerak."), new ScrollViewer { Content = Ui.Card(form, 22) });
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
                    Str(e, "operatorName"), dur, Reasons.Short(Str(e, "reason")), () => _ = ShowKassaHistoryAsync(Str(item, "company"), Str(item, "machine"))));
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
        var loading = true;
        void Render()
        {
            list.Children.Clear();
            var q = (search.Text ?? "").Trim();
            _notesQuery = q;
            var rows = notes.Where(n => q.Length == 0 || new[] { "company", "machine", "text", "operatorName" }.Any(k => Str(n, k).Contains(q, StringComparison.CurrentCultureIgnoreCase))).ToList();
            if (loading)
                list.Children.Add(Ui.SoftText(L.T("Загружаю…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…")));
            else if (rows.Count == 0)
                list.Children.Add(Ui.SoftText(L.T("Заметок нет.", "Эскертме жок.", "No notes.", "Not yok.", "Eslatmalar yo'q.")));
            foreach (var n in rows)
                list.Children.Add(NoteCard(n));
        }
        search.TextChanged += (_, _) => Render();
        Render();
        _ = Task.Run(async () =>
        {
            try
            {
                var loaded = await _api.NotesAsync(null, null, 300);
                Dispatcher.UIThread.Post(() =>
                {
                    notes = loaded;
                    loading = false;
                    Render();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    loading = false;
                    Render();
                });
                Toast.Show(ex.Message, true); // Toast сам переходит в поток интерфейса
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
            list.Children.Add(Ui.SoftText($"{(Date(s, "end") ?? Date(s, "created"))?.ToLocalTime():dd.MM.yyyy HH:mm} · {Str(s, "operatorName")} · {Reasons.Short(Str(s, "reason"))}" +
                                          (Str(s, "note") is { Length: > 0 } nn ? $" · «{nn}»" : "")));
        var w = new Window
        {
            Title = $"{company} · {machine}", Width = 620, Height = 640, MinWidth = 320, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Window,
            Content = new Border { Padding = new Thickness(18), Child = new ScrollViewer { Content = list } },
        };
        Ui.FitToScreen(w, 620, 640, 320, 320);
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
        Margin = new Thickness(16, 0, 16, 24);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(16, 10);
        IsVisible = false;
        IsHitTestVisible = false;
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

/// <summary>Простые диалоги: ввод короткого значения (PIN) и подтверждение.</summary>
internal static class Dialogs
{
    public static async Task<string?> AskAsync(Window owner, string title, string text, string watermark, int digits = 0)
    {
        var input = Ui.Input(watermark);
        input.FontSize = 24;
        string? result = null;
        var error = new TextBlock { Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var w = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Window,
        };
        void Ok()
        {
            var v = (input.Text ?? "").Trim();
            if (digits > 0)
            {
                // 2026-10-27: без MaxLength — вставка «PIN: 5093» не обрезается; лишнее слева отбрасывается, берутся последние цифры.
                v = new string(v.Where(char.IsAsciiDigit).ToArray());
                if (v.Length > digits)
                    v = v[^digits..];
                if (v.Length != digits)
                {
                    error.Text = L.T($"Нужно {digits} цифры.", $"{digits} сан керек.", $"{digits} digits are needed.", $"{digits} hane gerekli.", $"{digits} ta raqam kerak.");
                    error.IsVisible = true;
                    return;
                }
            }
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
        w.Content = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(22),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        Ui.H2(title), Ui.SoftText(text, 14), input, error,
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                    },
                },
            },
        };
        w.Opened += (_, _) => input.Focus();
        await w.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string text, string okText)
    {
        var yes = false;
        var w = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Ui.Window };
        var ok = Ui.Btn(okText, "danger", () =>
        {
            yes = true;
            w.Close();
        });
        var cancel = Ui.Btn(L.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), "secondary", () => w.Close());
        w.Content = new Border
        {
            Padding = new Thickness(22),
            Child = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    Ui.H2(title), Ui.SoftText(text, 14),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
                },
            },
        };
        await w.ShowDialog(owner);
        return yes;
    }
}
