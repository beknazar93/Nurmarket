using System;
using System.IO;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>2026-10-27, владелец: «вид чека в десктопе как в мобилке» — кнопка «PDF» на чеке. Тот же вид, что на экране и в receipt_pdf.dart
/// мобилки: лента 80 мм без полей снизу (высота по содержимому), шапка магазина, плашка вида документа, пары «подпись — значение»,
/// позиции, «ИТОГО» крупно, оплата, подвал. Ничего не печатает на принтер — только пишет файл PDF (проверка и «Отправить»).</summary>
public static class ReceiptPaperPdf
{
    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };
    private const string Ink = "#1B2330", Soft = "#6B7686", RuleColor = "#B0B7C3", Red = "#B42318", Warn = "#B54708";

    static ReceiptPaperPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static void Create(SaleReceiptCardData d, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.ContinuousSize(80, Unit.Millimetre);
                page.Margin(4, Unit.Millimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(8.5f).FontColor(Ink));

                page.Content().Column(col =>
                {
                    col.Spacing(1.5f);
                    col.Item().AlignCenter().Text(d.Shop).FontSize(11.5f).Bold();
                    if (d.Inn.Length > 0)
                        col.Item().AlignCenter().Text(Tr.T("ИНН ", "ИНН ", "TIN ", "VKN ", "STIR ") + d.Inn).FontSize(7.5f).FontColor(Soft);
                    if (d.Address.Length > 0)
                        col.Item().AlignCenter().Text(d.Address).FontSize(7.5f).FontColor(Soft);
                    col.Item().PaddingVertical(3).AlignCenter().Background("#EEF1F5").PaddingHorizontal(8).PaddingVertical(2)
                        .Text(d.Title).FontSize(7.5f).Bold();

                    Pair(col, Tr.T("Чек №", "Чек №", "Receipt №", "Fiş №", "Chek №"), d.NumberText, bold: true);
                    if (d.Shift.Length > 0) Pair(col, Tr.T("Смена №", "Смена №", "Shift №", "Vardiya №", "Smena №"), d.Shift);
                    if (d.DateTimeText.Length > 0) Pair(col, Tr.T("Дата и время", "Күнү жана убактысы", "Date and time", "Tarih ve saat", "Sana va vaqt"), d.DateTimeText);
                    if (d.Cashier.Length > 0) Pair(col, Tr.T("Кассир", "Кассир", "Cashier", "Kasiyer", "Kassir"), d.Cashier);
                    if (d.Cashbox.Length > 0) Pair(col, Tr.T("Касса", "Касса", "Till", "Kasa", "Kassa"), d.Cashbox);
                    if (d.Client.Length > 0) Pair(col, Tr.T("Покупатель", "Сатып алуучу", "Customer", "Müşteri", "Xaridor"), d.Client);
                    if (d.Consultant.Length > 0) Pair(col, Tr.T("Консультант", "Консультант", "Consultant", "Danışman", "Maslahatchi"), d.Consultant);
                    Rule(col);

                    var n = 0;
                    foreach (var l in d.Lines)
                    {
                        n++;
                        col.Item().PaddingBottom(2).ShowEntire().Column(c =>
                        {
                            c.Item().Text($"{n}. {l.Name}").SemiBold();
                            c.Item().Row(r =>
                            {
                                r.RelativeItem().Text(l.QtyPrice).FontColor(Soft);
                                r.AutoItem().Text(l.Total).Bold();
                            });
                            if (l.Discount is { Length: > 0 })
                                c.Item().AlignRight().Text(l.Discount).FontSize(7.5f).FontColor(Red);
                            if (l.Returned is { Length: > 0 })
                                c.Item().AlignRight().Text(l.Returned).FontSize(7.5f).FontColor(Warn);
                        });
                    }
                    Rule(col);

                    foreach (var r in d.BeforeTotal) Pair(col, r.Label, r.Value, color: r.Accent == 2 ? Red : null);
                    col.Item().PaddingVertical(3).Row(r =>
                    {
                        r.RelativeItem().Text(d.TotalLabel).FontSize(12).Bold();
                        r.AutoItem().Text(d.TotalText).FontSize(13).Bold();
                    });
                    foreach (var r in d.Payment)
                        Pair(col, r.Label, r.Value, bold: r.Accent == 3, color: r.Accent == 1 ? Warn : null);

                    if (d.Returns.Count > 0)
                    {
                        Rule(col);
                        col.Item().Text(d.ReturnsCaption).Bold().FontColor(Warn);
                        foreach (var r in d.Returns)
                            Pair(col, r.Label.Trim(), r.Value);
                    }
                    if (d.OfflineNote.Length > 0)
                        col.Item().PaddingTop(3).AlignCenter().Text(d.OfflineNote).FontSize(7).FontColor(Warn);
                    if (d.QrUrl != null)
                        col.Item().PaddingTop(3).AlignCenter().Text(d.QrUrl).FontSize(6.5f).FontColor(Soft);

                    col.Item().PaddingTop(6).AlignCenter().Text(Tr.T("Спасибо за покупку!", "Сатып алганыңыз үчүн рахмат!", "Thank you for your purchase!", "Alışveriş için teşekkürler!", "Xaridingiz uchun rahmat!")).Bold();
                    col.Item().AlignCenter().Text("NurCRM").FontSize(7).FontColor(Soft);
                });
            });
        }).GeneratePdf(path);
    }

    private static void Pair(ColumnDescriptor col, string label, string value, bool bold = false, string? color = null)
    {
        col.Item().Row(r =>
        {
            r.AutoItem().PaddingRight(6).Text(label).FontColor(Soft);
            var t = r.RelativeItem().AlignRight().Text(value);
            if (bold) t.Bold(); else t.SemiBold();
            if (color != null) t.FontColor(color);
        });
    }

    private static void Rule(ColumnDescriptor col) =>
        col.Item().PaddingVertical(3).LineHorizontal(0.5f).LineColor(RuleColor);
}
