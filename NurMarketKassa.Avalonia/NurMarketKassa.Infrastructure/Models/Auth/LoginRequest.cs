using System.Text.Json.Serialization;

#nullable enable
namespace NurMarketKassa.Models.Auth;

public sealed class LoginRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    // 2026-11-13 (ответ Умара 13.11, п. 5; ТЗ 06.11): слоты устройств. Поля идут только при настоящем входе (не при проверке пароля), пустые не пишутся.
    [JsonPropertyName("client_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientType { get; set; }

    [JsonPropertyName("device_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceKey { get; set; }

    [JsonPropertyName("device_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }

    /// <summary>true — «Войти и отключить другую»: сервер отзывает чужую сессию в этом слоте (старая получит 401 session_revoked).</summary>
    [JsonPropertyName("replace_device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ReplaceDevice { get; set; }
}
