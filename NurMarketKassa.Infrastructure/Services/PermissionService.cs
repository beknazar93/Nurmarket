using System.Text.Json;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

/// <summary>Fail-closed authorization for cashier actions.</summary>
public sealed class PermissionService : IPermissionService
{
    private static readonly string[] PermissionContainers =
        ["permissions", "user_permissions", "access_rights", "rights"];
    private static readonly HashSet<string> PrivilegedRoles = new(
        ["admin", "administrator", "owner", "superadmin", "super_admin"],
        StringComparer.OrdinalIgnoreCase);

    private readonly IAuthApiService _authApi;

    public PermissionService(IAuthApiService authApi) => _authApi = authApi;

    public bool HasPermission(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        var user = OfflineAuthSessionStore.ResolveUserObject(_authApi.UserPayload);
        if (HasPrivilegedRole(user))
            return true;
        return ExtractPermissionNames(_authApi.UserPayload).Contains(permission);
    }

    public void Demand(string permission)
    {
        if (!HasPermission(permission))
            throw new PermissionDeniedException(permission);
    }

    internal static HashSet<string> ExtractPermissionNames(JsonElement payload)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(payload, result, 0);
        return result;
    }

    private static void Visit(JsonElement element, HashSet<string> result, int depth)
    {
        if (depth > 5)
            return;
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                    result.Add(name);
                else
                    Visit(item, result, depth + 1);
            }
            return;
        }
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.StartsWith("can_", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind is JsonValueKind.True)
            {
                result.Add(property.Name);
                continue;
            }

            if (PermissionContainers.Contains(property.Name, StringComparer.OrdinalIgnoreCase) ||
                property.Name is "user" or "profile" or "data" or "cashier")
            {
                Visit(property.Value, result, depth + 1);
            }
        }
    }

    private static bool HasPrivilegedRole(JsonElement user)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var key in new[] { "role", "user_role", "position" })
        {
            if (user.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
                value.GetString() is { } role && PrivilegedRoles.Contains(role))
                return true;
        }
        return false;
    }
}
