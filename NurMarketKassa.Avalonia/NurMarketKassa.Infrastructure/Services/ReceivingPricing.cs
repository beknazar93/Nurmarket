using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>Как менять цену продажи у строк с подорожанием закупки (кнопка «Обновить цены продажи»).</summary>
public enum SalePriceRaiseMode
{
    /// <summary>Сохранить прежнюю наценку в %: продажа = новая закупка × (1 + прежняя наценка).</summary>
    KeepMarkupPercent,

    /// <summary>Поднять продажу на столько же сомов, на сколько подорожала закупка.</summary>
    AddSameAmount,
}

/// <summary>Строка предпросмотра «Обновить цены продажи»: товар · закупка было → стало · продажа было → станет · наценка.</summary>
public sealed record SalePriceUpdateRow(
    ReceivingLineVm Line,
    double OldPurchase, double NewPurchase,
    double OldSale, double NewSale,
    double? OldMarkup, double? NewMarkup);

/// <summary>2026-10-24, ТЗ «Склад» (пакет Б0): наценка и подорожание в приёмке. Чистые расчёты без сети и без записи на сервер —
/// на сервер цены уходят только при «Провести» (PurchaseReceivingService.PostAsync), вместе с markup_percent, как карточка товара.
///
/// Для окон приёмки (Склад → Приёмка, Закупки → Приём товара):
///  • колонка «Наценка %» — ReceivingLineVm.MarkupText (правится; меняет цену продажи), пометки — PurchaseRaiseText / PreviousPurchaseText /
///    LowMarginText (и флажки IsPurchaseRaised / IsLowMargin);
///  • кнопка «Обновить цены продажи (N)» — N = <see cref="CountRaisable"/>, окно-превью — <see cref="PreviewRaise"/>, «Применить» — <see cref="ApplyRaise"/>;
///  • подтверждение перед «Провести» — <see cref="SalePriceChanges"/> (у каких товаров изменится цена продажи) и <see cref="CountRaised"/> («подорожало: N»).</summary>
public static class ReceivingPricing
{
    /// <summary>2026-10-24, ТЗ вопрос В-5 (решение по умолчанию): наценка ниже этого процента — «маржа низкая». Легко менять здесь.</summary>
    public const double LowMarginPercent = 10;

    /// <summary>2026-10-24, ТЗ вопрос В-6 (решение по умолчанию): цена продажи, посчитанная от наценки, округляется ВВЕРХ до этого шага, сом
    /// (1 = до целого сома). Вверх — чтобы наценка не стала ниже заданной.</summary>
    public const double SalePriceRoundStep = 1;

    /// <summary>Закупка выше прежней больше чем на столько процентов — «подорожало» (мелкие колебания копеек не считаются).</summary>
    public const double RaiseThresholdPercent = 0.5;

    /// <summary>Допуск сравнения цен (копейки), как в PurchaseReceivingService.</summary>
    private const double PriceEpsilon = 0.004;

    /// <summary>Наценка в % от цены закупки: round((S / P − 1) × 100, 2). null — закупка или продажа не заданы (считать нечем).</summary>
    public static double? MarkupPercent(double purchase, double sale) =>
        purchase > 0 && sale > 0 ? Math.Round((sale / purchase - 1) * 100, 2) : null;

    /// <summary>Сколько знаков после запятой принимает поле «Закупка» в приёмке и накладной. 2026-11-01 (проверка бэкенда): сервер хранит закупку с 4 знаками
    /// (OPTIONS товара: purchase_price decimal_places 4; строка прихода «0.0412» принята и сохранена), поэтому цену НЕ округляем до 2–3 знаков ни при вводе, ни при отправке,
    /// ни в расчётах. Раньше было 2 (проверка 29.10: сервер резал до 2 знаков и отклонял 4 в приходе) — сервер с тех пор обновлён. Итог строки по-прежнему
    /// «количество × цена, округление до 0,01 по итогу» (<see cref="LineTotal"/>, HALF_UP как у сервера — line_total).</summary>
    public const int PurchaseDecimals = 4;

    /// <summary>Цена продажи от закупки и наценки: P × (1 + M/100), округление по настройке магазина
    /// (<see cref="ReceivingMarkupSettings.Rounding"/>; по умолчанию вверх до целого сома, как было всегда).</summary>
    public static double SaleFromMarkup(double purchase, double markupPercent) =>
        SaleFromMarkup(purchase, markupPercent, ReceivingMarkupSettingsStore.Current.Rounding);

    public static double SaleFromMarkup(double purchase, double markupPercent, SaleRounding rounding)
    {
        if (rounding == SaleRounding.WholeUp)
            return RoundSaleUp(purchase * (1 + markupPercent / 100));
        if (purchase <= 0 || Math.Abs(purchase) > 1e12 || Math.Abs(markupPercent) > 1e6)
            return 0;
        return RoundSale((double)((decimal)purchase * (1 + (decimal)markupPercent / 100m)), rounding);
    }

