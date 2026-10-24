using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace NurMarketKassa.Services;

/// <summary>2026-10-21, владелец: «КП и товарную накладную, выставление счёта — тоже дизайн, пару вариантов подготовь». Бланки A4
/// документов клиента в трёх шаблонах:
///   • classic — строгая таблица, как бухгалтерский документ КР (для счёта — банковские реквизиты рамкой сверху);
///   • modern — фирменный цвет (акцент темы), логотип, крупный итог;
///   • minimal — чёрно-белый, тонкие линии без заливок (экономия тонера).
/// Один построитель (QuestPDF — уже в проекте, как предпросмотр чека) даёт и PDF, и картинки страниц для живого предпросмотра —
/// что видно в окне, то и печатается. Длинные названия переносятся; шапка таблицы повторяется на каждой странице, итог и
/// подписи — на последней, внизу «стр. N из M». Шрифт Arial (кириллица и ң, ө, ү), на Linux — DejaVu/Noto.</summary>
public static class ClientDocRenderer
{
    public const string Classic = "classic";
    public const string Modern = "modern";
    public const string Minimal = "minimal";
    // 2026-10-22, брендбук NUR CRM: четвёртый бланк — «Фирменный NUR CRM» (чёрная шапка со знаком, жёлтая шапка таблицы, полоса паттерна лучей).
    public const string Brand = "brand";
    public static readonly string[] Templates = { Classic, Modern, Minimal, Brand };

    private static readonly string[] FontStack = { "Arial", "Segoe UI", "DejaVu Sans", "Noto Sans", "Liberation Sans" };

    static ClientDocRenderer()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public sealed record Input(ClientDoc Doc, ClientDocProfile Seller, string Template, string AccentHex, byte[]? Logo);

    public static void SavePdf(Input input, string path) => Build(input).GeneratePdf(path);

    public static byte[] PdfBytes(Input input) => Build(input).GeneratePdf();

