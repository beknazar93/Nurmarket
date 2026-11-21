using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Billing;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-19 (ответ Умара 19.11, раздел 2; ТЗ «Оплата подписки клиентами через ELQR», раздел 8): окно «Подписка» — оплата подписки NurCRM магазином через ELQR (Элдик Банк).
/// Шапка: тариф и «Подписка до …» (за 7 дней и меньше — предупреждение, просрочена — красным). Выбор срока 1/3/6/12 мес. и тарифа, «Оплатить через ELQR» → счёт (строки сервера как есть,
/// итог, QR, таймер до expires_at) → опрос статуса раз в 3 с: «Ожидаем оплату…» → «Обрабатывается…» → «Оплачено ✓». Истёк / отменён / ошибка — «Создать новый счёт» (старый не переиспользуется).
/// Счёт создаётся с Idempotency-Key на попытку: повторное нажатие не плодит счета, повтор после обрыва связи — тот же ключ. «Учебная оплата (тест)» — только если config.mode == "test" и пользователь
/// владелец/админ; в боевом режиме её нет вообще. Ключ банка — только на сервере; QR-ссылки и картинки в журнал не пишутся. Одно окно на программу (WindowSingleton).</summary>
public sealed class SubscriptionWindow : Window
{
    private enum Page { Loading, NotConnected, Choose, Pay }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly IBillingApi _api;
    private readonly bool _canManage;

    private readonly TextBlock _planText = Txt("NurFontSubtitle", FontWeight.SemiBold);
    private readonly TextBlock _untilText = Txt("NurFontBody", FontWeight.SemiBold);
    private readonly TextBlock _trialNote = Txt("NurFontCaption", brush: "BrushTextSoft"); // 2026-11-21: «Мини» — 14 дней пробного при регистрации
    private readonly TextBlock _info = Txt("NurFontBody", brush: "BrushTextSoft");
    private readonly TextBlock _error = Txt("NurFontBody");
    private readonly Border _errorBox;

    private readonly StackPanel _choosePanel = new() { Spacing = 12 };
    private readonly WrapPanel _monthChips = new();
    private readonly WrapPanel _planChips = new();
    private readonly TextBlock _planLabel = Txt("NurFontCaption", FontWeight.SemiBold, "BrushTextSoft");
    private readonly TextBlock _monthLabel = Txt("NurFontCaption", FontWeight.SemiBold, "BrushTextSoft");
    private readonly TextBlock _feeNote = Txt("NurFontCaption", brush: "BrushTextSoft");
    // 2026-11-21 (ответ Умара 21.11): «Месяц / Год» — год = 10 месяцев (счёт period "year" на 12 месяцев; цену и скидку даёт сервер).
    private readonly WrapPanel _periodChips = new();
    private readonly TextBlock _periodLabel = Txt("NurFontCaption", FontWeight.SemiBold, "BrushTextSoft");
    private readonly TextBlock _yearNote = Txt("NurFontCaption", brush: "BrushTextSoft");
    private readonly Dictionary<string, Button> _periodButtons = new();
    private readonly StackPanel _history = new() { Spacing = 4 };
    private readonly Control _historyBox;

    private readonly StackPanel _payPanel = new() { Spacing = 12, IsVisible = false };
    private readonly StackPanel _lines = new() { Spacing = 4 };
    private readonly TextBlock _amountText = Txt("NurFontTitle", FontWeight.Bold);
    private readonly Image _qrImage = new() { Width = 200, Height = 200, Stretch = Stretch.Uniform };
    private readonly Border _qrBox;
    private readonly TextBlock _timerText = Txt("NurFontBody", FontWeight.SemiBold);
    private readonly TextBlock _statusText = Txt("NurFontSubtitle", FontWeight.SemiBold);
    private readonly TextBlock _linkLost = Txt("NurFontCaption", brush: "BrushWarning");
    private readonly Button _openBank;
    private readonly Button _copyLink;
    private readonly TextBlock _copied = Txt("NurFontCaption", brush: "BrushSuccess");
    private readonly Border _testBox;

    private readonly Button _btnClose;
    private readonly Button _btnPay;
    private readonly Button _btnBack;
    private readonly Button _btnCancel;
    private readonly Button _btnNew;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer _clockTick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<int, Button> _monthButtons = new();
    private readonly Dictionary<string, Button> _planButtons = new(StringComparer.OrdinalIgnoreCase);

    private Page _page = Page.Loading;
    private BillingConfig? _config;
    private BillingInvoice? _invoice;
    private string? _qrLink;
    private Bitmap? _qrBitmap;
    private DateTimeOffset? _validUntil;
    private int _months = 1;
    private bool _year; // 2026-11-21: годовая оплата (period "year")
    private string? _planCode;
    private string? _attemptKey;
    private bool _creating;
    private bool _polling;
    private bool _linkWasLost;
    private bool _closing;
    private bool _paidHandled;
    private bool _cancelSent;
    private int _loadStarted;

    /// <summary>Часы (для проверок таймера без ожидания).</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Что сделать после «Оплачено ✓»: обновить профиль компании и срок в шапке кассы. По умолчанию — CompanyInfoService.RefreshAsync (ошибки не мешают окну).</summary>
    public Func<Task>? AfterPaid { get; set; }

