using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Канцтовары», КТ2): редактор «Списков к школе» для владельца. Слева — список шаблонов («1 класс», «5 класс»,
/// «Офис-старт»), справа — название и позиции: товар из каталога и количество. «Добавить товары» — тот же выбор с поиском, что у
/// комплектов (<see cref="BundleItemPickerDialog"/>). Сохранение — в файл этой кассы (<see cref="SchoolListStore"/>, ≤ 16 КБ).
/// Окно собрано в коде, цвета — из темы, оболочка ModalShell, кнопки и поля ≥ 44 px.
/// </summary>
public sealed class SchoolListsEditorWindow : Window
{
    private readonly List<SchoolList> _lists;
    private readonly IReadOnlyList<CatalogProductTileVm> _catalog;
    private readonly ListBox _listBox = new() { MinHeight = 160 };
    private readonly StackPanel _right = new() { Spacing = 8 };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private Border _errorBox = null!;
    private bool _suspend;

    /// <summary>Записаны ли списки (кнопка «Сохранить»).</summary>
    public bool Saved { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public SchoolListsEditorWindow(IEnumerable<SchoolList> lists, IReadOnlyList<CatalogProductTileVm>? catalog = null)
    {
        _lists = lists.Select(Clone).ToList();
        _catalog = catalog ?? CatalogCacheService.Products;
        Title = T("Списки к школе — правка", "Мектепке тизмелер — оңдоо", "Back-to-school lists — edit", "Okul listeleri — düzenle", "Maktab ro'yxatlari — tahrirlash");
        SectionCards.AsModal(this, 860);

        var left = new StackPanel { Spacing = 8, Width = 240 };
        _listBox.SelectionChanged += (_, _) => { if (!_suspend) BuildRight(); };
        AutomationProperties.SetName(_listBox, T("Списки", "Тизмелер", "Lists", "Listeler", "Ro'yxatlar"));
        left.Children.Add(_listBox);
        var newList = SectionCards.Secondary(T("Новый список", "Жаңы тизме", "New list", "Yeni liste", "Yangi ro'yxat"));
        newList.Click += (_, _) => AddList();
        var delete = SectionCards.Secondary(T("Удалить список", "Тизмени өчүрүү", "Delete list", "Listeyi sil", "Ro'yxatni o'chirish"));
        delete.Click += (_, _) => DeleteList();
        left.Children.Add(newList);
        left.Children.Add(delete);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,16,*") };
        body.Children.Add(left);
        Grid.SetColumn(_right, 2);
        body.Children.Add(_right);

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close(Saved);
        var save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        save.Click += (_, _) => Save();
        _errorBox = SectionCards.ErrorBox(this, _error);
        _error.IsVisible = false;

        Content = SectionCards.Shell(Title!, T("Шаблоны: товар и сколько штук нужно. Кассир добавляет список в чек одной кнопкой.",
            "Үлгүлөр: товар жана канча даана керек. Кассир тизмени чекке бир баскыч менен кошот.",
            "Templates: a product and how many pieces. The cashier adds a list to the receipt with one button.",
            "Şablonlar: ürün ve kaç adet gerektiği. Kasiyer listeyi tek tuşla fişe ekler.",
            "Shablonlar: mahsulot va nechta dona kerak. Kassir ro'yxatni bir tugma bilan chekka qo'shadi."), body, SectionCards.Buttons(close, save), _errorBox);
        RefreshListBox(0);
    }

    private static SchoolList Clone(SchoolList l) => new()
    {
        Id = l.Id, Name = l.Name,
        Items = l.Items.Select(i => new SchoolListItem { ProductId = i.ProductId, Name = i.Name, Quantity = i.Quantity }).ToList(),
    };

    private SchoolList? Current => _listBox.SelectedIndex is >= 0 and var i && i < _lists.Count ? _lists[i] : null;

    private void RefreshListBox(int select)
    {
        _suspend = true;
        _listBox.ItemsSource = _lists.Select(l => $"{(string.IsNullOrWhiteSpace(l.Name) ? "…" : l.Name)} · {l.Items.Count}").ToList();
        _listBox.SelectedIndex = _lists.Count == 0 ? -1 : Math.Clamp(select, 0, _lists.Count - 1);
        _suspend = false;
        BuildRight();
    }

    private void AddList()
    {
        _lists.Add(new SchoolList { Name = T("Новый список", "Жаңы тизме", "New list", "Yeni liste", "Yangi ro'yxat") });
        RefreshListBox(_lists.Count - 1);
    }

    private void DeleteList()
    {
        if (Current is not { } cur)
            return;
        var at = _lists.IndexOf(cur);
        _lists.Remove(cur);
        RefreshListBox(Math.Min(at, _lists.Count - 1));
    }

    private void BuildRight()
    {
        _right.Children.Clear();
        if (Current is not { } list)
        {
            _right.Children.Add(SectionCards.Empty(this, T("Нажмите «Новый список».", "«Жаңы тизме» баскычын басыңыз.", "Press “New list”.", "«Yeni liste»ye basın.", "«Yangi ro'yxat» tugmasini bosing.")));
            return;
        }
        var name = SectionCards.Field(new TextBox { Text = list.Name, MaxLength = SchoolListStore.MaxNameLength, VerticalContentAlignment = VerticalAlignment.Center, Watermark = T("Название, например «1 класс»", "Аталышы, мисалы «1-класс»", "Name, e.g. “Grade 1”", "Ad, örn. «1. sınıf»", "Nomi, masalan «1-sinf»") });
        AutomationProperties.SetName(name, T("Название списка", "Тизменин аталышы", "List name", "Liste adı", "Ro'yxat nomi"));
        name.TextChanged += (_, _) =>
        {
            list.Name = name.Text ?? "";
            var at = _listBox.SelectedIndex;
            _suspend = true;
            _listBox.ItemsSource = _lists.Select(l => $"{(string.IsNullOrWhiteSpace(l.Name) ? "…" : l.Name)} · {l.Items.Count}").ToList();
            _listBox.SelectedIndex = at;
            _suspend = false;
        };
        _right.Children.Add(name);

        var rows = new StackPanel { Spacing = 4 };
        foreach (var item in list.Items.ToList())
            rows.Children.Add(BuildItemRow(list, item));
        if (list.Items.Count == 0)
            rows.Children.Add(SectionCards.Empty(this, T("Товаров пока нет. Нажмите «Добавить товары».", "Товарлар азырынча жок. «Товар кошуу» баскычын басыңыз.", "No products yet. Press “Add products”.", "Henüz ürün yok. «Ürün ekle»ye basın.", "Mahsulot hozircha yo'q. «Mahsulot qo'shish» tugmasini bosing.")));
        _right.Children.Add(rows);

        var add = SectionCards.Secondary(T("Добавить товары", "Товар кошуу", "Add products", "Ürün ekle", "Mahsulot qo'shish"));
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Click += async (_, _) => await PickProductsAsync(list).ConfigureAwait(true);
        _right.Children.Add(add);
    }

    private Control BuildItemRow(SchoolList list, SchoolListItem item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), MinHeight = 44 };
        var title = new TextBlock { Text = string.IsNullOrWhiteSpace(item.Name) ? item.ProductId : item.Name, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("BrushText"));
        grid.Children.Add(title);
        var qty = SectionCards.Field(new TextBox { Text = item.Quantity.ToString(), Width = 72, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center });
        AutomationProperties.SetName(qty, T("Количество", "Саны", "Quantity", "Miktar", "Miqdor") + ": " + title.Text);
        qty.TextChanged += (_, _) =>
            item.Quantity = int.TryParse((qty.Text ?? "").Trim(), out var q) ? Math.Clamp(q, 1, SchoolListStore.MaxQuantity) : 1;
        Grid.SetColumn(qty, 1);
        grid.Children.Add(qty);
        var remove = new Button { Content = "✕", MinWidth = 44, MinHeight = 44, VerticalAlignment = VerticalAlignment.Center };
        remove.Classes.Add("modal-ghost");
        AutomationProperties.SetName(remove, T("Убрать из списка", "Тизмеден алып салуу", "Remove from list", "Listeden kaldır", "Ro'yxatdan olib tashlash") + ": " + title.Text);
        remove.Click += (_, _) => { list.Items.Remove(item); RefreshListBox(_listBox.SelectedIndex); };
        Grid.SetColumn(remove, 2);
        grid.Children.Add(remove);
        return grid;
    }

    private async Task PickProductsAsync(SchoolList list)
    {
        var picker = new BundleItemPickerDialog(_catalog, list.Items.Select(i => i.ProductId));
        if (await PosDialogHost.ShowModalAsync(picker, this).ConfigureAwait(true) != true)
            return;
        var keep = picker.Result.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        list.Items.RemoveAll(i => !keep.Contains(i.ProductId));
        foreach (var product in picker.Result)
            if (!list.Items.Any(i => string.Equals(i.ProductId, product.Id, StringComparison.OrdinalIgnoreCase)))
                list.Items.Add(new SchoolListItem { ProductId = product.Id, Name = product.Title, Quantity = 1 });
        RefreshListBox(_listBox.SelectedIndex);
    }

    private void Save()
    {
        var problem = SchoolListStore.Save(_lists);
        _error.Text = problem ?? "";
        _error.IsVisible = problem is not null;
        if (problem is null)
        {
            Saved = true;
            Close(true);
        }
    }

    /// <summary>Для проверок.</summary>
    public IReadOnlyList<SchoolList> ListsForTests => _lists;
}
