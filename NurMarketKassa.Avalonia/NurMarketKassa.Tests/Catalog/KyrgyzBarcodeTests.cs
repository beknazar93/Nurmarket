using System.Text.Json;
using FluentAssertions;
using NurMarketKassa.Configuration;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using NurMarketKassa.Tests.Helpers;
using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.Tests.Catalog;

/// <summary>
/// 2026-11-11 (владелец: «внутри кыргызстанские штрихкоды тоже посмотри!»; клиент Оруку: банки огурцов и кукурузы с кодами «470…» в приёмке подставляли чужие товары).
/// Кыргызстанские товарные коды — EAN-13 с префиксом GS1 «470» (4700004710078 «Огурцы 900гр»), редко EAN-8 «47…». Проверяем, что такой код находит ТОЛЬКО товар с точно этим кодом
/// (основным или дополнительным): ни соседа в одну цифру, ни укороченный/удлинённый/«содержащий» код, ни артикул, ни весовую этикетку; мусор сканера (перевод строки, пробелы,
/// управляющий GS) код не меняет; дубль кода выбирает товар с остатком, а не случайный; ответ сервера с другим штрихкодом не принимается.
/// Разбор — docs/Отчеты/«Кыргызстанские штрихкоды (470) — разбор.md». Настоящая база — временная (NURMARKET_DATA_ROOT из test-safe.ps1). Запускать только tools/qa/test-safe.ps1.
/// </summary>
[Collection("Wave5Database")]
public sealed class KyrgyzBarcodeTests : IDisposable
{
    private const string ApiBase = "https://app.nurcrm.kg/";
    private readonly LocalProductRepository _repo = LocalProductRepository.Instance;
    private readonly List<CatalogProductTileVm> _savedCatalog = CatalogCacheService.Products.ToList();

    public KyrgyzBarcodeTests()
    {
        _repo.EnsureSchema();
        _repo.ClearAll();
        CatalogCacheService.Products.Clear();
    }

    public void Dispose()
    {
        _repo.ClearAll();
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(_savedCatalog);
    }

    // ---------------------------------------------------------------- помощники

    /// <summary>13-значный код EAN-13 с верной контрольной цифрой из первых 12 цифр.</summary>
    private static string Ean13(string first12)
    {
        first12.Should().MatchRegex(@"^\d{12}$");
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + ((10 - sum % 10) % 10);
    }

    /// <summary>EAN-8 с верной контрольной цифрой из первых 7 цифр (веса 3/1).</summary>
    private static string Ean8(string first7)
    {
        first7.Should().MatchRegex(@"^\d{7}$");
        var sum = 0;
        for (var i = 0; i < 7; i++)
            sum += (first7[i] - '0') * (i % 2 == 0 ? 3 : 1);
        return first7 + ((10 - sum % 10) % 10);
    }

    // Первые 12 цифр «настоящих по форме» кыргызстанских кодов (470 + код предприятия + номер товара); 4700004710078 и …085 — банки огурцов с видео клиента.
    private static readonly string[] Bodies =
    [
        "470000471007", "470000471008", "470000471001", "470000471002", "470000016022",
        "470000031032", "470000048005", "470004069014", "470296344002", "470215400001",
        "470372103508", "470121206094",
    ];

    private static List<string> KyrgyzCodes() => Bodies.Select(Ean13).ToList();

    private static CatalogProductTileVm Tile(string id, string code, double quantity = 10, string? title = null)
    {
        var tile = CatalogTestHelper.CreateTile(id, title ?? "Товар " + id, code, 50);
        tile.Quantity = quantity;
        return tile;
    }

    private void Seed(params CatalogProductTileVm[] tiles)
    {
        _repo.SyncReplaceAllWithDiff(tiles);
        // каталог в памяти (второй слой поиска ProductCodeLookup.FindInCatalog) — тот же набор
        CatalogCacheService.Products.Clear();
        CatalogCacheService.Products.AddRange(tiles);
    }

    private void SeedKyrgyzCatalog() => Seed(KyrgyzCodes().Select((c, i) => Tile("kg-" + i, c)).ToArray());

