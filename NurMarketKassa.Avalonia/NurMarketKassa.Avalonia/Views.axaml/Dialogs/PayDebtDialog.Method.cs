using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.ViewModels;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-18, владелец: способ оплаты долга в кассе — по общим настройкам «Оплата долга» (DebtPaymentSettings):
/// разрешён один способ — выбора нет, как раньше (по умолчанию — только наличные); оба — «Наличные / Безнал».
/// Наличные — "cash" (в ящик, в смену), безнал — "transfer" (в смене — безналом, не в ящике). Плюс синхронизация: оплата
/// в другой кассе или у владельца (событие LAN «debt_paid» → PosDataEvents.ClientDebtChanged) — окно перечитывает долг
/// этого клиента.</summary>
public partial class PayDebtDialog
{
    private string _method = DebtPaymentSettings.MethodCash;
    /// <summary>Идёт оплата — оповещение «долг изменился» список не перезагружает (оплата перезагрузит сама).</summary>
    private bool _paying;
    /// <summary>2026-10-18, шестой круг ревью: «свежий список» — по версии. Восьмой круг (п.9): версия общая —
    /// PosDataEvents.DebtVersionOf(клиент) (растёт при каждом оповещении о его долге); загрузка запоминает её на момент НАЧАЛА
    /// (_loadedSeq — у последней успешной). После оплаты список перечитывается, если загруженный начат до последнего оповещения.</summary>
    private long _loadedSeq;
    /// <summary>Ключ замка, который сейчас показан на экране (п.5: действие — по экрану; не совпал с Runner — отказ).</summary>
    private string? _shownUnknownKey;

    /// <summary>Выбранный способ: "cash" или "transfer" (только из разрешённых настройкой).</summary>
    private string SelectedMethod => DebtPaymentSettings.MethodsHere().Contains(_method) ? _method : DebtPaymentSettings.MethodsHere()[0];

    public bool ShowMethodChoice => HasSelectedClient && DebtSales.Count > 0 && DebtPaymentSettings.MethodsHere().Count > 1;

    public string MethodLabel => Tr.T("Способ оплаты:", "Төлөө ыкмасы:", "Payment method:", "Ödeme yöntemi:", "To'lov usuli:");
    public string CashMethodLabel => DebtPaymentFlow.MethodTitle(DebtPaymentSettings.MethodCash);
    public string TransferMethodLabel => DebtPaymentFlow.MethodTitle(DebtPaymentSettings.MethodTransfer);

    public bool IsCashMethod
    {
        get => SelectedMethod == DebtPaymentSettings.MethodCash;
        set
        {
            if (!value)
                return;
            _method = DebtPaymentSettings.MethodCash;
            OnMethodChanged();
        }
    }

    public bool IsTransferMethod
    {
        get => SelectedMethod == DebtPaymentSettings.MethodTransfer;
        set
        {
            if (!value)
                return;
            _method = DebtPaymentSettings.MethodTransfer;
            OnMethodChanged();
        }
    }

    /// <summary>Под способом — пояснение для безнала (и при единственном разрешённом безнале — что оплата пойдёт безналом).</summary>
    public bool ShowTransferHint => HasSelectedClient && DebtSales.Count > 0 && IsTransferMethod;

    public string TransferHint => (DebtPaymentSettings.MethodsHere().Count > 1 ? "" : Tr.T(
            "Оплата долга принимается безналом (так задано в настройках «Оплата долга»). ",
            "Карыз накталай эмес кабыл алынат («Карыз төлөө» жөндөөсүндө ушундай коюлган). ",
            "Debt payments are taken as non-cash (set in the “Debt payment” settings). ",
            "Borç ödemesi nakitsiz alınır («Borç ödemesi» ayarlarında böyle belirlendi). ",
            "Qarz to'lovi naqd emas qabul qilinadi («Qarzni to'lash» sozlamalarida shunday belgilangan). "))
        + Tr.T(
            "Безнал попадёт в смену безналичными — в ожидаемую сумму ящика не входит.",
            "Накталай эмес төлөм сменага накталай эмес болуп кирет — кутулган кассанын суммасына кошулбайт.",
            "Non-cash goes to the shift as non-cash — it is not part of the expected drawer amount.",
            "Nakitsiz ödeme vardiyaya nakitsiz olarak girer — beklenen kasa tutarına dahil değildir.",
            "Naqd emas to'lov smenaga naqd emas sifatida tushadi — kutilgan kassa summasiga kirmaydi.");

