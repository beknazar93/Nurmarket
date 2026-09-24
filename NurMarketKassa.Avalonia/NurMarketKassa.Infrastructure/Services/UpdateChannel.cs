using System.Security.Cryptography;
using System.Text;

namespace NurMarketKassa.Services;

/// <summary>Канал обновлений кассы: обычный — для клиентов, тестовый — для тестировщиков
/// (2026-09-24, просьба владельца: «обновление у клиентов только после завершения теста,
/// тестерам — код на скачивание»).
///
/// Новая версия сначала выходит на GitHub как тестовая (pre-release). Обычный канал её не
/// видит: клиенту на «Проверить обновления» касса отвечает, что версия в тестировании.
/// Тестировщик вводит код в «Настройки → Обновления» — касса переключается на тестовый канал и
/// видит pre-release. Когда тестирование закончено, с релиза снимают пометку «тестовый», и
/// обновление получают все.
///
/// Сам код в исходниках не хранится — только его SHA-256: исходники лежат на GitHub.</summary>
public static class UpdateChannel
{
    private const string TesterCodeSha256 = "e1c603f329be5e74dd11488cbc8570acd6cebaf7b6241964b05b69232f20b18e";

    /// <summary>Касса в тестовом канале — код введён и верный.</summary>
    public static bool IsTester => IsValid(UserPreferences.Instance.UpdateTesterCode);

    /// <summary>Включить тестовый канал. false — код неверный, ничего не меняется.</summary>
    public static bool TryActivate(string? code)
    {
        if (!IsValid(code))
            return false;

        UserPreferences.Instance.UpdateTesterCode = Normalize(code);
        UserPreferences.Instance.SaveToDisk();
        return true;
    }

    public static void Deactivate()
    {
        UserPreferences.Instance.UpdateTesterCode = "";
        UserPreferences.Instance.SaveToDisk();
    }

    private static string Normalize(string? code) => (code ?? "").Trim().ToUpperInvariant();

    private static bool IsValid(string? code)
    {
        var normalized = Normalize(code);
        if (normalized.Length == 0)
            return false;

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return string.Equals(hash, TesterCodeSha256, StringComparison.OrdinalIgnoreCase);
    }
}