    /// <summary>Соседи кода: тот же код с другой последней цифрой товара (контрольная цифра верная), только с неверной контрольной цифрой, перестановка двух цифр. Сами в каталоге не лежат.</summary>
    private static IEnumerable<string> Neighbours(string code, ISet<string> taken)
    {
        var body = code[..12];
        var lastBody = (char)('0' + (body[11] - '0' + 3) % 10);
        var list = new List<string>
        {
            Ean13(body[..11] + lastBody),                                      // другая цифра номера товара, контрольная цифра верная
            body + (char)('0' + (code[12] - '0' + 1) % 10),                    // тот же код, контрольная цифра неверная (скан с ошибкой)
            Ean13(body[..9] + body[10] + body[9] + body[11]),                  // перестановка двух соседних цифр
            Ean13("471" + body[3..]),                                          // другой префикс GS1 (471…)
        };
        return list.Where(n => n != code && !taken.Contains(n)).Distinct();
    }

    // ---------------------------------------------------------------- форма кодов и сверка с настоящими примерами

    [Fact]
    public void Generated_codes_have_valid_check_digit_and_match_the_real_cucumber_codes()
    {
        foreach (var code in KyrgyzCodes())
        {
            code.Should().HaveLength(13).And.StartWith("470");
            BarcodeGenerator.HasValidCheckDigit(code).Should().BeTrue(code);
        }
        Ean13("470000471007").Should().Be("4700004710078", "банка огурцов 900 г (видео клиента)");
        Ean13("470000471008").Should().Be("4700004710085", "банка огурцов 3 л (видео клиента)");
        BarcodeGenerator.HasValidCheckDigit(Ean8("4700953")).Should().BeFalse("EAN-8 этот метод не проверяет (только 13 цифр)");
    }

    [Fact]
    public void Kyrgyz_code_is_not_changed_by_barcode_forms_or_candidates()
    {
        foreach (var code in KyrgyzCodes())
        {
            BarcodeForms.Alternate(code).Should().BeNull("13 цифр не с нуля — второй записи нет");
            // 2026-11-11 (п. 1д): плюс точная запись GTIN-14 с нулём впереди — «укороченных» и «соседних» вариантов по-прежнему нет
            ProductCodeLookup.Candidates(code).Should().Equal(code, "0" + code);
        }

        // 12 цифр → добавляется 0 впереди (UPC-A), 14 цифр с нулём → без него; других «укороченных» вариантов нет
        var c = Ean13(Bodies[0]);
        ProductCodeLookup.Candidates(c[1..]).Should().Equal(c[1..], "0" + c[1..]);
        ProductCodeLookup.Candidates("0" + c).Should().Equal("0" + c, c);
        ProductCodeLookup.Candidates("1" + c).Should().Equal(["1" + c]); // GTIN-14 с индикатором 1 — отдельная упаковка, как есть
    }

    // ---------------------------------------------------------------- только точное совпадение

    [Fact]
    public void Each_kyrgyz_code_finds_exactly_its_own_product()
    {
        SeedKyrgyzCatalog();
        for (var i = 0; i < Bodies.Length; i++)
        {
            var code = Ean13(Bodies[i]);
            ProductCodeLookup.FindExact(code)!.Id.Should().Be("kg-" + i);
            var hit = ProductCodeLookup.FindLocal(code, allowWeightLabel: false);
            hit.Should().NotBeNull();
            hit!.Product.Id.Should().Be("kg-" + i);
            hit.Kind.Should().Be(ProductCodeLookup.HitKind.Barcode);
            _repo.TryGetTileByBarcode(code)!.Id.Should().Be("kg-" + i);
        }
    }

    [Fact]
    public void Neighbour_codes_find_nothing_even_with_the_whole_family_in_the_catalog()
    {
        SeedKyrgyzCatalog();
        var taken = KyrgyzCodes().ToHashSet();
        var checkedCount = 0;
        foreach (var code in taken.ToList())
            foreach (var neighbour in Neighbours(code, taken))
            {
                checkedCount++;
                ProductCodeLookup.FindExact(neighbour).Should().BeNull($"{neighbour} — сосед {code}, но не он");
                ProductCodeLookup.FindLocal(neighbour, allowWeightLabel: false).Should().BeNull($"{neighbour}: чужой товар подставляться не должен");
                ProductCodeLookup.FindLocal(neighbour, allowWeightLabel: true).Should().BeNull($"{neighbour}: и с разбором весовой этикетки");
                _repo.TryGetTileByBarcode(neighbour).Should().BeNull();
            }
        checkedCount.Should().BeGreaterThan(30, "набор соседей не выродился");
    }

