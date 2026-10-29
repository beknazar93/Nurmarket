using NurMarketKassa.Core.Application;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Analytics;

/// <summary>2026-10-22 (аналитика по категориям): вкладка «По категориям» раздела «Аналитика». Владелец: «в аналитике должен быть
/// селект по категориям — маржа, прибыль, расходы, движение товара».
///
/// Вверху — выбор категорий (список с поиском, несколько галочек, «Все категории»; у родителя подкатегории включаются сами).
/// Ниже за выбранный период (его задаёт шапка раздела): плитки выручки, себестоимости, прибыли, МАРЖИ и наценки, штук, чеков,
/// среднего чека и доли в выручке компании — каждая с ▲▼ к прошлому периоду той же длины; сравнение с компанией; расходы
/// (честно: общие расходы магазина показаны отдельно, а «чистая прибыль» — только с подписанным способом деления по выручке);
/// движение товара (продано, остаток, оборачиваемость, дней запаса); топ-10 товаров и товары, которые не продавались.
/// Цифры — только с сервера (<see cref="CategoryAnalyticsData"/>), экран ничего не считает про деньги кассы. Красный — только
/// отрицательная маржа/прибыль.</summary>
public sealed class CategoryAnalyticsView : UserControl
{
    private readonly Button _pickButton;
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly TextBlock _status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private readonly HashSet<string> _picked = [];
    private IReadOnlyList<CategoryAnalyticsData.Node> _nodes = [];
    private DateTime _from = DateTime.Today, _to = DateTime.Today;
    private CancellationTokenSource? _cts;
    private bool _loadedOnce;

    /// <summary>Последний рассчитанный отчёт — его же берёт выгрузка Word/Excel (одни цифры на экране и в файле).</summary>
    public CategoryAnalyticsData? Data { get; private set; }

    private static string Som => Tr.T("сом", "сом", "som", "som", "so'm");

