using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
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

        CardPayrollLabel.Text = Tr.T("К выплате за период", "Мезгил үчүн төлөөгө", "Payable for the period", "Dönem için ödenecek", "Davr uchun to'lanadigan summa");
        CardBaseLabel.Text = Tr.T("Оклады за период", "Мезгил үчүн айлыктар", "Salaries for the period", "Dönemin sabit maaşları", "Davr uchun maoshlar");
        CardBonusLabel.Text = Tr.T("Проценты, комиссии, за товары", "Пайыздар, комиссиялар, товарлар үчүн", "Percentages, commissions, per item", "Yüzdeler, komisyonlar, ürün başına", "Foizlar, komissiyalar, mahsulot uchun");
        CardSalesLabel.Text = Tr.T("Продажи сотрудников", "Кызматкерлердин сатуулары", "Employee sales", "Personel satışları", "Xodimlar sotuvlari");
        CardEmployeesLabel.Text = Tr.T("Сотрудников со схемой", "Схемасы бар кызматкерлер", "Employees with a scheme", "Şeması olan çalışanlar", "Sxemasi bor xodimlar");

        HeadEmployee.Text = Tr.T("Сотрудник", "Кызматкер", "Employee", "Çalışan", "Xodim");
        HeadScheme.Text = Tr.T("Схема", "Схема", "Scheme", "Şema", "Sxema");
        HeadBase.Text = Tr.T("Оклад за период", "Мезгил үчүн айлык", "Salary for the period", "Dönem sabit maaşı", "Davr uchun maosh");
        HeadCashier.Text = Tr.T("Продажи как кассир", "Кассир катары сатуу", "Sales as cashier", "Kasiyer olarak satış", "Kassir sifatida sotuvlar");
        HeadConsultant.Text = Tr.T("Продажи как консультант", "Консультант катары сатуу", "Sales as consultant", "Danışman olarak satış", "Maslahatchi sifatida sotuvlar");
        HeadCommission.Text = Tr.T("Комиссия консультанта", "Консультанттын комиссиясы", "Consultant commission", "Danışman komisyonu", "Maslahatchi komissiyasi");
        HeadBonus.Text = Tr.T("Бонус (% и за товары)", "Бонус (% жана товарлар)", "Bonus (% and per item)", "Bonus (% ve ürün başına)", "Bonus (% va mahsulot)");
        HeadTotal.Text = Tr.T("К выплате", "Төлөөгө", "Payable", "Ödenecek", "To'lanadigan summa");
    }

    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await ReloadAsync();

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    /// <summary>Раздел программы владельца (см. <see cref="IOwnerSection"/>): крупное название уже
    /// стоит над разделом, «Закрыть» не нужна. Пояснение остаётся мелкой строкой слева, кнопки —
    /// справа в той же строке.</summary>
    public void AsOwnerSection()
    {
        TitleText.IsVisible = false;
        CloseButton.IsVisible = false;
        RefreshButton.Margin = new Avalonia.Thickness(0);
        SubtitleText.Margin = new Avalonia.Thickness(0, 0, 16, 0);
        if (SubtitleText.Parent is Control subtitlePanel)
            subtitlePanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        RootGrid.Margin = OwnerSectionLayout.Margin;
    }

    private async void ThisMonth_Click(object? sender, RoutedEventArgs e)
    {
        var today = DateTime.Today;
        await SetPeriodAsync(new DateTime(today.Year, today.Month, 1), today);
    }

    private async void LastMonth_Click(object? sender, RoutedEventArgs e)
    {
        var firstThisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        await SetPeriodAsync(firstThisMonth.AddMonths(-1), firstThisMonth.AddDays(-1));
    }

    private async void Last7_Click(object? sender, RoutedEventArgs e) =>
        await SetPeriodAsync(DateTime.Today.AddDays(-6), DateTime.Today);

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
        PeriodText.Text = Tr.T("период", "мезгил", "period", "dönem", "davr") + $": {days} " + Tr.T("дн.", "күн", "days", "gün", "kun");

        var today = DateTime.Today;
        var firstThisMonth = new DateTime(today.Year, today.Month, 1);
        SetActive(ThisMonthButton, _from == firstThisMonth && _to == today);
        SetActive(LastMonthButton, _from == firstThisMonth.AddMonths(-1) && _to == firstThisMonth.AddDays(-1));
        SetActive(Last7Button, _from == today.AddDays(-6) && _to == today);
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
        try
        {
            var data = await App.SalesApi.MarketSalaryReportAsync(_from, _to, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Render(data);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ApiException ex) when (ex.StatusCode is 401 or 403)
        {
            Render(default);
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

    private void Render(JsonElement data)
    {
        var cards = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("cards", out var c) ? c : default;
        CardPayrollValue.Text = Money(Num(cards, "total_payroll")) + " " + Som;
        CardBaseValue.Text = Money(Num(cards, "total_base_prorated"));
        // 2026-10-05: проценты, комиссии и начисление за проданные товары (total_per_item_bonus).
        CardBonusValue.Text = Money(Num(cards, "total_percent_bonus") + Num(cards, "total_per_item_bonus"));
        CardSalesValue.Text = Money(Num(cards, "total_employee_sales"));
        CardEmployeesValue.Text = ((int)Num(cards, "employees_with_profile")).ToString(Ru);

        var rows = new List<SalaryRow>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in list.EnumerateArray())
                rows.Add(SalaryRow.From(r));
        }

        rows = rows
            .OrderByDescending(r => r.Total)
            .ThenBy(r => r.Name, StringComparer.Create(Ru, true))
            .ToList();
        RowsList.ItemsSource = rows;
        EmptyText.IsVisible = rows.Count == 0;
        EmptyText.Text = Tr.T(
            "За этот период начислять некому: нет продаж и ни у кого не настроена схема зарплаты.",
            "Бул мезгилде эсептей турган эч ким жок: сатуу жок жана эч кимде эмгек акы схемасы жок.",
            "Nobody to pay for this period: no sales and no salary schemes set up.",
            "Bu dönemde maaş hesaplanacak kimse yok: satış yok ve hiç kimse için maaş şeması ayarlanmamış.",
            "Bu davr uchun hisoblanadigan hech kim yo'q: sotuvlar yo'q va hech kimga ish haqi sxemasi sozlanmagan.");
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
    public sealed class SalaryRow
    {
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
        public string TotalText { get; init; } = "";
        public double Total { get; init; }
        public string EditText => Tr.T("Схема", "Схема", "Scheme", "Şema", "Sxema");

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
                ConsultantSalesText = Money(Num(r, "consultant_sales_period")),
                ConsultantCountText = Checks(consultantCount),
                CommissionText = Money(Num(r, "consultant_commission_period")),
                // 2026-10-05: + начисление за проданные товары (сколько штук — в скобках).
                BonusText = Num(r, "per_item_bonus") is > 0 and var itemBonus
                    ? $"{Money(Num(r, "percent_bonus") + itemBonus)} ({Tr.T("за товары", "товарлар үчүн", "items", "ürünler", "mahsulotlar")}: {Money(itemBonus)}, {Num(r, "items_sold_period").ToString("0.##", Ru)} {Tr.T("шт", "даана", "pcs", "adet", "dona")})"
                    : Money(Num(r, "percent_bonus")),
                TotalText = Money(total),
                Total = total,
            };
        }

        private static string Checks(int count) =>
            $"{count} " + Tr.T("чеков", "чек", "receipts", "fiş", "chek");
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
