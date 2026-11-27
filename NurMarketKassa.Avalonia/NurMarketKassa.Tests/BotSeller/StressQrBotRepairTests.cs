using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Infrastructure;
using static NurMarketKassa.Tests.Wave1.ScenarioSupport;

namespace NurMarketKassa.Tests.BotSeller;

/// <summary>2026-11-26 (стресс-тест новых функций дня, docs/Отчеты/Стресс-тест QR, бота, торга, ремонта 26.11.2026.md): кривые ответы сервера (пустое тело, массив вместо объекта,
/// null и строки в списке, числа строкой, мусор в суммах, длинные тексты и эмодзи), классификация «точно не ушло / исход неизвестен» для ответа покупателю,
/// проверка правил торга до PUT, сумма в QR до тыйына, «Смена №» не из uuid, ремонт: мусор в суммах не обнуляет квитанцию, испорченный файл сохраняется копией, 1 000 квитанций.</summary>
[Collection("ServerFeatures")]
public sealed class StressBotApiTests : IDisposable
{
    private readonly FakeNurCrmServer _fake = new();

    public void Dispose() => _fake.Dispose();

    private async Task<ServerTelegramBotApi> ApiAsync() => new(await LoggedInAsync(_fake));

    [Fact]
    public async Task Reply_with_empty_body_204_does_not_throw_message_already_sent()
    {
        _fake.Json("POST", "/api/main/telegram-bot/dialogs/5/", 204, "");
        var api = await ApiAsync();

        var r = await api.ReplyToDialogAsync("5", "Доставим завтра 🚚");

        r.Sent.Should().BeFalse("сервер не подтвердил полем sent — но и падения «Не отправлено» нет");
        r.Status.Should().BeEmpty();
    }

