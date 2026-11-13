using NurMarketKassa.Core.Application;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-11-06: В15г — часть класса PurchasesWindow (Render): перенесено из PurchasesWindow.Receive.cs без изменений логики.
public partial class PurchasesWindow
{
    // ------------------------------------------------------------------ таблица

    // 2026-10-24, ТЗ «Склад» (Б2): колонки как в Склад → Приёмка — Товар · Остаток · Количество · Закупка · Наценка % · Продажа · Сумма · ✕.
    // 2026-11-07 (ТЗ «Порядковая нумерация товаров в накладной»): слева узкая колонка «№» (44 px, на узком окне 40) — порядок добавления, только для чтения.
    // 2026-11-10 (владелец: «Наименование должно быть длинным!», как колонка «Товар» в вебе): название — главная колонка (было 2* / 2,3*), числа уже.
    private string ReceiveColumns => _receive?.Narrow == true ? "40,2.4*,0,1*,1.15*,0.8*,1.15*,0.9*,Auto" : "44,3.2*,0.65*,0.9*,1.05*,0.8*,1.05*,1*,Auto";

    private void ReceiveRefresh()
    {
        if (_receive is not { } s)
            return;
        ReceiveTiles();
        ReceiveSteps();
        ReceiveFooter();
    }

    /// <summary>2026-11-12 (аудит Д-9): сумма в ячейке и в итоге не рвётся между числом и «сом» («4 800,00» / «сом» на разных строках) — пробелы внутри суммы неразрывные.</summary>
    private static string NoWrap(string money) => money.Replace(' ', ' ');

    private void ReceiveRender()
    {
        if (_receive is not { } s)
            return;
        _purchasePackBar?.AttachScanBox(s.Scan); // 2026-11-12 (Л-9): метка «УПАКОВКАМИ» в поле скана, пока способ приёма не «В штуках»
        s.Head.Children.Clear();
        s.Head.ColumnDefinitions = new ColumnDefinitions(ReceiveColumns);
        var heads = new[]
        {
            "№",
            // 2026-10-29, жалоба «сделай понятно»: «На складе», «Принимаем», «Сумма закупки» — как в Склад → Приёмка.
            T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("На складе", "Кампада", "In stock", "Depoda", "Omborda"), T("Принимаем", "Кабыл алабыз", "Receiving", "Alınıyor", "Qabul qilinadi"),
            T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %"),
            T("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi"), T("Сумма закупки", "Сатып алуу суммасы", "Purchase total", "Alış tutarı", "Xarid summasi"), "",
        };
        for (var i = 0; i < heads.Length; i++)
        {
            var t = new TextBlock { Text = heads[i], FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = i == 0 ? TextAlignment.Center : i is >= 2 and <= 7 ? TextAlignment.Right : TextAlignment.Left, IsVisible = !(s.Narrow && i == 2), MinWidth = i == 8 ? 44 : 0 };
            if (i == 0)
                t.Margin = new Thickness(0, 0, 4, 0);
            Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(t, i);
            s.Head.Children.Add(t);
        }
        ReceivingListOrder.Renumber(s.Lines); // 2026-11-07: «№» без пропусков после удаления/очистки; новые строки (прием из заказа, возврат из очереди) — следующими
        s.Rows.Children.Clear();
        s.RowMap.Clear();
        if (s.Lines.Count == 0)
        {
            s.Rows.Children.Add(SectionCards.Empty(this, ReceiveHintText() + " " + T("Или нажмите «Товары поставщика» — найдите нужный товар в списке, а не прокручивайте сотни строк.",
                "Же «Жеткирүүчүнүн товарларын» басыңыз — керектүү товарды тизмеден табыңыз, жүздөгөн саптарды сыдырбаңыз.",
                "Or press “Supplier's products” — find the product in the list instead of scrolling hundreds of lines.",
                "Veya «Tedarikçi ürünleri»ne basın — yüzlerce satırı kaydırmak yerine ürünü listeden bulun.",
                "Yoki «Yetkazib beruvchi mahsulotlari»ni bosing — yuzlab qatorni aylantirmasdan mahsulotni ro'yxatdan toping.")));
        }
        foreach (var line in s.Lines)
            s.Rows.Children.Add(ReceiveRow(line));
        ReceiveRefresh();
    }

    private static string ReceiveNum(double v) => v > 0 ? v.ToString("0.#########", Ru) : ""; // 2026-10-31: без округления показа (было 0.###)

    /// <summary>2026-11-11 (БП-4): кисть темы с учётом варианта окна (светлая/тёмная); нет ресурса — null.</summary>
    private IBrush? ThemeBrush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    /// <summary>2026-10-28: цена в ячейке «100,60» (целая — «100»), не «100,6»; пусто для нуля.</summary>
    private static string ReceiveMoney(double v) => v > 0 ? ReceivingLineVm.FormatPrice(v).Replace('.', ',') : "";

    private static string ReceiveMarkup(ReceivingLineVm line) => line.MarkupPercent is { } m ? m.ToString("0.##", Ru) : "";

    private static string RoundedQuantityHint => ReceivingLineVm.QuantityPrecisionText; // 2026-10-31: «Не округляй» — теперь пояснение без округления

