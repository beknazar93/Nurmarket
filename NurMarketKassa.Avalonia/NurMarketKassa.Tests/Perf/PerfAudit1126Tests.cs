using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Lan;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Perf;

/// <summary>2026-11-26 (аудит быстродействия «до 10/10»): замеры, которых не было в SpeedBudgetTests, и проверки, что исправленное считает то же самое.
/// Цифры каждого прогона — в %TEMP%\nurmarket-tests\speed-budget.txt (тем же SpeedProbe). Запускать только tools/qa/test-safe.ps1.
/// База — во временной папке данных (TestDataRoot), настоящая pos_local.db не трогается.</summary>
[Collection("Wave5Database")]
public sealed class PerfAudit1126Tests
{
    private const int CatalogSize = 15_000;

    private static void PrepareDb()
    {
        DatabaseService.Instance.EnsureSchema();
        LocalProductRepository.Instance.EnsureSchema(); // включает WAL, как в настоящей кассе
        LanJournal.ResetSchemaFlag();
    }

    private static void SeedJournal(int events)
    {
        LanJournal.ReadMeta("perf-seed"); // создаёт таблицы журнала
        DatabaseService.Instance.WithConnection(c =>
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO LanJournal(id, kind, created_at, json) VALUES ($id, 'sale', $at, '{}')";
            var id = cmd.CreateParameter(); id.ParameterName = "$id"; cmd.Parameters.Add(id);
            var at = cmd.CreateParameter(); at.ParameterName = "$at"; cmd.Parameters.Add(at);
            for (var i = 0; i < events; i++)
            {
                id.Value = "perf-1126-" + i;
                at.Value = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        });
    }

