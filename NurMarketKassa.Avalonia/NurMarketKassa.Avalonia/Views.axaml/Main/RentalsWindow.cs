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
    /// <summary>2026-10-02, редизайн: счётчики плитками (на руках, просрочено, залоги деньгами).
    /// 2026-10-18, этап О4: общие плитки разделов (KpiTileGrid + UiKit.Kpi) — сумма залогов уменьшается целиком, а не обрезается,
    /// цифры цветом текста (красный — только просрочка), без цветных значков.</summary>
    private readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid _stats = new() { MinTileWidth = 170, Spacing = 8, Margin = new Thickness(0, 4, 0, 16) };
    private string _tab = "active";
    /// <summary>2026-10-05: окно уже 700 точек (телефон) — см. NarrowLayout в конструкторе.</summary>
    private bool _narrow;
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
        if (!_readOnly)
        {
            // 2026-10-22 (аудит новой сборки, П2): в кассе окно было в системной рамке Windows и высотой 780 (на экране 768 «Закрыть»
            // и низ списка уходили за край). Теперь — общая оболочка ModalShell (ниже), высота под экран, список прокручивается сам.
            Width = 1112;
            Height = 700;
            CanResize = false;
            SystemDecorations = SystemDecorations.None;
            Use(this, BackgroundProperty, "DialogWindowBackground");
            Use(this, TransparencyLevelHintProperty, "DialogTransparencyHint");
        }
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        var root = new Grid { Margin = _readOnly ? new Thickness(24, 18, 24, 24) : new Thickness(0), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        head.Classes.Add("no-reflow"); // 2026-10-05: на телефоне перестраивается здесь же (NarrowLayout ниже)
        // 2026-10-18, этап О4: в программе владельца название уже в полосе над разделом — второго крупного заголовка нет.
        var title = new TextBlock { Text = T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat"), FontSize = 24, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, IsVisible = false }; // 2026-10-22 (П2): заголовок — в шапке оболочки ModalShell (в программе владельца и так скрыт)
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        head.Children.Add(title);
        // 2026-10-02, владелец: «когда приносит обратно — по этому чеку закрывали прокат». Номер проката
        // печатается в чеке («Прокат №N…») — вводим его здесь, Enter открывает приём возврата.
        var find = UiKit.Input(this, T("№ проката с чека", "Чектеги прокат №", "Rental # from receipt", "Fişteki kiralama №", "Chekdagi prokat №"), 46);
        find.Width = 190;
        find.Margin = new Thickness(0, 0, 10, 0);
        find.IsVisible = !_readOnly;
        Avalonia.Automation.AutomationProperties.SetName(find, find.Watermark); // 2026-10-18, этап О4: имя поля для доступности
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
        if (_readOnly)
        {
            // 2026-10-18, этап О4: в программе владельца заголовка нет — пояснение «Выдача и приём возврата — в кассе» стоит
            // в строке шапки слева от «Обновить», а не отдельной строкой под пустой шапкой.
            _status.Margin = new Thickness(0, 0, 16, 0);
            _status.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(_status);
            head.Margin = new Thickness(0, 0, 0, 14);
        }
        else
        {
            Grid.SetRow(_status, 1);
            root.Children.Add(_status);
        }

        Grid.SetRow(_stats, 2);
        root.Children.Add(_stats);

        // 2026-10-05, снимок владельца с телефона: «Возвращены 19» уходила за край — вкладки листаются вбок.
        // 2026-10-18, этап О4 (правило аудита 2): «На руках / Просрочены / Возвращены» — сегменты в рамке (выбранный тёмно-синий),
        // как период в отчётах, а не отдельные чипы.
        var tabsSegment = UiKit.Segment(_tabs);
        tabsSegment.HorizontalAlignment = HorizontalAlignment.Left;
        var tabsScroll = new ScrollViewer
        {
            Content = tabsSegment,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 14),
        };
        Grid.SetRow(tabsScroll, 3);
        root.Children.Add(tabsScroll);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 4);
        root.Children.Add(scroll);
        if (_readOnly)
            Content = root;
        else
        {
            var close = new Button { IsCancel = true, MaxWidth = 200, Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish") };
            close.Classes.Add("modal-cancel");
            close.Click += (_, _) => Close();
            Content = new NurMarketKassa.AvaloniaHost.Views.Dialogs.ModalShell
            {
                Margin = new Thickness(16),
                ContentScroll = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Title = T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat"),
                Footer = close,
                Content = root,
            };
        }

        // 2026-10-05, снимок владельца с телефона: «+ Новый прокат» уходила за край, в карточке залог
        // наезжал на имя клиента и даты, «Принять возврат» обрезалась. Телефон: заголовок и «+ Новый
        // прокат» — первой строкой, поиск по номеру и «Обновить» — второй; карточка — в две строки.
        NarrowLayout.Attach(this, 700, narrow =>
        {
            _narrow = narrow;
            root.Margin = !_readOnly ? new Thickness(0) : narrow ? new Thickness(10, 8, 10, 10) : new Thickness(24, 18, 24, 24);
            if (narrow)
            {
                head.ColumnDefinitions = new ColumnDefinitions("*,Auto");
                head.RowDefinitions = new RowDefinitions("Auto,8,Auto");
                Grid.SetColumn(add, 1);
                Grid.SetRow(find, 2);
                Grid.SetColumn(find, 0);
                find.Width = double.NaN;
                find.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetRow(refresh, 2);
                Grid.SetColumn(refresh, 1);
            }
            else
            {
                head.ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto");
                head.RowDefinitions = new RowDefinitions();
                Grid.SetColumn(add, 3);
                Grid.SetRow(find, 0);
                Grid.SetColumn(find, 1);
                find.Width = 190;
                find.Margin = new Thickness(0, 0, 10, 0);
                Grid.SetRow(refresh, 0);
                Grid.SetColumn(refresh, 2);
            }
            Render();
        });

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
        _stats.Children.Add(UiKit.Kpi(T("На руках", "Колдо", "Out", "Dışarıda", "Qo'lda"), active.ToString(CultureInfo.InvariantCulture)));
        _stats.Children.Add(UiKit.Kpi(T("Просрочено", "Мөөнөтү өткөн", "Overdue", "Gecikmiş", "Muddati o'tgan"), overdue.ToString(CultureInfo.InvariantCulture), danger: overdue > 0));
        var deposits = UiKit.Kpi(T("Залоги деньгами", "Акчалай күрөө", "Cash deposits", "Nakit depozito", "Naqd garov"), Money(money));
        if (documents > 0 && deposits.Child is StackPanel tile)
            tile.Children.Add(Text(T($"+ документов: {documents}", $"+ документтер: {documents}", $"+ documents: {documents}", $"+ belgeler: {documents}", $"+ hujjatlar: {documents}"),
                12, FontWeight.Normal, "BrushTextSoft", wrap: true));
        _stats.Children.Add(deposits);
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

        // 2026-10-21 (пакет 5, п. 52): «Возвращены» — строго по порядку: последний возврат сверху, без даты возврата —
        // по дате создания, при равенстве — больший номер выше (живой аудит 20.10: №22, 25, 21, 24… — у возвратов одного
        // дня порядок выглядел случайным). «Просрочены» — как «На руках», по сроку возврата.
        var rows = _tab switch
        {
            "overdue" => overdue.OrderBy(r => r.DateTo ?? DateTime.MaxValue).ThenBy(r => r.Number).ToList(),
            "returned" => _returned.OrderByDescending(r => r.ReturnedAt ?? r.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(r => r.Number).ToList(),
            _ => _active.OrderBy(r => r.DateTo ?? DateTime.MaxValue).ThenBy(r => r.Number).ToList(),
        };
        _list.Children.Clear();
        if (rows.Count == 0)
        {
            var empty = new TextBlock
            {
                // 2026-10-18, этап О4: в программе владельца кнопки «+ Новый прокат» нет — без отсылки к ней.
                // Доводка О4: пусто — по вкладке и с подсказкой, что делать.
                Text = _tab == "overdue"
                    ? T("Просроченных нет — все вещи возвращают вовремя.", "Мөөнөтү өткөн жок — бардык буюмдар убагында кайтарылууда.", "Nothing overdue — everything comes back on time.",
                        "Gecikmiş yok — her şey zamanında iade ediliyor.", "Muddati o'tganlar yo'q — hammasi o'z vaqtida qaytarilmoqda.")
                    : _tab == "returned"
                        ? T("Возвратов пока нет. Когда клиент вернёт вещь, прокат появится здесь.", "Азырынча кайтаруу жок. Кардар буюмду кайтарганда прокат ушул жерде көрүнөт.",
                            "No returns yet. When a client brings an item back, the rental appears here.", "Henüz iade yok. Müşteri ürünü iade edince kiralama burada görünür.",
                            "Hozircha qaytarishlar yo'q. Mijoz buyumni qaytarganda prokat shu yerda ko'rinadi.")
                        : _readOnly
                            ? T("Сейчас на руках ничего нет. Выдать вещь в прокат можно в кассе: меню → «Прокат».", "Азыр колдо эч нерсе жок. Буюмду прокатка кассада берсе болот: меню → «Прокат».",
                                "Nothing is out right now. Rent out an item in the till: menu → “Rentals”.", "Şu an dışarıda ürün yok. Kiralamayı kasada yapın: menü → «Kiralama».",
                                "Hozir qo'lda hech narsa yo'q. Buyumni prokatga kassada berish mumkin: menyu → «Prokat».")
                            : T("Здесь пока пусто. Нажмите «+ Новый прокат», чтобы выдать вещь клиенту.", "Азырынча бош. Кардарга буюм берүү үчүн «+ Жаңы прокат» басыңыз.",
                    "Nothing here yet. Press “+ New rental” to give an item to a client.", "Henüz boş. Müşteriye ürün vermek için «+ Yeni kiralama»ya basın.",
                    "Hozircha bo'sh. Mijozga buyum berish uchun «+ Yangi prokat»ni bosing."),
                // 2026-10-18, доводка О4: по центру, как пустые таблицы других разделов.
                FontSize = 14, Margin = new Thickness(24, 56, 24, 0), TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 520,
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
        var b = UiKit.Seg($"{text}  {count}", _tab == key);
        b.Click += (_, _) => { _tab = key; Render(); };
        _tabs.Children.Add(b);
    }

    private Control Card(RentalDto r)
    {
        // 2026-10-02, редизайн: цветная полоса статуса, вещи — плашками, даты и залог — с пиктограммами.
        // 2026-10-02: срок сегодня или завтра — оранжевая полоса и отметка («уведомление о приближении срока»).
        var due = RentalDueNotifier.Kind(r);
        // 2026-10-18, этап О4 (правило аудита 4): цвет — только у того, что требует внимания (просрочка — красный, срок сегодня/
        // завтра и повреждение — оранжевый); обычный прокат «на руках» — нейтральный, без зелёного. Рамка карточки — обычная.
        var statusBrush = r.IsOverdue ? "BrushDanger" : due is RentalDueNotifier.DueKind.Today or RentalDueNotifier.DueKind.Tomorrow ? "BrushWarning"
            : !r.IsActive && r.Condition == "damaged" ? "BrushWarning" : "BrushBorderStrong";
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = true };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        var outer = new Grid { ColumnDefinitions = new ColumnDefinitions("6,*") };
        var strip = new Border();
        Use(strip, Border.BackgroundProperty, statusBrush);
        outer.Children.Add(strip);

        // 2026-10-18, доводка О4: колонки — вещи · срок · залог · «Принять возврат» (раньше середина карточки пустовала).
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), Margin = new Thickness(16, 14, 16, 14) };
        grid.Classes.Add("no-reflow"); // 2026-10-05: карточка на телефоне — в две строки (_narrow ниже)
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
            head.Children.Add(Badge(T("на руках", "колдо", "out", "dışarıda", "qo'lda"), "BrushTextSoft"));
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
        // 2026-10-18, доводка О4: колонка срока — только у владельца (в кассе на 1024 рядом с «Принять возврат» ей тесно).
        var withTerm = _readOnly && !_narrow;
        dates.Children.Add(Icon(Icons.Calendar, r.IsOverdue && !withTerm ? "BrushDanger" : "BrushTextSoft"));
        // 2026-10-18, доводка О4: у владельца срок — отдельной колонкой; здесь — когда выдано и на сколько суток.
        var term = TermDays(r);
        // 2026-10-21 (пакет 5, п. 52): в кассе у возвращённого — и когда вернули: список «Возвращены» идёт по этому времени.
        var datesText = !withTerm
            ? $"{D(r.DateFrom)} — {D(r.DateTo)}" + (!r.IsActive && r.ReturnedAt is { } backAt
                ? " · " + T("возвращено", "кайтарылды", "returned", "iade", "qaytarildi") + " " + backAt.LocalDateTime.ToString("dd.MM HH:mm", Ru)
                : "")
            : T($"выдано {D(r.DateFrom)}", $"берилди {D(r.DateFrom)}", $"given {D(r.DateFrom)}", $"verildi {D(r.DateFrom)}", $"berildi {D(r.DateFrom)}")
              + (term > 0 ? " · " + T($"на {term} сут.", $"{term} суткага", $"for {term} d", $"{term} gün", $"{term} sutkaga") : "");
        dates.Children.Add(Text(datesText + (string.IsNullOrWhiteSpace(r.Note) ? "" : " · " + r.Note), 13, FontWeight.Normal,
            r.IsOverdue && !withTerm ? "BrushDanger" : "BrushTextSoft", wrap: true));
        left.Children.Add(dates);
        grid.Children.Add(left);
        if (withTerm)
        {
            var termBlock = new StackPanel { Spacing = 2, MinWidth = 150, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            termBlock.Children.Add(Text(r.IsActive
                ? T("Вернуть до", "Кайтаруу мөөнөтү", "Return by", "İade tarihi", "Qaytarish muddati")
                : T("Возвращено", "Кайтарылды", "Returned", "İade edildi", "Qaytarildi"), 12, FontWeight.SemiBold, "BrushTextSoft"));
            // 2026-10-21 (пакет 5, п. 52): у возвращённых — и время возврата: по нему видно, почему порядок такой.
            termBlock.Children.Add(Text(r.IsActive ? D(r.DateTo) : r.ReturnedAt is { } back ? back.LocalDateTime.ToString("dd.MM.yyyy HH:mm", Ru) : "—",
                14.5, FontWeight.Bold, r.IsOverdue ? "BrushDanger" : "BrushText"));
            // Сколько осталось — только у обычного проката: у просроченного и «сегодня / завтра» это уже сказано отметкой.
            if (r.IsActive && !r.IsOverdue && due is not (RentalDueNotifier.DueKind.Today or RentalDueNotifier.DueKind.Tomorrow)
                && r.DateTo is { } to && (to.Date - DateTime.Today).Days is var left2 and > 1)
                termBlock.Children.Add(Text(T($"через {left2} дн.", $"{left2} күндөн кийин", $"in {left2} days", $"{left2} gün sonra", $"{left2} kundan keyin"), 12, FontWeight.Normal, "BrushTextSoft"));
            Grid.SetColumn(termBlock, 1);
            grid.Children.Add(termBlock);
        }

        // 2026-10-18, доводка О4: у владельца (без «Принять возврат») залог — вплотную к правому краю карточки.
        var deposit = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Margin = new Thickness(16, 0, _readOnly ? 0 : 16, 0), VerticalAlignment = VerticalAlignment.Center, MinWidth = 210, MaxWidth = 280 };
        Use(deposit, Border.BackgroundProperty, "BrushPanelSoft");
        var depGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var depIcon = Icon(r.IsDocumentDeposit ? Icons.IdCard : Icons.Cash, "BrushTextSoft"); // 2026-10-18, этап О4: значок без жёлтого
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
        Grid.SetColumn(deposit, 2);
        grid.Children.Add(deposit);
        if (_narrow)
        {
            // 2026-10-05: телефон — вещи и даты первой строкой, залог и «Принять возврат» — второй.
            grid.ColumnDefinitions = new ColumnDefinitions("*,Auto");
            grid.RowDefinitions = new RowDefinitions("Auto,10,Auto");
            grid.Margin = new Thickness(12, 12, 12, 12);
            Grid.SetColumnSpan(left, 2);
            Grid.SetRow(deposit, 2);
            Grid.SetColumn(deposit, 0);
            deposit.Margin = new Thickness(0, 0, 8, 0);
            deposit.MinWidth = 0;
            deposit.HorizontalAlignment = HorizontalAlignment.Left;
        }

        if (r.IsActive && !_readOnly)
        {
            // 2026-10-18, этап О4 (правило аудита 1): жёлтая на экране одна — «+ Новый прокат»; «Принять возврат» в строках — обводкой.
            var ret = UiKit.Ghost(this, T("Принять возврат", "Кайтарууну кабыл алуу", "Take back", "İadeyi al", "Qaytarishni qabul qilish"));
            ret.VerticalAlignment = VerticalAlignment.Center;
            ret.Click += async (_, _) => await ReturnAsync(r).ConfigureAwait(true);
            Grid.SetColumn(ret, _narrow ? 1 : 3);
            Grid.SetRow(ret, _narrow ? 2 : 0);
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

    /// <summary>2026-10-18, доводка О4: на сколько суток выдано (по датам проката, без запросов).</summary>
    private static int TermDays(RentalDto r) => r.DateFrom is { } from && r.DateTo is { } to ? Math.Max(0, (to.Date - from.Date).Days) : 0;

    private static int OverdueDays(RentalDto r) => r.DateTo is { } to ? Math.Max(1, (DateTime.Today - to.Date).Days) : 1;
    internal static string D(DateTime? d) => d?.ToString("dd.MM.yyyy", Ru) ?? "—";
    internal static string Money(double v) => v.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private TextBlock Text(string text, double size, FontWeight weight, string brush, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        Use(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>2026-10-18, этап О4 (правило аудита 4): отметка мягкой плашкой — белый текст на оранжевой/зелёной заливке
    /// читался плохо; красная — только просрочка, оранжевая мягкая — срок/повреждение, остальное — нейтральное.</summary>
    private Border Badge(string text, string brush)
    {
        var (bg, fg) = brush switch
        {
            "BrushDanger" => ("BrushDangerSoft", "BrushDangerOnSoft"),
            "BrushWarning" => ("BrushWarningSoft", "BrushText"),
            _ => ("BrushPanelSoft", brush),
        };
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2), VerticalAlignment = VerticalAlignment.Center };
        Use(b, Border.BackgroundProperty, bg);
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.Bold };
        Use(t, TextBlock.ForegroundProperty, fg);
        b.Child = t;
        return b;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>Общие кнопки окон проката — в цветах темы, как в окне выбора размера.</summary>
internal static class UiKit
{
    // 2026-10-18, этап О3 (правила аудита 1–2): цвета кнопок UiKit — общими классами PosDialogStyles.axaml, а не
    // кистями на месте: главная — sec-primary (акцент темы, у «NurMarket» жёлтая), остальные — sec-outline (обводка
    // цветом рамки, без заливки), чип — sec-chip, выбранный чип — active (тёмно-синий, как период «Сводки»). Так у
    // них есть и наведение по теме (раньше Fluent красил наведённую кнопку серым). Размеры — прежние.
    // 2026-10-22 (модалки, пакет 0): у кнопок UiKit — необязательные isDefault (Enter нажимает её) и isCancel (Esc и ✕
    // ModalShell нажимают её). Раньше в ~25 окнах из кода Esc и Enter не работали. Умолчания — false, вид прежний (46 px).
    public static Button Primary(Control host, string text, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button
        {
            Content = text, Height = 46, Padding = new Thickness(20, 0), CornerRadius = new CornerRadius(12), FontWeight = FontWeight.Bold, FontSize = 14.5,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
            IsDefault = isDefault, IsCancel = isCancel,
        };
        b.Classes.Add("sec-primary");
        return b;
    }

    public static Button Ghost(Control host, string text, bool isCancel = false, bool isDefault = false)
    {
        var b = new Button
        {
            Content = text, Height = 46, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Focusable = false,
            IsDefault = isDefault, IsCancel = isCancel,
        };
        b.Classes.Add("sec-outline");
        return b;
    }

    /// <summary>2026-10-22 (модалки, пакет 0): второстепенная кнопка с обводкой — то же, что <see cref="Ghost"/> (название по
    /// стандарту модалки: Primary / Secondary / Danger).</summary>
    public static Button Secondary(Control host, string text, bool isCancel = false) => Ghost(host, text, isCancel);

    /// <summary>2026-10-22 (модалки, пакет 0): опасная главная («Удалить», «Списать») — красная заливка (sec-primary danger)
    /// вместо жёлтой. Enter по умолчанию её не нажимает.</summary>
    public static Button Danger(Control host, string text, bool isDefault = false)
    {
        var b = Primary(host, text, isDefault);
        b.Classes.Add("danger");
        return b;
    }

    public static Button Chip(Control host, string text, bool selected)
    {
        var b = new Button
        {
            Content = text, MinHeight = 40, Padding = new Thickness(16, 6), CornerRadius = new CornerRadius(20), FontSize = 14,
            BorderThickness = new Thickness(1), Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Classes.Add("sec-chip");
        SetActive(b, selected);
        return b;
    }

    /// <summary>2026-10-18, этап О3: выбранный чип или сегмент (класс active) — для окон, которые переключают его на месте,
    /// а не пересоздают (сортировка в «Долгах»).</summary>
    public static void SetActive(Button b, bool active)
    {
        b.Classes.Set("active", active);
        b.FontWeight = active ? FontWeight.Bold : FontWeight.Normal;
    }

    /// <summary>2026-10-18, этап О3: сегмент выбора (период, фильтр) — Button.sec-seg, выбранный — active; группу оборачивает
    /// <see cref="Segment"/>. Вид — как период «Сводки» и modal-seg кассы.</summary>
    public static Button Seg(string text, bool active)
    {
        // 2026-10-18, ревью О3: сегмент доступен с клавиатуры — Tab, Enter/Пробел выбирают период (вид прежний).
        var b = new Button { Content = text };
        b.Classes.Add("sec-seg");
        b.Classes.Set("active", active);
        Avalonia.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>2026-10-20, проверка фильтров: даты выбранного периода рядом с сегментами («01.10–20.10», PeriodRange.Label) —
    /// видно, за какие дни список. Подпись обновляет окно при смене периода.</summary>
    public static Control WithPeriodLabel(Control segment, TextBlock label)
    {
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(10, 0, 0, 0);
        label.FontSize = 13;
        // Окно пересобирает ряд фильтров (Филиалы) — подпись переезжает из прежнего ряда.
        (label.Parent as Panel)?.Children.Remove(label);
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("BrushTextSoft"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(segment);
        row.Children.Add(label);
        return row;
    }

    /// <summary>2026-10-18, этап О3: рамка группы сегментов (Border.sec-segment) вокруг готовой панели или кнопок.</summary>
    public static Border Segment(Panel items)
    {
        if (items is StackPanel stack)
            stack.Spacing = 2;
        var border = new Border { Child = items };
        border.Classes.Add("sec-segment");
        return border;
    }

    /// <summary>2026-10-18, этап О3 (правило аудита 3): плитка показателя — подпись переносится, сумма уменьшается
    /// целиком (Viewbox), а не обрезается «…»; красный — только для долга/убытка (danger). Плитки кладут в KpiTileGrid.</summary>
    public static Border Kpi(string label, string value, bool danger = false)
    {
        var caption = new TextBlock { Text = label };
        caption.Classes.Add("sec-kpi-label");
        var number = new TextBlock { Text = value };
        number.Classes.Add("sec-kpi-value");
        if (danger)
            number.Classes.Add("danger");
        var fit = new Viewbox { Child = number };
        fit.Classes.Add("sec-kpi-fit");
        var tile = new Border { Child = new StackPanel { Spacing = 4, Children = { caption, fit } } };
        tile.Classes.Add("sec-kpi");
        return tile;
    }

    /// <summary>2026-10-18, доводка О4: подсказка «пусто — что делать» поверх пустой таблицы (в той же ячейке сетки, что и
    /// таблица). Показывается только после того, как список хотя бы раз изменился (загрузка прошла), — пока данные идут,
    /// её нет. Таблицу переносят в другое место (встроенные «Продажи») — подсказка переезжает следом.</summary>
    public static void EmptyHint(DataGrid grid, string text)
    {
        var hint = new TextBlock
        {
            Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(24, 72, 24, 0),
            IsVisible = false, IsHitTestVisible = false,
        };
        hint.Bind(TextBlock.ForegroundProperty, grid.GetResourceObservable("BrushTextSoft"));
        System.Collections.Specialized.INotifyCollectionChanged? watched = null;
        var changed = false;

        void Place()
        {
            if (grid.Parent is not Grid host || ReferenceEquals(hint.Parent, host))
                return;
            (hint.Parent as Panel)?.Children.Remove(hint);
            Grid.SetRow(hint, Grid.GetRow(grid));
            Grid.SetColumn(hint, Grid.GetColumn(grid));
            host.Children.Add(hint);
        }

        void Update() => hint.IsVisible = changed && grid.ItemsSource is System.Collections.IEnumerable items && !items.GetEnumerator().MoveNext();

        void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            changed = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(Update);
        }

        void Watch()
        {
            if (watched != null)
                watched.CollectionChanged -= OnChanged;
            watched = grid.ItemsSource as System.Collections.Specialized.INotifyCollectionChanged;
            if (watched != null)
                watched.CollectionChanged += OnChanged;
            Update();
        }

        grid.PropertyChanged += (_, e) =>
        {
            // У DataGrid своё свойство ItemsSource (он не ItemsControl) — сравниваем по имени.
            if (e.Property.Name == nameof(DataGrid.ItemsSource))
                Watch();
        };
        grid.AttachedToVisualTree += (_, _) => Place();
        Watch();
    }

    public static TextBox Input(Control host, string watermark, double height = 44)
    {
        var t = new TextBox { Watermark = watermark, Height = height, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(10), FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
        return t;
    }

    /// <summary>2026-10-27 (приёмка 26.10, Д-4): поле секрета (ключ ИИ, токен) — то же, что Input, но без выдачи значения через UI Automation, пока оно скрыто точками.</summary>
    public static TextBox SecretInput(Control host, string watermark, double height = 44)
    {
        return new NurMarketKassa.AvaloniaHost.Views.Controls.SecretTextBox { Watermark = watermark, Height = height, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(10), FontSize = 15, VerticalContentAlignment = VerticalAlignment.Center };
    }

    public static TextBlock Label(Control host, string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, host.GetResourceObservable("BrushTextSoft"));
        return t;
    }
}
