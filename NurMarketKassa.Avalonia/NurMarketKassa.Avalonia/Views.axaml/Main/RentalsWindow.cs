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
    /// <summary>2026-10-02, редизайн: счётчики плитками (на руках, просрочено, залоги деньгами).</summary>
    private readonly Grid _stats = new() { ColumnDefinitions = new ColumnDefinitions("*,12,*,12,*"), Margin = new Thickness(0, 4, 0, 16) };
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

        var root = new Grid { Margin = new Thickness(24, 18, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        var title = new TextBlock { Text = T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat"), FontSize = 24, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(title);
        // 2026-10-02, владелец: «когда приносит обратно — по этому чеку закрывали прокат». Номер проката
        // печатается в чеке («Прокат №N…») — вводим его здесь, Enter открывает приём возврата.
        var find = UiKit.Input(this, T("№ проката с чека", "Чектеги прокат №", "Rental # from receipt", "Fişteki kiralama №", "Chekdagi prokat №"), 46);
        find.Width = 190;
        find.Margin = new Thickness(0, 0, 10, 0);
        find.IsVisible = !_readOnly;
        find.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await FindByNumberAsync(find.Text).ConfigureAwait(true);
        };
        Grid.SetColumn(find, 1);
        head.Children.Add(find);
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Click += (_, _) => _ = LoadAsync();
        Grid.SetColumn(refresh, 2);
        head.Children.Add(refresh);
        var add = UiKit.Primary(this, "+  " + T("Новый прокат", "Жаңы прокат", "New rental", "Yeni kiralama", "Yangi prokat"));
        add.Margin = new Thickness(10, 0, 0, 0);
        add.Click += async (_, _) => await NewRentalAsync().ConfigureAwait(true);
        add.IsVisible = !_readOnly;
        Grid.SetColumn(add, 3);
        head.Children.Add(add);
        root.Children.Add(head);

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _status.Margin = new Thickness(0, 6, 0, 14);
        Grid.SetRow(_status, 1);
        root.Children.Add(_status);

        Grid.SetRow(_stats, 2);
        root.Children.Add(_stats);

        _tabs.Margin = new Thickness(0, 0, 0, 14);
        Grid.SetRow(_tabs, 3);
        root.Children.Add(_tabs);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 4);
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
            var active = await api.ListAsync("active").ConfigureAwait(true);
            var overdueList = await api.ListAsync("overdue").ConfigureAwait(true);
            _active = active.Concat(overdueList).Where(r => r.IsActive).GroupBy(r => r.Id).Select(g => g.First()).ToList();
            // 2026-10-02: значок и карточка «сроки проката» в программе владельца — сразу по свежему списку.
            RentalDueNotifier.Publish(_active);
            _returned = await api.ListAsync("returned").ConfigureAwait(true);
            var overdue = _active.Count(r => r.IsOverdue);
            var hint = _readOnly
                ? " " + T("Выдача и приём возврата — в кассе (меню → «Прокат»).", "Берүү жана кайтарып алуу — кассада (меню → «Прокат»).", "Renting out and taking back — in the till (menu → “Rentals”).",
                    "Kiralama ve iade — kasada (menü → «Kiralama»).", "Berish va qaytarib olish — kassada (menyu → «Prokat»).")
                : "";
            _status.Text = hint.Trim();
            _status.IsVisible = _status.Text.Length > 0;
            RenderStats(_active.Count, overdue, _active.Where(r => !r.IsDocumentDeposit).Sum(r => r.DepositAmount), _active.Count(r => r.IsDocumentDeposit));
        }
        catch (Exception ex)
        {
            _status.IsVisible = true;
            _status.Text = T("Не удалось загрузить прокат: ", "Прокатты жүктөө болбой калды: ", "Could not load rentals: ", "Kiralamalar yüklenemedi: ", "Prokatni yuklab bo'lmadi: ")
                           + RentalsApi.Describe(ex);
            PosLogger.Log($"Прокат: список не загружен ({RentalsApi.Describe(ex)}).", "WARNING");
        }
        Render();
    }

    private void RenderStats(int active, int overdue, double money, int documents)
    {
        _stats.Children.Clear();
        AddStat(0, T("На руках", "Колдо", "Out", "Dışarıda", "Qo'lda"), active.ToString(CultureInfo.InvariantCulture), "BrushText", Icons.Hanger);
        AddStat(2, T("Просрочено", "Мөөнөтү өткөн", "Overdue", "Gecikmiş", "Muddati o'tgan"), overdue.ToString(CultureInfo.InvariantCulture),
            overdue > 0 ? "BrushDanger" : "BrushText", Icons.Clock);
        AddStat(4, T("Залоги деньгами", "Акчалай күрөө", "Cash deposits", "Nakit depozito", "Naqd garov"), Money(money), "BrushCatalogPrice", Icons.Cash,
            documents > 0 ? T($"+ документов: {documents}", $"+ документтер: {documents}", $"+ documents: {documents}", $"+ belgeler: {documents}", $"+ hujjatlar: {documents}") : null);
    }

    private void AddStat(int column, string label, string value, string valueBrush, string icon, string? sub = null)
    {
        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12), BorderThickness = new Thickness(1) };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        Use(circle, Border.BackgroundProperty, "BrushAccentSoft");
        var path = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(icon), Width = 20, Height = 20, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Use(path, Avalonia.Controls.Shapes.Shape.FillProperty, "BrushAccentStrong");
        circle.Child = path;
        g.Children.Add(circle);
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Text(label, 12.5, FontWeight.SemiBold, "BrushTextSoft"));
        texts.Children.Add(Text(value, 22, FontWeight.Bold, valueBrush));
        if (sub != null)
            texts.Children.Add(Text(sub, 12, FontWeight.Normal, "BrushTextSoft"));
        Grid.SetColumn(texts, 1);
        g.Children.Add(texts);
        card.Child = g;
        Grid.SetColumn(card, column);
        _stats.Children.Add(card);
    }

    /// <summary>Пиктограммы (Material Design Icons, свободная лицензия).</summary>
    internal static class Icons
    {
        public const string Hanger = "M12,4A3,3 0 0,1 15,7C15,8.27 14.21,9.36 13.1,9.79C12.91,9.9 12.71,10 12.5,10.1V11.3L21,17.5C21.88,18.13 21.43,19.5 20.35,19.5H3.65C2.57,19.5 2.12,18.13 3,17.5L11.5,11.3V9.14C11.5,8.76 11.73,8.43 12.07,8.28C12.67,8 13,7.54 13,7A1,1 0 0,0 12,6A1,1 0 0,0 11,7H9A3,3 0 0,1 12,4M12,13.04L6.37,17.5H17.63L12,13.04Z";
        public const string Clock = "M12,20A8,8 0 0,0 20,12A8,8 0 0,0 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20M12,2A10,10 0 0,1 22,12A10,10 0 0,1 12,22C6.47,22 2,17.5 2,12A10,10 0 0,1 12,2M12.5,7V12.25L17,14.92L16.25,16.15L11,13V7H12.5Z";
        public const string Cash = "M3,6H21V18H3V6M12,9A3,3 0 0,1 15,12A3,3 0 0,1 12,15A3,3 0 0,1 9,12A3,3 0 0,1 12,9M7,8A2,2 0 0,1 5,10V14A2,2 0 0,1 7,16H17A2,2 0 0,1 19,14V10A2,2 0 0,1 17,8H7Z";
        public const string Calendar = "M19,19H5V8H19M16,1V3H8V1H6V3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3H18V1M17,12H12V17H17V12Z";
        public const string IdCard = "M2,3H22C23.05,3 24,3.95 24,5V19C24,20.05 23.05,21 22,21H2C0.95,21 0,20.05 0,19V5C0,3.95 0.95,3 2,3M14,6V7H22V6H14M14,8V9H21.5L22,9V8H14M14,10V11H21V10H14M8,13.91C6,13.91 2,15 2,17V18H14V17C14,15 10,13.91 8,13.91M8,6A3,3 0 0,0 5,9A3,3 0 0,0 8,12A3,3 0 0,0 11,9A3,3 0 0,0 8,6Z";
    }

    private void Render()
    {
        _tabs.Children.Clear();
        var overdue = _active.Where(r => r.IsOverdue).ToList();
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
        // 2026-10-02, редизайн: цветная полоса статуса, вещи — плашками, даты и залог — с пиктограммами.
        // 2026-10-02: срок сегодня или завтра — оранжевая полоса и отметка («уведомление о приближении срока»).
        var due = RentalDueNotifier.Kind(r);
        var statusBrush = r.IsOverdue ? "BrushDanger" : due is RentalDueNotifier.DueKind.Today or RentalDueNotifier.DueKind.Tomorrow ? "BrushWarning"
            : r.IsActive ? "BrushSuccess" : r.Condition == "damaged" ? "BrushWarning" : "BrushBorderStrong";
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, r.IsOverdue ? "BrushDanger" : "BrushBorder");
        var outer = new Grid { ColumnDefinitions = new ColumnDefinitions("6,*") };
        var strip = new Border();
        Use(strip, Border.BackgroundProperty, statusBrush);
        outer.Children.Add(strip);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(16, 14, 16, 14) };
        Grid.SetColumn(grid, 1);
        outer.Children.Add(grid);

        var left = new StackPanel { Spacing = 8 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        head.Children.Add(Text($"№{r.Number}", 16, FontWeight.Bold, "BrushTextSoft"));
        head.Children.Add(Text(r.ClientName, 16, FontWeight.Bold, "BrushText"));
        if (r.IsOverdue)
            head.Children.Add(Badge(T($"просрочен на {OverdueDays(r)} дн.", $"{OverdueDays(r)} күн кечикти", $"{OverdueDays(r)} d overdue", $"{OverdueDays(r)} gün gecikti", $"{OverdueDays(r)} kun kechikdi"), "BrushDanger"));
        else if (due == RentalDueNotifier.DueKind.Today)
            head.Children.Add(Badge(T("вернуть сегодня", "бүгүн кайтаруу", "due today", "bugün iade", "bugun qaytarish"), "BrushWarning"));
        else if (due == RentalDueNotifier.DueKind.Tomorrow)
            head.Children.Add(Badge(T("вернуть завтра", "эртең кайтаруу", "due tomorrow", "yarın iade", "ertaga qaytarish"), "BrushWarning"));
        else if (r.IsActive)
            head.Children.Add(Badge(T("на руках", "колдо", "out", "dışarıda", "qo'lda"), "BrushSuccess"));
        else
            head.Children.Add(Badge(r.Condition == "damaged"
                ? T("возвращено, повреждено", "кайтарылды, бузулган", "returned, damaged", "iade, hasarlı", "qaytarildi, shikastlangan")
                : T("возвращено", "кайтарылды", "returned", "iade edildi", "qaytarildi"), r.Condition == "damaged" ? "BrushWarning" : "BrushTextMuted"));
        left.Children.Add(head);

        var items = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in r.Items)
        {
            var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4), Margin = new Thickness(0, 0, 6, 6) };
            Use(chip, Border.BackgroundProperty, "BrushPanelSoft");
            chip.Child = Text(i.Label + (i.Qty > 1 ? $" × {i.Qty:0.###}" : ""), 13.5, FontWeight.SemiBold, "BrushText");
            items.Children.Add(chip);
        }
        left.Children.Add(items);

        var dates = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        dates.Children.Add(Icon(Icons.Calendar, r.IsOverdue ? "BrushDanger" : "BrushTextSoft"));
        dates.Children.Add(Text($"{D(r.DateFrom)} — {D(r.DateTo)}" + (string.IsNullOrWhiteSpace(r.Note) ? "" : " · " + r.Note), 13, FontWeight.Normal,
            r.IsOverdue ? "BrushDanger" : "BrushTextSoft", wrap: true));
        left.Children.Add(dates);
        grid.Children.Add(left);

        var deposit = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 210, MaxWidth = 280 };
        Use(deposit, Border.BackgroundProperty, "BrushPanelSoft");
        var depGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var depIcon = Icon(r.IsDocumentDeposit ? Icons.IdCard : Icons.Cash, "BrushAccentStrong");
        depIcon.Margin = new Thickness(0, 2, 10, 0);
        depIcon.VerticalAlignment = VerticalAlignment.Top;
        depGrid.Children.Add(depIcon);
        var depText = new StackPanel { Spacing = 2 };
        depText.Children.Add(Text(r.IsDocumentDeposit
            ? T("Залог: документ", "Күрөө: документ", "Deposit: document", "Depozito: belge", "Garov: hujjat")
            : T("Залог деньгами", "Акчалай күрөө", "Cash deposit", "Nakit depozito", "Naqd garov"), 12, FontWeight.SemiBold, "BrushTextSoft"));
        depText.Children.Add(Text(r.IsDocumentDeposit ? r.DepositDocument : Money(r.DepositAmount), 14.5, FontWeight.Bold, "BrushText", wrap: true));
        // 2026-10-02, проверка: список проката не отдаёт deposit_withheld (только ответ на возврат) — считаем из штрафа.
        var withheld = r.DepositWithheld > 0 ? r.DepositWithheld : r.IsDocumentDeposit ? 0 : Math.Min(r.Penalty, r.DepositAmount);
        if (!r.IsActive && withheld > 0)
            depText.Children.Add(Text(T($"удержано {Money(withheld)}", $"кармалды {Money(withheld)}", $"withheld {Money(withheld)}", $"tutuldu {Money(withheld)}", $"ushlab qolindi {Money(withheld)}"), 12, FontWeight.Normal, "BrushDanger"));
        else if (!r.IsActive && r.Penalty > 0)
            depText.Children.Add(Text(T($"штраф {Money(r.Penalty)}", $"айып {Money(r.Penalty)}", $"penalty {Money(r.Penalty)}", $"ceza {Money(r.Penalty)}", $"jarima {Money(r.Penalty)}"), 12, FontWeight.Normal, "BrushDanger"));
        Grid.SetColumn(depText, 1);
        depGrid.Children.Add(depText);
        deposit.Child = depGrid;
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

        card.Child = outer;
        return card;
    }

    private Avalonia.Controls.Shapes.Path Icon(string data, string brush)
    {
        var p = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
        Use(p, Avalonia.Controls.Shapes.Shape.FillProperty, brush);
        return p;
    }

    private async Task FindByNumberAsync(string? text)
    {
        var digits = new string((text ?? "").Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var number))
            return;
        var rental = _active.FirstOrDefault(r => r.Number == number);
        if (rental is null)
        {
            var returned = _returned.FirstOrDefault(r => r.Number == number);
            PosMessageBox.Show(this, returned != null
                ? T($"Прокат №{number} уже возвращён.", $"Прокат №{number} мурунтан кайтарылган.", $"Rental #{number} has already been returned.", $"Kiralama №{number} zaten iade edildi.", $"Prokat №{number} allaqachon qaytarilgan.")
                : T($"Прокат №{number} не найден среди выданных.", $"Прокат №{number} берилгендердин арасында табылган жок.", $"Rental #{number} not found among rented items.", $"Kiralama №{number} bulunamadı.", $"Prokat №{number} topilmadi."), Title ?? "");
            return;
        }
        await ReturnAsync(rental).ConfigureAwait(true);
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
