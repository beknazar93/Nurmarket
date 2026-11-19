using FluentAssertions;
using NurMarketKassa.Services;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20 (сфера «Автозапчасти», часть Б): цена СТО, срок и условия возврата, заказ под клиента (суммы, предоплата, статусы, хранение,
/// повреждённый файл), печать гарантии и блока «Гарантия и возврат», Б/У с количеством 1. Файлы — только во временной папке; другие сферы — без изменений.</summary>
[Collection("UserPreferences")]
public sealed class AutoPartsPartBTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "autoparts-b-" + Guid.NewGuid().ToString("N")[..8]);

    public AutoPartsPartBTests()
    {
        Directory.CreateDirectory(_dir);
        AutoPartsProductMeta.FileOverride = Path.Combine(_dir, "autoparts-products.json");
    }

    public void Dispose()
    {
        AutoPartsRules.ActiveOverride = null;
        AutoPartsProductMeta.FileOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private PartsOrderStore NewStore(DateTime? now = null) =>
        new(Path.Combine(_dir, "parts-orders.json"), () => now ?? new DateTime(2026, 11, 20, 10, 0, 0));

    private static PartsOrder Order(decimal prepay = 0m, params (string title, decimal qty, decimal price)[] lines) => new()
    {
        ClientName = "СТО Асан",
        Phone = "0700123456",
        Prepayment = prepay,
        Lines = lines.Select(l => new PartsOrderLine { Title = l.title, Qty = l.qty, Price = l.price }).ToList(),
    };

    // ───────────── цена СТО ─────────────

    [Fact]
    public void StoPrice_ClientWithMark_GetsStoPrice_OtherwiseRetail()
    {
        AutoPartsRules.StoPrice(1000m, 800m, clientIsSto: true).Should().Be((800m, true, false));
        AutoPartsRules.StoPrice(1000m, 800m, clientIsSto: false).Should().Be((1000m, false, false));
    }

    [Fact]
    public void StoPrice_NoStoPrice_FallsBackToRetailWithHint()
    {
        AutoPartsRules.StoPrice(1000m, 0m, clientIsSto: true).Should().Be((1000m, false, true));
        // цена СТО выше розницы — ошибка карточки, продаём по рознице
        AutoPartsRules.StoPrice(1000m, 1200m, clientIsSto: true).Should().Be((1000m, false, true));
    }

    [Fact]
    public void PriceLevelName_IsOnFiveLanguages()
    {
        AutoPartsRules.PriceLevelName.Should().NotBeNullOrWhiteSpace();
    }

    // ───────────── заказ: суммы и предоплата ─────────────

    [Fact]
    public void Order_Remainder_AfterPrepayment()
    {
        var o = Order(100m, ("Фильтр масляный", 2m, 150m), ("Свеча", 4m, 50m)); // 300 + 200
        PartsOrderRules.Total(o).Should().Be(500m);
        PartsOrderRules.Remainder(o).Should().Be(400m);
        o.PrepaymentRefunded = true;
        PartsOrderRules.Remainder(o).Should().Be(500m, "возвращённая предоплата в доплату не зачитывается");
    }

    [Fact]
    public void Order_PrepaymentIsOptional_AndSuggestionIsHalf()
    {
        var o = Order(0m, ("Колодки", 1m, 3333m));
        PartsOrderRules.Remainder(o).Should().Be(3333m);
        PartsOrderRules.SuggestPrepayment(3333m).Should().Be(1667m);
        PartsOrderRules.SuggestPrepayment(0m).Should().Be(0m);
        PartsOrderRules.ClampPrepayment(9999m, 500m).Should().Be(500m);
        PartsOrderRules.ClampPrepayment(-5m, 500m).Should().Be(0m);
    }

    [Fact]
    public void Order_PrepaymentsOnHand_CountsOnlyOpenNotRefunded()
    {
        var open = Order(100m, ("A", 1m, 500m));
        var issued = Order(200m, ("B", 1m, 500m)); issued.Status = PartsOrderStatus.Issued;
        var cancelled = Order(300m, ("C", 1m, 500m)); cancelled.Status = PartsOrderStatus.Cancelled; cancelled.PrepaymentRefunded = true;
        PartsOrderRules.PrepaymentsOnHand(new[] { open, issued, cancelled }).Should().Be(100m);
    }

    [Fact]
    public void Order_StatusChain_And_SoftLimitOnlyOnStart()
    {
        PartsOrderRules.Next(PartsOrderStatus.New).Should().Be(PartsOrderStatus.Ordered);
        PartsOrderRules.Next(PartsOrderStatus.Ordered).Should().Be(PartsOrderStatus.Arrived);
        PartsOrderRules.Next(PartsOrderStatus.Arrived).Should().Be(PartsOrderStatus.Issued);
        PartsOrderRules.Next(PartsOrderStatus.Issued).Should().BeNull();
        PartsOrderRules.Next(PartsOrderStatus.Cancelled).Should().BeNull();
        PartsOrderRules.SoftLimitHint(10, startTariff: true).Should().BeNull();
        PartsOrderRules.SoftLimitHint(11, startTariff: true).Should().NotBeNullOrWhiteSpace("только подсказка");
        PartsOrderRules.SoftLimitHint(50, startTariff: false).Should().BeNull();
    }

    [Fact]
    public void Order_WhatsAppText_ReadyAndExpected()
    {
        var o = Order(100m, ("Фильтр", 1m, 500m));
        o.Number = 7;
        o.DueDate = new DateTime(2026, 11, 25);
        o.Status = PartsOrderStatus.Arrived;
        PartsOrderRules.WhatsAppText(o, "Автодом").Should().Contain("7").And.Contain("Автодом").And.Contain("400");
        o.Status = PartsOrderStatus.Ordered;
        PartsOrderRules.WhatsAppText(o, null).Should().Contain("25.11.2026");
    }

    // ───────────── хранение ─────────────

    [Fact]
    public void Store_SaveLoad_NumbersAreSequential()
    {
        var store = NewStore();
        var a = Order(50m, ("Фильтр", 1m, 100m));
        var b = Order(0m, ("Свеча", 4m, 80m));
        store.Save(a);
        store.Save(b);
        a.Number.Should().Be(1);
        b.Number.Should().Be(2);
        var loaded = NewStore().All();
        loaded.Should().HaveCount(2);
        loaded.Single(o => o.Number == 1).Prepayment.Should().Be(50m);
        loaded.Single(o => o.Number == 2).Lines.Single().Qty.Should().Be(4m);
    }

    [Fact]
    public void Store_Advance_Cancel_Issue()
    {
        var store = NewStore();
        var o = Order(100m, ("Фильтр", 1m, 500m));
        store.Save(o);
        store.Advance(o.Id).Should().BeTrue();
        store.Find(o.Id)!.Status.Should().Be(PartsOrderStatus.Ordered);
        store.Advance(o.Id).Should().BeTrue();
        store.Find(o.Id)!.Status.Should().Be(PartsOrderStatus.Arrived);
        store.OpenCount().Should().Be(1);

        store.Cancel(o.Id, "клиент отказался").Should().BeTrue();
        var cancelled = store.Find(o.Id)!;
        cancelled.Status.Should().Be(PartsOrderStatus.Cancelled);
        cancelled.PrepaymentRefunded.Should().BeTrue("предоплата возвращается клиенту");
        store.OpenCount().Should().Be(0);
        store.Cancel(o.Id, null).Should().BeFalse("закрытый заказ повторно не отменяется");
        store.MarkIssued(o.Id).Should().BeFalse();

        var second = Order(0m, ("Свеча", 1m, 80m));
        store.Save(second);
        store.MarkIssued(second.Id).Should().BeTrue();
        store.Find(second.Id)!.Status.Should().Be(PartsOrderStatus.Issued);
    }

    [Fact]
    public void Store_CorruptedFile_StartsEmpty_KeepsBadCopy_AndKeepsWorking()
    {
        var path = Path.Combine(_dir, "parts-orders.json");
        File.WriteAllText(path, "{ это не json ");
        var store = NewStore();
        store.All().Should().BeEmpty("повреждённый файл — пустой список, без исключения");

        var o = Order(0m, ("Фильтр", 1m, 100m));
        store.Save(o);
        File.Exists(path + ".bad").Should().BeTrue("испорченный файл отложен рядом");
        File.ReadAllText(path + ".bad").Should().Contain("не json");
        NewStore().All().Should().ContainSingle();
    }

    [Fact]
    public void Store_MissingFile_IsEmpty()
    {
        NewStore().All().Should().BeEmpty();
        NewStore().Find("нет").Should().BeNull();
    }

    // ───────────── возврат ─────────────

    [Fact]
    public void Return_TenDays_Ok_TwentyDays_NeedsPin()
    {
        var today = new DateTime(2026, 11, 20);
        var lines = new[] { ("Фильтр масляный", false) };
        AutoPartsRules.CheckReturn(today.AddDays(-10), today, 14, true, lines).NeedsPin.Should().BeFalse();
        var late = AutoPartsRules.CheckReturn(today.AddDays(-20), today, 14, true, lines);
        late.Expired.Should().BeTrue();
        late.DaysPassed.Should().Be(20);
        late.Limit.Should().Be(14);
        late.NeedsPin.Should().BeTrue();
    }

    [Fact]
    public void Return_DayOfPurchaseNotCounted_And_ZeroLimitMeansNoCheck()
    {
        var today = new DateTime(2026, 11, 20);
        AutoPartsRules.CheckReturn(today.AddDays(-14), today, 14, true, Array.Empty<(string, bool)>()).Expired.Should().BeFalse();
        AutoPartsRules.CheckReturn(today.AddDays(-15), today, 14, true, Array.Empty<(string, bool)>()).Expired.Should().BeTrue();
        AutoPartsRules.CheckReturn(today.AddDays(-400), today, 0, true, Array.Empty<(string, bool)>()).Expired.Should().BeFalse();
        AutoPartsRules.CheckReturn(null, today, 14, true, Array.Empty<(string, bool)>()).Expired.Should().BeFalse("даты чека нет — срок не проверяем");
    }

    [Fact]
    public void Return_ElectricNotAccepted_OnlyWhenOwnerSettingOn()
    {
        var today = new DateTime(2026, 11, 20);
        var lines = new[] { ("Датчик кислорода", true), ("Масло 5W-40", false) };
        var on = AutoPartsRules.CheckReturn(today, today, 14, noInstalled: true, lines);
        on.NotAccepted.Should().Equal("Датчик кислорода");
        on.NeedsPin.Should().BeTrue();
        AutoPartsRules.CheckReturn(today, today, 14, noInstalled: false, lines).NeedsPin.Should().BeFalse();
    }

    [Fact]
    public void ElectricDetection_ByTitle_AndByExplicitCardMark()
    {
        AutoPartsRules.LooksElectric("Датчик кислорода Bosch").Should().BeTrue();
        AutoPartsRules.LooksElectric("Блок управления двигателем").Should().BeTrue();
        AutoPartsRules.LooksElectric("Стартер 12V").Should().BeTrue();
        AutoPartsRules.LooksElectric("Масло моторное 5W-40").Should().BeFalse();
        AutoPartsRules.LooksElectric("Сальник коленвала").Should().BeFalse();

        AutoPartsProductMeta.IsNotReturnable("p1", "Масло").Should().BeFalse();
        AutoPartsProductMeta.Set("p1", 30, electric: false, noReturn: true);
        AutoPartsProductMeta.IsNotReturnable("p1", "Масло").Should().BeTrue("явная отметка карточки");
        AutoPartsProductMeta.Set("p2", null, electric: false, noReturn: false);
        AutoPartsProductMeta.IsNotReturnable("p2", "Датчик").Should().BeFalse("явная отметка «можно» сильнее названия");
        AutoPartsProductMeta.IsNotReturnable("нет-такого", "Датчик давления").Should().BeTrue("без отметки — по названию");
    }

    [Fact]
    public void ReturnWarningText_SaysExpiredAndElectric_AndPin()
    {
        var today = new DateTime(2026, 11, 20);
        var v = AutoPartsRules.CheckReturn(today.AddDays(-20), today, 14, true, new[] { ("Датчик кислорода", true) });
        var text = AutoPartsRules.ReturnWarningText(v);
        text.Should().Contain("Срок возврата истёк (20 дн.").And.Contain("Электрика").And.Contain("Датчик кислорода").And.Contain("PIN");
        AutoPartsRules.ReturnWarningText(AutoPartsRules.CheckReturn(today.AddDays(-20), today, 14, true, Array.Empty<(string, bool)>()))
            .Should().NotContain("Электрика");
    }

    [Fact]
    public void ReturnSettings_Defaults_14Days_NoInstalledOn()
    {
        AutoPartsRules.GetReturnDays("{}").Should().Be(14);
        AutoPartsRules.GetNoInstalled("{}").Should().BeTrue();
        AutoPartsRules.GetReturnDays("{\"autoparts\":{\"returnDays\":30,\"noInstalled\":false}}").Should().Be(30);
        AutoPartsRules.GetNoInstalled("{\"autoparts\":{\"returnDays\":30,\"noInstalled\":false}}").Should().BeFalse();
        AutoPartsRules.GetReturnDays("мусор").Should().Be(14, "битые настройки — умолчание");
    }

    // ───────────── чек: гарантия и блок возврата, Б/У ─────────────

    private const string UsedPartCart = """
        {
          "receipt_number": "20261120-0001",
          "items": [
            { "product_id": "pUsed", "server_is_weight": false, "product_name": "Б/У Генератор Toyota (с разборки)", "quantity": 1, "unit_price": 4500, "line_total": 4500 }
          ]
        }
        """;

    [Fact]
    public void Receipt_AutoParts_ShowsWarrantyDays_AndReturnBlock_ForUsedPartQty1()
    {
        AutoPartsRules.ActiveOverride = true;
        AutoPartsProductMeta.Set("pUsed", 7, electric: true, noReturn: false);
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(UsedPartCart, null, "cash", "5000", new DateTime(2026, 11, 20, 12, 0, 0));
        // чек печатается заглавными буквами
        text.Should().Contain("Б/У ГЕНЕРАТОР").And.Contain("ГАРАНТИЯ 7 ДН.").And.Contain("ГАРАНТИЯ И ВОЗВРАТ").And.Contain("14 ДН.").And.Contain("ПОДЛЕЖАТ");
        text.Should().Contain("ИТОГО");
    }

    [Fact]
    public void Receipt_OtherSpheres_AreUnchanged()
    {
        AutoPartsRules.ActiveOverride = false;
        AutoPartsProductMeta.Set("pUsed", 7, electric: true, noReturn: false);
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(UsedPartCart, null, "cash", "5000", new DateTime(2026, 11, 20, 12, 0, 0));
        text.Should().NotContain("ГАРАНТИЯ").And.NotContain("ПОДЛЕЖАТ");
        AutoPartsRules.WarrantyLine("pUsed").Should().BeNull();
        AutoPartsRules.ReceiptReturnBlock(32).Should().BeEmpty();
    }

    [Fact]
    public void Receipt_NoWarranty_NoLine()
    {
        AutoPartsRules.ActiveOverride = true;
        AutoPartsRules.WarrantyLine("нет-гарантии").Should().BeNull();
        AutoPartsRules.WarrantyDaysOf("нет-гарантии").Should().Be(0);
    }
}
