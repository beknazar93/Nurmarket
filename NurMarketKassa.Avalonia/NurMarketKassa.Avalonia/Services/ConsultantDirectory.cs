using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NurMarketKassa.AvaloniaHost;

namespace NurMarketKassa.Services;

/// <summary>Сотрудники для блока «Консультант» в окне оплаты (2026-09-25). Список почти не
/// меняется, а окно оплаты открывается на каждую продажу — поэтому держим его 10 минут, а
/// процент сотрудника из профиля выплат — 5 минут, чтобы оплата не ждала сервер каждый раз.</summary>
public static class ConsultantDirectory
{
    private static readonly TimeSpan ListTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PercentTtl = TimeSpan.FromMinutes(5);
    private static readonly SemaphoreSlim ListGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, (DateTime At, double? Percent)> Percents = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<(string Id, string Name)>? _list;
    private static DateTime _listAt;

    public static async Task<IReadOnlyList<(string Id, string Name)>> ListAsync(CancellationToken ct)
    {
        await ListGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_list != null && DateTime.UtcNow - _listAt < ListTtl)
                return _list;
            var list = await App.SalesApi.ListConsultantsAsync(ct).ConfigureAwait(false);
            _list = list;
            _listAt = DateTime.UtcNow;
            return list;
        }
        finally
        {
            ListGate.Release();
        }
    }

    public static async Task<double?> DefaultPercentAsync(string userId, CancellationToken ct)
    {
        if (Percents.TryGetValue(userId, out var cached) && DateTime.UtcNow - cached.At < PercentTtl)
            return cached.Percent;
        var percent = await App.SalesApi.ConsultantDefaultPercentAsync(userId, ct).ConfigureAwait(false);
        Percents[userId] = (DateTime.UtcNow, percent);
        return percent;
    }

    /// <summary>Сбрасывает кэш — после смены аккаунта или правки процентов в окне «Зарплата».</summary>
    public static void Forget()
    {
        _list = null;
        Percents.Clear();
    }
}
