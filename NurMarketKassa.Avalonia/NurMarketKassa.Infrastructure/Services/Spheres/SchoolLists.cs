using System.Text.Json;
using NurMarketKassa.Models.Pos;

namespace NurMarketKassa.Services;

/// <summary>2026-11-12 (ТЗ «Сфера Канцтовары», КТ2): одна позиция списка к школе — товар и сколько штук нужно.
/// Название хранится на момент добавления: если товар потом удалят из каталога, список всё равно покажет, чего не хватает.</summary>
public sealed class SchoolListItem
{
    public string ProductId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
}

/// <summary>Список к школе («1 класс», «5 класс», «Офис-старт»): название и позиции.</summary>
public sealed class SchoolList
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<SchoolListItem> Items { get; set; } = new();
}

/// <summary>
/// 2026-11-12 (КТ2): хранение списков к школе в файле ЭТОЙ кассы (<c>school-lists.json</c> рядом с настройками), атомарно
/// (<see cref="AtomicFile"/>, с копией .bak). Схема базы не меняется. Предел — 16 КБ, как у ключа общих настроек компании в ТЗ
/// (когда ХМ15 заведёт ключ SchoolListsJson, тот же текст поедет в облако без пересчёта). Списки видны только в сфере «Канцтовары».
/// </summary>
public static class SchoolListStore
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxLists = 40;
    public const int MaxItemsPerList = 80;
    public const int MaxNameLength = 60;
    public const int MaxQuantity = 999;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, PropertyNameCaseInsensitive = true };

    public static string FilePath => Path.Combine(AppPaths.KassaRoaming, "school-lists.json");

    /// <summary>Прочитать списки. Нет файла или он испорчен — пустой список (копия .bak подставляется самим AtomicFile).</summary>
    public static List<SchoolList> Load(string? path = null)
    {
        try
        {
            var text = AtomicFile.ReadAllText(path ?? FilePath, IsValid);
            return text is null ? new List<SchoolList>() : Parse(text) ?? new List<SchoolList>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Списки к школе не прочитаны ({ex.Message}).", "WARNING");
            return new List<SchoolList>();
        }
    }

    /// <summary>Разбор текста: null — не списки. Мусорные поля чинятся (пустое название → пропуск, количество в 1…999).</summary>
    public static List<SchoolList>? Parse(string text)
    {
        try
        {
            var lists = JsonSerializer.Deserialize<List<SchoolList>>(text, Json);
            if (lists is null)
                return null;
            var result = new List<SchoolList>();
            foreach (var l in lists.Where(l => l is not null && !string.IsNullOrWhiteSpace(l.Name)))
            {
                l.Name = Clip(l.Name.Trim(), MaxNameLength);
                if (string.IsNullOrWhiteSpace(l.Id))
                    l.Id = Guid.NewGuid().ToString("N");
                l.Items = (l.Items ?? new List<SchoolListItem>())
                    .Where(i => i is not null && !string.IsNullOrWhiteSpace(i.ProductId))
                    .Select(i => new SchoolListItem { ProductId = i.ProductId.Trim(), Name = Clip((i.Name ?? "").Trim(), 120), Quantity = Math.Clamp(i.Quantity, 1, MaxQuantity) })
                    .ToList();
                result.Add(l);
            }
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsValid(string text) => Parse(text) is not null;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max];

    public static string Serialize(IEnumerable<SchoolList> lists) => JsonSerializer.Serialize(lists, Json);

    /// <summary>Проверка перед записью: null — всё в порядке, иначе понятная причина (на языке интерфейса).</summary>
    public static string? Validate(IReadOnlyList<SchoolList> lists)
    {
        if (lists.Count > MaxLists)
            return Tr.T($"Списков не больше {MaxLists}.", $"Тизме {MaxLists} дан ашпашы керек.", $"No more than {MaxLists} lists.", $"En fazla {MaxLists} liste.", $"{MaxLists} tadan ko'p ro'yxat bo'lmaydi.");
        foreach (var l in lists)
        {
            if (string.IsNullOrWhiteSpace(l.Name))
                return Tr.T("У списка нет названия.", "Тизмеде аты жок.", "The list has no name.", "Listenin adı yok.", "Ro'yxat nomi yo'q.");
            if (l.Items.Count > MaxItemsPerList)
                return Tr.T($"В списке «{l.Name}» больше {MaxItemsPerList} товаров.", $"«{l.Name}» тизмесинде {MaxItemsPerList} товардан көп.", $"List “{l.Name}” has more than {MaxItemsPerList} products.", $"«{l.Name}» listesinde {MaxItemsPerList} üründen fazla var.", $"«{l.Name}» ro'yxatida {MaxItemsPerList} tadan ko'p mahsulot bor.");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(Serialize(lists)) > MaxBytes)
            return Tr.T("Списки не помещаются в 16 КБ — уберите лишние товары или списки.", "Тизмелер 16 КБга батпайт — ашыкча товарларды же тизмелерди алып салыңыз.",
                "The lists do not fit in 16 KB — remove extra products or lists.", "Listeler 16 KB'a sığmıyor — fazla ürün veya listeleri kaldırın.", "Ro'yxatlar 16 KB ga sig'maydi — ortiqcha mahsulot yoki ro'yxatlarni olib tashlang.");
        return null;
    }

    /// <summary>Записать списки атомарно. null — записано, иначе причина отказа.</summary>
    public static string? Save(IReadOnlyList<SchoolList> lists, string? path = null)
    {
        if (Validate(lists) is { } problem)
            return problem;
        try
        {
            AtomicFile.WriteAllText(path ?? FilePath, Serialize(lists));
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PosLogger.Log($"Списки к школе не записаны: {ex.Message}", "WARNING");
            return Tr.T("Не удалось записать списки. Повторите.", "Тизмелерди жазуу мүмкүн болгон жок. Кайра аракет кылыңыз.", "Could not save the lists. Try again.", "Listeler kaydedilemedi. Tekrar deneyin.", "Ro'yxatlarni saqlab bo'lmadi. Qayta urinib ko'ring.");
        }
    }

    /// <summary>Списки к школе доступны только в сфере «Канцтовары» (у других сфер ничего не меняется).</summary>
    public static bool Available => MarketSpheres.IsStationery && StationeryProfile.Features.SchoolLists;
}