    // ── Для проверок ─────────────────────────────────────────────────────────────
    public string StatusText => _statusText.Text ?? "";
    public string? ErrorText => _errorBox.IsVisible ? _error.Text : null;
    public string UntilText => _untilText.Text ?? "";
    public string TimerText => _timerText.Text ?? "";
    public string AmountText => _amountText.Text ?? "";
    public bool IsPayPage => _page == Page.Pay;
    public bool IsNotConnected => _page == Page.NotConnected;
    public bool IsChoosePage => _page == Page.Choose;
    public bool PayEnabled => _btnPay.IsVisible && _btnPay.IsEnabled;
    public bool HasQr => _qrBox.IsVisible && _qrBitmap is not null;
    public bool TestPayVisible => _testBox.IsVisible;
    public bool OpenBankVisible => _openBank.IsVisible;
    public bool CopyLinkVisible => _copyLink.IsVisible;
    public bool NewInvoiceVisible => _btnNew.IsVisible;
    public string? InvoiceId => _invoice?.InvoiceId;
    public IReadOnlyList<(string Title, string Amount)> LineTexts => _lines.Children.OfType<Grid>().Select(g => (((TextBlock)g.Children[0]).Text ?? "", ((TextBlock)g.Children[1]).Text ?? "")).ToList();
    public int HistoryRows => _history.Children.OfType<Grid>().Count();
    public bool ChooseMonthsVisible => _monthButtons.Count > 0 && _monthChips.IsVisible;
    public Task CreateForTestAsync() => CreateInvoiceAsync();
    public Task PollForTestAsync() => PollOnceAsync();
    public Task BackForTestAsync() => BackAsync(close: false);
    public Task CancelForTestAsync() => BackAsync(close: true);
    public Task TestPayForTestAsync(string result) => TestPayAsync(result);
    public Task TickForTestAsync() => OnClockTickAsync();
    public void SetMonths(int months) { _months = months; _year = false; RefreshChips(); }
    public void SetYear(bool year) { _year = year; RefreshChips(); }
    public bool IsYear => _year;
    public bool PeriodChoiceVisible => _periodChips.IsVisible;
    public string YearNoteText => _yearNote.IsVisible ? _yearNote.Text ?? "" : "";
    public string PlanChipText(string code) => _planButtons.TryGetValue(code, out var b) ? b.Content?.ToString() ?? "" : "";
    public void SetPlan(string? code) { _planCode = code; RefreshChips(); }
    public IReadOnlyList<int> MonthChoices => _monthButtons.Keys.OrderBy(x => x).ToList();

