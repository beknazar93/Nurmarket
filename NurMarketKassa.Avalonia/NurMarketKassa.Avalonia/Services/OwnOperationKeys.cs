namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-18, шестой круг ревью: ключи своих операций с долгом в окне (новая оплата, повтор, решение по замку, взносы
/// старого пути). Оповещение PosDataEvents.ClientDebtChanged с таким ключом окно-автор не перечитывает. Общий для окна кассы
/// (PayDebtDialog) и окна владельца (AcceptDebtPaymentWindow); не больше <see cref="Capacity"/> последних ключей.</summary>
internal sealed class OwnOperationKeys
{
    private const int Capacity = 64;
    private readonly object _sync = new();
    private readonly HashSet<string> _set = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public void Remember(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        lock (_sync)
        {
            if (!_set.Add(key))
                return;
            _order.Enqueue(key);
            while (_order.Count > Capacity)
                _set.Remove(_order.Dequeue());
        }
    }

    public bool Contains(string? key)
    {
        if (key is null)
            return false;
        lock (_sync)
            return _set.Contains(key);
    }
}
