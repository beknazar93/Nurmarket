using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Analytics;

/// <summary>Раздел «ABC-анализ» целиком: вкладки по срезам, в каждой — легенда, диаграмма
/// Парето, доли групп и таблица.
///
/// Собран кодом, а не разметкой, по двум причинам. Срезов пять, и в XAML это были бы пять
/// почти одинаковых кусков по полсотни строк в каждом из двух окон — любая правка пришлось бы
/// повторять десять раз. Плюс набор срезов задаётся данными (AnalyticsReportData.AbcSlices), а
/// не разметкой: добавится шестой срез — вкладка появится сама.
///
/// Вкладки создаются один раз, дальше обновляется их содержимое: пересоздание сбрасывало бы
/// выбранную вкладку на первую при каждом обновлении периода.
///
/// 2026-09-27: какие срезы показывать, решает место, где стоит раздел (<see cref="SliceKeys"/>):
/// в «Финансах» программы владельца — деньги, в «Продажах» — штуки, категории и бренды, на
/// «Складе» — стоимость остатка, в «Сводке» и окне ABC — всё. По умолчанию — пять срезов продаж и
/// сезонность, как было до разделения: касса выглядит по-прежнему.</summary>
public sealed class AbcSectionView : UserControl
{
    /// <summary>Срезы по продажам за период — набор по умолчанию (касса).</summary>
    public static readonly IReadOnlyList<string> SalesSliceKeys =
    [
        AnalyticsReportData.KeyRevenue, AnalyticsReportData.KeyProfit, AnalyticsReportData.KeyQuantity,
        AnalyticsReportData.KeyCategory, AnalyticsReportData.KeyBrand,
    ];

    /// <summary>Все срезы, включая склад по стоимости остатка.</summary>
    public static readonly IReadOnlyList<string> AllSliceKeys = [.. SalesSliceKeys, AnalyticsReportData.KeyStock];

    // 2026-10-18, доводка О4 (две полосы одинаковых подчёркнутых вкладок подряд): срезы — сегментами в рамке (sec-seg, выбранный
    // тёмно-синий), как период; страница среза — под ними. Одна страница (склад, «продаж нет») — без полосы выбора.
    private readonly StackPanel _segItems = new() { Orientation = Orientation.Horizontal };
    private readonly Border _segment;
    private readonly ScrollViewer _segScroll;
    private readonly ContentControl _page = new();
    private readonly List<(Button Button, Control Content)> _pages = [];
    private int _selected;
    private readonly List<SliceView> _views = [];

    // 2026-10-30 (стресс-тест: 20 000 чеков, 10 000 товаров): все срезы наполнялись разом — 250–290 мс в UI-потоке (3 таблицы по 10 000 строк
    // + диаграммы), фриз «ABC-анализа» 825 мс. Теперь наполняется только открытая вкладка, остальные — при первом переключении на них.
    private IReadOnlyList<AnalyticsReportData.AbcSlice> _slicesNow = [];
    private bool[] _dirty = [];
    private readonly SeasonalityView _seasonality = new();
    private IReadOnlyList<string> _sliceKeys = SalesSliceKeys;
    private bool _showSeasonality = true;

    /// <summary>Раскладка вкладок, построенная в прошлый раз: пока она та же, вкладки не
    /// пересоздаются и выбранная остаётся выбранной.</summary>
    private string? _layout;

    /// <summary>Какие срезы показывать (коды <see cref="AnalyticsReportData.AbcSlice.Key"/>).
    /// Задаётся до первого <see cref="Update"/>; порядок вкладок — порядок срезов в отчёте.</summary>
    public IReadOnlyList<string> SliceKeys
    {
        get => _sliceKeys;
        set
        {
            _sliceKeys = value is { Count: > 0 } ? value : SalesSliceKeys;
            _layout = null;
        }
    }

    /// <summary>Вкладка «Сезонность». В программе владельца она живёт в разделе «Аналитика», и
    /// там, где её нет, отчёт можно строить без неё (includeSeasonality: false) — это полный
    /// проход по всей истории продаж.</summary>
    public bool ShowSeasonality
    {
        get => _showSeasonality;
        set
        {
            _showSeasonality = value;
            _layout = null;
        }
    }

