#if !NURANDROID
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services.RemoteSupport;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-07, владелец: «начни разработку аналога AnyDesk для тех поддержки». Своя удалённая помощь NurMarket в окне
/// «Тех. поддержка»: «Получить код» → код и PIN крупно (кассир диктует оператору) → «Оператор X хочет подключиться» с кнопками
/// «Разрешить» / «Отклонить» (60 с) → во время сеанса поверх всех окон красная полоса «Идёт сеанс поддержки · Завершить».
/// Блок виден, только если задан адрес сервера поддержки (RemoteSupportHost.RelayUrl); иначе окно — как раньше, с AnyDesk.
/// Сеанс живёт отдельно от окна (RemoteSupportSession): закрыли окно — сеанс идёт, полоса остаётся.</summary>
public partial class RemoteSupportWindow
{
    private TextBlock? _nurStatus;
    private TextBlock? _nurCode;
    private Button? _nurStart;
    private Button? _nurStop;
    private Border? _nurConsent;
    private TextBlock? _nurConsentText;
    private TaskCompletionSource<bool>? _nurConsentAnswer;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Встроить блок своей поддержки над AnyDesk (вызов из конструктора).</summary>
    private void BuildNurSupport()
    {
        if (string.IsNullOrWhiteSpace(RemoteSupportHost.RelayUrl) || StatusText.Parent is not StackPanel host)
            return;
        var panel = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(16), BorderThickness = new Thickness(1),
        };
        panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("BrushAccentSoft"));
        panel.Bind(Border.BorderBrushProperty, panel.GetResourceObservable("BrushAccent"));
        var stack = new StackPanel { Spacing = 10 };
        var title = new TextBlock
        {
            Text = T("Поддержка NurMarket — без AnyDesk", "NurMarket колдоосу — AnyDesk'сиз", "NurMarket support — no AnyDesk", "NurMarket desteği — AnyDesk olmadan",
                "NurMarket yordami — AnyDesk'siz"),
            FontSize = 15, FontWeight = FontWeight.Bold,
        };
        _nurStatus = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        _nurCode = new TextBlock { FontSize = 30, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, IsVisible = false };
        _nurStart = new Button
        {
            Content = T("Получить код для поддержки", "Колдоо үчүн код алуу", "Get a code for support", "Destek için kod al", "Yordam uchun kod olish"),
            Classes = { "PrimaryActionButton" }, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _nurStart.Click += async (_, _) => await StartNurSupportAsync().ConfigureAwait(true);
        _nurStop = new Button
        {
            Content = T("Завершить", "Аяктоо", "End", "Bitir", "Tugatish"), Classes = { "SecondaryActionButton" },
            HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false,
        };
        _nurStop.Click += async (_, _) => await RemoteSupportSession.StopAsync().ConfigureAwait(true);
        _nurConsentText = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var allow = new Button { Content = T("Разрешить", "Уруксат берүү", "Allow", "İzin ver", "Ruxsat berish"), Classes = { "PrimaryActionButton" }, Margin = new Thickness(0, 0, 8, 0) };
        var deny = new Button { Content = T("Отклонить", "Четке кагуу", "Decline", "Reddet", "Rad etish"), Classes = { "SecondaryActionButton" } };
        allow.Click += (_, _) => AnswerConsent(true);
        deny.Click += (_, _) => AnswerConsent(false);
        _nurConsent = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), BorderThickness = new Thickness(2), IsVisible = false,
            Child = new StackPanel { Spacing = 10, Children = { _nurConsentText, new StackPanel { Orientation = Orientation.Horizontal, Children = { allow, deny } } } },
        };
        _nurConsent.Bind(Border.BorderBrushProperty, _nurConsent.GetResourceObservable("BrushWarning"));
        stack.Children.Add(title);
        stack.Children.Add(_nurStatus);
        stack.Children.Add(_nurCode);
        stack.Children.Add(_nurConsent);
        stack.Children.Add(_nurStart);
        stack.Children.Add(_nurStop);
        panel.Child = stack;
        host.Children.Insert(0, panel);
        Height = Math.Max(Height, 720);
        RemoteSupportSession.Changed += OnNurSessionChanged;
        Closed += (_, _) => RemoteSupportSession.Changed -= OnNurSessionChanged;
        OnNurSessionChanged();
    }

    private async Task StartNurSupportAsync()
    {
        var store = UserPreferences.Instance.StoreName;
        var version = typeof(RemoteSupportWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        await RemoteSupportSession.StartAsync(store, version, AskConsentAsync).ConfigureAwait(true);
    }

    private Task<bool> AskConsentAsync(string op)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            _nurConsentAnswer?.TrySetResult(false);
            _nurConsentAnswer = tcs;
            if (_nurConsent is null || _nurConsentText is null)
            {
                tcs.TrySetResult(false);
                return;
            }
            _nurConsentText.Text = T($"Оператор «{op}» хочет подключиться к этому компьютеру и увидеть экран. Разрешить?",
                $"«{op}» оператору ушул компьютерге туташып, экранды көргүсү келет. Уруксат бересизби?",
                $"Operator “{op}” wants to connect to this computer and see the screen. Allow?",
                $"«{op}» operatörü bu bilgisayara bağlanıp ekranı görmek istiyor. İzin verilsin mi?",
                $"«{op}» operatori ushbu kompyuterga ulanib, ekranni ko'rmoqchi. Ruxsat berilsinmi?");
            _nurConsent.IsVisible = true;
            WindowState = WindowState.Normal;
            Activate();
        });
        return tcs.Task;
    }

    private void AnswerConsent(bool accept)
    {
        if (_nurConsent is not null)
            _nurConsent.IsVisible = false;
        _nurConsentAnswer?.TrySetResult(accept);
        _nurConsentAnswer = null;
    }

    private void OnNurSessionChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_nurStatus is null || _nurCode is null || _nurStart is null || _nurStop is null)
            return;
        var s = RemoteSupportSession.Host?.Current;
        var busy = s is RemoteSupportHost.State.Connecting or RemoteSupportHost.State.WaitingOperator or RemoteSupportHost.State.AskingConsent
            or RemoteSupportHost.State.InSession;
        _nurStart.IsVisible = !busy;
        _nurStop.IsVisible = busy;
        _nurCode.IsVisible = s is RemoteSupportHost.State.WaitingOperator or RemoteSupportHost.State.AskingConsent && RemoteSupportSession.Code.Length == 9;
        if (_nurCode.IsVisible)
        {
            var c = RemoteSupportSession.Code;
            _nurCode.Text = $"{c[..3]} {c[3..6]} {c[6..]}   PIN {RemoteSupportSession.Pin}";
        }
        if (s != RemoteSupportHost.State.AskingConsent && _nurConsent is not null)
            _nurConsent.IsVisible = false;
        _nurStatus.Text = s switch
        {
            RemoteSupportHost.State.Connecting => T("Подключаюсь к серверу поддержки…", "Колдоо серверине туташып жатам…", "Connecting to the support server…",
                "Destek sunucusuna bağlanılıyor…", "Qo'llab-quvvatlash serveriga ulanmoqda…"),
            RemoteSupportHost.State.WaitingOperator => T("Продиктуйте оператору поддержки код и PIN. Код действует 15 минут. Без вашего разрешения никто не подключится.",
                "Колдоо операторуна код менен PIN'ди айтып бериңиз. Код 15 мүнөт жарактуу. Сиздин уруксатыңызсыз эч ким туташпайт.",
                "Read the code and PIN to the support operator. The code is valid for 15 minutes. Nobody can connect without your permission.",
                "Kodu ve PIN'i destek operatörüne söyleyin. Kod 15 dakika geçerlidir. İzniniz olmadan kimse bağlanamaz.",
                "Kod va PIN'ni qo'llab-quvvatlash operatoriga ayting. Kod 15 daqiqa amal qiladi. Ruxsatingizsiz hech kim ulanmaydi."),
            RemoteSupportHost.State.AskingConsent => T("Оператор просит доступ — ответьте ниже.", "Оператор уруксат сурап жатат — төмөндө жооп бериңиз.",
                "The operator is asking for access — answer below.", "Operatör erişim istiyor — aşağıdan yanıt verin.", "Operator ruxsat so'ramoqda — quyida javob bering."),
            RemoteSupportHost.State.InSession => T($"Идёт сеанс поддержки: {RemoteSupportSession.Host?.Operator}. Оператор видит экран и может управлять. «Завершить» — сразу отключить.",
                $"Колдоо сеансы жүрүп жатат: {RemoteSupportSession.Host?.Operator}. Оператор экранды көрүп, башкара алат. «Аяктоо» — дароо ажыратуу.",
                $"Support session in progress: {RemoteSupportSession.Host?.Operator}. The operator sees the screen and can control it. “End” disconnects at once.",
                $"Destek oturumu sürüyor: {RemoteSupportSession.Host?.Operator}. Operatör ekranı görüyor ve kontrol edebiliyor. «Bitir» — hemen bağlantıyı keser.",
                $"Qo'llab-quvvatlash seansi davom etmoqda: {RemoteSupportSession.Host?.Operator}. Operator ekranni ko'radi va boshqara oladi. «Tugatish» — darhol uzish."),
            _ => RemoteSupportSession.LastEnd.Length > 0
                ? RemoteSupportSession.LastEnd
                : T("Оператор поддержки NurMarket подключится к этому компьютеру по коду — только после вашего «Разрешить».",
                    "NurMarket колдоо оператору бул компьютерге код аркылуу туташат — сиздин «Уруксат берүү» баскычыңыздан кийин гана.",
                    "A NurMarket support operator connects to this computer with a code — only after you press “Allow”.",
                    "NurMarket destek operatörü bu bilgisayara kodla bağlanır — yalnızca siz «İzin ver»e bastıktan sonra.",
                    "NurMarket operatori bu kompyuterga kod orqali ulanadi — faqat siz «Ruxsat berish»ni bosgandan keyin."),
        };
    });
}

