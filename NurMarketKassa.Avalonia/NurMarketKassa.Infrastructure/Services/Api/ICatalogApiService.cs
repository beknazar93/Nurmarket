using System.Text.Json;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services.Api;

/// <summary>
/// 2026-11-06 (Б-9): докачка полной загрузки каталога. Раньше обрыв на странице 151 из 155 (журнал 13:34) значил «в следующий раз с первой» — 340 запросов за час.
/// Вызывающий задаёт <see cref="StartPage"/>/<see cref="PageSize"/> (из сохранённого хода), получает <see cref="PageDone"/> после КАЖДОЙ страницы (запись в базу и
/// сохранение хода — его дело) и <see cref="FromStart"/> — шла ли загрузка с первой страницы (только тогда по её итогам можно удалять лишнее).
/// </summary>
public sealed class CatalogFullResume
{
    /// <summary>С какой страницы идти (1 — с начала).</summary>
    public int StartPage { get; set; } = 1;

    /// <summary>Размер страницы прошлой попытки (0 — как обычно). Нужен, чтобы номера страниц совпали; не совпали (сервер отдал меньше) — загрузка начинается с первой.</summary>
    public int PageSize { get; set; }

    /// <summary>Вызывается после каждой отданной страницы: (следующая страница, размер страницы, число принятых товаров). Ошибка внутри обрывает загрузку.</summary>
    public Func<int, int, int, Task>? PageDone { get; set; }

