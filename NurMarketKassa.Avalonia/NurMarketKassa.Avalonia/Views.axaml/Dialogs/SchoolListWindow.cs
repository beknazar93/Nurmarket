using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-12 (ТЗ «Сфера Канцтовары», КТ2): окно «Списки к школе». Кассир выбирает список («1 класс», «Офис-старт»), снимает галочки
/// с того, чего покупателю не нужно или чего нет в наличии, и нажимает «В чек» — позиции идут в чек обычными строками по обычным
/// ценам (каждая — тем же путём, что скан: смена, просрочка, остаток). Нет товара в каталоге — строка серая, пропускается;
/// остаток 0 — предупреждение и галочка снята. Окно собрано в коде, цвета — из темы, оболочка ModalShell, строки ≥ 44 px (сенсор 1024×600).
/// Открывается только в сфере «Канцтовары» (<see cref="SchoolListStore.Available"/>); результат — <see cref="Result"/>.
/// </summary>
public sealed class SchoolListWindow : Window
{
    private readonly IReadOnlyList<SchoolList> _lists;
    private readonly Func<string, CatalogProductTileVm?> _lookup;
    private readonly ComboBox _listBox = new();
    private readonly StackPanel _rows = new() { Spacing = 6 };
    private readonly TextBlock _summary = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Button _add;
    private readonly List<RowState> _state = new();

    private sealed class RowState
    {
        public required SchoolLinePlan Plan { get; init; }
        public required CheckBox Check { get; init; }
        public required TextBox Qty { get; init; }
    }

    /// <summary>Выбранные строки: товар каталога и количество. Заполняется, когда окно закрыто кнопкой «В чек».</summary>
    public IReadOnlyList<(CatalogProductTileVm Tile, int Quantity)> Result { get; private set; } = Array.Empty<(CatalogProductTileVm, int)>();

