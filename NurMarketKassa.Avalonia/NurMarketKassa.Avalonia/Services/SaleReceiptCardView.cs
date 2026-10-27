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
/// Печатный текст чека не затрагивает (его строит SaleReceiptTextBuilder).
///
/// 2026-10-27, владелец (дважды): «вид чека в десктопе как в мобилке — дизайн, отображение чека». Это теперь ЕДИНЫЙ контрол чека
/// на экране: его строят историю чеков кассы, «Продажи и финансы → Чеки» (касса и владелец), чек клиента, окно «Чек» после оплаты
/// и предпросмотр. Порядок и типографика — один в один с receipt_view.dart мобилки: Montserrat (как в мобилке; Onest тут не
/// используется), магазин 18, ИНН/адрес 12 мягким, плашка «ТОВАРНЫЙ ЧЕК» (11,5, разрядка), пары «подпись — значение» 13, позиции
/// «n. Название / кол-во × цена — сумма жирно», пунктир, «Сумма без скидки / Скидка» красным, ИТОГО 20/22, оплата, сдача жирно,
/// «Вернуть до», QR (если сервер выдал ссылку), «Спасибо за покупку!» и «NurMarket», зубцы бумаги 10 px. Возвраты — отдельным блоком.</summary>
internal static class SaleReceiptCardView
{
    /// <summary>Ширина бумаги: как в мобилке (макс. 400, у нас 380 — в окнах с карточкой и кнопками). 80 мм ≈ 302 px.</summary>
    public const double DefaultWidth = 380;

