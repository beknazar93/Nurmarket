using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Security;

public sealed class SensitiveDataRedactorTests
{
    [Theory]
    [InlineData("{\"password\":\"secret-123\",\"name\":\"shop\"}", "secret-123")]
    [InlineData("cashier_password = 9876; result=ok", "9876")]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("email: cashier@example.com", "cashier@example.com")]
    [InlineData("address: Bishkek, Chuy 10", "Chuy 10")]
    [InlineData("Server=localhost;Password=db-secret;Database=pos", "db-secret")]
    public void Redact_RemovesSensitiveValue(string source, string forbidden)
    {
        var result = SensitiveDataRedactor.Redact(source);

        result.Should().NotContain(forbidden);
        result.Should().Contain("***");
    }
}
