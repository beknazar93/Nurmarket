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
/// выбранную вкладку на первую при каждом обновлении периода.</summary>
public sealed class AbcSectionView : UserControl
{
    private readonly TabControl _tabs = new() { Margin = new Thickness(0) };
    private readonly List<SliceView> _views = [];
    private readonly SeasonalityView _seasonality = new();

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
        _seasonality.Show(data.Seasonality);
        UpdateSlices(data.AbcSlices);
    }

    private void UpdateSlices(IReadOnlyList<AnalyticsReportData.AbcSlice> slices)
    {
        if (slices.Count == 0)
        {
            _tabs.ItemsSource = null;
            _tabs.Items.Clear();
            _views.Clear();
            _tabs.Items.Add(new TabItem
            {
                Header = Tr.T("ABC-анализ", "ABC-анализ", "ABC analysis", "ABC analizi", "ABC tahlili"),
                Content = new TextBlock
                {
                    Text = Tr.T("Продаж за выбранный период нет — считать ABC не на чем.", "Тандалган мезгилде сатуу жок — ABC эсептөөгө эч нерсе жок.", "No sales in the selected period - nothing to calculate ABC from.", "Seçilen dönemde satış yok - ABC hesaplanacak bir şey yok.", "Tanlangan davrda sotuv yo'q - ABC hisoblash uchun hech narsa yo'q."),
                    Margin = new Thickness(12),
                    Foreground = Brushes.Gray,
                },
            });
            // Сезонность считается по всей истории, а не за выбранный период, поэтому она
            // осмысленна даже когда в периоде продаж нет.
            _tabs.Items.Add(new TabItem { Header = Tr.T("Сезонность", "Мезгилдүүлүк", "Seasonality", "Mevsimsellik", "Mavsumiylik"), Content = _seasonality });
            return;
        }

        if (_views.Count != slices.Count || _tabs.Items.Count != slices.Count + 1)
        {
            _tabs.Items.Clear();
            _views.Clear();
            foreach (var slice in slices)
            {
                var view = new SliceView();
            view.ProductAnalyticsRequested += name => ProductAnalyticsRequested?.Invoke(name);
                _views.Add(view);
                _tabs.Items.Add(new TabItem { Header = slice.Title, Content = view });
            }
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

        public SliceView()
        {
            // По строке таблицы — тот же разбор, что и по столбцу диаграммы: искать товар
            // глазами на диаграмме из двух десятков столбцов неудобно, а в таблице он есть весь.
            _grid.DoubleTapped += (_, _) =>
            {
                if (_grid.SelectedItem is RowVm row && !string.IsNullOrWhiteSpace(row.Name))
                    ProductAnalyticsRequested?.Invoke(row.Name);
            };

            _grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = Tr.T("Гр.", "Тп.", "Gr.", "Gr.", "Gr."),
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
                Header = Tr.T("Накопл.", "Топтолмо", "Cumul.", "Kümül.", "Jami"),
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
            _valueColumn.Header = money
                ? Tr.T("Сумма", "Суммасы", "Amount", "Tutar", "Summa")
                : Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
            // В срезе «по количеству» мера и есть количество — вторая такая же колонка мешала бы.
            _quantityColumn.IsVisible = money;

            var shown = Math.Min(slice.Rows.Count, 20);
            _paretoTitle.Text = slice.Rows.Count > shown
                ? Tr.T("Диаграмма Парето — крупнейшие", "Парето диаграммасы — эң ириси", "Pareto chart - largest", "Pareto grafiği - en büyükleri", "Pareto diagrammasi - eng yiriklari") + $": {shown} / {slice.Rows.Count}"
                : Tr.T("Диаграмма Парето — все", "Парето диаграммасы — баары", "Pareto chart - all", "Pareto grafiği - tümü", "Pareto diagrammasi - barchasi") + $": {slice.Rows.Count}";

            var rightTitle = slice.IsMoney
                ? Tr.T("Выручка", "Түшкөн акча", "Revenue", "Ciro", "Tushum")
                : Tr.T("Количество", "Саны", "Quantity", "Miktar", "Miqdor");
            _pyramidTitle.Text = Tr.T("Доля позиций против доли", "Позициялардын үлүшү үлүшкө каршы", "Positions versus share", "Kalemler paya karşı", "Pozitsiyalar ulushga qarshi") + ": " + rightTitle.ToLowerInvariant();
            BarChartRenderer.RenderAbcPyramid(_pyramid, slice.Summary,
                Tr.T("Позиции", "Позициялар", "Positions", "Kalemler", "Pozitsiyalar"), rightTitle);

            BarChartRenderer.RenderAbcLegend(_legend, slice.Summary, slice.Unit);
            _paretoHint.Text = Tr.T(
                "Товары слева направо — от самого весомого к самому мелкому. Высота столбца — доля товара, цвет — его группа. Ломаная сверху — та же доля нарастающим итогом: где она пересекает пунктир 80 %, заканчивается группа A, где 95 % — группа B. Нажмите на столбец, чтобы посмотреть разбор товара.",
                "Товарлар солдон оңго — эң салмактуудан эң майдага. Мамынын бийиктиги — товардын үлүшү, түсү — анын тобу. Үстүндөгү сызык — ошол эле үлүш топтолмо түрүндө: ал 80 % пунктирин кесип өткөн жерде A тобу, 95 % кесип өткөн жерде B тобу аяктайт. Товардын чечмелөөсүн көрүү үчүн мамыны басыңыз.",
                "Products run left to right, from the heaviest to the smallest. Bar height is the product's share, colour is its group. The line on top is the same share accumulated: where it crosses the 80 % dashes group A ends, where it crosses 95 % group B ends. Click a bar to see the product breakdown.",
                "Ürünler soldan sağa, en ağırdan en küçüğe. Çubuk yüksekliği ürünün payı, rengi grubudur. Üstteki çizgi aynı payın birikmiş hâli: %80 kesik çizgisini geçtiği yerde A grubu, %95'i geçtiği yerde B grubu biter. Ürün ayrıntısı için bir çubuğa tıklayın.",
                "Mahsulotlar chapdan o'ngga — eng salmoqlidan eng maydaga. Ustun balandligi — mahsulot ulushi, rangi — uning guruhi. Yuqoridagi chiziq — o'sha ulush to'plangan holda: u 80 % punktirini kesib o'tgan joyda A guruhi, 95 % ni kesib o'tgan joyda B guruhi tugaydi. Mahsulot tahlilini ko'rish uchun ustunni bosing.");

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
                "Парето столбцами", "Парето мамылар менен", "Pareto as columns", "Sütunlarla Pareto", "Ustunlar bilan Pareto")
                + $": {shown} / {slice.Rows.Count}";
            _paretoColumnsHint.Text = Tr.T(
                "Та же картина, но накопленная доля и порог 80 % — столбцами: так их высоты сравниваются напрямую. Где оранжевый столбец перерос серый, заканчивается группа A.",
                "Ошол эле сүрөт, бирок топтолгон үлүш жана 80 % босогосу — мамылар менен: ошондо алардын бийиктиги түз салыштырылат. Кызгылт сары мамы бозду басып озгон жерде A тобу аяктайт.",
                "The same picture, but the cumulative share and the 80 % threshold are columns, so their heights compare directly. Where the orange column overtakes the grey one, group A ends.",
                "Aynı tablo, ama kümülatif pay ve %80 eşiği sütun hâlinde: yükseklikleri doğrudan karşılaştırılır. Turuncu sütun griyi geçtiği yerde A grubu biter.",
                "O'sha manzara, lekin to'plangan ulush va 80 % chegarasi — ustunlar: balandliklari to'g'ridan-to'g'ri taqqoslanadi. To'q sariq ustun kulrangdan o'zib ketgan joyda A guruhi tugaydi.");

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
