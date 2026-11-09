using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Models;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Shop;

/// <summary>2026-11-09 (владелец: «Приёмка: 1. редактировать наименование товара. 2. указывать вначале как на старом — база / база NurCRM / неизвестно / из интернета, значками, с подсказкой»
/// + «не найден — только ПРЕДЛОЖИТЬ найти в интернете, по нажатию»). Модель строки: правка названия (переименование карточки), пустое название, источник → подпись и подсказка на 5 языках;
/// «Найти в интернете»: само не вызывается, по нажатию — один раз, отказ оставляет «Неизвестно». Поставщик поиска — подставной, настоящий интернет и сервер не затрагиваются.</summary>
[Collection("BackgroundReceivingQueue")] // меняет общие статические (язык, PosApp.CatalogApi, поставщик поиска) — без параллельных соседей
public sealed class ReceivingSourceAndNameTests : IDisposable
{
    private readonly Func<string, CancellationToken, Task<ReceivingInternetLookup.Found?>> _oldProvider = ReceivingInternetLookup.Provider;
    private readonly AppLanguage _oldLanguage = UserPreferences.Instance.Language;

    public ReceivingSourceAndNameTests() => UserPreferences.Instance.Language = AppLanguage.Russian; // проверки по-русски; прежний язык вернёт Dispose

    public void Dispose()
    {
        ReceivingInternetLookup.Provider = _oldProvider;
        UserPreferences.Instance.Language = _oldLanguage;
    }

    private static readonly AppLanguage[] AllLanguages = { AppLanguage.Russian, AppLanguage.Kyrgyz, AppLanguage.English, AppLanguage.Turkish, AppLanguage.Uzbek };
    private static readonly ReceivingSource[] AllSources = { ReceivingSource.Warehouse, ReceivingSource.GlobalBase, ReceivingSource.Unknown, ReceivingSource.Internet };

    private static ReceivingLineVm Existing(string name = "Молоко 1 л") => new()
    {
        ProductId = "p1", Barcode = "4600000000011", ProductName = name, OriginalName = name, OriginalUnit = "шт", Unit = "шт",
        Source = ReceivingSource.Warehouse, Quantity = 2, PurchasePrice = 50, SalePrice = 60, OriginalPurchasePrice = 50, OriginalSalePrice = 60,
    };

    private static ReceivingLineVm Unknown(string barcode = "4607000000012") => new() { Barcode = barcode, Source = ReceivingSource.Unknown, Quantity = 1 };

    // ------------------------------------------------------------------ 1. название

    [Fact]
    public void Renaming_a_warehouse_line_marks_it_renamed_and_names_the_old_title_in_the_hint()
    {
        var line = Existing();
        line.NameRenamed.Should().BeFalse("пока название то же — ничего не отмечено");
        line.NameWasHint.Should().BeEmpty();

        line.ProductName = "Молоко 1 л 3,2 %";

        line.NameRenamed.Should().BeTrue();
        line.NameOrUnitChanged.Should().BeTrue("PurchaseReceivingService.AddNameAndUnit отправит новое название в карточку (PATCH name)");
        line.NameWasHint.Should().Contain("Молоко 1 л").And.Contain("переименована");
        line.NameSubText.Should().Contain("название изменено");

        line.ProductName = "Молоко 1 л";
        line.NameRenamed.Should().BeFalse("вернули прежнее — пометка снимается");
    }

    [Fact]
    public void Renaming_raises_property_changes_for_the_view()
    {
        var line = Existing();
        var changed = new List<string?>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        line.ProductName = "Другое";

        changed.Should().Contain(new[] { nameof(ReceivingLineVm.NameRenamed), nameof(ReceivingLineVm.NameWasHint), nameof(ReceivingLineVm.NameSubText) });
    }

    [Fact]
    public void New_product_name_is_just_the_name_not_a_rename()
    {
        var line = Unknown();
        line.ProductName = "Новый товар";
        line.NameRenamed.Should().BeFalse();
        line.NameWasHint.Should().BeEmpty();
        line.NameOrUnitChanged.Should().BeFalse("у нового товара нет карточки для переименования — название уйдёт при создании");
    }