    /// <summary>Итог: true — загрузка прошла все страницы с первой (докачка не использовалась или от неё отказались).</summary>
    public bool FromStart { get; set; } = true;
}

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

    /// <summary>2026-11-02 (Н3-01, память): тот же полный каталог, но страницами — каждая отдаётся в <paramref name="onPage"/> и тут же забывается, весь
    /// список JsonElement не копится. Реализация по умолчанию (стенды, чужие реализации) — старая загрузка целиком и одна «страница». Возвращает число товаров.
    /// 2026-11-06 (Б-9): <paramref name="resume"/> — докачка оборванной загрузки с середины (см. <see cref="CatalogFullResume"/>); null — с первой страницы.</summary>
    async Task<int> ProductsCatalogStreamAsync(int limit, int maxPages, Action<List<JsonElement>> onPage, CancellationToken ct = default, CatalogFullResume? resume = null)
    {
        var all = await ProductsCatalogAsync(limit, maxPages, ct).ConfigureAwait(false);
        onPage(all);
        return all.Count;
    }

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

    /// <summary>2026-11-01 (проверка бэкенда): архив товара — вместо удаления, когда сервер отвечает 409 product_has_history. POST products/{id}/archive/ (признак product_archive);
    /// архивный товар не попадает в обычные списки и в продажи (виден только с ?include_archived=1, которого касса не шлёт), GET products/{id}/ по нему даёт 404.</summary>
    Task ArchiveProductAsync(string productId, CancellationToken ct = default);

    /// <summary>Вернуть товар из архива: POST products/{id}/unarchive/.</summary>
    Task UnarchiveProductAsync(string productId, CancellationToken ct = default);

    /// <summary>2026-11-02 (аудит №2, С3-12): архивные товары компании (GET products/list/?include_archived=1, оставляем только помеченные is_archived). Пустой список — архива нет или сервер не отдаёт.</summary>
    /// 2026-11-03 (аудит №3, Т3-04): <paramref name="onFound"/> вызывается для каждого найденного архивного товара сразу при чтении страницы (из фонового потока) — окно показывает их по мере поиска; отмена — <paramref name="ct"/>.
    Task<List<JsonElement>> ListArchivedProductsAsync(IProgress<(int Loaded, int? Total)>? progress = null, CancellationToken ct = default, Action<JsonElement>? onFound = null) => Task.FromResult(new List<JsonElement>());

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

    /// <summary>2026-11-11 (Умар включил на боевом): атомарный перевод остатка товара в упаковки — POST products/&lt;id&gt;/convert-to-packages/
    /// {quantity_in_package, unit?, package_name?, expected_quantity?, allow_rounding?, idempotency_key} + заголовок Idempotency-Key.
    /// Отказы — ApiException (409 stock_changed / already_has_packages / …, 400 not_divisible / price_overflow); разбор — ReceivingPackConvert.
    /// По умолчанию (другие реализации) — 405 «сервер не умеет»: вызывающий откатывается на прежний PATCH.</summary>
    Task<JsonElement> ConvertToPackagesAsync(string productId, IReadOnlyDictionary<string, object?> body, string idempotencyKey, CancellationToken ct = default) =>
        throw new ApiException("convert-to-packages не поддерживается", 405);

    /// <summary>Поставщики компании: GET clients/?type=suppliers.</summary>
    Task<JsonElement> ListSuppliersAsync(CancellationToken ct = default);

    /// <summary>2026-10-20: новый поставщик — POST clients/ с type=suppliers (так его создаёт форма товара сайта,
    /// «+ Создать поставщика»: тот же справочник клиентов, тип «suppliers»).</summary>
    // 2026-10-20, этап П1: requisites — реквизиты поставщика (email, llc, inn, okpo, score, bik, address), только заполненные.
    Task<JsonElement> CreateSupplierAsync(string fullName, string phone, CancellationToken ct = default, IReadOnlyDictionary<string, string>? requisites = null);

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

    /// <summary>2026-09-30: PATCH /api/main/products/{id}/ {"plu": N} — PLU товара на сайте (проверено на
    /// тестовом аккаунте: 200; занятый номер — 400 «Этот PLU уже используется в вашей компании»,
    /// приходит как ApiException). Остальные поля товара не трогаются.</summary>
    Task SetProductPluAsync(string productId, int plu, CancellationToken ct = default);

    /// <summary>POST /api/users/scales/send-products/ — выгрузка весовых товаров на сетевые
    /// весы Штрих-М (сервер сам обращается к весам по LAN, см. scales-and-plu.md /
    /// ScalesPage.tsx на сайте). plu_start — стартовый PLU при автоназначении (веб всегда шлёт
    /// 1). Возвращает true при успешном ответе сервера.</summary>
    Task<bool> SendProductsToScaleAsync(int pluStart, IReadOnlyList<string> productIds, CancellationToken ct = default);

    /// <summary>GET /api/main/products/scale-export/?format=txp&amp;translit=1 — тот же файл,
    /// что сайт отдаёт на вкладке «Rongta» (/crm/scales) — уже с транслитерацией кириллицы
    /// (Rongta не печатает кириллицу). Возвращает null при ошибке/пустом ответе.</summary>
    Task<byte[]?> DownloadScaleExportAsync(bool translit = true, CancellationToken ct = default);

    /// <summary>2026-10-01, магазин одежды: варианты товара (размер, цвет, своя цена, остаток,
    /// штрихкод) — GET /api/main/products/{id}/variants/.</summary>
    Task<List<ProductVariantDto>> GetProductVariantsAsync(string productId, CancellationToken ct = default);

    /// <summary>Создать вариант (POST) или изменить (PATCH, если Id задан). Ответ — вариант сервера.</summary>
    Task<ProductVariantDto> SaveProductVariantAsync(string productId, ProductVariantDto variant, CancellationToken ct = default);

    /// <summary>DELETE /api/main/products/{id}/variants/{vid}/.</summary>
    Task DeleteProductVariantAsync(string productId, string variantId, CancellationToken ct = default);
}

