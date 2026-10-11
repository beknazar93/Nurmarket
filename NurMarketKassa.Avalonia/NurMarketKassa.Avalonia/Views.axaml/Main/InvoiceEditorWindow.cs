using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-11, тестировщик (иишка.md, 2–6): «рядом с «Выполнить» кнопка «Отредактировать» — подробный редактор списка, который
/// выдаёт ИИ: наименование с накладной (можно заменить), наименование в чеке (по умолчанию пустое — берётся с накладной), количество
/// (если ИИ неверно прочитал рукописное), закупка и продажа. Всё — аккуратно, каждое в своей ячейке». Правятся строки прихода и новых
/// товаров; прочие действия карточки (списание, цены…) остаются как есть. Результат — новый список шагов (или null — «Отмена»).</summary>
public sealed class InvoiceEditorWindow : Window
{
    private sealed class Row
    {
        public required ProductActionPlan.Step Step { get; init; }
        public required TextBox InvoiceName { get; init; }
        public required TextBox ReceiptName { get; init; }
        public required TextBox Quantity { get; init; }
        public required TextBox Purchase { get; init; }
        public required TextBox Price { get; init; }
    }

    private readonly List<ProductActionPlan.Step> _steps;
    private readonly List<Row> _rows = new();
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Num(double? v, string format) => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : "";

