using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-30, просьба владельца «на складе, при нажатии на товар — модалка: какой партии, какого числа, от кого пришла, и ссылка на накладную».
/// Вкладка «Поступления» карточки товара (ProductEditDialog): итоги сверху, поиск по поставщику/№ и период (Неделя/Месяц/Год/Всё) закреплены,
/// список поступлений (новые сверху) прокручивается и виртуализирован — у ходового товара их сотни. У каждого: дата и время, поставщик
/// (клик — карточка поставщика), «Накладная № …» (клик — «Позиции прихода» с подсвеченной строкой товара), «Файл накладной» (если копия
/// сохранена), принял, оплата, количество × цена = сумма и «остаток партии» (ОЦЕНКА по FIFO — реальных партий NurCRM не ведёт).
/// Ролям без права на закупочные цены (как <see cref="WarehouseWindow.RevisionShowPurchase"/>) — только дата, количество и остаток партии.
/// Данные — <see cref="ProductReceiptsService"/>; окна и загрузка подменяются в стенде проверки.</summary>
internal sealed class ProductReceiptsView : UserControl
{
    public delegate Task<ProductReceiptsResult> Loader(Action<ProductReceiptsResult> progress, CancellationToken ct);

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string Q(double v) => v.ToString("0.###", Ru);

    private readonly Loader _loader;
    private readonly string _productId;
    private readonly double _stock;
    private readonly string _unit;
    private readonly bool _money;