    [Fact]
    public void Shortened_extended_and_containing_codes_find_nothing()
    {
        SeedKyrgyzCatalog();
        var code = Ean13(Bodies[0]);
        string[] wrong =
        [
            code[..12],            // без контрольной цифры (12 цифр)
            code[1..],             // без первой цифры (12 цифр)
            code[..11],            // 11 цифр
            code[..8],             // 8 цифр — начало кода
            code[5..],             // хвост кода
            code[3..9],            // середина
            "1" + code,            // 14 цифр, лишняя цифра впереди
            code + "0",            // 14 цифр, лишняя цифра позади
            code + code,           // двойное чтение
            "47",                  // только префикс GS1
            "470",
        ];
        foreach (var w in wrong)
        {
            ProductCodeLookup.FindExact(w).Should().BeNull($"«{w}» — не код товара");
            ProductCodeLookup.FindLocal(w, allowWeightLabel: false).Should().BeNull($"«{w}» — не должен находить чужой товар");
        }
    }

    [Fact]
    public void Gtin14_with_leading_zero_finds_the_ean13_product()
    {
        SeedKyrgyzCatalog();
        var code = Ean13(Bodies[0]);
        ProductCodeLookup.FindLocal("0" + code, allowWeightLabel: false)!.Product.Id.Should().Be("kg-0", "GTIN-14 с нулём — тот же товар");
    }

    // ---------------------------------------------------------------- мусор сканера

    [Theory]
    [InlineData("{0}\r\n")]
    [InlineData("{0}\n")]
    [InlineData("\r{0}")]
    [InlineData(" {0} ")]
    [InlineData("\t{0}\t")]
    [InlineData("\u001d{0}")]
    [InlineData("{0}\u001d")]
    [InlineData("\u0002{0}\u0003")]
    public void Scanner_noise_around_the_code_does_not_change_the_found_product(string template)
    {
        SeedKyrgyzCatalog();
        var code = Ean13(Bodies[3]);
        var raw = string.Format(template, code);
        ProductCodeLookup.Normalize(raw).Should().Be(code);
        ProductCodeLookup.FindLocal(raw, allowWeightLabel: false)!.Product.Id.Should().Be("kg-3");
    }

    [Fact]
    public void Spaces_inside_the_digits_are_removed_like_in_an_invoice()
    {
        SeedKyrgyzCatalog();
        var code = Ean13(Bodies[5]);
        var spaced = $"{code[..3]} {code[3..7]} {code[7..]}";
        ProductCodeLookup.Normalize(spaced).Should().Be(code);
        ProductCodeLookup.FindLocal(spaced, allowWeightLabel: false)!.Product.Id.Should().Be("kg-5");
    }

    [Fact]
    public void Letters_inside_a_code_are_not_silently_turned_into_digits()
    {
        SeedKyrgyzCatalog();
        var code = Ean13(Bodies[0]);
        // сканер-клавиатура в русской раскладке даёт буквы: из «4700004710078» с буквой «О» вместо нуля товар не находится
        var withLetter = code.Replace('0', 'O');
        ProductCodeLookup.FindLocal(withLetter, allowWeightLabel: false).Should().BeNull();
    }

    // ---------------------------------------------------------------- EAN-8

