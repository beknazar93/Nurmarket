using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-06 (В12, С7): окно «Списать просроченное» со списком и галочками. Раньше списывался весь остаток всех просроченных одним подтверждением — вычеркнуть строку было нельзя.
/// Строки, где ПОСЛЕ даты годности был приход (в остатке может быть свежая партия), по умолчанию не отмечены и помечены предупреждением. «Списать» возвращает отмеченные
/// товары (<see cref="Result"/>); само списание делает <c>ExpiredWriteOff</c> тем же путём, что кнопка «Списать».</summary>
public sealed class ExpiredWriteOffDialog : Window
{
    private readonly IReadOnlyList<ExpiredWriteOffItem> _items;
    private readonly HashSet<string> _selected;
    private readonly Dictionary<string, CheckBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _note = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly Button _ok;
    private bool _setting;

    /// <summary>Отмеченные строки после «Списать»; null — окно закрыли без списания.</summary>
    public IReadOnlyList<ExpiredWriteOffItem>? Result { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public ExpiredWriteOffDialog(IReadOnlyList<ExpiredWriteOffItem> items)
    {
        _items = items;
        _selected = ExpiredWriteOffPlan.DefaultSelection(items);
        Title = T("Списать просроченное", "Мөөнөтү өткөндү эсептен чыгаруу", "Write off expired", "Süresi geçmişi düş", "Muddati o'tganni hisobdan chiqarish");
        SectionCards.AsModal(this, 912);

        var rows = new StackPanel { Spacing = 2 };
        rows.Children.Add(Row(null, T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Спишется", "Эсептен чыгат", "Write-off", "Düşülecek", "Chiqariladi"), T("Годен до", "Жарактуу", "Best before", "SKT", "Muddati"), soft: true));
        foreach (var item in items)
            rows.Children.Add(BuildRow(item));
        var scroll = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = rows };

        var all = SectionCards.Ghost(T("Отметить все", "Баарын белгилөө", "Select all", "Tümünü seç", "Hammasini belgilash"));
        all.Click += (_, _) => SetAll(true);
        var none = SectionCards.Ghost(T("Снять все", "Баарын алуу", "Clear all", "Tümünü kaldır", "Hammasini olib tashlash"));
        none.Click += (_, _) => SetAll(false);
        var bulk = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { all, none } };
        _note.Bind(TextBlock.ForegroundProperty, _note.GetResourceObservable("BrushTextSoft"));
        var root = new StackPanel { Spacing = 8, Children = { bulk, scroll, _note } };

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        _ok = SectionCards.Danger("");
        _ok.Click += (_, _) =>
        {
            Result = ExpiredWriteOffPlan.Selected(_items, _selected);
            Close();
        };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } };

