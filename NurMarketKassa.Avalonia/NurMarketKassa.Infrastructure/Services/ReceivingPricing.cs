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

    /// <summary>Цена продажи от закупки и наценки: P × (1 + M/100), округление вверх до <see cref="SalePriceRoundStep"/>.</summary>
    public static double SaleFromMarkup(double purchase, double markupPercent) =>
        RoundSaleUp(purchase * (1 + markupPercent / 100));

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
