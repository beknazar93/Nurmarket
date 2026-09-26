using System.Collections.Generic;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Доменный сервис смен: список касс/смен, открытие и закрытие смены на сервере.
/// </summary>
public interface IShiftApiService
{
    /// <summary>GET /api/construction/cashboxes/ — при 404 возвращает [].</summary>
    Task<JsonElement> ConstructionCashboxesListAsync(CancellationToken ct = default);

    /// <summary>GET /api/construction/shifts/ — список смен (статус смены с сервера).
    /// 2026-09-15, живой баг ("не работает подсчёт", таймауты в логах "totals/balance fetch
    /// TIMED OUT after 20s"): запрос без фильтра тянет ВСЮ историю смен компании — для
    /// магазина с заметной историей это может не укладываться даже в 20-секундный таймаут,
    /// после чего касса откатывается на устаревший локальный кэш (отсюда "Остаток по системе:
    /// 0.00" при реально ненулевых продажах). openOnly=true добавляет "?status=open"
    /// (подтверждено живым захватом DevTools, см. комментарий у ShiftBalanceHelper) — нужно
    /// только там, где интересует именно ТЕКУЩАЯ открытая смена (баланс/восстановление ID),
    /// а не полная История смен, которой openOnly не подходит.</summary>
    Task<JsonElement> ConstructionShiftsListAsync(bool openOnly = false, CancellationToken ct = default);

    /// <summary>POST открытия смены (перебор URL и вариантов тела).</summary>
    Task<JsonElement> ConstructionShiftOpenAsync(string cashboxId, string openingCash = "0.00", CancellationToken ct = default);

    /// <summary>POST закрытия смены (перебор URL).</summary>
    /// <param name="extraFields">Дополнительные поля тела. Нужны для обхода серверной
    /// проверки «не более 2 цифр после запятой»: сервер сам хранит income_total с пятью
    /// знаками (живой случай 2026-09-22: '150.00000') и сам же отклоняет его при закрытии.
    /// Касса в этом случае повторяет запрос, передав округлённые значения явно.</param>
    Task<JsonElement> ConstructionShiftCloseAsync(
        string shiftId,
        string? closingCash = null,
        IReadOnlyDictionary<string, string>? extraFields = null,
        CancellationToken ct = default);

    /// <summary>POST /api/construction/cashflows/ — движение денег кассы (внесение/изъятие).
    /// С полем shift изъятие входит в расход и ожидаемый остаток смены на сервере.</summary>
    Task<JsonElement> ConstructionCashFlowCreateAsync(IReadOnlyDictionary<string, string> body, CancellationToken ct = default);

    /// <summary>GET /api/construction/cashflows/?shift= — движения денег одной смены (страница page).</summary>
    Task<JsonElement> ConstructionCashFlowsForShiftAsync(string shiftId, int page = 1, CancellationToken ct = default);
}