    [Fact]
    public void Empty_name_of_a_warehouse_line_is_restored_and_validation_rejects_it()
    {
        var line = Existing();
        line.ProductName = "   ";

        PurchaseReceivingService.Validate(new[] { line }).Should().Contain(p => p.Contains("не может быть пустым"), "страховка, если окно не вернуло название");

        line.CommitName().Should().BeTrue("пустое название нельзя — вернули прежнее");
        line.ProductName.Should().Be("Молоко 1 л");
        line.NameRenamed.Should().BeFalse();
    }

    [Fact]
    public void Empty_name_of_a_new_line_stays_empty_and_validation_asks_to_type_it()
    {
        var line = Unknown();
        line.CommitName().Should().BeFalse("у нового товара вернуть нечего");
        line.ProductName.Should().BeEmpty();
        PurchaseReceivingService.Validate(new[] { line }, requireSalePrice: false).Should().Contain(p => p.Contains("впишите название нового товара"));
    }

    [Fact]
    public void CommitName_trims_the_typed_name()
    {
        var line = Existing();
        line.ProductName = "  Молоко 2 л  ";
        line.CommitName().Should().BeFalse();
        line.ProductName.Should().Be("Молоко 2 л");
    }

    // ------------------------------------------------------------------ 2. источник: подпись и подсказка на 5 языках

    [Fact]
    public void Every_source_has_a_title_and_a_hint_in_all_five_languages()
    {
        foreach (var language in AllLanguages)
        {
            UserPreferences.Instance.Language = language;
            var titles = AllSources.Select(ReceivingLineVm.SourceTitle).ToList();
            titles.Should().OnlyHaveUniqueItems($"четыре источника различимы по подписи ({language})");
            foreach (var source in AllSources)
            {
                ReceivingLineVm.SourceTitle(source).Should().NotBeNullOrWhiteSpace($"{source} / {language}");
                ReceivingLineVm.SourceHintFor(source).Length.Should().BeGreaterThan(30, $"подсказка — пояснение, не одно слово ({source} / {language})");
                new ReceivingLineVm { Source = source }.SourceAutomationText.Should().Contain(ReceivingLineVm.SourceTitle(source)).And.Contain(ReceivingLineVm.SourceHintFor(source));
            }
        }
    }

    [Fact]
    public void Source_titles_follow_the_owner_wording_in_russian_and_texts_differ_between_languages()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        ReceivingLineVm.SourceTitle(ReceivingSource.Warehouse).Should().Be("База");
        ReceivingLineVm.SourceTitle(ReceivingSource.GlobalBase).Should().Be("База NurCRM");
        ReceivingLineVm.SourceTitle(ReceivingSource.Unknown).Should().Be("Неизвестно");
        ReceivingLineVm.SourceTitle(ReceivingSource.Internet).Should().Be("Из интернета");
        var ru = AllSources.Select(ReceivingLineVm.SourceHintFor).ToList();

