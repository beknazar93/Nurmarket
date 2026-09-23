namespace NurMarketKassa.Services;

/// <summary>Фасад над <see cref="DatabaseService"/> для бонусной программы (AI-фичи 2026-09-04) —
/// баланс хранится ЛОКАЛЬНО на этой кассе, ключ — id клиента из NurCRM (`ClientOption.Id`), так
/// что при появлении серверного поля для баллов перенос данных будет прямолинейным (тот же ключ).</summary>
public static class ClientLoyaltyStore
{
    private static DatabaseService Db => DatabaseService.Instance;

    public static double GetBalance(string clientId) =>
        string.IsNullOrWhiteSpace(clientId) ? 0 : Db.GetClientLoyaltyBalance(clientId);

    /// <summary>delta &gt; 0 — начисление, delta &lt; 0 — списание.</summary>
    public static void AdjustBalance(string clientId, double delta)
    {
        if (string.IsNullOrWhiteSpace(clientId) || System.Math.Abs(delta) < 1e-9)
            return;

        Db.AdjustClientLoyaltyBalance(clientId, delta);
    }

    /// <summary>Запоминает чистое изменение баллов конкретной продажи, чтобы полный возврат этой
    /// продажи мог его найти и отменить (см. ReverseRemainingForSale). Вызывать сразу после
    /// AdjustBalance с тем же delta — если для продажи запись уже есть, no-op.</summary>
    public static void RecordTransaction(string saleId, string clientId, double delta)
    {
        if (string.IsNullOrWhiteSpace(saleId) || string.IsNullOrWhiteSpace(clientId) || System.Math.Abs(delta) < 1e-9)
            return;

        Db.RecordClientLoyaltyTransaction(saleId, clientId, delta);
    }

    /// <summary>Скидка чека и оплаченная бонусами часть — для отчётов смены. Вызывается
    /// после успешной оплаты; повтор для того же чека ничего не меняет.</summary>
    public static void RecordSaleAdjustment(string saleId, string? shiftId, double discountTotal, double pointsRedeemed) =>
        Db.RecordShiftSaleAdjustment(saleId, shiftId, discountTotal, pointsRedeemed);

    /// <summary>Итоги по скидкам и бонусам за период (UTC).</summary>
    public static (double Discounts, double PointsRedeemed, int Receipts) AdjustmentsBetween(
        System.DateTime fromUtc, System.DateTime toUtc) =>
        Db.GetShiftAdjustmentsBetween(fromUtc, toUtc);

    /// <summary>Итоги по скидкам и бонусам конкретной смены.</summary>
    public static (double Discounts, double PointsRedeemed, int Receipts) AdjustmentsForShift(string? shiftId) =>
        Db.GetShiftAdjustmentsForShift(shiftId);

    /// <summary>Отменяет весь ещё не отменённый остаток баллов, начисленных/списанных при
    /// продаже — используется при полном возврате чека. Безопасно вызывать даже если для
    /// продажи не было транзакции (лояльность была выключена, продажа без клиента) — тогда
    /// просто ничего не делает.</summary>
    public static void ReverseRemainingForSale(string saleId)
    {
        if (string.IsNullOrWhiteSpace(saleId))
            return;

        Db.ReverseRemainingClientLoyaltyForSale(saleId);
    }
}
