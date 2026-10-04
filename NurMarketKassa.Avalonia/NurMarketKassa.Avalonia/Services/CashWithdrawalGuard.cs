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
            $"В кассе только {have:0.00} сом — изъять {amount:0.00} сом нельзя.",
            $"Кассада болгону {have:0.00} сом бар — {amount:0.00} сом алууга болбойт.",
            $"There is only {have:0.00} som in the till — {amount:0.00} som cannot be taken out.",
            $"Kasada yalnızca {have:0.00} som var — {amount:0.00} som çekilemez.",
            $"Kassada faqat {have:0.00} so'm bor — {amount:0.00} so'm olib bo'lmaydi.");
    }

    /// <summary>Вопрос перед изъятием: сумма и сколько останется в кассе.</summary>
    public static string ConfirmText(decimal amount) =>
        DrawerCash() is { } cash
            ? Tr.T(
                $"Изъять {amount:0.00} сом из кассы? В кассе станет {cash - amount:0.00} сом.",
                $"Кассадан {amount:0.00} сом алынсынбы? Кассада {cash - amount:0.00} сом калат.",
                $"Take {amount:0.00} som out of the till? {cash - amount:0.00} som will remain.",
                $"Kasadan {amount:0.00} som çekilsin mi? Kasada {cash - amount:0.00} som kalacak.",
                $"Kassadan {amount:0.00} so'm olinsinmi? Kassada {cash - amount:0.00} so'm qoladi.")
            : Tr.T(
                $"Изъять {amount:0.00} сом из кассы?",
                $"Кассадан {amount:0.00} сом алынсынбы?",
                $"Take {amount:0.00} som out of the till?",
                $"Kasadan {amount:0.00} som çekilsin mi?",
                $"Kassadan {amount:0.00} so'm olinsinmi?");

    public static string Title => Tr.T("Изъятие", "Акча алуу", "Cash out", "Para çıkışı", "Chiqim");
}
