using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-12 (ТЗ «Сфера Электроника», ЭЛ1–ЭЛ5): профиль сферы, IMEI, журнал серийников, печать серийника и гарантии, срок обмена.
/// Ключевое правило: всё новое включается ТОЛЬКО при сфере «Электроника» — у остальных сфер чек, срок обмена и тело продажи как раньше.
/// Настройки — общий UserPreferences.Instance (значения возвращаются), журнал и настройки сферы — во временных файлах.</summary>
[Collection("UserPreferences")]
public sealed class ElectronicsSphereTests : IDisposable
{
    private const string ValidImei = "490154203237518";
    private readonly string _sphere;
    private readonly string? _detail;
    private readonly int _exchangeDays;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-tests", "electronics-" + Guid.NewGuid().ToString("N")[..8]);

    public ElectronicsSphereTests()
    {
        var p = UserPreferences.Instance;
        _sphere = p.MarketSphere;
        _detail = p.MarketSphereDetail;
        _exchangeDays = p.ExchangeDaysLimit;
        Directory.CreateDirectory(_dir);
        SerialJournal.FileOverride = Path.Combine(_dir, "serial-journal.json");
        ElectronicsSettings.FileOverride = Path.Combine(_dir, "electronics-settings.json");
    }

    public void Dispose()
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = _sphere;
        p.MarketSphereDetail = _detail;
        p.ExchangeDaysLimit = _exchangeDays;
        SerialJournal.FileOverride = null;
        ElectronicsSettings.FileOverride = null;
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    private static void SetSphere(string baseSphere, string? detail)
    {
        var p = UserPreferences.Instance;
        p.MarketSphere = baseSphere;
        p.MarketSphereDetail = detail;
    }

    private static void Electronics() => SetSphere(MarketSpheres.Grocery, ElectronicsProfile.Id);

    // ── ЭЛ1: профиль ──

    [Fact]
    public void Profile_is_registered_with_grocery_base_and_five_complete_languages()
    {
        var p = SphereCatalog.TryGet(ElectronicsProfile.Id);
        p.Should().NotBeNull();
        p!.BaseBehavior.Should().Be(MarketSpheres.Grocery, "на сервер уходит grocery");
        p.Title.IsComplete.Should().BeTrue();
        p.ShortTitle.IsComplete.Should().BeTrue();
        p.Description.IsComplete.Should().BeTrue();
        p.CategoryHints.Should().HaveCountGreaterOrEqualTo(12).And.OnlyContain(c => c.IsComplete);
        p.Units.Should().OnlyContain(u => u.Name.IsComplete && !u.Fractional, "дробных единиц у электроники нет");
        ElectronicsProfile.ServiceTemplates.Should().HaveCount(4).And.OnlyContain(c => c.IsComplete);
        p.Icon.Should().Be("📱");
        p.IconKey.Should().Be("phone");
        p.AccentHex.Should().Be("#6366F1");
        p.ShowVariants.Should().BeFalse("память и цвет — отдельные карточки, не варианты");
        p.ShowProductDetails.Should().BeTrue();
        MarketSpheres.Normalize(ElectronicsProfile.Id).Should().Be(MarketSpheres.Grocery);
        p.ChoiceVisible.Should().BeTrue("плитка в окне первого запуска и в Настройках");
        SphereCatalog.ChoiceList().Select(x => x.Id).Should().Contain(ElectronicsProfile.Id);
    }

    [Fact]
    public void Other_spheres_are_not_electronics_and_see_no_new_behaviour()
    {
        foreach (var (baseSphere, detail) in new[] { ("grocery", (string?)null), ("clothing", null), ("services", null), ("grocery", "hardware") })
        {
            SetSphere(baseSphere, detail);
            ElectronicsProfile.IsActive.Should().BeFalse($"{baseSphere}/{detail}");
            ElectronicsProfile.NeedsSerial("Смартфоны").Should().BeFalse();
            ElectronicsProfile.WarrantyMonthsFor("Смартфоны").Should().Be(0);
        }
        // Точная сфера не из своей базы — базовая (clothing + electronics → clothing).
        SphereCatalog.Resolve("clothing", ElectronicsProfile.Id).Id.Should().Be("clothing");
        SphereCatalog.Resolve("grocery", ElectronicsProfile.Id).Id.Should().Be(ElectronicsProfile.Id);
        SphereCatalog.Get("grocery").Id.Should().Be("grocery");
    }

    // ── ЭЛ2: IMEI и серийные номера ──