/// <summary>Один сеанс поддержки на программу: живёт дольше окна, держит красную полосу во время сеанса.</summary>
public static class RemoteSupportSession
{
    public static RemoteSupportHost? Host { get; private set; }
    public static string Code { get; private set; } = "";
    public static string Pin { get; private set; } = "";
    public static string LastEnd { get; private set; } = "";
    public static event Action? Changed;
    private static Window? _banner;

    public static async Task StartAsync(string company, string version, Func<string, Task<bool>> askConsent)
    {
        if (Host is { Current: not RemoteSupportHost.State.Ended })
            return;
        var host = new RemoteSupportHost();
        Host = host;
        Code = Pin = LastEnd = "";
        host.StateChanged += _ => Changed?.Invoke();
        host.CodeReceived += (code, pin, _) =>
        {
            Code = code;
            Pin = pin;
            Changed?.Invoke();
        };
        host.ConsentRequested += askConsent;
        host.SessionStarted += op => Dispatcher.UIThread.Post(() => ShowBanner(op));
        host.Ended += reason =>
        {
            LastEnd = reason;
            Changed?.Invoke();
            Dispatcher.UIThread.Post(HideBanner);
        };
        Changed?.Invoke();
        await Task.Run(() => host.RunAsync(company, version)).ConfigureAwait(true);
    }

