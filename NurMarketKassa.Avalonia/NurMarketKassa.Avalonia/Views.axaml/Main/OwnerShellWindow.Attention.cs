using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-18, редизайн сводки (этап О1, макет design/NurMarket-design «Owner»): блок «Требует внимания» справа от
/// графика. Собирает то, что программа владельца уже знает, без новых запросов к серверу: сроки проката (RentalDueNotifier,
/// раньше — только карточка в меню), долги клиентов (карточка «Долги», OwnerShellWindow.Debts.cs), новые заказы с сайта
/// (число у пункта меню) и товары на исходе (каталог в памяти, ApplyLowStock). Строка: точка (красная — просрочено / нет
/// в наличии, оранжевая — ждёт), заголовок, серое пояснение и действие справа — переход тем же пунктом меню, что слева.
/// Здесь же — «Спросить Нур Советника» в шапке сводки.</summary>
public partial class OwnerShellWindow
{
    private sealed record AttentionStock(string Name, double Quantity, string? Unit);

    /// <param name="Approx">2026-10-18, ревью после живой проверки: сумма не точная (быстрый список или «уточняется») — «≈».</param>
    /// <param name="Note">Пояснение к «≈» (общий текст карточки «Долги»).</param>
    private sealed record AttentionDebts(double Total, int Count, double Old, bool Approx = false, string? Note = null);

    /// <summary>Сколько товаров на исходе показать строками (остальные — «Ещё товаров на исходе: N»).</summary>
    private const int AttentionStockRows = 3;

    private readonly List<AttentionStock> _attnStock = new();
    private int _attnStockMore;
    private AttentionDebts? _attnDebts;

    // 2026-10-18, ревью О1: «Всё в порядке — срочных дел нет» показывался, когда данные просто ещё не загрузились (конструктор,
    // офлайн, ошибка долгов). Теперь — «Загрузка…», пока хоть один источник не получен, «Всё в порядке» — когда все получены и пусты.
    private bool _attnStockLoaded;
    private bool _attnDebtsLoaded;
    /// <summary>2026-10-18, девятый круг (п.8): долги не загрузились — строка «Не удалось загрузить долги» с кнопкой «Повторить».</summary>
    private bool _attnDebtsFailed;
    private readonly DateTime _attnCreatedUtc = DateTime.UtcNow;

    /// <summary>Сроки проката известны: сфера без проката, проверка прошла — или прошло 3 минуты (RentalDueNotifier
    /// на 403/404 молча перестаёт спрашивать, первый раз — через 20 с после запуска).</summary>
    private bool AttentionRentalsLoaded =>
        !MarketSpheres.HasRentals // 2026-10-20: прокат во всех сферах
        || RentalDueNotifier.Last is not null
        || DateTime.UtcNow - _attnCreatedUtc > TimeSpan.FromMinutes(3);

    /// <summary>2026-10-20: подписка на «каталог в памяти обновлён» сделана (см. RenderAttention).</summary>
    private bool _attnCatalogHooked;

    /// <summary>2026-10-20, аудит программы владельца: при первом показе «Требует внимания» был неполным — каталог
    /// догружался позже, и «Нет в наличии» / «на исходе» появлялись только после смены периода. Теперь блок
    /// пересчитывается, когда каталог в памяти обновился (CatalogCacheService.CacheUpdated, приходит на UI-потоке).</summary>
    private void OnAttentionCatalogUpdated()
    {
        if (_loggingOut)
            return;
        ApplyLowStock();
    }

