using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-11-06, дефект Б-9 (живой тест Этапа 1): полная загрузка каталога шла после КАЖДОГО запуска (старый отсчёт 8 ч, ночью набегало 11) и при обрыве
/// на странице 151 из 155 начиналась с первой. Теперь: страховочная сверка раз в сутки и не при открытом чеке, самолечение не чаще раза в сутки,
/// ход загрузки (следующая страница) хранится в базе и докачка идёт с него, запись в базу — пачками. Тесты без интернета: настоящая база
/// (NURMARKET_DATA_ROOT из test-safe.ps1) и поддельный сервер WireMock. Запускать только tools/qa/test-safe.ps1.
/// </summary>
[Collection(nameof(CatalogMemoryIndexTests))]
public sealed class CatalogFullLoadB9Tests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;

    public CatalogFullLoadB9Tests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
    }

    public void Dispose() => _repo.ClearAll();

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly DateTime Now = new(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- страховочная сверка: раз в сутки, не при каждом запуске

    [Fact]
    public void SafetyReload_NotDue_WhenLastFullLoadWasToday_EvenAfterRestart()
    {
        // Повторный запуск с сегодняшней меткой: полной загрузки нет, только лента.
        CatalogFullLoadMarks.MarkFullLoad(_repo, Now.AddHours(-11));
        var last = CatalogFullLoadMarks.LastFullLoadOrLegacy(_repo);
        CatalogFullLoadMarks.IsSafetyReloadDue(last, Now, CatalogFullLoadMarks.SafetyFullReloadInterval, openCheck: false, out _)
            .Should().BeFalse("11 часов назад — меньше суток (раньше интервал 8 ч и каждое утро качались все 155 страниц)");
    }

    [Fact]
    public void SafetyReload_Due_AfterADay_WithReason()
    {
        CatalogFullLoadMarks.IsSafetyReloadDue(Now.AddHours(-25), Now, CatalogFullLoadMarks.SafetyFullReloadInterval, false, out var why).Should().BeTrue();
        why.Should().Contain("25").And.Contain("24");
    }

    [Fact]
    public void SafetyReload_Due_WhenNeverLoaded_ButNotOnClockMovedBackWithinInterval()
    {
        CatalogFullLoadMarks.IsSafetyReloadDue(null, Now, CatalogFullLoadMarks.SafetyFullReloadInterval, false, out var why).Should().BeTrue();
        why.Should().NotBeEmpty();
        CatalogFullLoadMarks.IsSafetyReloadDue(DateTime.MinValue, Now, CatalogFullLoadMarks.SafetyFullReloadInterval, false, out _).Should().BeTrue();
        // Метка из будущего (часы переведены назад) считается давней.
        CatalogFullLoadMarks.IsSafetyReloadDue(Now.AddHours(3), Now, CatalogFullLoadMarks.SafetyFullReloadInterval, false, out _).Should().BeTrue();
    }

    [Fact]
    public void SafetyReload_WaitsForOpenCheck_ButNotForever()
    {
        var interval = CatalogFullLoadMarks.SafetyFullReloadInterval;
        CatalogFullLoadMarks.IsSafetyReloadDue(Now.AddHours(-25), Now, interval, openCheck: true, out _).Should().BeFalse("кассир за чеком — не мешаем");
        CatalogFullLoadMarks.IsSafetyReloadDue(Now.AddHours(-31), Now, interval, openCheck: true, out _).Should().BeTrue("но не откладываем бесконечно");
    }

    // ---------------------------------------------------------------- ход загрузки хранится в базе

    [Fact]
    public void Progress_IsPersisted_StaleAndBrokenIgnored_ClearedWithAccount()
    {
        CatalogFullLoadMarks.GetProgress(_repo, Now).Should().BeNull();

        CatalogFullLoadMarks.SaveProgress(_repo, new CatalogFullLoadMarks.Progress(151, 100, Now.AddMinutes(-3), 15000));
        var p = CatalogFullLoadMarks.GetProgress(_repo, Now)!.Value;
        p.NextPage.Should().Be(151);
        p.PageSize.Should().Be(100);
        p.Accepted.Should().Be(15000);
        CatalogFullLoadMarks.ResumePage(p).Should().Be(150, "на страницу раньше — нахлёст на случай сдвига");

        CatalogFullLoadMarks.GetProgress(_repo, Now.AddHours(13)).Should().BeNull("через 12+ часов страницы уже сдвинулись");

        _repo.SetMetaValue(CatalogFullLoadMarks.ProgressMetaKey, "мусор");
        CatalogFullLoadMarks.GetProgress(_repo, Now).Should().BeNull();

        CatalogFullLoadMarks.SaveProgress(_repo, new CatalogFullLoadMarks.Progress(5, 100, Now, 400));
        CatalogFullLoadMarks.ClearProgress(_repo);
        CatalogFullLoadMarks.GetProgress(_repo, Now).Should().BeNull();

        CatalogFullLoadMarks.SaveProgress(_repo, new CatalogFullLoadMarks.Progress(5, 100, Now, 400));
        _repo.ClearAll(); // смена аккаунта
        CatalogFullLoadMarks.GetProgress(_repo, Now).Should().BeNull("ход принадлежал прежнему аккаунту");
    }

    // ---------------------------------------------------------------- самолечение — не чаще раза в сутки

    private void FillWithShortFeedCards(int count)
    {
        var broken = Enumerable.Range(1, count)
            .Select(i => ProductCatalogMapper.TryTile(J($$"""{"id":"s-{{i}}","name":"Товар {{i}}","price":"10.00","quantity":"5","updated_at":"2026-11-06T01:00:00Z"}"""), ApiBase)!)
            .ToList();
        _repo.SyncReplaceAllWithDiff(broken);
    }

    [Fact]
    public void SelfHeal_AtMostOncePerDay_AndContinuesAnInterruptedLoad()
    {
        FillWithShortFeedCards(20);

        CatalogFullLoadMarks.NeedsSelfHeal(_repo, Now, out _).Should().BeTrue("первый раз — лечим");
        CatalogFullLoadMarks.MarkSelfHealAttempt(_repo, Now);

        // Сервер не прислал is_weight (или загрузка сорвалась): следующий запуск лечить не пытается.
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, Now.AddMinutes(10), out _).Should().BeFalse();
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, Now.AddHours(23), out _).Should().BeFalse();
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, Now.AddHours(25), out _).Should().BeTrue("через сутки можно попробовать снова");

        // Но оборванная загрузка (есть ход) продолжается без ожидания суток.
        CatalogFullLoadMarks.SaveProgress(_repo, new CatalogFullLoadMarks.Progress(80, 100, Now.AddMinutes(9), 7900));
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, Now.AddMinutes(10), out _).Should().BeTrue();

        // Завершённая полная загрузка — лечить больше нечего.
        CatalogFullLoadMarks.MarkFullLoad(_repo, Now.AddMinutes(20));
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, Now.AddDays(3), out _).Should().BeFalse();
    }

    [Fact]
    public void SelfHeal_EmptyBase_IsNotHealing_FullLoadStaysImmediate()
    {
        // Пустая база — обычная первая загрузка (Б-1): самолечение тут ни при чём, ограничения раз в сутки к ней не относятся.
        CatalogFullLoadMarks.MarkSelfHealAttempt(_repo, Now);
        CatalogFullLoadMarks.NeedsSelfHeal(_repo, Now, out _).Should().BeFalse();
        CatalogFullLoadMarks.IsSafetyReloadDue(CatalogFullLoadMarks.LastFullLoadOrLegacy(_repo), Now, CatalogFullLoadMarks.SafetyFullReloadInterval, openCheck: true, out _)
            .Should().BeTrue("метки нет — полная загрузка нужна сразу, даже при открытом чеке");
    }

    // ---------------------------------------------------------------- пачки в базу и удаление лишнего

    [Fact]
    public void ChunkedWrite_DoesNotDeleteOthers_AndDeleteProductsNotInRemovesTheRest()
    {
        FillWithShortFeedCards(5); // s-1..s-5

        // Пачка с двумя товарами: остальные не трогаются.
        var chunk = new[] { "s-1", "n-1" }.Select(id => ProductCatalogMapper.TryTile(J($$"""{"id":"{{id}}","name":"Новый {{id}}","price":"11.00","quantity":"1","unit":"шт","is_weight":false}"""), ApiBase)!).ToList();
        var (added, changed, deleted) = _repo.SyncReplaceAllWithDiff(chunk, deleteMissing: false);
        added.Should().Be(1);
        changed.Should().Be(1);
        deleted.Should().Be(0);
        _repo.CountProducts().Should().Be(6);

        // Загрузка прошла все страницы с первой — лишнее (s-2..s-5) убираем.
        var seen = new HashSet<string>(["s-1", "n-1"], StringComparer.OrdinalIgnoreCase);
        _repo.DeleteProductsNotIn(seen).Should().Be(4);
        _repo.CountProducts().Should().Be(2);
        _repo.DeleteProductsNotIn(seen).Should().Be(0);
    }

    // ---------------------------------------------------------------- докачка со страницы обрыва (поддельный сервер)

    private const int PerPage = 100;
    private const int Pages = 6;

    private static string PageBody(int page, int perPage = PerPage, int totalPages = Pages)
    {
        var sb = new StringBuilder();
        sb.Append("{\"count\":").Append(totalPages * PerPage).Append(",\"next\":");
        sb.Append(page < totalPages ? "\"http://x/api/main/products/list/?page=" + (page + 1) + "\"" : "null");
        sb.Append(",\"results\":[");
        for (var i = 0; i < perPage; i++)
        {
            if (i > 0)
                sb.Append(',');
            var n = (page - 1) * PerPage + i + 1;
            sb.Append("{\"id\":\"p").Append(n).Append("\",\"name\":\"Товар ").Append(n).Append("\",\"price\":\"10.00\",\"quantity\":\"1\",\"unit\":\"шт\",\"is_weight\":false}");
        }

        return sb.Append("]}").ToString();
    }

    private static void StubPages(FakeNurCrmServer server, int failFromPage = int.MaxValue, int perPage = PerPage)
    {
        server.Server.ResetMappings();
        for (var page = 1; page <= Pages; page++)
        {
            var request = Request.Create().WithPath("/api/main/products/list/").UsingGet().WithParam("page", page.ToString());
            var response = page >= failFromPage
                ? Response.Create().WithStatusCode(403).WithHeader("Content-Type", "application/json").WithBody("{\"detail\":\"нет доступа\"}")
                : Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(PageBody(page, perPage));
            server.Server.Given(request).RespondWith(response);
        }
    }

    private static int[] RequestedPages(FakeNurCrmServer server) =>
        server.Server.LogEntries
            .Where(e => e.RequestMessage.Path == "/api/main/products/list/")
            .Select(e => int.Parse(e.RequestMessage.Query!["page"][0]))
            .ToArray();

    private static void ForgetServerPageCap() =>
        typeof(CatalogApiService).GetField("_serverPageCap", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0);

    [Fact]
    public async Task Interrupted_Load_ResumesFromSavedPage_NotFromTheFirst()
    {
        using var server = new FakeNurCrmServer();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var catalog = new CatalogApiService(api);
        ForgetServerPageCap();
        try
        {
            // Первая попытка: страницы 1–3 идут, на 4-й сервер отказывает (обрыв).
            StubPages(server, failFromPage: 4);
            var first = new CatalogFullResume();
            var stored = new List<string>();
            first.PageDone = (next, size, accepted) =>
            {
                CatalogFullLoadMarks.SaveProgress(_repo, new CatalogFullLoadMarks.Progress(next, size, DateTime.UtcNow, accepted));
                return Task.CompletedTask;
            };
            var firstRun = async () => await catalog.ProductsCatalogStreamAsync(int.MaxValue, 100,
                batch => stored.AddRange(batch.Select(b => b.GetProperty("id").GetString()!)), CancellationToken.None, first);
            (await firstRun.Should().ThrowAsync<ApiException>()).Which.StatusCode.Should().Be(403);
            first.FromStart.Should().BeTrue();
            stored.Should().HaveCount(3 * PerPage);
            RequestedPages(server).Should().Contain(new[] { 1, 2, 3, 4 });

            var progress = CatalogFullLoadMarks.GetProgress(_repo, DateTime.UtcNow);
            progress.Should().NotBeNull("ход сохранён после каждой удачной страницы");
            progress!.Value.NextPage.Should().Be(4);
            progress.Value.PageSize.Should().Be(PerPage, "запоминаем размер страницы, которым шла загрузка (сервер отдаёт по 100, просили 300)");

            // Вторая попытка (новый запуск): сервер ожил — идём с сохранённой страницы (с нахлёстом в одну), а не с первой.
            StubPages(server);
            server.Server.ResetLogEntries();
            var second = new CatalogFullResume
            {
                StartPage = CatalogFullLoadMarks.ResumePage(progress.Value),
                PageSize = progress.Value.PageSize,
            };
            var got = new List<string>();
            var delivered = await catalog.ProductsCatalogStreamAsync(int.MaxValue, 100,
                batch => got.AddRange(batch.Select(b => b.GetProperty("id").GetString()!)), CancellationToken.None, second);

            RequestedPages(server).Should().Equal(3, 4, 5, 6);
            second.FromStart.Should().BeFalse("докачка: по её итогам лишнее из базы удалять нельзя");
            delivered.Should().Be(4 * PerPage);
            got.Should().Contain("p600").And.NotContain("p1");
        }
        finally
        {
            ForgetServerPageCap();
        }
    }

    [Fact]
    public async Task Resume_WithChangedPageSize_FallsBackToFirstPage()
    {
        using var server = new FakeNurCrmServer();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var catalog = new CatalogApiService(api);
        ForgetServerPageCap();
        try
        {
            StubPages(server);
            server.Server.ResetLogEntries();
            // Прошлая попытка шла страницами по 200, а сервер сейчас режет до 100 — номера страниц не совпадают.
            var resume = new CatalogFullResume { StartPage = 3, PageSize = 200 };
            var got = new List<string>();
            var delivered = await catalog.ProductsCatalogStreamAsync(int.MaxValue, 100,
                batch => got.AddRange(batch.Select(b => b.GetProperty("id").GetString()!)), CancellationToken.None, resume);

            RequestedPages(server).Should().StartWith(new[] { 3, 1 }, "после несовпадения размера страницы — заново с первой");
            resume.FromStart.Should().BeTrue();
            delivered.Should().Be(Pages * PerPage);
            got.Should().Contain("p1").And.Contain("p600");
        }
        finally
        {
            ForgetServerPageCap();
        }
    }

    [Fact]
    public async Task PageDone_IsCalledPerPage_ButNotAfterTheLastOne()
    {
        using var server = new FakeNurCrmServer();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var catalog = new CatalogApiService(api);
        ForgetServerPageCap();
        try
        {
            StubPages(server);
            var nextPages = new List<int>();
            var resume = new CatalogFullResume { PageDone = (next, _, _) => { nextPages.Add(next); return Task.CompletedTask; } };
            await catalog.ProductsCatalogStreamAsync(int.MaxValue, 100, _ => { }, CancellationToken.None, resume);
            nextPages.Should().Equal(2, 3, 4, 5, 6);
            resume.FromStart.Should().BeTrue();
        }
        finally
        {
            ForgetServerPageCap();
        }
    }
}
