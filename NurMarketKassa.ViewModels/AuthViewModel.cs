using System.Windows.Input;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.ViewModels;

/// <summary>UI-independent authentication view-model for WPF and Avalonia.</summary>
public class AuthViewModel : ViewModelBase
{
    private readonly IOnlineOfflineAuthenticationService _authentication;
    private readonly IAppSession _appSession;
    private string _username = "";
    private string _password = "";
    private bool _rememberMe;
    private bool _isLoading;
    private bool _isOfflineMode;
    private string _errorMessage = "";
    private string _loadingStatus = "";

    public AuthViewModel(
        IOnlineOfflineAuthenticationService authentication,
        IAppSession appSession)
    {
        _authentication = authentication;
        _appSession = appSession;
        LoginCommand = new AsyncRelayCommand(LoginAsync, () => !IsLoading);
        AutoLoginCommand = new AsyncRelayCommand(AutoLoginAsync, () => !IsLoading);
        LogoutCommand = new AsyncRelayCommand(LogoutAsync, () => !IsLoading);
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

    public string LoginButtonText => IsOfflineMode ? "Работа без сети" : "Войти";
    public bool LoginButtonIsOfflineAccent => IsOfflineMode;

    public ICommand LoginCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand AutoLoginCommand { get; }

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
            ErrorMessage = "Введите логин и пароль.";
            return;
        }

        IsLoading = true;
        LoadingStatus = "Авторизация…";
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
            ErrorMessage = $"Не удалось выполнить вход: {ex.Message}";
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
        LoadingStatus = "Проверка сохранённой сессии…";
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
            ErrorMessage = $"Не удалось восстановить сессию: {ex.Message}";
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
            ClearApplicationSession();
            ClearUiCredentials();
            IsOfflineMode = false;
            await RaiseAsync(LogoutSuccess).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task HandleResultAsync(AuthenticationResult result)
    {
        if (!result.IsSuccess || result.Session is null)
        {
            ErrorMessage = result.ErrorMessage ?? "Не удалось выполнить вход.";
            IsOfflineMode = result.Failure == AuthenticationFailure.NetworkUnavailable;
            return;
        }

        var session = result.Session;
        IsOfflineMode = result.Mode == AuthenticationMode.Offline;
        _appSession.CurrentUserId = session.UserId;
        _appSession.PosCashboxDisplayName = session.DisplayName;
        _appSession.ActiveTerminal = session.BranchId;
        _appSession.IsOfflineBootstrap = IsOfflineMode;
        _appSession.OfflineBootstrapMessage = IsOfflineMode
            ? "Нет связи с сервером. Используются локальные данные."
            : null;

        LoadingStatus = IsOfflineMode ? "Запуск в автономном режиме…" : "Загрузка кассы…";
        // Identity remains inside the session/service and never flows back into
        // Username, which is strictly an input property.
        ClearUiCredentials();
        await RaiseAsync(LoginSuccess).ConfigureAwait(true);
    }

    private void ClearApplicationSession()
    {
        _appSession.CurrentUserId = null;
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
