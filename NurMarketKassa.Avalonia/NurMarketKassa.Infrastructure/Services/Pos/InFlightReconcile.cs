namespace NurMarketKassa.Services.Api
{
    /// <summary>2026-10-29 (приёмка 1.17.65, В-1): ответ сервера на «что с операцией по этому ключу» (GET api/main/idempotency/{key}/).</summary>
    public enum IdempotencyOutcome
    {
        /// <summary>Ответа по существу нет (старый сервер, обрыв, таймаут): исход неизвестен.</summary>
        Unknown,

        /// <summary>Операция с этим ключом уже есть на сервере (продажа проведена или проводится).</summary>
        Found,

        /// <summary>Сервер ответил «такого ключа нет» — продажа не проведена.</summary>
        NotFound,
    }
}

namespace NurMarketKassa.Services
{
    using NurMarketKassa.Services.Api;

    /// <summary>Что делать кассе с записью «оплата в полёте» того же чека после сверки по ключу.</summary>
    internal enum InFlightAction
    {
        /// <summary>Продажи нет — запись снять, оплата пойдёт с новым ключом.</summary>
        Forget,

        /// <summary>Продажа проведена — этот чек повторно не пробивать.</summary>
        ReportPaid,

        /// <summary>Исход неясен — предупредить кассира один раз; повтор пойдёт тем же ключом.</summary>
        WarnOnce,

        /// <summary>Кассир уже предупреждён — оплата идёт тем же ключом и составом (второй продажи сервер не создаст).</summary>
        Proceed,
    }

    /// <summary>2026-10-29 (В-1): чистое решение «что делать с оплатой, прерванной посреди отправки» (без сети и диска — его же прогоняет мини-проверка).</summary>
    internal static class InFlightReconcile
    {
        internal static InFlightAction Decide(IdempotencyOutcome outcome, TimeSpan age, bool alreadyWarned, TimeSpan settle)
        {
            if (outcome == IdempotencyOutcome.Found)
                return InFlightAction.ReportPaid;
            // «Такого ключа нет» верим только когда запрос давно не может «дорабатываться» на сервере.
            if (outcome == IdempotencyOutcome.NotFound && age > settle)
                return InFlightAction.Forget;
            return alreadyWarned ? InFlightAction.Proceed : InFlightAction.WarnOnce;
        }
    }
}
