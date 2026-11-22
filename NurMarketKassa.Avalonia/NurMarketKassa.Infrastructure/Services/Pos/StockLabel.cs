namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-22, владелец: «в кассе минус товары: количество, ушедшее в минус, должно показываться по-особенному — что он в минус на N».
/// С 1.17.92 касса продаёт без остатка (SellWithoutStock), сервер пускает остаток ниже нуля. Остаток берётся как есть (в данных он уже отрицательный) —
/// здесь только подпись: «Минус −3 шт.» вместо «-3 шт.» (короткая, чтобы влезала в плитку ~165 px и в колонку списка).
/// Расчёты («доступно к добавлению», резервы, фильтры) эту подпись не используют.
/// </summary>
public static class StockLabel
{
    /// <summary>Допуск: остаток −0,0001 — это ноль, а не «минус».</summary>
    public const double Epsilon = 0.0005;

    /// <summary>Товар в минусе (остаток меньше нуля).</summary>
    public static bool IsMinus(double quantity) => quantity < -Epsilon;

    /// <summary>Слово перед числом: «Минус −3 шт.».</summary>
    public static string MinusWord => Tr.T("Минус", "Минус", "Minus", "Eksi", "Minus");

    /// <summary>Число для подписи без знака: в минусе — модуль, около нуля — 0 (без «-0»).</summary>
    public static double Shown(double quantity) =>
        IsMinus(quantity) ? -quantity : Math.Abs(quantity) < Epsilon ? 0 : quantity;

    /// <summary>«Минус −3 шт.» из подписи без знака («3 шт.»); остаток не в минусе — подпись как есть.</summary>
    public static string Wrap(double quantity, string textWithoutSign) =>
        IsMinus(quantity) ? $"{MinusWord} −{textWithoutSign}" : textWithoutSign;
}
