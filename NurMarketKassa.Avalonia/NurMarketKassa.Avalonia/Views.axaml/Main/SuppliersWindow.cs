using NurMarketKassa.Core.Application;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-20, этап П1, владелец: «полностью возьми функционал оттуда (сайт NurCRM, «Поставщики») и нашим дизайном добавь нам».
/// Раздел программы владельца «Поставщики» (группа «Товары»): плитки (поставщиков, мы должны всего, просрочено, аванс), поиск,
/// «Все / С долгом / Просрочено», список — название, телефон, долг (красным), ближайший срок, последняя закупка; «＋ Поставщик» —
/// окно с реквизитами, как на сайте. Нажатие на поставщика — карточка (SupplierCardView, этап П2) на месте списка.
/// Данные — NurCRM (SupplierApiService): только чтение и реквизиты поставщика; оплат и сделок здесь нет (этапы П3–П5).</summary>
public sealed class SuppliersWindow : Window, IOwnerSection
{
    internal static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public static string TitleText => T("Поставщики", "Жеткирүүчүлөр", "Suppliers", "Tedarikçiler", "Yetkazib beruvchilar");

    private readonly SupplierApiService _svc;
    private readonly Panel _host = new();
    private readonly Grid _root = new() { Margin = new Thickness(24, 16, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*") };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) };
    private readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid _kpi = new() { MinTileWidth = 190, Spacing = 8, Margin = new Thickness(0, 0, 0, 12) };
    private readonly TextBox _search;
    private readonly StackPanel _filters = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), IsVisible = false };
    // Справа — место под полосу прокрутки (как список «Клиентов»).
    private readonly StackPanel _rows = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly Grid _listHead;
    private readonly Border _listCard;
    private List<SupplierInfo> _suppliers = new();
    private readonly Dictionary<string, SupplierSummary> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private string _filter = "all";
    private bool _summariesDone;
    private bool _loaded;
    private int _gen;
    private bool _narrow;
    private SupplierCardView? _card;
    // 2026-10-20, этап П3: плашка «Долг не записан: N» (очередь SupplierDebtRecorder) над списком.
    private readonly Border _debtQueueBox = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), IsVisible = false };

    public SuppliersWindow()
    {
        _svc = new SupplierApiService(App.GetRequiredService<NurMarketApiClient>());
        Title = TitleText;
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");
        AddRowStyles();

        _title.Text = Title;
        Use(_title, TextBlock.ForegroundProperty, "BrushText");

        // Подсказка и действия: «Обновить» — обводкой, «＋ Поставщик» — единственная жёлтая кнопка экрана.
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var hint = new TextBlock
        {
            Text = T("Кому и сколько должен магазин, сроки оплат, закупки и товары каждого поставщика — по данным NurCRM, те же, что на сайте. Нажмите на поставщика, чтобы открыть его карточку.",
                "Дүкөн кимге канча карыз, төлөм мөөнөттөрү, ар бир жеткирүүчүнүн сатып алуулары жана товарлары — NurCRM маалыматы боюнча, сайттагыдай. Карточкасын ачуу үчүн жеткирүүчүнү басыңыз.",
                "Who the shop owes and how much, payment due dates, purchases and products of every supplier — from NurCRM, the same as on the website. Tap a supplier to open their card.",
                "Mağazanın kime ne kadar borçlu olduğu, ödeme vadeleri, her tedarikçinin alımları ve ürünleri — NurCRM verileri, sitedekiyle aynı. Kartını açmak için tedarikçiye dokunun.",
                "Do'kon kimga qancha qarz, to'lov muddatlari, har bir yetkazib beruvchining xaridlari va mahsulotlari — NurCRM ma'lumotlari, saytdagidek. Kartasini ochish uchun yetkazib beruvchini bosing."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        top.Children.Add(hint);
        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        Avalonia.Automation.AutomationProperties.SetName(refresh, refresh.Content as string);
        refresh.Click += async (_, _) => await LoadAsync().ConfigureAwait(true);
        Grid.SetColumn(refresh, 1);
        top.Children.Add(refresh);
        var create = UiKit.Primary(this, T("＋ Поставщик", "＋ Жеткирүүчү", "＋ Supplier", "＋ Tedarikçi", "＋ Yetkazib beruvchi"));
        Avalonia.Automation.AutomationProperties.SetName(create, T("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi"));
        create.Margin = new Thickness(8, 0, 0, 0);
        create.Click += async (_, _) => await CreateAsync().ConfigureAwait(true);
        Grid.SetColumn(create, 2);
        top.Children.Add(create);
        top.Classes.Add("no-reflow");

        // Поиск и фильтр «Все / С долгом / Просрочено» (сегменты, выбранный тёмно-синий).
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        _search = UiKit.Search(this, T("Поиск: название, телефон, ИНН…", "Издөө: аталышы, телефон, ИНН…", "Search: name, phone, tax ID…", "Ara: ad, telefon, vergi no…", "Qidirish: nomi, telefon, STIR…"), 44); // 2026-10-27, аудит владельца С-5: было 42 (цель нажатия)
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        _search.TextChanged += (_, _) => Render();
        tools.Children.Add(_search);
        var filters = UiKit.Segment(_filters);
        filters.Margin = new Thickness(10, 0, 0, 0);
        filters.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(filters, 1);
        tools.Children.Add(filters);
        tools.Classes.Add("no-reflow");
        BuildFilters();

        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");

        // Список — карточкой: шапка и строки с одними колонками.
        _listHead = RowGrid();
        AddHead(_listHead, 0, T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), false);
        AddHead(_listHead, 1, T("Телефон", "Телефон", "Phone", "Telefon", "Telefon"), false);
        AddHead(_listHead, 2, T("Мы должны", "Биз карызбыз", "We owe", "Borcumuz", "Qarzimiz"), true);
        AddHead(_listHead, 3, T("Ближайший срок", "Жакынкы мөөнөт", "Next due", "En yakın vade", "Eng yaqin muddat"), false);
        AddHead(_listHead, 4, T("Последняя закупка", "Акыркы сатып алуу", "Last purchase", "Son alım", "Oxirgi xarid"), false);
        var headBox = new Border { Padding = new Thickness(16, 10, 16, 10), BorderThickness = new Thickness(0, 0, 0, 1), Child = _listHead };
        Use(headBox, Border.BackgroundProperty, "BrushPanelSoft");
        Use(headBox, Border.BorderBrushProperty, "BrushBorder");
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var listGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listGrid.Children.Add(headBox);
        Grid.SetRow(scroll, 1);
        listGrid.Children.Add(scroll);
        _listCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = listGrid };
        Use(_listCard, Border.BackgroundProperty, "BrushPanel");
        Use(_listCard, Border.BorderBrushProperty, "BrushBorder");

        Grid.SetRow(top, 1);
        Grid.SetRow(_kpi, 2);
        Grid.SetRow(tools, 3);
        var statusArea = new StackPanel { Children = { _debtQueueBox, _status } };
        Grid.SetRow(statusArea, 4);
        Grid.SetRow(_listCard, 5);
        _root.Children.Add(_title);
        _root.Children.Add(top);
        _root.Children.Add(_kpi);
        _root.Children.Add(tools);
        _root.Children.Add(statusArea);
        _root.Children.Add(_listCard);
        _host.Children.Add(_root);
        Content = _host;

        // Узкий экран (телефон): фильтр — строкой ниже поиска, в списке — без колонок телефона и последней закупки.
        NarrowLayout.Attach(this, 760, narrow =>
        {
            _narrow = narrow;
            tools.RowDefinitions = narrow ? new RowDefinitions("Auto,8,Auto") : new RowDefinitions();
            Grid.SetRow(filters, narrow ? 2 : 0);
            Grid.SetColumn(filters, narrow ? 0 : 1);
            filters.Margin = new Thickness(narrow ? 0 : 10, 0, 0, 0);
            filters.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            hint.IsVisible = !narrow;
            ApplyColumns(_listHead);
            Render();
        });

        RenderKpi();
        SetStatus(T("Загружаю поставщиков…", "Жеткирүүчүлөр жүктөлүүдө…", "Loading suppliers…", "Tedarikçiler yükleniyor…", "Yetkazib beruvchilar yuklanmoqda…"));
        // Данные — когда раздел впервые показан (как «Возвраты поставщикам»).
        Opened += async (_, _) =>
        {
            if (_loaded)
                return;
            _loaded = true;
            RenderDebtQueue();
            await LoadAsync().ConfigureAwait(true);
        };
        // 2026-10-20, этап П3: журнал записи долгов изменился (приёмка «в долг», другая программа на этом ПК) — плашка заново.
        Action onQueue = () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RenderDebtQueue();
            _card?.RefreshDebtQueue();
        });
        SupplierDebtRecorder.Changed += onQueue;
        Closed += (_, _) => SupplierDebtRecorder.Changed -= onQueue;
    }

    // ------------------------------------------------------------------ П3: долг не записан

    /// <summary>Плашка «Долг не записан: N на X сом» — приходы «в долг», после которых долг поставщику не записался (обрыв, отказ сервера).
    /// «Записать долг» — только владелец/администратор, с подтверждением и той же защитой от двойного долга. Журнал повреждён — красная плашка.</summary>
    private void RenderDebtQueue()
    {
        var queue = SupplierDebtRecorder.LoadQueue();
        _debtQueueBox.Child = SupplierDebtQueueBox(this, queue, null, async () =>
        {
            var dialog = new SupplierDebtRecordDialog(queue.Open.Select(e => e.ToRequest()).ToList(), editableTerm: false);
            await dialog.ShowDialog(this).ConfigureAwait(true);
            RenderDebtQueue();
            if (dialog.Recorded > 0)
                await LoadAsync().ConfigureAwait(true);
        });
        _debtQueueBox.IsVisible = _debtQueueBox.Child is not null;
        Use(_debtQueueBox, Border.BackgroundProperty, queue.Closed ? "BrushDangerSoft" : "BrushWarningSoft");
    }

    /// <summary>Содержимое плашки очереди (список и карточка поставщика): текст, «Записать долг» (владелец/админ) или подсказка. null — нечего показать.</summary>
    internal static Control? SupplierDebtQueueBox(Control host, SupplierDebtQueue queue, string? supplierName, Func<Task> record)
    {
        void Bind(AvaloniaObject t, AvaloniaProperty p, string key) => t.Bind(p, host.GetResourceObservable(key));
        if (queue.Closed)
        {
            var broken = new TextBlock
            {
                Text = T("Журнал записи долгов поставщикам повреждён — запись долгов закрыта. Обратитесь в поддержку; долг можно записать на сайте NurCRM.",
                    "Жеткирүүчүлөргө карыз жазуу журналы бузулган — карыз жазуу жабык. Колдоо кызматына кайрылыңыз; карызды NurCRM сайтында жазса болот.",
                    "The supplier debt log is damaged — recording debts is closed. Contact support; the debt can be recorded on the NurCRM website.",
                    "Tedarikçi borç günlüğü bozuk — borç kaydı kapalı. Desteğe başvurun; borç NurCRM sitesinde kaydedilebilir.",
                    "Yetkazib beruvchilar qarz jurnali buzilgan — qarz yozish yopiq. Qo'llab-quvvatlashga murojaat qiling; qarzni NurCRM saytida yozish mumkin.")
                       + (queue.ClosedFile is { Length: > 0 } f ? " (" + Path.GetFileName(f) + ")" : ""),
                FontSize = 13, TextWrapping = TextWrapping.Wrap,
            };
            Bind(broken, TextBlock.ForegroundProperty, "BrushDangerOnSoft");
            return broken;
        }
        if (queue.Open.Count == 0)
            return null;
        var sum = queue.Open.Sum(e => e.Amount);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var head = new TextBlock
        {
            Text = "⚠ " + T($"Долг не записан: {queue.Open.Count} на {MoneyText.Som(sum)}", $"Карыз жазылган жок: {queue.Open.Count}, {MoneyText.Som(sum)}",
                $"Debt not recorded: {queue.Open.Count}, {MoneyText.Som(sum)}", $"Borç kaydedilmedi: {queue.Open.Count}, {MoneyText.Som(sum)}",
                $"Qarz yozilmadi: {queue.Open.Count} ta, {MoneyText.Som(sum)}"),
            FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        Bind(head, TextBlock.ForegroundProperty, "BrushText");
        text.Children.Add(head);
        var names = queue.Open.Select(e => e.SupplierName).Distinct().ToList();
        var who = supplierName is null ? string.Join(", ", names.Take(4)) + (names.Count > 4 ? ", …" : "") : "";
        var reason = queue.Open.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Error))?.Error;
        var sub = new TextBlock
        {
            Text = T("Приход «в долг» проведён, а долг поставщику на сайте NurCRM не записался (нет связи или отказ сервера). ",
                       "«Карызга» кириш өткөрүлдү, бирок жеткирүүчүгө карыз NurCRM сайтында жазылган жок (байланыш жок же сервер баш тартты). ",
                       "The delivery “on credit” was posted, but the debt to the supplier wasn't recorded on the NurCRM website (no connection or server refusal). ",
                       "«Veresiye» giriş yapıldı, ancak tedarikçi borcu NurCRM sitesine kaydedilmedi (bağlantı yok veya sunucu reddetti). ",
                       "«Qarzga» kirim o'tkazildi, lekin yetkazib beruvchiga qarz NurCRM saytida yozilmadi (aloqa yo'q yoki server rad etdi). ")
                   + (who.Length > 0 ? who + ". " : "")
                   + (reason is not null ? T("Причина: ", "Себеби: ", "Reason: ", "Neden: ", "Sabab: ") + reason : ""),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        Bind(sub, TextBlock.ForegroundProperty, "BrushText");
        text.Children.Add(sub);
        grid.Children.Add(text);
        Control action;
        if (SupplierDebtUi.CanRecordDebts)
        {
            var button = new Button
            {
                Content = T("Записать долг", "Карызды жазуу", "Record debt", "Borcu kaydet", "Qarzni yozish"), Height = 36, MinHeight = 36, Padding = new Thickness(14, 0),
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), FontSize = 13, FontWeight = FontWeight.SemiBold,
                VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            button.Classes.Add("sec-outline");
            Avalonia.Automation.AutomationProperties.SetName(button, T("Записать долг поставщику", "Жеткирүүчүгө карызды жазуу", "Record the debt to the supplier",
                "Tedarikçiye borcu kaydet", "Yetkazib beruvchiga qarzni yozish"));
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try
                {
                    await record().ConfigureAwait(true);
                }
                finally
                {
                    button.IsEnabled = true;
                }
            };
            action = button;
        }
        else
        {
            var only = new TextBlock
            {
                Text = T("Записать может владелец или администратор.", "Ээси же администратор жаза алат.", "The owner or an administrator can record it.",
                    "Sahibi veya yönetici kaydedebilir.", "Egasi yoki administrator yozishi mumkin."),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 200, VerticalAlignment = VerticalAlignment.Center,
            };
            Bind(only, TextBlock.ForegroundProperty, "BrushTextSoft");
            action = only;
        }
        action.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(action, 1);
        grid.Children.Add(action);
        return grid;
    }

    public void AsOwnerSection()
    {
        _title.IsVisible = false;
        _root.Margin = OwnerSectionLayout.Margin;
    }

    // ------------------------------------------------------------------ загрузка

    private async Task LoadAsync()
    {
        var gen = ++_gen;
        _summariesDone = false;
        SetStatus(T("Загружаю поставщиков…", "Жеткирүүчүлөр жүктөлүүдө…", "Loading suppliers…", "Tedarikçiler yükleniyor…", "Yetkazib beruvchilar yuklanmoqda…"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            var list = await _svc.ListAsync(cts.Token).ConfigureAwait(true);
            if (gen != _gen)
                return;
            _suppliers = list;
            _summaries.Clear();
            RenderKpi();
            Render();
            if (list.Count == 0)
            {
                _summariesDone = true;
                SetStatus(null);
                RenderKpi();
                Render();
                return;
            }
            var done = 0;
            SetStatus(Counting(0, list.Count));
            using var sumCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await _svc.LoadSummariesAsync(list, s => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (gen != _gen)
                    return;
                _summaries[s.Info.Id] = s;
                done++;
                SetStatus(Counting(done, list.Count));
                if (done % 5 == 0)
                    Render();
            }), sumCts.Token).ConfigureAwait(true);
            // Отложенные Post из onEach — выполнить до итоговой отрисовки.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            if (gen != _gen)
                return;
            _summariesDone = true;
            var failed = _suppliers.Count(s => !_summaries.TryGetValue(s.Id, out var x) || !x.Loaded);
            SetStatus(failed == 0 ? null
                : T($"Долг не получен у {failed} поставщик(ов) — нажмите «Обновить».", $"{failed} жеткирүүчүнүн карызы алынган жок — «Жаңыртууну» басыңыз.",
                    $"Couldn't get the debt of {failed} supplier(s) — press “Refresh”.", $"{failed} tedarikçinin borcu alınamadı — «Yenile»ye basın.",
                    $"{failed} ta yetkazib beruvchining qarzi olinmadi — «Yangilash»ni bosing."));
            RenderKpi();
            Render();
            _card?.UpdateSummary(_summaries.GetValueOrDefault(_card.SupplierId));
            PosLogger.Log($"Поставщики: {_suppliers.Count} шт., мы должны {_summaries.Values.Sum(s => s.Debt):0.00} сом, не получено {failed}.", "INFO");
        }
        catch (Exception ex)
        {
            if (gen != _gen)
                return;
            _summariesDone = true;
            SetStatus(T("Не удалось загрузить поставщиков: ", "Жеткирүүчүлөрдү жүктөө мүмкүн болгон жок: ", "Couldn't load suppliers: ", "Tedarikçiler yüklenemedi: ", "Yetkazib beruvchilarni yuklab bo'lmadi: ")
                      + (ex is OperationCanceledException ? T("сервер не ответил", "сервер жооп берген жок", "the server didn't respond", "sunucu yanıt vermedi", "server javob bermadi") : ServerTelegramBotApi.DescribeFields(ex)),
                error: true);
            RenderKpi();
            Render();
            PosLogger.Log($"Поставщики: не загружены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
    }

    private static string Counting(int done, int total) =>
        T($"Считаю долги поставщикам: {done} из {total}…", $"Жеткирүүчүлөргө карыз эсептелүүдө: {done} / {total}…", $"Counting debts to suppliers: {done} of {total}…",
            $"Tedarikçi borçları hesaplanıyor: {done} / {total}…", $"Yetkazib beruvchilarga qarz hisoblanmoqda: {done} / {total}…");

    private void SetStatus(string? text, bool error = false)
    {
        _status.IsVisible = !string.IsNullOrEmpty(text);
        _status.Text = text ?? "";
        Use(_status, TextBlock.ForegroundProperty, error ? "BrushDanger" : "BrushTextSoft");
    }

    // ------------------------------------------------------------------ плитки и фильтр

    private void RenderKpi()
    {
        var all = _summaries.Values.ToList();
        var ready = _summariesDone && _suppliers.Count > 0;
        var debt = all.Sum(s => s.Debt);
        var overdue = all.Sum(s => s.Overdue);
        var advance = all.Sum(s => s.Prepayment);
        const string Dots = "…";
        _kpi.Children.Clear();
        _kpi.Children.Add(UiKit.Kpi(T("Поставщиков", "Жеткирүүчүлөр", "Suppliers", "Tedarikçi", "Yetkazib beruvchilar"),
            _suppliers.Count.ToString(CultureInfo.InvariantCulture)));
        _kpi.Children.Add(UiKit.Kpi(T("Мы должны всего", "Биздин жалпы карыз", "We owe in total", "Toplam borcumuz", "Jami qarzimiz"),
            ready || _suppliers.Count == 0 ? MoneyText.Som(debt) : Dots, danger: ready && debt > 0.005));
        _kpi.Children.Add(UiKit.Kpi(T("Просрочено", "Мөөнөтү өттү", "Overdue", "Vadesi geçmiş", "Muddati o'tgan"),
            ready || _suppliers.Count == 0 ? MoneyText.Som(overdue) : Dots, danger: ready && overdue > 0.005));
        _kpi.Children.Add(UiKit.Kpi(T("Аванс у поставщиков", "Жеткирүүчүлөрдөгү аванс", "Advances to suppliers", "Tedarikçilerdeki avans", "Yetkazib beruvchilardagi avans"),
            ready || _suppliers.Count == 0 ? MoneyText.Som(advance) : Dots));
        BuildFilters();
    }

    private void BuildFilters()
    {
        _filters.Children.Clear();
        var withDebt = _summaries.Values.Count(s => s.Debt > 0.005);
        var overdue = _summaries.Values.Count(s => s.Overdue > 0.005);
        foreach (var (key, text) in new[]
                 {
                     ("all", T("Все", "Баары", "All", "Tümü", "Barchasi")),
                     ("debt", T("С долгом", "Карызы бар", "With debt", "Borçlu", "Qarzi bor") + (_summariesDone ? $" ({withDebt})" : "")),
                     ("overdue", T("Просрочено", "Мөөнөтү өткөн", "Overdue", "Vadesi geçmiş", "Muddati o'tgan") + (_summariesDone ? $" ({overdue})" : "")),
                 })
        {
            var seg = UiKit.Seg(text, key == _filter);
            seg.Click += (_, _) =>
            {
                _filter = key;
                BuildFilters();
                Render();
            };
            _filters.Children.Add(seg);
        }
    }

    // ------------------------------------------------------------------ список

    private IEnumerable<SupplierInfo> Shown()
    {
        var query = (_search.Text ?? "").Trim();
        var digits = new string(query.Where(char.IsDigit).ToArray());
        IEnumerable<SupplierInfo> list = _suppliers.Where(s => query.Length == 0
            || SearchMatch.Matches(query, s.Name, s.Llc) // 2026-10-29: единый поиск
            || (digits.Length >= 3 && (new string(s.Phone.Where(char.IsDigit).ToArray()).Contains(digits, StringComparison.Ordinal) || s.Inn.Contains(digits, StringComparison.Ordinal))));
        list = _filter switch
        {
            "debt" => list.Where(s => Sum(s)?.Debt > 0.005),
            "overdue" => list.Where(s => Sum(s)?.Overdue > 0.005),
            _ => list,
        };
        // Сначала те, кому должны больше; дальше по названию.
        return list.OrderByDescending(s => Sum(s)?.Debt ?? 0).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private SupplierSummary? Sum(SupplierInfo s) => _summaries.TryGetValue(s.Id, out var x) ? x : null;

    private void Render()
    {
        _rows.Children.Clear();
        if (!_loaded || (_suppliers.Count == 0 && !_summariesDone))
            return;
        var shown = Shown().Take(500).ToList();
        if (shown.Count == 0)
        {
            var text = _suppliers.Count == 0
                ? T("Поставщиков пока нет. Нажмите «＋ Поставщик» — название, телефон и реквизиты. Поставщик сразу появится в приёмке товара и на сайте NurCRM.",
                    "Азырынча жеткирүүчүлөр жок. «＋ Жеткирүүчү» баскычын басыңыз — аталышы, телефону жана реквизиттери. Жеткирүүчү дароо товар кабыл алууда жана NurCRM сайтында пайда болот.",
                    "No suppliers yet. Press “＋ Supplier” — name, phone and bank details. The supplier will appear in stock receiving and on the NurCRM website right away.",
                    "Henüz tedarikçi yok. «＋ Tedarikçi»ye basın — ad, telefon ve künye. Tedarikçi mal kabulünde ve NurCRM sitesinde hemen görünür.",
                    "Hozircha yetkazib beruvchilar yo'q. «＋ Yetkazib beruvchi» tugmasini bosing — nomi, telefoni va rekvizitlari. U darhol tovar qabulida va NurCRM saytida paydo bo'ladi.")
                : (_search.Text ?? "").Trim().Length > 0
                    ? T("По этому запросу поставщиков нет. Проверьте написание или ищите по цифрам телефона.", "Бул сурам боюнча жеткирүүчү жок. Жазылышын текшериңиз же телефондун сандары менен издеңиз.",
                        "No suppliers match this search. Check the spelling or search by phone digits.", "Bu aramaya uyan tedarikçi yok. Yazımı kontrol edin veya telefon rakamlarıyla arayın.",
                        "Bu so'rov bo'yicha yetkazib beruvchi yo'q. Yozilishini tekshiring yoki telefon raqamlari bo'yicha qidiring.")
                    : _filter == "overdue"
                        ? T("Просроченных долгов перед поставщиками нет — все платежи по графику в срок.", "Жеткирүүчүлөр алдында мөөнөтү өткөн карыз жок — бардык төлөмдөр графикте.",
                            "No overdue debts to suppliers — every scheduled payment is on time.", "Tedarikçilere vadesi geçmiş borç yok — tüm ödemeler zamanında.",
                            "Yetkazib beruvchilar oldida muddati o'tgan qarz yo'q — barcha to'lovlar o'z vaqtida.")
                        : T("Долгов перед поставщиками нет. Закупка «в долг» на сайте NurCRM записывает долг — он появится здесь со сроком оплаты.",
                            "Жеткирүүчүлөр алдында карыз жок. NurCRM сайтында «карызга» сатып алуу карызды жазат — ал бул жерде төлөө мөөнөтү менен көрүнөт.",
                            "No debts to suppliers. A purchase “on credit” on the NurCRM website records a debt — it will appear here with its due date.",
                            "Tedarikçilere borç yok. NurCRM sitesinde «veresiye» alım borç kaydeder — burada vadesiyle görünür.",
                            "Yetkazib beruvchilar oldida qarz yo'q. NurCRM saytida «qarzga» xarid qarzni yozadi — u shu yerda to'lov muddati bilan ko'rinadi.");
            _rows.Children.Add(SectionCards.Empty(this, text));
            return;
        }
        foreach (var s in shown)
            _rows.Children.Add(BuildRow(s));
    }

    private Grid RowGrid()
    {
        var g = new Grid();
        ApplyColumns(g);
        return g;
    }

    /// <summary>Колонки: поставщик | телефон | долг | ближайший срок | последняя закупка (на телефоне — без телефона и закупки).</summary>
    private void ApplyColumns(Grid g) =>
        g.ColumnDefinitions = new ColumnDefinitions(_narrow ? "2*,0,1.2*,1.2*,0" : "2.3*,1.3*,1.3*,1.3*,1.1*");

    private void AddHead(Grid g, int col, string text, bool right)
    {
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(col == 3 ? 16 : 0, 0, 12, 0), TextAlignment = right ? TextAlignment.Right : TextAlignment.Left };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        Grid.SetColumn(t, col);
        g.Children.Add(t);
    }

    private Control BuildRow(SupplierInfo s)
    {
        var sum = Sum(s);
        var g = RowGrid();
        var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = Cell(s.Name, "BrushText", 14, FontWeight.SemiBold);
        name.MaxLines = 2;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        who.Children.Add(name);
        if (s.Details.Length > 0)
        {
            var sub = Cell(s.Details, "BrushTextSoft", 12);
            sub.MaxLines = 1;
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            who.Children.Add(sub);
        }
        g.Children.Add(who);
        Put(g, 1, Cell(s.Phone.Length > 0 ? s.Phone : "—", s.Phone.Length > 0 ? "BrushText" : "BrushTextSoft", 13.5));

        // Долг: красным только если он есть; пока считаем — «…», не получен — «не загрузилось».
        TextBlock debt;
        if (sum is null)
            debt = Cell(_summariesDone ? T("не загрузилось", "жүктөлгөн жок", "didn't load", "yüklenmedi", "yuklanmadi") : "…", "BrushTextSoft", 13);
        else if (!sum.Loaded)
            debt = Cell(T("не загрузилось", "жүктөлгөн жок", "didn't load", "yüklenmedi", "yuklanmadi"), "BrushTextSoft", 13);
        else if (sum.Debt > 0.005)
        {
            debt = Cell(MoneyText.Som(sum.Debt), "BrushDanger", 14, FontWeight.Bold);
            debt.TextWrapping = TextWrapping.NoWrap;
        }
        // 2026-10-20, аудит владельца: долга нет, но есть аванс — было просто «—», аванс в списке не виден.
        else if (sum.Prepayment > 0.005)
            debt = Cell(T("аванс ", "аванс ", "advance ", "avans ", "avans ") + MoneyText.Som(sum.Prepayment), "BrushTextSoft", 13);
        else
            debt = Cell("—", "BrushTextSoft", 13.5);
        debt.TextAlignment = TextAlignment.Right;
        // 2026-10-27, аудит владельца С-1: у поставщика есть и долг, и аванс — аванс в строке не был виден, и плитка «Аванс у поставщиков»
        // (15 208) не сходилась с суммой авансов в строках (15 156). Теперь под долгом вторая строка «аванс …» — плитка = сумме строк.
        Control debtCell = debt;
        if (sum is { Loaded: true } && sum.Debt > 0.005 && sum.Prepayment > 0.005)
        {
            var advance = Cell(T("+ аванс ", "+ аванс ", "+ advance ", "+ avans ", "+ avans ") + MoneyText.Som(sum.Prepayment), "BrushTextSoft", 12);
            advance.TextAlignment = TextAlignment.Right;
            debtCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { debt, advance } };
        }
        Put(g, 2, debtCell);

        // Ближайший срок — двумя строками: дата и сумма взноса; просроченный — красным «просрочено» и «с …».
        var nextText = "—";
        var nextBrush = "BrushTextSoft";
        string? nextSub = null;
        if (sum?.NextDue is { } due)
        {
            if (sum.Overdue > 0.005 || due.Date < DateTime.Today)
            {
                nextText = T("просрочено", "мөөнөтү өттү", "overdue", "vadesi geçti", "muddati o'tgan");
                nextSub = T($"с {due:dd.MM.yyyy}", $"{due:dd.MM.yyyy} баштап", $"since {due:dd.MM.yyyy}", $"{due:dd.MM.yyyy} tarihinden beri", $"{due:dd.MM.yyyy} dan beri");
                nextBrush = "BrushDanger";
            }
            else
            {
                nextText = due.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                nextSub = MoneyText.Som(sum.NextDueAmount);
                nextBrush = "BrushText";
            }
        }
        else if (sum is { Debt: > 0.005 })
            nextText = T("срок не задан", "мөөнөт коюлган эмес", "no due date", "vade yok", "muddat belgilanmagan");
        var nextCell = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 12, 0) };
        var nextMain = Cell(nextText, nextBrush, 13, nextBrush == "BrushDanger" ? FontWeight.SemiBold : null);
        nextMain.Margin = default;
        nextCell.Children.Add(nextMain);
        if (nextSub is not null)
        {
            var subCell = Cell(nextSub, nextBrush == "BrushDanger" ? "BrushDanger" : "BrushTextSoft", 12);
            subCell.Margin = default;
            nextCell.Children.Add(subCell);
        }
        Put(g, 3, nextCell);
        Put(g, 4, Cell(sum?.LastPurchase is { } lp ? lp.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "—", sum?.LastPurchase is null ? "BrushTextSoft" : "BrushText", 13));
        if (_narrow)
        {
            g.Children[1].IsVisible = false;
            g.Children[4].IsVisible = false;
        }

        var row = new Button { Content = g };
        row.Classes.Add("sup-row");
        Avalonia.Automation.AutomationProperties.SetName(row, s.Name + (sum is { Debt: > 0.005 } ? " — " + T("мы должны ", "биз карызбыз ", "we owe ", "borcumuz ", "qarzimiz ") + MoneyText.Som(sum.Debt) : ""));
        row.Click += (_, _) => OpenCard(s);
        return row;
    }

    private static void Put(Grid g, int col, Control c)
    {
        Grid.SetColumn(c, col);
        g.Children.Add(c);
    }

    private TextBlock Cell(string text, string brush, double size, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0) };
        t.Classes.Add("num");
        Use(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>Строка-кнопка во всю ширину (щелчок, Tab + Enter, UIA Invoke) — как строка «Клиентов» (О8).</summary>
    private void AddRowStyles()
    {
        static Style S(Func<Selector?, Selector> sel, params (AvaloniaProperty P, object V)[] setters)
        {
            var st = new Style(sel);
            foreach (var (p, v) in setters)
                st.Setters.Add(new Setter(p, v));
            return st;
        }
        var row = (Func<Selector?, Selector>)(x => x.OfType<Button>().Class("sup-row"));
        Styles.Add(S(row,
            (Button.BackgroundProperty, Brushes.Transparent), (Button.BorderBrushProperty, new DynamicResourceExtension("BrushBorder")),
            (Button.BorderThicknessProperty, new Thickness(0, 0, 0, 1)), (Button.CornerRadiusProperty, new CornerRadius(0)),
            (Button.PaddingProperty, new Thickness(16, 9, 4, 9)), (Button.MinHeightProperty, 52.0),
            (Button.HorizontalAlignmentProperty, HorizontalAlignment.Stretch), (Button.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch),
            (Button.VerticalContentAlignmentProperty, VerticalAlignment.Center), (Button.CursorProperty, new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand))));
        Styles.Add(S(x => x.OfType<Button>().Class("sup-row").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, Brushes.Transparent), (ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("BrushBorder"))));
        Styles.Add(S(x => x.OfType<Button>().Class("sup-row").Class(":pointerover").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelSoft")), (ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("BrushBorder"))));
        Styles.Add(S(x => x.OfType<Button>().Class("sup-row").Class(":pressed").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BackgroundProperty, new DynamicResourceExtension("BrushPanelPressed"))));
        Styles.Add(S(x => x.OfType<Button>().Class("sup-row").Class(":focus-visible").Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"),
            (ContentPresenter.BorderBrushProperty, new DynamicResourceExtension("BrushText")), (ContentPresenter.BorderThicknessProperty, new Thickness(2))));
    }

    // ------------------------------------------------------------------ карточка, новый поставщик

    private void OpenCard(SupplierInfo s)
    {
        _card = new SupplierCardView(_svc, s, Sum(s), _suppliers, CloseCard, OnSupplierChanged) { Margin = _root.Margin };
        _root.IsVisible = false;
        _host.Children.Add(_card);
    }

    private void CloseCard()
    {
        if (_card is not null)
            _host.Children.Remove(_card);
        _card = null;
        _root.IsVisible = true;
        Render();
    }

    /// <summary>Реквизиты изменены в карточке — та же строка в списке.</summary>
    private void OnSupplierChanged(SupplierInfo updated)
    {
        var i = _suppliers.FindIndex(x => x.Id == updated.Id);
        if (i >= 0)
            _suppliers[i] = updated;
        if (_summaries.TryGetValue(updated.Id, out var old))
            _summaries[updated.Id] = old with { Info = updated };
    }

    private async Task CreateAsync()
    {
        var dialog = new SupplierEditDialog(_svc, null, _suppliers);
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.Saved is not { } created)
            return;
        PosLogger.Log($"Поставщики: добавлен поставщик {created.Id}.", "INFO");
        _suppliers.Add(created);
        _suppliers = _suppliers.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _summaries[created.Id] = SupplierApiService.Summarize(created, new SupplierKpis(0, 0, 0, 0, 0, 0), Array.Empty<SupplierDeal>(), null);
        RenderKpi();
        Render();
        OpenCard(created);
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
