#nullable enable

namespace NurMarketKassa.Models.Pos;

/// <summary>2026-11-21 (просьба владельца «хлеб без заводского штрихкода — в начало каталога»): отметки «Без заводского штрихкода» — множество id товаров.
/// Живёт в Core, чтобы плитка (<see cref="CatalogProductTileVm.HasNoFactoryBarcode"/>) знала отметку без ссылки на Infrastructure.
/// Читает и пишет множество <c>NurMarketKassa.Services.NoFactoryBarcodes</c> (общие настройки магазина, облако + локальный кэш); здесь — только быстрый доступ.</summary>
public static class NoFactoryBarcodeMarks
{
    private static volatile HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>Товар отмечен «Без заводского штрихкода».</summary>
    public static bool IsMarked(string? id) => !string.IsNullOrEmpty(id) && _ids.Contains(id);

    public static int Count => _ids.Count;

    /// <summary>Заменить множество целиком (новый объект — читатели в других потоках видят либо старое, либо новое).</summary>
    public static void Replace(HashSet<string> ids) => _ids = ids;
}
