using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Controls;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-20 (жалоба владельца «неудобная и массовая загрузка товаров из другой программы!»): пошаговый мастер.
// 1 «Откуда у вас данные?» (плитки программ) → 2 «Файл» (зона перетаскивания, сведения о файле, узнанная программа) →
// 3 «Проверьте» (итог крупно, что делать с существующими, таблица, «Дополнительно» — боковая полоса) → 4 «Загрузка» (ход и итог).
// Разбор файлов, сопоставление столбцов, профили программ, журнал и загрузка не менялись — поменялись раскладка и порядок экранов.
// Раскладка — Grid: подпись отдельной строкой над полем, высоты 36/44, перенос по ширине; страницы всегда в дереве (видимость), нижняя панель кнопок закреплена.
public partial class BulkImportWizardWindow
{
    private int _step = 1;
    private int _pendingSource; // выбранная плитка: индекс как в _sourceCombo (0 — «по файлу»)
    private long _fileSize;
    private bool _modeSync;
    private TextBlock _sourceSub = null!;

    private readonly RadioButton _modeAll = new() { GroupName = "BulkWizardMode", IsChecked = true, MinHeight = 32 };
    private readonly RadioButton _modePrice = new() { GroupName = "BulkWizardMode", MinHeight = 32 };
    private readonly RadioButton _modeNew = new() { GroupName = "BulkWizardMode", MinHeight = 32 };
    private readonly CheckBox _onlySkipped = new() { MinHeight = 32 };
    // 2026-11-20: плашка «Похоже, это не список товаров» (колонки не про товары — почта, компания, цена в месяц): таблица ошибок прячется, пока человек не выберет, что делать
    private readonly Border _notProducts = new() { IsVisible = false, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(12, 8), Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _notProductsText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private bool _notProductsForced;
    private bool _notProductsBlock;
    private readonly ScrollViewer _advCol = new() { IsVisible = false, Width = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _bigSummary = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly Border _apSkippedBox = new() { IsVisible = false, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 8) };
    private readonly TextBlock _filePageTitle = new() { Margin = new Thickness(0, 6, 0, 0), FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _fileTitle = new() { FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _badgeText = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _fileCard = new() { IsVisible = false, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10) };
    private readonly Border _dropZone = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(2), Padding = new Thickness(16, 10) };
    private readonly Border _badge = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Margin = new Thickness(0, 0, 8, 4) };
    private readonly List<(Border Dot, TextBlock Num, TextBlock Label)> _stepItems = [];
    private Control _pageSource = null!, _pageFile = null!, _pageReview = null!, _pageLoad = null!;
    private Button _nextBtn = null!, _backBtn = null!, _warehouseBtn = null!, _closeBtn = null!, _changeSrc = null!, _howLink = null!;

    /// <summary>Переключатель «что делать с существующими» ↔ режим плана: «только цену» = без прихода, «только новые» = существующие пропускаются.
    /// Если в файле нет количества, «остаток и цену» недоступно (остаток всё равно не меняется).</summary>
    private void SetCardsMode(bool cards)
    {
        _modeSync = true;
        try
        {
            if (cards)
                _modePrice.IsChecked = true;
            else
                _modeAll.IsChecked = true;
        }
        finally
        {
            _modeSync = false;
        }
        UpdateModeUi();
    }

    private void UpdateModeUi() => _modeAll.IsEnabled = _mapping.HasQuantity || _apOn;

    private static void Bind(Control c, AvaloniaProperty p, string key) => c.Bind(p, c.GetResourceObservable(key));

