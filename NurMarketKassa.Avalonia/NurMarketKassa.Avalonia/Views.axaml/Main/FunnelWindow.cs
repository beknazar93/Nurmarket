using System.Globalization;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-10-05, владелец: «к десктопу добавь воронку». Воронка продаж программы владельца: сделки (клиент, телефон,
/// сумма, заметка) по этапам «Новые → В работе → Договорились → Оплачено / Отказ», перенос кнопками ◀ ▶,
/// «WhatsApp» — написать клиенту, «Подтянуть из бота» — покупатели Telegram-бота за 30 дней новыми сделками.
/// У NurCRM нет API сделок (на 05.10 в документации и коде его нет) — воронка хранится на этом компьютере
/// (funnel.json в папке данных программы). Когда сервер даст API, хранение можно перенести туда.
/// 2026-10-05, владелец: «добавь в воронку drag-and-drop» — карточку можно перетащить в другой этап мышью
/// (на телефоне — долгое нажатие и перетаскивание; кнопки ◀ ▶ остаются). «В воронке указывай источник» —
/// у сделки есть источник (Telegram, WhatsApp, Instagram, звонок, пришёл в магазин…), он виден на карточке,
/// по нему есть фильтр и разбивка в итогах.
/// 2026-10-05, владелец: «добавь к воронке WhatsApp, Instagram тоже!» — у сделки есть Instagram клиента; из карточки —
/// переписка в WhatsApp (в Windows — во встроенном WhatsApp Web, вход по QR один раз) и в Instagram Direct; в шапке —
/// кнопки «WhatsApp» и «Instagram»: все чаты магазина рядом с воронкой.
/// </summary>
public sealed class FunnelWindow : Window, IOwnerSection
{
    private sealed class Card
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "";
        public string Phone { get; set; } = "";
        public double Amount { get; set; }
        public string Note { get; set; } = "";
        public string Stage { get; set; } = "new";
        public string? ChatId { get; set; }
        public string Source { get; set; } = "";
        public string Instagram { get; set; } = "";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly (string Key, string Title, string Brush)[] Stages =
    {
        ("new", T("Новые обращения", "Жаңы кайрылуулар", "New leads", "Yeni talepler", "Yangi murojaatlar"), "BrushAccent"),
        ("work", T("В работе", "Иште", "In progress", "Görüşülüyor", "Jarayonda"), "BrushWarning"),
        ("agreed", T("Договорились", "Макулдашылды", "Agreed", "Anlaşıldı", "Kelishildi"), "BrushAccentStrong"),
        ("paid", T("Оплачено", "Төлөндү", "Paid", "Ödendi", "To'landi"), "BrushSuccess"),
        ("lost", T("Отказ", "Баш тартуу", "Lost", "Kaybedildi", "Rad etildi"), "BrushDanger"),
    };

    private static readonly (string Key, string Title)[] SourceList =
    {
        ("telegram", "Telegram"),
        ("whatsapp", "WhatsApp"),
        ("instagram", "Instagram"),
        ("call", T("Звонок", "Чалуу", "Phone call", "Telefon", "Qo'ng'iroq")),
        ("visit", T("Пришёл в магазин", "Дүкөнгө келди", "Store visit", "Mağazaya geldi", "Do'konga keldi")),
        ("referral", T("По рекомендации", "Сунуш боюнча", "Referral", "Tavsiye", "Tavsiya bo'yicha")),
        ("site", T("Сайт", "Сайт", "Website", "Web sitesi", "Sayt")),
        ("other", T("Другое", "Башка", "Other", "Diğer", "Boshqa")),
    };

    private static string SourceTitle(string key) =>
        SourceList.FirstOrDefault(s => s.Key == key).Title ?? T("не указан", "көрсөтүлгөн эмес", "not set", "belirtilmedi", "ko'rsatilmagan");

    private readonly List<Card> _cards;
    private readonly Grid _root = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), Margin = new Thickness(24, 18, 24, 24) };
    private readonly StackPanel _board = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10) };
    private readonly Border _editor = new() { CornerRadius = new CornerRadius(14), Padding = new Thickness(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 12), IsVisible = false };
    private readonly TextBox _nameBox, _phoneBox, _amountBox, _noteBox, _instagramBox;
    private Card? _editing;
    private string _editingSource = "";
    private readonly WrapPanel _sourceChips = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _filter = new() { MinWidth = 190, Height = 46, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private string _filterSource = "*";
    private readonly ScrollViewer _scroll;
    private readonly List<(string Key, Border Column)> _columns = new();

    // Перетаскивание карточки.
    private Card? _dragCard;
    private Border? _dragView;
    private IPointer? _dragPointer;
    private Point _dragStart;
    private bool _dragging;
    private string? _hoverStage;
    private DispatcherTimer? _holdTimer;
    private DateTime _pressAt;
    private TopLevel? _dragTop;
    private readonly List<(Avalonia.Input.GestureRecognizers.ScrollGestureRecognizer Recognizer, bool H, bool V)> _pausedScroll = new();

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "funnel.json");

    public FunnelWindow()
    {
        Title = T("Воронка", "Воронка", "Sales funnel", "Satış hunisi", "Savdo voronkasi");
        Width = 1100;
        Height = 760;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        _cards = Load();

        // 2026-10-05, проверка на телефоне: в шапке-сетке кнопки вставали по одной в столбик и занимали пол-экрана —
        // подсказка теперь строкой сверху, кнопки — строкой с переносом.
        var head = new StackPanel { Spacing = 10 };
        var tools = new WrapPanel { Orientation = Orientation.Horizontal };
        var hint = new TextBlock
        {
            Text = T("Сделки по этапам: перетащите карточку в нужный этап (на телефоне — долгое нажатие) или нажмите ◀ ▶. «WhatsApp» и «Instagram» — переписка с клиентом.",
                "Этаптар боюнча келишимдер: карточканы керектүү этапка сүйрөңүз (телефондо — узак басуу) же ◀ ▶ басыңыз. «WhatsApp» жана «Instagram» — кардар менен кат алышуу.",
                "Deals by stage: drag a card to another stage (on a phone — long press) or press ◀ ▶. “WhatsApp” and “Instagram” — chat with the client.",
                "Aşamalara göre anlaşmalar: kartı başka aşamaya sürükleyin (telefonda — uzun basın) veya ◀ ▶ basın. «WhatsApp» ve «Instagram» — müşteriyle yazışma.",
                "Bosqichlar bo'yicha bitimlar: kartani kerakli bosqichga torting (telefonda — uzoq bosing) yoki ◀ ▶ ni bosing. «WhatsApp» va «Instagram» — mijoz bilan yozishma."),
            FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        head.Children.Add(hint);
        _filter.Items.Add(new ComboBoxItem { Content = T("Все источники", "Бардык булактар", "All sources", "Tüm kaynaklar", "Barcha manbalar"), Tag = "*" });
        foreach (var (key, title) in SourceList)
            _filter.Items.Add(new ComboBoxItem { Content = title, Tag = key });
        _filter.Items.Add(new ComboBoxItem { Content = T("Источник не указан", "Булагы көрсөтүлгөн эмес", "Source not set", "Kaynak belirtilmedi", "Manba ko'rsatilmagan"), Tag = "" });
        _filter.SelectedIndex = 0;
        _filter.SelectionChanged += (_, _) =>
        {
            _filterSource = (_filter.SelectedItem as ComboBoxItem)?.Tag as string ?? "*";
            Render();
        };
        _filter.Margin = new Thickness(0, 0, 10, 8);
        tools.Children.Add(_filter);
        var whatsApp = UiKit.Ghost(this, "WhatsApp");
        whatsApp.Margin = new Thickness(0, 0, 10, 8);
        whatsApp.Click += (_, _) => OpenChats(WhatsAppWebUrl, "WhatsApp Web", "https://wa.me/");
        tools.Children.Add(whatsApp);
        var instagram = UiKit.Ghost(this, "Instagram");
        instagram.Margin = new Thickness(0, 0, 10, 8);
        instagram.Click += (_, _) => OpenChats(InstagramDirectUrl, "Instagram Direct", InstagramDirectUrl);
        tools.Children.Add(instagram);
        var fromBot = UiKit.Ghost(this, T("Подтянуть из бота", "Боттон алуу", "Import from bot", "Bottan al", "Botdan olish"));
        fromBot.Margin = new Thickness(0, 0, 10, 8);
        fromBot.Click += async (_, _) => await ImportFromBotAsync().ConfigureAwait(true);
        tools.Children.Add(fromBot);
        var add = UiKit.Primary(this, "+  " + T("Новая сделка", "Жаңы келишим", "New deal", "Yeni anlaşma", "Yangi bitim"));
        add.Margin = new Thickness(0, 0, 10, 8);
        add.Click += (_, _) => OpenEditor(null);
        tools.Children.Add(add);
        head.Children.Add(tools);
        _root.Children.Add(head);

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        Grid.SetRow(_status, 1);
        _root.Children.Add(_status);

        // Редактор сделки (вместо отдельного окна — проще и на телефоне).
        Use(_editor, Border.BackgroundProperty, "BrushPanel");
        Use(_editor, Border.BorderBrushProperty, "BrushBorder");
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,140"), RowDefinitions = new RowDefinitions("Auto,8,Auto,8,Auto,Auto,10,Auto") };
        _nameBox = UiKit.Input(this, T("Клиент (имя)", "Кардар (аты)", "Client (name)", "Müşteri (ad)", "Mijoz (ism)"));
        _phoneBox = UiKit.Input(this, T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"));
        _amountBox = UiKit.Input(this, T("Сумма, сом", "Сумма, сом", "Amount, som", "Tutar, som", "Summa, so'm"));
        _noteBox = UiKit.Input(this, T("Заметка: что хочет, когда перезвонить", "Эскертме: эмне каалайт, качан чалуу", "Note: what they want, when to call back", "Not: ne istiyor, ne zaman aranacak", "Izoh: nima xohlaydi, qachon qo'ng'iroq qilish"));
        // 2026-10-05: длинная подсказка на телефоне обрезалась («Instagram к») — короткая, понятная на всех языках.
        _instagramBox = UiKit.Input(this, "@instagram");
        ToolTip.SetTip(_instagramBox, T("Instagram клиента (@имя)", "Кардардын Instagram'ы (@аты)", "Client's Instagram (@name)", "Müşterinin Instagram'ı (@ad)", "Mijozning Instagrami (@nomi)"));
        form.Children.Add(_nameBox);
        Grid.SetColumn(_phoneBox, 2);
        form.Children.Add(_phoneBox);
        Grid.SetColumn(_amountBox, 4);
        form.Children.Add(_amountBox);
        Grid.SetRow(_instagramBox, 2);
        form.Children.Add(_instagramBox);
        Grid.SetRow(_noteBox, 2);
        Grid.SetColumn(_noteBox, 2);
        Grid.SetColumnSpan(_noteBox, 3);
        form.Children.Add(_noteBox);
        var sourceLabel = UiKit.Label(this, T("Источник — откуда пришёл клиент", "Булак — кардар кайдан келди", "Source — where the client came from",
            "Kaynak — müşteri nereden geldi", "Manba — mijoz qayerdan keldi"));
        Grid.SetRow(sourceLabel, 4);
        Grid.SetColumnSpan(sourceLabel, 5);
        form.Children.Add(sourceLabel);
        _sourceChips.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(_sourceChips, 5);
        Grid.SetColumnSpan(_sourceChips, 5);
        form.Children.Add(_sourceChips);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = UiKit.Primary(this, T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Click += (_, _) => SaveEditor();
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => _editor.IsVisible = false;
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 7);
        Grid.SetColumnSpan(buttons, 5);
        form.Children.Add(buttons);
        _editor.Child = form;
        Grid.SetRow(_editor, 2);
        _root.Children.Add(_editor);

        var scroll = _scroll = new ScrollViewer
        {
            Content = _board,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 3);
        _root.Children.Add(scroll);
        Content = _root;

        NarrowLayout.Attach(this, 700, narrow => _root.Margin = narrow ? new Thickness(10, 8, 10, 10) : new Thickness(24, 18, 24, 24));
        Render();
    }

    public void AsOwnerSection() => _root.Margin = OwnerSectionLayout.Margin;

    private void Render()
    {
        _board.Children.Clear();
        _columns.Clear();
        _hoverStage = null;
        var visible = _cards.Where(c => _filterSource == "*" || c.Source == _filterSource).ToList();
        foreach (var (key, title, brush) in Stages)
        {
            var cards = visible.Where(c => c.Stage == key).OrderByDescending(c => c.UpdatedAt).ToList();
            var column = new Border { Width = 280, MinHeight = 160, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(10) };
            Use(column, Border.BackgroundProperty, "BrushPanelSoft");
            Use(column, Border.BorderBrushProperty, "BrushBorder");
            var stack = new StackPanel { Spacing = 8 };
            var header = new StackPanel { Spacing = 2, Margin = new Thickness(2, 0, 2, 4) };
            var titleText = new TextBlock { Text = $"{title} · {cards.Count}", FontSize = 15, FontWeight = FontWeight.Bold };
            Use(titleText, TextBlock.ForegroundProperty, brush);
            header.Children.Add(titleText);
            var sum = new TextBlock { Text = Money(cards.Sum(c => c.Amount)), FontSize = 12.5 };
            Use(sum, TextBlock.ForegroundProperty, "BrushTextSoft");
            header.Children.Add(sum);
            stack.Children.Add(header);
            foreach (var card in cards)
                stack.Children.Add(CardView(card));
            column.Child = stack;
            _board.Children.Add(column);
            _columns.Add((key, column));
        }
        var open = visible.Where(c => c.Stage is not ("paid" or "lost")).ToList();
        _status.Text = T($"В работе сделок: {open.Count} на {Money(open.Sum(c => c.Amount))}. Оплачено: {_cards.Count(c => c.Stage == "paid")}.",
            $"Иштеги келишимдер: {open.Count}, {Money(open.Sum(c => c.Amount))}. Төлөндү: {_cards.Count(c => c.Stage == "paid")}.",
            $"Open deals: {open.Count} worth {Money(open.Sum(c => c.Amount))}. Paid: {_cards.Count(c => c.Stage == "paid")}.",
            $"Açık anlaşmalar: {open.Count}, {Money(open.Sum(c => c.Amount))}. Ödendi: {_cards.Count(c => c.Stage == "paid")}.",
            $"Ochiq bitimlar: {open.Count}, {Money(open.Sum(c => c.Amount))}. To'landi: {_cards.Count(c => c.Stage == "paid")}.");
        // Откуда приходят клиенты: число сделок и сколько из них оплачено — по каждому источнику.
        var bySource = _cards.GroupBy(c => c.Source).OrderByDescending(g => g.Count())
            .Select(g => $"{SourceTitle(g.Key)} {g.Count()}" + (g.Any(c => c.Stage == "paid") ? $" ({T("оплачено", "төлөндү", "paid", "ödendi", "to'landi")} {g.Count(c => c.Stage == "paid")})" : ""))
            .ToList();
        if (bySource.Count > 0)
            _status.Text += "  " + T("Источники: ", "Булактар: ", "Sources: ", "Kaynaklar: ", "Manbalar: ") + string.Join(" · ", bySource) + ".";
    }

    private Control CardView(Card card)
    {
        var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(10), BorderThickness = new Thickness(1) };
        Use(border, Border.BackgroundProperty, "BrushPanel");
        Use(border, Border.BorderBrushProperty, "BrushBorder");
        var stack = new StackPanel { Spacing = 4 };
        var name = new TextBlock { Text = string.IsNullOrWhiteSpace(card.Name) ? "—" : card.Name, FontSize = 14.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Use(name, TextBlock.ForegroundProperty, "BrushText");
        stack.Children.Add(name);
        var meta = new TextBlock
        {
            Text = string.Join(" · ", new[] { card.Phone, card.Amount > 0 ? Money(card.Amount) : null }.Where(s => !string.IsNullOrWhiteSpace(s))),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        Use(meta, TextBlock.ForegroundProperty, "BrushTextSoft");
        stack.Children.Add(meta);
        var sourceText = new TextBlock { Text = SourceTitle(card.Source), FontSize = 11.5, FontWeight = FontWeight.SemiBold };
        // 2026-10-05, проверка на телефоне: тёмно-синий текст на синем значке в тёмной теме не читался — текст обычного цвета.
        Use(sourceText, TextBlock.ForegroundProperty, string.IsNullOrEmpty(card.Source) ? "BrushTextSoft" : "BrushText");
        var sourceBadge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Left,
            Child = sourceText,
        };
        Use(sourceBadge, Border.BackgroundProperty, string.IsNullOrEmpty(card.Source) ? "BrushPanelSoft" : "BrushAccentSoft");
        ToolTip.SetTip(sourceBadge, T("Источник", "Булак", "Source", "Kaynak", "Manba"));
        stack.Children.Insert(1, sourceBadge);
        if (!string.IsNullOrWhiteSpace(card.Note))
        {
            var note = new TextBlock { Text = card.Note, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            Use(note, TextBlock.ForegroundProperty, "BrushText");
            stack.Children.Add(note);
        }
        var row = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var index = Array.FindIndex(Stages, s => s.Key == card.Stage);
        if (index > 0)
            row.Children.Add(Small("◀", () => Move(card, Stages[index - 1].Key)));
        if (index < Stages.Length - 1)
            row.Children.Add(Small("▶", () => Move(card, Stages[index + 1].Key)));
        row.Children.Add(Small("✎", () => OpenEditor(card)));
        if (!string.IsNullOrWhiteSpace(card.Phone))
            row.Children.Add(Small("WhatsApp", () => OpenWhatsApp(card.Phone)));
        if (InstagramName(card.Instagram) is { } igName)
            row.Children.Add(Small("Instagram", () => OpenInstagram(igName)));
        // 2026-10-05, проверка на телефоне: значок 🗑 на Android не рисовался (пустая кнопка), а сделка удалялась с одного
        // нажатия. Теперь «✕», первое нажатие спрашивает «Удалить?», второе — удаляет.
        Button? delete = null;
        delete = Small("✕", () =>
        {
            if (delete!.Tag is not "confirm")
            {
                delete.Tag = "confirm";
                delete.Content = T("Удалить?", "Өчүрөлүбү?", "Delete?", "Silinsin mi?", "O'chirilsinmi?");
                Use(delete, Button.BackgroundProperty, "BrushDanger");
                return;
            }
            _cards.Remove(card);
            Save();
            Render();
        });
        row.Children.Add(delete);
        stack.Children.Add(row);
        border.Child = stack;
        if (!OperatingSystem.IsAndroid())
            border.Cursor = new Cursor(StandardCursorType.Hand);
        border.PointerPressed += (_, e) => DragPress(card, border, e);
        border.PointerMoved += DragMove;
        border.PointerReleased += DragRelease;
        border.PointerCaptureLost += (_, _) =>
        {
            // Касание во время перетаскивания может перехватить другой элемент (на телефоне — прокрутка) — палец
            // дальше ведём через окно целиком (_dragTop), поэтому перетаскивание не прерываем.
            if (_dragView == border && _dragging && _dragTop is null)
            {
                PosLogger.Log("Воронка: перетаскивание прервано — захват пальца забрал другой элемент.", "UI");
                ResetDrag();
                Render();
            }
        };
        border.DoubleTapped += (_, _) => OpenEditor(card);
        return border;
    }

    // ── Перетаскивание. Мышь: нажать и повести. Палец: подержать ~0,45 с (иначе это прокрутка доски) и повести.
    // Своё перетаскивание вместо DragDrop.DoDragDrop: системного drag-and-drop в Avalonia на Android нет.

    private void DragPress(Card card, Border view, PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(view).Properties.IsLeftButtonPressed)
            return;
        ResetDrag();
        _dragCard = card;
        _dragView = view;
        _dragPointer = e.Pointer;
        _dragStart = e.GetPosition(_board);
        _pressAt = DateTime.UtcNow;
        if (e.Pointer.Type == PointerType.Mouse)
        {
            e.Pointer.Capture(view);
            return;
        }
        var pointer = e.Pointer;
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer?.Stop();
            // На Android палец сразу «держит» то, во что попал (надпись внутри карточки) — это тоже карточка.
            // Захват у прокрутки доски (ScrollViewer) — значит, человек листает, перетаскивание не начинаем.
            if (_dragCard == card && !_dragging
                && (pointer.Captured is null || pointer.Captured == view || (pointer.Captured is Visual held && view.IsVisualAncestorOf(held))))
                StartDrag(pointer);
        };
        _holdTimer.Start();
    }

    private void StartDrag(IPointer pointer)
    {
        if (_dragView is null || _dragCard is null)
            return;
        _dragging = true;
        // 2026-10-05, проверка на телефоне: прокрутка доски пальцем (ScrollGestureRecognizer) забирала захват посреди
        // перетаскивания — карточка возвращалась на место. Пока карточку тащат, прокрутку пальцем выключаем.
        PauseScrollGestures(_dragView);
        pointer.Capture(_dragView);
        // Палец/мышь ведём через всё окно (туннель, в том числе уже обработанные события): если касание перехватит
        // прокрутка или оболочка окна, карточка всё равно едет за пальцем и кладётся в нужный этап.
        _dragTop = TopLevel.GetTopLevel(_dragView);
        _dragTop?.AddHandler(PointerMovedEvent, DragMove, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        _dragTop?.AddHandler(PointerReleasedEvent, DragRelease, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        PosLogger.Log($"Воронка: перетаскивание начато ({pointer.Type}, захват={(pointer.Captured == _dragView ? "карточка" : pointer.Captured?.GetType().Name ?? "нет")}).", "UI");
        _dragView.Opacity = 0.9;
        _dragView.BoxShadow = BoxShadows.Parse("0 10 28 0 #55000000");
        // Колонка с карточкой — поверх соседних, иначе карточка уходит «под» следующую колонку.
        foreach (var (key, column) in _columns)
            column.ZIndex = key == _dragCard.Stage ? 10 : 0;
        HighlightColumn(_dragCard.Stage);
    }

    private void DragMove(object? sender, PointerEventArgs e)
    {
        if (_dragCard is null || _dragView is null || e.Pointer != _dragPointer)
            return;
        var p = e.GetPosition(_board);
        if (!_dragging)
        {
            if (Math.Abs(p.X - _dragStart.X) + Math.Abs(p.Y - _dragStart.Y) < 8)
                return;
            // Палец сдвинулся раньше долгого нажатия (0,3 с) — это прокрутка, не перетаскивание. Считаем по времени
            // нажатия, а не только по таймеру: Android начинает вести палец сразу после долгого нажатия, и первое
            // движение могло прийти раньше срабатывания таймера.
            if (e.Pointer.Type != PointerType.Mouse && DateTime.UtcNow - _pressAt < TimeSpan.FromMilliseconds(300))
            {
                ResetDrag();
                return;
            }
            StartDrag(e.Pointer);
        }
        e.Handled = true;
        _dragView.RenderTransform = new TranslateTransform(p.X - _dragStart.X, p.Y - _dragStart.Y);
        HighlightColumn(StageAt(p));
        // У края — прокрутить доску, чтобы дотянуть до «Оплачено» / «Отказ».
        var inScroll = e.GetPosition(_scroll);
        var maxX = Math.Max(0, _scroll.Extent.Width - _scroll.Viewport.Width);
        if (inScroll.X < 48 && _scroll.Offset.X > 0)
            _scroll.Offset = _scroll.Offset.WithX(Math.Max(0, _scroll.Offset.X - 24));
        else if (inScroll.X > _scroll.Bounds.Width - 48 && _scroll.Offset.X < maxX)
            _scroll.Offset = _scroll.Offset.WithX(Math.Min(maxX, _scroll.Offset.X + 24));
    }

    private void DragRelease(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragCard is null || e.Pointer != _dragPointer)
            return;
        var card = _dragCard;
        var dragging = _dragging;
        var target = dragging ? StageAt(e.GetPosition(_board)) : null;
        if (dragging)
            PosLogger.Log($"Воронка: карточка отпущена над этапом «{target ?? "—"}».", "UI");
        ResetDrag();
        if (!dragging)
            return;
        e.Handled = true;
        if (target is not null && target != card.Stage)
            Move(card, target);
        else
            Render();
    }

    private void PauseScrollGestures(Visual from)
    {
        foreach (var presenter in from.GetVisualAncestors().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>())
        {
            foreach (var r in presenter.GestureRecognizers.OfType<Avalonia.Input.GestureRecognizers.ScrollGestureRecognizer>())
            {
                _pausedScroll.Add((r, r.CanHorizontallyScroll, r.CanVerticallyScroll));
                r.CanHorizontallyScroll = false;
                r.CanVerticallyScroll = false;
            }
        }
    }

    private void ResumeScrollGestures()
    {
        foreach (var (r, h, v) in _pausedScroll)
        {
            r.CanHorizontallyScroll = h;
            r.CanVerticallyScroll = v;
        }
        _pausedScroll.Clear();
    }

    private void ResetDrag()
    {
        _holdTimer?.Stop();
        ResumeScrollGestures();
        if (_dragTop is { } top)
        {
            _dragTop = null;
            top.RemoveHandler(PointerMovedEvent, DragMove);
            top.RemoveHandler(PointerReleasedEvent, DragRelease);
        }
        _holdTimer = null;
        var view = _dragView;
        var pointer = _dragPointer;
        var wasDragging = _dragging;
        _dragCard = null;
        _dragView = null;
        _dragPointer = null;
        _dragging = false;
        if (view is not null && wasDragging)
        {
            view.RenderTransform = null;
            view.Opacity = 1;
            view.BoxShadow = default;
            HighlightColumn(null);
        }
        if (pointer is not null && view is not null && pointer.Captured == view)
            pointer.Capture(null);
    }

    /// <summary>Этап под точкой (координаты доски): колонка, в полосу которой по горизонтали попала точка.</summary>
    private string? StageAt(Point p)
    {
        foreach (var (key, column) in _columns)
        {
            if (column.TranslatePoint(new Point(0, 0), _board) is { } o && p.X >= o.X - 6 && p.X <= o.X + column.Bounds.Width + 6)
                return key;
        }
        return null;
    }

    private void HighlightColumn(string? key)
    {
        if (key == _hoverStage)
            return;
        _hoverStage = key;
        foreach (var (k, column) in _columns)
        {
            var on = k == key && _dragCard is not null && k != _dragCard.Stage;
            Use(column, Border.BorderBrushProperty, on ? "BrushAccent" : "BrushBorder");
            column.BorderThickness = new Thickness(on ? 2 : 1);
            column.Padding = new Thickness(on ? 9 : 10);
        }
    }

    private Button Small(string text, Action click)
    {
        var b = UiKit.Ghost(this, text);
        b.Height = 34;
        b.Padding = new Thickness(10, 0);
        b.FontSize = 13;
        b.Margin = new Thickness(0, 0, 6, 6);
        b.Click += (_, _) => click();
        return b;
    }

    private void Move(Card card, string stage)
    {
        card.Stage = stage;
        card.UpdatedAt = DateTime.Now;
        Save();
        Render();
    }

    private void OpenEditor(Card? card)
    {
        _editing = card;
        _nameBox.Text = card?.Name ?? "";
        _phoneBox.Text = card?.Phone ?? "";
        _amountBox.Text = card is { Amount: > 0 } ? card.Amount.ToString("0.##", CultureInfo.InvariantCulture) : "";
        _noteBox.Text = card?.Note ?? "";
        _instagramBox.Text = card?.Instagram ?? "";
        _editingSource = card?.Source ?? (_filterSource is "*" ? "" : _filterSource);
        RenderSourceChips();
        _editor.IsVisible = true;
        _nameBox.Focus();
    }

    private void RenderSourceChips()
    {
        _sourceChips.Children.Clear();
        foreach (var (key, title) in SourceList)
        {
            var chip = UiKit.Chip(this, title, key == _editingSource);
            chip.MinHeight = 34;
            chip.Padding = new Thickness(12, 4);
            chip.FontSize = 13;
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Click += (_, _) =>
            {
                // Повторное нажатие снимает выбор.
                _editingSource = _editingSource == key ? "" : key;
                RenderSourceChips();
            };
            _sourceChips.Children.Add(chip);
        }
    }

    private void SaveEditor()
    {
        var name = (_nameBox.Text ?? "").Trim();
        var phone = (_phoneBox.Text ?? "").Trim();
        var instagram = InstagramName(_instagramBox.Text) ?? "";
        if (name.Length == 0 && phone.Length == 0 && instagram.Length == 0)
            return;
        var card = _editing ?? new Card();
        card.Name = name;
        card.Phone = phone;
        card.Amount = double.TryParse((_amountBox.Text ?? "").Replace(',', '.').Replace(" ", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? Math.Max(0, a) : 0;
        card.Note = (_noteBox.Text ?? "").Trim();
        card.Source = _editingSource;
        card.Instagram = instagram;
        // Клиент из Instagram, а источник не выбрали — ставим Instagram.
        if (card.Source.Length == 0 && instagram.Length > 0 && phone.Length == 0)
            card.Source = "instagram";
        card.UpdatedAt = DateTime.Now;
        if (_editing is null)
            _cards.Add(card);
        _editor.IsVisible = false;
        Save();
        Render();
    }

    private async Task ImportFromBotAsync()
    {
        try
        {
            var customers = await App.GetRequiredService<ServerTelegramBotApi>()
                .GetCustomersAsync(DateTime.Today.AddDays(-29), DateTime.Today.AddDays(1)).ConfigureAwait(true);
            var added = 0;
            foreach (var c in customers)
            {
                if (string.IsNullOrWhiteSpace(c.ChatId) || _cards.Any(x => x.ChatId == c.ChatId))
                    continue;
                _cards.Add(new Card
                {
                    Name = string.IsNullOrWhiteSpace(c.Name) ? (c.Username ?? "Telegram") : c.Name,
                    ChatId = c.ChatId,
                    Source = "telegram",
                    Note = T($"Telegram{(string.IsNullOrWhiteSpace(c.Username) ? "" : " @" + c.Username)}: сообщений {c.Messages}, заказов {c.Orders}",
                        $"Telegram{(string.IsNullOrWhiteSpace(c.Username) ? "" : " @" + c.Username)}: билдирүүлөр {c.Messages}, заказдар {c.Orders}",
                        $"Telegram{(string.IsNullOrWhiteSpace(c.Username) ? "" : " @" + c.Username)}: messages {c.Messages}, orders {c.Orders}",
                        $"Telegram{(string.IsNullOrWhiteSpace(c.Username) ? "" : " @" + c.Username)}: mesaj {c.Messages}, sipariş {c.Orders}",
                        $"Telegram{(string.IsNullOrWhiteSpace(c.Username) ? "" : " @" + c.Username)}: xabarlar {c.Messages}, buyurtmalar {c.Orders}"),
                    Stage = c.Orders > 0 ? "agreed" : "new",
                    CreatedAt = c.LastAt?.LocalDateTime ?? DateTime.Now,
                    UpdatedAt = c.LastAt?.LocalDateTime ?? DateTime.Now,
                });
                added++;
            }
            Save();
            Render();
            _status.Text = T($"Из бота добавлено новых сделок: {added}.", $"Боттон кошулду: {added}.", $"New deals from the bot: {added}.",
                $"Bottan eklenen: {added}.", $"Botdan qo'shildi: {added}.") + " " + _status.Text;
        }
        catch (Exception ex)
        {
            _status.Text = T("Не удалось получить покупателей бота: ", "Боттун кардарлары алынган жок: ", "Could not get bot customers: ",
                "Bot müşterileri alınamadı: ", "Bot mijozlari olinmadi: ") + ex.Message;
        }
    }

    private const string WhatsAppWebUrl = "https://web.whatsapp.com";
    private const string InstagramDirectUrl = "https://www.instagram.com/direct/inbox/";

    /// <summary>«@shop.kg», «instagram.com/shop.kg/», «shop.kg» → «shop.kg». null — не похоже на имя Instagram.</summary>
    private static string? InstagramName(string? text)
    {
        var t = (text ?? "").Trim();
        var m = System.Text.RegularExpressions.Regex.Match(t, @"instagram\.com/([A-Za-z0-9._]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
            t = m.Groups[1].Value;
        t = t.TrimStart('@').Trim('/');
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"^[A-Za-z0-9._]{1,30}$") ? t : null;
    }

    /// <summary>WhatsApp клиента: в Windows — чат во встроенном WhatsApp Web (вход по QR-коду сохраняется), иначе —
    /// приложение WhatsApp через wa.me.</summary>
    private void OpenWhatsApp(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
            return;
        // Местный номер «0700 123 456» → международный 996700123456 (WhatsApp понимает только его).
        if (digits.Length == 10 && digits[0] == '0')
            digits = "996" + digits[1..];
        else if (digits.Length == 9)
            digits = "996" + digits;
        OpenChats(WhatsAppWebUrl + "/send?phone=" + digits, "WhatsApp Web", "https://wa.me/" + digits);
    }

    private void OpenInstagram(string name) =>
        OpenChats("https://ig.me/m/" + name, "Instagram Direct", "https://ig.me/m/" + name);

    /// <summary>Чат: Windows — окно со встроенным браузером (как раздел «WhatsApp»), Android и Linux — приложение или браузер.</summary>
    private async void OpenChats(string windowsUrl, string caption, string otherUrl)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                new CrmWebViewWindow(windowsUrl, caption).Show();
                return;
            }
            if (TopLevel.GetTopLevel(this)?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri(otherUrl)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Воронка: {caption} не открылся ({ex.Message}).", "WARNING");
        }
    }

    private static List<Card> Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var cards = JsonSerializer.Deserialize<List<Card>>(File.ReadAllText(StorePath)) ?? new List<Card>();
                // Сделки, подтянутые из бота до появления поля «Источник», — из Telegram.
                foreach (var c in cards)
                {
                    c.Source ??= "";
                    if (c.Source.Length == 0 && !string.IsNullOrWhiteSpace(c.ChatId))
                        c.Source = "telegram";
                }
                return cards;
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Воронка: файл не прочитан ({ex.Message}).", "WARNING");
        }
        return new List<Card>();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cards, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Воронка: не сохранена ({ex.Message}).", "WARNING");
        }
    }

    private static string Money(double v) => v.ToString("N0", Ru) + " " + T("сом", "сом", "som", "som", "so'm");

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