/// <summary>Состояние позиции списка относительно каталога.</summary>
public enum SchoolLineStatus
{
    /// <summary>Товар есть, остатка хватает (или это услуга).</summary>
    Ok,
    /// <summary>Товара нет в каталоге (удалён) — строка пропускается.</summary>
    Missing,
    /// <summary>Остаток 0 — предупреждение, как при скане; в чек добавляется только по подтверждению кассира.</summary>
    OutOfStock,
    /// <summary>Остатка меньше, чем нужно по списку.</summary>
    NotEnough,
}

/// <summary>Одна строка плана «список → чек».</summary>
public sealed record SchoolLinePlan(SchoolListItem Item, SchoolLineStatus Status, CatalogProductTileVm? Tile, double Stock)
{
    /// <summary>Строку можно положить в чек (товар найден).</summary>
    public bool CanAdd => Tile is not null;

    /// <summary>Цена за единицу — обычная цена товара из каталога (скидок и опта список не делает).</summary>
    public decimal UnitPrice => Tile is null ? 0m : (decimal)LocalCartService.ParsePrice(Tile.PriceLine);

    public decimal LineTotal => UnitPrice * Item.Quantity;
}

/// <summary>2026-11-12 (КТ2): превращает список в строки чека. Чистая логика без окон и сети: каталог подставляет вызывающий.</summary>
public static class SchoolListPlanner
{
    public static IReadOnlyList<SchoolLinePlan> Plan(SchoolList list, Func<string, CatalogProductTileVm?> lookup)
    {
        var plan = new List<SchoolLinePlan>(list.Items.Count);
        foreach (var item in list.Items)
        {
            var tile = lookup(item.ProductId);
            if (tile is null)
            {
                plan.Add(new SchoolLinePlan(item, SchoolLineStatus.Missing, null, 0));
                continue;
            }
            var status = SchoolLineStatus.Ok;
            if (!tile.IsService)
                status = tile.Quantity <= 0 ? SchoolLineStatus.OutOfStock : tile.Quantity < item.Quantity ? SchoolLineStatus.NotEnough : SchoolLineStatus.Ok;
            plan.Add(new SchoolLinePlan(item, status, tile, tile.Quantity));
        }
        return plan;
    }

    /// <summary>Отмеченные по умолчанию: товар найден и остаток есть. Нет в наличии — не отмечено, кассир может отметить сам.</summary>
    public static bool CheckedByDefault(SchoolLinePlan line) => line.Status is SchoolLineStatus.Ok or SchoolLineStatus.NotEnough;

    /// <summary>Сумма отмеченных строк по обычным ценам.</summary>
    public static decimal Total(IEnumerable<SchoolLinePlan> lines) => lines.Where(l => l.CanAdd).Sum(l => l.LineTotal);
}