    private readonly KpiTileGrid _kpi = new() { MinTileWidth = 150, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBlock _kpiLine = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), IsVisible = false };
    private readonly TextBox _search;
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly ListBox _list = new() { Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _empty;
    private readonly Button _showAll; // 2026-10-30: «Показать за всё время» — когда за выбранный период пусто, а за всё время есть
    private readonly StackPanel _emptyHost;
    private readonly Grid _head = new();

    private ProductReceiptsResult _data = new();
    private List<ReceiptItem> _items = new();
    private string _period = "all"; // 2026-10-30: по умолчанию «Всё» — «Неделя» показывала пустоту у товара с давними приходами
    private CancellationTokenSource? _cts;
    private IReadOnlyDictionary<string, PurchaseReceiptNote> _notes = new Dictionary<string, PurchaseReceiptNote>();
    private IReadOnlySet<string> _debtKeys = new HashSet<string>();
    private bool _loaded;

    /// <summary>Открыть «Позиции прихода» этого прихода (подсвечивая товар). Подменяется в стенде.</summary>
    public Func<ProductReceiptRow, string?, Task> OpenReceipt { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>Открыть карточку поставщика.</summary>
    public Func<ProductReceiptRow, Task> OpenSupplier { get; set; } = _ => Task.CompletedTask;

    /// <summary>Открыть сохранённый файл накладной по пути.</summary>
    public Action<string> OpenFile { get; set; } = PurchaseDocs.OpenFile;

    public ProductReceiptsView(string productId, double stock, string unit, bool showMoney, Loader loader)
    {
        _productId = productId;
        _stock = stock;
        _unit = string.IsNullOrWhiteSpace(unit) ? T("шт", "даана", "pcs", "adet", "dona") : unit;
        _money = showMoney;
        _loader = loader;

        Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class("rc-item"))
        {
            Setters =
            {
                new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(ListBoxItem.MinHeightProperty, 0.0),
                new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent), new Setter(ListBoxItem.FocusableProperty, false),
            },
        });
        foreach (var state in new[] { ":selected", ":pointerover" })
            Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class("rc-item").Class(state).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, Brushes.Transparent) },
            });

        Use(_kpiLine, TextBlock.ForegroundProperty, "BrushText");
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        _search = UiKit.Search(this, T("Поиск: поставщик или № прихода", "Издөө: жеткирүүчү же кирүү №", "Search: supplier or receipt no.", "Ara: tedarikçi veya giriş no.", "Qidirish: yetkazib beruvchi yoki kirim №"), 40);
        Avalonia.Automation.AutomationProperties.SetName(_search, _search.Watermark);
        _search.TextChanged += (_, _) => Render();
        BuildPeriods();

        var refresh = UiKit.Ghost(this, T("Обновить", "Жаңыртуу", "Refresh", "Yenile", "Yangilash"));
        refresh.Height = 40;
        refresh.Padding = new Thickness(14, 0);
        Avalonia.Automation.AutomationProperties.SetName(refresh, T("Обновить поступления", "Кирүүлөрдү жаңыртуу", "Refresh receipts", "Girişleri yenile", "Kirimlarni yangilash"));
        refresh.Click += (_, _) => { ProductReceiptsService.ResetScanCache(); StartLoad(); }; // 2026-10-30: «Обновить» — мимо 5-минутного кэша приходов

        // Строка управления: поиск | период | «Обновить». Узкое окно (Android, 1024×600 с боковыми полями) — два ряда.
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 2) };
        controls.Children.Add(_search);
        var seg = UiKit.Segment(_periods);
        seg.Margin = new Thickness(8, 0, 0, 0);
        seg.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(seg, 1);
        controls.Children.Add(seg);
        refresh.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(refresh, 2);
        controls.Children.Add(refresh);
        if (!_money)
            _search.IsVisible = false; // без права на закупки поиск по поставщику/№ не нужен
        controls.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 520 && _money;
            Grid.SetColumn(_search, 0);
            Grid.SetColumnSpan(_search, narrow ? 3 : 1);
            _search.Margin = new Thickness(0, 0, 0, narrow ? 6 : 0);
            if (narrow)
            {
                controls.RowDefinitions = new RowDefinitions("Auto,Auto");
                Grid.SetRow(seg, 1);
                Grid.SetRow(refresh, 1);
                Grid.SetColumn(seg, 0);
                Grid.SetColumnSpan(seg, 2);
                seg.Margin = new Thickness(0);
            }
            else
            {
                controls.RowDefinitions = new RowDefinitions("Auto");
                Grid.SetRow(seg, 0);
                Grid.SetRow(refresh, 0);
                Grid.SetColumn(seg, 1);
                Grid.SetColumnSpan(seg, 1);
                seg.Margin = new Thickness(8, 0, 0, 0);
            }
        };

        // Шапка столбцов — закреплена над списком.
        _head = BuildHead();

        _empty = new TextBlock
        {
            FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center,
        };
        Use(_empty, TextBlock.ForegroundProperty, "BrushTextSoft");
        _showAll = UiKit.Ghost(this, T("Показать за всё время", "Бардык убакытка көрсөтүү", "Show all time", "Tüm zamanları göster", "Butun davr uchun ko'rsatish"));
        _showAll.Height = 40;
        _showAll.Padding = new Thickness(14, 0);
        _showAll.HorizontalAlignment = HorizontalAlignment.Center;
        _showAll.IsVisible = false;
        _showAll.Click += (_, _) =>
        {
            _period = "all";
            BuildPeriods();
            Render();
        };
        _emptyHost = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(24, 40, 24, 0), IsVisible = false };
        _empty.Margin = new Thickness(0, 0, 0, 12);
        _emptyHost.Children.Add(_empty);
        _emptyHost.Children.Add(_showAll);

        _list.ItemTemplate = new FuncDataTemplate<ReceiptItem>((item, _) => item is null ? new Panel() : BuildRow(item));
        _list.ContainerPrepared += (_, e) => e.Container.Classes.Add("rc-item");
        _list.SelectionMode = SelectionMode.Single;
        var listHost = new Grid();
        listHost.Children.Add(_list);
        listHost.Children.Add(_emptyHost);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"), MinHeight = 220 };
        Grid.SetRow(_kpi, 0);
        Grid.SetRow(_kpiLine, 1);
        Grid.SetRow(controls, 2);
        Grid.SetRow(_status, 3);
        Grid.SetRow(_head, 4);
        Grid.SetRow(listHost, 5);
        root.Children.Add(_kpi);
        root.Children.Add(_kpiLine);
        root.Children.Add(controls);
        root.Children.Add(_status);
        root.Children.Add(_head);
        root.Children.Add(listHost);
        Content = root;

        // Низкое окно (моноблок 1024×600): плитки итогов — одной строкой текста, списку остаётся место.
        root.SizeChanged += (_, e) =>
        {
            var low = e.NewSize.Height < 430;
            _kpi.IsVisible = !low;
            _kpiLine.IsVisible = low;
        };
    }

    /// <summary>Загрузить при первом показе вкладки (запрос к серверу — карточку открывают часто).</summary>
    public void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        StartLoad();
    }

    // ------------------------------------------------------------------ загрузка

    private void StartLoad()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _status.Text = T("Загружаем поступления…", "Кирүүлөр жүктөлүүдө…", "Loading receipts…", "Girişler yükleniyor…", "Kirimlar yuklanmoqda…");
        try
        {
            _notes = PurchasesStore.ReceiptNotes();
            _debtKeys = SupplierDebtRecorder.DoneKeys();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поступления товара: журнал закупок не прочитан: {ex.Message}", "WARNING");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _loader(r => Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested)
                        Apply(r);
                }), cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!cts.IsCancellationRequested)
                        Apply(result);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Поступления товара {_productId}: {ex.Message}", "WARNING");
                Dispatcher.UIThread.Post(() => _status.Text = T("Поступления не загрузились: ", "Кирүүлөр жүктөлгөн жок: ", "Receipts failed to load: ", "Girişler yüklenemedi: ", "Kirimlar yuklanmadi: ") + ex.Message);
            }
        });
    }

    /// <summary>Закрыли карточку — незаконченную загрузку обрываем.</summary>
    public void Stop() => _cts?.Cancel();

    private void Apply(ProductReceiptsResult data)
    {
        _data = data;
        // Остаток партии считаем по ВСЕЙ истории (не по отфильтрованной) — период и поиск его не меняют.
        ProductReceiptsService.ApplyFifo(data.Rows, _stock);
        _items = data.Rows.Select(r => new ReceiptItem(r, _notes, _debtKeys)).ToList();
        BuildKpi();
        Render();
    }

    // ------------------------------------------------------------------ вид

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        foreach (var (key, text) in new[]
                 {
                     ("week", T("Неделя", "Апта", "Week", "Hafta", "Hafta")), ("month", T("Месяц", "Ай", "Month", "Ay", "Oy")),
                     ("year", T("Год", "Жыл", "Year", "Yıl", "Yil")), ("all", T("Всё", "Баары", "All", "Tümü", "Barchasi")),
                 })
        {
            var seg = UiKit.Seg(text, key == _period);
            seg.MinHeight = 40;
            seg.Click += (_, _) =>
            {
                _period = key;
                BuildPeriods();
                Render();
            };
            _periods.Children.Add(seg);
        }
    }

    private DateTime? PeriodFrom() => _period switch
    {
        "week" => DateTime.Today.AddDays(-6),
        "month" => DateTime.Today.AddMonths(-1),
        "year" => DateTime.Today.AddYears(-1),
        _ => null,
    };

    private void BuildKpi()
    {
        _kpi.Children.Clear();
        var s = ProductReceiptsService.Summarize(_data.Rows);
        var som = MoneyText.Currency;
        var total = Q(s.TotalQty) + " " + _unit;
        if (_money)
        {
            var avg = s.Count == 0 ? "—" : MoneyText.Amount(s.AvgPurchase) + " " + som;
            var last = s.LastPurchase is { } lp ? MoneyText.Amount(lp) + " " + som : "—";
            // 2026-10-30: итоги — по ВСЕЙ истории (период и поиск их не меняют); приходы без поставщика — «не указан», а не прочерк.
            var supplier = string.IsNullOrWhiteSpace(s.LastSupplier) ? (s.Count == 0 ? "—" : T("не указан", "көрсөтүлгөн эмес", "not specified", "belirtilmemiş", "ko'rsatilmagan")) : s.LastSupplier!;
            _kpi.Children.Add(UiKit.Kpi(T("Всего получено", "Жалпы алынды", "Total received", "Toplam alınan", "Jami qabul qilingan"), total));
            _kpi.Children.Add(UiKit.Kpi(T("Средняя закупка", "Орточо сатып алуу", "Average cost", "Ortalama alış", "O'rtacha xarid"), avg));
            _kpi.Children.Add(UiKit.Kpi(T("Последняя закупка", "Акыркы сатып алуу", "Last purchase", "Son alış", "Oxirgi xarid"), last));
            _kpi.Children.Add(UiKit.Kpi(T("Последний поставщик", "Акыркы жеткирүүчү", "Last supplier", "Son tedarikçi", "Oxirgi yetkazib beruvchi"), supplier));
            _kpiLine.Text = T("Всего получено ", "Жалпы алынды ", "Received ", "Alınan ", "Qabul qilingan ") + total + (s.Count == 0 ? "" : " · " + T("средняя ", "орточо ", "avg ", "ort. ", "o'rtacha ") + avg
                             + " · " + T("последняя ", "акыркы ", "last ", "son ", "oxirgi ") + last + (supplier == "—" ? "" : " · " + supplier));
        }
        else
        {
            var lastDate = s.LastAt is { } at ? at.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "—";
            _kpi.Children.Add(UiKit.Kpi(T("Всего получено", "Жалпы алынды", "Total received", "Toplam alınan", "Jami qabul qilingan"), total));
            _kpi.Children.Add(UiKit.Kpi(T("Последнее поступление", "Акыркы кабыл алуу", "Last receipt", "Son giriş", "Oxirgi kirim"), lastDate));
            _kpiLine.Text = T("Всего получено ", "Жалпы алынды ", "Received ", "Alınan ", "Qabul qilingan ") + total + " · " + T("последнее ", "акыркы ", "last ", "son ", "oxirgi ") + lastDate;
        }
    }

    private void Render()
    {
        var filtered = ProductReceiptsService.Filter(_data.Rows, PeriodFrom(), _money ? _search.Text : null);
        var set = filtered.ToHashSet();
        var shown = _items.Where(i => set.Contains(i.Row)).ToList();
        _list.ItemsSource = shown;

        _emptyHost.IsVisible = shown.Count == 0 && _data.Done;
        // 2026-10-30: пустой период при непустой истории — объясняем и даём кнопку «Показать за всё время» (владелец не понял, «где список поставщиков»).
        var periodOnly = _data.Rows.Count > 0 && PeriodFrom() is not null && string.IsNullOrWhiteSpace(_money ? _search.Text : null);
        _showAll.IsVisible = periodOnly;
        if (_data.Rows.Count == 0)
            _empty.Text = T("У этого товара ещё нет оприходований. Они появятся после приёмки или загрузки накладной.", "Бул товарда азырынча кирүү жок. Кабыл алуудан же накладной жүктөгөндөн кийин пайда болот.",
                "This product has no receipts yet. They will appear after receiving or loading an invoice.", "Bu ürünün henüz girişi yok. Mal kabulden veya fatura yüklemesinden sonra görünür.", "Bu mahsulotda hali kirim yo'q. Qabul yoki yuk xati yuklangandan keyin paydo bo'ladi.");
        else if (periodOnly)
        {
            var newest = _data.Rows.FirstOrDefault(r => r.At != default);
            var n = _data.Rows.Count;
            var date = newest is null ? "—" : newest.At.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
            var name = _money && newest is not null && !string.IsNullOrWhiteSpace(newest.SupplierName) ? newest.SupplierName : "";
            _empty.Text = T($"За выбранный период поступлений нет (за всё время: {n}, последний — {date}{(name.Length > 0 ? ", поставщик " + name : "")})",
                $"Тандалган мөөнөттө кирүүлөр жок (бардыгы: {n}, акыркысы — {date}{(name.Length > 0 ? ", жеткирүүчү " + name : "")})",
                $"No receipts in the selected period (all time: {n}, latest — {date}{(name.Length > 0 ? ", supplier " + name : "")})",
                $"Seçilen dönemde giriş yok (tüm zamanlar: {n}, son — {date}{(name.Length > 0 ? ", tedarikçi " + name : "")})",
                $"Tanlangan davrda kirim yo'q (jami: {n}, oxirgisi — {date}{(name.Length > 0 ? ", yetkazib beruvchi " + name : "")})");
        }
        else
            _empty.Text = T("Ничего не найдено — измените период или поиск.", "Эч нерсе табылган жок — мөөнөттү же издөөнү өзгөртүңүз.", "Nothing found — change the period or the search.",
                "Bulunamadı — dönemi veya aramayı değiştirin.", "Hech narsa topilmadi — davr yoki qidiruvni o'zgartiring.");

        var text = new List<string>();
        if (!_data.Done)
            text.Add(T($"Загружаем… {_data.Scanned} из {_data.TotalReceipts} приходов", $"Жүктөлүүдө… {_data.Scanned} / {_data.TotalReceipts} кирүү",
                $"Loading… {_data.Scanned} of {_data.TotalReceipts} receipts", $"Yükleniyor… {_data.Scanned} / {_data.TotalReceipts} giriş", $"Yuklanmoqda… {_data.Scanned} / {_data.TotalReceipts} kirim"));
        text.Add(T($"Показано {shown.Count} из {_data.Rows.Count}", $"Көрсөтүлдү {shown.Count} / {_data.Rows.Count}", $"Showing {shown.Count} of {_data.Rows.Count}",
            $"Gösterilen {shown.Count} / {_data.Rows.Count}", $"Ko'rsatildi {shown.Count} / {_data.Rows.Count}"));
        if (_data.Offline)
            text.Add(_data.LoadedAt > DateTime.MinValue
                ? T($"Нет связи с сервером — данные от {_data.LoadedAt:HH:mm}", $"Сервер менен байланыш жок — маалымат {_data.LoadedAt:HH:mm}", $"No connection — data from {_data.LoadedAt:HH:mm}",
                    $"Bağlantı yok — veriler {_data.LoadedAt:HH:mm}", $"Aloqa yo'q — ma'lumot {_data.LoadedAt:HH:mm}")
                : T("Нет связи с сервером — показаны только приёмки этой кассы", "Сервер менен байланыш жок — ушул кассанын кабыл алуулары гана көрсөтүлдү",
                    "No connection — only this till's receiving is shown", "Bağlantı yok — yalnızca bu kasanın mal kabulleri gösteriliyor", "Aloqa yo'q — faqat shu kassaning qabullari ko'rsatilgan"));
        if (_data.Truncated)
            text.Add(T($"просмотрены первые {ProductReceiptsService.MaxPages * 100} приходов", $"алгачкы {ProductReceiptsService.MaxPages * 100} кирүү каралды",
                $"first {ProductReceiptsService.MaxPages * 100} receipts scanned", $"ilk {ProductReceiptsService.MaxPages * 100} giriş tarandı", $"dastlabki {ProductReceiptsService.MaxPages * 100} kirim ko'rildi"));
        _status.Text = string.Join(" · ", text);
    }

    private Grid BuildHead()
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,Auto"), Margin = new Thickness(10, 0, 14, 4) };
        TextBlock Cap(string text, int col, HorizontalAlignment align)
        {
            var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, HorizontalAlignment = align, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, col);
            return t;
        }
        g.Children.Add(Cap(T("Дата", "Күнү", "Date", "Tarih", "Sana"), 0, HorizontalAlignment.Left));
        g.Children.Add(Cap(_money ? T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi") : T("Поступление", "Кабыл алуу", "Receipt", "Giriş", "Kirim"),
            1, HorizontalAlignment.Left));
        g.Children.Add(Cap(_money ? T("Сумма · остаток партии (оценка)", "Суммасы · партия калдыгы (болжол)", "Amount · batch left (estimate)", "Tutar · parti kalanı (tahmin)", "Summa · partiya qoldig'i (taxmin)")
            : T("Остаток партии (оценка)", "Партия калдыгы (болжол)", "Batch left (estimate)", "Parti kalanı (tahmin)", "Partiya qoldig'i (taxmin)"), 2, HorizontalAlignment.Right));
        return g;
    }

    private Control BuildRow(ReceiptItem it)
    {
        var r = it.Row;
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };

        TextBlock Text(string text, double size, string brush, FontWeight? weight = null, bool num = false)
        {
            var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeight.Normal, TextWrapping = TextWrapping.Wrap };
            if (num)
                t.Classes.Add("num");
            Use(t, TextBlock.ForegroundProperty, brush);
            return t;
        }

        // Дата и время.
        var when = new StackPanel { Margin = new Thickness(0, 2, 8, 0) };
        when.Children.Add(Text(it.DateText, 13, "BrushText", FontWeight.SemiBold, num: true));
        when.Children.Add(Text(it.TimeText, 12, "BrushTextSoft", num: true));
        Grid.SetRowSpan(when, 3);
        g.Children.Add(when);

        if (_money)
        {
            // Поставщик — ссылка на карточку.
            Control supplier;
            if (it.HasSupplier)
            {
                var b = SectionCards.Ghost(r.SupplierName);
                b.MinHeight = 32;
                b.Padding = new Thickness(0, 2);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.FontWeight = FontWeight.SemiBold;
                b.FontSize = 13.5;
                b.Content = new TextBlock { Text = r.SupplierName, TextDecorations = TextDecorations.Underline, TextTrimming = TextTrimming.CharacterEllipsis };
                ToolTip.SetTip(b, T("Открыть карточку поставщика", "Жеткирүүчүнүн картасын ачуу", "Open the supplier card", "Tedarikçi kartını aç", "Yetkazib beruvchi kartasini ochish"));
                b.Click += async (_, _) => await OpenSupplier(r).ConfigureAwait(true);
                supplier = b;
            }
            else
            {
                // 2026-10-30: приход с сайта без поставщика — «Поставщик не указан», а не пустое место; приёмка этой кассы без поставщика — как раньше.
                supplier = r.FromServer
                    ? Text(T("Поставщик не указан", "Жеткирүүчү көрсөтүлгөн эмес", "Supplier not specified", "Tedarikçi belirtilmemiş", "Yetkazib beruvchi ko'rsatilmagan"), 13.5, "BrushTextSoft", FontWeight.SemiBold)
                    : Text(T("Без поставщика (приёмка кассы)", "Жеткирүүчүсүз (касса кабыл алуусу)", "No supplier (till receiving)", "Tedarikçisiz (kasa mal kabulü)", "Yetkazib beruvchisiz (kassa qabuli)"),
                        13.5, "BrushText", FontWeight.SemiBold);
            }
            Grid.SetColumn(supplier, 1);
            g.Children.Add(supplier);

            // Документ, принял, оплата, возврат.
            var details = new WrapPanel { Orientation = Orientation.Horizontal };
            void Part(Control c)
            {
                c.VerticalAlignment = VerticalAlignment.Center;
                details.Children.Add(c);
            }
            if (!string.IsNullOrEmpty(r.ReceiptId))
            {
                var invoice = SectionCards.Ghost(T("Накладная № ", "Накладной № ", "Invoice № ", "Fatura № ", "Yuk xati № ") + r.Number + " ›");
                invoice.MinHeight = 30;
                invoice.Padding = new Thickness(0, 2, 10, 2);
                invoice.FontSize = 12.5;
                invoice.HorizontalAlignment = HorizontalAlignment.Left;
                ToolTip.SetTip(invoice, T("Открыть «Позиции прихода»: состав документа, строка этого товара выделена", "«Кирүү позициялары» ачылат: документтин курамы, ушул товардын сабы белгиленет",
                    "Opens “Delivery items”: the document lines, this product is highlighted", "«Giriş kalemleri» açılır: belge satırları, bu ürün vurgulanır", "«Kirim pozitsiyalari» ochiladi: hujjat tarkibi, shu mahsulot ajratiladi"));
                Avalonia.Automation.AutomationProperties.SetName(invoice, T("Накладная № ", "Накладной № ", "Invoice № ", "Fatura № ", "Yuk xati № ") + r.Number);
                invoice.Click += async (_, _) => await OpenReceipt(r, it.FilePath).ConfigureAwait(true);
                Part(invoice);
                if (it.FilePath is not null)
                {
                    var file = SectionCards.Ghost(T("Файл накладной", "Накладной файлы", "Invoice file", "Fatura dosyası", "Yuk xati fayli"));
                    file.MinHeight = 30;
                    file.Padding = new Thickness(0, 2, 10, 2);
                    file.FontSize = 12.5;
                    file.IsVisible = !OperatingSystem.IsAndroid();
                    file.Click += (_, _) => OpenFile(it.FilePath);
                    Part(file);
                }
            }
            foreach (var piece in it.DetailParts)
            {
                var piecePart = Text(piece, 12, "BrushTextSoft");
                piecePart.Margin = new Thickness(0, 0, 10, 0);
                Part(piecePart);
            }
            details.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(details, 1);
            Grid.SetRow(details, 1);
            Grid.SetRowSpan(details, 2);
            g.Children.Add(details);

            // Сумма и «количество × цена».
            var sum = Text(it.SumText, 13.5, "BrushText", FontWeight.Bold, num: true);
            sum.HorizontalAlignment = HorizontalAlignment.Right;
            sum.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(sum, 2);
            g.Children.Add(sum);
            var qtyLine = Text(it.QtyPriceText, 12, "BrushTextSoft", num: true);
            qtyLine.HorizontalAlignment = HorizontalAlignment.Right;
            qtyLine.TextWrapping = TextWrapping.NoWrap;
            Grid.SetColumn(qtyLine, 2);
            Grid.SetRow(qtyLine, 1);
            g.Children.Add(qtyLine);
        }
        else
        {
            var qty = Text(Q(r.Net) + " " + it.UnitText, 13.5, "BrushText", FontWeight.SemiBold, num: true);
            Grid.SetColumn(qty, 1);
            g.Children.Add(qty);
        }

        // Остаток партии (оценка) и статус.
        var (bg, fg) = it.StatusBrushes;
        var pill = SectionCards.Pill(this, it.PillText, bg, fg);
        pill.HorizontalAlignment = HorizontalAlignment.Right;
        pill.Margin = new Thickness(0, 4, 0, 0);
        ToolTip.SetTip(pill, T($"Оценка по FIFO: раньше пришедший товар продаётся первым, поэтому текущий остаток ({Q(_stock)} {_unit}) считается из самых свежих поступлений. Реальные партии NurCRM не ведёт.",
            $"FIFO боюнча болжол: мурун келген товар биринчи сатылат, ошондуктан учурдагы калдык ({Q(_stock)} {_unit}) эң жаңы кирүүлөрдөн эсептелет. NurCRM чыныгы партияларды жүргүзбөйт.",
            $"FIFO estimate: earlier stock is sold first, so the current stock ({Q(_stock)} {_unit}) is taken from the newest receipts. NurCRM does not track real batches.",
            $"FIFO tahmini: önce gelen mal önce satılır, bu yüzden mevcut stok ({Q(_stock)} {_unit}) en yeni girişlerden hesaplanır. NurCRM gerçek parti tutmaz.",
            $"FIFO taxmini: avval kelgan tovar birinchi sotiladi, shuning uchun joriy qoldiq ({Q(_stock)} {_unit}) eng yangi kirimlardan hisoblanadi. NurCRM haqiqiy partiyalarni yuritmaydi."));
        Grid.SetColumn(pill, 2);
        Grid.SetRow(pill, 2);
        g.Children.Add(pill);

        var box = new Border { Padding = new Thickness(10, 8, 14, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
        box.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        return box;
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, this.GetResourceObservable(key));

    // ------------------------------------------------------------------ строка списка

    /// <summary>Строка списка с готовыми подписями. Файл накладной ищется лениво — только для строк, которые реально показаны.</summary>
    private sealed class ReceiptItem
    {
        private readonly IReadOnlyDictionary<string, PurchaseReceiptNote> _notes;
        private readonly IReadOnlySet<string> _debtKeys;
        private string? _filePath;
        private bool _fileLooked;

        public ReceiptItem(ProductReceiptRow row, IReadOnlyDictionary<string, PurchaseReceiptNote> notes, IReadOnlySet<string> debtKeys)
        {
            Row = row;
            _notes = notes;
            _debtKeys = debtKeys;
        }

        public ProductReceiptRow Row { get; }

        public bool HasSupplier => !string.IsNullOrWhiteSpace(Row.SupplierName);
        public string DateText => Row.At == default ? "—" : Row.At.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        public string TimeText => Row.At == default ? "" : Row.At.ToString("HH:mm", CultureInfo.InvariantCulture);
        public string UnitText => string.IsNullOrWhiteSpace(Row.Unit) ? T("шт", "даана", "pcs", "adet", "dona") : Row.Unit;
        public string SumText => MoneyText.Amount(Row.Sum) + " " + MoneyText.Currency;
        public string QtyPriceText => Q(Row.Quantity) + " " + UnitText + " × " + MoneyText.Amount(Row.PurchasePrice);

        /// <summary>Копия файла накладной (журнал загрузок накладных этого ПК), если она есть и подходит к приходу.</summary>
        public string? FilePath
        {
            get
            {
                if (!_fileLooked)
                {
                    _fileLooked = true;
                    if (!string.IsNullOrEmpty(Row.ReceiptId) && InvoiceImportRegistry.FindForReceipt(Row.SupplierName, Row.At, Row.ReceiptTotal) is { } entry)
                        _filePath = InvoiceImportRegistry.StoredPath(entry);
                }
                return _filePath;
            }
        }

        /// <summary>Принял, способ оплаты (если журнал кассы его помнит), возврат поставщику.</summary>
        public IEnumerable<string> DetailParts
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Row.Employee))
                    yield return T("принял ", "кабыл алды ", "by ", "teslim aldı ", "qabul qildi ") + Row.Employee;
                var pay = PayText();
                if (pay.Length > 0)
                    yield return pay;
                if (Row.ReturnedQty > 0.0005)
                    yield return T("возвращено ", "кайтарылды ", "returned ", "iade ", "qaytarildi ") + Q(Row.ReturnedQty);
            }
        }

        private string PayText()
        {
            if (string.IsNullOrEmpty(Row.ReceiptId))
                return "";
            if (_notes.TryGetValue(Row.ReceiptId!, out var note))
                return note.PayKind switch
                {
                    "cash" or "drawer" => T("оплачено", "төлөндү", "paid", "ödendi", "to'landi"),
                    "partial" => T("частично в долг", "жарым карызга", "partly on credit", "kısmen veresiye", "qisman qarzga"),
                    "debt" => T("в долг", "карызга", "on credit", "veresiye", "qarzga"),
                    _ => "",
                };
            return _debtKeys.Contains(SupplierDebtRecorder.MarkerForReceipt(Row.ReceiptId!)) ? T("в долг", "карызга", "on credit", "veresiye", "qarzga") : "";
        }

        /// <summary>«В остатке · 12 из 20 шт» / «Частично · …» / «Продана» / «Возвращена».</summary>
        public string PillText
        {
            get
            {
                var unit = UnitText;
                if (Row.Net <= 0.0005)
                    return T("Возвращена", "Кайтарылган", "Returned", "İade edildi", "Qaytarilgan");
                if (Row.Remaining <= 0.0005)
                    return T("Продана · 0 из ", "Сатылган · 0 / ", "Sold · 0 of ", "Satıldı · 0 / ", "Sotilgan · 0 / ") + Q(Row.Net) + " " + unit;
                if (Row.Remaining + 0.0005 >= Row.Net)
                    return T("В остатке · ≈ ", "Калдыкта · ≈ ", "In stock · ≈ ", "Stokta · ≈ ", "Qoldiqda · ≈ ") + Q(Row.Remaining) + " " + unit;
                return T("Частично · ≈ ", "Жарым-жартылай · ≈ ", "Partly · ≈ ", "Kısmen · ≈ ", "Qisman · ≈ ") + Q(Row.Remaining) + T(" из ", " / ", " of ", " / ", " / ") + Q(Row.Net) + " " + unit;
            }
        }

        public (string Background, string Foreground) StatusBrushes
        {
            get
            {
                if (Row.Net <= 0.0005)
                    return ("BrushDangerSoft", "BrushDangerOnSoft");
                if (Row.Remaining <= 0.0005)
                    return ("BrushPanelSoft", "BrushTextSoft");
                if (Row.Remaining + 0.0005 >= Row.Net)
                    return ("BrushSuccessSoft", "BrushSuccess");
                return ("BrushWarningSoft", "BrushWarning");
            }
        }
    }
}