    private static TextBlock Txt(string size, FontWeight? weight = null, string brush = "BrushText")
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.FontSizeProperty, t.GetResourceObservable(size));
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));
        if (weight is { } w)
            t.FontWeight = w;
        return t;
    }

    private static void Paint(TextBlock t, string brush) => t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(brush));

    private static string M(decimal value)
    {
        var a = MoneyText.Amount(value);
        if (a.EndsWith(",00", StringComparison.Ordinal))
            a = a[..^3];
        return a + " " + MoneyText.Currency;
    }

    /// <summary>Окно «Подписка» показывается только при признаке billing_elqr и роли владельца/админа (и не в автономном режиме). Кассир видит только «Подписка до …» в Настройках → Аккаунт.</summary>
    public static bool CanOffer()
    {
        try
        {
            if (App.GetRequiredService<NurMarketKassa.Core.Contracts.IAutonomousAuthService>().IsCurrentSessionAutonomous)
                return false;
            return BillingAccess.ShouldOffer(BillingAccess.FeatureOn(), IsOwnerOrAdmin());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool IsOwnerOrAdmin()
    {
        try
        {
            return PermissionService.IsOwnerOrAdmin(App.GetRequiredService<IAuthApiService>().UserPayload);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Открыть окно или вывести открытое вперёд (одно на программу).</summary>
    public static SubscriptionWindow ShowOrActivate(Window? owner) =>
        WindowSingleton.ShowOnce(owner, () =>
        {
            try
            {
                if (TariffApi.CachedCatalog is null)
                    _ = App.GetRequiredService<TariffApi>().GetCatalogAsync().ContinueWith(_ => { }, TaskScheduler.Default); // для подписи «14 дней пробного» у «Мини»
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Подписка: каталог тарифов не подгружен: {ex.Message}", "DEBUG");
            }
            return new SubscriptionWindow(App.GetRequiredService<BillingApi>(), IsOwnerOrAdmin());
        });

    public SubscriptionWindow(IBillingApi api, bool canManage)
    {
        _api = api;
        _canManage = canManage;
        Title = T("Подписка", "Жазылуу", "Subscription", "Abonelik", "Obuna");
        SectionCards.AsModal(this, 672);

        _errorBox = SectionCards.ErrorBox(this, _error);
        _error.IsVisible = false;
        _info.Text = T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
        _planLabel.Text = T("Тариф", "Тариф", "Plan", "Tarife", "Tarif");
        _monthLabel.Text = T("Срок подписки", "Жазылуу мөөнөтү", "Subscription term", "Abonelik süresi", "Obuna muddati");
        _periodLabel.Text = T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov");
        _qrBox = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(12), Padding = new Thickness(8), Child = _qrImage, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = false };

        _openBank = SectionCards.Secondary(T("Открыть в приложении банка", "Банк тиркемесинде ачуу", "Open in the bank app", "Banka uygulamasında aç", "Bank ilovasida ochish"));
        _openBank.IsVisible = false;
        _openBank.Click += (_, _) =>
        {
            if (!PlatformLauncher.OpenUrl(_qrLink))
                ShowError(T("Не удалось открыть приложение банка — отсканируйте QR или скопируйте ссылку.", "Банк тиркемесин ачуу мүмкүн болгон жок — QR сканерлеңиз же шилтемени көчүрүңүз.",
                    "Could not open the bank app — scan the QR or copy the link.", "Banka uygulaması açılamadı — QR'ı tarayın veya bağlantıyı kopyalayın.", "Bank ilovasini ochib bo'lmadi — QR ni skanerlang yoki havolani nusxalang."));
        };
        _copyLink = SectionCards.Secondary(T("Скопировать ссылку", "Шилтемени көчүрүү", "Copy link", "Bağlantıyı kopyala", "Havolani nusxalash"));
        _copyLink.IsVisible = false;
        _copyLink.Click += async (_, _) => await CopyLinkAsync().ConfigureAwait(true);

        _testBox = BuildTestBox();
        _testBox.IsVisible = false;

        _btnClose = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        _btnClose.Click += (_, _) => Close();
        _btnPay = SectionCards.Primary(T("Оплатить через ELQR", "ELQR аркылуу төлөө", "Pay with ELQR", "ELQR ile öde", "ELQR orqali to'lash"));
        _btnPay.IsVisible = false;
        _btnPay.Click += async (_, _) => await CreateInvoiceAsync().ConfigureAwait(true);
        _btnBack = SectionCards.Secondary(T("Назад", "Артка", "Back", "Geri", "Orqaga"));
        _btnBack.IsVisible = false;
        _btnBack.Click += async (_, _) => await BackAsync(close: false).ConfigureAwait(true);
        _btnCancel = SectionCards.Cancel(T("Отменить", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        _btnCancel.IsVisible = false;
        _btnCancel.IsCancel = false;
        _btnCancel.Click += async (_, _) => await BackAsync(close: true).ConfigureAwait(true);
        _btnNew = SectionCards.Primary(T("Создать новый счёт", "Жаңы эсеп түзүү", "Create a new invoice", "Yeni fatura oluştur", "Yangi hisob yaratish"));
        _btnNew.IsVisible = false;
        _btnNew.Click += (_, _) => { ShowChoose(); };

        // История платежей: список счетов (дата, срок, сумма, статус).
        var historyTitle = Txt("NurFontBody", FontWeight.SemiBold);
        historyTitle.Text = T("История платежей", "Төлөмдөр тарыхы", "Payment history", "Ödeme geçmişi", "To'lovlar tarixi");
        _historyBox = SectionCards.Group(this, new StackPanel { Spacing = 6, Children = { historyTitle, _history } });
        _historyBox.IsVisible = false;

        _choosePanel.Children.Add(_planLabel);
        _choosePanel.Children.Add(_planChips);
        _choosePanel.Children.Add(_periodLabel);
        _choosePanel.Children.Add(_periodChips);
        _choosePanel.Children.Add(_monthLabel);
        _choosePanel.Children.Add(_monthChips);
        _choosePanel.Children.Add(_yearNote);
        _choosePanel.Children.Add(_feeNote);
        _choosePanel.Children.Add(_historyBox);
        _choosePanel.IsVisible = false;

        // Экран оплаты: слева QR, справа строки счёта, итог, таймер и статус.
        var right = new StackPanel { Spacing = 8 };
        right.Children.Add(_lines);
        right.Children.Add(_amountText);
        right.Children.Add(_timerText);
        right.Children.Add(_statusText);
        right.Children.Add(_linkLost);
        var qrColumn = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 16, 0), Children = { _qrBox } };
        var payGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        payGrid.Children.Add(qrColumn);
        Grid.SetColumn(right, 1);
        payGrid.Children.Add(right);
        var linkRow = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new Control[] { _openBank, _copyLink })
        {
            b.Margin = new Thickness(0, 0, 8, 8);
            linkRow.Children.Add(b);
        }
        _payPanel.Children.Add(payGrid);
        _payPanel.Children.Add(linkRow);
        _payPanel.Children.Add(_copied);
        _payPanel.Children.Add(_testBox);

        var header = new StackPanel { Spacing = 2, Children = { _planText, _untilText, _trialNote } };
        _trialNote.IsVisible = false;
        var headerBox = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Child = header };
        headerBox.Bind(Border.BackgroundProperty, headerBox.GetResourceObservable("BrushPanelSoft"));

        Content = SectionCards.Shell(Title, null,
            new StackPanel { Spacing = 12, Children = { headerBox, _info, _errorBox, _choosePanel, _payPanel } },
            SectionCards.Buttons(_btnClose, _btnCancel, _btnBack, _btnNew, _btnPay));

        _poll.Tick += async (_, _) => await PollOnceAsync().ConfigureAwait(true);
        _clockTick.Tick += async (_, _) => await OnClockTickAsync().ConfigureAwait(true);
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Closed += (_, _) => OnClosed();
        RenderHeader();
    }

    // ── Шапка ──────────────────────────────────────────────────────────────────

    private static string PlanTitle(string? code, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name!;
        return (code ?? "").ToLowerInvariant() switch
        {
            "start" => T("Старт", "Старт", "Start", "Start", "Start"),
            "standard" => T("Стандарт", "Стандарт", "Standard", "Standart", "Standart"),
            "custom" => T("Своё", "Өзүмдүкү", "Custom", "Özel", "O'zimniki"),
            "mini" => T("Мини", "Мини", "Mini", "Mini", "Mini"),
            "" => "",
            _ => code!,
        };
    }

    private void RenderHeader()
    {
        var company = CompanyInfoService.LastCompany;
        var planName = _config?.CurrentPlanName
            ?? _config?.Plans.FirstOrDefault(p => string.Equals(p.Code, _config.CurrentPlan, StringComparison.OrdinalIgnoreCase))?.Name;
        var title = PlanTitle(_config?.CurrentPlan, planName);
        if (string.IsNullOrWhiteSpace(title))
            title = company?.SubscriptionPlanName ?? "";
        _planText.Text = string.IsNullOrWhiteSpace(title) ? T("Подписка NurCRM", "NurCRM жазылуусу", "NurCRM subscription", "NurCRM aboneliği", "NurCRM obunasi")
            : T("Тариф: ", "Тариф: ", "Plan: ", "Tarife: ", "Tarif: ") + title;

        // 2026-11-21 (ответ Умара 21.11): «Мини» — 14 дней пробного при регистрации (public/tariffs trial_days) — рядом с тарифом.
        _trialNote.IsVisible = false;
        if (string.Equals(_config?.CurrentPlan, "mini", StringComparison.OrdinalIgnoreCase) && TariffApi.CachedCatalog?.Plan("mini")?.TrialDays is { } trialDays)
        {
            _trialNote.Text = T($"Пробный период «Мини» при регистрации — {trialDays} дн.", $"«Мини» катталууда сыноо мөөнөтү — {trialDays} күн", $"“Mini” trial at sign-up — {trialDays} days", $"«Mini» kayıtta deneme süresi — {trialDays} gün", $"«Mini» ro'yxatdan o'tishda sinov muddati — {trialDays} kun");
            _trialNote.IsVisible = true;
        }

        var until = _validUntil ?? _config?.ValidUntil;
        if (until is null && company?.EndDate is { } raw && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            until = parsed;
        if (until is null)
        {
            _untilText.Text = T("Срок подписки не указан", "Жазылуу мөөнөтү көрсөтүлгөн эмес", "Subscription end date not specified", "Abonelik bitiş tarihi belirtilmedi", "Obuna tugash sanasi ko'rsatilmagan");
            Paint(_untilText, "BrushTextSoft");
            return;
        }

        var now = Clock();
        var days = BillingAccess.DaysLeft(until, now) ?? 0;
        var state = BillingAccess.ExpiryState(until, now);
        var day = BillingAccess.Day(until.Value);
        var text = T($"Подписка до {day}", $"Жазылуу {day} чейин", $"Subscription until {day}", $"Abonelik bitişi: {day}", $"Obuna {day} gacha");
        if (state == BillingExpiry.Expired)
        {
            text += " — " + T("просрочена", "мөөнөтү бүттү", "expired", "süresi doldu", "muddati tugagan");
            Paint(_untilText, "BrushDanger");
        }
        else if (state == BillingExpiry.Soon)
        {
            text += " (" + (days == 0 ? T("сегодня", "бүгүн", "today", "bugün", "bugun") : T($"осталось {days} дн.", $"{days} күн калды", $"{days} days left", $"{days} gün kaldı", $"{days} kun qoldi")) + ")";
            Paint(_untilText, "BrushWarning");
        }
        else
            Paint(_untilText, "BrushText");
        _untilText.Text = text;
    }

    // ── Показ страниц ──────────────────────────────────────────────────────────────

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
    }

    private void SetPage(Page page)
    {
        _page = page;
        _choosePanel.IsVisible = page == Page.Choose;
        _payPanel.IsVisible = page == Page.Pay;
        _info.IsVisible = page is Page.Loading or Page.NotConnected;
        _btnPay.IsVisible = page == Page.Choose && _canManage;
        _btnPay.IsEnabled = !_creating;
        _btnClose.IsVisible = page != Page.Pay || (_invoice?.Status == BillingStatus.Paid);
        _btnClose.IsCancel = _btnClose.IsVisible;
        var payOpen = page == Page.Pay && _invoice is { IsFinal: false };
        _btnCancel.IsVisible = payOpen;
        _btnCancel.IsCancel = payOpen;
        var final = page == Page.Pay && _invoice is { IsFinal: true };
        _btnBack.IsVisible = payOpen || (final && _invoice!.Status != BillingStatus.Paid);
        _btnNew.IsVisible = final && _invoice!.Status != BillingStatus.Paid && _canManage;
    }

    /// <summary>Загрузка настроек оплаты. Для тестов — вызывается и вручную; повторный вызов ничего не делает.</summary>
    public async Task LoadAsync()
    {
        if (Interlocked.Exchange(ref _loadStarted, 1) != 0)
            return;
        await ReloadConfigAsync().ConfigureAwait(true);
    }

    private async Task ReloadConfigAsync()
    {
        ShowError(null);
        try
        {
            _config = await _api.GetConfigAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailure("Подписка: настройки оплаты не получены", ex);
            if (BillingApi.Classify(ex).Kind == BillingErrorKind.Unavailable)
                ShowNotConnected();
            else
            {
                _info.Text = BillingApi.Describe(ex);
                SetPage(Page.Loading);
                _btnClose.IsVisible = true;
            }
            RenderHeader();
            return;
        }

        _validUntil = _config.ValidUntil ?? _validUntil;
        RenderHeader();
        if (!BillingAccess.CanPay(true, _config))
        {
            ShowNotConnected();
            return;
        }

        if (_config.CurrentPlan is { } cur && (_planCode is null || _config.Plans.All(p => !string.Equals(p.Code, _planCode, StringComparison.OrdinalIgnoreCase))))
            _planCode = _config.Plans.Any(p => string.Equals(p.Code, cur, StringComparison.OrdinalIgnoreCase)) ? cur : _config.Plans.FirstOrDefault()?.Code;
        else if (_planCode is null)
            _planCode = _config.Plans.FirstOrDefault()?.Code;
        if (!_config.Months.Contains(_months) || (_config.CanPayYear && _months == 12))
            _months = _config.Months.Where(m => !_config.CanPayYear || m != 12).DefaultIfEmpty(_config.Months.First()).First();
        if (!_config.CanPayYear)
            _year = false;
        BuildChips();
        RefreshChips();
        ShowChoose();
    }

    private void ShowNotConnected()
    {
        _info.Text = BillingApi.NotConnectedText();
        SetPage(Page.NotConnected);
    }

    private void ShowChoose()
    {
        StopTimers();
        _invoice = null;
        _qrLink = null;
        SetQr(null);
        ShowError(null);
        _paidHandled = false;
        _cancelSent = false;
        _copied.Text = "";
        if (!_canManage)
        {
            _info.Text = T("Оплату подписки выполняет владелец или администратор компании. Здесь видно только, до какого числа подписка действует.",
                "Жазылуунун акысын компаниянын ээси же администратору төлөйт. Бул жерде жазылуу качанга чейин иштээри гана көрүнөт.",
                "The subscription is paid by the company owner or administrator. Here you can only see the date it is valid until.",
                "Aboneliği şirket sahibi veya yönetici öder. Burada yalnızca aboneliğin geçerlilik tarihi görünür.",
                "Obunani kompaniya egasi yoki administrator to'laydi. Bu yerda faqat obuna qaysi sanagacha amal qilishi ko'rinadi.");
            SetPage(Page.NotConnected);
            _info.IsVisible = true;
            return;
        }

        SetPage(Page.Choose);
        _ = LoadHistoryAsync();
    }

    private void BuildChips()
    {
        _monthChips.Children.Clear();
        _monthButtons.Clear();
        // 2026-11-21: сервер умеет год (periods содержит "year") — «12 мес.» заменяет переключатель «Месяц / Год»; год = 10 месяцев.
        var canYear = _config!.CanPayYear;
        _periodChips.Children.Clear();
        _periodButtons.Clear();
        _periodChips.IsVisible = canYear;
        _periodLabel.IsVisible = canYear;
        if (canYear)
            foreach (var (code, label) in new[]
                     {
                         ("month", T("По месяцам", "Айлар боюнча", "By months", "Aylık", "Oylar bo'yicha")),
                         ("year", T("Год — платите за 10 мес.", "Жыл — 10 ай үчүн төлөйсүз", "Year — pay for 10 months", "Yıl — 10 ay için ödersiniz", "Yil — 10 oy uchun to'laysiz")),
                     })
            {
                var c = code;
                var pb = UiKit.Chip(this, label, (c == "year") == _year);
                pb.Margin = new Thickness(0, 0, 8, 8);
                SquareChip(pb);
                pb.Click += (_, _) => { _year = c == "year"; RefreshChips(); };
                _periodButtons[c] = pb;
                _periodChips.Children.Add(pb);
            }

        foreach (var n in _config.Months.Where(m => !canYear || m != 12))
        {
            var count = n;
            var b = UiKit.Chip(this, T($"{n} мес.", $"{n} ай", $"{n} mo.", $"{n} ay", $"{n} oy"), count == _months);
            b.Margin = new Thickness(0, 0, 8, 8);
            SquareChip(b);
            b.Click += (_, _) => { _months = count; RefreshChips(); };
            _monthButtons[n] = b;
            _monthChips.Children.Add(b);
        }

        _planChips.Children.Clear();
        _planButtons.Clear();
        var showPlans = _config.Plans.Count > 1;
        _planChips.IsVisible = showPlans;
        _planLabel.IsVisible = showPlans;
        if (showPlans)
            foreach (var plan in _config.Plans)
            {
                var code = plan.Code;
                var label = PlanChipLabel(plan);
                var b = UiKit.Chip(this, label, string.Equals(code, _planCode, StringComparison.OrdinalIgnoreCase));
                b.Margin = new Thickness(0, 0, 8, 8);
                SquareChip(b);
                b.Click += (_, _) => { _planCode = code; RefreshChips(); };
                _planButtons[code] = b;
                _planChips.Children.Add(b);
            }

        _feeNote.Text = T($"К оплате — цена тарифа за выбранный срок плюс комиссия банка {M(_config.BankFee)} (её платит клиент, один раз на счёт). Точный итог сервер покажет на следующем шаге; продление — по цене тарифа.",
            $"Төлөм — тандалган мөөнөткө тарифтин баасы жана банктын комиссиясы {M(_config.BankFee)} (аны кардар төлөйт, бир эсепке бир жолу). Так жыйынтыкты сервер кийинки кадамда көрсөтөт; узартуу — тарифтин баасы боюнча.",
            $"To pay: the plan price for the chosen term plus the bank fee of {M(_config.BankFee)} (paid by the customer, once per invoice). The server shows the exact total on the next step; renewal is at the plan price.",
            $"Ödenecek tutar: seçilen süre için tarife fiyatı artı {M(_config.BankFee)} banka komisyonu (müşteri öder, fatura başına bir kez). Kesin toplamı sunucu sonraki adımda gösterir; yenileme tarife fiyatı üzerindendir.",
            $"To'lov: tanlangan muddat uchun tarif narxi va bank komissiyasi {M(_config.BankFee)} (uni mijoz to'laydi, har bir hisobga bir marta). Aniq jami summani server keyingi bosqichda ko'rsatadi; uzaytirish — tarif narxi bo'yicha.");
    }

    /// <summary>Подпись тарифа: название и цена С СЕРВЕРА (price_month или, при годовой оплате, price_year; год = 10 месяцев).</summary>
    private string PlanChipLabel(BillingPlanOption plan)
    {
        var title = PlanTitle(plan.Code, plan.Name);
        if (_year && plan.PriceYear is > 0)
            return title + " · " + M(plan.PriceYear.Value) + " " + T("/ год", "/ жыл", "/ yr", "/ yıl", "/ yil");
        return title + (plan.PriceMonth > 0 ? " · " + M(plan.PriceMonth) + " " + T("/ мес.", "/ ай", "/ mo.", "/ ay", "/ oy") : "");
    }

    /// <summary>2026-11-21 (обход кассы, № 26): тариф, срок и способ оплаты — не «пилюли» (радиус 20), а тот же радиус кнопок темы, что у остальных кнопок окна и кассы.</summary>
    private static void SquareChip(Button b)
    {
        if (Application.Current is { } app && app.TryFindResource("NurRadiusControl", app.ActualThemeVariant, out var r) && r is CornerRadius radius)
            b.CornerRadius = radius;
    }

    private void RefreshChips()
    {
        foreach (var (n, b) in _monthButtons)
            UiKit.SetActive(b, !_year && n == _months);
        foreach (var (code, b) in _periodButtons)
            UiKit.SetActive(b, (code == "year") == _year);
        _monthLabel.IsVisible = !_year;
        _monthChips.IsVisible = !_year;
        _yearNote.IsVisible = _year;
        if (_year)
        {
            var paid = _config?.YearPaidMonths ?? 10;
            _yearNote.Text = T($"Год = {paid} месяцев: подписка на 12 месяцев по цене {paid}. Цену и скидку считает сервер.", $"Жыл = {paid} ай: 12 айлык жазылуу {paid} айдын баасына. Баасын жана арзандатууну сервер эсептейт.",
                $"Year = {paid} months: a 12-month subscription at the price of {paid}. The server calculates the price and discount.", $"Yıl = {paid} ay: 12 aylık abonelik {paid} ayın fiyatına. Fiyatı ve indirimi sunucu hesaplar.", $"Yil = {paid} oy: 12 oylik obuna {paid} oy narxida. Narx va chegirmani server hisoblaydi.");
        }
        if (_config is { } cfg)
            foreach (var plan in cfg.Plans)
                if (_planButtons.TryGetValue(plan.Code, out var pb))
                    pb.Content = PlanChipLabel(plan);
        foreach (var (code, b) in _planButtons)
            UiKit.SetActive(b, string.Equals(code, _planCode, StringComparison.OrdinalIgnoreCase));
    }

    // ── История ───────────────────────────────────────────────────────────────────

    private static string StatusLabel(BillingStatus s) => s switch
    {
        BillingStatus.Pending => T("Ждёт оплаты", "Төлөмдү күтүүдө", "Awaiting payment", "Ödeme bekliyor", "To'lov kutilmoqda"),
        BillingStatus.Processing => T("Обрабатывается", "Иштелүүдө", "Processing", "İşleniyor", "Qayta ishlanmoqda"),
        BillingStatus.Paid => T("Оплачено", "Төлөндү", "Paid", "Ödendi", "To'landi"),
        BillingStatus.Expired => T("Истёк", "Мөөнөтү өттү", "Expired", "Süresi doldu", "Muddati tugadi"),
        BillingStatus.Cancelled => T("Отменён", "Жокко чыгарылды", "Cancelled", "İptal edildi", "Bekor qilindi"),
        BillingStatus.Error => T("Ошибка", "Ката", "Error", "Hata", "Xato"),
        _ => "—",
    };

    private async Task LoadHistoryAsync()
    {
        try
        {
            var list = await _api.ListInvoicesAsync().ConfigureAwait(true);
            _history.Children.Clear();
            var rows = list.Where(i => i.Months > 0 || i.Amount > 0).OrderByDescending(i => i.CreatedAt ?? DateTimeOffset.MinValue).Take(8).ToList();
            if (rows.Count == 0)
            {
                var none = Txt("NurFontCaption", brush: "BrushTextSoft");
                none.Text = T("Платежей пока не было.", "Төлөмдөр азырынча болгон жок.", "No payments yet.", "Henüz ödeme yok.", "Hozircha to'lovlar bo'lmagan.");
                _history.Children.Add(none);
            }

            foreach (var inv in rows)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
                var left = Txt("NurFontCaption");
                left.Text = (inv.CreatedAt is { } d ? d.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "—") + " · " + T($"{inv.Months} мес.", $"{inv.Months} ай", $"{inv.Months} mo.", $"{inv.Months} ay", $"{inv.Months} oy");
                var amount = Txt("NurFontCaption", FontWeight.SemiBold);
                amount.Text = M(inv.Amount);
                amount.Margin = new Thickness(12, 0, 12, 0);
                amount.TextWrapping = TextWrapping.NoWrap;
                var status = Txt("NurFontCaption", FontWeight.SemiBold, inv.Status == BillingStatus.Paid ? "BrushSuccess" : inv.Status is BillingStatus.Error or BillingStatus.Expired ? "BrushDanger" : "BrushTextSoft");
                status.Text = StatusLabel(inv.Status);
                status.TextWrapping = TextWrapping.NoWrap;
                Grid.SetColumn(amount, 1);
                Grid.SetColumn(status, 2);
                grid.Children.Add(left);
                grid.Children.Add(amount);
                grid.Children.Add(status);
                _history.Children.Add(grid);
            }
            _historyBox.IsVisible = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailure("Подписка: история платежей не получена", ex);
            _historyBox.IsVisible = false;
        }
    }

    // ── Счёт ──────────────────────────────────────────────────────────────────────

    private async Task CreateInvoiceAsync()
    {
        if (_creating || _page != Page.Choose || !_canManage)
            return;
        _creating = true;
        _btnPay.IsEnabled = false;
        ShowError(null);
        // Один ключ на попытку: повторное нажатие или повтор после обрыва связи — тот же ключ, счёт не задваивается. Ключ сбрасывается после определённого ответа сервера.
        _attemptKey ??= Guid.NewGuid().ToString().Replace("-", ""); // 2026-11-19: без дефисов (не числовой формат для храповика)
        try
        {
            var invoice = _year
                ? await _api.CreateInvoiceAsync(12, _planCode, "year", _attemptKey).ConfigureAwait(true) // 2026-11-21: год = 10 месяцев, счёт на 12 мес. со строкой скидки от сервера
                : await _api.CreateInvoiceAsync(_months, _planCode, _attemptKey).ConfigureAwait(true);
            _attemptKey = null;
            PosLogger.Log($"Подписка: счёт {invoice.InvoiceId} создан ({invoice.Months} мес.).", "INFO");
            ShowInvoice(invoice);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var kind = BillingApi.Classify(ex);
            if (ex is ApiException { StatusCode: < 500 } || kind.Kind == BillingErrorKind.BankUnavailable)
                _attemptKey = null; // определённый ответ сервера: следующая попытка — новый счёт
            LogFailure("Подписка: счёт не создан" + (kind.InvoiceId is { } id ? $" (счёт {id} со статусом error)" : ""), ex);
            if (kind.Kind == BillingErrorKind.Unavailable)
            {
                ShowNotConnected();
                return;
            }

            var text = BillingApi.Describe(ex);
            if (kind.Kind == BillingErrorKind.Network || (ex is ApiException { StatusCode: >= 500 } && kind.Kind != BillingErrorKind.BankUnavailable))
                text += "\n" + T("Можно нажать «Оплатить через ELQR» ещё раз — счёт не задвоится.", "«ELQR аркылуу төлөө» баскычын дагы бир жолу басса болот — эсеп эки эсе болбойт.",
                    "You can press “Pay with ELQR” again — the invoice will not be duplicated.", "«ELQR ile öde» düğmesine tekrar basabilirsiniz — fatura tekrarlanmaz.", "«ELQR orqali to'lash» tugmasini yana bosish mumkin — hisob takrorlanmaydi.");
            ShowError(text);
        }
        finally
        {
            _creating = false;
            if (_page == Page.Choose)
                _btnPay.IsEnabled = true;
        }
    }

    private void ShowInvoice(BillingInvoice invoice)
    {
        _invoice = invoice;
        _qrLink = string.IsNullOrWhiteSpace(invoice.QrLink) ? null : invoice.QrLink;
        _paidHandled = false;
        _cancelSent = false;
        _linkWasLost = false;
        _linkLost.Text = "";
        _copied.Text = "";
        ShowError(null);
        SetQr(BillingParser.DecodeQr(invoice.QrImageBase64));

        _lines.Children.Clear();
        foreach (var line in invoice.Lines)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var title = Txt("NurFontBody");
            title.Text = line.Title;
            var amount = Txt("NurFontBody", FontWeight.SemiBold);
            amount.Text = M(line.Amount);
            amount.Margin = new Thickness(12, 0, 0, 0);
            amount.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(amount, 1);
            grid.Children.Add(title);
            grid.Children.Add(amount);
            _lines.Children.Add(grid);
        }
        _amountText.Text = T("К оплате: ", "Төлөөгө: ", "To pay: ", "Ödenecek: ", "To'lov: ") + M(invoice.Amount);

        var canOpen = _qrLink is not null && PlatformLauncher.IsLaunchableUrl(_qrLink) && (OperatingSystem.IsAndroid() || OperatingSystem.IsWindows());
        _openBank.IsVisible = canOpen;
        _copyLink.IsVisible = _qrLink is not null;
        _testBox.IsVisible = BillingAccess.CanTestPay(_config, _canManage) && !invoice.IsFinal;

        SetPage(Page.Pay);
        ApplyStatus(invoice);
        if (!invoice.IsFinal)
        {
            _poll.Start();
            _clockTick.Start();
            UpdateTimer();
        }
    }

    private void SetQr(byte[]? png)
    {
        var old = _qrBitmap;
        _qrBitmap = null;
        if (png is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(png);
                _qrBitmap = new Bitmap(ms);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
            {
                PosLogger.Log("Подписка: картинка QR не открылась.", "WARNING");
            }
        }
        _qrImage.Source = _qrBitmap;
        _qrBox.IsVisible = _qrBitmap is not null;
        old?.Dispose();
    }

    private void ApplyStatus(BillingInvoice fresh)
    {
        // В _invoice держим последний ответ (статус, сроки); QR и ссылка банка остаются от создания счёта.
        _invoice = fresh;
        ShowError(null);
        switch (fresh.Status)
        {
            case BillingStatus.Pending:
            case BillingStatus.Unknown:
                _statusText.Text = T("Ожидаем оплату…", "Төлөмдү күтүп жатабыз…", "Waiting for payment…", "Ödeme bekleniyor…", "To'lov kutilmoqda…");
                Paint(_statusText, "BrushText");
                break;
            case BillingStatus.Processing:
                _statusText.Text = T("Обрабатывается…", "Иштелүүдө…", "Processing…", "İşleniyor…", "Qayta ishlanmoqda…");
                Paint(_statusText, "BrushText");
                break;
            case BillingStatus.Paid:
                OnPaid(fresh);
                break;
            case BillingStatus.Expired:
                FinishUnpaid(T("Время оплаты вышло", "Төлөө убактысы бүттү", "The payment time has run out", "Ödeme süresi doldu", "To'lov vaqti tugadi"));
                break;
            case BillingStatus.Cancelled:
                FinishUnpaid(T("Счёт отменён", "Эсеп жокко чыгарылды", "The invoice was cancelled", "Fatura iptal edildi", "Hisob bekor qilindi"));
                break;
            case BillingStatus.Error:
                FinishUnpaid(T("Оплата не прошла", "Төлөм өткөн жок", "The payment failed", "Ödeme başarısız", "To'lov o'tmadi") + (string.IsNullOrWhiteSpace(fresh.Error) ? "" : ": " + fresh.Error));
                break;
        }
        _testBox.IsVisible = BillingAccess.CanTestPay(_config, _canManage) && !fresh.IsFinal;
        SetPage(Page.Pay);
    }

    private void FinishUnpaid(string message)
    {
        StopTimers();
        _statusText.Text = message + ". " + T("Создайте новый счёт — старый использовать нельзя.", "Жаңы эсеп түзүңүз — эскисин колдонууга болбойт.", "Create a new invoice — the old one cannot be reused.",
            "Yeni bir fatura oluşturun — eski fatura yeniden kullanılamaz.", "Yangi hisob yarating — eskisidan foydalanib bo'lmaydi.");
        Paint(_statusText, "BrushDanger");
        _timerText.Text = "";
        _linkLost.Text = "";
        SetQr(null);
        _openBank.IsVisible = false;
        _copyLink.IsVisible = false;
    }

    private void OnPaid(BillingInvoice paid)
    {
        StopTimers();
        var until = paid.ValidUntil ?? _config?.ValidUntil;
        _statusText.Text = T("Оплачено ✓", "Төлөндү ✓", "Paid ✓", "Ödendi ✓", "To'landi ✓")
            + (until is { } u ? " " + T("Подписка продлена до ", "Жазылуу узартылды: ", "Subscription extended until ", "Abonelik şu tarihe uzatıldı: ", "Obuna quyidagi sanagacha uzaytirildi: ") + BillingAccess.Day(u) : "");
        Paint(_statusText, "BrushSuccess");
        _timerText.Text = "";
        _linkLost.Text = "";
        SetQr(null);
        _openBank.IsVisible = false;
        _copyLink.IsVisible = false;
        if (until is { } v)
        {
            _validUntil = v;
            RenderHeader();
        }
        if (_paidHandled)
            return;
        _paidHandled = true;
        PosLogger.Log($"Подписка: счёт {paid.InvoiceId} оплачен.", "INFO");
        _ = RefreshCompanyAsync();
    }

    private async Task RefreshCompanyAsync()
    {
        try
        {
            if (AfterPaid is { } custom)
                await custom().ConfigureAwait(true);
            else
                await CompanyInfoService.RefreshAsync(App.AuthApi).ConfigureAwait(true);
            RenderHeader();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailure("Подписка: профиль компании после оплаты не обновлён", ex);
        }
    }

    // ── Опрос и таймер ─────────────────────────────────────────────────────────────

    private void StopTimers()
    {
        _poll.Stop();
        _clockTick.Stop();
    }

    /// <summary>Один опрос статуса: раз в 3 с, пока счёт не закрыт. Потеря связи — сообщение и продолжение опроса по номеру счёта.</summary>
    public async Task PollOnceAsync()
    {
        if (_invoice is not { IsFinal: false } current || _polling || _closing)
            return;
        _polling = true;
        try
        {
            var fresh = await _api.GetInvoiceAsync(current.InvoiceId).ConfigureAwait(true);
            if (_closing || _invoice is null || _invoice.InvoiceId != current.InvoiceId)
                return;
            if (_linkWasLost)
            {
                _linkWasLost = false;
                _linkLost.Text = "";
            }
            ApplyStatus(fresh);
            if (!fresh.IsFinal)
                UpdateTimer();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_closing)
                return;
            _linkWasLost = true;
            _linkLost.Text = T("Нет связи с сервером. Если вы уже оплатили — подождите: проверка продолжится сама.", "Сервер менен байланыш жок. Эгер төлөп койсоңуз — күтүңүз: текшерүү өзү улантылат.",
                "No connection to the server. If you have already paid, wait — the check will continue by itself.", "Sunucuyla bağlantı yok. Zaten ödediyseniz bekleyin — kontrol kendiliğinden devam eder.",
                "Server bilan aloqa yo'q. Agar to'lagan bo'lsangiz — kutib turing: tekshiruv o'zi davom etadi.");
            if (ex is ApiException { StatusCode: 404 })
            {
                StopTimers();
                ShowError(BillingApi.Describe(ex));
            }
            LogFailure("Подписка: статус счёта не получен", ex);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task OnClockTickAsync()
    {
        if (_invoice is not { IsFinal: false } inv)
            return;
        UpdateTimer();
        if (inv.ExpiresAt is not { } exp)
            return;
        var left = exp - Clock();
        if (left <= TimeSpan.Zero)
        {
            await PollOnceAsync().ConfigureAwait(true); // срок вышел — сервер ответит expired
            // сервер ещё не закрыл счёт, а срок давно вышел и оплаты нет — считаем счёт просроченным здесь
            if (_invoice is { Status: BillingStatus.Pending } pending && left < TimeSpan.FromSeconds(-10))
                ApplyStatus(pending.AsExpired());
        }
    }

    private void UpdateTimer()
    {
        if (_invoice is not { IsFinal: false } inv || inv.ExpiresAt is not { } exp)
        {
            _timerText.Text = "";
            return;
        }

        var left = exp - Clock();
        if (left < TimeSpan.Zero)
            left = TimeSpan.Zero;
        _timerText.Text = T("Осталось ", "Калды: ", "Time left: ", "Kalan süre: ", "Qolgan vaqt: ") + ((int)left.TotalMinutes).ToString().PadLeft(2, '0') + ":" + left.Seconds.ToString().PadLeft(2, '0'); // 2026-11-19: мм:сс без числового формата
        Paint(_timerText, left < TimeSpan.FromMinutes(2) ? "BrushWarning" : "BrushText");
    }

    // ── Кнопки ─────────────────────────────────────────────────────────────────────

    /// <summary>Отмена неоплаченного счёта на сервере. false — счёт уже оплачен/обрабатывается (сервер не дал отменить): окно остаётся, показывает результат.</summary>
    private async Task<bool> CancelOnServerAsync()
    {
        if (_invoice is not { Status: BillingStatus.Pending } inv || _cancelSent)
            return true;
        try
        {
            var result = await _api.CancelInvoiceAsync(inv.InvoiceId).ConfigureAwait(true);
            _cancelSent = true;
            if (result is { Status: BillingStatus.Paid or BillingStatus.Processing })
            {
                ApplyStatus(result);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            LogFailure("Подписка: счёт не отменён", ex);
            if (BillingApi.Classify(ex).Kind == BillingErrorKind.InvoiceFinal)
            {
                // Счёт уже закрыт на сервере (оплачен, истёк): узнаём итог и показываем его.
                await PollOnceAsync().ConfigureAwait(true);
                return _invoice is not { IsFinal: false } && _invoice?.Status != BillingStatus.Paid;
            }
            return true; // не вышло отменить — не держим владельца: счёт сам истечёт через ~20 минут
        }
    }

    private async Task BackAsync(bool close)
    {
        if (_page != Page.Pay)
        {
            if (close)
                Close();
            return;
        }
        if (!await CancelOnServerAsync().ConfigureAwait(true))
            return;
        if (close)
        {
            Close();
            return;
        }
        await ReloadConfigAsync().ConfigureAwait(true); // срок «до …» мог измениться
    }

    private async Task CopyLinkAsync()
    {
        if (_qrLink is null)
            return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_qrLink).ConfigureAwait(true);
                _copied.Text = T("Ссылка скопирована.", "Шилтеме көчүрүлдү.", "Link copied.", "Bağlantı kopyalandı.", "Havola nusxalandi.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Подписка: ссылка не скопирована ({ex.GetType().Name}).", "WARNING");
        }
    }

    private Border BuildTestBox()
    {
        var title = Txt("NurFontBody", FontWeight.SemiBold);
        title.Text = T("Учебная оплата (тест)", "Окуу төлөмү (тест)", "Practice payment (test)", "Deneme ödemesi (test)", "O'quv to'lovi (test)");
        var note = Txt("NurFontCaption", brush: "BrushTextSoft");
        note.Text = T("Учебный банк: деньги не списываются, но подписка продлевается по-настоящему. Выберите, чем закончится платёж.",
            "Окуу банкы: акча алынбайт, бирок жазылуу чындап узартылат. Төлөм эмне менен бүтөрүн тандаңыз.",
            "Practice bank: no money is charged, but the subscription is really extended. Choose how the payment ends.",
            "Deneme bankası: para çekilmez, ancak abonelik gerçekten uzatılır. Ödemenin nasıl biteceğini seçin.",
            "O'quv banki: pul yechilmaydi, lekin obuna haqiqatan uzaytiriladi. To'lov qanday tugashini tanlang.");
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (code, label) in new[]
                 {
                     ("success", T("Успешно", "Ийгиликтүү", "Success", "Başarılı", "Muvaffaqiyatli")),
                     ("processing", T("В обработке", "Иштелүүдө", "Processing", "İşleniyor", "Jarayonda")),
                     ("error", T("Ошибка", "Ката", "Error", "Hata", "Xato")),
                     ("cancelled", T("Отмена", "Жокко чыгаруу", "Cancelled", "İptal", "Bekor")),
                 })
        {
            var result = code;
            var b = UiKit.Chip(this, label, false);
            b.Margin = new Thickness(0, 0, 8, 8);
            SquareChip(b);
            b.Click += async (_, _) => await TestPayAsync(result).ConfigureAwait(true);
            row.Children.Add(b);
        }
        var box = SectionCards.Group(this, new StackPanel { Spacing = 6, Children = { title, note, row } });
        return box;
    }

    private async Task TestPayAsync(string result)
    {
        // Только учебный банк и только владелец/админ: в боевом режиме метод ничего не отправляет.
        if (_invoice is not { IsFinal: false } inv || !BillingAccess.CanTestPay(_config, _canManage))
            return;
        try
        {
            var updated = await _api.TestPayAsync(inv.InvoiceId, result).ConfigureAwait(true);
            if (updated is not null && updated.InvoiceId == inv.InvoiceId)
                ApplyStatus(updated);
            await PollOnceAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailure("Подписка: учебная оплата не выполнена", ex);
            if (BillingApi.Classify(ex).Kind == BillingErrorKind.InvoiceFinal)
                await PollOnceAsync().ConfigureAwait(true);
            ShowError(BillingApi.Describe(ex));
        }
    }

    // ── Закрытие и журнал ───────────────────────────────────────────────────────────

    private void OnClosed()
    {
        _closing = true;
        StopTimers();
        // Закрыли окно с неоплаченным счётом — просим сервер отменить (он сам истечёт через ~20 минут, это только аккуратность).
        if (_invoice is { Status: BillingStatus.Pending } inv && !_cancelSent)
        {
            _cancelSent = true;
            var api = _api;
            _ = Task.Run(async () =>
            {
                try { await api.CancelInvoiceAsync(inv.InvoiceId).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* не вышло — счёт истечёт сам */ }
            });
        }
        SetQr(null);
    }

    /// <summary>В журнал — только вид ошибки и код/статус ответа: тело ответа (в нём может быть QR) не пишем.</summary>
    private static void LogFailure(string what, Exception ex)
    {
        var detail = ex is ApiException api ? $"HTTP {api.StatusCode}, {ServerErrorCodes.Code(api) ?? "без кода"}" : ex.GetType().Name;
        PosLogger.Log($"{what} ({detail}).", "WARNING");
    }
}
