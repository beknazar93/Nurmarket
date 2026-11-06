using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;
using SkiaSharp;

namespace NurMarketKassa.Tests.Hardware;

/// <summary>2026-11-06 (проверка Умара на настоящих весах): на главном экране РС-200 видны только товары с картинкой. Касса переводит фото в BMP 288×288 (24 бита, белый фон,
/// пропорции сохраняются), кладёт в Pictures\&lt;код&gt;.bmp, пишет строку ресурса «&lt;R» и код ресурса в поле 31 строки товара; неизменившиеся картинки не пересоздаёт.</summary>
public sealed class ShtrikhPcPicturesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nurmarket-pcpic-" + Guid.NewGuid().ToString("N")[..8]);

    public ShtrikhPcPicturesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ShtrikhPcSettings Settings(int offset = 100_000)
    {
        var folder = Path.Combine(_root, "Shared");
        Directory.CreateDirectory(folder);
        return new ShtrikhPcSettings { Folder = folder, PictureOffset = offset }.Normalize();
    }

    private ShtrikhPcPictureState State() => new(Path.Combine(_root, "state.json"));

    private static ShtrikhPcItem Item(long code, string? source = "https://nurcrm.kg/media/p.jpg") =>
        new(code, "Яблоки", 100m, false, PictureSource: source);

    private static Task<byte[]?> Bmp(ShtrikhPcItem _, CancellationToken __) => Task.FromResult<byte[]?>(new byte[ShtrikhPcBmp.HeaderSize + 4]);

    // ---------------------------------------------------------------- BMP

    private static byte[] SolidBgra(int w, int h, byte b, byte g, byte r, byte a = 255)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            px[i] = b;
            px[i + 1] = g;
            px[i + 2] = r;
            px[i + 3] = a;
        }
        return px;
    }

    private static int Int(byte[] data, int offset) => BitConverter.ToInt32(data, offset);

    [Fact]
    public void Encode24_288_HasBmpHeader_AndExactSize()
    {
        var bmp = ShtrikhPcBmp.Encode24(SolidBgra(288, 288, 10, 20, 30), 288, 288);

        bmp[0].Should().Be((byte)'B');
        bmp[1].Should().Be((byte)'M');
        bmp.Length.Should().Be(54 + 288 * 3 * 288, "288 × 3 = 864 байт на строку — уже кратно 4, поэтому без добавок");
        Int(bmp, 2).Should().Be(bmp.Length);   // размер файла
        Int(bmp, 10).Should().Be(54);          // смещение пикселей
        Int(bmp, 14).Should().Be(40);          // BITMAPINFOHEADER
        Int(bmp, 18).Should().Be(288);
        Int(bmp, 22).Should().Be(288);         // положительная высота — строки снизу вверх
        BitConverter.ToInt16(bmp, 26).Should().Be(1);
        BitConverter.ToInt16(bmp, 28).Should().Be(24);
        Int(bmp, 30).Should().Be(0);           // без сжатия
        Int(bmp, 34).Should().Be(bmp.Length - 54);
    }

    [Fact]
    public void Encode24_RowsAreStoredBottomUp_InBgrOrder()
    {
        var px = SolidBgra(4, 2, 255, 255, 255);
        // верхний левый — красный (B=0,G=0,R=255), нижний левый — синий (B=255,G=0,R=0)
        px[0] = 0; px[1] = 0; px[2] = 255;
        var bottomLeft = 1 * 4 * 4;
        px[bottomLeft] = 255; px[bottomLeft + 1] = 0; px[bottomLeft + 2] = 0;

        var bmp = ShtrikhPcBmp.Encode24(px, 4, 2);

        // строка 4 × 3 = 12 байт (кратно 4); первой в файле лежит НИЖНЯЯ строка
        bmp.Length.Should().Be(54 + 12 * 2);
        bmp.Skip(54).Take(3).Should().Equal(255, 0, 0);          // нижний левый: синий (B, G, R)
        bmp.Skip(54 + 12).Take(3).Should().Equal(0, 0, 255);     // верхний левый: красный
    }

    [Fact]
    public void Encode24_RowsArePaddedToFourBytes()
    {
        var bmp = ShtrikhPcBmp.Encode24(SolidBgra(3, 2, 1, 2, 3), 3, 2);

        bmp.Length.Should().Be(54 + 12 * 2, "3 × 3 = 9 байт → строка 12 байт");
        bmp.Skip(54 + 9).Take(3).Should().OnlyContain(b => b == 0, "добивка нулями");
    }

    [Fact]
    public void Encode24_TransparentPixelBecomesWhite()
    {
        var bmp = ShtrikhPcBmp.Encode24(SolidBgra(1, 1, 0, 0, 0, a: 0), 1, 1);

        bmp.Skip(54).Take(3).Should().Equal(255, 255, 255);
    }

    [Theory]
    [InlineData(600, 300, 0, 72, 288, 144)]
    [InlineData(100, 400, 108, 0, 72, 288)]
    [InlineData(500, 500, 0, 0, 288, 288)]
    [InlineData(50, 50, 0, 0, 288, 288)]   // маленькая увеличивается
    public void FitRect_KeepsProportions_AndCenters(int sw, int sh, int x, int y, int w, int h) =>
        ShtrikhPcBmp.FitRect(sw, sh).Should().Be((x, y, w, h));

    // ---------------------------------------------------------------- Skia: фото -> BMP 288x288

    private static byte[] Png(int w, int h, SKColor color)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (byte B, byte G, byte R) PixelAt(byte[] bmp, int x, int y, int size = 288)
    {
        var stride = (size * 3 + 3) & ~3;
        var o = 54 + (size - 1 - y) * stride + x * 3;
        return (bmp[o], bmp[o + 1], bmp[o + 2]);
    }

    [Fact]
    public void ToBmp_WidePhoto_Is288x288_WithWhiteBars_AndRedCenter()
    {
        var bmp = ShtrikhPcPictureConverter.ToBmp(Png(600, 300, SKColors.Red));

        bmp.Should().NotBeNull();
        bmp!.Length.Should().Be(54 + 288 * 3 * 288);
        Int(bmp, 18).Should().Be(288);
        Int(bmp, 22).Should().Be(288);
        BitConverter.ToInt16(bmp, 28).Should().Be(24);
        PixelAt(bmp, 0, 0).Should().Be(((byte)255, (byte)255, (byte)255), "угол — белое поле вокруг вписанной картинки");
        PixelAt(bmp, 287, 287).Should().Be(((byte)255, (byte)255, (byte)255));
        PixelAt(bmp, 144, 144).Should().Be(((byte)0, (byte)0, (byte)255), "центр — красная картинка (B=0, G=0, R=255)");
        PixelAt(bmp, 144, 80).Should().Be(((byte)0, (byte)0, (byte)255), "картинка занимает полосу высотой 144 по центру (с 72 по 216)");
        PixelAt(bmp, 144, 60).Should().Be(((byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void ToBmp_TallPhoto_IsFittedByHeight_NotStretched()
    {
        var bmp = ShtrikhPcPictureConverter.ToBmp(Png(100, 400, SKColors.Blue))!;

        PixelAt(bmp, 144, 144).Should().Be(((byte)255, (byte)0, (byte)0));
        PixelAt(bmp, 50, 144).Should().Be(((byte)255, (byte)255, (byte)255), "слева от узкой картинки — белое поле (она шириной 72 с отступом 108)");
        PixelAt(bmp, 120, 10).Should().Be(((byte)255, (byte)0, (byte)0), "по высоте картинка во всю сторону");
    }

    [Fact]
    public void ToBmp_TransparentPng_ComesOutOnWhite()
    {
        var bmp = ShtrikhPcPictureConverter.ToBmp(Png(200, 200, SKColors.Transparent))!;

        PixelAt(bmp, 144, 144).Should().Be(((byte)255, (byte)255, (byte)255));
    }

    [Fact]
    public void ToBmp_NotAnImage_IsNull()
    {
        ShtrikhPcPictureConverter.ToBmp(new byte[] { 1, 2, 3 }).Should().BeNull();
        ShtrikhPcPictureConverter.ToBmp(System.Text.Encoding.UTF8.GetBytes("<html>это не картинка, а страница сайта</html>")).Should().BeNull();
    }

    // ---------------------------------------------------------------- строки «<R» и поле 31

    [Fact]
    public void ResourceLine_MatchesTheScaleSample()
    {
        // образец весов: «<R  54002;3;;;;;;;;;;;;;;;;;;;;Pictures/48002.bmp» — тип 3, 19 пустых полей, путь (у нас один пробел после «<R», как у «<D»)
        var line = ShtrikhPcImportWriter.BuildResourceLine(100123, "Pictures/123.bmp", new ShtrikhPcSettings().Normalize());

        line.Should().Be("<R 100123;3" + new string(';', 20) + "Pictures/123.bmp");
        line["<R ".Length..].Split(';').Should().HaveCount(22);
    }

    [Fact]
    public void Build_ProductWithPicture_HasResourceLineBeforeIt_AndResourceCodeInField31()
    {
        var withPicture = Item(123) with { PictureResource = 100123, PictureFile = "Pictures/123.bmp" };
        var without = Item(124, source: null);

        var text = ShtrikhPcImportWriter.Build(new[] { withPicture, without }, new ShtrikhPcSettings().Normalize()).Text;
        var lines = text.Split("\r\n").Where(l => l.Length > 0).ToArray();

        lines[2].Should().StartWith("<R 100123;3;");
        lines[3].Should().StartWith("<D 123;");
        lines[4].Should().StartWith("<D 124;");
        lines.Count(l => l.StartsWith("<R", StringComparison.Ordinal)).Should().Be(1);
        lines[3]["<D ".Length..].Split(';')[30].Should().Be("100123", "поле 31 — код ресурса картинки");
        lines[4]["<D ".Length..].Split(';')[30].Should().Be("0", "без картинки — поле 31 равно 0, как раньше");
    }

    // ---------------------------------------------------------------- шаг «картинки»

    [Fact]
    public async Task Step_WritesBmpToPicturesFolder_AndGivesResourceCode()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep(Bmp, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var outcome = await step(new[] { Item(5) }, settings, CancellationToken.None);

        outcome.Stats.Written.Should().Be(1);
        outcome.Stats.WithPicture.Should().Be(1);
        File.Exists(Path.Combine(settings.Folder, "Pictures", "5.bmp")).Should().BeTrue();
        outcome.Items.Single().PictureResource.Should().Be(100_005);
        outcome.Items.Single().PictureFile.Should().Be("Pictures/5.bmp");
    }

    [Fact]
    public async Task Step_UnchangedPicture_IsNotReloadedOrRewritten()
    {
        var settings = Settings();
        var state = State();
        var calls = 0;
        Task<byte[]?> Counting(ShtrikhPcItem i, CancellationToken c)
        {
            Interlocked.Increment(ref calls);
            return Bmp(i, c);
        }
        var first = await ShtrikhPcPictures.CreateStep(Counting, state, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10))(new[] { Item(5) }, settings, CancellationToken.None);
        var file = Path.Combine(settings.Folder, "Pictures", "5.bmp");
        File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // тот же источник, состояние перечитано с диска (как после перезапуска кассы)
        var second = await ShtrikhPcPictures.CreateStep(Counting, ShtrikhPcPictureState.Load(Path.Combine(_root, "state.json")), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10))
            (new[] { Item(5) }, settings, CancellationToken.None);

        calls.Should().Be(1, "вторая выгрузка картинку не скачивает");
        second.Stats.Unchanged.Should().Be(1);
        second.Stats.Written.Should().Be(0);
        File.GetLastWriteTimeUtc(file).Should().Be(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), "файл не пересоздан");
        second.Items.Single().PictureResource.Should().Be(first.Items.Single().PictureResource, "ресурс всё равно попадает в файл импорта (особенно после $$$CLR)");
    }

    [Fact]
    public async Task Step_ChangedSource_OrMissingFile_IsRewritten()
    {
        var settings = Settings();
        var state = State();
        var calls = 0;
        Task<byte[]?> Counting(ShtrikhPcItem i, CancellationToken c)
        {
            Interlocked.Increment(ref calls);
            return Bmp(i, c);
        }
        ShtrikhPcPictureStep Step() => ShtrikhPcPictures.CreateStep(Counting, state, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));
        await Step()(new[] { Item(5, "https://x/a.jpg") }, settings, CancellationToken.None);

        var changed = await Step()(new[] { Item(5, "https://x/b.jpg") }, settings, CancellationToken.None);
        changed.Stats.Written.Should().Be(1);

        File.Delete(Path.Combine(settings.Folder, "Pictures", "5.bmp"));
        var restored = await Step()(new[] { Item(5, "https://x/b.jpg") }, settings, CancellationToken.None);
        restored.Stats.Written.Should().Be(1, "файл на весах пропал — картинку кладём заново");
        calls.Should().Be(3);
    }

    [Fact]
    public async Task Step_ProductWithoutPhoto_HasNoResource_AndCountsAsNoPhoto()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep(Bmp, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var outcome = await step(new[] { Item(7, source: null), Item(8, source: "  ") }, settings, CancellationToken.None);

        outcome.Stats.NoPhoto.Should().Be(2);
        outcome.Stats.WithPicture.Should().Be(0);
        outcome.Items.Should().OnlyContain(i => i.PictureResource == 0 && i.PictureFile == null);
        Directory.Exists(Path.Combine(settings.Folder, "Pictures")).Should().BeFalse("папку без картинок не создаём");
    }

    [Fact]
    public async Task Step_PhotoThatFailsToLoad_IsCountedAsFailed_AndProductGoesWithoutResource()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep((_, _) => Task.FromResult<byte[]?>(null), State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var outcome = await step(new[] { Item(9) }, settings, CancellationToken.None);

        outcome.Stats.Failed.Should().Be(1);
        outcome.Items.Single().PictureResource.Should().Be(0);
    }

    [Fact]
    public async Task Step_TimeBudget_StopsTheRest_AsTimedOut()
    {
        var settings = Settings();
        var step = ShtrikhPcPictures.CreateStep(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return null;
        }, State(), TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10), parallel: 1);

        var started = DateTime.UtcNow;
        var outcome = await step(new[] { Item(1), Item(2), Item(3) }, settings, CancellationToken.None);

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
        outcome.Stats.TimedOut.Should().Be(3);
        outcome.Stats.Written.Should().Be(0);
        outcome.Items.Should().OnlyContain(i => i.PictureResource == 0);
    }

    [Fact]
    public async Task Step_ResourceCodeAboveSixDigits_IsRejected()
    {
        var settings = Settings(offset: 900_000);
        var step = ShtrikhPcPictures.CreateStep(Bmp, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var outcome = await step(new[] { Item(99_999 + 1) }, settings, CancellationToken.None);

        outcome.Stats.Failed.Should().Be(1);
    }

    [Fact]
    public async Task Step_Progress_ReportsEveryProduct()
    {
        var settings = Settings();
        var reports = new List<(int, int)>();
        var step = ShtrikhPcPictures.CreateStep(Bmp, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), new SyncProgress(reports));

        await step(new[] { Item(1), Item(2), Item(3, source: null) }, settings, CancellationToken.None);

        reports.Should().HaveCount(3);
        reports.Max(r => r.Item1).Should().Be(3);
        reports.Should().OnlyContain(r => r.Item2 == 3);
    }

    private sealed class SyncProgress : IProgress<(int Done, int Total)>
    {
        private readonly List<(int, int)> _list;

        public SyncProgress(List<(int, int)> list) => _list = list;

        public void Report((int Done, int Total) value)
        {
            lock (_list)
                _list.Add(value);
        }
    }

    [Fact]
    public async Task WriteAsync_WithPictureStep_WritesResourcesAndField31_AndReportsStats()
    {
        var settings = Settings();
        var items = new[] { Item(123, "https://x/123.jpg"), Item(124, source: null) };
        var step = ShtrikhPcPictures.CreateStep(Bmp, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        var result = await ShtrikhPcImportWriter.WriteAsync(items, settings, pictures: step);

        result.Status.Should().Be(ShtrikhPcWriteStatus.Ok);
        result.Pictures.Should().NotBeNull();
        result.Pictures!.Written.Should().Be(1);
        result.Pictures.NoPhoto.Should().Be(1);
        var text = ShtrikhPcImportWriter.Decode(File.ReadAllBytes(ShtrikhPcImportWriter.ImportPath(settings)));
        text.Should().Contain("<R 100123;3;").And.Contain("Pictures/123.bmp");
        text.Split("\r\n").Single(l => l.StartsWith("<D 123;", StringComparison.Ordinal))["<D ".Length..].Split(';')[30].Should().Be("100123");
        File.Exists(Path.Combine(settings.Folder, "Pictures", "123.bmp")).Should().BeTrue();
    }

    [Fact]
    public async Task WriteAsync_FullUploadWithClear_KeepsResourcesAndPutsClearLast()
    {
        var settings = Settings();
        settings.ClearBase = true;
        var step = ShtrikhPcPictures.CreateStep(Bmp, State(), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10));

        await ShtrikhPcImportWriter.WriteAsync(new[] { Item(123) }, settings, pictures: step);

        var lines = ShtrikhPcImportWriter.Decode(File.ReadAllBytes(ShtrikhPcImportWriter.ImportPath(settings))).Split("\r\n").Where(l => l.Length > 0).ToArray();
        lines.Last().Should().Be("$$$CLR");
        lines.Should().Contain(l => l.StartsWith("<R 100123;3;", StringComparison.Ordinal));
    }

    [Fact]
    public void Defaults_PictureSettings_AndExampleLogin()
    {
        var s = new ShtrikhPcSettings().Normalize();

        s.SendPictures.Should().BeTrue();
        s.PictureOffset.Should().Be(100_000);
        s.DecimalSeparator.Should().Be(".");
        ShtrikhPcSettings.ExampleLogin.Should().Be(@"web-scale1\kassa");
        ShtrikhPcSettings.ExampleFolder.Should().Be(@"\\192.168.0.200\Shared");
        new ShtrikhPcSettings { PictureOffset = 5_000_000 }.Normalize().PictureOffset.Should().Be(900_000);
    }
}