    [Fact]
    public void Ean8_with_470_prefix_is_found_only_exactly()
    {
        var ean8 = Ean8("4700953");
        var other = Ean8("4701806");
        Seed(Tile("e8-1", ean8), Tile("e8-2", other), Tile("kg-0", Ean13(Bodies[0])));

        ProductCodeLookup.FindLocal(ean8, allowWeightLabel: false)!.Product.Id.Should().Be("e8-1");
        ProductCodeLookup.FindLocal(other, allowWeightLabel: false)!.Product.Id.Should().Be("e8-2");
        ProductCodeLookup.Candidates(ean8).Should().Equal([ean8]); // у EAN-8 вариантов с нулями нет
        ProductCodeLookup.FindLocal(ean8[..7], allowWeightLabel: false).Should().BeNull("без контрольной цифры — не товар");
        ProductCodeLookup.FindLocal("00000" + ean8, allowWeightLabel: false).Should().BeNull("EAN-8, дополненный нулями до 13 цифр, касса не разворачивает");
        ProductCodeLookup.FindLocal(ean8[..7] + (char)('0' + (ean8[7] - '0' + 1) % 10), allowWeightLabel: false).Should().BeNull("EAN-8 с неверной контрольной цифрой");
    }

    // ---------------------------------------------------------------- дубли, дополнительные коды, артикул

    [Theory]
    [InlineData(0, 7)]
    [InlineData(7, 0)]
    public void Duplicate_barcode_gives_the_product_with_stock_never_a_third_product(double firstQty, double secondQty)
    {
        var code = Ean13(Bodies[0]);
        Seed(Tile("dup-a", code, firstQty, "Адыгене"), Tile("dup-b", code, secondQty, "Адыгене 1л"), Tile("kg-1", Ean13(Bodies[1])));

        var expected = firstQty > 0 ? "dup-a" : "dup-b";
        ProductCodeLookup.FindExact(code)!.Id.Should().Be(expected, "дубль штрихкода: выигрывает товар с остатком");
        ProductCodeLookup.FindLocal(code, allowWeightLabel: false)!.Product.Id.Should().Be(expected);
    }

    [Fact]
    public void Alternate_kyrgyz_barcode_finds_its_product_and_the_neighbour_does_not()
    {
        var main = Tile("alt-1", "4600000000011");
        var alt = Ean13(Bodies[4]);
        main.AlternateBarcodesRaw = alt + "\n" + Ean13(Bodies[6]);
        Seed(main, Tile("kg-0", Ean13(Bodies[0])));

        ProductCodeLookup.FindLocal(alt, allowWeightLabel: false)!.Product.Id.Should().Be("alt-1");
        ProductCodeLookup.FindLocal(Ean13(Bodies[6]), allowWeightLabel: false)!.Product.Id.Should().Be("alt-1");
        foreach (var neighbour in Neighbours(alt, KyrgyzCodes().ToHashSet()))
            ProductCodeLookup.FindLocal(neighbour, allowWeightLabel: false).Should().BeNull();
    }

    [Fact]
    public void Main_barcode_wins_over_another_products_alternate_barcode_in_memory_catalog()
    {
        var code = Ean13(Bodies[0]);
        var holder = Tile("main", code);
        var squatter = Tile("squat", "4600000000011");
        squatter.AlternateBarcodesRaw = code;
        Seed(squatter, holder);
        // каталог памяти: чужой доп. код стоит РАНЬШЕ основного — основной всё равно в приоритете
        ProductCodeLookup.FindLocal(code, allowWeightLabel: false)!.Product.Id.Should().Be("main");
    }

    [Fact]
    public void Article_and_product_code_match_only_exactly_and_never_by_part_of_a_barcode()
    {
        var code = Ean13(Bodies[0]);
        var shortArticle = Tile("art-1", "4600000000011");
        shortArticle.Article = "470";            // начало кода
        shortArticle.ProductCode = "471007";     // кусок кода
        var longArticle = Tile("art-2", "4600000000028");
        longArticle.Article = code[..12];        // 12 цифр кода без контрольной
        longArticle.ProductCode = code[1..];     // хвост
        Seed(shortArticle, longArticle, Tile("kg-0", code));

        ProductCodeLookup.FindLocal(code, allowWeightLabel: false)!.Product.Id.Should().Be("kg-0");

        // код, которого нет ни в штрихкодах, ни в артикулах как точного значения
        var unknown = Ean13("470000999001");
        ProductCodeLookup.FindLocal(unknown, allowWeightLabel: false).Should().BeNull();
    }

