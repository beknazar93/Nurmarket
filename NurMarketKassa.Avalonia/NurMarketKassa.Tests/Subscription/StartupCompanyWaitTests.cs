using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Subscription;

/// <summary>2026-11-06 (В8, шаг 32, 20.2): запуск кассы с известной компанией на этом ПК не ждёт медленный сервер 8 секунд — открывается с сохранённой подпиской за ≈2 с,
/// а тот же запрос дозагружает компанию фоном (повторного запроса нет).</summary>
[Collection("Wave5Database")]
public sealed class StartupCompanyWaitTests
{
    public class SlowAuthProxy : DispatchProxy
    {
        public static int CompanyCalls;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name == "get_AccessToken")
                return "test-token";
            if (targetMethod.Name == nameof(IAuthApiService.GetCompanyAsync))
            {
                Interlocked.Increment(ref CompanyCalls);
                var ct = args is { Length: > 0 } && args[0] is CancellationToken t ? t : CancellationToken.None;
                return Task.Delay(Timeout.Infinite, ct).ContinueWith<CompanyDto?>(_ => null, TaskContinuationOptions.OnlyOnCanceled);
            }

            var rt = targetMethod.ReturnType;
            if (rt == typeof(Task))
                return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
            }

            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }

    [Fact]
    public async Task Startup_WithKnownCompany_AndSlowServer_ReturnsCachedSubscription_WithinTwoSeconds()
    {
        var prefs = UserPreferences.Instance;
        var oldKey = prefs.LastDataAccountKey;
        var oldEnd = prefs.SubscriptionEndDateRaw;
        var oldWait = CompanyInfoService.StartupCompanyWait;
        try
        {
            prefs.LastDataAccountKey = "company-known";
            prefs.SubscriptionEndDateRaw = DateTimeOffset.Now.AddDays(20).ToString("O");
            CompanyInfoService.StartupCompanyWait = TimeSpan.FromMilliseconds(300);
            SlowAuthProxy.CompanyCalls = 0;
            var auth = DispatchProxy.Create<IAuthApiService, SlowAuthProxy>();

            var watch = Stopwatch.StartNew();
            var status = await CompanyInfoService.RefreshAsync(auth, CancellationToken.None, startup: true);
            watch.Stop();

            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3), "раньше запуск ждал сервер до 8 секунд");
            status.Should().NotBeNull("подписка берётся из сохранённой даты");
            status!.IsExpired.Should().BeFalse();
            SlowAuthProxy.CompanyCalls.Should().Be(1, "запрос компании один: фоновая дозагрузка ждёт его же, а не шлёт второй");
        }
        finally
        {
            CompanyInfoService.StartupCompanyWait = oldWait;
            prefs.LastDataAccountKey = oldKey;
            prefs.SubscriptionEndDateRaw = oldEnd;
        }
    }
}
