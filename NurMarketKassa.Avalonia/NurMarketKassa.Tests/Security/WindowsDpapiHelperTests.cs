using System.Text;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Security;

/// <summary>
/// БЕЗОПАСНОСТЬ (OWASP A02 Cryptographic Failures): защита машинно-локальных секретов
/// через <see cref="WindowsDpapiHelper"/>.
/// <para>
/// Тесты работают только в памяти: DPAPI-scope <c>CurrentUser</c> доступен тестовому процессу
/// без каких-либо особых прав, файлы пользователя не создаются и не изменяются.
/// Целевой фреймворк проекта — <c>net8.0-windows</c>, поэтому платформенных пропусков не требуется.
/// </para>
/// </summary>
public sealed class WindowsDpapiHelperTests
{
    [Theory]
    [InlineData("секретный-токен-123")]
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature")]
    [InlineData("a")]
    [InlineData("!@#$%^&*()_+-=[]{};':\",./<>?\\|`~")]
    [InlineData("многобайтовый юникод 🔐 テスト")]
    public void ProtectToBase64_ThenUnprotect_RoundTripsExactly(string secret)
    {
        var protectedValue = WindowsDpapiHelper.ProtectToBase64(secret);

        protectedValue.Should().NotBeNullOrEmpty();
        WindowsDpapiHelper.UnprotectFromBase64(protectedValue).Should().Be(secret);
    }

    [Fact]
    public void ProtectToBase64_DoesNotLeakPlainTextIntoOutput()
    {
        const string secret = "секретный-токен-123";

        var protectedValue = WindowsDpapiHelper.ProtectToBase64(secret);

        protectedValue.Should().NotContain(secret, "шифротекст не должен содержать исходную строку");
        protectedValue.Should().NotBe(secret);

        // Ни в Base64-представлении, ни в сырых байтах шифротекста не должно быть открытых байтов секрета.
        var rawProtected = Convert.FromBase64String(protectedValue);
        var plainBytes = Encoding.UTF8.GetBytes(secret);
        IndexOfSequence(rawProtected, plainBytes).Should().Be(
            -1,
            "открытый текст обязан отсутствовать в защищённых байтах — иначе это no-op, а не шифрование");

        var base64OfPlain = Convert.ToBase64String(plainBytes).TrimEnd('=');
        protectedValue.Should().NotContain(base64OfPlain);
    }

    [Fact]
    public void ProtectToBase64_ProducesDifferentCipherTextForSameInput()
    {
        const string secret = "повторяемый-секрет";

        var first = WindowsDpapiHelper.ProtectToBase64(secret);
        var second = WindowsDpapiHelper.ProtectToBase64(secret);

        first.Should().NotBe(second, "DPAPI добавляет соль/IV — детерминированный шифротекст выдавал бы повторы секретов");
        WindowsDpapiHelper.UnprotectFromBase64(first).Should().Be(secret);
        WindowsDpapiHelper.UnprotectFromBase64(second).Should().Be(secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ProtectToBase64_OnEmptyInput_ReturnsEmpty_WithoutThrowing(string? input)
    {
        var act = () => WindowsDpapiHelper.ProtectToBase64(input);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void UnprotectFromBase64_OnEmptyInput_ReturnsEmpty_WithoutThrowing(string? input)
    {
        var act = () => WindowsDpapiHelper.UnprotectFromBase64(input);

        act.Should().NotThrow();
        act().Should().BeEmpty();
    }

    [Theory]
    [InlineData("не-base64-вообще")]
    [InlineData("###")]
    [InlineData("AAAA")]                     // валидный Base64, но не DPAPI-блоб → CryptographicException
    [InlineData("QUJDREVGR0g=")]             // "ABCDEFGH" в Base64 → не DPAPI-блоб
    [InlineData("A")]                        // некорректная длина Base64 → FormatException
    public void UnprotectFromBase64_OnCorruptedInput_ReturnsEmpty_InsteadOfThrowing(string corrupted)
    {
        var act = () => WindowsDpapiHelper.UnprotectFromBase64(corrupted);

        act.Should().NotThrow<FormatException>();
        act.Should().NotThrow();
        act().Should().BeEmpty("повреждённое хранилище секретов обязано вести к «нет секрета», а не к краху кассы");
    }

    [Fact]
    public void UnprotectFromBase64_OnTamperedCipherText_ReturnsEmpty()
    {
        var protectedValue = WindowsDpapiHelper.ProtectToBase64("секрет-для-подмены");
        var bytes = Convert.FromBase64String(protectedValue);

        // Порча полезной нагрузки: DPAPI проверяет целостность и обязан отказать.
        bytes[^1] ^= 0xFF;
        bytes[bytes.Length / 2] ^= 0x5A;

        WindowsDpapiHelper.UnprotectFromBase64(Convert.ToBase64String(bytes)).Should().BeEmpty();
    }

    private static int IndexOfSequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return -1;

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] == needle[j])
                    continue;
                match = false;
                break;
            }

            if (match)
                return i;
        }

        return -1;
    }
}
