using System.Windows.Input;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels;

/// <summary>2026-09-09: какой именно экран сейчас показан в форме входа — обычный NurCRM-логин
/// или один из шагов автономного (офлайн) режима.</summary>
public enum AuthScreen
{
    NurCrmLogin,
    AutonomousActivation,
    AutonomousCreateAccount,
    AutonomousLogin,
}

/// <summary>UI-independent authentication view-model for WPF and Avalonia.</summary>
public class AuthViewModel : ViewModelBase
{
    private readonly IOnlineOfflineAuthenticationService _authentication;
    private readonly IAutonomousAuthService _autonomous;
    private readonly IAppSession _appSession;
    private string _username = "";
    private string _password = "";
    private bool _rememberMe;
    private bool _isLoading;
    private bool _isOfflineMode;
    private string _errorMessage = "";
    private string _loadingStatus = "";
    private AuthScreen _currentScreen = AuthScreen.NurCrmLogin;
    private string _activationKey = "";
    private string _localEmail = "";
    private string _localPassword = "";
    private string _localDisplayName = "";

    public AuthViewModel(
        IOnlineOfflineAuthenticationService authentication,
        IAutonomousAuthService autonomous,
        IAppSession appSession)
    {
        _authentication = authentication;
        _autonomous = autonomous;
        _appSession = appSession;
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsLoading);
        AutoLoginCommand = new AsyncRelayCommand(AutoLoginAsync, () => !IsLoading);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsLoading);
        ShowAutonomousCommand = new RelayCommand(ShowAutonomousEntry);
        ShowNurCrmCommand = new RelayCommand(() => SwitchScreen(AuthScreen.NurCrmLogin));
        ActivateCommand = new AsyncRelayCommand(ActivateAsync, () => !IsLoading);
        CreateLocalAccountCommand = new AsyncRelayCommand(CreateLocalAccountAsync, () => !IsLoading);
        AutonomousLoginCommand = new AsyncRelayCommand(AutonomousLoginAsync, () => !IsLoading);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value ?? "");
    }

    /// <remarks>Held only in memory for the duration of the login attempt.</remarks>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value ?? "");
    }

    public bool RememberMe
    {
        get => _rememberMe;
        set => SetProperty(ref _rememberMe, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value))
                return;
            RaiseCommandStates();
        }
    }

    public bool IsOfflineMode
    {
        get => _isOfflineMode;
        private set
        {
            if (!SetProperty(ref _isOfflineMode, value))
                return;
            OnPropertyChanged(nameof(LoginButtonText));
            OnPropertyChanged(nameof(LoginButtonIsOfflineAccent));
        }
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value ?? ""))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public string LoadingStatus
    {
        get => _loadingStatus;
        private set => SetProperty(ref _loadingStatus, value ?? "");
    }

    public string LoginButtonText => IsOfflineMode
        ? Tr.T("Работа без сети", "Тармаксыз иштөө", "Working offline", "Ağsız çalışma", "Tarmoqsiz ishlash")
        : Tr.T("Войти", "Кирүү", "Log in", "Giriş yap", "Kirish");
    public bool LoginButtonIsOfflineAccent => IsOfflineMode;

    /// <summary>2026-09-09: какой экран формы входа сейчас показан — обычный NurCRM или один из
    /// шагов автономного (офлайн) режима.</summary>
    public AuthScreen CurrentScreen
    {
        get => _currentScreen;
        private set
        {
            if (!SetProperty(ref _currentScreen, value))
                return;
            OnPropertyChanged(nameof(IsNurCrmScreen));
            OnPropertyChanged(nameof(IsActivationScreen));
            OnPropertyChanged(nameof(IsCreateAccountScreen));
            OnPropertyChanged(nameof(IsAutonomousLoginScreen));
        }
    }

    public bool IsNurCrmScreen => CurrentScreen == AuthScreen.NurCrmLogin;
    public bool IsActivationScreen => CurrentScreen == AuthScreen.AutonomousActivation;
    public bool IsCreateAccountScreen => CurrentScreen == AuthScreen.AutonomousCreateAccount;
    public bool IsAutonomousLoginScreen => CurrentScreen == AuthScreen.AutonomousLogin;

    public bool IsAutonomousActivated => _autonomous.IsActivated;
    public bool HasLocalAccount => _autonomous.HasLocalAccount;

    public string ActivationKey
    {
        get => _activationKey;
        set => SetProperty(ref _activationKey, value ?? "");
    }

    public string LocalEmail
    {
        get => _localEmail;
        set => SetProperty(ref _localEmail, value ?? "");
    }

    public string LocalPassword
    {
        get => _localPassword;
        set => SetProperty(ref _localPassword, value ?? "");
    }

    public string LocalDisplayName
    {
        get => _localDisplayName;
        set => SetProperty(ref _localDisplayName, value ?? "");
    }

    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand AutoLoginCommand { get; }
    public ICommand ShowAutonomousCommand { get; }
    public ICommand ShowNurCrmCommand { get; }
    public ICommand ActivateCommand { get; }
    public ICommand CreateLocalAccountCommand { get; }
    public ICommand AutonomousLoginCommand { get; }

    public event Func<Task>? LoginSuccess;
    public event Func<Task>? LogoutSuccess;
    /// <summary>Requests wiping native UI control buffers as well as VM fields.</summary>
    public event Action? CredentialsClearRequested;

    public void SetLoadingStatus(string status) => LoadingStatus = status;

    /// <summary>Clears both bindable inputs and platform-native control buffers.</summary>
    public void ClearCredentialInputs() => ClearUiCredentials();

    public void ReportError(string message)
    {
        ErrorMessage = message;
        LoadingStatus = "";
        IsLoading = false;
    }

    private async Task LoginAsync()
    {
        ErrorMessage = "";
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            ErrorMessage = Tr.T("Введите логин и пароль.", "Логин менен паролду киргизиңиз.", "Enter your login and password.", "Kullanıcı adı ve şifrenizi girin.", "Login va parolni kiriting.");
            return;
        }

        IsLoading = true;
        LoadingStatus = Tr.T("Авторизация…", "Аутентификация…", "Authorizing…", "Yetkilendiriliyor…", "Avtorizatsiya…");
        try
        {
            var result = await _authentication.LoginAsync(
                Username,
                Password,
                RememberMe,
                CancellationToken.None).ConfigureAwait(true);
            await HandleResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T($"Не удалось выполнить вход: {ex.Message}", $"Кирүү мүмкүн болгон жок: {ex.Message}",
                $"Could not sign in: {ex.Message}", $"Giriş yapılamadı: {ex.Message}", $"Tizimga kirib bo'lmadi: {ex.Message}");
        }
        finally
        {
            // Clear after success. On failure the UI may let the user correct only
            // the login; retaining the in-memory value avoids a hidden mismatch
            // with Avalonia's non-bindable password control.
            if (!string.IsNullOrWhiteSpace(_appSession.CurrentUserId))
                Password = "";
            if (IsLoading)
            {
                IsLoading = false;
                LoadingStatus = "";
            }
        }
    }

    private async Task AutoLoginAsync()
    {
        ErrorMessage = "";
        IsLoading = true;
        LoadingStatus = Tr.T("Проверка сохранённой сессии…", "Сакталган сессия текшерилүүдө…", "Checking the saved session…", "Kaydedilen oturum kontrol ediliyor…", "Saqlangan sessiya tekshirilmoqda…");
        try
        {
            var result = await _authentication.AutoLoginAsync(CancellationToken.None)
                .ConfigureAwait(true);
            if (!result.IsSuccess && result.Failure == AuthenticationFailure.SessionExpired &&
                string.Equals(result.ErrorMessage, "Сохранённая сессия отсутствует.", StringComparison.Ordinal))
                return; // normal first launch

            if (result.IsSuccess)
                RememberMe = true;
            await HandleResultAsync(result).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T($"Не удалось восстановить сессию: {ex.Message}", $"Сессияны калыбына келтирүү мүмкүн болгон жок: {ex.Message}",
                $"Could not restore the session: {ex.Message}", $"Oturum geri yüklenemedi: {ex.Message}", $"Sessiyani tiklab bo'lmadi: {ex.Message}");
        }
        finally
        {
            if (IsLoading)
            {
                IsLoading = false;
                LoadingStatus = "";
            }
        }
    }

    private async Task LogoutAsync()
    {
        IsLoading = true;
        try
        {
            await _authentication.LogoutAsync(CancellationToken.None).ConfigureAwait(true);
            _autonomous.EndAutonomousSession();
            ClearApplicationSession();
            ClearUiCredentials();
            IsOfflineMode = false;
            SwitchScreen(AuthScreen.NurCrmLogin);
            await RaiseAsync(LogoutSuccess).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ------------------------------------------------------------------
    //  2026-09-09: автономный (офлайн, без NurCRM) режим — ключ активации
    //  проверяется онлайн ровно один раз (GithubLicenseRegistryClient),
    //  дальше локальный логин/пароль работают без интернета вообще.
    // ------------------------------------------------------------------

    private void SwitchScreen(AuthScreen screen)
    {
        ErrorMessage = "";
        CurrentScreen = screen;
    }

    private void ShowAutonomousEntry()
    {
        SwitchScreen(_autonomous.IsActivated
            ? AuthScreen.AutonomousLogin
            : AuthScreen.AutonomousActivation);
    }

    private async Task ActivateAsync()
    {
        ErrorMessage = "";
        if (string.IsNullOrWhiteSpace(ActivationKey))
        {
            ErrorMessage = Tr.T("Введите ключ активации.", "Активация ачкычын киргизиңиз.", "Enter the activation key.", "Etkinleştirme anahtarını girin.", "Faollashtirish kalitini kiriting.");
            return;
        }

        IsLoading = true;
        LoadingStatus = Tr.T("Проверка ключа…", "Ачкыч текшерилүүдө…", "Checking the key…", "Anahtar kontrol ediliyor…", "Kalit tekshirilmoqda…");
        try
        {
            var (success, error) = await _autonomous.ActivateAsync(ActivationKey, CancellationToken.None).ConfigureAwait(true);
            if (!success)
            {
                ErrorMessage = error ?? Tr.T("Не удалось активировать ключ.", "Ачкычты активдештирүү мүмкүн болгон жок.", "Could not activate the key.", "Anahtar etkinleştirilemedi.", "Kalitni faollashtirib bo'lmadi.");
                return;
            }

            ActivationKey = "";
            OnPropertyChanged(nameof(IsAutonomousActivated));
            SwitchScreen(AuthScreen.AutonomousCreateAccount);
        }
        finally
        {
            IsLoading = false;
            LoadingStatus = "";
        }
    }

    private async Task CreateLocalAccountAsync()
    {
        ErrorMessage = "";
        IsLoading = true;
        try
        {
            var (success, error) = _autonomous.CreateLocalAccount(LocalEmail, LocalPassword, LocalDisplayName);
            if (!success)
            {
                ErrorMessage = error ?? Tr.T("Не удалось создать аккаунт.", "Аккаунт түзүү мүмкүн болгон жок.", "Could not create the account.", "Hesap oluşturulamadı.", "Hisob yaratib bo'lmadi.");
                return;
            }

            OnPropertyChanged(nameof(HasLocalAccount));
            await FinishAutonomousLoginAsync(LocalDisplayName.Length > 0 ? LocalDisplayName : LocalEmail).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task AutonomousLoginAsync()
    {
        ErrorMessage = "";
        if (string.IsNullOrWhiteSpace(LocalEmail) || string.IsNullOrEmpty(LocalPassword))
        {
            ErrorMessage = Tr.T("Введите логин и пароль.", "Логин менен паролду киргизиңиз.", "Enter your login and password.", "Kullanıcı adı ve şifrenizi girin.", "Login va parolni kiriting.");
            return;
        }

        IsLoading = true;
        try
        {
            var (success, error, displayName) = _autonomous.LoginLocal(LocalEmail, LocalPassword);
            if (!success)
            {
                ErrorMessage = error ?? Tr.T("Неверный логин или пароль.", "Логин же пароль туура эмес.", "Wrong login or password.", "Kullanıcı adı veya şifre hatalı.", "Login yoki parol noto'g'ri.");
                LocalPassword = "";
                return;
            }

            await FinishAutonomousLoginAsync(displayName ?? LocalEmail).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task FinishAutonomousLoginAsync(string displayName)
    {
        _appSession.CurrentUserId = LocalEmail;
        _appSession.CurrentUserDisplayName = displayName;
        _appSession.PosCashboxDisplayName = displayName;
        _appSession.ActiveTerminal = null;
        IsOfflineMode = false;
        // 2026-09-10: было ошибочно false — из-за этого PosApp.IsOfflineBootstrap (через
        // App.SyncFromSession внутри MainWindow.InitializeApplicationAsync) никогда не
        // становился true для автономной сессии, и весь код на OfflineModeHelper.
        // UseLocalOperations (проверка смены, обновление каталога и т.д.) продолжал считать
        // кассу онлайн и дёргать сервер — то, что реально наблюдалось на живом тесте.
        _appSession.IsOfflineBootstrap = true;
        _appSession.OfflineBootstrapMessage = Tr.T(
            "Автономный режим — работа без интернета.",
            "Автономдук режим — интернетсиз иштөө.",
            "Offline mode — working without internet.",
            "Çevrimdışı mod — internetsiz çalışma.",
            "Oflayn rejim — internetsiz ishlash.");

        LocalEmail = "";
        LocalPassword = "";
        LocalDisplayName = "";
        SwitchScreen(AuthScreen.NurCrmLogin);
        await RaiseAsync(LoginSuccess).ConfigureAwait(true);
    }

    private async Task HandleResultAsync(AuthenticationResult result)
    {
        if (!result.IsSuccess || result.Session is null)
        {
            ErrorMessage = result.ErrorMessage ?? Tr.T("Не удалось выполнить вход.", "Кирүү мүмкүн болгон жок.", "Could not sign in.", "Giriş yapılamadı.", "Tizimga kirib bo'lmadi.");
            IsOfflineMode = result.Failure == AuthenticationFailure.NetworkUnavailable;
            return;
        }

        var session = result.Session;
        _autonomous.EndAutonomousSession();
        IsOfflineMode = result.Mode == AuthenticationMode.Offline;
        _appSession.CurrentUserId = session.UserId;
        _appSession.CurrentUserDisplayName = session.DisplayName;
        _appSession.PosCashboxDisplayName = session.DisplayName;
        _appSession.ActiveTerminal = session.BranchId;
        _appSession.IsOfflineBootstrap = IsOfflineMode;
        _appSession.OfflineBootstrapMessage = IsOfflineMode
            ? Tr.T("Нет связи с сервером. Используются локальные данные.", "Сервер менен байланыш жок. Жергиликтүү маалыматтар колдонулууда.", "No connection to the server. Using local data.", "Sunucuyla bağlantı yok. Yerel veriler kullanılıyor.", "Server bilan aloqa yo'q. Mahalliy ma'lumotlar ishlatilmoqda.")
            : null;

        LoadingStatus = IsOfflineMode
            ? Tr.T("Запуск в автономном режиме…", "Автономдук режимде иштетилүүдө…", "Starting in offline mode…", "Çevrimdışı modda başlatılıyor…", "Oflayn rejimda ishga tushirilmoqda…")
            : Tr.T("Загрузка кассы…", "Касса жүктөлүүдө…", "Loading the POS…", "Kasa yükleniyor…", "Kassa yuklanmoqda…");
        // Identity remains inside the session/service and never flows back into
        // Username, which is strictly an input property.
        ClearUiCredentials();
        await RaiseAsync(LoginSuccess).ConfigureAwait(true);
    }

    private void ClearApplicationSession()
    {
        _appSession.CurrentUserId = null;
        _appSession.CurrentUserDisplayName = null;
        _appSession.ActiveShiftId = null;
        _appSession.ActiveTerminal = null;
        _appSession.PosCashboxDisplayName = null;
        _appSession.IsOfflineBootstrap = false;
        _appSession.OfflineBootstrapMessage = null;
    }

    private static async Task RaiseAsync(Func<Task>? handlers)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().OfType<Func<Task>>())
            await handler().ConfigureAwait(true);
    }

    private void ClearUiCredentials()
    {
        Username = "";
        Password = "";
        CredentialsClearRequested?.Invoke();
    }

    private void RaiseCommandStates()
    {
        (LoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AutoLoginCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (LogoutCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}
