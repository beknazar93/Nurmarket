using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Services;

public sealed class CashShiftService : ICashShiftService
{
    private readonly IShiftApiService _shiftApi;
    private readonly IShiftStateService _shiftStateService;
    private readonly MySqlAuditService _auditDb;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly IOfflinePosStateStore _offlinePosStateStore;
    private readonly ISalesApiService _salesApi;

    public CashShiftService(
        IShiftApiService shiftApi,
        IShiftStateService shiftStateService,
        MySqlAuditService auditDb,
        IReceiptPrinterService receiptPrinter,
        IOfflinePosStateStore offlinePosStateStore,
        ISalesApiService salesApi)
    {
        _shiftApi = shiftApi;
        _shiftStateService = shiftStateService;
        _auditDb = auditDb;
        _receiptPrinter = receiptPrinter;
        _offlinePosStateStore = offlinePosStateStore;
        _salesApi = salesApi;
    }

    /// <summary>
    /// Итог локальных внесений минус изъятия по смене. Заполняется UI-слоем
    /// (Infrastructure не видит хранилище кассовых операций хоста).
    /// </summary>
    public static Func<string?, decimal>? ShiftCashOperationsNetProvider { get; set; }

    public async Task<CashShiftOperationResult> OpenShiftAsync(decimal openingCash, CancellationToken cancellationToken = default)
    {
        // Вторая смена поверх незакрытой затирает ActiveShiftId прежней и осиротляет её продажи.
        if (!string.IsNullOrWhiteSpace(PosApp.ActiveShiftId))
            return CashShiftOperationResult.Failed("Смена уже открыта. Закройте текущую смену перед открытием новой.");

        if (OfflineModeHelper.UseLocalOperations)
        {
            PosApp.ActiveShiftId = "offline-shift-" + Guid.NewGuid().ToString("N");
            ShiftService.IsShiftOpen = true;
            _offlinePosStateStore.SaveFromApp(openingCash);
            return CashShiftOperationResult.Success(openingCash, isOffline: true, infoMessage: "Смена открыта офлайн.");
        }

        var opening = openingCash.ToString("0.00", CultureInfo.InvariantCulture);
        try
        {
            // 2026-09-10: раньше вызывался ДО этого try/catch — ApiException отсюда (например,
            // "Сессия недействительна" при протухшем токене) улетал необработанным и ронял кассу
            // с крашем прямо при добавлении первого товара в чек (оно само открывает смену).
            // Теперь ошибки этого вызова обрабатываются теми же catch, что и ниже.
            var cashboxId = await EnsurePosCashboxIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(cashboxId))
                return CashShiftOperationResult.Failed("Не удалось определить кассу.");

            var response = await _shiftApi
                .ConstructionShiftOpenAsync(cashboxId, opening, cancellationToken)
                .ConfigureAwait(false);

            var shiftId = CartDisplayHelper.TryShiftIdFromOpenResponse(response);

            // 2026-09-26: «открыть смену» у сервера — это «дай мою открытую смену»: если у кассира
            // смена уже открыта на ДРУГОЙ кассе, сервер новую не открывает, а возвращает ту
            // (проверено на тестовой компании). Принять её как смену этой кассы нельзя — продажа
            // уйдёт с этой кассой и сервер ответит «Смена не открыта». Переходим на ту кассу, как
            // ShiftStateService при входе, а если касса закреплена в настройках — объясняем.
            var openedCashboxId = response.ValueKind == JsonValueKind.Object ? ShiftHelper.ReadCashboxId(response) : null;
            if (!string.IsNullOrWhiteSpace(openedCashboxId)
                && !string.Equals(openedCashboxId, cashboxId, StringComparison.OrdinalIgnoreCase))
            {
                var openedCashboxName = response.TryGetProperty("cashbox_name", out var cn) && cn.ValueKind == JsonValueKind.String
                    ? cn.GetString()
                    : null;
                var pinned = UserPreferences.Instance.PreferredCashboxId;
                if (!string.IsNullOrWhiteSpace(pinned)
                    && !string.Equals(pinned, openedCashboxId, StringComparison.OrdinalIgnoreCase))
                {
                    PosLogger.Log($"Открытие смены: у кассира уже открыта смена на другой кассе ({openedCashboxName ?? openedCashboxId}), эта касса закреплена в настройках.", "SHIFT");
                    var otherName = openedCashboxName ?? openedCashboxId;
                    return CashShiftOperationResult.Failed(
                        $"У вас уже открыта смена на кассе «{otherName}». Вторую смену сервер не открывает: " +
                        $"закройте ту смену (на той кассе или на сайте) либо выберите в настройках кассу «{otherName}».");
                }

                PosLogger.Log($"Открытие смены: сервер вернул уже открытую смену кассира на кассе {openedCashboxName ?? openedCashboxId} — касса переключена на неё.", "SHIFT");
                PosApp.PosCashboxId = openedCashboxId;
                if (!string.IsNullOrWhiteSpace(openedCashboxName))
                    PosApp.PosCashboxDisplayName = openedCashboxName;
            }

            if (!string.IsNullOrWhiteSpace(shiftId))
                PosApp.ActiveShiftId = shiftId;
            else
                await _shiftStateService.RefreshAsync(cancellationToken).ConfigureAwait(false);

            // Какую смену вернул сервер: новую или уже открытую (живой случай 2026-09-26 без этой
            // строки пришлось восстанавливать по косвенным признакам).
            PosLogger.Log(
                $"Смена открыта: id={PosApp.ActiveShiftId}, касса={PosApp.PosCashboxDisplayName ?? PosApp.PosCashboxId}, " +
                $"кассир={PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId}, открыта сервером в {(response.TryGetProperty("opened_at", out var oa) ? oa.ToString() : "?")}",
                "SHIFT");

            ShiftService.IsShiftOpen = true;
            _offlinePosStateStore.SaveFromApp(openingCash);
            _auditDb.LogShift("open", openingCash, PosApp.ActiveShiftId);
            return CashShiftOperationResult.Success(openingCash);
        }
        catch (HttpRequestException ex)
        {
            PosApp.ActiveShiftId = "offline-shift-" + Guid.NewGuid().ToString("N");
            ShiftService.IsShiftOpen = true;
            _offlinePosStateStore.SaveFromApp(openingCash);
            return CashShiftOperationResult.Success(
                openingCash,
                isOffline: true,
                infoMessage: $"Смена открыта офлайн ({ex.Message}).");
        }
        catch (Exception ex) when (IsCashboxRejectedError(ex.Message))
        {
            // 2026-09-14, тот же баг, что уже исправлен в PosCheckoutService (см. её
            // комментарий) — ConstructionCashboxesListAsync не фильтруется по филиалу, поэтому
            // EnsurePosCashboxIdAsync выше мог посчитать отвергнутую сервером кассу валидной.
            // Реальный отказ сервера при открытии смены — надёжный сигнал переподобрать кассу.
            var reassigned = await TryReassignCashboxAsync(cancellationToken).ConfigureAwait(false);
            return CashShiftOperationResult.Failed(reassigned
                ? "Касса была переназначена (старая не подходит для вашего филиала). Откройте смену ещё раз."
                : "Ни одна касса компании не подходит для вашего филиала. Обратитесь к администратору NurCRM — " +
                  "проверьте привязку кассы к филиалу в веб-версии.");
        }
        catch (Exception ex)
        {
            return CashShiftOperationResult.Failed("Ошибка открытия смены: " + ex.Message);
        }
    }

    /// <summary>Узнаёт именно ошибку "cashbox_id: Касса не найдена или не принадлежит этому
    /// филиалу" — см. одноимённый метод в PosCheckoutService (та же проблема, разные вызовы).</summary>
    private static bool IsCashboxRejectedError(string? message) =>
        !string.IsNullOrEmpty(message)
        && message.Contains("cashbox", StringComparison.OrdinalIgnoreCase)
        && (message.Contains("не найдена", StringComparison.OrdinalIgnoreCase)
            || message.Contains("не принадлежит", StringComparison.OrdinalIgnoreCase));

    /// <summary>См. одноимённый метод в PosCheckoutService — заново запрашивает список касс и
    /// выбирает первую доступную, отличную от только что отвергнутой сервером. Возвращает
    /// false, когда кандидатов не осталось (см. её комментарий).</summary>
    private async Task<bool> TryReassignCashboxAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rejectedId = PosApp.PosCashboxId;
            if (!string.IsNullOrWhiteSpace(rejectedId))
                PosApp.RejectedCashboxIds.Add(rejectedId);

            var rawList = await _shiftApi.ConstructionCashboxesListAsync(cancellationToken).ConfigureAwait(false);
            var candidates = CartDisplayHelper.ListCashboxes(rawList)
                .Where(c => !PosApp.RejectedCashboxIds.Contains(c.Id))
                .ToList();

            var next = candidates.FirstOrDefault(c => c.IsActive);
            if (next.Id == null)
                next = candidates.FirstOrDefault();

            if (next.Id == null)
            {
                PosLogger.Log("Cashbox reassignment: no alternative (unrejected) cashbox found in list.", "SHIFT");
                return false;
            }

            PosApp.PosCashboxId = next.Id;
            PosApp.PosCashboxDisplayName = next.DisplayName;
            PosLogger.Log($"Cashbox reassigned after server rejection: {rejectedId} -> {next.Id}", "SHIFT");
            return true;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Cashbox reassignment failed: {ex}", "SHIFT");
            return false;
        }
    }

    public async Task<CashShiftOperationResult> CloseShiftAsync(decimal? closingCash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(PosApp.ActiveShiftId))
            return CashShiftOperationResult.Failed("Смена уже закрыта.");

        var shiftId = PosApp.ActiveShiftId;
        var closing = closingCash?.ToString("0.00", CultureInfo.InvariantCulture);

        if (OfflineModeHelper.UseLocalOperations)
        {
            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            _offlinePosStateStore.SaveFromApp(0m);
            _auditDb.LogShift("close_offline", closingCash, shiftId);
            return CashShiftOperationResult.Success(closingCash, isOffline: true);
        }

        try
        {
            JsonElement response;
            try
            {
                response = await _shiftApi
                    .ConstructionShiftCloseAsync(shiftId!, closing, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ApiException ex) when (LooksLikeDecimalPlacesError(ex.Message))
            {
                // Живой случай 2026-09-22: смена не закрывалась вообще — сервер отвечал
                // «income_total: Убедитесь, что вы ввели не более 2 цифр после запятой».
                // Касса это поле не отправляет: сервер сам посчитал и сохранил '150.00000'
                // (проверено запросом к api/construction/shifts/) и сам же отклонил его при
                // закрытии. Кассир при этом оказывался заперт — смену не закрыть никак.
                // Повторяем закрытие, передав те же суммы округлёнными до копеек.
                PosLogger.Log(
                    "Закрытие смены отклонено из-за лишних знаков в суммах сервера — повтор с округлением.",
                    "SHIFT");

                var rounded = await BuildRoundedTotalsAsync(shiftId!, cancellationToken).ConfigureAwait(false);
                if (rounded.Count == 0)
                    throw;

                response = await _shiftApi
                    .ConstructionShiftCloseAsync(shiftId!, closing, rounded, cancellationToken)
                    .ConfigureAwait(false);
            }

            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            // A successful close is authoritative. An immediate list refresh may
            // still return the closed shift or restore it from the offline cache.
            _offlinePosStateStore.SaveFromApp(0m);
            _auditDb.LogShift("close", closingCash, shiftId);
            // 2026-09-15, живой баг: "система не считает наличную и безналичную" — отчёт по
            // смене раньше брал разбивку из _shiftTotals, снятого В ФОНЕ ЕЩЁ ДО закрытия (пока
            // был открыт CloseShiftDialog) — если кассир закрывал смену вскоре после последней
            // продажи, этот снимок мог не успеть учесть её. Ответ САМОГО закрытия — гарантированно
            // свежий, читаем разбивку из него.
            var totals = await ReadClosingTotalsAsync(response, shiftId, cancellationToken).ConfigureAwait(false);
            return CashShiftOperationResult.Success(closingCash, totals: totals);
        }
        catch (HttpRequestException ex)
        {
            // Симметрично OpenShiftAsync: сеть/DNS недоступны — закрываем смену локально,
            // а не оставляем кассира с зависшей открытой сменой без связи с сервером.
            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            _offlinePosStateStore.SaveFromApp(0m);
            _auditDb.LogShift("close_offline", closingCash, shiftId);
            return CashShiftOperationResult.Success(
                closingCash,
                isOffline: true,
                infoMessage: $"Смена закрыта офлайн ({ex.Message}).");
        }
        catch (Exception ex) when (ex.Message.Contains("уже закрыта", StringComparison.OrdinalIgnoreCase))
        {
            // 2026-09-12, живой случай: смену закрыли где-то ещё (например, на сайте), пока
            // касса ещё считала её открытой — сервер отвечает {'status': ['Смена уже закрыта.']}
            // на повторное закрытие. Это не сбой — сервер и так уже согласен, что смена закрыта,
            // нужно только привести локальное состояние в соответствие (та же логика, что и в
            // MainWindow.OnShiftDesyncDetected для похожего рассинхрона после отклонённой
            // оплаты), а не пугать кассира сырым текстом ошибки.
            PosApp.ActiveShiftId = null;
            ShiftService.IsShiftOpen = false;
            _offlinePosStateStore.SaveFromApp(0m);
            _auditDb.LogShift("close_already_closed", closingCash, shiftId);
            return CashShiftOperationResult.Success(
                closingCash,
                infoMessage: "Смена уже была закрыта (например, на сайте) — состояние кассы синхронизировано.");
        }
        catch (ApiException ex) when (ex.StatusCode == 404)
        {
            // 2026-09-15, живой баг: "Ошибка закрытия смены: Запрашиваемый ресурс или страница
            // не найдена (404)" на смене с реальным, накопленным за смену остатком — значит
            // PosApp.ActiveShiftId (тот ID, что касса запомнила при открытии смены) сервер уже
            // не узнаёт как текущую открытую смену для этой кассы (например, смена была открыта
            // при временной потере связи с локальным ID-заглушкой, который так и не был заменён
            // на настоящий, когда связь восстановилась). Вместо того чтобы сразу показать
            // кассиру сырую ошибку 404, один раз спрашиваем у сервера актуальный ID открытой
            // смены для этой кассы (ShiftHelper.PickOpenShiftId — тот же метод, что использует
            // ShiftStateService.RefreshAsync) и повторяем закрытие с ним.
            PosLogger.Log($"Shift close 404 for id={shiftId}, attempting recovery.", "SHIFT");
            var freshId = await TryRecoverActiveShiftIdAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(freshId) || string.Equals(freshId, shiftId, StringComparison.Ordinal))
                return CashShiftOperationResult.Failed(
                    "Не удалось закрыть смену: сервер не находит эту смену. Перезайдите в кассу и попробуйте снова.");

            try
            {
                var retryResponse = await _shiftApi.ConstructionShiftCloseAsync(freshId, closing, null, cancellationToken).ConfigureAwait(false);
                PosApp.ActiveShiftId = null;
                ShiftService.IsShiftOpen = false;
                _offlinePosStateStore.SaveFromApp(0m);
                _auditDb.LogShift("close", closingCash, freshId);
                return CashShiftOperationResult.Success(closingCash, totals: await ReadClosingTotalsAsync(retryResponse, freshId, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception retryEx)
            {
                PosLogger.Log($"Shift close retry with recovered id={freshId} failed: {retryEx}", "SHIFT");
                if (LooksLikeDecimalPlacesError(retryEx.Message))
                    return CloseLocallyAndQueue(freshId, closing, closingCash, retryEx.Message);

                return CashShiftOperationResult.Failed(DescribeCloseFailure(retryEx));
            }
        }
        catch (Exception ex)
        {
            // Ошибка на стороне сервера, которую из кассы не исправить (см. DescribeCloseFailure):
            // не запираем кассира. Смену закрываем ЛОКАЛЬНО и ставим в очередь — SyncService
            // дожмёт сервер, как только тот начнёт принимать закрытие.
            if (LooksLikeDecimalPlacesError(ex.Message))
                return CloseLocallyAndQueue(shiftId!, closing, closingCash, ex.Message);

            return CashShiftOperationResult.Failed(DescribeCloseFailure(ex));
        }
    }

    /// <summary>Закрывает смену на самой кассе и ставит её в очередь на закрытие сервера.
    /// Кассир продолжает работать: может открыть новую смену, пробивать чеки, сдавать кассу.
    /// Сервер дожимается фоном (SyncService.FlushPendingShiftClosesAsync).</summary>
    private CashShiftOperationResult CloseLocallyAndQueue(
        string shiftId, string? closingText, decimal? closingCash, string serverError)
    {
        PendingShiftCloseStore.Enqueue(shiftId, closingText, serverError);

        PosApp.ActiveShiftId = null;
        ShiftService.IsShiftOpen = false;
        _offlinePosStateStore.SaveFromApp(0m);
        _auditDb.LogShift("close_local_pending_server", closingCash, shiftId);

        PosLogger.Log(
            $"Смена {shiftId} закрыта локально: сервер отказал ({serverError}). Закрытие на сервере — фоном.",
            "SHIFT");

        return CashShiftOperationResult.Success(closingCash, isOffline: true);
    }

    /// <summary>Человеческое объяснение вместо сырого ответа Django.
    ///
    /// Разобрано 2026-09-22: сервер сам хранит сумму смены с ПЯТЬЮ знаками после запятой
    /// (income_total = '150.00000' — прочитано запросом к api/construction/shifts/) и сам же
    /// отклоняет её своей проверкой «не более 2 цифр после запятой» при закрытии. Касса это
    /// поле не отправляет вовсе, изменить его не может (PATCH на смену — 405 «Метод не
    /// разрешён»), а других эндпоинтов закрытия у сервера нет. То есть из программы это не
    /// лечится ничем — кассиру нужно сказать об этом прямо, а не показывать текст ошибки,
    /// по которому он ничего не поймёт и решит, что сломалась касса.</summary>
    private static string DescribeCloseFailure(Exception ex)
    {
        if (!LooksLikeDecimalPlacesError(ex.Message))
            return "Ошибка закрытия смены: " + ex.Message;

        return "Смену не удаётся закрыть из-за ошибки на стороне сервера NurCRM: он хранит сумму "
             + "смены с лишними знаками после запятой и сам же её не принимает при закрытии. "
             + "Из кассы это не исправить — поле доступно только для чтения."
             + Environment.NewLine + Environment.NewLine
             + "Что делать: попробуйте закрыть смену в веб-панели NurCRM. Если и там не выйдет — "
             + "обратитесь в поддержку NurCRM и передайте им это сообщение вместе с ответом сервера:"
             + Environment.NewLine + ex.Message;
    }

    /// <summary>Отличает именно ошибку «слишком много знаков после запятой» от прочих отказов
    /// сервера: повторять с округлением имеет смысл только в этом случае.</summary>
    private static bool LooksLikeDecimalPlacesError(string? message) =>
        !string.IsNullOrWhiteSpace(message)
        && (message.Contains("после запятой", StringComparison.OrdinalIgnoreCase)
            || message.Contains("decimal places", StringComparison.OrdinalIgnoreCase));

    /// <summary>Читает суммы смены с сервера и возвращает их округлёнными до копеек.
    /// Пустой словарь — значит прочитать не удалось и повторять нечем.</summary>
    private async Task<Dictionary<string, string>> BuildRoundedTotalsAsync(
        string shiftId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>();
        try
        {
            var list = await _shiftApi.ConstructionShiftsListAsync(true, cancellationToken).ConfigureAwait(false);
            var shift = FindShiftById(list, shiftId);
            if (shift is not { } element)
                return result;

            foreach (var field in new[] { "income_total", "expense_total" })
            {
                if (!element.TryGetProperty(field, out var value))
                    continue;

                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    result[field] = Math.Round(number, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Не удалось прочитать суммы смены для повтора закрытия: {ex.Message}", "WARNING");
        }

        return result;
    }

    private static JsonElement? FindShiftById(JsonElement list, string shiftId)
    {
        var items = list.ValueKind == JsonValueKind.Array
            ? list
            : list.ValueKind == JsonValueKind.Object && list.TryGetProperty("results", out var results)
                ? results
                : default;

        if (items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id)
                && string.Equals(id.GetString(), shiftId, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>См. комментарий у первого вызова Success(..., totals:) в CloseShiftAsync —
    /// переводит ShiftBalanceHelper.ShiftTotals (Infrastructure) в плоский Core-тип, который
    /// умеет пронести CashShiftOperationResult наружу без ссылки Core → Infrastructure.</summary>
    private async Task<CashShiftClosingTotals?> ReadClosingTotalsAsync(
        System.Text.Json.JsonElement response, string? shiftId, CancellationToken cancellationToken)
    {
        if (response.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        var t = ShiftBalanceHelper.ReadShiftTotals(response);
        var debt = t.DebtSales ?? await TryComputeDebtTotalAsync(t.SalesCount, shiftId, cancellationToken).ConfigureAwait(false);
        return new CashShiftClosingTotals(
            t.OpeningCash, t.TotalSales, t.CashSales, t.NonCashSales, debt, t.SalesCount);
    }

    /// <inheritdoc/>
    public Task<decimal?> ResolveShiftDebtTotalAsync(
        string shiftId, int? salesCountHint = null, CancellationToken cancellationToken = default) =>
        TryComputeDebtTotalAsync(salesCountHint, shiftId, cancellationToken);

    /// <summary>2026-09-15, живой баг ("Долг 480 сом за смену, где по факту только один долг на
    /// 160") — раньше этот метод суммировал ВСЕ продажи с payment_method=="debt" в списке продаж
    /// КАССЫ (не смены!), а список продаж одной кассы копится через много смен подряд — три
    /// тестовые продажи «в долг» по 160 сом в ТРЁХ РАЗНЫХ сменах одной кассы давали в сумме ровно
    /// 480 = 3×160 в отчёте закрытия КАЖДОЙ из них. Теперь дополнительно сверяем shift_id самой
    /// продажи с ID именно ЗАКРЫВАЕМОЙ смены. Поле подтверждено живым захватом (см. [DEBUG] Debt
    /// sale row sample в логе) — у строки продажи есть простое строковое поле "shift" (не
    /// вложенный объект), а сумма долга — отдельное числовое поле "debt_amount" (не "total": оно
    /// тоже 160 в наших тестах, но только потому что cash_received на сервере для payment_method=
    /// debt всегда "0.00" независимо от того, что кассир ввёл как "получено сейчас" — см.
    /// TryApplyInitialDebtPaymentAsync). Если НИ У ОДНОЙ строки в ответе нет "shift" (например,
    /// сервер сменит схему) — честно возвращаем null ("—" в отчёте), чем повторяем тот же баг
    /// с чужими сменами.
    ///
    /// 2026-09-15, второй живой баг ("оплата частичного долга вообще не попадает в отчёт") —
    /// salesCount ЗДЕСЬ приходит из "sales_count" самого ответа закрытия смены, а это поле, как
    /// уже выяснилось, НЕ считает долговые продажи вообще (смена только с одной продажей «в долг»
    /// и без единой наличной/безналичной показывала salesCount=0). Старая проверка "salesCount >
    /// 0" из-за этого пропускала весь расчёт долга ЦЕЛИКОМ для такой смены — отчёт показывал "—"
    /// вместо реального долга. Теперь salesCount используется только для размера страницы (когда
    /// его нет или он 0 — берём разумный запас по умолчанию), а не как условие "искать или нет".
    ///
    /// 2026-09-15, третий живой баг ("долг за смену вырос ПОСЛЕ успешной частичной оплаты") —
    /// раньше суммировалось поле "debt_amount"/"total" самой продажи, а это, как и cash_received,
    /// статичное поле исходной суммы продажи — сервер НЕ обновляет его после оплаты через сделку
    /// (тот же нюанс, что уже решён в PayDebtDialog.ResolveRealRemainingDebtAsync — тем же самым
    /// способом: реальный остаток — это remaining_debt сделки, а не total/debt_amount продажи).
    /// Теперь для каждой долговой продажи этой смены отдельно уточняем deal_id (PosSaleGetAsync)
    /// и текущий remaining_debt (ClientDealGetAsync), параллельно по всем сразу.</summary>
    private async Task<decimal?> TryComputeDebtTotalAsync(int? salesCount, string? shiftId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(shiftId))
            return null;

        try
        {
            var cashboxId = PosApp.PosCashboxId;
            var pageSize = Math.Clamp((salesCount ?? 0) + 20, 20, 80);
            var sales = await _salesApi.PosSalesListAsync(1, pageSize, cashboxId, cancellationToken).ConfigureAwait(false);

            var matchingDebtSales = new List<(string SaleId, string ClientId, decimal FallbackAmount)>();
            var sawAnyShiftField = false;
            var loggedSampleRow = false;
            foreach (var sale in sales)
            {
                if (sale.ValueKind != System.Text.Json.JsonValueKind.Object)
                    continue;
                if (!sale.TryGetProperty("payment_method", out var pm) || pm.ValueKind != System.Text.Json.JsonValueKind.String
                    || !string.Equals(pm.GetString(), "debt", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!loggedSampleRow)
                {
                    // Диагностика для подтверждения реального имени поля — см. комментарий у метода.
                    PosLogger.Log($"[DEBUG] Debt sale row sample: {sale.GetRawText()}", "SHIFT");
                    loggedSampleRow = true;
                }

                var saleShiftId = TryReadShiftId(sale);
                if (saleShiftId is null)
                    continue;
                sawAnyShiftField = true;
                if (!string.Equals(saleShiftId, shiftId, StringComparison.Ordinal))
                    continue;

                if (!sale.TryGetProperty("id", out var saleIdEl) || saleIdEl.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(saleIdEl.GetString()))
                    continue;
                if (!sale.TryGetProperty("client", out var clientIdEl) || clientIdEl.ValueKind != System.Text.Json.JsonValueKind.String
                    || string.IsNullOrWhiteSpace(clientIdEl.GetString()))
                    continue;

                var fallbackAmount = 0m;
                if (sale.TryGetProperty("debt_amount", out var totalEl) || sale.TryGetProperty("total", out totalEl))
                {
                    fallbackAmount = totalEl.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Number => totalEl.TryGetDecimal(out var d) ? d : 0m,
                        System.Text.Json.JsonValueKind.String =>
                            decimal.TryParse(totalEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d2) ? d2 : 0m,
                        _ => 0m,
                    };
                }

                matchingDebtSales.Add((saleIdEl.GetString()!, clientIdEl.GetString()!, fallbackAmount));
            }

            if (!sawAnyShiftField)
            {
                PosLogger.Log(
                    "Debt total: ни одна долговая продажа в ответе не содержит распознанного поля " +
                    "shift_id — посчитать долг именно этой смены нельзя, возвращаем «—».", "SHIFT");
                return null;
            }

            var remainingAmounts = await Task.WhenAll(
                matchingDebtSales.Select(s => ResolveRealRemainingDebtAsync(s.SaleId, s.ClientId, s.FallbackAmount, cancellationToken))
            ).ConfigureAwait(false);

            return remainingAmounts.Sum();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Debt total computation from sales list failed: {ex.Message}", "SHIFT");
            return null;
        }
    }

    /// <summary>Тот же приём, что уже проверен в PayDebtDialog.ResolveRealRemainingDebtAsync —
    /// remaining_debt сделки, а не статичный debt_amount/total продажи. При любой ошибке (сеть,
    /// сделка не найдена и т.п.) откатывается на fallbackAmount — лучше показать чуть неточную,
    /// но не нулевую цифру, чем уронить весь расчёт долга смены из-за одной продажи.</summary>
    private async Task<decimal> ResolveRealRemainingDebtAsync(
        string saleId, string clientId, decimal fallbackAmount, CancellationToken cancellationToken)
    {
        try
        {
            // По запросу на каждую долговую продажу смены — в общем темпе массовых загрузок,
            // чтобы не упереться в ограничение частоты запросов сервера.
            var saleDetail = await NurMarketKassa.Services.Api.ApiThrottle
                .RunBulkAsync(() => _salesApi.PosSaleGetAsync(saleId, cancellationToken), cancellationToken).ConfigureAwait(false);
            var dealId = saleDetail.ValueKind == System.Text.Json.JsonValueKind.Object
                && saleDetail.TryGetProperty("deal_id", out var dealIdEl)
                && dealIdEl.ValueKind == System.Text.Json.JsonValueKind.String
                    ? dealIdEl.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(dealId))
                return fallbackAmount;

            var deal = await _salesApi.ClientDealGetAsync(clientId, dealId, cancellationToken).ConfigureAwait(false);
            if (deal.ValueKind != System.Text.Json.JsonValueKind.Object
                || !deal.TryGetProperty("remaining_debt", out var remainingEl))
                return fallbackAmount;

            var remainingText = remainingEl.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => remainingEl.GetString(),
                System.Text.Json.JsonValueKind.Number => remainingEl.GetRawText(),
                _ => null,
            };
            return remainingText is not null
                && decimal.TryParse(remainingText, NumberStyles.Any, CultureInfo.InvariantCulture, out var remaining)
                    ? remaining
                    : fallbackAmount;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Debt total: remaining-debt resolve skipped for sale {saleId}: {ex.GetType().Name}", "SHIFT");
            return fallbackAmount;
        }
    }

    private static string? TryReadShiftId(System.Text.Json.JsonElement sale)
    {
        foreach (var key in new[] { "shift_id", "shift", "construction_shift_id", "construction_shift", "cashbox_shift_id" })
        {
            if (!sale.TryGetProperty(key, out var v))
                continue;

            if (v.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();

            if (v.ValueKind == System.Text.Json.JsonValueKind.Object && v.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(idEl.GetString()))
                return idEl.GetString();
        }

        return null;
    }

    /// <summary>См. catch(ApiException 404) в CloseShiftAsync — спрашивает у сервера ID
    /// реально открытой сейчас смены для текущей кассы.</summary>
    private async Task<string?> TryRecoverActiveShiftIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var list = await _shiftApi.ConstructionShiftsListAsync(openOnly: true, ct: cancellationToken).ConfigureAwait(false);
            return ShiftHelper.PickOpenShiftId(list, PosApp.PosCashboxId, PosApp.CurrentUserId);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Active shift id recovery failed: {ex}", "SHIFT");
            return null;
        }
    }

    public async Task<CashShiftReportResult> GenerateXReportAsync(decimal? currentBalance, CancellationToken cancellationToken = default)
    {
        var report = BuildReportText("X", PosApp.ActiveShiftId, currentBalance);
        return new CashShiftReportResult(report, await PrintReportAsync(report, cancellationToken).ConfigureAwait(false));
    }

    public async Task<CashShiftReportResult> GenerateZReportAsync(decimal? closingCash, CancellationToken cancellationToken = default)
    {
        var report = BuildReportText("Z", PosApp.ActiveShiftId, closingCash);
        return new CashShiftReportResult(report, await PrintReportAsync(report, cancellationToken).ConfigureAwait(false));
    }

    public Task<bool> PrintReportAsync(string reportText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reportText))
            return Task.FromResult(false);

        return _receiptPrinter.PrintReceiptAsync(
            new CartSnapshot
            {
                CartJson = "{}",
                ReceiptText = reportText,
                OfflineNote = "SHIFT REPORT",
                PaymentMethodKey = null,
                CashReceived = null,
            },
            cancellationToken);
    }

    private async Task<string?> EnsurePosCashboxIdAsync(CancellationToken cancellationToken)
    {
        // 2026-09-14, живой баг: "Оплата не прошла — cashbox_id: Касса не найдена или не
        // принадлежит этому филиалу". PosApp.PosCashboxId — это ИМЕННО то значение, которое
        // PosCheckoutService.BuildCheckoutRequestBody кладёт в cashbox_id при оплате (см. её
        // комментарий). Раньше, если оно уже было чем-то заполнено (восстановлено из прошлой
        // сессии), этот метод возвращал его как есть, без единой проверки — тот же класс бага,
        // что уже был исправлен в MainWindow.RefreshShiftStateAsync для ДРУГОГО, чисто
        // UI-слойного холдера ID кассы (App.PosCashboxId/NurMarketKassa.App.PosCashboxId), но
        // тот фикс на PosApp.PosCashboxId не влиял вовсе — отдельная переменная, синхронизация
        // между ними односторонняя (PosAppBridge.SyncToSession читает ИЗ PosApp, не пишет В
        // него). Если админ на сервере переназначил кассира на другой филиал или переместил/
        // удалил кассу, старый ID так и оставался тут навсегда — реальный источник ошибки.
        // Теперь уже сохранённый ID тоже сверяется со свежим списком касс перед открытием
        // смены (смена открывается один раз в начале работы, лишний сетевой запрос здесь не
        // ощутим, в отличие от ежесекундных операций).
        var current = PosApp.PosCashboxId;
        var rawList = await _shiftApi.ConstructionCashboxesListAsync(cancellationToken).ConfigureAwait(false);

        // 2026-09-14: "current" мог оказаться кассой, которую сервер УЖЕ отверг за эту сессию
        // (PosApp.RejectedCashboxIds) — например, если PosCheckoutService.TryReassignCashboxAsync
        // не нашёл замены и оставил её как есть (см. её комментарий). Раньше эта проверка
        // смотрела только "есть ли ID в списке компании" и снова отправляла на открытие смены
        // заведомо отвергнутую кассу — открытие сразу падало с той же ошибкой.
        if (!string.IsNullOrWhiteSpace(current) && !PosApp.RejectedCashboxIds.Contains(current)
            && CartDisplayHelper.ListCashboxes(rawList).Any(c => c.Id == current))
            return current;

        // Тот же порядок выбора, что в TryReassignCashboxAsync ниже — активная и ещё не
        // отвергнутая за сессию касса в приоритете, иначе первая ещё не отвергнутая.
        var candidates = CartDisplayHelper.ListCashboxes(rawList)
            .Where(c => !PosApp.RejectedCashboxIds.Contains(c.Id))
            .ToList();
        var next = candidates.FirstOrDefault(c => c.IsActive);
        if (next.Id == null)
            next = candidates.FirstOrDefault();
        if (next.Id == null)
            return current;

        PosApp.PosCashboxId = next.Id;
        PosApp.PosCashboxDisplayName = next.DisplayName;
        return next.Id;
    }

    private static string BuildReportText(string reportType, string? shiftId, decimal? cash)
    {
        // Внесения/изъятия смены хранятся локально и не входят в баланс с сервера/офлайн-состояния.
        var operationsNet = ShiftCashOperationsNetProvider?.Invoke(shiftId) ?? 0m;

        var sb = new StringBuilder();
        sb.AppendLine($"*** {reportType}-ОТЧЕТ ***");
        sb.AppendLine($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        sb.AppendLine($"Кассир: {PosApp.CurrentUserId ?? "—"}");
        sb.AppendLine($"Смена: {shiftId ?? "—"}");
        if (operationsNet != 0m)
            sb.AppendLine($"Внесения/изъятия: {operationsNet.ToString("+0.00;-0.00", CultureInfo.InvariantCulture)} сом");

        // Скидки и оплата бонусами (2026-09-22, по просьбе владельца). Считаются локально: у
        // сервера скидка одна на чек, и бонусы в ней неотличимы от обычной скидки — касса
        // записывает обе величины при оплате (ClientLoyaltyStore.RecordSaleAdjustment).
        var adjustments = ClientLoyaltyStore.AdjustmentsForShift(shiftId);
        if (adjustments.Discounts > 0.005 || adjustments.PointsRedeemed > 0.005)
        {
            sb.AppendLine($"Скидки за смену: {adjustments.Discounts.ToString("0.00", CultureInfo.InvariantCulture)} сом ({adjustments.Receipts} чек.)");
            if (adjustments.PointsRedeemed > 0.005)
                sb.AppendLine($"  из них бонусами: {adjustments.PointsRedeemed.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        }

        // Возвраты, списания, расход и оплата долгов (2026-09-22). Считает их касса: в итогах
        // смены на сервере таких полей нет — см. ShiftEventsStore.
        var events = TariffGate.CanUseShiftAnalytics
            ? ShiftEventsStore.TotalsForShift(shiftId)
            : new Dictionary<string, double>();
        void AppendEvent(string title, string kind)
        {
            if (events.TryGetValue(kind, out var value) && value > 0.005)
                sb.AppendLine($"{title}: {value.ToString("0.00", CultureInfo.InvariantCulture)} сом");
        }

        AppendEvent("Возвраты", ShiftEventsStore.KindReturn);
        AppendEvent("Списания", ShiftEventsStore.KindWriteOff);
        AppendEvent("Расход", ShiftEventsStore.KindExpense);
        AppendEvent("Оплата долгов", ShiftEventsStore.KindDebtPayment);

        sb.AppendLine($"Остаток: {((cash ?? 0m) + operationsNet).ToString("0.00", CultureInfo.InvariantCulture)} сом");
        sb.AppendLine("------------------------------");
        sb.AppendLine("NurMarket Kassa");
        return sb.ToString();
    }
}
