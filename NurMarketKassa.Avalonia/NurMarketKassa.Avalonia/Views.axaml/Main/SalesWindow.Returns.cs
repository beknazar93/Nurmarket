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
            // 2026-11-11 (живой показ 1.17.85, Д-8: кнопки «Возврат» в колонке не на одной вертикали — у чека без пометки кнопка левее): пометка слева
            // в своей колонке, кнопка — всегда у правого края ячейки.
            mark.HorizontalAlignment = HorizontalAlignment.Right;
            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(button, 1);
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
                    await ReloadAfterReturnAsync().ConfigureAwait(true); // список, итоги и пометки — после возврата с сервера
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Продажи: возврат не открылся: {ex}", "WARNING");
            }
        }

        /// <summary>2026-11-12 (владелец: «Возврат чеков добавь и программу владельца»): во «Продажах и финансах» список чеков, плитки и вкладку
        /// «Возвраты» грузит окно «Финансов» — после возврата перечитывает оно (свежими данными сервера); отдельные «Продажи» — сами, как раньше.</summary>
        internal Func<Task> HostReload { get; set; }

        private Task ReloadAfterReturnAsync() => HostReload is { } host ? host() : LoadDataAsync(_historyFrom, _historyTo);

        private ReceiptReturnActions _popupReturn;

        /// <summary>2026-11-12: под карточкой чека в программе владельца — «Возврат» (то же окно возврата, что в кассе, с этим чеком) и «Правка возвратов»
        /// (список возвратов чека с правкой/удалением 24 ч). Касса — без изменений (ReceiptReturnActions.Enabled).</summary>
        private void ShowPopupReturnActions(string saleId, string receiptNumber, System.Text.Json.JsonElement json)
        {
            if (!ReceiptReturnActions.Enabled)
                return;
            if (_popupReturn == null)
            {
                _popupReturn = new ReceiptReturnActions
                {
                    OwnerWindow = () => TopLevel.GetTopLevel(SalesGrid) as Window ?? this,
                    BeforeOpen = () => ReceiptDetailsPopup.IsOpen = false,
                    Changed = ReloadAfterReturnAsync,
                };
                if (PopupActionBarHost.Parent is Panel panel)
                    panel.Children.Insert(panel.Children.IndexOf(PopupActionBarHost) + 1, _popupReturn.View);
            }
            // 2026-11-12 (аудит 12.11, Д-5): с кнопками возврата карточка на 1024×600 занимала всю высоту и закрывала итог чека — высота карточки
            // ограничена окном с отступом, чек внутри прокручивается (строка «*» сетки карточки).
            if (ReceiptDetailsPopup.Child is Border card && (TopLevel.GetTopLevel(SalesGrid) ?? this) is { Bounds.Height: > 200 } top)
                card.MaxHeight = top.Bounds.Height - 24;
            var row = _sales.FirstOrDefault(s => string.Equals(s.Id, saleId, StringComparison.OrdinalIgnoreCase));
            _popupReturn.ShowFor(saleId, receiptNumber, row?.Status is { Length: > 0 } st ? st : JsonText(json, "status"),
                row?.ReturnStatus is { Length: > 0 } rs ? rs : JsonText(json, "return_status"));
        }

        private static string JsonText(System.Text.Json.JsonElement json, string name) =>
            json.ValueKind == System.Text.Json.JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

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
