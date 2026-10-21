using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NurRemoteSupport.Operator;

/// <summary>Вход сотрудника поддержки: адрес сервера, логин, пароль (операторов заводит администратор сервера:
/// dotnet Server.dll add-operator). Язык программы — 5 языков.</summary>
internal sealed class LoginWindow : Window
{
    private readonly TextBox _server = Ui.Input("https://support.nurcrm.kg", Settings.Current.Server);
    private readonly TextBox _login = Ui.Input(L.T("логин", "логин", "login", "kullanıcı adı", "login"), Settings.Current.Login);
    private readonly TextBox _password = new() { PasswordChar = '•', Watermark = L.T("пароль", "сырсөз", "password", "şifre", "parol") };
    private readonly TextBlock _error = new() { Foreground = Ui.Danger, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public LoginWindow()
    {
        Title = "NurMarket — " + L.T("Поддержка", "Колдоо", "Support", "Destek", "Yordam");
        Width = 460;
        Height = 600;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Window;
        Build();
        Opened += (_, _) => (_login.Text?.Length > 0 ? _password : _login).Focus();
    }

    private void Build()
    {
        // Смена языка пересобирает окно: поля (с уже введённым текстом) переезжают в новую разметку.
        foreach (var c in new Control[] { _server, _login, _password, _error })
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
        var enter = Ui.BtnAsync(L.T("Войти", "Кирүү", "Sign in", "Giriş", "Kirish"), "primary", LoginAsync);
        enter.HorizontalAlignment = HorizontalAlignment.Stretch;
        enter.Height = 46;
        _password.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
                await LoginAsync();
        };
        var logo = new Border
        {
            Width = 56, Height = 56, CornerRadius = new CornerRadius(16), Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock { Text = "N", FontSize = 30, FontWeight = FontWeight.ExtraBold, Foreground = Ui.Navy, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
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
                    HorizontalAlignment = HorizontalAlignment.Center, Foreground = Ui.TextSoft,
                },
                langs,
                Ui.Field(L.T("Сервер поддержки", "Колдоо сервери", "Support server", "Destek sunucusu", "Yordam serveri"), _server),
                Ui.Field(L.T("Логин", "Логин", "Login", "Kullanıcı adı", "Login"), _login),
                Ui.Field(L.T("Пароль", "Сырсөз", "Password", "Şifre", "Parol"), _password),
                _error,
                enter,
            },
        };
        Content = new Border { Padding = new Thickness(28), Child = Ui.Card(form, 24), VerticalAlignment = VerticalAlignment.Center };
    }

    private async Task LoginAsync()
    {
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
        }
    }
}
