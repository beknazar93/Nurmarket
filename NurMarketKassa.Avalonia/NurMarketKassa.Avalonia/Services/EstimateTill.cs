using NurMarketKassa.AvaloniaHost.Views.MainKassir;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-19 («Смета», ТЗ 5): что окно сметы может сделать с кассой — положить товар в чек и добавить строку «Доп. услуга». В программе владельца кассы нет — <see cref="EstimateTill.Resolve"/>
/// отдаёт null, и «В чек» там не показывается. Деньги не двигаются: чек оплачивается обычным путём кассы. Тест подставляет свою реализацию.</summary>
public interface IEstimateTill
{
    /// <summary>Товар в чек по id каталога и количеству; нет остатка — решает существующее правило продажи без остатка (окно кассы). false — товар не попал в чек (отказ кассира, нет смены).</summary>
    Task<bool> AddProductAsync(string productId, decimal quantity);

    /// <summary>Своя позиция сметы (работа, доставка) — строка «Доп. услуга» на всю сумму позиции.</summary>
    bool AddCustomLine(string name, decimal amount);
}

public static class EstimateTill
{
    /// <summary>Только для проверок: подмена кассы.</summary>
    public static IEstimateTill? Override { get; set; }

    public static IEstimateTill? Resolve()
    {
        if (Override is not null)
            return Override;
        try
        {
            return App.GetRequiredService<MainWindowHostBridge>().Window is { } w ? new Till(w) : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed class Till(MainWindow window) : IEstimateTill
    {
        public Task<bool> AddProductAsync(string productId, decimal quantity) => window.EstimateAddProductAsync(productId, quantity);
        public bool AddCustomLine(string name, decimal amount) => window.EstimateAddCustomLine(name, amount);
    }
}

/// <summary>Каталог для сметы: поиск (название, штрихкод, артикул) и товар по id. По умолчанию — каталог кассы/программы владельца (<see cref="CatalogCacheService"/>); тест подставляет свой.</summary>
public interface IEstimateCatalog
{
    List<EstimateCatalogInfo> Search(string query, int max = 8);
    EstimateCatalogInfo? Find(string id);
}

public sealed class EstimateCatalog : IEstimateCatalog
{
    public List<EstimateCatalogInfo> Search(string query, int max = 8)
    {
        List<CatalogProductTileVm> all;
        try
        {
            all = CatalogCacheService.Products.ToList();
        }
        catch (InvalidOperationException)
        {
            return new();
        }
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return new();
        // Точный штрихкод / код / артикул — сканер: один товар. Иначе — единый поиск проекта (слова в любом порядке, ё=е, раскладка/транслит).
        var exact = all.Where(p => string.Equals(p.Barcode, q, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, q, StringComparison.OrdinalIgnoreCase)
                                   || (p.Article is { Length: > 0 } a && string.Equals(a, q, StringComparison.OrdinalIgnoreCase))).ToList();
        var found = exact.Count > 0
            ? exact
            : SearchMatch.Products(all, q).OrderByDescending(p => p.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase)).ThenBy(p => p.Title.Length).ToList();
        return found.Take(max).Select(EstimateRules.FromProduct).ToList();
    }

    public EstimateCatalogInfo? Find(string id) => CatalogCacheService.FindProductById(id) is { } p ? EstimateRules.FromProduct(p) : null;
}
