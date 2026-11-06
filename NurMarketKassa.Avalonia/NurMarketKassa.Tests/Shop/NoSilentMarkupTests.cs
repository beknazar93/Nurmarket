using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-06 (жалоба «Орук маркет», приход №48: программа молча ставила «закупка + 20 %» товарам без цены продажи): общая наценка выключена по умолчанию и после обновления,
/// строка без цены не получает цену сама, «Оставить без цены» не шлёт PATCH цены, существующая цена не перезаписывается, ввод цены/наценки в окне считается верно.
/// Без боевых данных: сервер — WireMock, настройки — в памяти.</summary>
[Collection("ReceivingMarkupSettings")]
public sealed class NoSilentMarkupTests
{
    private static T WithSettings<T>(ReceivingMarkupSettings settings, Func<T> body)
    {
        var previous = ReceivingMarkupSettingsStore.Current.Clone();
        var persistDisabled = ReceivingMarkupSettingsStore.PersistenceDisabled;
        try
        {
            ReceivingMarkupSettingsStore.PersistenceDisabled = true;
            ReceivingMarkupSettingsStore.Override(settings);
            return body();
        }
        finally
        {
            ReceivingMarkupSettingsStore.Override(previous);
            ReceivingMarkupSettingsStore.PersistenceDisabled = persistDisabled;
        }
    }

    private static ReceivingLineVm Existing(string id, double cardSale, double cardPurchase)
    {
        var line = new ReceivingLineVm { OriginalSalePrice = cardSale, OriginalPurchasePrice = cardPurchase, ProductName = "Товар " + id };
        line.ProductId = id;
        line.Source = ReceivingSource.Warehouse;
        line.Quantity = 1;
        line.PurchasePrice = cardPurchase;
        line.SalePrice = cardSale;
        return line;
    }

    // ------------------------------------------------------------------ настройки: выключено по умолчанию, сброс один раз

    [Fact]
    public void Common_markup_is_off_by_default()
    {
        var s = new ReceivingMarkupSettings();
        s.AutoEnabled.Should().BeFalse("новый пользователь: общая наценка выключена");
        s.DefaultPercent.Should().Be(0, "и не 20, и не 30");
    }

    [Fact]
    public void Old_saved_settings_with_20_percent_are_reset_once_to_off()
    {
        var persistDisabled = ReceivingMarkupSettingsStore.PersistenceDisabled;
        try
        {
            ReceivingMarkupSettingsStore.PersistenceDisabled = true; // в боевой %APPDATA% ничего не пишем
            // Файл прошлой версии: наценка включена на 20 %, признака сброса нет.
            var old = JsonSerializer.Deserialize<ReceivingMarkupSettings>("{\"AutoEnabled\":true,\"DefaultPercent\":20,\"Rounding\":\"WholeUp\"}")!;
            old.MarkupResetDone.Should().BeFalse();

            ReceivingMarkupSettingsStore.MigrateReset(old).Should().BeTrue("сброс выполняется один раз");
            old.AutoEnabled.Should().BeFalse();
            old.DefaultPercent.Should().Be(0);
            old.MarkupResetDone.Should().BeTrue();

            // Владелец включил наценку сам (признак сброса уже стоит) — повторно не сбрасываем.
            old.AutoEnabled = true;
            old.DefaultPercent = 25;
            ReceivingMarkupSettingsStore.MigrateReset(old).Should().BeFalse();
            old.AutoEnabled.Should().BeTrue();
            old.DefaultPercent.Should().Be(25);
        }
        finally
        {
            ReceivingMarkupSettingsStore.PersistenceDisabled = persistDisabled;
        }
    }

    [Fact]
    public void Typing_a_number_in_the_field_turns_the_common_markup_on_and_empty_turns_it_off()
    {
        WithSettings(new ReceivingMarkupSettings { MarkupResetDone = true }, () =>
        {
            ReceivingMarkupSettingsStore.Current.AutoEnabled.Should().BeFalse();
            ReceivingMarkupSettingsStore.SetDefaultPercent(25, persist: false);
            ReceivingMarkupSettingsStore.Current.AutoEnabled.Should().BeTrue("число в поле — явное включение");
            ReceivingMarkupSettingsStore.Current.DefaultPercent.Should().Be(25);

            ReceivingMarkupSettingsStore.SetDefaultPercent(0, persist: false);
            ReceivingMarkupSettingsStore.Current.AutoEnabled.Should().BeFalse("пусто/0 — общей наценки нет");
            ReceivingMarkupSettings.PercentBoxText(0).Should().BeEmpty();
            ReceivingMarkupSettings.PercentBoxText(12.5).Should().Be("12,5");
            return 0;
        });
    }

    // ------------------------------------------------------------------ сценарий владельца: товар без цены, закупка 100, цену не ввели

