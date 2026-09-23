using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Security;

public sealed class PermissionServiceTests
{
    [Fact]
    public void ExtractPermissionNames_ReadsNestedArraysAndTrueFlags()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": {
                "user": {
                  "permissions": ["can_view_settings", "can_view_sales"],
                  "can_view_shifts": true,
                  "can_view_cashier": false
                }
              }
            }
            """);

        var permissions = PermissionService.ExtractPermissionNames(document.RootElement);

        permissions.Should().BeEquivalentTo(
            "can_view_settings",
            "can_view_sales",
            "can_view_shifts");
    }

    [Fact]
    public void ExtractPermissionNames_DoesNotTreatUnrelatedStringsAsPermissions()
    {
        using var document = JsonDocument.Parse("""
            { "data": { "notes": ["can_view_settings"], "role": "cashier" } }
            """);

        PermissionService.ExtractPermissionNames(document.RootElement).Should().BeEmpty();
    }
}
