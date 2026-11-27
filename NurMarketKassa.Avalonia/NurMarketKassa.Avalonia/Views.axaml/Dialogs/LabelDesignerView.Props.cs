using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using NurMarketKassa.AvaloniaHost.Services;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-21 («Редактор ценников»): перетаскивание, свойства выбранного элемента, нижняя панель (товар для проверки, печать, PDF, шаблоны).
public sealed partial class LabelDesignerView
{
    private static readonly string[] FontNames = { "Arial", "Tahoma", "Verdana", "Segoe UI", "Times New Roman", "Consolas" };

    // ------------------------------------------------------------------ перетаскивание

    private void WireDrag(Control box, DElement el)
    {
        Point start = default;
        double sx = 0, sy = 0;
        var dragging = false;
        var pushed = false;
        var selectionChanged = false;
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(box).Properties.IsLeftButtonPressed)
                return;
            Focus();
            if (_selectedKey != el.Key)
            {
                // Выбор меняется сразу (свойства, переключатели), а рамки перестраиваются при отпускании мыши: иначе нажатая рамка
                // исчезла бы из дерева вместе с захватом мыши и элемент нельзя было бы сразу потянуть.
                _selectedKey = el.Key;
                BuildProps();
                RebuildChips();
                selectionChanged = true;
            }
            start = e.GetPosition(_canvas);
            sx = el.Layout.XMm;
            sy = el.Layout.YMm;
            dragging = true;
            pushed = false;
            e.Pointer.Capture(box);
            e.Handled = true;
        };
        box.PointerMoved += (_, e) =>
        {
            if (!dragging)
                return;
            var p = e.GetPosition(_canvas);
            var dx = (p.X - start.X) / _scale;
            var dy = (p.Y - start.Y) / _scale;
            if (!pushed && (Math.Abs(dx) > 0.05 || Math.Abs(dy) > 0.05))
            {
                Push();
                pushed = true;
            }
            if (!pushed)
                return;
            LabelDesignerMath.Move(el.Layout, _t, sx, sy, dx, dy, _grid);
            MoveBoxVisual(el.Key, el.Layout);
            QueuePreview();
        };
        box.PointerReleased += (_, e) =>
        {
            if (!dragging)
                return;
            dragging = false;
            e.Pointer.Capture(null);
            if (pushed || selectionChanged)
            {
                selectionChanged = false;
                Changed(true);
            }
        };
    }

    private void WireResize(Control grip, DElement el)
    {
        Point start = default;
        double sw = 0, sh = 0;
        var dragging = false;
        var pushed = false;
        grip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
                return;
            start = e.GetPosition(_canvas);
            sw = el.Layout.WidthMm;
            sh = el.Layout.HeightMm;
            dragging = true;
            pushed = false;
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (!dragging)
                return;
            var p = e.GetPosition(_canvas);
            if (!pushed)
            {
                Push();
                pushed = true;
            }
            LabelDesignerMath.Resize(el.Layout, _t, sw, sh, (p.X - start.X) / _scale, (p.Y - start.Y) / _scale, _grid);
            RebuildCanvasKeepCapture();
            QueuePreview();
        };
        grip.PointerReleased += (_, e) =>
        {
            if (!dragging)
                return;
            dragging = false;
            e.Pointer.Capture(null);
            Changed(true);
        };
    }

    /// <summary>Во время изменения размера рамки не пересоздаются (захват мыши остаётся у угла): двигаем и растягиваем существующие.</summary>
    private void RebuildCanvasKeepCapture()
    {
        var host = _boxes.Children.OfType<Canvas>().FirstOrDefault(c => (c.Tag as string) == _selectedKey);
        var el = Selected();
        if (host is null || el is null)
            return;
        var w = Math.Max(el.Layout.WidthMm * _scale, 6);
        var h = Math.Max(el.Layout.HeightMm * _scale, 6);
        host.Width = w;
        host.Height = h;
        foreach (var c in host.Children.OfType<Control>())
        {
            if (c is Border g && (g.Tag as string) == "grip")
            {
                Canvas.SetLeft(g, w - 6);
                Canvas.SetTop(g, h - 6);
            }
            else
            {
                c.Width = w;
                c.Height = h;
            }
        }
    }

    private void MoveBoxVisual(string key, LabelElementLayout l)
    {
        foreach (var c in _boxes.Children.OfType<Control>())
        {
            if ((c.Tag as string) != key)
                continue;
            Canvas.SetLeft(c, l.XMm * _scale);
            Canvas.SetTop(c, l.YMm * _scale);
        }
    }

    // ------------------------------------------------------------------ свойства

    private void BuildProps()
    {
        _building = true;
        try
        {
            _props.Children.Clear();
            var el = Selected();
            if (el is null)
            {
                _props.Children.Add(Hint(T("Выберите элемент на этикетке или нажмите «+ Добавить элемент».", "Этикеткадан элементти тандаңыз же «+ Элемент кошуу» басыңыз.",
                    "Pick an element on the label or press “+ Add element”.", "Etiketten bir öğe seçin veya «+ Öğe ekle»ye basın.", "Yorliqdan elementni tanlang yoki «+ Element qo'shish» ni bosing.")));
                return;
            }
            var l = el.Layout;
            var title = new TextBlock { Text = el.Title, FontSize = 15, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(title, TextBlock.ForegroundProperty, "BrushText");
            _props.Children.Add(title);

            // Показывать / удалить.
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            if (el.Extra is null)
            {
                var show = new CheckBox { Content = T("Показывать на этикетке", "Этикеткада көрсөтүү", "Show on label", "Etikette göster", "Yorliqda ko'rsatish"), IsChecked = l.Enabled, MinHeight = 32 };
                show.Click += (_, _) => { Push(); l.Enabled = show.IsChecked == true; RebuildChips(); Changed(true); };
                row.Children.Add(show);
            }
            else
            {
                row.Children.Add(Small(T("Дублировать", "Көчүрмө", "Duplicate", "Çoğalt", "Nusxa"), (_, _) => DuplicateSelected()));
                row.Children.Add(Small(T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish"), (_, _) => RemoveSelected()));
            }
            _props.Children.Add(row);

            // Положение и размер.
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            AddNum(grid, 0, 0, "X, " + Mm, l.XMm, 0, 150, v => l.XMm = v, () => l.XMm);
            AddNum(grid, 1, 0, "Y, " + Mm, l.YMm, 0, 150, v => l.YMm = v, () => l.YMm);
            AddNum(grid, 0, 1, T("Ширина, ", "Туурасы, ", "Width, ", "Genişlik, ", "Kengligi, ") + Mm, l.WidthMm, 0.3, 150, v => l.WidthMm = v, () => l.WidthMm);
            AddNum(grid, 1, 1, T("Высота, ", "Бийиктиги, ", "Height, ", "Yükseklik, ", "Balandligi, ") + Mm, l.HeightMm, 0.3, 150, v => l.HeightMm = v, () => l.HeightMm);
            _props.Children.Add(grid);

            // Выравнивание по краям этикетки.
            var alignRow = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (to, text) in new[]
            {
                (LabelAlignTo.Left, T("Влево", "Солго", "Left", "Sola", "Chapga")), (LabelAlignTo.CenterH, T("По центру", "Ортого", "Center", "Orta", "O'rtaga")),
                (LabelAlignTo.Right, T("Вправо", "Оңго", "Right", "Sağa", "O'ngga")), (LabelAlignTo.Top, T("Вверх", "Өйдө", "Top", "Üst", "Yuqoriga")),
                (LabelAlignTo.MiddleV, T("Посередине", "Ортого", "Middle", "Orta", "O'rtaga")), (LabelAlignTo.Bottom, T("Вниз", "Төмөн", "Bottom", "Alt", "Pastga")),
            })
            {
                var target = to;
                alignRow.Children.Add(Small(text, (_, _) => { Push(); LabelDesignerMath.Align(l, _t, target); Changed(true); }));
            }
            _props.Children.Add(Cap(T("Выровнять по этикетке", "Этикетка боюнча түздөө", "Align to label", "Etikete göre hizala", "Yorliqqa tekislash"), alignRow));

            var isText = el.Key is not "barcode" && el.Extra?.Kind is not (LabelExtraKind.Line or LabelExtraKind.Logo or LabelExtraKind.ShopQr);
            if (isText)
                BuildTextProps(el);
            if (el.Key == "barcode")
                BuildBarcodeProps();
            if (el.Key == "price")
                BuildPriceProps();
            if (el.Key == "sku")
                _props.Children.Add(Cap(T("Заменить текст (пусто — артикул товара)", "Текстти алмаштыруу (бош — товардын артикулу)", "Replace text (empty — product SKU)", "Metni değiştir (boş — ürün stok kodu)", "Matnni almashtirish (bo'sh — mahsulot artikuli)"),
                    TextField(_t.SkuCustomText, v => _t.SkuCustomText = string.IsNullOrWhiteSpace(v) ? null : v)));
            if (el.Extra is { } x)
                BuildExtraProps(x);

            // Вся этикетка.
            _props.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 6), Opacity = 0.4, Background = Brushes.Gray });
            var frame = new CheckBox { Content = T("Рамка по контуру этикетки", "Этикетканын контуру боюнча алкак", "Frame around the label", "Etiket çevresinde çerçeve", "Yorliq atrofida ramka"), IsChecked = _t.Frame, MinHeight = 32 };
            frame.Click += (_, _) => { Push(); _t.Frame = frame.IsChecked == true; Changed(); };
            _props.Children.Add(frame);
            var font = new ComboBox { MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
            var fonts = FontNames.ToList();
            if (!fonts.Contains(_t.FontFamily, StringComparer.OrdinalIgnoreCase))
                fonts.Add(_t.FontFamily);
            foreach (var f in fonts)
                font.Items.Add(f);
            font.SelectedIndex = Math.Max(0, fonts.FindIndex(f => string.Equals(f, _t.FontFamily, StringComparison.OrdinalIgnoreCase)));
            font.SelectionChanged += (_, _) =>
            {
                if (_building || font.SelectedItem is not string f)
                    return;
                Push();
                _t.FontFamily = f;
                Changed();
            };
            _props.Children.Add(Cap(T("Шрифт всей этикетки", "Бүт этикетканын шрифти", "Font of the whole label", "Tüm etiketin yazı tipi", "Butun yorliq shrifti"), font));
        }
        finally
        {
            _building = false;
        }
    }

    private static string Mm => T("мм", "мм", "mm", "mm", "mm");

    private TextBlock Hint(string text)
    {
        var h = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        Use(h, TextBlock.ForegroundProperty, "BrushTextSoft");
        return h;
    }

    private Button Small(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var b = Tool(text, click);
        b.MinHeight = 32;
        b.Padding = new Thickness(8, 0);
        b.FontSize = 12.5;
        b.Margin = new Thickness(0, 0, 4, 4);
        return b;
    }

    private void AddNum(Grid grid, int col, int row, string caption, double value, double min, double max, Action<double> set, Func<double> get)
    {
        var box = Num(min, max, _grid > 0 ? _grid : 0.5);
        box.MinWidth = 0;
        box.HorizontalAlignment = HorizontalAlignment.Stretch;
        box.Value = (decimal)Math.Round(value, 2);
        box.ValueChanged += (_, _) =>
        {
            if (_building || box.Value is null)
                return;
            Push();
            set((double)box.Value.Value);
            if (Selected() is { } s)
                LabelDesignerMath.ClampInside(s.Layout, _t);
            // 2026-11-27: элемент не выходит за этикетку (ClampInside), а в поле оставалось введённое число: X = 10 у штрихкода шириной 37,6 мм
            // на этикетке 40 мм — блок у правого края (X 2,4), а в поле «10». Поле показывает то, что реально на этикетке.
            var actual = (decimal)Math.Round(get(), 2);
            if (actual != box.Value)
                Dispatcher.UIThread.Post(() =>
                {
                    _building = true;
                    try { box.Value = actual; }
                    finally { _building = false; }
                }, DispatcherPriority.Background);
            Changed();
        };
        var cell = Cap(caption, box);
        cell.Margin = new Thickness(0, 0, 0, 2);
        Grid.SetColumn(cell, col);
        Grid.SetRow(cell, row);
        grid.Children.Add(cell);
    }

    private TextBox TextField(string? value, Action<string> set, int max = 80)
    {
        var tb = new TextBox { Text = value ?? "", MaxLength = max, MinHeight = 34 };
        tb.TextChanged += (_, _) =>
        {
            if (_building)
                return;
            set(tb.Text ?? "");
            Changed();
        };
        tb.GotFocus += (_, _) => Push();
        return tb;
    }

    private void BuildTextProps(DElement el)
    {
        var l = el.Layout;
        // Шрифт элемента: «Как у этикетки» или свой.
        var font = new ComboBox { MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
        font.Items.Add(T("Как у этикетки", "Этикетка сыяктуу", "As the label", "Etiket gibi", "Yorliq kabi"));
        foreach (var f in FontNames)
            font.Items.Add(f);
        var idx = string.IsNullOrWhiteSpace(l.FontFamily) ? 0 : Array.FindIndex(FontNames, f => string.Equals(f, l.FontFamily, StringComparison.OrdinalIgnoreCase)) + 1;
        font.SelectedIndex = Math.Max(0, idx);
        font.SelectionChanged += (_, _) =>
        {
            if (_building || font.SelectedIndex < 0)
                return;
            Push();
            l.FontFamily = font.SelectedIndex == 0 ? null : FontNames[font.SelectedIndex - 1];
            Changed();
        };
        _props.Children.Add(Cap(T("Шрифт", "Шрифт", "Font", "Yazı tipi", "Shrift"), font));

        var size = Num(0, 200, 1, 0);
        size.MinWidth = 0;
        size.HorizontalAlignment = HorizontalAlignment.Stretch;
        size.Value = (decimal)(l.FontSizePx ?? 0);
        size.ValueChanged += (_, _) =>
        {
            if (_building || size.Value is null)
                return;
            Push();
            l.FontSizePx = size.Value.Value <= 0 ? null : (double)size.Value.Value;
            Changed();
        };
        _props.Children.Add(Cap(T("Размер, px (0 — авто)", "Өлчөмү, px (0 — авто)", "Size, px (0 — auto)", "Boyut, px (0 — otomatik)", "O'lcham, px (0 — avto)"), size));

        var styleRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var defaultBold = el.Key is "price" or "variant" || el.Extra is { } xx && LabelExtras.IsBold(new LabelCustomElement { Kind = xx.Kind });
        var bold = new ToggleButton { Content = T("Ж", "Ж", "B", "K", "B"), FontWeight = FontWeight.Bold, MinHeight = 32, MinWidth = 40, IsChecked = l.Bold ?? defaultBold, Margin = new Thickness(0, 0, 4, 4), Focusable = false };
        bold.Click += (_, _) => { Push(); l.Bold = bold.IsChecked == true; Changed(); };
        var italic = new ToggleButton { Content = T("К", "К", "I", "İ", "K"), FontStyle = FontStyle.Italic, MinHeight = 32, MinWidth = 40, IsChecked = l.Italic == true, Margin = new Thickness(0, 0, 4, 4), Focusable = false };
        italic.Click += (_, _) => { Push(); l.Italic = italic.IsChecked == true ? true : null; Changed(); };
        styleRow.Children.Add(bold);
        styleRow.Children.Add(italic);
        var align = new ComboBox { MinHeight = 32, MinWidth = 110, Margin = new Thickness(0, 0, 4, 4) };
        align.Items.Add(T("Слева", "Солго", "Left", "Sola", "Chapga"));
        align.Items.Add(T("По центру", "Ортого", "Center", "Orta", "O'rtaga"));
        align.Items.Add(T("Справа", "Оңго", "Right", "Sağa", "O'ngga"));
        align.SelectedIndex = Math.Clamp(l.Align ?? 1, 0, 2);
        align.SelectionChanged += (_, _) => { if (_building || align.SelectedIndex < 0) return; Push(); l.Align = align.SelectedIndex == 1 ? null : align.SelectedIndex; Changed(); };
        styleRow.Children.Add(align);
        _props.Children.Add(Cap(T("Начертание и выравнивание текста", "Тексттин жазылышы жана түздөө", "Text style and alignment", "Metin biçimi ve hizalama", "Matn uslubi va tekislash"), styleRow));

        var rot = new ComboBox { MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var d in new[] { 0, 90, 180, 270 })
            rot.Items.Add(d + "°");
        rot.SelectedIndex = Math.Clamp(((l.Rotation % 360) + 360) % 360 / 90, 0, 3);
        rot.SelectionChanged += (_, _) => { if (_building || rot.SelectedIndex < 0) return; Push(); l.Rotation = rot.SelectedIndex * 90; Changed(); };
        _props.Children.Add(Cap(T("Поворот", "Буруу", "Rotation", "Döndürme", "Burish"), rot));
    }

    private void BuildBarcodeProps()
    {
        var format = new ComboBox { MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in new[] { T("Авто (по длине кода)", "Авто (коддун узундугу боюнча)", "Auto (by code length)", "Otomatik (kod uzunluğuna göre)", "Avto (kod uzunligi bo'yicha)"), "EAN-13", "Code 128", "QR" })
            format.Items.Add(s);
        format.SelectedIndex = Math.Clamp((int)_t.BarcodeFormat, 0, 3);
        // 2026-11-27: у QR цифр под кодом нет — галочка «Цифры» для QR неактивна (раньше она была, но ничего не делала).
        var digits = new CheckBox { Content = T("Цифры под штрихкодом", "Штрихкоддун астындагы сандар", "Digits under the barcode", "Barkodun altında rakamlar", "Shtrix-kod ostidagi raqamlar"), IsChecked = _t.BarcodeShowDigits, MinHeight = 32, IsEnabled = _t.BarcodeFormat != LabelBarcodeFormat.QrCode };
        // 2026-11-27 («Ошибка в редактировании штрих кода ценников»): «Толщина штриха» задавалась только в «Печати». При «1 (тонкий)» штрихкод оставался
        // узким (≈12 мм на этикетке 40 мм), как ни растягивай его блок в редакторе, и причины в редакторе не было видно. Теперь поле есть и здесь.
        var thickness = new ComboBox { MinHeight = 34, HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = _t.BarcodeFormat != LabelBarcodeFormat.QrCode };
        foreach (var s in ThicknessItems())
            thickness.Items.Add(s);
        thickness.SelectedIndex = Math.Clamp(_t.BarcodeModuleDots, 0, 4);
        format.SelectionChanged += (_, _) =>
        {
            if (_building || format.SelectedIndex < 0)
                return;
            Push();
            _t.BarcodeFormat = (LabelBarcodeFormat)format.SelectedIndex;
            digits.IsEnabled = thickness.IsEnabled = _t.BarcodeFormat != LabelBarcodeFormat.QrCode;
            Changed();
        };
        thickness.SelectionChanged += (_, _) =>
        {
            if (_building || thickness.SelectedIndex < 0)
                return;
            Push();
            _t.BarcodeModuleDots = thickness.SelectedIndex; // 0 — авто, 1…4 — точек на модуль (как «Толщина штриха» в «Печати»)
            Changed();
        };
        _props.Children.Add(Cap(T("Тип штрихкода", "Штрихкод түрү", "Barcode type", "Barkod türü", "Shtrix-kod turi"), format));
        _props.Children.Add(Cap(T("Толщина штриха", "Сызык калыңдыгы", "Bar thickness", "Çizgi kalınlığı", "Chiziq qalinligi"), thickness));
        digits.Click += (_, _) => { Push(); _t.BarcodeShowDigits = digits.IsChecked == true; Changed(); };
        _props.Children.Add(digits);
    }

    /// <summary>Те же пункты, что «Толщина штриха» в «Печати» (BarcodePrintView): Авто, 1–4 точки на штрих.</summary>
    private static string[] ThicknessItems() => new[]
    {
        T("Авто", "Авто", "Auto", "Otomatik", "Avto"), "1 " + T("(тонкий)", "(ичке)", "(thin)", "(ince)", "(ingichka)"),
        "2 " + T("(стандарт)", "(стандарт)", "(standard)", "(standart)", "(standart)"), "3 " + T("(толстый)", "(жоон)", "(thick)", "(kalın)", "(qalin)"),
        "4 " + T("(очень толстый)", "(өтө жоон)", "(extra thick)", "(çok kalın)", "(juda qalin)"),
    };

    private void BuildPriceProps()
    {
        _props.Children.Add(Cap(T("Валюта", "Валюта", "Currency", "Para birimi", "Valyuta"), TextField(_t.PriceCurrencyText, v => _t.PriceCurrencyText = v.Trim(), 8)));
        var dec = new CheckBox { Content = T("Скрывать копейки (,00)", "Тыйындарды жашыруу (,00)", "Hide cents (,00)", "Kuruşları gizle (,00)", "Tiyinlarni yashirish (,00)"), IsChecked = _t.PriceHideDecimals, MinHeight = 32 };
        dec.Click += (_, _) => { Push(); _t.PriceHideDecimals = dec.IsChecked == true; Changed(); };
        _props.Children.Add(dec);
    }

    private void BuildExtraProps(LabelCustomElement x)
    {
        switch (x.Kind)
        {
            case LabelExtraKind.FreeText:
                _props.Children.Add(Cap(T("Текст", "Текст", "Text", "Metin", "Matn"), TextField(x.Text, v => x.Text = v)));
                break;
            case LabelExtraKind.Date:
                _props.Children.Add(Cap(T("Формат даты (пусто — дд.ММ.гггг)", "Датанын форматы (бош — дд.ММ.гггг)", "Date format (empty — dd.MM.yyyy)", "Tarih biçimi (boş — gg.AA.yyyy)", "Sana formati (bo'sh — kk.OO.yyyy)"),
                    TextField(x.Text, v => x.Text = string.IsNullOrWhiteSpace(v) ? null : v, 20)));
                break;
            case LabelExtraKind.OldPrice:
            case LabelExtraKind.Discount:
                _props.Children.Add(Hint(T("Берётся из карточки товара со скидкой; без скидки элемент пустой.", "Арзандатуусу бар товардын карточкасынан алынат; арзандатуусуз — бош.",
                    "Taken from a product with a discount; without a discount the element stays empty.", "İndirimli ürün kartından alınır; indirim yoksa öğe boş kalır.", "Chegirmali mahsulot kartasidan olinadi; chegirmasiz element bo'sh.")));
                break;
            case LabelExtraKind.Brand:
                _props.Children.Add(Hint(T("Бренд из карточки товара.", "Товар карточкасындагы бренд.", "Brand from the product card.", "Ürün kartındaki marka.", "Mahsulot kartasidagi brend.")));
                break;
            case LabelExtraKind.Cell:
                _props.Children.Add(Hint(T("Ячейка из карточки товара («Автозапчасти»).", "Товар карточкасындагы уяча («Автозапчасти»).", "Cell from the product card (“Auto parts”).", "Ürün kartındaki hücre («Oto yedek parça»).", "Mahsulot kartasidagi katak («Avtoehtiyot qismlar»).")));
                break;
            case LabelExtraKind.ShopQr:
                _props.Children.Add(Hint(T("QR на витрину магазина (market.nurcrm.kg) с подписью «Заказать онлайн». Адрес берётся у магазина; нет витрины — QR не печатается. Нужен блок не меньше 14×14 мм.",
                    "Дүкөндүн витринасына (market.nurcrm.kg) QR, астында «Онлайн буйрутма берүү» жазуусу. Дарек дүкөндөн алынат; витрина жок болсо QR басылбайт. Блок 14×14 ммден кем эмес болушу керек.",
                    "A QR to the shop's storefront (market.nurcrm.kg) with the caption “Order online”. The address comes from the shop; no storefront — no QR is printed. The block should be at least 14×14 mm.",
                    "Mağaza vitrinine (market.nurcrm.kg) «Online sipariş» yazılı QR. Adres mağazadan alınır; vitrin yoksa QR basılmaz. Blok en az 14×14 mm olmalı.",
                    "Do'kon vitrinasiga (market.nurcrm.kg) «Onlayn buyurtma» yozuvli QR. Manzil do'kondan olinadi; vitrina yo'q bo'lsa QR chop etilmaydi. Blok kamida 14×14 mm bo'lishi kerak.")));
                break;
            case LabelExtraKind.Logo:
                var path = new TextBlock { Text = string.IsNullOrWhiteSpace(x.ImagePath) ? T("Файл не выбран", "Файл тандалган жок", "No file chosen", "Dosya seçilmedi", "Fayl tanlanmagan") : System.IO.Path.GetFileName(x.ImagePath), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
                Use(path, TextBlock.ForegroundProperty, "BrushTextSoft");
                _props.Children.Add(path);
                var pick = Small(T("Выбрать файл…", "Файлды тандоо…", "Choose file…", "Dosya seç…", "Fayl tanlash…"), async (_, _) => await PickLogoAsync(x).ConfigureAwait(true));
                var clear = Small(T("Убрать", "Алып салуу", "Remove", "Kaldır", "Olib tashlash"), (_, _) => { Push(); x.ImagePath = null; Changed(true); });
                _props.Children.Add(new WrapPanel { Children = { pick, clear } });
                _props.Children.Add(Hint(T("Картинка печатается чёрно-белой (чёрное — тёмные точки). Лучше PNG на белом или прозрачном фоне.", "Сүрөт кара-ак басылат. PNG ак же тунук фондо жакшы.",
                    "The picture prints in black and white. A PNG on a white or transparent background works best.", "Resim siyah-beyaz basılır. Beyaz veya şeffaf arka planlı PNG en iyisidir.", "Rasm oq-qora chop etiladi. Oq yoki shaffof fondagi PNG yaxshi.")));
                break;
        }
    }

    private async Task PickLogoAsync(LabelCustomElement x)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Логотип для этикетки", "Этикетка үчүн логотип", "Logo for the label", "Etiket için logo", "Yorliq uchun logotip"),
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType(T("Изображения", "Сүрөттөр", "Images", "Görseller", "Rasmlar")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" } } },
        }).ConfigureAwait(true);
        var file = files.FirstOrDefault();
        if (file is null)
            return;
        var path = await NurMarketKassa.AvaloniaHost.Services.ImportFileBridge.PermanentPathAsync(file).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
            return;
        Push();
        x.ImagePath = path;
        Changed(true);
    }

    // ------------------------------------------------------------------ шаблоны

    private readonly List<(string Label, Func<LabelTemplate> Build)> _presetEntries = new();

    private void FillPresets()
    {
        var was = _building;
        _building = true;
        try
        {
            _presetCombo.Items.Clear();
            _presetEntries.Clear();
            foreach (var p in LabelDesignPresets.All)
                _presetEntries.Add((p.Name(), p.Build));
            foreach (var m in LabelDesignLibrary.Shared.ToList())
            {
                var tpl = m.Template;
                _presetEntries.Add(("★ " + m.Name, () => tpl.Clone()));
            }
            foreach (var e in _presetEntries)
                _presetCombo.Items.Add(e.Label);
            _presetCombo.SelectedIndex = -1;
        }
        finally { _building = was; }
    }

    private void OnPresetPicked(object? sender, SelectionChangedEventArgs e)
    {
        if (_building || _presetCombo.SelectedIndex < 0 || _presetCombo.SelectedIndex >= _presetEntries.Count)
            return;
        var entry = _presetEntries[_presetCombo.SelectedIndex];
        Push();
        _t = entry.Build();
        _t.Item = null;
        EnsureSane(_t);
        var label = entry.Label;
        SyncAll();
        SetStatus(T($"Шаблон «{label}» открыт. «Отменить» вернёт прежний вид; «Сохранить» сделает его рабочим для печати.", $"«{label}» шаблону ачылды. «Артка кайтаруу» мурункусун кайтарат; «Сактоо» аны басуу үчүн иштөөчү кылат.",
            $"Template “{label}” opened. “Undo” brings the old look back; “Save” makes it the working one for printing.", $"«{label}» şablonu açıldı. «Geri al» eski görünümü getirir; «Kaydet» onu baskı için çalışan şablon yapar.",
            $"«{label}» shabloni ochildi. «Bekor qilish» avvalgisini qaytaradi; «Saqlash» uni chop etish uchun ishchi qiladi."));
    }

    // ------------------------------------------------------------------ нижняя панель

    private Control BuildBottom()
    {
        _search = new AutoCompleteBox
        {
            MinWidth = 220, MaxWidth = 340, MinHeight = 36, MinimumPrefixLength = 1, MaxDropDownHeight = 240, FilterMode = AutoCompleteFilterMode.Custom,
            Watermark = T("Товар для проверки: название, штрихкод…", "Текшерүү үчүн товар: аталышы, штрихкод…", "Product to preview: name, barcode…", "Önizleme için ürün: ad, barkod…", "Ko'rish uchun mahsulot: nom, shtrix-kod…"),
            ItemFilter = (text, item) => item is CatalogProductTileVm p && !string.IsNullOrWhiteSpace(text)
                && (Has(p.Title, text) || Has(p.Barcode, text) || Has(p.Article, text) || Has(p.Brand, text)),
            ValueMemberBinding = new Avalonia.Data.Binding("Title"),
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((o, _) => new TextBlock { Text = (o as CatalogProductTileVm)?.Title ?? "", TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 }),
        };
        _search.SelectionChanged += (_, _) =>
        {
            if (_search.SelectedItem is CatalogProductTileVm p)
                PickProduct(p);
        };
        _search.AttachedToVisualTree += (_, _) =>
        {
            if (CatalogCacheService.Products.Count == 0)
                CatalogCacheService.LoadFromDatabase();
            _search.ItemsSource = CatalogCacheService.Products;
        };
        _copiesBox = Num(1, 99, 1, 0);
        _copiesBox.Value = 1;
        _copiesBox.MinWidth = 80;

        var testPrint = Tool(T("Тест-печать", "Сынамык басуу", "Test print", "Deneme baskısı", "Sinov chop etish"), async (_, _) => await TestPrintAsync().ConfigureAwait(true));
        var pdf = Tool("PDF", async (_, _) => await SavePdfAsync().ConfigureAwait(true));
        var saveAs = Tool(T("В «Мои шаблоны»…", "«Менин шаблондорума»…", "To “My templates”…", "«Şablonlarım»a…", "«Mening shablonlarim»ga…"), null);
        var nameBox = new TextBox { MinWidth = 200, MaxLength = 40, MinHeight = 36, Watermark = T("Название шаблона", "Шаблондун аталышы", "Template name", "Şablon adı", "Shablon nomi") };
        var saveName = Tool(T("Сохранить шаблон", "Шаблонду сактоо", "Save template", "Şablonu kaydet", "Shablonni saqlash"), null);
        var flyoutPanel = new StackPanel { Spacing = 8, Width = 260, Children = { nameBox, saveName } };
        var flyout = new Flyout { Content = flyoutPanel };
        saveAs.Flyout = flyout;
        saveName.Click += (_, _) =>
        {
            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0)
            {
                SetStatus(T("Введите название шаблона.", "Шаблондун аталышын жазыңыз.", "Enter a template name.", "Şablon adını girin.", "Shablon nomini kiriting."));
                return;
            }
            var saved = LabelDesignLibrary.Upsert(name, _t, (int)(_copiesBox.Value ?? 1));
            flyout.Hide();
            FillPresets();
            SetStatus(saved is null
                ? T("Шаблонов уже 30 — удалите лишний в режиме «Шаблоны».", "Шаблон 30 болду — «Шаблондор» режиминде ашыкчасын өчүрүңүз.", "There are already 30 templates — delete one in “Templates”.", "Zaten 30 şablon var — «Şablonlar» modunda fazlasını silin.", "Shablonlar allaqachon 30 ta — «Shablonlar» rejimida ortiqchasini o'chiring.")
                : T($"Шаблон «{name}» сохранён — виден на всех компьютерах магазина.", $"«{name}» шаблону сакталды — дүкөндүн бардык компьютерлеринде көрүнөт.", $"Template “{name}” saved — visible on all computers of the store.", $"«{name}» şablonu kaydedildi — mağazanın tüm bilgisayarlarında görünür.", $"«{name}» shabloni saqlandi — do'konning barcha kompyuterlarida ko'rinadi."));
        };
        _saveButton = Tool(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), (_, _) => SaveAsWorking());
        _saveButton.Classes.Add("sec-primary");
        var print = Tool(T("Печать товаров…", "Товарларды басуу…", "Print products…", "Ürünleri yazdır…", "Mahsulotlarni chop etish…"), (_, _) =>
        {
            SaveAsWorking();
            PrintRequested?.Invoke();
        });
        ToolTip.SetTip(print, T("Выбрать товары (по поиску, категории, остатку) и напечатать с этим шаблоном — режим «Печать»", "Товарларды тандап, ушул шаблон менен басуу — «Басып чыгаруу» режими",
            "Pick products (by search, category, stock) and print with this template — the “Print” mode", "Ürünleri seçip bu şablonla yazdırın — «Yazdır» modu", "Mahsulotlarni tanlab, shu shablon bilan chop etish — «Chop etish» rejimi"));

        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Cap(T("Товар для проверки", "Текшерүү үчүн товар", "Product to preview", "Önizleme ürünü", "Ko'rish uchun mahsulot"), _search));
        row.Children.Add(Cap(T("Копий", "Көчүрмө", "Copies", "Kopya", "Nusxa"), _copiesBox));
        row.Children.Add(Cap("", testPrint));
        row.Children.Add(Cap("", pdf));
        row.Children.Add(Cap("", saveAs));
        row.Children.Add(Cap("", _saveButton));
        row.Children.Add(Cap("", print));
        Use(_status, TextBlock.ForegroundProperty, "BrushTextSoft");
        return new StackPanel { Spacing = 2, Children = { row, _status } };
    }

    private static bool Has(string? source, string text) => !string.IsNullOrEmpty(source) && source.Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private void PickProduct(CatalogProductTileVm p)
    {
        _product = p;
        _sampleName = p.Title;
        _sampleCode = BarcodePrintCatalog.Choices(p).FirstOrDefault()?.Code ?? "4870145004807";
        _sampleSku = p.Article;
        _sampleUnit = p.Unit;
        QueuePreview();
    }

    /// <summary>Выбрать товар для проверки (для тестов и «Шаблоны»).</summary>
    public void SetPreviewProduct(CatalogProductTileVm product) => PickProduct(product);

    /// <summary>Данные товара для рисования: цена и поля своих элементов (старая цена, скидка, бренд, ячейка) считаются по шаблону.</summary>
    private void RefreshSampleFromProduct()
    {
        if (_product is null)
        {
            _samplePrice = "285,00 сом";
            _sampleData = LabelExtras.Sample;
            return;
        }
        var (price, data) = BarcodePrintCatalog.LabelFor(new BarcodePrintItem(_product, _sampleCode, 1), _t);
        _samplePrice = price ?? BarcodePrintCatalog.LabelPrice(_product);
        _sampleData = data;
    }

    private void SaveAsWorking()
    {
        var copy = _t.Clone();
        copy.Item = null;
        LabelTemplateStore.Save(copy);
        _savedJson = Serialize(_t);
        UpdateSelectionUi();
        SetStatus(T("Шаблон сохранён: режим «Печать» печатает по нему.", "Шаблон сакталды: «Басып чыгаруу» режими ушуга ылайык басат.", "Template saved: the “Print” mode prints with it.", "Şablon kaydedildi: «Yazdır» modu bununla yazdırır.", "Shablon saqlandi: «Chop etish» rejimi shu bo'yicha chop etadi."));
        TemplateSaved?.Invoke();
    }

    private async Task TestPrintAsync()
    {
        if (!BarcodePrintAccess.IsAllowed())
        {
            SetStatus(BarcodePrintAccess.DeniedText);
            return;
        }
        var printerPath = UserPreferences.Instance.LabelPrinterDevicePath;
        if (string.IsNullOrWhiteSpace(printerPath))
        {
            SetStatus(LabelPrinterSelector.MissingHint());
            return;
        }
        SetStatus(T("Печать…", "Басып чыгарылууда…", "Printing…", "Yazdırılıyor…", "Chop etilmoqda…"));
        var template = PreviewTemplate();
        var request = new LabelPrintRequest(_sampleName, _sampleCode, _samplePrice, (int)(_copiesBox.Value ?? 1), printerPath, template, _sampleSku, _sampleUnit,
            UserPreferences.Instance.StoreName, _t.Variant is { Enabled: true } ? "44, " + T("Красный", "Кызыл", "Red", "Kırmızı", "Qizil") : null);
        var result = await Task.Run(() => BarcodeLabelService.Print(request)).ConfigureAwait(true);
        SetStatus(result switch
        {
            LabelPrintResult.Success => LabelPrinterSelector.SentText(LabelPrinterSelector.DisplayName(printerPath)),
            LabelPrintResult.PrinterNotFound => T("Принтер не найден — выберите его в «Настройки → Печать».", "Принтер табылган жок — «Жөндөөлөр → Басып чыгаруу» бөлүмүнөн тандаңыз.", "Printer not found — choose it in “Settings → Print”.", "Yazıcı bulunamadı — «Ayarlar → Yazdırma»dan seçin.", "Printer topilmadi — «Sozlamalar → Chop etish» dan tanlang."),
            _ => T("Ошибка печати. Подробности в журнале приложения.", "Басып чыгаруу катасы. Чоо-жайы колдонмонун журналында.", "Print error. See the application log.", "Yazdırma hatası. Ayrıntılar uygulama günlüğünde.", "Chop etish xatosi. Tafsilotlar ilova jurnalida."),
        });
    }

    /// <summary>PDF с этикетками (по странице на копию) — размер страницы равен размеру этикетки.</summary>
    public byte[] BuildPdf()
    {
        var template = PreviewTemplate();
        return LabelPdf.Create(_sampleName, _sampleCode, _samplePrice, template, (int)(_copiesBox.Value ?? 1), _sampleSku, _sampleUnit, UserPreferences.Instance.StoreName,
            _t.Variant is { Enabled: true } ? "44, " + T("Красный", "Кызыл", "Red", "Kırmızı", "Qizil") : null);
    }

    private async Task SavePdfAsync()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
                return;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = T("Сохранить этикетки в PDF", "Этикеткаларды PDFке сактоо", "Save labels as PDF", "Etiketleri PDF olarak kaydet", "Yorliqlarni PDF ga saqlash"),
                SuggestedFileName = "labels.pdf",
                DefaultExtension = "pdf",
                FileTypeChoices = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
            }).ConfigureAwait(true);
            if (file is null)
                return;
            var bytes = BuildPdf();
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
            await stream.WriteAsync(bytes).ConfigureAwait(true);
            SetStatus(T("PDF сохранён.", "PDF сакталды.", "PDF saved.", "PDF kaydedildi.", "PDF saqlandi."));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Label designer pdf failed: {ex}", "WARNING");
            SetStatus(T("Не удалось сохранить PDF. Подробности в журнале.", "PDFти сактоо мүмкүн болгон жок. Чоо-жайы журналда.", "Could not save the PDF. See the log.", "PDF kaydedilemedi. Ayrıntılar günlükte.", "PDFni saqlab bo'lmadi. Tafsilotlar jurnalda."));
        }
    }
}
