using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NurMarketKassa.ViewModels.Main;

namespace NurMarketKassa.AvaloniaHost.Views.MainKassir.Layouts;

/// <summary>Раскладка «Карточки» (2026-09-28): чипы категорий сверху, карточки товаров по центру,
/// чек справа. Чипы категорий строятся здесь из самого каталога (CatalogPanelViewModel.
/// AvailableCategories) — отдельного списка категорий у кассы нет. Выбор категории — тот же
/// фильтр, что в окне «Фильтр» обычной кассы (ApplyAdvancedFilter), поиск при этом не теряется.</summary>
public partial class CardsLayoutView : KassaLayoutBase
{
    private CatalogPanelViewModel? _catalog;
    private IReadOnlyList<string> _shownCategories = new List<string>();
    private string? _activeCategory;
    private bool _rebuildPending;

    public CardsLayoutView()
    {
        InitializeComponent();
        RegisterProductList(CardsList);
        SearchBox = ProductSearchBox;
        DataContextChanged += (_, _) => HookCatalog();
        AttachedToVisualTree += (_, _) => HookCatalog();
        DetachedFromVisualTree += (_, _) => UnhookCatalog();
    }

    private void HookCatalog()
    {
        UnhookCatalog();
        _catalog = Vm?.Catalog;
        if (_catalog is null)
            return;
        _catalog.Products.CollectionChanged += OnProductsChanged;
        Tr.LanguageChanged += OnLanguageChanged;
        RebuildCategoryChips();
    }

    private void UnhookCatalog()
    {
        if (_catalog is not null)
            _catalog.Products.CollectionChanged -= OnProductsChanged;
        Tr.LanguageChanged -= OnLanguageChanged;
        _catalog = null;
    }

    private void OnProductsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Страница каталога пересобирается целиком (Clear + полсотни Add) — чипы пересчитываем
        // один раз после всей пачки, а не на каждую плитку: список категорий считается по всему
        // каталогу, на десятках тысяч товаров это заметно.
        if (_rebuildPending)
            return;
        _rebuildPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildPending = false;
            RebuildCategoryChips();
        }, DispatcherPriority.Background);
    }

    private void OnLanguageChanged() =>
        Dispatcher.UIThread.Post(() =>
        {
            _shownCategories = new List<string>();
            CategoryChipsPanel.Children.Clear();
            RebuildCategoryChips();
        });

    private void RebuildCategoryChips()
    {
        if (_catalog is null)
            return;

        var categories = _catalog.AvailableCategories;
        CategoryDivider.IsVisible = categories.Count > 0;
        if (categories.SequenceEqual(_shownCategories) && (CategoryChipsPanel.Children.Count > 0) == (categories.Count > 0))
        {
            RefreshActiveChip();
            return;
        }

        _shownCategories = categories.ToList();
        CategoryChipsPanel.Children.Clear();
        if (categories.Count == 0)
            return;

        CategoryChipsPanel.Children.Add(MakeChip(Tr.T("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha kategoriyalar"), null));
        foreach (var category in categories.Take(40))
            CategoryChipsPanel.Children.Add(MakeChip(category, category));
        RefreshActiveChip();
    }

    private Button MakeChip(string text, string? category)
    {
        var chip = new Button { Content = text, Tag = category ?? "" };
        chip.Classes.Add("lx-chip");
        chip.Click += CategoryChip_Click;
        return chip;
    }

    private void CategoryChip_Click(object? sender, RoutedEventArgs e)
    {
        if (_catalog is null || sender is not Button { Tag: string tag })
            return;

        _activeCategory = tag.Length == 0 ? null : tag;
        _catalog.ApplyAdvancedFilter(new CatalogFilterCriteria(
            SearchText: _catalog.SearchText,
            Kind: "",
            Category: _activeCategory,
            Brand: null,
            PriceMin: null,
            PriceMax: null,
            OnlyInStock: false));
        RefreshActiveChip();
    }

    private void RefreshActiveChip()
    {
        foreach (var chip in CategoryChipsPanel.Children.OfType<Button>())
        {
            var tag = chip.Tag as string ?? "";
            chip.Classes.Set("active", _activeCategory is null ? tag.Length == 0 : tag == _activeCategory);
        }
    }
}