    [Fact]
    public void Imei_luhn_check_accepts_valid_and_rejects_one_digit_error()
    {
        ImeiValidator.IsValidImei(ValidImei).Should().BeTrue();
        ImeiValidator.IsValidImei("49-015420 323751/8").Should().BeTrue("разделители убираются");
        foreach (var bad in new[] { "490154203237519", "490154203237508", "390154203237518", "49015420323751", "4901542032375181" })
            ImeiValidator.IsValidImei(bad).Should().BeFalse(bad);
    }

    [Theory]
    [InlineData("490154203237518", ImeiValidator.Verdict.Ok)]
    [InlineData("490154203237519", ImeiValidator.Verdict.BadImei)]
    [InlineData("49015420323751", ImeiValidator.Verdict.BadImei)]   // 14 цифр — похоже на IMEI без цифры
    [InlineData("F2LXK0QJ1234", ImeiValidator.Verdict.Ok)]           // обычный серийный номер
    [InlineData("12345678", ImeiValidator.Verdict.Ok)]               // короткий цифровой — серийный номер
    [InlineData("  ", ImeiValidator.Verdict.Empty)]
    [InlineData("ab", ImeiValidator.Verdict.BadFormat)]
    [InlineData("SN@#$%123", ImeiValidator.Verdict.BadFormat)]
    public void Serial_check_verdicts(string text, ImeiValidator.Verdict expected) =>
        ImeiValidator.Check(text, out _).Should().Be(expected);

    [Fact]
    public void Serial_label_is_imei_only_for_valid_imei()
    {
        ImeiValidator.LabelFor(ValidImei).Should().Be("IMEI");
        ImeiValidator.LabelFor("F2LXK0QJ1234").Should().Be("S/N");
    }

    [Fact]
    public void Serial_categories_default_and_owner_override()
    {
        ElectronicsProfile.NeedsSerialIn("Смартфоны", null).Should().BeTrue();
        ElectronicsProfile.NeedsSerialIn("Ноутбуки и ПК", null).Should().BeTrue();
        ElectronicsProfile.NeedsSerialIn("Бытовая техника крупная", null).Should().BeTrue();
        ElectronicsProfile.NeedsSerialIn("Телевизоры", null).Should().BeTrue();
        ElectronicsProfile.NeedsSerialIn("Smartphones", null).Should().BeTrue("названия на других языках");
        ElectronicsProfile.NeedsSerialIn("Телефоны Samsung", null).Should().BeTrue("слово из категории владельца");
        ElectronicsProfile.NeedsSerialIn("Аксессуары (чехлы, стёкла, зарядки, кабели)", null).Should().BeFalse();
        ElectronicsProfile.NeedsSerialIn("Аксессуары для телефонов", null).Should().BeFalse("аксессуар — не телефон");
        ElectronicsProfile.NeedsSerialIn("Услуги (настройка, перенос данных, наклейка стекла)", null).Should().BeFalse();
        ElectronicsProfile.NeedsSerialIn("", null).Should().BeFalse();
        ElectronicsProfile.NeedsSerialIn("Смартфоны", new[] { "Часы" }).Should().BeFalse("список владельца главнее умолчания");
        ElectronicsProfile.NeedsSerialIn("часы", new[] { "Часы" }).Should().BeTrue();
    }

    [Fact]
    public void Warranty_defaults_and_owner_months()
    {
        Electronics();
        ElectronicsProfile.WarrantyMonthsFor("Смартфоны").Should().Be(12);
        ElectronicsProfile.WarrantyMonthsFor("Аксессуары для телефонов").Should().Be(0);
        ElectronicsSettings.Save(new ElectronicsSettings { WarrantyByCategory = new() { ["Ноутбуки и ПК"] = 24 } });
        ElectronicsProfile.WarrantyMonthsFor("Ноутбуки и ПК").Should().Be(24);
        ElectronicsProfile.WarrantyMonthsFor("Смартфоны").Should().Be(12);
        WarrantyCard.WarrantyUntil(new DateTime(2026, 11, 12, 15, 0, 0), 12).Should().Be(new DateTime(2027, 11, 12));
        WarrantyCard.WarrantyUntil(new DateTime(2026, 1, 31), 1).Should().Be(new DateTime(2026, 2, 28));
    }

    // ── Журнал, ЭЛ2/ЭЛ4 ──

