using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using NurMarketKassa.Services;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-27, загрузка электронных накладных: ручное сопоставление столбцов, когда автоподбор не уверен.
/// 2026-11-22 (жалоба владельца «не понятно как загружать с накладной»): вместо слепых стрелок ◀ ▶ — список столбцов у каждого поля (название столбца и пример значения из первой строки данных;
/// список раскрывается в самом окне, а не всплывающим ComboBox — в окнах без рамки он не открывается), строка заголовков — тоже списком строк. Сверху — инструкция из трёх шагов,
/// ниже — живой предпросмотр «что станет товаром» (штрихкод, название, количество, цена), внизу — понятная причина, почему «Продолжить» недоступна.
/// Если в файле нет количества (список товаров, прайс) — явная отметка «Количество = 1 для всех». Результат: <see cref="HeaderIndex"/>, <see cref="Mapping"/> (null — отмена), <see cref="Remember"/>.</summary>
public sealed class InvoiceColumnMapWindow : Window
{
    private const int PreviewRows = 4;
    private const int HeaderChoices = 60;
    private const int BigFileRows = 300;

    private readonly ProductFileTable _table;
    private readonly string _fileName;
    private int _headerIndex;
    private InvoiceColumnMapping _mapping;
    private readonly Dictionary<InvoiceField, ColumnPicker> _pickers = new();
    private readonly Dictionary<InvoiceField, TextBlock> _captions = new();
    private readonly ColumnPicker _headerPicker;
    private readonly CheckBox _qtyOne;
    private readonly StackPanel _extraRows = new() { Spacing = 8, IsVisible = false };
    private readonly Button _extraToggle;
    private readonly StackPanel _preview = new() { Spacing = 6 };
    private readonly TextBlock _previewNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _hint = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _remember;
    private readonly Button _ok;
    private bool _syncing;

    public InvoiceColumnMapping? Mapping { get; private set; }
    public int HeaderIndex { get; private set; }
    public bool Remember => _remember.IsChecked == true;

    /// <summary>Для тестов: «Продолжить» доступна.</summary>
    public bool CanContinue => _ok.IsEnabled;

    /// <summary>Для тестов: пояснение у кнопки «Продолжить» (пусто, когда всё в порядке).</summary>
    public string HintText => _hint.Text ?? "";

    /// <summary>Для тестов: столбец поля (-1 — «не выбрано»).</summary>
    public int ColumnOf(InvoiceField field) => _mapping.ColumnOf(field);

    public bool QuantityIsOne => _mapping.QuantityOne;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // Основные поля (названия, штрихкод, количество, цены) и «Ещё столбцы».
    private static readonly InvoiceField[] MainFields = [InvoiceField.Name, InvoiceField.Barcode, InvoiceField.Quantity, InvoiceField.Price, InvoiceField.Retail];
    private static readonly InvoiceField[] ExtraFields = [InvoiceField.Unit, InvoiceField.Article, InvoiceField.Sum, InvoiceField.SumWithVat, InvoiceField.VatRate];

