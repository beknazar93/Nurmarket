using System.Text.Json;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Tests.Import;

/// <summary>2026-11-13 (Л-10, «Новичок: перенос данных»): готовые профили столбцов для выгрузок 1С:Розница, МойСклад, Poster, Loyverse, Paloma365, Umag,
/// автоопределение источника, перенос клиентов (без долгов в API) и демо-магазин (JSON из Assets). Образцы файлов — строки в тестах (CSV через «;»/«,»).</summary>
[Collection("UserPreferences")] // 2026-11-13: трогает TrainingMode (общее состояние) — идёт вместе с TrainingModeTests
public sealed class ImportSourceProfilesTests
{
    private static ProductFileTable Table(string text)
    {
        var result = ProductFileReader.ReadClipboard(text);
        Assert.True(result.Ok, result.Error);
        return result.Table!;
    }

    private static ProductImportMapping Detect(string text, out ProductFileTable table)
    {
        table = Table(text);
        return ProductImportMapping.AutoDetect(table.Headers, table);
    }

    private static int Col(ProductFileTable t, string header) => t.Headers.ToList().FindIndex(h => h == header);

    [Fact]
    public void OneC_Retail_Export_IsDetected_AndMapped()
    {
        var m = Detect("Номенклатура;Артикул;Код;Вид номенклатуры;Ставка НДС;Единица измерения;Штрихкод;Цена;Конечный остаток\n"
            + "Хлеб белый;A-1;00001;Товар;0%;шт;4600000000011;35;40\n"
            + "Рис;A-2;00002;Товар;0%;кг;4600000000028;105;60\n", out var t);
        Assert.Equal("1c", m.Source?.Id);
        Assert.Equal(Col(t, "Штрихкод"), m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(Col(t, "Номенклатура"), m.ColumnOf(ProductImportField.Name));
        Assert.Equal(Col(t, "Артикул"), m.ColumnOf(ProductImportField.Article));
        Assert.Equal(Col(t, "Конечный остаток"), m.ColumnOf(ProductImportField.Quantity));
        Assert.Equal(Col(t, "Цена"), m.ColumnOf(ProductImportField.Sale));
        Assert.Equal(Col(t, "Единица измерения"), m.ColumnOf(ProductImportField.Unit));
    }

    [Fact]
    public void MoySklad_Export_IsDetected_TwoBarcodes_Prices()
    {
        var m = Detect("Группы;Код;Наименование;Артикул;Внешний код;Единица измерения;Цена: Цена продажи;Закупочная цена;Штрихкод EAN13;Штрихкод EAN8;Остаток\n"
            + "Бакалея;00012;Сахар 1 кг;S-1;abc123;шт;85;70;4600000000059;46000058;50\n", out var t);
        Assert.Equal("moysklad", m.Source?.Id);
        Assert.Equal(Col(t, "Штрихкод EAN13"), m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(Col(t, "Штрихкод EAN8"), m.ColumnOf(ProductImportField.AltBarcodes));
        Assert.Equal(Col(t, "Закупочная цена"), m.ColumnOf(ProductImportField.Purchase));
        Assert.Equal(Col(t, "Цена: Цена продажи"), m.ColumnOf(ProductImportField.Sale));
        Assert.Equal(Col(t, "Группы"), m.ColumnOf(ProductImportField.Category));
        Assert.Equal(Col(t, "Артикул"), m.ColumnOf(ProductImportField.Article));
        Assert.Equal(Col(t, "Остаток"), m.ColumnOf(ProductImportField.Quantity));
    }

    [Fact]
    public void Poster_Export_IsDetected()
    {
        var m = Detect("Название;Категория;Цех;SKU;Штрихкод;Тип;Цена;Весовой продукт;Налог\n"
            + "Картофель;Овощи;Кухня;P1;4600000000196;Товар;40;Да;0\n", out var t);
        Assert.Equal("poster", m.Source?.Id);
        Assert.Equal(Col(t, "Штрихкод"), m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(Col(t, "SKU"), m.ColumnOf(ProductImportField.Article));
        Assert.Equal(Col(t, "Весовой продукт"), m.ColumnOf(ProductImportField.Weight));
        Assert.Equal(Col(t, "Цена"), m.ColumnOf(ProductImportField.Sale));
        Assert.Equal(Col(t, "Название"), m.ColumnOf(ProductImportField.Name));
    }

    [Fact]
    public void Loyverse_Export_IsDetected_StoreSuffixColumns()
    {
        var m = Detect("Handle,REF,Name,Category,Sold by weight,Option 1 name,Option 1 value,Cost,Barcode,Track stock,Price [Main store],In stock [Main store]\n"
            + "apple,A1,Apple,Fruit,Y,,,60,4600000000233,Y,90,50\n", out var t);
        Assert.Equal("loyverse", m.Source?.Id);
        Assert.Equal(Col(t, "Price [Main store]"), m.ColumnOf(ProductImportField.Sale));
        Assert.Equal(Col(t, "In stock [Main store]"), m.ColumnOf(ProductImportField.Quantity));
        Assert.Equal(Col(t, "Cost"), m.ColumnOf(ProductImportField.Purchase));
        Assert.Equal(Col(t, "Sold by weight"), m.ColumnOf(ProductImportField.Weight));
        Assert.Equal(Col(t, "Barcode"), m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(Col(t, "REF"), m.ColumnOf(ProductImportField.Article));
    }

    [Fact]
    public void Paloma365_Export_IsDetected()
    {
        var m = Detect("Внешний ID;Признак группы;Родительская группа;Наименование;Цена;Единица измерения;Штрихкод\n"
            + "17;0;Молочное;Молоко 1 л;90;шт;4600000000028\n", out var t);
        Assert.Equal("paloma365", m.Source?.Id);
        Assert.Equal(Col(t, "Штрихкод"), m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(Col(t, "Родительская группа"), m.ColumnOf(ProductImportField.Category));
        Assert.Equal(Col(t, "Цена"), m.ColumnOf(ProductImportField.Sale));
    }

    [Fact]
    public void Umag_Export_IsDetected()
    {
        var m = Detect("Наименование товара;Штрихкод товара;Артикул;Категория товара;Закупочная цена;Цена продажи;Остаток на складе;Единица измерения\n"
            + "Сыр;4600000000264;U1;Молочное;520;650;10;кг\n", out var t);
        Assert.Equal("umag", m.Source?.Id);
        Assert.Equal(Col(t, "Штрихкод товара"), m.ColumnOf(ProductImportField.Barcode));
        Assert.Equal(Col(t, "Закупочная цена"), m.ColumnOf(ProductImportField.Purchase));
        Assert.Equal(Col(t, "Цена продажи"), m.ColumnOf(ProductImportField.Sale));
        Assert.Equal(Col(t, "Остаток на складе"), m.ColumnOf(ProductImportField.Quantity));
    }

    [Fact]
    public void OurOwnTemplate_AndPlainFiles_HaveNoSource()
    {
        var m = Detect("Штрихкод;Название;Количество;Закупка;Цена продажи\n4600000000011;Хлеб;5;25;35\n", out _);
        Assert.Null(m.Source);
        Assert.True(m.IsComplete);
        var m2 = Detect("Barcode;Name;Price\n4600000000011;Bread;35\n", out _);
        Assert.Null(m2.Source);
    }

    [Fact]
    public void ManualChoice_AppliesProfile_ToUnrecognisedFile()
    {
        var t = Table("Артикул;ШК;Товар;Остаток\nA;4600000000011;Хлеб;5\n");
        var m = ProductImportMapping.AutoDetect(t.Headers, t);
        Assert.Null(m.Source);
        ImportSourceProfiles.Apply(ImportSourceProfiles.ById("umag")!, m, t.Headers, t);
        Assert.Equal("umag", m.Source?.Id);
        Assert.Equal(3, m.ColumnOf(ProductImportField.Quantity));
    }

    [Fact]
    public void Profile_Mapping_BuildsCorrectPlanLines_Loyverse()
    {
        var m = Detect("Handle,REF,Name,Category,Sold by weight,Cost,Barcode,Price [Shop],In stock [Shop]\n"
            + "apple,A1,Apple,Fruit,Y,60,4600000000233,90,50\n"
            + "bread,B1,Bread,Bakery,N,25,4600000000011,35,10\n", out var t);
        var plan = BulkImportPlan.Build(t, m, new BulkImportSettings { UseReceivingMarkup = false }, new List<CatalogProductTileVm>());
        Assert.Null(plan.Problem);
        Assert.Equal(2, plan.Lines.Count);
        var apple = plan.Lines.First(l => l.Barcode == "4600000000233");
        Assert.Equal(90, apple.SalePrice);
        Assert.Equal(60, apple.PurchasePrice);
        Assert.Equal(50, apple.Quantity);
        Assert.True(apple.IsWeight);
        Assert.False(plan.Lines.First(l => l.Barcode == "4600000000011").IsWeight);
    }

    [Fact]
    public void Profile_Mapping_BuildsCorrectPlanLines_MoySklad()
    {
        var m = Detect("Группы;Наименование;Артикул;Единица измерения;Цена: Цена продажи;Закупочная цена;Штрихкод EAN13;Остаток\n"
            + "Бакалея;Сахар 1 кг;S-1;шт;85;70;4600000000059;50\n", out var t);
        var plan = BulkImportPlan.Build(t, m, new BulkImportSettings { UseReceivingMarkup = false }, new List<CatalogProductTileVm>());
        Assert.Null(plan.Problem);
        var line = Assert.Single(plan.Lines);
        Assert.Equal("Сахар 1 кг", line.ProductName);
        Assert.Equal(85, line.SalePrice);
        Assert.Equal(70, line.PurchasePrice);
        Assert.Equal(50, line.Quantity);
    }

    [Fact]
    public void EveryProfile_HowTo_And_Names_AreFilled()
    {
        Assert.Equal(7, ImportSourceProfiles.All.Count); // 2026-11-19: Л-10 добавил седьмой профиль источника
        foreach (var p in ImportSourceProfiles.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.HowToExport));
            Assert.NotEmpty(p.Rules);
            Assert.True(p.MinScore <= p.Signature.Count);
        }
        Assert.Equal(ImportSourceProfiles.All.Count, ImportSourceProfiles.All.Select(p => p.Id).Distinct().Count());
    }

    // ------------------------------------------------------------------ клиенты

    private sealed class FakeClients : IClientsApiService
    {
        public List<(string Name, string Phone, string? Address)> Created { get; } = [];
        public Task<List<JsonElement>> GetClientsAsync(string? search, CancellationToken ct = default) => Task.FromResult(new List<JsonElement>());
        public Task<(List<JsonElement> Items, bool HasNext)> GetClientsPageAsync(int page, string? search, CancellationToken ct = default) => Task.FromResult((new List<JsonElement>(), false));
        public Task<JsonElement> CreateClientAsync(string fullName, string phone, string? email, string? address = null, CancellationToken ct = default)
        {
            if (fullName == "Падение")
                throw new InvalidOperationException("boom");
            Created.Add((fullName, phone, address));
            return Task.FromResult(JsonDocument.Parse("{\"id\":\"1\"}").RootElement.Clone());
        }
        public Task<JsonElement> UpdateClientAsync(string id, string fullName, string phone, string? email, string? address = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteClientAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JsonElement> ResolveQrAsync(string qrText, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void Clients_Plan_FindsColumns_Duplicates_Debts()
    {
        var t = Table("ФИО;Телефон;Адрес;Долг\n"
            + "Айбек;0555 123 456;Бишкек;1500\n"
            + "Бермет;+996 700 111 222;;0\n"
            + "Айбек дубль;0555-123-456;;300\n"
            + "Без телефона;;;100\n"
            + "Уже в базе;0770 000 111;;50\n"
            + "Кривой долг;0555 999 888;;abc\n");
        var plan = ClientsImportPlan.Build(t, null, new HashSet<string> { "770000111" });
        Assert.Null(plan.Problem);
        Assert.Equal(6, plan.Rows.Count);
        Assert.Equal(2, plan.ReadyCount);               // Айбек, Бермет
        Assert.Equal(2, plan.ExistsCount);               // дубль в файле + уже в базе
        Assert.Equal(2, plan.ErrorCount);                // без телефона, кривой долг
        Assert.Equal(1500 + 300 + 100 + 50, plan.DebtTotal);
        Assert.Equal(4, plan.DebtorsCount);
    }

    [Fact]
    public void Clients_Plan_WithoutPhoneColumn_ExplainsProblem()
    {
        var plan = ClientsImportPlan.Build(Table("ФИО;Адрес\nАйбек;Бишкек\n"));
        Assert.NotNull(plan.Problem);
    }

    [Fact]
    public void Clients_LooksLikeClients_OnlyForClientFiles()
    {
        Assert.True(ClientsImportPlan.LooksLikeClients(["ФИО", "Телефон", "Долг"]));
        Assert.False(ClientsImportPlan.LooksLikeClients(["Штрихкод", "Название", "Цена"]));
    }

    [Fact]
    public async Task Clients_Run_CreatesReadyRows_OnlyOnce_NoDebtSent()
    {
        var t = Table("Имя;Телефон;Адрес;Долг\nАйбек;0555123456;Бишкек;1500\nПадение;0555222333;;0\nБермет;0700111222;;0\n");
        var plan = ClientsImportPlan.Build(t);
        var api = new FakeClients();
        var (created, failed) = await ClientsImportPlan.RunAsync(plan, api, null, CancellationToken.None);
        Assert.Equal(2, created);
        Assert.Equal(1, failed);
        Assert.Equal("Бишкек", api.Created[0].Address);
        // повторный запуск того же плана ничего не создаёт заново (заведённые помечены, упавшая строка получила причину)
        var (again, _) = await ClientsImportPlan.RunAsync(plan, api, null, CancellationToken.None);
        Assert.Equal(0, again);
        Assert.Equal(2, api.Created.Count);
    }

    // ------------------------------------------------------------------ демо-магазин

    private static string DemoJson()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "NurMarketKassa.Avalonia", "Assets", "demo", "demo-store.json");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("demo-store.json не найден рядом с репозиторием");
    }

    [Fact]
    public void DemoStore_HasThirtyProducts_AllLanguages_SaneNumbers()
    {
        var json = DemoJson();
        var products = DemoStoreCatalog.Products(json);
        Assert.Equal(30, products.Count);
        Assert.Equal(30, products.Select(p => p.Barcode).Distinct().Count());
        Assert.All(products, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Name));
            Assert.True(p.Sale > p.Purchase && p.Purchase > 0, p.Name);
            Assert.True(p.Quantity > 0, p.Name);
            Assert.Equal(p.Weighed, p.Unit == "кг");
        });
        // в файле у каждого товара названия и категории на всех 5 языках
        using var doc = JsonDocument.Parse(json);
        foreach (var p in doc.RootElement.GetProperty("products").EnumerateArray())
        {
            Assert.Equal(5, p.GetProperty("n").GetArrayLength());
            Assert.Equal(5, p.GetProperty("c").GetArrayLength());
            Assert.All(p.GetProperty("n").EnumerateArray(), x => Assert.False(string.IsNullOrWhiteSpace(x.GetString())));
        }
        Assert.Equal(5, doc.RootElement.GetProperty("title").GetArrayLength());
    }

    [Fact]
    public void DemoStore_Records_AreLocalOnly_WithDemoIds_AndNotVisibleOutsideTraining()
    {
        var json = DemoJson();
        var records = DemoStoreCatalog.ToRecords(json);
        Assert.Equal(30, records.Count);
        Assert.All(records, r => Assert.StartsWith(DemoStoreCatalog.IdPrefix, r.Id));
        // без учебного режима плитки не показываются, даже если набор включён
        Assert.False(TrainingMode.IsActive);
        Assert.False(DemoStoreCatalog.IsVisible);
        Assert.Empty(DemoStoreCatalog.Products("not json"));
        Assert.Empty(DemoStoreCatalog.Products(""));
    }

    [Fact]
    public void DemoStore_Tiles_AreBuilt()
    {
        var tiles = DemoStoreCatalog.BuildTiles(DemoJson());
        Assert.Equal(30, tiles.Count);
        Assert.All(tiles, t => Assert.StartsWith(DemoStoreCatalog.IdPrefix, t.Id));
    }

    [Fact]
    public void DemoStore_ActivatesOnlyWithSource_AndSwitchesOffWithTraining()
    {
        DemoStoreCatalog.Deactivate();
        DemoStoreCatalog.SetSource(DemoJson());
        var changes = 0;
        void OnChanged() => changes++;
        DemoStoreCatalog.Changed += OnChanged;
        try
        {
            DemoStoreCatalog.Activate();
            Assert.True(DemoStoreCatalog.IsActive);
            Assert.False(DemoStoreCatalog.IsVisible); // учебный режим не включён
            Assert.Equal(0, changes);
            TrainingMode.Enter();
            Assert.True(DemoStoreCatalog.IsVisible);
            Assert.Equal(1, changes);
            TrainingMode.Exit();
            Assert.False(DemoStoreCatalog.IsActive);  // выход из учебного режима выключает набор
            Assert.False(DemoStoreCatalog.IsVisible);
            Assert.Equal(2, changes);
        }
        finally
        {
            DemoStoreCatalog.Changed -= OnChanged;
            TrainingMode.Exit();
            DemoStoreCatalog.Deactivate();
        }
    }
}
