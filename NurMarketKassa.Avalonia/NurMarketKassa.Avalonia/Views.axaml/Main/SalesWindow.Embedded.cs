using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-10-18, решение владельца (аудит, правило 8 «Финансы ≈ Продажи»): «Финансы» и «Продажи» — один
    /// раздел «Продажи и финансы». Хозяин — окно «Финансов» (FinanceWindow.Merged.cs): у него вкладки, период, смены и
    /// выгрузка. Это окно при этом НЕ показывается — его части переезжают к хозяину: плитки «Скидки» и «Оплачено
    /// бонусами», поиск чека, таблица чеков с окном чека (повторная печать, товарный чек, накладная) и строка сообщений.
    /// Код, запросы и расчёты остаются этого окна, как были; период и список чеков даёт хозяин (<see cref="ShowFromFinance"/>, без второго списка с сервера).
    /// Почему не копия в «Финансах»: два расчёта одних цифр со временем разойдутся.</summary>
    public partial class SalesWindow
    {
        /// <summary>Окно встроено в «Продажи и финансы»: своего ABC нет (срезы — во вкладке «ABC» хозяина).</summary>
        private bool _embeddedInFinance;

        internal sealed class FinanceHostParts
        {
            /// <summary>Плитки «Скидки» и «Оплачено бонусами» — в ряд плиток хозяина.</summary>
            public Control[] Tiles { get; init; }

            /// <summary>Содержимое вкладки «Чеки»: поиск, таблица, сообщения, окно чека.</summary>
            public Control Checks { get; init; }
        }

        /// <summary>Вынимает части для окна «Финансов». Вызывается один раз, сразу после создания, до показа хозяина.</summary>
        internal FinanceHostParts DetachForFinance()
        {
            _embeddedInFinance = true;
            _clockTimer?.Stop();

            var tiles = new[] { TileOf(DiscountsText), TileOf(PointsRedeemedText) };
            foreach (var tile in tiles)
                (tile.Parent as Panel)?.Children.Remove(tile);

            // Поле поиска — в рамке; рамка уходит из шапки окна во вкладку «Чеки», над таблицей.
            var search = (Control)SearchReceiptBox.Parent ?? SearchReceiptBox;
            (search.Parent as Panel)?.Children.Remove(search);
            search.HorizontalAlignment = HorizontalAlignment.Left;
            search.Margin = new Thickness(0, 0, 0, 8);
            // Подсказка в поле поиска — в конструкторе окна (2026-10-18, этап О4: и у отдельных «Продаж»).

            // 2026-10-18, этап О4: таблица чеков теперь во вкладке «Чеки» этого окна — вынимаем её оттуда.
            ChecksTab.Content = null;
            RootGrid.Children.Remove(NoticeBox);
            RootGrid.Children.Remove(ReceiptDetailsPopup);

            var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
            Grid.SetRow(search, 0);
            Grid.SetRow(SalesGrid, 1);
            Grid.SetRow(NoticeBox, 2);
            body.Children.Add(search);
            body.Children.Add(SalesGrid);
            body.Children.Add(NoticeBox);
            body.Children.Add(ReceiptDetailsPopup);
            // 2026-10-18, 2-е ревью: признак загрузки во вкладке «Чеки» — тонкая полоса над таблицей, пока «Финансы» качают список.
            var loading = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 3,
                MinHeight = 3,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Background = Avalonia.Media.Brushes.Transparent,
                ZIndex = 10,
            };
            loading.Bind(ProgressBar.ForegroundProperty, loading.GetResourceObservable("BrushAccent"));
            loading.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(IsLoading)));
            Grid.SetRow(loading, 1);
            body.Children.Add(loading);

            // Привязки таблицы и сообщений (SalesView, HasError, ErrorMessage) — к этому окну, а не к «Финансам».
            var checks = new Border { Background = Avalonia.Media.Brushes.Transparent, Padding = new Thickness(8), Child = body, DataContext = this };
            foreach (var tile in tiles)
                tile.DataContext = this;
            return new FinanceHostParts { Tiles = tiles, Checks = checks };
        }

        /// <summary>2026-10-18, ревью объединения: список чеков периода загрузили «Финансы» (те же поля ответа сервера) —
        /// таблица, «Скидки» и «Оплачено бонусами» считаются прежним UpdateStats по нему. Своего списка, сводки, топа
        /// товаров и запросов на каждый чек нет (у встроенного окна их не видно); в фоне — состав новых чеков в строках,
        /// как было.</summary>
        internal void ShowFromFinance(List<SaleItem> sales, DateTime from, DateTime to, bool listTruncated)
        {
            CancelLoad();
            IsLoading = false;
            _historyFrom = from;
            _historyTo = to;
            _serverSummary = null;
            _salesListTruncated = listTruncated;
            AssignFallbackReceiptNumbers(sales);
            var noRefunds = new List<RefundItem>();
            UpdateCollections(sales, noRefunds);
            UpdateStats(sales, noRefunds);
            // Пометки «нет связи» / «список обрезан» показывают сами «Финансы» — вторую строку не дублируем.
            ErrorMessage = null;
            _ = LoadReceiptContentsAsync(sales);
        }

        /// <summary>2026-10-18, 2-е ревью: «Финансы» начали загрузку — признак загрузки во вкладке, «Скидки» и «Оплачено
        /// бонусами» — «…» (прошлый период не висит, пока качается новый список).</summary>
        internal void BeginFinanceLoad()
        {
            CancelLoad();
            IsLoading = true;
            DiscountsText.Text = "…";
            PointsRedeemedText.Text = "…";
        }

        /// <summary>Загрузка «Финансов» закончилась (в том числе отменой или ошибкой) — признак загрузки снимается.</summary>
        internal void EndFinanceLoad() => IsLoading = false;

        /// <summary>Первые страницы списка «Финансов» — сразу в таблицу; плитки считаются, когда придёт весь список.</summary>
        internal void ShowFirstRowsFromFinance(List<SaleItem> firstRows)
        {
            AssignFallbackReceiptNumbers(firstRows);
            UpdateCollections(firstRows, new List<RefundItem>());
        }

        /// <summary>Нет связи с сервером — по данным кассы, тем же источником, что плитки «Финансов» (проданные строки кассы,
        /// SoldLineItemsStore; так и раньше делали отдельные «Продажи» при сбое сервера). Без сети.</summary>
        internal void ShowLocalForFinance(DateTime from, DateTime to)
        {
            CancelLoad();
            IsLoading = false;
            _historyFrom = from;
            _historyTo = to;
            _serverSummary = null;
            _salesListTruncated = false;
            LoadFromLocalHistory(from, to, CancellationToken.None);
            ErrorMessage = null;
        }

        /// <summary>Esc в «Финансах»: сначала закрывается открытый чек.</summary>
        internal bool CloseReceiptPopup()
        {
            if (!ReceiptDetailsPopup.IsOpen)
                return false;
            ReceiptDetailsPopup.IsOpen = false;
            return true;
        }

        /// <summary>«Финансы» закрылись — останавливаем загрузки и закрываем это (не показанное) окно.</summary>
        internal void ReleaseFromFinance()
        {
            CancelLoad();
            // 2026-10-18, ревью: состав чеков в фоне (CancelLoad его тоже останавливает — здесь явно).
            _contentsCts?.Cancel();
            _contentsCts = null;
            _searchCts?.Cancel();
            _searchCts = null;
            _clockTimer?.Stop();
            try
            {
                Close();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Продажи и финансы: окно «Продаж» не закрылось: {ex.Message}", "WARNING");
            }
        }

        /// <summary>Плитка (прямой элемент ряда плиток), в которой лежит надпись.</summary>
        private static Control TileOf(Control control)
        {
            while (control.Parent is Control parent && parent is not KpiTileGrid)
                control = parent;
            return control;
        }
    }
}