    public static async Task StopAsync()
    {
        if (Host is { } host)
            await host.StopAsync().ConfigureAwait(true);
    }

    /// <summary>Красная полоса сверху посередине, поверх всех окон: кто подключён и «Завершить».</summary>
    private static void ShowBanner(string op)
    {
        HideBanner();
        var end = new Button
        {
            Content = Tr.T("Завершить", "Аяктоо", "End", "Bitir", "Tugatish"), Background = Brushes.White, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x10, 0x10)),
            FontWeight = FontWeight.Bold, Padding = new Thickness(14, 4), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        end.Click += async (_, _) => await StopAsync().ConfigureAwait(true);
        var text = new TextBlock
        {
            Text = Tr.T($"Идёт сеанс поддержки: {op}", $"Колдоо сеансы жүрүп жатат: {op}", $"Support session: {op}", $"Destek oturumu: {op}", $"Yordam seansi: {op}"),
            Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
        };
        _banner = new Window
        {
            SystemDecorations = SystemDecorations.None, Topmost = true, ShowInTaskbar = false, CanResize = false, SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), ShowActivated = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 6), Children = { text, end } },
        };
        _banner.Opened += (_, _) =>
        {
            if (_banner?.Screens.Primary is { } screen)
                _banner.Position = new PixelPoint(screen.WorkingArea.X + (int)((screen.WorkingArea.Width - _banner.Bounds.Width * screen.Scaling) / 2), screen.WorkingArea.Y);
        };
        _banner.Show();
    }

    private static void HideBanner()
    {
        _banner?.Close();
        _banner = null;
    }
}
#endif
