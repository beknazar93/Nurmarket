using System.Globalization;
using System.Text;
using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-11-19 («Смета», ТЗ 4): печатные и текстовые формы сметы — текст для WhatsApp/Telegram («Скопировать текстом»), лента 58/80 мм (через принтер чеков кассы) и
/// документ A4 (тот же бланк, что КП «Клиентов»: <see cref="ClientDocRenderer"/>, заголовок «Смета»). Закупка и маржа в клиентские формы НЕ попадают никогда (<see cref="InternalSummary"/> — только для владельца).</summary>
public static class EstimateText
{
    private static string Day(DateTime d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static string Qty(decimal v) => MoneyText.Weight(v, "0.##");

    public static string Title(EstimateDoc doc) => Tr.T("Смета", "Смета", "Estimate", "Keşif", "Smeta") + " " + EstimateRules.Code(doc.Number)
        + (string.IsNullOrWhiteSpace(doc.Title) ? "" : ": " + doc.Title.Trim());

    /// <summary>Строка позиции: «Краска белая — 4 банка × 1 250,00 = 5 000,00 сом» (скидка строки — в скобках).</summary>
    public static string LineText(EstimateLine line, EstimateDoc doc)
    {
        var qty = EstimateRules.EffectiveQty(line, doc);
        var disc = line.DiscountPercent > 0m ? " (−" + Qty(line.DiscountPercent) + " %)" : "";
        return $"{line.Name.Trim()} — {Qty(qty)} {line.Unit} × {MoneyText.Amount(EstimateRules.NetPrice(line))}{disc} = {MoneyText.Som(EstimateRules.LineSum(line, doc))}";
    }

    /// <summary>Группы для печати: сначала позиции без раздела, затем разделы по порядку.</summary>
    public static IEnumerable<(string? Name, string? SectionId, List<EstimateLine> Lines)> Groups(EstimateDoc doc)
    {
        var loose = doc.Lines.Where(l => string.IsNullOrWhiteSpace(l.SectionId) || doc.Sections.All(s => s.Id != l.SectionId)).ToList();
        if (loose.Count > 0)
            yield return (null, null, loose);
        foreach (var s in doc.Sections)
        {
            var lines = doc.Lines.Where(l => l.SectionId == s.Id).ToList();
            if (lines.Count > 0)
                yield return (s.Name, s.Id, lines);
        }
    }

    /// <summary>Готовый текст для WhatsApp/Telegram: шапка, разделы со списком, скидка, итог, срок. Без закупки и маржи.</summary>
    public static string Message(EstimateDoc doc, string? shopName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title(doc));
        if (!string.IsNullOrWhiteSpace(shopName))
            sb.AppendLine(shopName.Trim());
        if (!string.IsNullOrWhiteSpace(doc.ClientName))
            sb.AppendLine(Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz") + ": " + doc.ClientName.Trim());
        if (!string.IsNullOrWhiteSpace(doc.Address))
            sb.AppendLine(Tr.T("Объект", "Объект", "Site", "Şantiye", "Obyekt") + ": " + doc.Address.Trim());
        sb.AppendLine();
        var n = 0;
        foreach (var (name, _, lines) in Groups(doc))
        {
            if (name is not null)
                sb.AppendLine(name.Trim());
            foreach (var l in lines)
                sb.AppendLine($"{++n}. {LineText(l, doc)}");
        }
        sb.AppendLine();
        if (doc.DiscountPercent > 0m)
            sb.AppendLine(Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma") + $" {Qty(doc.DiscountPercent)} %: −{MoneyText.Som(EstimateRules.DiscountAmount(doc))}");
        sb.AppendLine(Tr.T("Итого", "Жыйынтыгы", "Total", "Toplam", "Jami") + ": " + MoneyText.Som(EstimateRules.Total(doc)));
        sb.AppendLine(Tr.T("Цены действуют до", "Баалар чейин жарактуу", "Prices valid until", "Fiyatlar şu tarihe kadar geçerli", "Narxlar quyidagi sanagacha amal qiladi") + " " + Day(doc.ValidUntil));
        if (!string.IsNullOrWhiteSpace(doc.Note))
            sb.AppendLine().AppendLine(doc.Note.Trim());
        return sb.ToString().TrimEnd();
    }

    /// <summary>Только для владельца и сотрудников с правом на закупку: себестоимость и маржа сметы. Если у части товаров закупка неизвестна — помечено.</summary>
    public static string InternalSummary(EstimateDoc doc)
    {
        var text = Tr.T("Себестоимость", "Өздүк нарк", "Cost", "Maliyet", "Tannarx") + ": " + MoneyText.Som(EstimateRules.Cost(doc)) + " · "
            + Tr.T("Маржа", "Маржа", "Margin", "Marj", "Marja") + ": " + MoneyText.Som(EstimateRules.Margin(doc)) + $" ({Qty(EstimateRules.MarginPercent(doc))} %)";
        if (!EstimateRules.CostKnown(doc))
            text += " · " + Tr.T("у части товаров нет закупочной цены", "кээ бир товарлардын сатып алуу баасы жок", "some products have no purchase price", "bazı ürünlerin alış fiyatı yok", "ba'zi mahsulotlarning xarid narxi yo'q");
        return text;
    }

    /// <summary>Лента 58/80 мм: шапка магазина, список позиций («кол-во × цена» и сумма справа), скидка, итог, срок. Ширина — в знаках (ReceiptLayout.CharWidth).</summary>
    public static string Receipt(EstimateDoc doc, int width)
    {
        var prefs = UserPreferences.Instance;
        var sb = new StringBuilder(600);
        sb.Append(ReceiptLineLayout.Center(Tr.T("СМЕТА", "СМЕТА", "ESTIMATE", "KEŞİF", "SMETA"), width)).Append('\n').Append('\n');
        if (!string.IsNullOrWhiteSpace(prefs.StoreName))
            foreach (var p in ReceiptLineLayout.WrapLeft(prefs.StoreName.Trim(), width))
                sb.Append(p).Append('\n');
        sb.Append(EstimateRules.Code(doc.Number)).Append("  ").Append(Day(doc.CreatedAt)).Append('\n');
        foreach (var p in ReceiptLineLayout.WrapLeft(doc.Title.Trim(), width).Where(_ => !string.IsNullOrWhiteSpace(doc.Title)))
            sb.Append(p).Append('\n');
        if (!string.IsNullOrWhiteSpace(doc.ClientName))
            foreach (var p in ReceiptLineLayout.WrapLeft(Tr.T("Клиент", "Кардар", "Customer", "Müşteri", "Mijoz") + ": " + doc.ClientName.Trim(), width))
                sb.Append(p).Append('\n');
        sb.Append(new string('-', width)).Append('\n');
        var n = 0;
        foreach (var (name, _, lines) in Groups(doc))
        {
            if (name is not null)
                sb.Append(ReceiptLineLayout.Center(name.Trim(), width)).Append('\n');
            foreach (var l in lines)
            {
                foreach (var p in ReceiptLineLayout.WrapLeft($"{++n}. {l.Name.Trim()}", width))
                    sb.Append(p).Append('\n');
                var qty = EstimateRules.EffectiveQty(l, doc);
                foreach (var p in ReceiptLineLayout.FormatItemBlock($"{Qty(qty)} {l.Unit} x {MoneyText.Amount(EstimateRules.NetPrice(l))}", MoneyText.Amount(EstimateRules.LineSum(l, doc)), width))
                    sb.Append(p).Append('\n');
            }
        }
        sb.Append(new string('-', width)).Append('\n');
        if (doc.DiscountPercent > 0m)
            sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("Скидка", "Арзандатуу", "Discount", "İndirim", "Chegirma") + $" {Qty(doc.DiscountPercent)}%", "-" + MoneyText.Som(EstimateRules.DiscountAmount(doc)), width)).Append('\n');
        sb.Append(ReceiptLineLayout.FormatLabelAmount(Tr.T("ИТОГО", "ЖЫЙЫНТЫК", "TOTAL", "TOPLAM", "JAMI"), MoneyText.Som(EstimateRules.Total(doc)), width)).Append('\n');
        sb.Append(Tr.T("Действует до", "Чейин жарактуу", "Valid until", "Geçerlilik", "Amal qiladi")).Append(": ").Append(Day(doc.ValidUntil)).Append('\n');
        return sb.ToString();
    }

