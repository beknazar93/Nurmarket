#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-11-10, владелец: «функцию возврата добавь в продажи» (в первую очередь — в программе владельца): у каждой продажи кнопка «Возврат»
    /// (то же окно возврата, что в кассе, сразу с этим чеком — общий ReturnSaleLauncher), пометка «↩ возвращён» / «↩ частично»; у чека, возвращённого целиком,
    /// кнопка неактивна с подсказкой; после возврата список и итоги перечитываются. Фильтры (оплата, статус, кассир, клиент, сумма) — общая панель
    /// SaleFilterBar / SaleListFilter (та же, что в окне возврата); поиск по товару в строке поиска понимает и штрихкод/артикул (SaleProductQuery).</summary>
    public partial class SalesWindow
    {
        private SaleFilterBar _filterBar;
        private Button _filtersToggle;
        private SaleProductQuery _searchProductQuery;
        private string _searchProductText;

        /// <summary>Колонка «Возврат» таблицы чеков — только при праве «Возврат».</summary>
        private void AddReturnColumn()
        {
            if (!ReturnSaleLauncher.CanReturn())
                return;
            var column = new DataGridTemplateColumn
            {
                Header = ReturnSaleLauncher.ButtonText,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                MinWidth = 120,
                CellTemplate = new FuncDataTemplate<SaleItem>((_, _) => BuildReturnCell(), supportsRecycling: true),
            };
            SalesGrid.Columns.Add(column);
        }

        private Control BuildReturnCell()
        {
            var mark = new TextBlock { FontSize = 12, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            mark.Bind(TextBlock.TextProperty, new Binding(nameof(SaleItem.ReturnMark)));
            mark.Bind(IsVisibleProperty, new Binding(nameof(SaleItem.HasReturnMark)));
            var button = new Button
            {
                Content = ReturnSaleLauncher.ButtonText,
                Height = 34,
                MinHeight = 34,
                Padding = new Thickness(10, 0),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            button.Classes.Add("sec-outline");
            button.Bind(IsEnabledProperty, new Binding(nameof(SaleItem.CanReturnRow)));
            button.Bind(ToolTip.TipProperty, new Binding(nameof(SaleItem.ReturnTip)));
            button.Bind(Avalonia.Automation.AutomationProperties.NameProperty, new Binding(nameof(SaleItem.ReturnButtonName)));
            button.Click += SalesReturn_Click;
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(mark);
            panel.Children.Add(button);
            return panel;
        }

        private async void SalesReturn_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if ((sender as Control)?.DataContext is not SaleItem item || string.IsNullOrWhiteSpace(item.Id) || item.Id.StartsWith("local-", StringComparison.Ordinal))
                return;
            if (TopLevel.GetTopLevel(SalesGrid) is not Window owner)
                owner = this;
            try
            {
                var product = string.IsNullOrWhiteSpace(_searchFilter) || NurCrmReportsApi.ParseSaleNumber(_searchFilter) is not null ? null : _searchFilter.Trim();
                var opened = await ReturnSaleLauncher.OpenAsync(owner, item.Id, item.ReceiptNumber, productQuery: product,
                    status: null).ConfigureAwait(true);
                if (opened)
                    await LoadDataAsync(_historyFrom, _historyTo).ConfigureAwait(true); // список, итоги и пометки — после возврата с сервера
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Продажи: возврат не открылся: {ex}", "WARNING");
            }
        }

        /// <summary>Строка поиска + «Фильтры (N)» и панель фильтров под ней (в обоих видах «Продаж» — отдельном и во «Продажах и финансах»).</summary>
        private Control SearchWithFilters(Control search)
        {
            _filterBar ??= CreateFilterBar();
            _filtersToggle ??= CreateFiltersToggle();
            (_filtersToggle.Parent as Panel)?.Children.Remove(_filtersToggle);
            (_filterBar.Parent as Panel)?.Children.Remove(_filterBar);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(search);
            row.Children.Add(_filtersToggle);
            _filtersToggle.VerticalAlignment = VerticalAlignment.Center;
            var stack = new StackPanel { Margin = search.Margin, HorizontalAlignment = HorizontalAlignment.Stretch };
            search.Margin = new Thickness(0);
            stack.Children.Add(row);
            stack.Children.Add(_filterBar);
            return stack;
        }

        private SaleFilterBar CreateFilterBar()
        {
            var bar = new SaleFilterBar(showPeriod: false) { IsVisible = false, Margin = new Thickness(0, 8, 0, 0) };
            bar.Changed += () =>
            {
                UpdateFiltersToggle();
                SalesView.Refresh();
                UpdateFilterCount();
            };
            bar.ResetRequested += () =>
            {
                if (!string.IsNullOrEmpty(SearchReceiptBox.Text))
                    SearchReceiptBox.Text = "";
            };
            return bar;
        }

        private Button CreateFiltersToggle()
        {
            var b = new Button { Height = 36, Padding = new Thickness(12, 0) };
            b.Classes.Add("sec-outline");
            b.Name = "SalesFiltersToggle";
            b.Click += (_, _) =>
            {
                _filterBar.IsVisible = !_filterBar.IsVisible;
                UpdateFiltersToggle();
            };
            UpdateFiltersToggle(b);
            return b;
        }

        private void UpdateFiltersToggle(Button b = null)
        {
            b ??= _filtersToggle;
            if (b is null)
                return;
            var n = _filterBar?.Filter.ActiveCount ?? 0;
            var open = _filterBar?.IsVisible == true;
            b.Content = (open ? "▴ " : "▾ ") + Tr.T("Фильтры", "Чыпкалар", "Filters", "Filtreler", "Filtrlar") + (n > 0 ? $" ({n})" : "");
            Avalonia.Automation.AutomationProperties.SetName(b, Tr.T("Фильтры чеков", "Чектердин чыпкалары", "Receipt filters", "Fiş filtreleri", "Chek filtrlari") + (n > 0 ? $": {n}" : ""));
        }

        /// <summary>Фильтр панели (без периода — он у «Продаж» свой).</summary>
        private bool PassesSharedFilter(SaleItem s) =>
            _filterBar is null
            || _filterBar.Filter.Matches(new SaleListFilter.Row(s.CreatedAt, s.TotalAmount, s.PaymentMethod, s.Status, s.ReturnStatus, s.Cashier, s.ClientName), DateTime.Now);

        /// <summary>Поиск по товару: штрихкод/артикул из каталога кассы → название в составе чека.</summary>
        private bool ProductCodeMatches(SaleItem s, string query)
        {
            if (!string.Equals(_searchProductText, query, StringComparison.Ordinal))
            {
                _searchProductText = query;
                List<CatalogProductTileVm> catalog;
                try
                {
                    catalog = CatalogCacheService.Products.ToList();
                }
                catch (InvalidOperationException)
                {
                    catalog = new List<CatalogProductTileVm>();
                }
                _searchProductQuery = new SaleProductQuery(query, catalog.Select(p => ((string)p.Id, (string)p.Title,
                    (IEnumerable<string>)new[] { p.Barcode, p.Article, p.ProductCode, p.AlternateBarcodesRaw })));
            }
            return _searchProductQuery?.MatchesText(s.ItemsSummary) == true;
        }

        /// <summary>«Найдено N из M» в панели фильтров.</summary>
        private void UpdateFilterCount()
        {
            if (_filterBar is null)
                return;
            var shown = SalesView.Cast<object>().Count();
            _filterBar.SetCount(shown, _sales.Count);
        }
    }
}
