using FluentAssertions;
using NurMarketKassa.Services.Hardware;
using Xunit;

namespace NurMarketKassa.Tests.Scales;

/// <summary>2026-11-13: автоотправка изменённых товаров на весы (решение владельца по сравнению с Paloma365). Проверяются правила без окна и без сети: отпечаток товара,
/// склейка правок (тишина 90 с, потолок ожидания 10 мин), повтор после неудачи (2, 5, 15, 30 мин), выключатель, «закреплены ли номера», Масса-К и CAS,
/// и координатор с ПОДСТАВНЫМИ весами — настоящие весы владельца (Rongta 192.168.1.87) тесты не трогают.</summary>
public sealed class ScaleAutoSendTests
{
    private static readonly DateTime T0 = new(2026, 11, 13, 10, 0, 0);

    // ───────────── отпечаток и «что изменилось» ─────────────

    [Fact]
    public void Signature_ChangesOnPriceNameUnitPluAndCode_NotOnNothingElse()
    {
        var a = ScaleAutoSendSignature.Of("Яблоки", 120m, "кг", true, 5, "5");
        ScaleAutoSendSignature.Of("Яблоки", 120m, "кг", true, 5, "5").Should().Be(a);
        ScaleAutoSendSignature.Of(" Яблоки ", 120.00m, "КГ", true, 5, "005").Should().Be(a, "пробелы, регистр единицы и ведущие нули кода не меняют товар");
        ScaleAutoSendSignature.Of("Яблоки", 125m, "кг", true, 5, "5").Should().NotBe(a);
        ScaleAutoSendSignature.Of("Яблоки красные", 120m, "кг", true, 5, "5").Should().NotBe(a);
        ScaleAutoSendSignature.Of("Яблоки", 120m, "шт", true, 5, "5").Should().NotBe(a);
        ScaleAutoSendSignature.Of("Яблоки", 120m, "кг", true, 6, "5").Should().NotBe(a);
        ScaleAutoSendSignature.Of("Яблоки", 120m, "кг", true, 5, "7").Should().NotBe(a);
        ScaleAutoSendSignature.Of("Яблоки", 120m, "кг", false, 5, "5").Should().NotBe(a);
    }

    [Fact]
    public void Changed_ReturnsNewAndChangedOnly_AndIgnoresRemoved()
    {
        var current = new[] { ("a", "s1"), ("b", "s2-new"), ("c", "s3") };
        var sent = new Dictionary<string, string?> { ["a"] = "s1", ["b"] = "s2-old", ["gone"] = "x" };
        ScaleAutoSendSignature.Changed(current, sent).Should().Equal("b", "c");
    }

    [Fact]
    public void Changed_NothingSent_MeansEverythingIsNew()
    {
        ScaleAutoSendSignature.Changed(new[] { ("a", "s"), ("b", "s") }, new Dictionary<string, string?>()).Should().Equal("a", "b");
    }

    // ───────────── склейка и повтор ─────────────

    [Fact]
    public void Queue_WaitsForQuietPeriod_ThenFires()
    {
        var q = new ScaleAutoSendQueue();
        q.IsDue(T0).Should().BeFalse("изменений не было");
        q.Note(T0);
        q.IsDue(T0.AddSeconds(30)).Should().BeFalse();
        q.IsDue(T0.AddSeconds(89)).Should().BeFalse();
        q.IsDue(T0.AddSeconds(90)).Should().BeTrue();
    }

    [Fact]
    public void Queue_NewChangesExtendTheQuietPeriod_ButNotBeyondMaxWait()
    {
        var q = new ScaleAutoSendQueue();
        q.Note(T0);
        // правка каждую минуту: склейка откладывает отправку
        for (var m = 1; m <= 9; m++)
        {
            q.Note(T0.AddMinutes(m));
            q.IsDue(T0.AddMinutes(m).AddSeconds(30)).Should().BeFalse($"на {m}-й минуте поток правок ещё идёт");
        }
        q.IsDue(T0.AddMinutes(10)).Should().BeTrue("потолок ожидания 10 минут с первой правки");
    }

