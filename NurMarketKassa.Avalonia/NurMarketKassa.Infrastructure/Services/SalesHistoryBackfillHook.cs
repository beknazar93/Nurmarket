using System;
using System.Threading;
using System.Threading.Tasks;

namespace NurMarketKassa.Services;

/// <summary>Точка подключения догрузки истории продаж к фоновой синхронизации.
///
/// Сама догрузка живёт в приложении (ей нужен клиент продаж из хоста), а вызывает её
/// SyncService, который лежит здесь и на приложение ссылаться не может. Поэтому — делегат,
/// который приложение подставляет на старте. Пока он не подставлен, фоновый цикл просто
/// ничего не делает: это не ошибка, а нормальное состояние до инициализации хоста.</summary>
public static class SalesHistoryBackfillHook
{
    private static Func<CancellationToken, Task<int>>? _handler;

    public static void Register(Func<CancellationToken, Task<int>> handler) => _handler = handler;

    /// <summary>Возвращает число добавленных строк истории. 0 — и когда добирать нечего, и
    /// когда обработчик ещё не подключён.</summary>
    public static Task<int> RunAsync(CancellationToken ct) =>
        _handler is null ? Task.FromResult(0) : _handler(ct);
}
