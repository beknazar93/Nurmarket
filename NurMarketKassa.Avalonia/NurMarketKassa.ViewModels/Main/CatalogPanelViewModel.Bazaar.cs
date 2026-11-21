using System.Collections.ObjectModel;
using System.Windows.Input;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels.Main;

/// <summary>Плитка категории на главном экране «Базара»: крупное название, число товаров и фото (обложка — первый товар категории с фото); без фото — цвет категории.</summary>
public sealed class BazaarCategoryTileVm : ViewModelBase
{
    public BazaarCategoryTileVm(string key, int count, CatalogProductTileVm? cover)
    {
        Key = key;
        Count = count;
        Cover = cover;
    }

    /// <summary>Категория товара как в карточке; пусто — «Без категории».</summary>
    public string Key { get; }

    public int Count { get; }

    /// <summary>Первый товар категории с фото (для обложки); null — фото нет.</summary>
    public CatalogProductTileVm? Cover { get; }

    public bool IsOther => Key.Length == 0;

    public string Title => IsOther
        ? Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz")
        : Key;

    public string CountText => Tr.T($"Товаров: {Count}", $"Товарлар: {Count}", $"Items: {Count}", $"Ürün: {Count}", $"Mahsulotlar: {Count}");
}

/// <summary>
/// 2026-11-21 (сфера «Базар», модуль <c>category_tiles</c>, ТЗ Контейнер-базар 2.5): режим «быстрые плитки категорий». На главном экране вместо длинного списка — крупные плитки категорий
/// (фото или цвет, название, число товаров); один тап → список товаров этой категории; тап по товару кладёт его в чек (как обычно); «← Все категории» возвращает к плиткам. Поиск, «Фильтр» и группы F1–F12
/// работают поверх всего каталога и плиток не показывают. Без модуля (и в других сферах) ничего не меняется: <see cref="ShowCategoryTiles"/> всегда false, <see cref="ShowProductGrid"/> = <see cref="HasProducts"/>.
/// </summary>
public sealed partial class CatalogPanelViewModel
{
    private string? _bazaarCategory;
    private ICommand? _openCategoryCommand;
    private ICommand? _backToCategoriesCommand;

    public ObservableCollection<BazaarCategoryTileVm> CategoryTiles { get; } = new();

    /// <summary>Режим плиток категорий включён (модуль «Плитки категорий»).</summary>
    public bool CategoryTilesMode => BazaarModules.CategoryTilesOn;

    /// <summary>Плитки показаны вместо списка товаров: корень каталога, нет поиска / фильтра / группы F, и в каталоге есть хоть одна настоящая категория.</summary>
    public bool ShowCategoryTiles =>
        CategoryTilesMode && _bazaarCategory is null && !IsSearchActive && !HasHotkeyGroupFilter && !HasActiveFilter && CategoryTiles.Any(t => !t.IsOther);

    /// <summary>Список товаров показан (его место занимают плитки, пока <see cref="ShowCategoryTiles"/>).</summary>
    public bool ShowProductGrid => HasProducts && !ShowCategoryTiles;

    /// <summary>Открыта категория: сверху строка «← Все категории · Название».</summary>
    public bool InBazaarCategory => CategoryTilesMode && _bazaarCategory is not null && !IsSearchActive;

    public string BazaarCategoryTitle => _bazaarCategory is null
        ? ""
        : _bazaarCategory.Length == 0 ? Tr.T("Без категории", "Категориясыз", "No category", "Kategorisiz", "Kategoriyasiz") : _bazaarCategory;

    public string BackToCategoriesText => "← " + Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar");

    public ICommand OpenCategoryCommand => _openCategoryCommand ??= new RelayCommand<BazaarCategoryTileVm>(tile =>
    {
        if (tile is not null)
            SetBazaarCategory(tile.Key);
    });

    public ICommand BackToCategoriesCommand => _backToCategoriesCommand ??= new RelayCommand(() => SetBazaarCategory(null));

    /// <summary>Открыть категорию (null — вернуться к плиткам).</summary>
    public void SetBazaarCategory(string? category)
    {
        if (string.Equals(_bazaarCategory, category, StringComparison.OrdinalIgnoreCase))
            return;
        _bazaarCategory = category;
        ResetCurrentPage();
        ApplyFilter();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Товар входит в открытую категорию (категория не открыта — все).</summary>
    private bool MatchesBazaarCategory(CatalogProductTileVm product) =>
        _bazaarCategory is null || !CategoryTilesMode
        || string.Equals((product.Category ?? "").Trim(), _bazaarCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Пересобрать плитки категорий (вызывается после каждого отбора каталога) и сообщить окну, что показывать. Без модуля — ничего не считает.</summary>
    private void RebuildBazaarCategoryTiles()
    {
        if (!CategoryTilesMode)
        {
            if (CategoryTiles.Count > 0)
                CategoryTiles.Clear();
            _bazaarCategory = null;
            RaiseBazaarTileState();
            return;
        }
        if (!IsSearchActive)
        {
            var groups = _allProducts
                .Where(MatchesTab)
                .Where(MatchesAdvancedFilter)
                .GroupBy(p => (p.Category ?? "").Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => (Key: g.Key, Items: g))
                .OrderBy(g => g.Key.Length == 0 ? 1 : 0) // «Без категории» — последней
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var fresh = groups
                .Select(g => new BazaarCategoryTileVm(g.Key, g.Items.Count(), g.Items.FirstOrDefault(p => !string.IsNullOrEmpty(p.ImageUrl))))
                .ToList();
            var same = fresh.Count == CategoryTiles.Count
                       && fresh.Select((t, i) => t.Key == CategoryTiles[i].Key && t.Count == CategoryTiles[i].Count && ReferenceEquals(t.Cover, CategoryTiles[i].Cover)).All(x => x);
            if (!same)
            {
                CategoryTiles.Clear();
                foreach (var t in fresh)
                    CategoryTiles.Add(t);
            }
            // открытая категория исчезла (товары ушли / сменили вкладку) — назад к плиткам
            if (_bazaarCategory is not null && !CategoryTiles.Any(t => string.Equals(t.Key, _bazaarCategory, StringComparison.OrdinalIgnoreCase)))
                _bazaarCategory = null;
        }
        RaiseBazaarTileState();
    }

    private void RaiseBazaarTileState()
    {
        OnPropertyChanged(nameof(CategoryTilesMode));
        OnPropertyChanged(nameof(ShowCategoryTiles));
        OnPropertyChanged(nameof(ShowProductGrid));
        OnPropertyChanged(nameof(InBazaarCategory));
        OnPropertyChanged(nameof(BazaarCategoryTitle));
    }
}