    [Fact]
    public void Product_without_price_and_purchase_100_gets_no_silent_120()
    {
        WithSettings(new ReceivingMarkupSettings { MarkupResetDone = true }, () =>
        {
            var line = Existing("p1", cardSale: 0, cardPurchase: 0);
            line.EnableReceivingPricing();
            line.SetPurchaseText("100");

            line.SalePrice.Should().Be(0, "программа не ставит 120 сама");
            line.SaleStatusText.Should().BeEmpty("подписи «авто (по общей наценке…)» нет, пока наценка не включена");
            line.IsSalePriceMissing.Should().BeTrue("строка подсвечивается");
            ReceivingPricing.MissingSalePrice(new[] { line }).Should().ContainSingle("перед отправкой прихода спросим цену");

            // Новый товар — то же самое.
            var fresh = new ReceivingLineVm { Barcode = "4600000000009", ProductName = "Новый", Source = ReceivingSource.Unknown, Quantity = 1 };
            fresh.EnableReceivingPricing();
            fresh.SetPurchaseText("30");
            fresh.SalePrice.Should().Be(0, "30 → 36 больше не происходит");
            return 0;
        });
    }

    [Fact]
    public void Explicit_common_markup_prices_lines_without_price_and_shows_the_result_in_the_row()
    {
        WithSettings(new ReceivingMarkupSettings { MarkupResetDone = true }, () =>
        {
            var line = Existing("p1", 0, 0);
            line.EnableReceivingPricing();
            line.SetPurchaseText("100");
            line.SalePrice.Should().Be(0);

            ReceivingMarkupSettingsStore.SetDefaultPercent(20, persist: false); // владелец ввёл 20 в поле
            ReceivingPricing.ApplyAutoToAll(new[] { line }).Should().Be(1);

            line.SalePrice.Should().Be(120, "итоговая цена видна в строке до отправки");
            line.SaleStatusText.Should().Contain("авто").And.Contain("20");
            return 0;
        });
    }

    [Fact]
    public void Apply_to_all_prices_lines_without_a_price_only_when_the_common_markup_is_on()
    {
        WithSettings(new ReceivingMarkupSettings { MarkupResetDone = true }, () =>
        {
            var line = Existing("p1", 0, 0);
            line.EnableReceivingPricing();
            line.SetPurchaseText("100");

            ReceivingPricing.ApplyCommonMarkupToAll(new[] { line }, out _).Should().Be(0, "наценка выключена — «Применить ко всем» ничего не ставит");
            line.SalePrice.Should().Be(0);

            ReceivingMarkupSettingsStore.SetDefaultPercent(20, persist: false);
            ReceivingPricing.ApplyCommonMarkupToAll(new[] { line }, out _).Should().Be(1);
            line.SalePrice.Should().Be(120);
            return 0;
        });
    }

    [Fact]
    public void Existing_price_is_not_overwritten_by_the_common_markup()
    {
        WithSettings(new ReceivingMarkupSettings { AutoEnabled = true, DefaultPercent = 20, MarkupResetDone = true }, () =>
        {
            var line = Existing("p1", cardSale: 50, cardPurchase: 30);
            line.EnableReceivingPricing();
            line.SetPurchaseText("45"); // закупка выросла: 45 × 1,2 = 54 — но цена из карточки остаётся 50

            line.SalePrice.Should().Be(50);
            ReceivingPricing.ApplyAutoToAll(new[] { line }).Should().Be(0);
            line.SalePrice.Should().Be(50);
            ReceivingPricing.MissingSalePrice(new[] { line }).Should().BeEmpty();
            return 0;
        });
    }

    // ------------------------------------------------------------------ окно «У N позиций нет цены продажи»

    [Theory]
    [InlineData(30, "36", "", 36)]       // введена цена
    [InlineData(30, "", "20", 36)]       // введена наценка
    [InlineData(100, "", "20,5", 121)]   // наценка с запятой, округление вверх
    [InlineData(30, "40", "20", 40)]     // цена важнее наценки
    public void Typed_price_or_markup_resolves_to_the_sale_price(double purchase, string price, string markup, double expected) =>
        WithSettings(new ReceivingMarkupSettings { MarkupResetDone = true }, () =>
        {
            ReceivingPricing.ResolveTypedSale(purchase, price, markup).Should().Be(expected);
            return 0;
        });

    [Fact]
    public void Nothing_typed_or_no_purchase_gives_no_price()
    {
        ReceivingPricing.ResolveTypedSale(30, "", "").Should().BeNull("ничего не введено — «Оставить без цены»");
        ReceivingPricing.ResolveTypedSale(30, null, "abc").Should().BeNull();
        ReceivingPricing.ResolveTypedSale(0, "", "20").Should().BeNull("наценку считать не от чего");
        ReceivingPricing.ResolveTypedSale(0, "15", "").Should().Be(15, "цену продажи можно ввести и без закупки");
    }

