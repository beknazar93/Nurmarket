using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Wave2;

/// <summary>2026-11-06: В2 А5-07 (хвост) — продажу без связи не начинаем, если дата устройства раньше даты сборки программы, а поправки по серверу нет.</summary>
public sealed class ServerClockDateGuardTests
{
    private static readonly DateTimeOffset Build = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DeviceDateYearAgo_NoSkew_IsImpossible() =>
        ServerClock.IsDeviceDateImpossible(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), Build, TimeSpan.Zero).Should().BeTrue();

    [Fact]
    public void DeviceDateSlightlyBeforeBuild_WithinADay_IsFine() =>
        ServerClock.IsDeviceDateImpossible(Build.AddHours(-20), Build, TimeSpan.Zero).Should().BeFalse("сборка могла быть на машине в другом часовом поясе");

    [Fact]
    public void DeviceDateAfterBuild_IsFine() =>
        ServerClock.IsDeviceDateImpossible(Build.AddDays(30), Build, TimeSpan.Zero).Should().BeFalse();

    [Fact]
    public void LargeSkewAlreadyApplied_SalesAreAllowed_SoldAtIsCorrected() =>
        ServerClock.IsDeviceDateImpossible(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), Build, TimeSpan.FromDays(400)).Should().BeFalse();

    [Fact]
    public void BuildDateUnknown_NeverBlocks() =>
        ServerClock.IsDeviceDateImpossible(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), null, TimeSpan.Zero).Should().BeFalse();

    [Fact]
    public void TodaysClock_OnThisMachine_DoesNotBlock()
    {
        ServerClock.OfflineSaleBlockedByDeviceDate(out var text).Should().BeFalse(text);
    }
}
