using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.InvoiceImport;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-27, загрузка электронных накладных: ручное сопоставление столбцов, когда автоподбор не уверен. Для каждого поля — шаг ◀ ▶ по
/// столбцам файла (без выпадающих списков: в окнах склада их список не открывается), строка заголовков тоже шагами, ниже — живой разбор
/// первых строк с выбранными столбцами. Кнопки ≥ 44 px. Результат: <see cref="HeaderIndex"/> и <see cref="Mapping"/> (null — отмена).</summary>
public sealed class InvoiceColumnMapWindow : Window
{
    private readonly ProductFileTable _table;
    private readonly string _fileName;
    private int _headerIndex;
    private readonly InvoiceColumnMapping _mapping;
    private readonly Dictionary<InvoiceField, TextBlock> _labels = new();
    private readonly TextBlock _headerLabel = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, MinWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _preview = new() { Spacing = 2 };
    private readonly TextBlock _previewNote = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _remember;
    private readonly Button _ok;

    public InvoiceColumnMapping? Mapping { get; private set; }
    public int HeaderIndex { get; private set; }
    public bool Remember => _remember.IsChecked == true;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // Поля, которые можно указать вручную (первые три обязательны).
    private static readonly InvoiceField[] Fields =
    [
        InvoiceField.Name, InvoiceField.Quantity, InvoiceField.Price, InvoiceField.Sum, InvoiceField.Barcode, InvoiceField.Article, InvoiceField.Unit,
        InvoiceField.SumWithVat, InvoiceField.VatRate,
    ];

    public InvoiceColumnMapWindow(ProductFileTable table, int headerIndex, InvoiceColumnMapping initial, string fileName)
    {
        _table = table;
        _fileName = fileName;
        _headerIndex = Math.Clamp(headerIndex, 0, Math.Max(0, table.Rows.Count - 1));
        _mapping = new InvoiceColumnMapping();
        foreach (var (field, column) in initial.Columns)
            _mapping.Set(field, column);

        Title = T("Столбцы накладной", "Накладнойдун мамычалары", "Invoice columns", "Fatura sütunları", "Yuk xati ustunlari");
        SectionCards.AsModal(this, 752);

        _previewNote.Bind(TextBlock.ForegroundProperty, _previewNote.GetResourceObservable("BrushTextSoft"));
        _remember = new CheckBox
        {
            IsChecked = true, MinHeight = 44,
            Content = T("Запомнить для файлов с такими же заголовками", "Ушундай аталыштагы файлдар үчүн эстеп калуу", "Remember for files with the same headers",
                "Aynı başlıklı dosyalar için hatırla", "Shunday sarlavhali fayllar uchun eslab qolish"),
        };

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(StepperRow(T("Строка заголовков", "Аталыштар сабы", "Header row", "Başlık satırı", "Sarlavha qatori"), _headerLabel, -1, step =>
        {
            _headerIndex = Math.Clamp(_headerIndex + step, 0, Math.Max(0, _table.Rows.Count - 1));
            Refresh();
        }));
        foreach (var field in Fields)
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
            _labels[field] = label;
            var f = field;
            root.Children.Add(StepperRow(InvoiceColumnMapping.FieldLabel(f) + (f is InvoiceField.Name or InvoiceField.Quantity or InvoiceField.Price ? " *" : ""), label, (int)f, step => Step(f, step)));
        }
        root.Children.Add(_remember);
        root.Children.Add(new TextBlock
        {
            Text = T("Так будут прочитаны первые строки:", "Биринчи саптар ушундай окулат:", "The first rows will be read like this:", "İlk satırlar şöyle okunacak:", "Birinchi qatorlar shunday o'qiladi:"),
            FontWeight = FontWeight.SemiBold, FontSize = 13,
        });
        root.Children.Add(SectionCards.Group(this, new StackPanel { Spacing = 4, Children = { _preview, _previewNote } }));

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.MinHeight = 44;
        cancel.Click += (_, _) => Close();
        _ok = SectionCards.Primary(T("Продолжить", "Улантуу", "Continue", "Devam", "Davom etish"), isDefault: false);
        _ok.MinHeight = 44;
        _ok.Click += (_, _) =>
        {
            if (!_mapping.IsComplete)
                return;
            HeaderIndex = _headerIndex;
            Mapping = _mapping;
            Close();
        };

