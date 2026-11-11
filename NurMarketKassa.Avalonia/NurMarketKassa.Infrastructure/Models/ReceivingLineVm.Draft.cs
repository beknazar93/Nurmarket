using NurMarketKassa.Services;

namespace NurMarketKassa.Models;

// 2026-11-11 (О-5 «Неоконченная приёмка пропадает»): снимок строки приёмки для ЧЕРНОВИКА (ReceivingDraft.cs) и обратная сборка строки.
// Строка собирается напрямую из полей (без сеттеров): никакие пересчёты (наценка, упаковки, цена по поставщику) при восстановлении не срабатывают —
// строка становится ровно такой, какой её оставили. Отдельный файл, чтобы не трогать остальной код строки.
public sealed partial class ReceivingLineVm
{
    /// <summary>Снимок всех значений строки (для записи в черновик).</summary>
    public ReceivingDraftLine ToDraft() => new()
    {
        LineNo = _lineNo,
        ProductId = _productId,
        Barcode = _barcode,
        ProductName = _productName,
        Unit = _unit,
        Source = _source,
        StockBefore = StockBefore,
        Quantity = _quantity,
        PurchasePrice = _purchasePrice,
        SalePrice = _salePrice,
        LandedExtraPerUnit = _landedExtraPerUnit,
        OriginalName = OriginalName,
        OriginalUnit = OriginalUnit,
        OriginalPurchasePrice = OriginalPurchasePrice,
        OriginalSalePrice = OriginalSalePrice,
        OriginalMarkupPercent = OriginalMarkupPercent,
        CategoryName = CategoryName,
        IsWeight = _isWeight,
        PackMode = _packMode,
        PackSale = _packSale,
        PackCount = _packCount,
        PiecesPerPack = _piecesPerPack,
        PackPrice = _packPrice,
        PackPriceTyped = _packPriceTyped,
        PackSaleTyped = _packSaleTyped,
        ScaleFromPiece = _scaleFromPiece,
        PiecePurchaseBase = _piecePurchaseBase,
        PieceSaleBase = _pieceSaleBase,
        PackName = string.IsNullOrEmpty(_packName) ? null : _packName,
        SaleMode = _saleMode,
        PurchaseManual = _purchaseManual,
        PurchaseSource = PurchaseSource,
        SaleFromHistory = _saleFromHistory,
        VariantQuantities = VariantQuantities is { Count: > 0 } vq ? new Dictionary<string, double>(vq) : null,
        VariantLabels = VariantLabels is { Count: > 0 } vl ? new Dictionary<string, string>(vl) : null,
    };

    private static double Finite(double v, double max = 1e12) => double.IsFinite(v) && Math.Abs(v) < max ? v : 0;

    /// <summary>Собрать строку из черновика. <paramref name="fresh"/> — свежая карточка товара склада из каталога (остаток и «прежние» цены/название перечитаны);
    /// null — товар не нашёлся (удалён или каталога ещё нет): остаются значения из черновика.</summary>
    public static ReceivingLineVm FromDraft(ReceivingDraftLine d, ReceivingLineVm? fresh = null)
    {
        var vm = new ReceivingLineVm
        {
            StockBefore = Finite(fresh?.StockBefore ?? d.StockBefore),
            OriginalName = fresh?.OriginalName is { Length: > 0 } fn ? fn : d.OriginalName ?? "",
            OriginalUnit = fresh?.OriginalUnit is { Length: > 0 } fu ? fu : d.OriginalUnit ?? "",
            OriginalPurchasePrice = Finite(fresh?.OriginalPurchasePrice ?? d.OriginalPurchasePrice),
            OriginalSalePrice = Finite(fresh?.OriginalSalePrice ?? d.OriginalSalePrice),
            OriginalMarkupPercent = Finite(fresh?.OriginalMarkupPercent ?? d.OriginalMarkupPercent),
        };
        vm._lineNo = Math.Max(0, d.LineNo);
        vm._productId = string.IsNullOrWhiteSpace(d.ProductId) ? null : d.ProductId;
        vm._barcode = (d.Barcode ?? "").Trim();
        vm._productName = d.ProductName ?? "";
        vm._unit = string.IsNullOrWhiteSpace(d.Unit) ? "шт" : d.Unit.Trim();
        vm._source = Enum.IsDefined(d.Source) ? d.Source : ReceivingSource.Unknown;
        vm._quantity = Finite(d.Quantity, 1e9);
        vm._purchasePrice = Finite(d.PurchasePrice, PriceHardMax);
        vm._salePrice = Finite(d.SalePrice, PriceHardMax);
        vm._landedExtraPerUnit = Finite(d.LandedExtraPerUnit, PriceHardMax);
        vm.CategoryName = d.CategoryName;
        vm._isWeight = d.IsWeight;
        vm._packMode = d.PackMode;
        vm._packSale = d.PackMode && d.PackSale;
        vm._packCount = Finite(d.PackCount, 1e9);
        vm._piecesPerPack = d.PiecesPerPack > 0 && d.PiecesPerPack < 1e9 ? d.PiecesPerPack : 0;
        vm._packPrice = Finite(d.PackPrice, PriceHardMax);
        vm._packPriceTyped = d.PackPriceTyped;
        vm._packSaleTyped = d.PackSaleTyped;
        vm._scaleFromPiece = d.ScaleFromPiece;
        vm._piecePurchaseBase = Finite(d.PiecePurchaseBase, PriceHardMax);
        vm._pieceSaleBase = Finite(d.PieceSaleBase, PriceHardMax);
        vm._packName = (d.PackName ?? "").Trim();
        vm._saleMode = Enum.IsDefined(d.SaleMode) ? d.SaleMode : SalePriceMode.None;
        vm._purchaseManual = d.PurchaseManual;
        vm.PurchaseSource = Enum.IsDefined(d.PurchaseSource) ? d.PurchaseSource : PurchaseDefaultSource.None;
        vm._saleFromHistory = d.SaleFromHistory;
        if (d.VariantQuantities is { Count: > 0 })
        {
            vm.VariantQuantities = new Dictionary<string, double>(d.VariantQuantities, StringComparer.OrdinalIgnoreCase);
            vm.VariantLabels = new Dictionary<string, string>(d.VariantLabels ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        }
        return vm;
    }
}
