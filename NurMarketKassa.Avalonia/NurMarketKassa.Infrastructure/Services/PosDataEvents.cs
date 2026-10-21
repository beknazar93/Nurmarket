using System;

namespace NurMarketKassa.Services;

/// <summary>Сигнал «данные по продажам изменились» — чтобы открытые окна аналитики
/// обновлялись сами, а не показывали цифры на момент открытия.
///
/// 2026-09-23, по жалобе владельца: «в реальном времени не обновляется аналитика, Z/X-отчёты,
/// ABC, продажи, финансы». Окна грузили данные один раз при открытии и потом только по кнопке
/// «Обновить». Кассир пробивал чек на втором экране, владелец смотрел на первый и видел
/// прежние цифры — и не понимал, прошла продажа или нет.
///
/// Событие поднимается в самих хранилищах, а не в местах оплаты: так оно срабатывает на ВСЁ,
/// что влияет на отчёты — продажу, возврат, списание, изъятие, оплату долга, — и добавление
/// нового пути в будущем не потребует не забыть про подписку.
///
/// Поднимается из фоновых потоков (выгрузка офлайн-очереди, синхронизация), поэтому
/// подписчик обязан сам уйти на UI-поток. Подписка статическая: отписываться при закрытии окна
/// обязательно, иначе окно останется в памяти навсегда.</summary>
public static class PosDataEvents
{
    /// <summary>Что-то, влияющее на отчёты, записано в локальные хранилища.</summary>
    public static event Action? SalesChanged;

    public static void RaiseSalesChanged()
    {
        try
        {
            SalesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // Сбой в обновлении одного окна не должен ронять продажу, из которой пришёл сигнал.
            PosLogger.Log($"Оповещение об изменении продаж не доставлено: {ex.Message}", "WARNING");
        }
    }

    /// <summary>2026-10-18: долг клиента изменился (оплата в этой программе, в другой кассе или у владельца — событие
    /// LAN «debt_paid»). Аргумент — id клиента (null — неизвестно чей, перечитать всех). Открытые окна оплаты долга,
    /// «Клиенты» и «Долги клиентов» перечитывают долги с сервера. Поднимается и из фоновых потоков.</summary>
    /// <remarks>2026-10-18, пятый круг ревью: второй аргумент — ключ оплаты (null — неизвестен, например событие из LAN):
    /// окно-автор узнаёт своё оповещение по ключу и себя не перечитывает.</remarks>
    public static event Action<string?, string?>? ClientDebtChanged;

    public static void RaiseClientDebtChanged(string? clientId, string? operationKey = null)
    {
        // 2026-10-18, восьмой круг ревью (п.9): одна общая версия долгов и одна отложенная рассылка вместо самодельных
        // счётчиков и пауз в каждом окне.
        lock (DebtSync)
        {
            _debtVersion++;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                _allDebtVersion = _debtVersion;
            }
            else
            {
                _clientDebtVersion[clientId] = _debtVersion;
            }
            _pendingDebtItems.Add((string.IsNullOrWhiteSpace(clientId) ? null : clientId, operationKey));
            _debtTimer ??= new System.Threading.Timer(_ => FlushDebouncedDebtChanged(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _debtTimer.Change(DebtDebounceMs, System.Threading.Timeout.Infinite);
        }
        try
        {
            ClientDebtChanged?.Invoke(clientId, operationKey);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Оповещение об изменении долга не доставлено: {ex.Message}", "WARNING");
        }
    }

    // ── 2026-10-18, восьмой круг ревью (п.9): версии долгов и отложенное оповещение ─────────────────────────────

    /// <summary>Пауза «тишины», после которой серия оповещений (одной суммой по нескольким чекам, соседи по LAN) уходит
    /// одним DebouncedDebtChanged.</summary>
    public const int DebtDebounceMs = 3000;

    private static readonly object DebtSync = new();
    private static long _debtVersion;
    private static long _allDebtVersion;
    private static readonly System.Collections.Generic.Dictionary<string, long> _clientDebtVersion = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>2026-10-18, девятый круг: оповещения пачки — (клиент, ключ операции), чтобы окно-автор узнало свои по ключу.</summary>
    private static readonly System.Collections.Generic.List<(string? ClientId, string? OperationKey)> _pendingDebtItems = new();
    private static System.Threading.Timer? _debtTimer;

    /// <summary>Версия долгов за запуск: растёт при каждом оповещении ClientDebtChanged (любой клиент).</summary>
    public static long DebtVersion
    {
        get
        {
            lock (DebtSync)
                return _debtVersion;
        }
    }

    /// <summary>Версия долга клиента: последнее оповещение по нему или «неизвестно чьё» (что новее). Загрузка запоминает
    /// версию на момент начала: стала больше — данные клиента могли устареть, пока шла загрузка.</summary>
    public static long DebtVersionOf(string? clientId)
    {
        lock (DebtSync)
            return Math.Max(_allDebtVersion,
                !string.IsNullOrWhiteSpace(clientId) && _clientDebtVersion.TryGetValue(clientId, out var v) ? v : 0);
    }

    /// <summary>Серия оповещений о долгах — одно событие через DebtDebounceMs тишины (пачка DebtChangeBatch). Поднимается с
    /// фонового потока — подписчик уходит на UI-поток.</summary>
    public static event Action<DebtChangeBatch>? DebouncedDebtChanged;

    private static void FlushDebouncedDebtChanged()
    {
        DebtChangeBatch batch;
        lock (DebtSync)
        {
            batch = new DebtChangeBatch(_pendingDebtItems.ToArray());
            _pendingDebtItems.Clear();
        }
        if (batch.Items.Count == 0)
            return;
        try
        {
            DebouncedDebtChanged?.Invoke(batch);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Отложенное оповещение об изменении долга не доставлено: {ex.Message}", "WARNING");
        }
    }
}

/// <summary>2026-10-18, девятый круг ревью: пачка оповещений о долгах за паузу DebtDebounceMs.</summary>
public sealed class DebtChangeBatch
{
    public DebtChangeBatch(System.Collections.Generic.IReadOnlyList<(string? ClientId, string? OperationKey)> items) => Items = items;

    /// <summary>(клиент или null — «неизвестно чей», ключ операции или null — неизвестен, например из LAN).</summary>
    public System.Collections.Generic.IReadOnlyList<(string? ClientId, string? OperationKey)> Items { get; }

    /// <summary>Клиенты пачки; null — среди них «неизвестно чей» (перечитать всех).</summary>
    public System.Collections.Generic.IReadOnlyCollection<string>? Clients
    {
        get
        {
            var set = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (clientId, _) in Items)
            {
                if (clientId is null)
                    return null;
                set.Add(clientId);
            }
            return set;
        }
    }

    /// <summary>Есть ли в пачке изменение долга этого клиента (или «неизвестно чьё»), сделанное НЕ этим окном: свои окно
    /// узнаёт по ключу операции (isOwnKey).</summary>
    public bool ForeignChangeOf(string? clientId, Func<string?, bool> isOwnKey)
    {
        foreach (var (id, key) in Items)
            if ((id is null || string.Equals(id, clientId, StringComparison.OrdinalIgnoreCase)) && !isOwnKey(key))
                return true;
        return false;
    }
}
