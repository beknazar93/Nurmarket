using NurMarketKassa.Models;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28: последний полученный с сервера список сотрудников (id → карточка) — чтобы списки и подписи (роль, телефон)
/// не ходили на сервер при каждой перерисовке. Обновляется при каждом GetEmployeesAsync, сбрасывается после создания/правки.
/// Только для отображения: права и решения берутся из свежего ответа сервера.</summary>
public static class EmployeeDirectory
{
    private static readonly object Gate = new();
    private static Dictionary<string, EmployeeInfoDto> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сотрудник сервера удалён/изменён здесь — следующий показ перечитает список.</summary>
    public static event Action? Changed;

    public static void Update(IEnumerable<EmployeeInfoDto> employees)
    {
        var map = new Dictionary<string, EmployeeInfoDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in employees)
            if (!string.IsNullOrWhiteSpace(e.Id))
                map[e.Id!] = e;
        lock (Gate)
            _byId = map;
        Changed?.Invoke();
    }

    public static EmployeeInfoDto? TryGet(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (Gate)
            return _byId.TryGetValue(id, out var e) ? e : null;
    }

    /// <summary>После создания/правки/удаления: кэш подписей устарел.</summary>
    public static void Invalidate()
    {
        lock (Gate)
            _byId = new Dictionary<string, EmployeeInfoDto>(StringComparer.OrdinalIgnoreCase);
    }
}
