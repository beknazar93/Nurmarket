using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Lifecycle;

/// <summary>2026-11-20 (ответ Умара 20.11, А.2–А.3: «device_key на ПК BEKNAZAR менялся при каждом входе — 4 разных ключа за ночь; касса должна слать desktop_pos»): ключ устройства создаётся один раз,
/// лежит в общем файле device-key.txt и не меняется между входами, перезапусками, выходом/сменой компании и обновлением (файл не в папке программы); касса и программа владельца шлют ОДИН ключ,
/// но каждая — свой client_type и заголовок X-NurMarket-Client (kassa/… + desktop_pos, owner/… + desktop_owner). Сервер — WireMock.</summary>
[Collection("OwnerModeSerial")]
public sealed class DeviceKeyStabilityTests : IDisposable
{
    private static readonly PropertyInfo IsOwner = typeof(AppMode).GetProperty(nameof(AppMode.IsOwner))!;
    private readonly bool _was = AppMode.IsOwner;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-device-key-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNurCrmServer _fake = new();

    public DeviceKeyStabilityTests()
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
        _fake.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<(string Key, string ClientType, string Header)> LoginOnceAsync()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var entry = _fake.Server.LogEntries.Last(e => e.RequestMessage.Path.Contains("/auth/login/"));
        using var body = JsonDocument.Parse(entry.RequestMessage.Body!);
        return (body.RootElement.GetProperty("device_key").GetString()!,
                body.RootElement.GetProperty("client_type").GetString()!,
                entry.RequestMessage.Headers!["X-NurMarket-Client"].First());
    }

    [Fact]
    public async Task Key_is_valid_for_the_server_and_does_not_change_between_logins_restarts_and_logout()
    {
        IsOwner.SetValue(null, false);
        var first = await LoginOnceAsync();
        first.Key.Should().MatchRegex("^[A-Za-z0-9_-]{8,64}$", "правило сервера для device_key");

        var second = await LoginOnceAsync(); // выход и новый вход без перезапуска
        DeviceIdentity.ResetForTests(); // перезапуск кассы: ключ читается из файла заново
        var afterRestart = await LoginOnceAsync();
        DeviceIdentity.ResetForTests();
        DeviceIdentity.Key.Should().Be(first.Key);

        second.Key.Should().Be(first.Key);
        afterRestart.Key.Should().Be(first.Key, "ключ не пересоздаётся: иначе сервер видит каждый вход как новое устройство (409 и закрытие старых входов)");
        File.ReadAllText(Path.Combine(_dir, "device-key.txt")).Trim().Should().Be(first.Key);
    }

    [Fact]
    public async Task Kassa_and_owner_send_one_key_but_own_client_type_and_header()
    {
        IsOwner.SetValue(null, false);
        DeviceIdentity.ResetForTests();
        var kassa = await LoginOnceAsync();

        IsOwner.SetValue(null, true);
        DeviceIdentity.ResetForTests();
        var owner = await LoginOnceAsync();

        owner.Key.Should().Be(kassa.Key, "одно устройство — один ключ");
        kassa.ClientType.Should().Be("desktop_pos");
        kassa.Header.Should().StartWith("kassa/");
        owner.ClientType.Should().Be("desktop_owner");
        owner.Header.Should().StartWith("owner/", "касса не должна слать desktop_owner под заголовком kassa/…, а владелец — наоборот");
    }

    [Fact]
    public void Key_file_stays_when_the_company_changes_and_is_not_in_the_program_folder()
    {
        CompanyStoreRegistry.Classify("device-key.txt").Should().Be(CompanyStoreRegistry.StoreKind.Terminal,
            "смена компании и выход не стирают ключ устройства — это свойство ПК");
        // Файл лежит в общей папке данных кассы (AppPaths.KassaLocal), а не рядом с exe: обновление Velopack заменяет папку программы, но не эту.
        var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        AppPaths.KassaLocal.TrimEnd(Path.DirectorySeparatorChar).Should().NotBe(appDir);
        AppPaths.KassaLocal.Should().NotStartWith(appDir + Path.DirectorySeparatorChar);
    }

    [Fact]
    public void A_file_that_another_program_is_just_writing_is_not_deleted_and_replaced()
    {
        // Первый запуск двух программ сразу: у одной файл уже создан, но ещё пуст — вторая не должна принять его за битый и подменить своим ключом.
        var path = Path.Combine(_dir, "race2", "device-key.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        const string theirs = "k0123456789abcdef0123456789abcdef";
        var writer = Task.Run(async () => { await Task.Delay(15); File.WriteAllText(path, theirs); });

        var got = DeviceIdentity.ReadOrCreate(path);
        writer.Wait();

        got.Should().Be(theirs);
        File.ReadAllText(path).Trim().Should().Be(theirs);
        Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Should().BeEmpty("временные файлы не остаются");
    }
}