    public InvoiceColumnMapWindow(ProductFileTable table, int headerIndex, InvoiceColumnMapping initial, string fileName)
    {
        _table = table;
        _fileName = fileName;
        _headerIndex = Math.Clamp(headerIndex, 0, Math.Max(0, table.Rows.Count - 1));
        _mapping = new InvoiceColumnMapping { QuantityOne = initial.QuantityOne };
        foreach (var (field, column) in initial.Columns)
            _mapping.Set(field, column);

        Title = T("Столбцы накладной", "Накладнойдун мамычалары", "Invoice columns", "Fatura sütunları", "Yuk xati ustunlari");
        SectionCards.AsModal(this, 900); // 2026-11-22: шире (было 752) — слева столбцы, справа предпросмотр; на узком экране (телефон) блоки встают друг под другом

        _previewNote.Bind(TextBlock.ForegroundProperty, _previewNote.GetResourceObservable("BrushTextSoft"));
        _remember = new CheckBox
        {
            IsChecked = true, MinHeight = 44,
            Content = T("Запомнить для файлов с такими же заголовками", "Ушундай аталыштагы файлдар үчүн эстеп калуу", "Remember for files with the same headers",
                "Aynı başlıklı dosyalar için hatırla", "Shunday sarlavhali fayllar uchun eslab qolish"),
        };

        var root = new StackPanel { Spacing = 8 };   // всё окно: инструкция сверху, под ней два блока
        var left = new StackPanel { Spacing = 6 };   // столбцы файла
        var right = new StackPanel { Spacing = 6 };  // предпросмотр

        // Инструкция: три шага.
        var howTo = new TextBlock
        {
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Text = T("1. Файл выбран. 2. Проверьте, какой столбец что означает (программа подобрала их сама). 3. Нажмите «Продолжить» — товары попадут в список приёмки.",
                "1. Файл тандалды. 2. Кайсы мамыча эмнени билдирерин текшериңиз (программа өзү тандады). 3. «Улантуу» баскычын басыңыз — товарлар кабыл алуу тизмесине түшөт.",
                "1. File chosen. 2. Check what each column means (picked automatically). 3. Press “Continue” — the items go to the receiving list.",
                "1. Dosya seçildi. 2. Her sütunun ne anlama geldiğini kontrol edin (otomatik seçildi). 3. «Devam»a basın — ürünler mal kabul listesine eklenir.",
                "1. Fayl tanlandi. 2. Qaysi ustun nimani bildirishini tekshiring (dastur o'zi tanladi). 3. «Davom etish»ni bosing — mahsulotlar qabul ro'yxatiga tushadi."),
        };
        howTo.Bind(TextBlock.ForegroundProperty, howTo.GetResourceObservable("BrushText"));
        var legend = new TextBlock
        {
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Text = T("* — обязательные поля. Название можно заменить штрихкодом, цену закупки — суммой.",
                "* — милдеттүү талаалар. Аталышын штрих-код менен, сатып алуу баасын сумма менен алмаштырса болот.",
                "* — required fields. A barcode can replace the name, an amount can replace the purchase price.",
                "* — zorunlu alanlar. Ad yerine barkod, alış fiyatı yerine tutar verilebilir.",
                "* — majburiy maydonlar. Nom o'rniga shtrix-kod, xarid narxi o'rniga summa ko'rsatish mumkin."),
        };
        legend.Bind(TextBlock.ForegroundProperty, legend.GetResourceObservable("BrushTextSoft"));
        root.Children.Add(howTo);
        root.Children.Add(legend);

        // Строка заголовков — список строк файла.
        _headerPicker = new ColumnPicker(this, T("— не выбрано —", "— тандалган жок —", "— not chosen —", "— seçilmedi —", "— tanlanmagan —"), allowNone: false);
        _headerPicker.SelectedChanged += i => ChangeHeader(i);
        left.Children.Add(PickerRow(T("Строка заголовков", "Аталыштар сабы", "Header row", "Başlık satırı", "Sarlavha qatori"), _headerPicker, null));

        // «Количество = 1 для всех» — явная опция под полем количества.
        _qtyOne = new CheckBox
        {
            MinHeight = 44, IsChecked = _mapping.QuantityOne && _mapping.ColumnOf(InvoiceField.Quantity) < 0,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, FontSize = 13,
                Text = T("В файле нет количества — считать «Количество = 1» для каждого товара", "Файлда саны жок — ар бир товар үчүн «Саны = 1» деп эсептөө",
                    "The file has no quantity — use “Quantity = 1” for every item", "Dosyada miktar yok — her ürün için «Miktar = 1» say",
                    "Faylda miqdor yo'q — har bir mahsulot uchun «Miqdor = 1» deb hisoblash"),
            },
        };
        _qtyOne.IsCheckedChanged += (_, _) =>
        {
            if (_syncing)
                return;
            _mapping.QuantityOne = _qtyOne.IsChecked == true;
            if (_mapping.QuantityOne)
                _mapping.Set(InvoiceField.Quantity, -1);
            Refresh();
        };
        var qtyOneRow = new Border { Margin = new Thickness(148, 0, 0, 0), Child = _qtyOne };


        foreach (var field in MainFields)
        {
            left.Children.Add(FieldRow(field));
            if (field == InvoiceField.Quantity)
                left.Children.Add(qtyOneRow); // отметка «Количество = 1 для всех» — прямо под полем количества
        }

