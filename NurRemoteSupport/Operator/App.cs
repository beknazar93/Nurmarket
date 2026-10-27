using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace NurRemoteSupport.Operator;

internal sealed class App : Application
{
    public override void Initialize()
    {
        // 2026-10-27 (О2): тема по сохранённому выбору; шрифт Montserrat (брендбук); брендовые цвета Fluent-элементов.
        Ui.SetDark(Settings.Current.Dark);
        RequestedThemeVariant = Settings.Current.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Resources["ContentControlThemeFontFamily"] = new FontFamily("avares://NurMarketSupport/Assets/Fonts#Montserrat, Segoe UI");
        Ui.AddBrandResources(Resources);
        Ui.AddStyles(Styles);
        QaQuiet.Install(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            L.Lang = Settings.Current.Lang;
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
            desktop.MainWindow = new LoginWindow();
            // 2026-10-27 (В-10): сервер сказал «вход недействителен» — один переход на экран входа, без потока сообщений об ошибке.
            Api.SessionExpired += () => Dispatcher.UIThread.Post(() => ForceLogin(L.T("Войдите снова — вход истёк, отозван или сменён пароль.",
                "Кайра кириңиз — кирүү мөөнөтү бүттү, жокко чыгарылды же сырсөз алмашты.", "Sign in again — the sign-in expired, was revoked or the password was changed.",
                "Yeniden giriş yapın — oturum süresi doldu, iptal edildi veya şifre değişti.", "Qayta kiring — kirish muddati tugadi, bekor qilindi yoki parol almashdi.")));
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Открытые окна сеансов (для «Выйти» и принудительного входа).</summary>
    public static readonly List<SessionWindow> Sessions = new();

    /// <summary>Закрыть все окна программы и открыть вход с сообщением.</summary>
    public static void ForceLogin(string notice)
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        // Сначала вход (иначе после закрытия последнего окна программа завершится), потом закрываем остальные.
        if (!desktop.Windows.OfType<LoginWindow>().Any())
            new LoginWindow(notice).Show();
        foreach (var w in desktop.Windows.ToList())
            if (w is not LoginWindow)
                w.Close();
    }
}
