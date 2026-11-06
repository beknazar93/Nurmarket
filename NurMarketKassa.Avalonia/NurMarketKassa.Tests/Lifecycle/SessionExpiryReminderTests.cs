using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Lifecycle;

[CollectionDefinition("SessionExpiry", DisableParallelization = true)]
public sealed class SessionExpiryCollection;

/// <summary>2026-11-06 (разбор журнала клиента, баг 3): «Позже» в окне «Сессия истекла» не значит «навсегда» — напоминание раз в 10 минут и сразу (не чаще раза в 2 минуты)
/// при действии, упавшем из-за сессии; повторы в журнале раз в 10 минут; при недействительной сессии запросы списка поставщиков на сервер не уходят;
/// сбой второй фоновой загрузки справочников (категории/бренды) не превращается в «Unobserved task exception». Время — через часы монитора, без Thread.Sleep.</summary>
[Collection("SessionExpiry")]
public sealed class SessionExpiryReminderTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private DateTime _now = T0;

    public SessionExpiryReminderTests()
    {
        SessionExpiryMonitor.ResetForTests();
        SessionExpiryMonitor.Clock = () => _now;
    }

    public void Dispose() => SessionExpiryMonitor.ResetForTests();

    private void Advance(TimeSpan by) => _now += by;

    [Fact]
    public void Timer_reminder_comes_after_ten_minutes_and_not_more_often()
    {
        SessionExpiryMonitor.Report("тест").Should().BeTrue();

        Advance(TimeSpan.FromMinutes(9));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeFalse("с первого окна прошло 9 минут");
        Advance(TimeSpan.FromMinutes(1));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeTrue("10 минут — пора");
        SessionExpiryMonitor.RemindIfDue(false).Should().BeFalse("слот занят: второй раз сразу — нет");
        Advance(TimeSpan.FromMinutes(9.5));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeFalse();
        Advance(TimeSpan.FromMinutes(0.5));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeTrue("и снова через 10 минут: «Позже» не навсегда");
    }

    [Fact]
    public void Action_reminder_is_immediate_but_not_more_often_than_every_two_minutes()
    {
        SessionExpiryMonitor.Report("тест");
        var shown = new List<bool>();
        void On(bool fromAction) => shown.Add(fromAction);
        SessionExpiryMonitor.Reminder += On;
        try
        {
            Advance(TimeSpan.FromMinutes(1));
            SessionExpiryMonitor.RemindIfDue(true).Should().BeFalse("первое окно только что показано (1 минуту назад)");
            Advance(TimeSpan.FromMinutes(1));
            SessionExpiryMonitor.RemindIfDue(true).Should().BeTrue("2 минуты — действие, упавшее из-за сессии, снова спрашивает");
            Advance(TimeSpan.FromSeconds(90));
            SessionExpiryMonitor.RemindIfDue(true).Should().BeFalse("не чаще раза в 2 минуты");
            Advance(TimeSpan.FromSeconds(30));
            SessionExpiryMonitor.RemindIfDue(true).Should().BeTrue();
            shown.Should().Equal(true, true);
        }
        finally
        {
            SessionExpiryMonitor.Reminder -= On;
        }
    }

    [Fact]
    public void Deferred_reminder_retries_in_a_minute()
    {
        SessionExpiryMonitor.Report("тест");
        Advance(TimeSpan.FromMinutes(10));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeTrue();
        SessionExpiryMonitor.DeferReminder(); // кассир печатает — окно не показали
        Advance(TimeSpan.FromSeconds(30));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeFalse();
        Advance(TimeSpan.FromSeconds(31));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeTrue("через минуту, а не через полные 10");
    }

    [Fact]
    public void RemindOnSessionLost_only_reacts_to_a_real_session_loss()
    {
        SessionExpiryMonitor.Report("тест");
        Advance(TimeSpan.FromMinutes(3));
        SessionExpiryMonitor.RemindOnSessionLost(new InvalidOperationException("другое")).Should().BeFalse();
        SessionExpiryMonitor.RemindOnSessionLost(new ApiException("нет", 500)).Should().BeFalse();
        SessionExpiryMonitor.RemindOnSessionLost(new ApiException(NurMarketApiClient.AuthInvalidHintRu, 401)).Should().BeTrue();
    }

    [Fact]
    public void Nothing_is_reminded_while_the_session_is_fine_and_Reset_restarts_the_timers()
    {
        SessionExpiryMonitor.RemindIfDue(true).Should().BeFalse("сессия в порядке");

        SessionExpiryMonitor.Report("тест");
        Advance(TimeSpan.FromMinutes(10));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeTrue();

        SessionExpiryMonitor.Reset(); // вошли снова
        SessionExpiryMonitor.IsExpired.Should().BeFalse();
        Advance(TimeSpan.FromHours(1));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeFalse("после входа напоминать нечего");

        SessionExpiryMonitor.Report("тест").Should().BeTrue("новая потеря сессии объявляется снова");
        Advance(TimeSpan.FromMinutes(9));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeFalse("отсчёт заново от нового объявления, а не от старого напоминания");
        Advance(TimeSpan.FromMinutes(1));
        SessionExpiryMonitor.RemindIfDue(false).Should().BeTrue();
    }

    [Fact]
    public void LogOnce_writes_once_per_source_per_ten_minutes_and_Reset_clears_it()
    {
        SessionExpiryMonitor.LogOnce("a", "тест журнала a").Should().BeTrue("первая запись остаётся");
        SessionExpiryMonitor.LogOnce("a", "тест журнала a").Should().BeFalse();
        SessionExpiryMonitor.LogOnce("b", "тест журнала b").Should().BeTrue("другой источник пишется отдельно");

        Advance(TimeSpan.FromMinutes(9));
        SessionExpiryMonitor.LogOnce("a", "тест журнала a").Should().BeFalse();
        Advance(TimeSpan.FromMinutes(1));
        SessionExpiryMonitor.LogOnce("a", "тест журнала a").Should().BeTrue("через 10 минут запись разрешена снова");

        SessionExpiryMonitor.Report("тест");
        SessionExpiryMonitor.Reset();
        SessionExpiryMonitor.LogOnce("a", "тест журнала a").Should().BeTrue("после входа счётчик начат заново");
    }

    // ------------------------------------------------------------------ сервер не получает запросов, пока сессия недействительна

    [Fact]
    public async Task Supplier_list_is_not_requested_from_the_server_while_the_session_is_expired()
    {
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/clients/", 200, "{\"count\":1,\"results\":[{\"id\":\"s1\",\"full_name\":\"Поставщик\",\"phone\":\"+996555000111\"}]}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        try
        {
            SessionExpiryMonitor.Report("тест").Should().BeTrue();

            var viaSupplierService = async () => await new SupplierApiService(api).ListAsync();
            (await viaSupplierService.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(401);

            // Приёмка/накладная: сохранённый список (если он есть у этой папки данных) либо тот же понятный отказ — но не запрос.
            try { await PurchaseReceivingService.Instance.LoadSuppliersAsync(); }
            catch (ApiException ex) { SessionExpiryMonitor.IsSessionLost(ex).Should().BeTrue(); }

            server.Server.LogEntries.Count(e => e.RequestMessage.Path.Contains("/api/main/clients/")).Should().Be(0, "при недействительной сессии список поставщиков на сервер не уходит");

            // Сессия восстановлена — запрос уходит как обычно.
            SessionExpiryMonitor.Reset();
            await new SupplierApiService(api).ListAsync();
            server.Server.LogEntries.Count(e => e.RequestMessage.Path.Contains("/api/main/clients/")).Should().Be(1);
        }
        finally
        {
            PosApp.CatalogApi = previous;
        }
    }

    // ------------------------------------------------------------------ второй сбой справочников не уходит в Unobserved task exception

    [Fact]
    public async Task Failed_second_reference_load_is_observed_and_does_not_reach_UnobservedTaskException()
    {
        using var server = new FakeNurCrmServer();
        server.Error("GET", "/api/main/categories/", 400);
        server.Error("GET", "/api/main/brands/", 400);
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var catalog = new CatalogApiService(api);
        var unobserved = new List<string>();
        void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) { lock (unobserved) unobserved.Add(e.Exception.ToString()); }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            // как карточка товара: обе задачи сразу, ждём по очереди; первая упала — вторую никто не ждёт
            await RunLikeProductDialogAsync(catalog);
            for (var i = 0; i < 4; i++)
            {
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            lock (unobserved)
                unobserved.Where(x => x.Contains("FetchReferenceNamesAsync")).Should().BeEmpty("сбой читается сразу, в журнал «Unobserved task exception» не попадает");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private static async Task RunLikeProductDialogAsync(CatalogApiService catalog)
    {
        var categories = catalog.GetCategoryNamesAsync();
        var brands = catalog.GetBrandNamesAsync();
        try
        {
            await categories;
            await brands;
        }
        catch (ApiException)
        {
            // ожидаемо: диалог пишет одну запись в журнал
        }
        // Вторую задачу даём завершиться с ошибкой, но никто её не ждёт.
        await Task.WhenAny(brands, Task.Delay(3000));
    }
}
