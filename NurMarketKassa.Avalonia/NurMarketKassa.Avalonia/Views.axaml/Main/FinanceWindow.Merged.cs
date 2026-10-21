using NurMarketKassa.AvaloniaHost.Views.Analytics;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-10-18, решение владельца (аудит, правило 8 «Финансы ≈ Продажи»): один раздел «Продажи и финансы».
    ///
    /// Окно «Финансов» — хозяин: одна шапка (период, Excel/Word, «Обновить»), один ряд плиток и вкладки. Части окна
    /// «Продаж» встают к нему (SalesWindow.Embedded.cs): плитки «Скидки» и «Оплачено бонусами» — в ряд плиток,
    /// вкладка «Продажи» становится «Чеками» — поиск чека и таблица «Продаж» с окном чека (повторная печать, товарный
    /// чек, накладная). Прежняя таблица чеков «Финансов» не показывается. ABC — срезы обоих разделов (выручка, прибыль,
    /// штуки, категории, бренды), последней вкладкой. Расчёты, запросы, выгрузка и печать — прежние, каждого окна свои.
    ///
    /// Без вызова <see cref="AsSalesAndFinance"/> окно прежнее: так открываются «Финансы», когда «Продажи» недоступны
    /// (нет права «Продажи» или часть скрыта в «Разделах меню»), и раздел «Аналитика».</summary>
    public partial class FinanceWindow
    {
        private SalesWindow _mergedSales;

        /// <summary>2026-10-18, доводка О4: пустые «Возвраты» и «История» — с подсказкой, что делать (вызывается из конструктора).</summary>
        private void AddEmptyHints()
        {
            UiKit.EmptyHint(RefundsGrid, Tr.T("Возвратов за этот период нет — это хорошо. Выберите другой период, чтобы посмотреть прошлые.",
                "Бул мезгилде кайтаруу жок — бул жакшы. Мурункуларды көрүү үчүн башка мезгилди тандаңыз.",
                "No returns for this period — that's good. Pick another period to see earlier ones.",
                "Bu dönemde iade yok — bu iyi. Öncekileri görmek için başka bir dönem seçin.",
                "Bu davrda qaytarishlar yo'q — bu yaxshi. Oldingilarini ko'rish uchun boshqa davrni tanlang."));
            UiKit.EmptyHint(HistoryGrid, Tr.T("За этот период операций нет. Выберите другой период.",
                "Бул мезгилде операция жок. Башка мезгилди тандаңыз.",
                "No operations for this period. Pick another period.",
                "Bu dönemde işlem yok. Başka bir dönem seçin.",
                "Bu davrda amallar yo'q. Boshqa davrni tanlang."));
        }

        internal static string SalesAndFinanceTitle =>
            Tr.T("Продажи и финансы", "Сатуулар жана каржы", "Sales & finance", "Satışlar ve finans", "Sotuvlar va moliya");

        /// <summary>Сделать окно разделом «Продажи и финансы». Вызывается сразу после создания, до показа (в программе
        /// владельца — до AsOwnerSection).</summary>
        internal FinanceWindow AsSalesAndFinance()
        {
            if (_mergedSales != null || _isAnalyticsSection)
                return this;

            var sales = new SalesWindow(); // как NavigateToSale_Click; в DI — тот же AddTransient без зависимостей
            var parts = sales.DetachForFinance();
            _mergedSales = sales;

            Title = SalesAndFinanceTitle;
            HeaderTitleText.Text = SalesAndFinanceTitle;

            if (TileGridOf(TotalSalesText) is { } tiles)
            {
                foreach (var tile in parts.Tiles)
                    tiles.Children.Add(tile);
                // 2026-10-18, доводка О4: второй ряд (средний чек, чеки, скидки, бонусы) — компактными плитками, главные четыре
                // (выручка, возвраты, наличные, безнал) — крупными; ряд плиток занимал треть экрана.
                for (var i = 4; i < tiles.Children.Count; i++)
                    tiles.Children[i].Classes.Set("compact", true);
            }

            SalesTab.Header = Tr.T("Чеки", "Чектер", "Receipts", "Fişler", "Cheklar");
            SalesTab.Content = parts.Checks;
            MoveAbcTabToEnd();
            FinanceTabs.SelectedItem = SalesTab;

            // Меню ☰ окна в кассе: «Продажа» открывала окно «Продаж» — теперь это вкладка «Чеки».
            NavSaleButton.IsVisible = false;
            return this;
        }

        /// <summary>Из AsOwnerSection, после её срезов ABC и вкладки «Смены»: срезы обоих разделов, ABC — последней.</summary>
        private void ApplySalesAndFinanceOwnerTabs()
        {
            AbcSection.SliceKeys = AbcSectionView.SalesSliceKeys;
            MoveAbcTabToEnd();
            FinanceTabs.SelectedItem = SalesTab;
        }

        private void MoveAbcTabToEnd()
        {
            if (!FinanceTabs.Items.Contains(AbcTab))
                return;
            FinanceTabs.Items.Remove(AbcTab);
            FinanceTabs.Items.Add(AbcTab);
        }

        /// <summary>Открыть вкладку по слову Нур Советника или меню: checks, returns, history, shifts, abc. Вкладки, которой
        /// в этом окне нет (например, «Смены» в кассе), — ничего не меняется.</summary>
        internal void SelectSalesFinanceTab(string tab)
        {
            TabItem target = tab switch
            {
                "checks" => SalesTab,
                "returns" => ReturnsTab,
                "history" => HistoryTab,
                "shifts" => _ownerShiftsTab,
                "abc" => AbcTab,
                _ => null,
            };
            if (target != null && FinanceTabs.Items.Contains(target))
                FinanceTabs.SelectedItem = target;
        }

        /// <summary>2026-10-18, ревью объединения: список чеков периода уже загружен «Финансами» — «Чеки», «Скидки» и «Оплачено
        /// бонусами» считаются по нему (поля — те же поля ответа сервера, что читает список «Продаж»). Второго списка, сводки,
        /// топа товаров и запросов на каждый чек у встроенных «Продаж» нет; в фоне — только состав новых чеков в строках
        /// таблицы (как было). Вызывается в конце загрузки списка, поэтому общая полоса загрузки гаснет уже после «Чеков».</summary>
        private void ShowMergedSales(List<SaleItem> sales, DateTime from, DateTime to)
        {
            if (_mergedSales == null)
                return;
            try
            {
                _mergedSales.ShowFromFinance(ToSalesRows(sales), from, to, _salesListTruncated);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Продажи и финансы: вкладка «Чеки» не обновлена: {ex}", "WARNING");
            }
        }

        /// <summary>2026-10-18, 2-е ревью: начало загрузки — «Чеки» с признаком загрузки, «Скидки» и «Оплачено бонусами» — «…».</summary>
        private void BeginMergedSalesLoad()
        {
            try
            {
                _mergedSales?.BeginFinanceLoad();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Продажи и финансы: начало загрузки «Чеков» не отмечено: {ex.Message}", "WARNING");
            }
        }

        /// <summary>Первые страницы списка — сразу во вкладку «Чеки» (плитки «Скидки» ждут весь список).</summary>
        private void ShowMergedSalesFirstRows(List<SaleItem> firstRows)
        {
            if (_mergedSales == null)
                return;
            try
            {
                _mergedSales.ShowFirstRowsFromFinance(ToSalesRows(firstRows));
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Продажи и финансы: первые строки «Чеков» не показаны: {ex.Message}", "WARNING");
            }
        }

        /// <summary>Строка списка «Финансов» → строка таблицы «Продаж» (те же поля ответа сервера).</summary>
        private static List<SalesWindow.SaleItem> ToSalesRows(List<SaleItem> sales) => sales.Select(s => new SalesWindow.SaleItem
        {
            Id = s.Id,
            CreatedAt = s.CreatedAt,
            ReceiptNumber = s.ReceiptNumber,
            TotalAmount = s.TotalAmount,
            DiscountTotal = s.DiscountTotal,
            PaymentMethod = s.PaymentMethod,
            Status = s.Status,
            IsRefund = s.IsRefund,
            RefundReason = s.RefundReason,
            ItemsSummary = s.FirstItemName,
        }).ToList();

        /// <summary>Нет связи с сервером — «Чеки» по данным кассы, прежним запасным путём «Продаж» (без сети).</summary>
        private void ShowMergedSalesLocal(DateTime from, DateTime to)
        {
            if (_mergedSales == null)
                return;
            try
            {
                _mergedSales.ShowLocalForFinance(from, to);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Продажи и финансы: вкладка «Чеки» (данные кассы) не обновлена: {ex}", "WARNING");
            }
        }

        private static KpiTileGrid TileGridOf(Control control)
        {
            for (var parent = control?.Parent; parent != null; parent = parent.Parent)
            {
                if (parent is KpiTileGrid grid)
                    return grid;
            }
            return null;
        }
    }
}