    [Fact]
    public async Task Reply_with_array_body_does_not_throw()
    {
        _fake.Json("POST", "/api/main/telegram-bot/dialogs/5/", 200, "[1,2]");
        var api = await ApiAsync();

        var act = async () => await api.ReplyToDialogAsync("5", "Привет");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Dialogs_list_skips_null_and_string_rows_reads_numbers_as_strings_long_text_and_emoji()
    {
        var longText = string.Concat(Enumerable.Repeat("Очень длинный вопрос 😀 ", 400));
        _fake.Json("GET", "/api/main/telegram-bot/dialogs/", 200,
            "{\"count\":\"3\",\"next\":null,\"results\":[null,\"мусор\",42," +
            "{\"chat_id\":770013812,\"name\":\"Айбек 👨‍🔧\",\"messages_count\":\"12\",\"orders_count\":\"x\",\"last_text\":" + JsonSerializer.Serialize(longText) + ",\"status\":\"needs_human\",\"handoff\":\"не объект\"}]}");
        var api = await ApiAsync();

        var page = await api.GetDialogsAsync(null, new DateTime(2026, 11, 1), new DateTime(2026, 11, 26), 1);

        page.Rows.Should().ContainSingle();
        var d = page.Rows[0];
        d.ChatId.Should().Be("770013812", "id числом — тоже id");
        d.MessagesCount.Should().Be(12, "число строкой — число");
        d.OrdersCount.Should().Be(0, "мусор — ноль, не падение");
        d.Name.Should().Contain("👨‍🔧");
        d.LastText.Should().Be(longText);
        d.Handoff.Should().BeNull("handoff не объект — передачи нет");
    }

    [Fact]
    public async Task Dialog_actions_and_test_answer_survive_empty_bodies()
    {
        _fake.Json("POST", "/api/main/telegram-bot/dialogs/9/", 200, "");
        _fake.Json("POST", "/api/main/telegram-bot/test-ai/", 200, "\"строка\"");
        _fake.Json("POST", "/api/main/telegram-bot/bargain/test/", 200, "null");
        var api = await ApiAsync();

        (await api.DialogActionAsync("9", "take")).Changed.Should().BeFalse();
        (await api.TestAsCustomerAsync("Есть утюг?")).Answer.Should().BeEmpty();
        (await api.TestBargainAsync("p1", 100m, false)).Decision.Should().BeEmpty();
    }

    [Fact]
    public async Task Sales_stats_with_wrong_types_gives_zeros_not_exception()
    {
        _fake.Json("GET", "/api/main/telegram-bot/sales-stats/", 200,
            "{\"dialogs\":\"7\",\"orders\":[1],\"funnel\":null,\"handoffs\":{\"by_reason\":{\"human\":\"x\",\"unknown\":3}},\"unknown_questions\":[null,{\"text\":\"Гарантия?\"}],\"bargain\":{\"concessions_total\":\"1250.55\",\"top_products\":[null,5,\"Куртка\"]}}");
        var api = await ApiAsync();

        var s = await api.GetSalesStatsAsync(new DateTime(2026, 11, 1), new DateTime(2026, 11, 26));

        s.Dialogs.Should().Be(7);
        s.OrdersTotal.Should().Be(0m);
        s.HandoffsByReason.Should().ContainKey("unknown").And.NotContainKey("human");
        s.UnknownQuestions.Should().ContainSingle().Which.Text.Should().Be("Гарантия?");
        s.Bargain!.ConcessionsTotal.Should().Be(1250.55m, "копейки не теряются");
        s.Bargain.TopProducts.Should().ContainSingle().Which.Name.Should().Be("Куртка");
    }

    [Fact]
    public async Task Bargain_products_wrong_types_null_money_and_exact_kopecks()
    {
        _fake.Json("GET", "/api/main/telegram-bot/bargain/products/", 200,
            "{\"count\":2,\"next\":null,\"results\":[null,{\"product_id\":\"p1\",\"name\":\"Куртка\",\"price\":\"135000.55\",\"purchase_price\":{\"x\":1},\"min_price\":\"abc\",\"max_discount_percent\":\"7.5\",\"floor\":130000.01,\"allowed\":\"yes\"}]}");
        var api = await ApiAsync();

        var page = await api.GetBargainProductsAsync(null, null, false, 1);

        var p = page.Rows.Should().ContainSingle().Subject;
        p.Price.Should().Be(135000.55m);
        p.PurchasePrice.Should().BeNull("объект вместо числа — «не задана», не 0 сом");
        p.MinPrice.Should().BeNull("мусор — не задана");
        p.MaxDiscountPercent.Should().Be(7.5m);
        p.Floor.Should().Be(130000.01m);
        p.Allowed.Should().BeNull("«yes» — не true/false, значит «как в общих»");
    }

    [Fact]
    public async Task Bargain_put_twice_sends_identical_body_idempotent_and_invariant_decimal()
    {
        _fake.Json("PUT", "/api/main/telegram-bot/bargain/products/p1/", 200, "{\"product_id\":\"p1\",\"price\":\"135000\"}");
        var api = await ApiAsync();
        var rule = new BotBargainRule(true, 128000.5m, 7m);

        await api.SaveBargainProductAsync("p1", rule);
        await api.SaveBargainProductAsync("p1", rule);

        var puts = Requests(_fake, "PUT", "/api/main/telegram-bot/bargain/products/p1/");
        puts.Should().HaveCount(2);
        puts[0].RequestMessage.Body.Should().Be(puts[1].RequestMessage.Body, "PUT — полная замена: повтор (двойное нажатие, повтор после таймаута) кладёт то же самое");
        BodyOf(puts[0])["min_price"]!.GetValue<string>().Should().Be("128000.5", "точка, а не запятая, без потери копеек");
    }

    [Theory]
    [InlineData(400, null, true)]
    [InlineData(403, null, true)]
    [InlineData(429, null, true)]
    [InlineData(408, null, false)]
    [InlineData(500, null, false)]
    [InlineData(503, null, false)]
    [InlineData(504, null, false)]
    [InlineData(502, "{\"code\":\"telegram_error\",\"detail\":\"chat not found\"}", true)]
    [InlineData(502, "{\"detail\":\"Bad gateway\"}", false)]
    public void Reply_failure_is_certain_only_when_server_refused_before_sending(int status, string? body, bool certain)
    {
        var ex = new ApiException("x", status, body is null ? null : JsonDocument.Parse(body).RootElement.Clone());
        BotDialogLimits.ReplyFailedForSure(ex).Should().Be(certain);
    }

    [Fact]
    public void Reply_network_errors_and_timeouts_are_unknown_outcome()
    {
        BotDialogLimits.ReplyFailedForSure(new TaskCanceledException()).Should().BeFalse();
        BotDialogLimits.ReplyFailedForSure(new HttpRequestException("reset")).Should().BeFalse();
        BotDialogLimits.ReplyFailedForSure(new ApiException("не JSON", 502)).Should().BeFalse("повреждённый ответ — сообщение могло уйти");
    }

    [Theory]
    [InlineData("128000", "7", 135000, true)]
    [InlineData("135000", "0", 135000, true)]
    [InlineData("0.01", "50", 135000, true)]
    [InlineData("135000.01", null, 135000, false)]
    [InlineData("0", null, 135000, false)]
    [InlineData("-5", null, 135000, false)]
    [InlineData("100.555", null, 135000, false)]
    [InlineData(null, "50.01", 135000, false)]
    [InlineData(null, "-1", 135000, false)]
    public void Bargain_rule_checked_before_put(string? min, string? disc, double price, bool ok)
    {
        decimal? m = min is null ? null : decimal.Parse(min, System.Globalization.CultureInfo.InvariantCulture);
        decimal? d = disc is null ? null : decimal.Parse(disc, System.Globalization.CultureInfo.InvariantCulture);
        (BotBargainText.CheckRule(m, d, (decimal)price) is null).Should().Be(ok);
    }
}

public sealed class StressQrAndReceiptTests
{
    private static string StaticElqrLink() =>
        "https://app.mbank.kg/qr/#" + ElqrQrBuilder.FinalizePayload(new ElqrQrBuilder.TlvField[]
        {
            new("00", "01"), new("01", "11"), new("32", "0012kg.elqr.test1015123456789012345"),
            new("52", "5999"), new("53", "417"), new("58", "KG"), new("59", "Test Shop"), new("63", "0000"),
        });

