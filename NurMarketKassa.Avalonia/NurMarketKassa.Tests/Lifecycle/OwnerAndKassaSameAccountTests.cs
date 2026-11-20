using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Lifecycle;

/// <summary>2026-11-20 (требование владельца «один аккаунт владельца — и в кассе, и в программе владельца»): на одном ПК касса и программа владельца делят ОДИН ключ устройства (одно устройство для сервера, а не два),
/// но у каждой своя сессия (auth.dat / offline_auth_session.dat в своей папке — вход или потеря входа в одной не стирает вход другой); роль владельца в кассе не режется. Сеть не нужна, файлы — во временной папке.</summary>
[Collection("OwnerModeSerial")]
public sealed class OwnerAndKassaSameAccountTests : IDisposable
{
    private static readonly PropertyInfo IsOwner = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _was = AppMode.IsOwner;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-same-account-" + Guid.NewGuid().ToString("N"));

    public OwnerAndKassaSameAccountTests()
    {
        Directory.CreateDirectory(_dir);
        DeviceIdentity.DirOverride = _dir;
        DeviceIdentity.ResetForTests();
    }

    public void Dispose()
    {
        IsOwner.SetValue(null, _was);
        DeviceIdentity.DirOverride = null;
        DeviceIdentity.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void DeviceKey_IsTheSame_ForKassaAndOwnerProgram_OnOnePc()
    {
        IsOwner.SetValue(null, false);
        DeviceIdentity.ResetForTests();
        var kassaKey = DeviceIdentity.Key;

        IsOwner.SetValue(null, true); // «другая программа» — новый процесс читает тот же файл
        DeviceIdentity.ResetForTests();
        var ownerKey = DeviceIdentity.Key;

        ownerKey.Should().Be(kassaKey);
        Directory.GetFiles(_dir, "device-key.txt").Should().ContainSingle("ключ лежит в общей папке, а не в папке программы");
    }

    [Fact]
    public void DeviceKey_DiffersBetweenPcs()
    {
        var first = DeviceIdentity.Key;

        DeviceIdentity.DirOverride = Path.Combine(_dir, "other-pc");
        DeviceIdentity.ResetForTests();
        DeviceIdentity.Key.Should().NotBe(first);
    }

    [Fact]
    public void DeviceKey_DefaultFolder_DoesNotDependOnProgramMode()
    {
        // Без подмены папки ключ — в общей папке кассы (AppPaths.KassaLocal) в обоих режимах; в папку владельца он не попадает.
        // Настоящие данные не трогаем: проверка только под NURMARKET_DATA_ROOT (его ставит tools/qa/test-safe.ps1).
        if (AppPaths.DataRootOverride is null)
            return;
        DeviceIdentity.DirOverride = null;
        foreach (var owner in new[] { false, true })
        {
            IsOwner.SetValue(null, owner);
            DeviceIdentity.ResetForTests();
            var key = DeviceIdentity.Key;
            File.ReadAllText(Path.Combine(AppPaths.KassaLocal, "device-key.txt")).Trim().Should().Be(key);
        }
        File.Exists(Path.Combine(AppPaths.Local, AppPaths.OwnerFolderName, "device-key.txt")).Should().BeFalse();
        File.Delete(Path.Combine(AppPaths.KassaLocal, "device-key.txt")); // временный ключ стенда — не оставляем
    }

    [Fact]
    public void DeviceKey_FirstSimultaneousStart_BothProgramsGetOneKey()
    {
        var path = Path.Combine(_dir, "race", "device-key.txt");
        var keys = new string[16];
        Parallel.For(0, keys.Length, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => keys[i] = DeviceIdentity.ReadOrCreate(path));

        keys.Distinct().Should().ContainSingle("все, кто стартовал одновременно, берут ключ, записанный первым");
        File.ReadAllText(path).Trim().Should().Be(keys[0]);
    }

    [Fact]
    public void DeviceKey_BrokenFile_IsReplacedWithValidKey()
    {
        var path = Path.Combine(_dir, "device-key.txt");
        File.WriteAllText(path, "!!");
        var key = DeviceIdentity.ReadOrCreate(path);

        key.Length.Should().BeGreaterThanOrEqualTo(8);
        DeviceIdentity.ReadOrCreate(path).Should().Be(key);
    }

    [Fact]
    public void Sessions_AreSeparate_KassaAndOwnerHaveOwnTokenFiles()
    {
        // Раздельные файлы входа: потеря/смена входа в одной программе (401, ротация refresh) не касается другой.
        var kassaFolder = Paths(owner: false);
        var ownerFolder = Paths(owner: true);

        kassaFolder.auth.Should().NotBe(ownerFolder.auth);
        kassaFolder.offline.Should().NotBe(ownerFolder.offline);
        Path.GetFileName(Path.GetDirectoryName(kassaFolder.auth)).Should().Be("NurMarketKassa");
        Path.GetFileName(Path.GetDirectoryName(ownerFolder.auth)).Should().Be("NurMarketOwner");
        Path.GetFileName(Path.GetDirectoryName(kassaFolder.offline)).Should().Be("NurMarketKassa");
        Path.GetFileName(Path.GetDirectoryName(ownerFolder.offline)).Should().Be("NurMarketOwner");

        (string auth, string offline) Paths(bool owner)
        {
            IsOwner.SetValue(null, owner);
            var service = new SecureStorageService();
            var auth = (string)typeof(SecureStorageService).GetField("_filePath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
            var offline = (string)typeof(OfflineAuthSessionStore).GetProperty("FilePath", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            return (auth, offline);
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    public void OwnerRole_IsNotCut_WhenLoggedIntoKassa(string role)
    {
        // Вход владельца/администратора в обычную кассу разрешён: роль берётся из профиля, режим программы (AppMode.IsOwner) её не режет.
        IsOwner.SetValue(null, false);
        using var doc = JsonDocument.Parse("{\"id\":\"u1\",\"email\":\"market@nur.kg\",\"role\":\"" + role + "\"}");

        PermissionService.HasOwnerOrAdminRole(doc.RootElement).Should().BeTrue();
        PermissionService.CanEditPurchaseDocuments(doc.RootElement).Should().BeTrue();
    }
}
