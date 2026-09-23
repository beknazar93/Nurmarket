using System.Text.Json.Serialization;

#nullable enable
namespace NurMarketKassa.Models.Auth;

public sealed class LoginRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}