        UserPreferences.Instance.Language = AppLanguage.English;
        var en = AllSources.Select(ReceivingLineVm.SourceHintFor).ToList();
        for (var i = 0; i < ru.Count; i++)
            en[i].Should().NotBe(ru[i], "английская подсказка — не русская");
    }

    [Fact]
    public void Source_text_and_hint_follow_a_source_change_of_the_line()
    {
        UserPreferences.Instance.Language = AppLanguage.Russian;
        var line = Unknown();
        var changed = new List<string?>();
        line.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        line.SourceText.Should().Be("Неизвестно");

        line.Source = ReceivingSource.Internet;

        line.SourceText.Should().Be("Из интернета");
        line.SourceHint.Should().Contain("интернета");
        line.IsNew.Should().BeTrue("товар из интернета — по-прежнему новый");
        changed.Should().Contain(new[] { nameof(ReceivingLineVm.SourceText), nameof(ReceivingLineVm.SourceHint), nameof(ReceivingLineVm.CanSearchInternet) });
    }

    [Fact]
    public void Internet_source_survives_the_job_snapshot_and_keeps_pack_sale_available()
    {
        var line = Unknown();
        line.ProductName = "Печенье";
        line.Source = ReceivingSource.Internet;

        var back = JsonSerializer.Deserialize<JobLine>(JsonSerializer.Serialize(JobLine.From(line)))!.ToVm();

        back.Source.Should().Be(ReceivingSource.Internet);
        back.IsNew.Should().BeTrue();
        line.CanPackSale.Should().BeTrue("упаковка с поштучной продажей — для нового товара, откуда бы ни взялось название");
    }

    // ------------------------------------------------------------------ 3. «Найти в интернете»: только по нажатию

    private static (Func<int> Count, Func<string?> LastCode) UseProvider(ReceivingInternetLookup.Found? answer = null, Exception? fail = null)
    {
        var count = 0;
        string? last = null;
        ReceivingInternetLookup.Provider = (code, _) =>
        {
            Interlocked.Increment(ref count);
            last = code;
            if (fail is not null)
                throw fail;
            return Task.FromResult(answer);
        };
        return (() => count, () => last);
    }

    [Fact]
    public void Button_is_offered_only_for_unknown_lines_with_a_real_barcode()
    {
        Unknown().CanSearchInternet.Should().BeTrue();
        Unknown("123").CanSearchInternet.Should().BeFalse("короткий код искать бессмысленно");
        Unknown("").CanSearchInternet.Should().BeFalse();
        Existing().CanSearchInternet.Should().BeFalse("товар склада искать не нужно");
        new ReceivingLineVm { Barcode = "4607000000012", Source = ReceivingSource.GlobalBase }.CanSearchInternet.Should().BeFalse("общая база NurCRM уже дала название");
        new ReceivingLineVm { Barcode = "4607000000012", Source = ReceivingSource.Internet }.CanSearchInternet.Should().BeFalse("уже найдено");
    }

    [Fact]
    public async Task Pressing_the_button_calls_the_search_once_and_marks_the_line_from_the_internet()
    {
        var (count, lastCode) = UseProvider(new ReceivingInternetLookup.Found("Печенье Юбилейное 112 г", "Open Food Facts"));
        var line = Unknown();
        count().Should().Be(0, "до нажатия поиска нет");

        var result = await ReceivingInternetLookup.ApplyAsync(line);

        count().Should().Be(1, "одно нажатие — один запрос");
        lastCode().Should().Be("4607000000012");
        result.Outcome.Should().Be(ReceivingInternetLookup.Outcome.Applied);
        line.ProductName.Should().Be("Печенье Юбилейное 112 г");
        line.Source.Should().Be(ReceivingSource.Internet);
        line.IsNew.Should().BeTrue();
        line.InternetSearching.Should().BeFalse();
        line.CanSearchInternet.Should().BeFalse("после подстановки кнопка не нужна");
        ReceivingInternetLookup.MessageFor(result).Should().Contain("Печенье Юбилейное").And.Contain("Проверьте");
    }

    [Fact]
    public async Task Refusal_leaves_the_line_unknown_and_creates_nothing()
    {
        var (count, _) = UseProvider(answer: null);
        var line = Unknown();

        var result = await ReceivingInternetLookup.ApplyAsync(line);

        count().Should().Be(1);
        result.Outcome.Should().Be(ReceivingInternetLookup.Outcome.NotFound);
        line.Source.Should().Be(ReceivingSource.Unknown, "не нашли — «Неизвестно»");
        line.ProductName.Should().BeEmpty();
        line.CanSearchInternet.Should().BeTrue("можно нажать ещё раз");
        ReceivingInternetLookup.MessageFor(result).Should().Contain("вручную");
    }

    [Fact]
    public async Task Search_failure_keeps_the_line_unknown()
    {
        var (count, _) = UseProvider(fail: new HttpRequestException("нет связи"));
        var line = Unknown();

        var result = await ReceivingInternetLookup.ApplyAsync(line);

        count().Should().Be(1);
        result.Outcome.Should().Be(ReceivingInternetLookup.Outcome.Failed);
        line.Source.Should().Be(ReceivingSource.Unknown);
        line.InternetSearching.Should().BeFalse("флаг «идёт поиск» снят и при сбое");
    }

    [Fact]
    public async Task A_name_typed_by_the_cashier_is_not_replaced()
    {
        UseProvider(new ReceivingInternetLookup.Found("Чужое название", "Open Food Facts"));
        var line = Unknown();
        line.ProductName = "Моё название";

        var result = await ReceivingInternetLookup.ApplyAsync(line);

        result.Outcome.Should().Be(ReceivingInternetLookup.Outcome.NameKept);
        line.ProductName.Should().Be("Моё название");
        line.Source.Should().Be(ReceivingSource.Unknown, "название не из интернета — метка не меняется");
    }

    [Theory]
    [InlineData("warehouse")]
    [InlineData("globalbase")]
    [InlineData("internet")]
    [InlineData("shortcode")]
    public async Task Search_is_not_run_for_lines_that_are_not_unknown_or_have_no_real_barcode(string kind)
    {
        var (count, _) = UseProvider(new ReceivingInternetLookup.Found("Не должно подставиться", "x"));
        var line = kind switch
        {
            "warehouse" => Existing(),
            "globalbase" => new ReceivingLineVm { Barcode = "4607000000012", Source = ReceivingSource.GlobalBase, ProductName = "Из базы" },
            "internet" => new ReceivingLineVm { Barcode = "4607000000012", Source = ReceivingSource.Internet, ProductName = "Уже найдено" },
            _ => Unknown("123"),
        };
        var name = line.ProductName;

        var result = await ReceivingInternetLookup.ApplyAsync(line);

        result.Outcome.Should().Be(ReceivingInternetLookup.Outcome.NotApplicable);
        count().Should().Be(0);
        line.ProductName.Should().Be(name);
    }

    [Fact]
    public async Task Scanning_an_unknown_code_neither_searches_the_internet_nor_creates_a_product()
    {
        var (count, _) = UseProvider(new ReceivingInternetLookup.Found("Не должно подставиться", "x"));
        using var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/products/warehouse-barcode/4607000000099/", 404, "{\"detail\":\"not found\"}");
        server.Json("GET", "/api/main/products/global-barcode/4607000000099/", 404, "{\"detail\":\"not found\"}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        try
        {
            var line = await PurchaseReceivingService.Instance.LookupAsync("4607000000099", 1);

            line.Source.Should().Be(ReceivingSource.Unknown, "нигде не найден — «Неизвестно»");
            line.ProductName.Should().BeEmpty("название само не подставляется");
            count().Should().Be(0, "скан не ходит в интернет — только кнопка");
            server.Server.LogEntries.Should().NotContain(e => e.RequestMessage.Method == "POST" && e.RequestMessage.Path.Contains("create"), "товар без действия кассира не создаётся");
        }
        finally
        {
            PosApp.CatalogApi = previous;
        }
    }

    // ------------------------------------------------------------------ проведение: что уходит на сервер при переименовании

    [Fact]
    public async Task Posting_a_renamed_warehouse_line_patches_the_product_name_and_leaves_an_unchanged_one_alone()
    {
        using var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        server.StubLogin();
        server.StubProfile();
        server.Json("GET", "/api/main/suppliers/receipts/", 200, "{\"count\":0,\"results\":[]}");
        server.Json("POST", "/api/main/suppliers/s1/receipt/", 200, "{\"id\":\"r-1\",\"total_amount\":\"200.00\"}");
        server.Json("GET", "/api/main/products/p1/", 200, "{\"id\":\"p1\",\"name\":\"Молоко 1 л\",\"quantity\":5,\"price\":\"60.00\",\"purchase_price\":\"50.00\"}");
        server.Json("PATCH", "/api/main/products/p1/", 200, "{\"id\":\"p1\",\"name\":\"Молоко 1 л 3,2 %\",\"price\":\"60.00\",\"purchase_price\":\"50.00\"}");
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        try
        {
            var line = Existing();
            line.ProductName = "Молоко 1 л 3,2 %";

            var result = await PurchaseReceivingService.Instance.PostAsync(new[] { line }, new PurchaseReceivingService.Supplier("s1", "Поставщик", null, null), true, "тест");

            result.PostedLines.Should().HaveCount(1, string.Join("; ", result.Errors));
            var patches = server.Server.LogEntries.Where(e => e.RequestMessage.Method == "PATCH" && e.RequestMessage.Path.Contains("/products/p1/")).ToList();
            patches.Should().NotBeEmpty("переименование карточки уходит тем же PATCH товара, что правка в окне товара");
            patches.Any(p => ((JsonObject)JsonNode.Parse(p.RequestMessage.Body!)!)["name"]?.GetValue<string>() == "Молоко 1 л 3,2 %").Should().BeTrue("в теле — новое название");
        }
        finally
        {
            PosApp.CatalogApi = previous;
        }
    }
}