    public CategoryAnalyticsView()
    {
        _pickButton = new Button { MinHeight = 44, Padding = new Thickness(16, 0), HorizontalAlignment = HorizontalAlignment.Left };
        _pickButton.Classes.Add("sec-outline");
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable("BrushTextSoft"));
        UpdatePickText();

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = BuildPicker(flyout);
        flyout.Closed += (_, _) => _ = ReloadAsync();
        _pickButton.Flyout = flyout;

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        top.Children.Add(_pickButton);
        var root = new StackPanel { Spacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        root.Children.Add(top);
        root.Children.Add(_status);
        root.Children.Add(_body);
        Content = new ScrollViewer { Content = root, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 12) };
    }

    // ------------------------------------------------------------------ выбор категорий

    private void UpdatePickText()
    {
        string text;
        if (_picked.Count == 0)
            text = Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar");
        else if (_picked.Count == 1)
            text = Name(_picked.First());
        else
            text = Tr.T($"Категорий: {_picked.Count}", $"Категория: {_picked.Count}", $"Categories: {_picked.Count}", $"Kategori: {_picked.Count}", $"Kategoriyalar: {_picked.Count}");
        _pickButton.Content = Tr.T("Категория: ", "Категория: ", "Category: ", "Kategori: ", "Kategoriya: ") + text + "  ▾";
        Avalonia.Automation.AutomationProperties.SetName(_pickButton, _pickButton.Content?.ToString());
    }

    private string Name(string id) =>
        id == CategoryAnalyticsData.NoneId ? NoneName : _nodes.FirstOrDefault(n => n.Id == id)?.Name ?? id;

    private static string NoneName => Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz");

    private Control BuildPicker(Flyout flyout)
    {
        var search = new TextBox
        {
            Watermark = Tr.T("Найти категорию…", "Категорияны издөө…", "Find a category…", "Kategori ara…", "Kategoriya qidirish…"),
            MinHeight = 44,
        };
        var list = new StackPanel { Spacing = 2 };
        var scroll = new ScrollViewer { Content = list, MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        void Fill()
        {
            list.Children.Clear();
            var q = search.Text?.Trim() ?? "";
            var all = new RadioButton
            {
                Content = Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar"),
                IsChecked = _picked.Count == 0, MinHeight = 44, GroupName = "catall",
            };
            all.IsCheckedChanged += (_, _) =>
            {
                if (all.IsChecked == true && _picked.Count > 0)
                {
                    _picked.Clear();
                    UpdatePickText();
                    Fill();
                }
            };
            if (q.Length == 0)
                list.Children.Add(all);
            var rows = _nodes.Select(n => (n.Id, n.Name, n.Depth)).Append((CategoryAnalyticsData.NoneId, NoneName, 0));
            foreach (var (id, name, depth) in rows)
            {
                if (q.Length > 0 && !SearchMatch.Matches(q, name)) // 2026-10-29: единый поиск
                    continue;
                var box = new CheckBox { Content = name, IsChecked = _picked.Contains(id), MinHeight = 44, Margin = new Thickness(q.Length > 0 ? 0 : depth * 20, 0, 0, 0) };
                var captured = id;
                box.IsCheckedChanged += (_, _) =>
                {
                    if (box.IsChecked == true)
                        _picked.Add(captured);
                    else
                        _picked.Remove(captured);
                    UpdatePickText();
                };
                list.Children.Add(box);
            }
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = Tr.T("Такой категории нет", "Мындай категория жок", "No such category", "Böyle bir kategori yok", "Bunday kategoriya yo'q"),
                    Margin = new Thickness(8, 12),
                });
        }

        search.TextChanged += (_, _) => Fill();
        Fill();

        var done = new Button { Content = Tr.T("Показать", "Көрсөтүү", "Show", "Göster", "Ko'rsatish"), MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        done.Classes.Add("sec-primary");
        done.Click += (_, _) => flyout.Hide();
        var hint = new TextBlock
        {
            Text = Tr.T("Можно отметить несколько. У категории с подкатегориями они входят сами.",
                "Бир нече белгилесе болот. Подкатегориялар категория менен кошо кирет.",
                "You can tick several. Subcategories are included with their parent.",
                "Birkaç tane işaretleyebilirsiniz. Alt kategoriler ana kategoriyle birlikte gelir.",
                "Bir nechtasini belgilash mumkin. Ichki kategoriyalar asosiy bilan birga kiradi."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        return new StackPanel { Width = 340, Spacing = 8, Margin = new Thickness(4), Children = { search, scroll, hint, done } };
    }

    // ------------------------------------------------------------------ загрузка

    /// <summary>Период задаёт шапка раздела; первый показ грузит справочник категорий.</summary>
    public Task LoadAsync(DateTime from, DateTime to)
    {
        _from = from.Date;
        _to = to.Date;
        return ReloadAsync();
    }

    public bool NeedsLoad => !_loadedOnce;

    private async Task ReloadAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _status.Text = Tr.T("Считаю…", "Эсептеп жатам…", "Calculating…", "Hesaplanıyor…", "Hisoblanmoqda…");
        try
        {
            if (_nodes.Count == 0)
            {
                var nodes = await CategoryAnalyticsData.LoadNodesAsync(cts.Token).ConfigureAwait(true);
                if (nodes != null)
                    _nodes = nodes;
                UpdatePickText();
            }
            var data = await Task.Run(() => CategoryAnalyticsData.BuildAsync(_from, _to, _nodes, _picked.ToList(), cts.Token), cts.Token).ConfigureAwait(true);
            if (cts.IsCancellationRequested)
                return;
            _loadedOnce = true;
            Data = data;
            _body.Children.Clear();
            if (data is null)
            {
                _status.Text = Tr.T("Нет связи с сервером NurCRM — аналитика по категориям считается только по серверу, чтобы цифры совпадали со «Сводкой». Проверьте интернет и нажмите «Обновить».",
                    "NurCRM серверине байланыш жок — категориялар боюнча аналитика «Жыйынтык» менен дал келиши үчүн сервер боюнча гана эсептелет. Интернетти текшериңиз да «Жаңыртуу» басыңыз.",
                    "No connection to the NurCRM server — category analytics is calculated from the server only, so the figures match “Summary”. Check the internet and press “Refresh”.",
                    "NurCRM sunucusuna bağlanılamadı — kategori analizi yalnızca sunucudan hesaplanır, böylece rakamlar «Özet» ile aynı olur. İnterneti kontrol edip «Yenile»ye basın.",
                    "NurCRM serveriga ulanib bo'lmadi — kategoriyalar tahlili «Xulosa» bilan mos bo'lishi uchun faqat serverdan hisoblanadi. Internetni tekshirib, «Yangilash»ni bosing.");
                return;
            }
            _status.Text = "";
            RenderReport(data);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Аналитика по категориям: {ex}", "WARNING");
            _status.Text = Tr.T("Не удалось посчитать: ", "Эсептөө мүмкүн болгон жок: ", "Could not calculate: ", "Hesaplanamadı: ", "Hisoblab bo'lmadi: ") + ex.Message;
        }
    }

    // ------------------------------------------------------------------ вид

    private TextBlock Soft(string text, double size = 12.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static string Money(double v) => v.ToString("N0") + " " + Som;
    private static string Pct(double v) => v.ToString("0.#") + " %";

    /// <summary>▲▼ к прошлому периоду: для денег и штук — в процентах, для маржи — в процентных пунктах. Нет прошлых данных — пусто.</summary>
    private static string Delta(double cur, double prev, bool points = false)
    {
        if (points)
            return Math.Abs(cur - prev) < 0.05 ? "= 0" : $"{(cur > prev ? "▲" : "▼")} {Math.Abs(cur - prev):0.#} " + Tr.T("п.п.", "п.п.", "pp", "puan", "p.p.");
        if (prev <= 0)
            return cur > 0 ? "▲ " + Tr.T("новое", "жаңы", "new", "yeni", "yangi") : "";
        var d = (cur - prev) / prev * 100;
        return Math.Abs(d) < 0.05 ? "= 0 %" : $"{(d > 0 ? "▲" : "▼")} {Math.Abs(d):0.#} %";
    }

    private Border Tile(string label, string value, string? delta = null, bool danger = false)
    {
        var tile = UiKit.Kpi(label, value, danger);
        if (!string.IsNullOrEmpty(delta) && tile.Child is StackPanel sp)
            sp.Children.Add(Soft(delta.StartsWith("=") ? Tr.T("без изменений к прошлому периоду", "мурунку мезгилге караганда өзгөрүүсүз", "unchanged vs previous period", "önceki döneme göre değişmedi", "oldingi davrga nisbatan o'zgarmagan") : delta + " " + Tr.T("к прошлому периоду", "мурунку мезгилге карата", "vs previous period", "önceki döneme göre", "oldingi davrga nisbatan"), 12));
        return tile;
    }

    private Grid Line(string left, string right, bool bold = false, bool danger = false)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 30 };
        var l = new TextBlock { Text = left, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        var r = new TextBlock { Text = right, VerticalAlignment = VerticalAlignment.Center, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal };
        r.Classes.Add("num");
        l.Bind(TextBlock.ForegroundProperty, l.GetResourceObservable("BrushText"));
        r.Bind(TextBlock.ForegroundProperty, r.GetResourceObservable(danger ? "BrushDanger" : "BrushText"));
        Grid.SetColumn(r, 1);
        g.Children.Add(l);
        g.Children.Add(r);
        return g;
    }

    private Control Card(string title, params Control[] rows)
    {
        var head = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(head);
        foreach (var r in rows)
            stack.Children.Add(r);
        return SectionCards.Group(this, stack);
    }

    private void RenderReport(CategoryAnalyticsData d)
    {
        var c = d.Current;
        var p = d.Previous;
        var period = $"{d.From:dd.MM.yyyy} — {d.To:dd.MM.yyyy}";
        var prevPeriod = $"{d.PrevFrom:dd.MM.yyyy} — {d.PrevTo:dd.MM.yyyy}";
        var title = d.SelectedIds.Count == 0
            ? Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar")
            : d.SelectionTitle;
        var head = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("BrushText"));
        _body.Children.Add(head);
        _body.Children.Add(Soft(period + "  ·  " + Tr.T("прошлый период: ", "мурунку мезгил: ", "previous period: ", "önceki dönem: ", "oldingi davr: ") + prevPeriod));

        if (c.Revenue <= 0)
            _body.Children.Add(SectionCards.Empty(this, Tr.T("За этот период по выбранным категориям продаж нет. Выберите другой период или другие категории.",
                "Бул мезгилде тандалган категориялар боюнча сатуу жок. Башка мезгил же башка категория тандаңыз.",
                "No sales in the selected categories for this period. Pick another period or other categories.",
                "Bu dönemde seçilen kategorilerde satış yok. Başka bir dönem veya kategori seçin.",
                "Bu davrda tanlangan kategoriyalar bo'yicha sotuv yo'q. Boshqa davr yoki kategoriya tanlang.")));

        // ---- плитки
        var costKnown = c.CostCoverage >= 0.5;
        var tiles = new KpiTileGrid { MinTileWidth = 200 };
        tiles.Children.Add(Tile(Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Money(c.Revenue), Delta(c.Revenue, p.Revenue)));
        tiles.Children.Add(Tile(Tr.T("Себестоимость", "Өздүк баа", "Cost of goods", "Maliyet", "Tannarx"), costKnown ? Money(c.Cost) : "—", costKnown ? Delta(c.Cost, p.Cost) : null));
        tiles.Children.Add(Tile(Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda"), costKnown ? Money(c.Gross) : "—", costKnown ? Delta(c.Gross, p.Gross) : null, danger: costKnown && c.Gross < 0));
        tiles.Children.Add(Tile(Tr.T("Маржа", "Маржа", "Margin", "Marj", "Marja"), costKnown ? Pct(c.MarginPct) : "—", costKnown ? Delta(c.MarginPct, p.MarginPct, points: true) : null, danger: costKnown && c.MarginPct < 0));
        tiles.Children.Add(Tile(Tr.T("Наценка", "Үстөк", "Markup", "Kâr marjı (maliyete göre)", "Ustama"), costKnown ? Pct(c.MarkupPct) : "—", costKnown ? Delta(c.MarkupPct, p.MarkupPct, points: true) : null));
        tiles.Children.Add(Tile(Tr.T("Продано, шт.", "Сатылды, даана", "Units sold", "Satılan adet", "Sotildi, dona"), c.Qty.ToString("0.###"), Delta(c.Qty, p.Qty)));
        tiles.Children.Add(Tile(Tr.T("Чеков с этими товарами", "Бул товарлар менен чектер", "Receipts with these goods", "Bu ürünlerle fişler", "Bu mahsulotlar bilan cheklar"), (d.SelectedIds.Count > 1 ? "≈ " : "") + c.Receipts, Delta(c.Receipts, p.Receipts)));
        tiles.Children.Add(Tile(Tr.T("Средний чек", "Орточо чек", "Average receipt", "Ortalama fiş", "O'rtacha chek"), Money(c.AvgReceipt), Delta(c.AvgReceipt, p.AvgReceipt)));
        tiles.Children.Add(Tile(Tr.T("Доля в выручке компании", "Компаниянын түшүмүндөгү үлүшү", "Share of company revenue", "Şirket cirosundaki pay", "Kompaniya tushumidagi ulushi"), Pct(d.SharePct), Delta(d.SharePct, d.CompanyPrevious.Revenue > 0 ? p.Revenue / d.CompanyPrevious.Revenue * 100 : 0, points: true)));
        _body.Children.Add(tiles);
        if (!costKnown && c.Revenue > 0)
            _body.Children.Add(Soft(Tr.T("У большинства проданных товаров категории не указана цена закупки — себестоимость и маржа не показаны, чтобы не ввести в заблуждение. Заполните закупочные цены в карточках товаров.",
                "Категориядагы сатылган товарлардын көбүндө сатып алуу баасы жок — өздүк баа жана маржа жалган көрсөтпөө үчүн көрсөтүлгөн жок. Товар карточкаларында сатып алуу баасын толтуруңуз.",
                "Most sold goods in this category have no purchase price — cost and margin are hidden so they do not mislead. Fill in purchase prices in the product cards.",
                "Bu kategoride satılan ürünlerin çoğunda alış fiyatı yok — yanıltmamak için maliyet ve marj gösterilmiyor. Ürün kartlarında alış fiyatlarını doldurun.",
                "Bu kategoriyada sotilgan mahsulotlarning ko'pida xarid narxi yo'q — chalg'itmaslik uchun tannarx va marja ko'rsatilmaydi. Mahsulot kartochkalarida xarid narxlarini to'ldiring.")));

        // ---- сравнение с компанией
        var co = d.Company;
        var cmp = new List<Control>
        {
            Line(Tr.T("Доля категории в выручке", "Категориянын түшүмдөгү үлүшү", "Category share of revenue", "Kategorinin ciro payı", "Kategoriyaning tushumdagi ulushi"), Pct(d.SharePct)),
        };
        if (costKnown && co.CostCoverage >= 0.5)
        {
            cmp.Add(Line(Tr.T("Маржа категории", "Категориянын маржасы", "Category margin", "Kategori marjı", "Kategoriya marjasi"), Pct(c.MarginPct), bold: true, danger: c.MarginPct < 0));
            cmp.Add(Line(Tr.T("Маржа всей компании (та же оценка)", "Бүт компаниянын маржасы (ошол эле баа)", "Whole company margin (same estimate)", "Tüm şirket marjı (aynı tahmin)", "Butun kompaniya marjasi (xuddi shu baho)"), Pct(co.MarginPct)));
            cmp.Add(Line(Tr.T("Категория относительно компании", "Категория компанияга карата", "Category vs company", "Kategori / şirket", "Kategoriya / kompaniya"),
                (c.MarginPct >= co.MarginPct ? "▲ " : "▼ ") + Math.Abs(c.MarginPct - co.MarginPct).ToString("0.#") + " " + Tr.T("п.п.", "п.п.", "pp", "puan", "p.p.")));
        }
        if (d.CompanyPnlMarginPct is { } pm)
            cmp.Add(Line(Tr.T("Маржа компании по отчёту прибылей (сервер, точнее)", "Компаниянын маржасы пайда отчету боюнча (сервер, тагыраак)", "Company margin per profit report (server, more exact)", "Kâr raporuna göre şirket marjı (sunucu, daha kesin)", "Foyda hisoboti bo'yicha kompaniya marjasi (server, aniqroq)"), Pct(pm)));
        _body.Children.Add(Card(Tr.T("Сравнение с компанией", "Компания менен салыштыруу", "Compared with the company", "Şirketle karşılaştırma", "Kompaniya bilan solishtirish"), cmp.ToArray()));

        // ---- расходы
        var exp = new List<Control>
        {
            Soft(Tr.T("Списания, брак, возвраты поставщику и уценка по товарам этой категории: сервер пока не отдаёт расходы по категориям, поэтому цифры здесь не выдуманы — их нет.",
                "Бул категориядагы товарлар боюнча эсептен чыгаруу, брак, жеткирүүчүгө кайтаруу жана арзандатуу: сервер категориялар боюнча чыгымдарды азырынча бербейт, ошондуктан бул жерде сандар ойлоп табылган эмес — алар жок.",
                "Write-offs, defects, supplier returns and markdowns for this category: the server does not yet give expenses by category, so no numbers are invented here.",
                "Bu kategorideki ürünlerin zayiat, fire, tedarikçiye iade ve indirimleri: sunucu henüz kategoriye göre gider vermiyor, bu yüzden burada uydurma rakam yok.",
                "Bu kategoriya mahsulotlari bo'yicha hisobdan chiqarish, brak, yetkazib beruvchiga qaytarish va arzonlashtirish: server hozircha kategoriyalar bo'yicha xarajat bermaydi, shuning uchun bu yerda raqam to'qilmagan.")),
        };
        if (d.Opex is { } opex)
        {
            exp.Add(Line(Tr.T("Общие расходы магазина за период — не относятся к категории", "Мезгилдеги дүкөндүн жалпы чыгымдары — категорияга таандык эмес", "Shop-wide expenses for the period — not tied to a category", "Dönem için mağazanın genel giderleri — kategoriye ait değil", "Davr uchun do'konning umumiy xarajatlari — kategoriyaga tegishli emas"), Money(opex)));
            if (costKnown && d.AllocatedOpex is { } alloc && d.NetAfterOpex is { } net)
            {
                exp.Add(Line(Tr.T($"Доля расходов, если делить ПРОПОРЦИОНАЛЬНО ВЫРУЧКЕ ({Pct(d.SharePct)})",
                    $"Чыгымдардын үлүшү, ТҮШҮМГӨ ПРОПОРЦИОНАЛДУУ бөлүнсө ({Pct(d.SharePct)})",
                    $"Expense share if split IN PROPORTION TO REVENUE ({Pct(d.SharePct)})",
                    $"Giderleri CİROYA ORANTILI paylaştırırsak payı ({Pct(d.SharePct)})",
                    $"Xarajatlarni TUSHUMGA MOS bo'lsak ulushi ({Pct(d.SharePct)})"), Money(alloc)));
                exp.Add(Line(Tr.T("Чистая прибыль категории после этой доли (оценка)", "Бул үлүштөн кийинки категориянын таза пайдасы (баалоо)", "Category net profit after this share (estimate)", "Bu paydan sonra kategorinin net kârı (tahmin)", "Shu ulushdan keyin kategoriyaning sof foydasi (baho)"), Money(net), bold: true, danger: net < 0));
            }
        }
        else
            exp.Add(Soft(Tr.T("Общие расходы магазина сервер не ответил.", "Дүкөндүн жалпы чыгымдарын сервер бербеди.", "The server did not return shop-wide expenses.", "Sunucu mağaza genel giderlerini vermedi.", "Server do'konning umumiy xarajatlarini bermadi.")));
        _body.Children.Add(Card(Tr.T("Расходы", "Чыгымдар", "Expenses", "Giderler", "Xarajatlar"), exp.ToArray()));

        // ---- движение товара
        var mov = new List<Control>
        {
            Line(Tr.T("Продано за период, шт.", "Мезгилде сатылды, даана", "Sold in the period, units", "Dönemde satılan adet", "Davrda sotildi, dona"), c.Qty.ToString("0.###") + (p.Qty > 0 ? "   " + Delta(c.Qty, p.Qty) : "")),
        };
        if (d.StockKnown)
        {
            mov.Add(Line(Tr.T("Остаток сейчас, шт.", "Азыр калдык, даана", "In stock now, units", "Şu an stok, adet", "Hozir qoldiq, dona"), d.StockQty.ToString("0.###")));
            mov.Add(Line(Tr.T("Остаток сейчас по закупочной цене", "Азыр калдык сатып алуу баасы боюнча", "Stock now at purchase price", "Alış fiyatıyla mevcut stok", "Hozirgi qoldiq xarid narxida"), Money(d.StockCost)));
            mov.Add(Line(Tr.T("Товаров с остатком", "Калдыгы бар товарлар", "Products in stock", "Stokta ürün", "Qoldig'i bor mahsulotlar"), d.StockProducts.ToString()));
        }
        if (d.Turnover is { } tv)
            mov.Add(Line(Tr.T("Оборачиваемость (раз за период)", "Айлануу (мезгилде жолу)", "Turnover (times per period)", "Devir hızı (dönemde kez)", "Aylanish (davrda marta)"), tv.ToString("0.##")));
        if (d.DaysOfStock is { } ds)
            mov.Add(Line(Tr.T("Запаса хватит, дней (при таких продажах)", "Запас канча күнгө жетет (мындай сатуу менен)", "Days of stock (at this sales pace)", "Stok kaç gün yeter (bu satış hızıyla)", "Zaxira necha kunga yetadi (shu sotuv bilan)"), ds.ToString("0")));
        mov.Add(Soft(Tr.T("Приход (закупки), возвраты клиентов и поставщикам, списания, перемещения и остаток на начало периода по категории сервер пока не отдаёт — этих строк здесь нет, чтобы не показывать угаданное.",
            "Категория боюнча кирешени (сатып алуулар), кардарлардан жана жеткирүүчүгө кайтаруулар, эсептен чыгаруулар, жылдырууларды жана мезгил башындагы калдыкты сервер азырынча бербейт — болжолдонгон сандарды көрсөтпөө үчүн бул саптар жок.",
            "Receipts (purchases), customer and supplier returns, write-offs, transfers and opening stock by category are not provided by the server yet — these rows are left out so nothing guessed is shown.",
            "Kategoriye göre mal girişi (alımlar), müşteri ve tedarikçi iadeleri, zayiat, transferler ve dönem başı stok henüz sunucudan gelmiyor — tahmin göstermemek için bu satırlar yok.",
            "Kategoriya bo'yicha kirim (xaridlar), mijoz va yetkazib beruvchiga qaytarishlar, hisobdan chiqarish, ko'chirish va davr boshidagi qoldiqni server hozircha bermaydi — taxminiy raqam ko'rsatmaslik uchun bu qatorlar yo'q.")));
        _body.Children.Add(Card(Tr.T("Движение товара", "Товардын кыймылы", "Product movement", "Ürün hareketi", "Mahsulot harakati"), mov.ToArray()));

        // ---- топ-10
        if (d.Top.Count > 0)
        {
            var chart = new StackPanel { Spacing = 4 };
            // 2026-10-26, владелец: «РАЗНЫЕ ЦВЕТА в диаграммах» — товары категории разноцветные (цвет по названию, ChartPalette); раньше — одной кистью темы.
            BarChartRenderer.Render(chart, d.Top.Select(t => (t.Name, t.Revenue, Money(t.Revenue))).ToList());
            var table = SectionCards.LinesTable(this,
                d.Top.Select(t => (t.Name, t.Qty.ToString("0.###"), Money(t.Revenue),
                    t.PurchasePrice > 0 && t.Revenue > 0 ? Pct((t.Revenue - t.Cost) / t.Revenue * 100) : "—")).ToList(),
                Tr.T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), Tr.T("Шт.", "Даана", "Qty", "Adet", "Dona"),
                Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum"), Tr.T("Маржа", "Маржа", "Margin", "Marj", "Marja"));
            _body.Children.Add(Card(Tr.T("Топ-10 товаров категории по выручке", "Категориянын түшүм боюнча топ-10 товары", "Top 10 products of the category by revenue", "Kategorinin ciroya göre en iyi 10 ürünü", "Kategoriyaning tushum bo'yicha top-10 mahsuloti"), chart, table));
        }

        // ---- аутсайдеры
        if (d.StockKnown)
        {
            var outs = new List<Control>();
            if (d.NotSold.Count == 0)
                outs.Add(Soft(Tr.T("Все товары категории с остатком продавались за период.", "Калдыгы бар бардык товарлар мезгилде сатылган.", "Every in-stock product of the category sold in the period.", "Stoktaki tüm ürünler dönemde satıldı.", "Qoldig'i bor barcha mahsulotlar davrda sotilgan.")));
            else
            {
                outs.Add(Soft(Tr.T($"Есть остаток, но не продавались за период: {d.NotSoldTotal}. Показаны 10 с самым большим остатком — это замороженные деньги.",
                    $"Калдыгы бар, бирок мезгилде сатылган эмес: {d.NotSoldTotal}. Эң чоң калдыктуу 10 көрсөтүлдү — бул тоңгон акча.",
                    $"In stock but not sold in the period: {d.NotSoldTotal}. The 10 with the largest stock are shown — this is frozen money.",
                    $"Stokta var ama dönemde satılmadı: {d.NotSoldTotal}. En büyük stoklu 10 gösteriliyor — bu donmuş para.",
                    $"Qoldig'i bor, lekin davrda sotilmagan: {d.NotSoldTotal}. Eng katta qoldiqli 10 tasi ko'rsatildi — bu muzlagan pul.")));
                foreach (var n in d.NotSold)
                    outs.Add(Line(n.Name, n.Qty.ToString("0.###")));
            }
            _body.Children.Add(Card(Tr.T("Не продавались (аутсайдеры)", "Сатылган жок (аутсайдерлер)", "Not selling (laggards)", "Satılmayanlar", "Sotilmaganlar"), outs.ToArray()));
        }

        // ---- пометки
        var notes = Tr.T("Себестоимость = проданное количество × закупочная цена из карточки товара СЕЙЧАС (сервер не хранит цену закупки на день продажи в этом отчёте): если закупка менялась, маржа прошлых дней приблизительная. Выручка, штуки и чеки — как в «Сводке». Разбивку категории по дням сервер пока не отдаёт.",
            "Өздүк баа = сатылган саны × товар карточкасындагы АЗЫРКЫ сатып алуу баасы (бул отчётто сервер сатуу күнүндөгү сатып алуу баасын сактабайт): сатып алуу баасы өзгөргөн болсо, мурунку күндөрдүн маржасы болжолдуу. Түшүм, даана жана чектер — «Жыйынтыктагыдай». Күндөр боюнча категориянын бөлүнүшүн сервер азырынча бербейт.",
            "Cost = sold quantity × the product card's CURRENT purchase price (this server report does not keep the purchase price on the sale day): if purchase prices changed, margin for past days is approximate. Revenue, units and receipts match “Summary”. A by-day split for a category is not provided by the server yet.",
            "Maliyet = satılan miktar × ürün kartındaki ŞİMDİKİ alış fiyatı (bu sunucu raporu satış günündeki alış fiyatını tutmuyor): alış fiyatı değiştiyse geçmiş günlerin marjı yaklaşıktır. Ciro, adet ve fişler «Özet» ile aynıdır. Kategoriye göre günlük döküm henüz sunucudan gelmiyor.",
            "Tannarx = sotilgan miqdor × mahsulot kartochkasidagi HOZIRGI xarid narxi (bu server hisoboti sotuv kunidagi xarid narxini saqlamaydi): xarid narxi o'zgargan bo'lsa, o'tgan kunlar marjasi taxminiy. Tushum, dona va cheklar «Xulosa» bilan bir xil. Kategoriya bo'yicha kunlik taqsimotni server hozircha bermaydi.");
        if (d.Truncated)
            notes += " " + Tr.T("Товаров очень много — список с сервера мог быть неполным.", "Товар өтө көп — сервердин тизмеси толук эмес болушу мүмкүн.", "There are very many products — the server list may be incomplete.", "Çok sayıda ürün var — sunucu listesi eksik olabilir.", "Mahsulotlar juda ko'p — server ro'yxati to'liq bo'lmasligi mumkin.");
        _body.Children.Add(Soft(notes, 12));
    }
}
