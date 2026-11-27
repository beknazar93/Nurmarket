using System.Text;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Security;

/// <summary>2026-11-27: вход кассира по QR-бейджу — формат кода (в т.ч. сканер в русской раскладке), хэш, реестр (выпуск, отзыв,
/// срок, PIN), защита от перебора, зашифрованные сохранённые входы, сам вход (CashierBadgeLogin) на подделках сервиса входа.</summary>
public sealed class CashierBadgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "badge-" + Guid.NewGuid().ToString("N")[..8]);
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    public CashierBadgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    // ── формат кода ─────────────────────────────────────────────────────────

    [Fact]
    public void Generate_ThenParse_RoundTrips_AndHasPrefix()
    {
        var (code, id, secret) = CashierBadgeCode.Generate();
        code.Should().StartWith("NMBADGE:v1:");
        id.Should().HaveLength(CashierBadgeCode.IdHexLength);
        secret.Should().HaveLength(CashierBadgeCode.SecretHexLength);
        CashierBadgeCode.TryParse(code, out var pid, out var psec).Should().BeTrue();
        pid.Should().Be(id);
        psec.Should().Be(secret);
        CashierBadgeCode.Generate().Code.Should().NotBe(code, "каждый бейдж — новый случайный ключ");
    }

    [Fact]
    public void Parse_ScannerInRussianLayout_StillReadsTheBadge()
    {
        var (code, id, secret) = CashierBadgeCode.Generate();
        var typed = ToRussianLayout(code);
        typed.Should().NotContain("NMBADGE", "проверяем именно ввод в раскладке ЙЦУКЕН");
        CashierBadgeCode.LooksLikeBadge(typed).Should().BeTrue();
        CashierBadgeCode.TryParse(typed, out var pid, out var psec).Should().BeTrue();
        pid.Should().Be(id);
        psec.Should().Be(secret);
        // CapsLock / нижний регистр.
        CashierBadgeCode.TryParse(code.ToLowerInvariant(), out _, out _).Should().BeTrue();
    }

    [Fact]
    public void ExtractFromInput_IgnoresTextTypedBeforeTheScan()
    {
        var (code, _, _) = CashierBadgeCode.Generate();
        CashierBadgeCode.ExtractFromInput("kassir@" + code).Should().Be(code);
        CashierBadgeCode.ExtractFromInput("kassir@mail.kg").Should().BeNull();
    }

    [Theory]
    [InlineData("NMBADGE:v1:abc:def")]
    [InlineData("NMBADGE:v2:0123456789ab:0123456789abcdef0123456789abcdef")]
    [InlineData("NMBADGE:v1:0123456789ab:0123456789abcdef0123456789abcdeZ")]
    [InlineData("NMBADGE:v1:0123456789ab:0123456789abcdef0123456789abcdef:extra")]
    [InlineData("NMBADGE")]
    public void Malformed_IsBadgeButNotParsed(string raw)
    {
        CashierBadgeCode.LooksLikeBadge(raw).Should().BeTrue();
        CashierBadgeCode.TryParse(raw, out _, out _).Should().BeFalse();
        CashierBadgeRegistry.Verify(raw, Array.Empty<CashierBadgeRecord>(), Now).Status.Should().Be(CashierBadgeStatus.Malformed);
    }

    [Theory]
    [InlineData("4607001234567")]
    [InlineData("2200123001234")]
    [InlineData("NURCRM996700123456")]
    [InlineData("kassir@mail.kg")]
    [InlineData("")]
    public void OrdinaryCodes_AreNotBadges(string raw)
    {
        CashierBadgeCode.LooksLikeBadge(raw).Should().BeFalse();
        CashierBadgeRegistry.Verify(raw, Array.Empty<CashierBadgeRecord>(), Now).Status.Should().Be(CashierBadgeStatus.NotBadge);
    }

    [Fact]
    public void Hash_IsDeterministic_AndRegistryNeverContainsTheSecret()
    {
        var (list, code, record) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "42", "Айгуль", "a@nbs.kg", Now);
        CashierBadgeCode.TryParse(code, out var id, out var secret).Should().BeTrue();
        record.Hash.Should().Be(CashierBadgeCode.HashSecret(id, secret));
        CashierBadgeCode.HashEquals(record.Hash, record.Hash.ToUpperInvariant()).Should().BeTrue();
        var json = CashierBadgeRegistry.Serialize(list);
        json.Should().NotContain(secret).And.NotContain(code);
        CashierBadgeRegistry.Parse(json).Single().Hash.Should().Be(record.Hash);
    }

    // ── проверка по реестру ─────────────────────────────────────────────────

    [Fact]
    public void Verify_Ok_Unknown_WrongSecret()
    {
        var (list, code, record) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "42", "Айгуль", null, Now);
        var ok = CashierBadgeRegistry.Verify(code, list, Now);
        ok.Status.Should().Be(CashierBadgeStatus.Ok);
        ok.Record!.EmployeeId.Should().Be("42");

        var forged = CashierBadgeCode.Format(record.Id, new string('0', CashierBadgeCode.SecretHexLength));
        CashierBadgeRegistry.Verify(forged, list, Now).Status.Should().Be(CashierBadgeStatus.WrongSecret);

        var other = CashierBadgeCode.Generate().Code;
        CashierBadgeRegistry.Verify(other, list, Now).Status.Should().Be(CashierBadgeStatus.Unknown);
    }

    [Fact]
    public void Verify_RevokedAndExpired()
    {
        var (list, code, _) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "42", "Айгуль", null, Now, TimeSpan.FromDays(30));
        CashierBadgeRegistry.Verify(code, list, Now.AddDays(29)).Status.Should().Be(CashierBadgeStatus.Ok);
        CashierBadgeRegistry.Verify(code, list, Now.AddDays(31)).Status.Should().Be(CashierBadgeStatus.Expired);

        CashierBadgeRegistry.Revoke(list, "42", Now.AddDays(1)).Should().BeTrue();
        CashierBadgeRegistry.Verify(code, list, Now.AddDays(2)).Status.Should().Be(CashierBadgeStatus.Revoked);
        CashierBadgeRegistry.Revoke(list, "42", Now.AddDays(3)).Should().BeFalse("отзывать больше нечего");
    }

    [Fact]
    public void Issue_RevokesThePreviousBadgeOfTheSameEmployee_Only()
    {
        var (l1, oldCode, _) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "42", "Айгуль", null, Now);
        var (l2, otherCode, _) = CashierBadgeRegistry.Issue(l1, "7", "Бакыт", null, Now);
        var (l3, newCode, newRecord) = CashierBadgeRegistry.Issue(l2, "42", "Айгуль", null, Now.AddMinutes(1));
        CashierBadgeRegistry.Verify(oldCode, l3, Now.AddMinutes(2)).Status.Should().Be(CashierBadgeStatus.Revoked);
        CashierBadgeRegistry.Verify(newCode, l3, Now.AddMinutes(2)).Status.Should().Be(CashierBadgeStatus.Ok);
        CashierBadgeRegistry.Verify(otherCode, l3, Now.AddMinutes(2)).Status.Should().Be(CashierBadgeStatus.Ok);
        CashierBadgeRegistry.ActiveFor(l3, "42", Now.AddMinutes(2))!.Id.Should().Be(newRecord.Id);
    }

    [Fact]
    public void Pin_SetAndVerify()
    {
        var (list, _, record) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "42", null, null, Now, pin: "4821");
        record.RequiresPin.Should().BeTrue();
        CashierBadgeRegistry.VerifyPin(record, "4821").Should().BeTrue();
        CashierBadgeRegistry.VerifyPin(record, "4822").Should().BeFalse();
        CashierBadgeRegistry.VerifyPin(record, "").Should().BeFalse();
        CashierBadgeRegistry.Serialize(list).Should().NotContain("4821");
        CashierBadgeRegistry.IsValidPin("12").Should().BeFalse();
        CashierBadgeRegistry.IsValidPin("12ab").Should().BeFalse();
        CashierBadgeRegistry.IsValidPin("123456789").Should().BeFalse();
        var noPin = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "1", null, null, Now).Record;
        CashierBadgeRegistry.VerifyPin(noPin, null).Should().BeTrue("без PIN хватает QR");
    }

    [Fact]
    public void Parse_GarbageOrHugeJson_IsEmptyRegistry()
    {
        CashierBadgeRegistry.Parse("{not json").Should().BeEmpty();
        CashierBadgeRegistry.Parse(null).Should().BeEmpty();
        CashierBadgeRegistry.Parse(new string('x', CashierBadgeRegistry.MaxJsonBytes + 1)).Should().BeEmpty();
    }

    // ── защита от перебора ──────────────────────────────────────────────────

    [Fact]
    public void Throttle_LocksAfterFiveFailures_DoublesAndPersists_ResetsOnSuccess()
    {
        var path = Path.Combine(_dir, "attempts.json");
        var t = new CashierBadgeThrottle(path);
        for (var i = 0; i < CashierBadgeThrottle.FreeAttempts - 1; i++)
            t.RegisterFailure(Now);
        t.LockedFor(Now).Should().BeNull("4 ошибки — ещё без паузы");
        t.RegisterFailure(Now);
        t.LockedFor(Now).Should().Be(TimeSpan.FromSeconds(30));
        t.RegisterFailure(Now);
        t.LockedFor(Now).Should().Be(TimeSpan.FromSeconds(60));

        new CashierBadgeThrottle(path).LockedFor(Now).Should().Be(TimeSpan.FromSeconds(60), "перезапуск кассы паузу не снимает");
        t.LockedFor(Now.AddSeconds(61)).Should().BeNull();

        for (var i = 0; i < 30; i++)
            t.RegisterFailure(Now);
        t.LockedFor(Now).Should().Be(CashierBadgeThrottle.MaxLock);

        t.RegisterSuccess();
        t.Failures.Should().Be(0);
        t.LockedFor(Now).Should().BeNull();
        File.ReadAllText(path).Should().NotContain("NMBADGE");
    }

    // ── сохранённые входы ───────────────────────────────────────────────────

    private static UserSession Session(string userId, string refresh = "refresh-1") => new()
    {
        AccessToken = "access-" + userId,
        RefreshToken = refresh,
        ExpiresAt = Now.AddMinutes(15),
        UserId = userId,
        Login = userId + "@nbs.kg",
        DisplayName = "Кассир " + userId,
        Role = "cashier",
        Permissions = new[] { "can_view_cashier" },
        LastOnlineContactAt = Now,
    };

    [Fact]
    public void Vault_SaveGetRemove_IsEncryptedOnDisk_AndFollowsTokenRotation()
    {
        var path = Path.Combine(_dir, "vault.dat");
        var vault = new CashierBadgeVault(path);
        vault.Save(Session("42", "refresh-secret-token"));
        vault.Save(Session("7"));
        vault.Count.Should().Be(2);
        new CashierBadgeVault(path).TryGet("42")!.DisplayName.Should().Be("Кассир 42");

        var raw = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        raw.Should().NotContain("refresh-secret-token", "токены на диске только зашифрованно");

        vault.OnTokensRefreshed("refresh-secret-token", "access-new", "refresh-new");
        var rotated = vault.TryGet("42")!;
        rotated.RefreshToken.Should().Be("refresh-new");
        rotated.AccessToken.Should().Be("access-new");
        vault.TryGet("7")!.RefreshToken.Should().Be("refresh-1", "чужой вход не трогается");

        vault.RetainOnly(new[] { "42" }).Should().Be(1);
        vault.Has("7").Should().BeFalse();
        vault.Remove("42").Should().BeTrue();
        File.Exists(path).Should().BeFalse("пустое хранилище удаляется");
    }

    [Fact]
    public void RememberAfterPasswordLogin_OnlyForBadgedOrPresentedEmployee()
    {
        var vault = new CashierBadgeVault(Path.Combine(_dir, "vault2.dat"));
        var (list, _, record) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "42", null, null, Now);
        var json = CashierBadgeRegistry.Serialize(list);

        CashierBadgeLogin.RememberAfterPasswordLogin(Session("7"), null, json, vault, Now);
        vault.Has("7").Should().BeFalse("у сотрудника нет бейджа — его вход не сохраняем");

        CashierBadgeLogin.RememberAfterPasswordLogin(Session("42"), null, json, vault, Now);
        vault.Has("42").Should().BeTrue();

        CashierBadgeLogin.RememberAfterPasswordLogin(Session("9"), new CashierBadgeRecord { Id = "x", EmployeeId = "9", Hash = "h" }, null, vault, Now);
        vault.Has("9").Should().BeTrue("бейдж только что предъявлен на этой кассе");
        _ = record;
    }

    // ── вход по бейджу ──────────────────────────────────────────────────────

    private sealed class FakeAuth : IOnlineOfflineAuthenticationService
    {
        public Func<AuthenticationResult> Next { get; set; } = () => AuthenticationResult.Failed(AuthenticationFailure.ServerError, "нет");
        public int AutoLogins, Logouts;
        public Task<AuthenticationResult> LoginAsync(string u, string p, bool r, CancellationToken ct = default, bool replaceDevice = false) => throw new NotSupportedException();
        public Task<AuthenticationResult> AutoLoginAsync(CancellationToken ct = default) { AutoLogins++; return Task.FromResult(Next()); }
        public Task LogoutAsync(CancellationToken ct = default) { Logouts++; return Task.CompletedTask; }
        public Task<AuthenticationResult> ContinueOfflineAsync(string u, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeStorage : IAuthSessionManager
    {
        public UserSession? Saved;
        public Task SaveSessionAsync(UserSession s) { Saved = s; return Task.CompletedTask; }
        public Task<UserSession?> LoadSessionAsync() => Task.FromResult(Saved);
        public Task ClearSessionAsync(CancellationToken ct = default) { Saved = null; return Task.CompletedTask; }
    }

    private (CashierBadgeLogin Login, FakeAuth Auth, FakeStorage Storage, CashierBadgeVault Vault, CashierBadgeThrottle Throttle, string Code, CashierBadgeRecord Record, Func<string?> Json)
        Setup(Func<CancellationToken, Task<string?>>? server = null)
    {
        var (list, code, record) = CashierBadgeRegistry.Issue(Array.Empty<CashierBadgeRecord>(), "42", "Айгуль", "a@nbs.kg", Now);
        var json = CashierBadgeRegistry.Serialize(list);
        var auth = new FakeAuth();
        var storage = new FakeStorage();
        var vault = new CashierBadgeVault(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".dat"));
        var throttle = new CashierBadgeThrottle(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));
        var login = new CashierBadgeLogin(auth, storage, vault, throttle, () => json, server ?? (_ => Task.FromResult<string?>(json)), () => Now);
        return (login, auth, storage, vault, throttle, code, record, () => json);
    }

    [Fact]
    public async Task Login_WithoutSavedLogin_AsksPasswordOnce()
    {
        var s = Setup();
        s.Login.Check(s.Code).Status.Should().Be(CashierBadgeStatus.Ok);
        s.Login.HasSavedLogin(s.Record).Should().BeFalse();
        var r = await s.Login.LoginAsync(s.Record, rememberMe: true);
        r.IsSuccess.Should().BeFalse();
        r.Failure.Should().Be(AuthenticationFailure.SessionExpired);
        s.Auth.AutoLogins.Should().Be(0, "без сохранённого входа сервер не трогаем");
    }

    [Fact]
    public async Task Login_WithSavedLogin_EntersAsThatCashier()
    {
        var s = Setup();
        s.Vault.Save(Session("42", "old-refresh"));
        s.Auth.Next = () => AuthenticationResult.Success(Session("42", "fresh-refresh"), AuthenticationMode.Online);
        var r = await s.Login.LoginAsync(s.Record, rememberMe: true);
        r.IsSuccess.Should().BeTrue();
        r.Session!.UserId.Should().Be("42");
        s.Auth.AutoLogins.Should().Be(1, "сервер подтверждает сохранённый вход");
        s.Vault.TryGet("42")!.RefreshToken.Should().Be("fresh-refresh");
        s.Auth.Logouts.Should().Be(0);
    }

    [Fact]
    public async Task Login_SavedLoginOfAnotherEmployee_IsRejectedAndSignedOut()
    {
        var s = Setup();
        s.Vault.Save(Session("42"));
        s.Auth.Next = () => AuthenticationResult.Success(Session("999"), AuthenticationMode.Online);
        var r = await s.Login.LoginAsync(s.Record, rememberMe: true);
        r.IsSuccess.Should().BeFalse();
        s.Auth.Logouts.Should().Be(1);
        s.Vault.Has("42").Should().BeFalse();
    }

    [Fact]
    public async Task Login_RevokedOnServer_ButNotYetOnThisTill_IsRejectedAndSignedOut()
    {
        string? serverJson = null;
        var s = Setup(_ => Task.FromResult(serverJson));
        var revoked = CashierBadgeRegistry.Parse(s.Json());
        CashierBadgeRegistry.Revoke(revoked, "42", Now);
        serverJson = CashierBadgeRegistry.Serialize(revoked);

        s.Vault.Save(Session("42"));
        s.Auth.Next = () => AuthenticationResult.Success(Session("42"), AuthenticationMode.Online);
        s.Login.Check(s.Code).Status.Should().Be(CashierBadgeStatus.Ok, "локальная копия ещё старая");
        var r = await s.Login.LoginAsync(s.Record, rememberMe: true);
        r.IsSuccess.Should().BeFalse();
        r.ErrorMessage.Should().Be(CashierBadgeRegistry.Message(CashierBadgeStatus.Revoked));
        s.Auth.Logouts.Should().Be(1);
        s.Vault.Has("42").Should().BeFalse();
    }

    [Fact]
    public async Task Login_ServerRegistryUnavailable_FallsBackToTillCopy()
    {
        var s = Setup(_ => throw new HttpRequestException("нет связи"));
        s.Vault.Save(Session("42"));
        s.Auth.Next = () => AuthenticationResult.Success(Session("42"), AuthenticationMode.Online);
        (await s.Login.LoginAsync(s.Record, rememberMe: true)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Login_ServerRejectsSavedLogin_ForgetsItAndAsksPassword()
    {
        var s = Setup();
        s.Vault.Save(Session("42"));
        s.Auth.Next = () => AuthenticationResult.Failed(AuthenticationFailure.SessionExpired, "Сессия истекла");
        var r = await s.Login.LoginAsync(s.Record, rememberMe: true);
        r.Failure.Should().Be(AuthenticationFailure.SessionExpired);
        s.Vault.Has("42").Should().BeFalse();
    }

    [Fact]
    public async Task Login_WithoutRememberMe_DoesNotLeaveAutoLoginFile()
    {
        var s = Setup();
        s.Vault.Save(Session("42"));
        s.Auth.Next = () => AuthenticationResult.Success(Session("42"), AuthenticationMode.Online);
        (await s.Login.LoginAsync(s.Record, rememberMe: false)).IsSuccess.Should().BeTrue();
        s.Storage.Saved.Should().BeNull();
    }

    [Fact]
    public void Check_FailuresLeadToLockout_EvenForAValidBadge()
    {
        var s = Setup();
        var forged = CashierBadgeCode.Format(s.Record.Id, new string('1', CashierBadgeCode.SecretHexLength));
        for (var i = 0; i < CashierBadgeThrottle.FreeAttempts; i++)
            s.Login.Check(forged).Status.Should().Be(CashierBadgeStatus.WrongSecret);
        var locked = s.Login.Check(s.Code);
        locked.Status.Should().Be(CashierBadgeStatus.LockedOut);
        locked.Wait.Should().NotBeNull();
        CashierBadgeRegistry.Message(CashierBadgeStatus.LockedOut, locked.Wait).Should().Contain("30");
    }

    [Fact]
    public void CheckPin_WrongPinCountsAsFailure()
    {
        var s = Setup();
        CashierBadgeRegistry.SetPin(s.Record, "1234");
        s.Login.CheckPin(s.Record, "0000").Should().BeFalse();
        s.Throttle.Failures.Should().Be(1);
        s.Login.CheckPin(s.Record, "1234").Should().BeTrue();
    }

    /// <summary>Как печатает сканер-клавиатура при русской раскладке: латинские клавиши → буквы ЙЦУКЕН, «:» (Shift+;) → «Ж».</summary>
    private static string ToRussianLayout(string text)
    {
        const string latin = "qwertyuiop[]asdfghjkl;'zxcvbnm,.`";
        const string cyr = "йцукенгшщзхъфывапролджэячсмитьбюё";
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c == ':')
            {
                sb.Append('Ж');
                continue;
            }
            var i = latin.IndexOf(char.ToLowerInvariant(c));
            sb.Append(i < 0 ? c : char.IsUpper(c) ? char.ToUpperInvariant(cyr[i]) : cyr[i]);
        }
        return sb.ToString();
    }
}
