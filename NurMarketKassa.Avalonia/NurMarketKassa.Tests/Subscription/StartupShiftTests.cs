using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Subscription;

/// <summary>2026-11-06 (В8, шаги 11 и 32/20.3): список открытых смен запрашивается заранее, одним запросом; окно по сохранённой смене — только для смены этого кассира, не старше суток.</summary>
[Collection("Wave5Database")]
public sealed class StartupShiftTests
{
    public class ShiftProxy : DispatchProxy
    {
        public static int ListCalls;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name == nameof(IShiftApiService.ConstructionShiftsListAsync))
            {
                Interlocked.Increment(ref ListCalls);
                return Task.FromResult(JsonDocument.Parse("[]").RootElement.Clone());
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

    public class NotAutonomous : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod is { ReturnType: var rt } && rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
    }

    [Fact]
    public async Task PrefetchedOpenShifts_AreTakenByRefresh_SoServerIsAskedOnce()
    {
        ShiftProxy.ListCalls = 0;
        var api = DispatchProxy.Create<IShiftApiService, ShiftProxy>();
        var service = new ShiftStateService(api, DispatchProxy.Create<IAutonomousAuthService, NotAutonomous>());

        ShiftStateService.PrefetchOpenShifts(api, CancellationToken.None);
        await service.RefreshAsync();

        ShiftProxy.ListCalls.Should().Be(1, "RefreshAsync забрал уже запущенный запрос, второго не было");
    }

    [Fact]
    public void WithoutPrefetch_RefreshAsksServerItself()
    {
        ShiftProxy.ListCalls = 0;
        var api = DispatchProxy.Create<IShiftApiService, ShiftProxy>();
        var service = new ShiftStateService(api, DispatchProxy.Create<IAutonomousAuthService, NotAutonomous>());

        service.RefreshAsync().GetAwaiter().GetResult();

        ShiftProxy.ListCalls.Should().Be(1);
    }

    private static OfflinePosState State(string? shift = "shift-1", string? cashier = "u1", double hoursAgo = 1) => new()
    {
        ActiveShiftId = shift,
        CashierId = cashier,
        SavedAt = DateTimeOffset.Now.AddHours(-hoursAgo),
    };

    [Fact]
    public void LocalState_Shown_OnlyForOwnFreshServerShift()
    {
        var now = DateTimeOffset.Now;

        StartupShiftLocalState.CanShow(State(), "u1", now).Should().BeTrue();
        StartupShiftLocalState.CanShow(State(cashier: "u2"), "u1", now).Should().BeFalse("смена другого кассира");
        StartupShiftLocalState.CanShow(State(cashier: null), "u1", now).Should().BeFalse("файл старого вида без владельца");
        StartupShiftLocalState.CanShow(State(hoursAgo: 30), "u1", now).Should().BeFalse("старше суток");
        StartupShiftLocalState.CanShow(State(shift: "offline-shift-1"), "u1", now).Should().BeFalse("офлайн-смену сверяет сервер");
        StartupShiftLocalState.CanShow(State(shift: null), "u1", now).Should().BeFalse("смены нет");
        StartupShiftLocalState.CanShow(State(), null, now).Should().BeFalse("кассир не известен");
        StartupShiftLocalState.CanShow(null, "u1", now).Should().BeFalse();
    }
}