    [Fact]
    public void Price_typed_in_the_dialog_is_a_manual_price_and_common_markup_does_not_recalculate_it()
    {
        WithSettings(new ReceivingMarkupSettings { AutoEnabled = true, DefaultPercent = 20, MarkupResetDone = true }, () =>
        {
            var line = Existing("p1", 0, 0);
            line.SetPurchaseText("30");
            line.SetSaleText("40"); // как «Применить» в окне
            line.SaleMode.Should().NotBe(SalePriceMode.Auto, "введённая цена — не расчётная");

            ReceivingPricing.ApplyAutoToAll(new[] { line }).Should().Be(0);
            line.SalePrice.Should().Be(40);
            return 0;
        });
    }

    [Fact]
    public void Validate_can_skip_the_missing_sale_price_when_the_screen_asks_for_it()
    {
        var line = Existing("p1", 0, 0);
        line.PurchasePrice = 100;
        PurchaseReceivingService.Validate(new[] { line }).Should().NotBeEmpty("по умолчанию цена продажи обязательна (массовые загрузки, сканирование)");
        PurchaseReceivingService.Validate(new[] { line }, requireSalePrice: false).Should().BeEmpty("экран спросит цену окном «нет цены продажи»");
    }

    // ------------------------------------------------------------------ что уходит в товар

    [Fact]
    public void Sale_price_is_written_to_the_product_only_for_new_or_changed_lines()
    {
        var noPrice = Existing("a", 0, 0);
        noPrice.PurchasePrice = 100;
        PurchaseReceivingService.ShouldWriteSalePrice(noPrice).Should().BeFalse("«Оставить без цены» — PATCH цены не уходит");

        var untouched = Existing("b", 50, 30);
        PurchaseReceivingService.ShouldWriteSalePrice(untouched).Should().BeFalse("цена существующего товара не менялась в этом приходе");

        var changed = Existing("c", 50, 30);
        changed.SalePrice = 60;
        PurchaseReceivingService.ShouldWriteSalePrice(changed).Should().BeTrue("цену изменили в приходе");

        var newPriced = new ReceivingLineVm { Barcode = "1", ProductName = "Новый", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 30, SalePrice = 40 };
        PurchaseReceivingService.ShouldWriteSalePrice(newPriced).Should().BeTrue();

        var newNoPrice = new ReceivingLineVm { Barcode = "2", ProductName = "Новый без цены", Source = ReceivingSource.Unknown, Quantity = 1, PurchasePrice = 30, SalePrice = 0 };
        PurchaseReceivingService.ShouldWriteSalePrice(newNoPrice).Should().BeFalse();
    }

    [Fact]
    public async Task Posting_without_supplier_sends_no_price_for_lines_left_without_price()
    {
        using var server = new FakeNurCrmServer();
        foreach (var id in new[] { "pa", "pb", "pc" })
        {
            server.Json("GET", $"/api/main/products/{id}/", 200, "{\"id\":\"" + id + "\",\"quantity\":10,\"price\":\"" + (id == "pa" ? "0.00" : "50.00") + "\",\"purchase_price\":\"30.00\"}");
            server.Json("PATCH", $"/api/main/products/{id}/", 200, "{\"id\":\"" + id + "\"}");
        }
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previousApi = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        try
        {
            var leftWithout = Existing("pa", cardSale: 0, cardPurchase: 0); // товар без цены, «Оставить без цены»
            leftWithout.PurchasePrice = 100;
            var untouched = Existing("pb", cardSale: 50, cardPurchase: 30); // цена из карточки не менялась
            var changed = Existing("pc", cardSale: 50, cardPurchase: 30);
            changed.SalePrice = 60; // цену изменили в этом приходе

            var result = await PurchaseReceivingService.Instance.PostAsync(new[] { leftWithout, untouched, changed }, null, false, "test");

            result.Errors.Should().BeEmpty();
            string PatchBody(string id) => server.Server.LogEntries.Last(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Path == $"/api/main/products/{id}/").RequestMessage.Body ?? "";
            using (var doc = JsonDocument.Parse(PatchBody("pa")))
            {
                doc.RootElement.TryGetProperty("price", out _).Should().BeFalse("«Оставить без цены» — цена товара не трогается");
                doc.RootElement.TryGetProperty("markup_percent", out _).Should().BeFalse();
                doc.RootElement.TryGetProperty("quantity", out _).Should().BeTrue("остаток при этом приходуется");
            }
            using (var doc = JsonDocument.Parse(PatchBody("pb")))
                doc.RootElement.TryGetProperty("price", out _).Should().BeFalse("существующая цена, которую не меняли, не перезаписывается");
            using (var doc = JsonDocument.Parse(PatchBody("pc")))
                doc.RootElement.GetProperty("price").GetDouble().Should().Be(60, "изменённая в приходе цена уходит");
        }
        finally
        {
            PosApp.CatalogApi = previousApi;
        }
    }
}
