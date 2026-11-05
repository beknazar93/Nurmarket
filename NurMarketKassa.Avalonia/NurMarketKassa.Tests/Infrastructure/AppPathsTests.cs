using System.IO;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-05 (аудит №4, Л4-01): единый путь данных и защита от записи в настоящие данные кассы.
/// Переменные окружения здесь НЕ меняются (тесты идут параллельно) — проверяются чистые функции AppPaths.</summary>
public sealed class AppPathsTests
{
    [Fact]
    public void Roaming_AndLocal_AreInsideDataRoot_NotInUserProfile()
    {
        var root = Path.GetFullPath(TestDataRoot.Path);
        AppPaths.DataRootOverride.Should().Be(root);
        AppPaths.Roaming.Should().StartWith(root);
        AppPaths.Local.Should().StartWith(root);

        var realRoaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(realRoaming) && !root.StartsWith(realRoaming, StringComparison.OrdinalIgnoreCase))
            AppPaths.Roaming.Should().NotStartWith(realRoaming);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("https://app.nurcrm.kg", null)]
    [InlineData("https://APP.nurcrm.kg/", null)]
    [InlineData("http://localhost:5123", "localhost:5123")]
    [InlineData("http://127.0.0.1:9000/api", "127.0.0.1:9000")]
    public void NonProductionHostOf_OnlyForStandAddresses(string? url, string? expected) =>
        AppPaths.NonProductionHostOf(url).Should().Be(expected);

    [Fact]
    public void CheckStandIsolation_Refuses_WhenAppDataSubstituted_WithoutDataRoot()
    {
        const string real = @"C:\Users\Real\AppData\Roaming";
        const string realLocal = @"C:\Users\Real\AppData\Local";
        var message = AppPaths.CheckStandIsolation(null, @"C:\stand\Roaming", real, realLocal, realLocal);
        message.Should().NotBeNull().And.Contain("NURMARKET_DATA_ROOT").And.Contain("APPDATA");

        AppPaths.CheckStandIsolation(null, real + @"\", real, realLocal, realLocal).Should().BeNull("то же значение с косой чертой — не подмена");
        AppPaths.CheckStandIsolation(null, null, real, null, realLocal).Should().BeNull("переменных нет — нечего проверять");
        AppPaths.CheckStandIsolation(null, real, real, @"C:\stand\Local", realLocal).Should().Contain("LOCALAPPDATA");
        AppPaths.CheckStandIsolation(@"C:\stand\root", @"C:\stand\Roaming", real, realLocal, realLocal).Should().BeNull("NURMARKET_DATA_ROOT задан — подмена безвредна");
    }

    [Fact]
    public void DatabasePath_StaysInsideDataRoot_AndNeverInUserProfile()
    {
        var dbPath = Path.GetFullPath(DatabaseService.Instance.DatabasePath);
        dbPath.Should().StartWith(Path.GetFullPath(TestDataRoot.Path),
            "тесты обязаны писать только в NURMARKET_DATA_ROOT, иначе они трогают реальную БД кассы");
    }
}
