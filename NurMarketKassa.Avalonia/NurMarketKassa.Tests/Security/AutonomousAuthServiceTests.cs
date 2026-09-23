using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Security;

/// <summary>
/// Автономный (офлайн, без NurCRM) режим — локальный логин/пароль (2026-09-10). Пишет в ту же
/// изолированную от реальной кассы БД, что и <see cref="DatabaseServiceInjectionTests"/> (см. её
/// комментарий о пути AppDomain.CurrentDomain.BaseDirectory/data/pos_local.db — гарантированно
/// НЕ каталог кассы). Использует те же логин/пароль, что владелец использовал для живого теста
/// (test@ofline.kg / 12345678), чтобы проверить именно тот путь, который он реально нажимал.
/// </summary>
[Collection("CatalogMemoryIndexTests")]
public sealed class AutonomousAuthServiceTests : IDisposable
{
    private const string Email = "test@ofline.kg";
    private const string Password = "12345678";

    public AutonomousAuthServiceTests() => DatabaseService.Instance.EnsureSchema();

    public void Dispose()
    {
        // Тест сам не удаляет LocalOwnerAccount намеренно — следующий прогон Upsert'ом
        // перезапишет запись, а живая проверка владельца (email test@ofline.kg) не должна
        // случайно пропасть между прогонами теста и его собственной ручной проверкой.
    }

    [Fact]
    public void CreateLocalAccount_ThenLoginLocal_WithCorrectPassword_Succeeds()
    {
        var service = new AutonomousAuthService();

        var (created, createError) = service.CreateLocalAccount(Email, Password, "Владелец");
        created.Should().BeTrue(createError);

        service.HasLocalAccount.Should().BeTrue();
        service.IsCurrentSessionAutonomous.Should().BeFalse("вход ещё не выполнялся, только создание аккаунта");

        var (success, loginError, displayName) = service.LoginLocal(Email, Password);
        success.Should().BeTrue(loginError);
        displayName.Should().Be("Владелец");
        service.IsCurrentSessionAutonomous.Should().BeTrue("после успешного локального входа сессия обязана считаться автономной");
    }

    [Fact]
    public void LoginLocal_WithWrongPassword_Fails_AndDoesNotMarkSessionAutonomous()
    {
        var service = new AutonomousAuthService();
        service.CreateLocalAccount(Email, Password, "Владелец").Success.Should().BeTrue();

        var (success, error, displayName) = service.LoginLocal(Email, "не тот пароль");

        success.Should().BeFalse("неверный пароль обязан отклоняться");
        error.Should().NotBeNullOrWhiteSpace();
        displayName.Should().BeNull();
        service.IsCurrentSessionAutonomous.Should().BeFalse("неудачный вход не должен включать автономный режим");
    }

    [Fact]
    public void LoginLocal_IsCaseInsensitiveOnEmail_ButPasswordStaysCaseSensitive()
    {
        // 2026-09-10: пароль с буквами специально для этого теста — Password ("12345678") состоит
        // только из цифр, у них нет регистра, ToUpperInvariant() над ним ничего не меняет, из-за
        // чего первая версия теста ошибочно "проверяла" регистрочувствительность сама с собой.
        const string mixedCasePassword = "SecretPass1";
        var service = new AutonomousAuthService();
        service.CreateLocalAccount(Email, mixedCasePassword, "Владелец").Success.Should().BeTrue();

        service.LoginLocal(Email.ToUpperInvariant(), mixedCasePassword).Success
            .Should().BeTrue("email — это логин, регистр в нём не должен иметь значения (как и везде в кассе)");

        service.LoginLocal(Email, mixedCasePassword.ToUpperInvariant()).Success
            .Should().BeFalse("пароль обязан быть чувствителен к регистру — иначе это не проверка пароля");
    }

    [Fact]
    public void LoginLocal_ForUnknownEmail_FailsGracefully_DoesNotThrow()
    {
        var service = new AutonomousAuthService();

        var act = () => service.LoginLocal("no-such-account@ofline.kg", Password);

        act.Should().NotThrow();
        var (success, error, displayName) = act();
        success.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        displayName.Should().BeNull();
    }

    [Theory]
    [InlineData("", "нет @, пустая строка")]
    [InlineData("not-an-email", "нет @")]
    public void CreateLocalAccount_RejectsInvalidEmail(string invalidEmail, string because)
    {
        var service = new AutonomousAuthService();

        var (success, error) = service.CreateLocalAccount(invalidEmail, Password, null);

        success.Should().BeFalse(because);
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void CreateLocalAccount_RejectsTooShortPassword()
    {
        var service = new AutonomousAuthService();

        var (success, error) = service.CreateLocalAccount("short-pass@ofline.kg", "123", null);

        success.Should().BeFalse("пароль короче 4 символов не должен приниматься");
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void StoredCredentials_AreHashed_NotPlainPassword()
    {
        var service = new AutonomousAuthService();
        const string email = "hash-check@ofline.kg";
        service.CreateLocalAccount(email, Password, null).Success.Should().BeTrue();

        var stored = DatabaseService.Instance.FindLocalOwnerAccount(email);
        stored.Should().NotBeNull();
        stored!.Value.PasswordHash.Should().NotBe(Password, "пароль обязан храниться хешированным, не как есть");
        stored.Value.PasswordSalt.Should().NotBeNullOrWhiteSpace("хеш без соли легко атаковать словарём/радужными таблицами");
    }

    [Fact]
    public void EndAutonomousSession_ResetsTheFlag()
    {
        var service = new AutonomousAuthService();
        service.CreateLocalAccount(Email, Password, "Владелец").Success.Should().BeTrue();
        service.LoginLocal(Email, Password).Success.Should().BeTrue();
        service.IsCurrentSessionAutonomous.Should().BeTrue();

        service.EndAutonomousSession();

        service.IsCurrentSessionAutonomous.Should().BeFalse("выход/переключение на NurCRM обязано снимать флаг автономной сессии");
    }
}
