using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-10-18, владелец: «Принять оплату» долга в программе владельца (раздел «Долги клиентов»). Решения владельца:
/// наличные — «в руки владельцу», БЕЗ смены (касса и смена кассира не меняются); безнал — тоже без смены. Способы — по
/// общим настройкам «Оплата долга» (DebtPaymentSettings, у владельца по умолчанию разрешено всё).
///
/// Как устроено: остаток — точный, с сервера (тот же загрузчик, что у кассы: PayDebtDialog.LoadDebtRowsAsync, remaining_debt
/// сделки); оплата — те же запросы, что у кассы (ClientDebtsApiService): «одной суммой» — clients/{id}/pay-debt/ (сервер
/// гасит от старых к новым), «по чеку» — pos/sales/{id}/pay-debt/; поле shift не отправляется. Ключ идемпотентности живёт до
/// ответа сервера (PendingIdempotencyKey, как у обмена): ответ не дошёл — поля закрываются и «Повторить» шлёт ТОТ ЖЕ ключ,
/// сервер второй раз деньги не проведёт. Старого пути по взносам сделки здесь нет: он кладёт деньги в кассу pos_main.
/// Только при связи с сервером. После оплаты — событие LAN «debt_paid», обновление раздела и «Сводки», запись в журнал.
/// </summary>
public sealed class AcceptDebtPaymentWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    // 2026-10-18, второй круг ревью: ключ идемпотентности, «исход неизвестен», журнал и LAN — в DebtPaymentRunner (общий с кассой).

    private readonly string _clientId;
    private readonly string _clientName;
    private List<DebtSaleRow> _rows = new();
    private DebtSaleRow? _selectedRow;
    private bool _bySale;
    private string _method = DebtPaymentSettings.MethodCash;
    private bool _loadStarted;
    private bool _loaded;
    private bool _running;
    private bool _done;
    /// <summary>Ответ сервера не дошёл: поля закрыты, «Повторить» шлёт ту же оплату с тем же ключом.</summary>
    private bool _unknownOutcome;
    private int _loadGen;
    /// <summary>2026-10-18, десятый круг (п.7): версия долга клиента (PosDataEvents) на момент начала последней успешной загрузки.</summary>
    private long _loadedVersion;

    private readonly TextBlock _total = new() { FontSize = 26, FontWeight = FontWeight.Bold };
    private readonly TextBlock _totalCaption = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _meta = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _retryLoad;
    private readonly RadioButton _modeAll;
    private readonly RadioButton _modeSale;
    private readonly StackPanel _salesPanel = new() { Spacing = 6 };
    private readonly Border _salesBlock;
    private readonly TextBox _amount;
    private readonly Button _fillAll;
    private readonly TextBlock _amountError = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly StackPanel _plan = new() { Spacing = 3 };
    private readonly Grid _methods = new() { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
    private readonly TextBlock _singleMethod = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, IsVisible = false };
    private readonly TextBlock _note = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _success = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    /// <summary>Плашки сообщений (мягкий красный / зелёный фон) — видимость у них, текст внутри.</summary>
    private readonly Border _errorBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), IsVisible = false };
    private readonly Border _successBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), IsVisible = false };
    /// <summary>2026-10-18, девятый круг (п.7): предупреждение к проведённой оплате — жёлтая плашка, закрывает владелец («Понятно»).</summary>
    private readonly Border _warningBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), IsVisible = false };
    private readonly TextBlock _warning = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>2026-10-30: полоса «Идёт оплата долга… N с» — видна только пока запрос оплаты у сервера (он гасит 100+ взносов 10–20 с).</summary>
    private readonly Border _payingBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), IsVisible = false };
    private readonly TextBlock _payingText = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private Avalonia.Threading.DispatcherTimer? _payingTimer;
    private readonly Stopwatch _payingWatch = new();
    private readonly Button _go;
    private readonly Button _cancel;
    /// <summary>2026-10-18, второй круг ревью: «Снять блокировку» — при оплате без ответа (в плашке ошибки).</summary>
    private readonly Button _unlock;
    /// <summary>2026-10-18, второй круг ревью: сумма, подставленная окном (не правленная руками) — при перечитывании пересчитывается.</summary>
    private string _amountDefault = "";
    /// <summary>2026-10-18, третий круг ревью: после «чек уже погашен» чек в режиме «По одному чеку» выбирается только щелчком.</summary>
    private bool _noAutoSelect;
    /// <summary>2026-10-18, ревью: это окно само поднимает оповещение о долге — себя не перечитывает (проверка ДО Post).</summary>
    /// <summary>Ключи своих операций (общий класс OwnOperationKeys): оповещение с таким ключом окно себя не перечитывает.</summary>
    private readonly OwnOperationKeys _ownKeys = new();
    /// <summary>2026-10-18, шестой круг (п.5): ключ замка, показанного на экране (null — экран без замка).</summary>
    private string? _shownUnknownKey;
    /// <summary>2026-10-18, пятый круг: «Оплата есть на сайте — отметить проведённой» (в плашке замка).</summary>
    private readonly Button _paidOnSite;
    /// <summary>Ключ своей НОВОЙ оплаты, которая сейчас идёт (её запись «в пути» — не замок).</summary>
    private string? _activeNewKey;
    /// <summary>2026-10-18, седьмой круг (п.1): последнее конкретное сообщение к показанному замку (закрытая смена, повтор не
    /// отправлен, «состояние изменилось»…) — обновление панели по событию Runner его не затирает, пока ключ тот же.</summary>
    private string? _unknownDetail;

    /// <summary>Оплата принята (окно раздела перечитывает долги).</summary>
    public bool Paid { get; private set; }

    public AcceptDebtPaymentWindow(string clientId, string clientName, string? phone)
    {
        _clientId = clientId;
        _clientName = clientName;
        Title = T("Принять оплату долга", "Карыз төлөмүн кабыл алуу", "Accept a debt payment", "Borç ödemesi al", "Qarz to'lovini qabul qilish");
        // Как у обмена (ModalShell): карточка 640, окно 672 с полями под тень; высота под экран — у ModalShell (85 %).
        Width = 672;
        // 2026-10-22 (модалки, пакет 2): высота по содержимому (было Height 860 — пустое поле вокруг карточки на больших экранах).
        SizeToContent = SizeToContent.Height;
        MinWidth = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        Use(this, BackgroundProperty, "DialogWindowBackground");
        Use(this, TransparencyLevelHintProperty, "DialogTransparencyHint");
        // Пока оплата отправляется, окно не закрывается (Esc, «Отмена», ✕): иначе владелец не увидит, прошла ли она.
        Closing += (_, e) =>
        {
            if (_running)
                e.Cancel = true;
        };

        var root = new StackPanel { Spacing = 12 };

        // 1. Долг клиента — точный остаток с сервера.
        Use(_total, TextBlock.ForegroundProperty, "BrushDanger");
        Use(_totalCaption, TextBlock.ForegroundProperty, "BrushTextSoft");
        Use(_meta, TextBlock.ForegroundProperty, "BrushTextSoft");
        // 2026-10-24 (приёмка, ПР-1b): «по данным сервера» — только когда сумма совпала со сводкой сервера (CheckServerTotalAsync).
        _totalCaption.Text = DealsCaption;
        _retryLoad = UiKit.Ghost(this, T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"));
        // 2026-10-22 (модалки, пакет 2): касание 44 px (было 36–38) у «Повторить», «Не на сайте», «Есть на сайте», «Понятно».
        _retryLoad.Height = 44;
        _retryLoad.MinHeight = 44;
        _retryLoad.IsVisible = false;
        _retryLoad.HorizontalAlignment = HorizontalAlignment.Left;
        _retryLoad.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        var debtCard = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10), BorderThickness = new Thickness(1) };
        Use(debtCard, Border.BackgroundProperty, "BrushPanelSoft");
        Use(debtCard, Border.BorderBrushProperty, "BrushBorder");
        // Подпись и «чеков…» слева, сумма долга справа — одной строкой (окно влезает в экран 1024×768 без прокрутки до «Способа»).
        var debtGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _total.VerticalAlignment = VerticalAlignment.Center;
        _total.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_total, 1);
        debtGrid.Children.Add(new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _totalCaption, _meta, _retryLoad } });
        debtGrid.Children.Add(_total);
        debtCard.Child = debtGrid;
        root.Children.Add(debtCard);

        // 2. Как принять: одной суммой (от старых к новым) или по одному чеку.
        _modeAll = Seg(T("Одной суммой", "Бир суммада", "One amount", "Tek tutar", "Bitta summada"), "debtPayMode");
        _modeSale = Seg(T("По одному чеку", "Бир чек боюнча", "For one receipt", "Tek fiş için", "Bitta chek bo'yicha"), "debtPayMode");
        _modeAll.IsChecked = true;
        _modeAll.IsCheckedChanged += (_, _) => { if (_modeAll.IsChecked == true) SetMode(false); };
        _modeSale.IsCheckedChanged += (_, _) => { if (_modeSale.IsChecked == true) SetMode(true); };
        var modes = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        Grid.SetColumn(_modeSale, 2);
        modes.Children.Add(_modeAll);
        modes.Children.Add(_modeSale);
        root.Children.Add(Block(T("Как принять", "Кантип кабыл алуу", "How to take it", "Nasıl alınır", "Qanday qabul qilish"), modes,
            Small(T("Одной суммой — сервер гасит долги от старых к новым. По чеку — только выбранный чек.",
                "Бир суммада — сервер карыздарды эскисинен жаңысына карай жабат. Чек боюнча — тандалган чек гана.",
                "One amount — the server pays off debts from oldest to newest. For a receipt — only the chosen receipt.",
                "Tek tutar — sunucu borçları eskiden yeniye kapatır. Fiş için — yalnızca seçilen fiş.",
                "Bitta summada — server qarzlarni eskisidan yangisiga yopadi. Chek bo'yicha — faqat tanlangan chek."))));

        // 3. Чек (только «по одному чеку»).
        _salesBlock = Block(T("Какой чек", "Кайсы чек", "Which receipt", "Hangi fiş", "Qaysi chek"), _salesPanel);
        _salesBlock.IsVisible = false;
        root.Children.Add(_salesBlock);

        // 4. Сумма: по умолчанию весь долг; часть — можно, больше остатка — нельзя. Разбор — как в кассе (CashAmountInput).
        _amount = UiKit.Input(this, "0", 52);
        _amount.FontSize = 22;
        _amount.FontWeight = FontWeight.Bold;
        _amount.MaxLength = 14;
        _amount.AddHandler(TextInputEvent, (_, e) =>
        {
            // Только цифры, запятая, точка и пробел (сумма в сомах).
            if (e.Text is { Length: > 0 } text && text.Any(ch => !char.IsDigit(ch) && ch is not (',' or '.' or ' ')))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _amount.TextChanged += (_, _) => UpdateState();
        // 2026-10-22 (модалки, пакет 2): Enter в сумме — «Принять» (кнопки UiKit без фокуса, IsDefault у них не срабатывал).
        _amount.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if (_go.IsEnabled && !_running)
                await SafelyAsync(GoAsync, "приём оплаты (Enter)").ConfigureAwait(true);
        };
        _fillAll = UiKit.Ghost(this, T("Весь долг", "Бүт карыз", "Whole debt", "Tüm borç", "Butun qarz"));
        _fillAll.Height = 52;
        _fillAll.MinHeight = 52;
        _fillAll.Margin = new Thickness(8, 0, 0, 0);
        _fillAll.Click += (_, _) =>
        {
            SetDefaultAmount(MaxAmount);
            _amount.CaretIndex = _amount.Text?.Length ?? 0;
        };
        var amountRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_fillAll, 1);
        amountRow.Children.Add(_amount);
        amountRow.Children.Add(_fillAll);
        Use(_amountError, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(Block(T("Сумма, сом", "Сумма, сом", "Amount, som", "Tutar, som", "Summa, so'm"), amountRow, _amountError, _plan));

        // 5. Способ — по общим настройкам «Оплата долга». Пояснение «в смену кассы не попадут» — внизу у кнопки (всегда видно).
        Use(_singleMethod, TextBlock.ForegroundProperty, "BrushText");
        Use(_note, TextBlock.ForegroundProperty, "BrushTextSoft");
        _note.FontSize = 12.5;
        _note.MinWidth = 140;
        root.Children.Add(Block(T("Способ", "Ыкма", "Method", "Yöntem", "Usul"), _methods, _singleMethod));

        Use(_error, TextBlock.ForegroundProperty, "BrushDangerOnSoft");
        Use(_success, TextBlock.ForegroundProperty, "BrushSuccess");
        Use(_errorBox, Border.BackgroundProperty, "BrushDangerSoft");
        Use(_successBox, Border.BackgroundProperty, "BrushSuccessSoft");
        _unlock = UiKit.Ghost(this, DebtPaymentRunner.UnlockLabel);
        _unlock.Classes.Add("danger");
        _unlock.Height = 44;
        _unlock.MinHeight = 44;
        _unlock.HorizontalAlignment = HorizontalAlignment.Left;
        _unlock.IsVisible = false;
        _unlock.Click += async (_, _) => await SafelyAsync(() => ResolveAsync(DebtResolution.NotOnSite), "решение «оплаты нет»").ConfigureAwait(true);
        _paidOnSite = UiKit.Ghost(this, DebtPaymentRunner.PaidOnSiteLabel);
        _paidOnSite.Height = 44;
        _paidOnSite.MinHeight = 44;
        _paidOnSite.Margin = new Thickness(0, 0, 8, 8);
        _paidOnSite.IsVisible = false;
        _paidOnSite.Click += async (_, _) => await SafelyAsync(() => ResolveAsync(DebtResolution.PaidOnSite), "решение «оплата есть»").ConfigureAwait(true);
        _unlock.Margin = new Thickness(0, 0, 0, 8);
        var decide = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _paidOnSite, _unlock } };
        _errorBox.Child = new StackPanel { Spacing = 8, Children = { _error, decide } };
        _successBox.Child = _success;
        Use(_warningBox, Border.BackgroundProperty, "BrushWarningSoft");
        Use(_warningBox, Border.BorderBrushProperty, "BrushWarning");
        Use(_warning, TextBlock.ForegroundProperty, "BrushText");
        var warningClose = UiKit.Ghost(this, T("Понятно", "Түшүнүктүү", "Got it", "Anladım", "Tushunarli"));
        warningClose.Height = 44;
        warningClose.MinHeight = 44;
        warningClose.Margin = new Thickness(10, 0, 0, 0);
        warningClose.Click += (_, _) => _warningBox.IsVisible = false;
        var warningGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(warningClose, 1);
        warningGrid.Children.Add(_warning);
        warningGrid.Children.Add(warningClose);
        _warningBox.Child = warningGrid;
        // 2026-10-30: полоса прогресса оплаты (неопределённая — сервер отвечает одним ответом) и секунды ожидания.
        Use(_payingBox, Border.BackgroundProperty, "BrushAccentSoft");
        Use(_payingText, TextBlock.ForegroundProperty, "BrushText");
        var payingBar = new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4, CornerRadius = new CornerRadius(2) };
        Use(payingBar, ProgressBar.ForegroundProperty, "BrushAccent");
        _payingBox.Child = new StackPanel { Spacing = 8, Children = { payingBar, _payingText } };
        root.Children.Add(_payingBox);
        root.Children.Add(_errorBox);
        root.Children.Add(_successBox);
        root.Children.Add(_warningBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        _cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        _cancel.IsCancel = true;
        _cancel.Click += (_, _) => { if (!_running) Close(); };
        _go = UiKit.Primary(this, T("Принять", "Кабыл алуу", "Accept", "Al", "Qabul qilish"));
        _go.MinWidth = 240;
        _go.Click += async (_, _) => await SafelyAsync(GoAsync, "приём оплаты").ConfigureAwait(true);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_go);
        var subtitle = string.Join(" · ", new[] { clientName, phone }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var shell = new ModalShell { Margin = new Thickness(16), Title = Title, Subtitle = subtitle, Footer = buttons, Hint = _note, Content = root };
        Content = shell;
        NarrowLayout.Attach(this, 560, narrow =>
        {
            shell.Margin = narrow ? new Thickness(6) : new Thickness(16);
            shell.Padding = narrow ? new Thickness(12, 12) : new Thickness(24, 18);
        });

        // 2026-10-18, ревью: долг клиента изменился в кассе / другой программе (LAN) — остаток, план и максимум перечитываются
        // без сброса введённой суммы и сообщений; больше нового остатка — кнопка неактивна с пояснением.
        // 2026-10-18, девятый круг (п.5): общая отложенная рассылка PosDataEvents.DebouncedDebtChanged; свои операции — по ключу.
        void OnDebtChanged(DebtChangeBatch batch)
        {
            if (!batch.ForeignChangeOf(_clientId, _ownKeys.Contains))
                return;
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                // Во время своей операции — пропуск; догонит SafelyAsync по версии (десятый круг, п.7).
                if (_running || !_loaded || _loadedVersion >= PosDataEvents.DebtVersionOf(_clientId))
                    return;
                PosLogger.Log($"Владелец: оплата долга — долг клиента {_clientId} изменился в другой программе, остаток перечитан.", "PAYMENT");
                await LoadAsync(keepInput: true).ConfigureAwait(true);
            });
        }
        // 2026-10-18, пятый круг (п.3): замок появился/снят (в т. ч. в другом окне) — _unknownOutcome заново из Runner.
        void OnRunnerChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(SyncUnknownFromRunner);
        Opened += (_, _) =>
        {
            PosDataEvents.DebouncedDebtChanged += OnDebtChanged;
            DebtPaymentRunner.Changed += OnRunnerChanged;
        };
        Closed += (_, _) =>
        {
            PosDataEvents.DebouncedDebtChanged -= OnDebtChanged;
            DebtPaymentRunner.Changed -= OnRunnerChanged;
            _payingTimer?.Stop();
        };

        BuildMethods();
        ShowLoading();
        Opened += async (_, _) =>
        {
            if (_loadStarted)
                return;
            _loadStarted = true;
            // 2026-10-18, седьмой круг: журнал незавершённых оплат общий с кассой на этом ПК — перечитать при открытии.
            DebtPaymentRunner.Refresh();
            // Способы — свежие с сервера (лёгкий запрос; сбой — прежние значения).
            await DebtPaymentSettings.RefreshAsync(force: true).ConfigureAwait(true);
            BuildMethods();
            await LoadAsync().ConfigureAwait(true);
            _amount.Focus();
        };
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Money(double v) => DebtPaymentFlow.Money(v);

    private static string AmountText(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <remarks>2026-10-18, десятый круг (п.6): итог и максимум — только по точным остаткам (без «уточняется»).</remarks>
    private double Total => _rows.Where(r => r.Amount > 0.005 && !r.RemainingUnknown).Sum(r => r.Amount);

    private double MaxAmount => _bySale ? (_selectedRow is { RemainingUnknown: false } row ? row.Amount : 0) : Total;

    private static string DealsCaption => T("Долг по сделкам клиента", "Кардардын келишимдери боюнча карыз", "Debt by the customer's deals",
        "Müşterinin işlemlerine göre borç", "Mijoz bitimlari bo'yicha qarz");

    private static string ServerCaption => T("Долг сейчас (по данным сервера)", "Азыркы карыз (сервердин маалыматы боюнча)", "Debt now (from the server)",
        "Şu anki borç (sunucu verisi)", "Hozirgi qarz (server ma'lumoti bo'yicha)");

    private int _captionGen;

    /// <summary>2026-10-24 (приёмка, ПР-1b): подпись «Долг сейчас (по данным сервера)» — только если сумма долга совпала с суммой клиента в
    /// сводке сервера (clients/debtors); иначе (нет связи, расхождение, остаток по части чеков уточняется) — «Долг по сделкам клиента».</summary>
    private async Task CheckServerTotalAsync(double total, bool anyUnknown)
    {
        var gen = ++_captionGen;
        _totalCaption.Text = DealsCaption;
        if (anyUnknown || !Online || App.AppHost?.Services.GetService<ClientDebtsApiService>() is not { } api)
            return;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
            var debtors = await api.GetDebtorsAsync(0.01, cts.Token).ConfigureAwait(true);
            if (gen != _captionGen)
                return;
            var server = debtors.Where(d => string.Equals(d.ClientId, _clientId, StringComparison.OrdinalIgnoreCase)).Sum(d => d.DebtTotal);
            if (Math.Abs(server - total) <= 0.01)
                _totalCaption.Text = ServerCaption;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Владелец: сумма долга клиента {_clientId} со сводкой сервера не сверена ({ex.GetType().Name}).", "WARNING");
        }
    }

    private static bool Online => !OfflineModeHelper.IsServerOutage && !OfflineModeHelper.UseLocalOperations;

    // ── Загрузка ───────────────────────────────────────────────────────────────────────

    private void ShowLoading()
    {
        _total.Text = "…";
        _meta.Text = T("Загружаю точный остаток с сервера…", "Сервердан так калдык жүктөлүүдө…", "Loading the exact balance from the server…",
            "Sunucudan kesin bakiye yükleniyor…", "Serverdan aniq qoldiq yuklanmoqda…");
        UpdateState();
    }

    /// <param name="keepInput">2026-10-18, ревью: перечитывание «со стороны» и проверка остатка — сообщения, введённая сумма и
    /// выбор остаются, прежние цифры видны до прихода новых. true — список получен.</param>
    private async Task<bool> LoadAsync(bool keepInput = false)
    {
        var gen = ++_loadGen;
        var startVersion = PosDataEvents.DebtVersionOf(_clientId);
        _retryLoad.IsVisible = false;
        if (!keepInput)
            _errorBox.IsVisible = false;
        if (!Online)
        {
            ShowLoadError(OfflineText);
            return false;
        }
        if (!keepInput)
            ShowLoading();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var salesApi = App.GetRequiredService<ISalesApiService>();
            var rows = await PayDebtDialog.LoadDebtRowsAsync(salesApi, _clientId, cts.Token).ConfigureAwait(true);
            if (gen != _loadGen)
                return false;
            _loadedVersion = startVersion;
            ApplyRows(rows, keepInput);
            return true;
        }
        catch (Exception ex)
        {
            if (gen != _loadGen)
                return false;
            PosLogger.Log($"Владелец: оплата долга — долги клиента {_clientId} не загружены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowLoadError(T("Не удалось получить долг с сервера: ", "Карызды серверден алуу мүмкүн болгон жок: ", "Could not get the debt from the server: ",
                              "Borç sunucudan alınamadı: ", "Qarzni serverdan olib bo'lmadi: ")
                          + (ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi") : ex.Message));
            return false;
        }
    }

    private static string OfflineText => T(
        "Нет связи с сервером. Принять оплату долга можно только при связи — долг и оплата хранятся на сервере.",
        "Сервер менен байланыш жок. Карыз төлөмүн байланыш болгондо гана кабыл алса болот — карыз жана төлөм серверде сакталат.",
        "No connection to the server. A debt payment can only be taken online — the debt and the payment live on the server.",
        "Sunucu bağlantısı yok. Borç ödemesi yalnızca bağlantı varken alınabilir — borç ve ödeme sunucuda tutulur.",
        "Server bilan aloqa yo'q. Qarz to'lovini faqat aloqa bo'lganda qabul qilish mumkin — qarz va to'lov serverda saqlanadi.");

    private void ShowLoadError(string text)
    {
        _loaded = false;
        _total.Text = "—";
        _meta.Text = "";
        _error.Text = text;
        _errorBox.IsVisible = true;
        _retryLoad.IsVisible = true;
        UpdateState();
    }

    /// <summary>Строки долга с точным остатком (от новых к старым, как у кассы); погашенные не показываются.</summary>
    private void ApplyRows(List<DebtSaleRow> rows, bool keepInput = false)
    {
        // 2026-10-18, ревью после живой проверки (п.3, деньги): неуточнённые строки не отбрасываются — нужны OneSumRule.
        _rows = rows.Where(r => r.Amount > 0.005 || r.RemainingUnknown).OrderBy(r => r.CreatedAt).ToList();
        _loaded = true;
        // 2026-10-18, второй круг ревью: выбранный чек погасили в другом месте — другой молча не выбираем: выбор снят,
        // сумма сброшена, пояснение.
        string? paidElsewhere = null;
        if (_selectedRow is { } was)
        {
            _selectedRow = _rows.FirstOrDefault(r => r.Id == was.Id);
            if (_selectedRow is null && _bySale && !_done && !_unknownOutcome)
            {
                paidElsewhere = was.Number.Length > 0 ? "№" + was.Number : was.DateDisplay;
                _noAutoSelect = true;
            }
        }
        else if (!_noAutoSelect)
            _selectedRow = _rows.FirstOrDefault(r => !r.IsNoSale) ?? _rows.FirstOrDefault(); // 2026-10-23: «долг без продажи» по чеку не принимается — по умолчанию не он
        var total = Total;
        _total.Text = Money(total);
        Use(_total, TextBlock.ForegroundProperty, total > 0.005 ? "BrushDanger" : "BrushSuccess");
        _ = CheckServerTotalAsync(total, _rows.Any(r => r.RemainingUnknown)); // 2026-10-24 (приёмка, ПР-1b): подпись суммы по сверке с сервером
        if (_rows.Count == 0)
        {
            _meta.Text = T("Долгов нет — клиент рассчитался.", "Карыз жок — кардар эсептешти.", "No debts — the customer has paid.",
                "Borç yok — müşteri ödedi.", "Qarz yo'q — mijoz to'lagan.");
        }
        else
        {
            var oldest = _rows.Min(r => r.CreatedAt).LocalDateTime;
            // 2026-10-18, девятый круг: по части чеков точный остаток не получен — «Повторить» загрузку видна.
            _retryLoad.IsVisible = _rows.Any(r => r.RemainingUnknown);
            _meta.Text = T($"чеков в долг: {_rows.Count} · самый старый с {oldest:dd.MM.yyyy}",
                $"карыз чектер: {_rows.Count} · эң эскиси {oldest:dd.MM.yyyy} баштап",
                $"receipts on credit: {_rows.Count} · oldest since {oldest:dd.MM.yyyy}",
                $"veresiye fiş: {_rows.Count} · en eskisi {oldest:dd.MM.yyyy}",
                $"qarzga cheklar: {_rows.Count} · eng eskisi {oldest:dd.MM.yyyy} dan")
                + DebtPaymentFlow.CheckingSuffix(_rows.Count(r => r.RemainingUnknown));
        }
        // 2026-10-18, второй круг ревью: сумма, которую не правили руками, пересчитывается в новый остаток; правленная остаётся.
        var edited = keepInput && (_amount.Text ?? "") != _amountDefault;
        if (!_done && !_unknownOutcome && !edited)
            SetDefaultAmount(total > 0.005 ? (_bySale ? MaxAmount : total) : 0);
        if (paidElsewhere is not null)
        {
            SetDefaultAmount(0);
            ShowError(T($"Чек {paidElsewhere} уже погашен — выберите чек.", $"{paidElsewhere} чек мурунтан эле жабылган — чекти тандаңыз.",
                $"Receipt {paidElsewhere} is already paid off — choose a receipt.", $"{paidElsewhere} fişi zaten kapanmış — bir fiş seçin.",
                $"{paidElsewhere} chek allaqachon yopilgan — chekni tanlang."));
        }
        // По клиенту висит оплата без ответа (окно закрывали, программу перезапускали) — вернуть её сумму, способ, чек и замок.
        if (!_unknownOutcome && !_done && !_running && DebtPaymentRunner.UnknownFor(_clientId) is { } unknown)
            RestoreUnknown(unknown);
        RenderSales();
        UpdateState();
    }

    /// <summary>Сумма по умолчанию (весь долг / остаток чека) — запоминается, чтобы отличать правку руками.</summary>
    private void SetDefaultAmount(double amount)
    {
        _amount.Text = amount > 0.005 ? AmountText(amount) : "";
        _amountDefault = _amount.Text ?? "";
    }

    /// <summary>Экран полностью по записи замка: способ, режим, чек, сумма из записи; поля закрыты. 2026-10-18, седьмой круг
    /// (п.6): вызывается и при смене ключа записи — «Повторить» отправляет только показанное.</summary>
    /// <param name="detail">Уточнение под текстом замка (null — нет).</param>
    private void RestoreUnknown(UnknownDebtPayment p, string? detail = null)
    {
        _unknownOutcome = true;
        _shownUnknownKey = p.Key;
        _unknownDetail = detail;
        _method = p.Method;
        if (p.SaleId is not null)
        {
            _modeSale.IsChecked = true; // SetMode(true)
            // 2026-10-18, третий круг ревью: только чек из записи; нет в списке — строкой «Чек №X — проверяется», другой не подсвечиваем.
            _selectedRow = _rows.FirstOrDefault(r => string.Equals(r.Id, p.SaleId, StringComparison.OrdinalIgnoreCase));
        }
        else
            _modeAll.IsChecked = true; // SetMode(false): прошлый замок мог быть по чеку
        _amount.Text = AmountText(p.Amount);
        ShowUnknown(p);
        BuildMethods();
    }

    /// <summary>Текст и кнопки замка ТОЙ ЖЕ записи, что на экране (ключ не меняется; сменился — RestoreUnknown).</summary>
    private void ShowUnknown(UnknownDebtPayment p)
    {
        _unlock.IsVisible = true;
        _paidOnSite.IsVisible = true;
        var inFlight = DebtPaymentRunner.IsInFlight(_clientId);
        // 2026-10-18, седьмой круг: оплату начала касса на этом ПК — решение только там.
        _unlock.IsEnabled = _paidOnSite.IsEnabled = !inFlight && !_running && DebtPaymentRunner.IsOwnProgram(p);
        var text = DebtPaymentRunner.UnknownText(p, ownOperation: _running && _ownKeys.Contains(p.Key));
        ShowError(string.IsNullOrEmpty(_unknownDetail) ? text : text + " " + _unknownDetail);
    }

    /// <summary>2026-10-18, седьмой круг (п.1): сообщение при замке на экране — уточнением к нему (не затирает текст замка
    /// и не затирается обновлением панели); без замка — обычной плашкой.</summary>
    private void ShowMessage(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (_unknownOutcome && DebtPaymentRunner.UnknownFor(_clientId) is { } p && p.Key == _shownUnknownKey)
        {
            _unknownDetail = text;
            ShowUnknown(p);
        }
        else
            ShowError(text);
    }

    /// <summary>Снять замок с экрана (записи больше нет).</summary>
    private void ClearUnknown()
    {
        _unknownOutcome = false;
        _unlock.IsVisible = _paidOnSite.IsVisible = false;
        _shownUnknownKey = null;
        _unknownDetail = null;
        _errorBox.IsVisible = false;
    }

    private void RememberOwnKey(string key) => _ownKeys.Remember(key);

    /// <summary>2026-10-18, шестой круг (п.3): обработчики кнопок — без необработанных исключений: ошибка текстом и в журнал;
    /// окно не остаётся «занятым».</summary>
    private async Task SafelyAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _running = false;
            PosLogger.Log($"Владелец: оплата долга — {what} не выполнено: {ex}", "WARNING");
            ShowMessage(T("Не удалось выполнить: ", "Аткаруу мүмкүн болгон жок: ", "Couldn't complete: ", "Tamamlanamadı: ", "Bajarib bo'lmadi: ") + ex.Message);
            UpdateState();
            RenderSales();
        }
        // 2026-10-18, десятый круг (п.7): оповещение «долг изменился», пришедшее во время своей операции (его пропускают при
        // _running), не теряется — после операции при любом исходе: загруженный список старше последнего изменения → перечитать.
        try
        {
            if (!_running && _loaded && _loadedVersion < PosDataEvents.DebtVersionOf(_clientId))
                await LoadAsync(keepInput: true).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Владелец: оплата долга — перечитывание после операции не удалось ({ex.Message}).", "WARNING");
        }
    }

    /// <summary>2026-10-18, пятый круг: окно не решает по своему кэшу — состояние замка всегда из DebtPaymentRunner.
    /// Седьмой круг: тот же ключ — только текст (уточнение остаётся, п.1); другой ключ — экран целиком по новой записи и
    /// «Состояние оплаты изменилось» (п.6).</summary>
    private void SyncUnknownFromRunner()
    {
        if (_done)
            return;
        var record = DebtPaymentRunner.UnknownFor(_clientId);
        var locked = record is not null && !(_running && record.Key == _activeNewKey);
        if (locked && record is not null)
        {
            if (!_unknownOutcome || record.Key != _shownUnknownKey)
                RestoreUnknown(record, _shownUnknownKey is not null && record.Key != _shownUnknownKey ? DebtPaymentRunner.StateChangedText : null);
            else
                ShowUnknown(record);
        }
        else if (_unknownOutcome)
        {
            ClearUnknown();
            // Сумма на экране была из записи (её завершили в другом окне / программе) — снова весь долг, а не чужая сумма.
            if (_loaded)
                SetDefaultAmount(MaxAmount);
            BuildMethods();
        }
        UpdateState();
        RenderSales();
    }

    /// <summary>Действие не совпало с Runner — экран по Runner и «Состояние оплаты изменилось».</summary>
    private void StateChanged()
    {
        SyncUnknownFromRunner();
        ShowMessage(DebtPaymentRunner.StateChangedText);
    }

    /// <summary>2026-10-18, пятый круг ревью: решение по замку после проверки на сайте NurCRM — подтверждение и код ДО очереди,
    /// ResolveAsync закрывает запись только с тем же ключом. «Есть на сайте» — журнал, LAN (смены у владельца нет).
    /// Седьмой круг (п.4): только по ключу, ПОКАЗАННОМУ на экране (как в кассе); в Runner другое — «Состояние изменилось».</summary>
    private async Task ResolveAsync(DebtResolution resolution)
    {
        if (_running || !_unknownOutcome || _shownUnknownKey is not { } shownKey)
            return;
        if (DebtPaymentRunner.UnknownFor(_clientId) is not { } p || p.Key != shownKey)
        {
            StateChanged();
            return;
        }
        _running = true;
        _unknownDetail = null;
        UpdateState();
        try
        {
            // 2026-10-18, десятый круг (п.5): одной суммой «Оплата есть» — сумма, которую сервер провёл (вводит владелец).
            if (await DebtPaymentRunner.ConfirmResolutionAsync(this, p, resolution).ConfigureAwait(true) is not { } appliedAmount)
                return;
            var (granted, gateError) = await DebtPaymentFlow.ConfirmEmployeeCodeAsync().ConfigureAwait(true);
            if (!granted)
            {
                ShowMessage(gateError);
                return;
            }
            RememberOwnKey(p.Key);
            var (done, error, warning) = await DebtPaymentRunner.ResolveAsync(p.Key, resolution, appliedAmount).ConfigureAwait(true);
            if (!done)
            {
                _running = false;
                SyncUnknownFromRunner();
                ShowMessage(error);
                return;
            }
            ClearUnknown();
            if (resolution == DebtResolution.PaidOnSite)
            {
                _done = true;
                Paid = true;
                _success.Text = DebtPaymentRunner.ResolvedPaidText(p with { Amount = appliedAmount });
            }
            else
                _success.Text = DebtPaymentRunner.ResolvedUnlockedText;
            _successBox.IsVisible = true;
            if (warning is not null)
                ShowWarning(warning);
        }
        finally
        {
            _running = false;
            UpdateState();
            RenderSales();
            BuildMethods();
        }
        await LoadAsync(keepInput: true).ConfigureAwait(true);
    }

    // ── Выбор ──────────────────────────────────────────────────────────────────────────

    private void SetMode(bool bySale)
    {
        if (_bySale == bySale)
            return;
        // 2026-10-18, второй круг ревью: при «по чеку» без выбранного чека — первый (после «чек уже погашен» выбор снят).
        if (bySale && _selectedRow is null && !_unknownOutcome && !_noAutoSelect)
            _selectedRow = _rows.FirstOrDefault(r => !r.IsNoSale) ?? _rows.FirstOrDefault(); // 2026-10-23: «долг без продажи» по чеку не принимается — по умолчанию не он
        _bySale = bySale;
        _salesBlock.IsVisible = bySale;
        _fillAll.Content = bySale
            ? T("Весь остаток чека", "Чектин бүт калдыгы", "Whole receipt balance", "Fişin tüm bakiyesi", "Chekning butun qoldig'i")
            : T("Весь долг", "Бүт карыз", "Whole debt", "Tüm borç", "Butun qarz");
        if (!_unknownOutcome && !_done)
            SetDefaultAmount(MaxAmount);
        RenderSales();
        UpdateState();
    }

    private void RenderSales()
    {
        _salesPanel.Children.Clear();
        if (!_bySale)
            return;
        // 2026-10-18, третий круг ревью: при оплате без ответа по чеку выделен именно её чек; его нет в списке — строкой сверху.
        var unknownSale = _unknownOutcome ? DebtPaymentRunner.UnknownFor(_clientId)?.SaleId : null;
        if (unknownSale is not null && !_rows.Any(r => string.Equals(r.Id, unknownSale, StringComparison.OrdinalIgnoreCase)))
        {
            var label = DebtPaymentRunner.UnknownFor(_clientId)?.SaleLabel ?? "";
            var pending = new TextBlock
            {
                Text = T($"Чек {label} — проверяется (оплата без ответа сервера).", $"{label} чек — текшерилүүдө (сервердин жообусуз төлөм).",
                    $"Receipt {label} — being checked (payment without a server answer).", $"{label} fişi — kontrol ediliyor (sunucu yanıtı olmayan ödeme).",
                    $"{label} chek — tekshirilmoqda (server javobisiz to'lov)."),
                FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            };
            Use(pending, TextBlock.ForegroundProperty, "BrushText");
            var pendingCard = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8), BorderThickness = new Thickness(2), Child = pending };
            Use(pendingCard, Border.BackgroundProperty, "BrushAccentSoft");
            Use(pendingCard, Border.BorderBrushProperty, "BrushAccent");
            _salesPanel.Children.Add(pendingCard);
        }
        foreach (var row in _rows.OrderBy(r => r.CreatedAt))
        {
            var selected = unknownSale is not null
                ? string.Equals(row.Id, unknownSale, StringComparison.OrdinalIgnoreCase)
                : ReferenceEquals(row, _selectedRow);
            var date = row.CreatedAt == default ? row.DateDisplay : row.CreatedAt.LocalDateTime.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var title = string.Join(" · ", new[] { date, row.Number.Length > 0 ? "№" + row.Number : null, row.FirstItemName }
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            if (row.IsNoSale)
                title += T("  (только «Одной суммой»)", "  («Бир сумма менен» гана)", "  (only “One sum”)", "  (yalnızca “Tek tutar”)", "  (faqat «Bir summada»)"); // 2026-10-23: у долга без продажи нет чека
            var name = new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            // 2026-10-18, девятый круг: точного остатка нет — «остаток уточняется» вместо суммы.
            var sum = new TextBlock { Text = row.RemainingUnknown ? row.RemainingUnknownLabel : Money(row.Amount), FontSize = 14.5, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Use(sum, TextBlock.ForegroundProperty, row.RemainingUnknown ? "BrushWarning" : "BrushDanger");
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Grid.SetColumn(sum, 1);
            g.Children.Add(name);
            g.Children.Add(sum);
            var radio = new RadioButton
            {
                GroupName = "debtPaySale", IsChecked = selected, Content = g, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center,
                IsEnabled = !_unknownOutcome && !_done && !_running && !row.IsNoSale,
            };
            Avalonia.Automation.AutomationProperties.SetName(radio, title + " — " + Money(row.Amount));
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked != true || ReferenceEquals(_selectedRow, row))
                    return;
                _selectedRow = row;
                if (!_unknownOutcome && !_done)
                    SetDefaultAmount(row.Amount);
                Avalonia.Threading.Dispatcher.UIThread.Post(RenderSales);
                UpdateState();
            };
            var card = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 6), BorderThickness = new Thickness(selected ? 2 : 1), Child = radio };
            Use(card, Border.BackgroundProperty, selected ? "BrushAccentSoft" : "BrushPanel");
            Use(card, Border.BorderBrushProperty, selected ? "BrushAccent" : "BrushBorder");
            _salesPanel.Children.Add(card);
        }
    }

    private void BuildMethods()
    {
        var allowed = DebtPaymentSettings.MethodsHere().ToList();
        if (!allowed.Contains(_method))
        {
            // Ответ на прошлую попытку не дошёл — повтор идёт тем же способом (и тем же ключом), даже если настройку поменяли.
            if (_unknownOutcome)
                allowed = new List<string> { _method };
            else
                _method = allowed[0];
        }
        _methods.Children.Clear();
        _methods.IsVisible = allowed.Count > 1;
        _singleMethod.IsVisible = allowed.Count == 1;
        _singleMethod.Text = DebtPaymentFlow.MethodTitle(_method);
        if (allowed.Count > 1)
        {
            var col = 0;
            foreach (var m in allowed)
            {
                var code = m;
                var seg = Seg(DebtPaymentFlow.MethodTitle(code), "debtPayMethod");
                seg.IsChecked = code == _method;
                seg.IsEnabled = !_unknownOutcome && !_done;
                seg.IsCheckedChanged += (_, _) =>
                {
                    if (seg.IsChecked != true)
                        return;
                    _method = code;
                    UpdateState();
                };
                Grid.SetColumn(seg, col);
                col += 2;
                _methods.Children.Add(seg);
            }
        }
        UpdateState();
    }

    // ── Проверка и подсказки ───────────────────────────────────────────────────────────

    /// <summary>Сумма из поля: null — не число или не больше нуля.</summary>
    private double? EnteredAmount =>
        CashAmountInput.TryParse(_amount.Text, out var v) && v > 0m ? (double)Math.Round(v, 2) : null;

    private string? AmountProblem()
    {
        if (!_loaded || _rows.Count == 0)
            return null;
        if (_bySale && _selectedRow is null && !_unknownOutcome)
            return T("Выберите чек.", "Чекти тандаңыз.", "Choose a receipt.", "Bir fiş seçin.", "Chekni tanlang.");
        // 2026-10-18, девятый круг (деньги): «точный остаток или отказ». Одной суммой — нужен точный остаток ВСЕХ чеков;
        // по чеку — выбранного (как в кассе).
        if (!_unknownOutcome)
        {
            if (_bySale)
            {
                if (_selectedRow is { RemainingUnknown: true } sel)
                    return DebtPaymentFlow.RemainingUnknownText(new[] { sel }, owner: true);
                // 2026-10-21 (ревью денег, A2): долг без продажи — только «всем долгом» (одной суммой).
                if (_selectedRow is { IsNoSale: true })
                    return DebtPaymentFlow.NoSaleByReceiptText;
            }
            // 2026-10-18, живая проверка: одной суммой — по правилу OneSumRule (неуточнённый новее точных — можно до их суммы).
            else if (DebtPaymentFlow.OneSumRule(_rows, owner: true) is { Allowed: false } oneSum)
                return oneSum.Text;
        }
        if (string.IsNullOrWhiteSpace(_amount.Text))
            return T("Укажите сумму оплаты.", "Төлөм суммасын көрсөтүңүз.", "Enter the payment amount.", "Ödeme tutarını belirtin.", "To'lov summasini ko'rsating.");
        if (EnteredAmount is not { } amount)
            return T("Сумма — число больше нуля, например 1500 или 1500,50.", "Сумма — нөлдөн чоң сан, мисалы 1500 же 1500,50.",
                "The amount must be a number above zero, e.g. 1500 or 1500.50.", "Tutar sıfırdan büyük bir sayı olmalı, örn. 1500 veya 1500,50.",
                "Summa — noldan katta son, masalan 1500 yoki 1500,50.");
        var max = MaxAmount;
        if (amount > max + 0.005)
            return _bySale
                ? T($"Больше остатка по чеку ({Money(max)}) принять нельзя.", $"Чектин калдыгынан ({Money(max)}) ашык кабыл алууга болбойт.",
                    $"You can't take more than the receipt balance ({Money(max)}).", $"Fiş bakiyesinden ({Money(max)}) fazlası alınamaz.",
                    $"Chek qoldig'idan ({Money(max)}) ko'p qabul qilib bo'lmaydi.")
                : T($"Больше долга ({Money(max)}) принять нельзя.", $"Карыздан ({Money(max)}) ашык кабыл алууга болбойт.",
                    $"You can't take more than the debt ({Money(max)}).", $"Borçtan ({Money(max)}) fazlası alınamaz.",
                    $"Qarzdan ({Money(max)}) ko'p qabul qilib bo'lmaydi.");
        return null;
    }

    private void UpdateState()
    {
        if (_go is null)
            return;
        var problem = AmountProblem();
        _amountError.Text = problem ?? "";
        _amountError.IsVisible = problem != null && _loaded && _rows.Count > 0 && !_done;
        var amount = EnteredAmount;
        var ok = _loaded && _rows.Count > 0 && problem is null && amount is not null && Online;
        // 2026-10-18, третий круг ревью: «Повторить» — по записи неизвестной оплаты, не по списку и полю суммы (долг мог уже стать 0:
        // первая попытка прошла и закрыла всё — сервер вернёт replayed).
        var unknownRecord = _unknownOutcome ? DebtPaymentRunner.UnknownFor(_clientId) : null;
        if (unknownRecord is not null)
        {
            // 2026-10-18, седьмой круг: оплату начала касса на этом ПК — «Повторить» только там.
            ok = Online && DebtPaymentRunner.IsOwnProgram(unknownRecord) && !DebtPaymentRunner.IsRetryExpired(unknownRecord);
            amount = unknownRecord.Amount;
        }

        _amount.IsEnabled = _loaded && _rows.Count > 0 && !_running && !_done && !_unknownOutcome;
        _fillAll.IsEnabled = _amount.IsEnabled;
        _modeAll.IsEnabled = _modeSale.IsEnabled = _loaded && _rows.Count > 0 && !_running && !_done && !_unknownOutcome;
        foreach (var seg in _methods.Children.OfType<RadioButton>())
            seg.IsEnabled = !_running && !_done && !_unknownOutcome;
        _cancel.IsEnabled = !_running;
        // После оплаты — одна кнопка «Готово»; Esc и ✕ нажимают её (ModalShell ищет видимую кнопку с IsCancel).
        _cancel.IsVisible = !_done;
        _cancel.IsCancel = !_done;
        _go.IsCancel = _done;
        _cancel.Content = _done ? T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish") : T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");

        if (_done)
        {
            _go.Content = T("Готово", "Даяр", "Done", "Tamam", "Tayyor");
            _go.IsEnabled = true;
        }
        else if (_running)
        {
            _go.Content = T("Оплата…", "Төлөм…", "Paying…", "Ödeniyor…", "To'lanmoqda…");
            _go.IsEnabled = false;
        }
        else
        {
            var sumText = amount is { } a && (problem is null || unknownRecord is not null) ? " " + Money(a) : "";
            _go.Content = _unknownOutcome
                ? T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish") + sumText
                : T("Принять", "Кабыл алуу", "Accept", "Al", "Qabul qilish") + sumText;
            _go.IsEnabled = ok;
        }
        Avalonia.Automation.AutomationProperties.SetName(_go, _go.Content as string);

        _note.Text = _method == DebtPaymentSettings.MethodTransfer
            ? T("Безнал (карта, перевод) — без смены: в смену кассы и в ящик не попадёт.",
                "Накталай эмес (карта, которуу) — сменасыз: кассанын сменасына жана кутусуна кирбейт.",
                "Non-cash (card, transfer) — no shift: it won't go into a till shift or the drawer.",
                "Nakitsiz (kart, havale) — vardiyasız: kasa vardiyasına ve çekmeceye girmez.",
                "Naqd emas (karta, o'tkazma) — smenasiz: kassa smenasiga va qutiga tushmaydi.")
            : T("Наличные у вас в руках — в смену кассы не попадут.",
                "Накталай акча сиздин колуңузда — кассанын сменасына кирбейт.",
                "The cash is in your hands — it won't go into a till shift.",
                "Nakit sizin elinizde — kasa vardiyasına girmez.",
                "Naqd pul sizning qo'lingizda — kassa smenasiga tushmaydi.");
        _singleMethod.Text = DebtPaymentFlow.MethodTitle(_method);
        RenderPlan(amount, problem is null);
    }

    /// <summary>Какие чеки закроет сумма: «одной суммой» — от старых к новым; «по чеку» — закроется ли он.</summary>
    private void RenderPlan(double? amount, bool valid)
    {
        _plan.Children.Clear();
        // 2026-10-18, третий круг ревью: при оплате без ответа план не показываем (повтор идёт по записи, не по списку).
        if (!_loaded || _done || _unknownOutcome || amount is not { } a || !valid || _rows.Count == 0)
            return;
        void Line(string text, string brush = "BrushTextSoft", bool bold = false)
        {
            var t = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
            Use(t, TextBlock.ForegroundProperty, brush);
            _plan.Children.Add(t);
        }
        _plan.Margin = new Thickness(0, 4, 0, 0);
        if (_bySale && _selectedRow is { } row)
        {
            var left = Math.Max(0, row.Amount - a);
            Line(left <= 0.005
                ? T("Чек закроется полностью.", "Чек толугу менен жабылат.", "The receipt will be fully paid off.", "Fiş tamamen kapanır.", "Chek to'liq yopiladi.")
                : T($"По чеку останется {Money(left)}.", $"Чек боюнча {Money(left)} калат.", $"{Money(left)} will remain on the receipt.",
                    $"Fişte {Money(left)} kalır.", $"Chek bo'yicha {Money(left)} qoladi."), left <= 0.005 ? "BrushSuccess" : "BrushText", true);
            return;
        }
        var parts = DebtPaymentFlow.Allocate(_rows.Where(r => !r.RemainingUnknown), a);
        var restAll = Math.Max(0, Total - a);
        Line(restAll <= 0.005
            ? T("Долг закроется полностью:", "Карыз толугу менен жабылат:", "The debt will be fully paid off:", "Borç tamamen kapanır:", "Qarz to'liq yopiladi:")
            : T($"Закроет (от старых к новым), останется {Money(restAll)}:", $"Жабат (эскисинен жаңысына), {Money(restAll)} калат:",
                $"Will pay off (oldest first), {Money(restAll)} will remain:", $"Kapatır (eskiden yeniye), {Money(restAll)} kalır:",
                $"Yopadi (eskisidan yangisiga), {Money(restAll)} qoladi:"), restAll <= 0.005 ? "BrushSuccess" : "BrushText", true);
        // 2026-10-21 (ревью денег, A2): есть долг без продажи — порядок сервера (по сроку) касса точно не знает: по чекам план
        // не обещаем (кроме полного погашения — тогда закроется всё), только остаток.
        var noSaleTotal = _rows.Where(r => r.IsNoSale && !r.RemainingUnknown).Sum(r => r.Amount);
        if (noSaleTotal > 0.005 && restAll > 0.005)
        {
            Line(DebtPaymentFlow.NoSalePlanNote(noSaleTotal));
            return;
        }
        foreach (var (r, part, closed) in parts.Take(4))
        {
            var date = r.CreatedAt == default ? r.DateDisplay : r.CreatedAt.LocalDateTime.ToString("dd.MM", CultureInfo.InvariantCulture);
            var who = (r.IsNoSale ? T("долг без продажи ", "сатуусуз карыз ", "debt without a sale ", "satışsız borç ", "sotuvsiz qarz ") : "") // 2026-10-21 (ревью денег, A2)
                      + (r.Number.Length > 0 ? "№" + r.Number + " " : "") + T($"от {date}", $"{date}", $"of {date}", $"{date}", $"{date}");
            Line(closed
                ? T($"✓ {who} — {Money(part)}, закроется", $"✓ {who} — {Money(part)}, жабылат", $"✓ {who} — {Money(part)}, paid off",
                    $"✓ {who} — {Money(part)}, kapanır", $"✓ {who} — {Money(part)}, yopiladi")
                : T($"◐ {who} — {Money(part)} из {Money(r.Amount)}, останется {Money(r.Amount - part)}",
                    $"◐ {who} — {Money(r.Amount)} ичинен {Money(part)}, {Money(r.Amount - part)} калат",
                    $"◐ {who} — {Money(part)} of {Money(r.Amount)}, {Money(r.Amount - part)} left",
                    $"◐ {who} — {Money(r.Amount)} içinden {Money(part)}, {Money(r.Amount - part)} kalır",
                    $"◐ {who} — {Money(r.Amount)} dan {Money(part)}, {Money(r.Amount - part)} qoladi"));
        }
        if (parts.Count > 4)
            Line(T($"…и ещё чеков: {parts.Count - 4}", $"…дагы чектер: {parts.Count - 4}", $"…and {parts.Count - 4} more receipts",
                $"…ve {parts.Count - 4} fiş daha", $"…va yana {parts.Count - 4} ta chek"));
    }

    // ── Оплата ─────────────────────────────────────────────────────────────────────────

    private async Task GoAsync()
    {
        if (_done)
        {
            Close();
            return;
        }
        if (_running)
            return;
        _errorBox.IsVisible = false;
        if (!Online)
        {
            ShowError(OfflineText);
            return;
        }
        // 2026-10-18, шестой круг ревью (п.5, деньги): решение — по СОСТОЯНИЮ НА ЭКРАНЕ. На экране замок — только «Повторить»
        // ровно этой записи (RetryAsync по показанному ключу); замка нет — только новая оплата введённой суммы (PayNewAsync).
        // Runner показывает другое — отказ «Состояние оплаты изменилось», экран обновляется.
        var runnerRecord = DebtPaymentRunner.UnknownFor(_clientId);
        UnknownDebtPayment? unknownRecord = null;
        double amount;
        if (_unknownOutcome)
        {
            if (runnerRecord is null || runnerRecord.Key != _shownUnknownKey)
            {
                StateChanged();
                return;
            }
            unknownRecord = runnerRecord;
            amount = runnerRecord.Amount;
        }
        else
        {
            if (runnerRecord is not null)
            {
                StateChanged();
                return;
            }
            if (AmountProblem() is not null || EnteredAmount is not { } entered)
            {
                UpdateState();
                return;
            }
            amount = entered;
        }
        var row = unknownRecord is not null ? null : _bySale ? _selectedRow : null;
        if (unknownRecord is null && _bySale && row is null)
            return;
        var method = unknownRecord?.Method ?? _method;
        // Занято — ДО вопроса о коде и обновления настроек: второе нажатие не начнёт вторую оплату.
        _running = true;
        UpdateState();
        RenderSales();
        // 2026-10-22 (модалки, пакет 2): новая оплата — после подтверждения суммы, способа и остатка, как в кассе
        // (PayDebtDialog «Принять N сом»); «Повторить» записи с неизвестным исходом — без вопроса (сумма уже отправлялась).
        if (unknownRecord is null)
        {
            var amountText = Money(amount);
            var leftText = Money(Math.Max(0, Total - amount));
            var methodTitle = DebtPaymentFlow.MethodTitle(method);
            var question = T($"Принять от «{_clientName}» {amountText} в счёт долга ({methodTitle})?\nОстанется долг: {leftText}",
                $"«{_clientName}» кардардан карыз үчүн {amountText} кабыл алынсынбы ({methodTitle})?\nКалган карыз: {leftText}",
                $"Accept {amountText} from “{_clientName}” towards the debt ({methodTitle})?\nDebt left: {leftText}",
                $"«{_clientName}» müşterisinden borç için {amountText} alınsın mı ({methodTitle})?\nKalan borç: {leftText}",
                $"«{_clientName}» mijozdan qarz uchun {amountText} qabul qilinsinmi ({methodTitle})?\nQolgan qarz: {leftText}");
            if (!await PosDialogs.ConfirmYesNoModalAsync(this, question,
                    T("Принять оплату долга", "Карыз төлөмүн кабыл алуу", "Accept a debt payment", "Borç ödemesi al", "Qarz to'lovini qabul qilish"),
                    confirmText: T($"Принять {amountText}", $"{amountText} кабыл алуу", $"Accept {amountText}", $"{amountText} al", $"{amountText} qabul qilish"))
                    .ConfigureAwait(true))
            {
                _running = false;
                UpdateState();
                RenderSales();
                _amount.Focus();
                return;
            }
        }
        var api = App.AppHost?.Services.GetService<ClientDebtsApiService>();
        // Код сотрудника на приём оплаты (если включён в «Оплате долга» и вошёл не владелец/администратор).
        bool granted;
        string? gateError = null;
        try
        {
            if (api is null)
            {
                // 2026-10-18, ревью после живой проверки (п.5): без молчаливых возвратов — текст и строка в журнале.
                granted = false;
                gateError = T("Служба оплаты долга недоступна — оплата не проведена. Перезапустите программу.",
                    "Карыз төлөө кызматы жеткиликсиз — төлөм өткөн жок. Программаны кайра иштетиңиз.",
                    "The debt payment service is unavailable — the payment was not made. Restart the program.",
                    "Borç ödeme hizmeti kullanılamıyor — ödeme yapılmadı. Programı yeniden başlatın.",
                    "Qarz to'lash xizmati mavjud emas — to'lov o'tmadi. Dasturni qayta ishga tushiring.");
                PosLogger.Log("Владелец: оплата долга — нет ClientDebtsApiService, оплата не начата.", "WARNING");
            }
            else
                (granted, gateError) = await DebtPaymentFlow.ConfirmEmployeeCodeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Окно не должно остаться «занятым» навсегда (его тогда не закрыть).
            PosLogger.Log($"Владелец: оплата долга — проверка кода не удалась ({ex.Message}).", "WARNING");
            granted = false;
            gateError = T("Не удалось проверить код сотрудника — оплата не проведена: ", "Кызматкердин кодун текшерүү мүмкүн болгон жок — төлөм өткөн жок: ",
                "Couldn't check the employee code — the payment was not made: ", "Çalışan kodu kontrol edilemedi — ödeme yapılmadı: ",
                "Xodim kodini tekshirib bo'lmadi — to'lov o'tmadi: ") + ex.Message;
        }
        if (gateError is not null)
            ShowMessage(gateError);
        // Пока спрашивали код, пришли новые настройки «Оплата долга» и выбранный способ запрещён — не проводим молча.
        var methodGone = granted && unknownRecord is null && !DebtPaymentSettings.MethodsHere().Contains(method);
        if (!granted || methodGone || api is null)
        {
            _running = false;
            if (methodGone)
            {
                BuildMethods();
                ShowMessage(DebtPaymentFlow.MethodChangedMessage);
            }
            UpdateState();
            RenderSales();
            return;
        }

        DebtPayAttempt attempt;
        ShowPaying(true); // 2026-10-30: «Идёт оплата долга… N с» на всё время запроса
        try
        {
            if (unknownRecord is not null)
            {
                // 2026-10-18, пятый круг: «Повторить» — только повтор записи по её ключу, никогда не новая оплата.
                RememberOwnKey(unknownRecord.Key);
                _unknownDetail = null; // «Повторяем…» вместо прошлой причины
                attempt = await DebtPaymentRunner.RetryAsync(api, unknownRecord.Key).ConfigureAwait(true);
            }
            else
            {
                var key = Guid.NewGuid().ToString();
                RememberOwnKey(key);
                _activeNewKey = key;
                // Без смены: shiftId = null — поле shift не отправляется (решение владельца 18.10).
                attempt = await DebtPaymentRunner.PayNewAsync(api, key, _clientId, _clientName, row?.Id,
                    row?.Number is { Length: > 0 } number ? "№" + number : null, amount, method, null).ConfigureAwait(true);
            }
        }
        finally
        {
            _activeNewKey = null;
            _running = false;
            ShowPaying(false);
        }

        // 2026-10-18, седьмой круг (п.10): исход — DebtPayOutcome, текст — общий DebtPaymentRunner.OutcomeText (как в кассе).
        var bySale = unknownRecord is not null ? unknownRecord.SaleId is not null : row is not null;
        var text = DebtPaymentRunner.OutcomeText(attempt, bySale, owner: true);
        if (attempt.Outcome != DebtPayOutcome.Success)
        {
            // Оплату завершили в другом окне / долга уже нет — список с сервера. 2026-10-18, девятый круг (п.1, деньги): пока
            // перечитываем, «Принять» закрыта (_loaded = false); потом сумма — по СВЕЖЕМУ остатку, «введено вручную» снято.
            var reload = DebtPaymentRunner.NeedsReload(attempt);
            if (reload)
            {
                _loaded = false;
                UpdateState();
                await LoadAsync(keepInput: true).ConfigureAwait(true);
            }
            // Замок — из Runner: тот же ключ — уточнение к нему; новый (своя оплата без ответа, чужая запись) — экран по записи.
            if (DebtPaymentRunner.UnknownFor(_clientId) is { } still)
            {
                if (!_unknownOutcome || still.Key != _shownUnknownKey)
                    RestoreUnknown(still, text);
                else
                {
                    _unknownDetail = text;
                    ShowUnknown(still);
                }
            }
            else
            {
                var wasLocked = _unknownOutcome;
                if (_unknownOutcome)
                    ClearUnknown();
                if ((reload || wasLocked) && _loaded && !_done)
                    SetDefaultAmount(MaxAmount);
                if (!string.IsNullOrEmpty(text))
                    ShowError(text);
            }
            UpdateState();
            RenderSales();
            BuildMethods();
            return;
        }

        ClearUnknown();
        _done = true;
        Paid = true;
        // 2026-10-18, четвёртый круг: тексты успеха — один метод на кассу и владельца.
        _success.Text = text ?? "";
        _successBox.IsVisible = true;
        if (DebtPaymentRunner.OutcomeWarning(attempt) is { } warning)
            ShowWarning(warning);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _successBox.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
        UpdateState();
        RenderSales();
        BuildMethods();
        // Точный остаток после оплаты — с сервера.
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>2026-10-30: полоса «Идёт оплата долга… N с». Таймер раз в секунду обновляет только подпись (UI-поток свободен:
    /// запрос идёт в фоне, окно не «белеет»).</summary>
    private void ShowPaying(bool on)
    {
        _payingTimer?.Stop();
        _payingTimer = null;
        _payingBox.IsVisible = on;
        if (!on)
            return;
        _payingWatch.Restart();
        _payingText.Text = DebtPaymentFlow.PayingText(0);
        _payingTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _payingTimer.Tick += (_, _) => _payingText.Text = DebtPaymentFlow.PayingText((int)_payingWatch.Elapsed.TotalSeconds);
        _payingTimer.Start();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _payingBox.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>2026-10-18, девятый круг (п.7): предупреждение не стирается следующими сообщениями — только «Понятно».</summary>
    private void ShowWarning(string text)
    {
        _warning.Text = text;
        _warningBox.IsVisible = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _warningBox.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _errorBox.IsVisible = true;
        // Сообщение — в конце окна: прокрутить к нему, чтобы его было видно рядом с кнопкой.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _errorBox.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Оформление ─────────────────────────────────────────────────────────────────────

    private Border Block(string title, params Control[] content)
    {
        var stack = new StackPanel { Spacing = 8 };
        var t = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.SemiBold };
        Use(t, TextBlock.ForegroundProperty, "BrushText");
        stack.Children.Add(t);
        foreach (var c in content)
            stack.Children.Add(c);
        return new Border { Child = stack };
    }

    private TextBlock Small(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private static RadioButton Seg(string text, string group)
    {
        var r = new RadioButton { Content = text, GroupName = group, Focusable = true };
        r.Classes.Add("modal-seg");
        Avalonia.Automation.AutomationProperties.SetName(r, text);
        return r;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
