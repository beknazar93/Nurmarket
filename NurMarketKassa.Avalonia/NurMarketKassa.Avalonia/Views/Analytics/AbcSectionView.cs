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

        private readonly StackPanel _legend = new() { Margin = new Thickness(0, 0, 0, 8) };
        private readonly StackPanel _pyramid = new();
        private readonly StackPanel _pareto = new();
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
            Margin = new Thickness(0, 0, 0, 6),
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
            body.Children.Add(Card(_paretoTitle, _pareto));
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
            BarChartRenderer.RenderPareto(_pareto, slice.Rows
                .Take(shown)
                .Select(r => (
                    Label: r.Name,
                    Share: r.Share,
                    Cumulative: r.Cumulative,
                    Group: r.Group,
                    ValueText: Format(r.Sum, slice.Unit)))
                .ToList());
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

        private static Border Card(Control title, Control body)
        {
            var panel = new StackPanel();
            panel.Children.Add(title);
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
