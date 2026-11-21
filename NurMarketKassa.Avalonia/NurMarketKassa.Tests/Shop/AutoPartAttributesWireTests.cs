using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-20 (ответ Умара 20.11, А.4 и В.3): attributes товара — PATCH заменяет массив ЦЕЛИКОМ. Касса читает свежую карточку с сервера и накладывает на неё только то, что менял владелец:
/// part_no_norm и чужие ключи (их мог завести сайт или другое устройство) не теряются; значение — строка до 64 или число, флаги 1/0 (не true/false), не больше 30; 400 invalid_attributes показывается понятным текстом.</summary>
[Collection("UserPreferences")]
public sealed class AutoPartAttributesWireTests : IDisposable
{
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;
    private readonly FakeNurCrmServer _fake = new();

    public AutoPartAttributesWireTests() => UserPreferences.Instance.Language = AppLanguage.Russian;

    public void Dispose()
    {
        UserPreferences.Instance.Language = _oldLanguage;
        _fake.Dispose();
    }

    private static AutoPartAttr A(string key, string value, bool number = false) => new() { Key = key, Value = value, IsNumber = number };

    // ───────────── правила значений ─────────────

    [Fact]
    public void Flags_are_one_and_zero_never_true_or_false_and_numbers_go_as_numbers()
    {
        var wire = AutoPartAttrWire.ToWire(new[] { A("warranty_days", "14"), A("electric", "true"), A("no_return", "false"), A("part_no_norm", "W71294"), A("size", "12.5", number: true) });
        var json = JsonSerializer.Serialize(wire);
        json.Should().Contain("{\"key\":\"warranty_days\",\"value\":14}");
        json.Should().Contain("{\"key\":\"electric\",\"value\":1}").And.Contain("{\"key\":\"no_return\",\"value\":0}");
        json.Should().Contain("{\"key\":\"part_no_norm\",\"value\":\"W71294\"}", "строка остаётся строкой");
        json.Should().Contain("{\"key\":\"size\",\"value\":12.5}", "чужое число не превращается в строку");
        json.ToLowerInvariant().Should().NotContain("true").And.NotContain("false");
    }

    [Fact]
    public void Wrong_keys_and_empty_values_are_dropped_long_values_cut_duplicates_collapsed_and_not_more_than_30()
    {
        var input = new List<AutoPartAttr> { A("Хорошо", "x"), A("1abc", "x"), A("ok key", "x"), A(new string('k', 33), "x"), A("empty", "  "), A("long", new string('v', 100)), A("dup", "1"), A("dup", "2") };
        input.AddRange(Enumerable.Range(0, 40).Select(i => A("k" + i, "v")));
        var list = AutoPartAttrWire.Sanitize(input);

        list.Count.Should().Be(30);
        list.Select(a => a.Key).Should().OnlyHaveUniqueItems().And.NotContain(new[] { "Хорошо", "1abc", "ok key", "empty" });
        list.Single(a => a.Key == "long").Value.Length.Should().Be(64);
        list.Single(a => a.Key == "dup").Value.Should().Be("2", "повтор ключа — последнее значение");
        list.Should().OnlyContain(a => AutoPartAttrWire.IsValidKey(a.Key));
    }

    // ───────────── склейка со свежей карточкой ─────────────

    private static List<AutoPartAttr> Server(string json) => AutoPartAttrWire.ReadServer(JsonDocument.Parse(json).RootElement)!;

    [Fact]
    public void Merge_keeps_part_no_norm_and_foreign_keys_from_the_fresh_server_card_and_replaces_only_what_the_dialog_owns()
    {
        var server = Server("""{"attributes":[{"key":"part_no_norm","value":"W71294"},{"key":"color","value":"red"},{"key":"voltage","value":12},{"key":"cross_1","value":"OLD1"},{"key":"cross_2","value":"OLD2"},{"key":"electric","value":1},{"key":"warranty_days","value":14}]}""");
        // окно товара: владелец оставил артикул, заменил кроссы, гарантию не менял; «Other» в окне — устаревшая копия (color=blue)
        var own = new List<AutoPartAttr> { A("color", "blue"), A("part_no_norm", "W71294"), A("cross_1", "NEW1") };

        var merged = AutoPartAttrWire.Merge(own, server);

        merged.Should().Contain(a => a.Key == "part_no_norm" && a.Value == "W71294");
        merged.Single(a => a.Key == "color").Value.Should().Be("red", "свежее значение сервера главнее старой копии окна");
        merged.Single(a => a.Key == "voltage").IsNumber.Should().BeTrue("чужое число остаётся числом");
        merged.Where(a => a.Key.StartsWith("cross_")).Select(a => a.Value).Should().Equal(new[] { "NEW1" }, "cross_2 владелец убрал — удалён, cross_1 заменён");
        merged.Single(a => a.Key == "electric").Value.Should().Be("1", "окно флаг не задавало — значение сервера не трогаем");
        merged.Single(a => a.Key == "warranty_days").Value.Should().Be("14");
    }

    [Fact]
    public void Merge_lets_the_dialog_change_flags_it_set_and_without_server_list_writes_what_the_dialog_has()
    {
        var server = Server("""{"attributes":[{"key":"electric","value":1},{"key":"no_return","value":1}]}""");
        var merged = AutoPartAttrWire.Merge(new[] { A("electric", "0") }, server);
        merged.Single(a => a.Key == "electric").Value.Should().Be("0");
        merged.Single(a => a.Key == "no_return").Value.Should().Be("1");

        AutoPartAttrWire.Merge(new[] { A("part_no_norm", "X1"), A("color", "blue") }, null).Select(a => a.Key).Should().Equal("part_no_norm", "color");
    }

