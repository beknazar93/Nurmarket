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
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Analytics;

/// <summary>Вкладка «Сезонность»: какие товары продаются в одни месяцы и не продаются в другие.
///
/// Построена вокруг одной мысли: короткая история продаж делает сезонным ВЕСЬ ассортимент, и
/// такой вывод вреднее, чем его отсутствие — по нему закупят не то. Поэтому месяцы, за которые
/// истории нет, показаны отдельным цветом и подписаны «нет истории», а пока история не покрывает
/// хотя бы два сезона, вверху висит предупреждение и ни один товар не помечается сезонным.</summary>
public sealed class SeasonalityView : UserControl
{
    private static readonly string[] MonthShort =
        ["янв", "фев", "мар", "апр", "май", "июн", "июл", "авг", "сен", "окт", "ноя", "дек"];

    private const string ColorSeasonal = "#16A34A";
    private const string ColorYearRound = "#3B82F6";
    private const string ColorUnknown = "#64748B";

    private readonly TextBlock _note = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 10),
    };

    private readonly Border _warning;
    private readonly TextBlock _warningText = new()
    {
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold,
    };

    private readonly StackPanel _shopChart = new();
    private readonly StackPanel _seasonGroups = new() { Spacing = 10 };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        MinHeight = 260,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
    };

    public SeasonalityView()
    {
        _warning = new Border
        {
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#F59E0B"), 0.18),
            BorderBrush = Brush.Parse("#F59E0B"),
            BorderThickness = new Thickness(1),
            IsVisible = false,
            Child = _warningText,
        };

        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Тип",
            Width = new DataGridLength(120),
            CellTemplate = new FuncDataTemplate<RowVm>((row, _) => row is null ? null : new Border
            {
                Background = row.KindBrush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(4, 3, 4, 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = row.Kind,
                    Foreground = Brushes.White,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                },
            }),
        });

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Товар",
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            Binding = new Avalonia.Data.Binding(nameof(RowVm.Name)),
        });

        // Полоска из двенадцати клеток: насыщенность — доля месяца в выручке товара,
        // серая штриховка — месяц, за который истории нет.
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "Янв … дек",
            Width = new DataGridLength(230),
            CellTemplate = new FuncDataTemplate<RowVm>((row, _) => row is null ? null : BuildStrip(row)),
        });

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Пик",
            Width = new DataGridLength(170),
            Binding = new Avalonia.Data.Binding(nameof(RowVm.Peak)),
        });

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Не продаётся",
            Width = new DataGridLength(170),
            Binding = new Avalonia.Data.Binding(nameof(RowVm.Quiet)),
        });

        _grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Выручка",
            Width = new DataGridLength(120),
            Binding = new Avalonia.Data.Binding(nameof(RowVm.SumText)),
        });

        var body = new StackPanel { Margin = new Thickness(10) };
        body.Children.Add(_warning);
        body.Children.Add(_note);
        body.Children.Add(Card("Выручка магазина по месяцам (вся история)", _shopChart));
        body.Children.Add(Card("Сезонные товары по сезонам", _seasonGroups));
        body.Children.Add(_grid);

        var scroller = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        ScrollViewer.SetBringIntoViewOnFocusChange(scroller, false);
        Content = scroller;
    }

    public void Show(AnalyticsReportData.SeasonalityReport report)
    {
        _note.Text = report.Note;
        _note.Foreground = Brushes.Gray;

        _warning.IsVisible = !report.Reliable && report.Rows.Count > 0;
        _warningText.Text = "Выводов о сезонности пока нет: история продаж покрывает "
            + report.CoveredSeasons.ToString(CultureInfo.InvariantCulture)
            + " сезон(а) из четырёх. Копите историю — раздел заполнится сам.";
        _warningText.Foreground = Brush.Parse("#F59E0B");

        RenderShopMonths(report);
        RenderSeasonGroups(report);

        _grid.ItemsSource = report.Rows.Select(r => new RowVm
        {
            Kind = r.Kind,
            KindBrush = Brush.Parse(r.Kind switch
            {
                "Сезонный" => ColorSeasonal,
                "Круглогодичный" => ColorYearRound,
                _ => ColorUnknown,
            }),
            Name = r.Name,
            MonthShare = r.MonthShare,
            MonthCovered = report.MonthCovered,
            Peak = r.PeakMonths,
            Quiet = r.QuietMonths,
            SumText = r.TotalSum.ToString("N2", CultureInfo.CurrentCulture) + " сом",
        }).ToList();
    }

    /// <summary>Столбики выручки магазина по месяцам. Месяцы без истории показаны пустой
    /// рамкой, а не нулевым столбиком: ноль и «не знаем» — разные вещи.</summary>
    private void RenderShopMonths(AnalyticsReportData.SeasonalityReport report)
    {
        _shopChart.Children.Clear();

        var max = report.ShopMonthRevenue.Count > 0 ? report.ShopMonthRevenue.Max() : 0;
        if (max <= 0)
        {
            _shopChart.Children.Add(new TextBlock
            {
                Text = "Продаж в истории нет.",
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var m = 0; m < 12; m++)
        {
            var covered = report.MonthCovered[m];
            var value = report.ShopMonthRevenue[m];
            var column = new StackPanel { Width = 62, VerticalAlignment = VerticalAlignment.Bottom };

            column.Children.Add(new TextBlock
            {
                Text = covered ? value.ToString("N0", CultureInfo.CurrentCulture) : "нет",
                FontSize = 10,
                Foreground = Brushes.Gray,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3),
            });

            column.Children.Add(new Border
            {
                Width = 34,
                Height = covered ? Math.Max(4, value / max * 130) : 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = covered
                    ? Brush.Parse(ColorSeasonal)
                    : new SolidColorBrush(Color.Parse(ColorUnknown), 0.25),
                BorderBrush = covered ? null : Brush.Parse(ColorUnknown),
                BorderThickness = new Thickness(covered ? 0 : 1),
            });

            column.Children.Add(new TextBlock
            {
                Text = MonthShort[m],
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = covered ? Brushes.Gray : Brush.Parse(ColorUnknown),
            });

            row.Children.Add(column);
        }

        _shopChart.Children.Add(new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        _shopChart.Children.Add(new TextBlock
        {
            Text = "Пустая рамка — за этот месяц истории нет, а не ноль продаж.",
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 8, 0, 0),
        });
    }

    /// <summary>Списки «что продаётся летом», «что зимой» и так далее — ровно тот вопрос,
    /// ради которого владелец сюда заходит.</summary>
    private void RenderSeasonGroups(AnalyticsReportData.SeasonalityReport report)
    {
        _seasonGroups.Children.Clear();

        var seasonal = report.Rows.Where(r => r.Kind == "Сезонный").ToList();
        if (seasonal.Count == 0)
        {
            _seasonGroups.Children.Add(new TextBlock
            {
                Text = report.Reliable
                    ? "Сезонных товаров не нашлось — продажи распределены по месяцам ровно."
                    : "Пока история короткая, сезонные товары не выделяются.",
                FontSize = 12,
                Foreground = Brushes.Gray,
            });
            return;
        }

        foreach (var season in new[] { "лето", "осень", "зима", "весна" })
        {
            var items = seasonal.Where(r => r.PeakSeason == season).Take(12).ToList();
            if (items.Count == 0)
                continue;

            var block = new StackPanel();
            block.Children.Add(new TextBlock
            {
                Text = char.ToUpperInvariant(season[0]) + season[1..]
                    + " — " + items.Count.ToString(CultureInfo.InvariantCulture) + " товар(ов)",
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 4),
            });

            foreach (var item in items)
            {
                block.Children.Add(new TextBlock
                {
                    Text = "• " + item.Name + " — пик: " + item.PeakMonths
                        + "; не продаётся: " + item.QuietMonths,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(8, 0, 0, 2),
                });
            }

            _seasonGroups.Children.Add(block);
        }
    }

    private static Control BuildStrip(RowVm row)
    {
        var strip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var max = row.MonthShare.Count > 0 ? row.MonthShare.Max() : 0;
        for (var m = 0; m < 12; m++)
        {
            var covered = row.MonthCovered[m];
            var share = row.MonthShare[m];
            var intensity = max > 0 ? share / max : 0;

            var cell = new Border
            {
                Width = 15,
                Height = 20,
                CornerRadius = new CornerRadius(2),
                Background = covered
                    ? new SolidColorBrush(Color.Parse(ColorSeasonal), Math.Max(0.08, intensity))
                    : new SolidColorBrush(Color.Parse(ColorUnknown), 0.15),
                BorderBrush = covered ? null : Brush.Parse(ColorUnknown),
                BorderThickness = new Thickness(covered ? 0 : 1),
            };
            ToolTip.SetTip(cell, covered
                ? MonthShort[m] + ": " + share.ToString("0.#", CultureInfo.InvariantCulture) + " %"
                : MonthShort[m] + ": истории нет");
            strip.Children.Add(cell);
        }

        return strip;
    }

    private static Border Card(string title, Control body)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 6),
        });
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

    private sealed class RowVm
    {
        public string Kind { get; init; } = "";
        public IBrush KindBrush { get; init; } = Brushes.Gray;
        public string Name { get; init; } = "";
        public IReadOnlyList<double> MonthShare { get; init; } = new double[12];
        public IReadOnlyList<bool> MonthCovered { get; init; } = new bool[12];
        public string Peak { get; init; } = "";
        public string Quiet { get; init; } = "";
        public string SumText { get; init; } = "";
    }
}
