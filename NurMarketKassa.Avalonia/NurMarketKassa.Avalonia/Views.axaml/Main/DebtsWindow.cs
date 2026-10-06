using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, владелец: «аналитика по долгам! при нажатии на неё должен подробно показывать долги». Раздел программы
/// владельца «Долги клиентов»: сколько должны магазину (сумма, клиенты, чеки, самый старый долг), разбивка по давности,
/// по каждому клиенту — сумма, с какого числа, телефон и напоминание в WhatsApp; по нажатию — все его чеки в долг (дата,
/// номер, товар, сумма чека, внесено, остаток, кассир). Данные — продажи со статусом debt с сервера NurCRM
/// (ISalesApiService.PosDebtSalesAsync), телефоны — из списка клиентов. Окно собрано в коде, как «Продажи в убыток».</summary>
public sealed class DebtsWindow : Window
{
    public DebtsWindow()
    {
        Title = DebtsView.TitleText;
        Width = 1100;
        Height = 800;
        Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        Content = new DebtsView(showTitle: true);
    }
}

/// <summary>2026-10-05, владелец: «где в сводке и аналитике долги??» — содержимое «Долгов клиентов» отдельным блоком:
/// то же самое и в разделе «Долги клиентов», и вкладкой «Долги» в «Аналитике». Данные загружаются, когда блок впервые
/// показан на экране. Карточка «Долги клиентов» в «Сводке» берёт итоги из <see cref="LoadDebtorsAsync"/>.</summary>
public sealed class DebtsView : UserControl
{
    public static string TitleText => T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari");

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly TextBlock _summary = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _aging = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 12) };
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBox _search;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private List<Debtor> _debtors = new();
    private string _sort = "amount";
    private int _loadGen;
    private bool _loadStarted;

    internal sealed record DebtSale(string Id, string Number, DateTime At, string Item, double Total, double Paid, double Left, string Cashier, string Cashbox);

    internal sealed record Debtor(string ClientId, string Name, string Phone, List<DebtSale> Sales)
    {
        public double Total => Sales.Sum(s => s.Left);
        public DateTime Oldest => Sales.Min(s => s.At);
    }

    public DebtsView(bool showTitle)
    {
        var root = new Grid { Margin = showTitle ? new Thickness(24, 16, 24, 24) : new Thickness(8, 4, 8, 8), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*") };
        var title = new TextBlock { Text = TitleText, FontSize = 22, FontWeight = FontWeight.Bold, IsVisible = showTitle };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var hint = new TextBlock
        {
            Text = T("Кто и сколько должен магазину — по данным NurCRM, со всех касс. Нажмите на клиента, чтобы увидеть его чеки в долг.",
                "Ким дүкөнгө канча карыз — NurCRM маалыматы боюнча, бардык кассалардан. Карыз чектерин көрүү үчүн кардарды басыңыз.",
                "Who owes the shop and how much — from NurCRM data, all tills. Tap a customer to see their receipts on credit.",
                "Mağazaya kim ne kadar borçlu — NurCRM verilerinden, tüm kasalar. Veresiye fişlerini görmek için müşteriye dokunun.",
                "Do'konga kim qancha qarz — NurCRM ma'lumotlari bo'yicha, barcha kassalardan. Qarzga cheklarini ko'rish uchun mijozni bosing."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        _search = UiKit.Input(this, T("Поиск по имени или телефону…", "Аты же телефону боюнча издөө…", "Search by name or phone…", "Ad veya telefona göre ara…", "Ism yoki telefon bo'yicha qidirish…"), 40);
        _search.TextChanged += (_, _) => Render();
        tools.Children.Add(_search);
        var byAmount = UiKit.Chip(this, T("По сумме", "Сумма боюнча", "By amount", "Tutara göre", "Summa bo'yicha"), true);
        var byAge = UiKit.Chip(this, T("По давности", "Эскилиги боюнча", "By age", "Süreye göre", "Muddati bo'yicha"), false);
        void SetSort(string sort)
        {
            _sort = sort;
            byAmount.Bind(Button.BackgroundProperty, this.GetResourceObservable(sort == "amount" ? "BrushAccentSoft" : "BrushPanel"));
            byAge.Bind(Button.BackgroundProperty, this.GetResourceObservable(sort == "age" ? "BrushAccentSoft" : "BrushPanel"));
            Render();
        }
        byAmount.Margin = new Thickness(10, 0, 0, 0);
        byAge.Margin = new Thickness(8, 0, 0, 0);
        byAmount.Click += (_, _) => SetSort("amount");
        byAge.Click += (_, _) => SetSort("age");
        Grid.SetColumn(byAmount, 1);
        Grid.SetColumn(byAge, 2);
        tools.Children.Add(byAmount);
        tools.Children.Add(byAge);
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Margin = new Thickness(8, 0, 0, 0);
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Grid.SetColumn(refresh, 3);
        tools.Children.Add(refresh);
        tools.Classes.Add("no-reflow");

        Use(_summary, TextBlock.ForegroundProperty, "BrushDanger");
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(hint, 1);
        Grid.SetRow(tools, 2);
        Grid.SetRow(_summary, 3);
        Grid.SetRow(_aging, 4);
        Grid.SetRow(scroll, 5);
        root.Children.Add(title);
        root.Children.Add(hint);
        root.Children.Add(tools);
        root.Children.Add(_summary);
        root.Children.Add(_aging);
        root.Children.Add(scroll);

        _summary.Text = T("Загружаю долги…", "Карыздар жүктөлүүдө…", "Loading debts…", "Borçlar yükleniyor…", "Qarzlar yuklanmoqda…");
        Content = root;
        AttachedToVisualTree += async (_, _) =>
        {
            if (_loadStarted)
                return;
            _loadStarted = true;
            await LoadAsync().ConfigureAwait(true);
        };
    }

    /// <summary>Должники с сервера NurCRM (продажи в долг с остатком), по клиентам. withPhones — ещё и телефоны из списка клиентов.</summary>
    internal static async Task<List<Debtor>> LoadDebtorsAsync(bool withPhones, CancellationToken ct)
    {
        var sales = await App.GetRequiredService<ISalesApiService>().PosDebtSalesAsync(null, ct).ConfigureAwait(true);
        // Телефоны — из списка клиентов (в продаже только имя). Не получилось — без телефонов.
        var phones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (withPhones)
        {
            try
            {
                var clients = await App.GetRequiredService<IClientsApiService>().GetClientsAsync(null, ct).ConfigureAwait(true);
                foreach (var c in clients)
                    if (Str(c, "id") is { Length: > 0 } id)
                        phones[id] = Str(c, "phone") ?? "";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                PosLogger.Log($"Долги клиентов: телефоны не получены ({ex.Message}).", "WARNING");
            }
        }

        var debtors = new Dictionary<string, Debtor>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sales)
        {
            var total = Num(s, "total") ?? 0;
            var left = Num(s, "debt_remaining") ?? Num(s, "debt_amount") ?? total;
            if (left <= 0.005)
                continue;
            var clientId = Str(s, "client") ?? "";
            var key = clientId.Length > 0 ? clientId : "—";
            if (!debtors.TryGetValue(key, out var debtor))
            {
                debtor = new Debtor(clientId, Str(s, "client_name") ?? T("Без клиента", "Кардарсыз", "No customer", "Müşterisiz", "Mijozsiz"),
                    phones.TryGetValue(clientId, out var ph) ? ph : "", new List<DebtSale>());
                debtors[key] = debtor;
            }
            DateTime.TryParse(Str(s, "created_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var at);
            debtor.Sales.Add(new DebtSale(Str(s, "id") ?? "", Str(s, "number") ?? "", at.ToLocalTime(), Str(s, "first_item_name") ?? "", total,
                Math.Max(0, total - left), left, Str(s, "user_display") ?? "", Str(s, "cashbox_name") ?? ""));
        }
        return debtors.Values.ToList();
    }

    private async Task LoadAsync()
    {
        var gen = ++_loadGen;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var debtors = await LoadDebtorsAsync(withPhones: true, cts.Token).ConfigureAwait(true);
            if (gen != _loadGen)
                return;
            _debtors = debtors;
            PosLogger.Log($"Долги клиентов: {_debtors.Count} клиент(ов), {_debtors.Sum(d => d.Sales.Count)} чек(ов) в долг.", "INFO");
            Render();
        }
        catch (Exception ex)
        {
            if (gen != _loadGen)
                return;
            PosLogger.Log($"Долги клиентов: не загружены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            _summary.Text = T("Не удалось загрузить долги: ", "Карыздарды жүктөө мүмкүн болгон жок: ", "Couldn't load debts: ", "Borçlar yüklenemedi: ", "Qarzlarni yuklab bo'lmadi: ")
                            + (ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi") : ex.Message);
        }
    }

    private void Render()
    {
        _list.Children.Clear();
        _aging.Children.Clear();
        var all = _debtors;
        var total = all.Sum(d => d.Total);
        var checks = all.Sum(d => d.Sales.Count);
        if (all.Count == 0)
        {
            _summary.Text = T("Долгов нет — все клиенты рассчитались.", "Карыз жок — бардык кардарлар эсептешти.", "No debts — every customer has paid.",
                "Borç yok — tüm müşteriler ödedi.", "Qarz yo'q — barcha mijozlar to'lagan.");
            return;
        }
        var oldest = all.Min(d => d.Oldest);
        _summary.Text = T($"Вам должны {Money(total)} · клиентов: {all.Count} · чеков в долг: {checks} · самый старый долг с {oldest:dd.MM.yyyy} ({Days(oldest)})",
            $"Сизге карыз {Money(total)} · кардарлар: {all.Count} · карыз чектер: {checks} · эң эски карыз {oldest:dd.MM.yyyy} баштап ({Days(oldest)})",
            $"You are owed {Money(total)} · customers: {all.Count} · receipts on credit: {checks} · oldest debt since {oldest:dd.MM.yyyy} ({Days(oldest)})",
            $"Size borçlu: {Money(total)} · müşteri: {all.Count} · veresiye fiş: {checks} · en eski borç {oldest:dd.MM.yyyy} ({Days(oldest)})",
            $"Sizga qarz: {Money(total)} · mijozlar: {all.Count} · qarzga cheklar: {checks} · eng eski qarz {oldest:dd.MM.yyyy} dan ({Days(oldest)})");

        // Давность: до 7 дней, 8–30, больше 30 — по остатку долга.
        var sales = all.SelectMany(d => d.Sales).ToList();
        AddAging(T("до 7 дней", "7 күнгө чейин", "up to 7 days", "7 güne kadar", "7 kungacha"), sales.Where(s => AgeDays(s.At) <= 7).Sum(s => s.Left), "BrushSuccess");
        AddAging(T("8–30 дней", "8–30 күн", "8–30 days", "8–30 gün", "8–30 kun"), sales.Where(s => AgeDays(s.At) is > 7 and <= 30).Sum(s => s.Left), "BrushWarning");
        AddAging(T("больше 30 дней", "30 күндөн ашык", "over 30 days", "30 günden fazla", "30 kundan ortiq"), sales.Where(s => AgeDays(s.At) > 30).Sum(s => s.Left), "BrushDanger");

        var query = (_search.Text ?? "").Trim();
        var digits = new string(query.Where(char.IsDigit).ToArray());
        IEnumerable<Debtor> shown = all.Where(d => query.Length == 0
            || d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (digits.Length >= 3 && new string(d.Phone.Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal)));
        shown = _sort == "age" ? shown.OrderBy(d => d.Oldest) : shown.OrderByDescending(d => d.Total);
        foreach (var d in shown.Take(300))
            _list.Children.Add(BuildCard(d));
    }

    private void AddAging(string label, double amount, string brush)
    {
        var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 6), BorderThickness = new Thickness(1) };
        Use(pill, Border.BackgroundProperty, "BrushPanel");
        Use(pill, Border.BorderBrushProperty, brush);
        var text = new TextBlock { Text = $"{label}: {Money(amount)}", FontSize = 13, FontWeight = FontWeight.SemiBold };
        Use(text, TextBlock.ForegroundProperty, brush);
        pill.Child = text;
        _aging.Children.Add(pill);
    }

    private Control BuildCard(Debtor d)
    {
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12), Cursor = new Cursor(StandardCursorType.Hand) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, AgeDays(d.Oldest) > 30 ? "BrushDanger" : "BrushBorder");
        var body = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var who = new StackPanel { Spacing = 2 };
        var name = new TextBlock { Text = d.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        who.Children.Add(name);
        var meta = new TextBlock
        {
            Text = string.Join(" · ", new[]
            {
                d.Phone,
                T($"чеков: {d.Sales.Count}", $"чектер: {d.Sales.Count}", $"receipts: {d.Sales.Count}", $"fiş: {d.Sales.Count}", $"cheklar: {d.Sales.Count}"),
                T($"долг с {d.Oldest:dd.MM.yyyy} ({Days(d.Oldest)})", $"карыз {d.Oldest:dd.MM.yyyy} баштап ({Days(d.Oldest)})", $"owing since {d.Oldest:dd.MM.yyyy} ({Days(d.Oldest)})",
                    $"{d.Oldest:dd.MM.yyyy} tarihinden beri ({Days(d.Oldest)})", $"{d.Oldest:dd.MM.yyyy} dan beri ({Days(d.Oldest)})"),
            }.Where(x => !string.IsNullOrWhiteSpace(x))),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        Use(meta, TextBlock.ForegroundProperty, "BrushTextSoft");
        who.Children.Add(meta);
        head.Children.Add(who);
        var right = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        var sum = new TextBlock { Text = Money(d.Total), FontSize = 17, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right };
        Use(sum, TextBlock.ForegroundProperty, "BrushDanger");
        right.Children.Add(sum);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        if (ClientQrCode.NationalDigits(d.Phone) is { } national)
        {
            var wa = UiKit.Ghost(this, T("Напомнить в WhatsApp", "WhatsApp'та эскертүү", "Remind on WhatsApp", "WhatsApp'tan hatırlat", "WhatsApp'da eslatish"));
            wa.Height = 32;
            wa.FontSize = 12.5;
            wa.Click += (_, e) =>
            {
                e.Handled = true;
                var text = T($"Здравствуйте, {d.Name}! Напоминаем о долге в нашем магазине: {Money(d.Total)}. Спасибо!",
                    $"Саламатсызбы, {d.Name}! Дүкөнүбүздөгү карызыңызды эскертебиз: {Money(d.Total)}. Рахмат!",
                    $"Hello, {d.Name}! A reminder about your debt at our shop: {Money(d.Total)}. Thank you!",
                    $"Merhaba {d.Name}! Mağazamızdaki borcunuzu hatırlatırız: {Money(d.Total)}. Teşekkürler!",
                    $"Assalomu alaykum, {d.Name}! Do'konimizdagi qarzingizni eslatamiz: {Money(d.Total)}. Rahmat!");
                SiteOrdersWindow.OpenUrl($"https://wa.me/996{national}?text={Uri.EscapeDataString(text)}");
            };
            buttons.Children.Add(wa);
        }
        right.Children.Add(buttons);
        Grid.SetColumn(right, 1);
        head.Children.Add(right);
        body.Children.Add(head);

        var expanded = _expanded.Contains(d.ClientId + d.Name);
        var more = new TextBlock
        {
            Text = expanded ? T("▴ Скрыть чеки", "▴ Чектерди жашыруу", "▴ Hide receipts", "▴ Fişleri gizle", "▴ Cheklarni yashirish")
                : T("▾ Показать чеки в долг", "▾ Карыз чектерди көрсөтүү", "▾ Show receipts on credit", "▾ Veresiye fişleri göster", "▾ Qarzga cheklarni ko'rsatish"),
            FontSize = 12.5,
        };
        Use(more, TextBlock.ForegroundProperty, "BrushAccentStrong");
        body.Children.Add(more);
        if (expanded)
            body.Children.Add(BuildSalesTable(d));
        card.Child = body;
        card.PointerPressed += (_, e) =>
        {
            if (e.Handled)
                return;
            var key = d.ClientId + d.Name;
            if (!_expanded.Remove(key))
                _expanded.Add(key);
            Render();
        };
        return card;
    }

    private Control BuildSalesTable(Debtor d)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        var row = 0;
        void Cell(string text, int col, bool head = false, string brush = "BrushText", bool right = false)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = head ? 11.5 : 13, FontWeight = head ? FontWeight.SemiBold : FontWeight.Normal,
                Margin = new Thickness(col == 0 ? 0 : 14, 3, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            Use(t, TextBlock.ForegroundProperty, head ? "BrushTextSoft" : brush);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            grid.Children.Add(t);
        }
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Cell(T("Дата", "Күнү", "Date", "Tarih", "Sana"), 0, head: true);
        Cell(T("Чек", "Чек", "Receipt", "Fiş", "Chek"), 1, head: true);
        Cell(T("Товар · кассир", "Товар · кассир", "Item · cashier", "Ürün · kasiyer", "Mahsulot · kassir"), 2, head: true);
        Cell(T("Сумма чека", "Чектин суммасы", "Receipt total", "Fiş tutarı", "Chek summasi"), 3, head: true, right: true);
        Cell(T("Внесено", "Төлөндү", "Paid", "Ödenen", "To'langan"), 4, head: true, right: true);
        Cell(T("Остаток", "Калдык", "Left", "Kalan", "Qoldiq"), 5, head: true, right: true);
        foreach (var s in d.Sales.OrderBy(s => s.At))
        {
            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Cell($"{s.At:dd.MM.yyyy HH:mm}", 0);
            Cell(s.Number.Length > 0 ? "№" + s.Number : "—", 1);
            Cell(string.Join(" · ", new[] { s.Item, s.Cashier }.Where(x => !string.IsNullOrWhiteSpace(x))), 2, brush: "BrushTextSoft");
            Cell(Money(s.Total), 3, right: true);
            Cell(Money(s.Paid), 4, brush: "BrushTextSoft", right: true);
            Cell(Money(s.Left), 5, brush: "BrushDanger", right: true);
        }
        return grid;
    }

    internal static int AgeDays(DateTime at) => Math.Max(0, (int)(DateTime.Today - at.Date).TotalDays);

    internal static string Days(DateTime at)
    {
        var n = AgeDays(at);
        return n == 0
            ? T("сегодня", "бүгүн", "today", "bugün", "bugun")
            : T($"{n} дн.", $"{n} күн", n == 1 ? "1 day" : $"{n} days", $"{n} gün", $"{n} kun");
    }

    internal static string Money(double v) => v.ToString("N2", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString() : null;

    private static double? Num(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
