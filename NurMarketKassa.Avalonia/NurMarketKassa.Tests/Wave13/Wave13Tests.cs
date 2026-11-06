using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.Tests.Wave13;

/// <summary>2026-11-06, волна В13 «Картинки, ИИ, внешние службы»: чистка кэша фото, история в фоне, реестр ключей (Н-17), пауза Gemini Live (Н-11),
/// PIN владельца на ПК с кассой (Л4-19), выбор фото (штраф .webp), отчёты о падениях.</summary>
public sealed class Wave13Tests : IDisposable
{
    private readonly string _dir = Path.Combine(NurMarketKassa.Tests.Infrastructure.TestDataRoot.Path, "w13-" + Guid.NewGuid().ToString("N")[..8]);

    public Wave13Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* временная папка */ }
    }

    private string MakeFile(string rel, int bytes, DateTime? written = null)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (written is { } w)
            File.SetLastWriteTimeUtc(path, w);
        return path;
    }

    // ---------- FileCachePruner (шаг 21) ----------

    [Fact]
    public void PruneBySize_UnderLimit_DeletesNothing()
    {
        MakeFile("a.bin", 100);
        MakeFile("b.bin", 100);
        var (deleted, _) = FileCachePruner.PruneBySize(_dir, 1000);
        deleted.Should().Be(0);
        Directory.GetFiles(_dir).Length.Should().Be(2);
    }

    [Fact]
    public void PruneBySize_OverLimit_DeletesOldestFirst_DownToTarget()
    {
        var now = DateTime.UtcNow;
        var oldest = MakeFile("old.bin", 400, now.AddDays(-3));
        var mid = MakeFile("full/mid.bin", 400, now.AddDays(-2));
        var newest = MakeFile("new.bin", 400, now.AddDays(-1));

        var (deleted, freed) = FileCachePruner.PruneBySize(_dir, 1000, 0.5); // 1200 → цель 500: уйдут два самых старых

        deleted.Should().Be(2);
        freed.Should().Be(800);
        File.Exists(oldest).Should().BeFalse();
        File.Exists(mid).Should().BeFalse();
        File.Exists(newest).Should().BeTrue();
    }

    [Fact]
    public void PruneBySize_RemovesStaleTmp_KeepsFreshTmp()
    {
        var stale = MakeFile("x.tmp", 10, DateTime.UtcNow.AddHours(-3));
        var fresh = MakeFile("y.tmp", 10, DateTime.UtcNow);
        FileCachePruner.PruneBySize(_dir, 1_000_000);
        File.Exists(stale).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void PruneBySize_MissingFolder_NoException() =>
        FileCachePruner.PruneBySize(Path.Combine(_dir, "нет"), 10).Deleted.Should().Be(0);

    [Fact]
    public void PruneByCount_KeepsNewest()
    {
        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            MakeFile($"crash_{i}.txt", 5, now.AddMinutes(-i));
        MakeFile("other.txt", 5, now.AddDays(-9));

        var deleted = FileCachePruner.PruneByCount(_dir, "crash_*.txt", 2);

        deleted.Should().Be(3);
        Directory.GetFiles(_dir, "crash_*.txt").Select(Path.GetFileName).Should().BeEquivalentTo("crash_0.txt", "crash_1.txt");
        File.Exists(Path.Combine(_dir, "other.txt")).Should().BeTrue();
    }

    [Fact]
    public void MoveAll_MovesFiles_KeepsExisting_RemovesOldFolder()
    {
        var from = Path.Combine(_dir, "old");
        var to = Path.Combine(_dir, "new");
        File.WriteAllBytes(MakeFile("old/a.webp", 3), new byte[] { 1, 1, 1 });
        MakeFile("old/full/b.webp", 3);
        Directory.CreateDirectory(to);
        File.WriteAllBytes(Path.Combine(to, "a.webp"), new byte[] { 9, 9, 9, 9 }); // уже есть в новой папке — не затирается

        var moved = FileCachePruner.MoveAll(from, to);

        moved.Should().Be(1); // b.webp; a.webp остался новый
        File.ReadAllBytes(Path.Combine(to, "a.webp")).Length.Should().Be(4);
        File.Exists(Path.Combine(to, "full", "b.webp")).Should().BeTrue();
        Directory.Exists(from).Should().BeFalse();
    }

    // ---------- CrashReportService: не больше 50 отчётов ----------

    [Fact]
    public void CrashReports_AreCapped()
    {
        for (var i = 0; i < CrashReportService.MaxReports + 7; i++)
        {
            CrashReportService.WriteReport(new InvalidOperationException("тест " + i), "w13-test");
            Thread.Sleep(2); // имя файла — по миллисекундам
        }
        CrashReportService.PendingReportCount().Should().BeLessThanOrEqualTo(CrashReportService.MaxReports);
    }

    // ---------- DeferredJsonList (Л4-13: история Советника) ----------

    private sealed record Row(string Id, string Text);

    [Fact]
    public void DeferredJsonList_LoadAfterStore_ComesFromMemory_AndFileAppears()
    {
        var path = Path.Combine(_dir, "chats.json");
        var list = new DeferredJsonList<Row>(() => path, 30);

        list.Store(new List<Row> { new("1", "привет") });
        list.Load().Should().ContainSingle(r => r.Text == "привет"); // сразу, не дожидаясь диска
        list.Flush(TimeSpan.FromSeconds(5)).Should().BeTrue();

        File.Exists(path).Should().BeTrue();
        new DeferredJsonList<Row>(() => path, 30).Load().Should().ContainSingle(r => r.Id == "1");
    }

    [Fact]
    public void DeferredJsonList_ManyQuickStores_LastWins_AndCapped()
    {
        var path = Path.Combine(_dir, "chats2.json");
        var list = new DeferredJsonList<Row>(() => path, 3);

        for (var i = 0; i < 20; i++)
            list.Store(Enumerable.Range(0, 10).Select(n => new Row($"{i}-{n}", "x")).ToList());
        list.Flush(TimeSpan.FromSeconds(5)).Should().BeTrue();

        var onDisk = JsonSerializer.Deserialize<List<Row>>(File.ReadAllText(path))!;
        onDisk.Should().HaveCount(3);
        onDisk[0].Id.Should().Be("19-0");
    }

    [Fact]
    public void DeferredJsonList_ReturnsCopy_ChangesDoNotLeak()
    {
        var path = Path.Combine(_dir, "chats3.json");
        var list = new DeferredJsonList<Row>(() => path, 30);
        list.Store(new List<Row> { new("1", "a") });
        var copy = list.Load();
        copy.Clear();
        list.Load().Should().HaveCount(1);
        list.Flush(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void DeferredJsonList_FileChangedByOutsider_IsReread()
    {
        var path = Path.Combine(_dir, "chats4.json");
        var list = new DeferredJsonList<Row>(() => path, 30);
        list.Store(new List<Row> { new("1", "a") });
        list.Flush(TimeSpan.FromSeconds(5));

        File.WriteAllText(path, "[{\"Id\":\"zz\",\"Text\":\"чужая\"}]");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        list.Load().Should().ContainSingle(r => r.Id == "zz");
    }

    [Fact]
    public void DeferredJsonList_FileDeleted_ReadsEmpty()
    {
        var path = Path.Combine(_dir, "chats5.json");
        var list = new DeferredJsonList<Row>(() => path, 30);
        list.Store(new List<Row> { new("1", "a") });
        list.Flush(TimeSpan.FromSeconds(5));
        File.Delete(path);
        File.Delete(path + ".bak");

        list.Load().Should().BeEmpty(); // смена компании стёрла файл — прежние разговоры не воскресают
    }

    // ---------- Н-11: пауза между переподключениями Gemini Live ----------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 8)]
    [InlineData(9, 8)]
    [InlineData(-1, 1)]
    public void GeminiLive_ReconnectDelay_1_2_4_8(int attempt, int seconds) =>
        GeminiLiveVoice.ReconnectDelay(attempt).Should().Be(TimeSpan.FromSeconds(seconds));

    // ---------- Л4-19: PIN владельца на ПК с кассой ----------

    [Fact]
    public void OwnerStartupGate_AsksOnlyOwnerProgramOnKassaPc()
    {
        OwnerStartupGate.ShouldAskPin(isOwnerMode: true, kassaInstalledHere: true).Should().BeTrue();
        OwnerStartupGate.ShouldAskPin(isOwnerMode: true, kassaInstalledHere: false).Should().BeFalse();
        OwnerStartupGate.ShouldAskPin(isOwnerMode: false, kassaInstalledHere: true).Should().BeFalse();
    }

    [Fact]
    public void OwnerStartupGate_KassaInstalled_ByPosLocalDb()
    {
        var roaming = Path.Combine(_dir, "roaming");
        OwnerStartupGate.KassaInstalledHere(roaming).Should().BeFalse();
        File.WriteAllBytes(MakeFile("roaming/NurMarketOwner/data/pos_local.db", 1), new byte[] { 1 });
        OwnerStartupGate.KassaInstalledHere(roaming).Should().BeFalse(); // база программы владельца — не касса
        MakeFile("roaming/NurMarketKassa/data/pos_local.db", 1);
        OwnerStartupGate.KassaInstalledHere(roaming).Should().BeTrue();
    }

    // ---------- шаг 21: .webp без штрафа ----------

    [Fact]
    public void ProductImageUrl_WebpNoLongerPenalised()
    {
        using var doc = JsonDocument.Parse("""{"images":["https://x.kg/a.webp","https://x.kg/b.gif"]}""");
        // раньше webp получал −50 и проигрывал даже gif; теперь легче всех, кроме jpg/png
        ProductImageUrl.TryGet(doc.RootElement, "https://x.kg/").Should().Be("https://x.kg/a.webp");

        using var doc2 = JsonDocument.Parse("""{"images":["https://x.kg/a.webp","https://x.kg/b.jpg"]}""");
        ProductImageUrl.TryGet(doc2.RootElement, "https://x.kg/").Should().Be("https://x.kg/b.jpg");
    }

    // ---------- Н-17: реестр ключей — после неясного ответа перечитать ----------

    private sealed class FakeGitHub : HttpMessageHandler
    {
        public JsonObject Registry = new();
        public string Sha = "sha-1";
        public int Gets;
        public int Puts;
        public Func<int, HttpResponseMessage?>? PutScript;   // номер PUT (с 1) → ответ; null → принять и записать
        public bool PutAppliesBeforeFailure;                 // сервер принял запись, но ответ «потерялся»
        public bool GetFailsAfterPut;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                Gets++;
                if (GetFailsAfterPut && Puts > 0)
                    throw new HttpRequestException("сеть пропала");
                var json = Registry.ToJsonString();
                var body = new JsonObject { ["sha"] = Sha, ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)) };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") });
            }

            Puts++;
            var payload = JsonNode.Parse(request.Content!.ReadAsStringAsync(ct).Result)!.AsObject();
            var written = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload["content"]!.GetValue<string>())))!.AsObject();
            var scripted = PutScript?.Invoke(Puts);
            if (scripted is null || PutAppliesBeforeFailure)
            {
                Registry = written;
                Sha = "sha-" + (Puts + 1);
            }
            if (scripted is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
            if (scripted.StatusCode == HttpStatusCode.RequestTimeout && PutAppliesBeforeFailure)
                throw new TaskCanceledException("срок");
            return Task.FromResult(scripted);
        }
    }

    private static GithubLicenseRegistryClient Client(FakeGitHub fake)
    {
        var client = new GithubLicenseRegistryClient(new HttpClient(fake), "test-token") { Delay = (_, _) => Task.CompletedTask };
        return client;
    }

    private static FakeGitHub RegistryWith(string key, string status = "unused", string? device = null)
    {
        var entry = new JsonObject { ["status"] = status };
        if (device is not null)
            entry["device"] = device;
        return new FakeGitHub { Registry = new JsonObject { [key] = entry } };
    }

    [Fact]
    public async Task License_FreeKey_IsActivated_AndMarkedWithThisPc()
    {
        var fake = RegistryWith("KEY-1");
        var (ok, error) = await Client(fake).TryActivateAsync(" KEY-1 ");

        ok.Should().BeTrue(error);
        fake.Puts.Should().Be(1);
        fake.Registry["KEY-1"]!["status"]!.GetValue<string>().Should().Be("used");
        fake.Registry["KEY-1"]!["device"]!.GetValue<string>().Should().Be(GithubLicenseRegistryClient.DeviceTag());
    }

    [Fact]
    public async Task License_PutTimesOutButWasApplied_RereadConfirms_NotBurned()
    {
        var fake = RegistryWith("KEY-2");
        fake.PutAppliesBeforeFailure = true;
        fake.PutScript = _ => new HttpResponseMessage(HttpStatusCode.RequestTimeout);

        var (ok, error) = await Client(fake).TryActivateAsync("KEY-2");

        ok.Should().BeTrue(error);          // запись прошла на GitHub — ключ не «сгорел»
        fake.Gets.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task License_Put502WasNotApplied_ReportsRetryable_AndKeyStaysFree()
    {
        var fake = RegistryWith("KEY-3");
        fake.PutScript = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var (ok, error) = await Client(fake).TryActivateAsync("KEY-3");

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
        fake.Registry["KEY-3"]!["status"]!.GetValue<string>().Should().Be("unused");
        // повторная попытка после восстановления сети — проходит
        fake.PutScript = null;
        (await Client(fake).TryActivateAsync("KEY-3")).Success.Should().BeTrue();
    }

    [Fact]
    public async Task License_PutAppliedButRereadFails_ReturnsRetryableMessage_ThenSecondAttemptSucceedsOnSamePc()
    {
        var fake = RegistryWith("KEY-4");
        fake.PutAppliesBeforeFailure = true;
        fake.GetFailsAfterPut = true;
        fake.PutScript = _ => new HttpResponseMessage(HttpStatusCode.RequestTimeout);

        var first = await Client(fake).TryActivateAsync("KEY-4");
        first.Success.Should().BeFalse();

        // сеть вернулась: ключ уже отмечен ЭТИМ ПК — это успех, а не «ключ использован на другом устройстве»
        fake.GetFailsAfterPut = false;
        var second = await Client(fake).TryActivateAsync("KEY-4");
        second.Success.Should().BeTrue(second.Error);
        fake.Puts.Should().Be(1); // повторной записи нет
    }

    [Fact]
    public async Task License_UsedByAnotherDevice_IsRefused()
    {
        var fake = RegistryWith("KEY-5", "used", device: "ffffffffffff");
        var (ok, error) = await Client(fake).TryActivateAsync("KEY-5");
        ok.Should().BeFalse();
        error.Should().Contain("другом устройстве");
        fake.Puts.Should().Be(0);
    }

    [Fact]
    public async Task License_UsedOldFormatWithoutDevice_IsRefused()
    {
        var fake = RegistryWith("KEY-6", "used"); // записи прежних активаций без метки ПК
        (await Client(fake).TryActivateAsync("KEY-6")).Success.Should().BeFalse();
    }

    [Fact]
    public async Task License_UnknownKey_IsRefused_WithoutWrite()
    {
        var fake = RegistryWith("KEY-7");
        var (ok, error) = await Client(fake).TryActivateAsync("НЕТ-ТАКОГО");
        ok.Should().BeFalse();
        error.Should().Contain("не найден");
        fake.Puts.Should().Be(0);
    }

    [Fact]
    public async Task License_Conflict409_AsksToRetry_NoReread()
    {
        var fake = RegistryWith("KEY-8");
        fake.PutScript = _ => new HttpResponseMessage(HttpStatusCode.Conflict);
        var (ok, error) = await Client(fake).TryActivateAsync("KEY-8");
        ok.Should().BeFalse();
        error.Should().Contain("одновременно");
        fake.Gets.Should().Be(1);
    }

    [Fact]
    public async Task License_NoToken_SaysUnavailable()
    {
        var client = new GithubLicenseRegistryClient(new HttpClient(new FakeGitHub()), null);
        var (ok, error) = await client.TryActivateAsync("KEY");
        ok.Should().BeFalse();
        error.Should().Contain("недоступна");
    }

    [Fact]
    public void License_Classify_Variants()
    {
        var r = new JsonObject
        {
            ["a"] = new JsonObject { ["status"] = "unused" },
            ["b"] = new JsonObject { ["status"] = "used", ["device"] = "abc" },
            ["c"] = new JsonObject { ["status"] = "USED" },
        };
        GithubLicenseRegistryClient.Classify(r, "a", "abc").Should().Be(GithubLicenseRegistryClient.KeyState.Free);
        GithubLicenseRegistryClient.Classify(r, "b", "abc").Should().Be(GithubLicenseRegistryClient.KeyState.UsedByThisPc);
        GithubLicenseRegistryClient.Classify(r, "b", "xyz").Should().Be(GithubLicenseRegistryClient.KeyState.UsedByOther);
        GithubLicenseRegistryClient.Classify(r, "c", "abc").Should().Be(GithubLicenseRegistryClient.KeyState.UsedByOther);
        GithubLicenseRegistryClient.Classify(r, "zzz", "abc").Should().Be(GithubLicenseRegistryClient.KeyState.NotFound);
    }
}
