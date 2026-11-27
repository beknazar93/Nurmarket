using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Ui.Shared;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-27 (просьба владельца «открытие кассы с помощью QR — при сканере сразу входит от имени кассира»):
/// на экране входа кассы скан QR-бейджа кассира (USB-сканер = клавиатура) входит от его имени. Ввод перехватывается
/// на уровне окна (туннелем — до полей), строка с префиксом NMBADGE по Enter не уходит ни в логин, ни в пароль, поля
/// очищаются, код в журнал не пишется. Проверка и вход — <see cref="CashierBadgeLogin"/>.</summary>
public partial class LoginWindow
{
    private readonly StringBuilder _badgeBuffer = new();
    private DateTime _badgeLastInputUtc;
    /// <summary>Бейдж принят, но на этой кассе нужен вход паролем один раз — после него вход сохраняется под бейдж.</summary>
    private CashierBadgeRecord? _pendingBadge;
    private bool _badgeBusy;

    private void AttachBadgeScanner()
    {
        if (AppMode.IsOwner)
            return; // вход по бейджу — только в кассе
        CashierBadgeVault.StartAutoRemember(App.GetRequiredService<IAuthSessionManager>());
        AddHandler(TextInputEvent, OnBadgeTextInput, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnBadgeKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Отозванные владельцем бейджи — их сохранённые входы на кассе стираются.
        CashierBadgeVault.PruneRevoked();
    }

    private void OnBadgeTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
            return;
        var now = DateTime.UtcNow;
        // Сканер печатает подряд; пауза больше 3 с — начинаем копить заново (ручной ввод логина не смешивается со сканом).
        if (_badgeBuffer.Length > 0 && (now - _badgeLastInputUtc).TotalSeconds > 3)
            _badgeBuffer.Clear();
        _badgeLastInputUtc = now;
        if (_badgeBuffer.Length < 256)
            _badgeBuffer.Append(e.Text);
    }

