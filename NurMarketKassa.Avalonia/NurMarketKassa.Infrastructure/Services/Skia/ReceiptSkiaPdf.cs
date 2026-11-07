using SkiaSharp;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (В18, А5-04): PDF чека на SkiaSharp (SKDocument) для Android — там нативная библиотека QuestPDF (glibc) не загружается, и PDF не создавался.
/// Вид тот же, что у <see cref="ReceiptPaperPdf"/> (QuestPDF): лента 80 мм, поля 4 мм, высота по содержимому, шапка магазина, плашка вида документа, пары
/// «подпись — значение», позиции, «ИТОГО» крупно, оплата, возвраты, подвал. Ничего не печатает на принтер — только пишет файл PDF. На Windows и Linux остаётся QuestPDF.</summary>
public static class ReceiptSkiaPdf
{
    private const float MmToPt = 72f / 25.4f;
    private const uint Ink = 0xFF1B2330, Soft = 0xFF6B7686, RuleColor = 0xFFB0B7C3, Red = 0xFFB42318, Warn = 0xFFB54708, Chip = 0xFFEEF1F5;

    private static SkText T(float pt, bool bold = false, uint color = Ink) => new("Arial", pt, bold, false, false, false, color);

    public static void Create(SaleReceiptCardData d, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Render(d));
    }

    /// <summary>Байты PDF чека (одна страница шириной 80 мм).</summary>
    public static byte[] Render(SaleReceiptCardData d)
    {
        var pageW = 80f * MmToPt;
        var margin = 4f * MmToPt;
        var w = pageW - 2 * margin;
        var ops = new List<Action<SKCanvas>>();
        var y = margin;
        const float spacing = 1.5f;

        void Gap(float h) => y += h;

        void Center(string text, SkText st, float padTop = 0)
        {
            y += padTop;
            foreach (var line in SkiaText.Wrap(text, w, st))
            {
                var top = y;
                var x = margin + (w - SkiaText.Measure(line, st)) / 2f;
                ops.Add(c => SkiaText.Draw(c, line, x, top, st));
                y += SkiaText.LineHeight(st);
            }
            y += spacing;
        }

        void Pair(string label, string value, bool bold = false, uint? color = null)
        {
            var ls = T(8.5f, false, Soft);
            var vs = T(8.5f, true, color ?? Ink);
            _ = bold;
            var lw = SkiaText.Measure(label, ls) + 6;
            var lines = SkiaText.Wrap(value, Math.Max(w - lw, 20), vs);
            var top = y;
            ops.Add(c => SkiaText.Draw(c, label, margin, top, ls));
            var yy = y;
            foreach (var line in lines)
            {
                var t0 = yy;
                var x = margin + w - SkiaText.Measure(line, vs);
                ops.Add(c => SkiaText.Draw(c, line, x, t0, vs));
                yy += SkiaText.LineHeight(vs);
            }
            y = Math.Max(yy, y + SkiaText.LineHeight(ls)) + spacing;
        }

        void Rule()
        {
            y += 3;
            var yy = y;
            ops.Add(c =>
            {
                using var p = new SKPaint { Color = new SKColor(RuleColor), StrokeWidth = 0.5f, IsAntialias = true };
                c.DrawLine(margin, yy, margin + w, yy, p);
            });
            y += 0.5f + 3 + spacing;
        }

        void RowLeftRight(string left, string right, SkText ls, SkText rs)
        {
            var rw = SkiaText.Measure(right, rs);
            var lines = SkiaText.Wrap(left, Math.Max(w - rw - 6, 20), ls);
            var top = y;
            var yy = y;
            foreach (var line in lines)
            {
                var t0 = yy;
                ops.Add(c => SkiaText.Draw(c, line, margin, t0, ls));
                yy += SkiaText.LineHeight(ls);
            }
            ops.Add(c => SkiaText.Draw(c, right, margin + w - rw, top, rs));
            y = Math.Max(yy, top + SkiaText.LineHeight(rs));
        }

        // Шапка
        Center(d.Shop, T(11.5f, true));
        if (d.Inn.Length > 0)
            Center(Tr.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ") + d.Inn, T(7.5f, false, Soft));
        if (d.Address.Length > 0)
            Center(d.Address, T(7.5f, false, Soft));
        {
            // Плашка вида документа: фон #EEF1F5, отступы 8/2.
            y += 3;
            var st = T(7.5f, true);
            var tw = SkiaText.Measure(d.Title, st);
            var bw = Math.Min(tw + 16, w);
            var bh = SkiaText.LineHeight(st) + 4;
            var bx = margin + (w - bw) / 2f;
            var by = y;
            ops.Add(c =>
            {
                using var p = new SKPaint { Color = new SKColor(Chip), IsAntialias = true };
                c.DrawRect(new SKRect(bx, by, bx + bw, by + bh), p);
                SkiaText.Draw(c, SkiaText.Truncate(d.Title, bw - 16, st), bx + (bw - Math.Min(tw, bw - 16)) / 2f, by + 2, st);
            });
            y += bh + 3 + spacing;
        }

        Pair(Tr.T("Чек №", "Чек №", "Receipt №", "Fiş №", "Chek №"), d.NumberText, bold: true);
        if (d.Shift.Length > 0) Pair(Tr.T("Смена №", "Смена №", "Shift №", "Vardiya №", "Smena №"), d.Shift);
        if (d.DateTimeText.Length > 0) Pair(Tr.T("Дата и время", "Күнү жана убактысы", "Date and time", "Tarih ve saat", "Sana va vaqt"), d.DateTimeText);
        if (d.Cashier.Length > 0) Pair(Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"), d.Cashier);
        if (d.Cashbox.Length > 0) Pair(Tr.T("Касса", "Касса", "Till", "Kasa", "Kassa"), d.Cashbox);
        if (d.Client.Length > 0) Pair(Tr.T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor"), d.Client);
        if (d.Consultant.Length > 0) Pair(Tr.T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), d.Consultant);
        Rule();

        var n = 0;
        foreach (var l in d.Lines)
        {
            n++;
            var nameStyle = T(8.5f, true);
            var top = y;
            foreach (var line in SkiaText.Wrap($"{n}. {l.Name}", w, nameStyle))
            {
                var t0 = y;
                ops.Add(c => SkiaText.Draw(c, line, margin, t0, nameStyle));
                y += SkiaText.LineHeight(nameStyle);
            }
            _ = top;
            RowLeftRight(l.QtyPrice, l.Total, T(8.5f, false, Soft), T(8.5f, true));
            if (l.Discount is { Length: > 0 })
            {
                var st = T(7.5f, false, Red);
                var t0 = y;
                var x = margin + w - SkiaText.Measure(l.Discount, st);
                ops.Add(c => SkiaText.Draw(c, l.Discount, x, t0, st));
                y += SkiaText.LineHeight(st);
            }
            if (l.Returned is { Length: > 0 })
            {
                var st = T(7.5f, false, Warn);
                var t0 = y;
                var x = margin + w - SkiaText.Measure(l.Returned, st);
                ops.Add(c => SkiaText.Draw(c, l.Returned, x, t0, st));
                y += SkiaText.LineHeight(st);
            }
            y += 2 + spacing;
        }
        Rule();

        foreach (var r in d.BeforeTotal)
            Pair(r.Label, r.Value, color: r.Accent == 2 ? Red : null);
        y += 3;
        RowLeftRight(d.TotalLabel, d.TotalText, T(12, true), T(13, true));
        y += 3 + spacing;
        foreach (var r in d.Payment)
            Pair(r.Label, r.Value, bold: r.Accent == 3, color: r.Accent == 1 ? Warn : null);

        if (d.Returns.Count > 0)
        {
            Rule();
            var cap = T(8.5f, true, Warn);
            var t0 = y;
            ops.Add(c => SkiaText.Draw(c, d.ReturnsCaption, margin, t0, cap));
            y += SkiaText.LineHeight(cap) + spacing;
            foreach (var r in d.Returns)
                Pair(r.Label.Trim(), r.Value);
        }
        if (d.OfflineNote.Length > 0)
            Center(d.OfflineNote, T(7f, false, Warn), 3);
        if (d.QrUrl != null)
            Center(d.QrUrl, T(6.5f, false, Soft), 3);

        Center(Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışveriş için teşekkürler!", "Xaridingiz uchun rahmat!"), T(8.5f, true), 6);
        Center("NurCRM", T(7f, false, Soft));

        var pageH = y + margin;
        using var stream = new SKDynamicMemoryWStream();
        using (var doc = SKDocument.CreatePdf(stream))
        {
            var canvas = doc.BeginPage(pageW, pageH);
            canvas.Clear(SKColors.White);
            foreach (var op in ops)
                op(canvas);
            doc.EndPage();
            doc.Close();
        }
        using var data = stream.DetachAsData();
        return data.ToArray();
    }
}
