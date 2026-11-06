using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.Money;

/// <summary>2026-11-06, живой тест Этапа 2, Б2-1: касса запущена без связи — компания не загружалась до перезапуска, «Оплата долга» отвечала «Не удалось определить компанию».
/// Теперь: возврат связи и обращение к компании перезапрашивают её (один запрос одновременно, не чаще раза в 10 с, со сроком); при старте без связи берётся сохранённая компания
/// этого ПК (ключ данных); ответ ДРУГОЙ компании не применяется.</summary>
[Collection("Wave5Database")]
public sealed class CompanyEnsureAfterOfflineStartTests : IDisposable
{
    public class AuthProxy : DispatchProxy
    {
        public static int Calls;
        public static Func<CancellationToken, Task<CompanyDto?>> Handler = _ => Task.FromResult<CompanyDto?>(null);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;
            if (targetMethod.Name == "get_AccessToken")
                return "test-token";
            if (targetMethod.Name == nameof(IAuthApiService.GetCompanyAsync))
            {
                Interlocked.Increment(ref Calls);
                var ct = args is { Length: > 0 } && args[0] is CancellationToken t ? t : CancellationToken.None;
                return Handler(ct);
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

    private readonly CompanyDto? _previousCompany = CompanyInfoService.LastCompany;
    private readonly string? _previousKey = UserPreferences.Instance.LastDataAccountKey;
    private readonly TimeSpan _previousTimeout = CompanyInfoService.EnsureTimeout;

    public CompanyEnsureAfterOfflineStartTests()
    {
        SetLastCompany(null);
        CompanyInfoService.ResetEnsureForTests();
        ServerOutageMonitor.ResetForTests();
        AuthProxy.Calls = 0;
        AuthProxy.Handler = _ => Task.FromResult<CompanyDto?>(null);
        // Без полной загрузки компании (таймеры, сетевые задачи): подставляем только сам факт «компания применена».
        CompanyInfoService.ApplyLoadedOverride = (company, _) =>
        {
            SetLastCompany(company);
            return null;
        };
    }

    public void Dispose()
    {
        CompanyInfoService.ResetEnsureForTests();
        CompanyInfoService.EnsureTimeout = _previousTimeout;
        ServerOutageMonitor.ResetForTests();
        SetLastCompany(_previousCompany);
        UserPreferences.Instance.LastDataAccountKey = _previousKey;
    }

    private static void SetLastCompany(CompanyDto? company) =>
        typeof(CompanyInfoService).GetProperty("LastCompany", BindingFlags.Public | BindingFlags.Static)!.SetValue(null, company);

    private static IAuthApiService Auth() => DispatchProxy.Create<IAuthApiService, AuthProxy>();

    private static async Task WaitForAsync(Func<bool> condition, int ms = 3000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
            await Task.Delay(20);
    }

    [Fact]
    public void KnownCompanyId_ServerCompanyFirst_ThenSavedDataKey_NeverAnotherServersKey()
    {
        UserPreferences.Instance.LastDataAccountKey = AppPaths.CompanyKey("77");
        CompanyInfoService.KnownCompanyId.Should().Be("77", "связи не было — берётся компания, чьи данные лежат на этом ПК");

        SetLastCompany(new CompanyDto { Id = "88" });
        CompanyInfoService.KnownCompanyId.Should().Be("88", "загруженная с сервера главнее сохранённой");

        SetLastCompany(null);
        UserPreferences.Instance.LastDataAccountKey = "company-known";
        CompanyInfoService.KnownCompanyId.Should().BeNull("ключ не вида «c:{id}» — компанию не угадываем");

        CompanyInfoService.CompanyIdFromDataKey("other-stand.example|c:5").Should().BeNull("ключ другого сервера (стенда) не подходит боевому");
        CompanyInfoService.CompanyIdFromDataKey("c:").Should().BeNull();
    }

    [Fact]
    public async Task OfflineStart_PaymentImpossibleBeforeRecovery_ThenRecoveryLoadsCompanyByItself()
    {
        UserPreferences.Instance.LastDataAccountKey = AppPaths.CompanyKey("co-1");
        AuthProxy.Handler = _ => Task.FromResult<CompanyDto?>(new CompanyDto { Id = "co-1", Name = "Магазин" });
        var auth = Auth();

        // Касса запущена без интернета: авария объявлена, RefreshAsync отдаёт сохранённую подписку и компанию не просит.
        ServerOutageMonitor.ReportFailure("тест", "нет интернета", hard: true);
        await CompanyInfoService.RefreshAsync(auth, CancellationToken.None, startup: true);
        CompanyInfoService.LastCompany.Should().BeNull();

        // Обращение за компанией в аварии — быстрый отказ «нет связи», сервер не дёргается.
        (await CompanyInfoService.EnsureLoadedAsync(auth, TimeSpan.Zero)).Should().Be(CompanyInfoService.CompanyEnsureOutcome.Unavailable);
        AuthProxy.Calls.Should().Be(0);

        // Связь вернулась — компания загружается сама, без перезапуска и без F5.
        ServerOutageMonitor.ReportSuccess("тест");
        await WaitForAsync(() => CompanyInfoService.LastCompany is not null);

        CompanyInfoService.LastCompany.Should().NotBeNull("возврат связи перезапросил компанию");
        CompanyInfoService.LastCompany!.Id.Should().Be("co-1");
        AuthProxy.Calls.Should().Be(1);
        CompanyInfoService.KnownCompanyId.Should().Be("co-1");
    }

    [Fact]
    public async Task ParallelCalls_ShareOneRequest()
    {
        UserPreferences.Instance.LastDataAccountKey = AppPaths.CompanyKey("co-2");
        AuthProxy.Handler = async _ =>
        {
            await Task.Delay(300);
            return new CompanyDto { Id = "co-2" };
        };
        var auth = Auth();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CompanyInfoService.EnsureLoadedAsync(auth, TimeSpan.FromSeconds(10))));

