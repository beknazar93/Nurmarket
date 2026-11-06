using System.Collections.ObjectModel;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-06 (жалоба владельца «при приёмке очередь и порядок сбиваются», Закупки → Приём товара): порядок строк списка приёма и очередь фоновых приходов.
/// Порядок на экране = порядок строк в приходе; повторный скан не перескакивает наверх; правки на месте не пересортировывают; очередь — строго по порядку создания (FIFO).</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class ReceivingOrderTests
{
    private static ReceivingLineVm Line(string id, double qty = 1, double purchase = 10, double sale = 15)
    {
        var l = new ReceivingLineVm { OriginalSalePrice = sale, OriginalPurchasePrice = purchase, ProductName = "Товар " + id };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = qty;
        l.PurchasePrice = purchase;
        l.SalePrice = sale;
        return l;
    }

    private static string[] Ids(IEnumerable<ReceivingLineVm> lines) => lines.Select(l => l.ProductId!).ToArray();

    /// <summary>Как «скан/выбор товара»: есть в списке — прибавляем количество в той же строке, нет — новая строка наверх.</summary>
    private static ReceivingLineVm Scan(ObservableCollection<ReceivingLineVm> lines, string id)
    {
        var line = lines.FirstOrDefault(x => x.ProductId == id);
        if (line is not null)
            line.Quantity += 1;
        else
            line = Line(id, 1);
        ReceivingListOrder.Place(lines, line);
        return line;
    }

    [Fact]
    public void New_scans_go_on_top_and_a_repeated_scan_stays_in_place()
    {
        var lines = new ObservableCollection<ReceivingLineVm>();
        Scan(lines, "A");
        Scan(lines, "B");
        Scan(lines, "C");
        Ids(lines).Should().Equal("C", "B", "A");

        var again = Scan(lines, "A"); // повторный скан самого нижнего
        Ids(lines).Should().Equal("C", "B", "A");
        again.Quantity.Should().Be(2, "количество прибавилось в той же строке");

        Scan(lines, "B");
        Ids(lines).Should().Equal(new[] { "C", "B", "A" }, "порядок не плывёт");
        lines.Should().HaveCount(3, "дубля строки нет");
    }

    [Fact]
    public void Place_reports_whether_the_line_is_new()
    {
        var lines = new ObservableCollection<ReceivingLineVm>();
        var a = Line("A");
        ReceivingListOrder.Place(lines, a).Should().BeTrue();
        ReceivingListOrder.Place(lines, Line("B")).Should().BeTrue();
        ReceivingListOrder.Place(lines, a).Should().BeFalse("уже в списке — остаётся на месте");
        Ids(lines).Should().Equal("B", "A");
    }

    [Fact]
    public void Supplier_change_price_pull_and_apply_to_all_change_lines_in_place_without_reordering()
    {
        var previous = ReceivingMarkupSettingsStore.Current.Clone();
        var persistDisabled = ReceivingMarkupSettingsStore.PersistenceDisabled;
        try
        {
            ReceivingMarkupSettingsStore.PersistenceDisabled = true;
            ReceivingMarkupSettingsStore.Override(new ReceivingMarkupSettings { AutoEnabled = true, DefaultPercent = 20, MarkupResetDone = true });
            var lines = new ObservableCollection<ReceivingLineVm>();
            foreach (var id in new[] { "A", "B", "C", "D" })
                Scan(lines, id);
            var before = Ids(lines);
            var changes = 0;
            lines.CollectionChanged += (_, _) => changes++;

            // смена поставщика: закупки подтягиваются из истории
            foreach (var l in lines)
                ReceivingDefaults.ApplyPurchase(l, new ReceivingDefaults.SupplierPrice(12, new DateTime(2026, 11, 1)));
            // «Применить ко всем» и пересчёт наценки
            ReceivingPricing.ApplyCommonMarkupToAll(lines, out _);
            ReceivingPricing.ApplyAutoToAll(lines);
            // «Обнулить кол-во»
            foreach (var l in lines)
                l.Quantity = 0;

            Ids(lines).Should().Equal(before, "порядок строк прежний");
            changes.Should().Be(0, "коллекция не пересобиралась: ни Add/Remove/Reset");
        }
        finally
        {
            ReceivingMarkupSettingsStore.Override(previous);
            ReceivingMarkupSettingsStore.PersistenceDisabled = persistDisabled;
        }
    }

    [Fact]
    public void Posted_lines_keep_the_screen_order()
    {
        var lines = new ObservableCollection<ReceivingLineVm>();
        foreach (var id in new[] { "A", "B", "C" })
            Scan(lines, id);
        lines[1].Quantity = 0; // строка без количества в приход не идёт
        // приход собирается так же, как в ReceivePostCoreAsync: Where по списку, порядок сохраняется
        Ids(lines.Where(l => l.Quantity > 0).ToList()).Should().Equal("C", "A");
    }

    // ------------------------------------------------------------------ очередь фоновых приходов

    private static ReceivingJob Job(string id, string status, DateTime createdAt) =>
        new() { Id = id, Status = status, CreatedAt = createdAt, CompanyId = "co", Lines = new List<JobLine>() };

    [Fact]
    public void Queue_picks_jobs_in_creation_order_even_if_the_pc_clock_went_back()
    {
        var t = new DateTime(2026, 11, 6, 12, 0, 0);
        var jobs = new List<ReceivingJob>
        {
            Job("first", ReceivingJobStatus.Queued, t),
            Job("second", ReceivingJobStatus.Queued, t.AddHours(-1)), // часы ПК перевели назад
            Job("third", ReceivingJobStatus.Queued, t),               // одна и та же секунда
        };
        BackgroundReceivingQueue.OldestActive(jobs, _ => true)!.Id.Should().Be("first");
        jobs[0].Status = ReceivingJobStatus.Done;
        BackgroundReceivingQueue.OldestActive(jobs, _ => true)!.Id.Should().Be("second");
        jobs[1].Status = ReceivingJobStatus.Done;
        BackgroundReceivingQueue.OldestActive(jobs, _ => true)!.Id.Should().Be("third");
    }

    [Fact]
    public void A_job_waiting_for_retry_stays_first_and_later_jobs_do_not_jump_ahead()
    {
        var t = DateTime.Now;
        var jobs = new List<ReceivingJob>
        {
            Job("first", ReceivingJobStatus.RetryWait, t),
            Job("second", ReceivingJobStatus.Queued, t.AddSeconds(1)),
        };
        BackgroundReceivingQueue.OldestActive(jobs, _ => true)!.Id.Should().Be("first", "повтор/ошибка не меняет порядок");
        BackgroundReceivingQueue.OldestActive(jobs, j => j.Id == "second")!.Id.Should().Be("second", "чужая компания пропускается, остальные идут по порядку");
    }
}