    /// <summary>Нажали «Править списки» — вызывающий откроет редактор и покажет это окно снова.</summary>
    public bool EditRequested { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public SchoolListWindow(IReadOnlyList<SchoolList> lists, Func<string, CatalogProductTileVm?> lookup, bool canEdit, string? selectedListId = null)
    {
        _lists = lists;
        _lookup = lookup;
        Title = T("Списки к школе", "Мектепке тизмелер", "Back-to-school lists", "Okul listeleri", "Maktab ro'yxatlari");
        SectionCards.AsModal(this, 720);

        var root = new StackPanel { Spacing = 12 };
        Control content;
        if (lists.Count == 0)
        {
            content = SectionCards.Empty(this, canEdit
                ? T("Списков пока нет. Нажмите «Править списки» и заведите первый: «1 класс», «5 класс», «Офис-старт».",
                    "Тизмелер азырынча жок. «Тизмелерди оңдоо» баскычын басып биринчисин түзүңүз: «1-класс», «5-класс», «Офис-старт».",
                    "No lists yet. Press “Edit lists” and create the first one: “Grade 1”, “Grade 5”, “Office start”.",
                    "Henüz liste yok. «Listeleri düzenle»ye basıp ilkini oluşturun: «1. sınıf», «5. sınıf», «Ofis başlangıç».",
                    "Hozircha ro'yxat yo'q. «Ro'yxatlarni tahrirlash» tugmasini bosib birinchisini yarating: «1-sinf», «5-sinf», «Ofis-start».")
                : T("Списков пока нет. Их заводит владелец магазина.", "Тизмелер азырынча жок. Аларды дүкөндүн ээси түзөт.",
                    "No lists yet. The shop owner creates them.", "Henüz liste yok. Bunları mağaza sahibi oluşturur.", "Hozircha ro'yxat yo'q. Ularni do'kon egasi yaratadi."));
        }
        else
        {
            _listBox.ItemsSource = lists.Select(l => $"{l.Name} · {l.Items.Count}").ToList();
            _listBox.HorizontalAlignment = HorizontalAlignment.Stretch;
            _listBox.MinHeight = 44;
            AutomationProperties.SetName(_listBox, Title);
            _listBox.SelectionChanged += (_, _) => Rebuild();
            root.Children.Add(_listBox);
            root.Children.Add(_rows);
            content = root;
        }

        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close(false);
        _add = SectionCards.Primary(T("В чек", "Чекке", "To receipt", "Fişe", "Chekka"));
        _add.IsEnabled = false;
        // Enter не нажимает «В чек»: сканер штрихкодов шлёт Enter в активное поле, и список ушёл бы в чек случайно.
        _add.Click += (_, _) => Commit();

        var buttons = new List<Button>();
        if (canEdit)
        {
            var edit = SectionCards.Secondary(T("Править списки", "Тизмелерди оңдоо", "Edit lists", "Listeleri düzenle", "Ro'yxatlarni tahrirlash"));
            edit.Click += (_, _) => { EditRequested = true; Close(false); };
            buttons.Add(edit);
        }
        buttons.Add(close);
        buttons.Add(_add);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Margin = new Thickness(0, 0, 12, 0);
        _summary.Bind(TextBlock.ForegroundProperty, _summary.GetResourceObservable("BrushText"));
        footer.Children.Add(_summary);
        var btnRow = SectionCards.Buttons(buttons.ToArray());
        Grid.SetColumn(btnRow, 1);
        footer.Children.Add(btnRow);

        var shell = SectionCards.Shell(Title!, T("Отметьте, что нужно покупателю, и нажмите «В чек»", "Сатып алуучуга эмне керек экенин белгилеп, «Чекке» басыңыз",
            "Tick what the customer needs and press “To receipt”", "Müşterinin ihtiyacı olanları işaretleyip «Fişe»ye basın", "Xaridorga nima kerakligini belgilab, «Chekka»ni bosing"), content, footer);
        Content = shell;

        if (lists.Count > 0)
        {
            var index = selectedListId is null ? 0 : Math.Max(0, lists.ToList().FindIndex(l => l.Id == selectedListId));
            _listBox.SelectedIndex = index;
        }
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        _state.Clear();
        if (_listBox.SelectedIndex < 0 || _listBox.SelectedIndex >= _lists.Count)
            return;
        var list = _lists[_listBox.SelectedIndex];
        foreach (var plan in SchoolListPlanner.Plan(list, _lookup))
            _rows.Children.Add(BuildRow(plan));
        if (list.Items.Count == 0)
            _rows.Children.Add(SectionCards.Empty(this, T("В этом списке нет товаров.", "Бул тизмеде товарлар жок.", "This list has no products.", "Bu listede ürün yok.", "Bu ro'yxatda mahsulot yo'q.")));
        UpdateSummary();
    }

    private Control BuildRow(SchoolLinePlan plan)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinHeight = 44 };
        var check = new CheckBox
        {
            IsChecked = SchoolListPlanner.CheckedByDefault(plan),
            IsEnabled = plan.CanAdd,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 44,
        };
        var name = plan.Tile?.Title ?? plan.Item.Name;
        AutomationProperties.SetName(check, name);
        check.IsCheckedChanged += (_, _) => UpdateSummary();
        grid.Children.Add(check);

        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Text = name, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable(plan.CanAdd ? "BrushText" : "BrushTextSoft"));
        texts.Children.Add(title);
        if (Warning(plan) is { } warning)
        {
            var warn = new TextBlock { Text = warning, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            warn.Bind(TextBlock.ForegroundProperty, warn.GetResourceObservable(plan.Status == SchoolLineStatus.NotEnough ? "BrushTextSoft" : "BrushDangerOnSoft"));
            texts.Children.Add(warn);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var qty = SectionCards.Field(new TextBox { Text = plan.Item.Quantity.ToString(), Width = 72, TextAlignment = TextAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = plan.CanAdd, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) });
        AutomationProperties.SetName(qty, T("Количество", "Саны", "Quantity", "Miktar", "Miqdor") + ": " + name);
        qty.TextChanged += (_, _) => UpdateSummary();
        Grid.SetColumn(qty, 2);
        grid.Children.Add(qty);

        var price = new TextBlock
        {
            Text = plan.CanAdd ? MoneyText.Som(plan.UnitPrice) : "—", FontSize = 13, MinWidth = 96, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        price.Classes.Add("num");
        price.Bind(TextBlock.ForegroundProperty, price.GetResourceObservable("BrushTextSoft"));
        Grid.SetColumn(price, 3);
        grid.Children.Add(price);

        _state.Add(new RowState { Plan = plan, Check = check, Qty = qty });
        return grid;
    }

    private static string? Warning(SchoolLinePlan plan) => plan.Status switch
    {
        SchoolLineStatus.Missing => T("Товара нет в каталоге — пропущен", "Товар каталогто жок — өткөрүлдү", "Not in the catalog — skipped", "Katalogda yok — atlandı", "Katalogda yo'q — o'tkazib yuborildi"),
        SchoolLineStatus.OutOfStock => T("Нет в наличии (остаток 0)", "Кампада жок (калдык 0)", "Out of stock (0 left)", "Stokta yok (kalan 0)", "Omborda yo'q (qoldiq 0)"),
        SchoolLineStatus.NotEnough => T($"Остаток {plan.Stock:0.##} — меньше, чем нужно", $"Калдык {plan.Stock:0.##} — керектен аз", $"Only {plan.Stock:0.##} left — less than needed", $"Kalan {plan.Stock:0.##} — gerekenden az", $"Qoldiq {plan.Stock:0.##} — kerakdan kam"),
        _ => null,
    };

    private static int ParseQty(string? text) =>
        int.TryParse((text ?? "").Trim(), out var q) ? Math.Clamp(q, 0, SchoolListStore.MaxQuantity) : 0;

    private IEnumerable<RowState> Selected() => _state.Where(r => r.Plan.CanAdd && r.Check.IsChecked == true && ParseQty(r.Qty.Text) > 0);

    private void UpdateSummary()
    {
        var picked = Selected().ToList();
        var total = picked.Sum(r => r.Plan.UnitPrice * ParseQty(r.Qty.Text));
        _add.IsEnabled = picked.Count > 0;
        _summary.Text = picked.Count == 0
            ? T("Ничего не выбрано", "Эч нерсе тандалган жок", "Nothing selected", "Hiçbir şey seçilmedi", "Hech narsa tanlanmagan")
            : T($"Выбрано {picked.Count} · {MoneyText.Som(total)}", $"Тандалды {picked.Count} · {MoneyText.Som(total)}", $"Selected {picked.Count} · {MoneyText.Som(total)}", $"Seçildi {picked.Count} · {MoneyText.Som(total)}", $"Tanlandi {picked.Count} · {MoneyText.Som(total)}");
    }

    private void Commit()
    {
        Result = Selected().Select(r => (r.Plan.Tile!, ParseQty(r.Qty.Text))).ToList();
        Close(true);
    }

    /// <summary>Для проверок: выбранный список и строки окна.</summary>
    public IReadOnlyList<(SchoolLinePlan Plan, bool Checked, int Quantity)> RowsForTests() =>
        _state.Select(r => (r.Plan, r.Check.IsChecked == true, ParseQty(r.Qty.Text))).ToList();

    public void SelectListForTests(int index) => _listBox.SelectedIndex = index;

    public void CommitForTests() => Commit();
}
