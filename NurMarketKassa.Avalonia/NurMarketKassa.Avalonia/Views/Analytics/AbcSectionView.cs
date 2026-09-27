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

    private readonly TabControl _tabs = new() { Margin = new Thickness(0) };
    private readonly List<SliceView> _views = [];
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

    /// <summary>Попросили разбор конкретного товара — нажали на столбец диаграммы Парето или
    /// на строку таблицы. Само окно раздел не открывает: он живёт и во вкладке «Продаж», и в
    /// отдельном окне ABC, а владелец окна у них разный.</summary>
    public event Action<string>? ProductAnalyticsRequested;

    public AbcSectionView()
    {
        Content = _tabs;
    }

    /// <summary>Заполняет раздел. Срезы приходят в том же порядке, поэтому вкладка,
    /// выбранная пользователем, остаётся выбранной.</summary>
    public void Update(AnalyticsReportData data)
    {
        if (_showSeasonality)
            _seasonality.Show(data.Seasonality);
        UpdateSlices(data.AbcSlices);
    }

    private void UpdateSlices(IReadOnlyList<AnalyticsReportData.AbcSlice> allSlices)
    {
        var slices = allSlices.Where(s => _sliceKeys.Contains(s.Key)).ToList();

        // Срезы продаж приходят все пять или ни одного (продаж за период не было). Если здесь
        // ждут хоть один из них, а пришёл только склад или ничего, — это «продаж нет», и об этом
        // говорит отдельная вкладка: иначе вкладки продаж просто пропали бы без объяснения.
        var wantsSales = _sliceKeys.Any(k => k != AnalyticsReportData.KeyStock);
        var noSales = wantsSales && !slices.Any(s => s.Key != AnalyticsReportData.KeyStock);
        var message = noSales || slices.Count == 0;

        var layout = (message ? "!" : "") + string.Join("|", slices.Select(s => s.Key)) + (_showSeasonality ? "|~" : "");
        if (layout != _layout)
        {
            _layout = layout;
            _tabs.ItemsSource = null;
            _tabs.Items.Clear();
            _views.Clear();

            if (message)
            {
                _tabs.Items.Add(new TabItem
                {
                    Header = Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC tahlili"),
                    Content = new TextBlock
                    {
                        Text = wantsSales
                            ? Tr.T("Продаж за выбранный период нет — считать ABC не на чем.", "Тандалган мезгилде сатуу жок — ABC эсептөө үчүн маалымат жок.", "No sales in the selected period — nothing to run ABC analysis on.", "Seçilen dönemde satış yok — ABC analizi yapılamıyor.", "Tanlangan davrda sotuv yo'q — ABC tahlili uchun ma'lumot yo'q.")
                            : Tr.T("На складе нет товаров с остатком и закупочной ценой — считать ABC не на чем.", "Кампада калдыгы жана сатып алуу баасы бар товар жок — ABC эсептөө үчүн маалымат жок.", "No products in stock with a purchase price — nothing to run ABC analysis on.", "Depoda stoğu ve alış fiyatı olan ürün yok — ABC analizi yapılamıyor.", "Omborda qoldig'i va xarid narxi bor mahsulot yo'q — ABC tahlili uchun ma'lumot yo'q."),
                        Margin = new Thickness(12),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Brushes.Gray,
                    },
                });
            }

            foreach (var slice in slices)
            {
                var view = new SliceView(TableMaxHeight);
                view.ProductAnalyticsRequested += name => ProductAnalyticsRequested?.Invoke(name);
                _views.Add(view);
                _tabs.Items.Add(new TabItem { Header = slice.Title, Content = view });
            }

            // Сезонность считается по всей истории, а не за выбранный период, поэтому она
            // осмысленна даже когда в периоде продаж нет.
            if (_showSeasonality)
                _tabs.Items.Add(new TabItem { Header = Tr.T("Сезонность", "Мезгилдүүлүк", "Seasonality", "Mevsimsellik", "Mavsumiylik"), Content = _seasonality });
            _tabs.SelectedIndex = 0;
        }

        for (var i = 0; i < slices.Count; i++)
            _views[i].Show(slices[i]);
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

        public SliceView(double tableMaxHeight = double.PositiveInfinity)
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
                Width = new DataGridLength(54),
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
                Width = new DataGridLength(130),
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
                Width = new DataGridLength(95),
                Binding = new Avalonia.Data.Binding(nameof(RowVm.CumulativeText)),
            });

            var body = new StackPanel { Margin = new Thickness(10, 10, 10, 10) };
            body.Children.Add(_hint);
            body.Children.Add(_legend);
            // Пирамида идёт первой: она отвечает на главный вопрос анализа («малая часть
            // ассортимента даёт почти всю выручку — насколько сильно?») одним взглядом, а
            // Парето и таблица уже уточняют, какие именно позиции за этим стоят.
            body.Children.Add(Card(_pyramidTitle, _pyramid));
            body.Children.Add(Card(_paretoTitle, _paretoHint, _pareto));
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

            var shown = Math.Min(slice.Rows.Count, 20);
            _paretoTitle.Text = slice.Rows.Count > shown
                ? Tr.T("Диаграмма Парето — крупнейшие", "Парето диаграммасы — эң ирилери", "Pareto chart — top items", "Pareto grafiği — en büyükler", "Pareto diagrammasi — eng yiriklari") + $": {shown} / {slice.Rows.Count}"
                : Tr.T("Диаграмма Парето — все", "Парето диаграммасы — баары", "Pareto chart — all items", "Pareto grafiği — tümü", "Pareto diagrammasi — barchasi") + $": {slice.Rows.Count}";

            var rightTitle = stock
                ? Tr.T("Стоимость остатка", "Калдыктын наркы", "Inventory value", "Stok değeri", "Qoldiq qiymati")
                : slice.IsMoney
                    ? Tr.T("Выручка", "Түшүм", "Revenue", "Ciro", "Tushum")
                    : Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
            _pyramidTitle.Text = Tr.T("Доля позиций против доли", "Позициялардын жана көрсөткүчтүн үлүшү", "Item share vs. total share", "Kalem payına karşı pay", "Pozitsiyalar ulushi va natija ulushi") + ": " + rightTitle.ToLowerInvariant();
            BarChartRenderer.RenderAbcPyramid(_pyramid, slice.Summary,
                Tr.T("Позиции", "Позициялар", "Items", "Kalemler", "Pozitsiyalar"), rightTitle);

            BarChartRenderer.RenderAbcLegend(_legend, slice.Summary, slice.Unit);
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
                "Парето столбцами", "Парето мамылар менен", "Pareto as bars", "Sütunlarla Pareto", "Ustunli Pareto")
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

            BarChartRenderer.RenderAbcGroups(_groups, slice.Summary);

            _grid.ItemsSource = slice.Rows.Select(r => new RowVm
            {
                Group = r.Group,
                GroupBrush = Brush.Parse(BarChartRenderer.AbcColor(r.Group)),
                Name = r.Name,
                QuantityText = r.Quantity.ToString("0.###", CultureInfo.InvariantCulture),
                SumText = Format(r.Sum, slice.Unit),
                ShareText = r.Share.ToString("0.##", CultureInfo.InvariantCulture) + " %",
                CumulativeText = r.Cumulative.ToString("0.##", CultureInfo.InvariantCulture) + " %",
            }).ToList();
        }

        private static string Format(double value, string unit) =>
            unit == "шт." || unit == "даана" || unit == "pcs" || unit == "adet" || unit == "dona"
                ? value.ToString("0.###", CultureInfo.InvariantCulture) + " " + unit
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