    [Theory]
    [InlineData("0.01", "1")]
    [InlineData("1", "100")]
    [InlineData("99.99", "9999")]
    [InlineData("100.125", "10013")]
    [InlineData("250.5", "25050")]
    [InlineData("1000000.99", "100000099")]
    [InlineData("9999999999.99", "999999999999")]
    public void Qr_amount_equals_receipt_total_to_the_tiyin_and_bank_checksum_stays_valid(string total, string tiyin)
    {
        var amount = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture);
        var link = ElqrQrBuilder.BuildWithAmount(StaticElqrLink(), amount)!;

        link.Should().NotBeNull();
        var payload = Uri.UnescapeDataString(link[(link.IndexOf('#') + 1)..]);
        ElqrQrBuilder.Parse(payload).Single(f => f.Tag == ElqrQrBuilder.TagAmount).Value.Should().Be(tiyin);
        ElqrQrBuilder.Inspect(link).Should().Be(ElqrQrBuilder.StaticQrCheck.Ok, "QR с суммой сам проходит проверку контрольной суммы банка");
        ElqrQrBuilder.Parse(payload).Single(f => f.Tag == "59").Value.Should().Be("Test Shop", "реквизиты не тронуты");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0.004")]
    [InlineData("-10")]
    [InlineData("10000000000")]
    [InlineData("79228162514264337593543950335")]
    public void Qr_amount_zero_negative_or_too_big_gives_plain_qr_not_exception(string total)
    {
        var amount = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture);
        var act = () => ElqrQrBuilder.BuildWithAmount(StaticElqrLink(), amount);
        act.Should().NotThrow();
        act().Should().BeNull();
    }

