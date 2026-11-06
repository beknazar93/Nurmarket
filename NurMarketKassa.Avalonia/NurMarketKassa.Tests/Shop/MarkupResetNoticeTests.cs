using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-06 (живой тест, Б-10): общую наценку приёмки обновление один раз выключает (это задумано), но владелец должен узнать об этом на экране, а не только из журнала.
/// Проверяем данные уведомления: прежний процент запоминается, показ — один раз, запоминается в файле (по компании), включённая наценка уведомление снимает. Файл — во временной папке.</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class MarkupResetNoticeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-markup-notice-" + Guid.NewGuid().ToString("N"));
    private readonly ReceivingMarkupSettings _previous = ReceivingMarkupSettingsStore.Current.Clone();
    private readonly bool _persistDisabled = ReceivingMarkupSettingsStore.PersistenceDisabled;
    private readonly string? _previousDir = ReceivingMarkupSettingsStore.DataDirOverride;

    public MarkupResetNoticeTests()
    {
        Directory.CreateDirectory(_dir);
        ReceivingMarkupSettingsStore.DataDirOverride = _dir;
        ReceivingMarkupSettingsStore.PersistenceDisabled = false;
    }

    public void Dispose()
    {
        ReceivingMarkupSettingsStore.Override(_previous);
        ReceivingMarkupSettingsStore.PersistenceDisabled = _persistDisabled;
        ReceivingMarkupSettingsStore.DataDirOverride = _previousDir;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ReceivingMarkupSettings OldFile(bool enabled, double percent) =>
        JsonSerializer.Deserialize<ReceivingMarkupSettings>("{\"AutoEnabled\":" + (enabled ? "true" : "false") + ",\"DefaultPercent\":" + percent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}")!;

    private ReceivingMarkupSettings OnDisk() =>
        JsonSerializer.Deserialize<ReceivingMarkupSettings>(File.ReadAllText(Path.Combine(_dir, "receiving-markup.json")))!;

    [Fact]
    public void Reset_of_an_enabled_markup_is_announced_once_with_the_old_percent()
    {
        var old = OldFile(enabled: true, percent: 20);

        ReceivingMarkupSettingsStore.MigrateReset(old).Should().BeTrue();
        old.ResetNoticePending.Should().BeTrue("наценку выключили — владельцу нужно сказать на экране");
        old.ResetPreviousPercent.Should().Be(20, "прежнее значение сохранено для подсказки, но сама наценка не включается");
        old.AutoEnabled.Should().BeFalse();
        OnDisk().ResetNoticePending.Should().BeTrue("уведомление пережило бы перезапуск, пока его не показали");

        ReceivingMarkupSettingsStore.Override(old);
        ReceivingMarkupSettingsStore.TakeResetNotice().Should().Be(20, "первый показ — прежние 20 %");
        ReceivingMarkupSettingsStore.TakeResetNotice().Should().BeNull("второй раз не показываем");
        OnDisk().ResetNoticePending.Should().BeFalse("показ запомнен в файле компании — после перезапуска тоже не покажется");
        ReceivingMarkupSettingsStore.Current.AutoEnabled.Should().BeFalse("уведомление наценку само не включает");
    }

    [Fact]
    public void Nothing_to_announce_when_the_markup_was_not_enabled_before_the_reset()
    {
        var old = OldFile(enabled: false, percent: 0);

        ReceivingMarkupSettingsStore.MigrateReset(old).Should().BeTrue();
        old.ResetNoticePending.Should().BeFalse("выключенную наценку выключать нечего — и говорить не о чем");
        ReceivingMarkupSettingsStore.Override(old);
        ReceivingMarkupSettingsStore.TakeResetNotice().Should().BeNull();
    }

    [Fact]
    public void Turning_the_common_markup_on_clears_the_pending_notice()
    {
        var old = OldFile(enabled: true, percent: 30);
        ReceivingMarkupSettingsStore.MigrateReset(old);
        ReceivingMarkupSettingsStore.Override(old);

        ReceivingMarkupSettingsStore.SetDefaultPercent(15, persist: true);

        ReceivingMarkupSettingsStore.TakeResetNotice().Should().BeNull("владелец уже включил общую наценку сам");
    }
}