    /// <summary>Журнал локальной сети читали под замком ЗАПИСИ: ответ соседу (раз в 4 с), остатки каталога и «Сводка» владельца стояли в очереди за каждой записью продажи
    /// (synchronous=FULL) — в журнале кассы «SQLITE SLOW LanJournal.With» до 1176 мс. Теперь чтение идёт без замка записи (WAL). «До» меряется тем же запросом через замок записи,
    /// как было; пока писатель держит замок 400 мс, чтение журнала должно уложиться в десятки мс, а не ждать писателя.</summary>
    [Fact]
    public void LanJournalReads_DoNotWaitForWriterLock()
    {
        PrepareDb();
        SeedJournal(200);
        // прогрев: схема готова, JIT
        LanJournal.ReadOwnAfter(0, 500);
        LanJournal.PeerLastSeq("perf-device");

        static double MeasureWithWriterHolding(Action read)
        {
            using var inside = new ManualResetEventSlim(false);
            var writer = Task.Run(() => DatabaseService.Instance.WithConnection(_ =>
            {
                inside.Set();
                Thread.Sleep(400);
            }));
            inside.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            var w = Stopwatch.StartNew();
            read();
            w.Stop();
            writer.Wait(TimeSpan.FromSeconds(10));
            return w.Elapsed.TotalMilliseconds;
        }

        // «до»: тот же SELECT через замок записи (как делал LanJournal.With)
        var before = MeasureWithWriterHolding(() => DatabaseService.Instance.WithConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT seq, id, kind, created_at, json FROM LanJournal WHERE seq > 0 ORDER BY seq LIMIT 500";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { }
        }));
        SpeedProbe.Check("ДО: чтение журнала LAN через замок записи, пока идёт запись (400 мс)", before, 100_000);

        var after = MeasureWithWriterHolding(() =>
        {
            var (events, last) = LanJournal.ReadOwnAfter(0, 500);
            events.Count.Should().BeGreaterThanOrEqualTo(200);
            last.Should().BeGreaterThanOrEqualTo(events.Count);
            LanJournal.PeerLastSeq("perf-device").Should().Be(0);
            LanJournal.ReadStockAdjust().Should().NotBeNull();
            LanJournal.ReadMeta("perf-no-such-key").Should().BeNull();
            LanJournal.ReadPeerSales(DateTime.UtcNow.AddHours(-1)).Should().NotBeNull();
        });
        SpeedProbe.Check("ПОСЛЕ: чтение журнала LAN (5 чтений), пока идёт запись (400 мс)", after, 150);
        before.Should().BeGreaterThan(300, "«до» упирается в замок записи");
    }

    /// <summary>Результат чтения тот же: через новый путь события читаются в порядке seq, курсор и счёт совпадают с прямым SELECT.</summary>
    [Fact]
    public void LanJournalReadOwnAfter_ReturnsSameRowsAsDirectSelect()
    {
        PrepareDb();
        SeedJournal(50);
        var (events, last) = LanJournal.ReadOwnAfter(0, 10_000);
        long count = 0, max = 0;
        DatabaseService.Instance.WithReadConnection(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*), IFNULL(MAX(seq),0) FROM LanJournal";
            using var r = cmd.ExecuteReader();
            r.Read();
            count = r.GetInt64(0);
            max = r.GetInt64(1);
        });
        events.Should().HaveCount((int)count);
        last.Should().Be(max);
        events.Select(e => e.Seq).Should().BeInAscendingOrder();
        LanJournal.ReadOwnAfter(max, 10).Events.Should().BeEmpty("после последнего номера событий нет");
    }

    /// <summary>Полная загрузка каталога 15 000: разбор JSON страниц + превращение в плитки (то, что делает фон при полной синхронизации) и запись/чтение базы.
    /// Базовые цифры — в speed-budget.txt; пределы — «храповик» ×3–5 к базе этого ПК.</summary>
    [Fact]
    public void CatalogFullLoad_On15kProducts_StaysWithinBudget()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < CatalogSize; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"id\":\"pf").Append(i).Append("\",\"name\":\"Макаронные изделия «Макфа» спагетти длинное название ").Append(i)
              .Append("\",\"price\":\"").Append(10 + i % 90).Append(".00\",\"purchase_price\":\"7.00\",\"barcode\":\"46").Append(i.ToString("D11"))
              .Append("\",\"quantity\":\"").Append(i % 50).Append("\",\"category\":{\"id\":\"c").Append(i % 40).Append("\",\"name\":\"Категория ").Append(i % 40)
              .Append("\"},\"unit\":\"шт\",\"is_active\":true,\"has_variants\":false,\"updated_at\":\"2026-11-26T10:00:00Z\"}");
        }
        sb.Append(']');
        var json = sb.ToString();

        List<CatalogProductTileVm> tiles = null!;
        var parse = Stopwatch.StartNew();
        using (var doc = JsonDocument.Parse(json))
        {
            tiles = new List<CatalogProductTileVm>(CatalogSize);
            foreach (var p in doc.RootElement.EnumerateArray())
                if (ProductCatalogMapper.TryTile(p, "https://app.nurcrm.kg/") is { } t)
                    tiles.Add(t);
        }
        parse.Stop();
        tiles.Should().HaveCount(CatalogSize);
        SpeedProbe.Check("Полная загрузка 15 тыс.: разбор JSON и плитки (без сети)", parse.Elapsed.TotalMilliseconds, 5000);

        PrepareDb();
        var repo = LocalProductRepository.Instance;
        try
        {
            var full = Stopwatch.StartNew();
            repo.SyncReplaceAllWithDiff(tiles);
            full.Stop();
            SpeedProbe.Check("Запись 15 тыс. товаров в базу (первая синхронизация)", full.Elapsed.TotalMilliseconds, 15_000);

            var same = Stopwatch.StartNew();
            var (added, changed, deleted) = repo.SyncReplaceAllWithDiff(tiles);
            same.Stop();
            (added + changed + deleted).Should().Be(0, "ничего не менялось — писать нечего");
            SpeedProbe.Check("Повторная сверка 15 тыс. без изменений (diff)", same.Elapsed.TotalMilliseconds, 5000);

            var load = SpeedProbe.MedianMs(3, () => repo.LoadAllTiles());
            SpeedProbe.Check("Чтение всех 15 тыс. плиток из памяти репозитория", load, 2000);
        }
        finally
        {
            repo.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());
        }
    }

    /// <summary>Справочник штрихкодов размеров («Одежда»): фоновый обход каталога после КАЖДОГО товара перезаписывал весь файл (отметки «проверено» на все товары каталога) под общим
    /// замком справочника — на 15 000 товаров 15 000 перезаписей за обход; сканер штрихкода (Find) стоит за тем же замком. Теперь обход пишет не чаще раза в SaveEvery и в конце прохода.
    /// «До» — тот же Update с немедленной записью (как раньше), «после» — отложенная запись; содержимое файла после Flush то же.</summary>
    [Fact]
    public void VariantIndexUpdate_On15kCheckedProducts_DefersFileWrites()
    {
        var pathProp = typeof(VariantBarcodeIndex).GetProperty("FilePath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var path = (string)pathProp.GetValue(null)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var oldEvery = VariantBarcodeIndex.SaveEvery;
        try
        {
            var sb = new StringBuilder("{\"Items\":{},\"Variants\":{},\"Version\":2,\"Checked\":{");
            for (var i = 0; i < CatalogSize; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("\"seed-").Append(i).Append("\":\"2026-11-26T10:00:00Z\"");
            }
            sb.Append("}}");
            File.WriteAllText(path, sb.ToString());
            VariantBarcodeIndex.Clear();

            var list = new List<ProductVariantDto> { new() { Id = "v-1126", Size = "M", Color = "синий", Barcode = "2900000011261", Quantity = 3, IsActive = true } };
            var n = 0;
            VariantBarcodeIndex.Update("bench-warm", list); // прогрев: чтение файла, JIT
            var before = SpeedProbe.MedianMs(15, () => VariantBarcodeIndex.Update("bench-now-" + n++, list));
            SpeedProbe.Check("ДО: справочник размеров, один проверенный товар (запись файла на 15 тыс. отметок каждый раз)", before, 100_000);

            VariantBarcodeIndex.SaveEvery = TimeSpan.FromHours(1);
            var after = SpeedProbe.MedianMs(15, () => VariantBarcodeIndex.Update("bench-def-" + n++, list, deferSave: true));
            SpeedProbe.Check("ПОСЛЕ: справочник размеров, один проверенный товар (запись отложена)", after, 20);

            // содержимое то же: сканер видит размер сразу из памяти, а после Flush в файле все отметки (и отложенные тоже)
            VariantBarcodeIndex.Find("2900000011261").Should().NotBeNull();
            VariantBarcodeIndex.Flush();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var checkedProps = doc.RootElement.GetProperty("Checked");
            checkedProps.EnumerateObject().Count().Should().Be(CatalogSize + 1 + 15 + 15);
            checkedProps.TryGetProperty("bench-def-" + (n - 1), out _).Should().BeTrue("отложенная запись дошла до файла после Flush");
            before.Should().BeGreaterThan(after, "отложенная запись не дороже немедленной");
        }
        finally
        {
            VariantBarcodeIndex.SaveEvery = oldEvery;
            VariantBarcodeIndex.Clear();
            try { File.Delete(path); } catch { /* временная папка */ }
        }
    }
}
