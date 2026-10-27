using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NurRemoteSupport.Operator;

/// <summary>Вход сотрудника поддержки: адрес сервера, логин, пароль (операторов заводит администратор сервера:
/// dotnet Server.dll add-operator). Язык программы — 5 языков, тема светлая/тёмная (2026-10-27, пакет О2).</summary>
internal sealed class LoginWindow : Window
{
    private readonly TextBox _server = Ui.Input("https://support.nurcrm.kg", Settings.Current.Server);
    private readonly TextBox _login = Ui.Input(L.T("логин", "логин", "login", "kullanıcı adı", "login"), Settings.Current.Login);
    private readonly TextBox _password = new() { PasswordChar = '•', Watermark = L.T("пароль", "сырсөз", "password", "şifre", "parol") };
    private readonly TextBlock _error = new() { Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Border _notice = new() { Background = Ui.WarnSoft, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), IsVisible = false };
    private readonly TextBlock _noticeText = new() { Foreground = Ui.Warn, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    // 2026-10-27: двойной Enter / двойной щелчок открывал два главных окна — пока вход идёт, повторный запрос игнорируется.
    private bool _busy;

    public LoginWindow(string? notice = null)
    {
        Title = "NurMarket — " + L.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Window;
        Ui.FitToScreen(this, 480, 700, 340, 420);
        if (notice is { Length: > 0 })
        {
            _noticeText.Text = notice;
            _notice.Child = _noticeText;
            _notice.IsVisible = true;
        }
        _password.A11y(L.T("Пароль", "Сырсөз", "Password", "Şifre", "Parol"));
        Build();
        Opened += (_, _) => (_login.Text?.Length > 0 ? _password : _login).Focus();
    }

    private void Build()
    {
        // Смена языка/темы пересобирает окно: поля (с уже введённым текстом) переезжают в новую разметку.
        foreach (var c in new Control[] { _server, _login, _password, _error, _notice })
            if (c.Parent is Panel old)
                old.Children.Remove(c);
        _login.Watermark = L.T("логин", "логин", "login", "kullanıcı adı", "login");
        _password.Watermark = L.T("пароль", "сырсөз", "password", "şifre", "parol");
        var langs = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (id, name) in new[] { ("ru", "Русский"), ("ky", "Кыргызча"), ("en", "English"), ("tr", "Türkçe"), ("uz", "O'zbekcha") })
        {
            var lang = id;
            var chip = Ui.Chip(name, L.Lang == lang, () =>
            {
                L.Lang = lang;
                Settings.Current.Lang = lang;
                Settings.Current.Save();
                Build();
            });
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.FontSize = 12;
            langs.Children.Add(chip);
        }
        var theme = Ui.Btn(Ui.IsDark ? L.T("☀ Светлая тема", "☀ Жарык тема", "☀ Light theme", "☀ Açık tema", "☀ Yorug' mavzu") : L.T("☾ Тёмная тема", "☾ Караңгы тема", "☾ Dark theme", "☾ Koyu tema", "☾ Qorong'i mavzu"), "ghost", () =>
        {
            Settings.Current.Dark = !Ui.IsDark;
            Settings.Current.Save();
            Ui.SetDark(Settings.Current.Dark);
            Build();
        });
        theme.HorizontalAlignment = HorizontalAlignment.Center;
        var enter = Ui.BtnAsync(L.T("Войти", "Кирүү", "Sign in", "Giriş", "Kirish"), "primary", LoginAsync);
        enter.HorizontalAlignment = HorizontalAlignment.Stretch;
        enter.Height = 48;
        foreach (var box in new[] { _server, _login, _password })
        {
            box.KeyDown -= OnEnter;
            box.KeyDown += OnEnter;
        }
        var logo = new BrandMark { Width = 64, Height = 64, HorizontalAlignment = HorizontalAlignment.Center };
        var form = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                logo,
                new TextBlock { Text = "NurMarket " + L.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam"), FontSize = 22, FontWeight = FontWeight.ExtraBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.Text },
                new TextBlock
                {
                    Text = L.T("Программа сотрудника технической поддержки", "Техникалык колдоо кызматкеринин программасы", "Technical support operator app",
                        "Teknik destek personeli programı", "Texnik yordam xodimi dasturi"),
                    HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Ui.TextSoft,
                },
                langs,
                _notice,
                Ui.Field(L.T("Сервер поддержки", "Колдоо сервери", "Support server", "Destek sunucusu", "Yordam serveri"), _server),
                Ui.Field(L.T("Логин", "Логин", "Login", "Kullanıcı adı", "Login"), _login),
                Ui.Field(L.T("Пароль", "Сырсөз", "Password", "Şifre", "Parol"), _password),
                _error,
                enter,
                theme,
            },
        };
        // Прокрутка: на экранах 1024×600 форма не помещается целиком.
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(24, 16), Child = Ui.Card(form, 22), VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private async void OnEnter(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await LoginAsync();
    }

    private async Task LoginAsync()
    {
        if (_busy)
            return;
        _busy = true;
        _error.IsVisible = false;
        try
        {
            var api = Api.For(_server.Text ?? "");
            await api.LoginAsync((_login.Text ?? "").Trim(), _password.Text ?? "");
            Settings.Current.Server = api.Base.ToString().TrimEnd('/');
            Settings.Current.Login = api.Login;
            Settings.Current.Save();
            var main = new MainWindow(api);
            main.Show();
            Close();
        }
        catch (Exception ex)
        {
            _error.Text = ex.Message;
            _error.IsVisible = true;
            _notice.IsVisible = false;
            _busy = false;
        }
    }
}
