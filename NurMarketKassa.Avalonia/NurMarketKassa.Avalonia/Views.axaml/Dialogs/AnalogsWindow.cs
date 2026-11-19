using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>
/// 2026-11-20 (ТЗ «Сфера Автозапчасти», АЗ6): окно «Аналоги». Заголовок — исходный номер; список карточек с общим номером или связью (<see cref="PartAnalogs"/>)
/// по возрастанию цены: название, бренд · номер · ячейка, значок состояния, остаток, цена. Нет в наличии — строка бледная со словом «Нет».
/// Enter или двойное нажатие кладёт выбранную строку в чек. Окно собрано в коде, цвета — из темы, ширина 672 (как карточка товара).
/// </summary>
public sealed class AnalogsWindow : Window
{
    private readonly ListBox _list = new() { SelectionMode = SelectionMode.Single, MaxHeight = 360 };
    private readonly Action<CatalogProductTileVm> _add;
    private bool _added;

    public AnalogsWindow(CatalogProductTileVm source, IReadOnlyList<CatalogProductTileVm> analogs, Action<CatalogProductTileVm> add)
    {
        _add = add;
        Title = Tr.T("Аналоги", "Аналогдор", "Analogues", "Muadiller", "Analoglar");
        SectionCards.AsModal(this, 672);

        var number = !string.IsNullOrWhiteSpace(source.Article) ? source.Article!.Trim() : source.Part?.Numbers().FirstOrDefault() ?? "";
        var subtitle = (number.Length > 0 ? number + " · " : "") + source.Title;

        var sorted = analogs.OrderBy(p => LocalCartService.ParsePrice(p.PriceLine)).ToList();
        Control body;
        if (sorted.Count == 0)
        {
            body = SectionCards.Empty(this, Tr.T("Аналогов не найдено. Добавьте их в карточке товара: «Кросс-номера» или «Выбрать аналоги».",
                "Аналогдор табылган жок. Аларды товар картасына кошуңуз: «Кросс-номерлер» же «Аналогдорду тандоо».",
                "No analogues found. Add them in the product card: “Cross numbers” or “Choose analogues”.",
                "Muadil bulunamadı. Ürün kartında ekleyin: «Çapraz numaralar» veya «Muadil seç».",
                "Analoglar topilmadi. Ularni mahsulot kartasida qo'shing: «Kross-raqamlar» yoki «Analoglarni tanlash»."));
        }
        else
        {
            _list.ItemsSource = sorted;
            _list.ItemTemplate = new FuncDataTemplate<CatalogProductTileVm>((p, _) => p is null ? new Panel() : BuildRow(p));
            _list.SelectedIndex = sorted.FindIndex(p => p.Quantity > 0) is var first and >= 0 ? first : 0;
            _list.DoubleTapped += (_, _) => AddSelected();
            _list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    AddSelected();
                }
            };
            body = _list;
        }

        var close = SectionCards.Cancel(Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var buttons = sorted.Count == 0
            ? SectionCards.Buttons(close)
            : SectionCards.Buttons(close, BuildAddButton());
        Content = SectionCards.Shell(Tr.T("Аналоги", "Аналогдор", "Analogues", "Muadiller", "Analoglar") + " (" + sorted.Count + ")", subtitle, body, buttons);
        Opened += (_, _) => _list.Focus();
    }

    private Button BuildAddButton()
    {
        var add = SectionCards.Primary(Tr.T("Добавить в чек", "Чекке кошуу", "Add to receipt", "Fişe ekle", "Chekka qo'shish"), isDefault: false);
        add.Click += (_, _) => AddSelected();
        return add;
    }

    private void AddSelected()
    {
        // Двойное нажатие (или Enter + клик) не добавляет товар дважды.
        if (_added || _list.SelectedItem is not CatalogProductTileVm product)
            return;
        _added = true;
        _add(product);
        Close();
    }

    private Control BuildRow(CatalogProductTileVm p)
    {
        var inStock = p.Quantity > 0 || p.IsService;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), MinHeight = 52, Margin = new Thickness(4, 2) };
        if (!inStock)
            grid.Opacity = 0.5;

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
        left.Children.Add(new TextBlock { Text = p.Title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        var sub = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.Brand))
            sub.Add(p.Brand!.Trim());
        if (p.PartLine.Length > 0)
            sub.Add(p.PartLine);
        if (sub.Count > 0)
        {
            var subText = new TextBlock { Text = string.Join(" · ", sub), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            subText.Bind(TextBlock.ForegroundProperty, subText.GetResourceObservable("BrushTextSoft"));
            left.Children.Add(subText);
        }
        grid.Children.Add(left);

        if (p.HasPartBadge)
        {
            var pill = SectionCards.Pill(this, p.PartBadgeText);
            pill.Margin = new Thickness(0, 0, 12, 0);
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);
        }

        var stock = new TextBlock
        {
            Text = inStock ? p.StockWithUnitText : Tr.T("Нет", "Жок", "None", "Yok", "Yo'q"),
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), MinWidth = 56, TextAlignment = TextAlignment.Right,
        };
        stock.Bind(TextBlock.ForegroundProperty, stock.GetResourceObservable("BrushTextSoft"));
        Grid.SetColumn(stock, 2);
        grid.Children.Add(stock);

        var price = new TextBlock
        {
            Text = LocalCartService.ParsePrice(p.PriceLine).ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " " + Tr.T("сом", "сом", "som", "som", "so'm"),
            FontSize = 15, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 96, TextAlignment = TextAlignment.Right,
        };
        price.Bind(TextBlock.ForegroundProperty, price.GetResourceObservable("BrushCatalogPrice"));
        Grid.SetColumn(price, 3);
        grid.Children.Add(price);
        return grid;
    }
}