    public static Control Build(SaleReceiptCardData d, double maxWidth = DefaultWidth)
    {
        var body = new StackPanel { Spacing = 0 };

        body.Children.Add(Text(d.Shop, 18, "BrushText", bold: true, center: true));
        if (d.Inn.Length > 0) body.Children.Add(Text(Tr.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ") + d.Inn, 12, "BrushTextSoft", center: true));
        if (d.Address.Length > 0) body.Children.Add(Text(d.Address, 12, "BrushTextSoft", center: true));

        // Плашка вида документа по центру (как у мобилки) и, если нужно, статус чека.
        var pill = new Border { CornerRadius = new CornerRadius(999), Padding = new Thickness(12, 5), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
            Child = Text(d.Title, 11.5, "BrushText", bold: true, center: true).With(t => t.LetterSpacing = 0.6) };
        Res(pill, Border.BackgroundProperty, "BrushWindow");
        body.Children.Add(pill);
        if (d.ShowStatusBadge && d.StatusNotable)
        {
            var badge = Badge(d);
            badge.HorizontalAlignment = HorizontalAlignment.Center;
            badge.Margin = new Thickness(0, 6, 0, 0);
            body.Children.Add(badge);
        }
        body.Children.Add(new Border { Height = 12 });

        body.Children.Add(Pair(Tr.T("Чек №", "Чек №", "Receipt №", "Fiş №", "Chek №"), d.NumberText, 3));
        if (d.Shift.Length > 0) body.Children.Add(Pair(Tr.T("Смена №", "Смена №", "Shift №", "Vardiya №", "Smena №"), d.Shift));
        if (d.DateTimeText.Length > 0) body.Children.Add(Pair(Tr.T("Дата и время", "Күнү жана убактысы", "Date and time", "Tarih ve saat", "Sana va vaqt"), d.DateTimeText));
        if (d.Cashier.Length > 0) body.Children.Add(Pair(Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"), d.Cashier));
        if (d.Cashbox.Length > 0) body.Children.Add(Pair(Tr.T("Касса", "Касса", "Till", "Kasa", "Kassa"), d.Cashbox));
        if (d.Consultant.Length > 0) body.Children.Add(Pair(Tr.T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), d.Consultant));
        if (d.Client.Length > 0) body.Children.Add(Pair(Tr.T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor"), d.Client));
        body.Children.Add(Dash());

        var n = 0;
        foreach (var l in d.Lines)
        {
            var item = new StackPanel { Spacing = 0, Margin = new Thickness(0, 4) };
            item.Children.Add(Text($"{++n}. {l.Name}", 13, "BrushText", semi: true));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(Text(l.QtyPrice, 13, "BrushTextSoft"));
            var tot = Text(l.Total, 13, "BrushText", bold: true);
            tot.Margin = new Thickness(12, 0, 0, 0);
            Grid.SetColumn(tot, 1);
            row.Children.Add(tot);
            item.Children.Add(row);
            if (l.Discount is { Length: > 0 })
                item.Children.Add(Text(l.Discount, 12, "BrushDanger").With(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            if (l.Returned is { Length: > 0 })
                item.Children.Add(Text(l.Returned, 12, "BrushWarning").With(t => t.HorizontalAlignment = HorizontalAlignment.Right));
            body.Children.Add(item);
        }
        body.Children.Add(Dash());

        foreach (var r in d.BeforeTotal) body.Children.Add(Pair(r.Label, r.Value, r.Accent));

        // ИТОГО — сразу после позиций/скидки, перед способом оплаты, внесено, сдачей и долгом (2026-10-24, аудит, круг 8, С-15).
        var total = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6) };
        var tl = Text(d.TotalLabel, 20, "BrushText", bold: true);
        tl.VerticalAlignment = VerticalAlignment.Center;
        total.Children.Add(tl);
        var tv = Text(d.TotalText, 22, "BrushText", bold: true);
        tv.Margin = new Thickness(12, 0, 0, 0);
        tv.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(tv, 1);
        total.Children.Add(tv);
        body.Children.Add(total);

        foreach (var r in d.Payment) body.Children.Add(Pair(r.Label, r.Value, r.Accent));

        // Возвраты — отдельным блоком: чек выше остаётся «как был продан».
        if (d.Returns.Count > 0)
        {
            body.Children.Add(Dash());
            body.Children.Add(Text(d.ReturnsCaption, 14, "BrushWarning", bold: true).With(t => t.Margin = new Thickness(0, 0, 0, 6)));
            foreach (var r in d.Returns) body.Children.Add(Pair(r.Label, r.Value));
        }

        if (d.OfflineNote.Length > 0)
            body.Children.Add(Text(d.OfflineNote, 11.5, "BrushWarning", center: true).With(t => t.Margin = new Thickness(0, 8, 0, 0)));

        if (d.QrUrl != null)
        {
            var qr = new ReceiptQrView { Data = d.QrUrl, Size = 132, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
            body.Children.Add(qr);
            body.Children.Add(Text(Tr.T("Электронный чек (PDF)", "Электрондук чек (PDF)", "E-receipt (PDF)", "E-fiş (PDF)", "Elektron chek (PDF)"), 11, "BrushTextSoft", center: true));
        }

        body.Children.Add(Text(Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışveriş için teşekkürler!", "Xaridingiz uchun rahmat!"),
            13, "BrushText", bold: true, center: true).With(t => t.Margin = new Thickness(0, 14, 0, 0)));
        body.Children.Add(Text("NurMarket", 10.5, "BrushTextSoft", center: true));

        var top = new Border
        {
            BorderThickness = new Thickness(0),
            Padding = new Thickness(20, 22, 20, 12),
            BoxShadow = BoxShadows.Parse("0 6 18 0 #22000000"),
            Child = body,
        };
        Res(top, Border.BackgroundProperty, "BrushPanel");

        // Зубчатый нижний край: зуб 10 px, число зубцов — по ширине карточки.
        var edge = new ReceiptZigzagEdge { Height = 10 };
        edge.Bind(ReceiptZigzagEdge.FillProperty, edge.GetResourceObservable("BrushPanel"));

        return new StackPanel { MaxWidth = maxWidth, Spacing = 0, Children = { top, edge } };
    }

    // Подпись слева (мягким), значение справа. accent: 3 — жирное, 1 — цвет долга, 2 — цвет скидки.
    private static Control Pair(string label, string value, int accent = 0)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 1.5) };
        g.Children.Add(Text(label, 13, "BrushTextSoft"));
        // Долг и «Вернуть до» — обычным цветом, как в мобилке (там цвет только у скидки); accent 1 остаётся в модели для печатных/текстовых видов.
        var v = Text(value, 13, accent == 2 ? "BrushDanger" : "BrushText", bold: accent == 3, semi: accent != 3);
        v.TextAlignment = TextAlignment.Right;
        v.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        return g;
    }

    private static Control Dash()
    {
        var line = new Line { StartPoint = new Point(0, 0.5), EndPoint = new Point(4000, 0.5), StrokeThickness = 1, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 3.5, 3.5 } };
        Res(line, Shape.StrokeProperty, "BrushBorderStrong");
        return new Canvas { Height = 1, ClipToBounds = true, Margin = new Thickness(0, 8), Children = { line } };
    }

    // Плашка статуса: Оплачен — зелёная, Долг — оранжевая, Возврат — серая (цвета темы).
    private static Border Badge(SaleReceiptCardData d)
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
            Child = Text(d.StatusText, 12, fg, bold: true),
        };
        Res(b, Border.BackgroundProperty, bg);
        Res(b, Border.BorderBrushProperty, fg);
        return b;
    }

