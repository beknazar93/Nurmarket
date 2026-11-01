using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Application;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-10-30: таблица накладной — виртуальный список (ListBox), закреплённая шапка колонок, итог внизу плитками, поиск и чипы-фильтры.
// Колонки по макету: №, Код, Штрихкод, Наименование, Ед., Количество, Цена покупки (+ маркер ↑), Сумма, Наценка %, Цена реализации, Прибыль с шт., Примечание.
// 1024×600: скрыты Код, Штрихкод, Ед., Прибыль. Без права на закупочные цены денежных колонок нет.
public sealed partial class InvoiceDocumentWindow
{
    private readonly Stack<(InvoiceRow Row, double Old)> _undo = new();
    private bool _undoing;
    private Grid _headRow = new();

    private sealed record Col(string Id, string Width, string Title, bool Right = true, bool Money = false, bool Wide = false);

    private List<Col> Columns()
    {
        var all = new List<Col>
        {
            new("no", "40", "№", false),
            new("code", "70", T("Код", "Коду", "Code", "Kod", "Kod"), false, Wide: true),
            new("bar", "118", T("Штрихкод", "Штрих-код", "Barcode", "Barkod", "Shtrix-kod"), false, Wide: true),
            new("name", "*", T("Наименование", "Аталышы", "Name", "Ad", "Nomi"), false),
            new("unit", "44", T("Ед.", "Бирд.", "Unit", "Birim", "Birl."), false, Wide: true),
            new("qty", _edit ? "200" : "88", /* 2026-11-01: в режиме правки в ячейке «−» [число] «+» «🗑» */ T("Кол-во", "Саны", "Qty", "Adet", "Soni")),
            new("buy", "112", T("Цена покупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), Money: true),
            new("sum", "104", T("Сумма", "Сумма", "Amount", "Tutar", "Summa"), Money: true),
            new("mk", _edit ? "92" : "84", T("Наценка %", "Үстөк %", "Markup %", "Kâr marjı %", "Ustama %"), Money: true),
            new("sale", _edit ? "104" : "104", T("Цена реализации", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotish narxi"), Money: true),
            new("pf", "96", T("Прибыль с шт.", "Даанасынан пайда", "Profit/unit", "Birim kâr", "Dona foydasi"), Money: true, Wide: true),
            new("note", _compact ? "128" : "170", T("Примечание", "Эскертүү", "Note", "Not", "Izoh"), false),
        };
        return all.Where(c => (_showMoney || !c.Money) && (!_compact || !c.Wide)).ToList();
    }

    private string GridDefs() => "4," + string.Join(",", Columns().Select(c => c.Width));

    private Control BuildTable()
    {
        var box = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        _headRow = new Grid { ColumnDefinitions = new ColumnDefinitions(GridDefs()), Margin = new Thickness(0, 0, 14, 0), MinHeight = 34 };
        FillHeader();
        var headBox = new Border { Child = _headRow, Padding = new Thickness(4, 4), CornerRadius = new CornerRadius(8, 8, 0, 0) };
        headBox.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushPanelSoft"));
        box.Children.Add(headBox);

        _list.ItemTemplate = new FuncDataTemplate<InvoiceRow>((row, _) => row is null ? new Border() : BuildRow(row), true);
        _list.ItemsSource = _shown;
        _list.Padding = new Thickness(0);
        _list.BorderThickness = new Thickness(1);
        _list.Bind(BorderBrushProperty, this.GetResourceObservable("BrushBorder"));
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        _list.ItemContainerTheme = null;
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0.0) } });
        Grid.SetRow(_list, 1);
        box.Children.Add(_list);
        return box;
    }

