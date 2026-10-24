using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-23, владелец: «чек красивым как в мобилке сделай» — карточка чека для окна «Продажи и финансы → Чеки»
/// (как ReceiptView мобилки): «бумага» с зубчатым нижним краем, шапка с магазином, №, плашка статуса, позиции «1 × 4,00 — сумма»,
/// блок оплаты, крупный итог. Цвета — ресурсы темы (светлая/тёмная переключаются сами), шрифты — Montserrat и Onest из брендбука.
/// Печатный текст чека не затрагивает (его строит SaleReceiptTextBuilder).</summary>
internal static class SaleReceiptCardView
{
    public static Control Build(SaleReceiptCardData d)
    {
        var body = new StackPanel { Spacing = 0 };

        body.Children.Add(Text(d.Shop, 17, "BrushText", bold: true, heading: true, center: true));
        if (d.Inn.Length > 0) body.Children.Add(Text(Tr.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ") + d.Inn, 12, "BrushTextSoft", center: true));
        if (d.Address.Length > 0) body.Children.Add(Text(d.Address, 12, "BrushTextSoft", center: true));

        // «Чек № 001882» слева, плашка статуса справа.
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 6) };
        var num = Text(Tr.T("Чек № ", "Чек № ", "Receipt № ", "Fiş № ", "Chek № ") + d.Number, 15, "BrushText", bold: true, heading: true);
        num.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(num);
        var badge = Badge(d);
        Grid.SetColumn(badge, 1);
        head.Children.Add(badge);
        body.Children.Add(head);

        if (d.DateTimeText.Length > 0) body.Children.Add(Pair(Tr.T("Дата и время", "Күнү жана убактысы", "Date and time", "Tarih ve saat", "Sana va vaqt"), d.DateTimeText));
        if (d.Cashier.Length > 0) body.Children.Add(Pair(Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"), d.Cashier));
        if (d.Consultant.Length > 0) body.Children.Add(Pair(Tr.T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), d.Consultant));
        if (d.Client.Length > 0) body.Children.Add(Pair(Tr.T("Клиент", "Кардар", "Client", "Müşteri", "Mijoz"), d.Client));
        body.Children.Add(Dash());

        foreach (var l in d.Lines)
        {
            var item = new StackPanel { Spacing = 1, Margin = new Thickness(0, 0, 0, 9) };
            item.Children.Add(Text(l.Name, 14, "BrushText"));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(Text(l.QtyPrice, 13, "BrushTextSoft", num: true));
            var tot = Text(l.Total, 14, "BrushText", bold: true, num: true);
            tot.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(tot, 1);
            row.Children.Add(tot);
            item.Children.Add(row);
            body.Children.Add(item);
        }
        body.Children.Add(Dash());

        foreach (var r in d.BeforeTotal) body.Children.Add(Pair(r.Label, r.Value, r.Accent));

        // 2026-10-24 (аудит, круг 8, С-15): крупный ИТОГО — сразу после позиций/скидки, ПЕРЕД способом оплаты, внесено, сдачей,
        // долгом и «Вернуть до…» (раньше он был внизу под ними и терялся). Onest.
        if (d.BeforeTotal.Count > 0) body.Children.Add(Dash());
        var total = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 0) };
        var tl = Text(d.TotalLabel, 17, "BrushText", bold: true, heading: true);
        tl.VerticalAlignment = VerticalAlignment.Center;
        total.Children.Add(tl);
        var tv = Text(d.TotalText, 24, "BrushText", bold: true, heading: true, num: true);
        tv.Margin = new Thickness(12, 0, 0, 0);
        tv.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tv, 1);
        total.Children.Add(tv);
        body.Children.Add(total);

        if (d.Payment.Count > 0) body.Children.Add(Dash());
        foreach (var r in d.Payment) body.Children.Add(Pair(r.Label, r.Value, r.Accent));

        body.Children.Add(Text(Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışveriş için teşekkürler!", "Xaridingiz uchun rahmat!"),
            13, "BrushTextSoft", center: true, bold: true).With(t => t.Margin = new Thickness(0, 14, 0, 0)));

        var top = new Border
        {
            CornerRadius = new CornerRadius(14, 14, 0, 0),
            BorderThickness = new Thickness(1, 1, 1, 0),
            Padding = new Thickness(18, 16, 18, 8),
            BoxShadow = BoxShadows.Parse("0 2 8 0 #14101828"),
            Child = body,
        };
        Res(top, Border.BackgroundProperty, "BrushPanel");
        Res(top, Border.BorderBrushProperty, "BrushBorder");

        // Зубчатый нижний край: 22 зубца, растягивается на ширину карточки.
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            const int teeth = 22;
            c.BeginFigure(new Point(0, 0), true);
            c.LineTo(new Point(teeth * 10, 0));
            for (var i = teeth; i > 0; i--)
            {
                c.LineTo(new Point(i * 10 - 5, 8));
                c.LineTo(new Point((i - 1) * 10, 0));
            }
            c.EndFigure(true);
        }
        var edge = new Avalonia.Controls.Shapes.Path { Data = g, Stretch = Stretch.Fill, Height = 8 };
        Res(edge, Shape.FillProperty, "BrushPanel");

        return new StackPanel { MaxWidth = 380, Spacing = 0, Children = { top, edge } };
    }

    // Подпись слева (мягким), значение справа.
    private static Control Pair(string label, string value, int accent = 0)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 0, 0, 4) };
        g.Children.Add(Text(label, 13.5, "BrushTextSoft"));
        var v = Text(value, 13.5, accent == 1 ? "BrushWarning" : accent == 2 ? "BrushDanger" : "BrushText", bold: accent != 0, num: true);
        v.TextAlignment = TextAlignment.Right;
        v.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    private static Control Dash()
    {
        var line = new Line { StartPoint = new Point(0, 0.5), EndPoint = new Point(4000, 0.5), StrokeThickness = 1, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3, 3 } };
        Res(line, Shape.StrokeProperty, "BrushBorderStrong");
        return new Canvas { Height = 1, ClipToBounds = true, Margin = new Thickness(0, 10), Children = { line } };
    }

    // Плашка статуса: Оплачен — зелёная, Долг — оранжевая, Возврат — серая (цвета темы).
    private static Control Badge(SaleReceiptCardData d)
    {
        var (bg, fg) = d.Status switch
        {
            SaleReceiptCardData.StatusKind.Debt => ("BrushWarningSoft", "BrushWarning"),
            SaleReceiptCardData.StatusKind.Return => ("BrushPanelSoft", "BrushTextSoft"),
            _ => ("BrushSuccessSoft", "BrushUiStatusOk"),
        };
        var b = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 3),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(d.StatusText, 12, fg, bold: true),
        };
        Res(b, Border.BackgroundProperty, bg);
        Res(b, Border.BorderBrushProperty, fg);
        return b;
    }

    private static TextBlock Text(string s, double size, string brush, bool bold = false, bool heading = false, bool center = false, bool num = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (bold) t.FontWeight = FontWeight.Bold;
        if (center) t.TextAlignment = TextAlignment.Center;
        if (heading) Res(t, TextBlock.FontFamilyProperty, "AppHeadingFontFamily");
        if (num) t.Classes.Add("num");
        Res(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static void Res(Control c, AvaloniaProperty p, string key) => c.Bind(p, c.GetResourceObservable(key));

    private static T With<T>(this T t, System.Action<T> a) { a(t); return t; }
}
