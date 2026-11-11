using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-10 (владелец: «и фильтр в возврате!», «функцию возврата добавь в продажи … поиск по товару и фильтры доступны и в «Продажах»»):
/// одна панель фильтров чеков для окна возврата и «Продаж» поверх общего <see cref="SaleListFilter"/>. Период (в «Продажах» он свой — скрыт),
/// способ оплаты, статус, кассир, клиент, сумма от–до, «Сбросить» и счётчик «Найдено N». Изменение — событие <see cref="Changed"/>; смена периода —
/// <see cref="PeriodChanged"/> (окно возврата перечитывает чеки с сервера за период). Все надписи — на 5 языках, у полей — имена для экранного диктора.</summary>
public sealed class SaleFilterBar : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public SaleListFilter Filter { get; } = new();

    public event Action? Changed;
    public event Action? PeriodChanged;

    private readonly ComboBox _period = new();
    private readonly CalendarDatePicker _date = new() { Width = 130, IsVisible = false, SelectedDateFormat = CalendarDatePickerFormat.Short };
    private readonly ComboBox _payment = new();
    private readonly ComboBox _status = new();
    private readonly ComboBox _cashier = new();
    private readonly TextBox _client = new() { Width = 150 };
    private readonly TextBox _from = new() { Width = 96 };
    private readonly TextBox _to = new() { Width = 96 };
    private readonly Button _reset = new();
    private readonly TextBlock _count = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private bool _silent;
    private readonly bool _showPeriod;

    public SaleFilterBar(bool showPeriod = true)
    {
        _showPeriod = showPeriod;
        Fill(_period, T("Все даты", "Бардык күндөр", "All dates", "Tüm tarihler", "Barcha sanalar"), T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"),
            T("Неделя", "Апта", "Week", "Hafta", "Hafta"), T("Месяц", "Ай", "Month", "Ay", "Oy"), T("Дата…", "Күн…", "Date…", "Tarih…", "Sana…"));
        Fill(_payment, T("Любая оплата", "Каалаган төлөм", "Any payment", "Her ödeme", "Har qanday to'lov"), T("Наличные", "Накталай", "Cash", "Nakit", "Naqd"),
            T("Карта / перевод", "Карта / которуу", "Card / transfer", "Kart / havale", "Karta / o'tkazma"), "QR", T("Долг", "Карыз", "Debt", "Borç", "Qarz"),
            T("Смешанная", "Аралаш", "Mixed", "Karışık", "Aralash"));
        Fill(_status, T("Любой статус", "Каалаган статус", "Any status", "Her durum", "Har qanday holat"), T("Обычный", "Кадимки", "Regular", "Normal", "Oddiy"),
            T("Долговой", "Карыз", "On credit", "Veresiye", "Qarzga"), T("Частично возвращён", "Жарым-жартылай кайтарылган", "Partly returned", "Kısmen iade", "Qisman qaytarilgan"),
            T("Возвращён", "Кайтарылган", "Returned", "İade edildi", "Qaytarilgan"));
        SetCashiers(Array.Empty<string>());

        Name(_period, T("Период", "Мезгил", "Period", "Dönem", "Davr"));
        Name(_date, T("Дата чека", "Чектин күнү", "Receipt date", "Fiş tarihi", "Chek sanasi"));
        Name(_payment, T("Способ оплаты", "Төлөм ыкмасы", "Payment method", "Ödeme yöntemi", "To'lov usuli"));
        Name(_status, T("Статус чека", "Чектин статусу", "Receipt status", "Fiş durumu", "Chek holati"));
        Name(_cashier, T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"));
        _client.Watermark = T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz");
        _from.Watermark = T("Сумма от", "Сумма -дан", "Amount from", "Tutar min", "Summa dan");
        _to.Watermark = T("до", "-га чейин", "to", "max", "gacha");
        Name(_client, T("Клиент: имя или телефон", "Кардар: аты же телефону", "Customer: name or phone", "Müşteri: ad veya telefon", "Mijoz: ism yoki telefon"));
        Name(_from, T("Сумма от", "Сумма -дан", "Amount from", "Tutar en az", "Summa dan"));
        Name(_to, T("Сумма до", "Сумма -га чейин", "Amount to", "Tutar en çok", "Summa gacha"));
        _reset.Content = T("Сбросить", "Тазалоо", "Reset", "Sıfırla", "Tozalash");
        Name(_reset, T("Сбросить фильтры", "Чыпкаларды тазалоо", "Reset filters", "Filtreleri sıfırla", "Filtrlarni tozalash"));
        _reset.Classes.Add("btn-secondary"); // стиль окна возврата
        _reset.Classes.Add("sec-outline");   // стиль разделов владельца («Продажи»)
        _reset.Padding = new Thickness(12, 0);

        foreach (var c in new Control[] { _period, _date, _payment, _status, _cashier, _client, _from, _to, _reset })
        {
            c.Height = 40;
            c.Margin = new Thickness(0, 0, 8, 8);
            c.VerticalAlignment = VerticalAlignment.Center;
        }
        foreach (var box in new[] { _period, _payment, _status, _cashier })
            box.MinWidth = 120;
        foreach (var tb in new[] { _client, _from, _to })
        {
            tb.VerticalContentAlignment = VerticalAlignment.Center;
            tb.FontSize = 13;
            tb.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    ReadText();
                    e.Handled = true;
                }
            };
            tb.LostFocus += (_, _) => ReadText();
        }
        _period.IsVisible = showPeriod;
        _count.Margin = new Thickness(4, 0, 0, 8);
        _count.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));

        _period.SelectionChanged += (_, _) =>
        {
            if (_silent)
                return;
            Filter.Period = (SaleListFilter.PeriodKind)Math.Max(0, _period.SelectedIndex);
            _date.IsVisible = Filter.Period == SaleListFilter.PeriodKind.Date;
            if (Filter.Period == SaleListFilter.PeriodKind.Date && Filter.Date is null)
            {
                Filter.Date = DateTime.Today;
                _silent = true;
                _date.SelectedDate = DateTime.Today;
                _silent = false;
            }
            RaisePeriod();
        };
        _date.SelectedDateChanged += (_, _) =>
        {
            if (_silent)
                return;
            Filter.Date = _date.SelectedDate;
            RaisePeriod();
        };
        _payment.SelectionChanged += (_, _) => Apply(() => Filter.Payment = (SaleListFilter.PaymentKind)Math.Max(0, _payment.SelectedIndex));
        _status.SelectionChanged += (_, _) => Apply(() => Filter.Status = (SaleListFilter.StatusKind)Math.Max(0, _status.SelectedIndex));
        _cashier.SelectionChanged += (_, _) => Apply(() => Filter.Cashier = _cashier.SelectedIndex > 0 ? _cashier.SelectedItem as string : null);
        _client.TextChanged += (_, _) => Apply(() => Filter.Client = string.IsNullOrWhiteSpace(_client.Text) ? null : _client.Text);
        _reset.Click += (_, _) => Reset();

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new Control[] { _period, _date, _payment, _status, _cashier, _client, _from, _to, _reset, _count })
            wrap.Children.Add(c);
        Content = wrap;
    }

    private static void Fill(ComboBox box, params string[] items)
    {
        box.ItemsSource = items;
        box.SelectedIndex = 0;
    }

    private static void Name(Control c, string name)
    {
        Avalonia.Automation.AutomationProperties.SetName(c, name);
        ToolTip.SetTip(c, name);
    }

    private void Apply(Action set)
    {
        if (_silent)
            return;
        set();
        Changed?.Invoke();
    }

    private void RaisePeriod()
    {
        PeriodChanged?.Invoke();
        Changed?.Invoke();
    }

    private void ReadText()
    {
        var from = SaleListFilter.ParseAmount(_from.Text);
        var to = SaleListFilter.ParseAmount(_to.Text);
        if (from == Filter.AmountFrom && to == Filter.AmountTo)
            return;
        Filter.AmountFrom = from;
        Filter.AmountTo = to;
        Changed?.Invoke();
    }

    /// <summary>Кассиры из загруженных чеков (первым — «Все кассиры»); выбранный остаётся выбранным.</summary>
    public void SetCashiers(IEnumerable<string> names)
    {
        var current = Filter.Cashier;
        var list = new List<string> { T("Все кассиры", "Бардык кассирлер", "All cashiers", "Tüm kasiyerler", "Barcha kassirlar") };
        list.AddRange(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        _silent = true;
        _cashier.ItemsSource = list;
        var index = current is null ? 0 : list.FindIndex(n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
        _cashier.SelectedIndex = Math.Max(0, index);
        _silent = false;
        _cashier.IsVisible = list.Count > 2 || current is not null;
    }

    /// <summary>Задать статус снаружи (карточка клиента: «Долговой»).</summary>
    public void SetStatus(SaleListFilter.StatusKind status)
    {
        Filter.Status = status;
        _silent = true;
        _status.SelectedIndex = (int)status;
        _silent = false;
    }

    /// <summary>«Найдено N из M» (+ пояснение — например, «ищет в составе чеков…»).</summary>
    public void SetCount(int found, int total, string? note = null)
    {
        _count.Text = (Filter.IsActive
                ? T($"Найдено: {found} из {total}", $"Табылды: {found} / {total}", $"Found: {found} of {total}", $"Bulunan: {found} / {total}", $"Topildi: {found} / {total}")
                : T($"Чеков: {total}", $"Чектер: {total}", $"Receipts: {total}", $"Fiş: {total}", $"Cheklar: {total}"))
            + (string.IsNullOrWhiteSpace(note) ? "" : " · " + note);
    }

    public string CountText => _count.Text ?? "";

    /// <summary>Счётчик внутри панели (окно возврата показывает свой — панель там сворачивается).</summary>
    public bool ShowCount
    {
        get => _count.IsVisible;
        set => _count.IsVisible = value;
    }

    /// <summary>Сбросить все фильтры этой панели (товар сбрасывает его владелец — событие <see cref="Changed"/>).</summary>
    public void Reset()
    {
        var periodWas = Filter.Period != SaleListFilter.PeriodKind.All;
        var product = Filter.Product;
        Filter.Reset();
        Filter.Product = product;
        _silent = true;
        _period.SelectedIndex = 0;
        _date.IsVisible = false;
        _date.SelectedDate = null;
        _payment.SelectedIndex = 0;
        _status.SelectedIndex = 0;
        _cashier.SelectedIndex = 0;
        _client.Text = "";
        _from.Text = "";
        _to.Text = "";
        _silent = false;
        ResetRequested?.Invoke();
        if (periodWas && _showPeriod)
            PeriodChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Нажали «Сбросить» — владелец очищает и свои поля (поиск по товару).</summary>
    public event Action? ResetRequested;
}
