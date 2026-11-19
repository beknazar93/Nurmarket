using System.Collections.Generic;
using System.Text.Json;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-19: одна страница смен месяца. <see cref="Rows"/> — массив строк; <see cref="HasNext"/> — есть ли следующая (поле next ответа), null — ответ не сказал (решает число строк).</summary>
public sealed record ShiftsMonthPage(JsonElement Rows, bool? HasNext);

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

    /// <summary>2026-11-19 («Табель»: полный список смен не успевал за 20 с): одна страница списка смен (GET ...shifts/?page=N, по 100, новые первыми) —
    /// массив строк как в ответе; null — страницы нет (404, конец истории). Ошибка сети/сервера — исключение (в отличие от полного списка, который молча отдаёт неполное).</summary>
    Task<JsonElement?> ConstructionShiftsPageAsync(int page, CancellationToken ct = default);

    /// <summary>2026-11-19 (ответ Умара, признак shifts_month_filter): смены, открытые в месяце, одним запросом — GET ...shifts/?month=YYYY-MM&amp;page_size=100[&amp;page=N] (~1 с).
    /// null — страницы нет (404). Неверный месяц — ApiException 400 (validation_error), сеть/сервер — исключение. Без реализации (заглушки тестов) — null.</summary>
    Task<ShiftsMonthPage?> ConstructionShiftsMonthPageAsync(int year, int month, int page, CancellationToken ct = default) => Task.FromResult<ShiftsMonthPage?>(null);

    /// <summary>2026-11-19 (ответ Умара): готовый табель сервера GET api/main/staff/timesheet/?month=YYYY-MM — «сырой» ответ (разбор — <see cref="ServerTimesheet.Parse"/>).
    /// Неверный месяц — ApiException 400 (period_invalid). Кассир без права «Сотрудники» получает только себя (это не ошибка). Без реализации — null.</summary>
    Task<JsonElement?> StaffTimesheetAsync(int year, int month, CancellationToken ct = default) => Task.FromResult<JsonElement?>(null);

    /// <summary>GET /api/construction/shifts/{id}/ — одна смена. 2026-09-27, жалоба владельца
    /// «медленно открывает отчёты / Z-отчёт»: полный список смен сервер отдаёт за 2,2–3,7 с
    /// (100 строк, 108 КБ), а одну смену — за 0,2–0,5 с, с теми же полями и теми же значениями
    /// (сверено запросами к тестовой компании на 6 сменах, расхождений нет).</summary>
    Task<JsonElement> ConstructionShiftGetAsync(string shiftId, CancellationToken ct = default);

    /// <summary>Последний полный список смен (без openOnly), если он получен не раньше
    /// <paramref name="maxAge"/> назад и под тем же входом. Нужен отчёту смены: окно «Финансы →
    /// Смены» только что скачало этот список, а нажатие на смену качало его заново (2–3 с) лишь
    /// затем, чтобы найти в нём одну строку.</summary>
    bool TryGetRecentShiftsList(TimeSpan maxAge, out JsonElement payload);

    /// <summary>2026-10-04, отчёт о производительности (п. 8): продажа проведена на сервере — следующий
    /// ответ списка открытых смен уже содержит её в остатке смены.</summary>
    void NoteSaleRecorded() { }

    /// <summary>2026-10-04, п. 8: список открытых смен (?status=open), запрошенный уже ПОСЛЕ последней
    /// продажи и не старше <paramref name="maxAge"/>; идущий такой запрос — дождаться. null — такого
    /// нет, нужен свой запрос. Шапка кассы берёт остаток смены после оплаты отсюда: проверку смены сразу
    /// после продажи (ShiftStateService) делает касса, и раньше шапка через 2 с спрашивала то же самое
    /// ещё раз.</summary>
    Task<JsonElement?> OpenShiftsListAfterSaleAsync(TimeSpan maxAge, CancellationToken ct = default) =>
        Task.FromResult<JsonElement?>(null);

    /// <summary>2026-10-23 (аудит, круг 6): closing_cash последней закрытой смены кассы — «Остаток по системе» в «Открыть смену»,
    /// когда открытых смен нет. Один запрос (первая страница списка) или свежий снимок списка; ответ кэшируется на минуту.
    /// null — нет связи или закрытых смен с суммой нет.</summary>
    Task<decimal?> LastClosedShiftCashAsync(string? cashboxId, CancellationToken ct = default) =>
        Task.FromResult<decimal?>(null);

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
