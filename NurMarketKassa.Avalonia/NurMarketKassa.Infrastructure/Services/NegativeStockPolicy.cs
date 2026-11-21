using System.Text.Json.Nodes;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-21, владелец: «если товара нет на складе, сделай так, чтобы кассир мог продавать в минус».
/// Одно место правды для настройки «Продавать товар без остатка (в минус)» (<see cref="UserPreferences.SellWithoutStock"/>, по умолчанию включено).
/// Включено: касса не блокирует добавление товара без остатка / сверх остатка, не открывает окна «Нет в наличии» и «Пополнить склад», не снимает лимит «+» в строке чека
/// и не блокирует оплату; чек уходит на сервер с <c>allow_negative_stock: true</c> (ответ Умара 30.10, п. 11: продажа проходит, остаток уходит в минус,
/// в ответе <c>stock_negative</c> / warnings <c>negative_stock</c> — их уже показывает «Неотправленное», см. OutboxSentNotes). Выключено — всё как раньше.
/// Услуги остатка не требуют ни при какой настройке (см. StockAvailabilityService.IsService).
/// </summary>
public static class NegativeStockPolicy
{
    /// <summary>Поле тела pos/checkout/ (быстрый путь). Старый путь (sales/start → позиции → checkout) его не передаёт.</summary>
    public const string BodyField = "allow_negative_stock";

    /// <summary>Разрешена ли продажа без остатка (настройка магазина).</summary>
    public static bool Enabled => UserPreferences.Instance.SellWithoutStock;

    /// <summary>Хватает ли остатка: <paramref name="available"/> — свободно к добавлению (склад − резервы − уже в чеке). Услуге остаток не нужен.</summary>
    public static bool HasStock(double available, double qtyToAdd, bool isService) =>
        qtyToAdd <= 0 || isService || available + 1e-6 >= qtyToAdd;

    /// <summary>Можно ли добавить количество в чек: остатка хватает ИЛИ продажа в минус разрешена настройкой.</summary>
    public static bool CanAdd(double available, double qtyToAdd, bool isService) =>
        Enabled || HasStock(available, qtyToAdd, isService);

    /// <summary>Лимит количества в строке чека («+», ручной ввод): при разрешённой продаже в минус лимита нет (null), иначе — остаток как был.</summary>
    public static double? LineLimit(double? stockLimit) => Enabled ? null : stockLimit;

    /// <summary>Добавляет allow_negative_stock в тело pos/checkout. Выключено — тело прежнее. Уже заданное поле не трогает.</summary>
    public static void ApplyTo(JsonObject body)
    {
        if (Enabled && body[BodyField] is null)
            body[BodyField] = true;
    }
}