        results.Should().OnlyContain(r => r == CompanyInfoService.CompanyEnsureOutcome.Loaded);
        AuthProxy.Calls.Should().Be(1, "один запрос одновременно: остальные ждут тот же");
    }

    [Fact]
    public async Task FailedAttempt_IsNotRepeatedWithinInterval_ButUserAskAfterwardsRetries()
    {
        UserPreferences.Instance.LastDataAccountKey = AppPaths.CompanyKey("co-3");
        AuthProxy.Handler = _ => Task.FromException<CompanyDto?>(new HttpRequestException("нет сети"));
        var auth = Auth();

        (await CompanyInfoService.EnsureLoadedAsync(auth, TimeSpan.FromSeconds(10))).Should().Be(CompanyInfoService.CompanyEnsureOutcome.Unavailable);
        (await CompanyInfoService.EnsureLoadedAsync(auth, TimeSpan.FromSeconds(10))).Should().Be(CompanyInfoService.CompanyEnsureOutcome.Unavailable);
        AuthProxy.Calls.Should().Be(1, "шквала запросов нет: не чаще раза в 10 с");

        AuthProxy.Handler = _ => Task.FromResult<CompanyDto?>(new CompanyDto { Id = "co-3" });
        (await CompanyInfoService.EnsureLoadedAsync(auth, TimeSpan.Zero)).Should().Be(CompanyInfoService.CompanyEnsureOutcome.Loaded);
        AuthProxy.Calls.Should().Be(2);
    }

    [Fact]
    public async Task HangingServer_AttemptEndsByTimeout()
    {
        CompanyInfoService.EnsureTimeout = TimeSpan.FromMilliseconds(250);
        AuthProxy.Handler = ct => Task.Delay(Timeout.Infinite, ct).ContinueWith<CompanyDto?>(_ => null, TaskContinuationOptions.OnlyOnCanceled);
        var auth = Auth();

        var work = CompanyInfoService.EnsureLoadedAsync(auth, TimeSpan.Zero);
        (await Task.WhenAny(work, Task.Delay(3000))).Should().BeSameAs(work, "запрос обрывается по сроку, окно оплаты не висит");
        (await work).Should().Be(CompanyInfoService.CompanyEnsureOutcome.Unavailable);
    }

    [Fact]
    public async Task LateAnswerOfAnotherCompany_IsNotApplied()
    {
        UserPreferences.Instance.LastDataAccountKey = AppPaths.CompanyKey("111");
        AuthProxy.Handler = _ => Task.FromResult<CompanyDto?>(new CompanyDto { Id = "222" });
        string? warnedFor = null;
        Action<string?> handler = id => warnedFor = id;
        CompanyInfoService.OtherCompanyDetected += handler;
        try
        {
            var outcome = await CompanyInfoService.EnsureLoadedAsync(Auth(), TimeSpan.Zero);

            outcome.Should().Be(CompanyInfoService.CompanyEnsureOutcome.OtherCompany);
            CompanyInfoService.LastCompany.Should().BeNull("компании не смешиваем");
            warnedFor.Should().Be("222");
            CompanyInfoService.KnownCompanyId.Should().Be("111", "платежи пишутся только в компанию данных этого ПК");
        }
        finally
        {
            CompanyInfoService.OtherCompanyDetected -= handler;
        }
    }
}