        // «Ещё столбцы».
        foreach (var field in ExtraFields)
            _extraRows.Children.Add(FieldRow(field));
        _extraToggle = new Button { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 0) };
        _extraToggle.Classes.Add("modal-secondary");
        _extraToggle.Click += (_, _) =>
        {
            _extraRows.IsVisible = !_extraRows.IsVisible;
            RefreshExtraToggle();
        };
        left.Children.Add(_extraToggle);
        left.Children.Add(_extraRows);
        left.Children.Add(_remember);

        right.Children.Add(new TextBlock
        {
            Text = T("Так будут прочитаны первые строки:", "Биринчи саптар ушундай окулат:", "The first rows will be read like this:", "İlk satırlar şöyle okunacak:", "Birinchi qatorlar shunday o'qiladi:"),
            FontWeight = FontWeight.SemiBold, FontSize = 13,
        });
        right.Children.Add(SectionCards.Group(this, new StackPanel { Spacing = 6, Children = { _preview, _previewNote } }));

        // Две колонки (столбцы | предпросмотр), а на узком окне — друг под другом.
        var body = new Grid();
        body.Children.Add(left);
        body.Children.Add(right);
        void Layout(double width)
        {
            var wide = width >= 760;
            body.ColumnDefinitions = wide ? new ColumnDefinitions("5*,4*") : new ColumnDefinitions("*");
            body.RowDefinitions = wide ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
            Grid.SetColumn(left, 0);
            Grid.SetRow(left, 0);
            Grid.SetColumn(right, wide ? 1 : 0);
            Grid.SetRow(right, wide ? 0 : 1);
            right.Margin = wide ? new Thickness(16, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        }
        Layout(900);
        body.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        root.Children.Add(body);

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.MinHeight = 44;
        cancel.Click += (_, _) => Close();
        _ok = SectionCards.Primary(T("Продолжить", "Улантуу", "Continue", "Devam", "Davom etish"), isDefault: false);
        _ok.MinHeight = 44;
        _ok.Click += (_, _) => Accept();

        Content = SectionCards.Shell(Title,
            T($"Файл «{fileName}»: укажите, где название, количество и цена.", $"«{fileName}» файлы: аталышы, саны жана баасы кайда экенин көрсөтүңүз.",
                $"File “{fileName}”: point out where the name, quantity and price are.", $"«{fileName}» dosyası: ad, miktar ve fiyatın nerede olduğunu belirtin.",
                $"«{fileName}» fayli: nomi, miqdori va narxi qayerdaligini ko'rsating."),
            root, SectionCards.Buttons(cancel, _ok), _hint);
        Refresh();
    }

    /// <summary>Для тестов: нажать «Продолжить» (как кликом).</summary>
    public void Accept()
    {
        if (!_ok.IsEnabled)
            return;
        HeaderIndex = _headerIndex;
        Mapping = _mapping;
        Close();
    }

    /// <summary>Для тестов и сценариев: выбрать столбец поля (-1 — «не выбрано»), как в списке окна.</summary>
    public void PickColumn(InvoiceField field, int column)
    {
        _mapping.Set(field, column);
        if (field == InvoiceField.Quantity && column >= 0)
            _mapping.QuantityOne = false;
        Refresh();
    }

    /// <summary>Для тестов: отметка «Количество = 1 для всех».</summary>
    public void SetQuantityOne(bool on)
    {
        _mapping.QuantityOne = on;
        if (on)
            _mapping.Set(InvoiceField.Quantity, -1);
        Refresh();
    }

    // ------------------------------------------------------------------ строки формы

    private Control FieldRow(InvoiceField field)
    {
        var picker = new ColumnPicker(this, T("— не выбрано —", "— тандалган жок —", "— not chosen —", "— seçilmedi —", "— tanlanmagan —"), allowNone: true);
        _pickers[field] = picker;
        var f = field;
        picker.SelectedChanged += i =>
        {
            _mapping.Set(f, i);
            if (f == InvoiceField.Quantity && i >= 0)
                _mapping.QuantityOne = false;
            Refresh();
        };
        var required = field is InvoiceField.Name or InvoiceField.Quantity or InvoiceField.Price;
        var caption = InvoiceColumnMapping.FieldLabel(field) + (required ? " *" : "");
        var row = PickerRow(caption, picker, field);
        return row;
    }

    private Control PickerRow(string caption, ColumnPicker picker, InvoiceField? field)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*"), MinHeight = 44 };
        var cap = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) };
        cap.Bind(TextBlock.ForegroundProperty, cap.GetResourceObservable("BrushText"));
        Grid.SetColumn(cap, 0);
        Grid.SetColumn(picker, 1);
        grid.Children.Add(cap);
        grid.Children.Add(picker);
        if (field is { } f)
            _captions[f] = cap;
        return grid;
    }

    // ------------------------------------------------------------------ данные списков

    private string[] HeaderCells => _table.Rows.Count > 0 ? _table.Rows[_headerIndex].Cells : [];

    private static string ColumnLetter(int index)
    {
        var s = "";
        index++;
        while (index > 0)
        {
            index--;
            s = (char)('A' + index % 26) + s;
            index /= 26;
        }
        return s;
    }

    private string ColumnTitle(int column)
    {
        var headers = HeaderCells;
        var text = column < headers.Length ? headers[column].Trim() : "";
        return ColumnLetter(column) + (text.Length > 0 ? " — " + text : "");
    }

    /// <summary>Пример значения столбца: первая непустая ячейка в строках данных под заголовком (смотрим до 25 строк).</summary>
    private string Example(int column)
    {
        for (var r = _headerIndex + 1; r < _table.Rows.Count && r <= _headerIndex + 25; r++)
        {
            var v = _table.Rows[r][column].Trim();
            if (v.Length > 0)
                return v.Length > 28 ? v[..28] + "…" : v;
        }
        return "";
    }

    private List<(string Title, string Example)> ColumnItems()
    {
        var items = new List<(string, string)>();
        var count = Math.Max(1, _table.ColumnCount);
        for (var c = 0; c < count; c++)
        {
            var ex = Example(c);
            items.Add((ColumnTitle(c), ex.Length > 0 ? T("пример: ", "мисал: ", "example: ", "örnek: ", "misol: ") + ex : T("пусто", "бош", "empty", "boş", "bo'sh")));
        }
        return items;
    }

    private List<(string Title, string Example)> HeaderItems()
    {
        var items = new List<(string, string)>();
        for (var r = 0; r < Math.Min(_table.Rows.Count, Math.Max(HeaderChoices, _headerIndex + 1)); r++)
        {
            var row = _table.Rows[r];
            items.Add((T($"строка {row.FileRow}", $"сап {row.FileRow}", $"row {row.FileRow}", $"satır {row.FileRow}", $"qator {row.FileRow}"),
                string.Join(" | ", row.Cells.Where(c => !string.IsNullOrWhiteSpace(c)).Take(4).Select(c => c.Trim()))));
        }
        return items;
    }

    private void ChangeHeader(int index)
    {
        if (index < 0 || index >= _table.Rows.Count)
            return;
        _headerIndex = index;
        // Строку заголовков выбрали вручную — подбираем столбцы заново по ней (старые столбцы относились к другой строке).
        var fresh = InvoiceColumnMapping.AutoDetect(HeaderCells);
        fresh.QuantityOne = _mapping.QuantityOne && fresh.ColumnOf(InvoiceField.Quantity) < 0;
        _mapping = fresh;
        Refresh();
    }

    private void RefreshExtraToggle()
    {
        var chosen = ExtraFields.Count(f => _mapping.ColumnOf(f) >= 0);
        var text = T("Ещё столбцы: ед. изм., артикул, суммы, НДС", "Дагы мамычалар: өлчөм, артикул, суммалар, НДС", "More columns: unit, article, amounts, VAT", "Diğer sütunlar: birim, kod, tutarlar, KDV", "Yana ustunlar: birlik, artikul, summalar, QQS");
        if (chosen > 0)
            text += T($" (выбрано: {chosen})", $" (тандалды: {chosen})", $" (chosen: {chosen})", $" (seçili: {chosen})", $" (tanlangan: {chosen})");
        _extraToggle.Content = (_extraRows.IsVisible ? "▴ " : "▾ ") + text;
    }

    // ------------------------------------------------------------------ обновление

    private void Refresh()
    {
        _syncing = true;
        try
        {
            _headerPicker.SetItems(HeaderItems());
            _headerPicker.Selected = _headerIndex;
            var items = ColumnItems();
            foreach (var (field, picker) in _pickers)
            {
                picker.SetItems(items);
                picker.Selected = _mapping.ColumnOf(field);
                picker.Locked = field == InvoiceField.Quantity && _mapping.QuantityOne
                    ? T("= 1 для всех товаров", "= 1 бардык товарга", "= 1 for every item", "= her ürün için 1", "= har bir mahsulot uchun 1") : null;
            }
            _qtyOne.IsChecked = _mapping.QuantityOne && _mapping.ColumnOf(InvoiceField.Quantity) < 0;
        }
        finally
        {
            _syncing = false;
        }
        RefreshExtraToggle();

        var nameOrCode = _mapping.ColumnOf(InvoiceField.Name) >= 0 || _mapping.ColumnOf(InvoiceField.Barcode) >= 0;
        var hasQty = _mapping.ColumnOf(InvoiceField.Quantity) >= 0 || _mapping.QuantityOne;
        var hasPrice = new[] { InvoiceField.Price, InvoiceField.PriceWithVat, InvoiceField.Sum, InvoiceField.SumWithVat }.Any(f => _mapping.ColumnOf(f) >= 0);
        MarkCaption(InvoiceField.Name, !nameOrCode);
        MarkCaption(InvoiceField.Quantity, !hasQty);
        MarkCaption(InvoiceField.Price, !hasPrice);

        _preview.Children.Clear();

        var missing = new List<string>();
        if (!nameOrCode)
            missing.Add(T("название (или штрихкод)", "аталышы (же штрих-код)", "the name (or barcode)", "ad (veya barkod)", "nomi (yoki shtrix-kod)"));
        if (!hasQty)
            missing.Add(T("количество (выберите столбец или отметьте «Количество = 1 для всех»)", "саны (мамычаны тандаңыз же «Саны = 1» белгисин коюңуз)",
                "the quantity (pick a column or tick “Quantity = 1”)", "miktar (sütun seçin veya «Miktar = 1» işaretleyin)", "miqdor (ustunni tanlang yoki «Miqdor = 1» belgisini qo'ying)"));
        if (!hasPrice)
            missing.Add(T("цена закупки (или сумма)", "сатып алуу баасы (же сумма)", "the purchase price (or amount)", "alış fiyatı (veya tutar)", "xarid narxi (yoki summa)"));
        if (missing.Count > 0)
        {
            SetHint(T("Не хватает: ", "Жетишпейт: ", "Missing: ", "Eksik: ", "Yetishmaydi: ") + string.Join("; ", missing) + ".", blocked: true);
            _previewNote.Text = T("Выберите столбцы выше — здесь появится, что станет товаром.", "Жогортон мамычаларды тандаңыз — бул жерде товар кандай болору көрүнөт.",
                "Pick the columns above — what becomes an item will show here.", "Yukarıdan sütunları seçin — ürün burada görünecek.", "Yuqoridan ustunlarni tanlang — mahsulot shu yerda ko'rinadi.");
            _ok.IsEnabled = false;
            return;
        }

        var doc = InvoiceTableParser.Parse(_table, _headerIndex, _mapping, _fileName);
        if (doc.Items.Count == 0)
        {
            SetHint(T("С такими столбцами строки товаров не найдены — проверьте строку заголовков и столбцы.", "Мындай мамычалар менен товар саптары табылган жок — аталыштар сабын жана мамычаларды текшериңиз.",
                "No product rows with these columns — check the header row and the columns.", "Bu sütunlarla ürün satırı yok — başlık satırını ve sütunları kontrol edin.", "Bu ustunlar bilan mahsulot qatorlari yo'q — sarlavha qatorini va ustunlarni tekshiring."), blocked: true);
            _previewNote.Text = "";
            _ok.IsEnabled = false;
            return;
        }

        BuildPreview(doc);
        var found = T($"Найдено товаров: {doc.Items.Count}. Они попадут в список приёмки.", $"Табылган товарлар: {doc.Items.Count}. Алар кабыл алуу тизмесине түшөт.",
            $"Items found: {doc.Items.Count}. They will go to the receiving list.", $"Bulunan ürün: {doc.Items.Count}. Mal kabul listesine eklenecek.", $"Topilgan mahsulotlar: {doc.Items.Count}. Ular qabul ro'yxatiga tushadi.");
        // Много строк без количества — скорее каталог товаров, чем накладная.
        if (_mapping.QuantityOne && doc.Items.Count > BigFileRows)
            found += " " + T("Это похоже на список товаров, а не накладную — для каталога удобнее «Массовая загрузка».", "Бул накладной эмес, товарлар тизмесине окшойт — каталог үчүн «Жаппай жүктөө» ылайыктуураак.",
                "This looks like a product list, not an invoice — “Bulk import” suits a catalog better.", "Bu fatura değil, ürün listesine benziyor — katalog için «Toplu yükleme» daha uygun.", "Bu yuk xati emas, mahsulotlar ro'yxatiga o'xshaydi — katalog uchun «Ommaviy yuklash» qulayroq.");
        _previewNote.Text = found;
        SetHint("", blocked: false);
        _ok.IsEnabled = true;
    }

    private void MarkCaption(InvoiceField field, bool bad)
    {
        if (!_captions.TryGetValue(field, out var cap))
            return;
        cap.Foreground = null;
        cap.Bind(TextBlock.ForegroundProperty, cap.GetResourceObservable(bad ? "BrushDangerOnSoft" : "BrushText"));
    }

    private void SetHint(string text, bool blocked)
    {
        _hint.Text = text;
        _hint.Foreground = null;
        _hint.Bind(TextBlock.ForegroundProperty, _hint.GetResourceObservable(blocked ? "BrushDangerOnSoft" : "BrushTextSoft"));
    }

    /// <summary>Предпросмотр: по две строки на товар — название, ниже «штрихкод · количество × цена закупки» (что станет товаром в списке приёмки).</summary>
    private void BuildPreview(InvoiceDocument doc)
    {
        foreach (var item in doc.Items.Take(PreviewRows))
        {
            var price = item.UnitPrice(false);
            var name = new TextBlock { Text = item.Name.Length > 0 ? item.Name : "—", FontSize = 13, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
            var details = new TextBlock
            {
                FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = (item.Barcode.Length > 0 ? item.Barcode : T("без штрихкода", "штрих-коду жок", "no barcode", "barkod yok", "shtrix-kodsiz"))
                       + $" · {InvoiceText.Num(item.Quantity)} {item.Unit}".TrimEnd()
                       + " × " + (price > 0 ? InvoiceText.Num(price) : T("нет цены", "баасы жок", "no price", "fiyat yok", "narx yo'q")),
            };
            details.Bind(TextBlock.ForegroundProperty, details.GetResourceObservable("BrushTextSoft"));
            _preview.Children.Add(new StackPanel { Children = { name, details } });
        }
    }

    /// <summary>Закрыть раскрытые списки всех полей, кроме <paramref name="except"/> (одновременно открыт один).</summary>
    private void CloseOtherLists(ColumnPicker? except)
    {
        foreach (var p in _pickers.Values.Append(_headerPicker))
            if (!ReferenceEquals(p, except))
                p.CloseList();
    }

    // ------------------------------------------------------------------ список столбцов в самом окне

    /// <summary>Кнопка с текущим выбором («C — Цена · пример: 177») и список под ней, раскрывающийся в самом окне (не ComboBox/Popup: в окнах без рамки он не открывается).</summary>
    private sealed class ColumnPicker : UserControl
    {
        private readonly InvoiceColumnMapWindow _owner;
        private readonly string _none;
        private readonly bool _allowNone;
        private readonly Button _button;
        private readonly Border _panel;
        private readonly StackPanel _list = new();
        private List<(string Title, string Example)> _items = [];
        private int _selected = -1;
        private string? _locked;
        private TopLevel? _top;

        public event Action<int>? SelectedChanged;

        public ColumnPicker(InvoiceColumnMapWindow owner, string none, bool allowNone)
        {
            _owner = owner;
            _none = none;
            _allowNone = allowNone;
            _button = new Button { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 0) };
            _button.Classes.Add("modal-secondary");
            _button.Click += (_, _) =>
            {
                if (_panel.IsVisible)
                    _panel.IsVisible = false;
                else
                {
                    _owner.CloseOtherLists(this);
                    Rebuild();
                    _panel.IsVisible = true;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => _panel.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background); // 2026-11-22: список раскрылся ниже видимой части — прокрутить к нему
                }
            };
            _panel = new Border
            {
                IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 4, 0, 0),
                Child = new ScrollViewer { MaxHeight = 220, Content = _list, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto },
            };
            _panel.Bind(Border.BackgroundProperty, _panel.GetResourceObservable("BrushPanel"));
            _panel.Bind(Border.BorderBrushProperty, _panel.GetResourceObservable("BrushBorder"));
            Content = new StackPanel { Children = { _button, _panel } };
            ShowCurrent();
        }

        /// <summary>Вместо выбранного столбца показать этот текст, а кнопку выключить («= 1 для всех товаров»).</summary>
        public string? Locked
        {
            get => _locked;
            set
            {
                _locked = value;
                _button.IsEnabled = value is null;
                if (value is not null)
                    _panel.IsVisible = false;
                ShowCurrent();
            }
        }

        public int Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                ShowCurrent();
            }
        }

        public void SetItems(List<(string Title, string Example)> items)
        {
            _items = items;
            ShowCurrent();
        }

        public void CloseList() => _panel.IsVisible = false;

        public bool IsListOpen => _panel.IsVisible;

        private Control Line(string title, string? example, bool soft, bool arrow)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(arrow ? "Auto,*,Auto" : "Auto,*") };
            var t = new TextBlock { Text = title, FontSize = 13, FontWeight = soft ? FontWeight.Normal : FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, TextAlignment = TextAlignment.Left, MaxWidth = 300 };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
            grid.Children.Add(t);
            if (!string.IsNullOrEmpty(example))
            {
                var e = new TextBlock { Text = example, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, TextAlignment = TextAlignment.Left, Margin = new Thickness(12, 0, 0, 0) };
                e.Bind(TextBlock.ForegroundProperty, e.GetResourceObservable("BrushTextSoft"));
                Grid.SetColumn(e, 1);
                grid.Children.Add(e);
            }
            if (arrow)
            {
                var a = new TextBlock { Text = "▾", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                a.Bind(TextBlock.ForegroundProperty, a.GetResourceObservable("BrushTextSoft"));
                Grid.SetColumn(a, 2);
                grid.Children.Add(a);
            }
            return grid;
        }

        private void ShowCurrent()
        {
            if (_locked is not null)
                _button.Content = Line(_locked, null, soft: false, arrow: false);
            else if (_selected >= 0 && _selected < _items.Count)
                _button.Content = Line(_items[_selected].Title, _items[_selected].Example, soft: false, arrow: true);
            else
                _button.Content = Line(_allowNone ? _none : (_items.Count > 0 ? _items[0].Title : _none), null, soft: _allowNone, arrow: true);
        }

        private void Rebuild()
        {
            _list.Children.Clear();
            void Add(string title, string? example, int index, bool none)
            {
                var b = new Button { Content = Line(title, example, soft: none, arrow: false), MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Focusable = false };
                b.Classes.Add(index == _selected ? "modal-primary" : "modal-secondary");
                b.Margin = new Thickness(0, 0, 0, 3);
                b.Click += (_, _) =>
                {
                    _panel.IsVisible = false;
                    if (index != _selected)
                        SelectedChanged?.Invoke(index);
                };
                _list.Children.Add(b);
            }
            if (_allowNone)
                Add(_none, null, -1, true);
            for (var i = 0; i < _items.Count; i++)
                Add(_items[i].Title, _items[i].Example, i, false);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _top = TopLevel.GetTopLevel(this);
            _top?.AddHandler(PointerPressedEvent, OnTopPressed, RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            _top?.RemoveHandler(PointerPressedEvent, OnTopPressed);
            _top = null;
            base.OnDetachedFromVisualTree(e);
        }

        private void OnTopPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!_panel.IsVisible)
                return;
            for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
            {
                if (ReferenceEquals(v, this))
                    return;
            }
            _panel.IsVisible = false;
        }
    }
}