    [Fact]
    public void Exact_article_equal_to_the_scanned_code_is_found_by_article_only_when_no_barcode_matches()
    {
        // Пограничный случай: в карточке без штрихкода в «артикул» вписали целиком кыргызстанский код (импорт). Скан находит этот товар, но ТОЛЬКО по полному совпадению.
        var code = Ean13(Bodies[2]);
        var imported = Tile("imp-1", "4600000000011");
        imported.Article = code;
        Seed(imported);

        var hit = ProductCodeLookup.FindLocal(code, allowWeightLabel: false);
        hit.Should().NotBeNull();
        hit!.Product.Id.Should().Be("imp-1");
        hit.Kind.Should().Be(ProductCodeLookup.HitKind.Article);
        ProductCodeLookup.FindLocal(Neighbours(code, new HashSet<string>()).First(), allowWeightLabel: false).Should().BeNull();
    }

    // ---------------------------------------------------------------- «47…» не весовая этикетка

    [Fact]
    public void Prefix_470_is_never_a_scale_label_whatever_prefixes_are_configured()
    {
        var savedW = WeightBarcodeParser.WeightPrefixes;
        var savedA = WeightBarcodeParser.AmountPrefixes;
        var savedL = WeightBarcodeParser.CustomLayouts;
        try
        {
            var all = Enumerable.Range(20, 10).Select(n => n.ToString()).ToHashSet();
            WeightBarcodeParser.WeightPrefixes = all;
            WeightBarcodeParser.AmountPrefixes = new HashSet<string>();
            foreach (var code in KyrgyzCodes())
            {
                WeightBarcodeParser.TryParse(code, out _).Should().BeFalse(code + ": «47…» не весовой");
                ProductCodeLookup.ScalePrefixConfigured(code).Should().BeFalse("префикс «47» у магазина не может быть настроен как весовой (допускаются 20–29)");
                ProductCodeLookup.FindWeightLabel(code).Should().BeNull();
                WeightBarcodeParser.IsCustomLabelWithBadCheckDigit(code).Should().BeFalse();
            }

            WeightBarcodeParser.WeightPrefixes = new HashSet<string>();
            WeightBarcodeParser.AmountPrefixes = all;
            foreach (var code in KyrgyzCodes())
                WeightBarcodeParser.TryParse(code, out _).Should().BeFalse();
        }
        finally
        {
            WeightBarcodeParser.WeightPrefixes = savedW;
            WeightBarcodeParser.AmountPrefixes = savedA;
            WeightBarcodeParser.CustomLayouts = savedL;
        }
    }

    // ---------------------------------------------------------------- контрольная цифра: что делает касса сейчас

    [Fact]
    public void Regular_code_with_wrong_check_digit_is_simply_unknown_and_gets_no_rescan_hint()
    {
        // Фиксируем текущее поведение (см. отчёт, пункт «Валидация»): «отсканируйте ещё раз» есть только для этикеток весов своей раскладки; обычный код с неверной
        // контрольной цифрой идёт как незнакомый (дальше — сервер → «Добавить товар»).
        SeedKyrgyzCatalog();
        var code = Ean13(Bodies[0]);
        var wrong = code[..12] + (char)('0' + (code[12] - '0' + 1) % 10);
        BarcodeGenerator.HasValidCheckDigit(wrong).Should().BeFalse();
        WeightBarcodeParser.IsCustomLabelWithBadCheckDigit(wrong).Should().BeFalse();
        ProductCodeLookup.FindLocal(wrong, allowWeightLabel: false).Should().BeNull();
    }

    // ---------------------------------------------------------------- генерация

    [Fact]
    public void Generator_never_produces_a_470_code_or_a_scale_prefix()
    {
        for (var i = 0; i < 30000; i++)
        {
            var code = BarcodeGenerator.Generate();
            code.Should().HaveLength(13);
            code[0].Should().NotBe('0').And.NotBe('2');
            code.Should().NotStartWith("470", "470 — GS1 Кыргызстана: сгенерированный код не должен выглядеть как настоящий товарный код республики");
            BarcodeGenerator.HasValidCheckDigit(code).Should().BeTrue();
        }
    }

    // ---------------------------------------------------------------- сервер: чужой штрихкод в ответе не принимается

    private static async Task<CatalogApiService> LoginAsync(FakeNurCrmServer server)
    {
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        return new CatalogApiService(api);
    }

