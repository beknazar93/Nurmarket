using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa;

/// <summary>
/// Статический мост POS-сессии для общих сервисов Infrastructure:
/// идентификаторы смены, кассы, пользователя и REST API клиенты сайта.
/// </summary>
public static class PosApp
{
    public static AppSettings Settings { get; set; } = null!;
    public static IAuthApiService AuthApi { get; set; } = null!;
    public static ICatalogApiService CatalogApi { get; set; } = null!;
    public static ISalesApiService SalesApi { get; set; } = null!;
    public static IShiftApiService ShiftApi { get; set; } = null!;
    public static MySqlAuditService AuditDb { get; set; } = null!;
    public static string? CurrentUserId { get; set; }
    public static string? CurrentUserDisplayName { get; set; }
    public static string? PosCashboxId { get; set; }
    public static string? PosCashboxDisplayName { get; set; }

    /// <summary>2026-09-14, живой баг: список касс с сервера (ConstructionCashboxesListAsync)
    /// не фильтруется по филиалу компании — когда сервер отвергает текущую кассу ("не
    /// принадлежит этому филиалу"), клиент не знает заранее, какая именно из оставшихся в
    /// списке касс действительно подходит, и может вслепую выбрать ДРУГУЮ, тоже неверную —
    /// без учёта уже отвергнутых кандидатов кассир мог бы бесконечно скакать между двумя-тремя
    /// неподходящими кассами на каждой продаже. Копится за сессию (PosCheckoutService.
    /// TryReassignCashboxAsync / CashShiftService.TryReassignCashboxAsync), гарантируя, что
    /// повторный подбор пройдёт по списку до первой ещё не отвергнутой кассы, а не зациклится.</summary>
    public static HashSet<string> RejectedCashboxIds { get; } = new(StringComparer.OrdinalIgnoreCase);
    // 2026-11-06 (аудит Этапа 2, С-1): «эпоха» смены — растёт при каждой смене ActiveShiftId (открытие/закрытие/подмена); по ней отбрасывается заранее запрошенный
    // список открытых смен, если смена за это время менялась (иначе закрытая смена «воскресала» из старого списка).
    private static string? _activeShiftId;
    private static int _shiftEpoch;
    public static string? ActiveShiftId
    {
        get => Volatile.Read(ref _activeShiftId);
        set
        {
            if (!string.Equals(Volatile.Read(ref _activeShiftId), value, StringComparison.Ordinal))
                Interlocked.Increment(ref _shiftEpoch);
            Volatile.Write(ref _activeShiftId, value);
        }
    }
    public static int ShiftEpoch => Volatile.Read(ref _shiftEpoch);
    public static bool IsOfflineBootstrap { get; set; }
    public static string? OfflineBootstrapMessage { get; set; }
}