    [Fact]
    public void Server_bool_values_are_read_as_one_and_zero()
    {
        var list = Server("""{"attributes":[{"key":"electric","value":true},{"key":"no_return","value":false}]}""");
        list.Select(a => a.Value).Should().Equal("1", "0");
    }

    // ───────────── PATCH по сети ─────────────

    private ProductEditRequest Request(params AutoPartAttr[] own) => new() { Name = "Фильтр", Unit = "шт", Quantity = 1, Price = 100, PurchasePrice = 50, MarkupPercent = 100, Attributes = own.ToList() };

    private async Task<NurMarketApiClient> LoginAsync()
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = _fake.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return api;
    }

    private JsonNode PatchBody() => JsonNode.Parse(_fake.Server.LogEntries.Single(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Path == "/api/main/products/p1/").RequestMessage.Body!)!;

    [Fact]
    public async Task Patch_sends_the_whole_merged_array_after_reading_the_fresh_card()
    {
        _fake.Json("GET", "/api/main/products/p1/", 200, """{"id":"p1","attributes":[{"key":"part_no_norm","value":"W71294"},{"key":"color","value":"red"},{"key":"cross_1","value":"OLD"}]}""");
        _fake.Json("PATCH", "/api/main/products/p1/", 200, """{"id":"p1"}""");
        var api = await LoginAsync();

        // окно открыли до загрузки карточки: известно только то, что владелец заполнил сам (кросс-номер и «электрика»), чужих ключей в окне нет
        await new CatalogApiService(api).UpdateProductAsync("p1", Request(A("part_no_norm", "W71294"), A("cross_1", "OC983"), A("electric", "1")));

        var attrs = PatchBody()["attributes"]!.AsArray();
        attrs.Select(x => x!["key"]!.GetValue<string>()).Should().Contain(new[] { "part_no_norm", "color", "cross_1", "electric" });
        attrs.Single(x => x!["key"]!.GetValue<string>() == "color")!["value"]!.GetValue<string>().Should().Be("red", "чужой ключ с сервера не потерян");
        attrs.Single(x => x!["key"]!.GetValue<string>() == "cross_1")!["value"]!.GetValue<string>().Should().Be("OC983");
        attrs.Single(x => x!["key"]!.GetValue<string>() == "electric")!["value"]!.GetValue<int>().Should().Be(1, "флаг числом 1, не true");
        _fake.Server.LogEntries.Count(e => e.RequestMessage.Method == "GET" && e.RequestMessage.Path == "/api/main/products/p1/").Should().Be(1);
    }

    [Fact]
    public async Task Patch_is_not_sent_when_the_fresh_card_cannot_be_read_so_the_array_is_never_replaced_blindly()
    {
        _fake.Error("GET", "/api/main/products/p1/", 403, "read_only"); // 403: 5xx дал бы повторы и объявил бы аварию сервера на весь прогон тестов
        _fake.Json("PATCH", "/api/main/products/p1/", 200, """{"id":"p1"}""");
        var api = await LoginAsync();

        var act = () => new CatalogApiService(api).UpdateProductAsync("p1", Request(A("part_no_norm", "W71294")));

        await act.Should().ThrowAsync<ApiException>();
        _fake.Server.LogEntries.Count(e => e.RequestMessage.Method == "PATCH").Should().Be(0);
    }

    [Fact]
    public async Task Patch_without_attributes_in_the_request_does_not_read_the_card_and_is_unchanged()
    {
        _fake.Json("PATCH", "/api/main/products/p1/", 200, """{"id":"p1"}""");
        var api = await LoginAsync();
        var request = Request();
        request.Attributes = null;

        await new CatalogApiService(api).UpdateProductAsync("p1", request);

        _fake.Server.LogEntries.Count(e => e.RequestMessage.Method == "GET" && e.RequestMessage.Path == "/api/main/products/p1/").Should().Be(0);
        PatchBody().AsObject().ContainsKey("attributes").Should().BeFalse();
    }

    [Fact]
    public async Task Server_400_invalid_attributes_gives_a_friendly_text_not_the_raw_answer()
    {
        _fake.Json("GET", "/api/main/products/p1/", 200, """{"id":"p1","attributes":[]}""");
        _fake.Json("PATCH", "/api/main/products/p1/", 400, """{"code":"invalid_attributes","detail":"attributes[2].value: boolean not allowed"}""");
        var api = await LoginAsync();

        var ex = (await FluentActions.Awaiting(() => new CatalogApiService(api).UpdateProductAsync("p1", Request(A("part_no_norm", "W71294"))))
            .Should().ThrowAsync<ApiException>()).Which;

        ex.StatusCode.Should().Be(400);
        ex.Message.Should().Contain("не приняты сервером").And.Contain("не больше 30").And.NotContain("invalid_attributes");
    }

    [Fact]
    public void Invalid_attributes_text_exists_in_all_five_languages()
    {
        var old = UserPreferences.Instance.Language;
        try
        {
            var texts = new[] { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek }.Select(l =>
            {
                UserPreferences.Instance.Language = l;
                return ApiErrorParser.Friendly("invalid_attributes")!;
            }).ToList();
            texts.Should().OnlyHaveUniqueItems().And.OnlyContain(t => t.Length > 40);
        }
        finally
        {
            UserPreferences.Instance.Language = old;
        }
    }
}
