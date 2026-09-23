using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Subscription;

/// <summary>Проверяет офлайн-безопасный расчёт статуса подписки компании
/// (CompanyInfoService.GetCachedSubscriptionStatus) — используется и при входе, и фоновым
/// монитором в MainWindow, чтобы отсчёт дней до истечения работал без связи с сервером.</summary>
public sealed class SubscriptionStatusTests
{
    [Fact]
    public void GetCachedSubscriptionStatus_ReturnsNull_WhenNoEndDateSaved()
    {
        using var scope = new SubscriptionEndDateScope(null);

        var status = CompanyInfoService.GetCachedSubscriptionStatus();

        status.Should().BeNull();
    }

    [Fact]
    public void GetCachedSubscriptionStatus_ReturnsNull_ForUnparsableDate()
    {
        using var scope = new SubscriptionEndDateScope("not-a-date");

        var status = CompanyInfoService.GetCachedSubscriptionStatus();

        status.Should().BeNull();
    }

    [Fact]
    public void GetCachedSubscriptionStatus_IsExpired_WhenEndDateInThePast()
    {
        var pastDate = DateTimeOffset.Now.AddDays(-1);
        using var scope = new SubscriptionEndDateScope(pastDate.ToString("O"));

        var status = CompanyInfoService.GetCachedSubscriptionStatus();

        status.Should().NotBeNull();
        status!.IsExpired.Should().BeTrue();
        status.IsNearExpiry.Should().BeFalse();
    }

    [Fact]
    public void GetCachedSubscriptionStatus_IsNearExpiry_WhenTwoDaysRemain()
    {
        var soonDate = DateTimeOffset.Now.AddDays(2);
        using var scope = new SubscriptionEndDateScope(soonDate.ToString("O"));

        var status = CompanyInfoService.GetCachedSubscriptionStatus();

        status.Should().NotBeNull();
        status!.IsExpired.Should().BeFalse();
        status.IsNearExpiry.Should().BeTrue();
        status.DaysRemaining.Should().BeInRange(1, 2);
    }

    [Fact]
    public void GetCachedSubscriptionStatus_NotNearExpiry_WhenTenDaysRemain()
    {
        var farDate = DateTimeOffset.Now.AddDays(10);
        using var scope = new SubscriptionEndDateScope(farDate.ToString("O"));

        var status = CompanyInfoService.GetCachedSubscriptionStatus();

        status.Should().NotBeNull();
        status!.IsExpired.Should().BeFalse();
        status.IsNearExpiry.Should().BeFalse();
    }

    [Fact]
    public void GetCachedSubscriptionStatus_BoundaryAtExactlyThreeDays_IsNearExpiry()
    {
        var boundaryDate = DateTimeOffset.Now.AddDays(3);
        using var scope = new SubscriptionEndDateScope(boundaryDate.ToString("O"));

        var status = CompanyInfoService.GetCachedSubscriptionStatus();

        status.Should().NotBeNull();
        status!.IsExpired.Should().BeFalse();
        status.IsNearExpiry.Should().BeTrue();
    }

    /// <summary>Изолирует UserPreferences.Instance.SubscriptionEndDateRaw на время одного
    /// теста — это общий синглтон, но SaveToDisk() тут не вызывается, так что мутация
    /// остаётся только в памяти и восстанавливается в Dispose без побочных эффектов на диске.</summary>
    private sealed class SubscriptionEndDateScope : IDisposable
    {
        private readonly string? _original;

        public SubscriptionEndDateScope(string? value)
        {
            _original = UserPreferences.Instance.SubscriptionEndDateRaw;
            UserPreferences.Instance.SubscriptionEndDateRaw = value;
        }

        public void Dispose() => UserPreferences.Instance.SubscriptionEndDateRaw = _original;
    }
}
