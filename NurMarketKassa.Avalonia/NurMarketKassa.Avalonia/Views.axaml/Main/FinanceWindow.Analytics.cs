using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

#nullable disable

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-10-19, этап О10 (аудит 19.10: «Аналитика по-прежнему повторяет 6 плиток Продаж», «вкладка «Долги» = раздел
    /// «Долги клиентов»», «Способы оплаты дважды»). Раздел «Аналитика» программы владельца — прибыль, тренды и сравнения, без
    /// повтора «Продаж и финансов»: ряд из 6 плиток (выручка, возвраты, наличные, безнал, средний чек, чеки) скрыт, на его месте —
    /// строка «где смотреть каждый чек» со ссылками «Все продажи» и «Долги клиентов» в их разделы; вкладки «Долги» нет (это
    /// ссылка); круговая «Способы оплаты» скрыта (те же цифры — полосой выше). Данные и запросы окна прежние — убраны только
    /// повторы с экрана. В кассе и в «Продажах и финансах» ничего из этого не вызывается.</summary>
    public partial class FinanceWindow
    {
        private void ApplyAnalyticsLayoutO10()
        {
            if (TileGridOf(TotalSalesText) is { } tiles)
                tiles.IsVisible = false;
            PaymentPieTitle.IsVisible = false;
            PaymentPieCard.IsVisible = false;
            ProfitMetricsTitle.Text = Tr.T("Прибыль за период", "Мезгилдеги пайда", "Profit for the period", "Dönem kârı", "Davr foydasi");
            // «Товары»: круговая «Доля выручки топ-10» повторяет полосы над ней — скрыта (полосы и таблица остаются).
            if (PopularProductsPie.Parent is Border pieCard && pieCard.Parent is Panel pieHost)
            {
                var at = pieHost.Children.IndexOf(pieCard);
                pieCard.IsVisible = false;
                if (at > 0 && pieHost.Children[at - 1] is TextBlock pieTitle)
                    pieTitle.IsVisible = false;
            }

            var links = BuildAnalyticsLinks();
            Grid.SetRow(links, 2);
            ContentGrid.Children.Add(links);
        }

        /// <summary>Строка под шапкой: что здесь, где каждый чек и долги — кнопками-обводками в те разделы программы владельца.</summary>
        private Control BuildAnalyticsLinks()
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
            var text = new TextBlock
            {
                Text = Tr.T("Здесь — прибыль, тренды по дням и часам, лидеры продаж и сезонность. Каждый чек, выручку и оплаты по чекам смотрите в «Продажах и финансах».",
                    "Бул жерде — пайда, күндөр жана сааттар боюнча тренддер, сатуу лидерлери жана мезгилдүүлүк. Ар бир чекти, түшүмдү жана төлөмдөрдү «Сатуулар жана каржы» бөлүмүнөн караңыз.",
                    "Here: profit, trends by day and hour, best sellers and seasonality. Every receipt, revenue and payments by receipt are in “Sales & finance”.",
                    "Burada: kâr, gün ve saate göre eğilimler, en çok satanlar ve mevsimsellik. Her fişi, ciroyu ve fiş ödemelerini «Satışlar ve finans» bölümünde görün.",
                    "Bu yerda — foyda, kunlar va soatlar bo'yicha tendensiyalar, sotuv yetakchilari va mavsumiylik. Har bir chek, tushum va to'lovlarni «Sotuvlar va moliya» bo'limida ko'ring."),
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable("BrushTextSoft"));
            row.Children.Add(text);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var sales = LinkButton(Tr.T("Все продажи", "Бардык сатуулар", "All sales", "Tüm satışlar", "Barcha sotuvlar"));
            sales.Click += (_, _) =>
            {
                if (Owner is OwnerShellWindow shell)
                    shell.OpenAllSales();
            };
            buttons.Children.Add(sales);
            if (TariffGate.CanUseDebts)
            {
                var debts = LinkButton(Tr.T("Долги клиентов", "Кардарлардын карыздары", "Customer debts", "Müşteri borçları", "Mijozlar qarzlari"));
                debts.Click += (_, _) =>
                {
                    if (Owner is OwnerShellWindow shell)
                        shell.OpenDebtsSection();
                };
                buttons.Children.Add(debts);
            }
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            return row;
        }

        private static Button LinkButton(string text)
        {
            var b = new Button { Content = text, MinHeight = 36, Padding = new Thickness(12, 0) };
            b.Classes.Add("sec-outline");
            Avalonia.Automation.AutomationProperties.SetName(b, text);
            return b;
        }
    }
}
