using System.Text.Json.Serialization;

#nullable enable
namespace NurMarketKassa.Models.Auth;

public sealed class RefreshRequest
{
    [JsonPropertyName("refresh")]
    public string Refresh { get; set; } = "";
}