    private static string PhoneAndCaseCart(bool withSerials, int phoneQty = 1)
    {
        var phone = new JsonObject
        {
            ["id"] = "line-1", ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = "Телефон X1", ["quantity"] = phoneQty, ["unit_price"] = 20000.0, ["line_total"] = 20000.0 * phoneQty,
        };
        if (withSerials)
        {
            phone[ElectronicsProfile.SerialsField] = new JsonArray(ValidImei, "F2LXK0QJ1234");
            phone[ElectronicsProfile.WarrantyMonthsField] = 12;
        }
        var back = new JsonObject
        {
            ["id"] = "line-2", ["product_id"] = Guid.NewGuid().ToString(), ["product_name"] = "Чехол X1", ["quantity"] = 1, ["unit_price"] = 500.0, ["line_total"] = 500.0,
        };
        return new JsonObject { ["items"] = new JsonArray(phone, back), ["receipt_number"] = "123" }.ToJsonString();
    }

    [Fact]
    public void Journal_is_written_only_for_electronics_and_only_lines_with_serials()
    {
        var cart = PhoneAndCaseCart(withSerials: true, phoneQty: 2);
        SetSphere("grocery", null);
        SerialJournal.RecordSale("sale-0", "1", new DateTime(2026, 11, 12), cart);
        SerialJournal.ReadAll().Should().BeEmpty("в других сферах журнал не пишется");

        Electronics();
        SerialJournal.RecordSale("sale-1", "123", new DateTime(2026, 11, 12), cart);
        var all = SerialJournal.ReadAll();
        all.Should().HaveCount(2, "два номера у телефона, у чехла номеров нет");
        all.Should().OnlyContain(r => r.SaleId == "sale-1" && r.Title == "Телефон X1" && r.WarrantyMonths == 12 && r.WarrantyUntil == new DateTime(2027, 11, 12));
        SerialJournal.RecordSale("sale-1", "123", new DateTime(2026, 11, 12), cart);
        SerialJournal.ReadAll().Should().HaveCount(2, "повторная запись той же продажи не дублирует");
    }

    [Fact]
    public void Journal_search_by_last_six_digits_finds_receipt_and_foreign_serial_is_not_found()
    {
        Electronics();
        SerialJournal.RecordSale("sale-1", "123", DateTime.Now, PhoneAndCaseCart(true));
        SerialJournal.Find("237518").Should().ContainSingle(r => r.SaleId == "sale-1", "последние 6 цифр IMEI");
        SerialJournal.Find("f2lxk").Should().ContainSingle("без учёта регистра, по части");
        SerialJournal.Find("999999").Should().BeEmpty("чужого номера на этой кассе нет");
        SerialJournal.Find("23").Should().BeEmpty("слишком короткий запрос не ищет");
        SerialJournal.FindSold(ValidImei)!.SaleId.Should().Be("sale-1");
        SerialJournal.ForSale("sale-1").Should().HaveCount(2);
        SerialJournal.ForSale("other").Should().BeEmpty();
    }

    // ── ЭЛ3: печать ──

    private static readonly DateTime Moment = new(2026, 11, 12, 14, 30, 0);

    [Fact]
    public void Receipt_shows_serial_and_warranty_only_under_the_phone_in_electronics()
    {
        Electronics();
        var text = CartReceiptTextBuilder.BuildSimpleReceipt(PhoneAndCaseCart(true), null, "cash", "30000", Moment);
        text.Should().Contain("IMEI: " + ValidImei).And.Contain("S/N: F2LXK0QJ1234");
        text.Should().Contain("12.11.2027", "гарантия до даты продажи + 12 месяцев");
        text.Split("12.11.2027").Length.Should().Be(2, "блок гарантии — один, у телефона; у чехла его нет");
        var lines = text.Split('\n');
        var caseIndex = Array.FindIndex(lines, l => l.Contains("Чехол X1"));
        lines.Skip(caseIndex).Take(3).Should().NotContain(l => l.Contains("IMEI") || l.Contains("S/N"));
    }

    [Theory]
    [InlineData("grocery", null)]
    [InlineData("clothing", null)]
    [InlineData("services", null)]
    [InlineData("grocery", "hardware")]
    public void Receipts_of_other_spheres_are_byte_identical_with_or_without_serial_fields(string baseSphere, string? detail)
    {
        SetSphere(baseSphere, detail);
        var withFields = CartReceiptTextBuilder.BuildSimpleReceipt(PhoneAndCaseCart(true), null, "cash", "30000", Moment);
        var without = CartReceiptTextBuilder.BuildSimpleReceipt(PhoneAndCaseCart(false), null, "cash", "30000", Moment);
        withFields.Should().Be(without);
        withFields.Should().NotContain("IMEI").And.NotContain("S/N");
    }