    /// <summary>Предел высоты таблицы среза. Таблица внутри прокрутки получает бесконечную высоту
    /// и рисует все строки разом — на срезе склада это тысячи строк. С пределом она прокручивается
    /// сама и рисует только видимые. По умолчанию предела нет — как было в кассе.</summary>
    public double TableMaxHeight { get; set; } = double.PositiveInfinity;

    /// <summary>2026-10-21 (пакет 2, п. 86, аудит 18.10: «прокрутка внутри прокрутки» в Сводке): false — у среза нет своей
    /// вертикальной прокрутки, раздел растёт по содержимому и прокручивается вместе со страницей, где стоит (Сводка).
    /// По умолчанию true — как было (окна «Продажи», «Финансы», ABC, касса).</summary>
    public bool ScrollsVertically { get; set; } = true;

    /// <summary>Попросили разбор конкретного товара — нажали на столбец диаграммы Парето или
    /// на строку таблицы. Само окно раздел не открывает: он живёт и во вкладке «Продаж», и в
    /// отдельном окне ABC, а владелец окна у них разный.</summary>
    public event Action<string>? ProductAnalyticsRequested;

    public AbcSectionView()
    {
        _segment = NurMarketKassa.AvaloniaHost.Views.UiKit.Segment(_segItems);
        _segment.HorizontalAlignment = HorizontalAlignment.Left;
        _segScroll = new ScrollViewer
        {
            Content = _segment,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 8, 0, 4),
        };
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(_page, 1);
        root.Children.Add(_segScroll);
        root.Children.Add(_page);
        Content = root;
    }

    /// <summary>2026-10-18, доводка О4: страница среза (вместо вкладки TabControl).</summary>
    private void AddPage(string title, Control content)
    {
        var index = _pages.Count;
        var button = NurMarketKassa.AvaloniaHost.Views.UiKit.Seg(title, false);
        button.Click += (_, _) => Select(index);
        _segItems.Children.Add(button);
        _pages.Add((button, content));
    }

    private void Select(int index)
    {
        if (_pages.Count == 0)
        {
            _page.Content = null;
            return;
        }
        _selected = Math.Clamp(index, 0, _pages.Count - 1);
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Button.Classes.Set("active", i == _selected);
        _page.Content = _pages[_selected].Content;
        FillSelectedView();
    }

    /// <summary>2026-10-30: наполнить срез открытой вкладки, если он ещё не показан для текущих данных.</summary>
    private void FillSelectedView()
    {
        if (_selected < 0 || _selected >= _pages.Count || _pages[_selected].Content is not SliceView view)
            return;
        var i = _views.IndexOf(view);
        if (i < 0 || i >= _slicesNow.Count || i >= _dirty.Length || !_dirty[i])
            return;
        _dirty[i] = false;
        view.Show(_slicesNow[i]);
    }

    /// <summary>Заполняет раздел. Срезы приходят в том же порядке, поэтому вкладка,
    /// выбранная пользователем, остаётся выбранной.</summary>
    public void Update(AnalyticsReportData data)
    {
        _serverFailed = data.ServerFailed;
        if (_showSeasonality)
            _seasonality.Show(data.Seasonality);
        UpdateSlices(data.AbcSlices);
    }

    /// <summary>2026-10-31 (стресс-тест 1.17.68, п. 9): сервер не ответил — вместо «продаж нет» плашка «Нет связи» и «Повторить».</summary>
    private bool _serverFailed;

    public event Action? RetryRequested;

    private void UpdateSlices(IReadOnlyList<AnalyticsReportData.AbcSlice> allSlices)
    {
        var slices = allSlices.Where(s => _sliceKeys.Contains(s.Key)).ToList();
        _slicesNow = slices;
        _dirty = new bool[slices.Count];
        Array.Fill(_dirty, true);

        // Срезы продаж приходят все пять или ни одного (продаж за период не было). Если здесь
        // ждут хоть один из них, а пришёл только склад или ничего, — это «продаж нет», и об этом
        // говорит отдельная вкладка: иначе вкладки продаж просто пропали бы без объяснения.
        var wantsSales = _sliceKeys.Any(k => k != AnalyticsReportData.KeyStock);
        var noSales = wantsSales && !slices.Any(s => s.Key != AnalyticsReportData.KeyStock);
        var message = noSales || slices.Count == 0;

        var layout = (message ? (_serverFailed ? "!!" : "!") : "") + string.Join("|", slices.Select(s => s.Key)) + (_showSeasonality ? "|~" : "");
        if (layout != _layout)
        {
            _layout = layout;
            _page.Content = null;
            _segItems.Children.Clear();
            _pages.Clear();
            _views.Clear();

            if (message && _serverFailed && wantsSales)
                AddPage(Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC tahlili"),
                    NurMarketKassa.AvaloniaHost.Views.ServerOfflineNotice.Build(this, RetryRequested == null ? null : () => RetryRequested?.Invoke()));
            else if (message)
            {
                AddPage(Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC tahlili"),
                    new TextBlock
                    {
                        Text = wantsSales
                            ? Tr.T("Продаж за выбранный период нет — считать ABC не на чем.", "Тандалган мезгилде сатуу жок — ABC эсептөө үчүн маалымат жок.", "No sales in the selected period — nothing to run ABC analysis on.", "Seçilen dönemde satış yok — ABC analizi yapılamıyor.", "Tanlangan davrda sotuv yo'q — ABC tahlili uchun ma'lumot yo'q.")
                            : Tr.T("На складе нет товаров с остатком и закупочной ценой — считать ABC не на чем.", "Кампада калдыгы жана сатып алуу баасы бар товар жок — ABC эсептөө үчүн маалымат жок.", "No products in stock with a purchase price — nothing to run ABC analysis on.", "Depoda stoğu ve alış fiyatı olan ürün yok — ABC analizi yapılamıyor.", "Omborda qoldig'i va xarid narxi bor mahsulot yo'q — ABC tahlili uchun ma'lumot yo'q."),
                        Margin = new Thickness(12),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gray,
                    });
            }

            foreach (var slice in slices)
            {
                var view = new SliceView(TableMaxHeight, ScrollsVertically);
                view.ProductAnalyticsRequested += name => ProductAnalyticsRequested?.Invoke(name);
                _views.Add(view);
                AddPage(slice.Title, view);
            }

            // Сезонность считается по всей истории, а не за выбранный период, поэтому она
            // осмысленна даже когда в периоде продаж нет.
            if (_showSeasonality)
                AddPage(Tr.T("Сезонность", "Мезгилдүүлүк", "Seasonality", "Mevsimsellik", "Mavsumiylik"), _seasonality);
            _segScroll.IsVisible = _pages.Count > 1;
            Select(0);
        }

        FillSelectedView();
    }

    /// <summary>Одна вкладка среза.</summary>
    private sealed class SliceView : UserControl
    {
        private readonly TextBlock _hint = new()
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = Brushes.Gray,
        };

        public event Action<string>? ProductAnalyticsRequested;

        private readonly StackPanel _legend = new() { Margin = new Thickness(0, 0, 0, 8) };
        private readonly StackPanel _pyramid = new();
        private readonly StackPanel _pareto = new();
        private readonly StackPanel _paretoColumns = new();

        // 2026-10-28, просьба владельца: диаграммы Парето показывали только первые 20 позиций — переключатель «Показать: 20 / 50 / 100 / все»
        // (по умолчанию 20). Группы A/B/C считаются по всем позициям и от переключателя не зависят; при многих столбцах диаграмма
        // прокручивается по горизонтали (ScrollViewer в BarChartRenderer). «Все» — не больше 1000 столбцов, иначе рисунок слишком тяжёлый.
        private const int AllLimit = 1000;
        private int _paretoLimit = 20;
        private AnalyticsReportData.AbcSlice? _shownSlice;
        private readonly StackPanel _limitRow = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8), IsVisible = false };

        private void BuildLimitRow(int total)
        {
            _limitRow.Children.Clear();
            _limitRow.IsVisible = total > 20;
            if (total <= 20)
                return;
            _limitRow.Children.Add(new TextBlock
            {
                Text = Tr.T("Показать:", "Көрсөтүү:", "Show:", "Göster:", "Ko'rsatish:"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            });
            var seg = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (limit, text) in new[] { (20, "20"), (50, "50"), (100, "100"), (AllLimit, Tr.T("Все", "Баары", "All", "Tümü", "Barchasi")) })
            {
                var b = NurMarketKassa.AvaloniaHost.Views.UiKit.Seg(text, _paretoLimit == limit);
                b.MinHeight = 44;
                b.MinWidth = 44;
                var chosen = limit;
                b.Click += (_, _) =>
                {
                    _paretoLimit = chosen;
                    if (_shownSlice is { } slice)
                        Show(slice);
                };
                seg.Children.Add(b);
            }
            _limitRow.Children.Add(NurMarketKassa.AvaloniaHost.Views.UiKit.Segment(seg));
        }

        private readonly TextBlock _paretoColumnsTitle = new()
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 2),
        };

        private readonly TextBlock _paretoColumnsHint = new()
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.Gray,
        };
        private readonly StackPanel _groups = new();

        private readonly TextBlock _pyramidTitle = new()
        {
            Text = "",
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6),
        };
        private readonly TextBlock _paretoTitle = new()
        {
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 2),
        };

        /// <summary>Пояснение к диаграмме. Владелец попросил подписать, что она показывает:
        /// без этого три ряда столбцов и порог читаются как украшение, а не как ответ на
        /// вопрос «какие товары держат магазин».</summary>
        private readonly TextBlock _paretoHint = new()
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = Brushes.Gray,
        };

        private readonly DataGrid _grid = new()
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            MinHeight = 240,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        };

        private readonly DataGridTextColumn _valueColumn;
        private readonly DataGridTextColumn _quantityColumn;

        public SliceView(double tableMaxHeight = double.PositiveInfinity, bool scrollsVertically = true)
        {
            _grid.MaxHeight = tableMaxHeight;
            // По строке таблицы — тот же разбор, что и по столбцу диаграммы: искать товар
            // глазами на диаграмме из двух десятков столбцов неудобно, а в таблице он есть весь.
            _grid.DoubleTapped += (_, _) =>
            {
                if (_grid.SelectedItem is RowVm row && !string.IsNullOrWhiteSpace(row.Name))
                    ProductAnalyticsRequested?.Invoke(row.Name);
            };

            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = Tr.T("Гр.", "Тп.", "Grp", "Gr.", "Gr."),
                Width = new DataGridLength(72), // 2026-10-18, доводка О4: заголовок «Гр.» не обрезается; 2026-10-31: 64→72 («Grp» в en)
                CellTemplate = new FuncDataTemplate<RowVm>((row, _) => row is null ? null : new Border
                {
                    Background = row.GroupBrush,
                    CornerRadius = new CornerRadius(4),
                    Width = 24,
                    Height = 20,
                    Margin = new Thickness(4, 2, 4, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = row.Group,
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.Bold,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                }),
            });

            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Tr.T("Название", "Аталышы", "Name", "Ad", "Nomi"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                Binding = new Avalonia.Data.Binding(nameof(RowVm.Name)),
            });

            _quantityColumn = new DataGridTextColumn
            {
                Header = Tr.T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
                Width = new DataGridLength(95),
                Binding = new Avalonia.Data.Binding(nameof(RowVm.QuantityText)),
            };
            _grid.Columns.Add(_quantityColumn);

            _valueColumn = new DataGridTextColumn
            {
                Header = Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa"),
                Width = new DataGridLength(145), // 2026-10-31: 130→145 («14 846,49 so'm» обрезалась)
                Binding = new Avalonia.Data.Binding(nameof(RowVm.SumText)),
            };
            _grid.Columns.Add(_valueColumn);

            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Tr.T("Доля", "Үлүшү", "Share", "Pay", "Ulush"),
                Width = new DataGridLength(85),
                Binding = new Avalonia.Data.Binding(nameof(RowVm.ShareText)),
            });

            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = Tr.T("Накопл.", "Топтолгон", "Cumul.", "Kümül.", "To'plangan"),
                Width = new DataGridLength(120), // 2026-10-31: 95→120 («Топтолгон»/«To'plangan» обрезались)
                Binding = new Avalonia.Data.Binding(nameof(RowVm.CumulativeText)),
            });

            var body = new StackPanel { Margin = new Thickness(10, 10, 10, 10) };
            body.Children.Add(_hint);
            body.Children.Add(_legend);
            // Пирамида идёт первой: она отвечает на главный вопрос анализа («малая часть
            // ассортимента даёт почти всю выручку — насколько сильно?») одним взглядом, а
            // Парето и таблица уже уточняют, какие именно позиции за этим стоят.
            body.Children.Add(Card(_pyramidTitle, _pyramid));
            body.Children.Add(Card(_paretoTitle, _paretoHint, new StackPanel { Children = { _limitRow, _pareto } }));
            body.Children.Add(Card(_paretoColumnsTitle, _paretoColumnsHint, _paretoColumns));
            body.Children.Add(Card(
                new TextBlock
                {
                    Text = Tr.T("Доля групп", "Топтордун үлүшү", "Group share", "Grup payı", "Guruhlar ulushi"),
                    FontWeight = FontWeight.SemiBold,
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 0, 6),
                },
                _groups));
            body.Children.Add(_grid);

            // 2026-10-21 (пакет 2, п. 86): без своей прокрутки — содержимое как есть, прокручивает страница.
            if (!scrollsVertically)
            {
                Content = body;
                return;
            }

            var scroller = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            // Без этого таблица внизу, получив фокус, «подтягивает» себя в видимую часть и
            // прокручивает раздел так, что заголовок и легенда уезжают за верхний край.
            ScrollViewer.SetBringIntoViewOnFocusChange(scroller, false);
            Content = scroller;
        }

        public void Show(AnalyticsReportData.AbcSlice slice)
        {
            _hint.Text = slice.Hint;

            var money = slice.IsMoney;
            var stock = slice.Key == AnalyticsReportData.KeyStock;
            _valueColumn.Header = stock
                ? Tr.T("Стоимость", "Наркы", "Value", "Değer", "Qiymati")
                : money
                    ? Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa")
                    : Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
            // У среза склада «количество» — это остаток на складе, а не проданные штуки.
            _quantityColumn.Header = stock
                ? Tr.T("Остаток", "Калдык", "Stock", "Stok", "Qoldiq")
                : Tr.T("Кол-во", "Саны", "Qty", "Adet", "Soni");
            // В срезе «по количеству» мера и есть количество — вторая такая же колонка мешала бы.
            _quantityColumn.IsVisible = money;

            _shownSlice = slice;
            BuildLimitRow(slice.Rows.Count);
            var shown = Math.Min(slice.Rows.Count, _paretoLimit);
            // 2026-10-18, доводка О4: без слова «Парето» — простыми словами, что показывает диаграмма.
            _paretoTitle.Text = slice.Rows.Count > shown
                ? Tr.T($"Вклад каждого — первые {shown} из {slice.Rows.Count}", $"Ар биринин салымы — алгачкы {shown} / {slice.Rows.Count}", $"Each item's contribution — top {shown} of {slice.Rows.Count}",
                    $"Her birinin katkısı — ilk {shown} / {slice.Rows.Count}", $"Har birining hissasi — birinchi {shown} / {slice.Rows.Count}")
                : Tr.T($"Вклад каждого — все {slice.Rows.Count}", $"Ар биринин салымы — баары {slice.Rows.Count}", $"Each item's contribution — all {slice.Rows.Count}",
                    $"Her birinin katkısı — tümü {slice.Rows.Count}", $"Har birining hissasi — barchasi {slice.Rows.Count}");

            var rightTitle = stock
                ? Tr.T("Стоимость остатка", "Калдыктын наркы", "Inventory value", "Stok değeri", "Qoldiq qiymati")
                : slice.IsMoney
                    ? Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum")
                    : Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
            // 2026-10-18, доводка О4: заголовок пирамиды простыми словами («Доля позиций против доли: выручка» читалось с трудом).
            var right = rightTitle.ToLowerInvariant();
            _pyramidTitle.Text = Tr.T($"Позиции и {right} по группам A · B · C", $"Позициялар жана {right} A · B · C топтору боюнча", $"Items and {right} by group A · B · C",
                $"Kalemler ve {right}: A · B · C grupları", $"Pozitsiyalar va {right}: A · B · C guruhlari");
            BarChartRenderer.RenderAbcPyramid(_pyramid, slice.Summary,
                Tr.T("Позиции", "Позициялар", "Items", "Kalemler", "Pozitsiyalar"), rightTitle);

            // 2026-10-18, доводка О4: «поз.» → словом по срезу (товаров / категорий / брендов).
            var itemsWord = slice.Key == AnalyticsReportData.KeyCategory
                ? Tr.T("категорий", "категория", "categories", "kategori", "kategoriya")
                : slice.Key == AnalyticsReportData.KeyBrand
                    ? Tr.T("брендов", "бренд", "brands", "marka", "brend")
                    : Tr.T("товаров", "товар", "products", "ürün", "mahsulot");
            BarChartRenderer.RenderAbcLegend(_legend, slice.Summary, slice.Unit, itemsWord);
            _paretoHint.Text = Tr.T(
                "Товары слева направо — от самого весомого к самому мелкому. Высота столбца — доля товара, цвет — его группа. Ломаная сверху — та же доля нарастающим итогом: где она пересекает пунктир 80 %, заканчивается группа A, где 95 % — группа B. Нажмите на столбец, чтобы посмотреть разбор товара.",
                "Товарлар солдон оңго — эң маанилүүсүнөн эң майдасына чейин. Мамынын бийиктиги — товардын үлүшү, түсү — анын тобу. Үстүндөгү сынык сызык — ошол эле үлүштүн топтолгон жыйынтыгы: ал 80 % пунктирин кесип өткөн жерде A тобу, 95 % пунктирин кесип өткөн жерде B тобу бүтөт. Товардын талдоосун көрүү үчүн мамыны басыңыз.",
                "Products run left to right, from the biggest contributor to the smallest. Bar height is the product's share, color is its group. The line on top is the same share as a running total: where it crosses the 80% dashed line, group A ends; where it crosses 95%, group B ends. Click a bar to see the product breakdown.",
                "Ürünler soldan sağa, en büyük paydan en küçüğe sıralanır. Çubuk yüksekliği ürünün payını, rengi grubunu gösterir. Üstteki kırık çizgi aynı payın kümülatif toplamıdır: %80 kesik çizgisini geçtiği yerde A grubu, %95'i geçtiği yerde B grubu biter. Ürün ayrıntısını görmek için bir çubuğa tıklayın.",
                "Mahsulotlar chapdan o'ngga — eng salmoqlisidan eng kichigigacha. Ustun balandligi — mahsulot ulushi, rangi — uning guruhi. Yuqoridagi siniq chiziq — o'sha ulushning o'sib boruvchi jami: u 80 % li punktirni kesib o'tgan joyda A guruhi, 95 % li punktirni kesgan joyda B guruhi tugaydi. Mahsulot tahlilini ko'rish uchun ustunni bosing.");

            BarChartRenderer.RenderPareto(_pareto, slice.Rows
                .Take(shown)
                .Select(r => (
                    Label: r.Name,
                    Share: r.Share,
                    Cumulative: r.Cumulative,
                    Group: r.Group,
                    ValueText: Format(r.Sum, slice.Unit)))
                .ToList(),
                name => ProductAnalyticsRequested?.Invoke(name));
            _paretoColumnsTitle.Text = Tr.T(
                "Накопленная доля столбцами", "Топтолгон үлүш мамылар менен", "Cumulative share as bars", "Sütunlarla kümülatif pay", "To'plangan ulush ustunlarda")
                + $": {shown} / {slice.Rows.Count}";
            _paretoColumnsHint.Text = Tr.T(
                "Та же картина, но накопленная доля и порог 80 % — столбцами: так их высоты сравниваются напрямую. Где оранжевый столбец перерос серый, заканчивается группа A.",
                "Ошол эле сүрөт, бирок топтолгон үлүш жана 80 % босогосу — мамылар менен: ошондо алардын бийиктиктерин түз салыштырса болот. Кызгылт сары мамы боз мамыдан ашып кеткен жерде A тобу бүтөт.",
                "The same picture, but the cumulative share and the 80% threshold are shown as bars, so their heights compare directly. Where the orange bar grows taller than the gray one, group A ends.",
                "Aynı görünüm, ancak kümülatif pay ve %80 eşiği sütunlarla gösterilir: böylece yükseklikler doğrudan karşılaştırılır. Turuncu sütunun griyi geçtiği yerde A grubu biter.",
                "Xuddi shu manzara, lekin to'plangan ulush va 80 % chegarasi ustunlar ko'rinishida: shunda balandliklar to'g'ridan-to'g'ri taqqoslanadi. To'q sariq ustun kulrangdan oshib ketgan joyda A guruhi tugaydi.");

            BarChartRenderer.RenderParetoColumns(_paretoColumns, slice.Rows
                .Take(shown)
                .Select(r => (
                    Label: r.Name,
                    Value: r.Sum,
                    Cumulative: r.Cumulative,
                    ValueText: Format(r.Sum, slice.Unit)))
                .ToList(),
                rightTitle,
                name => ProductAnalyticsRequested?.Invoke(name));

            BarChartRenderer.RenderAbcGroups(_groups, slice.Summary, itemsWord);

            _grid.ItemsSource = slice.Rows.Select(r => new RowVm
            {
                Group = r.Group,
                GroupBrush = GroupBrushes.TryGetValue(r.Group, out var gb) ? gb : (GroupBrushes[r.Group] = Brush.Parse(BarChartRenderer.AbcColor(r.Group))), // 2026-10-30: кисть на группу, а не на строку (10 000 строк)
                Name = r.Name,
                QuantityText = r.Quantity.ToString("0.###", CultureInfo.CurrentCulture),
                SumText = Format(r.Sum, slice.Unit),
                // 2026-10-18, доводка О4: проценты с запятой по культуре, как суммы («87,3 %», а не «87.3 %»).
                ShareText = r.Share.ToString("0.##", CultureInfo.CurrentCulture) + " %",
                CumulativeText = r.Cumulative.ToString("0.##", CultureInfo.CurrentCulture) + " %",
            }).ToList();
        }

        private static readonly Dictionary<string, IBrush> GroupBrushes = new();

        private static string Format(double value, string unit) =>
            unit == "шт." || unit == "даана" || unit == "pcs" || unit == "adet" || unit == "dona"
                ? value.ToString("0.###", CultureInfo.CurrentCulture) + " " + unit
                : value.ToString("N2", CultureInfo.CurrentCulture) + " " + unit;

        private static Border Card(Control title, Control body) => Card(title, null, body);

        private static Border Card(Control title, Control? hint, Control body)
        {
            var panel = new StackPanel();
            panel.Children.Add(title);
            if (hint is not null)
                panel.Children.Add(hint);
            panel.Children.Add(body);

            return new Border
            {
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 12),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.Parse("#94A3B8"), 0.35),
                Child = panel,
            };
        }
    }

    /// <summary>Строка таблицы. Проценты и суммы храним уже оформленными строками: в DataGrid
    /// формат числа зависит от локали системы, а здесь нужен один и тот же вид у всех.</summary>
    private sealed class RowVm
    {
        public string Group { get; init; } = "";
        public IBrush GroupBrush { get; init; } = Brushes.Gray;
        public string Name { get; init; } = "";
        public string QuantityText { get; init; } = "";
        public string SumText { get; init; } = "";
        public string ShareText { get; init; } = "";
        public string CumulativeText { get; init; } = "";
    }
}
