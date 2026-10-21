using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-19, решение владельца: «Выплатить» зарплату сотруднику из раздела «Зарплата» — расход из кассы компании
/// без смены (см. <see cref="SalaryPayoutService"/>). Сумма по умолчанию — остаток «К выплате», только если «Выплачено»
/// проверено полностью (<see cref="SalaryPayoutService.Trust"/>); иначе пусто и жёлтая плашка. Частичная можно.
///
/// Четвёртое ревью (упрощено, как в оплате долга): прошлая выплата этому сотруднику без ответа сервера — ЗАМОК. Новая выплата
/// закрыта; в окне плашка и три кнопки: «Проверить на сервере» (нашлась — учесть; нет — ничего не решать), «Выплата ЕСТЬ на
/// сайте — учесть», «Выплаты НЕТ на сайте — снять блокировку» (обе с подтверждением). Итог окна — <see cref="PaidAny"/>.</summary>
public sealed class SalaryPayoutDialog : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private const double MaxAmount = 9_999_999;

    private readonly string _userId;
    private readonly string _employee;
    private readonly DateTime _from;
    private readonly DateTime _to;
    private readonly double _payable;
    /// <summary>Тот же расчёт «Выплачено», что у таблицы (все сотрудники периода) — для пересчёта (четвёртое ревью, п.3).</summary>
    private readonly Func<CancellationToken, Task<SalaryPayoutService.PaidResult>> _reloadPaid;
    private SalaryPayoutService.PaidResult? _paidResult;
    private bool _paidTimedOut;
    private double _paid;
    private bool _sure;

    /// <summary>«Уже выплачено · Остаток» в карточке.</summary>
    private readonly TextBlock _paidLine = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    /// <summary>Жёлтая плашка «не уверены» (правило Trust).</summary>
    private readonly TextBlock _trustText = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Border _trustBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), IsVisible = false };
    /// <summary>Замок: прошлая выплата без ответа сервера — плашка и три кнопки.</summary>
    private readonly TextBlock _lockText = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Border _lockBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), IsVisible = false };
    private readonly Button _lockCheck;
    private readonly Button _lockPaid;
    private readonly Button _lockNotPaid;

    private readonly TextBox _amount;
    private readonly ComboBox _cashbox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44, FontSize = 15 };
    private readonly TextBox _comment;
    private readonly TextBlock _amountError = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    // 2026-10-19, главная сессия: выбрана касса продаж (pos_*) — предупреждение под выбором кассы.
    private readonly TextBlock _cashboxWarning = new() { FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly Border _messageBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), IsVisible = false };
    private readonly Button _cancel;
    private readonly Button _go;

    private List<SalaryPayoutService.PayoutCashbox> _cashboxes = new();
    private bool _loaded;
    private bool _running;
    private bool _done;
    private bool _paidAny;
    private bool _amountErrorHard = true;
    /// <summary>Выплаты закрыты: журнал повреждён или не прочитался, компания не загружена.</summary>
    private bool _journalBroken;
    /// <summary>Замок: запись журнала ПК «без ответа сервера» по этому сотруднику.</summary>
    private SalaryPayoutService.SalaryPayoutRecord? _lock;

    /// <summary>2026-10-19, второе ревью (п.8): была ли проведена выплата (или учтена прошлая) — раздел читает после закрытия
    /// окна любым способом (кнопка, ✕, Esc, Alt+F4) и перечитывает «Выплачено».</summary>
    public bool PaidAny => _paidAny;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Som => T("сом", "сом", "som", "som", "so'm");

    private static string Short(double v) => Math.Abs(v % 1) < 0.005 ? v.ToString("N0", Ru) : v.ToString("N2", Ru);

    internal SalaryPayoutDialog(string userId, string employee, DateTime from, DateTime to, double payable,
        SalaryPayoutService.PaidResult? paidResult, bool paidTimedOut, Func<CancellationToken, Task<SalaryPayoutService.PaidResult>> reloadPaid)
    {
        _userId = userId;
        _employee = employee;
        _from = from;
        _to = to;
        _payable = payable;
        _reloadPaid = reloadPaid;
        _paidResult = paidResult;
        _paidTimedOut = paidTimedOut;
        Title = T("Выплата зарплаты", "Эмгек акы төлөө", "Salary payout", "Maaş ödemesi", "Ish haqi to'lovi");
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Use(this, BackgroundProperty, "DialogWindowBackground");
        Use(this, TransparencyLevelHintProperty, "DialogTransparencyHint");

        var root = new StackPanel { Spacing = 14 };

        // «К выплате за период» — с сервера (тот же расчёт, что в строке раздела).
        var payableCaption = Small(T("К выплате за период", "Мезгил үчүн төлөөгө", "Payable for the period", "Dönem için ödenecek", "Davr uchun to'lanadigan summa")
                                   + " " + SalaryPayoutService.PeriodText(from, to));
        var payableValue = new TextBlock { Text = SalaryWindow.Money(payable) + " " + Som, FontSize = 24, FontWeight = FontWeight.Bold };
        Use(payableValue, TextBlock.ForegroundProperty, "BrushText");
        Use(_paidLine, TextBlock.ForegroundProperty, "BrushTextSoft");
        var card = new StackPanel { Spacing = 4, Children = { payableCaption, payableValue, _paidLine } };
        var cardBorder = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 12), BorderThickness = new Thickness(1), Child = card };
        Use(cardBorder, Border.BackgroundProperty, "BrushPanelSoft");
        Use(cardBorder, Border.BorderBrushProperty, "BrushBorder");
        root.Children.Add(cardBorder);

        Use(_trustText, TextBlock.ForegroundProperty, "BrushText");
        _trustBox.Child = _trustText;
        Use(_trustBox, Border.BackgroundProperty, "BrushWarningSoft");
        Use(_trustBox, Border.BorderBrushProperty, "BrushWarning");
        root.Children.Add(_trustBox);

        // Замок — плашка и три решения владельца.
        Use(_lockText, TextBlock.ForegroundProperty, "BrushText");
        _lockCheck = UiKit.Ghost(this, T("Проверить на сервере", "Серверден текшерүү", "Check on the server", "Sunucuda kontrol et", "Serverda tekshirish"));
        _lockPaid = UiKit.Ghost(this, T("Выплата ЕСТЬ на сайте — учесть", "Төлөм сайтта БАР — эсепке алуу", "The payout IS on the website — count it",
            "Ödeme sitede VAR — hesaba kat", "To'lov saytda BOR — hisobga olish"));
        _lockNotPaid = UiKit.Ghost(this, T("Выплаты НЕТ на сайте — снять блокировку", "Төлөм сайтта ЖОК — бөгөттү алуу", "The payout is NOT on the website — unlock",
            "Ödeme sitede YOK — kilidi kaldır", "To'lov saytda YO'Q — blokni olish"));
        _lockNotPaid.Classes.Add("danger");
        foreach (var b in new[] { _lockCheck, _lockPaid, _lockNotPaid })
        {
            b.Height = 38;
            b.MinHeight = 38;
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        _lockCheck.Click += async (_, _) => await LockCheckAsync().ConfigureAwait(true);
        _lockPaid.Click += async (_, _) => await LockResolveAsync(paidOnSite: true).ConfigureAwait(true);
        _lockNotPaid.Click += async (_, _) => await LockResolveAsync(paidOnSite: false).ConfigureAwait(true);
        _lockBox.Child = new StackPanel { Spacing = 8, Children = { _lockText, _lockCheck, _lockPaid, _lockNotPaid } };
        Use(_lockBox, Border.BackgroundProperty, "BrushWarningSoft");
        Use(_lockBox, Border.BorderBrushProperty, "BrushWarning");
        root.Children.Add(_lockBox);

        // Сумма: по умолчанию — остаток, только если «Выплачено» проверено полностью; ввод денег — как в кассе (CashAmountInput).
        _amount = UiKit.Input(this, "0", 52);
        _amount.FontSize = 22;
        _amount.FontWeight = FontWeight.Bold;
        _amount.MaxLength = 14;
        _amount.AddHandler(TextInputEvent, (_, e) =>
        {
            if (e.Text is { Length: > 0 } text && text.Any(ch => !char.IsDigit(ch) && ch is not (',' or '.' or ' ')))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Avalonia.Automation.AutomationProperties.SetName(_amount, T("Сумма, сом", "Сумма, сом", "Amount, som", "Tutar, som", "Summa, so'm"));
        root.Children.Add(Block(T("Сумма, сом", "Сумма, сом", "Amount, som", "Tutar, som", "Summa, so'm"), _amount, _amountError));

        _cashbox.PlaceholderText = T("Загрузка касс…", "Кассалар жүктөлүүдө…", "Loading cash desks…", "Kasalar yükleniyor…", "Kassalar yuklanmoqda…");
        _cashbox.IsEnabled = false;
        _cashbox.SelectionChanged += (_, _) => UpdateState();
        Avalonia.Automation.AutomationProperties.SetName(_cashbox, T("Касса компании", "Компаниянын кассасы", "Company cash desk", "Şirket kasası", "Kompaniya kassasi"));
        _cashboxWarning.Text = SalaryPayoutService.SalesCashboxWarning;
        Use(_cashboxWarning, TextBlock.ForegroundProperty, "BrushDanger");
        root.Children.Add(Block(T("Касса компании", "Компаниянын кассасы", "Company cash desk", "Şirket kasası", "Kompaniya kassasi"), _cashbox, _cashboxWarning,
            Small(T("Из этой кассы уйдёт расход — как расход на сайте NurCRM. Смена кассира не меняется.",
                "Чыгым ушул кассадан кетет — NurCRM сайтындагы чыгым сыяктуу. Кассирдин сменасы өзгөрбөйт.",
                "The expense comes out of this cash desk — like an expense on the NurCRM website. The cashier's shift is not affected.",
                "Gider bu kasadan çıkar — NurCRM sitesindeki gider gibi. Kasiyerin vardiyası değişmez.",
                "Xarajat shu kassadan chiqadi — NurCRM saytidagi xarajat kabi. Kassir smenasi o'zgarmaydi."))));

        _comment = UiKit.Input(this, T("Например: аванс", "Мисалы: аванс", "For example: advance", "Örneğin: avans", "Masalan: avans"));
        _comment.MaxLength = 120;
        root.Children.Add(Block(T("Комментарий (необязательно)", "Комментарий (милдеттүү эмес)", "Comment (optional)", "Yorum (isteğe bağlı)", "Izoh (ixtiyoriy)"), _comment));

        Use(_amountError, TextBlock.ForegroundProperty, "BrushDanger");
        Use(_note, TextBlock.ForegroundProperty, "BrushTextSoft");
        _note.MinWidth = 140;
        _note.Text = SalaryPayoutService.ApprovedRightAway
            ? T("Расход «Зарплата» проводится сразу (вы владелец).", "«Эмгек акы» чыгымы дароо өткөрүлөт (сиз ээсисиз).",
                "The “Salary” expense is approved right away (you are the owner).", "«Maaş» gideri hemen onaylanır (sahibi sizsiniz).",
                "«Ish haqi» xarajati darhol o'tkaziladi (siz egasisiz).")
            : T("Расход будет ждать подтверждения владельца на сайте NurCRM.", "Чыгым NurCRM сайтында ээсинин ырастоосун күтөт.",
                "The expense will wait for the owner's approval on the NurCRM website.", "Gider NurCRM sitesinde sahibin onayını bekleyecek.",
                "Xarajat NurCRM saytida egasining tasdig'ini kutadi.");
        root.Children.Add(_messageBox);
        _messageBox.Child = _message;

        _cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        _cancel.IsCancel = true;
        _cancel.Click += (_, _) => { if (!_running) Close(); };
        _go = UiKit.Primary(this, T("Выплатить", "Төлөө", "Pay out", "Öde", "To'lash"));
        _go.MinWidth = 220;
        _go.IsEnabled = false;
        _go.Click += async (_, _) => await GoAsync().ConfigureAwait(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { _cancel, _go } };
        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = Title, Subtitle = employee + " · " + SalaryPayoutService.PeriodText(from, to),
            Footer = buttons, Hint = _note, Content = root,
        };

        ApplyPaid(prefill: true);
        _amount.TextChanged += (_, _) => UpdateState();
        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Closing += (_, e) =>
        {
            // Пока расход уходит на сервер, окно не закрывается (✕/Esc) — иначе владелец не узнает, прошла ли выплата.
            if (_running)
                e.Cancel = true;
        };
    }

    /// <summary>«Выплачено» этого сотрудника и правило доверия (четвёртое ревью, п.9): строка в карточке, жёлтая плашка и — только
    /// если уверены — остаток в поле суммы.</summary>
    private void ApplyPaid(bool prefill)
    {
        var info = _paidResult is not null && _paidResult.Users.TryGetValue(_userId, out var p) ? p : null;
        _paid = info?.Paid ?? 0;
        var (sure, warning) = SalaryPayoutService.Trust(_paidResult, info, _paidTimedOut);
        _sure = sure;
        _trustText.Text = warning ?? "";
        _trustBox.IsVisible = warning is not null;

        var coverage = _paidTimedOut ? SalaryPayoutService.PaidCoverage.Partial : _paidResult?.Coverage ?? SalaryPayoutService.PaidCoverage.None;
        var note = SalaryPayoutService.CoverageNote(coverage) is { Length: > 0 } n ? " " + n : "";
        if (_paid > 0)
        {
            var paidText = SalaryWindow.Money(_paid);
            var rest = _payable - _paid;
            // Четвёртое ревью (п.8): выплачено больше начисленного — «Сверх начисленного», как в таблице.
            _paidLine.Text = rest >= -0.005
                ? T($"Уже выплачено: {paidText} · Остаток: {SalaryWindow.Money(rest)}", $"Төлөнгөн: {paidText} · Калдык: {SalaryWindow.Money(rest)}",
                    $"Already paid: {paidText} · Remaining: {SalaryWindow.Money(rest)}", $"Ödenen: {paidText} · Kalan: {SalaryWindow.Money(rest)}",
                    $"To'langan: {paidText} · Qoldiq: {SalaryWindow.Money(rest)}") + note
                : T($"Уже выплачено: {paidText} · Сверх начисленного: {SalaryWindow.Money(-rest)}", $"Төлөнгөн: {paidText} · Эсептелгенден ашык: {SalaryWindow.Money(-rest)}",
                    $"Already paid: {paidText} · Over the accrued amount: {SalaryWindow.Money(-rest)}", $"Ödenen: {paidText} · Hesaplananın üstünde: {SalaryWindow.Money(-rest)}",
                    $"To'langan: {paidText} · Hisoblangandan ortiq: {SalaryWindow.Money(-rest)}") + note;
            _paidLine.IsVisible = true;
        }
        else
        {
            _paidLine.Text = sure ? "" : T("Уже выплачено: не проверено", "Төлөнгөн: текшерилген жок", "Already paid: not checked", "Ödenen: kontrol edilmedi", "To'langan: tekshirilmagan");
            _paidLine.IsVisible = !sure;
        }

        if (prefill)
        {
            var restToPay = Math.Round(_payable - _paid, 2);
            _amount.Text = sure && restToPay > 0 && _lock is null ? restToPay.ToString("0.##", CultureInfo.InvariantCulture) : "";
        }
    }

    /// <summary>Пересчёт «Выплачено» тем же вызовом, что у таблицы (все сотрудники периода), и сумма по умолчанию.</summary>
    private async Task RecalcPaidAsync()
    {
        try
        {
            SalaryPayoutService.ForgetCache();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            _paidResult = await _reloadPaid(cts.Token).ConfigureAwait(true);
            _paidTimedOut = false;
        }
        catch (OperationCanceledException)
        {
            _paidTimedOut = true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: «Выплачено» не пересчитано: {ex.Message}", "WARNING");
            _paidResult = null;
        }
        ApplyPaid(prefill: true);
    }

    private async Task LoadAsync()
    {
        // Журнал выплат повреждён или компания не загружена — выплаты закрыты (третье ревью п.6, пятая проверка п.1).
        var (readable, prev) = SalaryPayoutService.LockFor(_userId);
        if ((SalaryPayoutService.JournalBrokenText ?? SalaryPayoutService.CompanyUnknownText) is { } closed)
        {
            _journalBroken = true;
            ShowMessage(closed, "danger");
        }
        else if (!readable)
        {
            // Пятая проверка (п.2): журнал не прочитался — замок неизвестен, выплата закрыта до повтора (переоткрыть окно).
            _journalBroken = true;
            ShowMessage(SalaryPayoutService.JournalUnavailableText, "warning");
        }
        else if (prev is not null)
        {
            ShowLock(prev);
            // Один раз сами спросим сервер: нашлась — замок снимается и выплата учитывается. Не нашлась — решает владелец.
            await LockCheckAsync(quiet: true).ConfigureAwait(true);
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _cashboxes = await SalaryPayoutService.LoadCashboxesAsync(cts.Token).ConfigureAwait(true);
            _cashbox.ItemsSource = _cashboxes.Select(b => b.Name).ToList();
            _cashbox.SelectedIndex = SalaryPayoutService.DefaultCashboxIndex(_cashboxes);
            _cashbox.PlaceholderText = _cashboxes.Count == 0
                ? T("Касс нет — добавьте кассу на сайте NurCRM", "Кассалар жок — NurCRM сайтында касса кошуңуз", "No cash desks — add one on the NurCRM website",
                    "Kasa yok — NurCRM sitesinde kasa ekleyin", "Kassalar yo'q — NurCRM saytida kassa qo'shing")
                : T("Выберите кассу", "Кассаны тандаңыз", "Choose a cash desk", "Kasa seçin", "Kassani tanlang");
            _loaded = true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: кассы не получены: {ex.Message}", "WARNING");
            ShowMessage(T("Не удалось загрузить кассы компании: ", "Компаниянын кассаларын жүктөө мүмкүн болгон жок: ", "Could not load the company cash desks: ",
                "Şirket kasaları yüklenemedi: ", "Kompaniya kassalarini yuklab bo'lmadi: ") + ex.Message, "danger");
        }
        UpdateState();
        _amount.Focus();
        _amount.CaretIndex = _amount.Text?.Length ?? 0;
    }

    // ── Замок ─────────────────────────────────────────────────────────────────────

    private void ShowLock(SalaryPayoutService.SalaryPayoutRecord record)
    {
        _lock = record;
        _lockText.Text = SalaryPayoutService.LockText(record);
        _lockBox.IsVisible = true;
        _amount.Text = "";
        UpdateState();
    }

    private void HideLock()
    {
        _lock = null;
        _lockBox.IsVisible = false;
        UpdateState();
    }

    /// <summary>«Проверить на сервере»: нашлась — выплата учтена, замок снят; не нашлась или сервер не ответил — замок остаётся
    /// (таймауты на замок не влияют), владелец решает сам.</summary>
    private async Task LockCheckAsync(bool quiet = false)
    {
        if (_lock is not { } record || _running)
            return;
        _running = true;
        UpdateState();
        var found = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            found = await SalaryPayoutService.CheckUnknownAsync(record.Key, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: проверка выплаты {record.Key} не удалась: {ex.Message}", "WARNING");
        }
        finally
        {
            _running = false;
        }
        if (found)
        {
            HideLock();
            _paidAny = true;
            await RecalcPaidAsync().ConfigureAwait(true);
            ShowMessage(T($"Прошлая выплата {Short(record.Amount)} {Som} найдена на сервере — учтена.",
                $"Мурунку {Short(record.Amount)} {Som} төлөм серверден табылды — эсепке алынды.",
                $"The previous payout of {Short(record.Amount)} {Som} was found on the server — it is counted.",
                $"Önceki {Short(record.Amount)} {Som} ödemesi sunucuda bulundu — hesaba katıldı.",
                $"Oldingi {Short(record.Amount)} {Som} to'lov serverda topildi — hisobga olindi."), "success");
        }
        else if (!quiet)
        {
            ShowMessage(T($"На сервере не найдено. Проверьте расходы кассы «{record.CashboxName}» на сайте NurCRM и выберите: выплата есть или нет.",
                $"Серверден табылган жок. NurCRM сайтында «{record.CashboxName}» кассасынын чыгымдарын текшерип, тандаңыз: төлөм барбы же жокпу.",
                $"Not found on the server. Check the “{record.CashboxName}” cash desk expenses on the NurCRM website and choose whether the payout is there or not.",
                $"Sunucuda bulunamadı. NurCRM sitesinde «{record.CashboxName}» kasasının giderlerini kontrol edip ödemenin olup olmadığını seçin.",
                $"Serverda topilmadi. NurCRM saytida «{record.CashboxName}» kassasi xarajatlarini tekshirib, tanlang: to'lov bormi yoki yo'qmi."), "warning");
        }
        UpdateState();
    }

    /// <summary>Решение владельца по «неизвестной» выплате — с подтверждением, строка [PAYMENT] в журнал.</summary>
    private async Task LockResolveAsync(bool paidOnSite)
    {
        if (_lock is not { } record || _running)
            return;
        var money = $"{Short(record.Amount)} {Som}";
        var confirmed = await PosConfirmDialog.ShowModalAsync(this,
            paidOnSite
                ? T("Выплата есть на сайте?", "Төлөм сайтта барбы?", "Is the payout on the website?", "Ödeme sitede var mı?", "To'lov saytda bormi?")
                : T("Выплаты нет на сайте?", "Төлөм сайтта жокпу?", "Is the payout missing on the website?", "Ödeme sitede yok mu?", "To'lov saytda yo'qmi?"),
            paidOnSite
                ? T($"Выплата {money} — {record.Employee} будет учтена как проведённая (войдёт в «Выплачено»).",
                    $"{money} төлөм — {record.Employee} өткөрүлгөн катары эсепке алынат («Төлөндү» ичине кирет).",
                    $"The payout of {money} to {record.Employee} will be counted as made (included in “Paid”).",
                    $"{record.Employee} için {money} ödeme yapılmış sayılacak («Ödenen»e girer).",
                    $"{record.Employee}ga {money} to'lov o'tkazilgan deb hisobga olinadi («To'langan»ga kiradi).")
                : T($"Выплата {money} — {record.Employee} будет считаться НЕ проведённой, блокировка снимется. Если на самом деле расход на сайте есть, новая выплата будет двойной.",
                    $"{money} төлөм — {record.Employee} өткөрүлгөн ЭМЕС деп эсептелет, бөгөт алынат. Чынында сайтта чыгым болсо, жаңы төлөм эки жолку болот.",
                    $"The payout of {money} to {record.Employee} will be treated as NOT made and the lock removed. If the expense is actually on the website, a new payout will be a double one.",
                    $"{record.Employee} için {money} ödeme YAPILMAMIŞ sayılacak, kilit kalkacak. Gider aslında sitede varsa yeni ödeme çift olur.",
                    $"{record.Employee}ga {money} to'lov o'tkazilmagan deb hisoblanadi, blok olinadi. Agar saytda xarajat aslida bo'lsa, yangi to'lov ikki marta bo'ladi."),
            paidOnSite ? T("Учесть", "Эсепке алуу", "Count it", "Hesaba kat", "Hisobga olish") : T("Снять блокировку", "Бөгөттү алуу", "Unlock", "Kilidi kaldır", "Blokni olish"),
            T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
            paidOnSite ? PosConfirmAccent.Primary : PosConfirmAccent.Danger).ConfigureAwait(true);
        if (!confirmed)
            return;
        if (!SalaryPayoutService.ResolveUnknown(record.Key, paidOnSite))
        {
            ShowMessage(SalaryPayoutService.JournalBrokenText ?? T("Не удалось записать решение в журнал этого ПК.", "Чечимди ушул компьютердин журналына жазуу мүмкүн болгон жок.",
                "Could not write the decision to this PC's log.", "Karar bu bilgisayarın günlüğüne yazılamadı.", "Qarorni ushbu kompyuter jurnaliga yozib bo'lmadi."), "danger");
            return;
        }
        HideLock();
        _paidAny = true;
        await RecalcPaidAsync().ConfigureAwait(true);
        ShowMessage(paidOnSite
            ? T($"Выплата {money} учтена.", $"{money} төлөм эсепке алынды.", $"The payout of {money} is counted.", $"{money} ödeme hesaba katıldı.", $"{money} to'lov hisobga olindi.")
            : T("Блокировка снята — можно выплатить.", "Бөгөт алынды — төлөсө болот.", "Unlocked — you can pay now.", "Kilit kaldırıldı — ödeme yapılabilir.", "Blok olindi — to'lash mumkin."),
            "success");
    }

    // ── Сумма и выплата ───────────────────────────────────────────────────────────

    /// <summary>Четвёртое ревью (п.5): сумма округляется до 0,01 ДО проверки «больше нуля». null — не число.</summary>
    private double? Value =>
        CashAmountInput.TryParse(_amount.Text, out var v) ? (double)Math.Round(v, 2, MidpointRounding.AwayFromZero) : null;

    private SalaryPayoutService.PayoutCashbox? SelectedCashbox =>
        _cashbox.SelectedIndex >= 0 && _cashbox.SelectedIndex < _cashboxes.Count ? _cashboxes[_cashbox.SelectedIndex] : null;

    private bool Blocked => _journalBroken || _lock is not null;

    private void UpdateState()
    {
        if (_go is null)
            return;
        string? problem = null;
        var value = Value;
        if (!string.IsNullOrWhiteSpace(_amount.Text))
        {
            if (value is null)
                problem = T("Сумма — число.", "Сумма — сан.", "The amount must be a number.", "Tutar bir sayı olmalı.", "Summa — son.");
            else if (value <= 0)
                problem = T("Введите сумму.", "Сумманы жазыңыз.", "Enter the amount.", "Tutarı girin.", "Summani kiriting.");
            else if (value > MaxAmount)
                problem = T("Слишком большая сумма.", "Сумма өтө чоң.", "The amount is too large.", "Tutar çok büyük.", "Summa juda katta.");
        }
        string? soft = null;
        var remaining = _payable - _paid;
        if (problem is null && value is { } v && v > 0 && v > remaining + 0.005)
            soft = T($"Больше остатка к выплате ({SalaryWindow.Money(Math.Max(0, remaining))}) — выплата всё равно возможна (например, аванс).",
                $"Төлөнө турган калдыктан ({SalaryWindow.Money(Math.Max(0, remaining))}) көп — баары бир төлөөгө болот (мисалы, аванс).",
                $"More than the remaining amount ({SalaryWindow.Money(Math.Max(0, remaining))}) — you can still pay (e.g. an advance).",
                $"Kalan tutardan ({SalaryWindow.Money(Math.Max(0, remaining))}) fazla — yine de ödenebilir (ör. avans).",
                $"Qoldiqdan ({SalaryWindow.Money(Math.Max(0, remaining))}) ko'p — baribir to'lash mumkin (masalan, avans).");
        _amountError.Text = problem ?? soft ?? "";
        _amountError.IsVisible = (problem ?? soft) is not null;
        if (_amountErrorHard != (problem is not null))
        {
            _amountErrorHard = problem is not null;
            Use(_amountError, TextBlock.ForegroundProperty, _amountErrorHard ? "BrushDanger" : "BrushTextSoft");
        }

        _cashboxWarning.IsVisible = SelectedCashbox is { } box && SalaryPayoutService.IsSalesCashbox(box);
        var ok = _loaded && !Blocked && !_running && !_done && problem is null && value is > 0 && SelectedCashbox is not null;
        _go.Content = T("Выплатить", "Төлөө", "Pay out", "Öde", "To'lash") + (value is > 0 and var a && problem is null ? $" {Short(a)} {Som}" : "");
        _go.IsEnabled = ok;
        _amount.IsEnabled = _comment.IsEnabled = !_running && !_done && !Blocked;
        _cashbox.IsEnabled = !_running && !_done && !Blocked && _cashboxes.Count > 0;
        _lockCheck.IsEnabled = _lockPaid.IsEnabled = _lockNotPaid.IsEnabled = !_running && _lock is not null;
        _cancel.IsEnabled = !_running;
        if (_done)
            _cancel.Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
    }

    private async Task GoAsync()
    {
        // Защита от двойного нажатия: флаг ставится до первого await.
        if (_running || Blocked || _done || Value is not { } amount || amount <= 0 || SelectedCashbox is not { } cashbox || amount > MaxAmount)
            return;

        _running = true;
        UpdateState();
        HideMessage();
        // 2026-10-19, главная сессия (на NBS в один клик ушли 268 316,36 сом): перед каждой выплатой — подтверждение.
        bool confirmed;
        try
        {
            confirmed = await new SalaryPayoutConfirmDialog(amount, _employee, cashbox).ShowDialog<bool>(this).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: подтверждение не открылось: {ex}", "ERROR");
            confirmed = false;
        }
        if (!confirmed)
        {
            _running = false;
            UpdateState();
            return;
        }
        var key = Guid.NewGuid().ToString();
        try
        {
            // Пятая проверка (п.3): время на категорию (15 с) и на расход (45 с) PayAsync отмеряет сам.
            var result = await SalaryPayoutService.PayAsync(
                new SalaryPayoutService.PayoutRequest(key, _userId, _employee, _from, _to, amount, cashbox, _comment.Text), CancellationToken.None).ConfigureAwait(true);
            switch (result.Outcome)
            {
                case SalaryPayoutService.PayoutOutcome.Done:
                    Done(amount, cashbox, result.Status);
                    break;
                case SalaryPayoutService.PayoutOutcome.Rejected:
                    ShowMessage(T("Выплата не прошла: ", "Төлөм өткөн жок: ", "The payout failed: ", "Ödeme yapılmadı: ", "To'lov o'tmadi: ") + result.Error, "danger");
                    break;
                default:
                    // Ответ не дошёл — замок на этом сотруднике, решения — в плашке.
                    _paidAny = true;
                    if (SalaryPayoutService.UnknownFor(_userId) is { } unknown)
                        ShowLock(unknown);
                    ShowMessage(UnknownText(amount, cashbox.Name), "warning");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Отмена возможна только до отправки расхода (справочник категорий) — расход не ушёл.
            ShowMessage(T("Сервер не ответил вовремя — выплата не отправлена. Попробуйте ещё раз.", "Сервер өз убагында жооп берген жок — төлөм жөнөтүлгөн жок. Кайра аракет кылыңыз.",
                "The server didn't answer in time — the payout was not sent. Try again.", "Sunucu zamanında yanıt vermedi — ödeme gönderilmedi. Tekrar deneyin.",
                "Server o'z vaqtida javob bermadi — to'lov yuborilmadi. Qayta urinib ko'ring."), "danger");
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: сбой окна: {ex}", "ERROR");
            ShowMessage(ex.Message, "danger");
        }
        finally
        {
            _running = false;
            UpdateState();
        }
    }

    private void Done(double amount, SalaryPayoutService.PayoutCashbox? cashbox, string? status)
    {
        _done = true;
        _paidAny = true;
        if (cashbox is not null && UserPreferences.Instance.SalaryPayoutCashboxId != cashbox.Id)
        {
            UserPreferences.Instance.SalaryPayoutCashboxId = cashbox.Id;
            UserPreferences.Instance.SaveToDisk();
        }
        var pending = status is { Length: > 0 } s && !string.Equals(s, "approved", StringComparison.OrdinalIgnoreCase);
        ShowMessage(T($"Выплачено {Short(amount)} {Som}: расход «Зарплата» из кассы «{cashbox?.Name}».",
                $"{Short(amount)} {Som} төлөндү: «{cashbox?.Name}» кассасынан «Эмгек акы» чыгымы.",
                $"Paid {Short(amount)} {Som}: a “Salary” expense from the “{cashbox?.Name}” cash desk.",
                $"{Short(amount)} {Som} ödendi: «{cashbox?.Name}» kasasından «Maaş» gideri.",
                $"{Short(amount)} {Som} to'landi: «{cashbox?.Name}» kassasidan «Ish haqi» xarajati.")
            + (pending ? " " + T("Ждёт подтверждения на сайте.", "Сайтта ырастоону күтөт.", "Awaiting approval on the website.", "Sitede onay bekliyor.", "Saytda tasdiqni kutmoqda.") : ""),
            "success");
    }

    private static string UnknownText(double amount, string cashbox) =>
        T($"Ответ сервера не дошёл — выплата {Short(amount)} {Som} могла пройти. Повторно не отправляем, чтобы не было двойного расхода. Проверьте на сервере или на сайте NurCRM (касса «{cashbox}») и выберите решение ниже.",
            $"Сервердин жообу келген жок — {Short(amount)} {Som} төлөм өтүшү мүмкүн. Эки жолку чыгым болбошу үчүн кайра жөнөтпөйбүз. Серверден же NurCRM сайтынан («{cashbox}» кассасы) текшерип, төмөнкү чечимди тандаңыз.",
            $"The server reply didn't arrive — the payout of {Short(amount)} {Som} may have gone through. It is not resent, to avoid a double expense. Check on the server or on the NurCRM website (“{cashbox}” cash desk) and choose a decision below.",
            $"Sunucu yanıtı gelmedi — {Short(amount)} {Som} ödemesi geçmiş olabilir. Çift gider olmaması için yeniden gönderilmiyor. Sunucuda veya NurCRM sitesinde («{cashbox}» kasası) kontrol edip aşağıdan karar seçin.",
            $"Server javobi kelmadi — {Short(amount)} {Som} to'lov o'tgan bo'lishi mumkin. Ikki marta xarajat bo'lmasligi uchun qayta yuborilmaydi. Serverda yoki NurCRM saytida («{cashbox}» kassasi) tekshirib, quyidagi qarorni tanlang.");

    private void ShowMessage(string text, string kind)
    {
        _message.Text = text;
        var (bg, fg) = kind switch
        {
            "success" => ("BrushSuccessSoft", "BrushSuccess"),
            "warning" => ("BrushWarningSoft", "BrushText"),
            _ => ("BrushDangerSoft", "BrushDangerOnSoft"),
        };
        Use(_messageBox, Border.BackgroundProperty, bg);
        Use(_message, TextBlock.ForegroundProperty, fg);
        _messageBox.IsVisible = true;
    }

    private void HideMessage() => _messageBox.IsVisible = false;

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

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>2026-10-19, главная сессия: подтверждение каждой выплаты зарплаты — «Выплатить N сом — ФИО из кассы «…»?», сумма
/// крупно, фокус по умолчанию на «Отмена» (Enter не выплачивает). От 10 000 сом — ещё галочка «Сумма верная», без неё
/// «Выплатить» выключена. Результат ShowDialog — true, только если нажата «Выплатить».</summary>
internal sealed class SalaryPayoutConfirmDialog : Window
{
    public const double BigAmount = 10_000;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public SalaryPayoutConfirmDialog(double amount, string employee, SalaryPayoutService.PayoutCashbox cashbox)
    {
        Title = T("Подтвердите выплату", "Төлөмдү ырастаңыз", "Confirm the payout", "Ödemeyi onaylayın", "To'lovni tasdiqlang");
        Width = 512;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var money = SalaryWindow.Money(amount);
        var som = T("сом", "сом", "som", "som", "so'm");
        var big = new TextBlock { Text = $"{money} {som}", FontSize = 30, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        big.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var question = new TextBlock
        {
            Text = T($"Выплатить {money} сом — {employee} из кассы «{cashbox.Name}»?", $"{money} сом төлөнсүнбү — {employee}, «{cashbox.Name}» кассасынан?",
                $"Pay {money} som to {employee} from the “{cashbox.Name}” cash desk?", $"{employee} için «{cashbox.Name}» kasasından {money} som ödensin mi?",
                $"{employee}ga «{cashbox.Name}» kassasidan {money} so'm to'lansinmi?"),
            FontSize = 15, TextWrapping = TextWrapping.Wrap,
        };
        question.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var content = new StackPanel { Spacing = 10, Children = { big, question } };
        if (SalaryPayoutService.IsSalesCashbox(cashbox))
        {
            var warn = new TextBlock
            {
                Text = SalaryPayoutService.SalesCashboxWarning,
                FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
            };
            warn.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
            content.Children.Add(warn);
        }

        var cancel = new Button { Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        var ok = new Button { Content = T("Выплатить", "Төлөө", "Pay out", "Öde", "To'lash") };
        ok.Classes.Add("modal-primary");
        ok.Click += (_, _) => Close(true);
        if (amount >= BigAmount)
        {
            // Крупная сумма — отдельная галочка: «Выплатить» включается только после неё.
            var sure = new CheckBox
            {
                Content = T($"Сумма верная: {money} сом", $"Сумма туура: {money} сом", $"The amount is correct: {money} som",
                    $"Tutar doğru: {money} som", $"Summa to'g'ri: {money} so'm"),
                FontSize = 14,
            };
            ok.IsEnabled = false;
            sure.IsCheckedChanged += (_, _) => ok.IsEnabled = sure.IsChecked == true;
            content.Children.Add(sure);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };
        Content = new ModalShell { Margin = new Thickness(16), Title = Title, Footer = buttons, Content = content };
        // По умолчанию фокус на «Отмена»: случайный Enter выплату не проводит.
        Opened += (_, _) => cancel.Focus();
    }
}