    /// <summary>2026-10-28: округление цены продажи по режиму. «Вверх до целого» — прежнее; «до 0,5», «до 0,1», «до 0,01» — к ближайшему
    /// (15,9375 → 16 / 15,9 / 15,94; середина — вверх, AwayFromZero). Считается в decimal — без шума double.</summary>
    public static double RoundSale(double price, SaleRounding rounding)
    {
        if (price <= 0 || double.IsNaN(price) || double.IsInfinity(price) || price > 1e12)
            return 0;
        if (rounding == SaleRounding.WholeUp)
            return RoundSaleUp(price);
        var perUnit = rounding switch { SaleRounding.Half => 2m, SaleRounding.Tenth => 10m, _ => 100m };
        var rounded = Math.Round((decimal)price * perUnit, 0, MidpointRounding.AwayFromZero) / perUnit;
        return (double)Math.Max(rounded, 1m / perUnit); // копеечная цена не должна обнулиться
    }

    /// <summary>2026-10-28: сумма строки приёмки = количество × закупка с округлением ДО 2 ЗНАКОВ ПО ИТОГУ (AwayFromZero), а не по цене:
    /// 3 × 12,333 = 36,999 → 37,00. Итог документа — сумма таких строк (копейки не теряются на каждой цене отдельно).</summary>
    public static double LineTotal(double quantity, double price)
    {
        if (double.IsNaN(quantity) || double.IsNaN(price) || Math.Abs(quantity) > 1e9 || Math.Abs(price) > 1e9)
            return quantity * price;
        return (double)Math.Round((decimal)quantity * (decimal)price, 2, MidpointRounding.AwayFromZero);
    }

    // ------------------------------------------------------------------ 2026-10-28: автонаценка в ручном режиме приёмки

    /// <summary>Откуда взята наценка автоматической цены продажи.</summary>
    public enum MarkupSource { None, Product, Category, Default }

    /// <summary>Наценка для автоцены строки: наценка её категории (если задана в таблице категорий), иначе общая «Наценка для всех, %».
    /// null — автонаценка выключена.</summary>
    public static double? AutoMarkup(ReceivingLineVm line, ReceivingMarkupSettings settings, out MarkupSource source)
    {
        source = MarkupSource.None;
        if (!settings.AutoEnabled)
            return null;
        // 2026-10-28 (уточнение владельца): главное — ОДНО общее поле «Наценка для всех, %» для всех строк приёмки; наценка товара из карточки
        // больше не берётся (мешала бы общему полю). Необязательное исключение — наценка категории, если её задал владелец в окне «Наценка…».
        var category = CategoryOf(line);
        if (category is not null && settings.CategoryPercents.TryGetValue(category, out var byCategory))
        {
            source = MarkupSource.Category;
            return byCategory;
        }
        // 2026-11-06: общая наценка 0 / не задана — считать нечем (раньше «авто» при 0 % ставило цену = закупке, а по умолчанию была 30 %).
        if (settings.DefaultPercent <= 0)
            return null;
        source = MarkupSource.Default;
        return settings.DefaultPercent;
    }

    /// <summary>Категория строки: заданная (импорт, новая строка) или категория товара в каталоге кассы.</summary>
    public static string? CategoryOf(ReceivingLineVm line)
    {
        if (!string.IsNullOrWhiteSpace(line.CategoryName))
            return line.CategoryName.Trim();
        if (string.IsNullOrEmpty(line.ProductId))
            return null;
        try
        {
            var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(tile?.Category) ? null : tile!.Category!.Trim();
        }
        catch (InvalidOperationException)
        {
            return null; // каталог обновлялся в этот момент
        }
    }

    /// <summary>Общий процент или округление изменились: пересчитать цену продажи у всех строк в режиме «авто» (ручные не трогаются).
    /// Возвращает, у скольких строк цена изменилась.</summary>
    public static int ApplyAutoToAll(IEnumerable<ReceivingLineVm> lines)
    {
        var n = 0;
        foreach (var l in lines.ToList())
        {
            // 2026-11-06: общую наценку только что включили (ввели число) — строки без цены продажи получают расчёт, который сразу виден в строке; цена из карточки не меняется.
            if (l.SaleMode == SalePriceMode.None && l.SalePrice <= 0 && l.PurchasePrice > 0)
            {
                l.EnableAutoSale();
                if (l.SalePrice > 0)
                    n++;
                continue;
            }
            if (l.ApplyAutoSale())
                n++;
        }
        return n;
    }

