namespace NurMarketKassa.Services;

// 2026-11-05 (волна 2-Б, функция 12): «Права кассира по действиям + PIN старшего». Здесь — чистая политика: нужен ли PIN для этого действия с этим значением.
// Сам вопрос PIN, проверка на сервере и запись в журнал антифрода — в Services/ActionGuard (программа). Идея — Odoo/OCA pos_user_restriction; код свой.

public static class ActionGuardPolicy
{
    /// <summary>true — для действия нужен PIN старшего. value: процент скидки (Discount), сумма в сомах (Return, RemovalAfterRing, ShiftShortage); для остальных не используется.
    /// Выключено или действие не отмечено — false (поведение как раньше).</summary>
    public static bool NeedsPin(GuardAction action, double value, ActionGuardRules g)
    {
        if (!g.Enabled)
            return false;
        return action switch
        {
            GuardAction.Discount => g.Discount && value > 0 && value > g.DiscountPercent,
            GuardAction.Return => g.Return && value >= g.ReturnAmount,
            GuardAction.ManualPrice => g.ManualPrice,
            GuardAction.RemovalAfterRing => g.RemovalAfterRing && value > 0 && value >= g.RemovalAmount,
            GuardAction.OpenDrawer => g.OpenDrawer,
            GuardAction.ShiftShortage => g.ShiftShortage && value > 0 && value >= g.ShortageAmount,
            _ => false,
        };
    }

    /// <summary>Подпись суммы/процента для окна PIN: «скидка 15 %», «возврат 2 400 сом».</summary>
    public static string Describe(GuardAction action, double value)
    {
        var name = FraudText.ActionName(action.ToString());
        return action switch
        {
            GuardAction.Discount => $"{name} {value:0.#} %",
            GuardAction.Return or GuardAction.RemovalAfterRing or GuardAction.ShiftShortage when value > 0 => $"{name} {MoneyText.Som(value)}",
            _ => name,
        };
    }
}
