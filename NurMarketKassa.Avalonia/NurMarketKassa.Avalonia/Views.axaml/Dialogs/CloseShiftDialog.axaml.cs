using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class CloseShiftDialog : Window
{
    // Поле заполняется значением в инвариантной культуре (точка), поэтому и разбор
    // должен быть инвариантным — иначе на ru-RU/de-DE сумма теряется или растёт в 100 раз.
    // 2026-10-15, ревью: NumberStyles.Number считал запятую разделителем тысяч — «1500,50» с русской
    // цифровой клавиатуры становилось 150050. Теперь общий разбор CashAmountInput (запятая = точка).
    // 2026-10-17, ревью этапа 1: сумма запоминается в момент подтверждения — вызывающий код читает её уже
    // после закрытия окна, а фоновое UpdateSystemBalance могло успеть подменить подсказку в поле.
    public decimal? ClosingCash => _confirmedClosingCash ?? CashAmountInput.Parse(ClosingCashBox.Text);

    private decimal? _confirmedClosingCash;

    /// <summary>2026-10-15, ревью: кассир сам менял поле «Фактический остаток» — фоновое UpdateSystemBalance
    /// его больше не трогает, даже если введённое совпало с прежней подсказкой (пересчитал и подтвердил).</summary>
    private bool _closingCashEdited;

    /// <summary>Текст, который поставила в поле сама касса (подсказка) — всё остальное считается вводом кассира.</summary>
    private string? _programmaticClosingCash;

    public decimal? SuggestedBalance { get; set; }

    /// <summary>2026-09-12, по просьбе пользователя ("сделай так же как в вебе") — разбивка
    /// (начальная сумма/продажи/наличные/безналичные), заполняется вызывающим кодом так же, как
    /// SuggestedBalance: сначала кэш при открытии диалога, затем свежее значение из фона через
    /// UpdateTotals, чтобы не блокировать сам клик "Закрыть смену".</summary>
    public ShiftBalanceHelper.ShiftTotals? Totals { get; set; }

    /// <summary>2026-10-16, редизайн (этап 1): «слепой пересчёт» (Настройки → Операции,
    /// UserPreferences.BlindCashCount). Поле суммы пустое, сумма по системе, разбивка (по ней её легко
    /// вычислить) и расхождение скрыты, пока кассир не ввёл пересчитанные наличные и не нажал
    /// «Пересчитал»; после этого сумма закрепляется (поле только для чтения) и видны ожидаемое и
    /// расхождение — вторым нажатием смена закрывается. Режим берётся один раз при создании окна.</summary>
    private readonly bool _blindCount;

    /// <summary>Кассир нажал «Пересчитал» — ожидаемое и расхождение показаны.</summary>
    private bool _blindRevealed;

    /// <summary>Когда показали ожидаемое: второе нажатие сразу следом (двойной Enter) не закрывает смену,
    /// не дав увидеть расхождение. 2026-10-17, ревью: TickCount64 вместо UtcNow — перевод часов назад
    /// при синхронизации больше не «замораживает» кнопку.</summary>
    private long _blindRevealedAtTicks;

    /// <summary>2026-10-17, ревью: Enter был зажат в момент «Пересчитал» — автоповтор (~0,75 с) иначе сразу
    /// закрывал смену. Следующий Enter засчитывается только после того, как клавишу отпустили.</summary>
    private bool _enterDown;
    private bool _awaitEnterRelease;

    /// <summary>2026-10-17, ревью: первый слепой пересчёт этой смены, если окно потом закрыли отменой
    /// (UserPreferences.BlindCountFirstAttempt) — показывается кассиру и пишется в журнал, чтобы
    /// «посмотрел ожидаемое → отмена → ввёл его» не проходило бесследно.</summary>
    private (DateTime At, decimal Counted, decimal? Expected)? _firstAttempt;

    private bool ExpectedHidden => _blindCount && !_blindRevealed;

    /// <summary>2026-10-23 (аудит, круг 8, В-7): отчёт смены сервера — те же цифры, что в X-отчёте («Просмотр смены»):
    /// оплата долгов, внесения/изъятия берутся из него, а не только из журнала этой кассы. null — ещё не пришёл / нет связи.</summary>
    private ServerShiftReport? _report;
    private readonly CancellationTokenSource _reportCts = new();

    public CloseShiftDialog()
    {
        InitializeComponent();
        _blindCount = UserPreferences.Instance.BlindCashCount;
        Opened += OnDialogOpened;
        Closed += (_, _) => _reportCts.Cancel(); // 2026-10-23 (аудит, круг 8, В-7)
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) _enterDown = true; }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            _enterDown = false;
            _awaitEnterRelease = false;
        }, RoutingStrategies.Tunnel);
        // Отпущенный в другом окне Enter не должен блокировать щелчок мышью по «Закрыть смену».
        AddHandler(PointerPressedEvent, (_, _) => { _enterDown = false; _awaitEnterRelease = false; }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _enterDown = false;
    }

    private static (DateTime At, decimal Counted, decimal? Expected)? ReadFirstAttempt()
    {
        var raw = UserPreferences.Instance.BlindCountFirstAttempt;
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var parts = raw.Split('|');
        if (parts.Length < 2
            || !DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            || !decimal.TryParse(parts[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var counted))
            return null;
        // Запись прошлых дней — от другой смены (сервер сам закрывает смены в 01:00).
        if (at.Date != DateTime.Now.Date)
            return null;
        decimal? expected = parts.Length > 2 && decimal.TryParse(parts[2], NumberStyles.Number, CultureInfo.InvariantCulture, out var exp) ? exp : null;
        return (at, counted, expected);
    }

    private static void SaveFirstAttempt(decimal counted, decimal? expected)
    {
        UserPreferences.Instance.BlindCountFirstAttempt = string.Join('|',
            DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
            counted.ToString("0.00", CultureInfo.InvariantCulture),
            expected?.ToString("0.00", CultureInfo.InvariantCulture) ?? "");
        UserPreferences.Instance.SaveToDisk();
    }

    private string FirstAttemptText() => _firstAttempt is not { } a
        ? ""
        : Tr.T("Первый пересчёт в ", "Биринчи эсептөө ", "First count at ", "İlk sayım ", "Birinchi sanash ")
          + a.At.ToString("HH:mm", CultureInfo.InvariantCulture) + ": "
          + a.Counted.ToString("0.00", CultureInfo.InvariantCulture)
          + Tr.T(" сом (окно закрыли без закрытия смены).", " сом (терезе смена жабылбай жабылган).", " som (the window was closed without closing the shift).",
              " som (pencere vardiya kapatılmadan kapatıldı).", " so'm (oyna smena yopilmasdan yopilgan).");

    private void OnDialogOpened(object? sender, EventArgs e)
    {
        if (SuggestedBalance.HasValue)
        {
            SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(SuggestedBalance);
            // 2026-10-16: при слепом пересчёте подсказку в поле не ставим — иначе кассир просто нажмёт «Закрыть».
            // 2026-10-21 (пакет 6, хвост К16 живого аудита): и без слепого пересчёта поле больше не заполняется суммой
            // по системе — кассир закрывал смену, не пересчитав ящик. Ожидаемое — подсказкой в поле и кнопкой «= по системе».
        }
        UpdateExpectedHint();

        if (_blindCount)
        {
            _firstAttempt = ReadFirstAttempt();
            if (_firstAttempt is { } a)
                PosLogger.Log($"Слепой пересчёт: окно закрытия открыто повторно, первый пересчёт {a.At:HH:mm} — {a.Counted:0.00}"
                    + (a.Expected is { } ex ? $" (по системе {ex:0.00})" : ""), "SHIFT");
        }

        ApplyTotals(Totals);
        _ = LoadServerReportAsync(); // 2026-10-23 (аудит, круг 8, В-7)
        ApplyBlindState();
        ClosingCashBox.Focus();
        ClosingCashBox.SelectAll();
        UpdateDiscrepancy();
    }

    /// <summary>2026-10-23 (аудит, круг 8, В-7): Z-отчёт не совпадал с X-отчётом — «Оплата долгов наличными +3» (только события этой кассы)
    /// и «Корректировка +201 620» против +201 623 в X. Грузим тот же отчёт смены сервера (таймаут 6 с; без связи остаётся как было)
    /// и перестраиваем строки и раскладку ящика по нему.</summary>
    private async Task LoadServerReportAsync()
    {
        var shiftId = NurMarketKassa.PosApp.ActiveShiftId;
        if (string.IsNullOrWhiteSpace(shiftId))
            return;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_reportCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var report = await NurCrmReportsApi.GetShiftReportAsync(shiftId, cts.Token).ConfigureAwait(true);
            if (report is null || _reportCts.IsCancellationRequested)
                return;
            _report = report;
            ApplyShiftOperations();
        }
        catch (OperationCanceledException)
        {
            // Закрыли окно или вышел таймаут — показываем по журналу кассы, как раньше.
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно закрытия смены: отчёт сервера не получен: {ex.Message}", "SHIFT");
        }
    }

    /// <summary>2026-10-16: видимость ожидаемой суммы и надписи главной кнопки для слепого пересчёта.</summary>
    private void ApplyBlindState()
    {
        if (!_blindCount)
            return;

        var hidden = ExpectedHidden;
        SystemBalanceBorder.IsVisible = !hidden;
        // 2026-10-17, ревью: первый пересчёт (если окно уже закрывали отменой) виден и после «Пересчитал».
        BlindNoteBorder.IsVisible = hidden || _firstAttempt is not null;
        var instruction = hidden
            ? Tr.T(
                "Слепой пересчёт: посчитайте наличные в ящике и введите сумму. Сумма по системе и расхождение появятся после нажатия «Пересчитал».",
                "Сокур эсептөө: кутудагы накталай акчаны санап, сумманы киргизиңиз. Система боюнча сумма жана айырма «Санадым» баскычын баскандан кийин көрүнөт.",
                "Blind count: count the cash in the drawer and enter the amount. The system amount and the difference appear after you press “Counted”.",
                "Kör sayım: çekmecedeki nakdi sayıp tutarı girin. Sistem tutarı ve fark «Saydım» düğmesine bastıktan sonra görünür.",
                "Ko'r sanash: tortmadagi naqd pulni sanab, summani kiriting. Tizim bo'yicha summa va farq «Sanadim» tugmasini bosgandan keyin ko'rinadi.")
            : "";
        var first = FirstAttemptText();
        BlindNoteText.Text = string.IsNullOrEmpty(instruction) ? first
            : string.IsNullOrEmpty(first) ? instruction
            : instruction + "\n" + first;
        PrimaryButton.Content = hidden
            ? Tr.T("Пересчитал", "Санадым", "Counted", "Saydım", "Sanadim")
            : Tr.T("Закрыть смену", "Сменаны жабуу", "Close shift", "Vardiyayı kapat", "Smenani yopish");
        if (!hidden)
            Shell.Hint = Tr.T(
                "Проверьте расхождение и закройте смену",
                "Айырманы текшерип, сменаны жабыңыз",
                "Check the difference and close the shift",
                "Farkı kontrol edip vardiyayı kapatın",
                "Farqni tekshirib, smenani yoping");
    }

    /// <summary>Обновление разбивки из фона (см. Totals) — параллельно UpdateSystemBalance.</summary>
    public void UpdateTotals(ShiftBalanceHelper.ShiftTotals? totals)
    {
        Totals = totals;
        ApplyTotals(totals);
    }

    private void ApplyTotals(ShiftBalanceHelper.ShiftTotals? totals)
    {
        if (TotalsBorder is null)
            return;
        // 2026-10-20 (К16): возвраты, оплаты долга, внесения/изъятия смены — вместе с разбивкой продаж.
        ApplyShiftOperations();

        // Хотя бы одно значение известно — показываем карточку, остальные строки покажут "—".
        var hasAny = totals is { OpeningCash: not null } or { TotalSales: not null }
            or { CashSales: not null } or { NonCashSales: not null };
        // 2026-09-12, временная диагностика по жалобе "не отображаешь подробно" — снять после
        // подтверждения, что разбивка реально доходит до диалога и карточка становится видимой.
        PosLogger.Log($"ApplyTotals: hasAny={hasAny}, totalsIsNull={totals is null}", "SHIFT");
        // 2026-10-16: при слепом пересчёте разбивка скрыта до «Пересчитал» (по ней видно ожидаемое).
        TotalsBorder.IsVisible = hasAny && !ExpectedHidden;
        if (!hasAny)
            return;

        OpeningCashText.Text = ShiftBalanceHelper.FormatBalance(totals!.OpeningCash);
        // 2026-10-23 (аудит К5, п. 3): «Выручка (без долга)» = наличные + безнал, как «Сводка» владельца; sales_total сервера включает долг.
        TotalSalesText.Text = ShiftBalanceHelper.FormatBalance(
            totals.TotalSales is { } ts && totals.CashSales is { } cs && totals.NonCashSales is { } ns ? Math.Min(ts, cs + ns) : totals.TotalSales);
        CashSalesText.Text = ShiftBalanceHelper.FormatBalance(totals.CashSales);
        NonCashSalesText.Text = ShiftBalanceHelper.FormatBalance(totals.NonCashSales);
    }

    /// <summary>2026-10-20 (живой аудит кассы, К16): окно закрытия смены показывало только продажи («Продажи всего 0,00» при
    /// возвратах, оплате долга, внесении и изъятии). Теперь — и эти операции смены из журнала кассы (ShiftEventsStore,
    /// внесения/изъятия — ShiftCashOperationsStore); у возвратов — чем вернули (наличными / на карту / в счёт долга).</summary>
    private void ApplyShiftOperations()
    {
        try
        {
            var shiftId = NurMarketKassa.PosApp.ActiveShiftId;
            var rows = new List<ShiftOpRow>();
            if (!string.IsNullOrWhiteSpace(shiftId))
            {
                var returns = ShiftEventsStore.ListForShift(shiftId, ShiftEventsStore.KindReturn);
                var returnsSum = returns.Sum(r => r.Amount);
                if (returnsSum > 0.005)
                {
                    double cash = 0, nonCash = 0, debt = 0;
                    var split = false;
                    foreach (var r in returns)
                    {
                        if (ParseNote(r.Note, "cash") is { } c)
                        {
                            split = true;
                            cash += c;
                            nonCash += ParseNote(r.Note, "noncash") ?? 0;
                            debt += ParseNote(r.Note, "debt") ?? 0;
                        }
                        else
                        {
                            cash += r.Amount; // записи до 20.10 — без разбивки (как раньше — из ящика)
                        }
                    }
                    var parts = new List<string>();
                    if (cash > 0.005)
                        parts.Add(Tr.T("наличными ", "накталай ", "cash ", "nakit ", "naqd ") + MoneyText.Som(cash));
                    if (nonCash > 0.005)
                        parts.Add(Tr.T("на карту/перевод ", "картага/которуу ", "card/transfer ", "kart/havale ", "karta/o'tkazma ") + MoneyText.Som(nonCash));
                    if (debt > 0.005)
                        parts.Add(Tr.T("в счёт долга ", "карыздын эсебинен ", "against debt ", "borçtan ", "qarz hisobidan ") + MoneyText.Som(debt));
                    rows.Add(new ShiftOpRow(
                        Tr.T($"Возвраты ({returns.Count})", $"Кайтаруулар ({returns.Count})", $"Returns ({returns.Count})", $"İadeler ({returns.Count})", $"Qaytarishlar ({returns.Count})"),
                        "−" + MoneyText.Som(returnsSum),
                        split || parts.Count > 1 ? string.Join(" · ", parts) : ""));

                    // 2026-10-21 (ревью денег, мелочь 5): сервер не учитывает скидку на чек при возврате (ТЗ 19.9) и списывает из
                    // смены больше, чем касса выдала, — «по системе» меньше ящика на эту сумму. Объясняем, чтобы ящик не подгоняли.
                    var serverOver = returns.Sum(r => ParseNote(r.Note, "server_over") ?? 0);
                    if (serverOver > 0.005)
                        rows.Add(new ShiftOpRow(
                            Tr.T("Сервер записал возвраты больше выданного", "Сервер кайтарууларды берилгенден көп жазды", "The server recorded returns above what was paid out",
                                "Sunucu iadeleri verilenden fazla kaydetti", "Server qaytarishlarni berilganidan ko'p yozdi"),
                            MoneyText.Som(serverOver),
                            Tr.T("«По системе» в ящике может быть меньше на эту сумму (скидка на чек при возврате не учтена сервером). Ящик не подгоняйте — см. «Некорректные чеки».",
                                "«Система боюнча» кутуда ушул суммага азыраак болушу мүмкүн (кайтарууда чектин арзандатуусун сервер эсепке алган жок). Кутуну тууралабаңыз — «Туура эмес чектер» бөлүмүн караңыз.",
                                "“As system” may be lower than the drawer by this amount (the server ignored the receipt discount on return). Do not adjust the drawer — see “Irregular receipts”.",
                                "«Sisteme göre» tutar çekmeceden bu kadar az olabilir (sunucu iadede fiş indirimini dikkate almadı). Çekmeceyi ayarlamayın — «Sorunlu fişler» bölümüne bakın.",
                                "«Tizim bo'yicha» tortmadagidan shu summaga kam bo'lishi mumkin (server qaytarishda chek chegirmasini hisobga olmadi). Tortmani moslamang — «Noto'g'ri cheklar»ni ko'ring.")));
                }

                var totals = ShiftEventsStore.TotalsForShift(shiftId);
                double Get(string kind) => totals.TryGetValue(kind, out var v) ? v : 0;
                // 2026-10-23 (аудит, круг 8, В-7): как в X-отчёте — оплата долгов наличными из отчёта сервера (там и другие кассы, и программа владельца).
                var debtCash = _report?.DebtPaymentsCash is { } serverDebtCash ? (double)serverDebtCash : Get(ShiftEventsStore.KindDebtPayment);
                var debtNonCash = Get(ShiftEventsStore.KindDebtPaymentNonCash);
                if (debtCash > 0.005)
                    rows.Add(new ShiftOpRow(Tr.T("Оплата долгов наличными", "Карыздарды накталай төлөө", "Debt payments in cash", "Nakit borç ödemeleri", "Qarz to'lovlari naqd"),
                        "+" + MoneyText.Som(debtCash), ""));
                if (debtNonCash > 0.005)
                    rows.Add(new ShiftOpRow(Tr.T("Оплата долгов безналом", "Карыздарды накталай эмес төлөө", "Debt payments non-cash", "Nakitsiz borç ödemeleri", "Qarz to'lovlari naqd emas"),
                        "+" + MoneyText.Som(debtNonCash),
                        Tr.T("в ящик не входит", "кутуга кирбейт", "not in the drawer", "çekmeceye girmez", "tortmaga kirmaydi")));

                // 2026-10-22 (статьи изъятия): под суммой — разбивка по статьям («Аренда 5 000 · Инкассация 20 000»).
                static string ByArticle(IReadOnlyList<(string Title, decimal Amount, bool IsCollection)> parts) =>
                    parts.Count == 0 || parts.Count == 1 && parts[0].Title == Tr.T("без статьи", "беренесиз", "no category", "kalemsiz", "moddasiz")
                        ? ""
                        : string.Join(" · ", parts.Select(p => $"{p.Title} {MoneyText.Som((double)p.Amount)}"));
                // 2026-10-23 (аудит, круг 8, В-7): внесения/изъятия — из отчёта сервера, если он пришёл (как в ShiftDetailsDialog).
                var (deposits, withdrawals) = _report is { } depReport ? (depReport.Deposits, depReport.Withdrawals) : ShiftCashOperationsStore.SumsForShift(shiftId);
                if (deposits > 0.005m)
                    rows.Add(new ShiftOpRow(Tr.T("Внесения", "Акча салуу", "Cash in", "Para girişi", "Kirim"), "+" + MoneyText.Som(deposits),
                        ByArticle(ShiftCashOperationsStore.ArticleSumsForShift(shiftId, withdrawal: false))));
                if (withdrawals > 0.005m)
                    rows.Add(new ShiftOpRow(Tr.T("Изъятия", "Акча алуу", "Cash out", "Para çıkışı", "Chiqim"), "−" + MoneyText.Som(withdrawals),
                        ByArticle(ShiftCashOperationsStore.ArticleSumsForShift(shiftId, withdrawal: true))));
            }

            ShiftOpsList.ItemsSource = rows;
            ShiftOpsBorder.IsVisible = rows.Count > 0 && !ExpectedHidden;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно закрытия смены: операции смены не показаны: {ex.Message}", "SHIFT");
            ShiftOpsBorder.IsVisible = false;
        }
        RefreshDrawerBreakdown();
    }

    /// <summary>2026-10-23 (аудит, круг 7): раскладка «в ящике должно быть» (как в X-отчёте). Нужны начальная сумма и «по системе»;
    /// при слепом пересчёте — только после «Пересчитал».</summary>
    private void RefreshDrawerBreakdown()
    {
        if (DrawerBreakdownBorder is null)
            return;
        try
        {
            var shiftId = NurMarketKassa.PosApp.ActiveShiftId;
            if (ExpectedHidden || string.IsNullOrWhiteSpace(shiftId) || Totals?.OpeningCash is not { } opening || SuggestedBalance is not { } expected)
            {
                DrawerBreakdownBorder.IsVisible = false;
                return;
            }
            var totals = ShiftEventsStore.TotalsForShift(shiftId);
            // 2026-10-23 (аудит, круг 8, В-7): те же источники, что в X-отчёте (ShiftDetailsDialog): отчёт сервера, а без него — журнал кассы.
            // Возвраты: ReturnsTotal сервера пока 0 всегда (у возвратов нет смены) — тогда берём журнал возвратов кассы, как было.
            var debtCash = _report?.DebtPaymentsCash ?? (totals.TryGetValue(ShiftEventsStore.KindDebtPayment, out var dc) ? (decimal)dc : 0m);
            var returns = _report is { ReturnsTotal: > 0m } withReturns
                ? withReturns.ReturnsTotal
                : (decimal)ShiftEventsStore.ListForShift(shiftId, ShiftEventsStore.KindReturn).Sum(r => r.Amount);
            var (deposits, withdrawals) = _report is { } depReport ? (depReport.Deposits, depReport.Withdrawals) : ShiftCashOperationsStore.SumsForShift(shiftId);
            DrawerBreakdownBuilder.Fill(this, DrawerBreakdown, opening, Totals.CashSales ?? 0m, debtCash, deposits, withdrawals, returns, expected);
            DrawerBreakdownBorder.IsVisible = true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Окно закрытия смены: раскладка ящика не показана: {ex.Message}", "SHIFT");
            DrawerBreakdownBorder.IsVisible = false;
        }
    }

    internal static double? ParseNote(string? note, string key) // 2026-10-22 (аудит 1.17.59, П-Д): нужен и X-отчёту (ShiftDetailsDialog)
    {
        if (string.IsNullOrWhiteSpace(note))
            return null;
        foreach (var part in note.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && string.Equals(kv[0].Trim(), key, StringComparison.Ordinal)
                && double.TryParse(kv[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
                return value;
        }
        return null;
    }

    private sealed record ShiftOpRow(string Label, string Value, string Detail)
    {
        public bool HasDetail => Detail.Length > 0;
    }

    /// <summary>2026-09-12, по просьбе пользователя (сравнение со скриншотами веб-версии) —
    /// живой расчёт расхождения фактической суммы с ожидаемой (как на сайте): отрицательное —
    /// недостача, положительное — излишек.</summary>
    private void ClosingCashBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        // 2026-10-15: любой текст, кроме подставленного кассой, — ввод кассира (флаг не сбрасывается).
        if (!string.Equals(ClosingCashBox.Text ?? "", _programmaticClosingCash ?? "", StringComparison.Ordinal))
            _closingCashEdited = true;
        SectionCards.ShowError(ClosingCashError, null);
        UpdateDiscrepancy();
    }

    private void SetClosingCashProgrammatically(decimal value)
    {
        _programmaticClosingCash = value.ToString("0.00", CultureInfo.InvariantCulture);
        ClosingCashBox.Text = _programmaticClosingCash;
    }

    /// <summary>2026-09-12: диалог открывается СРАЗУ с уже известным (возможно чуть устаревшим)
    /// балансом — вызывающий код (MainWindow.CloseShiftAsync) в фоне подтягивает свежий с
    /// сервера и вызывает этот метод, когда он придёт, не блокируя сам клик "Закрыть смену".
    /// Если кассир уже начал вручную менять "Фактический остаток" — не перезаписываем его ввод,
    /// только саму строку "Остаток по системе" и расхождение относительно неё.</summary>
    public void UpdateSystemBalance(decimal balance)
    {
        var previousSuggested = SuggestedBalance;
        SuggestedBalance = balance;
        SystemBalanceText.Text = ShiftBalanceHelper.FormatBalance(balance);
        UpdateExpectedHint();
        RefreshDrawerBreakdown(); // 2026-10-23

        // 2026-10-15, ревью: раньше поле перезаписывалось, если в нём всё ещё прежняя подсказка, — даже когда кассир
        // уже пересчитал ящик и сам ввёл/подтвердил это же число. Теперь только пока кассир поле не трогал.
        // 2026-10-16: при слепом пересчёте поле не заполняем никогда.
        var stillDefault = previousSuggested.HasValue
            && !_blindCount
            && !_closingCashEdited
            && CashAmountInput.TryParse(ClosingCashBox.Text, out var current)
            && current == previousSuggested.Value;
        if (stillDefault)
            SetClosingCashProgrammatically(balance);

        UpdateDiscrepancy();
    }

    /// <summary>2026-10-21 (пакет 6, хвост К16): подсказка «Ожидается N сом» в пустом поле и кнопка «= по системе»
    /// (только без слепого пересчёта — там ожидаемое скрыто до «Пересчитал»).</summary>
    private void UpdateExpectedHint()
    {
        if (ExpectedHintText is null || UseExpectedButton is null)
            return;
        var show = !_blindCount && SuggestedBalance.HasValue;
        UseExpectedButton.IsVisible = show;
        ExpectedHintText.IsVisible = show;
        if (!show)
        {
            ClosingCashBox.Watermark = Tr.T("Сумма в ящике", "Кутудагы сумма", "Amount in the drawer", "Çekmecedeki tutar", "Tortmadagi summa");
            return;
        }
        var expected = MoneyText.Som(SuggestedBalance!.Value);
        ClosingCashBox.Watermark = Tr.T($"Ожидается {expected}", $"Күтүлүүдө {expected}", $"Expected {expected}", $"Beklenen {expected}", $"Kutilmoqda {expected}");
        UseExpectedButton.Content = Tr.T("= по системе", "= система боюнча", "= as system", "= sisteme göre", "= tizim bo'yicha");
        ExpectedHintText.Text = Tr.T("Пересчитайте наличные в ящике и введите сумму. Сошлось с системой — нажмите «= по системе».",
            "Кутудагы накталай акчаны санап, сумманы киргизиңиз. Система менен дал келсе — «= система боюнча» басыңыз.",
            "Count the cash in the drawer and enter the amount. If it matches the system — press “= as system”.",
            "Çekmecedeki nakdi sayıp tutarı girin. Sistemle tutuyorsa «= sisteme göre»ye basın.",
            "Tortmadagi naqd pulni sanab, summani kiriting. Tizim bilan mos kelsa — «= tizim bo'yicha»ni bosing.");
    }

    /// <summary>2026-10-21: «= по системе» — сумма по системе в поле (ввод кассира: фоновое обновление её не перепишет).</summary>
    private void UseExpected_Click(object? sender, RoutedEventArgs e)
    {
        if (SuggestedBalance is not { } expected || _blindCount)
            return;
        ClosingCashBox.Text = expected.ToString("0.00", CultureInfo.InvariantCulture);
        ClosingCashBox.Focus();
        ClosingCashBox.CaretIndex = ClosingCashBox.Text.Length;
    }

    private void UpdateDiscrepancy()
    {
        if (DiscrepancyBorder is null || DiscrepancyText is null)
            return;

        // 2026-10-16: слепой пересчёт — расхождение только после «Пересчитал».
        if (ExpectedHidden || !SuggestedBalance.HasValue || ClosingCash is not { } actual)
        {
            DiscrepancyBorder.IsVisible = false;
            return;
        }

        var discrepancy = actual - SuggestedBalance.Value;
        DiscrepancyBorder.IsVisible = true;
        // 2026-10-19, К1: «+1 234,50 сом» (MoneyText); само расхождение прежнее.
        DiscrepancyText.Text = (discrepancy >= 0 ? "+" : "") + MoneyText.Som(discrepancy);

        var (background, foreground) = discrepancy switch
        {
            0m => (ThemeBrush("BrushSuccessSoft", "BrushPanelSoft"), ThemeBrush("BrushSuccess", "BrushText")),
            _ => (ThemeBrush("BrushDangerSoft", "BrushPanelSoft"), ThemeBrush("BrushDanger", "BrushText")),
        };
        DiscrepancyBorder.Background = background;
        DiscrepancyText.Foreground = foreground;
    }

    private IBrush ThemeBrush(string key, string fallbackKey) =>
        Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true && value is IBrush brush
            ? brush
            : Application.Current?.TryFindResource(fallbackKey, ActualThemeVariant, out var fallback) == true && fallback is IBrush fallbackBrush
                ? fallbackBrush
                : Brushes.Gray;

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (!CashAmountInput.TryParse(ClosingCashBox.Text, out var amount) || amount < 0)
        {
            // 2026-10-22 (модалки, пакет 2): ошибка под полем с фокусом в нём (было всплывающее окно поверх).
            SectionCards.ShowError(ClosingCashError,
                Tr.T("Введите фактическую сумму наличных. Если наличных нет — введите 0.", "Нак акчанын чыныгы суммасын киргизиңиз. Нак акча жок болсо — 0 киргизиңиз.", "Enter the counted cash amount. Enter 0 if there is no cash.",
                    "Sayılmış nakit tutarını girin. Nakit yoksa 0 girin.", "Hisoblangan naqd pulni kiriting. Naqd pul bo'lmasa 0 kiriting."),
                ClosingCashBox);
            return;
        }
        SectionCards.ShowError(ClosingCashError, null);

        // 2026-10-16: слепой пересчёт — первое нажатие только показывает ожидаемое и расхождение,
        // введённая сумма закрепляется (иначе её можно было бы «подогнать» под показанную).
        if (ExpectedHidden)
        {
            _blindRevealed = true;
            _blindRevealedAtTicks = Environment.TickCount64;
            _awaitEnterRelease = _enterDown;
            ClosingCashBox.IsReadOnly = true;
            // 2026-10-17, ревью: первый пересчёт смены запоминается до закрытия смены — если окно закроют
            // отменой и откроют снова, кассир и журнал увидят, что было введено в первый раз.
            if (ReadFirstAttempt() is null)
                SaveFirstAttempt(amount, SuggestedBalance);
            ApplyBlindState();
            ApplyTotals(Totals);
            UpdateDiscrepancy();
            PosLogger.Log($"Слепой пересчёт при закрытии смены: введено {amount:0.00}, по системе "
                + (SuggestedBalance is { } expected ? $"{expected:0.00}, расхождение {amount - expected:+0.00;-0.00;0.00}" : "неизвестно"), "SHIFT");
            return;
        }
        if (_blindRevealed && (_awaitEnterRelease || Environment.TickCount64 - _blindRevealedAtTicks < 700))
            return;

        _confirmedClosingCash = amount;
        if (_blindCount && !string.IsNullOrEmpty(UserPreferences.Instance.BlindCountFirstAttempt))
        {
            PosLogger.Log($"Слепой пересчёт: смена закрывается с суммой {amount:0.00}"
                + (_firstAttempt is { } a ? $", первый пересчёт был {a.Counted:0.00}" : ""), "SHIFT");
            UserPreferences.Instance.BlindCountFirstAttempt = null;
            UserPreferences.Instance.SaveToDisk();
        }
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
}
