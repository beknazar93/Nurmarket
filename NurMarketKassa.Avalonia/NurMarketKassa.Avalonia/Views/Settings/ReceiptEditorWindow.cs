using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-21 (просьба владельца: «Печать чека! Редактор нужен!»): «Настройки → Печать → Чек → Редактор чека». Слева — блоки чека (включить, переставить,
/// выбрать блок), под ними — вид выбранного блока (выравнивание, жирность, размер, свой текст); справа — предпросмотр «как напечатает принтер» (58 / 80 мм) на
/// образце чека. Предпросмотр и печать идут через один конвейер (ReceiptSample → ReceiptPrintPipeline), поэтому на бумаге то же, что на экране. Окно строится в коде
/// (как LabelScaleEditWindow): в ModalShell, помещается на 1024×600, содержимое колонок прокручивается, кнопки внизу всегда видны.
/// Фискальный чек ГНС печатается по требованиям ГНС и здесь не меняется.</summary>
public sealed class ReceiptEditorWindow : Window
{
    private ReceiptTemplate _template;
    private string _selected = ReceiptTemplate.Items;
    private int _paperMm;
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly StackPanel _props = new() { Spacing = 8 };
    private readonly StackPanel _paper = new() { Spacing = 0 };
    private readonly Border _paperHost = new() { Name = "ReceiptPaper", Background = Brushes.White, Padding = new Thickness(10, 12), HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(4) };
    private readonly TextBlock _status = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly RadioButton _paper58 = new() { GroupName = "receiptPaper", Content = "58 мм", MinHeight = 44, MinWidth = 90 };
    private readonly RadioButton _paper80 = new() { GroupName = "receiptPaper", Content = "80 мм", MinHeight = 44, MinWidth = 90 };
    private bool _loading;

    /// <summary>Тесты: подмена отправки образца на принтер (настоящий принтер не трогаем). Возвращает текст ошибки или null.</summary>
    public static Func<string, Task<string?>>? TestPrintOverride { get; set; }

    /// <summary>Сохранили (а не закрыли/отменили).</summary>
    public bool Saved { get; private set; }

    private static string L(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public ReceiptEditorWindow()
    {
        _template = ReceiptTemplateEngine.Load();
        _paperMm = ReceiptPaperProfile.NormalizePaperWidthMm(UserPreferences.Instance.ReceiptPaperWidthMm);

        Title = L("Редактор чека", "Чек редактору", "Receipt editor", "Fiş düzenleyici", "Chek muharriri");
        SectionCards.AsModal(this, 960);
        Styles.Add(new StyleInclude(new Uri("avares://NurMarketKassa.Avalonia/"))
        {
            Source = new Uri("avares://NurMarketKassa.Avalonia/Views/Settings/SettingsSharedStyles.axaml"),
        });

        // Левая колонка: ширина ленты, блоки, вид выбранного блока.
        _paper58.Content = L("58 мм", "58 мм", "58 mm", "58 mm", "58 mm");
        _paper80.Content = L("80 мм", "80 мм", "80 mm", "80 mm", "80 mm");
        _paper58.IsChecked = _paperMm == ReceiptPaperProfile.Paper58mm;
        _paper80.IsChecked = _paperMm == ReceiptPaperProfile.Paper80mm;
        _paper58.IsCheckedChanged += (_, _) => { if (_paper58.IsChecked == true) { _paperMm = ReceiptPaperProfile.Paper58mm; RefreshPreview(); } };
        _paper80.IsCheckedChanged += (_, _) => { if (_paper80.IsChecked == true) { _paperMm = ReceiptPaperProfile.Paper80mm; RefreshPreview(); } };
        var paperRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        paperRow.Children.Add(new TextBlock { Text = L("Предпросмотр для ленты:", "Алдын ала көрүү — лента:", "Preview for paper:", "Önizleme — kağıt:", "Oldindan ko‘rish — lenta:"), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        paperRow.Children.Add(_paper58);
        paperRow.Children.Add(_paper80);

        var left = new StackPanel { Spacing = 10 };
        left.Children.Add(Card(L("Блоки чека", "Чектин блоктору", "Receipt blocks", "Fiş blokları", "Chek bloklari"),
            L("Галочка — печатать блок. Стрелки — переставить. Нажмите на блок, чтобы настроить его вид.",
              "Белги — блокту басуу. Жебелер — жылдыруу. Көрүнүшүн жөндөө үчүн блокту басыңыз.",
              "Tick — print the block. Arrows — reorder. Click a block to adjust how it looks.",
              "İşaret — bloğu yazdır. Oklar — sırayı değiştir. Görünümü ayarlamak için bloğa dokunun.",
              "Belgi — blokni chop etish. Strelkalar — o‘rnini almashtirish. Ko‘rinishini sozlash uchun blokni bosing."),
            _rows));
        left.Children.Add(Card(L("Вид выбранного блока", "Тандалган блоктун көрүнүшү", "Selected block look", "Seçili bloğun görünümü", "Tanlangan blok ko‘rinishi"), null, _props));

        var leftScroll = new ScrollViewer { Content = left, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 8, 0) };

        // Правая колонка: бумага.
        _paperHost.Child = _paper;
        var right = new StackPanel { Spacing = 8 };
        right.Children.Add(paperRow);
        right.Children.Add(new TextBlock
        {
            Text = L("Так напечатает принтер (образец; название, адрес, ИНН и кассир — ваши).",
                "Принтер ушундай басат (үлгү; аталышы, дареги, ИНН жана кассир — өзүңүздүкү).",
                "This is how the printer will print it (sample; name, address, TIN and cashier are yours).",
                "Yazıcı böyle basar (örnek; ad, adres, vergi no ve kasiyer sizinki).",
                "Printer shunday chop etadi (namuna; nom, manzil, STIR va kassir — sizniki)."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        right.Children.Add(_paperHost);
        var rightScroll = new ScrollViewer { Content = right, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Padding = new Thickness(8, 0, 0, 0) };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), MinHeight = 320, MaxHeight = 560 };
        Grid.SetColumn(leftScroll, 0);
        Grid.SetColumn(rightScroll, 1);
        body.Children.Add(leftScroll);
        body.Children.Add(rightScroll);

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(body);
        content.Children.Add(_status);

        var cancel = SectionCards.Cancel(L("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        var save = SectionCards.Primary(L("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), isDefault: false);
        save.Click += (_, _) => SaveAndClose();
        var reset = SectionCards.Secondary(L("Сбросить на стандарт", "Стандартка кайтаруу", "Reset to default", "Varsayılana sıfırla", "Standartga qaytarish"));
        reset.Click += (_, _) => { _template = ReceiptTemplate.Default(); Rebuild(); SetStatus(L("Стандартный вид чека. Нажмите «Сохранить», чтобы применить.", "Чектин стандарттык түрү. Колдонуу үчүн «Сактоо» басыңыз.", "Standard receipt look. Press “Save” to apply.", "Standart fiş görünümü. Uygulamak için «Kaydet».", "Standart chek ko‘rinishi. Qo‘llash uchun «Saqlash» ni bosing."), false); };
        var test = SectionCards.Secondary(L("Тест-печать", "Сыноо басуу", "Test print", "Deneme baskısı", "Sinov chop etish"));
        test.Click += async (_, _) => await TestPrintAsync(test);

        Content = SectionCards.Shell(
            L("Редактор чека", "Чек редактору", "Receipt editor", "Fiş düzenleyici", "Chek muharriri"),
            L("Какие блоки печатать, в каком порядке и каким шрифтом", "Кайсы блоктор кандай тартипте жана кандай шрифт менен басылат", "Which blocks to print, in what order and in what font", "Hangi bloklar, hangi sırayla ve hangi yazıyla basılır", "Qaysi bloklar, qanday tartibda va qanday shrift bilan chop etiladi"),
            content, SectionCards.Buttons(cancel, reset, test, save));

        Rebuild();
    }

    private static Border Card(string title, string? hint, Control body)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, Classes = { "SettingsCardTitle" }, FontSize = 14 });
        if (hint is not null)
            stack.Children.Add(new TextBlock { Text = hint, Classes = { "SettingsCardBody" }, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(body);
        return new Border { Classes = { "SettingsCard" }, Padding = new Thickness(12), Child = stack };
    }

    /// <summary>Название блока на языке интерфейса.</summary>
    public static string BlockName(string id) => id switch
    {
        ReceiptTemplate.Title => L("Заголовок («Товарный чек»)", "Аталышы («Товардык чек»)", "Title (“Sales receipt”)", "Başlık («Satış fişi»)", "Sarlavha («Tovar cheki»)"),
        ReceiptTemplate.HeaderText => L("Свой текст в шапке", "Башындагы өз тексти", "Your header text", "Kendi üst metniniz", "O‘z sarlavha matnim"),
        ReceiptTemplate.Store => L("Магазин: название, ИНН, адрес", "Дүкөн: аталышы, ИНН, дареги", "Store: name, TIN, address", "Mağaza: ad, vergi no, adres", "Do‘kon: nomi, STIR, manzil"),
        ReceiptTemplate.Meta => L("Кассир, номер, дата и время", "Кассир, номер, күнү жана убактысы", "Cashier, number, date and time", "Kasiyer, numara, tarih ve saat", "Kassir, raqam, sana va vaqt"),
        ReceiptTemplate.Notes => L("Пометки: офлайн, прокат, долг", "Белгилер: оффлайн, прокат, карыз", "Notes: offline, rental, debt", "Notlar: çevrimdışı, kiralama, borç", "Belgilar: oflayn, ijara, qarz"),
        ReceiptTemplate.Items => L("Товары", "Товарлар", "Items", "Ürünler", "Tovarlar"),
        ReceiptTemplate.Totals => L("Скидки и оплата (сдача, долг)", "Арзандатуулар жана төлөм (кайтарым, карыз)", "Discounts and payment (change, debt)", "İndirimler ve ödeme (para üstü, borç)", "Chegirmalar va to‘lov (qaytim, qarz)"),
        ReceiptTemplate.Total => L("Итого", "Жыйынтык", "Total", "Toplam", "Jami"),
        ReceiptTemplate.Status => L("Повторная печать и возврат", "Кайра басуу жана кайтаруу", "Reprint and return marks", "Tekrar baskı ve iade", "Qayta chop etish va qaytarish"),
        ReceiptTemplate.Warranty => L("Гарантия и возврат", "Кепилдик жана кайтаруу", "Warranty and returns", "Garanti ve iade", "Kafolat va qaytarish"),
        ReceiptTemplate.Footer => L("Благодарность и ссылка Telegram", "Ыраазычылык жана Telegram шилтемеси", "Thanks and Telegram link", "Teşekkür ve Telegram bağlantısı", "Minnatdorchilik va Telegram havolasi"),
        ReceiptTemplate.FooterText => L("Свой текст в подвале", "Төмөнкү өз тексти", "Your footer text", "Kendi alt metniniz", "O‘z quyi matnim"),
        _ => id,
    };

    private void Rebuild()
    {
        _loading = true;
        _rows.Children.Clear();
        for (var i = 0; i < _template.Blocks.Count; i++)
            _rows.Children.Add(BuildRow(i));
        BuildProps();
        _loading = false;
        RefreshPreview();
    }

    private Control BuildRow(int index)
    {
        var block = _template.Blocks[index];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), MinHeight = 44 };
        var on = new CheckBox { IsChecked = block.On, Classes = { "ToggleSwitch" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Avalonia.Automation.AutomationProperties.SetName(on, BlockName(block.Id));
        on.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            block.On = on.IsChecked == true;
            RefreshPreview();
        };
        var name = new TextBlock { Text = BlockName(block.Id), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 6, 0) };
        var up = new Button { Content = "▲", Width = 44, Height = 44, Padding = new Thickness(0), IsEnabled = index > 0 };
        var down = new Button { Content = "▼", Width = 44, Height = 44, Padding = new Thickness(0), IsEnabled = index < _template.Blocks.Count - 1 };
        up.Classes.Add("modal-secondary");
        down.Classes.Add("modal-secondary");
        Avalonia.Automation.AutomationProperties.SetName(up, L("Выше", "Жогору", "Move up", "Yukarı", "Yuqoriga"));
        Avalonia.Automation.AutomationProperties.SetName(down, L("Ниже", "Төмөн", "Move down", "Aşağı", "Pastga"));
        up.Click += (_, _) => Move(index, -1);
        down.Click += (_, _) => Move(index, 1);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(up, 2);
        Grid.SetColumn(down, 3);
        grid.Children.Add(on);
        grid.Children.Add(name);
        grid.Children.Add(up);
        grid.Children.Add(down);

        var border = new Border { Padding = new Thickness(8, 2), CornerRadius = new CornerRadius(8), Child = grid, BorderThickness = new Thickness(1) };
        var selected = block.Id == _selected;
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable(selected ? "BrushAccentSoft" : "BrushPanelSoft"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable(selected ? "BrushAccent" : "BrushBorder"));
        border.PointerPressed += (_, e) =>
        {
            if (e.Source is Button || e.Source is CheckBox) return;
            _selected = block.Id;
            Rebuild();
        };
        return border;
    }

    private void Move(int index, int delta)
    {
        var to = index + delta;
        if (to < 0 || to >= _template.Blocks.Count) return;
        (_template.Blocks[index], _template.Blocks[to]) = (_template.Blocks[to], _template.Blocks[index]);
        Rebuild();
    }

    private void BuildProps()
    {
        _props.Children.Clear();
        var block = _template.Blocks.FirstOrDefault(b => b.Id == _selected) ?? _template.Blocks[0];
        _selected = block.Id;
        _props.Children.Add(new TextBlock { Text = BlockName(block.Id), FontWeight = FontWeight.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap });

        // Выравнивание.
        var align = new ComboBox { Classes = { "TouchComboBox" }, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 44 };
        var options = new (string Key, string Text)[]
        {
            ("auto", L("Как в чеке", "Чектегидей", "As in receipt", "Fişteki gibi", "Chekdagidek")),
            ("left", L("Слева", "Солго", "Left", "Sola", "Chapga")),
            ("center", L("По центру", "Ортого", "Center", "Ortaya", "Markazga")),
            ("right", L("Справа", "Оңго", "Right", "Sağa", "O‘ngga")),
        };
        foreach (var o in options)
            align.Items.Add(new ComboBoxItem { Content = o.Text, Tag = o.Key });
        align.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o.Key == block.Align));
        align.SelectionChanged += (_, _) =>
        {
            if (_loading || align.SelectedItem is not ComboBoxItem { Tag: string key }) return;
            block.Align = key;
            RefreshPreview();
        };
        _props.Children.Add(Field(L("Выравнивание", "Тегиздөө", "Alignment", "Hizalama", "Tekislash"), align));

        // Размер.
        var size = new ComboBox { Classes = { "TouchComboBox" }, HorizontalAlignment = HorizontalAlignment.Stretch, Height = 44 };
        size.Items.Add(new ComboBoxItem { Content = L("Обычный", "Кадимки", "Normal", "Normal", "Oddiy"), Tag = 1 });
        size.Items.Add(new ComboBoxItem { Content = L("Высокий (×2 по высоте)", "Бийик (бийиктиги ×2)", "Tall (×2 height)", "Yüksek (×2 yükseklik)", "Baland (balandligi ×2)"), Tag = 2 });
        size.Items.Add(new ComboBoxItem { Content = L("Крупный (×2)", "Чоң (×2)", "Large (×2)", "Büyük (×2)", "Katta (×2)"), Tag = 3 });
        size.SelectedIndex = Math.Clamp(block.Size, 1, 3) - 1;
        size.SelectionChanged += (_, _) =>
        {
            if (_loading || size.SelectedItem is not ComboBoxItem { Tag: int s }) return;
            block.Size = s;
            RefreshPreview();
        };
        _props.Children.Add(Field(L("Размер шрифта", "Шрифт өлчөмү", "Font size", "Yazı boyutu", "Shrift o‘lchami"), size));

        // Жирность.
        var bold = new CheckBox { IsChecked = block.Bold, Classes = { "ToggleSwitch" }, Content = L("Жирный шрифт", "Калың шрифт", "Bold", "Kalın yazı", "Qalin shrift"), MinHeight = 44 };
        bold.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            block.Bold = bold.IsChecked == true;
            RefreshPreview();
        };
        _props.Children.Add(bold);

        // Свой текст (только шапка и подвал).
        if (ReceiptTemplate.IsCustomTextBlock(block.Id))
        {
            var text = new TextBox
            {
                Classes = { "ModernTextBox" },
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 88,
                MaxLength = ReceiptTemplate.MaxTextChars,
                Text = block.Text ?? "",
                Watermark = L("Например: Мы работаем с 9:00 до 21:00", "Мисалы: Биз 9:00дөн 21:00гө чейин иштейбиз", "For example: We are open 9:00–21:00", "Örneğin: 09:00–21:00 arası açığız", "Masalan: Biz 9:00–21:00 ishlaymiz"),
            };
            text.TextChanged += (_, _) =>
            {
                if (_loading) return;
                block.Text = text.Text;
                if (!block.On && !string.IsNullOrWhiteSpace(text.Text))
                    block.On = true;
                RefreshPreview();
            };
            _props.Children.Add(Field(L("Текст (до 6 строк)", "Текст (6 саптан ашык эмес)", "Text (up to 6 lines)", "Metin (en çok 6 satır)", "Matn (6 qatorgacha)"), text));
        }
    }

    private static Control Field(string label, Control field)
    {
        var s = new StackPanel { Spacing = 4 };
        s.Children.Add(new TextBlock { Text = label, Classes = { "FieldLabel" } });
        s.Children.Add(field);
        return s;
    }

    private static readonly FontFamily Mono = new("Consolas, Courier New, monospace");

    private void RefreshPreview()
    {
        if (_loading) return;
        RenderedReceipt receipt;
        try
        {
            receipt = ReceiptSample.Render(_template, _paperMm);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Редактор чека: предпросмотр не собран: {ex.Message}", "WARNING");
            SetStatus(ex.Message, true);
            return;
        }

        _paper.Children.Clear();
        const double fontSize = 12;
        const double charWidth = 7.2;
        _paperHost.Width = (receipt.Width + 1) * charWidth + 20;
        foreach (var line in receipt.Lines)
        {
            // Строка бумаги — всегда чёрным по белому (как настоящая лента), независимо от темы программы.
            var tb = new TextBlock
            {
                Text = line.Text.Length == 0 ? " " : line.Text,
                FontFamily = Mono,
                FontSize = line.Size >= 3 ? fontSize * 2 : fontSize,
                FontWeight = line.Bold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.NoWrap,
            };
            if (line.Size == 2)
                _paper.Children.Add(new LayoutTransformControl { LayoutTransform = new ScaleTransform(1, 2), Child = tb });
            else
                _paper.Children.Add(tb);
        }
    }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.Bind(TextBlock.ForegroundProperty, _status.GetResourceObservable(error ? "BrushDanger" : "BrushTextSoft"));
    }

    private async Task TestPrintAsync(Button button)
    {
        button.IsEnabled = false;
        try
        {
            var text = ReceiptSample.BuildText(_template, UserPreferences.Instance.ReceiptPaperWidthMm);
            string? error;
            if (TestPrintOverride is { } hook)
                error = await hook(text);
            else
                error = await Task.Run(() =>
                {
                    try
                    {
                        // Как настоящая печать: тот же путь, что у чека после оплаты (режим текст/графика — по настройкам).
                        ReceiptPrintService.PrintReceipt("{}", receiptText: text);
                        return null;
                    }
                    catch (Exception ex)
                    {
                        return ex.Message;
                    }
                });
            SetStatus(error ?? L("Образец отправлен на принтер.", "Үлгү принтерге жөнөтүлдү.", "Sample sent to the printer.", "Örnek yazıcıya gönderildi.", "Namuna printerga yuborildi."), error is not null);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void SaveAndClose()
    {
        try
        {
            ReceiptTemplateEngine.Save(_template);
            Saved = true;
            Close();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    // Для тестов: текущий черновик и выбранный блок.
    public ReceiptTemplate DraftForTests => _template;
    public void SelectForTests(string id) { _selected = id; Rebuild(); }
}