/// <summary>2026-10-01, магазин одежды: вариант товара NurCRM. Price = null — цена товара.
/// Проверено на тестовом аккаунте: add-item с variant_id даёт строку с этим вариантом и ЕГО ценой
/// (variant, variant_size, variant_color в строке продажи).</summary>
public sealed class ProductVariantDto
{
    public string? Id { get; set; }
    public string Size { get; set; } = "";
    public string Color { get; set; } = "";
    public string? Barcode { get; set; }
    public double Quantity { get; set; }
    public double? Price { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>2026-11-01 (расследование «остаток не меняется»): false — остаток размера в PATCH не шлём (кассир его не менял; продажи других касс не затираем).</summary>
    public bool SendQuantity { get; set; } = true;
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
    /// <summary>2026-11-01 (аудит 10 из 10, С-01, приёмка): false — поле alternate_barcodes (с названиями и количествами, прочитанными при открытии) не шлём: список не менялся.</summary>
    public bool SendAlternateBarcodes { get; set; } = true;
    /// <summary>2026-11-01 (контрольный аудит, С2-03): false — скидку, опт, описание, страну и характеристики (вес/габариты) не шлём: кассир их не менял (карточка могла открыться из кэша без этих полей).</summary>
    public bool SendDiscountPercent { get; set; } = true;
    public bool SendWholesalePrice { get; set; } = true;
    public bool SendDescription { get; set; } = true;
    public bool SendCountry { get; set; } = true;
    public bool SendCharacteristics { get; set; } = true;
    /// <summary>2026-11-01 (контрольный аудит, С2-33): у существующего товара поштучная продажа была и кассир её выключил — шлём пустой packages_input (раньше выключить было нельзя).</summary>
    public bool DisablePieceSale { get; set; }
    /// <summary>2026-11-01 (аудит 10 из 10, С-03): ключ повтора создания товара — уходит заголовком Idempotency-Key (не в тело). Тот же
    /// ключ при повторной попытке (фоновое создание после таймаута), чтобы сервер, поддерживающий заголовок, не завёл второй товар.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>2026-11-06 (класс Б, Б-3): ключи тела запроса (name, unit, is_weight, plu, kind, article, barcode, category_name, brand_name, hotkey_group), которые кассир при правке
    /// существующего товара НЕ менял — PATCH их не шлёт. Раньше они уходили всегда «как открыли»: карточка, открытая из памяти кассы (сервер не ответил), с «шт»/не весовой/без штрихкода
    /// затирала на сервере настоящие значения. null — шлём всё (создание товара, массовые загрузки).</summary>
    public HashSet<string>? UnchangedKeys { get; set; }
    public bool SendPrice { get; set; } = true;
    public bool SendPurchasePrice { get; set; } = true;
    public bool SendMarkupPercent { get; set; } = true;
    public double? WholesalePrice { get; set; }
    /// <summary>2026-10-11, тестировщик (склад.md, 2.1.3): «Минимальный остаток» на вкладке «Упаковка». null — поле не трогаем
    /// (значение с сервера не прочитано), число — пишем в minimum_quantity.</summary>
    public double? MinimumQuantity { get; set; }
    /// <summary>2026-11-13 (сервер sn1311, «Электроника»): track_serials — товар продаётся с серийными номерами. null — поле не трогаем (не электроника / старый сервер).</summary>
    public bool? TrackSerials { get; set; }
    /// <summary>2026-11-13 (сервер sn1311): warranty_months — гарантия в месяцах (null вместе с <see cref="SendWarrantyMonths"/> = убрать гарантию). Пишется только при <see cref="SendWarrantyMonths"/>.</summary>
    public int? WarrantyMonths { get; set; }
    public bool SendWarrantyMonths { get; set; }
    /// <summary>2026-10-20, владелец: поставщик товара — поле «client» товара NurCRM (id поставщика из clients/?type=suppliers;
    /// так его пишет форма товара сайта). null — поле не трогаем (кассир поставщика не менял), "" — убрать поставщика.</summary>
    public string? SupplierId { get; set; }
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
    /// <summary>2026-11-10: название упаковки («Пачка», как подсказка веба); пусто — «упаковка», как раньше.</summary>
    public string? PackageName { get; set; }
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
