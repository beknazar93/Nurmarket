namespace NurMarketKassa.Core.Contracts;

public interface IShiftStateService
{
    event Action<ShiftStateSnapshot>? StateRefreshed;

    string? ActiveShiftId { get; }

    bool IsShiftOpen { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>2026-10-22 (аудит новой сборки, П1): сверка текущей смены с сервером. Сервер мог закрыть смену сам
    /// (автозакрытие в 23:00), а касса об этом не знала. Возвращает сведения о закрытой смене, если сервер её закрыл
    /// (подтверждено вторым запросом и карточкой смены); null — смена на месте, связи нет или проверять нечего.
    /// Сам ничего не меняет в состоянии кассы — это делает окно (см. MainWindow.ApplyShiftClosedByServer).</summary>
    Task<ShiftClosedByServer?> CheckServerClosedAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<ShiftClosedByServer?>(null);

    /// <summary>2026-10-22 (смена 23 ч): время открытия текущей смены (местное) для предупреждения «сервер закроет сам через 24 ч».
    /// Берётся из карточки смены один раз на смену и хранится в памяти (ShiftAgeWarning); null — смены нет, она офлайн
    /// или сервер недоступен.</summary>
    Task<DateTime?> GetActiveShiftOpenedAtAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<DateTime?>(null);
}

public sealed class ShiftStateSnapshot
{
    public string? ActiveShiftId { get; init; }
    public decimal? CashBalance { get; init; }
}

/// <summary>2026-10-22 (П1): смена, закрытая сервером без участия кассы.</summary>
public sealed class ShiftClosedByServer
{
    public string ShiftId { get; init; } = "";
    /// <summary>Когда сервер закрыл смену (местное время); null — сервер время не прислал.</summary>
    public DateTime? ClosedAt { get; init; }
    public decimal? ExpectedCash { get; init; }
    public decimal? ClosingCash { get; init; }
    public decimal? TotalSales { get; init; }
    public int? SalesCount { get; init; }
}

/// <summary>2026-10-22 (П1): запомненное «смена закрыта сервером» до открытия новой смены — по нему шапка, меню и
/// окна оплаты/возврата/внесения показывают понятный текст. Только в памяти: локальные данные смены не трогаем.</summary>
public static class ServerShiftClosedState
{
    public static ShiftClosedByServer? Current { get; set; }

    public static void Clear() => Current = null;

    /// <summary>Время закрытия «ЧЧ:ММ»; без времени — пустая строка.</summary>
    public static string TimeText(ShiftClosedByServer? info) =>
        info?.ClosedAt is { } t ? t.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>Готовый текст для блокировки операций; по-русски (Core без переводов), окна кассы переводят сами.</summary>
    public static string BlockText(ShiftClosedByServer? info) =>
        TimeText(info) is { Length: > 0 } t
            ? $"Смена закрыта сервером в {t} — откройте новую смену."
            : "Смена закрыта сервером — откройте новую смену.";
}

/// <summary>2026-10-22 (смена 23 ч): уровень предупреждения «смена идёт слишком долго». Сервер NurCRM сам закрывает смену,
/// открытую дольше 24 ч, ближайшим целым часом после opened_at + 24 ч (ТЗ п.30.39). Чистая логика без сети и денег.</summary>
public enum ShiftAgeLevel
{
    None = 0,
    /// <summary>22–23 ч: мягкая жёлтая плашка в шапке.</summary>
    Yellow = 1,
    /// <summary>23 ч и больше: плюс уведомление (один раз на смену) и строка под чеком при оплате.</summary>
    Late = 2,
    /// <summary>23:45 и больше: плашка красная.</summary>
    Red = 3,
}

public static class ShiftAgeWarning
{
    public static readonly TimeSpan YellowFrom = TimeSpan.FromHours(22);
    public static readonly TimeSpan LateFrom = TimeSpan.FromHours(23);
    public static readonly TimeSpan RedFrom = new(23, 45, 0);

    /// <summary>Текущее время; в тесте подменяется. Для живой проверки без ожидания: переменная окружения
    /// NURMARKET_FAKE_SHIFT_AGE_HOURS=N добавляет N часов к возрасту смены (только показ, на сервер не влияет).</summary>
    public static Func<DateTime> NowProvider { get; set; } = () => DateTime.Now;

    public static DateTime Now()
    {
        var now = NowProvider();
        var fake = Environment.GetEnvironmentVariable("NURMARKET_FAKE_SHIFT_AGE_HOURS");
        if (!string.IsNullOrWhiteSpace(fake)
            && double.TryParse(fake, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h))
            now = now.AddHours(h);
        return now;
    }

    private static string? _shiftId;
    private static DateTime? _openedAt;

    public static void Remember(string? shiftId, DateTime? openedAt)
    {
        _shiftId = shiftId;
        _openedAt = openedAt;
    }

    /// <summary>Запомненное время открытия для этой смены; для другой смены — null.</summary>
    public static DateTime? Get(string? shiftId) =>
        !string.IsNullOrEmpty(shiftId) && string.Equals(shiftId, _shiftId, StringComparison.OrdinalIgnoreCase) ? _openedAt : null;

    /// <summary>Когда сервер закроет смену: ближайший целый час после opened_at + 24 ч (ровно в HH:00:00 — это же время).</summary>
    public static DateTime ServerCloseAt(DateTime openedAt)
    {
        var t = openedAt.AddHours(24);
        var floor = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0, t.Kind);
        return floor == t ? t : floor.AddHours(1);
    }

    public static ShiftAgeLevel LevelFor(DateTime openedAt, DateTime now)
    {
        var age = now - openedAt;
        if (age >= RedFrom) return ShiftAgeLevel.Red;
        if (age >= LateFrom) return ShiftAgeLevel.Late;
        if (age >= YellowFrom) return ShiftAgeLevel.Yellow;
        return ShiftAgeLevel.None;
    }

    /// <summary>Сколько полных часов идёт смена.</summary>
    public static int WholeHours(DateTime openedAt, DateTime now) => (int)Math.Floor((now - openedAt).TotalHours);
}