    /// <summary>Страницы картинками (PNG) — предпросмотр в окне и печать.</summary>
    public static List<byte[]> PageImages(Input input, int dpi = 96) =>
        Build(input).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = dpi }).ToList();

    public static string DefaultTemplate(ClientDocKind kind) => kind == ClientDocKind.Waybill ? Classic : Modern;

    // ───────────────────────────── подписи бланка (ru / ky) ─────────────────────────────

    private sealed class L
    {
        private readonly bool _ky;
        public L(string? lang) => _ky = string.Equals(lang, "ky", StringComparison.OrdinalIgnoreCase);
        public string T(string ru, string ky) => _ky ? ky : ru;
        public bool Ky => _ky;
    }

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private static string M(double v) => v.ToString("N2", Ru);
    private static string Q(double v) => v.ToString("0.###", Ru);

    // 2026-10-22 (закупки как в вебе): документ закупки (приходная накладная, заказ поставщику) рисуется тем же бланком накладной,
    // но со своим заголовком — Extra["title"] / Extra["title_ky"] документа. У документов клиентов этих полей нет — заголовок прежний.
    private static string Title(ClientDoc d, L l) =>
        d.Extra is { } x && x.TryGetValue(l.Ky ? "title_ky" : "title", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
        && t.GetString() is { Length: > 0 } custom ? custom : Title(d.Kind, l);

    private static string Title(ClientDocKind kind, L l) => kind switch
    {
        ClientDocKind.Quote => l.T("Коммерческое предложение", "Коммерциялык сунуш"),
        ClientDocKind.Invoice => l.T("Счёт на оплату", "Төлөм үчүн эсеп"),
        _ => l.T("Расходная накладная", "Чыгым накладнойу"),
    };

    private static string DateLong(DateTime d, L l)
    {
        if (l.Ky)
        {
            string[] months = { "январь", "февраль", "март", "апрель", "май", "июнь", "июль", "август", "сентябрь", "октябрь", "ноябрь", "декабрь" };
            return $"{d.Year}-ж. {d.Day}-{months[d.Month - 1]}";
        }
        return d.ToString("d MMMM yyyy", Ru) + " г.";
    }

    private static string NumberLine(ClientDoc d, L l) => l.Ky
        ? $"№ {d.Number}, {DateLong(d.Date, l)}"
        : $"№ {d.Number} от {DateLong(d.Date, l)}";

    private static string Term(ClientDoc d, L l) => d.Kind switch
    {
        ClientDocKind.Quote => l.T($"Предложение действует до {d.Until:dd.MM.yyyy}.", $"Сунуш {d.Until:dd.MM.yyyy} чейин жарактуу."),
        ClientDocKind.Invoice => l.T($"Оплатить до {d.Until:dd.MM.yyyy}. В назначении платежа укажите номер счёта.",
            $"{d.Until:dd.MM.yyyy} чейин төлөңүз. Төлөмдүн максатында эсептин номерин көрсөтүңүз."),
        // Накладная: основание уже в шапке — второй раз внизу не пишем.
        _ => "",
    };

    /// <summary>Основание для бланка: номер документа (как хранит телефон — «КП-0014») — с названием вида.</summary>
    public static string Basis(ClientDoc d, string? lang)
    {
        var b = (d.BasedOn ?? "").Trim();
        var ky = string.Equals(lang, "ky", StringComparison.OrdinalIgnoreCase);
        foreach (var kind in new[] { ClientDocKind.Quote, ClientDocKind.Invoice, ClientDocKind.Waybill })
            if (ClientDoc.NumberIndex(b, kind) != null)
                return kind switch
                {
                    ClientDocKind.Quote => ky ? "коммерциялык сунуш " : "коммерческое предложение ",
                    ClientDocKind.Invoice => ky ? "төлөм үчүн эсеп " : "счёт на оплату ",
                    _ => ky ? "накладной " : "накладная ",
                } + b;
        return b;
    }

    private static (string Left, string Right) Signs(ClientDocKind kind, L l) => kind == ClientDocKind.Waybill
        ? (l.T("Отпустил", "Берди"), l.T("Получил", "Алды"))
        : (l.T("Руководитель", "Жетекчи"), l.T("Бухгалтер", "Бухгалтер"));

    private static string SellerLine(ClientDocProfile s, L l) => string.Join(", ", new[]
    {
        s.Name, Join("ИНН ", s.Inn), s.Address, Join(l.T("тел. ", "тел. "), s.Phone),
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string BankLine(ClientDocProfile s, L l) => string.Join(", ", new[]
    {
        Join(l.T("р/с ", "эсеп ") , s.Account), s.Bank, Join("БИК ", s.Bik),
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string BuyerLine(ClientDoc d, L l) => string.Join(", ", new[]
    {
        d.Client, Join("ИНН ", d.Inn), d.Address, Join(l.T("тел. ", "тел. "), d.Phone),
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private static string? Join(string prefix, string? value) => string.IsNullOrWhiteSpace(value) ? null : prefix + value.Trim();

    // ───────────────────────────── цвета ─────────────────────────────

    private sealed record Palette(string Ink, string Soft, string Line, string HeadFill, string HeadText, string Accent, string AccentText, string Tint, string Zebra);

    private static Palette PaletteFor(string template, string accentHex)
    {
        var accent = NormalizeHex(accentHex, "#1F3A93");
        var accentText = IsLight(accent) ? "#111111" : "#FFFFFF";
        return template switch
        {
            Modern => new Palette("#1A1A1A", "#5F6368", "#E3E5E8", accent, accentText, accent, accentText, Mix(accent, "#FFFFFF", 0.88), "#F6F7F9"),
            Minimal => new Palette("#000000", "#555555", "#9A9A9A", "#FFFFFF", "#000000", "#000000", "#FFFFFF", "#FFFFFF", "#FFFFFF"),
            // Deep Night #191919 + Golden Light #FFB208: текст на жёлтом тёмный (9,7:1), шапка и итог — чёрные с белым текстом.
            Brand => new Palette("#191919", "#666666", "#DDDDDD", "#FFB208", "#191919", "#191919", "#FFFFFF", "#FFF1DB", "#F7F7F7"),
            _ => new Palette("#000000", "#333333", "#000000", "#E9E9E9", "#000000", "#000000", "#FFFFFF", "#F4F4F4", "#FFFFFF"),
        };
    }

    private static string NormalizeHex(string? hex, string fallback)
    {
        var h = (hex ?? "").Trim();
        if (h.StartsWith('#'))
            h = h[1..];
        if (h.Length == 8)
            h = h[2..];
        return h.Length == 6 && h.All(Uri.IsHexDigit) ? "#" + h.ToUpperInvariant() : fallback;
    }

    private static (int R, int G, int B) Rgb(string hex) =>
        (Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16), Convert.ToInt32(hex.Substring(5, 2), 16));

    private static bool IsLight(string hex)
    {
        var (r, g, b) = Rgb(hex);
        return 0.299 * r + 0.587 * g + 0.114 * b > 160;
    }

    private static string Mix(string a, string b, double t)
    {
        var (r1, g1, b1) = Rgb(a);
        var (r2, g2, b2) = Rgb(b);
        int C(int x, int y) => (int)Math.Round(x + (y - x) * t);
        return $"#{C(r1, r2):X2}{C(g1, g2):X2}{C(b1, b2):X2}";
    }

    // ───────────────────────────── документ ─────────────────────────────

    private static IDocument Build(Input input)
    {
        var d = input.Doc;
        var l = new L(d.Lang);
        var template = Templates.Contains(input.Template) ? input.Template : DefaultTemplate(d.Kind);
        var pal = PaletteFor(template, input.AccentHex);
        var minimal = template == Minimal;
        var modern = template == Modern || template == Brand;
        // Фирменный бланк строится как «современный» (таблица, итог, подписи) — другие цвета и шапка со знаком.
        var body = template == Brand ? Modern : template;
        var fontSize = minimal ? 9.5f : 10f;

        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginHorizontal(modern ? 14 : 16, Unit.Millimetre);
                page.MarginVertical(modern ? 12 : 14, Unit.Millimetre);
                page.PageColor("#FFFFFF");
                page.DefaultTextStyle(x => x.FontFamily(FontStack).FontSize(fontSize).FontColor(pal.Ink));

                page.Content().Column(col =>
                {
                    col.Spacing(minimal ? 6 : 8);
                    switch (template)
                    {
                        case Modern:
                        case Brand:
                            ModernHeader(col, input, l, pal, template == Brand);
                            break;
                        case Minimal:
                            MinimalHeader(col, input, l, pal);
                            break;
                        default:
                            ClassicHeader(col, input, l, pal);
                            break;
                    }
                    LinesTable(col, d, l, pal, body);
                    col.Item().ShowEntire().Column(end =>
                    {
                        end.Spacing(minimal ? 5 : 7);
                        Totals(end, d, l, pal, body);
                        var term = Term(d, l);
                        if (term.Length > 0)
                            end.Item().Text(term).FontSize(fontSize);
                        if (!string.IsNullOrWhiteSpace(d.Note))
                            end.Item().Text(t =>
                            {
                                t.Span(l.T("Примечание: ", "Эскертүү: ")).SemiBold();
                                t.Span(d.Note!.Trim());
                            });
                        Signatures(end, d, input.Seller, l, pal, body);
                    });
                });

                page.Footer().PaddingTop(6).Row(row =>
                {
                    row.RelativeItem().Text($"{Title(d, l)} {d.Number}").FontSize(8).FontColor(pal.Soft);
                    row.AutoItem().Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(8).FontColor(pal.Soft));
                        if (l.Ky)
                        {
                            t.CurrentPageNumber();
                            t.Span(" / ");
                            t.TotalPages();
                            t.Span(" бет");
                        }
                        else
                        {
                            t.Span("стр. ");
                            t.CurrentPageNumber();
                            t.Span(" из ");
                            t.TotalPages();
                        }
                    });
                });
            });
        });
    }

    // ── classic ──

    private static void ClassicHeader(ColumnDescriptor col, Input input, L l, Palette pal)
    {
        var d = input.Doc;
        var s = input.Seller;
        col.Item().Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text(s.Name ?? "").Bold().FontSize(12);
                var addr = string.Join(", ", new[] { Join("ИНН ", s.Inn), s.Address, Join(l.T("тел. ", "тел. "), s.Phone) }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (addr.Length > 0)
                    c.Item().Text(addr).FontSize(9);
            });
            if (input.Logo is { Length: > 0 } logo)
                row.ConstantItem(36, Unit.Millimetre).Height(16, Unit.Millimetre).AlignRight().Image(logo).FitArea();
        });

        if (d.Kind == ClientDocKind.Invoice)
        {
            // Банковские реквизиты рамкой — как в бухгалтерском счёте.
            col.Item().PaddingTop(4).Table(t =>
            {
                t.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3);
                    c.RelativeColumn(2);
                });
                IContainer Cell(IContainer x) => x.Border(0.7f).BorderColor(pal.Line).Padding(4);
                t.Cell().Element(Cell).Column(c =>
                {
                    c.Item().Text(l.T("Банк получателя", "Алуучунун банкы")).FontSize(8).FontColor(pal.Soft);
                    c.Item().Text(string.IsNullOrWhiteSpace(s.Bank) ? "—" : s.Bank!);
                });
                t.Cell().Element(Cell).Column(c =>
                {
                    c.Item().Text("БИК").FontSize(8).FontColor(pal.Soft);
                    c.Item().Text(string.IsNullOrWhiteSpace(s.Bik) ? "—" : s.Bik!);
                });
                t.Cell().Element(Cell).Column(c =>
                {
                    c.Item().Text(l.T("Получатель", "Алуучу")).FontSize(8).FontColor(pal.Soft);
                    c.Item().Text(string.Join(", ", new[] { s.Name, Join("ИНН ", s.Inn) }.Where(x => !string.IsNullOrWhiteSpace(x))));
                });
                t.Cell().Element(Cell).Column(c =>
                {
                    c.Item().Text(l.T("Расчётный счёт", "Эсептешүү эсеби")).FontSize(8).FontColor(pal.Soft);
                    c.Item().Text(string.IsNullOrWhiteSpace(s.Account) ? "—" : s.Account!);
                });
            });
        }

        col.Item().PaddingTop(8).AlignCenter().Text(Title(input.Doc, l) + " " + NumberLine(d, l)).Bold().FontSize(14);
        col.Item().LineHorizontal(1.2f).LineColor(pal.Ink);

        col.Item().Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(30, Unit.Millimetre);
                c.RelativeColumn();
            });
            void Party(string label, string value)
            {
                t.Cell().PaddingVertical(2).Text(label).SemiBold();
                t.Cell().PaddingVertical(2).Text(value.Length == 0 ? "____________________________" : value);
            }
            Party(d.Kind == ClientDocKind.Waybill ? l.T("Отправитель:", "Жөнөтүүчү:") : l.T("Поставщик:", "Жеткирүүчү:"), SellerLine(s, l));
            if (d.Kind != ClientDocKind.Invoice && BankLine(s, l) is { Length: > 0 } bank)
                Party(l.T("Реквизиты:", "Реквизиттер:"), bank);
            Party(d.Kind == ClientDocKind.Waybill ? l.T("Получатель:", "Алуучу:") : l.T("Покупатель:", "Сатып алуучу:"), BuyerLine(d, l));
            if (!string.IsNullOrWhiteSpace(d.BasedOn))
                Party(l.T("Основание:", "Негиз:"), Basis(d, d.Lang));
        });
    }

    // ── modern ──

    private static void ModernHeader(ColumnDescriptor col, Input input, L l, Palette pal, bool brand = false)
    {
        var d = input.Doc;
        var s = input.Seller;
        col.Item().Background(pal.Accent).Padding(10).Row(row =>
        {
            if (brand)
            {
                // Горизонтальный логотип NUR CRM с буквами для чёрного фона (брендбук 2.2, 3.2): ≥ 35–40 мм — здесь 40 × 16 мм.
                row.ConstantItem(40, Unit.Millimetre).Height(16, Unit.Millimetre).Svg(BrandLogoSvg.Build("horizontal", "dark"));
                row.ConstantItem(5, Unit.Millimetre);
            }
            if (input.Logo is { Length: > 0 } logo)
            {
                row.ConstantItem(22, Unit.Millimetre).Height(16, Unit.Millimetre).Background("#FFFFFF").Padding(2).AlignMiddle().AlignCenter().Image(logo).FitArea();
                row.ConstantItem(4, Unit.Millimetre);
            }
            row.RelativeItem().AlignMiddle().Column(c =>
            {
                c.Item().Text(s.Name ?? "").Bold().FontSize(14).FontColor(pal.AccentText);
                var sub = string.Join(" · ", new[] { Join("ИНН ", s.Inn), s.Phone }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (sub.Length > 0)
                    c.Item().Text(sub).FontSize(9).FontColor(pal.AccentText);
            });
            // Название документа — своей колонкой справа: длинное имя магазина переносится, а не наезжает на название.
            row.ConstantItem(4, Unit.Millimetre);
            row.ConstantItem(72, Unit.Millimetre).AlignMiddle().AlignRight().Column(c =>
            {
                c.Item().AlignRight().Text(Title(d, l)).Bold().FontSize(15).FontColor(pal.AccentText);
                c.Item().AlignRight().Text(NumberLine(d, l)).FontSize(9.5f).FontColor(pal.AccentText);
            });
        });

        if (brand)
        {
            // Полоса паттерна лучей (брендбук 6.1): чёрная, звёзды жёлтые вполсилы — умеренно, без перегруза.
            col.Item().Height(7, Unit.Millimetre).Background(BrandColors.DeepNight).Svg(BrandGeometry.PatternSvg(BrandColors.GoldenLight, 0.55, 26, 1));
        }

        col.Item().PaddingTop(4).Row(row =>
        {
            void Card(RowDescriptor r, string label, string body, string? extra)
            {
                r.RelativeItem().Background(pal.Tint).Padding(8).Column(c =>
                {
                    c.Item().Text(label).FontSize(8.5f).SemiBold().FontColor(pal.Soft);
                    c.Item().Text(body.Length == 0 ? "—" : body).SemiBold();
                    if (!string.IsNullOrWhiteSpace(extra))
                        c.Item().Text(extra).FontSize(9);
                });
            }
            Card(row, d.Kind == ClientDocKind.Waybill ? l.T("Отправитель", "Жөнөтүүчү") : l.T("От кого", "Кимден"), s.Name ?? "",
                string.Join("\n", new[] { s.Address, BankLine(s, l) }.Where(x => !string.IsNullOrWhiteSpace(x))));
            row.ConstantItem(4, Unit.Millimetre);
            Card(row, d.Kind == ClientDocKind.Waybill ? l.T("Получатель", "Алуучу") : l.T("Кому", "Кимге"), d.Client,
                string.Join("\n", new[] { Join("ИНН ", d.Inn), d.Address, d.Phone }.Where(x => !string.IsNullOrWhiteSpace(x))));
        });

        var meta = new List<(string, string)>
        {
            (l.T("Дата", "Күнү"), d.Date.ToString("dd.MM.yyyy", Ru)),
        };
        if (d.Kind == ClientDocKind.Quote)
            meta.Add((l.T("Действует до", "Жарактуу"), d.Until.ToString("dd.MM.yyyy", Ru)));
        else if (d.Kind == ClientDocKind.Invoice)
            meta.Add((l.T("Оплатить до", "Төлөө мөөнөтү"), d.Until.ToString("dd.MM.yyyy", Ru)));
        if (!string.IsNullOrWhiteSpace(d.BasedOn))
            meta.Add((l.T("Основание", "Негиз"), Basis(d, d.Lang)));
        col.Item().Row(row =>
        {
            foreach (var (k, v) in meta)
                row.AutoItem().PaddingRight(18).Text(t =>
                {
                    t.Span(k + ": ").FontColor(pal.Soft);
                    t.Span(v).SemiBold();
                });
        });
    }

    // ── minimal ──

    private static void MinimalHeader(ColumnDescriptor col, Input input, L l, Palette pal)
    {
        var d = input.Doc;
        var s = input.Seller;
        col.Item().Row(row =>
        {
            row.RelativeItem().Column(c =>
            {
                c.Item().Text(Title(d, l)).FontSize(16);
                c.Item().Text(NumberLine(d, l)).FontColor(pal.Soft);
            });
            row.RelativeItem().AlignRight().Column(c =>
            {
                c.Item().AlignRight().Text(s.Name ?? "").SemiBold();
                foreach (var line in new[] { Join("ИНН ", s.Inn), s.Address, s.Phone, BankLine(s, l) }.Where(x => !string.IsNullOrWhiteSpace(x)))
                    c.Item().AlignRight().Text(line!).FontSize(8.5f).FontColor(pal.Soft);
            });
        });
        col.Item().LineHorizontal(0.5f).LineColor(pal.Line);
        col.Item().Text(t =>
        {
            t.Span((d.Kind == ClientDocKind.Waybill ? l.T("Получатель: ", "Алуучу: ") : l.T("Покупатель: ", "Сатып алуучу: "))).FontColor(pal.Soft);
            var buyer = BuyerLine(d, l);
            t.Span(buyer.Length == 0 ? "____________________________" : buyer);
        });
        if (!string.IsNullOrWhiteSpace(d.BasedOn))
            col.Item().Text(t =>
            {
                t.Span(l.T("Основание: ", "Негиз: ")).FontColor(pal.Soft);
                t.Span(Basis(d, d.Lang));
            });
    }

    // ── таблица строк (шапка повторяется на каждой странице) ──

    private static void LinesTable(ColumnDescriptor col, ClientDoc d, L l, Palette pal, string template)
    {
        var showDiscount = d.Lines.Any(x => x.DiscountPercent is > 0);
        var minimal = template == Minimal;
        var modern = template == Modern;
        col.Item().PaddingTop(4).Table(t =>
        {
            t.ColumnsDefinition(c =>
            {
                c.ConstantColumn(9, Unit.Millimetre);
                c.RelativeColumn(6);
                c.ConstantColumn(13, Unit.Millimetre);
                c.ConstantColumn(17, Unit.Millimetre);
                c.ConstantColumn(23, Unit.Millimetre);
                if (showDiscount)
                    c.ConstantColumn(15, Unit.Millimetre);
                c.ConstantColumn(26, Unit.Millimetre);
            });

            IContainer Head(IContainer x)
            {
                x = template switch
                {
                    Modern => x.Background(pal.HeadFill).PaddingVertical(5).PaddingHorizontal(4),
                    Minimal => x.BorderBottom(0.8f).BorderColor(pal.Ink).PaddingVertical(4).PaddingHorizontal(3),
                    _ => x.Border(0.7f).BorderColor(pal.Line).Background(pal.HeadFill).PaddingVertical(4).PaddingHorizontal(3),
                };
                return x.DefaultTextStyle(s => s.SemiBold().FontSize(8.5f).FontColor(pal.HeadText));
            }

            t.Header(h =>
            {
                h.Cell().Element(Head).AlignCenter().Text("№");
                h.Cell().Element(Head).Text(l.T("Наименование", "Аталышы"));
                h.Cell().Element(Head).AlignCenter().Text(l.T("Ед.", "Бирд."));
                h.Cell().Element(Head).AlignRight().Text(l.T("Кол-во", "Саны"));
                h.Cell().Element(Head).AlignRight().Text(l.T("Цена", "Баасы"));
                if (showDiscount)
                    h.Cell().Element(Head).AlignRight().Text(l.T("Скидка", "Арзанд."));
                h.Cell().Element(Head).AlignRight().Text(l.T("Сумма", "Сумма"));
            });

            var i = 0;
            foreach (var line in d.Lines)
            {
                i++;
                var zebra = modern && i % 2 == 0;
                IContainer Cell(IContainer x)
                {
                    x = template switch
                    {
                        Modern => x.Background(zebra ? pal.Zebra : "#FFFFFF").BorderBottom(0.5f).BorderColor(pal.Line).PaddingVertical(4).PaddingHorizontal(4),
                        Minimal => x.BorderBottom(0.4f).BorderColor(pal.Line).PaddingVertical(3).PaddingHorizontal(3),
                        _ => x.Border(0.7f).BorderColor(pal.Line).PaddingVertical(3).PaddingHorizontal(3),
                    };
                    return x;
                }
                t.Cell().Element(Cell).AlignCenter().Text(i.ToString(CultureInfo.InvariantCulture));
                t.Cell().Element(Cell).Text(line.Name.Length == 0 ? "—" : line.Name);
                t.Cell().Element(Cell).AlignCenter().Text(line.Unit);
                t.Cell().Element(Cell).AlignRight().Text(Q(line.Qty));
                t.Cell().Element(Cell).AlignRight().Text(M(line.BasePrice));
                if (showDiscount)
                    t.Cell().Element(Cell).AlignRight().Text(line.DiscountPercent is > 0 and var dp ? dp.ToString("0.##", Ru) + "%" : "—");
                t.Cell().Element(Cell).AlignRight().Text(M(line.Sum));
            }
            if (d.Lines.Count == 0)
            {
                var span = (uint)(showDiscount ? 7 : 6);
                t.Cell().ColumnSpan(span).Element(x => minimal ? x.BorderBottom(0.4f).BorderColor(pal.Line).Padding(6) : x.Border(0.7f).BorderColor(pal.Line).Padding(6))
                    .AlignCenter().Text(l.T("Товары не добавлены", "Товарлар кошула элек")).FontColor(pal.Soft);
            }
        });
    }

    // ── итоги ──

    private static void Totals(ColumnDescriptor col, ClientDoc d, L l, Palette pal, string template)
    {
        var rows = new List<(string Label, string Value, bool Bold)>();
        var discount = Math.Max(0, d.Discount);
        if (discount > 0.004)
        {
            rows.Add((l.T("Итого", "Жыйынтык"), M(d.Subtotal), false));
            rows.Add((l.T("Скидка", "Арзандатуу"), "−" + M(Math.Min(discount, d.Subtotal)), false));
        }
        if (d.VatPercent is > 0 and var vat)
            rows.Add((l.T($"В том числе НДС {vat:0.##}%", $"Анын ичинде КНС {vat:0.##}%"), M(d.VatAmount), false));
        else
            rows.Add((l.T("Без НДС", "КНСсиз"), "—", false));
        if (d.Kind == ClientDocKind.Invoice && d.Paid > 0.004)
        {
            rows.Add((l.T("Оплачено", "Төлөндү"), M(d.Paid), false));
            rows.Add((l.T("Осталось оплатить", "Төлөнө турган калдык"), M(d.Left), false));
        }
        var dueLabel = d.Kind switch
        {
            ClientDocKind.Invoice => l.T("Всего к оплате", "Төлөөгө бардыгы"),
            ClientDocKind.Quote => l.T("Сумма предложения", "Сунуштун суммасы"),
            _ => l.T("Всего по накладной", "Накладной боюнча бардыгы"),
        };

        if (template == Modern)
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().PaddingRight(10).Column(c =>
                {
                    foreach (var r in rows)
                        c.Item().Row(rr =>
                        {
                            rr.RelativeItem().AlignRight().Text(r.Label + ":").FontColor(pal.Soft);
                            rr.ConstantItem(30, Unit.Millimetre).AlignRight().Text(r.Value);
                        });
                });
                row.ConstantItem(64, Unit.Millimetre).Background(pal.Accent).Padding(8).Column(c =>
                {
                    c.Item().AlignRight().Text(dueLabel).FontSize(9).FontColor(pal.AccentText);
                    c.Item().AlignRight().Text(M(d.Total) + " " + l.T("сом", "сом")).Bold().FontSize(17).FontColor(pal.AccentText);
                });
            });
        }
        else
        {
            col.Item().AlignRight().Width(95, Unit.Millimetre).Column(c =>
            {
                foreach (var r in rows)
                    c.Item().Row(rr =>
                    {
                        rr.RelativeItem().AlignRight().Text(r.Label + ":");
                        rr.ConstantItem(30, Unit.Millimetre).AlignRight().Text(r.Value);
                    });
                c.Item().PaddingTop(2).Row(rr =>
                {
                    rr.RelativeItem().AlignRight().Text(dueLabel + ":").Bold();
                    rr.ConstantItem(30, Unit.Millimetre).AlignRight().Text(M(d.Total)).Bold();
                });
            });
        }

        col.Item().Text(l.T($"Всего наименований {d.Lines.Count}, на сумму {M(d.Total)} сом.",
            $"Бардыгы {d.Lines.Count} аталыш, {M(d.Total)} сом.")).FontSize(9);
        col.Item().Text(MoneyWords.Amount(d.Total, d.Lang)).Bold();
        if (template != Minimal)
            col.Item().LineHorizontal(template == Modern ? 0.6f : 1f).LineColor(template == Modern ? pal.Line : pal.Ink);
    }

    // ── подписи и М.П. ──

    private static void Signatures(ColumnDescriptor col, ClientDoc d, ClientDocProfile s, L l, Palette pal, string template)
    {
        var (left, right) = Signs(d.Kind, l);
        var leftName = d.Kind == ClientDocKind.Waybill ? (string.IsNullOrWhiteSpace(d.Author) ? s.Director : d.Author) : s.Director;
        var rightName = d.Kind == ClientDocKind.Waybill ? null : s.Accountant;
        col.Item().PaddingTop(template == Minimal ? 10 : 16).Row(row =>
        {
            void Sign(RowDescriptor r, string label, string? name)
            {
                r.RelativeItem().Column(c =>
                {
                    c.Item().Row(rr =>
                    {
                        rr.AutoItem().AlignBottom().Text(label + ":").SemiBold();
                        rr.RelativeItem().PaddingLeft(6).PaddingBottom(2).AlignBottom().BorderBottom(0.6f).BorderColor(pal.Ink).Height(14);
                    });
                    c.Item().PaddingLeft(28, Unit.Millimetre).Text(string.IsNullOrWhiteSpace(name) ? l.T("(подпись, Ф.И.О.)", "(колу, аты-жөнү)") : "/" + name!.Trim() + "/")
                        .FontSize(8).FontColor(pal.Soft);
                });
            }
            Sign(row, left, leftName);
            row.ConstantItem(12, Unit.Millimetre);
            Sign(row, right, rightName);
        });
        if (s.ShowStamp)
            col.Item().PaddingTop(10).PaddingLeft(10, Unit.Millimetre).Width(26, Unit.Millimetre).Height(26, Unit.Millimetre)
                .Border(template == Minimal ? 0.4f : 0.6f).BorderColor(template == Modern ? pal.Line : pal.Soft)
                .AlignMiddle().AlignCenter().Text(l.T("М.П.", "М.О.")).FontSize(9).FontColor(pal.Soft);
    }
}
