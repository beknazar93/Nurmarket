using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-09-12: "Выбор товаров для комплекта" — по образцу веб-версии (скриншоты
/// пользователя): поиск + список с чекбоксами + "Выбрать все" + счётчик выбранного. Источник
/// товаров — CatalogCacheService.Products, тот же в офлайне и онлайне (уже загруженный каталог
/// в памяти), поэтому отдельного сетевого запроса здесь не нужно ни в каком режиме.</summary>
public partial class BundleItemPickerDialog : Window
{
    private readonly List<PickerItem> _allItems;
    private string _searchText = "";

    /// <summary>Заполняется только при подтверждении (AddButton_Click) — что вернуть вызывающей
    /// форме после ShowDialog.</summary>
    public List<CatalogProductTileVm> Result { get; private set; } = new();

    public BundleItemPickerDialog(IEnumerable<CatalogProductTileVm> allProducts, IEnumerable<string> preSelectedIds)
    {
        var preSelected = preSelectedIds.ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        _allItems = allProducts
            // Комплект не может включать сам себя/другой комплект (сервер и локальная логика
            // продажи не умеют разворачивать вложенные наборы) — исключаем из выбора.
            .Where(p => !p.IsBundle)
            .OrderBy(p => p.Title, System.StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PickerItem(p) { IsSelected = preSelected.Contains(p.Id) })
            .ToList();

        InitializeComponent();
        Rebuild();
    }

    /// <summary>Parameterless ctor required by Avalonia XAML previewer/designer only.</summary>
    public BundleItemPickerDialog() : this([], [])
    {
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text ?? "";
        Rebuild();
    }

    private IEnumerable<PickerItem> VisibleItems() =>
        string.IsNullOrWhiteSpace(_searchText)
            ? _allItems
            : _allItems.Where(i =>
                i.Product.Title.Contains(_searchText, System.StringComparison.CurrentCultureIgnoreCase)
                || (i.Product.Barcode ?? "").Contains(_searchText, System.StringComparison.OrdinalIgnoreCase));

    private void Rebuild()
    {
        ItemsList.Items.Clear();
        var visible = VisibleItems().ToList();
        foreach (var item in visible)
            ItemsList.Items.Add(BuildRow(item));

        EmptyHintText.Text = visible.Count == 0
            ? Tr.T("Ничего не найдено.", "Эч нерсе табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi.")
            : "";

        SelectAllCheckBox.IsCheckedChanged -= SelectAllCheckBox_CheckedChangedNoop;
        SelectAllCheckBox.IsChecked = visible.Count > 0 && visible.All(i => i.IsSelected);
        SelectAllCheckBox.IsCheckedChanged += SelectAllCheckBox_CheckedChangedNoop;

        UpdateSelectedCount();
    }

    // Avalonia требует обработчик на событие, даже если он тут ничего не делает — реальное
    // переключение состояния уже произошло через CheckBox.IsChecked binding до срабатывания Click.
    private void SelectAllCheckBox_CheckedChangedNoop(object? sender, RoutedEventArgs e)
    {
    }

    private Control BuildRow(PickerItem item)
    {
        var checkBox = new CheckBox
        {
            IsChecked = item.IsSelected,
            Margin = new Avalonia.Thickness(0, 0, 0, 4),
        };

        var stockText = item.Product.MustWeigh
            ? $"{item.Product.Quantity:0.###} кг"
            : $"{item.Product.Quantity:0.#} шт";

        checkBox.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = item.Product.Title, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                new TextBlock
                {
                    Text = $"· {(string.IsNullOrWhiteSpace(item.Product.Category) ? "—" : item.Product.Category)} · {stockText}",
                    Foreground = TryGetBrush("BrushTextSoft"),
                    FontSize = 12,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                },
            },
        };

        checkBox.IsCheckedChanged += (_, _) =>
        {
            item.IsSelected = checkBox.IsChecked == true;
            UpdateSelectedCount();
            SelectAllCheckBox.IsCheckedChanged -= SelectAllCheckBox_CheckedChangedNoop;
            var visible = VisibleItems().ToList();
            SelectAllCheckBox.IsChecked = visible.Count > 0 && visible.All(i => i.IsSelected);
            SelectAllCheckBox.IsCheckedChanged += SelectAllCheckBox_CheckedChangedNoop;
        };

        return checkBox;
    }

    private Avalonia.Media.IBrush? TryGetBrush(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, ActualThemeVariant, out var value) == true
        && value is Avalonia.Media.IBrush brush
            ? brush
            : null;

    private void SelectAllCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        var selectAll = SelectAllCheckBox.IsChecked == true;
        foreach (var item in VisibleItems())
            item.IsSelected = selectAll;
        Rebuild();
    }

    private void UpdateSelectedCount()
    {
        var count = _allItems.Count(i => i.IsSelected);
        SelectedCountText.Text = Tr.T($"Выбрано: {count}", $"Тандалды: {count}", $"Selected: {count}", $"Seçildi: {count}", $"Tanlandi: {count}");
        AddButton.Content = count > 0
            ? Tr.T($"Добавить ({count})", $"Кошуу ({count})", $"Add ({count})", $"Ekle ({count})", $"Qo'shish ({count})")
            : Tr.T("Добавить", "Кошуу", "Add", "Ekle", "Qo'shish");
    }

    private void AddButton_Click(object? sender, RoutedEventArgs e)
    {
        Result = _allItems.Where(i => i.IsSelected).Select(i => i.Product).ToList();
        Close(true);
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);

    private sealed class PickerItem
    {
        public PickerItem(CatalogProductTileVm product) => Product = product;
        public CatalogProductTileVm Product { get; }
        public bool IsSelected { get; set; }
    }
}
