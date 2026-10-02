using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-10-02, владелец: «реализуй аренду для услуг и для магазина одежды — прокат!», «в залоге и паспорт добавь».
/// Окно «Прокат» кассы (меню → «Прокат», в сферах «Одежда» и «Услуги»): вкладки «Активные / Просрочены /
/// Возвращены», «Новый прокат» и «Принять возврат». Документ проката, склад и залог ведёт сервер NurCRM
/// (<see cref="RentalsApi"/>), стоимость проката касса добавляет в чек строкой и берёт обычной оплатой.
/// </summary>
public sealed class RentalsWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly Func<Task<bool>>? _ensureShift;
    private readonly Func<string, double, bool>? _addRentLine;
    /// <summary>2026-10-02, владелец: «и админку не забудь при смене режима». В программе владельца
    /// раздел только для просмотра: там нет смены и чека, а залог и его возврат должны пройти через
    /// кассу смены. Выдача и приём возврата — в кассе.</summary>
    private readonly bool _readOnly;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private string _tab = "active";
    private IReadOnlyList<RentalDto> _active = Array.Empty<RentalDto>();
    private IReadOnlyList<RentalDto> _returned = Array.Empty<RentalDto>();

    /// <param name="ensureShift">Открыть смену, если закрыта (залог приходуется в кассу смены).</param>
    /// <param name="addRentLine">Добавить в текущий чек строку «Прокат…» на сумму — для оплаты проката.</param>
    public RentalsWindow(Func<Task<bool>>? ensureShift, Func<string, double, bool>? addRentLine)
    {
        _ensureShift = ensureShift;
        _addRentLine = addRentLine;
        _readOnly = ensureShift is null || addRentLine is null;
        Title = T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat");
        Width = 1100;
        Height = 780;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var root = new Grid { Margin = new Thickness(24, 18, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*") };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
        var title = new TextBlock { Text = T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat"), FontSize = 24, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(title);
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Click += (_, _) => _ = LoadAsync();
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);
        var add = UiKit.Primary(this, "+  " + T("Новый прокат", "Жаңы прокат", "New rental", "Yeni kiralama", "Yangi prokat"));
        add.Margin = new Thickness(10, 0, 0, 0);
        add.Click += async (_, _) => await NewRentalAsync().ConfigureAwait(true);
        add.IsVisible = !_readOnly;
        Grid.SetColumn(add, 2);
        head.Children.Add(add);
        root.Children.Add(head);

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status.Margin = new Thickness(0, 6, 0, 14);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);

        _tabs.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(_tabs, 2);
        root.Children.Add(_tabs);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 3);
        root.Children.Add(scroll);
        Content = root;

        Opened += (_, _) => _ = LoadAsync();
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private async Task LoadAsync()
    {
        _status.Text = T("Загружаю прокат с сервера NurCRM…", "Прокатты NurCRM серверинен жүктөп жатам…", "Loading rentals from the NurCRM server…",
            "Kiralamalar NurCRM sunucusundan yükleniyor…", "Prokat NurCRM serveridan yuklanmoqda…");
        try
        {
            var api = App.GetRequiredService<RentalsApi>();
            _active = await api.ListAsync("active").ConfigureAwait(true);
            _returned = await api.ListAsync("returned").ConfigureAwait(true);
            var overdue = _active.Count(r => r.Overdue);
            var hint = _readOnly
                ? " " + T("Выдача и приём возврата — в кассе (меню → «Прокат»).", "Берүү жана кайтарып алуу — кассада (меню → «Прокат»).", "Renting out and taking back — in the till (menu → “Rentals”).",
                    "Kiralama ve iade — kasada (menü → «Kiralama»).", "Berish va qaytarib olish — kassada (menyu → «Prokat»).")
                : "";
            _status.Text = T($"На руках: {_active.Count}, из них просрочено: {overdue}. Залогов деньгами: {Money(_active.Where(r => !r.IsDocumentDeposit).Sum(r => r.DepositAmount))}.",
                $"Колдо: {_active.Count}, анын ичинде мөөнөтү өткөн: {overdue}. Акчалай күрөө: {Money(_active.Where(r => !r.IsDocumentDeposit).Sum(r => r.DepositAmount))}.",
                $"Out: {_active.Count}, overdue: {overdue}. Cash deposits: {Money(_active.Where(r => !r.IsDocumentDeposit).Sum(r => r.DepositAmount))}.",
                $"Dışarıda: {_active.Count}, gecikmiş: {overdue}. Nakit depozito: {Money(_active.Where(r => !r.IsDocumentDeposit).Sum(r => r.DepositAmount))}.",
                $"Qo'lda: {_active.Count}, muddati o'tgan: {overdue}. Naqd garov: {Money(_active.Where(r => !r.IsDocumentDeposit).Sum(r => r.DepositAmount))}.") + hint;
        }
        catch (Exception ex)
        {
            _status.Text = T("Не удалось загрузить прокат: ", "Прокатты жүктөө болбой калды: ", "Could not load rentals: ", "Kiralamalar yüklenemedi: ", "Prokatni yuklab bo'lmadi: ")
                           + RentalsApi.Describe(ex);
            PosLogger.Log($"Прокат: список не загружен ({RentalsApi.Describe(ex)}).", "WARNING");
        }
        Render();
    }

    private void Render()
    {
        _tabs.Children.Clear();
        var overdue = _active.Where(r => r.Overdue).ToList();
        AddTab("active", T("На руках", "Колдо", "Out", "Dışarıda", "Qo'lda"), _active.Count);
        AddTab("overdue", T("Просрочены", "Мөөнөтү өткөн", "Overdue", "Gecikmiş", "Muddati o'tgan"), overdue.Count);
        AddTab("returned", T("Возвращены", "Кайтарылган", "Returned", "İade edildi", "Qaytarilgan"), _returned.Count);

        var rows = _tab switch
        {
            "overdue" => overdue,
            "returned" => _returned.OrderByDescending(r => r.ReturnedAt).ToList(),
            _ => _active.OrderBy(r => r.DateTo).ToList(),
        };
        _list.Children.Clear();
        if (rows.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = T("Здесь пока пусто. Нажмите «+ Новый прокат», чтобы выдать вещь клиенту.", "Азырынча бош. Кардарга буюм берүү үчүн «+ Жаңы прокат» басыңыз.",
                    "Nothing here yet. Press “+ New rental” to give an item to a client.", "Henüz boş. Müşteriye ürün vermek için «+ Yeni kiralama»ya basın.",
                    "Hozircha bo'sh. Mijozga buyum berish uchun «+ Yangi prokat»ni bosing."),
                FontSize = 15, Margin = new Thickness(4, 30, 4, 0), TextWrapping = TextWrapping.Wrap,
            };
            Use(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _list.Children.Add(empty);
            return;
        }
        foreach (var r in rows)
            _list.Children.Add(Card(r));
    }

    private void AddTab(string key, string text, int count)
    {
        var b = UiKit.Chip(this, $"{text}  {count}", _tab == key);
        b.Click += (_, _) => { _tab = key; Render(); };
        _tabs.Children.Add(b);
    }

    private Control Card(RentalDto r)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(18, 14), BorderThickness = new Thickness(r.Overdue ? 2 : 1) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, r.Overdue ? "BrushDanger" : "BrushBorder");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };

        var left = new StackPanel { Spacing = 4 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(Text($"№{r.Number} · {r.ClientName}", 16, FontWeight.Bold, "BrushText"));
        if (r.Overdue)
            head.Children.Add(Badge(T($"просрочен на {OverdueDays(r)} дн.", $"{OverdueDays(r)} күн кечикти", $"{OverdueDays(r)} d overdue", $"{OverdueDays(r)} gün gecikti", $"{OverdueDays(r)} kun kechikdi"), "BrushDanger"));
        else if (!r.IsActive)
            head.Children.Add(Badge(r.Condition == "damaged"
                ? T("возвращено, повреждено", "кайтарылды, бузулган", "returned, damaged", "iade, hasarlı", "qaytarildi, shikastlangan")
                : T("возвращено", "кайтарылды", "returned", "iade edildi", "qaytarildi"), r.Condition == "damaged" ? "BrushWarning" : "BrushSuccess"));
        left.Children.Add(head);
        left.Children.Add(Text(string.Join("; ", r.Items.Select(i => i.Label + (i.Qty > 1 ? $" × {i.Qty:0.###}" : ""))), 14, FontWeight.Normal, "BrushText", wrap: true));
        left.Children.Add(Text(T($"с {D(r.DateFrom)} по {D(r.DateTo)}", $"{D(r.DateFrom)} — {D(r.DateTo)}", $"{D(r.DateFrom)} – {D(r.DateTo)}", $"{D(r.DateFrom)} – {D(r.DateTo)}", $"{D(r.DateFrom)} – {D(r.DateTo)}")
                               + (string.IsNullOrWhiteSpace(r.Note) ? "" : " · " + r.Note), 13, FontWeight.Normal, "BrushTextSoft", wrap: true));
        grid.Children.Add(left);

        var deposit = new StackPanel { Spacing = 2, Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 190 };
        deposit.Children.Add(Text(T("Залог", "Күрөө", "Deposit", "Depozito", "Garov"), 12, FontWeight.SemiBold, "BrushTextSoft"));
        deposit.Children.Add(Text(r.IsDocumentDeposit ? r.DepositDocument : Money(r.DepositAmount), 15, FontWeight.Bold, "BrushText", wrap: true));
        if (!r.IsActive && (r.DepositWithheld > 0 || r.Penalty > 0))
            deposit.Children.Add(Text(T($"удержано {Money(r.DepositWithheld)}", $"кармалды {Money(r.DepositWithheld)}", $"withheld {Money(r.DepositWithheld)}", $"tutuldu {Money(r.DepositWithheld)}", $"ushlab qolindi {Money(r.DepositWithheld)}"), 12, FontWeight.Normal, "BrushDanger"));
        Grid.SetColumn(deposit, 1);
        grid.Children.Add(deposit);

        if (r.IsActive && !_readOnly)
        {
            var ret = UiKit.Primary(this, T("Принять возврат", "Кайтарууну кабыл алуу", "Take back", "İadeyi al", "Qaytarishni qabul qilish"));
            ret.VerticalAlignment = VerticalAlignment.Center;
            ret.Click += async (_, _) => await ReturnAsync(r).ConfigureAwait(true);
            Grid.SetColumn(ret, 2);
            grid.Children.Add(ret);
        }

        card.Child = grid;
        return card;
    }

    private async Task NewRentalAsync()
    {
        if (_ensureShift is null || _addRentLine is null || !await _ensureShift().ConfigureAwait(true))
            return;
        var dlg = new NewRentalWindow();
        var created = await dlg.ShowDialog<RentalDto?>(this).ConfigureAwait(true);
        if (created is null)
            return;
        var msg = T($"Прокат №{created.Number} оформлен.", $"Прокат №{created.Number} түзүлдү.", $"Rental #{created.Number} created.", $"Kiralama №{created.Number} oluşturuldu.", $"Prokat №{created.Number} rasmiylashtirildi.");
        if (!created.IsDocumentDeposit && created.DepositAmount > 0)
            msg += " " + T($"Залог {Money(created.DepositAmount)} принят в кассу.", $"Күрөө {Money(created.DepositAmount)} кассага кабыл алынды.", $"Deposit {Money(created.DepositAmount)} put into the till.",
                $"Depozito {Money(created.DepositAmount)} kasaya alındı.", $"Garov {Money(created.DepositAmount)} kassaga qabul qilindi.");
        if (dlg.RentTotal > 0)
        {
            var line = $"Прокат №{created.Number}: {string.Join(", ", created.Items.Select(i => i.Label))}, {D(created.DateFrom)}–{D(created.DateTo)}";
            if (_addRentLine(line, dlg.RentTotal))
                msg += " " + T($"Стоимость проката {Money(dlg.RentTotal)} добавлена в чек — примите оплату.", $"Прокаттын баасы {Money(dlg.RentTotal)} чекке кошулду — төлөмдү кабыл алыңыз.",
                    $"Rental price {Money(dlg.RentTotal)} added to the receipt — take the payment.", $"Kiralama bedeli {Money(dlg.RentTotal)} fişe eklendi — ödemeyi alın.",
                    $"Prokat narxi {Money(dlg.RentTotal)} chekka qo'shildi — to'lovni qabul qiling.");
        }
        PosLogger.Log($"Прокат №{created.Number} оформлен: {created.Items.Count} вещ., залог {(created.IsDocumentDeposit ? "документ" : created.DepositAmount.ToString("0.00", CultureInfo.InvariantCulture))}, стоимость {dlg.RentTotal:0.00}.", "RENTAL");
        PosMessageBox.Show(this, msg, Title ?? "");
        _tab = "active";
        await LoadAsync().ConfigureAwait(true);
    }

    private async Task ReturnAsync(RentalDto r)
    {
        if (_ensureShift is null || !await _ensureShift().ConfigureAwait(true))
            return;
        var dlg = new ReturnRentalWindow(r);
        var done = await dlg.ShowDialog<RentalDto?>(this).ConfigureAwait(true);
        if (done is null)
            return;
        string msg;
        if (done.IsDocumentDeposit)
            msg = T($"Возврат принят. Верните клиенту документ: {done.DepositDocument}.", $"Кайтаруу кабыл алынды. Кардарга документти кайтарыңыз: {done.DepositDocument}.",
                $"Return accepted. Give the client back the document: {done.DepositDocument}.", $"İade alındı. Müşteriye belgeyi geri verin: {done.DepositDocument}.",
                $"Qaytarish qabul qilindi. Mijozga hujjatni qaytaring: {done.DepositDocument}.");
        else
            msg = T($"Возврат принят. Верните клиенту залог: {Money(done.DepositRefunded)}.", $"Кайтаруу кабыл алынды. Кардарга күрөөнү кайтарыңыз: {Money(done.DepositRefunded)}.",
                $"Return accepted. Give the client back the deposit: {Money(done.DepositRefunded)}.", $"İade alındı. Müşteriye depozitoyu geri verin: {Money(done.DepositRefunded)}.",
                $"Qaytarish qabul qilindi. Mijozga garovni qaytaring: {Money(done.DepositRefunded)}.")
                  + (done.DepositWithheld > 0 ? " " + T($"Удержано: {Money(done.DepositWithheld)}.", $"Кармалды: {Money(done.DepositWithheld)}.", $"Withheld: {Money(done.DepositWithheld)}.", $"Tutulan: {Money(done.DepositWithheld)}.", $"Ushlab qolindi: {Money(done.DepositWithheld)}.") : "");
        PosLogger.Log($"Прокат №{done.Number}: возврат ({done.Condition}), штраф {done.Penalty:0.00}, залог вернуть {done.DepositRefunded:0.00}, удержано {done.DepositWithheld:0.00}.", "RENTAL");
        PosMessageBox.Show(this, msg, Title ?? "");
        await LoadAsync().ConfigureAwait(true);
    }

    private static int OverdueDays(RentalDto r) => r.DateTo is { } to ? Math.Max(1, (DateTime.Today - to.Date).Days) : 1;
    internal static string D(DateTime? d) => d?.ToString("dd.MM.yyyy", Ru) ?? "—";
    internal static string Money(double v) => v.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private TextBlock Text(string text, double size, FontWeight weight, string brush, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Use(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    private Border Badge(string text, string brush)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        Use(b, Border.BackgroundProperty, brush);
        b.Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White };
        return b;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>Общие кнопки окон проката — в цветах темы, как в окне выбора размера.</summary>
internal static class UiKit
{
    public static Button Primary(Control host, string text)
    {
        var b = new Button
        {
            Content = text, Height = 46, Padding = new Thickness(20, 0), CornerRadius = new CornerRadius(12), FontWeight = FontWeight.Bold, FontSize = 14.5,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Bind(Button.BackgroundProperty, host.GetResourceObservable("BrushAccent"));
        b.Bind(Button.ForegroundProperty, host.GetResourceObservable("BrushAccentForeground"));
        return b;
    }

    public static Button Ghost(Control host, string text)
    {
        var b = new Button
        {
            Content = text, Height = 46, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
        };
        b.Bind(Button.BackgroundProperty, host.GetResourceObservable("BrushPanel"));
        b.Bind(Button.BorderBrushProperty, host.GetResourceObservable("BrushBorder"));
        b.Bind(Button.ForegroundProperty, host.GetResourceObservable("BrushText"));
        return b;
    }

    public static Button Chip(Control host, string text, bool selected)
    {
        var b = new Button
        {
            Content = text, MinHeight = 40, Padding = new Thickness(16, 6), CornerRadius = new CornerRadius(20), FontSize = 14,
            FontWeight = selected ? FontWeight.Bold : FontWeight.Normal, BorderThickness = new Thickness(selected ? 2 : 1), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Bind(Button.BackgroundProperty, host.GetResourceObservable(selected ? "BrushAccentSoft" : "BrushPanel"));
        b.Bind(Button.BorderBrushProperty, host.GetResourceObservable(selected ? "BrushAccentStrong" : "BrushBorder"));
        b.Bind(Button.ForegroundProperty, host.GetResourceObservable("BrushText"));
        return b;
    }

    public static TextBox Input(Control host, string watermark, double height = 44)
    {
        var t = new TextBox { Watermark = watermark, Height = height, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(10), FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
        return t;
    }

    public static TextBlock Label(Control host, string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushTextSoft"));
        return t;
    }
}