    private void FillHeader()
    {
        _headRow.Children.Clear();
        _headRow.ColumnDefinitions = new ColumnDefinitions(GridDefs());
        var i = 1;
        foreach (var c in Columns())
        {
            var t = new TextBlock
            {
                Text = c.Title, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = c.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0),
            };
            t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            Grid.SetColumn(t, i++);
            _headRow.Children.Add(t);
        }
    }

    private TextBlock Cell(string bindPath, bool right, string brush = "BrushText", FontWeight? weight = null, bool wrap = false)
    {
        var t = new TextBlock
        {
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Margin = new Thickness(6, 0), FontWeight = weight ?? FontWeight.Normal,
        };
        if (right)
            t.Classes.Add("num");
        t.Bind(TextBlock.TextProperty, new Binding(bindPath));
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(brush));
        return t;
    }

    private TextBox Editor(string bindPath, string tag)
    {
        var tb = new TextBox
        {
            Tag = tag, FontSize = 13, MinHeight = 30, Height = 32, Padding = new Thickness(6, 0), Margin = new Thickness(2, 0),
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        tb.Classes.Add("num");
        tb.Bind(TextBox.TextProperty, new Binding(bindPath) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus });
        tb.KeyDown += OnCellKeyDown;
        tb.GotFocus += (_, _) => Dispatcher.UIThread.Post(() => tb.SelectAll());
        return tb;
    }

    private Control BuildRow(InvoiceRow row)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(GridDefs()), MinHeight = _compact ? 40 : 44, Margin = new Thickness(0, 0, 0, 0) };
        var stripe = new Border { Width = 3, Margin = new Thickness(0, 2), CornerRadius = new CornerRadius(2) };
        stripe.Bind(Border.BackgroundProperty, new Binding(nameof(InvoiceRow.StripeKey)) { Converter = StripeBrush.Instance, ConverterParameter = this });
        g.Children.Add(stripe);
        var i = 1;
        foreach (var c in Columns())
        {
            Control cell = c.Id switch
            {
                "no" => Cell(nameof(InvoiceRow.NoText), false, "BrushTextSoft"),
                "code" => Cell(nameof(InvoiceRow.Code), false, "BrushTextSoft"),
                "bar" => Cell(nameof(InvoiceRow.Barcode), false, "BrushTextSoft"),
                "name" => NameCell(row),
                "unit" => Cell(nameof(InvoiceRow.Unit), false, "BrushTextSoft"),
                "qty" => _edit ? QtyEditor(row) : Cell(nameof(InvoiceRow.QtyText), true, weight: FontWeight.SemiBold),
                "buy" => _edit && _patchMode && !row.ReadOnlyRow ? Editor(nameof(InvoiceRow.PurchaseEditText), "buy") : PurchaseCell(), // 2026-11-01: цена закупки правится при серверном PATCH
                "sum" => Cell(nameof(InvoiceRow.SumText), true),
                "mk" => _edit && !row.ReadOnlyRow ? Editor(nameof(InvoiceRow.MarkupText), "mk") : Cell(nameof(InvoiceRow.MarkupText), true),
                "sale" => _edit && !row.ReadOnlyRow ? Editor(nameof(InvoiceRow.SaleText), "sale") : Cell(nameof(InvoiceRow.SaleText), true),
                "pf" => Cell(nameof(InvoiceRow.ProfitText), true, "BrushTextSoft"),
                _ => Cell(nameof(InvoiceRow.NoteText), false, "BrushTextSoft", wrap: true),
            };
            if (c.Id is "mk" or "sale" && !_edit)
                cell.Bind(TextBlock.ForegroundProperty, new Binding(nameof(InvoiceRow.FlagKey)) { Converter = ThemeBrush.Instance, ConverterParameter = this });
            Grid.SetColumn(cell, i++);
            g.Children.Add(cell);
        }
        // серая строка (количество 0) и зачёркнутая (удалена при правке)
        g.Bind(OpacityProperty, new Binding(nameof(InvoiceRow.Dim)) { Converter = DimOpacity.Instance });
        // 2026-11-01: изменённая в этой правке строка подсвечена (было → стало видно в «было …» под количеством)
        g.Bind(Panel.BackgroundProperty, new Binding(nameof(InvoiceRow.RowChanged)) { Converter = ChangedBackground.Instance, ConverterParameter = this });
        var line = new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
        line.Bind(Border.BackgroundProperty, this.GetResourceObservable("BrushBorder"));
        Grid.SetColumnSpan(line, 40);
        g.Children.Add(line);
        return g;
    }

    /// <summary>2026-11-01: шаг кнопок «−»/«+»: 1 для штучных, 0,1 для весовых (кг, л, м) и дробных.</summary>
    private static double StepFor(InvoiceRow row) =>
        row.Unit.Trim().ToLowerInvariant() is "кг" or "kg" or "л" or "l" or "м" or "m" or "г" || Math.Abs(row.OrigQty - Math.Round(row.OrigQty)) > 0.0005 ? 0.1 : 1;

    private Button QtyButton(string text, string tip, Action click)
    {
        var b = SectionCards.Secondary(text);
        b.Width = b.MinWidth = 36;
        b.Height = b.MinHeight = 36; // цель нажатия не меньше 36 px
        b.Padding = new Thickness(0);
        b.Margin = new Thickness(1, 0);
        b.FontSize = 16;
        b.HorizontalContentAlignment = HorizontalAlignment.Center;
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Удалить позицию = количество 0: остаток уменьшается, поставщику оформляется возврат (для добавляемого в этой правке товара — просто убирается из списка).</summary>
    private async Task DeleteLineAsync(InvoiceRow row)
    {
        if (row.IsPendingAdd)
        {
            _rows.Remove(row);
            Refresh();
            return;
        }
        var ok = await PosConfirmDialog.ShowModalAsync(this, T("Удалить позицию?", "Позицияны өчүрөбүзбү?", "Delete the line?", "Satır silinsin mi?", "Pozitsiya o'chirilsinmi?"),
            T($"«{row.Name}»: количество станет 0. Остаток на складе уменьшится, поставщику оформится возврат по этой накладной. Саму накладную это не удаляет, а правка запишется в журнал.",
                $"«{row.Name}»: саны 0 болот. Кампадагы калдык азаят, жеткирүүчүгө ушул накладной боюнча кайтаруу түзүлөт. Накладнойдун өзүн өчүрбөйт, оңдоо журналга жазылат.",
                $"“{row.Name}”: the quantity becomes 0. Stock is reduced and a return to the supplier is created against this invoice. The invoice itself is not deleted; the edit is written to the journal.",
                $"«{row.Name}»: miktar 0 olur. Stok azalır, bu faturaya karşı tedarikçiye iade oluşturulur. Faturanın kendisi silinmez; düzenleme günlüğe yazılır.",
                $"«{row.Name}»: miqdor 0 bo'ladi. Ombor qoldig'i kamayadi, shu yuk xati bo'yicha yetkazib beruvchiga qaytarish rasmiylashtiriladi. Yuk xatining o'zi o'chirilmaydi; tahrir jurnalga yoziladi."),
            T("Удалить позицию", "Позицияны өчүрүү", "Delete the line", "Satırı sil", "Pozitsiyani o'chirish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Danger).ConfigureAwait(true);
        if (ok)
            row.Qty = 0;
    }

    /// <summary>Подсвечивает изменённую строку (было → стало) мягким фоном.</summary>
    private sealed class ChangedBackground : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly ChangedBackground Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is true && parameter is Control host && host.TryFindResource("BrushAccentSoft", host.ActualThemeVariant, out var res) && res is IBrush brush ? brush : Brushes.Transparent;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => BindingOperations.DoNothing;
    }

    /// <summary>2026-11-01: введённое в ячейку число записывается в строку до нажатия «−»/«+» (иначе оно теряется при потере фокуса).</summary>
    private void CommitFocusedCell()
    {
        if (FocusManager?.GetFocusedElement() is TextBox tb)
            CommitCell(tb);
    }

    /// <summary>2026-11-01 (просьба владельца): название товара и под ним мелким шрифтом (12 px) отметка последней правки позиции — «✎ Имя (роль), 01.11 15:42 · количество 10 → 8».
    /// Нажатие (или наведение) показывает всю историю правок этой позиции. Не правилась — отметки нет.</summary>
    private Control NameCell(InvoiceRow row)
    {
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(Cell(nameof(InvoiceRow.Name), false, weight: FontWeight.SemiBold));
        var mark = new TextBlock { FontSize = 12, Margin = new Thickness(6, 0), TextWrapping = TextWrapping.Wrap, Cursor = new Cursor(StandardCursorType.Hand) };
        mark.Bind(TextBlock.TextProperty, new Binding(nameof(InvoiceRow.MarkText)));
        mark.Bind(Visual.IsVisibleProperty, new Binding(nameof(InvoiceRow.HasMark)));
        mark.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        ToolTip.SetTip(mark, row.MarkTip);
        mark.PointerPressed += (_, _) => _ = ShowLineHistoryAsync(row);
        sp.Children.Add(mark);
        return sp;
    }

    private async Task ShowLineHistoryAsync(InvoiceRow row)
    {
        if (row.LineEdits.Count == 0)
            return;
        await PosDialogHost.ShowAsync(new LineHistoryDialog(row.Name, _docNumber, row.LineEdits), this).ConfigureAwait(true);
    }

    private Control QtyEditor(InvoiceRow row)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var qtyBox = Editor(nameof(InvoiceRow.QtyText), "qty");
        qtyBox.Width = 64;
        qtyBox.MinHeight = 36;
        qtyBox.Height = 36;
        if (row.ReadOnlyRow)
        {
            qtyBox.IsEnabled = false;
            ToolTip.SetTip(qtyBox, T("Товар из дополнительного прихода: уменьшить его можно возвратом поставщику в карточке товара.", "Кошумча кирүүдөн келген товар: аны товардын карточкасында жеткирүүчүгө кайтаруу менен азайтууга болот.",
                "A product from an additional receipt: reduce it with a return to the supplier in the product card.", "Ek girişten gelen ürün: ürün kartında tedarikçiye iadeyle azaltılabilir.", "Qo'shimcha kirimdan mahsulot: uni mahsulot kartasida yetkazib beruvchiga qaytarish bilan kamaytirish mumkin."));
            stack.Children.Add(qtyBox);
            return stack;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(QtyButton("−", T("Уменьшить количество (возврат поставщику)", "Санды азайтуу (жеткирүүчүгө кайтаруу)", "Decrease quantity (return to the supplier)", "Miktarı azalt (tedarikçiye iade)", "Miqdorni kamaytirish (yetkazib beruvchiga qaytarish)"),
            () => { CommitFocusedCell(); row.Qty = Math.Max(0, row.Qty - StepFor(row)); }));
        buttons.Children.Add(qtyBox);
        buttons.Children.Add(QtyButton("+", T("Увеличить количество (дополнительный приход)", "Санды көбөйтүү (кошумча кирүү)", "Increase quantity (additional receipt)", "Miktarı artır (ek giriş)", "Miqdorni oshirish (qo'shimcha kirim)"),
            () => { CommitFocusedCell(); row.Qty += StepFor(row); }));
        buttons.Children.Add(QtyButton("🗑", T("Удалить позицию (количество 0)", "Позицияны өчүрүү (саны 0)", "Delete the line (quantity 0)", "Satırı sil (miktar 0)", "Pozitsiyani o'chirish (miqdor 0)"), () => _ = DeleteLineAsync(row)));
        stack.Children.Add(buttons);
        var was = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        was.Bind(TextBlock.TextProperty, new Binding(nameof(InvoiceRow.WasText)));
        was.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(was);
        return stack;
    }

    private Control PurchaseCell()
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Spacing = 4, Margin = new Thickness(6, 0) };
        var mark = new TextBlock { FontSize = 12, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
        mark.Bind(TextBlock.TextProperty, new Binding(nameof(InvoiceRow.PurchaseMark)));
        mark.Bind(TextBlock.ForegroundProperty, new Binding(nameof(InvoiceRow.MarkKey)) { Converter = ThemeBrush.Instance, ConverterParameter = this });
        var val = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        val.Classes.Add("num");
        val.Bind(TextBlock.TextProperty, new Binding(nameof(InvoiceRow.PurchaseText)));
        val.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        sp.Children.Add(mark);
        sp.Children.Add(val);
        return sp;
    }

    // ------------------------------------------------------------------ конвертеры кистей (ключ ресурса темы → кисть)

    private sealed class ThemeBrush : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly ThemeBrush Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var key = value as string ?? "BrushText";
            if (parameter is Control host && host.TryFindResource(key, host.ActualThemeVariant, out var res) && res is IBrush brush)
                return brush;
            return null;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => BindingOperations.DoNothing;
    }

    private sealed class StripeBrush : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly StripeBrush Instance = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var key = value as string ?? "";
            if (key.Length == 0)
                return Brushes.Transparent;
            return ThemeBrush.Instance.Convert(key, targetType, parameter, culture);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => BindingOperations.DoNothing;
    }

    private sealed class DimOpacity : Avalonia.Data.Converters.IValueConverter
    {
        public static readonly DimOpacity Instance = new();
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => value is true ? 0.55 : 1.0;
        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => BindingOperations.DoNothing;
    }

    // ------------------------------------------------------------------ фильтры, чипы, итог

    private bool Matches(InvoiceRow r)
    {
        var f = r.Facts;
        var chip = _chip switch
        {
            "below" => InvoiceMath.IsBelowCost(f),
            "low" => InvoiceMath.IsLowMargin(f),
            "raised" => InvoiceMath.IsRaised(f),
            "new" => r.IsNew,
            "zero" => r.Qty <= 0.0005,
            "attention" => r.NeedsAttention,
            "changed" => r.Changed,
            "error" => r.HasError,
            "returned" => r.ReturnedQty > 0.0005,
            "removed" => r.IsRemoved,
            _ => true,
        };
        if (!chip)
            return false;
        var q = (_search.Text ?? "").Trim();
        return q.Length == 0 || SearchMatch.Matches(q, r.Name, r.Barcode, r.Code);
    }

    private void ApplyFilter()
    {
        _shown = _rows.Where(Matches).ToList();
        _list.ItemsSource = _shown;
        UpdateCounter();
        RebuildChips();
    }

    private void UpdateCounter()
    {
        _counter.Text = T($"Показано {_shown.Count} из {_rows.Count}", $"Көрсөтүлдү {_shown.Count} / {_rows.Count}", $"Showing {_shown.Count} of {_rows.Count}", $"{_shown.Count} / {_rows.Count} gösteriliyor", $"Ko'rsatildi {_shown.Count} / {_rows.Count}");
        if (_shown.Count == 0)
            _counter.Text += " · " + (_rows.Count == 0
                ? T("в накладной нет позиций — проверьте связь", "накладнойдо позиция жок — байланышты текшериңиз", "no lines — check the connection", "satır yok — bağlantıyı kontrol edin", "pozitsiya yo'q — aloqani tekshiring")
                : T("ничего не найдено: проверьте раскладку или снимите фильтр", "эч нерсе табылган жок: тилкемени текшериңиз же чыпканы алыңыз", "nothing found: check the layout or clear the filter", "bulunamadı: düzeni kontrol edin veya filtreyi kaldırın", "hech narsa topilmadi: tartibni tekshiring yoki filtrni olib tashlang"));
    }

    private void RebuildChips()
    {
        _chips.Children.Clear();
        var defs = new List<(string Id, string Title, Func<InvoiceRow, bool> Test)>
        {
            ("all", T("Все", "Баары", "All", "Tümü", "Hammasi"), _ => true),
        };
        if (_showMoney)
        {
            defs.Add(("attention", T("Требуют внимания", "Көңүл буруу керек", "Need attention", "Dikkat gerektirir", "E'tibor kerak"), r => r.NeedsAttention));
            defs.Add(("below", T("Ниже закупки", "Сатып алуудан төмөн", "Below cost", "Maliyetin altında", "Xariddan past"), r => InvoiceMath.IsBelowCost(r.Facts)));
            defs.Add(("low", T("Маржа < 10 %", "Маржа < 10 %", "Margin < 10 %", "Marj < %10", "Marja < 10 %"), r => InvoiceMath.IsLowMargin(r.Facts)));
            defs.Add(("raised", T("Закупка выросла", "Сатып алуу кымбаттады", "Purchase price up", "Alış fiyatı arttı", "Xarid narxi oshdi"), r => InvoiceMath.IsRaised(r.Facts)));
            defs.Add(("new", T("Новые", "Жаңылар", "New", "Yeni", "Yangi"), r => r.IsNew));
        }
        defs.Add(("zero", T("Без количества", "Саны жок", "No quantity", "Miktar yok", "Miqdor yo'q"), r => r.Qty <= 0.0005));
        defs.Add(("returned", T("С возвратом", "Кайтаруу менен", "With return", "İadeli", "Qaytarish bilan"), r => r.ReturnedQty > 0.0005));
        defs.Add(("removed", T("Удалённые", "Өчүрүлгөндөр", "Removed", "Silinenler", "O'chirilgan"), r => r.IsRemoved)); // 2026-11-01: удалённые позиции остаются в списке с пометкой «удалил …»
        if (_edit)
        {
            defs.Add(("changed", T("Изменённые", "Өзгөргөндөр", "Changed", "Değişenler", "O'zgargan"), r => r.Changed));
            defs.Add(("error", T("С ошибкой", "Ката менен", "With error", "Hatalı", "Xato bilan"), r => r.HasError));
        }
        foreach (var (id, title, test) in defs)
        {
            var n = id == "all" ? _rows.Count : _rows.Count(test);
            if (id != "all" && n == 0 && _chip != id)
                continue;
            var chip = UiKit.Chip(this, $"{title} {n}", _chip == id);
            chip.MinHeight = 36;
            chip.Padding = new Thickness(12, 4);
            chip.FontSize = 12.5;
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Click += (_, _) =>
            {
                _chip = id;
                ApplyFilter();
            };
            _chips.Children.Add(chip);
        }
    }

    /// <summary>Пересчитать плитки итога (7 штук) и счётчик — по всем строкам, а не только показанным.</summary>
    private void Recalc()
    {
        var t = InvoiceMath.Totals(_rows.Select(r => r.Facts));
        _tiles.Children.Clear();
        var approx = _rows.Any(r => r.SaleEstimated && !r.PriceTouched && r.Sale > 0) ? "≈ " : "";
        _tiles.Children.Add(UiKit.Kpi(T("Позиций", "Позиция", "Lines", "Satır", "Pozitsiya"), t.Positions.ToString(Ru)));
        _tiles.Children.Add(UiKit.Kpi(T("Штук", "Даана", "Units", "Adet", "Dona"), t.Units.ToString("0.###", Ru)));
        if (_showMoney)
        {
            _tiles.Children.Add(UiKit.Kpi(T("Сумма закупки", "Сатып алуу суммасы", "Purchase total", "Alış toplamı", "Xarid summasi"), MoneyText.Som(t.PurchaseSum)));
            _tiles.Children.Add(UiKit.Kpi(T("Ср. наценка", "Орточо үстөк", "Avg markup", "Ort. marj", "O'rt. ustama"), t.AvgMarkup is { } avg ? approx + avg.ToString("0.##", Ru) + " %" : "—"));
            _tiles.Children.Add(UiKit.Kpi(T("Ожидаемая выручка", "Күтүлгөн түшүм", "Expected revenue", "Beklenen ciro", "Kutilayotgan tushum"), approx + MoneyText.Som(t.Revenue)));
            _tiles.Children.Add(UiKit.Kpi(T("Прибыль", "Пайда", "Profit", "Kâr", "Foyda"), approx + MoneyText.Som(t.Profit), t.Profit < 0));
            var attention = UiKit.Kpi(T("Требуют внимания ›", "Көңүл буруу керек ›", "Need attention ›", "Dikkat gerektirir ›", "E'tibor kerak ›"), t.Attention.ToString(Ru), t.Attention > 0);
            attention.Cursor = new Avalonia.Input.Cursor(StandardCursorType.Hand);
            attention.PointerPressed += (_, _) =>
            {
                _chip = _chip == "attention" ? "all" : "attention";
                ApplyFilter();
            };
            ToolTip.SetTip(attention, T("Ниже закупки, маржа меньше 10 % или нет цены закупки. Нажмите — оставить в списке только их.", "Сатып алуудан төмөн, маржа 10 %дан аз же баасы жок. Басыңыз — тизмеде алар гана калат.",
                "Below cost, margin under 10 % or no purchase price. Click to show only them.", "Maliyetin altında, marj %10'dan az veya alış fiyatı yok. Yalnızca bunları göstermek için tıklayın.", "Xariddan past, marja 10 % dan kam yoki xarid narxi yo'q. Faqat ularni ko'rsatish uchun bosing."));
            _tiles.Children.Add(attention);
        }
        _tileLine.Text = _showMoney
            ? T($"Позиций {t.Positions} · Штук {t.Units.ToString("0.###", Ru)} · Закупка {MoneyText.Som(t.PurchaseSum)} · Ср. наценка {(t.AvgMarkup is { } av ? approx + av.ToString("0.##", Ru) + " %" : "—")} · Выручка {approx}{MoneyText.Som(t.Revenue)} · Прибыль {approx}{MoneyText.Som(t.Profit)} · Внимание {t.Attention}",
                $"Позиция {t.Positions} · Даана {t.Units.ToString("0.###", Ru)} · Сатып алуу {MoneyText.Som(t.PurchaseSum)} · Орт. үстөк {(t.AvgMarkup is { } av2 ? approx + av2.ToString("0.##", Ru) + " %" : "—")} · Түшүм {approx}{MoneyText.Som(t.Revenue)} · Пайда {approx}{MoneyText.Som(t.Profit)} · Көңүл {t.Attention}",
                $"Lines {t.Positions} · Units {t.Units.ToString("0.###", Ru)} · Purchase {MoneyText.Som(t.PurchaseSum)} · Avg markup {(t.AvgMarkup is { } av3 ? approx + av3.ToString("0.##", Ru) + " %" : "—")} · Revenue {approx}{MoneyText.Som(t.Revenue)} · Profit {approx}{MoneyText.Som(t.Profit)} · Attention {t.Attention}",
                $"Satır {t.Positions} · Adet {t.Units.ToString("0.###", Ru)} · Alış {MoneyText.Som(t.PurchaseSum)} · Ort. marj {(t.AvgMarkup is { } av4 ? approx + av4.ToString("0.##", Ru) + " %" : "—")} · Ciro {approx}{MoneyText.Som(t.Revenue)} · Kâr {approx}{MoneyText.Som(t.Profit)} · Dikkat {t.Attention}",
                $"Pozitsiya {t.Positions} · Dona {t.Units.ToString("0.###", Ru)} · Xarid {MoneyText.Som(t.PurchaseSum)} · O'rt. ustama {(t.AvgMarkup is { } av5 ? approx + av5.ToString("0.##", Ru) + " %" : "—")} · Tushum {approx}{MoneyText.Som(t.Revenue)} · Foyda {approx}{MoneyText.Som(t.Profit)} · E'tibor {t.Attention}")
            : T($"Позиций {t.Positions} · Штук {t.Units.ToString("0.###", Ru)}", $"Позиция {t.Positions} · Даана {t.Units.ToString("0.###", Ru)}", $"Lines {t.Positions} · Units {t.Units.ToString("0.###", Ru)}", $"Satır {t.Positions} · Adet {t.Units.ToString("0.###", Ru)}", $"Pozitsiya {t.Positions} · Dona {t.Units.ToString("0.###", Ru)}");
        UpdateCounter();
    }

    private void Refresh()
    {
        ApplyFilter();
        Recalc();
    }

    private void OnQtyEdited(InvoiceRow row, double old)
    {
        if (!_undoing)
            _undo.Push((row, old));
        row.ErrorText = null;
        Recalc();
        if (_chip == "changed")
            ApplyFilter();
        else
            RebuildChips();
    }

    private void UndoLast()
    {
        if (_undo.Count == 0)
            return;
        var (row, old) = _undo.Pop();
        _undoing = true;
        try { row.SetQtySilently(old); }
        finally { _undoing = false; }
        row.ErrorText = null;
        Recalc();
    }

    // ------------------------------------------------------------------ Insert / F2 / навигация по ячейкам

    /// <summary>Insert: фокус в поиск по накладной. Новые позиции в готовую накладную добавляются после обновления сервера (Б-лайт правит только количество).</summary>
    private void FocusSearchForInsert()
    {
        if (_edit && IsOwner())
        {
            _ = AddItemAsync(); // 2026-11-01: Insert в режиме правки открывает «Добавить товар» (дополнительный приход)
            return;
        }
        _search.Focus();
        _search.SelectAll();
        if (_edit)
            Say(ReceiptEditService.NeedsServerText + ": " + T("новую позицию в проведённую накладную добавить пока нельзя. Найдите нужную строку и измените количество (0 — убрать позицию).",
                "өткөрүлгөн накладнойго жаңы позиция кошууга азырынча болбойт. Керектүү саптан санын өзгөртүңүз (0 — позицияны алып салуу).",
                "a new line can't be added to a posted invoice yet. Find the line and change its quantity (0 removes the line).",
                "işlenmiş faturaya henüz yeni satır eklenemez. Satırı bulup miktarı değiştirin (0 satırı kaldırır).", "o'tkazilgan yuk xatiga hali yangi pozitsiya qo'shib bo'lmaydi. Qatorni topib miqdorini o'zgartiring (0 — pozitsiyani olib tashlash)."));
    }

    private void OnBarcodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true; // Enter в поле штрихкода документ не проводит
        var code = (_barcode.Text ?? "").Trim();
        _barcode.Text = "";
        if (code.Length == 0)
            return;
        var row = _rows.FirstOrDefault(r => string.Equals(r.Barcode, code, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Code, code, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            Say(T($"Штрихкод {code} в этой накладной не найден.", $"{code} штрих-коду бул накладнойдо табылган жок.", $"Barcode {code} is not in this invoice.", $"{code} barkodu bu faturada yok.", $"{code} shtrix-kodi bu yuk xatida yo'q."));
            return;
        }
        _chip = "all";
        _search.Text = "";
        ApplyFilter();
        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
        if (_edit)
        {
            row.Qty += 1; // найден в накладной → +1 шт
            Say($"{row.Name}: " + row.QtyText);
        }
        else
            Say(row.Name);
    }

    private void OnCellKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not InvoiceRow row)
            return;
        var tag = tb.Tag as string ?? "qty";
        if (e.Key is Key.Enter or Key.Tab && e.KeyModifiers != KeyModifiers.Shift)
        {
            e.Handled = true;
            CommitCell(tb);
            var order = CellOrder();
            var idx = Array.IndexOf(order, tag);
            if (idx >= 0 && idx < order.Length - 1)
                FocusCell(row, order[idx + 1]);
            else
                MoveRow(row, 1, "qty");
        }
        else if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)
        {
            e.Handled = true;
            CommitCell(tb);
            var order = CellOrder();
            var idx = Array.IndexOf(order, tag);
            if (idx > 0)
                FocusCell(row, order[idx - 1]);
            else
                MoveRow(row, -1, order[^1]);
        }
        else if (e.Key == Key.Down)
        {
            e.Handled = true;
            CommitCell(tb);
            MoveRow(row, 1, tag);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            CommitCell(tb);
            MoveRow(row, -1, tag);
        }
        else if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            row.Qty = 0;
        }
    }

    /// <summary>Порядок ячеек при Tab/Enter: количество, (закупка при серверном PATCH), наценка, цена реализации.</summary>
    private string[] CellOrder() => !_showMoney ? new[] { "qty" } : _patchMode ? new[] { "qty", "buy", "mk", "sale" } : new[] { "qty", "mk", "sale" };

    /// <summary>Записать текст ячейки в строку сейчас (без ожидания потери фокуса).</summary>
    private static void CommitCell(TextBox tb)
    {
        if (tb.DataContext is not InvoiceRow row)
            return;
        switch (tb.Tag as string)
        {
            case "qty": row.QtyText = tb.Text ?? ""; break;
            case "buy": row.PurchaseEditText = tb.Text ?? ""; break;
            case "mk": row.MarkupText = tb.Text ?? ""; break;
            case "sale": row.SaleText = tb.Text ?? ""; break;
        }
    }

    private void MoveRow(InvoiceRow from, int delta, string tag)
    {
        var i = _shown.IndexOf(from) + delta;
        if (i < 0 || i >= _shown.Count)
            return;
        FocusCell(_shown[i], tag);
    }

    private void FocusCell(InvoiceRow row, string tag)
    {
        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
        Dispatcher.UIThread.Post(() =>
        {
            var i = _shown.IndexOf(row);
            if (i < 0 || _list.ContainerFromIndex(i) is not Control container)
                return;
            var target = container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => (t.Tag as string) == tag);
            target?.Focus();
        }, DispatcherPriority.Background);
    }
}