    private void RenderAttention()
    {
        if (AttentionList is null)
            return;
        if (!_attnCatalogHooked)
        {
            _attnCatalogHooked = true;
            CatalogCacheService.CacheUpdated += OnAttentionCatalogUpdated;
            Closed += (_, _) => CatalogCacheService.CacheUpdated -= OnAttentionCatalogUpdated;
        }
        AttentionList.Children.Clear();
        var urgent = new List<Control>();
        var waiting = new List<Control>();

        // Прокат — те же условия, что у значка «Проката» и плашки проката (UpdateRentalAlert; 2026-10-18, О2 — плашка над разделом, не в меню).
        if (RentalDueNotifier.Last is { Count: > 0 } rentals && MarketSpheres.HasRentals) // 2026-10-20: во всех сферах
        {
            var action = Tr.T("Прокат", "Прокат", "Rentals", "Kiralama", "Prokat");
            if (rentals.Overdue.Count > 0)
                urgent.Add(AttentionRow(true,
                    Tr.T($"Прокат: просрочено {rentals.Overdue.Count}", $"Прокат: мөөнөтү өттү {rentals.Overdue.Count}", $"Rentals: {rentals.Overdue.Count} overdue",
                        $"Kiralama: {rentals.Overdue.Count} gecikmiş", $"Prokat: muddati o'tgan {rentals.Overdue.Count}"),
                    RentalLine(rentals.Overdue), action, "rentals"));
            if (rentals.Today.Count > 0)
                waiting.Add(AttentionRow(false,
                    Tr.T($"Прокат: вернуть сегодня {rentals.Today.Count}", $"Прокат: бүгүн кайтаруу {rentals.Today.Count}", $"Rentals: {rentals.Today.Count} due today",
                        $"Kiralama: bugün iade {rentals.Today.Count}", $"Prokat: bugun qaytarish {rentals.Today.Count}"),
                    RentalLine(rentals.Today), action, "rentals"));
            if (rentals.Tomorrow.Count > 0)
                waiting.Add(AttentionRow(false,
                    Tr.T($"Прокат: вернуть завтра {rentals.Tomorrow.Count}", $"Прокат: эртең кайтаруу {rentals.Tomorrow.Count}", $"Rentals: {rentals.Tomorrow.Count} due tomorrow",
                        $"Kiralama: yarın iade {rentals.Tomorrow.Count}", $"Prokat: ertaga qaytarish {rentals.Tomorrow.Count}"),
                    RentalLine(rentals.Tomorrow), action, "rentals"));
        }

        // Новые заказы с сайта — число, что у пункта меню (опрос PollSiteOrdersAsync, только если список заказов включён).
        if (_siteOrdersNewCount > 0 && _navButtons.ContainsKey("siteorders"))
            waiting.Add(AttentionRow(false,
                Tr.T($"Новые заказы с сайта: {_siteOrdersNewCount}", $"Сайттан жаңы заказдар: {_siteOrdersNewCount}", $"New website orders: {_siteOrdersNewCount}",
                    $"Yeni web sitesi siparişleri: {_siteOrdersNewCount}", $"Saytdan yangi buyurtmalar: {_siteOrdersNewCount}"),
                Tr.T("Ждут обработки", "Иштетүүнү күтүп жатат", "Waiting to be processed", "İşlenmeyi bekliyor", "Ishlov berilishini kutmoqda"),
                Tr.T("Заказы", "Заказдар", "Orders", "Siparişler", "Buyurtmalar"), "siteorders"));

        // 2026-10-19, вторая живая проверка: строка долгов — на постоянном месте (сразу после просроченного проката, до товаров).
        // Раньше она шла в «ждущие» после всех «Нет в наличии», и когда остатки догружались позже, переезжала с 1-го места вниз.
        if (_attnDebtsFailed)
            urgent.Add(AttentionRetryRow(
                Tr.T("Не удалось загрузить долги", "Карыздарды жүктөө мүмкүн болгон жок", "Couldn't load debts", "Borçlar yüklenemedi", "Qarzlarni yuklab bo'lmadi"),
                Tr.T("Повторить", "Кайталоо", "Retry", "Tekrar dene", "Qayta urinish"),
                () => _ = RefreshDebtsCardAsync(force: true)));
        else if (_attnDebts is { } debts)
        {
            var sub = Tr.T($"Должников: {debts.Count}", $"Карыздарлар: {debts.Count}", $"Debtors: {debts.Count}", $"Borçlu: {debts.Count}", $"Qarzdorlar: {debts.Count}")
                      + (debts.Note is { } note ? "\n" + note : "");
            if (debts.Old > 0.005)
                sub += "\n" + Tr.T($"Больше 30 дней: {Money(debts.Old)}", $"30 күндөн ашык: {Money(debts.Old)}", $"Over 30 days: {Money(debts.Old)}",
                    $"30 günden fazla: {Money(debts.Old)}", $"30 kundan ortiq: {Money(debts.Old)}");
            urgent.Add(AttentionRow(false,
                Tr.T($"Долги клиентов: {(debts.Approx ? "≈ " : "")}{Money(debts.Total)}", $"Кардарлардын карыздары: {(debts.Approx ? "≈ " : "")}{Money(debts.Total)}",
                    $"Customer debts: {(debts.Approx ? "≈ " : "")}{Money(debts.Total)}", $"Müşteri borçları: {(debts.Approx ? "≈ " : "")}{Money(debts.Total)}",
                    $"Mijozlar qarzlari: {(debts.Approx ? "≈ " : "")}{Money(debts.Total)}"),
                sub, Tr.T("Долги", "Карыздар", "Debts", "Borçlar", "Qarzlar"), "debts"));
        }

        var restock = Tr.T("Пополнение", "Толуктоо", "Restock", "Stok yenileme", "To'ldirish");
        foreach (var p in _attnStock)
        {
            var left = p.Quantity <= 0
                ? Tr.T("Нет в наличии", "Жок", "Out of stock", "Stokta yok", "Mavjud emas")
                : Tr.T($"Осталось {Qty(p.Quantity)}", $"{Qty(p.Quantity)} калды", $"{Qty(p.Quantity)} left", $"{Qty(p.Quantity)} kaldı", $"{Qty(p.Quantity)} qoldi")
                  + (string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit);
            (p.Quantity <= 0 ? urgent : waiting).Add(AttentionRow(p.Quantity <= 0, p.Name, left, restock, "restock"));
        }
        if (_attnStockMore > 0)
            waiting.Add(AttentionRow(false,
                Tr.T($"Ещё товаров на исходе: {_attnStockMore}", $"Дагы түгөнүп бараткан товарлар: {_attnStockMore}", $"{_attnStockMore} more items running low",
                    $"Azalan {_attnStockMore} ürün daha", $"Yana {_attnStockMore} ta mahsulot tugayapti"),
                // 2026-10-20, аудит программы владельца: «на исходе 81» ≠ «Заканчивается 154» на Складе — правила разные, пишем какое.
                // 2026-10-27, С-7: порог теперь один (меньше 10) — подпись говорит, откуда число, и что оно совпадает со Складом.
                Tr.T("Остаток меньше 10 — то же правило, что «Заканчивается» на Складе",
                    "Калдык 10дон аз — Кампадагы «Түгөнүп баратат» менен бирдей эреже",
                    "Fewer than 10 left — the same rule as “Running low” in Warehouse",
                    "10'dan az kaldı — Depodaki «Azalan» ile aynı kural",
                    "Qoldiq 10 dan kam — Ombordagi «Tugayapti» bilan bir xil qoida"),
                restock, "restock"));

        foreach (var row in urgent.Concat(waiting))
            AttentionList.Children.Add(row);
        AttentionList.IsVisible = AttentionList.Children.Count > 0;
        AttentionEmptyText.IsVisible = AttentionList.Children.Count == 0;
        AttentionEmptyText.Text = _attnStockLoaded && _attnDebtsLoaded && AttentionRentalsLoaded
            ? Tr.T("Всё в порядке — срочных дел нет", "Баары жайында — шашылыш иш жок", "All good — nothing urgent",
                "Her şey yolunda — acil bir şey yok", "Hammasi joyida — shoshilinch ish yo'q")
            : Tr.T("Загрузка…", "Жүктөлүүдө…", "Loading…", "Yükleniyor…", "Yuklanmoqda…");
    }