    [Fact]
    public void Reprint_of_server_sale_takes_serials_from_journal_only_in_electronics()
    {
        Electronics();
        var phoneId = Guid.NewGuid().ToString();
        var cart = new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = "l1", ["product_id"] = phoneId, ["product_name"] = "Телефон X1", ["quantity"] = 1, ["unit_price"] = 20000.0, ["line_total"] = 20000.0,
                [ElectronicsProfile.SerialsField] = new JsonArray(ValidImei), [ElectronicsProfile.WarrantyMonthsField] = 12,
            }),
        }.ToJsonString();
        SerialJournal.RecordSale("sale-9", "9", Moment, cart);
        var sale = $$"""{"id":"sale-9","items":[{"product":"{{phoneId}}","product_name":"Телефон X1","quantity":"1.000","price":"20000.00","total":"20000.00"}],"created_at":"2026-11-12T14:30:00"}""";
        var electronicsText = SaleReceiptTextBuilder.Build(sale, "9", 0m, 20000m, isReprint: true);
        electronicsText.Should().Contain("IMEI: " + ValidImei);

        SetSphere("grocery", null);
        SaleReceiptTextBuilder.Build(sale, "9", 0m, 20000m, isReprint: true).Should().NotContain("IMEI");
    }

    [Fact]
    public void Warranty_card_has_store_item_serial_dates_and_five_languages()
    {
        var rec = new SerialRecord { SaleId = "s", ReceiptNo = "123", SoldAt = Moment, Title = "Телефон X1", Serial = ValidImei, WarrantyMonths = 12, WarrantyUntil = new DateTime(2027, 11, 12) };
        var prefs = UserPreferences.Instance;
        var oldLang = prefs.Language;
        try
        {
            foreach (var lang in new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek })
            {
                prefs.Language = lang;
                var card = WarrantyCard.BuildCard(rec, 32);
                card.Should().Contain("Телефон X1").And.Contain(ValidImei).And.Contain("12.11.2027").And.Contain("12.11.2026").And.Contain("123");
                card.Split('\n').Should().OnlyContain(l => l.Length <= 40, "ширина ленты 58 мм");
            }
        }
        finally
        {
            prefs.Language = oldLang;
        }
        var ru = WarrantyCard.BuildCard(rec, 32);
        WarrantyCard.BuildCards(new[] { rec, rec }, 32).Should().Contain(ru).And.HaveLength(ru.Length * 2 + 1);
    }

    // ── Корзина и тело продажи ──

    [Fact]
    public void Serials_are_added_to_cart_line_trimmed_on_quantity_decrease_and_never_reach_the_sale_body()
    {
        Electronics();
        var cart = new CartService();
        CartTestHelper.StartEmptyCart(cart);
        var phone = CartTestHelper.CreateProduct(Guid.NewGuid().ToString(), "Телефон X1", 20000m);
        cart.AddItem(phone, 2);
        var itemId = cart.Items.Single().Id!;

        ReceiptSnapshotCartEditor.AddLineSerials(cart, itemId, new[] { ValidImei, "F2LXK0QJ1234" }, 12).Should().BeTrue();
        using (var doc = JsonDocument.Parse(cart.GetRawText()))
        {
            var line = CartDisplayHelper.EnumerateItems(doc.RootElement).Single();
            SerialJournal.ReadSerials(line).Should().Equal(ValidImei, "F2LXK0QJ1234");
            SerialJournal.ReadMonths(line).Should().Be(12);
        }

        var shift = Guid.NewGuid().ToString();
        var body = QuickCheckoutBody.TryBuild(cart.GetRawText(), "cash", "50000", null, null, shift, null, false, null, false, out _);
        body.Should().NotBeNull();
        var bodyText = body!.ToJsonString();
        bodyText.Should().NotContain("serial").And.NotContain(ValidImei).And.NotContain("warranty");

        ReceiptSnapshotCartEditor.UpdateLineQuantity(cart, itemId, 1);
        using var doc2 = JsonDocument.Parse(cart.GetRawText());
        SerialJournal.ReadSerials(CartDisplayHelper.EnumerateItems(doc2.RootElement).Single()).Should().Equal(new[] { ValidImei }, "остаётся столько номеров, сколько штук");
    }
}