/// <summary>2026-11-06, живой тест Этапа 2, Б2-2: «Оплата долга» видела только status=debt; долговой чек после частичного возврата (partially_returned) с остатком долга исчезал из «Непогашенных».
/// Теперь в список входят ВСЕ чеки клиента с положительным остатком долга (debt_remaining / remaining_debt / debt_amount), чек с остатком 0 — нет.</summary>
[Collection("ServerFeatures")]
public sealed class DebtListIncludesReturnedChecksTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<JsonElement> Many(params string[] json) => json.Select(J).ToList();

    private void StubSales(string status, string resultsJson) =>
        _fake.Server.Given(Request.Create().WithPath("/api/main/pos/sales/").UsingGet().WithParam("status", status))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("{\"count\":1,\"next\":null,\"previous\":null,\"results\":" + resultsJson + "}"));

    [Fact]
    public void PartiallyReturned_WithRemaining14_IsListed_AndZeroRemainingIsNot()
    {
        var debt = Many("{\"id\":\"s1\",\"status\":\"debt\",\"debt_remaining\":\"9.00\"}");
        var others = Many(
            "{\"id\":\"s2\",\"status\":\"partially_returned\",\"debt_remaining\":\"14.00\"}",
            "{\"id\":\"s3\",\"status\":\"partially_returned\",\"debt_remaining\":\"0.00\",\"debt_amount\":\"30.00\"}",
            "{\"id\":\"s4\",\"status\":\"partially_returned\",\"debt_amount\":0}",
            "{\"id\":\"s5\",\"status\":\"partially_returned\"}");

        var merged = DebtSaleListRules.Merge(debt, others);

        merged.Select(m => m.GetProperty("id").GetString()).Should().Equal("s1", "s2");
    }

    [Fact]
    public void RemainingField_Priority_AndFallbacks()
    {
        DebtSaleListRules.SaleDebtRemaining(J("{\"debt_remaining\":5,\"remaining_debt\":7,\"debt_amount\":9}")).Should().Be(5);
        DebtSaleListRules.SaleDebtRemaining(J("{\"remaining_debt\":\"7.50\",\"debt_amount\":9}")).Should().Be(7.5);
        DebtSaleListRules.SaleDebtRemaining(J("{\"debt_amount\":\"9\"}")).Should().Be(9);
        DebtSaleListRules.SaleDebtRemaining(J("{\"total\":9}")).Should().BeNull();
        DebtSaleListRules.HasOpenDebt(J("{\"debt_remaining\":0.004}")).Should().BeFalse("копейки-хвост не долг");
    }

    [Fact]
    public void SameCheckInBothLists_IsNotDuplicated_AndSumOfRowsEqualsSumOfRemainders()
    {
        var debt = Many("{\"id\":10,\"debt_remaining\":23}", "{\"id\":11,\"debt_remaining\":17}");
        var others = Many("{\"id\":10,\"status\":\"partially_returned\",\"debt_remaining\":23}", "{\"id\":12,\"status\":\"partially_returned\",\"debt_remaining\":14}");

        var merged = DebtSaleListRules.Merge(debt, others);

        merged.Should().HaveCount(3);
        merged.Sum(m => DebtSaleListRules.SaleDebtRemaining(m)!.Value).Should().Be(23 + 17 + 14, "«по чекам» = сумма остатков списка");
    }

    [Fact]
    public void SummaryMismatch_Honest_ButNotFalseAlarmWhenEitherTotalMatches()
    {
        DebtSaleListRules.SummaryMismatch(40, 0, 0).Should().BeTrue("«по чекам 40» при пустом списке — расхождение");
        DebtSaleListRules.SummaryMismatch(40, 40, 40).Should().BeFalse();
        DebtSaleListRules.SummaryMismatch(40, 30, 40).Should().BeFalse("сводка включает долг без продажи");
        DebtSaleListRules.SummaryMismatch(30, 30, 40).Should().BeFalse("сводка только по чекам");
        DebtSaleListRules.SummaryMismatch(40, 14, 14).Should().BeTrue();
    }

    [Fact]
    public async Task Api_AsksBothStatuses_AndListsPartiallyReturnedWithRemainder()
    {
        StubSales("debt", "[{\"id\":\"d1\",\"status\":\"debt\",\"debt_remaining\":\"9.00\"}]");
        StubSales("partially_returned", "[{\"id\":\"r1\",\"status\":\"partially_returned\",\"debt_remaining\":\"14.00\"},{\"id\":\"r2\",\"status\":\"partially_returned\",\"debt_remaining\":\"0.00\"}]");
        var sales = new SalesApiService(await LoggedInAsync(_fake));

        var list = await sales.PosDebtSalesWithReturnedAsync("client-1");

        list.Select(x => x.GetProperty("id").GetString()).Should().Equal("d1", "r1");
        Requests(_fake, "GET", "/api/main/pos/sales/").Select(e => e.RequestMessage.Query!["status"].First()).Should().Contain(["debt", "partially_returned"]);
    }

    [Fact]
    public async Task Api_ServerRejectsExtraStatus_FallsBackToDebtOnly()
    {
        StubSales("debt", "[{\"id\":\"d1\",\"status\":\"debt\",\"debt_remaining\":\"9.00\"}]");
        _fake.Server.Given(Request.Create().WithPath("/api/main/pos/sales/").UsingGet().WithParam("status", "partially_returned"))
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json").WithBody("{\"status\":[\"Недопустимое значение\"]}"));
        var sales = new SalesApiService(await LoggedInAsync(_fake));

        var list = await sales.PosDebtSalesWithReturnedAsync("client-1");

        list.Select(x => x.GetProperty("id").GetString()).Should().Equal("d1");
    }
}
