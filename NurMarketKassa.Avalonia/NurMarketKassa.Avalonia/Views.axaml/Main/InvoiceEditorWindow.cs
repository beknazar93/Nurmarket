using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-11, тестировщик (иишка.md, 2–6): «рядом с «Выполнить» кнопка «Отредактировать» — подробный редактор списка, который
/// выдаёт ИИ: наименование с накладной (можно заменить), наименование в чеке (по умолчанию пустое — берётся с накладной), количество
/// (если ИИ неверно прочитал рукописное), закупка и продажа. Всё — аккуратно, каждое в своей ячейке».
///
/// 2026-10-19, владелец: «при скане накладной добавляет товар на склад напрямую, а не через закупку». Теперь это окно «Закупка по
/// накладной»: поставщик из списка NurCRM (как в «Приёмке» склада; распознанный в накладной — только подсказкой, выбирает владелец),
/// «Оплачено сразу / В долг поставщику», итог, добавить/удалить строку, и «Провести закупку» — тем же путём, что «Приёмка»
/// (PurchaseReceivingService.PostAsync: новые товары создаются с остатком 0, остаток приходит документом закупки, ключ
/// идемпотентности — у сервиса). Без поставщика провести нельзя. Строки живут в черновике карточки (PurchaseDraft): после обрыва связи
/// повторное «Провести» уходит с теми же строками и тем же ключом — приход не задваивается, созданный товар не создаётся второй раз.</summary>
public sealed class InvoiceEditorWindow : Window
{
    /// <summary>2026-10-19: черновик закупки по накладной — живёт в карточке советника между открытиями окна.</summary>
    public sealed class PurchaseDraft
    {
        public List<DraftRow> Rows { get; } = new();
        public PurchaseReceivingService.Supplier? Supplier { get; set; }
        public bool PaidNow { get; set; } = true;
        /// <summary>2026-10-20, этап П3: «В долг» — срок оплаты поставщику, дней (дата оплаты = день прихода + срок).</summary>
        public int DebtDays { get; set; } = 30;
        /// <summary>Поставщик, прочитанный ИИ в накладной (ProductActionPlan.SupplierHint), — только подсказка.</summary>
        public string? InvoiceSupplierName { get; init; }
        public string? InvoiceSupplierInn { get; init; }
        public List<PurchaseReceivingService.Supplier>? Suppliers { get; set; }
        /// <summary>Повторы строк, объединённые при разборе (показать владельцу).</summary>
        public List<string> MergeNotes { get; } = new();
        /// <summary>Итоги проведённых частей закупки (для карточки советника).</summary>
        public List<string> PostedSummaries { get; } = new();
        public bool Posting { get; set; }
        public bool Completed => PostedSummaries.Count > 0 && Rows.Count == 0;

        public static PurchaseDraft FromSteps(IEnumerable<ProductActionPlan.Step> steps, string? supplierName, string? supplierInn)
        {
            var draft = new PurchaseDraft { InvoiceSupplierName = supplierName, InvoiceSupplierInn = supplierInn };
            foreach (var step in steps.Where(ProductActionPlan.IsPurchaseStep))
            {
                var qty = step.Qty ?? 0;
                if (step.Op == "receive" && step.Product.Id is { Length: > 0 } id)
                {
                    var tile = ProductActions.Find(id) is { } fresh && string.Equals(fresh.Id, id, StringComparison.OrdinalIgnoreCase) ? fresh : step.Product;
                    // Повтор того же товара в накладной — одна строка (количество складывается, цены — по первой).
                    if (draft.Rows.FirstOrDefault(r => r.Existing is { } e && string.Equals(e.Id, tile.Id, StringComparison.OrdinalIgnoreCase)) is { } same)
                    {
                        same.Qty = Num(ParseOr(same.Qty) + qty, "0.###");
                        draft.MergeNotes.Add($"«{tile.Title}» → {same.Qty}");
                        continue;
                    }
                    var price = step.Price ?? (tile.PriceValue > 0 ? tile.PriceValue : null);
                    draft.Rows.Add(new DraftRow
                    {
                        Line = PurchaseReceivingService.FromTile(tile, qty),
                        Existing = tile,
                        InvoiceName = tile.Title,
                        Barcode = tile.Barcode ?? "",
                        Unit = string.IsNullOrWhiteSpace(tile.Unit) ? "шт" : tile.Unit!,
                        Qty = Num(qty, "0.###"),
                        Purchase = Num(step.Purchase, "0.##"),
                        Price = Num(price, "0.##"),
                    });
                    continue;
                }
                // 2026-10-19, ревью: «Кг.», «килограмм», «kg» → «кг» (весовой, ProductUnitNormalizer.IsKilogramWord).
                var unit = ProductUnitNormalizer.IsKilogramWord(step.Unit) ? "кг" : string.IsNullOrWhiteSpace(step.Unit) ? "шт" : step.Unit!.Trim();
                var key = NameKey(step.Product.Title);
                if (draft.Rows.FirstOrDefault(r => r.Existing is null && NameKey(r.InvoiceName) == key
                        && string.Equals(NameKey(r.Unit), NameKey(unit), StringComparison.Ordinal)
                        && (r.Barcode.Length == 0 || string.IsNullOrWhiteSpace(step.Barcode) || string.Equals(r.Barcode, step.Barcode.Trim(), StringComparison.OrdinalIgnoreCase))) is { } dup)
                {
                    dup.Qty = Num(ParseOr(dup.Qty) + qty, "0.###");
                    if (dup.Barcode.Length == 0 && !string.IsNullOrWhiteSpace(step.Barcode))
                        dup.Barcode = step.Barcode.Trim();
                    draft.MergeNotes.Add($"«{step.Product.Title}» → {dup.Qty}");
                    continue;
                }
                draft.Rows.Add(new DraftRow
                {
                    Line = new ReceivingLineVm { Source = ReceivingSource.Unknown, Unit = unit },
                    InvoiceName = step.Product.Title,
                    Barcode = step.Barcode?.Trim() ?? "",
                    Unit = unit,
                    Category = step.Category,
                    Qty = Num(qty, "0.###"),
                    Purchase = Num(step.Purchase, "0.##"),
                    Price = Num(step.Price, "0.##"),
                });
            }
            // 2026-10-19, ревью: та же накладная уже частично (или целиком) проведена — такие строки помечаются «уже проведено»
            // и без второго подтверждения не проводятся (PostAsync).
            foreach (var row in draft.Rows)
                row.PostedBefore = PostedLines.Find(null, row.Existing?.Id, new[] { row.InvoiceName }, ParseOr(row.Qty), ParseOr(row.Purchase));
            return draft;
        }
    }

    /// <summary>2026-10-19, ревью: реестр проведённых строк закупок по накладным (в памяти процесса, 24 часа): поставщик, итоговый
    /// товар (id — и у созданного), названия, количество и цена закупки. По нему: та же строка второй раз (второе фото той же
    /// накладной, новая карточка после частичного проведения) проводится только после предупреждения со списком и второго нажатия.</summary>
    public static class PostedLines
    {
        public sealed record Entry(string SupplierId, string SupplierName, string ProductId, IReadOnlyCollection<string> NameKeys, string Name,
            double Qty, double Purchase, DateTime At);

