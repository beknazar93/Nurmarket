using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-05, владелец: «и ещё где в сводке и аналитике долги??». Карточка «Долги клиентов» в «Сводке»: сколько
/// всего должны магазину, сколько должников, сколько из этого старше 30 дней и три крупных должника; нажатие на карточку
/// или «Подробно» открывает раздел «Долги клиентов». Данные — те же, что в разделе (DebtsView.LoadDebtorsAsync, сервер
/// NurCRM); долги от периода «Сводки» не зависят — это то, что должны сейчас. Обновляется не чаще раза в 2 минуты.</summary>
public partial class OwnerShellWindow
{
    private DateTime _debtsCardAt = DateTime.MinValue;
    private bool _debtsCardLoading;

    private async Task RefreshDebtsCardAsync(bool force = false)
    {
        if (!TariffGate.CanUseDebts || SectionVisibility.IsHidden("debts"))
        {
            DebtsCard.IsVisible = false;
            return;
        }
        if (_debtsCardLoading || (!force && DateTime.UtcNow - _debtsCardAt < TimeSpan.FromMinutes(2)))
            return;
        _debtsCardLoading = true;
        _debtsCardAt = DateTime.UtcNow;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var debtors = await DebtsView.LoadDebtorsAsync(withPhones: false, cts.Token).ConfigureAwait(true);
            RenderDebtsCard(debtors);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Owner app: карточка долгов не обновлена ({ex.Message}).", "WARNING");
        }
        finally
        {
            _debtsCardLoading = false;
        }
    }

    private void RenderDebtsCard(List<DebtsView.Debtor> debtors)
    {
        DebtsCard.IsVisible = true;
        DebtsTitle.Text = DebtsView.TitleText;
        DebtsLinkText.Text = T5("Подробно", "Толугу менен", "Details", "Ayrıntılar", "Batafsil");
        DebtsHost.Children.Clear();
        if (debtors.Count == 0)
        {
            DebtsHost.Children.Add(Line(T5("Долгов нет — все клиенты рассчитались.", "Карыз жок — бардык кардарлар эсептешти.", "No debts — every customer has paid.",
                "Borç yok — tüm müşteriler ödedi.", "Qarz yo'q — barcha mijozlar to'lagan."), good: true));
            return;
        }
        var total = debtors.Sum(d => d.Total);
        var old = debtors.SelectMany(d => d.Sales).Where(s => DebtsView.AgeDays(s.At) > 30).Sum(s => s.Left);
        var sum = new TextBlock { Text = Money(total), FontSize = 24, FontWeight = FontWeight.Bold };
        sum.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushDanger"));
        DebtsHost.Children.Add(sum);
        DebtsHost.Children.Add(Line(T5($"Должников: {debtors.Count} · чеков в долг: {debtors.Sum(d => d.Sales.Count)}",
            $"Карыздарлар: {debtors.Count} · карыз чектер: {debtors.Sum(d => d.Sales.Count)}",
            $"Debtors: {debtors.Count} · receipts on credit: {debtors.Sum(d => d.Sales.Count)}",
            $"Borçlu: {debtors.Count} · veresiye fiş: {debtors.Sum(d => d.Sales.Count)}",
            $"Qarzdorlar: {debtors.Count} · qarzga cheklar: {debtors.Sum(d => d.Sales.Count)}")));
        if (old > 0.005)
            DebtsHost.Children.Add(Line(T5($"Больше 30 дней: {Money(old)}", $"30 күндөн ашык: {Money(old)}", $"Over 30 days: {Money(old)}",
                $"30 günden fazla: {Money(old)}", $"30 kundan ortiq: {Money(old)}"), bad: true));
        var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
        line.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushBorder"));
        DebtsHost.Children.Add(line);
        foreach (var d in debtors.OrderByDescending(d => d.Total).Take(3))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var who = new StackPanel { Spacing = 1 };
            var name = new TextBlock { Text = d.Name, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            var since = new TextBlock
            {
                Text = T5($"с {d.Oldest:dd.MM} · {DebtsView.Days(d.Oldest)}", $"{d.Oldest:dd.MM} баштап · {DebtsView.Days(d.Oldest)}",
                    $"since {d.Oldest:dd.MM} · {DebtsView.Days(d.Oldest)}", $"{d.Oldest:dd.MM} tarihinden · {DebtsView.Days(d.Oldest)}", $"{d.Oldest:dd.MM} dan · {DebtsView.Days(d.Oldest)}"),
                FontSize = 12,
            };
            since.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(DebtsView.AgeDays(d.Oldest) > 30 ? "BrushDanger" : "BrushTextSoft"));
            who.Children.Add(name);
            who.Children.Add(since);
            row.Children.Add(who);
            var amount = new TextBlock { Text = Money(d.Total), FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            amount.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
            Grid.SetColumn(amount, 1);
            row.Children.Add(amount);
            DebtsHost.Children.Add(row);
        }
        if (DebtsCard.Cursor is null)
        {
            DebtsCard.Cursor = new Cursor(StandardCursorType.Hand);
            DebtsCard.PointerPressed += (_, e) =>
            {
                if (!e.Handled)
                    OpenDebts();
            };
        }
    }

    private void DebtsLink_Click(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OpenDebts();
    }
}
