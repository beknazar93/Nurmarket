namespace NurMarketKassa.Services;

/// <summary>
/// 2026-10-04, отчёт «офлайн и сбои сервера», раздел «Риски»: досылка чека СТАРЫМ путём (корзина →
/// позиции → checkout), когда checkout этой корзины уже отправлялся и ответ не дошёл. Защита — сверка
/// статуса корзины: оплачена → повтора нет. Но пока сервер ЕЩЁ проводит первый checkout (очень
/// медленный сервер: оплата сдалась через 1,8 с, а сервер думает 20–60 с), корзина у него ещё «open» —
/// досылка пересобирала её и проводила второй раз: две продажи на один чек. На стенде (поддельный
/// сервер проводит checkout 30 с) так и вышло — 2 продажи.
///
/// Касса не может отличить «первый checkout ещё идёт» от «первый checkout не дошёл» по одному
/// ответу. Поэтому: корзина открыта, а с отправки прошло меньше <see cref="LegacyCheckoutSettleTime"/> —
/// чек ждёт в очереди («отправлен, ждём ответа сервера»), остальные чеки досылаются дальше. Следующие
/// проходы сверяют корзину снова: оплачена — чек закрыт без повтора; всё ещё открыта спустя это время —
/// первый checkout точно не провёлся (сервер за Cloudflare не держит запрос дольше ~100 с), корзина
/// пересобирается и проводится, как раньше.
/// </summary>
public sealed partial class SyncService
{
    /// <summary>Сколько после отправки checkout старого пути корзина «open» ещё может означать
    /// «сервер её проводит». Cloudflare обрывает запрос на ~100 с (524); с запасом — 3 минуты.</summary>
    internal static readonly TimeSpan LegacyCheckoutSettleTime = TimeSpan.FromMinutes(3);

    /// <summary>Бросает <see cref="ReplayDeferredException"/>, если checkout этой корзины ушёл
    /// недавно и сервер может его ещё проводить.</summary>
    private static void DeferWhileLegacyCheckoutMayRun(OfflineSaleEntry entry)
    {
        if (entry.CheckoutSubmittedAt is not { } submitted)
            return;

        var since = DateTimeOffset.Now - submitted;
        // Часы ПК ушли назад сильнее, чем на окно ожидания, — не держим чек часами.
        if (since >= LegacyCheckoutSettleTime || since < -LegacyCheckoutSettleTime)
            return;

        var wait = LegacyCheckoutSettleTime - (since < TimeSpan.Zero ? TimeSpan.Zero : since);
        throw new ReplayDeferredException(
            $"Оплата отправлена {Math.Max(0, since.TotalSeconds):0} с назад, сервер ещё может её проводить — сверка корзины через {wait.TotalSeconds:0} с.");
    }
}

/// <summary>2026-10-04: досылка чека отложена (см. <see cref="SyncService"/>.LegacySettle) — не ошибка
/// и не сбой связи: чек остаётся в очереди, остальные досылаются.</summary>
internal sealed class ReplayDeferredException : Exception
{
    public ReplayDeferredException(string message) : base(message)
    {
    }
}