/// <summary>2026-10-30: действия вкладки «Поступления» — «Позиции прихода» с выделенной строкой товара и карточка поставщика. Окна — через общий PosDialogHost.</summary>
internal static class ProductReceiptsActions
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static void Toast(string text)
    {
        var prompts = NurMarketKassa.AvaloniaHost.App.AppHost?.Services.GetService<NurMarketKassa.Core.Contracts.IUserPrompts>();
        if (prompts is not null)
            prompts.ShowToast(text, isWarning: true);
        else
            PosLogger.Log(text, "WARNING");
    }

    public static async Task OpenReceiptAsync(Window owner, SupplierApiService api, ProductReceiptRow row, string productId, string? filePath)
    {
        if (string.IsNullOrEmpty(row.ReceiptId))
            return;
        SupplierReceipt detail;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            detail = await api.GetReceiptAsync(row.ReceiptId!, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поступления товара: приход {row.ReceiptId} не загружен ({ex.Message}).", "WARNING");
            Toast(T("Приход не загрузился — проверьте связь.", "Кирүү жүктөлгөн жок — байланышты текшериңиз.", "The receipt didn't load — check the connection.",
                "Giriş yüklenmedi — bağlantıyı kontrol edin.", "Kirim yuklanmadi — aloqani tekshiring."));
            return;
        }

        SupplierInfo? supplier = null;
        try
        {
            if (!string.IsNullOrEmpty(detail.SupplierId))
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                supplier = await api.GetAsync(detail.SupplierId, cts.Token).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поступления товара: поставщик {detail.SupplierId} не загружен ({ex.Message}).", "WARNING");
        }

        var note = PurchasesStore.ReceiptNotes().GetValueOrDefault(detail.Id);
        var debt = SupplierDebtRecorder.DoneKeys().Contains(SupplierDebtRecorder.MarkerForReceipt(detail.Id));
        // 2026-10-30: «Накладная №…» из карточки товара — та же форма «Накладная прихода», что и в «Закупках».
        var dialog = new InvoiceDocumentWindow(detail, note, debt, supplier, new PurchaseReceiptContext(productId, filePath));
        await PosDialogHost.ShowAsync(dialog, owner).ConfigureAwait(true);
    }

    public static async Task OpenSupplierAsync(Window owner, SupplierApiService api, ProductReceiptRow row)
    {
        SupplierInfo? info = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (!string.IsNullOrEmpty(row.SupplierId))
                info = await api.GetAsync(row.SupplierId, cts.Token).ConfigureAwait(true);
            if (info is null)
            {
                var all = await api.ListAsync(cts.Token).ConfigureAwait(true);
                info = all.FirstOrDefault(s => string.Equals(s.Name.Trim(), row.SupplierName.Trim(), StringComparison.CurrentCultureIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Поступления товара: поставщик «{row.SupplierName}» не загружен ({ex.Message}).", "WARNING");
        }

        if (info is null)
        {
            Toast(T("Карточка поставщика не открылась — поставщик не найден или нет связи.", "Жеткирүүчүнүн картасы ачылган жок — жеткирүүчү табылган жок же байланыш жок.",
                "The supplier card didn't open — supplier not found or no connection.", "Tedarikçi kartı açılmadı — tedarikçi bulunamadı veya bağlantı yok.", "Yetkazib beruvchi kartasi ochilmadi — topilmadi yoki aloqa yo'q."));
            return;
        }

        await PosDialogHost.ShowAsync(new SupplierCardWindow(api, info, owner), owner).ConfigureAwait(true);
    }
}

/// <summary>2026-10-30: отдельное окно с готовой карточкой поставщика (SupplierCardView из «Поставщиков») — для ссылки из «Поступлений» товара.</summary>
internal sealed class SupplierCardWindow : Window
{
    public SupplierCardWindow(SupplierApiService api, SupplierInfo info, Window owner)
    {
        Title = info.Name;
        Width = Math.Max(360, Math.Min(1000, owner.Bounds.Width > 0 ? owner.Bounds.Width - 24 : 1000));
        Height = Math.Max(360, Math.Min(760, owner.Bounds.Height > 0 ? owner.Bounds.Height - 24 : 760));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushWindowBackdrop"));
        Content = new SupplierCardView(api, info, null, new[] { info }, Close, _ => { }) { Margin = new Thickness(24, 16) };
    }
}