    /// <summary>«№22 Арслан — Аренда бетономешалки» (и «ещё N», если прокатов несколько).</summary>
    private static string RentalLine(IReadOnlyList<NurMarketKassa.Services.Api.RentalDto> list)
    {
        var r = list[0];
        var line = $"№{r.Number} {r.ClientName} — {string.Join(", ", r.Items.Select(i => i.Label))}";
        if (list.Count > 1)
            line += "\n" + Tr.T($"и ещё {list.Count - 1}", $"дагы {list.Count - 1}", $"and {list.Count - 1} more", $"ve {list.Count - 1} tane daha", $"yana {list.Count - 1} ta");
        return line;
    }

    /// <summary>Строка блока. Действие — нажатие того же пункта меню (права и тариф проверяет он); пункта нет — строка без действия.</summary>
    private Control AttentionRow(bool urgent, string title, string? sub, string action, string navKey)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 10, 0) };
        UseBrush(dot, Shape.FillProperty, urgent ? "BrushDanger" : "BrushWarning");
        grid.Children.Add(dot);

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        UseBrush(head, TextBlock.ForegroundProperty, "BrushText");
        texts.Children.Add(head);
        if (!string.IsNullOrWhiteSpace(sub))
        {
            var note = new TextBlock { Text = sub, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            UseBrush(note, TextBlock.ForegroundProperty, "BrushTextSoft");
            texts.Children.Add(note);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (!_navButtons.TryGetValue(navKey, out var navButton))
            return new Border { Classes = { "attnRow" }, Child = grid };

        var go = new TextBlock
        {
            Text = action + " ›",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };
        UseBrush(go, TextBlock.ForegroundProperty, "BrushText");
        Grid.SetColumn(go, 2);
        grid.Children.Add(go);
        var button = new Button { Classes = { "attnRow" }, Content = grid };
        button.Click += (_, _) => navButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        return button;
    }

    /// <summary>2026-10-18, девятый круг (п.8): строка с действием «Повторить» (без перехода в раздел).</summary>
    private Control AttentionRetryRow(string title, string action, Action retry)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 10, 0) };
        UseBrush(dot, Shape.FillProperty, "BrushDanger");
        grid.Children.Add(dot);
        var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        UseBrush(head, TextBlock.ForegroundProperty, "BrushText");
        Grid.SetColumn(head, 1);
        grid.Children.Add(head);
        var go = new TextBlock { Text = action + " ›", FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        UseBrush(go, TextBlock.ForegroundProperty, "BrushText");
        Grid.SetColumn(go, 2);
        grid.Children.Add(go);
        var button = new Button { Classes = { "attnRow" }, Content = grid };
        Avalonia.Automation.AutomationProperties.SetName(button, title + " — " + action);
        button.Click += (_, _) => retry();
        return button;
    }

    private void AskAi_Click(object? sender, RoutedEventArgs e)
    {
        if (_navButtons.TryGetValue("aiadvisor", out var button))
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    /// <summary>Узкая шапка (1024×768, телефон): у «Спросить Нур Советника» остаётся значок с подсказкой — иначе заголовок
    /// «Сводка» сжимается до нуля.</summary>
    private void FitHeaderTools()
    {
        if (AskAiText is null || HeaderGrid.Bounds.Width <= 0)
            return;
        var width = HeaderGrid.Bounds.Width;
        FitHeaderRows(width);
        // 2026-10-18, ревью О2: меню стало 308 — на 1366 шапка ~1020 и порог «уже 1060» прятал подпись Советника, хотя место
        // есть (дата переносится). Теперь подпись остаётся, если помещаются заголовок, «Обновить», период, полная кнопка и кнопки
        // окна. Значок без подписи — с подсказкой и именем для диктора (ApplyTexts).
        var compact = _phoneLayout || width < AskAiFullWidthNeeded();
        AskAiText.IsVisible = !compact;
        AskAiButton.Padding = compact ? new Thickness(11, 0) : new Thickness(14, 0);
        if (_dateFull.Length > 0)
            DateText.Text = _phoneLayout || width < 1060 ? _dateShort : _dateFull;
        // 2026-10-18, этап О2: плашка проката в полосе над разделом — на узкой полосе без чисел (они в подсказке), ещё уже —
        // без заголовка (значок, «Открыть прокат» и ✕), чтобы кнопки окна не уходили за край.
        // 2026-10-19, вид «Вкладки»: плашка — в полосе вкладок (шапка раздела скрыта), ширина — по этой полосе.
        var alertWidth = TabsMenu && TabsBar.Bounds.Width > 0 ? TabsBar.Bounds.Width : width;
        RentalAlertText.IsVisible = alertWidth >= 1000;
        RentalAlertTitle.IsVisible = alertWidth >= 640;
    }

    /// <summary>Ширина шапки, при которой «Спросить Нур Советника» помещается с подписью.</summary>
    private double AskAiFullWidthNeeded()
    {
        double TextWidth(string? text, double size) => string.IsNullOrEmpty(text) ? 0 : new FormattedText(text, UiCulture,
            FlowDirection.LeftToRight, new Typeface(AskAiText.FontFamily, FontStyle.Normal, FontWeight.Bold), size, null).Width;
        var segment = OverviewTools.Children.OfType<Border>().FirstOrDefault()?.Bounds.Width ?? 0;
        if (segment <= 0)
            segment = 340;
        // «Обновить» 36 + промежуток 10 + период + 10 + кнопка (значок 18, промежуток 8, подпись, поля 28) + правое поле 18.
        var tools = 36 + 10 + segment + 10 + 18 + 8 + TextWidth(AskAiText.Text, 14) + 28 + 18;
        // 2026-10-19: у «Вкладок» кнопки окна — в полосе вкладок, не в шапке сводки.
        var captions = TabsMenu ? 0 : 140;
        // В две строки инструменты стоят под заголовком; в одну — рядом с ним (заголовку — не меньше его ширины).
        return _headerTwoRows ? tools + captions : TextWidth(DashboardTitle.Text, 28) + 12 + tools + captions;
    }

    private bool _headerTwoRows;

    /// <summary>2026-10-18, этап О2: меню стало шире (76 + 232), и на 1024×768 заголовок «Сводки» сжимался до «Сво…». На узкой
    /// шапке «Сводки» (не телефон) — две строки: заголовок с датой, под ним «Обновить», период и Советник.</summary>
    private void FitHeaderRows(double width)
    {
        var twoRows = !_phoneLayout && OverviewTools.IsVisible && width < 860;
        if (twoRows == _headerTwoRows)
            return;
        _headerTwoRows = twoRows;
        if (twoRows)
        {
            HeaderGrid.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetRow(OverviewTools, 1);
            Grid.SetColumn(OverviewTools, Grid.GetColumn(TitlePanel));
            OverviewTools.HorizontalAlignment = HorizontalAlignment.Left;
            OverviewTools.Margin = new Thickness(0, 10, 0, 0);
            return;
        }
        // Строки шапки на телефоне задаёт PlacePeriodSegment (OwnerShellWindow.Phone.cs) — их не трогаем.
        if (!_phoneLayout)
            HeaderGrid.RowDefinitions = new RowDefinitions();
        Grid.SetRow(OverviewTools, 0);
        Grid.SetColumn(OverviewTools, Grid.GetColumn(TitlePanel) + 1);
        OverviewTools.ClearValue(HorizontalAlignmentProperty);
        OverviewTools.Margin = _phoneLayout ? new Thickness(0) : new Thickness(0, 6, 18, 0);
    }

    private string _dateFull = "";
    private string _dateShort = "";
}
