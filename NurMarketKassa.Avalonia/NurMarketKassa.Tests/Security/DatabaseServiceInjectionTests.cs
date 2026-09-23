using System.IO;
using FluentAssertions;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Tests.Helpers;

namespace NurMarketKassa.Tests.Security;

/// <summary>
/// БЕЗОПАСНОСТЬ (OWASP A03 Injection): SQL-инъекции в локальную SQLite-БД.
/// <para>
/// БЕЗОПАСНОСТЬ ДАННЫХ ПОЛЬЗОВАТЕЛЯ: <see cref="DatabaseService"/> хранит БД по пути
/// <c>AppDomain.CurrentDomain.BaseDirectory/data/pos_local.db</c>, то есть В КАТАЛОГЕ СБОРКИ
/// ТЕСТОВ, а не в профиле пользователя (<c>%APPDATA%</c>/<c>%LOCALAPPDATA%</c>). Первый тест
/// класса это проверяет как предохранитель: если путь когда-нибудь переедет в профиль
/// пользователя, тесты упадут ДО того, как что-либо запишут в реальные данные кассы.
/// </para>
/// <para>
/// Все данные создаются с префиксом <c>test-sec-</c> и удаляются в <see cref="Dispose"/>.
/// Класс включён в общую с каталожными тестами xUnit-коллекцию: таблица <c>Products</c>
/// одна и та же, параллельный прогон затирал бы состояние.
/// </para>
/// </summary>
[Collection("CatalogMemoryIndexTests")]
public sealed class DatabaseServiceInjectionTests : IDisposable
{
    /// <summary>Классическая полезная нагрузка «оборвать строку и выполнить свою команду».</summary>
    private const string DropPayload = "1'; DROP TABLE Products; --";

    private const string OfflineSalesDropPayload = "x'; DROP TABLE OfflineSales; --";

    private readonly LocalProductRepository _repository = LocalProductRepository.Instance;
    private readonly List<string> _createdSaleIds = new();

    public DatabaseServiceInjectionTests()
    {
        OfflineDatabase.EnsureSchema();
        _repository.EnsureSchema();
    }

    public void Dispose()
    {
        if (_createdSaleIds.Count > 0)
            OfflineDatabase.RemoveIds(_createdSaleIds);

        _repository.SyncReplaceAllWithDiff(Array.Empty<CatalogProductTileVm>());
    }

    [Fact]
    public void DatabasePath_StaysInsideBuildOutput_AndNeverInUserProfile()
    {
        var dbPath = Path.GetFullPath(DatabaseService.Instance.DatabasePath);
        var baseDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);

        dbPath.Should().StartWith(
            baseDirectory,
            "тесты обязаны писать только в каталог сборки, иначе они трогают реальную БД кассы");

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            var userFolder = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(userFolder))
                continue;

            var normalized = Path.GetFullPath(userFolder);
            if (baseDirectory.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
                continue; // репозиторий лежит внутри профиля — сравнение теряет смысл

            dbPath.Should().NotStartWith(normalized);
        }
    }

    [Fact]
    public void OfflineSales_StoreSqlPayloadLiterally_AndKeepTableIntact()
    {
        var id = $"test-sec-{Guid.NewGuid():N}-{OfflineSalesDropPayload}";
        _createdSaleIds.Add(id);

        var before = OfflineDatabase.LoadAll().Count;

        OfflineDatabase.Append(new OfflineSaleEntry
        {
            Id = id,
            PaymentMethod = "cash'; DELETE FROM OfflineSales WHERE '1'='1",
            CartId = "1 OR 1=1",
            CartJson = """{"items":[],"note":"Robert'); DROP TABLE Products;--"}""",
            Status = OfflineSaleEntry.PendingSync,
        });

        // Если бы значения конкатенировались в SQL-текст, таблица бы исчезла и вызов упал бы.
        var all = OfflineDatabase.LoadAll();
        all.Should().HaveCount(before + 1);

        var stored = all.Single(x => x.Id == id);
        stored.Id.Should().Be(id, "идентификатор обязан сохраниться литерально, символ ' — обычные данные");
        stored.PaymentMethod.Should().Be("cash'; DELETE FROM OfflineSales WHERE '1'='1");
        stored.CartJson.Should().Contain("Robert'); DROP TABLE Products;--");

        OfflineDatabase.RemoveIds([id]);
        _createdSaleIds.Remove(id);
        OfflineDatabase.LoadAll().Should().HaveCount(before);
    }

    [Fact]
    public void Catalog_StoresSqlPayloadLiterally_AndProductsTableSurvives()
    {
        var injected = CatalogTestHelper.CreateTile(
            id: $"test-sec-{DropPayload}",
            title: $"Товар {DropPayload}",
            barcode: "4600000'; DROP TABLE Products; --",
            price: 123);
        injected.Category = "Категория'; DELETE FROM Products; --";

        var benign = CatalogTestHelper.CreateTile("test-sec-benign", "Обычный товар", "4600000000001", 50);

        _repository.SyncReplaceAllWithDiff([injected, benign]);

        // Таблица цела: запрос вообще выполняется и видит обе строки.
        _repository.CountProducts().Should().Be(2, "DROP TABLE не должен был выполниться");
        _repository.LoadAllTiles().Should().HaveCount(2);

        var byBarcode = _repository.TryGetTileByBarcode("4600000'; DROP TABLE Products; --");
        byBarcode.Should().NotBeNull("штрихкод с кавычкой — это данные, а не код");
        byBarcode!.Id.Should().Be($"test-sec-{DropPayload}");

        var byId = _repository.TryGetTileById($"test-sec-{DropPayload}");
        byId.Should().NotBeNull();
        byId!.Title.Should().Be($"Товар {DropPayload}");

        _repository.GetDistinctCategories().Should().Contain("Категория'; DELETE FROM Products; --");
    }

    [Theory]
    [InlineData("' OR '1'='1")]
    [InlineData("' OR 1=1 --")]
    [InlineData("admin'--")]
    [InlineData("'; DROP TABLE Products; --")]
    [InlineData("%")]
    [InlineData("_")]
    public void CatalogSearch_TreatsInjectionPayloadAsLiteralText(string payload)
    {
        var benign = CatalogTestHelper.CreateTile("test-sec-search", "Совершенно обычный товар", "4600000000002", 10);
        _repository.SyncReplaceAllWithDiff([benign]);

        var results = _repository.SearchFullCatalogText(payload);

        // Никакой payload не имеет права «раскрыть» каталог: он ищется как обычная подстрока.
        results.Should().BeEmpty("поисковый запрос обязан оставаться литералом, включая LIKE-метасимволы % и _");
        _repository.CountProducts().Should().Be(1, "таблица Products обязана остаться нетронутой");
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("admin@example.com'; DELETE FROM Users; --")]
    public void TryGetRememberedUser_DoesNotAllowAuthenticationBypass(string payload)
    {
        var act = () => DatabaseService.Instance.TryGetRememberedUser(payload);

        act.Should().NotThrow();
        act().Should().BeNull("инъекция в e-mail не должна возвращать чужую сохранённую сессию");
    }
}
