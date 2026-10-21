using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Зарплата сотрудников за период (2026-09-25, «вот веб новая функция зарплата»).
/// Расчёт целиком серверный — тот же, что на сайте в карточке сотрудника и в аналитике
/// (api/main/analytics/market/?tab=salary): оклад пропорционально дням периода, процент с
/// оплаченных чеков, где сотрудник пробил оплату, и комиссия консультанта с чеков, где он указан
/// в окне оплаты. Касса только показывает его и даёт поменять схему начисления сотрудника.</summary>
public partial class SalaryWindow : Window, IOwnerSection
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private DateTime _from;
    private DateTime _to;
    private CancellationTokenSource? _cts;
    // 2026-10-19, четвёртое ревью (п.3, п.9): последний расчёт «Выплачено» и сотрудники таблицы — окно выплаты пересчитывает
    // тем же вызовом (все сотрудники периода) и тем же правилом доверия.
    private SalaryPayoutService.PaidResult? _paidResult;
    private bool _paidTimedOut;
    private List<(string UserId, string Name)> _paidEmployees = new();
    private bool _suppressPickerEvents;

    public SalaryWindow()
    {
        InitializeComponent();
        var today = DateTime.Today;
        _from = new DateTime(today.Year, today.Month, 1);
        _to = today;
        EscapeKey.Attach(this);
    }

    public static void Open(Window? owner)
    {
        var window = new SalaryWindow();
        if (owner != null)
            window.Show(owner);
        else
            window.Show();
    }

    private async void Window_Loaded(object? sender, RoutedEventArgs e)
    {
        ApplyTexts();
        SyncPickers();
        await ReloadAsync();
    }

    private void ApplyTexts()
    {
        Title = Tr.T("Зарплата", "Эмгек акы", "Salary", "Maaş", "Ish haqi");
        TitleText.Text = Title;
        SubtitleText.Text = Tr.T(
            "Расчёт делает сервер — те же цифры, что на сайте в карточке сотрудника.",
            "Эсепти сервер жасайт — сайттагы кызматкердин карточкасындагыдай эле сандар.",
            "Calculated by the server — the same figures as on the employee's page on the website.",
            "Hesaplamayı sunucu yapar — rakamlar sitedeki çalışan kartındakilerle aynıdır.",
            "Hisob-kitobni server bajaradi — raqamlar saytdagi xodim kartochkasidagi bilan bir xil.");
        RefreshButton.Content = Tr.T("Обновить", "Жаңылоо", "Refresh", "Yenile", "Yangilash");
        // 2026-10-19, этап О7: пояснение «Как считается» — по кнопке (раньше жёлтая плашка всегда занимала 90 px).
        GuideButton.Content = Tr.T("Как считается?", "Кантип эсептелет?", "How is it calculated?", "Nasıl hesaplanır?", "Qanday hisoblanadi?");
        AutomationProperties.SetName(GuideButton, GuideButton.Content as string);
        CloseButton.Content = Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish");
        ThisMonthButton.Content = Tr.T("Текущий месяц", "Ушул ай", "This month", "Bu ay", "Joriy oy");
        LastMonthButton.Content = Tr.T("Прошлый месяц", "Өткөн ай", "Last month", "Geçen ay", "O'tgan oy");
        Last7Button.Content = Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun");

        GuideTitle.Text = Tr.T("Как считается зарплата", "Эмгек акы кантип эсептелет", "How the salary is calculated",
            "Maaş nasıl hesaplanır", "Ish haqi qanday hisoblanadi");
        GuideStep1.Text = Tr.T(
            "1. У каждого сотрудника своя схема: оклад, процент от продаж, оплата за каждый проданный товар или их сочетание (кнопка «Схема» в строке).",
            "1. Ар бир кызматкердин өз схемасы бар: айлык, сатуудан пайыз, ар бир сатылган товар үчүн төлөм же алардын айкалышы (саптагы «Схема» баскычы).",
            "1. Each employee has their own scheme: fixed salary, percentage of sales, pay per item sold, or a combination (the “Scheme” button in the row).",
            "1. Her çalışanın kendi şeması vardır: sabit maaş, satış yüzdesi, satılan ürün başına ödeme veya bunların birleşimi (satırdaki «Şema» düğmesi).",
            "1. Har bir xodimning o'z sxemasi bor: maosh, sotuvdan foiz, har bir sotilgan mahsulot uchun to'lov yoki ularning birikmasi (qatordagi «Sxema» tugmasi).");
        GuideStep2.Text = Tr.T(
            "2. В расчёт идут только оплаченные чеки, где сотрудник пробил оплату, — и чеки, где он указан консультантом.",
            "2. Эсепке кызматкер төлөмүн өткөргөн төлөнгөн чектер жана ал консультант катары көрсөтүлгөн чектер гана кирет.",
            "2. Only paid receipts count: those where the employee took the payment, and those where they are listed as the consultant.",
            "2. Hesaba yalnızca çalışanın ödemesini aldığı ödenmiş fişler ile danışman olarak belirtildiği fişler girer.",
            "2. Hisobga faqat xodim to'lovni o'tkazgan to'langan cheklar hamda u maslahatchi sifatida ko'rsatilgan cheklar kiradi.");
        GuideStep3.Text = Tr.T(
            "3. Оклад считается пропорционально дням выбранного периода.",
            "3. Айлык тандалган мезгилдин күндөрүнө жараша эсептелет.",
            "3. The fixed salary is prorated by the days in the selected period.",
            "3. Sabit maaş, seçilen dönemin gün sayısına göre orantılı hesaplanır.",
            "3. Maosh tanlangan davr kunlariga mutanosib ravishda hisoblanadi.");

        // 2026-10-20, живой аудит владельца: «К выплате» показывало начисленное, не вычитая выплаченное (Nur Test 70 666,67 при
        // выплаченных 50 000) — риск выплатить дважды. Теперь «Начислено», под ним и в строке — «Выплачено · Осталось».
        CardPayrollLabel.Text = Tr.T("Начислено за период", "Мезгил үчүн эсептелди", "Accrued for the period", "Dönem için tahakkuk", "Davr uchun hisoblangan");
        CardBaseLabel.Text = Tr.T("Оклады за период", "Мезгил үчүн айлыктар", "Salaries for the period", "Dönemin sabit maaşları", "Davr uchun maoshlar");
        CardBonusLabel.Text = Tr.T("Проценты, комиссии, за товары", "Пайыздар, комиссиялар, товарлар үчүн", "Percentages, commissions, per item", "Yüzdeler, komisyonlar, ürün başına", "Foizlar, komissiyalar, mahsulot uchun");
        CardSalesLabel.Text = Tr.T("Продажи сотрудников", "Кызматкерлердин сатуулары", "Employee sales", "Personel satışları", "Xodimlar sotuvlari");
        CardEmployeesLabel.Text = Tr.T("Сотрудников со схемой", "Схемасы бар кызматкерлер", "Employees with a scheme", "Şeması olan çalışanlar", "Sxemasi bor xodimlar");

        // 2026-10-19, этап О7: колонки сведены — «Сотрудник и схема», «Продажи» (кассир; консультант второй строкой),
        // «Бонус и комиссия» (комиссия второй строкой). Числа те же.
        HeadEmployee.Text = Tr.T("Сотрудник и схема", "Кызматкер жана схема", "Employee and scheme", "Çalışan ve şema", "Xodim va sxema");
        HeadBase.Text = Tr.T("Оклад за период", "Мезгил үчүн айлык", "Salary for the period", "Dönem sabit maaşı", "Davr uchun maosh");
        HeadCashier.Text = Tr.T("Продажи", "Сатуулар", "Sales", "Satışlar", "Sotuvlar");
        HeadBonus.Text = Tr.T("Бонус и комиссия", "Бонус жана комиссия", "Bonus and commission", "Bonus ve komisyon", "Bonus va komissiya");
        HeadTotal.Text = Tr.T("Начислено, сом", "Эсептелди, сом", "Accrued, som", "Tahakkuk, som", "Hisoblangan, so'm");
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e)
    {
        // 2026-10-19, третье ревью (п.2): «Обновить» всегда перечитывает выплаты с сервера (кэш 2 мин — только для
        // переключения разделов и повторного открытия окна).
        SalaryPayoutService.ForgetCache();
        await ReloadAsync();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>2026-10-19, этап О7: «Как считается?» — показать/скрыть пояснение.</summary>
    private void Guide_Click(object? sender, RoutedEventArgs e) => GuideBox.IsVisible = !GuideBox.IsVisible;

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): крупное название уже
    /// стоит над разделом, «Закрыть» не нужна. Пояснение остаётся мелкой строкой слева, кнопки —
    /// справа в той же строке.</summary>
    public void AsOwnerSection()
    {
        TitleText.IsVisible = false;
        CloseButton.IsVisible = false;
        SubtitleText.Margin = new Avalonia.Thickness(0, 0, 16, 0);
        if (SubtitleText.Parent is Control subtitlePanel)
            subtitlePanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        RootGrid.Margin = OwnerSectionLayout.Margin;
    }

    // 2026-10-20, проверка фильтров: нажатый сегмент. 7-го числа «Текущий месяц» и «7 дней» — одни и те же даты, и подсвечивались
    // оба; теперь подсвечен тот, который нажали (по датам — только когда даты выбраны в полях вручную).
    private Button? _chosenPreset;

    private async void ThisMonth_Click(object? sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        _chosenPreset = ThisMonthButton;
        await SetPeriodAsync(PeriodRange.MonthFrom(today), today);
    }

    private async void LastMonth_Click(object? sender, RoutedEventArgs e)
    {
        var firstThisMonth = PeriodRange.MonthFrom(DateTime.Today);
        _chosenPreset = LastMonthButton;
        await SetPeriodAsync(firstThisMonth.AddMonths(-1), firstThisMonth.AddDays(-1));
    }

    private async void Last7_Click(object? sender, RoutedEventArgs e)
    {
        _chosenPreset = Last7Button;
        await SetPeriodAsync(PeriodRange.WeekFrom(DateTime.Today), DateTime.Today);
    }

    private async void Period_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressPickerEvents)
            return;
        if (FromPicker.SelectedDate is { } from)
            _from = from.Date;
        if (ToPicker.SelectedDate is { } to)
            _to = to.Date;
        if (_from > _to)
            (_from, _to) = (_to, _from);
        _chosenPreset = null;
        SyncPickers();
        await ReloadAsync();
    }

    private async Task SetPeriodAsync(DateTime from, DateTime to)
    {
        _from = from;
        _to = to;
        SyncPickers();
        await ReloadAsync();
    }

    private void SyncPickers()
    {
        _suppressPickerEvents = true;
        FromPicker.SelectedDate = _from;
        ToPicker.SelectedDate = _to;
        _suppressPickerEvents = false;

        var days = (_to - _from).Days + 1;
        PeriodText.Text = Tr.T("период", "мезгил", "period", "dönem", "davr") + $": {days} " + Tr.T("дн.", "күн", "days", "gün", "kun")
            + " · " + PeriodRange.Label(_from, _to);

        var today = DateTime.Today;
        var firstThisMonth = PeriodRange.MonthFrom(today);
        var presets = new (Button Button, DateTime From, DateTime To)[]
        {
            (ThisMonthButton, firstThisMonth, today), (LastMonthButton, firstThisMonth.AddMonths(-1), firstThisMonth.AddDays(-1)),
            (Last7Button, PeriodRange.WeekFrom(today), today),
        };
        var matching = presets.Where(p => _from == p.From && _to == p.To).Select(p => p.Button).ToList();
        var chosen = _chosenPreset != null && matching.Contains(_chosenPreset) ? _chosenPreset : matching.FirstOrDefault();
        foreach (var p in presets)
            SetActive(p.Button, ReferenceEquals(p.Button, chosen));
    }

    private static void SetActive(Button button, bool active)
    {
        if (active && !button.Classes.Contains("active"))
            button.Classes.Add("active");
        else if (!active)
            button.Classes.Remove("active");
    }

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        LoadingBar.IsVisible = true;
        ShowError(null);
        // 2026-10-19, второе ревью (п.3): строки прошлого периода сразу убираются — их «Выплатить» не должна сработать
        // с суммой и периодом, которых уже нет на экране. Ошибка загрузки старые строки не возвращает.
        var from = _from;
        var to = _to;
        RowsList.ItemsSource = null;
        EmptyBox.IsVisible = false;
        PayoutWarningBox.IsVisible = false;
        ResetCards();
        try
        {
            var data = await App.SalesApi.MarketSalaryReportAsync(from, to, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Render(data, from, to);
            // 2026-10-19: «Выплачено · Остаток» по выплатам-расходам — отдельно, таблица уже на экране.
            _ = LoadPaidAsync(RowsList.ItemsSource as List<SalaryRow>, from, to, cts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException ex) when (ex.StatusCode is 401 or 403)
        {
            ResetCards(); // 2026-10-19, четвёртое ревью (п.7): прочерки, а не нули прошлого расчёта
            ShowError(Tr.T(
                "Нет доступа к зарплате: у этого аккаунта нет права на аналитику сотрудников на сайте.",
                "Эмгек акыга уруксат жок: бул аккаунттун сайтта кызматкерлердин аналитикасына укугу жок.",
                "No access to salaries: this account doesn't have the employee analytics permission on the website.",
                "Maaşlara erişim yok: bu hesabın sitede personel analizine yetkisi yok.",
                "Ish haqini ko'rishga ruxsat yo'q: bu akkauntda saytdagi xodimlar tahliliga huquq yo'q."));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Salary report failed: {ex}", "WARNING");
            ShowError(Tr.T("Не удалось загрузить зарплату", "Эмгек акыны жүктөө мүмкүн болгон жок", "Could not load salaries",
                "Maaşlar yüklenemedi", "Ish haqini yuklab bo'lmadi") + ": " + ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                LoadingBar.IsVisible = false;
        }
    }

    /// <summary>2026-10-19, четвёртое ревью (п.7): итоги прошлого периода не остаются на экране — прочерки до нового отчёта.</summary>
    private void ResetCards()
    {
        foreach (var card in new[] { CardPayrollValue, CardBaseValue, CardBonusValue, CardSalesValue, CardEmployeesValue })
            card.Text = "—";
        CardPayrollPaidText.IsVisible = false; // 2026-10-20
        _paidResult = null;
        _paidTimedOut = false;
        _paidEmployees = new();
    }

    private void Render(JsonElement data, DateTime from = default, DateTime to = default)
    {
        var cards = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("cards", out var c) ? c : default;
        CardPayrollValue.Text = Money(Num(cards, "total_payroll")) + " " + Som;
        // 2026-10-19, этап О7: «сом» у всех денежных плиток (было только у первой).
        CardBaseValue.Text = Money(Num(cards, "total_base_prorated")) + " " + Som;
        // 2026-10-05: проценты, комиссии и начисление за проданные товары (total_per_item_bonus).
        CardBonusValue.Text = Money(Num(cards, "total_percent_bonus") + Num(cards, "total_per_item_bonus")) + " " + Som;
        CardSalesValue.Text = Money(Num(cards, "total_employee_sales")) + " " + Som;
        CardEmployeesValue.Text = ((int)Num(cards, "employees_with_profile")).ToString(Ru);

        var rows = new List<SalaryRow>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in list.EnumerateArray())
            {
                // Строка помнит, за какой период посчитана (окно выплаты берёт период из строки).
                var row = SalaryRow.From(r);
                row.PeriodFrom = from;
                row.PeriodTo = to;
                rows.Add(row);
            }
        }

        rows = rows
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Name, StringComparer.Create(Ru, true))
            .ToList();
        RowsList.ItemsSource = rows;
        EmptyBox.IsVisible = rows.Count == 0;
        EmptyTitle.Text = Tr.T("Начислений за период нет", "Мезгил үчүн эсептөө жок", "No pay for this period",
            "Bu dönem için tahakkuk yok", "Davr uchun hisoblash yo'q");
        EmptyText.Text = Tr.T(
            "За этот период начислять некому: нет продаж и ни у кого не настроена схема зарплаты.",
            "Бул мезгилде эсептей турган эч ким жок: сатуу жок жана эч кимде эмгек акы схемасы жок.",
            "Nobody to pay for this period: no sales and no salary schemes set up.",
            "Bu dönemde maaş hesaplanacak kimse yok: satış yok ve hiç kimse için maaş şeması ayarlanmamış.",
            "Bu davr uchun hisoblanadigan hech kim yo'q: sotuvlar yo'q va hech kimga ish haqi sxemasi sozlanmagan.")
            + "\n" + Tr.T(
                "Выберите другой период или задайте схему сотруднику на сайте NurCRM (Сотрудники → карточка).",
                "Башка мезгилди тандаңыз же NurCRM сайтында кызматкерге схема коюңуз (Кызматкерлер → карточка).",
                "Pick another period or set a scheme for an employee on the NurCRM website (Employees → card).",
                "Başka bir dönem seçin veya NurCRM sitesinde çalışana şema atayın (Çalışanlar → kart).",
                "Boshqa davrni tanlang yoki NurCRM saytida xodimga sxema bering (Xodimlar → kartochka).");
    }

    /// <summary>2026-10-19: сколько уже выплачено сотрудникам за период — выплаты, чей период зарплаты пересекается с периодом
    /// на экране, с сервера и из журнала этого ПК (см. <see cref="SalaryPayoutService.PaidForPeriodAsync"/>). Пока не загружено —
    /// «Выплатить» недоступна; сервер прочитан частично или не прочитан — доступна, но окно выплаты предупредит и не подставит сумму.</summary>
    private async Task LoadPaidAsync(List<SalaryRow>? rows, DateTime from, DateTime to, CancellationTokenSource cts)
    {
        if (rows is not { Count: > 0 })
            return;
        SalaryPayoutService.PaidResult? result = null;
        var timedOut = false;
        // Третье ревью (п.5): общий таймаут 60 с; не успели — как «частично» (кнопки доступны, сумма не подставляется).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            result = await SalaryPayoutService.PaidForPeriodAsync(rows.Select(r => (r.UserId, r.Name)).ToList(), from, to, timeout.Token);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            timedOut = true;
            PosLogger.Log("Выплата зарплаты: проверка выплат не уложилась в 60 с.", "WARNING");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: «Выплачено» не посчитано: {ex.Message}", "WARNING");
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(RowsList.ItemsSource, rows))
            return;

        // Журнал выплат повреждён — выплаты закрыты (п.6).
        var broken = SalaryPayoutService.JournalBrokenText ?? SalaryPayoutService.CompanyUnknownText; // пятая проверка: и без компании
        if (broken is not null)
            ShowError(broken);
        // Расходы «Зарплата» без привязки к сотруднику — над таблицей (п.3).
        var unassigned = result is { UnassignedCount: > 0 } u ? SalaryPayoutService.UnassignedPlate(u.UnassignedSum, u.UnassignedCount) : null;
        PayoutWarningText.Text = unassigned ?? "";
        PayoutWarningBox.IsVisible = unassigned is not null;

        var coverage = timedOut ? SalaryPayoutService.PaidCoverage.Partial : result?.Coverage ?? SalaryPayoutService.PaidCoverage.None;
        _paidResult = result;
        _paidTimedOut = timedOut;
        _paidEmployees = rows.Select(r => (r.UserId, r.Name)).ToList();
        foreach (var row in rows)
        {
            var info = result is not null && result.Users.TryGetValue(row.UserId, out var p) ? p : null;
            // Четвёртое ревью (п.9): одно правило доверия — то же в окне выплаты и при пересчёте.
            var (sure, warning) = SalaryPayoutService.Trust(result, info, timedOut);
            row.ApplyPaid(info, coverage, sure, warning, blocked: broken is not null);
        }

        // 2026-10-20, аудит владельца: итог под «Начислено» — «Выплачено · Осталось»; выплаты проверены не полностью — так и пишем.
        var accrued = rows.Sum(r => r.Total);
        var paidAll = rows.Sum(r => r.Paid);
        var allSure = rows.All(r => SalaryPayoutService.Trust(result, result is not null && result.Users.TryGetValue(r.UserId, out var p) ? p : null, timedOut).Sure);
        var left = rows.Sum(r => Math.Max(0, r.Total - r.Paid));
        CardPayrollPaidText.Text = Tr.T($"Выплачено: {Money(paidAll)} · Осталось: {Money(left)} сом", $"Төлөндү: {Money(paidAll)} · Калды: {Money(left)} сом",
                $"Paid: {Money(paidAll)} · Left: {Money(left)} som", $"Ödendi: {Money(paidAll)} · Kalan: {Money(left)} som", $"To'landi: {Money(paidAll)} · Qoldi: {Money(left)} so'm")
            + (allSure ? "" : " " + Tr.T("(выплаты проверены не полностью)", "(төлөмдөр толук текшерилген жок)", "(payouts not fully checked)",
                "(ödemeler tam kontrol edilmedi)", "(to'lovlar to'liq tekshirilmagan)"));
        CardPayrollPaidText.IsVisible = accrued > 0.005 || paidAll > 0.005;
    }

    /// <summary>2026-10-19, решение владельца: «Выплатить» — расход «Зарплата» из кассы компании (без смены).</summary>
    private async void Pay_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SalaryRow row || string.IsNullOrEmpty(row.UserId) || !row.PayEnabled)
            return;
        try
        {
            // Период — из строки (за него посчитаны «К выплате» и «Выплачено»), а не текущий в полях дат.
            var employees = _paidEmployees;
            var (from, to) = (row.PeriodFrom, row.PeriodTo);
            var dialog = new SalaryPayoutDialog(row.UserId, row.Name, from, to, row.Total, _paidResult, _paidTimedOut,
                ct => SalaryPayoutService.PaidForPeriodAsync(employees, from, to, ct));
            await dialog.ShowDialog(this);
            // Второе ревью (п.8): итог — свойство окна, как бы его ни закрыли (кнопка, ✕, Esc, Alt+F4).
            if (dialog.PaidAny)
            {
                SalaryPayoutService.ForgetCache();
                await ReloadAsync();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Выплата зарплаты: окно не открылось: {ex}", "ERROR");
            ShowError(ex.Message);
        }
    }

    private async void EditScheme_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SalaryRow row)
            return;
        var saved = await new SalarySchemeDialog(row.UserId, row.Name).ShowDialog<bool>(this);
        if (!saved)
            return;
        // Процент консультанта в окне оплаты подставляется из этой же схемы — сбрасываем кэш.
        ConsultantDirectory.Forget();
        await ReloadAsync();
    }

    private void ShowError(string? message)
    {
        ErrorBox.IsVisible = !string.IsNullOrWhiteSpace(message);
        ErrorText.Text = message ?? "";
    }

    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    internal static string Money(double value) => value.ToString("N2", Ru);

    internal static double Num(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v))
            return 0;
        var text = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    internal static string Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>Строка таблицы — поля ответа tab=salary (rows[]).</summary>
    public sealed class SalaryRow : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public string UserId { get; init; } = "";
        public string Name { get; init; } = "";
        public string ProfileNote { get; init; } = "";
        public bool HasProfileNote => ProfileNote.Length > 0;
        public string SchemeLabel { get; init; } = "";
        public string SchemeDetails { get; init; } = "";
        public string BaseText { get; init; } = "";
        public string DaysText { get; init; } = "";
        public string CashierSalesText { get; init; } = "";
        public string CashierCountText { get; init; } = "";
        public string ConsultantSalesText { get; init; } = "";
        public string ConsultantCountText { get; init; } = "";
        public string CommissionText { get; init; } = "";
        public string BonusText { get; init; } = "";
        // 2026-10-19, этап О7 (вид): схема одной строкой под именем; консультант, комиссия и «за товары» — вторыми строками.
        public string SchemeLine => SchemeDetails.Length > 0 && SchemeDetails != SchemeLabel ? SchemeLabel + " · " + SchemeDetails : SchemeLabel;
        public string ConsultantLine { get; init; } = "";
        public bool HasConsultant => ConsultantLine.Length > 0;
        public string CommissionLine { get; init; } = "";
        public bool HasCommission => CommissionLine.Length > 0;
        public string BonusNote { get; init; } = "";
        public bool HasBonusNote => BonusNote.Length > 0;
        public string TotalText { get; init; } = "";
        public double Total { get; init; }
        public string EditText => Tr.T("Схема", "Схема", "Scheme", "Şema", "Sxema");
        // 2026-10-19: выплата зарплаты (расход из кассы компании) и уже выплаченное за период.
        public string PayText => Tr.T("Выплатить", "Төлөө", "Pay out", "Öde", "To'lash");
        /// <summary>Период, за который посчитана строка (второе ревью, п.3).</summary>
        public DateTime PeriodFrom { get; set; }
        public DateTime PeriodTo { get; set; }
        public double Paid { get; private set; }
        /// <summary>«Выплачено» загружено (с сервера или только журнал ПК) — до этого «Выплатить» недоступна.</summary>
        public bool PaidLoaded { get; private set; }
        /// <summary>Насколько полно прочитаны выплаты с сервера (None/Partial — окно выплаты предупредит).</summary>
        internal SalaryPayoutService.PaidCoverage Coverage { get; private set; }
        /// <summary>Чем окно выплаты предупредит (жёлтая плашка) и почему не подставит сумму; null — уверены.</summary>
        internal string? DialogWarning { get; private set; }
        /// <summary>Выплаты закрыты (журнал выплат повреждён).</summary>
        public bool PayBlocked { get; private set; }
        public bool PayEnabled => PaidLoaded && !PayBlocked;
        /// <summary>Третье ревью (п.5): пока проверяем — надпись в строке.</summary>
        public string PaidText { get; private set; } = Tr.T("Проверяем выплаты…", "Төлөмдөр текшерилүүдө…", "Checking payouts…",
            "Ödemeler kontrol ediliyor…", "To'lovlar tekshirilmoqda…");
        public bool HasPaid => PaidText.Length > 0;

        /// <summary>«Выплачено» приходит после строки — уведомления вместо пересоздания списка (второе ревью, п.9).</summary>
        internal void ApplyPaid(SalaryPayoutService.UserPaid? info, SalaryPayoutService.PaidCoverage coverage, bool sure, string? warning, bool blocked)
        {
            var paid = info?.Paid ?? 0;
            Paid = paid;
            Coverage = coverage;
            DialogWarning = warning;
            PayBlocked = blocked;
            PaidLoaded = true;
            var rest = Total - paid;
            var parts = new List<string>();
            if (paid > 0)
            {
                parts.Add(Tr.T("Выплачено", "Төлөндү", "Paid", "Ödendi", "To'landi") + ": " + Money(paid) + " · "
                    + (rest >= -0.005
                        ? Tr.T("Осталось", "Калды", "Left", "Kalan", "Qoldi") + ": " + Money(Math.Max(0, rest)) // 2026-10-20: как под «Начислено»
                        : Tr.T("Сверх начисленного", "Эсептелгенден ашык", "Over the accrued amount", "Hesaplananın üstünde", "Hisoblangandan ortiq") + ": " + Money(-rest))
                    + (SalaryPayoutService.CoverageNote(coverage) is { Length: > 0 } note ? " " + note : "")
                    + (info is { ByName: true } ? " " + SalaryPayoutService.ByNameNote : ""));
            }
            // Третье ревью (п.4): выплаты, лишь частично попавшие в период, — пометкой, без вычета.
            if (info is { PartialNotes.Count: > 0 })
                parts.AddRange(info.PartialNotes);
            // Четвёртое ревью (п.6): проверка неполная, а выплат не нашли — не пусто, а «не проверено».
            if (parts.Count == 0 && !sure)
                parts.Add(Tr.T("Выплачено: не проверено", "Төлөндү: текшерилген жок", "Paid: not checked", "Ödenen: kontrol edilmedi", "To'langan: tekshirilmagan"));
            PaidText = string.Join("\n", parts);
            foreach (var name in new[] { nameof(Paid), nameof(Coverage), nameof(PaidLoaded), nameof(PayBlocked), nameof(PayEnabled), nameof(PaidText), nameof(HasPaid) })
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        public static SalaryRow From(JsonElement r)
        {
            var scheme = Str(r, "pay_scheme");
            var monthly = Num(r, "monthly_base_salary");
            var percent = Num(r, "sales_percent");
            var scope = Str(r, "profile_scope");
            var cashierSales = r.TryGetProperty("cashier_sales_period", out _) ? Num(r, "cashier_sales_period") : Num(r, "employee_sales_period");
            var cashierCount = (int)Num(r, "cashier_sales_count");
            var consultantCount = (int)Num(r, "consultant_sales_count");
            var total = Num(r, "total");
            var consultantSales = Num(r, "consultant_sales_period");
            var commission = Num(r, "consultant_commission_period");
            var name = Str(r, "employee_label");

            var perItem = Num(r, "per_item_amount");
            var perItemText = $"{Money(perItem)} / " + Tr.T("шт", "даана", "pc", "adet", "dona");
            var details = scheme switch
            {
                "per_item" => perItemText,
                "salary_plus_per_item" => $"{Money(monthly)} / " + Tr.T("мес", "ай", "month", "ay", "oy") + " · " + perItemText,
                "percent_plus_per_item" => $"{percent.ToString("0.##", Ru)} % · " + perItemText,
                "percent" => $"{percent.ToString("0.##", Ru)} %",
                "salary_plus_percent" => $"{Money(monthly)} / " + Tr.T("мес", "ай", "month", "ay", "oy") + $" · {percent.ToString("0.##", Ru)} %",
                _ => $"{Money(monthly)} / " + Tr.T("мес", "ай", "month", "ay", "oy"),
            };

            return new SalaryRow
            {
                UserId = Str(r, "user_id"),
                Name = name.Length > 0 ? name : "—",
                ProfileNote = scope == "none"
                    ? Tr.T("схема не настроена", "схема жөндөлгөн эмес", "no scheme set up", "şema ayarlanmamış", "sxema sozlanmagan")
                    : "",
                SchemeLabel = SchemeName(scheme, Str(r, "pay_scheme_label")),
                SchemeDetails = details,
                BaseText = Money(Num(r, "base_prorated")),
                DaysText = $"{(int)Num(r, "period_days")} " + Tr.T("дн.", "күн", "days", "gün", "kun"),
                CashierSalesText = Money(cashierSales),
                CashierCountText = Checks(cashierCount),
                ConsultantSalesText = Money(consultantSales),
                ConsultantCountText = Checks(consultantCount),
                CommissionText = Money(commission),
                // 2026-10-19, этап О7: консультант и комиссия — второй строкой, только если были (было — отдельные колонки с нулями).
                ConsultantLine = consultantSales > 0 || consultantCount > 0
                    ? Tr.T("консультант", "консультант", "consultant", "danışman", "maslahatchi") + $": {Money(consultantSales)} · {Checks(consultantCount)}"
                    : "",
                CommissionLine = commission > 0
                    ? Tr.T("комиссия консультанта", "консультанттын комиссиясы", "consultant commission", "danışman komisyonu", "maslahatchi komissiyasi") + $": {Money(commission)}"
                    : "",
                // 2026-10-05: + начисление за проданные товары (сколько штук). 2026-10-19, О7: подробность — второй строкой.
                BonusText = Money(Num(r, "percent_bonus") + Num(r, "per_item_bonus")),
                BonusNote = Num(r, "per_item_bonus") is > 0 and var itemBonus
                    ? $"{Tr.T("за товары", "товарлар үчүн", "items", "ürünler", "mahsulotlar")}: {Money(itemBonus)} · {Num(r, "items_sold_period").ToString("0.##", Ru)} {Tr.T("шт", "даана", "pcs", "adet", "dona")}"
                    : "",
                TotalText = Money(total),
                Total = total,
            };
        }

        // 2026-10-20, аудит программы владельца: «243 чеков», «1 чеков» — падеж по числу (ru: 1 чек / 2–4 чека / 5 чеков;
        // en: 1 receipt / receipts; ky, tr, uz — слово после числа не меняется).
        private static string Checks(int count)
        {
            var ru = count % 10 == 1 && count % 100 != 11 ? "чек"
                : count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14 ? "чека" : "чеков";
            return $"{count} " + Tr.T(ru, "чек", count == 1 ? "receipt" : "receipts", "fiş", "chek");
        }
    }

    /// <summary>Название схемы на языке кассы; сайт отдаёт подпись только по-русски.</summary>
    internal static string SchemeName(string scheme, string serverLabel = "") => scheme switch
    {
        "salary" => Tr.T("Оклад", "Айлык", "Salary", "Sabit maaş", "Maosh"),
        "percent" => Tr.T("Процент от продаж", "Сатуудан пайыз", "Sales percentage", "Satış yüzdesi", "Sotuvdan foiz"),
        "salary_plus_percent" => Tr.T("Оклад + процент от продаж", "Айлык + сатуудан пайыз", "Salary + sales percentage",
            "Sabit maaş + satış yüzdesi", "Maosh + sotuvdan foiz"),
        // 2026-10-05: оплата за каждый проданный товар (ТЗ ч.13, п. 3).
        "per_item" => Tr.T("За каждый проданный товар", "Ар бир сатылган товар үчүн", "Per item sold", "Satılan ürün başına", "Har bir sotilgan mahsulot uchun"),
        "salary_plus_per_item" => Tr.T("Оклад + за каждый товар", "Айлык + ар бир товар үчүн", "Salary + per item", "Sabit maaş + ürün başına", "Maosh + har bir mahsulot uchun"),
        "percent_plus_per_item" => Tr.T("Процент + за каждый товар", "Пайыз + ар бир товар үчүн", "Percentage + per item", "Yüzde + ürün başına", "Foiz + har bir mahsulot uchun"),
        _ => serverLabel.Length > 0 ? serverLabel : "—",
    };
}
