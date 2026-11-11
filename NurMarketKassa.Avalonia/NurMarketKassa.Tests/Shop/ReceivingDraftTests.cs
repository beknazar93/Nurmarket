using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-11 (О-5 «Неоконченная приёмка пропадает», «Маркет ОРУК Ош»): черновик приёмки. Сохранение и восстановление всех значений строки (упаковки, источник «Из интернета»,
/// переименованное название, режимы цены), дебаунс записи, «Продолжить» (перечитывание остатка), «Начать заново»/проведение (удаление и запоздавшая запись), битый/пустой/чужой по версии файл,
/// устаревший черновик, предел размера, записи из нескольких потоков (межпроцессный замок — AtomicFile.Update, он проверен отдельно), пустое состояние не пишется.
/// Папка — временная (DataDirOverride); боевые данные не трогаются.</summary>
[Collection("ReceivingDraft")]
public sealed class ReceivingDraftTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nm-draft-" + Guid.NewGuid().ToString("N"));

    public ReceivingDraftTests()
    {
        Directory.CreateDirectory(_dir);
        ReceivingDraftStore.DataDirOverride = _dir;
        ReceivingDraftStore.Disabled = false;
        ReceivingDraftStore.ResetForTests();
    }

    public void Dispose()
    {
        ReceivingDraftStore.DataDirOverride = null;
        ReceivingDraftStore.ResetForTests();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* антивирус */ }
    }

    // ------------------------------------------------------------------ обвязка

    private static ReceivingLineVm Existing(string id = "p1", double stock = 10, double card = 25, double sale = 30)
    {
        var l = new ReceivingLineVm
        {
            ProductName = "Вода " + id, Barcode = "4600" + id, OriginalName = "Вода " + id, OriginalUnit = "шт", StockBefore = stock,
            OriginalPurchasePrice = card, OriginalSalePrice = sale, OriginalMarkupPercent = 20,
        };
        l.ProductId = id;
        l.Source = ReceivingSource.Warehouse;
        l.Quantity = 3;
        l.PurchasePrice = card;
        l.SalePrice = sale;
        return l;
    }

    private static ReceivingDraftFile FileOf(params ReceivingLineVm[] lines) => new() { Lines = lines.Select(l => l.ToDraft()).ToList(), SupplierId = "s1", SupplierName = "Поставщик" };

    private static ReceivingDraftFile RoundTrip(ReceivingDraftFile file)
    {
        var parsed = ReceivingDraftStore.TryParse(ReceivingDraftStore.Serialize(file), out var problem);
        parsed.Should().NotBeNull(problem);
        return parsed!;
    }

    private static bool WaitFor(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition() && DateTime.UtcNow < until)
            Thread.Sleep(10);
        return condition();
    }

    private static ReceivingDraftFile OneMeaningful() => new() { Lines = { new ReceivingDraftLine { ProductName = "Хлеб", Quantity = 2, PurchasePrice = 10, SalePrice = 12 } } };

    private string PathOf(string slot = ReceivingDraftSlots.Warehouse) => ReceivingDraftStore.PathFor(slot);

    // ------------------------------------------------------------------ все поля строки

    [Fact]
    public void Line_RoundTrip_KeepsEveryValue_IncludingPacksAndRenamedName()
    {
        var packs = Existing("pk", stock: 7);
        packs.SetPackMode(true);
        packs.SetPackCount(5);
        packs.SetPiecesPerPack(24);
        packs.SetPackPriceText("600");
        packs.SalePrice = 30;
        packs.LineNo = 3;
        packs.CategoryName = "Напитки";
        packs.LandedExtraPerUnit = 0.5;
        packs.ProductName = "Вода «Новая» 0,5 л"; // переименование товара склада: OriginalName остаётся прежним
        packs.EnableReceivingPricing();

        var restored = ReceivingLineVm.FromDraft(RoundTrip(FileOf(packs)).Lines[0]);

        restored.PackMode.Should().BeTrue();
        restored.PackCount.Should().Be(5);
        restored.PiecesPerPack.Should().Be(24);
        restored.PackPrice.Should().Be(600);
        restored.Quantity.Should().Be(packs.Quantity).And.Be(120);
        restored.PurchasePrice.Should().Be(packs.PurchasePrice).And.Be(25);
        restored.SalePrice.Should().Be(30);
        restored.LandedExtraPerUnit.Should().Be(0.5);
        restored.LineNo.Should().Be(3);
        restored.CategoryName.Should().Be("Напитки");
        restored.StockBefore.Should().Be(7);
        restored.ProductName.Should().Be("Вода «Новая» 0,5 л");
        restored.OriginalName.Should().Be("Вода pk", "прежнее название нужно, чтобы показать «Было…» и переименовать карточку при проведении");
        restored.NameRenamed.Should().BeTrue();
        restored.SaleMode.Should().Be(packs.SaleMode);
        restored.PurchaseIsManual.Should().Be(packs.PurchaseIsManual).And.BeTrue();
        ReceivingDraftStore.Serialize(new ReceivingDraftFile { Lines = { restored.ToDraft() } })
            .Should().Be(ReceivingDraftStore.Serialize(new ReceivingDraftFile { Lines = { packs.ToDraft() } }), "строка после восстановления даёт тот же снимок, что и до записи");
    }

    [Fact]
    public void Line_RoundTrip_NewProductsFromInternet_PackSale_Variants_AndSource()
    {
        var internet = new ReceivingLineVm { Barcode = "4601234567890", ProductName = "Сок яблочный", Source = ReceivingSource.Internet, Quantity = 12, PurchasePrice = 55.5, SalePrice = 70, CategoryName = "Соки", Unit = "л" };
        internet.EnableAutoSale();
        var packSale = new ReceivingLineVm { Barcode = "4609999999991", ProductName = "Конфеты", Source = ReceivingSource.Unknown, Quantity = 2, PurchasePrice = 480, SalePrice = 600 };
        packSale.SetPackSale(true, 24).Should().BeTrue();
        packSale.PackName = "Пачка";
        var global = new ReceivingLineVm { Barcode = "4608888888880", ProductName = "Чай", Source = ReceivingSource.GlobalBase, Quantity = 4, PurchasePrice = 90, SalePrice = 120 };
        var clothes = new ReceivingLineVm { ProductName = "Футболка", Source = ReceivingSource.Unknown, PurchasePrice = 300, SalePrice = 500 };
        clothes.SetVariants(new Dictionary<string, double> { ["v1"] = 2, ["v2"] = 3 }, new Dictionary<string, string> { ["v1"] = "44, Чёрный", ["v2"] = "46, Красный" });

        var back = RoundTrip(FileOf(internet, packSale, global, clothes)).Lines.Select(d => ReceivingLineVm.FromDraft(d)).ToList();

        back[0].Source.Should().Be(ReceivingSource.Internet);
        back[0].IsNew.Should().BeTrue();
        back[0].Unit.Should().Be("л");
        back[0].PurchasePrice.Should().Be(55.5);
        back[0].SaleMode.Should().Be(internet.SaleMode);
        back[1].PackSale.Should().BeTrue();
        back[1].PackSaleSize.Should().Be(24);
        back[1].PackName.Should().Be("Пачка");
        back[1].Quantity.Should().Be(2);
        back[1].PurchasePrice.Should().Be(480);
        back[2].Source.Should().Be(ReceivingSource.GlobalBase);
        back[3].VariantQuantities.Should().BeEquivalentTo(new Dictionary<string, double> { ["v1"] = 2, ["v2"] = 3 });
        back[3].VariantLabels!["v1"].Should().Be("44, Чёрный");
        back[3].Quantity.Should().Be(5);
    }

    [Fact]
    public void Line_FromDraft_SanitizesGarbage_NoNaN_NoNegativeNumbers()
    {
        var parsed = ReceivingDraftStore.TryParse("{\"Version\":1,\"Lines\":[{\"ProductName\":\"X\",\"Quantity\":1e30,\"PurchasePrice\":-5,\"Source\":\"Nonsense\",\"PiecesPerPack\":-3,\"LineNo\":-9}]}", out _);
        // «Nonsense» не enum — разбор отказывает целиком, но не падает
        parsed.Should().BeNull();

        var d = new ReceivingDraftLine { ProductName = "X", Quantity = double.PositiveInfinity, PurchasePrice = double.NaN, PiecesPerPack = -3, LineNo = -9, Source = (ReceivingSource)77 };
        var vm = ReceivingLineVm.FromDraft(d);
        vm.Quantity.Should().Be(0);
        vm.PurchasePrice.Should().Be(0);
        vm.PiecesPerPack.Should().Be(0);
        vm.LineNo.Should().Be(0);
        vm.Source.Should().Be(ReceivingSource.Unknown);
    }

    [Fact]
    public void Header_RoundTrip_KeepsSupplierPaymentDocAndLanded()
    {
        var f = FileOf(Existing());
        f.PaidNow = true;
        f.DebtDays = 45;
        f.DocNumber = "НКЛ-77";
        f.DocDate = new DateTime(2026, 11, 10);
        f.OrderId = "o1";
        f.OrderNumber = "ЗК-5";
        f.LandedTotal = 300;
        f.LandedMode = LandedMethod.ByQuantity.ToString();
        f.SupplierInn = "123";
        f.SupplierPhone = "+996";
        var back = RoundTrip(f);
        back.SupplierId.Should().Be("s1");
        back.SupplierName.Should().Be("Поставщик");
        back.SupplierInn.Should().Be("123");
        back.SupplierPhone.Should().Be("+996");
        back.PaidNow.Should().BeTrue();
        back.DebtDays.Should().Be(45);
        back.DocNumber.Should().Be("НКЛ-77");
        back.DocDate.Should().Be(new DateTime(2026, 11, 10));
        back.OrderId.Should().Be("o1");
        back.OrderNumber.Should().Be("ЗК-5");
        back.LandedTotal.Should().Be(300);
        back.LandedMode.Should().Be("ByQuantity");
        back.Version.Should().Be(ReceivingDraftFile.CurrentVersion);
    }

    // ------------------------------------------------------------------ восстановление: остаток перечитывается

    [Fact]
    public void Restore_RereadsStockAndCardPrices_ForWarehouseProducts_KeepsDraftValuesForMissing()
    {
        var a = Existing("a", stock: 10, card: 25, sale: 30);
        a.ProductName = "Вода А (моё название)";
        a.SalePrice = 33; // кассир поставил свою цену
        var gone = Existing("gone", stock: 4);
        var newOne = new ReceivingLineVm { Barcode = "4601111111111", ProductName = "Новинка", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 5, SalePrice = 8 };
        var file = FileOf(a, gone, newOne);

        // за время «простоя» остаток «а» вырос до 15, карточка стала 26/31 и название поменяли на сайте
        var tile = new CatalogProductTileVm("a", "Вода А (с сайта)", "31 сом", false) { Barcode = "4600a", Unit = "шт", Quantity = 15, PurchasePrice = 26 };
        var restored = ReceivingDraftRestore.Build(file, id => id == "a" ? tile : null);

        restored.StockChanged.Should().Be(1, "остаток изменился у одного товара");
        restored.Lines[0].StockBefore.Should().Be(15, "остаток — из каталога, не из черновика");
        restored.Lines[0].OriginalPurchasePrice.Should().Be(26);
        restored.Lines[0].OriginalSalePrice.Should().Be(31);
        restored.Lines[0].OriginalName.Should().Be("Вода А (с сайта)");
        restored.Lines[0].ProductName.Should().Be("Вода А (моё название)", "введённое кассиром остаётся");
        restored.Lines[0].NameRenamed.Should().BeTrue();
        restored.Lines[0].SalePrice.Should().Be(33, "цена продажи, которую поставил кассир, не затирается");
        restored.Lines[0].Quantity.Should().Be(3);
        restored.Lines[1].StockBefore.Should().Be(4, "товара нет в каталоге — значения из черновика");
        restored.Lines[2].Source.Should().Be(ReceivingSource.Unknown);
        restored.Lines[2].Barcode.Should().Be("4601111111111");
    }

    [Fact]
    public void Restore_WithoutChange_ReportsZeroStockChanged()
    {
        var a = Existing("a", stock: 10);
        var tile = new CatalogProductTileVm("a", "Вода a", "30 сом", false) { Barcode = "4600a", Unit = "шт", Quantity = 10, PurchasePrice = 25 };
        ReceivingDraftRestore.Build(FileOf(a), _ => tile).StockChanged.Should().Be(0);
    }

    // ------------------------------------------------------------------ файл: запись, чтение, повреждения

    [Fact]
    public void Store_SaveAndLoad_RoundTrips_AndAtomicWriteLeavesNoTempFiles()
    {
        var f = OneMeaningful();
        f.Slot = ReceivingDraftSlots.Warehouse;
        f.StartedAt = new DateTime(2026, 11, 11, 9, 0, 0);
        f.SavedAt = new DateTime(2026, 11, 11, 9, 5, 0);
        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, ReceivingDraftStore.Serialize(f), ReceivingDraftStore.NextSeq()).Should().BeTrue();

        var loaded = ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse);
        loaded.Should().NotBeNull();
        loaded!.Lines.Should().HaveCount(1);
        loaded.Lines[0].ProductName.Should().Be("Хлеб");
        ReceivingDraftStore.Peek(ReceivingDraftSlots.Warehouse)!.Positions.Should().Be(1);
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ это не json")]
    [InlineData("{\"Version\":1,\"Lines\":[{\"ProductName\":\"Хле")] // оборвано посреди записи
    [InlineData("null")]
    [InlineData("{\"Version\":99,\"Lines\":[{\"ProductName\":\"X\",\"Quantity\":1}]}")] // из будущего
    [InlineData("{\"Version\":1,\"Lines\":[]}")] // пустой черновик
    [InlineData("{\"Version\":1,\"Lines\":[{\"ProductName\":\"X\",\"Quantity\":0}]}")] // только нулевые строки
    public void Load_BrokenEmptyOrNewerFile_IsIgnored_WithoutThrowing(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf(), content);
        var act = () => ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse);
        act.Should().NotThrow();
        act().Should().BeNull();
        ReceivingDraftStore.Peek(ReceivingDraftSlots.Warehouse).Should().BeNull();
    }

    [Fact]
    public void Load_BrokenMain_FallsBackToBackupCopy()
    {
        var good = OneMeaningful();
        good.Slot = ReceivingDraftSlots.Purchases;
        // две записи подряд: вторая оставляет первую в .bak
        ReceivingDraftStore.Save(ReceivingDraftSlots.Purchases, ReceivingDraftStore.Serialize(good), ReceivingDraftStore.NextSeq());
        ReceivingDraftStore.Save(ReceivingDraftSlots.Purchases, ReceivingDraftStore.Serialize(good), ReceivingDraftStore.NextSeq());
        File.WriteAllText(PathOf(ReceivingDraftSlots.Purchases), "{оборвано");

        var loaded = ReceivingDraftStore.Load(ReceivingDraftSlots.Purchases);
        loaded.Should().NotBeNull("рабочий файл битый — берём копию прошлой записи");
        loaded!.Lines[0].ProductName.Should().Be("Хлеб");
    }

    [Fact]
    public void Stale_DraftOlderThan30Days_IsMarked()
    {
        var f = OneMeaningful();
        f.SavedAt = DateTime.Now.AddDays(-31);
        ReceivingDraftStore.IsStale(f).Should().BeTrue();
        f.SavedAt = DateTime.Now.AddDays(-29);
        ReceivingDraftStore.IsStale(f).Should().BeFalse();

        f.SavedAt = DateTime.Now.AddDays(-40);
        f.Slot = ReceivingDraftSlots.Warehouse;
        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, ReceivingDraftStore.Serialize(f), ReceivingDraftStore.NextSeq());
        ReceivingDraftStore.Peek(ReceivingDraftSlots.Warehouse)!.Stale.Should().BeTrue();
    }

    [Fact]
    public void Delete_RemovesFileAndBackup_AndLateOlderWriteDoesNotResurrectIt()
    {
        var text = ReceivingDraftStore.Serialize(OneMeaningful());
        var s1 = ReceivingDraftStore.NextSeq();
        var late = ReceivingDraftStore.NextSeq(); // запись, начатая ДО удаления, но дошедшая позже
        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, text, s1);
        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, text, s1 + 100); // второй раз — появится .bak
        File.Exists(PathOf()).Should().BeTrue();

        ReceivingDraftStore.Delete(ReceivingDraftSlots.Warehouse, ReceivingDraftStore.NextSeq() + 200);
        File.Exists(PathOf()).Should().BeFalse();
        File.Exists(PathOf() + ".bak").Should().BeFalse();

        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, text, late).Should().BeFalse("запоздавшая старая запись не воскрешает удалённый черновик");
        File.Exists(PathOf()).Should().BeFalse();
        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, text, ReceivingDraftStore.NextSeq() + 1000).Should().BeTrue("новая приёмка снова пишется");
    }

    [Fact]
    public void ParallelWrites_FromManyThreads_LeaveOneValidFile_WithTheNewestContent()
    {
        var seqs = Enumerable.Range(0, 24).Select(_ => ReceivingDraftStore.NextSeq()).ToList();
        Parallel.ForEach(seqs.Select((seq, i) => (seq, i)), new ParallelOptions { MaxDegreeOfParallelism = 8 }, x =>
        {
            var f = OneMeaningful();
            f.Lines[0].ProductName = "запись " + x.i;
            ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, ReceivingDraftStore.Serialize(f), x.seq);
        });

        var loaded = ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse);
        loaded.Should().NotBeNull("файл целый, не полуфайл");
        Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        // запись с меньшим номером, дошедшая позже большей, отбрасывается: итог всегда — запись с наибольшим номером
        loaded!.Lines[0].ProductName.Should().Be("запись 23");
    }

    // ------------------------------------------------------------------ сессия: дебаунс, пустое состояние, удаление

    private sealed class Probe
    {
        public int Captures;
        public volatile ReceivingDraftFile? State;

        public ReceivingDraftFile? Capture()
        {
            Interlocked.Increment(ref Captures);
            return State;
        }
    }

    private static ReceivingDraftSession Session(Probe p, string slot = ReceivingDraftSlots.Warehouse, int debounceMs = 120, int maxWaitMs = 1000) =>
        new(slot, p.Capture, TimeSpan.FromMilliseconds(debounceMs), TimeSpan.FromMilliseconds(maxWaitMs));

    [Fact]
    public void Session_Debounce_ManyChanges_OneWrite_AfterQuietPeriod()
    {
        var probe = new Probe { State = OneMeaningful() };
        using var session = Session(probe);
        session.Arm();
        var saved = 0;
        session.Saved += _ => Interlocked.Increment(ref saved);

        for (var i = 0; i < 20; i++)
        {
            probe.State!.Lines[0].Quantity = i + 1;
            session.Touch();
            Thread.Sleep(5);
        }
        File.Exists(PathOf()).Should().BeFalse("сразу после ввода файла ещё нет — ждём паузу");
        // 2026-11-11: в полном прогоне (2,5 тыс. тестов, пул потоков занят) запись не успевала за 5 с — ждём до 15 с; суть проверки (один снимок на серию) не меняется.
        WaitFor(() => File.Exists(PathOf()), 15000).Should().BeTrue("через паузу запись появилась");
        session.WaitIdle(TimeSpan.FromSeconds(5));
        Thread.Sleep(300);

        probe.Captures.Should().Be(1, "двадцать изменений подряд — один снимок");
        ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!.Lines[0].Quantity.Should().Be(20, "записано последнее значение");
        saved.Should().Be(1);
        session.LastSavedAt.Should().NotBeNull();
    }

    [Fact]
    public void Session_ContinuousTyping_StillWritesWithinMaxWait()
    {
        var probe = new Probe { State = OneMeaningful() };
        using var session = Session(probe, debounceMs: 400, maxWaitMs: 500);
        session.Arm();
        var until = DateTime.UtcNow.AddSeconds(4);
        var wrote = false;
        while (DateTime.UtcNow < until && !wrote)
        {
            session.Touch(); // человек печатает без пауз длиннее дебаунса
            Thread.Sleep(50);
            wrote = File.Exists(PathOf());
        }
        wrote.Should().BeTrue("непрерывный ввод не откладывает запись бесконечно");
    }

    [Fact]
    public void Session_NotArmed_DoesNothing_AndNeverTouchesAnOldDraft()
    {
        var old = OneMeaningful();
        old.Lines[0].ProductName = "Старый черновик";
        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, ReceivingDraftStore.Serialize(old), ReceivingDraftStore.NextSeq());

        var probe = new Probe { State = new ReceivingDraftFile() }; // пустая приёмка, на которую ещё не ответили «Продолжить?»
        using var session = Session(probe);
        session.Touch();
        session.Flush(wait: true);
        Thread.Sleep(400);

        probe.Captures.Should().Be(0);
        ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!.Lines[0].ProductName.Should().Be("Старый черновик");
    }

    [Fact]
    public void Session_EmptyState_IsNotWritten_AndDoesNotDeleteWhatItNeverHad()
    {
        var probe = new Probe { State = new ReceivingDraftFile { Lines = { new ReceivingDraftLine { ProductName = "Хлеб", Quantity = 0 } } } };
        using var session = Session(probe);
        session.Arm();
        session.Touch();
        session.Flush(wait: true);
        File.Exists(PathOf()).Should().BeFalse("нулевое количество — не приёмка, файл не создаётся");
        Directory.Exists(_dir).Should().BeTrue();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public void Session_ListBecomesEmpty_AfterPosting_RemovesDraft()
    {
        var probe = new Probe { State = OneMeaningful() };
        using var session = Session(probe);
        session.Arm();
        session.Flush(wait: true);
        File.Exists(PathOf()).Should().BeTrue();
        var cleared = 0;
        session.Cleared += () => Interlocked.Increment(ref cleared);

        probe.State = new ReceivingDraftFile(); // «Оприходовать» принято очередью — список пуст
        session.Flush(wait: true);

        File.Exists(PathOf()).Should().BeFalse("после проведения черновик удаляется");
        File.Exists(PathOf() + ".bak").Should().BeFalse();
        WaitFor(() => Volatile.Read(ref cleared) == 1).Should().BeTrue("событие «черновик удалён» дошло (оно идёт через контекст интерфейса)");
        session.LastSavedAt.Should().BeNull();
    }

    [Fact]
    public void Session_PostingFailed_ListKept_DraftStays()
    {
        var probe = new Probe { State = OneMeaningful() };
        using var session = Session(probe);
        session.Arm();
        session.Flush(wait: true);
        // ошибка проведения: строки остались в списке — файл не трогаем
        session.Flush(wait: true);
        File.Exists(PathOf()).Should().BeTrue();
        ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!.Lines.Should().HaveCount(1);
    }

    [Fact]
    public void Session_Discard_StartOver_DeletesFile_AndLaterNewWorkIsWrittenAsNewDraft()
    {
        var probe = new Probe { State = OneMeaningful() };
        using var session = Session(probe);
        session.Arm();
        session.Flush(wait: true);
        var firstStart = ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!.StartedAt;

        session.Discard(wait: true);
        File.Exists(PathOf()).Should().BeFalse();

        Thread.Sleep(20);
        probe.State = OneMeaningful();
        probe.State.Lines[0].ProductName = "Новая приёмка";
        session.Flush(wait: true);
        var again = ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!;
        again.Lines[0].ProductName.Should().Be("Новая приёмка");
        again.StartedAt.Should().BeAfter(firstStart, "это уже новый черновик");
    }

    [Fact]
    public void Session_Continue_KeepsStartTime_AndDoesNotRewriteIdenticalContent()
    {
        var f = OneMeaningful();
        f.Slot = ReceivingDraftSlots.Warehouse;
        f.StartedAt = new DateTime(2026, 11, 9, 8, 0, 0);
        f.SavedAt = new DateTime(2026, 11, 9, 8, 30, 0);
        ReceivingDraftStore.Save(ReceivingDraftSlots.Warehouse, ReceivingDraftStore.Serialize(f), ReceivingDraftStore.NextSeq());

        var probe = new Probe { State = OneMeaningful() };
        using var session = Session(probe);
        using (session.Suspend())
        {
            session.Touch(); // загрузка строк из черновика не пишет файл
        }
        session.Adopt(f);
        session.Flush(wait: true);

        var loaded = ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!;
        loaded.StartedAt.Should().Be(new DateTime(2026, 11, 9, 8, 0, 0), "«начата» не сдвигается после «Продолжить»");
    }

    [Fact]
    public void Session_TooManyLines_IsNotWritten()
    {
        var big = new ReceivingDraftFile();
        for (var i = 0; i < ReceivingDraftStore.MaxLines + 1; i++)
            big.Lines.Add(new ReceivingDraftLine { ProductName = "т" + i, Quantity = 1 });
        var probe = new Probe { State = big };
        using var session = Session(probe);
        session.Arm();
        session.Flush(wait: true);
        File.Exists(PathOf()).Should().BeFalse("огромный список не пишем (в журнал), чтобы не тормозить");
    }

    [Fact]
    public void Session_WarehouseAndPurchases_AreSeparateFiles()
    {
        var w = new Probe { State = OneMeaningful() };
        var p = new Probe { State = OneMeaningful() };
        p.State!.Lines[0].ProductName = "Из закупок";
        using var sw = Session(w, ReceivingDraftSlots.Warehouse);
        using var sp = Session(p, ReceivingDraftSlots.Purchases);
        sw.Arm();
        sp.Arm();
        sw.Flush(wait: true);
        sp.Flush(wait: true);
        ReceivingDraftStore.Load(ReceivingDraftSlots.Warehouse)!.Lines[0].ProductName.Should().Be("Хлеб");
        ReceivingDraftStore.Load(ReceivingDraftSlots.Purchases)!.Lines[0].ProductName.Should().Be("Из закупок");
        sw.Discard(wait: true);
        File.Exists(PathOf(ReceivingDraftSlots.Purchases)).Should().BeTrue("удаление одного окна не трогает другое");
    }

    [Fact]
    public void Session_SecondSessionForSameSlot_IsDetected()
    {
        var probe = new Probe();
        using (Session(probe))
        {
            ReceivingDraftSession.IsSlotActive(ReceivingDraftSlots.Warehouse).Should().BeTrue();
            ReceivingDraftSession.IsSlotActive(ReceivingDraftSlots.Purchases).Should().BeFalse();
        }
        ReceivingDraftSession.IsSlotActive(ReceivingDraftSlots.Warehouse).Should().BeFalse("после закрытия окна слот свободен");
    }

    [Fact]
    public void Session_CaptureThrows_DoesNotCrash()
    {
        using var session = new ReceivingDraftSession(ReceivingDraftSlots.Warehouse, () => throw new InvalidOperationException("Collection was modified"),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200));
        session.Arm();
        var act = () => session.Flush(wait: true);
        act.Should().NotThrow();
        File.Exists(PathOf()).Should().BeFalse();
    }

    // ------------------------------------------------------------------ тексты на 5 языках

    [Fact]
    public void Texts_AllFiveLanguages_NotEmpty_AndPluralizedInRussian()
    {
        ReceivingDraftTexts.Positions(1).Should().Be("1 позиция");
        ReceivingDraftTexts.Positions(3).Should().Be("3 позиции");
        ReceivingDraftTexts.Positions(5).Should().Be("5 позиций");
        ReceivingDraftTexts.Positions(11).Should().Be("11 позиций");
        ReceivingDraftTexts.Positions(21).Should().Be("21 позиция");
        ReceivingDraftTexts.Positions(112).Should().Be("112 позиций");

        var info = new ReceivingDraftInfo(ReceivingDraftSlots.Warehouse, 12, new DateTime(2026, 11, 11, 9, 0, 0), new DateTime(2026, 11, 11, 14, 5, 0), false);
        var q = ReceivingDraftTexts.Question(info);
        q.Should().Contain("12 позиций").And.Contain("11.11 14:05");
        ReceivingDraftTexts.Question(info with { Stale = true }).Should().Contain("30 дней");
        ReceivingDraftTexts.SavedCaption(new DateTime(2026, 11, 11, 14, 5, 0)).Should().Be("Черновик сохранён 14:05");
        ReceivingDraftTexts.DeleteQuestion(info).Should().Contain("12 позиций");
    }
}