    [Fact]
    public void Queue_AfterSuccess_IsClean()
    {
        var q = new ScaleAutoSendQueue();
        q.Note(T0);
        q.Succeeded();
        q.Dirty.Should().BeFalse();
        q.IsDue(T0.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void Queue_Failure_BacksOff_2_5_15_30_Minutes_AndStaysDirty()
    {
        var q = new ScaleAutoSendQueue();
        q.Note(T0);
        var now = T0.AddMinutes(2);
        var expected = new[] { 2, 5, 15, 30, 30 };
        foreach (var minutes in expected)
        {
            q.IsDue(now).Should().BeTrue();
            q.Failed(now);
            q.Dirty.Should().BeTrue("неудача — изменения остаются в очереди");
            q.RetryAt.Should().Be(now.AddMinutes(minutes));
            q.IsDue(now.AddMinutes(minutes).AddSeconds(-1)).Should().BeFalse();
            now = now.AddMinutes(minutes);
        }
        q.Succeeded();
        q.Failures.Should().Be(0);
    }

    // ───────────── кому можно ─────────────

    [Theory]
    [InlineData("tm", false, false, false, true)]
    [InlineData("shtrikhpc", false, false, false, true)]
    [InlineData("rongta", true, false, false, true)]
    [InlineData("rongta", false, false, false, false)]   // через RLS1000 / свой сервер — не тихая отправка
    [InlineData("shtrikh", false, true, false, true)]
    [InlineData("shtrikh", false, false, false, false)]  // «через сервер NurCRM»
    [InlineData("ai", true, true, true, false)]          // AI — только файл
    [InlineData("cas", true, true, true, false)]         // решение 13.11: CAS в первой версии не включаем
    [InlineData("massak", true, true, false, false)]     // Масса-К — только после MassaKVerified
    [InlineData("massak", true, true, true, true)]
    public void Policy_Supported(string brand, bool rongtaDirect, bool shtrikhDirect, bool massaKVerified, bool expected) =>
        ScaleAutoSendPolicy.Supported(brand, rongtaDirect, shtrikhDirect, massaKVerified).Should().Be(expected);

    [Fact]
    public void Policy_ShouldRun_NeedsSwitchDirectSendAndPreparedNumbers()
    {
        ScaleAutoSendPolicy.ShouldRun(new ScaleAutoSendProfile { AutoSend = true, DirectSend = true, Prepared = true }).Should().BeTrue();
        ScaleAutoSendPolicy.ShouldRun(new ScaleAutoSendProfile { AutoSend = false, DirectSend = true, Prepared = true }).Should().BeFalse("выключатель выключен — по умолчанию у существующих весов");
        ScaleAutoSendPolicy.ShouldRun(new ScaleAutoSendProfile { AutoSend = true, DirectSend = false, Prepared = true }).Should().BeFalse();
        ScaleAutoSendPolicy.ShouldRun(new ScaleAutoSendProfile { AutoSend = true, DirectSend = true, Prepared = false }).Should().BeFalse("номера PLU ещё не закреплены — первой отправкой руками");
    }

    // ───────────── координатор с подставными весами ─────────────

    private sealed class FakeSink : IScaleAutoSendSink
    {
        public readonly List<(string Profile, string[] Ids)> Calls = new();
        public Func<ScaleAutoSendResult> Answer = () => new ScaleAutoSendResult(true, 1, null, false);

        public Task<ScaleAutoSendResult> SendAsync(string profileId, IReadOnlyList<string> productIds, CancellationToken ct)
        {
            Calls.Add((profileId, productIds.ToArray()));
            return Task.FromResult(Answer());
        }
    }

    private sealed class Rig
    {
        public DateTime Now = T0;
        public bool Blocked;
        public bool AutoSend = true, Direct = true, Prepared = true;
        public List<(string, string)> Catalog = new() { ("a", "a1"), ("b", "b1") };
        public Dictionary<string, string?> Sent = new() { ["a"] = "a1", ["b"] = "b1" };
        public readonly FakeSink Sink = new();
        public ScaleAutoSendCoordinator Coordinator = null!;

        public Rig()
        {
            Coordinator = new ScaleAutoSendCoordinator(
                () => Now,
                () => new[] { new ScaleAutoSendProfile { Id = "p1", Brand = "tm", AutoSend = AutoSend, DirectSend = Direct, Prepared = Prepared } },
                _ => Catalog,
                _ => Sent,
                Sink,
                () => Blocked);
        }
    }

    [Fact]
    public async Task Coordinator_SendsOnlyChangedProducts_AfterQuietPeriod_AndOnce()
    {
        var rig = new Rig();
        rig.Catalog[1] = ("b", "b2"); // у товара b сменилась цена
        rig.Coordinator.OnCatalogChanged();

        (await rig.Coordinator.TickAsync(default)).Should().Be(0, "тишины 90 с ещё нет — склейка");
        rig.Now = T0.AddSeconds(95);
        (await rig.Coordinator.TickAsync(default)).Should().Be(1);
        rig.Sink.Calls.Should().ContainSingle().Which.Ids.Should().Equal("b");

        rig.Sent["b"] = "b2"; // приёмник записал отпечаток
        rig.Now = T0.AddMinutes(10);
        (await rig.Coordinator.TickAsync(default)).Should().Be(0, "очередь чистая — повторно не шлёт");
    }

    [Fact]
    public async Task Coordinator_ChangeNotAffectingScale_SendsNothing()
    {
        var rig = new Rig(); // отпечатки те же (менялся остаток или закупочная цена)
        rig.Coordinator.OnCatalogChanged();
        rig.Now = T0.AddMinutes(2);
        (await rig.Coordinator.TickAsync(default)).Should().Be(0);
        rig.Sink.Calls.Should().BeEmpty();
        rig.Coordinator.QueueOf("p1").Dirty.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinator_NewWeighedProduct_IsSent()
    {
        var rig = new Rig();
        rig.Catalog.Add(("c", "c1"));
        rig.Coordinator.OnCatalogChanged();
        rig.Now = T0.AddMinutes(2);
        await rig.Coordinator.TickAsync(default);
        rig.Sink.Calls.Single().Ids.Should().Equal("c");
    }

    [Fact]
    public async Task Coordinator_ScaleOffline_RetriesLater_WithoutErrorsToCashier()
    {
        var rig = new Rig();
        rig.Catalog[0] = ("a", "a2");
        rig.Sink.Answer = () => new ScaleAutoSendResult(false, 0, "нет подключения", true);
        rig.Coordinator.OnCatalogChanged();
        rig.Now = T0.AddMinutes(2);
        (await rig.Coordinator.TickAsync(default)).Should().Be(1);

        rig.Now = T0.AddMinutes(3);
        (await rig.Coordinator.TickAsync(default)).Should().Be(0, "повтор не раньше чем через 2 минуты");
        rig.Now = T0.AddMinutes(4).AddSeconds(5);
        rig.Sink.Answer = () => new ScaleAutoSendResult(true, 1, null, false);
        (await rig.Coordinator.TickAsync(default)).Should().Be(1, "весы включили — повтор прошёл");
        rig.Sink.Calls.Should().HaveCount(2);
        rig.Coordinator.QueueOf("p1").Dirty.Should().BeFalse();
    }

    [Fact]
    public async Task Coordinator_SinkThrows_IsTreatedAsRetry()
    {
        var rig = new Rig();
        rig.Catalog[0] = ("a", "a2");
        rig.Sink.Answer = () => throw new InvalidOperationException("boom");
        rig.Coordinator.OnCatalogChanged();
        rig.Now = T0.AddMinutes(2);
        (await rig.Coordinator.TickAsync(default)).Should().Be(1);
        rig.Coordinator.QueueOf("p1").Failures.Should().Be(1);
        rig.Coordinator.QueueOf("p1").Dirty.Should().BeTrue();
    }

    [Fact]
    public async Task Coordinator_SwitchOff_NeverSends_AndNeverQueues()
    {
        var rig = new Rig { AutoSend = false };
        rig.Catalog[0] = ("a", "a2");
        rig.Coordinator.OnCatalogChanged();
        rig.Now = T0.AddHours(1);
        (await rig.Coordinator.TickAsync(default)).Should().Be(0);
        rig.Coordinator.QueueOf("p1").Dirty.Should().BeFalse();
        rig.Sink.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Coordinator_NotPrepared_OrNoDirectSend_NeverSends()
    {
        foreach (var rig in new[] { new Rig { Prepared = false }, new Rig { Direct = false } })
        {
            rig.Catalog[0] = ("a", "a2");
            rig.Coordinator.OnCatalogChanged();
            rig.Now = T0.AddHours(1);
            (await rig.Coordinator.TickAsync(default)).Should().Be(0);
            rig.Sink.Calls.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Coordinator_WhileScalesWindowIsOpen_Waits_ThenSends()
    {
        var rig = new Rig { Blocked = true };
        rig.Catalog[0] = ("a", "a2");
        rig.Coordinator.OnCatalogChanged();
        rig.Now = T0.AddMinutes(5);
        (await rig.Coordinator.TickAsync(default)).Should().Be(0, "открыто окно «Весы» — не мешаем");
        rig.Blocked = false;
        (await rig.Coordinator.TickAsync(default)).Should().Be(1);
    }

    [Fact]
    public void LastText_ShowsTimeCountAndError()
    {
        ScaleAutoSendText.Last(null, 0, null, null).Should().NotBeNullOrWhiteSpace();
        ScaleAutoSendText.Last(T0, 3, null, null).Should().Contain("10:00").And.Contain("3");
        ScaleAutoSendText.Last(T0, 0, "нет подключения", T0.AddMinutes(2)).Should().Contain("нет подключения").And.Contain("10:02");
    }
}
