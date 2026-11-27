namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-26, жалоба «скорость интернета ИИ при звонке очень тормозит» (журнал: «сводка по частям (3; Periods, Debts) за 12078 мс» при заявленных 6 с):
/// части сводки магазина собираются параллельно, но ждали их ПО ОЧЕРЕДИ — каждая по 6 с заново, и медленный сервер растягивал ожидание до 6 × N с.
/// Теперь на всю сводку — ОДИН общий срок: часть, которая уже готова, берётся сразу, а не готовая к сроку — пропускается.
/// </summary>
public static class AiDeadline
{
    /// <summary>Результат задачи, если она готова или успеет до общего срока <paramref name="deadline"/>; иначе Done = false (задача продолжает работу и ляжет в кэш).
    /// Ошибка задачи пробрасывается как есть.</summary>
    public static async Task<(bool Done, T? Value)> WithinAsync<T>(Task<T> task, Task deadline)
    {
        if (!task.IsCompleted && await Task.WhenAny(task, deadline).ConfigureAwait(false) != task)
            return (false, default);
        return (true, await task.ConfigureAwait(false));
    }
}