    [Fact]
    public void Qr_garbage_static_input_never_throws()
    {
        foreach (var junk in new[] { "%%%", "#", "https://x/#%E0%A4%A", "0002", "00020101021163", new string('9', 5000), "😀😀😀" })
        {
            var act = () => (ElqrQrBuilder.BuildWithAmount(junk, 10m), ElqrQrBuilder.Inspect(junk));
            act.Should().NotThrow(junk);
        }
    }

    [Theory]
    [InlineData("{\"shift_no\":12}", "12")]
    [InlineData("{\"shift_no\":\"7\"}", "7")]
    [InlineData("{\"shift_no\":0}", "")]
    [InlineData("{\"shift_no\":\"0\"}", "")]
    [InlineData("{\"shift_no\":\"3f2b1c9e-6c1d-4f33-9d55-0a1b2c3d4e5f\"}", "")]
    [InlineData("{\"shift_no\":null,\"shift\":\"3f2b1c9e-6c1d-4f33-9d55-0a1b2c3d4e5f\"}", "")]
    [InlineData("{\"shift\":{\"number\":\"15\"}}", "15")]
    [InlineData("{\"shift\":{\"number\":\"abc\"}}", "")]
    [InlineData("{\"shift_number\":\"-\"}", "")]
    [InlineData("[]", "")]
    public void Shift_number_is_short_digits_never_uuid(string json, string expected) =>
        SaleReceiptCardData.ShiftNo(JsonDocument.Parse(json).RootElement).Should().Be(expected);

    [Fact]
    public async Task Offline_receipt_without_share_url_prints_without_qr()
    {
        var url = await ReceiptShareLink.EnsureAsync(null, null, null);
        url.Should().BeNull();
        ReceiptShareLink.Attach("ЧЕК №1\nИТОГО 100", url, enabled: true).Should().Be("ЧЕК №1\nИТОГО 100", "нет ссылки — текст без служебной строки, пустой QR не печатается");
        ReceiptShareLink.Attach("ЧЕК", "http://insecure/r/1", enabled: true).Should().Be("ЧЕК", "не https — не печатаем");
        ReceiptShareLink.Attach("ЧЕК", "https://app.nurcrm.kg/r/" + new string('a', 400), enabled: true).Should().Be("ЧЕК", "слишком длинная ссылка — не печатаем");
        ReceiptShareLink.FromSale(JsonDocument.Parse("{\"share_url\":123}").RootElement).Should().BeNull();
        ReceiptShareLink.FromSale(JsonDocument.Parse("{\"share_url\":\"\"}").RootElement).Should().BeNull();
    }
}

