using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-03, владелец: «если в убыток продаёт со скидкой — фиксировать это в админке». Раздел программы
/// владельца «Продажи в убыток»: чеки, где со скидкой товар ушёл дешевле закупки (журнал LossSalesStore, пишет
/// касса при оплате). Период, итог убытка, по каждому чеку — кассир и строки «закупка → продано → убыток».
/// Окно собрано в коде, как «Прибыль и деньги»; цвета — из темы.</summary>
public sealed class LossSalesWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly StackPanel _periods = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly TextBlock _summary = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new() { Spacing = 10 };
    private string _period = "month";

    public LossSalesWindow()
    {
        Title = Tr.T("Продажи в убыток", "Зыян менен сатуулар", "Sales at a loss", "Zararına satışlar", "Zarariga sotuvlar");
        Width = 1100;
        Height = 800;
        Use(this, BackgroundProperty, "BrushWindowBackdrop");

        var root = new Grid { Margin = new Thickness(24, 16, 24, 24), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
        var title = new TextBlock { Text = Title, FontSize = 22, FontWeight = FontWeight.Bold };
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var hint = new TextBlock
        {
            Text = Tr.T("Чеки, где из-за скидки товар продан дешевле закупочной цены. Записывает касса этого компьютера при оплате.",
                "Арзандатуудан улам товар сатып алуу баасынан арзан сатылган чектер. Бул компьютердеги касса төлөөдө жазат.",
                "Receipts where a discount sold an item below its purchase price. Recorded by this computer's till at payment.",
                "İndirim yüzünden ürünün alış fiyatının altında satıldığı fişler. Bu bilgisayardaki kasa ödeme sırasında kaydeder.",
                "Chegirma tufayli mahsulot xarid narxidan arzon sotilgan cheklar. Bu kompyuterdagi kassa to'lovda yozadi."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        };
        Use(hint, TextBlock.ForegroundProperty, "BrushTextSoft");
        _periods.Margin = new Thickness(0, 0, 0, 12);
        _summary.Margin = new Thickness(0, 0, 0, 12);
        Use(_summary, TextBlock.ForegroundProperty, "BrushDanger");
        Grid.SetRow(hint, 1);
        Grid.SetRow(_periods, 2);
        Grid.SetRow(_summary, 3);
        var scroll = new ScrollViewer { Content = _list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 4);
        root.Children.Add(title);
        root.Children.Add(hint);
        root.Children.Add(_periods);
        root.Children.Add(_summary);
        root.Children.Add(scroll);
        Content = root;

        BuildPeriods();
        Opened += (_, _) => Render();
        LossSalesStore.Changed += OnChanged;
        Closed += (_, _) => LossSalesStore.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Render);

    private DateTimeOffset From() => _period switch
    {
        "today" => DateTime.Today,
        "week" => DateTime.Today.AddDays(-6),
        "all" => DateTimeOffset.MinValue,
        _ => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1),
    };

    private void BuildPeriods()
    {
        _periods.Children.Clear();
        void Add(string key, string text)
        {
            var b = new Button { Content = text, Padding = new Thickness(14, 6) };
            if (key == _period)
                Use(b, Button.BackgroundProperty, "BrushAccentSoft");
            b.Click += (_, _) =>
            {
                _period = key;
                BuildPeriods();
                Render();
            };
            _periods.Children.Add(b);
        }

        Add("today", Tr.T("Сегодня", "Бүгүн", "Today", "Bugün", "Bugun"));
        Add("week", Tr.T("7 дней", "7 күн", "7 days", "7 gün", "7 kun"));
        Add("month", Tr.T("Этот месяц", "Бул ай", "This month", "Bu ay", "Shu oy"));
        Add("all", Tr.T("Всё (180 дней)", "Баары (180 күн)", "All (180 days)", "Tümü (180 gün)", "Hammasi (180 kun)"));
    }

    private void Render()
    {
        _list.Children.Clear();
        var from = From();
        var records = LossSalesStore.ReadAll().Where(r => r.At >= from).ToList();
        var total = records.Sum(r => r.Loss);
        _summary.Text = records.Count == 0
            ? Tr.T("За этот период продаж в убыток нет.", "Бул мезгилде зыян менен сатуу жок.", "No sales at a loss for this period.", "Bu dönemde zararına satış yok.", "Bu davrda zarariga sotuv yo'q.")
            : Tr.T($"Чеков в убыток: {records.Count} · убыток всего {Money(total)}", $"Зыян менен чектер: {records.Count} · жалпы зыян {Money(total)}",
                $"Receipts at a loss: {records.Count} · total loss {Money(total)}", $"Zararına fişler: {records.Count} · toplam zarar {Money(total)}",
                $"Zarariga cheklar: {records.Count} · jami zarar {Money(total)}");

        foreach (var r in records.Take(300))
        {
            var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12) };
            Use(card, Border.BackgroundProperty, "BrushPanel");
            Use(card, Border.BorderBrushProperty, "BrushBorder");
            var body = new StackPanel { Spacing = 4 };
            var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var when = new TextBlock
            {
                Text = r.At.LocalDateTime.ToString("dd.MM.yyyy HH:mm", Ru) + (string.IsNullOrWhiteSpace(r.Cashier) ? "" : " · " + r.Cashier),
                FontSize = 14, FontWeight = FontWeight.SemiBold,
            };
            Use(when, TextBlock.ForegroundProperty, "BrushText");
            var loss = new TextBlock { Text = "−" + Money(r.Loss), FontSize = 15, FontWeight = FontWeight.Bold };
            Use(loss, TextBlock.ForegroundProperty, "BrushDanger");
            Grid.SetColumn(loss, 1);
            head.Children.Add(when);
            head.Children.Add(loss);
            body.Children.Add(head);
            foreach (var l in r.Lines)
            {
                var line = new TextBlock
                {
                    Text = Tr.T(
                        $"• {l.Title}: {l.Quantity:0.###} {l.Unit} · закупка {Money(l.UnitCost * l.Quantity)} · продано за {Money(l.Net)} · убыток {Money(l.Loss)}",
                        $"• {l.Title}: {l.Quantity:0.###} {l.Unit} · сатып алуу {Money(l.UnitCost * l.Quantity)} · сатылды {Money(l.Net)} · зыян {Money(l.Loss)}",
                        $"• {l.Title}: {l.Quantity:0.###} {l.Unit} · cost {Money(l.UnitCost * l.Quantity)} · sold for {Money(l.Net)} · loss {Money(l.Loss)}",
                        $"• {l.Title}: {l.Quantity:0.###} {l.Unit} · alış {Money(l.UnitCost * l.Quantity)} · satış {Money(l.Net)} · zarar {Money(l.Loss)}",
                        $"• {l.Title}: {l.Quantity:0.###} {l.Unit} · xarid {Money(l.UnitCost * l.Quantity)} · sotildi {Money(l.Net)} · zarar {Money(l.Loss)}"),
                    FontSize = 13, TextWrapping = TextWrapping.Wrap,
                };
                Use(line, TextBlock.ForegroundProperty, "BrushTextSoft");
                body.Children.Add(line);
            }
            card.Child = body;
            _list.Children.Add(card);
        }
    }

    private static string Money(double v) => v.ToString("N2", Ru) + " " + Tr.T("сом", "сом", "som", "som", "so'm");

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) =>
        target.Bind(property, this.GetResourceObservable(key));
}
