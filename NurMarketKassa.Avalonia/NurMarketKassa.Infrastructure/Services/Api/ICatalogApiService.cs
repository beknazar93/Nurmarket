using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// Доменный сервис каталога: товары, остатки, версии каталога и синхронизация.
/// </summary>
public interface ICatalogApiService
{
    /// <summary>GET список товаров агента с остатками (для склада).</summary>
    Task<List<JsonElement>> GetAgentProductsAsync(CancellationToken ct = default);

    /// <summary>Синхронизация статуса «избранный» с сайтом.</summary>
    Task<bool> SetProductFavoriteAsync(string productId, bool isFavorite, CancellationToken ct = default);

    /// <summary>Поиск товаров по названию (быстрый, через потоковый парсинг).</summary>
    Task<List<ProductDto>> ProductsSearchAsync(string query, int limit = 40, CancellationToken ct = default);

    /// <summary>Лёгкая проверка версии каталога без полной загрузки SKU.</summary>
    Task<CatalogVersionInfo?> ProductsCatalogVersionAsync(CancellationToken ct = default);

    /// <summary>Полный каталог с пагинацией (все SKU, до limit).</summary>
    Task<List<JsonElement>> ProductsCatalogAsync(int limit, int maxPages, CancellationToken ct = default);

    /// <summary>Карточка товара с картинками (как products_detail).</summary>
    Task<JsonElement?> ProductsDetailAsync(string productId, CancellationToken ct = default);

    /// <summary>Обновление остатка товара на сервере (перебор типовых путей/полей).</summary>
    Task<bool> TrySetProductStockAsync(string productId, double quantity, CancellationToken ct = default);

    /// <summary>Загрузка фото товара (POST /api/main/products/{id}/images/). Возвращает URL нового фото.</summary>
    Task<string?> UploadProductImageAsync(string productId, string localFilePath, bool isPrimary, CancellationToken ct = default);

    /// <summary>Создание нового товара (POST /api/main/products/). Возвращает созданную карточку.</summary>
    Task<JsonElement> CreateProductAsync(ProductEditRequest request, CancellationToken ct = default);

    /// <summary>Обновление существующего товара (PATCH /api/main/products/{id}/).</summary>
    Task<JsonElement> UpdateProductAsync(string productId, ProductEditRequest request, CancellationToken ct = default);

    /// <summary>Удаление товара (DELETE /api/main/products/{id}/, 2026-09-07 — кнопка "Удалить" в
    /// Складе). true — удалён; false — ни один из известных путей не принял DELETE (404/405).
    /// Остальные ошибки API пробрасываются как ApiException.</summary>
    Task<bool> DeleteProductAsync(string productId, CancellationToken ct = default);

    /// <summary>Приёмка, 2026-09-24 — те же запросы, что шлёт «Массовое сканирование» сайта
    /// (подсмотрены в его сетевых запросах).
    /// Товар своего склада по штрихкоду: GET products/warehouse-barcode/{код}/ — объект товара
    /// целиком (остаток, цены, единица) или null, если на складе такого нет (404).</summary>
    Task<JsonElement?> FindWarehouseProductByBarcodeAsync(string barcode, CancellationToken ct = default);

    /// <summary>Товар общей базы CRM по штрихкоду: GET products/global-barcode/{код}/ —
    /// {id, name, barcode} или null (404: такого штрихкода в общей базе нет).</summary>
    Task<JsonElement?> FindGlobalProductByBarcodeAsync(string barcode, CancellationToken ct = default);

    /// <summary>Завести товар из общей базы на свой склад: POST products/create-by-barcode/
    /// {barcode, name, price}. Для штрихкода, которого нет в общей базе, сервер отвечает 404 —
    /// такой товар создаётся обычной карточкой (<see cref="CreateProductAsync"/>).</summary>
    Task<JsonElement> CreateProductFromGlobalBarcodeAsync(string barcode, string name, double price, CancellationToken ct = default);

    /// <summary>PATCH только перечисленных полей товара. Общий <see cref="UpdateProductAsync"/>
    /// отправляет карточку целиком и перетёр бы на сервере всё, чего касса не знает.</summary>
    Task<JsonElement> PatchProductFieldsAsync(string productId, IReadOnlyDictionary<string, object?> fields, CancellationToken ct = default);

    /// <summary>Поставщики компании: GET clients/?type=suppliers.</summary>
    Task<JsonElement> ListSuppliersAsync(CancellationToken ct = default);

    /// <summary>Приход от поставщика: POST suppliers/{id}/receipt/ — документ «Закупки» сайта.
    /// Остаток товаров прибавляет сам сервер.</summary>
    Task<JsonElement> CreateSupplierReceiptAsync(string supplierId, object body, CancellationToken ct = default);

    /// <summary>Страница приходов от поставщиков: GET suppliers/receipts/?page=&amp;limit= (строки
    /// приходят сразу внутри документа).</summary>
    Task<JsonElement> ListSupplierReceiptsAsync(int page, int limit, CancellationToken ct = default);

    /// <summary>Справочники категорий/брендов компании (GET /api/main/categories/ и /api/main/brands/,
    /// 2026-09-07 — выпадающие списки в карточке товара, как на сайте). Все страницы, только имена
    /// (карточка отправляет category_name/brand_name, а не id), отсортированы по алфавиту.</summary>
    Task<List<string>> GetCategoryNamesAsync(CancellationToken ct = default);
    Task<List<string>> GetBrandNamesAsync(CancellationToken ct = default);