public sealed class StressRepairTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nurmarket-stress-repair-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly DateTime _now = new(2026, 11, 26, 12, 0, 0);

    public StressRepairTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* временная папка */ }
    }

    private RepairOrderStore Store() => new(Path.Combine(_dir, RepairOrderStore.FileName), () => _now);

    private static JsonElement J(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Garbage_money_from_server_does_not_zero_the_receipt()
    {
        var o = new RepairOrder { Estimate = 1500.50m, FinalPrice = 2000.75m };
        var row = RepairServerRow.Parse(J("{\"id\":\"s1\",\"estimate\":\"abc\",\"final_price\":{\"v\":1},\"device\":\"Телефон 📱\",\"updated_at\":\"2026-11-26T10:00:00Z\"}"))!;

        row.Apply(o, newOrder: false);

        o.Estimate.Should().Be(1500.50m, "мусор в сумме — поле пропускается, а не 0 сом");
        o.FinalPrice.Should().Be(2000.75m);
        o.Device.Should().Be("Телефон 📱");
    }

    [Fact]
    public void Server_money_with_spaces_commas_and_nbsp_keeps_kopecks()
    {
        var o = new RepairOrder();
        RepairServerRow.Parse(J("{\"id\":\"s1\",\"estimate\":\"1 500,55\",\"final_price\":\"2 000.05\"}"))!.Apply(o, newOrder: true);
        o.Estimate.Should().Be(1500.55m);
        o.FinalPrice.Should().Be(2000.05m);
    }

    [Fact]
    public void Unreadable_file_is_backed_up_before_it_is_overwritten()
    {
        var path = Path.Combine(_dir, RepairOrderStore.FileName);
        File.WriteAllText(path, "{\"Orders\":[{\"Number\":\"не число\"}]}");

        Store().Create(new RepairOrder { ClientName = "А", ClientPhone = "0555", Device = "Ноутбук" }, "К");

        Directory.GetFiles(_dir, RepairOrderStore.FileName + ".bad-*").Should().ContainSingle("испорченный файл не теряется молча");
        Store().All().Should().ContainSingle();
    }

    [Fact]
    public void Null_orders_in_file_do_not_crash()
    {
        File.WriteAllText(Path.Combine(_dir, RepairOrderStore.FileName), "{\"Orders\":null}");
        Store().All().Should().BeEmpty();
        Store().Create(new RepairOrder { ClientName = "А", ClientPhone = "0555", Device = "Ноутбук" }, "К").Number.Should().Be(1);
    }

    [Fact]
    public void Thousand_receipts_merge_search_and_update_fast()
    {
        var store = Store();
        var rows = Enumerable.Range(1, 1000).Select(i => RepairServerRow.Parse(J(
            $"{{\"id\":\"s{i}\",\"code\":\"Р-{i:0000}\",\"status\":\"accepted\",\"device\":\"Телефон {i} 📱\",\"customer_name\":\"Клиент {i}\",\"customer_phone\":\"0555{i:000000}\",\"estimate\":\"{i}.50\",\"updated_at\":\"2026-11-26T10:{i % 60:00}:00Z\"}}"))!).ToList();

        var sw = Stopwatch.StartNew();
        store.MergeServerRows(rows, "2026-11-26T10:59:00Z").Should().Be(1000);
        var mergeMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var all = store.All();
        var found = RepairOrderStore.Filter(all, "Клиент 777", null).ToList();
        var searchMs = sw.ElapsedMilliseconds;

        sw.Restart();
        var updates = rows.Take(200).Select(r => RepairServerRow.Parse(J(r.Raw.GetRawText().Replace("\"accepted\"", "\"in_work\"").Replace("2026-11-26T10:", "2026-11-26T11:")))!).ToList();
        store.MergeServerRows(updates, "2026-11-26T11:59:00Z").Should().Be(200);
        var updateMs = sw.ElapsedMilliseconds;

        all.Should().HaveCount(1000);
        all.Select(o => o.Number).Distinct().Should().HaveCount(1000, "без дублей номеров");
        found.Should().ContainSingle().Which.Estimate.Should().Be(777.50m);
        store.All().Count(o => o.Status == RepairStatus.InWork).Should().Be(200);
        mergeMs.Should().BeLessThan(5000, $"слияние 1 000 строк: {mergeMs} мс");
        searchMs.Should().BeLessThan(1500, $"чтение и поиск по 1 000 квитанций: {searchMs} мс");
        updateMs.Should().BeLessThan(3000, $"обновление 200 из 1 000: {updateMs} мс");
    }

    [Fact]
    public void Merge_of_same_page_twice_is_idempotent()
    {
        var store = Store();
        var rows = new[] { RepairServerRow.Parse(J("{\"id\":\"s1\",\"code\":\"Р-0001\",\"status\":\"ready\",\"final_price\":\"999.99\",\"updated_at\":\"2026-11-26T10:00:00Z\"}"))! };
        store.MergeServerRows(rows, "2026-11-26T10:00:00Z").Should().Be(1);
        store.MergeServerRows(rows, "2026-11-26T10:00:00Z").Should().Be(0, "та же страница второй раз (повтор после таймаута) ничего не меняет");
        store.All().Should().ContainSingle().Which.FinalPrice.Should().Be(999.99m);
    }
}