    [Fact]
    public async Task Server_search_that_returns_neighbours_does_not_substitute_a_foreign_product()
    {
        var code = Ean13(Bodies[0]);
        using var server = new FakeNurCrmServer();
        // карточки найденных товаров: чужие основные коды и чужие доп. коды — искомого среди них нет
        server.Json("GET", "/api/main/products/n-1/", 200, "{\"id\":\"n-1\",\"name\":\"Вафли Енсо вес\",\"price\":\"420\",\"barcode\":\"5739000000011\",\"alternate_barcodes\":[{\"barcode\":\"" + Ean13(Bodies[1]) + "\"}],\"is_weight\":true,\"unit\":\"кг\"}");
        server.Json("GET", "/api/main/products/n-2/", 200, "{\"id\":\"n-2\",\"name\":\"Рола Макарон ассорти\",\"price\":\"30\",\"barcode\":\"4243000000019\",\"alternate_barcodes\":[],\"unit\":\"шт\"}");
        var catalog = await LoginAsync(server);

        var results = new List<ProductDto>
        {
            new() { Id = "n-1", Title = "Вафли Енсо вес", Price = 420m, Barcode = "5739000000011" },
            new() { Id = "n-2", Title = "Рола Макарон ассорти", Price = 30m, Barcode = "4243000000019" },
            new() { Id = "n-3", Title = "Огурцы 3л", Price = 200m, Barcode = Ean13(Bodies[1]) },   // сосед в одну цифру
        };
        var resolved = await ProductCodeLookup.ResolveServerResultsAsync(catalog, results, ProductCodeLookup.Candidates(code), ApiBase);

        resolved.Tile.Should().BeNull("сервер вернул только соседей и чужие товары — искомого кода среди них нет");
        resolved.Unavailable.Should().BeFalse();
        _repo.TryGetTileByBarcode(code).Should().BeNull("ничего не записано в базу кассы");
    }

    [Fact]
    public async Task Server_search_accepts_only_the_exact_barcode_among_neighbours()
    {
        var code = Ean13(Bodies[0]);
        using var server = new FakeNurCrmServer();
        server.Json("GET", "/api/main/products/ok-1/", 200, "{\"id\":\"ok-1\",\"name\":\"Огурцы 900гр х6\",\"price\":\"250\",\"quantity\":\"4\",\"barcode\":\"" + code + "\",\"unit\":\"шт\",\"kind\":\"product\"}");
        var catalog = await LoginAsync(server);

        var results = new List<ProductDto>
        {
            new() { Id = "n-3", Title = "Огурцы 3л", Price = 200m, Barcode = Ean13(Bodies[1]) },
            new() { Id = "ok-1", Title = "Огурцы 900гр х6", Price = 250m, Barcode = code },
        };
        var resolved = await ProductCodeLookup.ResolveServerResultsAsync(catalog, results, ProductCodeLookup.Candidates(code), ApiBase);

        resolved.Tile.Should().NotBeNull();
        resolved.Tile!.Id.Should().Be("ok-1", "порядок ответа не важен: выбран товар с точным штрихкодом");
        resolved.Tile.Barcode.Should().Be(code);
    }
}