    private void OnBadgeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return)
            return;
        var raw = CashierBadgeCode.ExtractFromInput(_badgeBuffer.ToString());
        _badgeBuffer.Clear();
        if (raw is null)
            return;
        // Строка бейджа — не логин и не пароль: Enter не уходит в поля, набранное сканером стирается.
        e.Handled = true;
        ClearCredentialControls();
        _ = HandleBadgeScanAsync(raw);
    }

    /// <summary>Скан бейджа: проверка → PIN (если задан) → вход сохранённым входом кассира или просьба пароля один раз.</summary>
    internal async Task HandleBadgeScanAsync(string raw)
    {
        if (_badgeBusy || _enteringMain || _viewModel.IsLoading)
            return;
        _badgeBusy = true;
        try
        {
            var login = CreateBadgeLogin();
            var check = login.Check(raw);
            if (check.Status != CashierBadgeStatus.Ok || check.Record is null)
            {
                if (check.Status != CashierBadgeStatus.NotBadge)
                {
                    LogBadgeAudit("badge_denied", null, check.Record?.Id ?? IdOf(raw), check.Status.ToString());
                    _viewModel.ReportError(CashierBadgeRegistry.Message(check.Status, check.Wait));
                }
                return;
            }

            var record = check.Record;
            if (record.RequiresPin)
            {
                var pin = await CashierBadgePinWindow.AskAsync(this, record.Name);
                if (pin is null)
                    return; // кассир закрыл окно PIN
                if (!login.CheckPin(record, pin))
                {
                    LogBadgeAudit("badge_denied", null, record.Id, "WrongPin");
                    _viewModel.ReportError(Tr.T("Неверный PIN бейджа.", "Бейдждин PIN-коду туура эмес.", "Wrong badge PIN.", "Rozet PIN'i yanlış.", "Beydj PIN-kodi noto'g'ri."));
                    return;
                }
            }

            if (!login.HasSavedLogin(record))
            {
                AskPasswordOnce(record);
                return;
            }

            var result = await _viewModel.LoginWithAsync(
                () => login.LoginAsync(record, _viewModel.RememberMe),
                Tr.T("Вход по бейджу…", "Бейдж менен кирүү…", "Signing in with the badge…", "Rozetle giriş…", "Beydj bilan kirish…"));
            if (result is { IsSuccess: true })
            {
                _pendingBadge = null;
                LogBadgeAudit("badge_login", result.Session?.UserId, record.Id, result.Mode.ToString());
            }
            else if (result is { Failure: AuthenticationFailure.SessionExpired })
                AskPasswordOnce(record);
            else
                LogBadgeAudit("badge_denied", null, record.Id, result?.Failure.ToString() ?? "Error");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Вход по бейджу: сбой ({ex.GetType().Name}).", "WARNING");
            _viewModel.ReportError(Tr.T("Не удалось войти по бейджу. Войдите паролем.", "Бейдж менен кирүү мүмкүн болгон жок. Сырсөз менен кириңиз.",
                "Could not sign in with the badge. Sign in with the password.", "Rozetle giriş yapılamadı. Şifreyle giriş yapın.", "Beydj bilan kirib bo'lmadi. Parol bilan kiring."));
        }
        finally
        {
            _badgeBusy = false;
        }
    }

    /// <summary>Бейдж верный, но сохранённого входа кассира на этой кассе нет (или истёк) — логин подставлен, ввести пароль один раз.</summary>
    private void AskPasswordOnce(CashierBadgeRecord record)
    {
        _pendingBadge = record;
        if (!string.IsNullOrWhiteSpace(record.Login))
            EmailBox.Text = record.Login;
        _viewModel.PrefillLogin(null, CashierBadgeLogin.NeedPasswordText(record));
        if (_passwordVisible)
            VisiblePasswordBox.Focus();
        else
            PasswordBox.Focus();
    }

    /// <summary>После любого успешного входа паролем в кассе: сохранить вход под бейдж (если он у сотрудника есть или только что предъявлен).</summary>
    private void RememberLoginForBadge()
    {
        if (AppMode.IsOwner)
            return;
        try
        {
            var session = _viewModel.LastSession;
            if (_pendingBadge is { } pending && session is not null
                && !string.Equals(pending.EmployeeId.Trim(), session.UserId?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                // Паролем вошёл другой сотрудник — бейдж не его, привязку не делаем.
                PosLogger.Log($"Вход по бейджу: паролем вошёл другой сотрудник — бейдж {CashierBadgeCode.Mask(pending.Id)} не привязан.", "AUTH");
                _pendingBadge = null;
            }
            CashierBadgeLogin.RememberAfterPasswordLogin(session, _pendingBadge, UserPreferences.Instance.CashierBadgesJson, CashierBadgeVault.Instance, DateTimeOffset.UtcNow);
            if (_pendingBadge is not null)
                LogBadgeAudit("badge_bound", session?.UserId, _pendingBadge.Id, "password");
            _pendingBadge = null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Вход по бейджу: вход не сохранён ({ex.GetType().Name}).", "AUTH");
        }
    }

    private static CashierBadgeLogin CreateBadgeLogin() => new(
        App.GetRequiredService<IOnlineOfflineAuthenticationService>(),
        App.GetRequiredService<IAuthSessionManager>(),
        CashierBadgeVault.Instance,
        CashierBadgeThrottle.Instance,
        () => UserPreferences.Instance.CashierBadgesJson,
        async ct =>
        {
            var api = App.GetRequiredService<NurMarketApiClient>();
            return await CashierBadgeLogin.FetchServerRegistryAsync(api, ct).ConfigureAwait(false);
        });

    private static string? IdOf(string raw) => CashierBadgeCode.TryParse(raw, out var id, out _) ? id : null;

    /// <summary>Журнал входов: кто, когда, на какой кассе — без кода бейджа (только маска id).</summary>
    private static void LogBadgeAudit(string action, string? userId, string? badgeId, string result)
    {
        try
        {
            App.AuditDb.LogEvent("auth", action, new { userId, badge = CashierBadgeCode.Mask(badgeId), device = Environment.MachineName, result }, userId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Вход по бейджу: запись в журнал не удалась ({ex.GetType().Name}).", "AUTH");
        }
    }
}