    private static bool TryNum(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    public InvoiceEditorWindow(List<ProductActionPlan.Step> steps)
    {
        _steps = steps;
        Title = T("Редактор накладной", "Накладнойдун редактору", "Invoice editor", "Fatura düzenleyici", "Yuk xati muharriri");
        Width = 1120;
        Height = 640;
        MinWidth = 720;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("BrushPanel"));

        var hint = new TextBlock
        {
            Text = T("Поправьте то, что ИИ прочитал неверно. «Наименование в чеке» пустое — в чеке будет наименование с накладной.",
                "ИИ туура эмес окуганын оңдоңуз. «Чектеги аталышы» бош болсо — чекте накладнойдогу аталыш болот.",
                "Fix what the AI read wrong. If “Name on receipt” is empty, the invoice name is used on the receipt.",
                "Yapay zekânın yanlış okuduklarını düzeltin. «Fişteki ad» boşsa fişte faturadaki ad kullanılır.",
                "SI noto'g'ri o'qiganini tuzating. «Chekdagi nom» bo'sh bo'lsa, chekda yuk xatidagi nom bo'ladi."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));

        // № | Действие | Наименование с накладной | Наименование в чеке | Кол-во | Закупка | Продажа
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("40,110,2*,2*,90,110,110") };
        string[] headers =
        {
            "№", T("Действие", "Аракет", "Action", "İşlem", "Amal"),
            T("Наименование с накладной", "Накладнойдогу аталышы", "Name on the invoice", "Faturadaki ad", "Yuk xatidagi nom"),
            T("Наименование в чеке", "Чектеги аталышы", "Name on the receipt", "Fişteki ad", "Chekdagi nom"),
            T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"),
            T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"),
            T("Продажа", "Сатуу", "Sale price", "Satış", "Sotuv"),
        };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < headers.Length; c++)
        {
            var h = new TextBlock { Text = headers[c], FontWeight = FontWeight.Bold, FontSize = 12.5, Margin = new Thickness(4, 0, 4, 8), TextWrapping = TextWrapping.Wrap };
            h.Bind(TextBlock.ForegroundProperty, h.GetResourceObservable("BrushText"));
            Grid.SetColumn(h, c);
            grid.Children.Add(h);
        }

        var number = 0;
        foreach (var step in steps.Where(s => s.Op is "create" or "receive"))
        {
            number++;
            var r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBox Box(string text, string? watermark = null, bool right = false)
            {
                var box = new TextBox
                {
                    Text = text, Watermark = watermark, MinHeight = 36, Margin = new Thickness(4, 3),
                    HorizontalContentAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                };
                return box;
            }
            var invoiceName = Box(step.Product.Title);
            var receiptName = Box("", step.Product.Title);
            invoiceName.TextChanged += (_, _) => receiptName.Watermark = string.IsNullOrWhiteSpace(invoiceName.Text) ? step.Product.Title : invoiceName.Text;
            var price = step.Price ?? (step.Product.PriceValue > 0 ? step.Product.PriceValue : null);
            var row = new Row
            {
                Step = step, InvoiceName = invoiceName, ReceiptName = receiptName,
                Quantity = Box(Num(step.Qty, "0.###"), right: true),
                Purchase = Box(Num(step.Purchase, "0.##"), right: true),
                Price = Box(Num(price, "0.##"), right: true),
            };
            _rows.Add(row);
            var cells = new Control[]
            {
                new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) },
                new TextBlock
                {
                    Text = step.Op == "create" ? T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot") : T("Приход", "Кириш", "Receipt", "Giriş", "Kirim"),
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
                },
                invoiceName, receiptName, row.Quantity, row.Purchase, row.Price,
            };
            for (var c = 0; c < cells.Length; c++)
            {
                Grid.SetRow(cells[c], r);
                Grid.SetColumn(cells[c], c);
                grid.Children.Add(cells[c]);
            }
        }

        _error.Bind(TextBlock.ForegroundProperty, _error.GetResourceObservable("BrushDanger"));
        var save = UiKit.Primary(this, T("Сохранить изменения", "Өзгөртүүлөрдү сактоо", "Save changes", "Değişiklikleri kaydet", "O'zgarishlarni saqlash"));
        save.Height = 40;
        save.Click += (_, _) => Save();
        var cancel = UiKit.Ghost(this, T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Height = 40;
        cancel.Click += (_, _) => Close(null);
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { save, cancel } };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(20) };
        Grid.SetRow(hint, 0);
        var scroll = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        Grid.SetRow(_error, 2);
        Grid.SetRow(bottom, 3);
        root.Children.Add(hint);
        root.Children.Add(scroll);
        root.Children.Add(_error);
        root.Children.Add(bottom);
        Content = root;
    }

    private void Save()
    {
        var problems = new List<string>();
        var edited = new Dictionary<ProductActionPlan.Step, List<ProductActionPlan.Step>>(ReferenceEqualityComparer.Instance);
        var n = 0;
        foreach (var row in _rows)
        {
            n++;
            var name = (row.InvoiceName.Text ?? "").Trim();
            var receipt = (row.ReceiptName.Text ?? "").Trim();
            if (!TryNum(row.Quantity.Text, out var qty) || qty <= 0)
                problems.Add(T($"строка {n}: количество", $"{n}-сап: саны", $"line {n}: quantity", $"{n}. satır: miktar", $"{n}-qator: miqdor"));
            double? purchase = null, price = null;
            if (!string.IsNullOrWhiteSpace(row.Purchase.Text))
            {
                if (TryNum(row.Purchase.Text, out var p) && p >= 0)
                    purchase = p;
                else
                    problems.Add(T($"строка {n}: закупка", $"{n}-сап: сатып алуу", $"line {n}: purchase", $"{n}. satır: alış", $"{n}-qator: xarid"));
            }
            if (!string.IsNullOrWhiteSpace(row.Price.Text))
            {
                if (TryNum(row.Price.Text, out var p) && p >= 0)
                    price = p;
                else
                    problems.Add(T($"строка {n}: продажа", $"{n}-сап: сатуу", $"line {n}: sale price", $"{n}. satır: satış", $"{n}-qator: sotuv"));
            }
            var step = row.Step;
            var replaced = new List<ProductActionPlan.Step>();
            if (step.Op == "create")
            {
                // Новый товар: имя в чеке — то, что вписали; пусто — наименование с накладной.
                var finalName = receipt.Length > 0 ? receipt : name.Length > 0 ? name : step.Product.Title;
                var tile = new CatalogProductTileVm("", finalName, (price ?? 0).ToString("0.00", CultureInfo.InvariantCulture), false);
                replaced.Add(step with { Product = tile, Qty = qty, Purchase = purchase, Price = price });
            }
            else
            {
                // Приход к товару склада: цену продажи меняем, только если её поправили; имя в чеке — переименование товара.
                var unchangedPrice = step.Price is null && price is { } pv && Math.Abs(pv - step.Product.PriceValue) < 0.005;
                replaced.Add(step with { Qty = qty, Purchase = purchase, Price = unchangedPrice ? null : price });
                if (receipt.Length > 0 && !string.Equals(receipt, step.Product.Title.Trim(), StringComparison.Ordinal))
                    replaced.Add(new ProductActionPlan.Step("set_name", step.Product, null, receipt, null));
            }
            edited[step] = replaced;
        }
        if (problems.Count > 0)
        {
            _error.Text = T("Проверьте числа: ", "Сандарды текшериңиз: ", "Check the numbers: ", "Sayıları kontrol edin: ", "Raqamlarni tekshiring: ") + string.Join(", ", problems);
            _error.IsVisible = true;
            return;
        }
        var result = new List<ProductActionPlan.Step>();
        foreach (var step in _steps)
        {
            if (edited.TryGetValue(step, out var replaced))
                result.AddRange(replaced);
            else
                result.Add(step);
        }
        PosLogger.Log($"ИИ-советник: накладная поправлена в редакторе ({_rows.Count} строк).", "INFO");
        Close(result);
    }
}