    public static string Receipt(EstimateDoc doc) => Receipt(doc, ReceiptLayout.CharWidth);

    /// <summary>A4: смета как КП с заголовком «Смета» (бланк «Клиентов»). Строки — по разделам; название раздела — приставка к названию позиции. Цена строки — после скидки строки,
    /// запас уже в количестве, общая скидка — суммой. Закупки в документе нет.</summary>
    public static ClientDoc ToClientDoc(EstimateDoc doc)
    {
        var ky = UserPreferences.Instance.Language == AppLanguage.Kyrgyz;
        var cd = new ClientDoc
        {
            Kind = ClientDocKind.Quote,
            Number = EstimateRules.Code(doc.Number),
            Client = doc.ClientName,
            ClientId = doc.ClientId,
            Phone = doc.Phone,
            Address = string.IsNullOrWhiteSpace(doc.Address) ? doc.Title : doc.Title + (string.IsNullOrWhiteSpace(doc.Title) ? "" : ", ") + doc.Address,
            Status = doc.Status == EstimateStatus.Accepted ? "accepted" : doc.Status == EstimateStatus.Sent ? "sent" : "draft",
            Note = doc.Note,
            Lang = ky ? "ky" : "ru",
            Discount = (double)EstimateRules.DiscountAmount(doc),
        };
        cd.Date = doc.CreatedAt;
        cd.Until = doc.ValidUntil;
        cd.Extra = new Dictionary<string, JsonElement>
        {
            ["title"] = JsonSerializer.SerializeToElement("Смета"),
            ["title_ky"] = JsonSerializer.SerializeToElement("Смета"),
        };
        foreach (var (name, _, lines) in Groups(doc))
            foreach (var l in lines)
            {
                var row = new ClientDocLine
                {
                    ProductId = l.ProductId ?? "",
                    Name = (name is null ? "" : name.Trim() + " · ") + l.Name.Trim(),
                    Unit = string.IsNullOrWhiteSpace(l.Unit) ? "шт" : l.Unit,
                    Qty = (double)EstimateRules.EffectiveQty(l, doc),
                    Free = l.IsCustom ? true : null,
                };
                row.SetPrice((double)l.Price, (double)l.DiscountPercent);
                cd.Lines.Add(row);
            }
        return cd;
    }
}

/// <summary>2026-11-19: доступность «Сметы» — ОДНО место (ТЗ 1: «признак легко менять в одном месте»). Модуль входит в сферу «Строймаг» (2026-11-20: не в Хозмаг) сразу (без покупки);
/// в остальных сферах его включают в Маркетплейсе (<see cref="UserPreferences.EstimateEnabled"/>). <see cref="PriceSom"/> — цена модуля (пока 0 — бесплатно; если владелец назначит цену,
/// включение пойдёт через ключ, как у «Ремонта» — понадобится хэш ключа в LicenseKeys).</summary>
public static class EstimateFeature
{
    public const decimal PriceSom = 0m;

    public static bool IsAvailable => MarketSpheres.Profile.HasEstimate || UserPreferences.Instance.EstimateEnabled; // 2026-11-20: сразу только в Строймаге (Хозмаг и остальные — Маркетплейс)
}