    private void OnMethodChanged()
    {
        OnPropertyChanged(nameof(IsCashMethod));
        OnPropertyChanged(nameof(IsTransferMethod));
        OnPropertyChanged(nameof(ShowTransferHint));
        OnPropertyChanged(nameof(TransferHint));
    }

    private void RefreshMethodChoice()
    {
        OnPropertyChanged(nameof(ShowMethodChoice));
        OnMethodChanged();
    }

    // ── 2026-10-18, пятый круг ревью: своё оповещение — по ключу оплаты ─────────────────────────────────────────

    /// <summary>Ключи своих операций (общий класс OwnOperationKeys): оповещение с таким ключом окно-автор не перечитывает.</summary>
    private readonly OwnOperationKeys _ownKeys = new();

    private void RememberOwnKey(string key) => _ownKeys.Remember(key);

    private bool IsOwnKey(string? key) => _ownKeys.Contains(key);

    /// <summary>Ключ своей НОВОЙ оплаты, которая сейчас идёт: её запись «в пути» — не чужой замок.</summary>
    private string? _activeNewKey;

    private void InitMethodChoice()
    {
        DebtSales.CollectionChanged += (_, _) => RefreshMethodChoice();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(HasSelectedClient))
            {
                // У клиента висит оплата без ответа — показать её способ и закрыть изменения.
                if (DebtPaymentRunner.UnknownFor(_selectedClient?.Id) is { } unknown)
                    _method = unknown.Method;
                // 2026-10-18, седьмой круг: другой клиент — замок прошлого клиента не «сменившееся состояние».
                _shownUnknownKey = null;
                RefreshMethodChoice();
                OnUnknownChanged();
            }
        };
        void OnSettings() => Dispatcher.UIThread.Post(RefreshMethodChoice);
        // 2026-10-18, девятый круг (п.5): общая отложенная рассылка PosDataEvents.DebouncedDebtChanged; свои операции — по ключу.
        void OnDebtChanged(DebtChangeBatch batch) => Dispatcher.UIThread.Post(() =>
        {
            // Во время своей оплаты — не теряем, перечитаем после неё (SetPaying(false): версия уже выросла в PosDataEvents).
            if (_paying || _selectedClient is not { } client || !batch.ForeignChangeOf(client.Id, IsOwnKey))
                return;
            // Список уже загружен не раньше последнего изменения (например, перечитан после своей оплаты) — второй раз не читаем.
            if (_loadedSeq >= PosDataEvents.DebtVersionOf(client.Id))
                return;
            _ = ReloadAfterOutsideChangeAsync(client.Id);
        });
        // Замок/оплата «в пути» появились или сняты (в т. ч. в другом окне) — панель перечитывает состояние из Runner.
        void OnRunnerChanged() => Dispatcher.UIThread.Post(OnUnknownChanged);
        DebtPaymentSettings.Changed += OnSettings;
        PosDataEvents.DebouncedDebtChanged += OnDebtChanged;
        DebtPaymentRunner.Changed += OnRunnerChanged;
        Closed += (_, _) =>
        {
            DebtPaymentSettings.Changed -= OnSettings;
            PosDataEvents.DebouncedDebtChanged -= OnDebtChanged;
            DebtPaymentRunner.Changed -= OnRunnerChanged;
        };
        // 2026-10-18, пятый круг (п.5): пока идёт операция, окно не закрывается — иначе кассир не увидит её исход.
        Closing += (_, e) =>
        {
            if (!_paying)
                return;
            e.Cancel = true;
            ErrorMessage = Tr.T("Дождитесь ответа сервера — идёт оплата.", "Сервердин жообун күтүңүз — төлөм жүрүп жатат.",
                "Wait for the server's answer — a payment is in progress.", "Sunucunun yanıtını bekleyin — ödeme sürüyor.",
                "Server javobini kuting — to'lov ketmoqda.");
        };
        // Настройки «Оплата долга» — свежие при каждом открытии окна (лёгкий запрос; сбой — молча, прежние значения).
        Opened += async (_, _) =>
        {
            // 2026-10-18, седьмой круг: журнал незавершённых оплат общий с программой владельца — перечитать при открытии.
            DebtPaymentRunner.Refresh();
            OnUnknownChanged();
            await DebtPaymentSettings.RefreshAsync(force: true).ConfigureAwait(true);
            RefreshMethodChoice();
        };
    }

    /// <summary>Долг этого клиента изменился не в этом окне — перечитать список (если сейчас не идёт своя оплата). Введённые
    /// суммы и сообщения сохраняются (keepInput); замок неизвестной оплаты остаётся.</summary>
    private async Task ReloadAfterOutsideChangeAsync(string? clientId)
    {
        if (_paying || _selectedClient is not { } client)
            return;
        if (clientId is not null && !string.Equals(clientId, client.Id, StringComparison.OrdinalIgnoreCase))
            return;
        PosLogger.Log($"Оплата долга: долг клиента {client.Id} изменился в другой программе — список перечитан.", "PAYMENT");
        if (!await LoadDebtSalesAsync(client.Id, keepInput: true).ConfigureAwait(true) || !ReferenceEquals(_selectedClient, client))
            return;
        // Пояснение — один раз (не дописывается при каждом оповещении).
        if (!SuccessMessage.Contains(DebtPaymentFlow.ChangedElsewhereMessage, StringComparison.Ordinal))
            SuccessMessage = string.IsNullOrEmpty(SuccessMessage)
                ? DebtPaymentFlow.ChangedElsewhereMessage
                : SuccessMessage + " " + DebtPaymentFlow.ChangedElsewhereMessage;
    }

    // ── оплата без ответа сервера — состояние всегда из DebtPaymentRunner ───────────────────────────────────────

    private UnknownDebtPayment? CurrentUnknown => DebtPaymentRunner.UnknownFor(_selectedClient?.Id);

    /// <summary>У выбранного клиента запись «в пути» или замок (кроме своей новой оплаты, которая сейчас идёт).</summary>
    public bool HasUnknownPayment => CurrentUnknown is { } p && !(_paying && p.Key == _activeNewKey);

    /// <summary>Менять сумму, способ, чек и платить можно: нет незавершённой оплаты и не идёт своя операция (одна за раз).</summary>
    public bool CanEditPayment => !HasUnknownPayment && !_paying;

    /// <summary>2026-10-18, восьмой круг (п.4): замок закрывает только оплату ЭТОГО клиента — сменить клиента (обслужить
    /// следующего), листать таблицу и историю можно; нельзя только во время своей операции.</summary>
    public bool CanChangeClient => !_paying;

    /// <summary>Кнопки панели — не во время своей операции и не пока запрос по этой записи идёт в другом окне.</summary>
    /// <remarks>2026-10-18, седьмой круг: оплату начала другая программа на этом ПК — повтор и решение только там.</remarks>
    public bool CanUseUnknownButtons => !_paying && !DebtPaymentRunner.IsInFlight(_selectedClient?.Id)
                                        && (CurrentUnknown is not { } p || DebtPaymentRunner.IsOwnProgram(p));

    /// <summary>2026-10-18, десятый круг (п.2, деньги): «Повторить» — только пока сервер помнит ключ (не старше суток).</summary>
    public bool CanRetryUnknown => CanUseUnknownButtons && (CurrentUnknown is not { } p || !DebtPaymentRunner.IsRetryExpired(p));

    /// <remarks>2026-10-18, шестой круг: свой повтор — «Повторяем…», чужой запрос — «идёт в другом окне».</remarks>
    public string UnknownPaymentText => CurrentUnknown is { } p ? DebtPaymentRunner.UnknownText(p, ownOperation: _paying && IsOwnKey(p.Key)) : "";

    /// <remarks>Во время повтора — «Оплата…» (привязка, Content кнопки не трогаем).</remarks>
    public string UnknownRetryLabel => CurrentUnknown is { } p ? (_paying ? Tr.T("Оплата…", "Төлөм…", "Paying…", "Ödeniyor…", "To'lanmoqda…") : DebtPaymentRunner.RetryLabel(p)) : "";

    public string UnknownPaidOnSiteLabel => DebtPaymentRunner.PaidOnSiteLabel;

    public string UnknownUnlockLabel => DebtPaymentRunner.UnlockLabel;

    /// <summary>Идёт операция (одна за раз в окне): все кнопки оплаты закрыты, окно не закрывается.</summary>
    private void SetPaying(bool paying)
    {
        var wasPaying = _paying;
        _paying = paying;
        if (!paying && wasPaying)
        {
            _activeNewKey = null;
            // Последний загруженный список начат до последнего оповещения — перечитать.
            if (_loadedSeq < PosDataEvents.DebtVersionOf(_selectedClient?.Id))
                Dispatcher.UIThread.Post(() => _ = ReloadAfterOutsideChangeAsync(_selectedClient?.Id));
        }
        OnPropertyChanged(nameof(CanEditPayment));
        OnPropertyChanged(nameof(CanChangeClient));
        OnPropertyChanged(nameof(CanUseUnknownButtons));
        OnPropertyChanged(nameof(CanRetryUnknown));
        OnPropertyChanged(nameof(UnknownRetryLabel));
        OnPropertyChanged(nameof(PayAllButtonLabel));
        OnUnknownChanged();
    }

    private void OnUnknownChanged()
    {
        // 2026-10-18, седьмой круг (п.6): на экране был один замок, а теперь другой (другая запись, другой ключ) — панель
        // перерисовывается по новой записи (сумма и способ из неё) и прямо говорится «Состояние оплаты изменилось».
        var shown = HasUnknownPayment ? CurrentUnknown : null;
        if (shown is not null && _shownUnknownKey is not null && shown.Key != _shownUnknownKey)
        {
            ErrorMessage = DebtPaymentRunner.StateChangedText;
            SuccessMessage = "";
        }
        if (shown is not null && shown.Key != _shownUnknownKey)
        {
            _method = shown.Method;
            OnMethodChanged();
        }
        _shownUnknownKey = shown?.Key;
        OnPropertyChanged(nameof(HasUnknownPayment));
        OnPropertyChanged(nameof(CanEditPayment));
        OnPropertyChanged(nameof(CanUseUnknownButtons));
        OnPropertyChanged(nameof(CanRetryUnknown));
        OnPropertyChanged(nameof(UnknownPaymentText));
        OnPropertyChanged(nameof(UnknownRetryLabel));
    }

    // ── 2026-10-18, пятый круг (п.10): одна обёртка для «Оплатить» у строки и «Погасить» ─────────────────────────

    /// <summary>Общая обёртка оплаты: одна операция за раз, замок, SetPaying, код сотрудника (ДО очереди Runner), проверка
    /// способа, «долг уже погашен», ошибки, finally. pay получает выбранный способ.</summary>
    private async Task RunPaymentAsync(Func<string, Task> pay, string what)
    {
        if (_paying || _selectedClient is not { } client)
            return;
        if (HasUnknownPayment)
        {
            ErrorMessage = CurrentUnknown is { } foreign && !DebtPaymentRunner.IsOwnProgram(foreign) ? DebtPaymentRunner.ForeignText(foreign)
                : DebtPaymentRunner.IsInFlight(client.Id) ? DebtPaymentRunner.BusyText : DebtPaymentRunner.LockedText;
            return;
        }
        var shownMethod = SelectedMethod;
        SetPaying(true);
        try
        {
            var (granted, gateError) = await DebtPaymentFlow.ConfirmEmployeeCodeAsync().ConfigureAwait(true);
            if (!granted)
            {
                ErrorMessage = gateError ?? "";
                return;
            }
            // Пока спрашивали код, пришли новые настройки «Оплата долга» и способ на экране стал другим — не проводим молча.
            var method = SelectedMethod;
            if (method != shownMethod)
            {
                RefreshMethodChoice();
                ErrorMessage = DebtPaymentFlow.MethodChangedMessage;
                return;
            }
            await pay(method).ConfigureAwait(true);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            PosLogger.Log($"Pay-debt failed ({what}): {ex}", "PAYMENT");
            // Долг уже погашен (другая касса, владелец) — перечитать список и сказать прямо.
            if (DebtPaymentFlow.IsNoDebt(ex))
            {
                await LoadDebtSalesAsync(client.Id).ConfigureAwait(true);
                ErrorMessage = DebtPaymentFlow.NoDebtMessage;
            }
        }
        catch (Exception ex)
        {
            // 2026-10-18, десятый круг (п.9): Runner исключений не бросает — сюда приходят только ошибки старого пути.
            ErrorMessage = Tr.T("Не удалось оплатить долг: ", "Карызды төлөө мүмкүн болгон жок: ", "Could not pay the debt: ", "Borç ödenemedi: ", "Qarzni to'lab bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Pay-debt failed ({what}): {ex}", "PAYMENT");
        }
        finally
        {
            SetPaying(false);
            // Старый путь по взносам пишет ход в Content — вернуть привязку надписи.
            PayAllButton[!ContentControl.ContentProperty] = new Avalonia.Data.Binding(nameof(PayAllButtonLabel));
            // 2026-10-18, седьмой круг: ErrorMessage здесь больше не стирается — при замке в нём только уточнение к панели
            // (OutcomeText: «сервер принял, журнал не записан», «есть незавершённая оплата»), а первая неизвестная — пустое.
        }
    }

    /// <summary>Новая оплата через сервер (DebtPaymentRunner.PayNewAsync: очередь, запись заранее, решение по ответу). row null —
    /// «одной суммой». true — проведено; false — сервер адреса не знает; отказ, обрыв, «не отправлено» — исключением.</summary>
    private async Task<bool> PayViaServerAsync(DebtSaleRow? row, double amount, string method)
    {
        var api = App.AppHost?.Services.GetService<ClientDebtsApiService>();
        if (api == null || _selectedClient is not { } client)
            return false;
        var key = Guid.NewGuid().ToString();
        RememberOwnKey(key);
        _activeNewKey = key;
        DebtPayAttempt attempt;
        try
        {
            attempt = await DebtPaymentRunner.PayNewAsync(api, key, client.Id, client.DisplayName, row?.Id,
                row?.Number is { Length: > 0 } number ? "№" + number : null, amount, method, PosApp.ActiveShiftId).ConfigureAwait(true);
        }
        finally
        {
            _activeNewKey = null;
            OnUnknownChanged();
        }
        return await ApplyAttemptAsync(client, attempt, bySale: row is not null).ConfigureAwait(true);
    }

    /// <summary>Итог операции Runner в окне: сервер не знает адреса — false (старый путь); остальное — текст общим
    /// сопоставлением DebtPaymentRunner.OutcomeText (как у владельца), при замке — уточнение под панелью.</summary>
    /// <remarks>2026-10-18, седьмой круг (п.10): исход — enum DebtPayOutcome, исключения больше не бросаются.</remarks>
    private async Task<bool> ApplyAttemptAsync(ClientOption client, DebtPayAttempt attempt, bool bySale)
    {
        if (attempt.Outcome == DebtPayOutcome.EndpointMissing)
        {
            if (bySale)
                _serverSalePayMissing = true;
            else
                _serverPayDebtMissing = true;
            return false;
        }
        OnUnknownChanged();
        var text = DebtPaymentRunner.OutcomeText(attempt, bySale, owner: false) ?? "";
        if (DebtPaymentRunner.NeedsReload(attempt))
            await LoadDebtSalesAsync(client.Id, keepInput: attempt.Outcome != DebtPayOutcome.Success).ConfigureAwait(true);
        if (attempt.Outcome == DebtPayOutcome.Success)
            SuccessMessage = text;
        else
            ErrorMessage = text;
        // 2026-10-18, девятый круг (п.7): предупреждение к успеху — отдельной плашкой.
        if (DebtPaymentRunner.OutcomeWarning(attempt) is { } warning)
            WarningMessage = warning;
        return true;
    }

    /// <summary>«Повторить» — ТОЛЬКО повтор записи по её ключу (RetryAsync), никогда не новая оплата. Любая ошибка — замок
    /// и ключ остаются.</summary>
    private async void RetryUnknown_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedClient is not { } client || _paying)
            return;
        // 2026-10-18, шестой круг (п.5): повтор — ровно той записи, что на экране; Runner показывает другое — отказ и обновить экран.
        if (CurrentUnknown is not { } p || p.Key != _shownUnknownKey)
        {
            OnUnknownChanged();
            ErrorMessage = DebtPaymentRunner.StateChangedText;
            return;
        }
        if (DebtPaymentRunner.IsRetryExpired(p))
        {
            ErrorMessage = DebtPaymentRunner.RetryExpiredText;
            return;
        }
        ErrorMessage = "";
        SuccessMessage = "";
        SetPaying(true);
        try
        {
            var (granted, error) = await DebtPaymentFlow.ConfirmEmployeeCodeAsync().ConfigureAwait(true);
            if (!granted)
            {
                ErrorMessage = error ?? "";
                return;
            }
            var api = App.AppHost?.Services.GetService<ClientDebtsApiService>();
            if (api is null)
                return;
            RememberOwnKey(p.Key);
            var attempt = await DebtPaymentRunner.RetryAsync(api, p.Key).ConfigureAwait(true);
            // Тексты (повтор не отправлен, закрытая смена, «повтор не удался — блокировка остаётся») — OutcomeText.
            await ApplyAttemptAsync(client, attempt, bySale: p.SaleId is not null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = DebtPaymentRunner.RetryErrorText(ex);
            PosLogger.Log($"Оплата долга: повтор не выполнен: {ex}", "WARNING");
        }
        finally
        {
            SetPaying(false);
        }
    }

    private async void PaidOnSite_Click(object? sender, RoutedEventArgs e) => await ResolveSafelyAsync(DebtResolution.PaidOnSite).ConfigureAwait(true);

    private async void NotOnSite_Click(object? sender, RoutedEventArgs e) => await ResolveSafelyAsync(DebtResolution.NotOnSite).ConfigureAwait(true);

    /// <summary>2026-10-18, шестой круг (п.3): без необработанных исключений из async void — ошибка текстом и в журнал.</summary>
    private async Task ResolveSafelyAsync(DebtResolution resolution)
    {
        try
        {
            await ResolveUnknownAsync(resolution).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("Не удалось выполнить: ", "Аткаруу мүмкүн болгон жок: ", "Couldn't complete: ", "Tamamlanamadı: ", "Bajarib bo'lmadi: ") + ex.Message;
            PosLogger.Log($"Оплата долга: решение по замку ({resolution}) не выполнено: {ex}", "WARNING");
        }
    }

    /// <summary>Решение по замку после проверки на сайте NurCRM: подтверждение и код — ДО очереди; ResolveAsync закрывает запись
    /// только с тем же ключом (её могли уже завершить в другом окне).</summary>
    private async Task ResolveUnknownAsync(DebtResolution resolution)
    {
        if (_selectedClient is not { } client || _paying)
            return;
        // п.5: решение — ровно по той записи, что на экране.
        if (CurrentUnknown is not { } p || p.Key != _shownUnknownKey)
        {
            OnUnknownChanged();
            ErrorMessage = DebtPaymentRunner.StateChangedText;
            return;
        }
        ErrorMessage = "";
        SuccessMessage = "";
        SetPaying(true);
        try
        {
            // 2026-10-18, десятый круг (п.5): одной суммой «Оплата есть» — сумма, которую сервер провёл (вводит кассир).
            if (await DebtPaymentRunner.ConfirmResolutionAsync(this, p, resolution).ConfigureAwait(true) is not { } appliedAmount)
                return;
            var (granted, gateError) = await DebtPaymentFlow.ConfirmEmployeeCodeAsync().ConfigureAwait(true);
            if (!granted)
            {
                ErrorMessage = gateError ?? "";
                return;
            }
            RememberOwnKey(p.Key);
            var (done, error, warning) = await DebtPaymentRunner.ResolveAsync(p.Key, resolution, appliedAmount).ConfigureAwait(true);
            OnUnknownChanged();
            await LoadDebtSalesAsync(client.Id, keepInput: true).ConfigureAwait(true);
            if (done)
            {
                SuccessMessage = resolution == DebtResolution.PaidOnSite ? DebtPaymentRunner.ResolvedPaidText(p with { Amount = appliedAmount }) : DebtPaymentRunner.ResolvedUnlockedText;
                // 2026-10-18, седьмой круг (п.2): замок снят, а событие в смену не записалось — предупредить (недосчёт, не двойной счёт).
                if (warning is not null)
                    WarningMessage = warning;
            }
            else
                ErrorMessage = error ?? "";
        }
        finally
        {
            SetPaying(false);
        }
    }

    private string _warningMessage = "";

    /// <summary>2026-10-18, девятый круг (п.7): предупреждение к проведённой оплате (другая смена, не записано в смену). Не
    /// стирается ни перечитыванием, ни следующей оплатой — только «Понятно» или закрытием окна.</summary>
    public string WarningMessage
    {
        get => _warningMessage;
        private set
        {
            _warningMessage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWarning));
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(_warningMessage);

    public string WarningCloseLabel => Tr.T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli");

    private void CloseWarning_Click(object? sender, RoutedEventArgs e) => WarningMessage = "";

    /// <summary>Оплата прошла по старому пути — соседям по LAN и окнам этой кассы (себя — нет: ключ запомнен).</summary>
    private void AnnouncePaid(string operationKey, ClientOption client, double paid, double left, string method)
    {
        RememberOwnKey(operationKey);
        DebtPaymentFlow.Announce(operationKey, client.Id, client.DisplayName, paid, left, method);
    }

    /// <summary>Безнал по старому пути (сервер без pay-debt) провести нельзя — тот гасит взносы только наличными в кассу.</summary>
    private bool RefuseLegacyForNonCash(string method)
    {
        if (method == DebtPaymentSettings.MethodCash)
            return false;
        ErrorMessage = Tr.T(
            "Сервер пока не принимает оплату долга безналом. Примите наличными.",
            "Сервер азырынча карызды накталай эмес кабыл албайт. Накталай кабыл алыңыз.",
            "The server does not accept non-cash debt payments yet. Take cash.",
            "Sunucu henüz nakitsiz borç ödemesi kabul etmiyor. Nakit alın.",
            "Server hozircha qarzni naqd emas qabul qilmaydi. Naqd qabul qiling.");
        return true;
    }
}
