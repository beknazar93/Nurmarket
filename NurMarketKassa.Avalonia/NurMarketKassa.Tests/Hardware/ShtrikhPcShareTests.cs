using FluentAssertions;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06: доступ к сетевой папке весов РС-200 с логином и паролем Windows весов (WNetAddConnection2 через подставную обёртку — без настоящей сети):
/// коды ошибок Windows → понятные причины (53/67 нет связи, 86/1326 неверный логин или пароль, 5 нет прав), конфликт учётных данных 1219, отключение после записи.</summary>
[Collection("UserPreferences")]
public sealed class ShtrikhPcShareTests : IDisposable
{
    private sealed class FakeConnector : IShtrikhPcShareConnector
    {
        public Queue<int> Codes { get; } = new();
        public List<string> Calls { get; } = new();
        public int Delay { get; set; }

        public int Connect(string share, string user, string password)
        {
            Calls.Add($"connect {share} {user}");
            if (Delay > 0)
                Thread.Sleep(Delay);
            return Codes.Count > 0 ? Codes.Dequeue() : 0;
        }

        public int Disconnect(string share)
        {
            Calls.Add($"disconnect {share}");
            return 0;
        }
    }

    private readonly IShtrikhPcShareConnector _saved = ShtrikhPcShare.Connector;
    private readonly FakeConnector _fake = new();

    public ShtrikhPcShareTests() => ShtrikhPcShare.Connector = _fake;

    public void Dispose() => ShtrikhPcShare.Connector = _saved;

    private static ShtrikhPcSettings Unc(string login = "scale", string password = "p") =>
        new ShtrikhPcSettings { Folder = @"\\192.168.0.200\Shared", Login = login, Password = password }.Normalize();

    [Theory]
    [InlineData(0, ShtrikhPcNetStatus.Ok)]
    [InlineData(53, ShtrikhPcNetStatus.NoConnection)]
    [InlineData(67, ShtrikhPcNetStatus.NoConnection)]
    [InlineData(86, ShtrikhPcNetStatus.BadCredentials)]
    [InlineData(1326, ShtrikhPcNetStatus.BadCredentials)]
    [InlineData(5, ShtrikhPcNetStatus.AccessDenied)]
    [InlineData(1219, ShtrikhPcNetStatus.Failed)]
    [InlineData(1234, ShtrikhPcNetStatus.Failed)]
    public void Classify_WindowsErrorCodes(int code, ShtrikhPcNetStatus expected) =>
        ShtrikhPcShare.Classify(code).Should().Be(expected);

    [Fact]
    public void UncHelpers_ShareRootAndHost()
    {
        ShtrikhPcShare.IsUnc(@"\\192.168.0.200\Shared").Should().BeTrue();
        ShtrikhPcShare.IsUnc(@"C:\PC-200\Shared").Should().BeFalse();
        ShtrikhPcShare.ShareRoot(@"\\192.168.0.200\Shared\Import\PCScale.txt").Should().Be(@"\\192.168.0.200\Shared");
        ShtrikhPcShare.Host(@"\\192.168.0.200\Shared").Should().Be("192.168.0.200");
    }

