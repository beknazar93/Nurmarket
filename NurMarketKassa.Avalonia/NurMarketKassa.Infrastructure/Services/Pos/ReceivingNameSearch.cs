using NurMarketKassa.Core.Application;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.Services;

/// <summary>
/// 2026-11-06 (Б-12, живой тест Этапа 1): «Приём товара» без связи не находил существующий товар по НАЗВАНИЮ (только по штрихкоду) и предлагал «завести новый» — риск дубля.
/// Поиск шёл только по списку в памяти (CatalogCacheService.Products), а он без связи может быть пуст, тогда как штрихкод искался ещё и по локальной базе.
/// Теперь: список памяти → локальная база кассы (тот же поиск, что у каталога) → «умное» сопоставление накладных (сокращения «Мол.», «1л», латиница/кириллица).
/// Вынесено из PurchasesWindow, чтобы проверяться тестами без окон.
/// </summary>
public static class ReceivingNameSearch
{
    /// <param name="Tiles">Найденные товары (до <c>take</c>), подходящие по названию.</param>
    /// <param name="CatalogChecked">Поиск реально выполнен по непустому каталогу (памяти или локальной базе). false — каталога на этом ПК нет или база не открылась: «товара нет» утверждать нельзя.</param>
    /// <param name="UsedLocalDatabase">Список памяти ничего не дал и искали в локальной базе.</param>
    /// <param name="UsedSmartMatch">Точного совпадения не было — найдено умным сопоставлением (сокращения, единицы, латиница/кириллица).</param>
    public sealed record Outcome(IReadOnlyList<CatalogProductTileVm> Tiles, bool CatalogChecked, bool UsedLocalDatabase, bool UsedSmartMatch);

    /// <param name="memory">Каталог в памяти (CatalogCacheService.Products).</param>
    /// <param name="localLoader">Товары локальной базы кассы (LocalProductRepository.LoadAllTiles); null — без базы. Исключение = база недоступна.</param>
    /// <param name="firstIds">Товары выбранного поставщика идут первыми.</param>
    /// <param name="smart">true — при отсутствии обычного совпадения искать умным сопоставлением накладных (дороже: строит индекс; на каждый символ не вызывать).</param>
    public static Outcome Find(string? text, IReadOnlyList<CatalogProductTileVm> memory, Func<IReadOnlyList<CatalogProductTileVm>>? localLoader,
        ISet<string>? firstIds = null, bool smart = false, int take = 8)
    {
        var q = (text ?? "").Trim();
        if (q.Length < 2)
            return new Outcome([], memory.Count > 0, false, false);

        var found = SearchMatch.Products(memory, q);
        var catalogChecked = memory.Count > 0;
        var usedLocal = false;
        IReadOnlyList<CatalogProductTileVm> source = memory;

        if (found.Count == 0 && localLoader is not null)
        {
            IReadOnlyList<CatalogProductTileVm>? local = null;
            try { local = localLoader(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.LogThrottled("receiving-name-local", $"Приём товара: поиск по названию — локальная база недоступна ({ex.Message}).", "WARNING");
            }

            if (local is { Count: > 0 })
            {
                usedLocal = true;
                catalogChecked = true;
                source = local;
                found = SearchMatch.Products(local, q);
            }
        }

        var usedSmart = false;
        if (found.Count == 0 && smart && source.Count > 0)
        {
            try
            {
                var suggested = new InvoiceCatalogIndex(source).Suggest(q);
                if (suggested.Count > 0)
                {
                    usedSmart = true;
                    found = suggested.Select(c => c.Tile).ToList();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.LogThrottled("receiving-name-smart", $"Приём товара: умное сопоставление по названию не удалось ({ex.GetType().Name}).", "WARNING");
            }
        }

        var ordered = usedSmart
            ? found.Take(take).ToList() // уже по убыванию похожести
            : found
                .OrderBy(p => firstIds is { Count: > 0 } && firstIds.Contains(p.Id) ? 0 : 1)
                .ThenBy(p => p.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
                .ThenBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(take).ToList();
        return new Outcome(ordered, catalogChecked, usedLocal, usedSmart);
    }
}