    /// <summary>Создание категории/бренда по имени (POST тех же эндпоинтов, тело {"name": ...} —
    /// как кнопки "+ Создать категорию/бренд" на сайте). Ошибки API пробрасываются как ApiException.</summary>
    Task CreateCategoryAsync(string name, CancellationToken ct = default);
    Task CreateBrandAsync(string name, CancellationToken ct = default);

    /// <summary>Число весовых товаров на сервере (GET /api/main/products/list/?is_weight=true,
    /// поле "count" ответа) — для автогенерации PLU нового весового товара (см. buildProductPayload
    /// и useAddProductBootstrap в исходниках сайта: plu = weightProductsCount + 1).</summary>
    Task<int> GetWeightProductCountAsync(CancellationToken ct = default);

    /// <summary>POST /api/users/scales/send-products/ — выгрузка весовых товаров на сетевые
    /// весы Штрих-М (сервер сам обращается к весам по LAN, см. scales-and-plu.md /
    /// ScalesPage.tsx на сайте). plu_start — стартовый PLU при автоназначении (веб всегда шлёт
    /// 1). Возвращает true при успешном ответе сервера.</summary>
    Task<bool> SendProductsToScaleAsync(int pluStart, IReadOnlyList<string> productIds, CancellationToken ct = default);

    /// <summary>GET /api/main/products/scale-export/?format=txp&amp;translit=1 — тот же файл,
    /// что сайт отдаёт на вкладке «Rongta» (/crm/scales) — уже с транслитерацией кириллицы
    /// (Rongta не печатает кириллицу). Возвращает null при ошибке/пустом ответе.</summary>
    Task<byte[]?> DownloadScaleExportAsync(bool translit = true, CancellationToken ct = default);
}

/// <summary>Поля формы добавления/редактирования товара — соответствуют полям, которые
/// шлёт веб-версия NurCRM (см. buildProductPayload в исходниках сайта): category_name/
/// brand_name (не id), article, barcode, alternate_barcodes, unit, is_weight, quantity
/// (число), purchase_price/markup_percent/price (строки), hotkey_group (ЗАГЛАВНЫЕ или null),
/// kind="product".</summary>
public sealed class ProductEditRequest
{
    public string Name { get; set; } = "";
    public string? Article { get; set; }
    public string? Barcode { get; set; }
    public string? AlternateBarcodes { get; set; }
    /// <summary>Названия/количества уже известных доп. штрихкодов (пришли при загрузке карточки
    /// с сервера, см. CatalogProductTileVm.AlternateBarcodeVariants) — используются, чтобы при
    /// сохранении не стирать «Название»/«Кол-во в упаковке», заданные на сайте, для штрихкодов,
    /// которые кассир не трогал в этом многострочном поле (2026-09-21). Новые/вручную вписанные
    /// в кассе штрихкоды не имеют названия/количества — форма редактирования не даёт их ввести.</summary>
    public List<AlternateBarcodeVariant>? KnownAlternateBarcodeVariants { get; set; }
    public string? CategoryName { get; set; }
    public string? BrandName { get; set; }
    public string Unit { get; set; } = "шт";
    public bool IsWeight { get; set; }
    public double Quantity { get; set; }
    public double? PurchasePrice { get; set; }
    public double? MarkupPercent { get; set; }
    public double? Price { get; set; }

    /// <summary>Какие числовые поля вообще отправлять серверу. По умолчанию все true — обычная
    /// форма редактирования товара всегда знает и остаток, и цены, и должна их записывать.
    ///
    /// Нужно это массовому импорту CSV. Тело запроса собирается из абсолютных значений
    /// («стало столько»), а не из приращений, и null в цене превращается в «0». Поэтому прайс
    /// поставщика из колонок Название;Штрихкод;Цена, загруженный по существующим товарам,
    /// обнулял у них ОСТАТОК, а файл без колонки «Цена» — ещё и цену, после чего товар
    /// пробивался на кассе за 0 сом. Отличить «в файле не было такой колонки» от «в файле
    /// стоит ноль» по самому значению невозможно — отсюда отдельные признаки.</summary>
    public bool SendQuantity { get; set; } = true;
    public bool SendPrice { get; set; } = true;
    public bool SendPurchasePrice { get; set; } = true;
    public bool SendMarkupPercent { get; set; } = true;
    public double? WholesalePrice { get; set; }
    public double? DiscountPercent { get; set; }
    public string? Description { get; set; }
    public string? HotkeyGroup { get; set; }
    /// <summary>PLU-код для весового товара — целое число или null (весы кодируют товар им же).</summary>
    public int? Plu { get; set; }
    public string? Country { get; set; }
    public double? HeightCm { get; set; }
    public double? WidthCm { get; set; }
    public double? DepthCm { get; set; }
    public double? WeightKg { get; set; }
    /// <summary>Поштучная продажа из упаковки: одна строка "упаковки" с количеством и ценой
    /// за штуку — только если EnablePieceSale=true в форме (packages_input в API).</summary>
    public bool EnablePieceSale { get; set; }
    public double? PackageQuantity { get; set; }
    public double? PackagePiecePrice { get; set; }
    /// <summary>"product" (обычный товар с остатком), "service" (без остатка) или "bundle"
    /// (комплект из нескольких товаров, см. BundleItems) — поле "kind" в NurCRM.</summary>
    public string Kind { get; set; } = "product";
    /// <summary>Состав комплекта — только когда Kind="bundle".</summary>
    public List<BundleComponent>? BundleItems { get; set; }
    /// <summary>true только при создании нового товара — тогда в тело добавляются
    /// пустые promotion_rules_input/stock, как это делает веб-форма (иначе DRF может требовать
    /// эти поля). При редактировании их не шлём, чтобы не затереть существующие акции, для
    /// которых в этой форме пока нет UI.</summary>
    public bool IsNew { get; set; }
}