    [Fact]
    public async Task Open_LocalFolder_DoesNotTouchTheNetwork()
    {
        var (status, _, lease) = await ShtrikhPcShare.OpenAsync(new ShtrikhPcSettings { Folder = @"C:\PC-200\Shared", Login = "scale" }.Normalize());

        status.Should().Be(ShtrikhPcNetStatus.Ok);
        lease.Should().BeNull();
        _fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_UncWithoutLogin_UsesCurrentUser_NoConnect()
    {
        var (status, _, lease) = await ShtrikhPcShare.OpenAsync(Unc(login: ""));

        status.Should().Be(ShtrikhPcNetStatus.Ok);
        lease.Should().BeNull();
        _fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Open_UncWithLogin_ConnectsToShareRoot_AndDisconnectsOnDispose()
    {
        var (status, _, lease) = await ShtrikhPcShare.OpenAsync(Unc());

        status.Should().Be(ShtrikhPcNetStatus.Ok);
        lease.Should().NotBeNull();
        _fake.Calls.Should().Equal(@"connect \\192.168.0.200\Shared scale");
        lease!.Dispose();
        _fake.Calls.Should().Equal(@"connect \\192.168.0.200\Shared scale", @"disconnect \\192.168.0.200\Shared");
    }

    [Fact]
    public async Task Open_CredentialConflict1219_CancelsOldConnectionAndRetries()
    {
        _fake.Codes.Enqueue(1219);

        var (status, _, lease) = await ShtrikhPcShare.OpenAsync(Unc());

        status.Should().Be(ShtrikhPcNetStatus.Ok);
        _fake.Calls.Should().Equal(@"connect \\192.168.0.200\Shared scale", @"disconnect \\192.168.0.200\Shared", @"connect \\192.168.0.200\Shared scale");
        lease.Should().NotBeNull();
    }

    [Theory]
    [InlineData(53, ShtrikhPcNetStatus.NoConnection)]
    [InlineData(67, ShtrikhPcNetStatus.NoConnection)]
    [InlineData(86, ShtrikhPcNetStatus.BadCredentials)]
    [InlineData(1326, ShtrikhPcNetStatus.BadCredentials)]
    [InlineData(5, ShtrikhPcNetStatus.AccessDenied)]
    public async Task Open_Errors_AreMappedAndNothingIsLeftConnected(int code, ShtrikhPcNetStatus expected)
    {
        _fake.Codes.Enqueue(code);

        var (status, returned, lease) = await ShtrikhPcShare.OpenAsync(Unc());

        status.Should().Be(expected);
        returned.Should().Be(code);
        lease.Should().BeNull();
        _fake.Calls.Should().NotContain(c => c.StartsWith("disconnect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Open_HangingConnection_TimesOutAsNoConnection()
    {
        // 2026-11-06: запас 1 с был мал на загруженной машине (полный прогон) — задержка подставной сети 6 с, допуск 3 с сверх таймаута.
        _fake.Delay = (int)ShtrikhPcImportWriter.AvailabilityTimeout.TotalMilliseconds + 6000;
        var started = DateTime.UtcNow;

        var (status, _, _) = await ShtrikhPcShare.OpenAsync(Unc());

        status.Should().Be(ShtrikhPcNetStatus.NoConnection);
        (DateTime.UtcNow - started).Should().BeLessThan(ShtrikhPcImportWriter.AvailabilityTimeout + TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(53, ShtrikhPcFolderCheck.NoConnection)]
    [InlineData(86, ShtrikhPcFolderCheck.BadCredentials)]
    [InlineData(1326, ShtrikhPcFolderCheck.BadCredentials)]
    [InlineData(5, ShtrikhPcFolderCheck.NoAccess)]
    public async Task CheckFolder_UsesNetworkErrors(int code, ShtrikhPcFolderCheck expected)
    {
        _fake.Codes.Enqueue(code);

        var (status, detail) = await ShtrikhPcImportWriter.CheckFolderAsync(Unc());

        status.Should().Be(expected);
        detail.Should().Be(code.ToString(), "в деталях — только код Windows, не пароль");
    }

    [Theory]
    [InlineData(67, ShtrikhPcWriteStatus.NoConnection)]
    [InlineData(1326, ShtrikhPcWriteStatus.BadCredentials)]
    [InlineData(5, ShtrikhPcWriteStatus.NoAccess)]
    public async Task Write_UsesNetworkErrors_AndWritesNothing(int code, ShtrikhPcWriteStatus expected)
    {
        _fake.Codes.Enqueue(code);

        var result = await ShtrikhPcImportWriter.WriteAsync(new[] { new ShtrikhPcItem(1, "Лук", 10m, false) }, Unc());

        result.Status.Should().Be(expected);
        result.Written.Should().Be(0);
        result.Detail.Should().Be(code.ToString(), "в деталях — только код Windows, не пароль");
    }
}