    /// <summary>2026-11-06: строки, у которых нет цены продажи (количество есть, цена продажи 0) — их показываем кассиру ПЕРЕД отправкой прихода (окно «У N позиций нет цены продажи»);
    /// молча ставить им «закупка + %» нельзя.</summary>
    public static List<ReceivingLineVm> MissingSalePrice(IEnumerable<ReceivingLineVm> lines) =>
        lines.Where(l => l.Quantity > 0 && l.SalePrice <= 0).ToList();

    /// <summary>2026-11-06: цена продажи из того, что кассир ввёл в окне «нет цены продажи»: сначала цена, иначе наценка % от закупки (если закупка есть). null — не введено/не посчитать.</summary>
    public static double? ResolveTypedSale(double purchase, string? priceText, string? markupText)
    {
        if (CheckoutValidation.TryParseAmount(priceText, out var price, 2) == CheckoutValidation.AmountParse.Ok && price > 0)
            return price;
        if (purchase > 0 && CheckoutValidation.TryParseAmount(markupText, out var markup, 2) == CheckoutValidation.AmountParse.Ok && markup >= 0 && markup <= 1000
            && !string.IsNullOrWhiteSpace(markupText))
        {
            var sale = SaleFromMarkup(purchase, markup);
            return sale > 0 ? sale : null;
        }
        return null;
    }

    /// <summary>2026-11-06: кнопка «Применить ко всем» — общая наценка ЯВНО применяется к строкам в режиме «авто» и «из карточки» (цена существующего товара
    /// меняется только здесь, по нажатию). Строки, где цену правили руками, не трогаются (их возвращает «Как у всех» в самой строке). Возвращает, у скольких строк
    /// цена изменилась, и сколько ручных пропущено.</summary>
    public static int ApplyCommonMarkupToAll(IEnumerable<ReceivingLineVm> lines, out int skippedManual)
    {
        var changed = 0;
        skippedManual = 0;
        foreach (var l in lines.ToList())
        {
            if (l.SaleMode == SalePriceMode.Manual)
            {
                skippedManual++;
                continue;
            }
            // 2026-11-06: строка без цены продажи — явное «Применить ко всем» ставит ей цену по общей наценке (пользователь это видит в строке до отправки).
            if (l.SaleMode == SalePriceMode.None && l.SalePrice <= 0 && l.PurchasePrice > 0)
            {
                l.EnableAutoSale();
                if (l.SalePrice > 0)
                    changed++;
                continue;
            }
            if (l.SaleMode is not (SalePriceMode.Auto or SalePriceMode.Card) || l.PurchasePrice <= 0)
                continue;
            var before = l.SalePrice;
            l.ReturnToAuto();
            if (Math.Abs(l.SalePrice - before) > 1e-9)
                changed++;
        }
        return changed;
    }

    /// <summary>Автоматическая цена продажи строки по её закупке; null — закупки нет или считать нечем.</summary>
    public static double? AutoSalePrice(ReceivingLineVm line, ReceivingMarkupSettings settings)
    {
        if (line.PurchasePrice <= 0 || AutoMarkup(line, settings, out _) is not { } markup)
            return null;
        var sale = SaleFromMarkup(line.PurchasePrice, markup, settings.Rounding);
        return sale > 0 ? sale : null;
    }

    /// <summary>Округление цены продажи вверх до шага (1e-9 — против шума double: 54,00000000000001 не должно стать 55).</summary>
    public static double RoundSaleUp(double price) =>
        price <= 0 ? 0 : Math.Ceiling(price / SalePriceRoundStep - 1e-9) * SalePriceRoundStep;

    /// <summary>На сколько процентов закупка выше прежней (0 — не подорожала или прежней цены нет).</summary>
    public static double PurchaseRaisePercent(double originalPurchase, double purchase) =>
        originalPurchase > 0 && purchase > originalPurchase ? (purchase / originalPurchase - 1) * 100 : 0;

    public static bool IsPurchaseRaised(double originalPurchase, double purchase) =>
        PurchaseRaisePercent(originalPurchase, purchase) > RaiseThresholdPercent;

    /// <summary>Маржа низкая: продажа ниже закупки или наценка ниже порога. Пока нет обеих цен — нет и пометки.</summary>
    public static bool IsLowMargin(double purchase, double sale) =>
        MarkupPercent(purchase, sale) is { } m && (sale < purchase || m < LowMarginPercent);

    // ------------------------------------------------------------------ «Обновить цены продажи»

    /// <summary>Строка из тех, что кнопка «Обновить цены продажи» может поправить: закупка подорожала, а цена продажи осталась прежней
    /// (её не правили руками). Новые товары (прежних цен нет) сюда не попадают.</summary>
    public static bool IsRaisable(ReceivingLineVm line) =>
        !line.IsNew && line.IsPurchaseRaised && line.OriginalSalePrice > 0 && Math.Abs(line.SalePrice - line.OriginalSalePrice) <= PriceEpsilon;