        private static readonly List<Entry> Entries = new();

        public static void Remember(PurchaseReceivingService.Supplier supplier, IEnumerable<(ReceivingLineVm Line, string InvoiceName)> lines)
        {
            lock (Entries)
            {
                Entries.RemoveAll(e => DateTime.Now - e.At > TimeSpan.FromDays(1));
                foreach (var (line, invoiceName) in lines.Where(x => !string.IsNullOrWhiteSpace(x.Line.ProductId)))
                {
                    var keys = new[] { line.ProductName, line.OriginalName, invoiceName }.Select(NameKey).Where(k => k.Length > 0).Distinct().ToList();
                    Entries.Add(new Entry(supplier.Id, supplier.Name, line.ProductId!, keys, line.ProductName, line.Quantity, line.PurchasePrice, DateTime.Now));
                }
            }
        }

        /// <param name="supplierId">null — у любого поставщика.</param>
        public static Entry? Find(string? supplierId, string? productId, IEnumerable<string?> names, double qty, double purchase)
        {
            if (qty <= 0)
                return null;
            var keys = names.Select(NameKey).Where(k => k.Length > 0).ToList();
            lock (Entries)
            {
                return Entries.LastOrDefault(e => DateTime.Now - e.At <= TimeSpan.FromDays(1)
                    && (supplierId is null || e.SupplierId == supplierId)
                    && Math.Abs(e.Qty - qty) < 0.0005 && Math.Abs(e.Purchase - purchase) < 0.005
                    && ((!string.IsNullOrWhiteSpace(productId) && string.Equals(e.ProductId, productId, StringComparison.OrdinalIgnoreCase))
                        || keys.Any(e.NameKeys.Contains)));
            }
        }
    }

    /// <summary>2026-10-19: строка закупки — тексты ячеек и строка приёмки (ReceivingLineVm), которая уходит в PostAsync. Строка
    /// приёмки одна на всё время черновика: созданный при прошлой попытке товар (ProductId) второй раз не создаётся.</summary>
    public sealed class DraftRow
    {
        public required ReceivingLineVm Line { get; set; }
        /// <summary>Товар склада (приход к нему); null — новый товар.</summary>
        public CatalogProductTileVm? Existing { get; set; }
        public string InvoiceName { get; set; } = "";
        public string ReceiptName { get; set; } = "";
        public string Barcode { get; set; } = "";
        public string Unit { get; set; } = "шт";
        public string? Category { get; set; }
        public string Qty { get; set; } = "";
        public string Purchase { get; set; } = "";
        public string Price { get; set; } = "";
        /// <summary>Новый товар уже создан на сервере прошлой попыткой — название и штрихкод не меняются.</summary>
        public bool CreatedEarlier => Existing is null && !string.IsNullOrWhiteSpace(Line.ProductId);
        /// <summary>2026-10-19, ревью: такая же строка (товар, количество, цена) недавно проведена закупкой — показать «уже проведено».</summary>
        public PostedLines.Entry? PostedBefore { get; set; }
    }

