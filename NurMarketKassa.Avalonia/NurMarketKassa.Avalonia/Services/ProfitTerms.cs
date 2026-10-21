namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-19, этап О10 (аудит: «Чистая прибыль» в Аналитике и «Валовая прибыль» на Сводке — одно число, разные слова):
/// одинаковые простые названия прибыли во всех отчётах программы владельца и подсказка «как считается».
/// «Валовая прибыль» = выручка − закупочная цена проданного; «Чистая прибыль» = валовая − расходы.
/// Только надписи — сами суммы считают сервер и прежний код окон.</summary>
public static class ProfitTerms
{
    public static string Gross => Tr.T("Валовая прибыль", "Дүң пайда", "Gross profit", "Brüt kâr", "Yalpi foyda");

    public static string Net => Tr.T("Чистая прибыль", "Таза пайда", "Net profit", "Net kâr", "Sof foyda");

    public static string GrossHow => Tr.T(
        "Валовая прибыль = выручка − закупочная цена проданных товаров",
        "Дүң пайда = түшүм − сатылган товарлардын сатып алуу баасы",
        "Gross profit = revenue − purchase cost of the goods sold",
        "Brüt kâr = ciro − satılan ürünlerin alış fiyatı",
        "Yalpi foyda = tushum − sotilgan tovarlarning xarid narxi");

    public static string NetHow => Tr.T(
        "Чистая прибыль = валовая прибыль − расходы",
        "Таза пайда = дүң пайда − чыгымдар",
        "Net profit = gross profit − expenses",
        "Net kâr = brüt kâr − giderler",
        "Sof foyda = yalpi foyda − xarajatlar");

    public static string HowButton => Tr.T("Как считается?", "Кантип эсептелет?", "How is it calculated?", "Nasıl hesaplanır?", "Qanday hisoblanadi?");
}
