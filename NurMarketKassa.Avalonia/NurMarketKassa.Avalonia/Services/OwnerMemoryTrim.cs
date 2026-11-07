using NurMarketKassa.Services;
using System.Runtime;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-06 (В11, ПМ-1, критерий 3.5 «после закрытия раздела память возвращается к уровню ±50 МБ за 2 мин»). Журнал программы владельца (MemoryDiagnostics) показал:
/// закрытый раздел отдаёт объекты сборщику, но «куча больших объектов» (LOH: страницы каталога, строки отчётов, массивы таблиц) НЕ уплотняется — свободные дыры в ней остаются
/// занятой частной памятью процесса (пример: куча 373 МБ, LOH 328 МБ при одном открытом окне; через 8 с после сборки — 167 / 194 МБ, а частная память не упала).
/// Здесь: через <see cref="SettleDelay"/> после закрытия раздела — одна сборка с уплотнением LOH, не чаще раза в <see cref="MinSpacing"/>, не пока владелец печатает и только если
/// управляемая куча заметна (<see cref="MinManagedBytes"/>). Пауза сборки на куче 150–300 МБ — десятки миллисекунд, раз в несколько минут.
/// Решение — чистая функция <see cref="ShouldTrim"/> (проверяется тестами); сама сборка — <see cref="TrimNow"/>.</summary>
public static class OwnerMemoryTrim
{
    /// <summary>Сколько ждать после закрытия раздела: сборщик успевает освободить окно, а несколько закрытий подряд (смена языка, «Освобождение памяти») сливаются в одну сборку.</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(20);

    /// <summary>Не чаще: каждая уплотняющая сборка останавливает все потоки.</summary>
    public static readonly TimeSpan MinSpacing = TimeSpan.FromMinutes(2);

    /// <summary>Владелец печатает (клавиша была меньше этого назад) — сборку откладываем.</summary>
    public static readonly TimeSpan TypingQuiet = TimeSpan.FromSeconds(5);

    /// <summary>Управляемая куча меньше этого — уплотнять нечего.</summary>
    public const long MinManagedBytes = 120L * 1024 * 1024;

    private static long _lastTrimTicks;

    /// <summary>2026-11-06 (В16): что сделать перед сборкой (сброс кэша миниатюр). Ставит приложение.</summary>
    public static Action? BeforeTrim { get; set; }

    /// <summary>Чистое правило: можно ли делать уплотняющую сборку сейчас.</summary>
    /// <param name="nowUtc">Сейчас.</param>
    /// <param name="lastTrimUtc">Когда делали прошлую (DateTime.MinValue — не делали).</param>
    /// <param name="lastKeyUtc">Последняя клавиша владельца.</param>
    /// <param name="managedBytes">Размер управляемой кучи (GC.GetTotalMemory(false)).</param>
    public static bool ShouldTrim(DateTime nowUtc, DateTime lastTrimUtc, DateTime lastKeyUtc, long managedBytes) =>
        managedBytes >= MinManagedBytes
        && nowUtc - lastTrimUtc >= MinSpacing
        && nowUtc - lastKeyUtc >= TypingQuiet;

    /// <summary>Сделать сборку, если <see cref="ShouldTrim"/> разрешает. true — сборка выполнена. Вызывать из любого потока.</summary>
    public static bool TrimNow(DateTime lastKeyUtc)
    {
        // 2026-11-06 (В16, память): раздел закрыт — приложение (MemoryDiagnostics.Start) вешает сюда сброс кэша миниатюр до последних 48 (то, что ещё на экране, держат сами плитки);
        // остальное соберёт сборка ниже или обычная. Файл компилируется и в тестах без Avalonia, поэтому через делегат.
        try { BeforeTrim?.Invoke(); }
        catch (Exception) { /* кэш картинок — только ускорение */ }
        var now = DateTime.UtcNow;
        var last = new DateTime(Interlocked.Read(ref _lastTrimTicks), DateTimeKind.Utc);
        var before = GC.GetTotalMemory(false);
        if (!ShouldTrim(now, last, lastKeyUtc, before))
            return false;
        Interlocked.Exchange(ref _lastTrimTicks, now.Ticks);
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var after = GC.GetTotalMemory(false);
        PosLogger.Log($"ПАМЯТЬ: после закрытия раздела куча уплотнена: {before / (1024 * 1024)} → {after / (1024 * 1024)} МБ.", "INFO");
        return true;
    }

    /// <summary>Только для тестов: забыть время прошлой сборки.</summary>
    internal static void ResetForTests() => Interlocked.Exchange(ref _lastTrimTicks, 0);
}