    private static TextBlock Text(string s, double size, string brush, bool bold = false, bool center = false, bool semi = false)
    {
        var t = new TextBlock { Text = s, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.35 };
        if (bold) t.FontWeight = FontWeight.Bold;
        else if (semi) t.FontWeight = FontWeight.SemiBold;
        if (center) t.TextAlignment = TextAlignment.Center;
        Res(t, TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static void Res(Control c, AvaloniaProperty p, string key) => c.Bind(p, c.GetResourceObservable(key));

    private static T With<T>(this T t, System.Action<T> a) { a(t); return t; }
}

/// <summary>Нижний край чека зубцами, как у отрывной ленты (ZigzagClipper мобилки: зуб 10 px).</summary>
internal sealed class ReceiptZigzagEdge : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<ReceiptZigzagEdge, IBrush?>(nameof(Fill));

    static ReceiptZigzagEdge() => AffectsRender<ReceiptZigzagEdge>(FillProperty);

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0 || Fill is null)
            return;
        var teeth = System.Math.Max(1, (int)System.Math.Round(w / 10.0));
        var tw = w / teeth;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0), true);
            c.LineTo(new Point(w, 0));
            for (var i = teeth; i > 0; i--)
            {
                c.LineTo(new Point(i * tw - tw / 2, h));
                c.LineTo(new Point((i - 1) * tw, 0));
            }
            c.EndFigure(true);
        }
        context.DrawGeometry(Fill, null, g);
    }
}

/// <summary>QR как в мобилке: квадратные модули, цвет — как у текста чека на фоне бумаги (ZXing, без внешних картинок).</summary>
internal sealed class ReceiptQrView : Control
{
    public static readonly StyledProperty<string?> DataProperty = AvaloniaProperty.Register<ReceiptQrView, string?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> DarkProperty = AvaloniaProperty.Register<ReceiptQrView, IBrush?>(nameof(Dark));
    public static readonly StyledProperty<IBrush?> LightProperty = AvaloniaProperty.Register<ReceiptQrView, IBrush?>(nameof(Light));

    private ZXing.Common.BitMatrix? _matrix;

    static ReceiptQrView() => AffectsRender<ReceiptQrView>(DataProperty, DarkProperty, LightProperty);

    public string? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }

    /// <summary>Цвет модулей; не задан — текст темы.</summary>
    public IBrush? Dark { get => GetValue(DarkProperty); set => SetValue(DarkProperty, value); }

    /// <summary>Фон под QR; не задан — бумага чека.</summary>
    public IBrush? Light { get => GetValue(LightProperty); set => SetValue(LightProperty, value); }

    public double Size { get => Width; set { Width = value; Height = value; } }

    public ReceiptQrView()
    {
        this.Bind(DarkProperty, this.GetResourceObservable("BrushText"));
        this.Bind(LightProperty, this.GetResourceObservable("BrushPanel"));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty)
            _matrix = Encode(Data);
    }

    private static ZXing.Common.BitMatrix? Encode(string? data)
    {
        if (string.IsNullOrEmpty(data))
            return null;
        try
        {
            var hints = new System.Collections.Generic.Dictionary<ZXing.EncodeHintType, object>
            {
                [ZXing.EncodeHintType.CHARACTER_SET] = "UTF-8",
                [ZXing.EncodeHintType.MARGIN] = 0,
                [ZXing.EncodeHintType.ERROR_CORRECTION] = data.StartsWith("http", System.StringComparison.OrdinalIgnoreCase)
                    ? ZXing.QrCode.Internal.ErrorCorrectionLevel.M : ZXing.QrCode.Internal.ErrorCorrectionLevel.L,
            };
            return new ZXing.QrCode.QRCodeWriter().encode(data, ZXing.BarcodeFormat.QR_CODE, 0, 0, hints);
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    public override void Render(DrawingContext context)
    {
        var m = _matrix;
        var side = System.Math.Min(Bounds.Width, Bounds.Height);
        if (m is null || side <= 0)
            return;
        if (Light is { } light)
            context.DrawRectangle(light, null, new Rect(0, 0, side, side));
        if (Dark is not { } dark)
            return;
        var cell = side / m.Width;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (var y = 0; y < m.Height; y++)
            {
                for (var x = 0; x < m.Width; x++)
                {
                    if (!m[x, y])
                        continue;
                    c.BeginFigure(new Point(x * cell, y * cell), true);
                    c.LineTo(new Point((x + 1) * cell, y * cell));
                    c.LineTo(new Point((x + 1) * cell, (y + 1) * cell));
                    c.LineTo(new Point(x * cell, (y + 1) * cell));
                    c.EndFigure(true);
                }
            }
        }
        context.DrawGeometry(dark, null, g);
    }
}