        Content = SectionCards.Shell(Title,
            T($"Файл «{fileName}»: укажите, где наименование, количество и цена. Поля со звёздочкой обязательны (вместо цены можно указать сумму).",
                $"«{fileName}» файлы: аталышы, саны жана баасы кайда экенин көрсөтүңүз. Жылдызчалуу талаалар милдеттүү (баасынын ордуна сумманы көрсөтсөңүз болот).",
                $"File “{fileName}”: point out where the name, quantity and price are. Fields with a star are required (an amount can replace the price).",
                $"«{fileName}» dosyası: ad, miktar ve fiyatın nerede olduğunu belirtin. Yıldızlı alanlar zorunludur (fiyat yerine tutar verilebilir).",
                $"«{fileName}» fayli: nomi, miqdori va narxi qayerdaligini ko'rsating. Yulduzchali maydonlar majburiy (narx o'rniga summani ko'rsatish mumkin)."),
            root, SectionCards.Buttons(cancel, _ok));
        Refresh();
    }

    private Control StepperRow(string caption, TextBlock value, int fieldId, Action<int> step)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,Auto,*,Auto"), MinHeight = 44 };
        var cap = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        cap.Bind(TextBlock.ForegroundProperty, cap.GetResourceObservable("BrushText"));
        Grid.SetColumn(cap, 0);
        var left = StepButton("◀", T("Предыдущий столбец", "Мурунку мамыча", "Previous column", "Önceki sütun", "Oldingi ustun"), () => step(-1));
        var right = StepButton("▶", T("Следующий столбец", "Кийинки мамыча", "Next column", "Sonraki sütun", "Keyingi ustun"), () => step(1));
        Grid.SetColumn(left, 1);
        value.Margin = new Thickness(10, 0);
        value.FontSize = 13;
        Grid.SetColumn(value, 2);
        Grid.SetColumn(right, 3);
        grid.Children.Add(cap);
        grid.Children.Add(left);
        grid.Children.Add(value);
        grid.Children.Add(right);
        return grid;
    }

    private static Button StepButton(string text, string tip, Action click)
    {
        var b = new Button { Content = text, MinWidth = 44, MinHeight = 44, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        b.Classes.Add("modal-secondary");
        Avalonia.Automation.AutomationProperties.SetName(b, tip);
        ToolTip.SetTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private string ColumnTitle(int column)
    {
        var headers = _table.Rows.Count > 0 ? _table.Rows[_headerIndex].Cells : [];
        var text = column < headers.Length ? headers[column].Trim() : "";
        return ColumnLetter(column) + (text.Length > 0 ? " — " + text : "");
    }

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

    private void Step(InvoiceField field, int step)
    {
        var current = _mapping.ColumnOf(field);
        var count = Math.Max(1, _table.ColumnCount);
        // -1 («нет») ... count-1
        var next = current + step;
        if (next < -1)
            next = count - 1;
        if (next >= count)
            next = -1;
        _mapping.Set(field, next);
        Refresh();
    }

    private void Refresh()
    {
        var headerCells = _table.Rows.Count > 0 ? _table.Rows[_headerIndex].Cells : [];
        var rowNo = _table.Rows.Count > 0 ? _table.Rows[_headerIndex].FileRow : 0;
        _headerLabel.Text = T($"строка {rowNo}: ", $"сап {rowNo}: ", $"row {rowNo}: ", $"satır {rowNo}: ", $"qator {rowNo}: ")
            + string.Join(" | ", headerCells.Where(c => !string.IsNullOrWhiteSpace(c)).Take(4).Select(c => c.Trim()));
        foreach (var (field, label) in _labels)
        {
            var c = _mapping.ColumnOf(field);
            label.Text = c < 0 ? T("— нет —", "— жок —", "— none —", "— yok —", "— yo'q —") : ColumnTitle(c);
            label.Foreground = null;
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable(c < 0 ? "BrushTextSoft" : "BrushText"));
        }

        _preview.Children.Clear();
        _ok.IsEnabled = _mapping.IsComplete;
        if (!_mapping.IsComplete)
        {
            _previewNote.Text = T("Укажите наименование (или штрихкод), количество и цену (или сумму).", "Аталышын (же штрих-код), санын жана баасын (же сумманы) көрсөтүңүз.",
                "Pick the name (or barcode), the quantity and the price (or amount).", "Adı (veya barkodu), miktarı ve fiyatı (veya tutarı) seçin.", "Nomi (yoki shtrix-kod), miqdori va narxi (yoki summasi)ni tanlang.");
            return;
        }

        var doc = InvoiceTableParser.Parse(_table, _headerIndex, _mapping, _fileName);
        foreach (var item in doc.Items.Take(4))
        {
            var line = new TextBlock
            {
                FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Text = $"{item.Name} · {InvoiceText.Num(item.Quantity)} {item.Unit} × {InvoiceText.Num(item.UnitPrice(false))}"
                       + (item.Barcode.Length > 0 ? $" · {item.Barcode}" : ""),
            };
            line.Bind(TextBlock.ForegroundProperty, line.GetResourceObservable("BrushText"));
            _preview.Children.Add(line);
        }
        _previewNote.Text = doc.Items.Count == 0
            ? T("С такими столбцами строки товаров не найдены — попробуйте другую строку заголовков.", "Мындай мамычалар менен товар саптары табылган жок — башка аталыштар сабын сынаңыз.",
                "No product rows with these columns — try another header row.", "Bu sütunlarla ürün satırı yok — başka bir başlık satırı deneyin.", "Bu ustunlar bilan mahsulot qatorlari yo'q — boshqa sarlavha qatorini sinab ko'ring.")
            : T($"Найдено строк товара: {doc.Items.Count}.", $"Табылган товар саптары: {doc.Items.Count}.", $"Product rows found: {doc.Items.Count}.", $"Bulunan ürün satırı: {doc.Items.Count}.", $"Topilgan mahsulot qatorlari: {doc.Items.Count}.");
        _ok.IsEnabled = doc.Items.Count > 0;
    }
}
