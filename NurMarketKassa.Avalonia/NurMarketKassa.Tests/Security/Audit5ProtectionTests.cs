using System.Net.Http;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services.Api;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Security;

/// <summary>2026-11-06 (аудит №5, зона защиты и данных): Л5-06 (PIN старшего обходится только отсутствием связи), Л5-07 (оповещение о недостаче не теряется),
/// Л5-03 (отложенные записи прежней компании снимаются), Л5-10 (сбой чтения настроек защиты не сбрасывает их). Без сети и без боевых данных:
/// файлы — во временной папке. Статические хранилища общие с другими тестами — коллекция «UserPreferences» (по очереди).</summary>
[Collection("UserPreferences")]
public sealed class Audit5ProtectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "audit5-" + Guid.NewGuid().ToString("N")[..8]);

    public Audit5ProtectionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        AntiFraudSettingsStore.DataDirOverride = null;
        AntiFraudSettingsStore.Reset();
        ShopAutomationSettings.DataDirOverride = null;
        ShopAutomationSettings.Reset();
        ShopAlerts.DataDirOverride = null;
        ShopAlerts.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка — не страшно */ }
    }

    // ───────────── Л5-06: что считается «нет связи» ─────────────

    [Fact]
    public void LatePin_OnlyRealConnectivityFailureIsUnavailable()
    {
        SyncService.IsConnectivityFailure(new HttpRequestException("нет сети")).Should().BeTrue();
        SyncService.IsConnectivityFailure(new TimeoutException()).Should().BeTrue();
        SyncService.IsConnectivityFailure(new TaskCanceledException()).Should().BeTrue("тайм-аут запроса");
        SyncService.IsConnectivityFailure(new ApiException("обрыв", null)).Should().BeTrue("нет HTTP-ответа");
        SyncService.IsConnectivityFailure(new ApiException("медленно", 408)).Should().BeTrue();
        SyncService.IsConnectivityFailure(new ApiException("шлюз", 504)).Should().BeTrue();
        SyncService.IsConnectivityFailure(new InvalidOperationException("x", new HttpRequestException())).Should().BeTrue("вложенная сетевая ошибка");

        // Ответ сервера — это отказ, а не «нет связи»: обхода PIN нет.
        foreach (var code in new[] { 400, 401, 403, 404, 422, 500, 502, 503 })
            SyncService.IsConnectivityFailure(new ApiException("ответ сервера", code)).Should().BeFalse($"HTTP {code} — отказ");
        SyncService.IsConnectivityFailure(new InvalidOperationException("прочее")).Should().BeFalse();
    }

    [Fact]
    public void CycleCount_PinBypass_OnlyWithoutConnection()
    {
        CycleCountRules.PinOutcomeAllowsFinish(LatePinStatus.Ok).Should().BeTrue();
        CycleCountRules.PinOutcomeAllowsFinish(LatePinStatus.Unavailable).Should().BeTrue("нет связи — ревизию не блокируем");
        CycleCountRules.PinOutcomeAllowsFinish(LatePinStatus.Error).Should().BeFalse("ошибка/«нет прав» сервера — отказ");
        CycleCountRules.PinOutcomeAllowsFinish(LatePinStatus.Wrong).Should().BeFalse();
        CycleCountRules.PinOutcomeAllowsFinish(LatePinStatus.Locked).Should().BeFalse();
    }

    [Theory]
    [InlineData(GuardAction.Discount, true, false)]
    [InlineData(GuardAction.Return, true, false)]
    [InlineData(GuardAction.ManualPrice, true, false)]
    [InlineData(GuardAction.OpenDrawer, true, false)]
    [InlineData(GuardAction.RemovalAfterRing, true, false)]
    [InlineData(GuardAction.ShiftShortage, true, true)]   // смену с недостачей не блокируем (запись в журнал антифрода)
    [InlineData(GuardAction.Return, false, true)]          // PIN и не требовался — сбой проверки ничего не меняет
    public void ActionGuard_FailedCheck_IsNotPermission(GuardAction action, bool pinWasRequired, bool allowed) =>
        ActionGuardPolicy.AllowWhenCheckFailed(action, pinWasRequired).Should().Be(allowed);

    [Fact]
    public async Task OwnerPin_ServerRejectionIsError_NotBypass()
    {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/auth/login/").UsingMethod("POST"))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{\"access\":\"a\",\"refresh\":\"r\",\"user\":{\"id\":\"u\"}}"));
        server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST").WithBody("*1111*"))
            .RespondWith(Response.Create().WithStatusCode(403).WithHeader("Content-Type", "application/json").WithBody("{\"detail\":\"forbidden\"}"));
        server.Given(Request.Create().WithPath("/api/users/auth/pin/verify/").UsingMethod("POST").WithBody("*2222*"))
            .RespondWith(Response.Create().WithStatusCode(500).WithHeader("Content-Type", "application/json").WithBody("{\"detail\":\"boom\"}"));
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url! });
        await api.LoginAsync("a@b.kg", "12345678");
        var sync = new SyncService(new SalesApiService(api), null!, null!, null!, null!, null!);

        var forbidden = await sync.VerifyOwnerPinForLateAsync("1111");
        forbidden.Status.Should().Be(LatePinStatus.Error, "403/«нет прав» — отказ, а не «нет связи»");
        var failing = await sync.VerifyOwnerPinForLateAsync("2222");
        failing.Status.Should().Be(LatePinStatus.Error, "500 — ошибка сервера, обхода PIN нет");
        // «Нет связи» (Unavailable) проверяется отдельно, по классификатору (LatePin_OnlyRealConnectivityFailureIsUnavailable): на живой обрыв влияет общее состояние
        // предохранителя и монитора аварий других тестов.
    }

    // ───────────── Л5-07: оповещение не теряется ─────────────

    [Fact]
    public async Task ShopAlert_TelegramDown_GoesToQueueAndIsNotLost()
    {
        ShopAlerts.DataDirOverride = _dir;
        ShopAlerts.ResetForTests();
        var rules = new ShopAlertRules { Enabled = true, QuietFromHour = 0, QuietToHour = 0 };
        var noon = new DateTime(2026, 11, 6, 12, 0, 0);

        var sent = 0;
        await ShopAlerts.NotifyCoreAsync(rules, true, "Недостача", "20 000 сом", noon, false, _ => { sent++; return Task.FromResult<string?>("HTTP 502"); });
        sent.Should().Be(1);
        ShopAlerts.PendingCount.Should().Be(1, "ошибка отправки — сообщение в очереди, а не потеряно");

        await ShopAlerts.NotifyCoreAsync(rules, true, "Недостача 2", "1 000 сом", noon, false, _ => throw new HttpRequestException("нет сети"));
        ShopAlerts.PendingCount.Should().Be(2, "сбой связи (исключение) — тоже в очередь");

        ShopAlerts.ResetForTests(); // с диска: очередь пережила перезапуск
        ShopAlerts.PendingCount.Should().Be(2);

        // Успешная отправка в очередь не кладёт.
        await ShopAlerts.NotifyCoreAsync(rules, true, "Ок", "x", noon, false, _ => Task.FromResult<string?>(null));
        ShopAlerts.PendingCount.Should().Be(2);
    }

    [Fact]
    public async Task ShopAlert_DisabledOrNoBot_IsNotQueued()
    {
        ShopAlerts.DataDirOverride = _dir;
        ShopAlerts.ResetForTests();
        var noon = new DateTime(2026, 11, 6, 12, 0, 0);
        await ShopAlerts.NotifyCoreAsync(new ShopAlertRules { Enabled = false }, true, "a", "b", noon, false, _ => Task.FromResult<string?>("err"));
        await ShopAlerts.NotifyCoreAsync(new ShopAlertRules { Enabled = true }, false, "a", "b", noon, false, _ => Task.FromResult<string?>("err"));
        ShopAlerts.PendingCount.Should().Be(0);
    }

    // ───────────── Л5-03: отложенные записи при смене компании ─────────────

    [Fact]
    public void Deferred_DetachedOnCompanySwitch_AndRestoredOnRollback()
    {
        var company = Path.Combine(_dir, "company");
        Directory.CreateDirectory(company);
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        var path = Path.Combine(company, "deferred_carts.json");
        var otherPath = Path.Combine(other, "x.json");
        try
        {
            AtomicFile.DeferWrite(path, "deferred_carts.json", "[{\"Id\":\"A\"}]");
            AtomicFile.DeferWrite(otherPath, "x.json", "{\"k\":1}");
            AtomicFile.TryRead(path).FromMemory.Should().BeTrue("состояние компании А лежит в памяти и читается первым");

            var detached = AtomicFile.DetachDeferredUnder([company]);
            detached.Count.Should().Be(1, "снимается только то, что внутри папки данных компании");
            // карантин уносит и запасной файл *.pending вместе с рабочим
            File.Delete(path + ".pending");
            var afterSwitch = AtomicFile.TryRead(path);
            afterSwitch.FromMemory.Should().BeFalse("у новой компании чужого отложенного состояния нет");
            afterSwitch.Status.Should().Be(AtomicFile.ReadStatus.Missing);
            AtomicFile.TryRead(otherPath).FromMemory.Should().BeTrue("чужая папка не затронута");

            // откат смены: прежняя компания остаётся — её отложенная запись возвращается
            AtomicFile.RestoreDeferred(detached);
            AtomicFile.TryRead(path).FromMemory.Should().BeTrue();
        }
        finally
        {
            AtomicFile.CancelDeferred(path);
            AtomicFile.CancelDeferred(otherPath);
        }
    }

    // ───────────── Л5-10: сбой чтения настроек защиты ─────────────

    [Fact]
    public void AntiFraud_LockedFile_KeepsPreviousRules_AndRefusesToOverwrite()
    {
        AntiFraudSettingsStore.DataDirOverride = _dir;
        AntiFraudSettingsStore.Reset();
        AntiFraudSettingsStore.Update(d => { d.Guard.Enabled = true; d.Guard.ReturnAmount = 1000; });
        AntiFraudSettingsStore.Update(d => d.Guard.ReturnAmount = 1000); // вторая запись — появляется копия .bak
        AntiFraudSettingsStore.Current.Guard.Enabled.Should().BeTrue();
        var file = Path.Combine(_dir, "antifraud-settings.json");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1)); // «другая программа изменила файл» — повод перечитать

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None)) // файл занят (антивирус)
        {
            AntiFraudSettingsStore.Current.Guard.Enabled.Should().BeTrue("занятый файл не сбрасывает PIN старшего на «выключено»");
            AntiFraudSettingsStore.Current.Guard.ReturnAmount.Should().Be(1000);
            AntiFraudSettingsStore.Update(d => d.Guard.Enabled = false); // запись «умолчаний» поверх нечитаемого файла запрещена
        }
        File.Exists(file + ".bad").Should().BeFalse("занятый файл не переименовывается как битый");
        AntiFraudSettingsStore.Reset();
        AntiFraudSettingsStore.Current.Guard.Enabled.Should().BeTrue("в файле по-прежнему прежние правила");
    }

    [Fact]
    public void AntiFraud_LockedFileOnFirstRead_UsesBackupOrSafeMode_NotEverythingOff()
    {
        AntiFraudSettingsStore.DataDirOverride = _dir;
        AntiFraudSettingsStore.Reset();
        AntiFraudSettingsStore.Update(d => { d.Guard.Enabled = true; d.Guard.DiscountPercent = 7; });
        AntiFraudSettingsStore.Update(d => d.Guard.DiscountPercent = 7); // .bak
        var file = Path.Combine(_dir, "antifraud-settings.json");
        AntiFraudSettingsStore.Reset(); // новый запуск
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var rules = AntiFraudSettingsStore.Current.Guard;
            rules.Enabled.Should().BeTrue("первое чтение не удалось — берётся копия .bak (прошлая запись), а не «всё выключено»");
            rules.DiscountPercent.Should().Be(7);
        }

        // нет и копии — безопасный режим, а не «всё выключено»
        AntiFraudSettingsStore.SafeDefaults().Guard.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ShopAutomation_LockedFile_KeepsExpiryBlock_NoBadRename_NoOverwrite()
    {
        ShopAutomationSettings.DataDirOverride = _dir;
        ShopAutomationSettings.Reset();
        ShopAutomationSettings.Update(d => d.ExpiryMode = ExpiryModes.Block);
        ShopAutomationSettings.Update(d => d.ExpiryMode = ExpiryModes.Block);
        var file = Path.Combine(_dir, "shop-automation.json");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        ShopAutomationSettings.Reset();
        ShopAutomationSettings.Current.ExpiryMode.Should().Be(ExpiryModes.Block);

        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(2));
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            ShopAutomationSettings.Current.ExpiryMode.Should().Be(ExpiryModes.Block, "режим «Запретить» не превращается в «Предупреждать» из-за занятого файла");
            ShopAutomationSettings.Update(d => d.ExpiryMode = ExpiryModes.Off);
        }
        Directory.GetFiles(_dir, "shop-automation.bad-*").Should().BeEmpty("занятый файл не переименовывается в .bad");
        ShopAutomationSettings.Reset();
        ShopAutomationSettings.Current.ExpiryMode.Should().Be(ExpiryModes.Block, "запись поверх нечитаемого файла не состоялась");
    }
}