/// <summary>2026-11-11: приёмка (PurchaseReceivingService.LookupAsync) с кодом «470…»: сервер отвечает соседом или чужим товаром — «Неизвестно», а не чужой товар.</summary>
[Collection("BackgroundReceivingQueue")]
public sealed class KyrgyzBarcodeReceivingTests
{
    private static string Ean13(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (first12[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return first12 + ((10 - sum % 10) % 10);
    }

    private static async Task<(FakeNurCrmServer Server, ICatalogApiService? Previous)> StartAsync()
    {
        var server = new FakeNurCrmServer();
        server.Server.ResetMappings();
        server.StubLogin();
        server.StubProfile();
        var api = new NurMarketApiClient(new AppSettings { ApiBaseUrl = server.Url });
        await api.LoginAsync("cashier@test.kg", "12345678");
        var previous = PosApp.CatalogApi;
        PosApp.CatalogApi = new CatalogApiService(api);
        return (server, previous);
    }

    [Fact]
    public async Task Warehouse_and_global_answers_with_a_neighbour_barcode_are_dropped_for_a_470_code()
    {
        var scanned = Ean13("470000471099");           // кода нет нигде
        var neighbour = Ean13("470000471098");         // сосед в одну цифру
        var (server, previous) = await StartAsync();
        using (server)
        {
            server.Json("GET", $"/api/main/products/warehouse-barcode/{scanned}/", 200,
                "{\"product\":{\"id\":\"w1\",\"name\":\"Вафли Енсо вес\",\"barcode\":\"" + neighbour + "\",\"is_weight\":true,\"unit\":\"кг\",\"quantity\":3,\"price\":\"420\"}}");
            server.Json("GET", $"/api/main/products/global-barcode/{scanned}/", 200,
                "{\"name\":\"Рола Макарон ассорти\",\"barcode\":\"" + neighbour + "\"}");
            try
            {
                var line = await PurchaseReceivingService.Instance.LookupAsync(scanned, 1);

                line.Source.Should().Be(ReceivingSource.Unknown, "оба ответа сервера — про другой штрихкод");
                line.ProductId.Should().BeNull();
                line.ProductName.Should().BeEmpty();
                line.Barcode.Should().Be(scanned);
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }

    [Fact]
    public async Task Bulk_scan_drops_server_answers_about_another_barcode_and_keeps_exact_ones()
    {
        var foreignScan = Ean13("470000471097");     // сервер отвечает про соседа
        var neighbour = Ean13("470000471096");
        var exactScan = Ean13("470000471007");       // сервер отвечает про этот же код
        var (server, previous) = await StartAsync();
        using (server)
        {
            server.Json("GET", $"/api/main/products/warehouse-barcode/{foreignScan}/", 200,
                "{\"product\":{\"id\":\"w1\",\"name\":\"Вафли Енсо вес\",\"barcode\":\"" + neighbour + "\",\"is_weight\":true,\"unit\":\"кг\",\"quantity\":3,\"price\":\"420\"}}");
            server.Json("GET", $"/api/main/products/global-barcode/{foreignScan}/", 200, "{\"name\":\"Рола Макарон ассорти\",\"barcode\":\"" + neighbour + "\"}");
            server.Json("GET", $"/api/main/products/warehouse-barcode/{exactScan}/", 200,
                "{\"product\":{\"id\":\"w2\",\"name\":\"Огурцы 900гр х6\",\"barcode\":\"" + exactScan + "\",\"unit\":\"шт\",\"quantity\":3,\"price\":\"250\"}}");
            try
            {
                using var session = new BulkScanSession { PersistDraft = false };
                await session.ScanAsync(foreignScan);
                await session.ScanAsync(exactScan);
                await session.WhenChecksDoneAsync();

                var foreign = session.Rows.Single(r => r.ScannedCode == foreignScan || r.Line.Barcode == foreignScan);
                foreign.Line.Source.Should().Be(ReceivingSource.Unknown, "ответ склада и общей базы — про другой штрихкод");
                foreign.Line.ProductId.Should().BeNull();
                foreign.Line.ProductName.Should().BeEmpty();

                var exact = session.Rows.Single(r => r.ScannedCode == exactScan || r.Line.Barcode == exactScan);
                exact.Line.Source.Should().Be(ReceivingSource.Warehouse);
                exact.Line.ProductId.Should().Be("w2");
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }

    [Fact]
    public async Task Global_base_answer_with_the_exact_470_code_is_accepted()
    {
        var scanned = Ean13("470000471007");
        var (server, previous) = await StartAsync();
        using (server)
        {
            server.Json("GET", $"/api/main/products/warehouse-barcode/{scanned}/", 404, "{\"detail\":\"not found\"}");
            server.Json("GET", $"/api/main/products/global-barcode/{scanned}/", 200, "{\"name\":\"Огурцы 900гр х6\",\"barcode\":\"" + scanned + "\"}");
            try
            {
                var line = await PurchaseReceivingService.Instance.LookupAsync(scanned, 1);

                line.Source.Should().Be(ReceivingSource.GlobalBase);
                line.ProductName.Should().Be("Огурцы 900гр х6");
                line.Barcode.Should().Be(scanned);
            }
            finally
            {
                PosApp.CatalogApi = previous!;
            }
        }
    }
}
