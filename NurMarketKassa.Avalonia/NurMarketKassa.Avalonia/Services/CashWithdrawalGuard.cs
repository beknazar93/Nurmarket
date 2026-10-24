using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-04, ТЗ 1.17.48 P0-2: изъятие 1 000 000 сом при остатке 6 823,12 проходило без вопросов — баланс
/// кассы становился −993 176,88. Теперь изъять можно не больше наличных в кассе (остаток — как в шапке кассы:
/// смена + внесения/изъятия + чеки офлайн-очереди), а перед изъятием касса спрашивает, сколько останется.
/// Если остаток неизвестен (касса запущена без связи и смену ещё не получила) — только подтверждение.
/// Запрет на сервере NurCRM — отдельно, в ТЗ для бэкенда.</summary>
internal static class CashWithdrawalGuard
{
    /// <summary>Наличные в кассе сейчас; null — неизвестно (программа владельца, нет данных смены).</summary>
    public static decimal? DrawerCash()
    {
        try
        {
            return App.GetRequiredService<MainWindowHostBridge>().Window?.CurrentDrawerCash;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>null — изъять можно; иначе — текст отказа для кассира.</summary>
    public static string? Refusal(decimal amount)
    {
        if (DrawerCash() is not { } cash || amount <= cash + 0.005m)
            return null;
        var have = Math.Max(0m, cash);
        return Tr.T(
            $"В кассе только {MoneyText.Amount(have)} сом — изъять {MoneyText.Amount(amount)} сом нельзя.",
            $"Кассада болгону {MoneyText.Amount(have)} сом бар — {MoneyText.Amount(amount)} сом алууга болбойт.",
            $"There is only {MoneyText.Amount(have)} som in the till — {MoneyText.Amount(amount)} som cannot be taken out.",
            $"Kasada yalnızca {MoneyText.Amount(have)} som var — {MoneyText.Amount(amount)} som çekilemez.",
            $"Kassada faqat {MoneyText.Amount(have)} so'm bor — {MoneyText.Amount(amount)} so'm olib bo'lmaydi.");
    }

    /// <summary>Вопрос перед изъятием: сумма и сколько останется в кассе.</summary>
    public static string ConfirmText(decimal amount) =>
        DrawerCash() is { } cash
            ? Tr.T(
                $"Изъять {MoneyText.Amount(amount)} сом из кассы? В кассе станет {MoneyText.Amount(cash - amount)} сом.",
                $"Кассадан {MoneyText.Amount(amount)} сом алынсынбы? Кассада {MoneyText.Amount(cash - amount)} сом калат.",
                $"Take {MoneyText.Amount(amount)} som out of the till? {MoneyText.Amount(cash - amount)} som will remain.",
                $"Kasadan {MoneyText.Amount(amount)} som çekilsin mi? Kasada {MoneyText.Amount(cash - amount)} som kalacak.",
                $"Kassadan {MoneyText.Amount(amount)} so'm olinsinmi? Kassada {MoneyText.Amount(cash - amount)} so'm qoladi.")
            : Tr.T(
                $"Изъять {MoneyText.Amount(amount)} сом из кассы?",
                $"Кассадан {MoneyText.Amount(amount)} сом алынсынбы?",
                $"Take {MoneyText.Amount(amount)} som out of the till?",
                $"Kasadan {MoneyText.Amount(amount)} som çekilsin mi?",
                $"Kassadan {MoneyText.Amount(amount)} so'm olinsinmi?");

    public static string Title => Tr.T("Изъятие", "Акча алуу", "Cash out", "Para çıkışı", "Chiqim");
}
