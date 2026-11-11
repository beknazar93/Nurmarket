using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): PIN руководителя для ОДНОГО действия над ОДНИМ документом.
/// Поле PIN (точки вместо цифр, можно набирать и с клавиатуры) и цифровой блок 0–9 (только цифры PIN, не экранная клавиатура). PIN проверяет сервер
/// (<paramref name="verify"/> → одноразовый токен); окно PIN не хранит: поле очищается сразу после отправки и при закрытии, в журнал PIN не попадает.
/// Неверный PIN — «осталось попыток N» (число сервера), блокировка — обратный отсчёт до конца (кнопки выключены), срок вышел / документ изменён /
/// аннулирован / нет прав — сообщение, ввод выключен (повтор PIN бессмыслен), нет связи — сообщение, можно повторить.
/// Результат: <see cref="ConfirmToken"/> (не null — PIN принят), <see cref="FinalError"/> — отказ, после которого повтор бессмыслен.</summary>
public sealed class ManagerPinDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly Func<string, CancellationToken, Task<ReturnConfirmResult>> _verify;
    private readonly TextBox _pin;
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _ok;
    private readonly Grid _pad = new() { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ModalShell _shell;
    private DispatcherTimer? _lockTimer;
    private int _lockLeft;
    private bool _final;

    public string? ConfirmToken { get; private set; }
    public string ApprovedBy { get; private set; } = "";
    public ReturnEditException? FinalError { get; private set; }

    public ManagerPinDialog(string subtitle, Func<string, CancellationToken, Task<ReturnConfirmResult>> verify)
    {
        _verify = verify;
        Title = T("PIN руководителя", "Жетекчинин PIN коду", "Manager PIN", "Yönetici PIN'i", "Rahbar PIN kodi");
        SectionCards.AsModal(this, 440);

        var panel = new StackPanel { Spacing = 8 };
        var hint = new TextBlock
        {
            // 2026-11-11: коротко — окно должно помещаться на 1024×600 без прокрутки
            Text = T("PIN владельца или администратора (не пароль). Только для этого действия.",
                "Ээсинин же администратордун PIN коду (сырсөз эмес). Ушул аракет үчүн гана.",
                "Owner or administrator PIN (not the password). For this action only.",
                "Sahip veya yönetici PIN'i (şifre değil). Yalnızca bu işlem için.",
                "Egasi yoki administrator PIN kodi (parol emas). Faqat shu amal uchun."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(hint);

        _pin = SectionCards.Field(new TextBox
        {
            PasswordChar = '●', MaxLength = 12, FontSize = 22, HorizontalContentAlignment = HorizontalAlignment.Center, MinHeight = 46,
            Watermark = T("PIN", "PIN", "PIN", "PIN", "PIN"),
        });
        Avalonia.Automation.AutomationProperties.SetName(_pin, Title);
        _pin.AddHandler(TextInputEvent, (_, e) =>
        {
            // только цифры (с клавиатуры тоже)
            if (e.Text is { Length: > 0 } t && !t.All(char.IsDigit))
                e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _pin.TextChanged += (_, _) => _ok!.IsEnabled = !_final && _lockLeft <= 0 && !string.IsNullOrEmpty(_pin.Text);
        panel.Children.Add(_pin);

        BuildPad();
        panel.Children.Add(_pad);
        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close(false);
        _ok = SectionCards.Primary(T("Подтвердить", "Ырастоо", "Confirm", "Onayla", "Tasdiqlash"), isDefault: true);
        _ok.IsEnabled = false;
        _ok.Click += async (_, _) => await SubmitAsync().ConfigureAwait(true);
        Content = _shell = SectionCards.Shell(Title, subtitle, panel, SectionCards.Buttons(cancel, _ok));
        _shell.InitialFocus = _pin;
        Closed += (_, _) =>
        {
            _pin.Text = "";
            _lockTimer?.Stop();
        };
        Opened += (_, _) =>
        {
            if (ReturnEditApi.PinLockSecondsLeft is > 0 and var left)
                StartLock(left);
            _pin.Focus();
        };
    }

    private void BuildPad()
    {
        var keys = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "C", "⌫" }; // 2026-11-11: 4×3 — ниже окно
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            var b = new Button
            {
                Content = key, Width = 76, MinWidth = 0, Height = 46, MinHeight = 44, Padding = new Thickness(0), Margin = new Thickness(3), FontSize = 20, FontWeight = FontWeight.SemiBold,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
            };
            b.Classes.Add("modal-secondary");
            Avalonia.Automation.AutomationProperties.SetName(b, key switch
            {
                "C" => T("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"),
                "⌫" => T("Стереть цифру", "Санды өчүрүү", "Delete digit", "Rakamı sil", "Raqamni o'chirish"),
                _ => key,
            });
            b.Click += (_, _) =>
            {
                var text = _pin.Text ?? "";
                _pin.Text = key switch
                {
                    "C" => "",
                    "⌫" => text.Length > 0 ? text[..^1] : "",
                    _ => text.Length < (_pin.MaxLength > 0 ? _pin.MaxLength : 12) ? text + key : text,
                };
                _pin.CaretIndex = (_pin.Text ?? "").Length;
            };
            Grid.SetRow(b, i / 4);
            Grid.SetColumn(b, i % 4);
            _pad.Children.Add(b);
        }
    }

    private void SetPadEnabled(bool enabled)
    {
        _pad.IsEnabled = enabled;
        _pin.IsEnabled = enabled;
        _ok.IsEnabled = enabled && !string.IsNullOrEmpty(_pin.Text);
    }

    /// <summary>Показать сообщение под полем (ошибка/блокировка). Для снимков — тоже.</summary>
    public void ShowMessage(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private async Task SubmitAsync()
    {
        var pin = (_pin.Text ?? "").Trim();
        if (pin.Length == 0 || _final || _lockLeft > 0)
            return;
        _pin.Text = ""; // PIN не задерживается в окне
        if (!_shell.TryBeginBusy())
            return;
        SetPadEnabled(false);
        ReturnConfirmResult result;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            result = await _verify(pin, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = new ReturnConfirmResult(false, null, "", ReturnEditErrors.Classify(ex));
        }
        finally
        {
            pin = "";
            _shell.EndBusy();
        }
        Apply(result);
    }

    /// <summary>Разобрать итог проверки (открыт для тестов окна).</summary>
    public void Apply(ReturnConfirmResult result)
    {
        if (result.Ok && !string.IsNullOrEmpty(result.Token))
        {
            ConfirmToken = result.Token;
            ApprovedBy = result.ApprovedBy;
            Close(true);
            return;
        }
        var e = result.Error ?? new ReturnEditException(ReturnEditFailure.Server, "", "no_token");
        ShowMessage(ReturnEditErrors.Text(e));
        if (e.IsFinal)
        {
            _final = true;
            FinalError = e;
            SetPadEnabled(false);
            return;
        }
        if (e.Failure == ReturnEditFailure.PinLocked)
        {
            StartLock(e.RetryAfterSeconds ?? ReturnEditApi.PinLockSecondsLeft);
            return;
        }
        SetPadEnabled(true);
        _pin.Focus();
    }

    private void StartLock(int seconds)
    {
        if (seconds <= 0)
        {
            SetPadEnabled(true);
            return;
        }
        _lockLeft = seconds;
        SetPadEnabled(false);
        ShowMessage(ServerErrorCodes.PinLockedText(_lockLeft));
        _lockTimer?.Stop();
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _lockTimer.Tick += (_, _) =>
        {
            _lockLeft--;
            if (_lockLeft <= 0)
            {
                _lockTimer?.Stop();
                ShowMessage(null);
                SetPadEnabled(true);
                return;
            }
            if (_lockLeft % 30 == 0)
                ShowMessage(ServerErrorCodes.PinLockedText(_lockLeft));
        };
        _lockTimer.Start();
    }
}