    /// <summary>N для надписи «Обновить цены продажи (N)».</summary>
    public static int CountRaisable(IEnumerable<ReceivingLineVm> lines) => lines.Count(IsRaisable);

    /// <summary>«Подорожало: N» в итоге под таблицей — строк, где закупка выше прежней.</summary>
    public static int CountRaised(IEnumerable<ReceivingLineVm> lines) => lines.Count(l => l.IsPurchaseRaised);

    /// <summary>2026-10-29 (спека Fable, итоговая панель): позиции, штуки, сумма закупки, выручка по ценам продажи, прибыль, средняя наценка,
    /// сколько строк с маржой &lt; 10 % и с ценой ниже закупки.</summary>
    public sealed record ReceivingTotals(int Positions, double Quantity, double Purchase, double Sale, double Profit, double? AvgMarkup, int LowCount, int BelowCount, int RaisedCount, int NewCount);

    public static ReceivingTotals Totals(IEnumerable<ReceivingLineVm> lines)
    {
        var list = lines.ToList();
        var purchase = list.Sum(l => l.LineTotal);
        var sale = list.Sum(l => LineTotal(l.Quantity, l.SalePrice));
        // Средняя наценка — по строкам, где есть обе цены (взвешенно по количеству).
        var both = list.Where(l => l.PurchasePrice > 0 && l.SalePrice > 0).ToList();
        var bothPurchase = both.Sum(l => LineTotal(l.Quantity, l.PurchasePrice));
        var avg = bothPurchase > 0 ? (both.Sum(l => LineTotal(l.Quantity, l.SalePrice)) / bothPurchase - 1) * 100 : (double?)null;
        return new ReceivingTotals(list.Count, list.Sum(l => l.Quantity), purchase, sale, both.Sum(l => LineTotal(l.Quantity, l.SalePrice)) - bothPurchase, avg,
            list.Count(l => l.IsLowOnly), list.Count(l => l.IsBelowCost), CountRaised(list), list.Count(l => l.IsNew));
    }

    /// <summary>Какой станет цена продажи строки (null — посчитать не из чего: нет прежних цен/наценки).</summary>
    public static double? RaisedSalePrice(ReceivingLineVm line, SalePriceRaiseMode mode)
    {
        if (line.PurchasePrice <= 0)
            return null;
        if (mode == SalePriceRaiseMode.AddSameAmount)
            return line.OriginalPurchasePrice > 0 && line.OriginalSalePrice > 0
                ? RoundSaleUp(line.OriginalSalePrice + (line.PurchasePrice - line.OriginalPurchasePrice))
                : null;

        // Прежняя наценка: по прежним ценам товара (то, что видно на деле), иначе — поле markup_percent товара.
        double? old = line.OriginalPurchasePrice > 0 && line.OriginalSalePrice > 0
            ? (line.OriginalSalePrice / line.OriginalPurchasePrice - 1) * 100
            : line.OriginalMarkupPercent > 0 ? line.OriginalMarkupPercent : null;
        return old is { } m ? SaleFromMarkup(line.PurchasePrice, m) : null;
    }

    /// <summary>Окно-превью: что получится у каждой пригодной строки (<see cref="IsRaisable"/>). Строки, у которых считать не из чего
    /// или цена не выросла, в список не попадают. Ничего не меняет.</summary>
    public static List<SalePriceUpdateRow> PreviewRaise(IEnumerable<ReceivingLineVm> lines, SalePriceRaiseMode mode)
    {
        var rows = new List<SalePriceUpdateRow>();
        foreach (var l in lines.Where(IsRaisable))
            if (RaisedSalePrice(l, mode) is { } sale && sale > l.SalePrice + PriceEpsilon)
                rows.Add(new SalePriceUpdateRow(l, l.OriginalPurchasePrice, l.PurchasePrice, l.SalePrice, sale,
                    MarkupPercent(l.OriginalPurchasePrice, l.SalePrice), MarkupPercent(l.PurchasePrice, sale)));
        return rows;
    }

    /// <summary>«Применить к N строкам»: ставит новую цену продажи в строки приёмки (на сервер — только при «Провести»). Возвращает N.</summary>
    public static int ApplyRaise(IEnumerable<SalePriceUpdateRow> rows)
    {
        var n = 0;
        foreach (var r in rows)
        {
            r.Line.SalePrice = r.NewSale;
            n++;
        }
        return n;
    }

    /// <summary>Перед «Провести»: товары со склада, у которых цена продажи в приёмке отличается от прежней, — «Цена продажи изменится у K товаров».</summary>
    public static List<ReceivingLineVm> SalePriceChanges(IEnumerable<ReceivingLineVm> lines) =>
        lines.Where(l => !l.IsNew && l.OriginalSalePrice > 0 && Math.Abs(l.SalePrice - l.OriginalSalePrice) > PriceEpsilon).ToList();
}