        Content = SectionCards.Shell(Title,
            T("Отметьте, что списать. Причина акта — «Просрочка»; вернуть остаток можно только приходом или ревизией. Дата годности одна на карточку: где после неё был приход, в остатке может быть свежая партия — такие строки не отмечены.",
                "Эмнени эсептен чыгарууну белгилеңиз. Актынын себеби — «Просрочка»; калдыкты кирим же ревизия менен гана кайтарууга болот. Жарактуулук мөөнөтү карточкага бир: андан кийин кирим болсо, калдыкта жаңы партия болушу мүмкүн — мындай саптар белгиленген эмес.",
                "Tick what to write off. The reason is “Expired”; stock can only be restored by a delivery or a stock count. There is one expiry date per card: where a delivery came after it, the stock may be a fresh batch — those rows are not ticked.",
                "Düşülecekleri işaretleyin. Neden «Son kullanma tarihi geçmiş»; stok yalnızca mal kabulü veya sayımla geri getirilebilir. Kart başına tek tarih var: sonrasında mal kabulü olduysa stok taze parti olabilir — bu satırlar işaretli değil.",
                "Hisobdan chiqariladiganini belgilang. Sabab — «Muddati o'tgan»; qoldiqni faqat kirim yoki inventarizatsiya qaytaradi. Muddat kartochkaga bitta: undan keyin kirim bo'lgan bo'lsa, qoldiq yangi partiya bo'lishi mumkin — bunday qatorlar belgilanmagan."),
            root, footer);
        Refresh();
    }

    private Control BuildRow(ExpiredWriteOffItem item)
    {
        var box = new CheckBox { IsChecked = _selected.Contains(item.ProductId), VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(box, item.Mark.Name);
        box.IsCheckedChanged += (_, _) =>
        {
            if (_setting)
                return;
            if (box.IsChecked == true)
                _selected.Add(item.ProductId);
            else
                _selected.Remove(item.ProductId);
            Refresh();
        };
        _boxes[item.ProductId] = box;
        var row = Row(box, item.Mark.Name + (item.MayHaveFreshStock
                ? "\n" + T($"⚠ после даты годности был приход ({item.LastReceiptAt:dd.MM.yyyy}) — в остатке может быть свежий товар",
                    $"⚠ жарактуулук мөөнөтүнөн кийин кирим болгон ({item.LastReceiptAt:dd.MM.yyyy}) — калдыкта жаңы товар болушу мүмкүн",
                    $"⚠ a delivery came after the expiry date ({item.LastReceiptAt:dd.MM.yyyy}) — the stock may be fresh",
                    $"⚠ son kullanma tarihinden sonra mal kabulü var ({item.LastReceiptAt:dd.MM.yyyy}) — stok taze olabilir",
                    $"⚠ yaroqlilik muddatidan keyin kirim bo'lgan ({item.LastReceiptAt:dd.MM.yyyy}) — qoldiq yangi bo'lishi mumkin")
                : ""),
            ProductActions.Qty(item.Qty), item.Mark.Date.ToString("dd.MM.yyyy"), soft: false);
        return row;
    }

    private void SetAll(bool on)
    {
        _setting = true;
        _selected.Clear();
        foreach (var item in _items)
        {
            if (on)
                _selected.Add(item.ProductId);
            _boxes[item.ProductId].IsChecked = on;
        }
        _setting = false;
        Refresh();
    }

    private void Refresh()
    {
        var picked = ExpiredWriteOffPlan.Selected(_items, _selected);
        var cost = ExpiredWriteOffPlan.Cost(picked);
        _note.Text = T($"Отмечено {picked.Count} из {_items.Count}" + (cost > 0.005 ? $" · по закупке {MoneyText.Som(cost)}" : "") + ".",
            $"Белгиленди {picked.Count} / {_items.Count}" + (cost > 0.005 ? $" · сатып алуу баасы боюнча {MoneyText.Som(cost)}" : "") + ".",
            $"Ticked {picked.Count} of {_items.Count}" + (cost > 0.005 ? $" · {MoneyText.Som(cost)} at cost" : "") + ".",
            $"İşaretli {picked.Count} / {_items.Count}" + (cost > 0.005 ? $" · maliyet {MoneyText.Som(cost)}" : "") + ".",
            $"Belgilandi {picked.Count} / {_items.Count}" + (cost > 0.005 ? $" · xarid narxida {MoneyText.Som(cost)}" : "") + ".");
        _ok.IsEnabled = picked.Count > 0;
        _ok.Content = picked.Count > 0
            ? T($"Списать выбранное ({picked.Count})", $"Тандалганды эсептен чыгаруу ({picked.Count})", $"Write off selected ({picked.Count})", $"Seçileni düş ({picked.Count})", $"Tanlanganini hisobdan chiqarish ({picked.Count})")
            : T("Списать выбранное", "Тандалганды эсептен чыгаруу", "Write off selected", "Seçileni düş", "Tanlanganini hisobdan chiqarish");
        Avalonia.Automation.AutomationProperties.SetName(_ok, _ok.Content as string);
    }

    private Control Row(CheckBox? box, string name, string qty, string date, bool soft)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("32,*,90,100"), MinHeight = soft ? 0 : 36, Margin = new Thickness(0, 2) };
        if (box is not null)
        {
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);
        }
        void Cell(int col, string text, bool right)
        {
            var tb = new TextBlock
            {
                Text = text, FontSize = soft ? 12 : 13, FontWeight = soft ? FontWeight.SemiBold : FontWeight.Normal, TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center, TextAlignment = right ? TextAlignment.Right : TextAlignment.Left, Margin = new Thickness(0, 0, 10, 0),
            };
            tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
        Cell(1, name, false);
        Cell(2, qty, true);
        Cell(3, date, false);
        return grid;
    }
}