    private static TextBlock Soft(string text, double size = 12.5)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        Bind(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private static string FmtSize(long bytes) => bytes switch
    {
        <= 0 => "",
        < 1024 => $"{bytes} " + T("Б", "Б", "B", "B", "B"),
        < 1024 * 1024 => $"{MoneyText.Weight(bytes / 1024.0, "0.#")} " + T("КБ", "КБ", "KB", "KB", "KB"),
        _ => $"{MoneyText.Weight(bytes / 1024.0 / 1024.0, "0.#")} " + T("МБ", "МБ", "MB", "MB", "MB"),
    };

    // ------------------------------------------------------------------ сборка окна

    private void BuildSteps(Button close)
    {
        _closeBtn = close;
        close.Click += async (_, _) => await GuardAsync(RequestCloseAsync, "закрытие").ConfigureAwait(true);

        _backBtn = SectionCards.Secondary(T("Назад", "Артка", "Back", "Geri", "Orqaga"));
        _nextBtn = SectionCards.Primary(T("Далее", "Андан ары", "Next", "İleri", "Keyingi"));
        _warehouseBtn = SectionCards.Primary(T("Открыть склад", "Кампаны ачуу", "Open the warehouse", "Depoyu aç", "Omborni ochish"));
        foreach (var b in new[] { _backBtn, _nextBtn, _warehouseBtn })
            b.MinHeight = 44;
        _backBtn.Click += async (_, _) => await GuardAsync(BackAsync, "назад").ConfigureAwait(true);
        _nextBtn.Click += (_, _) =>
        {
            if (_step == 2 && _nextBtn.IsEnabled)
                GoToStep(3);
        };
        _warehouseBtn.Click += (_, _) => OpenWarehouseAndClose();
        _errorsBtn.Content = T("Скачать пропущенные (CSV)", "Өткөрүлгөндөрдү жүктөө (CSV)", "Download skipped (CSV)", "Atlananları indir (CSV)", "O'tkazilganlarni yuklash (CSV)");

        var steps = BuildStepBar();
        BuildSourcePage();
        BuildFilePage();
        BuildReviewPage();
        BuildLoadPage();

        _bar.Margin = new Thickness(0, 4, 0, 0);
        var statusRow = new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0), IsVisible = false, Children = { _status, _bar } };
        _status.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBlock.TextProperty)
                statusRow.IsVisible = !string.IsNullOrEmpty(_status.Text);
        };

        var pages = new Grid { Children = { _pageSource, _pageFile, _pageReview, _pageLoad } };
        var root = new Grid
        {
            Height = 400, // дальше Refit подгоняет под окно
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
        };
        Grid.SetRow(steps, 0);
        Grid.SetRow(pages, 1);
        Grid.SetRow(statusRow, 2);
        root.Children.Add(steps);
        root.Children.Add(pages);
        root.Children.Add(statusRow);
        _top = root;

        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = Title ?? "", ScreenHeightShare = 1.0, Content = root,
            Footer = SectionCards.Buttons(close, _backBtn, _cancelBtn, _pauseBtn, _errorsBtn, _warehouseBtn, _nextBtn, _loadBtn),
        };

        // перетаскивание файла на окно (в любом шаге, пока загрузка не идёт)
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = !_started && e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            if (_started)
                return;
            var path = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => !string.IsNullOrEmpty(p));
            if (path is not null)
                await GuardAsync(() => LoadPathAsync(path, advanceToReview: false), "перетаскивание файла").ConfigureAwait(true);
        });
        AddHandler(KeyDownEvent, OnWizardKeyDown, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);

        SetAdvanced(false);
        GoToStep(1);
    }

    private Control BuildStepBar()
    {
        var names = new[]
        {
            T("Откуда", "Кайдан", "Source", "Kaynak", "Manba"), T("Файл", "Файл", "File", "Dosya", "Fayl"),
            T("Проверка", "Текшерүү", "Check", "Kontrol", "Tekshiruv"), T("Готово", "Даяр", "Done", "Tamam", "Tayyor"),
        };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 0) };
        for (var i = 0; i < names.Length; i++)
        {
            var num = new TextBlock { Text = (i + 1).ToString(), FontSize = 12, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = num };
            var label = new TextBlock { Text = names[i], FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            _stepItems.Add((dot, num, label));
            bar.Children.Add(dot);
            bar.Children.Add(label);
            if (i < names.Length - 1)
            {
                var line = new Border { Width = 28, Height = 1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0) };
                Bind(line, Border.BackgroundProperty, "BrushBorder");
                bar.Children.Add(line);
            }
        }
        return bar;
    }

    private void UpdateStepBar()
    {
        for (var i = 0; i < _stepItems.Count; i++)
        {
            var (dot, num, label) = _stepItems[i];
            var current = i + 1 == _step;
            var done = i + 1 < _step;
            Bind(dot, Border.BackgroundProperty, current ? "BrushAccent" : done ? "BrushAccentSoft" : "BrushPanelSoft");
            Bind(dot, Border.BorderBrushProperty, current || done ? "BrushAccent" : "BrushBorder");
            Bind(num, TextBlock.ForegroundProperty, current ? "BrushAccentForeground" : done ? "BrushAccentText" : "BrushTextSoft");
            Bind(label, TextBlock.ForegroundProperty, current ? "BrushText" : "BrushTextSoft");
            label.FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal;
            num.Text = done ? "✓" : (i + 1).ToString();
        }
    }

    // ------------------------------------------------------------------ шаг 1: откуда данные

    private void BuildSourcePage()
    {
        var title = new TextBlock { Text = T("Откуда у вас данные?", "Маалыматтар кайдан?", "Where is your data from?", "Verileriniz nereden?", "Ma'lumotlar qayerdan?"), FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        Bind(title, TextBlock.ForegroundProperty, "BrushText");
        var sub = _sourceSub = Soft(T("Выберите программу — подскажем, какой файл выгрузить, и сами подберём столбцы.",
            "Программаны тандаңыз — кайсы файлды чыгарууну айтабыз жана мамычаларды өзүбүз тандайбыз.",
            "Pick the program — we will tell you which file to export and match the columns for you.",
            "Programı seçin — hangi dosyanın alınacağını söyler, sütunları kendimiz eşleriz.",
            "Dasturni tanlang — qaysi faylni chiqarishni aytamiz va ustunlarni o'zimiz moslaymiz."), 13);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 6, 0, 6) };
        var headText = new StackPanel { Spacing = 2, Children = { title, sub } };
        _demoBtn.MinHeight = 36;
        _demoBtn.VerticalAlignment = VerticalAlignment.Top;
        _demoBtn.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_demoBtn, 1);
        head.Children.Add(headText);
        head.Children.Add(_demoBtn);

        var what = T("Товары, цены, остатки", "Товарлар, баа, калдык", "Products, prices, stock", "Ürün, fiyat, stok", "Mahsulot, narx, qoldiq");
        var tiles = new List<Control>();
        for (var i = 0; i < ImportSourceProfiles.All.Count; i++)
        {
            var p = ImportSourceProfiles.All[i];
            var index = i + 1;
            var shortName = p.Id == "1c" ? "1С" : p.Name;
            tiles.Add(MakeTile(p.Id == "1c" ? "1С:Розница / Торговля" : p.Name, what, shortName, HowToSteps(shortName, p.HowToExport), () => ChooseSource(index)));
        }
        tiles.Add(MakeTile(T("Excel / CSV", "Excel / CSV", "Excel / CSV", "Excel / CSV", "Excel / CSV"),
            T("Таблица со штрихкодами", "Штрих-кодду таблица", "Any table with barcodes", "Barkodlu tablo", "Shtrix-kodli jadval"),
            "Excel / CSV", T("1) Откройте таблицу в Excel.\n2) Первая строка — заголовки столбцов (Штрихкод, Название, Количество, Цена продажи…).\n3) Сохраните как .xlsx или .csv.\nКнопка «Шаблон Excel» на следующем шаге даст пример.",
                "1) Таблицаны Excel'де ачыңыз.\n2) Биринчи сап — мамычалардын аталыштары (Штрих-код, Аталышы, Саны, Сатуу баасы…).\n3) .xlsx же .csv катары сактаңыз.\nКийинки кадамдагы «Excel үлгүсү» мисал берет.",
                "1) Open the table in Excel.\n2) The first row holds column headers (Barcode, Name, Quantity, Sale price…).\n3) Save as .xlsx or .csv.\nThe “Excel template” button on the next step gives an example.",
                "1) Tabloyu Excel'de açın.\n2) İlk satır sütun başlıklarıdır (Barkod, Ürün adı, Miktar, Satış fiyatı…).\n3) .xlsx veya .csv olarak kaydedin.\nSonraki adımdaki «Excel şablonu» örnek verir.",
                "1) Jadvalni Excel'da oching.\n2) Birinchi qator — ustun sarlavhalari (Shtrix-kod, Nomi, Miqdor, Sotuv narxi…).\n3) .xlsx yoki .csv sifatida saqlang.\nKeyingi qadamdagi «Excel shabloni» namuna beradi."),
            () => ChooseSource(0)));
        if (AutoPartsProfile.IsActive)
            tiles.Add(MakeTile(T("Прайс поставщика", "Жеткирүүчүнүн прайси", "Supplier price list", "Tedarikçi fiyat listesi", "Yetkazib beruvchi prays-listi"),
                T("Автозапчасти: прайс-лист", "Унаа тетиктери: прайс", "Auto parts price list", "Oto parça fiyat listesi", "Avto qismlar prays-listi"),
                T("прайса", "прайс", "a price list", "fiyat listesi", "prays-list"), AutoPartsPriceImport.HowTo,
                () => ChooseSource(ImportSourceProfiles.All.Count + 1)));
        tiles.Add(MakeTile(T("Клиенты и долги", "Кардарлар жана карыздар", "Customers and debts", "Müşteriler ve borçlar", "Mijozlar va qarzlar"),
            T("Клиенты из Excel/CSV", "Excel/CSV.ден кардарлар", "Customers from Excel/CSV", "Excel/CSV.den müşteriler", "Excel/CSV dan mijozlar"),
            null, null, () => _ = GuardAsync(async () => await ClientsImportWindow.ShowAsync(this).ConfigureAwait(true), "клиенты")));
        tiles.Add(MakeTile(T("Из Nurmarket (старая)", "Nurmarket'тен (эски)", "From Nurmarket (old)", "Nurmarket'ten (eski)", "Nurmarket dan (eski)"),
            T("CSV, остаток заменяется", "CSV, калдык алмашат", "CSV, stock is replaced", "CSV, stok değişir", "CSV, qoldiq almashadi"),
            null, null, () => BulkImportWindow.Open(this)));

        // колонок: 4 на ширине от ~700 (11 плиток в 3 ряда), 3 — от ~520, иначе 2
        // 2026-11-20: обход кассы — в высоком окне ряды плиток растягивались на всю высоту (пустые рамки по 190 px, щели 4 px): держим сверху по высоте содержимого (щель 4 px оставлена: тест «влезает без прокрутки» на 1024×600 uz).
        var grid = new UniformGrid { Columns = 4, VerticalAlignment = VerticalAlignment.Top };
        foreach (var t in tiles)
            grid.Children.Add(t);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
        scroll.SizeChanged += (_, e) => grid.Columns = e.NewSize.Width >= 1500 ? 6 : e.NewSize.Width >= 700 ? 4 : e.NewSize.Width >= 520 ? 3 : 2;

        var page = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(scroll, 1);
        page.Children.Add(head);
        page.Children.Add(scroll);
        _pageSource = page;
    }

    private static string HowToSteps(string name, string howTo)
    {
        var head = T($"Как выгрузить файл из {name}:", $"{name} программасынан файлды кантип чыгаруу:", $"How to export the file from {name}:", $"{name} programından dosya nasıl alınır:", $"{name} dasturidan faylni qanday chiqarish:");
        var body = howTo;
        var colon = body.IndexOf(':');
        if (colon is > 0 and < 30)
            body = body[(colon + 1)..].Trim();
        var parts = body.Split('→', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return head + "\n" + string.Join("\n", parts.Select((s, i) => $"{i + 1}) {s}"));
    }

    private Button MakeTile(string title, string what, string? linkName, string? howTo, Action onClick)
    {
        var t = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left };
        Bind(t, TextBlock.ForegroundProperty, "BrushText");
        var whatText = Soft(what, 12);
        whatText.TextAlignment = TextAlignment.Left;
        var stack = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Stretch, Children = { t, whatText } };
        if (linkName is not null && howTo is not null)
        {
            var linkText = new TextBlock
            {
                Text = T($"Как выгрузить файл из {linkName}", $"{linkName}: файл чыгаруу", $"How to export the file from {linkName}", $"{linkName}: dosya nasıl alınır", $"{linkName}: fayl chiqarish"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, TextDecorations = TextDecorations.Underline, TextAlignment = TextAlignment.Left,
            };
            Bind(linkText, TextBlock.ForegroundProperty, "BrushAccentText");
            var link = new Button
            {
                Content = linkText, Padding = new Thickness(0, 1), MinHeight = 22, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0),
                Flyout = new Flyout { Placement = PlacementMode.Bottom, Content = new TextBlock { Text = howTo, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, FontSize = 13 } },
            };
            stack.Children.Add(link);
        }
        var b = SectionCards.Secondary("");
        Avalonia.Automation.AutomationProperties.SetName(b, title);
        b.Content = stack;
        b.HorizontalAlignment = HorizontalAlignment.Stretch;
        b.VerticalAlignment = VerticalAlignment.Stretch;
        b.HorizontalContentAlignment = HorizontalAlignment.Left;
        b.VerticalContentAlignment = VerticalAlignment.Top;
        b.Padding = new Thickness(12, 6);
        b.MinHeight = 72;
        b.Margin = new Thickness(2);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void ChooseSource(int comboIndex)
    {
        _pendingSource = comboIndex;
        UpdateFolderPanel(comboIndex); // 2026-11-20: подсказка «Обычно здесь: …» для выбранной программы
        _filePageTitle.Text = comboIndex switch
        {
            0 => T("Файл Excel или CSV", "Excel же CSV файлы", "An Excel or CSV file", "Excel veya CSV dosyası", "Excel yoki CSV fayli"),
            _ when comboIndex <= ImportSourceProfiles.All.Count => T($"Файл из «{ImportSourceProfiles.All[comboIndex - 1].Name}»", $"«{ImportSourceProfiles.All[comboIndex - 1].Name}» файлы",
                $"A file from “{ImportSourceProfiles.All[comboIndex - 1].Name}”", $"«{ImportSourceProfiles.All[comboIndex - 1].Name}» dosyası", $"«{ImportSourceProfiles.All[comboIndex - 1].Name}» fayli"),
            _ => AutoPartsPriceImport.ProfileName,
        };
        GoToStep(2);
    }

    // ------------------------------------------------------------------ шаг 2: файл

    private void BuildFilePage()
    {
        _filePageTitle.Text = T("Файл Excel или CSV", "Excel же CSV файлы", "An Excel or CSV file", "Excel veya CSV dosyası", "Excel yoki CSV fayli");
        Bind(_filePageTitle, TextBlock.ForegroundProperty, "BrushText");

        var dropText = new TextBlock
        {
            Text = T("Перетащите файл сюда или нажмите «Выбрать файл…»", "Файлды бул жерге сүйрөңүз же «Файлды тандоо…» басыңыз", "Drag a file here or press “Choose file…”",
                "Dosyayı buraya sürükleyin veya «Dosya seç…»e basın", "Faylni bu yerga tortib keling yoki «Faylni tanlash…»ni bosing"),
            FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
        };
        Bind(dropText, TextBlock.ForegroundProperty, "BrushText");
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var b in new[] { _pickFile, _paste })
        {
            b.Margin = new Thickness(4, 0);
            buttons.Children.Add(b);
        }
        var formats = Soft(T("Подходят: .xlsx, .xls, .csv, .txt, .xml", "Ылайыктуу: .xlsx, .xls, .csv, .txt, .xml", "Accepted: .xlsx, .xls, .csv, .txt, .xml", "Kabul edilenler: .xlsx, .xls, .csv, .txt, .xml", "Mos keladi: .xlsx, .xls, .csv, .txt, .xml"));
        formats.HorizontalAlignment = HorizontalAlignment.Center;
        formats.TextAlignment = TextAlignment.Center;
        _dropZone.Child = new StackPanel { Spacing = 10, Children = { dropText, buttons, formats } };
        Bind(_dropZone, Border.BackgroundProperty, "BrushPanelSoft");
        Bind(_dropZone, Border.BorderBrushProperty, "BrushBorder");

        // карточка выбранного файла
        Bind(_fileTitle, TextBlock.ForegroundProperty, "BrushText");
        Bind(_badgeText, TextBlock.ForegroundProperty, "BrushAccentText");
        Bind(_badge, Border.BackgroundProperty, "BrushAccentSoft");
        _badge.Child = _badgeText;
        _changeSrc = SectionCards.Ghost(T("Изменить", "Өзгөртүү", "Change", "Değiştir", "O'zgartirish"));
        _changeSrc.MinHeight = 36;
        _changeSrc.Margin = new Thickness(0, 0, 0, 4);
        _sourceRow.IsVisible = false;
        _sourceRow.Margin = new Thickness(0, 4, 0, 0);
        _changeSrc.Click += (_, _) => _sourceRow.IsVisible = !_sourceRow.IsVisible;
        var badgeRow = new WrapPanel { Orientation = Orientation.Horizontal, Children = { _badge, _changeSrc } };
        Bind(_fileCard, Border.BackgroundProperty, "BrushPanel");
        Bind(_fileCard, Border.BorderBrushProperty, "BrushBorder");
        _fileCard.Child = new StackPanel { Spacing = 2, Children = { _fileTitle, _fileInfo, badgeRow, _sourceRow, _sourceNote } };

        // «Какой файл подходит?» и шаблоны — в одну строку ссылок
        _howLink = SectionCards.Ghost(T("Какой файл подходит?", "Кандай файл ылайыктуу?", "Which file fits?", "Hangi dosya uygun?", "Qanday fayl mos?"));
        _howLink.MinHeight = 36;
        _howLink.Flyout = new Flyout { Placement = PlacementMode.Bottom, Content = new Border { MaxWidth = 520, Child = _hintPanel } };
        _hintPanel.IsVisible = true;
        var links = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new Button[] { _howLink, _tplXlsx, _tplCsv })
        {
            b.MinHeight = 36;
            b.Margin = new Thickness(0, 0, 8, 4);
            links.Children.Add(b);
        }
        _fileInfo.Margin = new Thickness(0, 0, 0, 2);
        _fileWarn.Margin = new Thickness(0, 0, 0, 0);

        var body = new StackPanel { Spacing = 8, Children = { _filePageTitle, _dropZone, _fileWarn, _fileCard, BuildFolderPanel(), links } }; // 2026-11-20: панель «папка программы» (BulkImportWizardWindow.ProgramFolder.cs)
        _pageFile = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body };

        // нажатие на зону вне кнопок = «Выбрать файл…»
        _dropZone.PointerPressed += async (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<Button>(true) is not null)
                return;
            await GuardAsync(PickFileAsync, "выбор файла").ConfigureAwait(true);
        };
    }

    /// <summary>Сведения о выбранном файле: имя, размер, лист, число строк и столбцов, «Похоже на файл из …» (узнанная программа) и «Изменить».</summary>
    private void UpdateFileCard()
    {
        var has = _table is not null;
        _fileCard.IsVisible = has;
        _pickFile.Content = has || _fileWarn.IsVisible
            ? T("Выбрать другой файл…", "Башка файлды тандоо…", "Choose another file…", "Başka dosya seç…", "Boshqa faylni tanlash…")
            : T("Выбрать файл…", "Файлды тандоо…", "Choose file…", "Dosya seç…", "Faylni tanlash…");
        if (_table is null)
            return;
        _fileTitle.Text = _fileName;
        var sheet = _table.SheetName is { Length: > 0 } s ? T($"лист «{s}»", $"«{s}» барагы", $"sheet “{s}”", $"«{s}» sayfası", $"«{s}» varag'i") : "";
        var parts = new List<string>();
        if (FmtSize(_fileSize) is { Length: > 0 } size)
            parts.Add(size);
        if (sheet.Length > 0)
            parts.Add(sheet);
        parts.Add(T($"строк: {_table.DataRowCount}", $"саптар: {_table.DataRowCount}", $"rows: {_table.DataRowCount}", $"satır: {_table.DataRowCount}", $"qatorlar: {_table.DataRowCount}"));
        parts.Add(T($"столбцов: {_table.ColumnCount}", $"мамычалар: {_table.ColumnCount}", $"columns: {_table.ColumnCount}", $"sütun: {_table.ColumnCount}", $"ustunlar: {_table.ColumnCount}"));
        _fileInfo.Text = string.Join(" · ", parts);
        var src = _mapping.Source;
        _badgeText.Text = _apOn
            ? T("Похоже на прайс поставщика (автозапчасти)", "Жеткирүүчүнүн прайсына окшойт (унаа тетиктери)", "Looks like a supplier price list (auto parts)", "Tedarikçi fiyat listesine benziyor (oto parça)", "Yetkazib beruvchi prays-listiga o'xshaydi (avto ehtiyot qismlar)")
            : src is not null
                ? T($"Похоже на файл из «{src.Name}»", $"«{src.Name}» файлына окшойт", $"Looks like a file from “{src.Name}”", $"«{src.Name}» dosyasına benziyor", $"«{src.Name}» fayliga o'xshaydi")
                : T("Обычная таблица: столбцы подобраны по названиям", "Жөнөкөй таблица: мамычалар аталышы боюнча тандалды", "A plain table: columns matched by name", "Düz tablo: sütunlar adlarına göre eşlendi", "Oddiy jadval: ustunlar nomi bo'yicha tanlandi");
    }

    /// <summary>Выбранная плитка («МойСклад» и т. п.): столбцы ставятся по её профилю сразу после чтения файла.</summary>
    private void ApplyChosenSource()
    {
        if (_table is null || _pendingSource <= 0 || _pendingSource >= _sourceCombo.Items.Count)
            return;
        _sourceBusy = true;
        _sourceCombo.SelectedIndex = _pendingSource;
        _sourceBusy = false;
        ApplySelectedSource();
        _recheckTimer?.Stop(); // проверку сразу делает LoadResultAsync
    }

    // ------------------------------------------------------------------ шаг 3: проверьте

    private void BuildReviewPage()
    {
        _bigSummary.Bind(TextBlock.ForegroundProperty, _bigSummary.GetResourceObservable("BrushText"));
        _apSummary.FontSize = 16;
        _apSummary.IsVisible = false;
        // 2026-11-20: плашка «Похоже, это не список товаров» — над итогом; таблица ошибок под ней скрыта, пока человек не выберет действие
        Bind(_notProducts, Border.BackgroundProperty, "BrushPanelSoft");
        Bind(_notProducts, Border.BorderBrushProperty, "BrushDanger");
        Bind(_notProductsText, TextBlock.ForegroundProperty, "BrushText");
        var npClients = SectionCards.Primary(T("Это список клиентов → Клиенты и долги", "Бул кардарлардын тизмеси → Кардарлар жана карыздар", "This is a customer list → Customers and debts", "Bu müşteri listesi → Müşteriler ve borçlar", "Bu mijozlar ro'yxati → Mijozlar va qarzlar"));
        var npOther = SectionCards.Secondary(T("Выбрать другой файл", "Башка файлды тандоо", "Choose another file", "Başka dosya seç", "Boshqa faylni tanlash"));
        var npForce = SectionCards.Ghost(T("Всё равно загрузить как товары", "Баары бир товар катары жүктөө", "Load as products anyway", "Yine de ürün olarak yükle", "Baribir mahsulot sifatida yuklash"));
        foreach (var b in new[] { npClients, npOther, npForce })
        {
            b.MinHeight = 40;
            b.Margin = new Thickness(0, 0, 8, 4);
        }
        npClients.Click += (_, _) => _ = GuardAsync(async () => await ClientsImportWindow.ShowAsync(this).ConfigureAwait(true), "клиенты");
        npOther.Click += (_, _) =>
        {
            GoToStep(2);
            _ = GuardAsync(PickFileAsync, "выбор файла");
        };
        npForce.Click += (_, _) =>
        {
            _notProductsForced = true;
            UpdateNotProducts();
            ApplyFilter();
        };
        _notProducts.Child = new StackPanel { Spacing = 4, Children = { _notProductsText, new WrapPanel { Orientation = Orientation.Horizontal, Children = { npClients, npOther, npForce } } } };
        var summary = new StackPanel { Children = { _bigSummary, _apSummary } };

        // «что делать с существующими» — три радио, подпись отдельной строкой над ними
        _modeAll.Content = T("Обновить остаток и цену", "Калдыкты жана баасын жаңыртуу", "Update stock and price", "Stok ve fiyatı güncelle", "Qoldiq va narxni yangilash");
        _modePrice.Content = T("Только цену", "Баасын гана", "Price only", "Yalnızca fiyat", "Faqat narxni");
        _modeNew.Content = T("Только добавить новые", "Жаңыларын гана кошуу", "Add new items only", "Yalnızca yenileri ekle", "Faqat yangilarini qo'shish");
        ToolTip.SetTip(_modeAll, T("Количество из файла прибавляется к остатку (приход), цена обновляется.", "Файлдагы сан калдыкка кошулат (кирешe), баасы жаңырат.",
            "The quantity from the file is added to the stock (a receipt), the price is updated.", "Dosyadaki miktar stoğa eklenir (mal kabul), fiyat güncellenir.", "Fayldagi miqdor qoldiqqa qo'shiladi (kirim), narx yangilanadi."));
        ToolTip.SetTip(_modePrice, T("Остаток не меняется: у новых товаров нулевой остаток, у товаров склада обновляются цены.", "Калдык өзгөрбөйт: жаңы товарларда нөл калдык, кампадагы товарларда баалар жаңырат.",
            "The stock does not change: new products get zero stock, prices of stock products are updated.", "Stok değişmez: yeni ürünlerde sıfır stok, depo ürünlerinde fiyatlar güncellenir.", "Qoldiq o'zgarmaydi: yangi mahsulotlarda nol qoldiq, ombor mahsulotlarida narxlar yangilanadi."));
        ToolTip.SetTip(_modeNew, T("Товары, которые уже есть на складе, не трогаются; создаются только новые.", "Кампада бар товарларга тийилбейт; жаңылары гана түзүлөт.",
            "Products already in stock are left alone; only new ones are created.", "Depoda zaten olan ürünlere dokunulmaz; yalnızca yenileri oluşturulur.", "Omborda bor mahsulotlarga tegilmaydi; faqat yangilari yaratiladi."));
        foreach (var r in new[] { _modeAll, _modePrice, _modeNew })
            r.IsCheckedChanged += (_, _) =>
            {
                if (_modeSync)
                    return;
                UpdateModeNote();
                ScheduleRecheck();
            };
        // подпись и три переключателя — одна строка (на узком окне переносится); пояснение режима (_modeNote) — в боковой полосе «Дополнительно»
        var modeCaption = Caption(T("Если товар уже есть на складе:", "Товар кампада бар болсо:", "If the product is already in stock:", "Ürün depoda zaten varsa:", "Mahsulot omborda bor bo'lsa:"));
        modeCaption.Margin = new Thickness(0, 0, 14, 0);
        var radios = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { modeCaption } };
        foreach (var r in new[] { _modeAll, _modePrice, _modeNew })
        {
            r.Margin = new Thickness(0, 0, 14, 0);
            radios.Children.Add(r);
        }
        _modeNote.FontSize = 12;
        var modeBlock = radios;

        // панель инструментов: «только пропущенные», поиск, «Дополнительно»
        _onlySkipped.Content = T("Показать только пропущенные", "Өткөрүлгөндөрдү гана көрсөтүү", "Show skipped only", "Yalnızca atlananları göster", "Faqat o'tkazilganlarni ko'rsatish");
        _onlySkipped.IsCheckedChanged += (_, _) =>
        {
            _filter = _onlySkipped.IsChecked == true ? Filter.Skipped : Filter.All;
            _errorGroup = null;
            ApplyFilter();
        };
        _search.MinHeight = 36;
        _search.Width = 220;
        _search.HorizontalAlignment = HorizontalAlignment.Left;
        _mapToggle.MinHeight = 32;
        _onlySkipped.Margin = new Thickness(12, 0, 8, 0);
        // верхняя строка: итог (переносится) | «только пропущенные» | «Дополнительно» — одна строка вместо трёх, таблице больше места
        var topRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        Grid.SetRow(_notProducts, 1); // 2026-11-20: плашка «не список товаров» — на всю ширину под итогом
        Grid.SetColumnSpan(_notProducts, 3);
        topRow.Children.Add(_notProducts);
        summary.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_onlySkipped, 1);
        Grid.SetColumn(_mapToggle, 2);
        topRow.Children.Add(summary);
        topRow.Children.Add(_onlySkipped);
        topRow.Children.Add(_mapToggle);
        _onlySkipped.VerticalAlignment = VerticalAlignment.Center;
        _mapToggle.VerticalAlignment = VerticalAlignment.Center;

        // таблица (или список пропущенных строк прайса автозапчастей)
        _grid.MinHeight = 120;
        Bind(_apSkippedBox, Border.BorderBrushProperty, "BrushBorder");
        _apSkippedBox.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _apSkippedRows };
        var table = new Grid { Children = { _grid, _apSkippedBox } };


        // боковая полоса «Дополнительно»: столбцы и параметры, фильтры, действия над отмеченными
        foreach (var c in new ContentControl[] { _dupSum, _dupFirst, _defUnitCheck, _mergeNameCheck, _altExistingCheck, _altSkipBadCheck, _internalCodeCheck })
            if (c.Content is string s)
                c.Content = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap }; // 2026-11-20: узкая полоса — подписи переносятся, а не обрезаются
        _mapPanel.IsVisible = true;
        _opsPanel.IsVisible = false;
        _tiles.IsVisible = true;
        var searchTitle = Caption(T("Поиск по таблице", "Таблицадан издөө", "Search the table", "Tabloda ara", "Jadvaldan qidirish"));
        var filtersTitle = Caption(T("Показать строки", "Саптарды көрсөтүү", "Show rows", "Satırları göster", "Qatorlarni ko'rsatish"));
        var opsTitle = Caption(T("Действия над отмеченными", "Белгиленгендер үчүн аракеттер", "Actions on ticked rows", "İşaretlilerde işlemler", "Belgilanganlar ustida amallar"));
        _advCol.Content = new StackPanel { Spacing = 8, Children = { _modeNote, _mapPanel, searchTitle, _search, filtersTitle, _tiles, _errGroups, opsTitle, _opsPanel } };

        // итог и переключатель — на всю ширину, таблица и боковая полоса «Дополнительно» — под ними: открытие полосы высоту таблицы не меняет
        var page = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        Grid.SetRow(topRow, 0);
        Grid.SetColumnSpan(topRow, 2);
        Grid.SetRow(modeBlock, 1);
        Grid.SetColumnSpan(modeBlock, 2);
        Grid.SetRow(table, 2);
        Grid.SetRow(_advCol, 2);
        Grid.SetColumn(_advCol, 1);
        page.Children.Add(topRow);
        page.Children.Add(modeBlock);
        page.Children.Add(table);
        page.Children.Add(_advCol);
        _pageReview = page;
    }

    /// <summary>«Дополнительно»: боковая полоса справа (таблице высоту не отнимает); в ней же столбцы таблицы, которые обычно скрыты.</summary>
    private void SetAdvanced(bool open)
    {
        _advCol.IsVisible = open;
        // 0 — флажок строки, 1 — №, 4 — единица, 6 — закупка, 7 — наценка: нужны для действий над отмеченными
        foreach (var i in new[] { 0, 1, 7, 9, 10 }) // 2026-11-20: 4–6 — категория, бренд, доп. коды (видимы сами); 7 — единица, 9 — закупка, 10 — наценка
            if (i < _grid.Columns.Count)
                _grid.Columns[i].IsVisible = open;
        _opsPanel.IsVisible = open && _rows.Count > 0;
        UpdateToggles();
    }

    /// <summary>Итог крупно («Найдено …: создать, обновить, пропустить»). У прайса автозапчастей — его собственный итог («Будет создано …»).</summary>
    private void UpdateBigSummary()
    {
        var plan = _plan;
        _apSummary.IsVisible = _apOn && plan is not null && plan.Rows.Count > 0;
        if (plan is null || plan.Rows.Count == 0)
        {
            _bigSummary.Text = "";
            _bigSummary.IsVisible = false;
            return;
        }
        var n = plan.Rows.Count;
        var created = plan.NewCount;
        var updated = Math.Max(0, plan.ReceiptCount - plan.UnchangedCount);
        var skipped = plan.UnchangedCount + plan.ErrorCount + plan.MergedCount;
        _bigSummary.Text = T($"Найдено товаров: {n} — создать {created}, обновить {updated}, пропустить {skipped}",
            $"Табылган товарлар: {n} — түзүү {created}, жаңыртуу {updated}, өткөрүү {skipped}",
            $"Products found: {n} — create {created}, update {updated}, skip {skipped}",
            $"Bulunan ürün: {n} — oluştur {created}, güncelle {updated}, atla {skipped}",
            $"Topilgan mahsulotlar: {n} — yaratish {created}, yangilash {updated}, o'tkazish {skipped}");
        // 2026-11-20: «доп. кодов: N (упаковок: K)» и сколько плохих кодов пропущено
        if (plan.AltCodeCount > 0)
            _bigSummary.Text += T($" · доп. кодов: {plan.AltCodeCount}" + (plan.AltPackCount > 0 ? $" (упаковок: {plan.AltPackCount})" : ""),
                $" · кошумча коддор: {plan.AltCodeCount}" + (plan.AltPackCount > 0 ? $" (куту: {plan.AltPackCount})" : ""),
                $" · extra codes: {plan.AltCodeCount}" + (plan.AltPackCount > 0 ? $" (packs: {plan.AltPackCount})" : ""),
                $" · ek kod: {plan.AltCodeCount}" + (plan.AltPackCount > 0 ? $" (paket: {plan.AltPackCount})" : ""),
                $" · qo'shimcha kod: {plan.AltCodeCount}" + (plan.AltPackCount > 0 ? $" (qadoq: {plan.AltPackCount})" : ""));
        if (plan.AltSkippedCount > 0)
            _bigSummary.Text += T($", пропущено доп. кодов: {plan.AltSkippedCount}", $", өткөрүлгөн кошумча коддор: {plan.AltSkippedCount}", $", extra codes skipped: {plan.AltSkippedCount}",
                $", atlanan ek kod: {plan.AltSkippedCount}", $", o'tkazilgan qo'shimcha kodlar: {plan.AltSkippedCount}");
        // 2026-11-20: категории, которых нет в каталоге (создадутся при загрузке), строки-группы (CT MAX), служебные коды с внутренним штрихкодом
        if (plan.NewCategoryCount > 0)
            _bigSummary.Text += T($" · будет создано категорий: {plan.NewCategoryCount}", $" · түзүлө турган категориялар: {plan.NewCategoryCount}", $" · categories to be created: {plan.NewCategoryCount}",
                $" · oluşturulacak kategori: {plan.NewCategoryCount}", $" · yaratiladigan kategoriyalar: {plan.NewCategoryCount}");
        if (plan.GroupRowsSkipped > 0)
            _bigSummary.Text += T($" · групп (не товаров) пропущено: {plan.GroupRowsSkipped}", $" · топтор (товар эмес) өткөрүлдү: {plan.GroupRowsSkipped}", $" · groups (not products) skipped: {plan.GroupRowsSkipped}",
                $" · atlanan gruplar (ürün değil): {plan.GroupRowsSkipped}", $" · o'tkazilgan guruhlar (mahsulot emas): {plan.GroupRowsSkipped}");
        if (plan.InternalCodeCount > 0)
            _bigSummary.Text += T($" · внутренних кодов NurCRM: {plan.InternalCodeCount}", $" · NurCRM ички коддору: {plan.InternalCodeCount}", $" · NurCRM internal codes: {plan.InternalCodeCount}",
                $" · NurCRM iç kodları: {plan.InternalCodeCount}", $" · NurCRM ichki kodlari: {plan.InternalCodeCount}");
        _internalCodeCheck.IsVisible = _mapping.Source?.ServiceCodes == true; // 2026-11-20: переключатель служебных кодов — только у CT MAX
        _bigSummary.FontSize = (_bigSummary.Text?.Length ?? 0) > 75 ? 13 : 16; // 2026-11-20: длинный итог (доп. коды, категории) — мельче, чтобы таблица не теряла высоту на 1024×600
        _bigSummary.IsVisible = !_apOn && !_notProductsBlock;
    }

    /// <summary>2026-11-20: решить, похож ли файл на список товаров; нет — плашка вместо таблицы (кнопка «Всё равно загрузить как товары» снимает блок до следующего файла).</summary>
    private void UpdateNotProducts()
    {
        _notProductsBlock = !_apOn && !_notProductsForced && _table is not null && _plan is { Problem: null, Rows.Count: > 0 }
            && BulkImportPlan.LooksLikeNotProductList(_table, _mapping, _plan);
        _notProducts.IsVisible = _notProductsBlock;
        if (_notProductsBlock && _plan is { } p)
            _notProductsText.Text = T($"Похоже, это не список товаров: в столбце штрихкода — почта, адреса или текст, а не коды товаров (строк с ошибками: {p.ErrorCount} из {p.Rows.Count}). Что сделать?",
                $"Бул товарлардын тизмеси эмес окшойт: штрих-код мамычасында почта, дарек же текст, товар коддору эмес (катасы бар саптар: {p.ErrorCount} / {p.Rows.Count}). Эмне кылабыз?",
                $"This does not look like a product list: the barcode column holds emails, addresses or text, not product codes (rows with errors: {p.ErrorCount} of {p.Rows.Count}). What to do?",
                $"Bu bir ürün listesine benzemiyor: barkod sütununda e-posta, adres veya metin var, ürün kodu değil (hatalı satır: {p.ErrorCount} / {p.Rows.Count}). Ne yapalım?",
                $"Bu mahsulotlar ro'yxatiga o'xshamaydi: shtrix-kod ustunida pochta, manzil yoki matn bor, mahsulot kodlari emas (xatoli qatorlar: {p.ErrorCount} / {p.Rows.Count}). Nima qilamiz?");
        UpdateBigSummary();
        UpdateNav();
    }

    /// <summary>У прайса автозапчастей «пропущенные» — это ещё и группы с строки без цены, которых нет в таблице плана: вместо таблицы показываем их список.</summary>
    private void UpdateSkippedView()
    {
        var apList = _apOn && _filter == Filter.Skipped;
        _apSkippedBox.IsVisible = apList;
        _grid.IsVisible = !apList && _rows.Count > 0 && !_notProductsBlock;
    }

    // ------------------------------------------------------------------ шаг 4: загрузка

    private void BuildLoadPage()
    {
        var title = new TextBlock { Text = T("Загрузка товаров", "Товарларды жүктөө", "Loading products", "Ürünler yükleniyor", "Mahsulotlarni yuklash"), FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        Bind(title, TextBlock.ForegroundProperty, "BrushText");
        _summary.FontSize = 16;
        _summary.Margin = new Thickness(0, 4, 0, 0);
        _pageLoad = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Spacing = 10, Children = { title, _summary, _nextPanel } },
        };
    }

    // ------------------------------------------------------------------ переходы, кнопки, клавиши

    private void GoToStep(int step)
    {
        _step = Math.Clamp(step, 1, 4);
        _pageSource.IsVisible = _step == 1;
        _pageFile.IsVisible = _step == 2;
        _pageReview.IsVisible = _step == 3;
        _pageLoad.IsVisible = _step == 4;
        _status.FontSize = _step == 4 ? 15 : 13;
        _bar.Height = _step == 4 ? 12 : 8;
        UpdateStepBar();
        UpdateNav();
        Dispatcher.UIThread.Post(Refit, DispatcherPriority.Loaded);
    }

    private void UpdateNav()
    {
        if (_nextBtn is null)
            return; // зовётся из UpdateTiles раньше, чем собрана нижняя панель
        var ok = _plan is { Problem: null } && _rows.Count > 0;
        _backBtn.IsVisible = _step is 2 or 3 || (_step == 4 && !_started);
        _backBtn.Content = _step == 4 ? T("К проверке", "Текшерүүгө", "Back to the check", "Kontrole dön", "Tekshiruvga") : T("Назад", "Артка", "Back", "Geri", "Orqaga");
        _nextBtn.IsVisible = _step == 2;
        _nextBtn.IsEnabled = ok && !_building;
        _loadBtn.IsVisible = _step == 3;
        _warehouseBtn.IsVisible = _step == 4;
        _errorsBtn.IsVisible = _step >= 3 && _errorsBtn.IsEnabled;
    }

    private async Task BackAsync()
    {
        if (_step == 4)
        {
            if (_started)
                return;
            GoToStep(3);
            await CheckAsync().ConfigureAwait(true); // загруженное станет «без изменений», оставшееся можно дозагрузить
            return;
        }
        GoToStep(_step - 1);
    }

    private async Task RequestCloseAsync()
    {
        if (_started && _step == 4)
        {
            var cards = _pauseBtn.IsVisible; // загрузка карточек идёт в этом окне; приход ведёт фоновая очередь
            var ok = await PosConfirmDialog.ShowModalAsync(this,
                T("Загрузка идёт", "Жүктөө жүрүп жатат", "Loading is in progress", "Yükleme sürüyor", "Yuklash davom etmoqda"),
                cards
                    ? T("Если закрыть окно сейчас, загрузка остановится (уже записанное останется). Закрыть?", "Терезени азыр жапсаңыз, жүктөө токтойт (жазылганы калат). Жабабызбы?",
                        "If you close the window now, loading will stop (what is already written stays). Close?", "Pencereyi şimdi kapatırsanız yükleme durur (yazılanlar kalır). Kapatılsın mı?", "Oynani hozir yopsangiz, yuklash to'xtaydi (yozilganlari qoladi). Yopilsinmi?")
                    : T("Загрузка идёт в фоне и продолжится после закрытия окна. Закрыть?", "Жүктөө фондо жүрүп жатат, терезе жабылгандан кийин да улантылат. Жабабызбы?",
                        "Loading runs in the background and continues after the window is closed. Close?", "Yükleme arka planda sürüyor ve pencere kapandıktan sonra da devam edecek. Kapatılsın mı?", "Yuklash fonda ketmoqda va oyna yopilgandan keyin ham davom etadi. Yopilsinmi?"),
                T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), T("Остаться", "Калуу", "Stay", "Kal", "Qolish")).ConfigureAwait(true);
            if (!ok)
                return;
        }
        Close();
    }

    private async void OnWizardKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc: шаги 2–3 — на шаг назад, шаг 1 и 4 — закрыть (если загрузка идёт — с вопросом)
            e.Handled = true;
            if (_step is 2 or 3)
                GoToStep(_step - 1);
            else
                await GuardAsync(RequestCloseAsync, "закрытие").ConfigureAwait(true);
            return;
        }
        if (e.Key != Key.Enter || e.Source is TextBox or Button or ComboBox or ListBoxItem)
            return;
        if (e.Source is Visual v && v.FindAncestorOfType<DataGrid>() is not null)
            return;
        if (_step == 2 && _nextBtn.IsEnabled)
        {
            e.Handled = true;
            GoToStep(3);
        }
        else if (_step == 3 && _loadBtn.IsEnabled)
        {
            e.Handled = true;
            _loadBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    /// <summary>«Открыть склад»: открытое окно склада выводится вперёд (или открывается новое), мастер закрывается.</summary>
    private void OpenWarehouseAndClose()
    {
        try
        {
            var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
            var open = desktop?.Windows.OfType<WarehouseWindow>().FirstOrDefault(w => w.IsVisible);
            if (open is not null)
                open.Activate();
            else
            {
                var window = App.GetRequiredService<WarehouseWindow>();
                var owner = desktop?.MainWindow is { IsVisible: true } main ? main : null;
                if (owner is not null)
                    window.Show(owner);
                else
                    window.Show();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Массовая загрузка: склад не открылся: {ex.Message}", "WARNING");
        }
        Close();
    }
}