    private readonly PurchaseDraft _draft;
    // Колонки наименований — резиновые, но не уже 130: таблица подстраивается под ширину окна (1024 — всё видно без прокрутки вбок).
    private readonly Grid _grid = new()
    {
        ColumnDefinitions =
        {
            new ColumnDefinition(32, GridUnitType.Pixel), new ColumnDefinition(96, GridUnitType.Pixel),
            new ColumnDefinition(2, GridUnitType.Star) { MinWidth = 130 }, new ColumnDefinition(2, GridUnitType.Star) { MinWidth = 130 },
            new ColumnDefinition(132, GridUnitType.Pixel), new ColumnDefinition(70, GridUnitType.Pixel), new ColumnDefinition(88, GridUnitType.Pixel),
            new ColumnDefinition(88, GridUnitType.Pixel), new ColumnDefinition(94, GridUnitType.Pixel), new ColumnDefinition(44, GridUnitType.Pixel),
        },
    };
    private readonly List<TextBox> _navBoxes = new();
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false, LineHeight = 19 };
    private readonly TextBlock _total = new() { FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _supplierSelected = new() { FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _supplierStatus = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _needSupplier = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DockPanel _matchPanel = new() { IsVisible = false };
    private readonly TextBox _supplierSearch;
    private readonly ListBox _supplierList = new() { MaxHeight = 104, MinHeight = 0 };
    private readonly StackPanel _supplierPicker = new() { Spacing = 6 };
    private readonly Button _changeSupplier;
    private readonly Button _post;
    private readonly Button _close;
    private readonly RadioButton _paidNow;
    private readonly RadioButton _debt;
    // 2026-10-20, этап П3: срок оплаты долга поставщику (только при «В долг»).
    private readonly TextBox _debtDays;
    private string? _repeatConfirmedFor;
    private IDisposable? _supplierSelectedBrush;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private static string Num(double? v, string format) => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) : "";

    private static bool TryNum(string? text, out double value) =>
        double.TryParse((text ?? "").Trim().Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);

    private static double ParseOr(string? text) => TryNum(text, out var v) ? v : 0;

    private static string NameKey(string? name) => System.Text.RegularExpressions.Regex.Replace((name ?? "").Trim().ToLowerInvariant(), @"\s+", " ");

    private static void Res(AvaloniaObject target, AvaloniaProperty property, string key)
    {
        if (target is Control c)
            c.Bind(property, c.GetResourceObservable(key));
    }

    public InvoiceEditorWindow(PurchaseDraft draft)
    {
        _draft = draft;
        Title = T("Закупка по накладной", "Накладной боюнча сатып алуу", "Purchase from invoice", "Faturadan alım", "Yuk xati bo'yicha xarid");
        Width = 1120;
        Height = 700;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Res(this, BackgroundProperty, "BrushPanel");

        var hint = new TextBlock
        {
            Text = T("Накладная проводится закупкой у поставщика — как «Приёмка» склада: остаток придёт документом закупки, цены закупки — из накладной. Поправьте то, что ИИ прочитал неверно. «Наименование в чеке» пустое — останется наименование с накладной.",
                "Накладной жеткирүүчүдөн сатып алуу менен өткөрүлөт — кампанын «Кабыл алуусу» сыяктуу: калдык сатып алуу документи менен келет, сатып алуу баалары — накладнойдон. ИИ туура эмес окуганын оңдоңуз. «Чектеги аталышы» бош болсо — накладнойдогу аталыш калат.",
                "The invoice is posted as a purchase from a supplier — like warehouse “Receiving”: stock arrives with the purchase document, purchase prices come from the invoice. Fix what the AI read wrong. If “Name on receipt” is empty, the invoice name stays.",
                "Fatura tedarikçiden alım olarak işlenir — depodaki «Mal kabul» gibi: stok alım belgesiyle gelir, alış fiyatları faturadan alınır. Yapay zekânın yanlış okuduklarını düzeltin. «Fişteki ad» boşsa faturadaki ad kalır.",
                "Yuk xati yetkazib beruvchidan xarid sifatida o'tkaziladi — ombordagi «Qabul» kabi: qoldiq xarid hujjati bilan keladi, xarid narxlari yuk xatidan olinadi. SI noto'g'ri o'qiganini tuzating. «Chekdagi nom» bo'sh bo'lsa, yuk xatidagi nom qoladi."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), LineHeight = 19,
        };
        Res(hint, TextBlock.ForegroundProperty, "BrushTextSoft");

        // ── поставщик ──
        var supplierLabel = new TextBlock { Text = T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Res(supplierLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        _changeSupplier = UiKit.Ghost(this, T("Сменить", "Алмаштыруу", "Change", "Değiştir", "O'zgartirish"));
        _changeSupplier.Height = 34;
        _changeSupplier.Click += (_, _) =>
        {
            _supplierPicker.IsVisible = true;
            _changeSupplier.IsVisible = false;
            _supplierList.SelectedIndex = -1;
            _supplierSearch?.Focus();
        };
        var refresh = UiKit.Ghost(this, T("⟳ Обновить список", "⟳ Тизмени жаңыртуу", "⟳ Refresh list", "⟳ Listeyi yenile", "⟳ Ro'yxatni yangilash"));
        refresh.Height = 34;
        refresh.Click += async (_, _) => await LoadSuppliersAsync(force: true).ConfigureAwait(true);
        var supplierHead = new DockPanel { LastChildFill = true };
        var headRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _changeSupplier, refresh } };
        DockPanel.SetDock(headRight, Dock.Right);
        supplierHead.Children.Add(headRight);
        supplierLabel.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(supplierLabel, Dock.Left);
        supplierHead.Children.Add(supplierLabel);
        supplierHead.Children.Add(_supplierSelected);

        _supplierSearch = UiKit.Input(this, T("Найти поставщика по названию", "Жеткирүүчүнү аты боюнча табуу", "Find a supplier by name", "Tedarikçiyi ada göre bul", "Yetkazib beruvchini nomi bo'yicha topish"), 38);
        _supplierSearch.TextChanged += (_, _) => FillSupplierList();
        _supplierSearch.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            // Один найденный — его и выбираем; несколько — к списку (выбирает владелец).
            var items = _supplierList.Items.OfType<ListBoxItem>().ToList();
            if (items.Count == 1 && items[0].Tag is PurchaseReceivingService.Supplier only)
                SelectSupplier(only);
            else if (items.Count > 1)
            {
                _supplierList.SelectedIndex = -1;
                items[0].Focus();
            }
        };
        // Выбор — нажатием (мышь, касание) или Enter; стрелки только листают список (поставщик не выбирается сам по ходу).
        _supplierList.AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (_supplierList.SelectedItem is ListBoxItem { Tag: PurchaseReceivingService.Supplier s })
                SelectSupplier(s);
        }), RoutingStrategies.Bubble, handledEventsToo: true);
        _supplierList.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { Tag: PurchaseReceivingService.Supplier s })
                SelectSupplier(s);
            else if (_supplierList.SelectedItem is ListBoxItem { Tag: PurchaseReceivingService.Supplier selected })
                SelectSupplier(selected);
        };
        var notFound = new TextBlock
        {
            Text = T("Нет нужного поставщика? Добавьте его в NurCRM (Клиенты → Поставщики) и нажмите «Обновить список».",
                "Керектүү жеткирүүчү жокпу? Аны NurCRM'ге кошуңуз (Кардарлар → Жеткирүүчүлөр) жана «Тизмени жаңыртуу» баскычын басыңыз.",
                "Supplier not in the list? Add it in NurCRM (Clients → Suppliers) and press “Refresh list”.",
                "Tedarikçi listede yok mu? NurCRM'de ekleyin (Müşteriler → Tedarikçiler) ve «Listeyi yenile»ye basın.",
                "Kerakli yetkazib beruvchi yo'qmi? Uni NurCRM'ga qo'shing (Mijozlar → Yetkazib beruvchilar) va «Ro'yxatni yangilash»ni bosing."),
            FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
        };
        Res(notFound, TextBlock.ForegroundProperty, "BrushTextSoft");
        Res(_supplierStatus, TextBlock.ForegroundProperty, "BrushTextSoft");
        _supplierPicker.Children.Add(_supplierSearch);
        _supplierPicker.Children.Add(_supplierList);
        _supplierPicker.Children.Add(_supplierStatus);
        _supplierPicker.Children.Add(notFound);

        // ── оплата ──
        _paidNow = new RadioButton
        {
            GroupName = "invoice-pay", IsChecked = draft.PaidNow, VerticalContentAlignment = VerticalAlignment.Center,
            Content = T("Оплачено сразу (из кассы расходов)", "Дароо төлөндү (чыгым кассасынан)", "Paid now (from the expense cashbox)", "Hemen ödendi (gider kasasından)", "Darhol to'landi (xarajat kassasidan)"),
        };
        _debt = new RadioButton
        {
            GroupName = "invoice-pay", IsChecked = !draft.PaidNow, VerticalContentAlignment = VerticalAlignment.Center,
            Content = T("В долг поставщику", "Жеткирүүчүгө карызга", "On credit from the supplier", "Tedarikçiye borç", "Yetkazib beruvchiga qarzga"),
        };
        // 2026-10-20, этап П3: при «В долг» — срок оплаты, дней (после проведения касса записывает долг поставщику, как сайт).
        _debtDays = new TextBox { Text = draft.DebtDays.ToString(CultureInfo.InvariantCulture), Width = 72, MaxLength = 4, HorizontalContentAlignment = HorizontalAlignment.Right };
        var debtDaysLabel = new TextBlock { Text = T("Срок оплаты, дней", "Төлөө мөөнөтү, күн", "Pay within, days", "Ödeme vadesi, gün", "To'lov muddati, kun"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        Res(debtDaysLabel, TextBlock.ForegroundProperty, "BrushText");
        Avalonia.Automation.AutomationProperties.SetName(_debtDays, debtDaysLabel.Text);
        var debtDaysRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(28, 2, 0, 0), IsVisible = !draft.PaidNow, Children = { debtDaysLabel, _debtDays } };
        _debtDays.TextChanged += (_, _) =>
        {
            if (int.TryParse((_debtDays.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650)
                _draft.DebtDays = days;
        };
        _paidNow.IsCheckedChanged += (_, _) => { _draft.PaidNow = _paidNow.IsChecked == true; debtDaysRow.IsVisible = !_draft.PaidNow; UpdateTotal(); };
        _debt.IsCheckedChanged += (_, _) => { _draft.PaidNow = _debt.IsChecked != true; debtDaysRow.IsVisible = !_draft.PaidNow; UpdateTotal(); };
        var payLabel = new TextBlock { Text = T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), FontSize = 13, FontWeight = FontWeight.SemiBold };
        Res(payLabel, TextBlock.ForegroundProperty, "BrushTextSoft");
        var payPanel = new StackPanel { Spacing = 4, Children = { payLabel, _paidNow, _debt, debtDaysRow } };

        var supplierCol = new StackPanel { Spacing = 8, Children = { supplierHead, _matchPanel, _supplierPicker } };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(supplierCol);
        var paySep = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(16, 0, 0, 0), Margin = new Thickness(16, 0, 0, 0), Child = payPanel, VerticalAlignment = VerticalAlignment.Top };
        Res(paySep, Border.BorderBrushProperty, "BrushBorder");
        Grid.SetColumn(paySep, 1);
        top.Children.Add(paySep);
        var topCard = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 10), Margin = new Thickness(0, 0, 0, 10), Child = top };
        Res(topCard, Border.BorderBrushProperty, "BrushBorder");
        Res(topCard, Border.BackgroundProperty, "BrushPanelSoft");

        // ── строки ──
        var addRow = UiKit.Ghost(this, T("+ Добавить строку", "+ Сап кошуу", "+ Add a line", "+ Satır ekle", "+ Qator qo'shish"));
        addRow.Height = 36;
        addRow.HorizontalAlignment = HorizontalAlignment.Left;
        addRow.Margin = new Thickness(4, 8, 0, 0);
        addRow.Click += (_, _) =>
        {
            var row = new DraftRow { Line = new ReceivingLineVm { Source = ReceivingSource.Unknown } };
            _draft.Rows.Add(row);
            RebuildRows();
            UpdateTotal();
            // Курсор — в наименование новой строки.
            Dispatcher.UIThread.Post(() => _navBoxes.FirstOrDefault(b => ReferenceEquals(b.Tag, row))?.Focus(), DispatcherPriority.Background);
        };
        var rowsPanel = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Children = { _grid, addRow } };
        if (draft.MergeNotes.Count > 0)
        {
            var merged = new TextBlock
            {
                Text = T("Повторяющиеся строки накладной объединены: ", "Накладнойдун кайталанган саптары бириктирилди: ", "Repeated invoice lines were merged: ",
                    "Faturadaki yinelenen satırlar birleştirildi: ", "Yuk xatidagi takroriy qatorlar birlashtirildi: ") + string.Join(", ", draft.MergeNotes),
                FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 6, 4, 0),
            };
            Res(merged, TextBlock.ForegroundProperty, "BrushWarning");
            rowsPanel.Children.Add(merged);
        }
        var scroll = new ScrollViewer { Content = rowsPanel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        // ── низ ──
        Res(_error, TextBlock.ForegroundProperty, "BrushDanger");
        Res(_total, TextBlock.ForegroundProperty, "BrushText");
        Res(_needSupplier, TextBlock.ForegroundProperty, "BrushWarning");
        _post = UiKit.Primary(this, T("Провести закупку", "Сатып алууну өткөрүү", "Post purchase", "Alımı işle", "Xaridni o'tkazish"));
        _post.Height = 42;
        _post.Click += async (_, _) => await PostAsync().ConfigureAwait(true);
        _close = UiKit.Ghost(this, T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        _close.Height = 42;
        _close.Click += (_, _) => { if (!_draft.Posting) Close(null); };
        var bottomRight = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _needSupplier, _post, _close } };
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(bottomRight, Dock.Right);
        bottom.Children.Add(bottomRight);
        bottom.Children.Add(_total);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"), Margin = new Thickness(20, 16, 20, 16) };
        Grid.SetRow(hint, 0);
        Grid.SetRow(topCard, 1);
        Grid.SetRow(scroll, 2);
        Grid.SetRow(_error, 3);
        Grid.SetRow(bottom, 4);
        root.Children.Add(hint);
        root.Children.Add(topCard);
        root.Children.Add(scroll);
        root.Children.Add(_error);
        root.Children.Add(bottom);
        Content = root;

        RebuildRows();
        ShowSupplier();
        UpdateTotal();
        EscapeKey.Attach(this, closeWindow: () => { if (!_draft.Posting) Close(null); });
        // Пока идёт проведение — окно не закрывается (итог должен дойти до черновика и карточки).
        Closing += (_, e) =>
        {
            if (_draft.Posting)
                e.Cancel = true;
        };
        Opened += async (_, _) =>
        {
            this.FitToScreen();
            await LoadSuppliersAsync(force: false).ConfigureAwait(true);
        };
    }

    // ------------------------------------------------------------------ поставщик

    private async Task LoadSuppliersAsync(bool force)
    {
        if (_draft.Suppliers is not null && !force)
        {
            FillSupplierList();
            return;
        }
        _supplierStatus.Text = T("Загружаю список поставщиков…", "Жеткирүүчүлөрдүн тизмеси жүктөлүүдө…", "Loading suppliers…", "Tedarikçiler yükleniyor…", "Yetkazib beruvchilar yuklanmoqda…");
        _supplierStatus.IsVisible = true;
        try
        {
            _draft.Suppliers = await PurchaseReceivingService.Instance.LoadSuppliersAsync().ConfigureAwait(true);
            _supplierStatus.IsVisible = false;
            // Выбранный поставщик пропал из списка (удалили в NurCRM) — выбор снимается.
            if (_draft.Supplier is { } chosen && !_draft.Suppliers.Any(s => s.Id == chosen.Id))
                _draft.Supplier = null;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупка по накладной: список поставщиков не загружен: {ex.Message}", "WARNING");
            _supplierStatus.Text = T("Список поставщиков не загрузился: ", "Жеткирүүчүлөрдүн тизмеси жүктөлгөн жок: ", "The supplier list didn't load: ",
                "Tedarikçi listesi yüklenemedi: ", "Yetkazib beruvchilar ro'yxati yuklanmadi: ") + ex.Message
                + T(" — нажмите «Обновить список».", " — «Тизмени жаңыртуу» баскычын басыңыз.", " — press “Refresh list”.", " — «Listeyi yenile»ye basın.", " — «Ro'yxatni yangilash»ni bosing.");
            _supplierStatus.IsVisible = true;
        }
        FillSupplierList();
        ShowSupplier();
    }

    private void FillSupplierList()
    {
        var query = (_supplierSearch.Text ?? "").Trim();
        var all = _draft.Suppliers ?? new List<PurchaseReceivingService.Supplier>();
        var found = query.Length == 0
            ? all
            : all.Where(s => s.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || (s.Inn ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        _supplierList.Items.Clear();
        foreach (var s in found.Take(200))
        {
            var text = new TextBlock { Text = s.Name + (string.IsNullOrWhiteSpace(s.Inn) ? "" : $"  · {T("ИНН", "ИНН", "TIN", "VKN", "STIR")} {s.Inn}"), TextTrimming = TextTrimming.CharacterEllipsis };
            _supplierList.Items.Add(new ListBoxItem { Content = text, Tag = s, Padding = new Thickness(10, 6), MinHeight = 34 });
        }
        _supplierList.IsVisible = found.Count > 0;
        if (_draft.Suppliers is not null && found.Count == 0)
        {
            _supplierStatus.Text = all.Count == 0
                ? T("В NurCRM пока нет поставщиков.", "NurCRM'де азырынча жеткирүүчүлөр жок.", "There are no suppliers in NurCRM yet.", "NurCRM'de henüz tedarikçi yok.", "NurCRM'da hali yetkazib beruvchilar yo'q.")
                : T($"По «{query}» поставщик не найден.", $"«{query}» боюнча жеткирүүчү табылган жок.", $"No supplier matches “{query}”.", $"«{query}» için tedarikçi bulunamadı.", $"«{query}» bo'yicha yetkazib beruvchi topilmadi.");
            _supplierStatus.IsVisible = true;
        }
        else if (_draft.Suppliers is not null)
            _supplierStatus.IsVisible = false;
    }

    private void SelectSupplier(PurchaseReceivingService.Supplier supplier)
    {
        _draft.Supplier = supplier;
        _repeatConfirmedFor = null;
        PosLogger.Log($"Закупка по накладной: выбран поставщик «{supplier.Name}».", "STOCK");
        ShowSupplier();
        // К строкам: первое поле первой строки.
        Dispatcher.UIThread.Post(() => _navBoxes.FirstOrDefault()?.Focus(), DispatcherPriority.Background);
    }

    private void ShowSupplier()
    {
        var chosen = _draft.Supplier;
        _supplierSelected.Text = chosen is null
            ? T("не выбран", "тандалган жок", "not selected", "seçilmedi", "tanlanmagan")
            : "«" + chosen.Name + "»";
        _supplierSelectedBrush?.Dispose();
        _supplierSelectedBrush = _supplierSelected.Bind(TextBlock.ForegroundProperty, _supplierSelected.GetResourceObservable(chosen is null ? "BrushWarning" : "BrushText"));
        _supplierPicker.IsVisible = chosen is null;
        _changeSupplier.IsVisible = chosen is not null;

        // Поставщик из накладной — подсказка «Похоже: …» с кнопкой; сам не выбирается (подтверждает владелец).
        _matchPanel.Children.Clear();
        _matchPanel.IsVisible = false;
        if (chosen is null && (_draft.InvoiceSupplierName is { Length: > 0 } || _draft.InvoiceSupplierInn is { Length: > 0 }))
        {
            var invoiceText = T("В накладной: ", "Накладнойдо: ", "On the invoice: ", "Faturada: ", "Yuk xatida: ")
                              + "«" + (_draft.InvoiceSupplierName ?? "?") + "»"
                              + (string.IsNullOrWhiteSpace(_draft.InvoiceSupplierInn) ? "" : $" ({T("ИНН", "ИНН", "TIN", "VKN", "STIR")} {_draft.InvoiceSupplierInn})");
            var match = _draft.Suppliers is { } list ? PurchaseReceivingService.MatchSupplier(list, _draft.InvoiceSupplierName, _draft.InvoiceSupplierInn) : null;
            var info = new TextBlock
            {
                Text = invoiceText + (match is null
                    ? _draft.Suppliers is null ? "" : T(" — в списке NurCRM не найден.", " — NurCRM тизмесинде табылган жок.", " — not found in the NurCRM list.", " — NurCRM listesinde bulunamadı.", " — NurCRM ro'yxatida topilmadi.")
                    : T(". Похоже на:", ". Окшошу:", ". Looks like:", ". Benzer:", ". O'xshashi:")),
                FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            Res(info, TextBlock.ForegroundProperty, "BrushText");
            if (match is not null)
            {
                var pick = UiKit.Primary(this, T($"Выбрать «{match.Name}»", $"«{match.Name}» тандоо", $"Choose “{match.Name}”", $"«{match.Name}» seç", $"«{match.Name}»ni tanlash"));
                pick.Height = 34;
                pick.FontSize = 13;
                pick.Margin = new Thickness(8, 0, 0, 0);
                pick.MaxWidth = 320;
                pick.Click += (_, _) => SelectSupplier(match);
                DockPanel.SetDock(pick, Dock.Right);
                _matchPanel.Children.Add(pick);
            }
            _matchPanel.Children.Add(info);
            _matchPanel.IsVisible = true;
        }
        UpdatePostState();
    }

    // ------------------------------------------------------------------ строки

    private void RebuildRows()
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _navBoxes.Clear();
        string[] headers =
        {
            "№", T("Действие", "Аракет", "Action", "İşlem", "Amal"),
            T("Наименование с накладной", "Накладнойдогу аталышы", "Name on the invoice", "Faturadaki ad", "Yuk xatidagi nom"),
            T("Наименование в чеке", "Чектеги аталышы", "Name on the receipt", "Fişteki ad", "Chekdagi nom"),
            T("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod"),
            T("Кол-во", "Саны", "Qty", "Miktar", "Miqdor"),
            T("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid"),
            T("Продажа", "Сатуу", "Sale price", "Satış", "Sotuv"),
            T("Сумма", "Сумма", "Amount", "Tutar", "Summa"),
            "",
        };
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var c = 0; c < headers.Length; c++)
        {
            var h = new TextBlock { Text = headers[c], FontWeight = FontWeight.Bold, FontSize = 12.5, Margin = new Thickness(4, 0, 4, 8), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Bottom };
            if (c >= 5 && c <= 8)
                h.TextAlignment = TextAlignment.Right;
            Res(h, TextBlock.ForegroundProperty, "BrushTextSoft");
            Grid.SetColumn(h, c);
            _grid.Children.Add(h);
        }

        var number = 0;
        foreach (var row in _draft.Rows)
        {
            number++;
            var r = _grid.RowDefinitions.Count;
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TextBox Box(string text, Action<string> store, string? watermark = null, bool right = false, bool readOnly = false)
            {
                var box = new TextBox
                {
                    Text = text, Watermark = watermark, MinHeight = 36, Margin = new Thickness(4, 3), IsReadOnly = readOnly, Tag = row,
                    HorizontalContentAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center,
                };
                if (readOnly)
                {
                    box.Focusable = false;
                    box.Opacity = 0.75;
                    if (text.Length > 0)
                        ToolTip.SetTip(box, text); // на узком экране название обрезано — целиком в подсказке
                }
                else
                {
                    box.TextChanged += (_, _) =>
                    {
                        store(box.Text ?? "");
                        _repeatConfirmedFor = null;
                    };
                    box.KeyDown += NavKeyDown;
                    _navBoxes.Add(box);
                }
                return box;
            }
            var existing = row.Existing is not null;
            var invoiceName = Box(row.InvoiceName, v => row.InvoiceName = v, T("Наименование", "Аталышы", "Name", "Ad", "Nomi"), readOnly: existing || row.CreatedEarlier);
            var receiptName = Box(row.ReceiptName, v => row.ReceiptName = v, existing ? row.Existing!.Title : string.IsNullOrWhiteSpace(row.InvoiceName) ? null : row.InvoiceName,
                readOnly: row.CreatedEarlier);
            invoiceName.TextChanged += (_, _) => receiptName.Watermark = string.IsNullOrWhiteSpace(invoiceName.Text) ? null : invoiceName.Text;
            var barcode = Box(row.Barcode, v => row.Barcode = v.Trim(), existing ? "" : T("скан или вписать", "скан же жазуу", "scan or type", "okut veya yaz", "skan yoki yozing"),
                readOnly: existing || row.CreatedEarlier);
            var qty = Box(row.Qty, v => row.Qty = v, right: true);
            var purchase = Box(row.Purchase, v => row.Purchase = v, right: true);
            var price = Box(row.Price, v => row.Price = v, right: true);
            var sum = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(4, 0, 6, 0), FontWeight = FontWeight.SemiBold };
            Res(sum, TextBlock.ForegroundProperty, "BrushText");
            void Sum() => sum.Text = TryNum(row.Qty, out var q) && TryNum(row.Purchase, out var p) ? (q * p).ToString("N2", CultureInfo.CurrentCulture) : "—";
            Sum();
            qty.TextChanged += (_, _) => { Sum(); UpdateTotal(); };
            purchase.TextChanged += (_, _) => { Sum(); UpdateTotal(); };
            var remove = UiKit.Ghost(this, "✕");
            remove.Classes.Add("danger");
            remove.Width = 34;
            remove.Height = 34;
            remove.Padding = new Thickness(0);
            remove.Margin = new Thickness(4, 0, 0, 0);
            Avalonia.Automation.AutomationProperties.SetName(remove, T("Удалить строку", "Сапты өчүрүү", "Remove the line", "Satırı sil", "Qatorni o'chirish"));
            ToolTip.SetTip(remove, T("Удалить строку", "Сапты өчүрүү", "Remove the line", "Satırı sil", "Qatorni o'chirish"));
            remove.Click += (_, _) =>
            {
                if (_draft.Posting)
                    return;
                _draft.Rows.Remove(row);
                _repeatConfirmedFor = null;
                RebuildRows();
                UpdateTotal();
            };
            var action = new TextBlock
            {
                Text = existing ? T("Приход", "Кириш", "Receipt", "Giriş", "Kirim")
                    : row.CreatedEarlier ? T("Создан, ждёт прихода", "Түзүлдү, кириш күтүүдө", "Created, awaiting receipt", "Oluşturuldu, giriş bekliyor", "Yaratildi, kirim kutilmoqda")
                    : T("Новый товар", "Жаңы товар", "New product", "Yeni ürün", "Yangi mahsulot"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0), TextWrapping = TextWrapping.Wrap, FontSize = 12.5,
            };
            Res(action, TextBlock.ForegroundProperty, row.PostedBefore is null ? "BrushText" : "BrushWarning");
            if (!existing)
                action.FontWeight = FontWeight.SemiBold;
            if (row.PostedBefore is { } postedBefore)
                action.Text = T($"⚠ Проведено {postedBefore.At:HH:mm}", $"⚠ {postedBefore.At:HH:mm} өткөрүлгөн", $"⚠ Posted {postedBefore.At:HH:mm}",
                    $"⚠ {postedBefore.At:HH:mm} işlendi", $"⚠ {postedBefore.At:HH:mm} da o'tkazilgan");
            var no = new TextBlock { Text = number.ToString(CultureInfo.InvariantCulture), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0) };
            Res(no, TextBlock.ForegroundProperty, "BrushTextSoft");
            var cells = new Control[] { no, action, invoiceName, receiptName, barcode, qty, purchase, price, sum, remove };
            for (var c = 0; c < cells.Length; c++)
            {
                Grid.SetRow(cells[c], r);
                Grid.SetColumn(cells[c], c);
                _grid.Children.Add(cells[c]);
            }
        }
        if (_draft.Rows.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = T("Строк нет — добавьте строку.", "Саптар жок — сап кошуңуз.", "No lines — add one.", "Satır yok — satır ekleyin.", "Qatorlar yo'q — qator qo'shing."),
                Margin = new Thickness(4, 6), FontSize = 13,
            };
            Res(empty, TextBlock.ForegroundProperty, "BrushTextSoft");
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, 10);
            _grid.Children.Add(empty);
        }
        UpdatePostState();
    }

    /// <summary>2026-10-19: Enter — к следующему полю (по строкам слева направо), последнее поле остаётся на месте. Enter всегда
    /// обработан здесь: кнопок по умолчанию в окне нет, закупка проводится только кнопкой.</summary>
    private void NavKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (sender is not TextBox box)
            return;
        var i = _navBoxes.IndexOf(box);
        if (i >= 0 && i + 1 < _navBoxes.Count)
        {
            var next = _navBoxes[i + 1];
            next.Focus();
            next.SelectAll();
            next.BringIntoView();
        }
    }

    private void UpdateTotal()
    {
        var count = _draft.Rows.Count;
        var sum = _draft.Rows.Sum(r => TryNum(r.Qty, out var q) && TryNum(r.Purchase, out var p) ? q * p : 0);
        var pay = _draft.PaidNow
            ? T("оплачено сразу", "дароо төлөндү", "paid now", "hemen ödendi", "darhol to'landi")
            : T("в долг поставщику", "жеткирүүчүгө карызга", "on credit", "tedarikçiye borç", "yetkazib beruvchiga qarzga");
        _total.Text = T($"Итог закупки: {count} поз. на {sum.ToString("N2", CultureInfo.CurrentCulture)} сом — {pay}",
            $"Сатып алуунун жыйынтыгы: {count} позиция, {sum.ToString("N2", CultureInfo.CurrentCulture)} сом — {pay}",
            $"Purchase total: {count} items, {sum.ToString("N2", CultureInfo.CurrentCulture)} som — {pay}",
            $"Alım toplamı: {count} kalem, {sum.ToString("N2", CultureInfo.CurrentCulture)} som — {pay}",
            $"Xarid jami: {count} pozitsiya, {sum.ToString("N2", CultureInfo.CurrentCulture)} so'm — {pay}");
    }

    private void UpdatePostState()
    {
        var noSupplier = _draft.Supplier is null;
        _post.IsEnabled = !noSupplier && !_draft.Posting && _draft.Rows.Count > 0;
        _close.IsEnabled = !_draft.Posting;
        _needSupplier.IsVisible = noSupplier || _draft.Rows.Count == 0;
        _needSupplier.Text = noSupplier
            ? T("Выберите поставщика", "Жеткирүүчүнү тандаңыз", "Choose a supplier", "Tedarikçi seçin", "Yetkazib beruvchini tanlang")
            : T("Добавьте строку", "Сап кошуңуз", "Add a line", "Satır ekleyin", "Qator qo'shing");
        ToolTip.SetTip(_post, noSupplier ? _needSupplier.Text : null);
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = text.Length > 0;
    }

    // ------------------------------------------------------------------ проведение

    /// <summary>2026-10-19: тексты строки → строка приёмки. Новая строка, чей штрихкод или точное название уже есть на складе, —
    /// приход к тому товару (второй товар-дубль не создаётся).</summary>
    private static void ApplyRow(DraftRow row, int n, List<string> problems)
    {
        if (row.Existing is null && string.IsNullOrWhiteSpace(row.Line.ProductId))
        {
            var code = row.Barcode.Trim();
            CatalogProductTileVm? found = null;
            if (code.Length >= 4 && ProductActions.Find(code) is { } byCode && string.Equals((byCode.Barcode ?? "").Trim(), code, StringComparison.OrdinalIgnoreCase))
                found = byCode;
            else if (code.Length == 0 && NameKey(row.InvoiceName) is { Length: > 0 } key && ProductActions.Find(row.InvoiceName.Trim()) is { } byName && NameKey(byName.Title) == key)
                found = byName;
            if (found is not null)
            {
                row.Existing = found;
                row.Line = PurchaseReceivingService.FromTile(found, 0);
                row.Barcode = found.Barcode ?? "";
                if (string.IsNullOrWhiteSpace(row.Price) && found.PriceValue > 0)
                    row.Price = Num(found.PriceValue, "0.##");
                PosLogger.Log($"Закупка по накладной: строка {n} «{row.InvoiceName}» — товар склада «{found.Title}», приход к нему.", "STOCK");
            }
        }

        var line = row.Line;
        if (!TryNum(row.Qty, out var qty) || qty <= 0)
            problems.Add(T($"строка {n}: количество", $"{n}-сап: саны", $"line {n}: quantity", $"{n}. satır: miktar", $"{n}-qator: miqdor"));
        else
            line.Quantity = Math.Round(qty, 3);
        if (!TryNum(row.Purchase, out var purchase) || purchase <= 0)
            problems.Add(T($"строка {n}: цена закупки", $"{n}-сап: сатып алуу баасы", $"line {n}: purchase price", $"{n}. satır: alış fiyatı", $"{n}-qator: xarid narxi"));
        else
            line.PurchasePrice = Math.Round(purchase, 2);
        if (!TryNum(row.Price, out var price) || price <= 0)
            problems.Add(T($"строка {n}: цена продажи", $"{n}-сап: сатуу баасы", $"line {n}: sale price", $"{n}. satır: satış fiyatı", $"{n}-qator: sotuv narxi"));
        else
            line.SalePrice = Math.Round(price, 2);

        var receipt = row.ReceiptName.Trim();
        if (row.Existing is not null)
            line.ProductName = receipt.Length > 0 ? receipt : line.OriginalName;
        else if (!row.CreatedEarlier)
        {
            line.ProductName = receipt.Length > 0 ? receipt : row.InvoiceName.Trim();
            line.Barcode = row.Barcode.Trim();
            line.IsWeight = ProductUnitNormalizer.IsKilogramWord(row.Unit);
            line.Unit = line.IsWeight ? "кг" : row.Unit;
            line.CategoryName = string.IsNullOrWhiteSpace(row.Category) ? null : row.Category;
            if (line.ProductName.Length == 0)
                problems.Add(T($"строка {n}: наименование", $"{n}-сап: аталышы", $"line {n}: name", $"{n}. satır: ad", $"{n}-qator: nomi"));
        }
    }

    private async Task PostAsync()
    {
        if (_draft.Posting)
            return;
        if (_draft.Supplier is not { } supplier)
        {
            ShowError(T("Выберите поставщика — без поставщика закупку провести нельзя.", "Жеткирүүчүнү тандаңыз — жеткирүүчүсүз сатып алууну өткөрүүгө болбойт.",
                "Choose a supplier — a purchase can't be posted without one.", "Tedarikçi seçin — tedarikçisiz alım işlenemez.", "Yetkazib beruvchini tanlang — usiz xaridni o'tkazib bo'lmaydi."));
            return;
        }
        if (_draft.Rows.Count == 0)
            return;

        var problems = new List<string>();
        var n = 0;
        foreach (var row in _draft.Rows)
            ApplyRow(row, ++n, problems);

        // Один товар в двух строках — две строки документа с одним товаром; объединяет владелец (количество — в одной строке).
        var lines = _draft.Rows.Select(r => r.Line).ToList();
        foreach (var g in _draft.Rows.Select((r, i) => (r, i: i + 1))
                     .GroupBy(x => x.r.Existing is { } e ? "id:" + e.Id.ToLowerInvariant()
                         : !string.IsNullOrWhiteSpace(x.r.Line.ProductId) ? "id:" + x.r.Line.ProductId!.ToLowerInvariant()
                         : "new:" + NameKey(x.r.Line.ProductName) + "|" + x.r.Line.Barcode.ToLowerInvariant())
                     .Where(g => g.Count() > 1))
        {
            var nums = string.Join(", ", g.Select(x => x.i));
            var name = g.First().r.Existing?.Title ?? g.First().r.Line.ProductName;
            problems.Add(T($"«{name}» — в строках {nums}: оставьте одну строку с общим количеством",
                $"«{name}» — {nums} саптарда: жалпы саны менен бир сап калтырыңыз",
                $"“{name}” is on lines {nums}: keep one line with the total quantity",
                $"«{name}» {nums} satırlarında: toplam miktarla tek satır bırakın",
                $"«{name}» — {nums} qatorlarda: umumiy miqdor bilan bitta qator qoldiring"));
        }
        if (problems.Count == 0)
            problems.AddRange(PurchaseReceivingService.Validate(lines));
        if (problems.Count > 0)
        {
            RebuildRows();
            var shown = problems.Take(8).ToList();
            if (problems.Count > shown.Count)
                shown.Add(T($"…и ещё {problems.Count - shown.Count}", $"…жана дагы {problems.Count - shown.Count}", $"…and {problems.Count - shown.Count} more",
                    $"…ve {problems.Count - shown.Count} tane daha", $"…va yana {problems.Count - shown.Count}"));
            ShowError(T("Закупку нельзя провести: ", "Сатып алууну өткөрүүгө болбойт: ", "The purchase can't be posted: ", "Alım işlenemiyor: ", "Xaridni o'tkazib bo'lmaydi: ")
                      + string.Join("; ", shown));
            return;
        }

        // 2026-10-19, ревью: повтор — по КАЖДОЙ строке (реестр PostedLines: поставщик + товар + количество + цена закупки, итоговые
        // id и у созданных товаров). Хоть одна строка совпала с проведённой за сутки у этого поставщика (или помечена «уже
        // проведено» при открытии) — предупреждение со списком, провести можно только вторым нажатием.
        var repeats = new List<string>();
        foreach (var row in _draft.Rows)
        {
            var l = row.Line;
            var names = new[] { l.ProductName, l.OriginalName, row.InvoiceName };
            var hit = PostedLines.Find(supplier.Id, l.ProductId, names, l.Quantity, l.PurchasePrice)
                      ?? (row.PostedBefore is not null ? PostedLines.Find(null, l.ProductId, names, l.Quantity, l.PurchasePrice) : null);
            if (hit is not null)
                repeats.Add($"«{(row.Existing?.Title ?? l.ProductName)}» {l.Quantity.ToString("0.###", CultureInfo.CurrentCulture)} × {l.PurchasePrice.ToString("0.##", CultureInfo.CurrentCulture)} — {hit.At:HH:mm}, «{hit.SupplierName}»");
        }
        if (repeats.Count > 0)
        {
            var signature = supplier.Id + "|" + string.Join(";", repeats);
            if (_repeatConfirmedFor != signature)
            {
                _repeatConfirmedFor = signature;
                ShowError(T($"Эти строки уже проведены закупкой за последние сутки ({repeats.Count}): ", $"Бул саптар акыркы суткада сатып алуу менен өткөрүлгөн ({repeats.Count}): ",
                              $"These lines were already posted as a purchase in the last 24 hours ({repeats.Count}): ", $"Bu satırlar son 24 saatte alım olarak işlendi ({repeats.Count}): ",
                              $"Bu qatorlar so'nggi sutkada xarid sifatida o'tkazilgan ({repeats.Count}): ")
                          + string.Join("; ", repeats.Take(8)) + (repeats.Count > 8 ? "; …" : "")
                          + T(". Если это другая поставка — нажмите «Провести закупку» ещё раз; если та же — удалите эти строки (✕).",
                              ". Бул башка жеткирүү болсо — «Сатып алууну өткөрүү» баскычын дагы бир жолу басыңыз; ошол эле болсо — бул саптарды өчүрүңүз (✕).",
                              ". If this is another delivery, press “Post purchase” again; if it is the same one, remove these lines (✕).",
                              ". Başka bir teslimatsa «Alımı işle»ye tekrar basın; aynısıysa bu satırları silin (✕).",
                              ". Bu boshqa yetkazib berish bo'lsa, «Xaridni o'tkazish»ni yana bosing; o'sha bo'lsa, bu qatorlarni o'chiring (✕)."));
                return;
            }
        }

        _draft.Posting = true;
        UpdatePostState();
        _post.Content = T("Провожу закупку…", "Сатып алуу өткөрүлүүдө…", "Posting…", "İşleniyor…", "O'tkazilmoqda…");
        ShowError("");
        var paidNow = _draft.PaidNow;
        PurchaseReceivingService.PostResult result;
        try
        {
            var employee = NurMarketKassa.PosApp.CurrentUserDisplayName ?? NurMarketKassa.PosApp.CurrentUserId;
            PosLogger.Log($"Закупка по накладной: проведение — поставщик «{supplier.Name}», строк {lines.Count}, {(paidNow ? "оплачено сразу" : "в долг")}.", "STOCK");
            result = await PurchaseReceivingService.Instance.PostAsync(lines, supplier, paidNow, employee).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупка по накладной не проведена: {ex}", "WARNING");
            _draft.Posting = false;
            _post.Content = T("Провести закупку", "Сатып алууну өткөрүү", "Post purchase", "Alımı işle", "Xaridni o'tkazish");
            RebuildRows();
            ShowError(T("Не удалось провести закупку: ", "Сатып алуу өткөрүлгөн жок: ", "The purchase failed: ", "Alım işlenemedi: ", "Xaridni o'tkazib bo'lmadi: ") + ex.Message
                      + T(" Нажмите «Провести закупку» ещё раз — повтор не задвоит приход.", " «Сатып алууну өткөрүү» баскычын кайра басыңыз — кайталоо киришти эки эсе кылбайт.",
                          " Press “Post purchase” again — a retry won't double the receipt.", " «Alımı işle»ye tekrar basın — tekrar girişi ikiye katlamaz.",
                          " «Xaridni o'tkazish»ni yana bosing — takror kirimni ikki baravar qilmaydi."));
            return;
        }

        // Остаток в каталоге кассы — сразу, принятые строки уходят из списка, непринятые остаются для повтора.
        // 2026-10-19, ревью: от ТЕКУЩЕГО остатка плитки на момент ответа (продажи, прошедшие пока строки правились, не откатываются),
        // а не от остатка при открытии (StockBefore). Точный остаток придёт со следующей синхронизацией каталога.
        foreach (var line in result.PostedLines.Where(l => !l.IsNew && !string.IsNullOrWhiteSpace(l.ProductId)))
        {
            var vm = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
            if (vm is null)
                continue;
            var target = Math.Round(vm.Quantity + line.Quantity, 3);
            StockSyncService.ApplyQuantityToTileOnUi(vm, target, vm.MustWeigh);
            CatalogCacheService.PersistProductStock(vm.Id, target, vm.MustWeigh);
        }
        var posted = new HashSet<ReceivingLineVm>(result.PostedLines, ReferenceEqualityComparer.Instance);
        // 2026-10-19, ревью: в реестр — по итоговым id (и у созданных товаров), до того как строки уйдут из черновика.
        PostedLines.Remember(supplier, _draft.Rows.Where(r => posted.Contains(r.Line)).Select(r => (r.Line, r.InvoiceName)).ToList());
        _draft.Rows.RemoveAll(r => posted.Contains(r.Line));
        if (result.Created > 0)
        {
            try
            {
                await CatalogCacheService.RefreshFromApiAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Закупка по накладной: каталог после создания товаров не обновлён: {ex.Message}", "WARNING");
            }
        }
        ProductActions.RequestCatalogRefresh?.Invoke();

        var summary = PurchaseReceivingService.SummaryText(result);
        // 2026-10-20, этап П3: закупка «в долг» проведена — записать долг поставщику (сделка-долг, как сайт; защита от двойного долга).
        var debtFailed = false;
        if (!paidNow && result.PostedLines.Count > 0)
        {
            var debt = await SupplierDebtUi.RecordAfterReceiptAsync(supplier, result, _draft.DebtDays, "invoice").ConfigureAwait(true);
            if (debt is not null)
            {
                summary += " " + SupplierDebtRecorder.ResultText(debt);
                debtFailed = !debt.Recorded;
            }
        }
        if (result.PostedLines.Count > 0)
        {
            var who = T($"Поставщик «{supplier.Name}», ", $"Жеткирүүчү «{supplier.Name}», ", $"Supplier “{supplier.Name}”, ", $"Tedarikçi «{supplier.Name}», ", $"Yetkazib beruvchi «{supplier.Name}», ")
                      + (paidNow ? T("оплачено сразу.", "дароо төлөндү.", "paid now.", "hemen ödendi.", "darhol to'landi.")
                          : T("в долг поставщику.", "жеткирүүчүгө карызга.", "on credit.", "tedarikçiye borç.", "yetkazib beruvchiga qarzga."));
            _draft.PostedSummaries.Add(summary + " " + who);
            // Склад программы владельца: отметка у принятых товаров (как после «Выполнить» советника).
            var changed = result.PostedLines.Where(l => !string.IsNullOrWhiteSpace(l.ProductId)).Select(l => l.ProductId!).Distinct().ToList();
            if (changed.Count > 0)
                ProductActionPlan.ShowChangedProducts?.Invoke(changed, summary);
        }
        PosLogger.Log($"Закупка по накладной: {summary} Ошибок: {result.Errors.Count}.", result.Errors.Count == 0 ? "STOCK" : "WARNING");

        _draft.Posting = false;
        _post.Content = T("Провести закупку", "Сатып алууну өткөрүү", "Post purchase", "Alımı işle", "Xaridni o'tkazish");
        if (result.Errors.Count == 0 && _draft.Rows.Count == 0 && !debtFailed)
        {
            Close(summary);
            return;
        }
        RebuildRows();
        UpdateTotal();
        ShowError((result.PostedLines.Count > 0 ? summary + "\n" : "") + string.Join("\n", result.Errors.Take(10)));
    }
}
