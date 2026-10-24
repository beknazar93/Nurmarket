#if !NURANDROID
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services.RemoteSupport;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-07, владелец: своя удалённая помощь для тех поддержки. «Нур Поддержка» в окне
/// «Тех. поддержка»: «Получить код» → код и PIN крупно (кассир диктует оператору) → «Оператор X хочет подключиться» с кнопками
/// «Разрешить» / «Отклонить» (60 с) → во время сеанса поверх всех окон красная полоса «Идёт сеанс поддержки · Завершить».
/// 2026-10-16: единственный способ удалённой помощи (сторонняя программа убрана) — блок есть всегда на Windows; адрес сервера —
/// RemoteSupportHost.RelayUrl, по умолчанию боевой (wss://support.nurcrm.kg/ws/host).
/// Сеанс живёт отдельно от окна (RemoteSupportSession): закрыли окно — сеанс идёт, полоса остаётся.</summary>
public partial class RemoteSupportWindow
{
    private TextBlock? _nurStatus;
    private TextBlock? _nurCode;
    private Button? _nurStart;
    private Button? _nurStop;
    private Border? _nurConsent;
    private TextBlock? _nurConsentText;
    private TaskCompletionSource<RemoteSupportHost.Consent>? _nurConsentAnswer;
    private string _nurConsentOp = "";
    // 2026-10-16: владелец окна, за сворачиванием которого следим (см. BuildNurSupport); null — нет владельца или окно закрыто.
    private Window? _nurWatchedOwner;
    // 2026-10-21: связь с сервером поддержки: null — проверяю, true — есть, false — нет (тогда «Получить код» выключена)
    // (пакет 3, п. 143).
    private bool? _nurServerOk;
    private Button? _nurRetry;
    private Border? _nurContacts;
    private RemoteSupportHost? _nurUnreachableHost;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Встроить блок «Нур Поддержка» в окно над StatusText (вызов из конструктора).</summary>
    private void BuildNurSupport()
    {
        // 2026-10-16: блок есть всегда (адрес сервера по умолчанию боевой). Касса-хост работает только в Windows (экран — GDI,
        // ввод — SendInput): на Linux блок не строится, там окно — как на Android.
        if (!OperatingSystem.IsWindows() || StatusText.Parent is not StackPanel host)
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
            // 2026-10-16: название блока — «Нур Поддержка» (так же в базе знаний).
            Text = T("Нур Поддержка", "Нур Колдоо", "Nur Support", "Nur Destek", "Nur Yordam"),
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
        // 2026-10-21: сервер недоступен — «Получить код» выключена, вместо неё можно проверить связь ещё раз (пакет 3, п. 143).
        _nurRetry = new Button
        {
            Content = T("Проверить связь ещё раз", "Байланышты кайра текшерүү", "Check the connection again", "Bağlantıyı tekrar kontrol et", "Aloqani qayta tekshirish"),
            Classes = { "SecondaryActionButton" }, HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false,
        };
        _nurRetry.Click += async (_, _) => await CheckNurServerAsync().ConfigureAwait(true);
        _nurStop = new Button
        {
            Content = T("Завершить", "Аяктоо", "End", "Bitir", "Tugatish"), Classes = { "SecondaryActionButton" },
            HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false,
        };
        _nurStop.Click += async (_, _) => await RemoteSupportSession.StopAsync().ConfigureAwait(true);
        _nurConsentText = new TextBlock { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        // 2026-10-15: «Разрешить» и «Только просмотр» — только касанием/мышью (Focusable = false): Enter сканера штрихкодов
        // в момент появления вопроса не должен дать доступ.
        var allow = new Button { Content = RemoteSupportSession.AllowText, Classes = { "PrimaryActionButton" }, Margin = new Thickness(0, 0, 8, 8), Focusable = false };
        var viewOnly = new Button { Content = RemoteSupportSession.ViewOnlyText, Classes = { "SecondaryActionButton" }, Margin = new Thickness(0, 0, 8, 8), Focusable = false };
        var deny = new Button { Content = RemoteSupportSession.DenyText, Classes = { "SecondaryActionButton" }, Margin = new Thickness(0, 0, 8, 8) };
        allow.Click += (_, _) => AnswerConsent(RemoteSupportHost.Consent.Allow);
        viewOnly.Click += (_, _) => AnswerConsent(RemoteSupportHost.Consent.ViewOnly);
        deny.Click += (_, _) => AnswerConsent(RemoteSupportHost.Consent.Deny);
        _nurConsent = new Border
        {
            CornerRadius = new CornerRadius(10), Padding = new Thickness(12), BorderThickness = new Thickness(2), IsVisible = false,
            Child = new StackPanel { Spacing = 10, Children = { _nurConsentText, new WrapPanel { Orientation = Orientation.Horizontal, Children = { allow, viewOnly, deny } } } },
        };
        _nurConsent.Bind(Border.BorderBrushProperty, _nurConsent.GetResourceObservable("BrushWarning"));
        stack.Children.Add(title);
        stack.Children.Add(_nurStatus);
        stack.Children.Add(_nurCode);
        stack.Children.Add(_nurConsent);
        stack.Children.Add(_nurStart);
        stack.Children.Add(_nurRetry);
        stack.Children.Add(_nurStop);
        panel.Child = stack;
        host.Children.Insert(0, panel);
        // 2026-10-21: телефон поддержки — нажимается (WhatsApp, копировать номер), отдельной карточкой под блоком; в программе
        // владельца карточка встаёт справа (PlaceNurContactsBeside) (пакет 3, п. 143).
        _nurContacts = BuildNurContacts();
        host.Children.Insert(1, _nurContacts);
        Height = Math.Max(Height, 720);
        RemoteSupportSession.Changed += OnNurSessionChanged;
        // 2026-10-15: сеанс живёт дольше окна — вопрос кассиру идёт в окно, только пока оно открыто и видно; закрыли или
        // спрятали (раздел владельца) — вопрос переезжает в отдельное окно поверх программы. Заново открытое окно
        // подключается к идущему сеансу здесь же (раньше StartAsync при живом сеансе молча выходил, вопрос висел в закрытом окне).
        RemoteSupportSession.AttachWindow(this);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible)
                MoveConsentToDialog();
        };
        // 2026-10-16: окно открыто с владельцем (главным окном). Свернули главное, пока вопрос здесь, — Windows прячет и это
        // окно, а IsVisible у него остаётся true: вопрос тоже переезжает в отдельное окно поверх всех.
        Opened += (_, _) =>
        {
            if (Owner is Window owner)
            {
                // 2026-10-23: Opened приходит при каждом показе (раздел владельца прячут и показывают), а Closed снимал подписку
                // один раз — остальные держали закрытое окно через главное. Сначала снимаем прежнюю.
                if (_nurWatchedOwner is { } prev)
                    prev.PropertyChanged -= OnNurOwnerPropertyChanged;
                _nurWatchedOwner = owner;
                owner.PropertyChanged += OnNurOwnerPropertyChanged;
            }
        };
        Closed += (_, _) =>
        {
            RemoteSupportSession.Changed -= OnNurSessionChanged;
            RemoteSupportSession.DetachWindow(this);
            if (_nurWatchedOwner is { } watched)
                watched.PropertyChanged -= OnNurOwnerPropertyChanged;
            _nurWatchedOwner = null;
            MoveConsentToDialog();
        };
        OnNurSessionChanged();
        _ = CheckNurServerAsync();
    }

    /// <summary>2026-10-21: проверка связи с сервером поддержки при открытии окна и по кнопке «Проверить связь ещё раз» (пакет 3, п. 143).</summary>
    private async Task CheckNurServerAsync()
    {
        _nurServerOk = null;
        OnNurSessionChanged();
        var ok = await RemoteSupportSession.IsServerReachableAsync().ConfigureAwait(true);
        _nurServerOk = ok;
        OnNurSessionChanged();
    }

    /// <summary>2026-10-21: карточка «Связаться с поддержкой»: номер крупно, «Написать в WhatsApp», «Скопировать номер»
    /// (раньше номер был только словами во вступлении и не нажимался) (пакет 3, п. 143).</summary>
    private Border BuildNurContacts()
    {
        var card = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16), BorderThickness = new Thickness(1) };
        card.Bind(Border.BackgroundProperty, card.GetResourceObservable("BrushPanel"));
        card.Bind(Border.BorderBrushProperty, card.GetResourceObservable("BrushBorder"));
        var title = new TextBlock
        {
            Text = T("Связаться с поддержкой", "Колдоо менен байланышуу", "Contact support", "Destekle iletişim", "Yordam xizmati bilan bog'lanish"),
            FontSize = 15, FontWeight = FontWeight.Bold,
        };
        var phone = new SelectableTextBlock { Text = RemoteSupportHost.SupportPhone, FontSize = 22, FontWeight = FontWeight.Bold };
        var hint = new TextBlock
        {
            Text = T("Звонок или WhatsApp — один номер.", "Чалуу же WhatsApp — бир эле номер.", "Call or WhatsApp — the same number.",
                "Arama veya WhatsApp — aynı numara.", "Qo'ng'iroq yoki WhatsApp — bitta raqam."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        var whatsApp = new Button
        {
            Content = T("Написать в WhatsApp", "WhatsApp'ка жазуу", "Message on WhatsApp", "WhatsApp'tan yaz", "WhatsApp'ga yozish"),
            Classes = { "SecondaryActionButton" }, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        whatsApp.Click += (_, _) => SiteOrdersWindow.OpenUrl("https://wa.me/" + new string(RemoteSupportHost.SupportPhone.Where(char.IsDigit).ToArray()));
        var copy = new Button
        {
            Content = T("Скопировать номер", "Номерди көчүрүү", "Copy the number", "Numarayı kopyala", "Raqamni nusxalash"),
            Classes = { "SecondaryActionButton" }, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        copy.Click += async (_, _) =>
        {
            try
            {
                if (GetTopLevel(this)?.Clipboard is { } clipboard)
                {
                    await clipboard.SetTextAsync(RemoteSupportHost.SupportPhone).ConfigureAwait(true);
                    StatusText.Text = T("Номер поддержки скопирован.", "Колдоо номери көчүрүлдү.", "The support number is copied.",
                        "Destek numarası kopyalandı.", "Yordam raqami nusxalandi.");
                }
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Поддержка: номер не скопирован: {ex.Message}", "WARNING");
            }
        };
        card.Child = new StackPanel
        {
            Spacing = 8,
            Children = { title, phone, hint, whatsApp, copy },
        };
        return card;
    }

    /// <summary>2026-10-21: программа владельца — карточка контактов и «Скопировать информацию об устройстве» справа от блока
    /// «Нур Поддержка», а не пустая правая половина и кнопка отдельно по центру (пакет 3, п. 143).</summary>
    private void PlaceNurContactsBeside()
    {
        if (_nurContacts?.Parent is not Panel from)
            return;
        from.Children.Remove(_nurContacts);
        RootGrid.ColumnDefinitions = new ColumnDefinitions("*,24,300");
        foreach (var child in RootGrid.Children)
            Grid.SetColumn(child, 0);
        Grid.SetColumn(_nurContacts, 2);
        Grid.SetRow(_nurContacts, 1);
        Grid.SetRowSpan(_nurContacts, 2);
        _nurContacts.VerticalAlignment = VerticalAlignment.Top;
        RootGrid.Children.Add(_nurContacts);
        if (CopyDeviceInfoButton.Parent is Panel buttons && _nurContacts.Child is StackPanel card)
        {
            buttons.Children.Remove(CopyDeviceInfoButton);
            CopyDeviceInfoButton.Margin = new Thickness(0);
            CopyDeviceInfoButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            // Длинная надпись в узкой колонке — в две строки, а не обрезанная по высоте 44.
            CopyDeviceInfoButton.Height = double.NaN;
            CopyDeviceInfoButton.MinHeight = 44;
            card.Children.Add(CopyDeviceInfoButton);
        }
        RootGrid.MaxWidth = 940;
    }

    private async Task StartNurSupportAsync()
    {
        var store = UserPreferences.Instance.StoreName;
        var version = typeof(RemoteSupportWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        await RemoteSupportSession.StartAsync(store, version).ConfigureAwait(true);
    }

    /// <summary>2026-10-16: главное окно свернули — вопрос (если он сейчас здесь) переезжает в отдельное окно.</summary>
    private void OnNurOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowStateProperty && sender is Window { WindowState: WindowState.Minimized })
            MoveConsentToDialog();
    }

    /// <summary>2026-10-15: показать вопрос кассиру в этом окне (вызов из RemoteSupportSession на UI-потоке). false — блока нет.</summary>
    internal bool ShowConsentInWindow(string op, TaskCompletionSource<RemoteSupportHost.Consent> tcs)
    {
        if (_nurConsent is null || _nurConsentText is null)
            return false;
        _nurConsentAnswer?.TrySetResult(RemoteSupportHost.Consent.Deny);
        _nurConsentAnswer = tcs;
        _nurConsentOp = op;
        _nurConsentText.Text = RemoteSupportSession.ConsentQuestion(op);
        _nurConsent.IsVisible = true;
        WindowState = WindowState.Normal;
        Activate();
        return true;
    }

    /// <summary>2026-10-15: окно закрыли или спрятали, пока кассира спрашивали — тот же вопрос в отдельном окне (не авто-ответ).</summary>
    private void MoveConsentToDialog()
    {
        if (_nurConsentAnswer is not { } tcs)
            return;
        _nurConsentAnswer = null;
        if (_nurConsent is not null)
            _nurConsent.IsVisible = false;
        if (tcs.Task.IsCompleted)
            return;
        try
        {
            if (RemoteSupportSession.Host?.Current == RemoteSupportHost.State.AskingConsent)
                RemoteSupportSession.ShowConsentDialog(_nurConsentOp, tcs);
            else
                tcs.TrySetResult(RemoteSupportHost.Consent.Deny);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: вопрос кассиру не перенесён в отдельное окно ({ex.Message}) — отказ.", "WARNING");
            tcs.TrySetResult(RemoteSupportHost.Consent.Deny);
        }
    }

    private void AnswerConsent(RemoteSupportHost.Consent consent)
    {
        if (_nurConsent is not null)
            _nurConsent.IsVisible = false;
        _nurConsentAnswer?.TrySetResult(consent);
        _nurConsentAnswer = null;
    }

    private void OnNurSessionChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_nurStatus is null || _nurCode is null || _nurStart is null || _nurStop is null)
            return;
        var s = RemoteSupportSession.Host?.Current;
        var busy = s is RemoteSupportHost.State.Connecting or RemoteSupportHost.State.WaitingOperator or RemoteSupportHost.State.AskingConsent
            or RemoteSupportHost.State.InSession;
        // 2026-10-21: последняя попытка не дошла до сервера — значит, связи нет (пакет 3, п. 143).
        // Один раз на попытку: после «Проверить связь ещё раз» та же старая попытка связь уже не выключает.
        if (s == RemoteSupportHost.State.Ended && RemoteSupportSession.LastEnd == RemoteSupportHost.UnreachableText()
            && !ReferenceEquals(_nurUnreachableHost, RemoteSupportSession.Host))
        {
            _nurUnreachableHost = RemoteSupportSession.Host;
            _nurServerOk = false;
        }
        _nurStart.IsVisible = !busy;
        _nurStart.IsEnabled = _nurServerOk == true;
        if (_nurRetry is not null)
            _nurRetry.IsVisible = !busy && _nurServerOk == false;
        _nurStop.IsVisible = busy;
        _nurCode.IsVisible = s is RemoteSupportHost.State.WaitingOperator or RemoteSupportHost.State.AskingConsent && RemoteSupportSession.Code.Length == 9;
        if (_nurCode.IsVisible)
        {
            var c = RemoteSupportSession.Code;
            _nurCode.Text = $"{c[..3]} {c[3..6]} {c[6..]}   PIN {RemoteSupportSession.Pin}";
        }
        if (s != RemoteSupportHost.State.AskingConsent && _nurConsent is not null)
        {
            _nurConsent.IsVisible = false;
            // 2026-10-15: вопрос уже не актуален (истёк или сеанс кончился) — не переносить его потом в отдельное окно.
            _nurConsentAnswer?.TrySetResult(RemoteSupportHost.Consent.Deny);
            _nurConsentAnswer = null;
        }
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
            // 2026-10-15: кассир выбрал «Только просмотр» (или оператор сам включил) — так и пишем.
            RemoteSupportHost.State.InSession when RemoteSupportSession.Host?.ViewOnly == true => T(
                $"Идёт сеанс поддержки: {RemoteSupportSession.Host?.Operator}. Оператор только видит экран — мышь и клавиатура ему не доступны. «Завершить» — сразу отключить.",
                $"Колдоо сеансы жүрүп жатат: {RemoteSupportSession.Host?.Operator}. Оператор экранды гана көрөт — чычкан менен клавиатура ага жеткиликсиз. «Аяктоо» — дароо ажыратуу.",
                $"Support session in progress: {RemoteSupportSession.Host?.Operator}. The operator only sees the screen — no mouse or keyboard control. “End” disconnects at once.",
                $"Destek oturumu sürüyor: {RemoteSupportSession.Host?.Operator}. Operatör yalnızca ekranı görüyor — fare ve klavye kullanamaz. «Bitir» — hemen bağlantıyı keser.",
                $"Qo'llab-quvvatlash seansi davom etmoqda: {RemoteSupportSession.Host?.Operator}. Operator faqat ekranni ko'radi — sichqoncha va klaviatura unga yopiq. «Tugatish» — darhol uzish."),
            RemoteSupportHost.State.InSession => T($"Идёт сеанс поддержки: {RemoteSupportSession.Host?.Operator}. Оператор видит экран и может управлять. «Завершить» — сразу отключить.",
                $"Колдоо сеансы жүрүп жатат: {RemoteSupportSession.Host?.Operator}. Оператор экранды көрүп, башкара алат. «Аяктоо» — дароо ажыратуу.",
                $"Support session in progress: {RemoteSupportSession.Host?.Operator}. The operator sees the screen and can control it. “End” disconnects at once.",
                $"Destek oturumu sürüyor: {RemoteSupportSession.Host?.Operator}. Operatör ekranı görüyor ve kontrol edebiliyor. «Bitir» — hemen bağlantıyı keser.",
                $"Qo'llab-quvvatlash seansi davom etmoqda: {RemoteSupportSession.Host?.Operator}. Operator ekranni ko'radi va boshqara oladi. «Tugatish» — darhol uzish."),
            // 2026-10-21: без связи — одно понятное сообщение (кнопка «Получить код» при этом выключена) (пакет 3, п. 143).
            _ when _nurServerOk == false => RemoteSupportHost.UnreachableText(),
            _ when _nurServerOk is null => T("Проверяю связь с сервером поддержки…", "Колдоо сервери менен байланышты текшерип жатам…",
                "Checking the connection to the support server…", "Destek sunucusuyla bağlantı kontrol ediliyor…", "Yordam serveri bilan aloqa tekshirilmoqda…"),
            // 2026-10-21: без названия «NurMarket» — в окне и так «Нур Поддержка» и NurCRM (пакет 3, п. 143).
            _ => RemoteSupportSession.LastEnd.Length > 0 && RemoteSupportSession.LastEnd != RemoteSupportHost.UnreachableText()
                ? RemoteSupportSession.LastEnd
                : T("Оператор поддержки подключится к этому компьютеру по коду — только после вашего «Разрешить».",
                    "Колдоо оператору бул компьютерге код аркылуу туташат — сиздин «Уруксат берүү» баскычыңыздан кийин гана.",
                    "A support operator connects to this computer with a code — only after you press “Allow”.",
                    "Destek operatörü bu bilgisayara kodla bağlanır — yalnızca siz «İzin ver»e bastıktan sonra.",
                    "Yordam operatori bu kompyuterga kod orqali ulanadi — faqat siz «Ruxsat berish»ni bosgandan keyin."),
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
    // 2026-10-15: открытое окно «Тех. поддержка» (вопрос кассиру — в нём, пока оно видно); null — окно закрыто.
    private static RemoteSupportWindow? _window;

    internal static void AttachWindow(RemoteSupportWindow window) => _window = window;

    internal static void DetachWindow(RemoteSupportWindow window)
    {
        if (ReferenceEquals(_window, window))
            _window = null;
    }

    // 2026-10-15: текст вопроса честный — «Разрешить» даёт не только экран, но и мышь с клавиатурой.
    internal static string ConsentQuestion(string op) => Tr.T(
        $"Оператор «{op}» хочет подключиться к этому компьютеру: видеть экран и управлять мышью и клавиатурой. «Разрешить» — полный доступ, «Только просмотр» — только экран, без управления.",
        $"«{op}» оператору ушул компьютерге туташкысы келет: экранды көрүп, чычкан менен клавиатураны башкаруу. «Уруксат берүү» — толук кирүү, «Көрүү гана» — экран гана, башкаруусуз.",
        $"Operator “{op}” wants to connect to this computer: see the screen and control the mouse and keyboard. “Allow” gives full access, “View only” shows the screen without control.",
        $"«{op}» operatörü bu bilgisayara bağlanmak istiyor: ekranı görmek ve fare ile klavyeyi kontrol etmek. «İzin ver» — tam erişim, «Yalnızca izle» — kontrol olmadan yalnızca ekran.",
        $"«{op}» operatori ushbu kompyuterga ulanmoqchi: ekranni ko'rish va sichqoncha hamda klaviaturani boshqarish. «Ruxsat berish» — to'liq kirish, «Faqat ko'rish» — boshqaruvsiz faqat ekran.");

    internal static string AllowText => Tr.T("Разрешить", "Уруксат берүү", "Allow", "İzin ver", "Ruxsat berish");
    internal static string ViewOnlyText => Tr.T("Только просмотр", "Көрүү гана", "View only", "Yalnızca izle", "Faqat ko'rish");
    internal static string DenyText => Tr.T("Отклонить", "Четке кагуу", "Decline", "Reddet", "Rad etish");

    public static async Task StartAsync(string company, string version)
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
        host.ConsentRequested += AskConsentAsync; // 2026-10-15: не к экземпляру окна — окно могут закрыть, а сеанс идёт
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

    /// <summary>2026-10-21: есть ли связь с сервером поддержки — обычный HTTPS-запрос к его адресу (через прокси системы, как
    /// и само подключение); любой ответ сервера, даже «не найдено», — связь есть. Ничего не отправляет и сеанс не открывает
    /// (пакет 3, п. 143).</summary>
    internal static async Task<bool> IsServerReachableAsync()
    {
        try
        {
            var relay = new Uri(RemoteSupportHost.RelayUrl);
            if (relay.Scheme != "wss" && !(relay.Scheme == "ws" && relay.IsLoopback))
                return false;
            var probe = new UriBuilder(relay) { Scheme = relay.Scheme == "wss" ? "https" : "http", Path = "/", Query = "" }.Uri;
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var response = await client.GetAsync(probe, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Удалённая помощь: сервер поддержки недоступен ({ex.Message}).", "INFO");
            return false;
        }
    }

    public static async Task StopAsync()
    {
        if (Host is { } host)
            await host.StopAsync().ConfigureAwait(true);
    }

    /// <summary>2026-10-15: вопрос кассиру — в окне «Тех. поддержка», если оно открыто и видно, иначе отдельным окном поверх
    /// программы. Раньше вопрос уходил в закрытое окно, кассир его не видел, через 60 с — отказ.</summary>
    private static Task<RemoteSupportHost.Consent> AskConsentAsync(string op)
    {
        var tcs = new TaskCompletionSource<RemoteSupportHost.Consent>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // 2026-10-16: окно «Тех. поддержка» — только если оно действительно на экране (не свёрнуто, главное окно не свёрнуто).
                if (_window is { } window && IsOnScreen(window) && window.ShowConsentInWindow(op, tcs))
                    return;
                ShowConsentDialog(op, tcs);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Удалённая помощь: вопрос кассиру не показан ({ex.Message}) — отказ.", "WARNING");
                tcs.TrySetResult(RemoteSupportHost.Consent.Deny);
            }
        });
        return tcs.Task;
    }

    /// <summary>2026-10-16: окно действительно видно кассиру: показано, не свёрнуто, и его владелец (главное окно) тоже.
    /// В Windows окно с владельцем прячется вместе со свёрнутым владельцем, а IsVisible у него остаётся true.</summary>
    private static bool IsOnScreen(Window window)
    {
        for (var w = window; w is not null; w = w.Owner as Window)
        {
            if (!w.IsVisible || w.WindowState == WindowState.Minimized)
                return false;
        }
        return true;
    }

    /// <summary>2026-10-15: отдельное окно с вопросом кассиру, поверх всех окон.
    /// Ответ — только кнопкой; закрыли окно или Esc — отказ; вопрос истёк на кассе — окно закрывается само.
    /// 2026-10-16: без владельца (см. ниже у Show).</summary>
    internal static void ShowConsentDialog(string op, TaskCompletionSource<RemoteSupportHost.Consent> tcs)
    {
        Window? dialog = null;
        void Answer(RemoteSupportHost.Consent consent)
        {
            tcs.TrySetResult(consent);
            dialog?.Close();
        }
        // 2026-10-15: «Разрешить» и «Только просмотр» не берут фокус клавиатуры: Enter сканера не должен дать доступ.
        var allow = ConsentButton(AllowText, "BrushAccent", "BrushAccentForeground");
        var viewOnly = ConsentButton(ViewOnlyText, "BrushPanelSoft", "BrushText");
        var deny = ConsentButton(DenyText, "BrushPanelSoft", "BrushText");
        allow.Focusable = false;
        viewOnly.Focusable = false;
        allow.Click += (_, _) => Answer(RemoteSupportHost.Consent.Allow);
        viewOnly.Click += (_, _) => Answer(RemoteSupportHost.Consent.ViewOnly);
        deny.Click += (_, _) => Answer(RemoteSupportHost.Consent.Deny);
        // 2026-10-22 (модалки, пакет 2): без заголовка Windows поверх карточки (рамка была двойной) — карточка как у модалок:
        // скругление, фон панели, тень, поля окна под тень; рамка цвета предупреждения остаётся (это вопрос о доступе).
        // ✕ нет намеренно (макет): ответ — только кнопкой, Esc = «Отказать».
        var frame = new Border
        {
            Padding = new Thickness(20), BorderThickness = new Thickness(2), Margin = new Thickness(16),
            CornerRadius = new CornerRadius(14), BoxShadow = BoxShadows.Parse("0 12 32 0 #33101828"),
            Child = new StackPanel
            {
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = ConsentQuestion(op), FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
                    new WrapPanel { Orientation = Orientation.Horizontal, Children = { allow, viewOnly, deny } },
                },
            },
        };
        frame.Bind(Border.BorderBrushProperty, frame.GetResourceObservable("BrushWarning"));
        frame.Bind(Border.BackgroundProperty, frame.GetResourceObservable("BrushDialogPanel"));
        dialog = new Window
        {
            Title = Tr.T("Удалённая помощь NurMarket", "NurMarket алыстан жардам", "NurMarket remote support", "NurMarket uzaktan destek", "NurMarket masofaviy yordam"),
            SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Topmost = true, ShowInTaskbar = true,
            SystemDecorations = SystemDecorations.None,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = frame,
        };
        dialog.Bind(Window.BackgroundProperty, dialog.GetResourceObservable("DialogWindowBackground"));
        dialog.Bind(TopLevel.TransparencyLevelHintProperty, dialog.GetResourceObservable("DialogTransparencyHint"));
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Answer(RemoteSupportHost.Consent.Deny);
        };
        // 2026-10-15: вопрос истёк (60 с) или сеанс кончился — закрыть окно; состояние читаем в момент изменения, не позже.
        void OnChanged()
        {
            if (Host?.Current != RemoteSupportHost.State.AskingConsent)
                Dispatcher.UIThread.Post(() => Answer(RemoteSupportHost.Consent.Deny));
        }
        Changed += OnChanged;
        dialog.Closed += (_, _) =>
        {
            Changed -= OnChanged;
            tcs.TrySetResult(RemoteSupportHost.Consent.Deny); // 2026-10-15: закрыли крестиком — отказ, не согласие
        };
        dialog.Opened += (_, _) => dialog?.Activate();
        // 2026-10-16: всегда без владельца. С владельцем — главным окном — Windows прятал вопрос, пока главное окно свёрнуто
        // (или его свернули во время вопроса): кассир его не видел, через 60 с — отказ. Без владельца окно Topmost, со своей
        // кнопкой на панели задач, по центру экрана и видно при любом состоянии главного окна; закрывается само (OnChanged).
        dialog.Show();
        dialog.Activate();
    }

    private static Button ConsentButton(string text, string background, string foreground)
    {
        var button = new Button
        {
            Content = text, FontSize = 15, FontWeight = FontWeight.SemiBold, MinHeight = 44, Padding = new Thickness(18, 10),
            Margin = new Thickness(0, 0, 8, 8), Cursor = new Cursor(StandardCursorType.Hand),
        };
        button.Bind(Button.BackgroundProperty, button.GetResourceObservable(background));
        button.Bind(Button.ForegroundProperty, button.GetResourceObservable(foreground));
        return button;
    }

    /// <summary>Красная полоса сверху посередине, поверх всех окон: кто подключён и «Завершить».</summary>
    private static void ShowBanner(string op)
    {
        HideBanner();
        var end = new Button
        {
            Content = Tr.T("Завершить", "Аяктоо", "End", "Bitir", "Tugatish"), Background = Brushes.White, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x10, 0x10)),
            FontWeight = FontWeight.Bold, Padding = new Thickness(16, 4), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            // 2026-10-22 (модалки, пакет 2): «Завершить» 44 px — пальцем на сенсорной кассе (было ~30 px).
            MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Повторное нажатие, пока сеанс завершается, — ничего (было: второй вызов StopAsync).
        var stopping = false;
        end.Click += async (_, _) =>
        {
            if (stopping)
                return;
            stopping = true;
            end.IsEnabled = false;
            try
            {
                await StopAsync().ConfigureAwait(true);
            }
            finally
            {
                stopping = false;
                end.IsEnabled = true;
            }
        };
        var text = new TextBlock
        {
            Text = Tr.T($"Идёт сеанс поддержки: {op}", $"Колдоо сеансы жүрүп жатат: {op}", $"Support session: {op}", $"Destek oturumu: {op}", $"Yordam seansi: {op}"),
            Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
        };
        _banner = new Window
        {
            SystemDecorations = SystemDecorations.None, Topmost = true, ShowInTaskbar = false, CanResize = false, SizeToContent = SizeToContent.WidthAndHeight,
            Background = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), ShowActivated = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 4), Children = { text, end } },
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
