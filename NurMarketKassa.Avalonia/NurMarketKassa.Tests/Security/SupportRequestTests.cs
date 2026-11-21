using System.IO.Compression;
using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Security;

/// <summary>2026-11-13 (Л-9 «Поддержка»): обращение в поддержку не должно унести секреты — ни в хвосте журнала, ни в описании, ни в архиве.</summary>
[Collection("SupportRequestJournal")]
public sealed class SupportRequestTests : IDisposable
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4ifQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
    private const string BotToken = "123456789:AAH-abcdefghijklmnopqrstuvwxyz012345";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-support-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public SupportRequestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SupportRequestJournal.PathOverride = "";
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("GET /api/x?jwt=" + Jwt + " failed", Jwt)]
    [InlineData("Cookie: " + Jwt, Jwt)]
    [InlineData("Authorization: Bearer abc.def.ghi", "abc.def.ghi")]
    [InlineData("POST https://api.telegram.org/bot" + BotToken + "/sendDocument", BotToken)]
    [InlineData("{\"password\":\"p@ss-1234\",\"login\":\"shop\"}", "p@ss-1234")]
    [InlineData("client_secret=SUPER-SECRET-VALUE;", "SUPER-SECRET-VALUE")]
    [InlineData("api_key: 0123456789abcdef", "0123456789abcdef")]
    public void Redact_ClosesSecrets(string source, string forbidden)
    {
        SensitiveDataRedactor.Redact(source).Should().NotContain(forbidden);
    }

    [Fact]
    public void Redact_BareJwt_IsClosed()
    {
        SensitiveDataRedactor.Redact("tok " + Jwt + " end").Should().NotContain("eyJ");
    }

    [Fact]
    public void RedactCredentials_ClosesKeysButKeepsContactPhone()
    {
        var text = "Не печатает. Звоните 0555 12 34 56. Bearer abc123.def456 токен " + BotToken + " jwt " + Jwt;
        var clean = SensitiveDataRedactor.RedactCredentials(text);

        clean.Should().NotContain("abc123.def456").And.NotContain(BotToken).And.NotContain("eyJ");
        clean.Should().Contain("0555 12 34 56");
    }

    [Fact]
    public void LogTail_TakesLastLines_AndMasksSecrets()
    {
        var path = Path.Combine(_dir, "nurmarket-kassa.log");
        var lines = new List<string>();
        for (var i = 1; i <= 50; i++)
            lines.Add($"2026-11-13 10:00:{i:00}.000 +06:00 [Information] [INFO] строка {i}");
        lines.Add("2026-11-13 10:01:00.000 +06:00 [Information] [INFO] вход password=\"hunter2\" Authorization: Bearer top.secret.value");
        lines.Add("2026-11-13 10:01:01.000 +06:00 [Warning] [WARN] запрос ?token=abc999 " + Jwt);
        lines.Add("2026-11-13 10:01:02.000 +06:00 [Information] [INFO] бот " + BotToken);
        File.WriteAllLines(path, lines);

        var tail = SupportLogTail.Build(path, 10, out var count);

        count.Should().Be(10);
        tail.Split(Environment.NewLine).Should().HaveCount(10);
        tail.Should().Contain("строка 44").And.NotContain("строка 43");
        tail.Should().NotContain("hunter2").And.NotContain("top.secret.value").And.NotContain("abc999")
            .And.NotContain("eyJ").And.NotContain(BotToken);
    }

    [Fact]
    public void LogTail_MissingFile_IsEmpty()
    {
        SupportLogTail.Build(Path.Combine(_dir, "none.log"), 100, out var count).Should().BeEmpty();
        count.Should().Be(0);
    }

    [Fact]
    public void LogTail_FileOpenForWriting_IsReadable()
    {
        var path = Path.Combine(_dir, "open.log");
        using var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
        writer.WriteLine("первая");
        writer.WriteLine("вторая password=zzz");
        writer.Flush();

        var tail = SupportLogTail.Build(path, 5, out var count);

        count.Should().Be(2);
        tail.Should().Contain("первая").And.NotContain("zzz");
    }

    [Fact]
    public void CleanDescription_ClosesKeys_AndCutsLength()
    {
        SupportRequestService.CleanDescription("помогите Bearer abc.def.ghi").Should().NotContain("abc.def.ghi");
        SupportRequestService.CleanDescription(new string('я', 5000)).Length.Should().Be(SupportRequestService.MaxDescriptionChars);
        SupportRequestService.CleanDescription(null).Should().BeEmpty();
    }

    [Fact]
    public void BuildZip_ContainsRequestLogAndScreenshot_WithoutSecrets()
    {
        var record = new SupportRequestRecord
        {
            Id = "261113-100000", CreatedAt = new DateTime(2026, 11, 13, 10, 0, 0), Version = "1.17.87", Company = "Тест", Branch = "b1", Source = "меню",
            Description = SupportRequestService.CleanDescription("Не печатает чек. Bearer abc.def.ghi"),
        };
        var tail = SensitiveDataRedactor.Redact("вход password=\"hunter2\"");

        var zip = SupportRequestService.BuildZip(record, tail, [1, 2, 3]);

        using var archive = new ZipArchive(new MemoryStream(zip));
        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo("request.txt", "log-tail.txt", "screenshot.png");
        var all = string.Concat(archive.Entries.Where(e => e.FullName.EndsWith(".txt")).Select(e =>
        {
            using var r = new StreamReader(e.Open());
            return r.ReadToEnd();
        }));
        all.Should().Contain("1.17.87").And.Contain("Тест").And.Contain("Не печатает чек");
        all.Should().NotContain("hunter2").And.NotContain("abc.def.ghi");
    }

    [Fact]
    public void BuildZip_WithoutLogAndScreenshot_HasOnlyRequest()
    {
        var record = new SupportRequestRecord { Id = "1", Description = "x" };

        using var archive = new ZipArchive(new MemoryStream(SupportRequestService.BuildZip(record, "", null)));

        archive.Entries.Select(e => e.FullName).Should().BeEquivalentTo("request.txt");
    }

    [Fact]
    public void Journal_SavesNewestFirst_UpdatesById_AndKeepsLast100()
    {
        SupportRequestJournal.PathOverride = Path.Combine(_dir, "support-requests.json");

        SupportRequestJournal.Save(new SupportRequestRecord { Id = "a", Description = "первое", Status = SupportRequestRecord.StatusFailed });
        SupportRequestJournal.Save(new SupportRequestRecord { Id = "b", Description = "второе", Status = SupportRequestRecord.StatusSent });
        SupportRequestJournal.Save(new SupportRequestRecord { Id = "a", Description = "первое", Status = SupportRequestRecord.StatusSent });

        var list = SupportRequestJournal.Load();
        list.Select(r => r.Id).Should().Equal("b", "a");
        list.Single(r => r.Id == "a").Status.Should().Be(SupportRequestRecord.StatusSent);

        for (var i = 0; i < 120; i++)
            SupportRequestJournal.Save(new SupportRequestRecord { Id = "n" + i });
        SupportRequestJournal.Load().Should().HaveCount(100);
        SupportRequestJournal.Load()[0].Id.Should().Be("n119");
    }

    [Fact]
    public void Journal_BrokenFile_IsEmptyList()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ не json");
        SupportRequestJournal.PathOverride = path;

        SupportRequestJournal.Load().Should().BeEmpty();
    }

    // 2026-11-20: бот не подключён — не тупик «канал не подключён», а WhatsApp с готовым текстом на номер поддержки NurCRM.
    [Fact]
    public void NotConfiguredChannel_BecomesWhatsApp_AndOtherErrorsStayFailed()
    {
        SupportRequestService.StatusFor(null).Should().Be(SupportRequestRecord.StatusSent);
        SupportRequestService.StatusFor(SupportLogService.NotConfiguredMessage).Should().Be(SupportRequestRecord.StatusWhatsApp);
        SupportRequestService.StatusFor("нет связи").Should().Be(SupportRequestRecord.StatusFailed);
        SupportRequestService.IsWhatsApp(new SupportRequestRecord { Status = SupportRequestRecord.StatusWhatsApp }).Should().BeTrue();
        SupportRequestService.IsWhatsApp(new SupportRequestRecord { Status = SupportRequestRecord.StatusNotConfigured }).Should().BeTrue("старые записи показываются так же");
        SupportRequestService.IsWhatsApp(new SupportRequestRecord { Status = SupportRequestRecord.StatusFailed }).Should().BeFalse();
    }

    [Fact]
    public void WhatsAppUrl_UsesSupportPhone_AndCarriesRequestText()
    {
        var record = new SupportRequestRecord { Id = "261120-121338", Version = "1.17.90", Company = "NBS", Description = "не печатает чек" };
        var url = SupportRequestService.WhatsAppUrl(record);
        url.Should().StartWith("https://wa.me/996221068796?text=");
        Uri.UnescapeDataString(url).Should().Contain("261120-121338").And.Contain("не печатает чек");
    }

    [Fact]
    public void SupportInfo_WithoutSettings_HasNoValues_SoWindowHidesTheRows()
    {
        SupportInfo.HoursOrNull().Should().BeNull();
        SupportInfo.ResponseTimeOrNull().Should().BeNull();
    }

    [Fact]
    public void SupportInfo_WithoutSettings_ShowsPlaceholder()
    {
        string[] pending = ["уточняется", "тактала элек", "to be confirmed", "netleştiriliyor", "aniqlanmoqda"];
        SupportInfo.Hours().Should().BeOneOf(pending);
        SupportInfo.ResponseTime().Should().BeOneOf(pending);
    }
}