    /// <summary>Читает число из ячейки (запятая или точка). Пустая ячейка — 0; непонятное число — false (строка остаётся как была).</summary>
    private static bool ReceiveReadCell(TextBox box, out double value)
    {
        value = 0;
        var t = (box.Text ?? "").Trim().Replace(" ", "").Replace(',', '.');
        if (t.Length == 0)
            return true;
        if (ReceivingLineVm.LooksLikeBarcode(t))
            return false; // 2026-11-11: штрихкод (одни цифры, от 8) количеством не становится — строка остаётся как была
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0 || v >= 1e9)
            return false;
        value = v; // 2026-10-31 («Не округляй»): не округляем; больше 3 знаков — красное пояснение и блок проведения (Validate)
        return true;
    }

    /// <summary>Ячейка-поле таблицы приёма (40 px, число справа). Tag «rcv-cell» — по нему сканер в ячейке узнаёт свои поля.</summary>
    private TextBox ReceiveCellBox(string text, string name)
    {
        var box = new TextBox
        {
            Text = text, Watermark = "0", Height = 40, MinHeight = 40, FontSize = 14, Margin = new Thickness(0, 0, 10, 0), Tag = "rcv-cell",
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add("num");
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        box.GotFocus += (_, _) => box.SelectAll();
        return box;
    }

    /// <summary>2026-10-30: оборачивает элемент строки приёма в полосу высотой с поле (40 px) у верха строки — он по центру линии полей, подписи под полями его не двигают.</summary>
    private static Grid OnFieldLine(Control c)
    {
        c.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { MinHeight = 40, VerticalAlignment = VerticalAlignment.Top, Children = { c } };
    }

    /// <summary>2026-10-31 (владелец: «в приёмке весовые добавляются???»): у НОВОГО товара в «Приёме товара» не было выбора единицы — он создавался «шт» и
    /// нельзя было принять килограммы/литры. Теперь под названием — кнопки единиц (шт, кг, л, м, уп; список без выпадающего окна — в окнах кассы
    /// Popup не открывается). «кг» делает товар весовым (ReceivingLineVm.IsWeight), количество можно вводить дробным (до 3 знаков).</summary>
    private Control ReceiveUnitChips(ReceivingLineVm line)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var chips = new List<(string Unit, Border Box, TextBlock Label)>();
        var units = new List<string> { "шт", "кг", "л", "м", "уп" };
        var current = UnitCatalog.NormalizeOrKeep(line.Unit);
        if (!units.Contains(current))
            units.Add(current); // своя единица (из файла/накладной) остаётся в списке выбранной
        void Paint()
        {
            var now = UnitCatalog.NormalizeOrKeep(line.Unit);
            foreach (var (unit, box, label) in chips)
            {
                var on = string.Equals(unit, now, StringComparison.Ordinal);
                Use(box, Border.BackgroundProperty, on ? "BrushAccentSoft" : "BrushPanelSoft");
                Use(label, TextBlock.ForegroundProperty, on ? "BrushAccent" : "BrushTextSoft");
                label.FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }
        foreach (var unit in units)
        {
            var label = new TextBlock { Text = UnitCatalog.Display(unit), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
            var box = new Border { Child = label, MinHeight = ReceiveTouchMin(32), MinWidth = ReceiveTouchMin(40), // 2026-11-11 (БП-9): в Touch-теме ≥ 44
                                    Padding = new Thickness(10, 0), Margin = new Thickness(0, 0, 6, 4), CornerRadius = new CornerRadius(8), Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(box, unit == "кг"
                ? T("Килограммы: товар будет весовым, количество можно дробным (например 2,5).", "Килограмм: товар салмактуу болот, саны бөлчөк болушу мүмкүн (мисалы 2,5).",
                    "Kilograms: the product will be weighed, the quantity may be fractional (e.g. 2.5).", "Kilogram: ürün tartılı olur, miktar ondalık olabilir (örn. 2,5).", "Kilogramm: mahsulot vaznli bo'ladi, miqdor kasr bo'lishi mumkin (masalan 2,5).")
                : T("Единица измерения нового товара.", "Жаңы товардын өлчөө бирдиги.", "Unit of the new product.", "Yeni ürünün birimi.", "Yangi mahsulotning o'lchov birligi."));
            Avalonia.Automation.AutomationProperties.SetName(box, T("Единица", "Бирдик", "Unit", "Birim", "Birlik") + " " + UnitCatalog.Display(unit));
            box.PointerPressed += (_, _) =>
            {
                line.Unit = unit;
                Paint();
                ReceiveRefresh();
            };
            chips.Add((unit, box, label));
            panel.Children.Add(box);
        }
        Paint();
        return panel;
    }

    private Control ReceiveRow(ReceivingLineVm line)
    {
        var s = _receive!;
        var parts = new RowParts();
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(ReceiveColumns) };
        // Товар: название (у нового — поле), штрихкод/код и пометка «новый».
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        // 2026-11-09 (владелец: «добавить возможность редактировать наименование товара»): название правится у ЛЮБОЙ строки. У товара склада правка = переименование карточки
        // при проведении (PurchaseReceivingService.AddNameAndUnit → PATCH name, как «Склад → Приёмка»); под полем — «Было: …». Пустое название нельзя: прежнее возвращается.
        // 2026-11-10 (владелец: «Наименование должно быть длинным!»): длинное название переносится на 2 строки (не режется), полностью — в подсказке.
        var nameBox = new TextBox { Text = line.ProductName, Watermark = line.IsNew ? T("Название нового товара", "Жаңы товардын аты", "New product name", "Yeni ürün adı", "Yangi mahsulot nomi") : T("Название товара", "Товардын аты", "Product name", "Ürün adı", "Mahsulot nomi"), MinHeight = 40, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, Tag = "rcv-cell",
            TextWrapping = TextWrapping.Wrap, MaxLines = 2, AcceptsReturn = false };
        ToolTip.SetTip(nameBox, line.ProductName.Length > 0 ? line.ProductName : null);
        Avalonia.Automation.AutomationProperties.SetName(nameBox, nameBox.Watermark);
        nameBox.TextChanged += (_, _) =>
        {
            if (parts.Syncing)
                return;
            line.ProductName = nameBox.Text ?? "";
        };
        nameBox.LostFocus += (_, _) =>
        {
            if (line.CommitName())
            {
                nameBox.Text = line.ProductName;
                ReceiveMessage(T("Название не может быть пустым — вернули прежнее.", "Аты бош болбошу керек — мурункусу кайтарылды.", "The name cannot be empty — the previous one was restored.", "Ad boş olamaz — önceki geri yüklendi.", "Nom bo'sh bo'lmasligi kerak — avvalgisi qaytarildi."), warning: true);
            }
        };
        parts.Name = nameBox;
        who.Children.Add(nameBox);
        var renamedNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(renamedNote, TextBlock.ForegroundProperty, "BrushWarning");
        who.Children.Add(renamedNote);
        if (line.IsNew)
            who.Children.Add(ReceiveUnitChips(line)); // 2026-10-31: единица нового товара (кг — весовой)
        string MetaText() => (line.Barcode.Length > 0 ? line.Barcode : "—") + (line.IsNew ? " · " + T("новый товар", "жаңы товар", "new product", "yeni ürün", "yangi mahsulot") : "")
                             + (s.Narrow && !line.IsNew ? " · " + T("ост. ", "калд. ", "stock ", "stok ", "qold. ") + Q(line.StockBefore) : "");
        // 2026-11-11 (аудит «Закупки», БП-11): красным «новый товар» — только у действительно неизвестного («Неизвестно»); «База NurCRM» и «Из интернета» — нейтрально.
        string MetaBrush() => line.Source == ReceivingSource.Unknown ? "BrushDanger" : "BrushTextSoft";
        var meta = Cell(MetaText(), MetaBrush(), 12);
        meta.Margin = default;
        meta.VerticalAlignment = VerticalAlignment.Center;
        // 2026-11-09 (владелец: «не найден — только предложить найти в интернете, по нажатию»): у «Неизвестно» рядом со штрихкодом — кнопка; поиск идёт только по её нажатию.
        var netButton = new Button { Content = T("Найти в интернете", "Интернеттен издөө", "Find on the internet", "İnternette bul", "Internetdan topish"), MinHeight = ReceiveTouchMin(32), Padding = new Thickness(10, 0), Margin = new Thickness(8, 0, 0, 0), FontSize = 12, IsVisible = false, Tag = "rcv-netlink", Focusable = true }; // 2026-11-11 (БП-8/9)
        ToolTip.SetTip(netButton, T("Найти название по штрихкоду в открытых базах интернета. Выполняется только по нажатию; найденное нужно проверить.", "Штрихкод боюнча атын интернеттин ачык базаларынан издөө. Басканда гана аткарылат; табылганды текшериңиз.",
            "Look up the name by barcode in open internet databases. Runs only when you press it; check what is found.", "Adı barkoda göre internetin açık bazalarında bul. Yalnızca basınca çalışır; bulunanı kontrol edin.", "Nomini shtrix-kod bo'yicha internetning ochiq bazalaridan topish. Faqat bosganda ishlaydi; topilganini tekshiring."));
        Avalonia.Automation.AutomationProperties.SetName(netButton, T("Найти в интернете", "Интернеттен издөө", "Find on the internet", "İnternette bul", "Internetdan topish") + " " + line.Barcode);
        netButton.Click += async (_, _) =>
        {
            try
            {
                var result = await ReceivingInternetLookup.ApplyAsync(line).ConfigureAwait(true);
                var text = ReceivingInternetLookup.MessageFor(result);
                if (text.Length > 0 && _receive == s)
                    ReceiveMessage(text, warning: result.Outcome is ReceivingInternetLookup.Outcome.NotFound or ReceivingInternetLookup.Outcome.Failed);
                if (_receive == s)
                    ReceiveRefresh();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Приём товара: «Найти в интернете» — {ex.Message}", "WARNING");
            }
        };
        // 2026-11-10 (владелец: «Генерация штрихкода как в вебе!»): у нового товара без штрихкода — «Сгенерировать штрихкод» (тот же генератор, что в окне товара; без дублей среди каталога и строк приёма).
        var genButton = new Button { Content = T("Сгенерировать штрихкод", "Штрихкод түзүү", "Generate barcode", "Barkod oluştur", "Shtrix-kod yaratish"), MinHeight = ReceiveTouchMin(32), Padding = new Thickness(10, 0), Margin = new Thickness(8, 0, 0, 0), FontSize = 12, IsVisible = line.CanGenerateBarcode, Tag = "rcv-genbar", Focusable = true }; // 2026-11-11 (БП-8/9)
        ToolTip.SetTip(genButton, T("Свой штрихкод EAN-13 для нового товара, как в окне товара и на сайте. Не повторяет товары каталога и строки этого приёма.", "Жаңы товар үчүн өз EAN-13 штрихкоду, товар терезесиндегидей жана сайттагыдай. Каталогдогу товарларды жана бул кабыл алуунун саптарын кайталабайт.",
            "An own EAN-13 barcode for the new product, as in the product window and on the website. Does not repeat catalog products or lines of this receipt.", "Yeni ürün için kendi EAN-13 barkodu, ürün penceresindeki ve sitedeki gibi. Katalog ürünlerini ve bu girişin satırlarını tekrarlamaz.",
            "Yangi mahsulot uchun o'z EAN-13 shtrix-kodi, mahsulot oynasidagi va saytdagidek. Katalog mahsulotlari va bu qabul qatorlarini takrorlamaydi."));
        Avalonia.Automation.AutomationProperties.SetName(genButton, T("Сгенерировать штрихкод", "Штрихкод түзүү", "Generate barcode", "Barkod oluştur", "Shtrix-kod yaratish") + " " + line.ProductName);
        genButton.Click += (_, _) =>
        {
            if (!BarcodeGenerator.GenerateForReceivingLine(line, s.Lines))
                return;
            PosLogger.Log($"Приём товара: новому товару «{line.ProductName}» сгенерирован штрихкод {line.Barcode}.", "STOCK");
            ReceiveMessage(T($"Штрихкод {line.Barcode} сгенерирован.", $"{line.Barcode} штрихкоду түзүлдү.", $"Barcode {line.Barcode} generated.", $"{line.Barcode} barkodu oluşturuldu.", $"{line.Barcode} shtrix-kodi yaratildi."));
        };
        var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Children = { meta, netButton, genButton } };
        who.Children.Add(metaRow);
        // 2026-11-09 (владелец: «указывать вначале как на старом — база / база NurCRM / неизвестно / из интернета, значками, с подсказкой»): значок источника — первый в строке, перед названием.
        var badge = new ReceivingSourceBadge { Source = line.Source, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 8, 0) };
        var whoRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(who, 1);
        whoRow.Children.Add(badge);
        whoRow.Children.Add(who);
        who.Margin = default;
        whoRow.Margin = new Thickness(0, 0, 10, 0);
        var whoLine = OnFieldLine(whoRow);
        Grid.SetColumn(whoLine, 1);
        g.Children.Add(whoLine);

        // 2026-11-07: колонка «№» — порядок добавления (ReceivingLineVm.LineNo); только текст, фокус не получает (Tab/Enter идут по полям как раньше).
        var numText = Cell("", "BrushTextSoft", 13.5);
        numText.TextAlignment = TextAlignment.Center;
        numText.HorizontalAlignment = HorizontalAlignment.Center;
        numText.Margin = new Thickness(0, 0, 4, 0);
        var numLine = OnFieldLine(numText);
        Grid.SetColumn(numLine, 0);
        g.Children.Add(numLine);

        var stock = Cell(line.IsNew ? "—" : Q(line.StockBefore) + " " + line.Unit, "BrushTextSoft", 13.5);
        stock.TextAlignment = TextAlignment.Right;
        stock.IsVisible = !s.Narrow;
        var stockLine = OnFieldLine(stock);
        stockLine.IsVisible = !s.Narrow;
        Grid.SetColumn(stockLine, 2);
        g.Children.Add(stockLine);

        string Lbl(string ru, string ky, string en, string tr, string uz) => T(ru, ky, en, tr, uz) + " " + line.ProductName;
        var qty = ReceiveCellBox(ReceiveNum(line.Quantity), Lbl("Количество", "Саны", "Quantity", "Miktar", "Miqdor"));
        var purchase = ReceiveCellBox(ReceiveMoney(line.PurchasePrice), Lbl("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"));
        var markup = ReceiveCellBox(ReceiveMarkup(line), Lbl("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %"));
        var sale = ReceiveCellBox(ReceiveMoney(line.SalePrice), Lbl("Цена продажи", "Сатуу баасы", "Selling price", "Satış fiyatı", "Sotuv narxi"));
        // 2026-11-07 («Принять упаковками»): под «Принимаем» — ссылка «упаковками»; в режиме упаковок поле «Принимаем» — число упаковок, под ним «шт в упак.» и «= 120 шт»,
        // а «Цена закупки» — цена ЗА УПАКОВКУ (под ней «за упак. · 33,3333 за шт»). В строке, проведении, очереди и накладной всё равно штуки и цена за штуку (ReceivingLineVm.PackMode).
        // Товар с поштучной продажей из упаковки (PieceOption) склад ведёт упаковками — режим ему не предлагаем (умножение раздуло бы остаток), только подсказка.
        var cardPack = ReceivingPacks.PieceOptionSize(line);
        var pieces = ReceiveCellBox(ReceiveNum(line.PiecesPerPack), Lbl("Штук в упаковке", "Таңгактагы даана", "Pieces per pack", "Koli başına adet", "Qadoqdagi dona"));
        pieces.Height = 36;
        pieces.MinHeight = 36;
        pieces.FontSize = 13;
        pieces.Margin = new Thickness(0, 4, 10, 0);
        pieces.Watermark = T("шт в упак.", "даана/таңгак", "pcs/pack", "adet/koli", "dona/qadoq");
        pieces.IsVisible = false;
        // 2026-11-10 (эталон веба «Поштучная продажа из упаковки»: поле «Упаковка», подсказка «Пачка»): название упаковки для карточки — только у «упаковки с поштучной продажей».
        var packNameBox = new TextBox { Height = 36, MinHeight = 36, FontSize = 13, Margin = new Thickness(0, 4, 10, 0), IsVisible = false, Tag = "rcv-cell",
            Watermark = T("Пачка", "Пачка", "Pack", "Paket", "Pachka"), Text = line.PackName };
        Avalonia.Automation.AutomationProperties.SetName(packNameBox, Lbl("Название упаковки", "Таңгактын аты", "Pack name", "Koli adı", "Qadoq nomi"));
        ToolTip.SetTip(packNameBox, T("Название упаковки в карточке товара (например, «Пачка», «Блок»). Пусто — «упаковка».", "Товардын картасындагы таңгактын аты (мисалы, «Пачка», «Блок»). Бош — «таңгак».",
            "The pack name in the product card (e.g. “Pack”, “Block”). Empty — “pack”.", "Ürün kartındaki koli adı (ör. «Paket», «Blok»). Boş — «koli».", "Mahsulot kartasidagi qadoq nomi (masalan, «Pachka», «Blok»). Bo'sh — «qadoq»."));
        packNameBox.TextChanged += (_, _) =>
        {
            if (!parts.Syncing)
                line.PackName = packNameBox.Text ?? "";
        };
        var packNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 10, 0), IsVisible = false };
        // 2026-11-11 (владелец: «упаковку сверху»): ссылка способа в строке осталась (поменять способ только этой строки), но заметнее: подчёркнута (размер 12 прежний — в узкой колонке 1024 крупнее переносится на вторую строку).
        var packToggle = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 10, 0), Cursor = new Cursor(StandardCursorType.Hand), TextDecorations = TextDecorations.Underline };
        Use(packToggle, TextBlock.ForegroundProperty, "BrushAccent");
        if (cardPack > 0)
        {
            // 2026-11-10 (владелец: упаковка «как в вебе»): подпись нажимается — «Изменить штук в упаковке» (меню ReceivingPackMenu, подтверждение «Было → станет»).
            packToggle.Text = line.PackLinkText;
            ToolTip.SetTip(packToggle, T($"Склад ведёт этот товар упаковками (в упаковке {Q(cardPack)} шт): «Принимаем» и цена закупки — за упаковку.",
                $"Кампа бул товарды таңгак менен жүргүзөт (таңгакта {Q(cardPack)} даана): «Кабыл алабыз» жана баасы — таңгак үчүн.",
                $"The warehouse keeps this product in packs ({Q(cardPack)} pcs each): “Receiving” and the purchase price are per pack.",
                $"Depo bu ürünü koli ile tutar (kolide {Q(cardPack)} adet): «Alınıyor» ve alış fiyatı koli başınadır.",
                $"Ombor bu mahsulotni qadoq bilan yuritadi (qadoqda {Q(cardPack)} dona): «Qabul qilinadi» va xarid narxi qadoq uchun."));
        }
        var packPriceNote = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false };
        Use(packPriceNote, TextBlock.ForegroundProperty, "BrushTextSoft");
        var packLossText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, IsVisible = false };
        Use(packLossText, TextBlock.ForegroundProperty, "BrushDanger");
        parts.Qty = qty;
        parts.Purchase = purchase;
        parts.Markup = markup;
        parts.Sale = sale;
        // 2026-11-11 (регрессия «Принимаем 0 у всех строк», аудит «Закупки — удобство и дизайн»): TextChanged в Avalonia 11.2 приходит ПОЗЖЕ, через Dispatcher, — флаг parts.Syncing
        // к тому времени уже снят, и текст, который окно само поставило (создание строки, Sync), разбирался как ввод кассира. Скрытое поле «шт в упак.» с пустым текстом
        // давало SetPiecesPerPack(0) → количество строки 0 (позиций 0, итог пустой); «Наценка %» пересчитывала цену продажи из округлённого показа, «Цена продажи» переводила
        // строку во «вручную», «Цена закупки» резала знаки и ставила «вписана руками». Теперь текст, поставленный окном, запоминается, и его «эхо» обработчики пропускают.
        var echo = new Dictionary<TextBox, string>();
        foreach (var b in new[] { qty, pieces, purchase, markup, sale })
            echo[b] = b.Text ?? "";
        bool Echo(TextBox b)
        {
            if (echo.TryGetValue(b, out var put) && string.Equals(put, b.Text ?? "", StringComparison.Ordinal))
                return true;
            echo.Remove(b); // кассир набрал своё — дальше каждое изменение его
            return false;
        }
        // Под закупкой — «▲ +12 %» (оранжевым) и «было 45,00»; под продажей — «маржа 4 %» (красным); значения те же, что в Склад → Приёмка (ReceivingLineVm).
        // 2026-10-29: под закупкой — «↑ дороже на 5 сом (+10 %)» красным / «↓ дешевле на 2 сом» зелёным / «без изменений», «новый товар» серым;
        // под продажей — статус «авто (по общей наценке 30 %)» или «вручную · Как у всех», «прибыль 14 сом с шт.», красная пометка маржи.
        var changeText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
        Use(changeText, TextBlock.ForegroundProperty, "BrushTextSoft");
        var profitText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
        Use(profitText, TextBlock.ForegroundProperty, "BrushTextSoft");
        var lowText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
        Use(lowText, TextBlock.ForegroundProperty, "BrushDanger");
        var purchaseSub = new StackPanel { Margin = new Thickness(0, -2, 10, 0), HorizontalAlignment = HorizontalAlignment.Stretch, Children = { packPriceNote, packLossText, changeText } };
        var autoText = new TextBlock { FontSize = 12, FontWeight = FontWeight.SemiBold, Cursor = new Cursor(StandardCursorType.Hand), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right };
        Use(autoText, TextBlock.ForegroundProperty, "BrushAccent");
        autoText.PointerPressed += (_, _) =>
        {
            if (line.ShowReturnButton)
            {
                line.ReturnToAuto();
                ReceiveRefresh();
            }
        };
        var saleSub = new StackPanel { Margin = new Thickness(0, -2, 10, 0), HorizontalAlignment = HorizontalAlignment.Stretch, Children = { autoText, profitText, lowText } };
        var sum = Cell(NoWrap(MoneyText.Som(line.LineTotal)), "BrushText", 13.5, FontWeight.SemiBold);
        sum.TextAlignment = TextAlignment.Right;

        // Обновление чисел строки без пересборки (курсор остаётся в поле): все поля, где сейчас не стоит курсор, показывают актуальные значения.
        void Sync(bool force)
        {
            parts.Syncing = true;
            try
            {
                void Put(TextBox b, string t)
                {
                    if ((force || !b.IsFocused) && b.Text != t)
                    {
                        echo[b] = t; // 2026-11-11: отложенный TextChanged от этой записи — не ввод кассира
                        b.Text = t;
                    }
                }
                var pack = line.PackMode; // 2026-11-07: в режиме упаковок поля «Принимаем» и «Цена закупки» показывают упаковки и цену за упаковку
                qty.Watermark = pack ? T("упак.", "таңгак", "packs", "koli", "qadoq") : "0";
                Put(qty, ReceiveNum(pack ? line.PackCount : line.Quantity));
                Put(pieces, ReceiveNum(line.PiecesPerPack));
                pieces.IsVisible = pack;
                packNameBox.IsVisible = line.PackSale; // 2026-11-10
                Put(packNameBox, line.PackName);
                packNote.IsVisible = pack && line.PackNote.Length > 0;
                packNote.Text = line.PackNote;
                Use(packNote, TextBlock.ForegroundProperty, line.PackNeedsSize ? "BrushDanger" : "BrushTextSoft");
                packToggle.Text = line.PackLinkText; // 2026-11-07: «упаковками ▾» / «упак. → штуки ▾» / «упак. + поштучно ▾» — нажатие открывает выбор способа (2026-11-10: и «упак. = N шт ▾»)
                if (cardPack <= 0)
                {
                    ToolTip.SetTip(packToggle, line.PackSale
                        ? T("Упаковка с поштучной продажей: на склад идут УПАКОВКИ, новый товар заводится с упаковкой (штук в упаковке). Закупка и цена продажи — за упаковку.", "Даана менен сатылуучу таңгак: кампага ТАҢГАКТАР барат, жаңы товар таңгак менен түзүлөт. Сатып алуу жана сатуу баасы — таңгак үчүн.",
                            "Pack with single-piece sale: the warehouse gets PACKS, the new product is created with a pack (pieces per pack). Purchase and sale price — per pack.", "Adetli satışlı koli: depoya KOLİLER gider, yeni ürün koli ile oluşturulur. Alış ve satış fiyatı — koli başına.", "Donalab sotiladigan qadoq: omborga QADOQLAR boradi, yangi mahsulot qadoq bilan yaratiladi. Xarid va sotuv narxi — qadoq uchun.")
                        : pack
                            ? T("Упаковками → на склад штуки: вводите упаковки, штук в упаковке и цену за упаковку — на склад уйдут штуки и цена за штуку. Нажмите, чтобы выбрать другой способ.", "Таңгак менен → кампага даана: таңгакты, таңгактагы даананы жана таңгактын баасын киргизиңиз — кампага даана жана даанасынын баасы барат. Башкасын тандоо үчүн басыңыз.",
                                "By packs → pieces to stock: enter packs, pieces per pack and the price per pack — the warehouse gets pieces and the price per piece. Press to choose another way.", "Kolilerle → depoya adet: koli sayısını, koli başına adedi ve koli fiyatını girin — depoya adet ve adet fiyatı gider. Başka yol için basın.", "Qadoq bilan → omborga dona: qadoq sonini, qadoqdagi donani va qadoq narxini kiriting — omborga dona va dona narxi boradi. Boshqa usulni tanlash uchun bosing.")
                            : T("Способ приёмки: в штуках, упаковками (на склад штуки) или упаковка с поштучной продажей (новый товар). Нажмите, чтобы выбрать.", "Кабыл алуу ыкмасы: даана менен, таңгак менен (кампага даана) же даана менен сатылуучу таңгак (жаңы товар). Тандоо үчүн басыңыз.",
                                "How to receive: in pieces, by packs (pieces to stock) or a pack with single-piece sale (new product). Press to choose.", "Kabul yolu: adetle, kolilerle (depoya adet) veya adetli satışlı koli (yeni ürün). Seçmek için basın.", "Qabul usuli: donalab, qadoq bilan (omborga dona) yoki donalab sotiladigan qadoq (yangi mahsulot). Tanlash uchun bosing."));
                }
                packPriceNote.IsVisible = pack;
                packPriceNote.Text = line.PackPriceNote;
                packLossText.IsVisible = pack && line.PackLossText.Length > 0;
                packLossText.Text = line.PackLossText;
                Put(purchase, ReceiveMoney(pack ? line.PackPrice : line.PurchasePrice));
                Put(markup, ReceiveMarkup(line));
                Put(sale, ReceiveMoney(line.SalePrice));
                // 2026-11-09: название (подставленное из интернета по кнопке), значок источника, «Найти в интернете», пометка «Было: …» у переименованного товара склада.
                Put(nameBox, line.ProductName);
                badge.Source = line.Source;
                netButton.IsVisible = line.CanSearchInternet;
                genButton.IsVisible = line.CanGenerateBarcode; // 2026-11-10
                meta.Text = MetaText();
                Use(meta, TextBlock.ForegroundProperty, MetaBrush()); // 2026-11-11 (БП-11): после «Найти в интернете» источник меняется
                netButton.IsEnabled = line.NotSearchingInternet;
                renamedNote.IsVisible = line.NameRenamed;
                renamedNote.Text = line.NameWasHint;
                nameBox.FontStyle = line.NameRenamed ? FontStyle.Italic : FontStyle.Normal;
                ToolTip.SetTip(nameBox, line.NameRenamed ? line.ProductName + "\n" + line.NameWasHint : line.ProductName.Length > 0 ? line.ProductName : null); // 2026-11-10: полное название при наведении
                sum.Text = NoWrap(MoneyText.Som(line.LineTotal));
                numText.Text = line.LineNo > 0 ? line.LineNo.ToString(CultureInfo.InvariantCulture) : "";
                Avalonia.Automation.AutomationProperties.SetName(numText, T("Позиция", "Позиция", "Item", "Kalem", "Pozitsiya") + " " + numText.Text);
                changeText.Text = line.PurchaseChangeText + (line.PurchaseWasText.Length > 0 ? "\n" + line.PurchaseWasText : "") // 2026-10-29 (спека Fable): «↑ дороже на 5 сом (+3 %)» + «было 145,00»
                    + (line.LandedCostText.Length > 0 ? "\n" + line.LandedCostText : ""); // 2026-11-06 (В12, ЖП Н-6): «с расходами 2,125»
                Use(changeText, TextBlock.ForegroundProperty, line.IsChangeUp ? "BrushDanger" : line.IsChangeDown ? "BrushSuccess" : "BrushTextSoft");
                changeText.FontWeight = line.IsChangeUp || line.IsChangeDown ? FontWeight.SemiBold : FontWeight.Normal;
                ToolTip.SetTip(changeText, line.PurchaseChangeHint);
                profitText.Text = line.ProfitText;
                // 2026-11-06: нет цены продажи — красная пометка и подсветка строки (перед отправкой прихода спросим цену).
                lowText.Text = line.LowMarginText.Length == 0 && line.IsSalePriceMissing
                    ? T("нет цены продажи", "сатуу баасы жок", "no sale price", "satış fiyatı yok", "sotuv narxi yo'q")
                    : line.LowMarginText;
                autoText.Text = line.CanReturnToAuto ? line.SaleStatusText + " · " + line.SaleButtonText : line.SaleStatusText; // «вручную · Как у всех» (нажатие возвращает)
                ToolTip.SetTip(autoText, line.HasSaleMode ? line.SaleModeHint : null);
                // 2026-11-11 (аудит «Закупки», БП-4): кисть — по варианту темы окна (ловушка AI_HANDOFF §10: FindResource без варианта в тёмной теме подсветку не давал).
                g.Background = line.IsBelowCost ? ThemeBrush("BrushDangerSoft") : line.IsLowMargin || line.IsSalePriceMissing ? ThemeBrush("BrushWarningSoft") : null; // цена ниже закупки — красная строка, маржа мало — жёлтая
                if (parts.Box is { } rowBox)
                    rowBox.Opacity = line.Quantity <= 0 ? 0.72 : 1;
            }
            finally
            {
                parts.Syncing = false;
            }
        }
        parts.Refresh = Sync;
        Sync(true);

        // 2026-11-10: снимок значений строки на входе в КАЖДОЕ поле (для Esc и пустого поля при уходе): у каждого поля свой — фокус нового поля приходит раньше ухода из старого.
        var cellSnaps = new Dictionary<TextBox, object>();
        // Ввод в ячейку сразу попадает в строку. Закупка при неизменной продаже пересчитывает наценку (продажа сама не меняется);
        // наценка меняет цену продажи (ReceivingLineVm.MarkupText: округление вверх до сома); цена продажи меняет наценку.
        qty.TextChanged += (_, _) =>
        {
            if (parts.Syncing || Echo(qty) || !ReceiveReadCell(qty, out var v))
                return;
            if (!cellSnaps.ContainsKey(qty))
                cellSnaps[qty] = line.CaptureCellState(); // 2026-11-10: поле уже было в фокусе (вход без GotFocus) — значения до правки запоминаем перед первой буквой
            if (line.PackMode)
            {
                line.SetPackCount(v); // 2026-11-07: число упаковок — штуки считает строка (упаковки × штук в упаковке)
                v = line.Quantity;
            }
            else
                line.Quantity = v;
            // 2026-11-11 (Д-8, как в «Склад → Приёмка»): ручная правка количества строки последнего скана — не сброс «тот же код подряд»: пауза 20 с считается заново.
            if (s.RepeatGuard.LastCode is { } lastCode && ReferenceEquals(ReceivingRepeatScan.FindLine(s.Lines, lastCode), line))
                s.RepeatGuard.Touch();
            // 2026-10-31 («Не округляй»): сервер принимает не больше 3 знаков после запятой. «12,3456» остаётся как набрано, строка красная
            // (ReceivingLineVm.PriceProblemText), провести нельзя, пока не исправят (в «Склад → Приёмка» то же через ReceivingLineVm.QuantityText).
            var roundedHint = RoundedQuantityHint;
            if (ReceivingLineVm.QuantityTooPrecise(v))
                ReceiveMessage(roundedHint, warning: true);
            else if (s.Message.Text == roundedHint)
                ReceiveMessage(null);
            ReceiveRefresh();
        };
        // 2026-10-28 (дробные цены, автонаценка): закупка и продажа разбираются общим правилом ReceivingLineVm (запятая/точка, 2 знака, без «1e3»);
        // закупка пересчитывает цену продажи строки в режиме «авто», правка продажи переводит строку в «вручную».
        purchase.TextChanged += (_, _) =>
        {
            if (parts.Syncing || Echo(purchase))
                return;
            if (!cellSnaps.ContainsKey(purchase))
                cellSnaps[purchase] = line.CaptureCellState(); // 2026-11-10: поле уже было в фокусе (вход без GotFocus) — значения до правки запоминаем перед первой буквой
            if (line.PackMode)
                line.SetPackPriceText(purchase.Text); // 2026-11-07: в режиме упаковок поле — цена за упаковку, цену за штуку считает строка
            else
                line.SetPurchaseText(purchase.Text);
            ReceiveRefresh();
        };
        pieces.TextChanged += (_, _) =>
        {
            if (parts.Syncing || Echo(pieces) || !ReceiveReadCell(pieces, out var perPack))
                return;
            if (!cellSnaps.ContainsKey(pieces))
                cellSnaps[pieces] = line.CaptureCellState(); // 2026-11-10: поле уже было в фокусе (вход без GotFocus) — значения до правки запоминаем перед первой буквой
            line.SetPiecesPerPack(perPack);
            var roundedHint = RoundedQuantityHint;
            if (ReceivingLineVm.QuantityTooPrecise(line.Quantity))
                ReceiveMessage(roundedHint, warning: true);
            else if (s.Message.Text == roundedHint)
                ReceiveMessage(null);
            ReceiveRefresh();
        };
        packToggle.PointerPressed += (_, _) =>
        {
            // 2026-11-10: и у товара с упаковкой в карточке — меню «как в карточке / Изменить штук в упаковке» (раньше подпись была только подсказкой).
            // 2026-11-07: вместо переключателя «упаковками ↔ в штуках» — меню из трёх способов (в штуках, упаковками → штуки, упаковка с поштучной продажей; общий код со «Склад → Приёмка»).
            ReceivingPackMenu.Show(packToggle, line, () =>
            {
                Sync(true);
                ReceiveRefresh();
                if (line.PackMode && line.PiecesPerPack <= 0)
                    pieces.Focus();
                else
                    qty.Focus();
            });
        };
        markup.TextChanged += (_, _) =>
        {
            if (parts.Syncing || Echo(markup))
                return;
            var t = (markup.Text ?? "").Trim();
            if (t.Length == 0)
                return;
            line.MarkupText = t;
            ReceiveRefresh();
        };
        sale.TextChanged += (_, _) =>
        {
            if (parts.Syncing || Echo(sale))
                return;
            if (!cellSnaps.ContainsKey(sale))
                cellSnaps[sale] = line.CaptureCellState(); // 2026-11-10: поле уже было в фокусе (вход без GotFocus) — значения до правки запоминаем перед первой буквой
            line.SetSaleText(sale.Text);
            ReceiveRefresh();
        };
        // 2026-11-10 (жалоба владельца «удобства не удобно», как в «Склад → Приёмка»): вход в поле (клик, Tab, Enter) — всё число выделено, набор заменяет; пока набирают,
        // пустое поле остаётся пустым; ушли из поля, оставив его пустым, — прежнее значение поля; Esc — откат правки поля.
        foreach (var (cell, field) in new (TextBox, ReceivingCellField?)[] { (qty, ReceivingCellField.Quantity), (pieces, ReceivingCellField.Pieces), (purchase, ReceivingCellField.Purchase), (markup, null), (sale, ReceivingCellField.Sale) })
        {
            string? textOnEnter = null;
            cell.GotFocus += (_, _) =>
            {
                cellSnaps[cell] = line.CaptureCellState();
                textOnEnter = cell.Text ?? "";
                // клик мышью ставит курсор ПОСЛЕ получения фокуса — выделяем всё следом за ним
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (cell.IsFocused)
                        cell.SelectAll();
                }, Avalonia.Threading.DispatcherPriority.Background);
            };
            cell.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key != Key.Escape || !cellSnaps.TryGetValue(cell, out var snap) || (textOnEnter is not null && string.Equals(cell.Text ?? "", textOnEnter, StringComparison.Ordinal)))
                    return; // нечего откатывать — Esc идёт дальше (например, «назад»)
                e.Handled = true;
                line.RestoreCellField(field ?? ReceivingCellField.Sale, snap); // наценка меняет цену продажи — её и возвращаем
                Sync(true);
                ReceiveRefresh();
                cellSnaps[cell] = line.CaptureCellState();
                textOnEnter = cell.Text ?? "";
                cell.SelectAll();
            }, RoutingStrategies.Tunnel);
            cell.LostFocus += (_, _) =>
            {
                cellSnaps.Remove(cell, out var snap);
                // 2026-11-11 (Б-4): штрихкод, «напечатанный» медленным сканером в поле, — не число: поле возвращается, код обрабатывается как скан.
                var typedCode = line.TakeRejectedBarcode() ?? (ReceivingLineVm.LooksLikeBarcode(cell.Text) ? cell.Text!.Trim() : null);
                if (typedCode is not null)
                {
                    if (field is { } bf)
                        line.RestoreCellField(bf, snap);
                    Sync(true);
                    _ = ReceiveAddByCodeAsync(typedCode);
                }
                var restored = field is { } f && string.IsNullOrWhiteSpace(cell.Text) && line.RestoreEmptyField(f, snap);
                textOnEnter = null;
                line.CommitText(); // 2026-10-28: сырой набранный текст → нормальный показ («100,60»)
                Sync(false);
                if (restored)
                    ReceiveRefresh();
            };
        }

        // Enter и Tab: «Название» → «Количество» → «Закупка» → «Цена продажи» → поле скана (наценку Enter пропускает, её правят мышью или Shift+Tab).
        void Chain(TextBox from, Func<TextBox?> next) => from.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Tab) || e.KeyModifiers != KeyModifiers.None)
                return;
            e.Handled = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_receive != s)
                    return;
                var to = next();
                if (to is null)
                    ReceiveFocusScan(s);
                else
                {
                    to.Focus();
                    to.SelectAll();
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }, RoutingStrategies.Tunnel);
        if (parts.Name is { } nm)
            Chain(nm, () => qty);
        Chain(markup, () => sale);
        Chain(qty, () => line.PackMode ? pieces : purchase);
        Chain(pieces, () => purchase);
        Chain(purchase, () => sale);
        Chain(sale, () => null);

        // 2026-10-30 (баг владельца): подписи под «Закупкой» («без изменений» + «было 35,00») и «Продажей» («прибыль …») разной высоты сдвигали поля по вертикали
        // (поля центрировались вместе с подписями). Теперь все четыре поля прижаты к верху строки (одинаковая линия 40 px), подписи — под полем и ряд не двигают;
        // «На складе», «Сумма закупки», название и «✕» — по центру этой же линии (OnFieldLine). Деньги и расчёт не менялись — только разметка.
        var qtyBox = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { qty, pieces, packNameBox, packNote, packToggle } };
        Grid.SetColumn(qtyBox, 3);
        g.Children.Add(qtyBox);
        var purchaseBox = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { purchase, purchaseSub } };
        Grid.SetColumn(purchaseBox, 4);
        g.Children.Add(purchaseBox);
        var markupBox = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { markup } };
        Grid.SetColumn(markupBox, 5);
        g.Children.Add(markupBox);
        var saleBox = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Children = { sale, saleSub } };
        Grid.SetColumn(saleBox, 6);
        g.Children.Add(saleBox);
        var sumLine = OnFieldLine(sum);
        Grid.SetColumn(sumLine, 7);
        g.Children.Add(sumLine);

        var remove = new Button { Content = "✕", Width = 44, Height = 44, MinHeight = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(10), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, 0, 0), FontSize = 15 }; // 2026-10-30: 44 px по центру линии полей (40 px)
        Use(remove, Button.ForegroundProperty, "BrushTextSoft");
        Avalonia.Automation.AutomationProperties.SetName(remove, T("Убрать строку", "Сапты алып салуу", "Remove the line", "Satırı kaldır", "Qatorni olib tashlash") + " " + line.ProductName);
        remove.Focusable = true; // 2026-11-11 (БП-8): ✕ доступна с Tab; Del на ней убирает строку
        remove.Click += (_, _) => ReceiveRemoveLine(s, line); // 2026-11-11 (БП-5): с плашкой «Вернуть» на 5 с
        Grid.SetColumn(remove, 8);
        g.Children.Add(remove);

        var box = new Border { Padding = new Thickness(14, 6, 8, 6), BorderThickness = new Thickness(0, 0, 0, 1), Child = g, MinHeight = 66 };
        Use(box, Border.BorderBrushProperty, "BrushBorder");
        if (line.Quantity <= 0)
            box.Opacity = 0.72;
        parts.Box = box;
        s.RowMap[line] = parts;
        if (ReferenceEquals(s.Highlight, line))
            ReceiveApplyHighlight(parts);

        // Числа строки живут вместе с моделью (ячейка, «Обновить цены продажи», повторный скан); подписка — только пока строка на экране.
        System.ComponentModel.PropertyChangedEventHandler onChanged = (_, _) => Sync(false);
        box.AttachedToVisualTree += (_, _) => line.PropertyChanged += onChanged;
        box.DetachedFromVisualTree += (_, _) => line.PropertyChanged -= onChanged;
        return box;
    }

    /// <summary>Поле числа (запятая или точка), 40 px; изменение — сразу в строку.</summary>
    private TextBox NumberBox(double value, string name, Action<double> changed)
    {
        var box = new TextBox
        {
            Text = value > 0 ? value.ToString("0.###", Ru) : "", Watermark = "0", Height = 40, MinHeight = 40, FontSize = 14, Margin = new Thickness(0, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        box.Classes.Add("num");
        Avalonia.Automation.AutomationProperties.SetName(box, name);
        box.TextChanged += (_, _) =>
        {
            var t = (box.Text ?? "").Trim().Replace(" ", "").Replace(',', '.');
            if (t.Length == 0)
                changed(0);
            else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v < 1e9)
                changed(Math.Round(v, 3));
        };
        box.GotFocus += (_, _) => box.SelectAll();
        return box;
    }

    private void ReceiveTiles()
    {
        if (_receive is not { } s)
            return;
        var active = s.Lines.Where(l => l.Quantity > 0).ToList();
        s.Tiles.Children.Clear();
        s.Tiles.Children.Add(UiKit.Kpi(T("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), active.Count.ToString(CultureInfo.InvariantCulture)));
        s.Tiles.Children.Add(UiKit.Kpi(T("Общее количество", "Жалпы саны", "Total quantity", "Toplam miktar", "Umumiy miqdor"), Q(active.Sum(l => l.Quantity))));
        s.Tiles.Children.Add(UiKit.Kpi(T("Сумма накладной", "Накладнойдун суммасы", "Invoice total", "Fatura toplamı", "Nakladnoy summasi"), MoneyText.Som(active.Sum(l => l.LineTotal))));
        // 2026-10-24 (Б2): «подорожало: N» — строк, где закупка выше прежней (как в итоге Склад → Приёмка).
        if (ReceivingPricing.CountRaised(active) is > 0 and var raised)
            s.Tiles.Children.Add(UiKit.Kpi(T("Подорожало", "Кымбаттаган", "Price rose", "Fiyatı arttı", "Qimmatlagan"), raised.ToString(CultureInfo.InvariantCulture)));
    }

    private void ReceiveSteps()
    {
        if (_receive is not { } s)
            return;
        s.Steps.Children.Clear();
        var hasSupplier = s.Picker.Selected is not null;
        var hasGoods = s.Lines.Any(l => l.Quantity > 0);
        foreach (var (n, text, done, current) in new[]
                 {
                     (1, T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), hasSupplier, !hasSupplier),
                     (2, T("Товары", "Товарлар", "Goods", "Ürünler", "Tovarlar"), hasGoods, hasSupplier && !hasGoods),
                     (3, T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), false, hasSupplier && hasGoods),
                 })
        {
            var pill = SectionCards.Pill(this, (done ? "✓ " : n + ". ") + text, done ? "BrushSuccessSoft" : current ? "BrushWarningSoft" : "BrushPanelSoft",
                done ? "BrushSuccess" : current ? "BrushText" : "BrushTextSoft");
            pill.Padding = new Thickness(12, 6);
            s.Steps.Children.Add(pill);
        }
    }

    private void ReceiveFooter()
    {
        if (_receive is not { } s)
            return;
        var active = s.Lines.Where(l => l.Quantity > 0).ToList();
        var raised = ReceivingPricing.CountRaised(active);
        var raisedText = raised > 0 ? T($" · подорожало: {raised}", $" · кымбаттаган: {raised}", $" · price rose: {raised}", $" · fiyatı arttı: {raised}", $" · qimmatlagan: {raised}") : "";
        s.Footer.Text = active.Count == 0
            ? s.Picker.Selected is null
                ? T("Сканируйте товары или выберите поставщика (шаг 1).", "Товарларды сканерлеңиз же жеткирүүчүнү тандаңыз (1-кадам).", "Scan the products or choose a supplier (step 1).", "Ürünleri okutun veya tedarikçi seçin (1. adım).", "Mahsulotlarni skanerlang yoki yetkazib beruvchini tanlang (1-qadam).")
                : T("Добавьте товары и впишите количество (шаг 2).", "Товарларды кошуп, санын жазыңыз (2-кадам).", "Add products and enter quantities (step 2).", "Ürün ekleyip miktar girin (2. adım).", "Mahsulot qo'shib, miqdorni kiriting (2-qadam).")
            // 2026-10-29: «Позиций N · штук M · закупка на X сом · продажа на Y сом · прибыль Z сом»
            : T("Позиций", "Позициялар", "Items", "Kalem", "Pozitsiyalar") + $": {active.Count} · " + T("штук", "даана", "pieces", "adet", "dona") + $": {active.Sum(l => l.Quantity).ToString("0.###", Ru)} · "
              + T("закупка на", "сатып алуу", "purchase", "alış", "xarid") + " " + NoWrap(MoneyText.Som(active.Sum(l => l.LineTotal))) + " · "
              // 2026-11-12 (Д-9): на узком окне («продажа на …» есть в карточке строки) итог — не больше двух строк
              + (s.Narrow ? "" : T("продажа на", "сатуу", "sale", "satış", "sotuv") + " " + NoWrap(MoneyText.Som(active.Sum(l => ReceivingPricing.LineTotal(l.Quantity, l.SalePrice)))) + " · ")
              // 2026-11-05 (функция 11): прибыль считается от себестоимости (закупка + доп. расходы), как её увидит склад после прихода
              + T("прибыль", "пайда", "profit", "kâr", "foyda") + " " + NoWrap(MoneyText.Som(active.Sum(l => ReceivingPricing.LineTotal(l.Quantity, l.SalePrice)) - active.Sum(l => ReceivingPricing.LineTotal(l.Quantity, l.CostPrice)))) + raisedText
              + (active.Any(l => l.LandedExtraPerUnit > 0) ? T(" · доп. расходы ", " · кошумча чыгым ", " · extra costs ", " · ek masraf ", " · qo'shimcha xarajat ") + MoneyText.Som(LandedCost.DistributedTotal(active)) : "")
              + (s.Picker.Selected is null ? T(" — выберите поставщика (шаг 1) или оприходуйте без него.", " — жеткирүүчүнү тандаңыз (1-кадам) же жеткирүүчүсүз кабыл алыңыз.", " — choose a supplier (step 1) or post without one.", " — tedarikçi seçin (1. adım) veya tedarikçisiz kaydedin.", " — yetkazib beruvchini tanlang (1-qadam) yoki usiz kirim qiling.") : "");
        s.Footer.MaxLines = 2; // 2026-11-12 (Д-9): итог слева не больше 2 строк; остальное — в подсказке
        s.Footer.TextTrimming = TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(s.Footer, s.Footer.Text);
        // 2026-10-24 (Б2): «Обновить цены продажи (N)» — пока есть строки с подорожанием и непоправленной продажей.
        var raisable = PurchaseReceivingService.CountRaisableSalePrices(active);
        s.Raise.IsVisible = raisable > 0;
        if (raisable > 0)
        {
            s.Raise.Content = T($"Обновить цены продажи ({raisable})", $"Сатуу баасын жаңыртуу ({raisable})", $"Update sale prices ({raisable})", $"Satış fiyatlarını güncelle ({raisable})", $"Sotuv narxlarini yangilash ({raisable})");
            Avalonia.Automation.AutomationProperties.SetName(s.Raise, s.Raise.Content as string);
        }
        s.Post.IsEnabled = !s.Busy && active.Count > 0; // 2026-11-06: без поставщика кнопка доступна — по нажатию спросим (выбрать / без поставщика / отмена)
        ReceiveTotalRefresh(s); // 2026-11-11 (БП-6): «Итого приход» крупно у кнопки
    }

    // ------------------------------------------------------------------ оприходовать

    // 2026-10-25, ТЗ «оприходование в фоне» (Q4): «Оприходовать товары» → проверки и подтверждение цен → «Подтверждение оплаты» (способ оплаты, касса/ящик
    // смены, долг выбираются ДО постановки) → задача уходит в очередь BackgroundReceivingQueue (приход и оплата проводятся в фоне, переживают закрытие окна
    // и перезапуск), список приёма очищается сразу. s.Busy — только синхронная часть; дальше ждём итог не дольше 0,5 с: успели — диалог «Оприходование
    // завершено» как раньше, нет — плашка «Операция выполняется в фоне» и итог по JobFinished.
    private async Task ReceivePostAsync()
    {
        if (_receive is not { } s || s.Busy)
            return;
        s.Busy = true;
        ReceiveFooter();
        try
        {
            var supplierInfo = s.Picker.Selected;
            var active = s.Lines.Count(l => l.Quantity > 0);
            if (active == 0)
                return;
            // 2026-11-06: поставщик не выбран — вопрос «Выбрать поставщика / Оприходовать без поставщика / Отмена» (с поставщиком — как раньше, без вопроса).
            var step = ReceivingSupplierRule.Resolve(supplierInfo is not null, null);
            if (step == ReceivingSupplierRule.Step.Ask)
            {
                var ask = new PurchaseNoSupplierDialog(active);
                await ask.ShowDialog(this).ConfigureAwait(true);
                if (_receive != s)
                    return;
                step = ReceivingSupplierRule.Resolve(false, ask.Result);
            }
            if (step == ReceivingSupplierRule.Step.OpenSupplierPicker)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { if (_receive == s) s.Picker.Open(); }, Avalonia.Threading.DispatcherPriority.Background);
                return;
            }
            if (step == ReceivingSupplierRule.Step.Stop)
                return;
            await ReceivePostCoreAsync(s, supplierInfo).ConfigureAwait(true);
        }
        finally
        {
            s.Busy = false;
            if (_receive == s)
                ReceiveFooter();
        }
    }

    private async Task ReceivePostCoreAsync(ReceiveState s, SupplierInfo? supplierInfo)
    {
        // 2026-11-07 (ТЗ «Порядковая нумерация»): в приход строки уходят в порядке добавления («№» 1, 2, 3…), а не «новая сверху» — открытая позже накладная покажет те же номера.
        var lines = ReceivingListOrder.InAddedOrder(s.Lines.Where(l => l.Quantity > 0));
        if (lines.Count == 0)
            return;
        // 2026-11-06 (жалоба «Орук маркет», приход №48): отсутствие цены продажи — не ошибка: ниже окно «У N позиций нет цены продажи» (цена/наценка или «Оставить без цены»).
        var problems = PurchaseReceivingService.Validate(lines, requireSalePrice: false);
        problems.AddRange(PurchaseReceivingService.FractionalProblems(lines)); // 2026-10-31: «2,5 шт» у нового товара — выберите «кг» или целое
        if (problems.Count > 0)
        {
            ReceiveMessage(T("Нельзя оприходовать: ", "Кирешке алууга болбойт: ", "Can't post: ", "Kaydedilemez: ", "Kirim qilib bo'lmaydi: ") + string.Join("; ", problems.Take(4))
                           + (problems.Count > 4 ? T($" …и ещё {problems.Count - 4}", $" …жана дагы {problems.Count - 4}", $" …and {problems.Count - 4} more", $" …ve {problems.Count - 4} tane daha", $" …va yana {problems.Count - 4}") : ""), warning: true);
            return;
        }
        // 2026-11-07: «упаковка с поштучной продажей» меняет единицу остатка нового товара — явное подтверждение (как в «Склад → Приёмка»).
        if (lines.Where(l => l.PackSale).ToList() is { Count: > 0 } packSaleLines)
        {
            // 2026-11-11 (convert-to-packages): свежий остаток с сервера, «Округлить?» по согласию, затем «Было → станет» (общий шаг с «Склад → Приёмка»).
            var goOn = await ReceivingPackConvertFlow.ConfirmAsync(this, packSaleLines).ConfigureAwait(true);
            if (_receive != s || !goOn)
            {
                if (_receive == s)
                    ReceiveRender(); // строку могли пересчитать в упаковку товара (already_has_packages)
                return;
            }
        }
        // 2026-11-06: у строк нет цены продажи — спрашиваем ДО отправки; программа сама («закупка + %») ничего не ставит. «Отмена» — вернуться к списку.
        if (ReceivingPricing.MissingSalePrice(lines).Count > 0)
        {
            var proceed = await Dialogs.MissingSalePriceDialog.AskAsync(this, lines).ConfigureAwait(true);
            if (_receive != s)
                return;
            ReceiveRefresh();
            if (!proceed)
                return;
        }
        // 2026-11-05 (функция 11): количество могли поправить после «Доп. расходы…» — доли пересчитываем и показываем превью «закупка → с расходами»; долг поставщику не меняется.
        if (s.LandedTotal > 0)
        {
            var landedRows = LandedCost.Preview(lines, s.LandedTotal, s.LandedMode);
            LandedCost.Apply(landedRows);
            if (!await ReceiveConfirmLandedAsync(landedRows, s.LandedTotal).ConfigureAwait(true))
                return;
        }
        else
            LandedCost.Clear(lines);
        // 2026-10-24, ТЗ «Склад» (Б2): меняются цены продажи — короткое подтверждение со списком, как в Склад → Приёмка.
        if (PurchaseReceivingService.SalePriceChanges(lines) is { Count: > 0 } changedPrices
            && !await ReceiveConfirmPriceChangesAsync(changedPrices).ConfigureAwait(true))
            return;
        var total = Math.Round(lines.Sum(l => l.LineTotal), 2);
        // 2026-11-06: без поставщика окна оплаты нет — долг поставщику невозможен (ReceivingSupplierRule.DebtAllowed), оплата не записывается, товары идут прямо на склад.
        PurchasePayment? pay = null;
        if (supplierInfo is not null)
        {
            var dialog = new PurchasePaymentDialog(supplierInfo.Name, lines.Count, total);
            await dialog.ShowDialog(this).ConfigureAwait(true);
            if (dialog.Result is not { } chosen || _receive != s)
                return;
            pay = chosen;
        }
        var sinceConfirm = System.Diagnostics.Stopwatch.StartNew();

        // Оплата выбрана — всё нужное для финишера (смена, касса, срок долга) запоминаем в задаче сейчас: к концу фоновой работы смена могла смениться.
        var supplier = supplierInfo is null ? null : new PurchaseReceivingService.Supplier(supplierInfo.Id, supplierInfo.Name, supplierInfo.Inn, supplierInfo.Phone);
        var employee = PosApp.CurrentUserDisplayName ?? PosApp.CurrentUserId;
        var paidNow = pay is { Kind: PurchasePayKind.Cash or PurchasePayKind.Drawer };
        var shiftId = PosApp.ActiveShiftId;
        Dictionary<string, string>? fields = null;
        if (pay is { Kind: PurchasePayKind.Drawer })
            fields = new Dictionary<string, string>
            {
                ["cashbox_role"] = "pos_main",
                ["cashbox"] = PosApp.PosCashboxId ?? "",
                ["shift"] = shiftId ?? "",
            };
        if (supplier is null || pay is null)
            PosLogger.Log($"Закупки: приход без поставщика, строк {lines.Count} — остаток и цены пишутся в товары, долга и оплаты нет, в очередь фоновых приходов.", "STOCK");
        else
            PosLogger.Log($"Закупки: приход от «{supplier.Name}» ({supplier.Id}), строк {lines.Count}, оплата {pay.Kind}" +
                          (pay.Kind == PurchasePayKind.Partial ? $" — сразу {pay.PaidNow:0.00}{(pay.PartialFromDrawer ? " из ящика смены" : $" из кассы «{pay.CashboxName}»")}" : "") +
                          (pay.Kind is PurchasePayKind.Debt or PurchasePayKind.Partial ? $", срок {pay.DueDays} дн." : "") + " — в очередь фоновых приходов.", "PAYMENT");

        EnsureBackgroundSubscription();
        var postContent = s.Post.Content;
        s.Post.Content = T("Оприходую…", "Кирешке алынууда…", "Posting…", "Kaydediliyor…", "Kirim qilinmoqda…");
        string jobId;
        try
        {
            jobId = BackgroundReceivingQueue.Instance.Enqueue(new ReceivingJobRequest(ReceivingJobSource.Purchases, lines, supplier, paidNow, employee, pay?.DueDays ?? 30, fields,
                "purchases", pay is null ? null : PurchaseFinishPayload.From(pay, s.OrderId, shiftId), SupplierDocNumber: supplier is null ? null : s.DocNumber.Text));
        }
        catch (Exception ex)
        {
            // Очередь закрыта или диск не принял запись: строки остаются в списке, кассир повторит.
            s.Post.Content = postContent;
            PosLogger.Log($"Закупки: приход не поставлен в очередь ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ReceiveMessage(ex is InvalidOperationException ? ex.Message
                : T("Не удалось поставить приход в очередь: ", "Кирүүнү кезекке коюу мүмкүн болгон жок: ", "Could not queue the delivery: ", "Mal kabul kuyruğa alınamadı: ", "Kirimni navbatga qo'yib bo'lmadi: ") + ex.Message, warning: true);
            return;
        }

        if (supplier is not null)
            s.DocNumber.Text = ""; // 2026-10-30: номер накладной поставщика ушёл с приходом
        s.LandedTotal = 0; // 2026-11-05 (функция 11): доп. расходы ушли с этим приходом (доли — в строках задачи)
        // Задача на диске — список очищаем сразу и готовимся к следующей накладной (заказ уже закрыт этой задачей — к новому приёму не привязываем).
        _bgWaiting.Add(jobId);
        _purchasePackBar?.ResetAfterPosting(); // 2026-11-12 (Л-9, решение владельца): приход поставлен в очередь — способ приёма снова «В штуках»
        foreach (var line in lines)
            ReceivingPackSizes.Remember(line); // 2026-11-07: «штук в упаковке» запоминается локально — в следующий раз подставится (в карточку товара на сервере не пишется)
        foreach (var line in lines)
            s.Lines.Remove(line);
        if (s.OrderId is not null)
        {
            s.OrderId = null;
            s.OrderNumber = null;
            if (s.Title is not null)
                s.Title.Text = T("Приём товара", "Товар кабыл алуу", "Receive goods", "Mal kabul", "Tovar qabul qilish");
        }
        ReceiveMessage(null);
        ReceiveRender();
        ReceiveFocusScan(s);

        ReceivingJob? done;
        try
        {
            // 0,5 с отсчитываем от подтверждения оплаты: диск и перерисовка списка уже съели часть, индикатор не должен гореть дольше
            // (запас 40 мс на шаг системного таймера Windows ≈ 15 мс).
            var left = Math.Clamp(0.46 - sinceConfirm.Elapsed.TotalSeconds, 0.05, 0.46);
            done = await BackgroundReceivingQueue.Instance.WaitAsync(jobId, TimeSpan.FromSeconds(left)).ConfigureAwait(true);
            // Задача могла закончиться как раз на границе 0,5 с (обработчик JobFinished пропустил её, пока мы ждали) — смотрим ещё раз.
            if (done is null && BackgroundReceivingQueue.Instance.Get(jobId) is { IsFinal: true } late)
                done = late;
        }
        finally
        {
            _bgWaiting.Remove(jobId);
            s.Post.Content = postContent;
        }
        if (done is null)
        {
            if (_receive == s)
                ReceiveMessage(BackgroundNoticeText, notice: true);
            return;
        }
        _bgShown.Add(done.Id);
        await ReceiveShowResultAsync(s, done).ConfigureAwait(true);
    }

    /// <summary>«Операция выполняется в фоне — можно продолжать работу» (плашка вместо долгого ожидания).</summary>
    // 2026-11-06: офлайн-приход — без связи вместо «выполняется в фоне» говорим главное: приход сохранён и уйдёт сам (+ счётчик неотправленных).
    private static string BackgroundNoticeText => BackgroundReceivingQueue.NoConnectionNow
        ? BackgroundReceivingQueue.SavedOfflineText + ". " + BackgroundReceivingQueue.UnsentText(BackgroundReceivingQueue.Instance.ActiveCount)
        : T("Операция выполняется в фоне — можно продолжать работу", "Операция фондо аткарылууда — иштей берсеңиз болот",
            "The operation is running in the background — you can keep working", "İşlem arka planda yürütülüyor — çalışmaya devam edebilirsiniz",
            "Amal fonda bajarilmoqda — ishni davom ettirishingiz mumkin");

    /// <summary>Задача закончилась за 0,5 с: как раньше — «Оприходование завершено» (накладная, печать), не принятые строки возвращаются в список.</summary>
    private async Task ReceiveShowResultAsync(ReceiveState s, ReceivingJob job)
    {
        var posted = job.Lines.Where(l => l.Outcome == ReceivingLineOutcome.Posted).ToList();
        string? noSupplierText = null;
        var noSupplierWarning = false;
        if (job.Status != ReceivingJobStatus.Done && _receive == s)
        {
            // Отказ сервера / не отправлялись: строки снова в списке — поправить и повторить (как при ошибке раньше); «исход неизвестен» остаётся в очереди.
            foreach (var back in BackgroundReceivingQueue.Instance.TakeBackLines(job.Id))
                s.Lines.Add(back);
            ReceiveRender();
        }
        if (posted.Count > 0)
        {
            var result = new PurchaseReceivingService.PostResult(posted.Select(l => l.ToVm()).ToList(), posted.Sum(l => l.LineTotal), job.Created, job.Errors,
                job.ReceiptId, job.ReceiptTotal, job.ReceiptAt, job.State.ReceiptKey, job.State.ServerNumber);
            var outcome = new PurchaseOutcome(result, job.Summary ?? PurchaseReceivingService.SummaryText(result), job.Errors.Count > 0 || job.Status != ReceivingJobStatus.Done, null);
            var supplier = s.Picker.Selected;
            if (supplier is not null && job.Supplier is not null)
            {
                var done = new PurchaseDoneDialog(supplier, outcome, posted.Select(l => new PurchaseDocs.DocLine(l.ProductName, l.Unit, l.Quantity, l.PurchasePrice)).ToList());
                await done.ShowDialog(this).ConfigureAwait(true);
            }
            else
                noSupplierText = BackgroundResultText(job, out noSupplierWarning); // 2026-11-06: приход без поставщика — накладной нет, итог строкой
        }
        else if (_receive == s)
            ReceiveMessage(BackgroundResultText(job, out _), warning: true);

        if (_receive != s)
            return;
        if (posted.Count > 0)
            await ReceiveOfferLabelsAsync(posted).ConfigureAwait(true); // 2026-11-11 (аудит «Закупки», БП-10): «Печать этикеток (N)» после проведения
        if (_receive != s)
            return;
        // Всё принято — назад к истории (с новым приходом наверху); не всё — строки с ошибкой остаются, их можно поправить и повторить.
        if (posted.Count > 0 && !s.Lines.Any(l => l.Quantity > 0))
        {
            CloseSub();
            await ReloadAsync().ConfigureAwait(true);
            if (noSupplierText is not null)
                SetStatus(noSupplierText, noSupplierWarning);
        }
        else if (job.Errors.Count > 0)
            ReceiveMessage(string.Join("\n", job.Errors.Take(6)), warning: true);
    }

}
