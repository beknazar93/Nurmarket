#nullable enable

namespace NurMarketKassa.Models;

public sealed class LocalProductRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Price { get; set; }
    public string? Barcode { get; set; }
    public double Stock { get; set; }
    public string Unit { get; set; } = "шт";
    public bool IsFavorite { get; set; }
    public bool MustWeigh { get; set; }
    public string? ImageUrl { get; set; }
    public string? Category { get; set; }
    public string? Brand { get; set; }
    public double PurchasePrice { get; set; }
    /// <summary>Компактный JSON опции поштучной продажи из упаковки (ProductPackageOption) или null.</summary>
    public string? PieceOptionJson { get; set; }
    /// <summary>Короткий PLU-код (поле "plu" в NurCRM) — им весы обычно кодируют товар в весовом штрих-коде.</summary>
    public int? Plu { get; set; }
    /// <summary>Группа быстрого доступа F1-F12 (поле "hotkey_group" в NurCRM) или null.</summary>
    public string? HotkeyGroup { get; set; }
    /// <summary>Комплект/набор из нескольких разных товаров (поле "kind"="bundle" в NurCRM).</summary>
    public bool IsBundle { get; set; }
    /// <summary>Вид товара сайта: product / service / bundle (null — старая запись).</summary>
    public string? Kind { get; set; }
    /// <summary>Компактный JSON состава комплекта (List&lt;BundleComponent&gt;) или null.</summary>
    public string? BundleItemsJson { get; set; }
    /// <summary>Дополнительные штрихкоды — по одному в строке или через запятую (как их вводит
    /// кассир в карточке товара), см. CatalogProductTileVm.AlternateBarcodesRaw.</summary>
    public string? AlternateBarcodesRaw { get; set; }
    /// <summary>Компактный JSON доп. штрихкодов с названием/количеством варианта
    /// (List&lt;AlternateBarcodeVariant&gt;) или null — см. CatalogProductTileVm.AlternateBarcodeVariants.</summary>
    public string? AlternateBarcodeVariantsJson { get; set; }
    /// <summary>Артикул (поле "article" в NurCRM).</summary>
    public string? Article { get; set; }
    /// <summary>Код товара (поле "code" в NurCRM) — отдельное от Article поле; весы Rongta в
    /// раскладке "по коду" кодируют именно его. См. CatalogProductTileVm.ProductCode.</summary>
    public string? ProductCode { get; set; }
}
