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

/// <summary>2026-10-22 (закупки как в вебе): с чего начать приём — заказ поставщику (что осталось принять), прошлый приход («Повторить
/// закупку»), «Рекомендуемый заказ» или «Пополнение» (строки с количеством).</summary>
public sealed record ReceivePrefill(string? SupplierId, string? SupplierName, string? OrderId, string? OrderNumber,
    IReadOnlyList<(string ProductId, string Name, string Unit, double Qty, double? Price)> Lines)
{
    public static ReceivePrefill FromOrder(PurchaseOrder o) => new(o.SupplierId, o.SupplierName, o.Id, o.Number,
        o.Lines.Where(l => l.Left > 0.0005).Select(l => (l.ProductId, l.Name, l.Unit, l.Left, (double?)(l.Price > 0 ? l.Price : null))).ToList());

    public static ReceivePrefill FromReceipt(SupplierReceipt r) => new(r.SupplierId, r.SupplierName, null, null,
        r.Lines.Where(l => l.ProductId.Length > 0).Select(l => (l.ProductId, l.ProductName, l.Unit, l.Qty, (double?)(l.PurchasePrice > 0 ? l.PurchasePrice : null))).ToList());
}

public sealed partial class PurchasesWindow
{
    /// <summary>2026-11-07 (Б-E2): ширина страницы приёма, ниже которой панель «скан / товары / наценка» идёт в два ряда (public — для проверки в UiTests).</summary>
    public const double ReceiveToolsTwoRowsBelow = 1320;

    /// <summary>Состояние открытого мастера «Приём товара».</summary>
    private sealed class ReceiveState
    {
        public PurchasesSupplierPicker Picker = null!;
        /// <summary>2026-10-30 (нумерация закупок): «Номер накладной поставщика» в шапке приёмки.</summary>
        public TextBox DocNumber = null!;
        public readonly ObservableCollection<ReceivingLineVm> Lines = new();
        public readonly StackPanel Rows = new();
        public readonly StackPanel Steps = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
        public readonly NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid Tiles = new() { MinTileWidth = 150, Spacing = 8 };
        public TextBox Scan = null!;
        public readonly StackPanel Suggest = new() { Spacing = 2 };
        public Border SuggestBox = null!;
        public readonly TextBlock Message = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        public Button Post = null!;
        public readonly TextBlock Footer = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        public Grid Head = null!;
        public string? OrderId;
        public string? OrderNumber;
        public bool Busy;
        public TextBlock? Title;
        public Border MessageBox = null!;
        public bool Narrow;
        // 2026-10-24, ТЗ «Склад» (Б2): «скан → строка» как в Склад → Приёмка (Б1).
        public CheckBox FastNext = null!;
        /// <summary>2026-11-07 (память владельца, Б-J1): отписка от статического ReceivingMarkupSettingsStore.Changed — вызывается при закрытии страницы приёма и самого окна
        /// (раньше подписка снималась только при СЛЕДУЮЩЕМ срабатывании события, и закрытый раздел «Закупки» держался в памяти минутами).</summary>
        public Action? Detach;
        public Button Raise = null!;
        /// <summary>2026-11-05 (функция 11): доп. расходы прихода (доставка, грузчики) и способ разнесения; доли лежат в строках (LandedExtraPerUnit).</summary>
        public double LandedTotal;
        public LandedMethod LandedMode;
        public Button SupplierGoods = null!;
        public ScrollViewer Scroll = null!;
        public Border GoodsPanel = null!;
        public TextBox GoodsSearch = null!;
        public ListBox GoodsList = null!;
        public readonly TextBlock GoodsTitle = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        public List<SupplierGoodsRow> GoodsRows = new();
        public HashSet<string> SupplierIds = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>2026-11-06: последняя закупка каждого товара у выбранного поставщика (приходы с сервера) — цена закупки строки по умолчанию.</summary>
        public Dictionary<string, ReceivingDefaults.SupplierPrice> SupplierPrices = new(StringComparer.OrdinalIgnoreCase);
        public CancellationTokenSource? GoodsCts;
        public bool GoodsOpen;
        public bool GoodsLoading;
        public bool GoodsPicking;
        public bool AddedFromPanel;
        public ReceivingLineVm? Highlight;
        public readonly Dictionary<ReceivingLineVm, RowParts> RowMap = new();
        // Скан внутри ячейки таблицы (поле «Количество» и др.): быстрая серия символов + Enter — это сканер, а не человек.
        /// <summary>2026-11-09 (владелец: «повторный скан того же товара — спросить количество»): «тот же код подряд» этой приёмки (закрыли приёмку — отсчёт заново).</summary>
        public readonly ReceivingRepeatScanGuard RepeatGuard = new();
        public long FsTick;
        public ulong? FsRaw;
        public string FsBuf = "";
        public int FsRun;
        public TextBox? FsBox;
        public string? FsSnap;
        public int FsCaret;
    }

    /// <summary>Поля одной строки таблицы приёма: нужны, чтобы вести курсор по ячейкам и обновлять числа без пересборки строки.</summary>
    private sealed class RowParts
    {
        public Border Box = null!;
        public TextBox? Name;
        public TextBox Qty = null!, Purchase = null!, Markup = null!, Sale = null!;
        public bool Syncing;
        public IDisposable? HighlightBinding;
        public Action<bool> Refresh = _ => { };
    }

    /// <summary>Строка панели «Товары поставщика».</summary>
    private sealed record SupplierGoodsRow(string ProductId, string Name, string Unit, double Price, string Info, string SearchKey);

    private ReceiveState? _receive;

    private bool ReceiveHasWork => _receive is { } r && r.Lines.Any(l => l.Quantity > 0);

    /// <summary>«＋ Приём товара» (prefill — по заказу, прошлому приходу, рекомендуемому заказу или «Пополнению»).</summary>
    public void OpenReceive(ReceivePrefill? prefill)
    {
        _receive?.Detach?.Invoke(); // 2026-11-07: прежняя страница приёма (если была) отписывается от статических событий
        var s = new ReceiveState { OrderId = prefill?.OrderId, OrderNumber = prefill?.OrderNumber };
        _receive = s;
        _recommend = null;
        var page = new Grid { Margin = PageMargin, RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto") };

        // Шапка: «← К закупкам», название с шагами «1 Поставщик → 2 Товары → 3 Оплата».
        var back = UiKit.Ghost(this, T("← К закупкам", "← Сатып алууларга", "← Purchases", "← Satın almalar", "← Xaridlarga"));
        back.Height = 44;
        Avalonia.Automation.AutomationProperties.SetName(back, T("Назад к закупкам", "Сатып алууларга кайтуу", "Back to purchases", "Satın almalara dön", "Xaridlarga qaytish"));
        back.Click += async (_, _) =>
        {
            // 2026-11-11 (О-5): набранное теперь остаётся черновиком (ReceivingDraftSession) — при следующем открытии приёма его предложат продолжить.
            if (ReceiveHasWork && !await ConfirmAsync(T("Выйти без оприходования? Набранное сохранится черновиком — продолжите при следующем открытии приёма.", "Кабыл албай чыгасызбы? Терилгени долбоор катары сакталат — кийинки ачканда улантасыз.",
                    "Leave without posting? What you entered stays as a draft — continue it the next time you open receiving.", "Kabul etmeden çıkılsın mı? Girilenler taslak olarak kalır — bir sonraki açılışta devam edebilirsiniz.", "Qabul qilmasdan chiqasizmi? Kiritilgan qoralama sifatida saqlanadi — keyingi ochishda davom ettirasiz."),
                    T("Выйти", "Чыгуу", "Leave", "Çık", "Chiqish")).ConfigureAwait(true))
                return;
            CloseSub();
        };
        var title = new TextBlock
        {
            Text = T("Приём товара", "Товар кабыл алуу", "Receive goods", "Mal kabul", "Tovar qabul qilish") + (s.OrderNumber is { } num ? T($" по заказу {num}", $" — {num} буйрутмасы боюнча", $" for order {num}", $" — {num} siparişi", $" — {num} buyurtma bo'yicha") : ""),
            FontSize = 20, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        s.Title = title;
        Use(title, TextBlock.ForegroundProperty, "BrushText");
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(back);
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        Grid.SetColumn(s.Steps, 2);
        s.Steps.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(s.Steps);
        page.Children.Add(head);

        // Шаг 1: поставщик (поиск и «＋») и плитки «Поставщик / Позиций / Количество / Сумма накладной».
        s.Picker = new PurchasesSupplierPicker(_svc);
        s.Picker.SetSuppliers(_filterSupplier.All);
        s.Picker.Changed += sup =>
        {
            _ = ReceiveLoadGoodsAsync(s, sup);
            ReceiveReapplyPurchases(s); // 2026-11-06: цены закупки подтягиваются заново по истории нового поставщика (ручной ввод не трогаем)
            ReceiveRefresh();
        };
        var supplierRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,190"), Margin = new Thickness(0, 0, 0, 10) };
        var supplierLabel = UiKit.Label(this, T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"));
        supplierLabel.VerticalAlignment = VerticalAlignment.Center;
        supplierLabel.Margin = new Thickness(0, 0, 12, 0);
        supplierRow.Children.Add(supplierLabel);
        Grid.SetColumn(s.Picker, 1);
        supplierRow.Children.Add(s.Picker);
        // 2026-10-30 (нумерация закупок): номер накладной поставщика вводится здесь и сохраняется вместе с документом (комментарий прихода).
        var docLabel = UiKit.Label(this, T("№ накладной поставщика", "Жеткирүүчүнүн накладнойунун №", "Supplier invoice №", "Tedarikçi fatura №", "Yetkazib beruvchi yuk xati №"));
        docLabel.VerticalAlignment = VerticalAlignment.Center;
        docLabel.Margin = new Thickness(16, 0, 8, 0);
        Grid.SetColumn(docLabel, 2);
        supplierRow.Children.Add(docLabel);
        s.DocNumber = UiKit.Input(this, T("не обязательно", "милдеттүү эмес", "optional", "isteğe bağlı", "ixtiyoriy"), 44);
        s.DocNumber.MaxLength = 40;
        Avalonia.Automation.AutomationProperties.SetName(s.DocNumber, docLabel.Text);
        ToolTip.SetTip(s.DocNumber, T("Номер накладной, которую дал поставщик. Он сохранится вместе с приходом рядом с номером документа ПР-…",
            "Жеткирүүчү берген накладнойдун номери. Ал кирүү менен бирге ПР-… документ номеринин жанында сакталат.",
            "The invoice number given by the supplier. It is saved with the delivery next to the document number ПР-…",
            "Tedarikçinin verdiği fatura numarası. Giriş ile birlikte belge numarasının (ПР-…) yanında kaydedilir.",
            "Yetkazib beruvchi bergan yuk xati raqami. U kirim bilan birga hujjat raqami (ПР-…) yonida saqlanadi."));
        Grid.SetColumn(s.DocNumber, 3);
        supplierRow.Children.Add(s.DocNumber);
        Grid.SetRow(supplierRow, 1);
        page.Children.Add(supplierRow);
        s.Tiles.Margin = new Thickness(0, 0, 0, 10);
        Grid.SetRow(s.Tiles, 2);
        page.Children.Add(s.Tiles);

        // Шаг 2: товары — скан/поиск (общий поиск кассы ProductCodeLookup), «Товары поставщика», «Очистить количества», «Очистить список».
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        s.Scan = UiKit.Input(this, T("Сканируйте штрихкод или найдите товар по названию", "Штрихкодду сканерлеңиз же товарды аты боюнча табыңыз",
            "Scan a barcode or find a product by name", "Barkodu okutun veya ürünü adıyla bulun", "Shtrix-kodni skanerlang yoki mahsulotni nomi bo'yicha toping"), 44);
        Avalonia.Automation.AutomationProperties.SetName(s.Scan, s.Scan.Watermark);
        // 2026-10-24, ТЗ «Склад» (Б2): подсказка про порядок работы — скан первым, поставщика можно выбрать позже.
        ToolTip.SetTip(s.Scan, ReceiveHintText());
        s.Scan.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            await ReceiveAddByCodeAsync((s.Scan.Text ?? "").Trim()).ConfigureAwait(true);
        };
        s.Scan.TextChanged += (_, _) => ReceiveSuggest();
        tools.Children.Add(s.Scan);
        // 2026-10-24, ТЗ «Склад» (Б2): как в Склад → Приёмка — флажок «Сразу к скану» (курсор остаётся в поле скана, каждый скан = +1).
        s.FastNext = new CheckBox { Content = T("Сразу к скану", "Дароо сканерге", "Straight to scan", "Doğrudan okutmaya", "Darhol skanerga"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        Use(s.FastNext, ContentControl.ForegroundProperty, "BrushText");
        ToolTip.SetTip(s.FastNext, T("Курсор остаётся в поле скана, каждый скан прибавляет 1 к количеству.", "Курсор сканер талаасында калат, ар бир скан санга 1 кошот.",
            "The cursor stays in the scan field; every scan adds 1 to the quantity.", "İmleç okutma alanında kalır, her okutma miktara 1 ekler.", "Kursor skaner maydonida qoladi, har bir skan soniga 1 qo'shadi."));
        Avalonia.Automation.AutomationProperties.SetName(s.FastNext, s.FastNext.Content as string);
        // 2026-10-28, владелец: одно общее поле «Наценка для всех, %» — цена продажи всех строк и следующих сканов = закупка × (1 + %); у ручной строки — «Как у всех».
        var markupLabel = new TextBlock { Text = T("Наценка для всех, %", "Баарына кошумча, %", "Markup for all, %", "Tümü için kâr, %", "Hammasi uchun ustama, %"), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
        Use(markupLabel, TextBlock.ForegroundProperty, "BrushText");
        // 2026-11-06 (жалоба «Орук маркет», приход №48): общая наценка по умолчанию ВЫКЛЮЧЕНА — поле пустое (подсказка «выкл.»). Число = явное включение, пусто/0 = выключена.
        var markupBox = UiKit.Input(this, T("выкл.", "өчүк", "off", "kapalı", "o'chiq"), 44);
        markupBox.Width = 84;
        markupBox.HorizontalContentAlignment = HorizontalAlignment.Right;
        markupBox.Margin = new Thickness(6, 0, 0, 0);
        markupBox.Text = ReceivingMarkupSettings.PercentBoxText(ReceivingMarkupSettingsStore.Current.DefaultPercent);
        ToolTip.SetTip(markupBox, T("Пусто — общая наценка выключена, цену продажи программа сама не ставит. Введите число, чтобы строки без цены считались по нему.",
            "Бош — жалпы кошумча өчүк, сатуу баасын программа өзү койбойт. Баасы жок саптар эсептелиши үчүн санды киргизиңиз.",
            "Empty — the common markup is off and the program does not set sale prices itself. Enter a number to price lines without a price.",
            "Boş — ortak kâr kapalı, program satış fiyatını kendisi koymaz. Fiyatsız satırların hesaplanması için sayı girin.",
            "Bo'sh — umumiy ustama o'chiq, dastur sotuv narxini o'zi qo'ymaydi. Narxsiz qatorlar hisoblanishi uchun son kiriting."));
        Avalonia.Automation.AutomationProperties.SetName(markupBox, markupLabel.Text);
        void MarkupCommit(bool persist)
        {
            var typed = CheckoutValidation.TryParseAmount(markupBox.Text, out var percent);
            if (typed == CheckoutValidation.AmountParse.Empty)
                percent = 0;
            if ((typed is CheckoutValidation.AmountParse.Ok or CheckoutValidation.AmountParse.Empty) && percent <= 1000)
                ReceivingMarkupSettingsStore.SetDefaultPercent(percent, persist);
            else if (persist)
                markupBox.Text = ReceivingMarkupSettings.PercentBoxText(ReceivingMarkupSettingsStore.Current.DefaultPercent);
        }
        markupBox.TextChanged += (_, _) => { if (markupBox.IsFocused) MarkupCommit(false); };
        markupBox.LostFocus += (_, _) => MarkupCommit(true);
        markupBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; MarkupCommit(true); s.Scan.Focus(); } };
        Action markupChanged = null!;
        markupChanged = () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_receive != s)
            {
                ReceivingMarkupSettingsStore.Changed -= markupChanged; // приём закрыт — отписываемся
                return;
            }
            var auto = ReceivingMarkupSettingsStore.Current.AutoEnabled;
            if (!markupBox.IsFocused)
                markupBox.Text = ReceivingMarkupSettings.PercentBoxText(ReceivingMarkupSettingsStore.Current.DefaultPercent);
            if (auto)
                ReceivingPricing.ApplyAutoToAll(s.Lines);
            ReceiveRefresh();
        });
        ReceivingMarkupSettingsStore.Changed += markupChanged;
        s.Detach = () => ReceivingMarkupSettingsStore.Changed -= markupChanged; // 2026-11-07: см. ReceiveState.Detach
        // 2026-11-06 (Б-10): владельцу — один раз «общая наценка выключена при обновлении» (после того как окно приёма отрисовалось).
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = ReceivingMarkupDialog.ShowResetNoticeIfNeededAsync(this), Avalonia.Threading.DispatcherPriority.Background);
        // 2026-11-06: общая наценка сама цену продажи существующего товара не меняет — только по этой кнопке (и «Как у всех» в самой строке).
        var applyAll = UiKit.Ghost(this, T("Применить ко всем", "Баарына колдонуу", "Apply to all", "Tümüne uygula", "Hammasiga qo'llash"));
        applyAll.Height = 44;
        applyAll.Margin = new Thickness(6, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(applyAll, T("Применить общую наценку ко всем строкам", "Жалпы кошумчаны бардык саптарга колдонуу", "Apply the common markup to all lines", "Ortak kârı tüm satırlara uygula", "Umumiy ustamani barcha qatorlarga qo'llash"));
        ToolTip.SetTip(applyAll, T("Пересчитать цену продажи всех строк по общей наценке (в том числе у товаров с ценой из карточки). Строки, где цену правили руками, не меняются.",
            "Бардык саптардын сатуу баасын жалпы кошумча боюнча кайра эсептөө (карточкадагы баасы бар товарларда да). Баасы кол менен өзгөртүлгөн саптар өзгөрбөйт.",
            "Recalculate the sale price of all lines by the common markup (including products with a card price). Lines whose price was edited by hand stay as they are.",
            "Tüm satırların satış fiyatını ortak kâr oranıyla yeniden hesapla (kart fiyatı olan ürünler dahil). Fiyatı elle değiştirilen satırlar değişmez.",
            "Barcha qatorlarning sotuv narxini umumiy ustama bo'yicha qayta hisoblash (kartadagi narxli mahsulotlar ham). Narxi qo'lda o'zgartirilgan qatorlar o'zgarmaydi."));
        applyAll.Click += (_, _) =>
        {
            MarkupCommit(true);
            ReceiveApplyMarkupToAll(s);
        };
        var fastBox = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { s.FastNext, markupLabel, markupBox, applyAll } };
        Grid.SetColumn(fastBox, 1);
        tools.Children.Add(fastBox);
        // 2026-11-07 (живая проверка 1.17.81, Б-E2): при ширине ~1024 всё в один ряд не помещалось — поле скана сжималось до «Штр…», «Очистить» уходила за край.
        // Теперь на узком окне (< 1320) верхний ряд — поле скана + «Товары поставщика» / «Обнулить» / «Очистить», а «Сразу к скану», наценка и «Ко всем» — второй ряд.
        bool? toolsTwoRows = null;
        void ApplyToolsLayout(double width)
        {
            var two = width < ReceiveToolsTwoRowsBelow;
            if (two == toolsTwoRows)
                return;
            toolsTwoRows = two;
            Grid.SetRow(fastBox, two ? 1 : 0);
            Grid.SetColumn(fastBox, two ? 0 : 1);
            Grid.SetColumnSpan(fastBox, two ? 5 : 1);
            fastBox.Margin = two ? new Thickness(0, 8, 0, 0) : new Thickness(0);
        }
        var supplierGoods = s.SupplierGoods = UiKit.Ghost(this, ReceiveGoodsButtonText(s));
        supplierGoods.Height = 44;
        supplierGoods.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(supplierGoods, T("Товары поставщика", "Жеткирүүчүнүн товарлары", "Supplier's products", "Tedarikçi ürünleri", "Yetkazib beruvchi mahsulotlari"));
        supplierGoods.Click += (_, _) => ReceiveToggleGoods(s);
        Grid.SetColumn(supplierGoods, 2);
        tools.Children.Add(supplierGoods);
        var zero = UiKit.Ghost(this, T("Обнулить кол-во", "Санын нөлдөө", "Zero quantities", "Miktarları sıfırla", "Miqdorni nollash"));
        zero.Height = 44;
        zero.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(zero, T("Очистить количества", "Сандарды тазалоо", "Clear quantities", "Miktarları temizle", "Miqdorlarni tozalash"));
        zero.Click += (_, _) =>
        {
            foreach (var l in s.Lines)
                l.Quantity = 0;
            ReceiveRender();
        };
        Grid.SetColumn(zero, 3);
        tools.Children.Add(zero);
        var clear = UiKit.Ghost(this, T("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash"));
        clear.Height = 44;
        clear.Margin = new Thickness(8, 0, 0, 0);
        Avalonia.Automation.AutomationProperties.SetName(clear, T("Очистить список товаров", "Товарлар тизмесин тазалоо", "Clear the product list", "Ürün listesini temizle", "Mahsulotlar ro'yxatini tozalash"));
        clear.Click += async (_, _) =>
        {
            if (s.Lines.Count > 0 && !await ConfirmAsync(T("Убрать все товары из приёма?", "Бардык товарларды кабыл алуудан алып салуу керекпи?", "Remove all products from receiving?",
                    "Tüm ürünler kabulden çıkarılsın mı?", "Barcha mahsulotlar qabuldan olib tashlansinmi?"), T("Очистить", "Тазалоо", "Clear", "Temizle", "Tozalash")).ConfigureAwait(true))
                return;
            s.Lines.Clear();
            ReceiveRender();
        };
        Grid.SetColumn(clear, 4);
        tools.Children.Add(clear);
        var toolsBox = new StackPanel { Children = { tools } };
        s.SuggestBox = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 8), IsVisible = false, Child = s.Suggest };
        Use(s.SuggestBox, Border.BackgroundProperty, "BrushPanel");
        Use(s.SuggestBox, Border.BorderBrushProperty, "BrushBorder");
        toolsBox.Children.Add(s.SuggestBox);
        toolsBox.Children.Add(ReceiveBuildGoodsPanel(s));
        // 2026-10-25 (Q4): плашка «Операция выполняется в фоне» — жёлто-нейтральная (фон BrushWarningSoft), обычные сообщения — как раньше, без фона.
        s.MessageBox = new Border { CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 0, 8), IsVisible = false, Child = s.Message };
        toolsBox.Children.Add(s.MessageBox);
        Grid.SetRow(toolsBox, 3);
        page.Children.Add(toolsBox);

        // Таблица товаров.
        s.Head = new Grid();
        var headBox = new Border { Padding = new Thickness(14, 8, 8, 8), BorderThickness = new Thickness(0, 0, 0, 1), Child = s.Head };
        Use(headBox, Border.BackgroundProperty, "BrushPanelSoft");
        Use(headBox, Border.BorderBrushProperty, "BrushBorder");
        var scroll = s.Scroll = new ScrollViewer { Content = s.Rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var table = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        table.Children.Add(headBox);
        Grid.SetRow(scroll, 1);
        table.Children.Add(scroll);
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), ClipToBounds = true, Child = table };
        Use(card, Border.BackgroundProperty, "BrushPanel");
        Use(card, Border.BorderBrushProperty, "BrushBorder");
        Grid.SetRow(card, 5);
        page.Children.Add(card);

        // Низ: итог и одна жёлтая «Оприходовать товары» (шаг 3 — окно «Подтверждение оплаты»).
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        Use(s.Footer, TextBlock.ForegroundProperty, "BrushText");
        footer.Children.Add(s.Footer);
        // 2026-10-28: «Наценка…» — правила автонаценки (наценка по умолчанию, по категориям, округление); менять могут владелец и администратор.
        var markupButton = UiKit.Ghost(this, T("Наценка…", "Кошумча %…", "Markup…", "Kâr oranı…", "Ustama…"));
        markupButton.Height = 48;
        markupButton.Margin = new Thickness(0, 0, 10, 0);
        Avalonia.Automation.AutomationProperties.SetName(markupButton, T("Автоматическая наценка", "Автоматтык кошумча", "Automatic markup", "Otomatik kâr oranı", "Avtomatik ustama"));
        markupButton.Click += async (_, _) => await new ReceivingMarkupDialog().ShowDialog(this).ConfigureAwait(true);
        Grid.SetColumn(markupButton, 1);
        footer.Children.Add(markupButton);
        // 2026-11-05 (функция 11): «Доп. расходы…» — доставка/грузчики разносятся на закупочную цену строк (себестоимость); долг поставщику не меняется.
        var landedButton = UiKit.Ghost(this, T("Доп. расходы…", "Кошумча чыгым…", "Extra costs…", "Ek masraf…", "Qo'shimcha xarajat…"));
        landedButton.Height = 48;
        landedButton.Margin = new Thickness(0, 0, 10, 0);
        Avalonia.Automation.AutomationProperties.SetName(landedButton, T("Доп. расходы прихода: доставка, грузчики", "Кирүүнүн кошумча чыгымдары: жеткирүү, жүк ташуучулар", "Extra delivery costs: delivery, loaders", "Ek giriş masrafları: nakliye, hamal", "Kirimning qo'shimcha xarajatlari: yetkazib berish, yuk tashuvchilar"));
        landedButton.Click += async (_, _) => await ReceiveLandedAsync(s).ConfigureAwait(true);
        Grid.SetColumn(landedButton, 2);
        footer.Children.Add(landedButton);
        // 2026-10-24, ТЗ «Склад» (Б2): «Обновить цены продажи (N)» — видна, пока есть строки с подорожанием и непоправленной продажей.
        s.Raise = UiKit.Ghost(this, "");
        s.Raise.Height = 48;
        s.Raise.Margin = new Thickness(0, 0, 10, 0);
        s.Raise.IsVisible = false;
        s.Raise.Click += async (_, _) => await ReceiveRaiseAsync(s).ConfigureAwait(true);
        Grid.SetColumn(s.Raise, 3);
        footer.Children.Add(s.Raise);
        s.Post = UiKit.Primary(this, T("Оприходовать товары", "Товарларды кирешке алуу", "Post the goods", "Malları kaydet", "Tovarlarni kirim qilish"));
        s.Post.Height = 48;
        s.Post.Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(s.Post, s.Post.Content as string);
        s.Post.Click += async (_, _) => await ReceivePostAsync().ConfigureAwait(true);
        Grid.SetColumn(s.Post, 4);
        footer.Children.Add(s.Post);
        Grid.SetRow(footer, 6);
        page.Children.Add(footer);

        page.SizeChanged += (_, e) =>
        {
            ApplyToolsLayout(e.NewSize.Width);
            var narrow = e.NewSize.Width < 1000;
            // Низкий экран (касса 1024×600): плитки прячутся — те же цифры в строке итога внизу.
            s.Tiles.IsVisible = e.NewSize.Height >= 600;
            // 2026-10-24: панель «Товары поставщика» — выше на большом окне, ниже на 1024×600 (таблице остаётся место).
            s.GoodsList.Height = e.NewSize.Height >= 700 ? 220 : e.NewSize.Height >= 520 ? 150 : 110;
            if (narrow == s.Narrow && s.Head.ColumnDefinitions.Count > 0)
                return;
            s.Narrow = narrow;
            title.FontSize = narrow ? 17 : 20;
            supplierGoods.Content = ReceiveGoodsButtonText(s);
            applyAll.Content = narrow ? T("Ко всем", "Баарына", "To all", "Tümüne", "Hammasiga") : T("Применить ко всем", "Баарына колдонуу", "Apply to all", "Tümüne uygula", "Hammasiga qo'llash");
            // 2026-10-24 (Б2): на узком окне (1024) подсказка поля скана короче — не обрезается.
            s.Scan.Watermark = narrow
                ? T("Штрихкод или название", "Штрихкод же аталышы", "Barcode or name", "Barkod veya ad", "Shtrix-kod yoki nomi")
                : T("Сканируйте штрихкод или найдите товар по названию", "Штрихкодду сканерлеңиз же товарды аты боюнча табыңыз",
                    "Scan a barcode or find a product by name", "Barkodu okutun veya ürünü adıyla bulun", "Shtrix-kodni skanerlang yoki mahsulotni nomi bo'yicha toping");
            ReceiveRender();
        };
        // 2026-10-24, ТЗ «Склад» (Б2): скан во время правки ячейки — как в Склад → Приёмка: правка остаётся как была, штрихкод идёт в приём.
        page.AddHandler(InputElement.KeyDownEvent, (_, e) => ReceiveFieldScanKey(s, e), RoutingStrategies.Tunnel);
        ShowSub(page);

        if (prefill?.SupplierId is { Length: > 0 } sid)
        {
            var known = _filterSupplier.All.FirstOrDefault(x => x.Id == sid)
                        ?? new SupplierInfo(sid, prefill.SupplierName ?? "", "", "", "", "", "", "", "", "", null);
            s.Picker.Select(known, raise: false);
            _ = ReceiveLoadGoodsAsync(s, known);
        }
        // Из «Пополнения» приходит только название поставщика — выбираем его, когда список загрузится.
        var byName = prefill?.SupplierId is not { Length: > 0 } && prefill?.SupplierName is { Length: > 0 } ? prefill.SupplierName : null;
        if (s.Picker.All.Count == 0)
            _ = s.Picker.LoadAsync().ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => SelectByName(s, byName)), TaskScheduler.Default);
        else
            SelectByName(s, byName);
        ReceiveRender();
        if (prefill is { Lines.Count: > 0 })
            _ = ReceivePrefillAsync(prefill);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => s.Scan.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        ReceiveDraftInit(s); // 2026-11-11 (О-5): черновик неоконченной приёмки (PurchasesWindow.Receive.Draft.cs)
        PosLogger.Log($"Закупки: открыт приём товара{(prefill is null ? "" : $" ({prefill.Lines.Count} строк заранее{(prefill.OrderNumber is { } n ? ", заказ " + n : "")})")}.", "UI");
    }

    private void SelectByName(ReceiveState s, string? name)
    {
        if (name is null || s.Picker.Selected is not null || _receive != s)
            return;
        if (s.Picker.All.FirstOrDefault(x => string.Equals(x.Name.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase)) is { } found)
            s.Picker.Select(found);
    }

    private async Task ReceivePrefillAsync(ReceivePrefill prefill)
    {
        if (_receive is not { } s)
            return;
        foreach (var l in prefill.Lines)
        {
            var line = await LineForProductAsync(l.ProductId, l.Name, l.Unit, l.Qty, l.Price).ConfigureAwait(true);
            if (_receive != s)
                return;
            if (l.Price is > 0)
                line?.LockPurchase(); // 2026-11-06: цена из заказа/прошлого прихода — смена поставщика её не подменяет
            if (line is not null && s.Lines.All(x => !string.Equals(x.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)))
                s.Lines.Add(line);
        }
        ReceiveRender();
    }

    /// <summary>Строка приёма для товара склада: из каталога кассы (остаток, цены, штрихкод) или с сервера (товара в каталоге ещё нет).</summary>
    private static async Task<ReceivingLineVm?> LineForProductAsync(string productId, string name, string unit, double qty, double? purchasePrice)
    {
        var tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase));
        if (tile is not null)
        {
            var line = PurchaseReceivingService.FromTile(tile, qty);
            if (purchasePrice is > 0 and var pp)
                line.PurchasePrice = pp;
            return line;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            if (await PosApp.CatalogApi.ProductsDetailAsync(productId, cts.Token).ConfigureAwait(true) is { ValueKind: JsonValueKind.Object } p)
            {
                var price = SupplierApiService.Num(p, "price");
                var purchase = SupplierApiService.Num(p, "purchase_price");
                var title = SupplierApiService.Str(p, "name") is { Length: > 0 } n ? n : name;
                var u = SupplierApiService.Str(p, "unit") is { Length: > 0 } un ? un : unit.Length > 0 ? unit : "шт";
                return new ReceivingLineVm
                {
                    ProductId = productId, Barcode = SupplierApiService.Str(p, "barcode"), ProductName = title, Unit = u, OriginalName = title, OriginalUnit = u,
                    Source = ReceivingSource.Warehouse, StockBefore = SupplierApiService.Num(p, "quantity"), Quantity = qty,
                    PurchasePrice = purchasePrice is > 0 and var given ? given : purchase, SalePrice = price, OriginalPurchasePrice = purchase, OriginalSalePrice = price,
                    OriginalMarkupPercent = SupplierApiService.Num(p, "markup_percent"), // 2026-10-24 (Б2): запасной источник прежней наценки для «Обновить цены продажи»
                };
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: товар {productId} не прочитан ({ex.Message}).", "WARNING");
        }
        return null;
    }

    // ------------------------------------------------------------------ добавление товаров

    /// <summary>Подсказка про порядок работы (всплывающая у поля скана и текст пустого списка): скан первым, поставщика можно выбрать позже.</summary>
    private static string ReceiveHintText() => T(
        "Сканируйте товары по накладной. После скана курсор встаёт в «Количество»: число → Enter → цена закупки → Enter → цена продажи → Enter — и снова скан. Поставщика можно выбрать в любой момент до «Оприходовать товары». Незнакомый штрихкод — новая строка: впишите название.",
        "Накладная боюнча товарларды сканерлеңиз. Сканерден кийин курсор «Саны» уячасына тургандыктан: сан → Enter → сатып алуу баасы → Enter → сатуу баасы → Enter — жана кайра скан. Жеткирүүчүнү «Товарларды кирешке алуу» басылганга чейин каалаган убакта тандаса болот. Белгисиз штрихкод — жаңы сап: атын жазыңыз.",
        "Scan the items of the delivery note. After a scan the cursor lands in “Quantity”: number → Enter → purchase price → Enter → selling price → Enter — and scan again. You can pick the supplier at any moment before “Post the goods”. An unknown barcode is a new line: type its name.",
        "İrsaliyedeki ürünleri okutun. Okutmadan sonra imleç «Miktar» hücresinde olur: sayı → Enter → alış fiyatı → Enter → satış fiyatı → Enter — ve yine okutma. Tedarikçiyi «Malları kaydet»ten önce istediğiniz an seçebilirsiniz. Bilinmeyen barkod — yeni satır: adını yazın.",
        "Nakladnoy bo'yicha mahsulotlarni skanerlang. Skanerdan keyin kursor «Miqdor» katakchasida turadi: son → Enter → xarid narxi → Enter → sotuv narxi → Enter — va yana skaner. Yetkazib beruvchini «Tovarlarni kirim qilish»dan oldin istalgan paytda tanlash mumkin. Notanish shtrix-kod — yangi qator: nomini yozing.");

    /// <summary>Enter в поле: штрихкод/код — общий поиск кассы (этот ПК, затем сервер, затем общая база CRM и «новый товар»), иначе — первый
    /// найденный по названию. 2026-10-24 (Б2): строка встаёт СВЕРХУ, курсор — в «Количество» (повторный скан — +1, строка поднимается и подсвечивается).</summary>
    private async Task ReceiveAddByCodeAsync(string text)
    {
        if (_receive is not { } s || text.Length == 0)
            return;
        ReceiveMessage(null);
        var code = ProductCodeLookup.Normalize(text);
        // 2026-11-09 (владелец: «при повторном скане того же товара остановить сканер и спросить количество — и на закупках в десктопе»): тот же код сразу после добавления,
        // строка с ним уже в приёме — окно «Сколько штук?» вместо +1. Весовая этикетка (строки с таким штрихкодом нет) и код без строки идут как обычно.
        if (s.RepeatGuard.Check(code) == ReceivingScanVerdict.Repeat && ReceivingRepeatScan.FindLine(s.Lines, code) is { } again)
        {
            s.Scan.Text = "";
            await ReceiveAskRepeatAsync(s, code, again).ConfigureAwait(true);
            return;
        }
        var local = ProductCodeLookup.FindLocal(code, allowWeightLabel: false)?.Product;
        if (local is not null)
        {
            PurchaseReceivingService.LogScan(code, "база кассы", $"товар {local.Id}"); // 2026-11-11
            ReceiveAddTile(local, 1, byScan: true);
            s.RepeatGuard.Counted(code);
            s.Scan.Text = "";
            return;
        }
        // 2026-10-31 (владелец: «в приёмке весовые добавляются???»): этикетка весов (13 цифр, 2x…) — товар по коду внутри, количество = вес с этикетки
        // (повторная такая же этикетка прибавляется). Раньше она становилась «новым товаром» с кодом этикетки.
        // 2026-11-11 (владелец: «многие товары, которые не находит, показывает как ВЕСОВОЙ»): этикетка весов разбирается только ПОСЛЕ поиска по складу, серверу и общей базе
        // (ниже) и только при настроенном у магазина префиксе весов (ProductCodeLookup.FindWeightLabel). Раньше она шла до поиска — обычный код «20…» становился чужим весовым товаром.
        ProductCodeLookup.WeightLabelHit? weightLabel = null;
        bool AddWeightLabel()
        {
            weightLabel = ProductCodeLookup.FindWeightLabel(code);
            if (weightLabel?.Product is not { } weighedTile)
                return false;
            PurchaseReceivingService.LogScan(code, "весовая этикетка (префикс весов настроен)", $"товар {weighedTile.Id}, {weightLabel.Quantity:0.###}");
            ReceiveAddTile(weighedTile, weightLabel.Quantity);
            s.RepeatGuard.Counted(code); // 2026-11-09: код запоминается (между двумя сканами одного товара был другой код), но окно не вызывает
            s.Scan.Text = "";
            var byAmount = weightLabel.Label.Kind == NurMarketKassa.Core.Domain.WeightBarcodeValueKind.Amount;
            ReceiveMessage(weightLabel.Quantity > 0
                ? T($"Весовая этикетка: «{weighedTile.Title}» — {Q(weightLabel.Quantity)} {weighedTile.Unit} добавлено в «Принимаем»{(byAmount ? " (вес посчитан по сумме на этикетке)" : "")}. Проверьте количество.",
                    $"Салмак этикеткасы: «{weighedTile.Title}» — {Q(weightLabel.Quantity)} {weighedTile.Unit} «Кабыл алынат» тилкесине кошулду{(byAmount ? " (салмак этикеткадагы сумма боюнча эсептелди)" : "")}. Санын текшериңиз.",
                    $"Scale label: “{weighedTile.Title}” — {Q(weightLabel.Quantity)} {weighedTile.Unit} added to “Receiving”{(byAmount ? " (weight calculated from the amount on the label)" : "")}. Check the quantity.",
                    $"Terazi etiketi: «{weighedTile.Title}» — {Q(weightLabel.Quantity)} {weighedTile.Unit} «Kabul» sütununa eklendi{(byAmount ? " (ağırlık etiketteki tutardan hesaplandı)" : "")}. Miktarı kontrol edin.",
                    $"Tarozi yorlig'i: «{weighedTile.Title}» — {Q(weightLabel.Quantity)} {weighedTile.Unit} «Qabul» ustuniga qo'shildi{(byAmount ? " (vazn yorliqdagi summadan hisoblandi)" : "")}. Miqdorni tekshiring.")
                : T($"Весовая этикетка «{weighedTile.Title}» по сумме, а цены товара нет — вес не восстановить. Впишите количество руками.",
                    $"«{weighedTile.Title}» салмак этикеткасы сумма боюнча, товардын баасы жок — салмакты эсептей албайбыз. Санын кол менен жазыңыз.",
                    $"The scale label of “{weighedTile.Title}” is by amount and the product has no price — the weight can't be restored. Type the quantity by hand.",
                    $"«{weighedTile.Title}» terazi etiketi tutara göre, ürünün fiyatı yok — ağırlık hesaplanamaz. Miktarı elle girin.",
                    $"«{weighedTile.Title}» tarozi yorlig'i summa bo'yicha, mahsulot narxi yo'q — vaznni tiklab bo'lmaydi. Miqdorni qo'lda kiriting."), warning: weightLabel.Quantity <= 0);
            return true;
        }
        var looksLikeCode = code.Length >= 4 && code.All(char.IsLetterOrDigit) && code.Count(char.IsDigit) >= Math.Max(4, code.Length - 2);
        if (!looksLikeCode)
        {
            // 2026-11-06 (Б-12): поиск по названию — память, затем локальная база кассы, затем умное сопоставление накладных («Мол.», «1л», латиница/кириллица).
            // Без связи список памяти может быть пуст, а база нет: раньше здесь сразу писали «не найден — заведите новый» и кассир создавал дубль.
            s.Scan.IsEnabled = false;
            ReceivingNameSearch.Outcome outcome;
            try
            {
                var snapshot = CatalogCacheService.Products.ToArray();
                outcome = await Task.Run(() => ReceivingNameSearch.Find(text, snapshot, () => LocalProductRepository.Instance.LoadAllTiles(), s.SupplierIds, smart: true)).ConfigureAwait(true);
            }
            finally
            {
                s.Scan.IsEnabled = true;
                s.Scan.Focus();
            }
            if (_receive != s)
                return;
            if (outcome.Tiles.Count > 0 && !outcome.UsedSmartMatch)
            {
                ReceiveAddTile(outcome.Tiles[0], 1);
                s.Scan.Text = "";
            }
            else if (outcome.Tiles.Count > 0)
            {
                // Умное сопоставление — это «похожие», а не точные: сами в приём не добавляем, показываем список подсказок под полем.
                ReceiveSuggest(outcome.Tiles);
                ReceiveMessage(T($"«{text}»: точного совпадения нет, ниже похожие товары склада — выберите нужный. Новый товар заводите, только если среди них его нет.",
                    $"«{text}»: так дал келгени жок, төмөндө кампанын окшош товарлары — керектүүсүн тандаңыз. Жаңы товарды алардын арасында жок болсо гана түзүңүз.",
                    $"“{text}”: no exact match; similar warehouse products are listed below — pick the right one. Create a new product only if it is not among them.",
                    $"«{text}»: tam eşleşme yok, aşağıda depodaki benzer ürünler var — doğrusunu seçin. Yeni ürünü yalnızca aralarında yoksa oluşturun.",
                    $"«{text}»: aniq moslik yo'q, pastda ombordagi o'xshash mahsulotlar — keraklisini tanlang. Yangi mahsulotni faqat ular orasida bo'lmasa yarating."), warning: true);
            }
            else if (!outcome.CatalogChecked)
                // Каталога на этом ПК нет (или база не открылась) — «товара нет» утверждать нельзя, новый не предлагаем.
                ReceiveMessage(T($"«{text}»: каталог товаров на этом ПК ещё не загружен, поэтому найти по названию нельзя. Отсканируйте штрихкод или дождитесь загрузки каталога — новый товар пока не заводите, чтобы не получить дубль.",
                    $"«{text}»: бул ПКдеги товарлар каталогу али жүктөлө элек, ошондуктан ат боюнча издөө мүмкүн эмес. Штрихкодду сканерлеңиз же каталог жүктөлгөнчө күтүңүз — кайталанбашы үчүн жаңы товарды азырынча түзбөңүз.",
                    $"“{text}”: the product catalog is not loaded on this PC yet, so a search by name is not possible. Scan the barcode or wait for the catalog — do not create a new product yet, to avoid a duplicate.",
                    $"«{text}»: ürün kataloğu bu bilgisayara henüz yüklenmedi, bu yüzden ada göre arama yapılamıyor. Barkodu okutun veya kataloğun yüklenmesini bekleyin — kopya olmaması için henüz yeni ürün oluşturmayın.",
                    $"«{text}»: mahsulotlar katalogi bu kompyuterga hali yuklanmagan, shuning uchun nom bo'yicha qidirib bo'lmaydi. Shtrix-kodni skanerlang yoki katalog yuklanishini kuting — takrorlanmasligi uchun yangi mahsulotni hozircha yaratmang."), warning: true);
            else
                ReceiveMessage(T($"«{text}» не найден. Отсканируйте штрихкод — новый товар можно завести прямо здесь.", $"«{text}» табылган жок. Штрихкодду сканерлеңиз — жаңы товарды ушул жерде түзсө болот.",
                    $"“{text}” not found. Scan the barcode — a new product can be created right here.", $"«{text}» bulunamadı. Barkodu okutun — yeni ürün burada oluşturulabilir.",
                    $"«{text}» topilmadi. Shtrix-kodni skanerlang — yangi mahsulotni shu yerda yaratish mumkin."), warning: true);
            return;
        }
        s.Scan.IsEnabled = false;
        try
        {
            // Сервер (товар добавили на сайте минуту назад, доп. штрихкод), затем общая база CRM, затем «новый товар» — как «Приёмка» склада.
            var server = await ProductCodeLookup.FindOnServerAsync(code).ConfigureAwait(true);
            if (server.Hit?.Product is { } found)
            {
                PurchaseReceivingService.LogScan(code, "сервер (поиск кассы)", $"товар {found.Id}"); // 2026-11-11
                ReceiveAddTile(found, 1, byScan: true);
                s.RepeatGuard.Counted(code);
                s.Scan.Text = "";
                return;
            }
            var line = await PurchaseReceivingService.Instance.LookupAsync(code, 1).ConfigureAwait(true);
            if (_receive != s)
                return;
            // 2026-11-11: нигде не найден — только теперь код может оказаться этикеткой весов (при настроенном префиксе весов магазина).
            if (line.Source == ReceivingSource.Unknown && AddWeightLabel())
                return;
            var target = line;
            var added = true;
            if (line.ProductId is { Length: > 0 } && s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, line.ProductId, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                ReceivingRepeatScan.AddTo(same, 1); // 2026-11-11 (Б-4): в единицах колонки (упаковка в режиме упаковок)
                target = same;
                added = false;
            }
            else if (line.IsNew && s.Lines.FirstOrDefault(x => x.IsNew && string.Equals(x.Barcode, line.Barcode, StringComparison.OrdinalIgnoreCase)) is { } sameNew)
            {
                ReceivingRepeatScan.AddTo(sameNew, 1); // 2026-11-11 (Б-4)
                target = sameNew;
                added = false;
            }
            if (added && line.IsNew && weightLabel is not null)
                // 2026-10-31: код устроен как этикетка весов, но такого весового товара нет — не заводить двойника по коду этикетки молча.
                ReceiveMessage(T($"Код похож на этикетку весов (код товара {weightLabel.Label.ProductCode}), но весового товара с таким PLU/артикулом нет. Если это обычный штрихкод — впишите название, цены; если этикетка — уберите строку (✕) и проверьте PLU в карточке товара.",
                    $"Код тараза этикеткасына окшош (товар коду {weightLabel.Label.ProductCode}), бирок мындай PLU/артикулу бар салмактуу товар жок. Кадимки штрихкод болсо — атын, баасын жазыңыз; этикетка болсо — сапты өчүрүп (✕), товар карточкасындагы PLU текшериңиз.",
                    $"The code looks like a scale label (product code {weightLabel.Label.ProductCode}), but there is no weighed product with this PLU/article. If it is an ordinary barcode — enter the name and prices; if it is a label — remove the line (✕) and check the PLU in the product card.",
                    $"Kod terazi etiketine benziyor (ürün kodu {weightLabel.Label.ProductCode}) ama bu PLU/artikel ile tartılı ürün yok. Normal barkodsa — adı ve fiyatları girin; etiketse — satırı kaldırın (✕) ve ürün kartındaki PLU'yu kontrol edin.",
                    $"Kod tarozi yorlig'iga o'xshaydi (mahsulot kodi {weightLabel.Label.ProductCode}), lekin bunday PLU/artikulli vaznli mahsulot yo'q. Oddiy shtrix-kod bo'lsa — nom va narxlarni kiriting; yorliq bo'lsa — qatorni olib tashlang (✕) va mahsulot kartasidagi PLU ni tekshiring."), warning: true);
            else if (added && line.IsNew)
                ReceiveMessage(T("Новый товар: впишите название, цену закупки и цену продажи — он появится на складе при оприходовании.",
                    "Жаңы товар: атын, сатып алуу жана сатуу баасын жазыңыз — кирешке алганда кампада пайда болот.",
                    "New product: enter the name, purchase and selling price — it appears in stock when posted.",
                    "Yeni ürün: adı, alış ve satış fiyatını girin — kaydedildiğinde stokta görünür.",
                    "Yangi mahsulot: nomi, xarid va sotuv narxini kiriting — kirim qilinganda omborda paydo bo'ladi."));
            s.Scan.Text = "";
            ReceiveShowTop(s, target, false);
            s.RepeatGuard.Counted(code);
        }
        catch (AmbiguousBarcodeException ambiguous)
        {
            // 2026-11-11 (ответ Умара): один штрихкод у нескольких товаров — товар не подставляем, строку не добавляем; код в журнале замаскирован.
            PosLogger.Log($"Закупки: скан {PurchaseReceivingService.MaskCode(code)} — неоднозначный штрихкод, строка не добавлена.", "WARNING");
            ReceiveMessage(ambiguous.Message, warning: true);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: скан {code} не обработан ({ex.Message}).", "WARNING");
            ReceiveMessage(T("Не удалось найти товар: ", "Товар табылган жок: ", "Couldn't find the product: ", "Ürün bulunamadı: ", "Mahsulot topilmadi: ") + ex.Message, warning: true);
        }
        finally
        {
            s.Scan.IsEnabled = true;
            s.Scan.Focus();
        }
    }

    /// <summary>2026-11-09: окно «Сколько штук?» после повторного скана того же кода. «Добавить» прибавляет N к ТОЙ ЖЕ строке (не дубль) и ведёт курсор как после обычного скана,
    /// «Отмена» ничего не меняет (курсор — в поле скана). Скан во время окна окно игнорирует (ReceivingRepeatQtyDialog).</summary>
    private async Task ReceiveAskRepeatAsync(ReceiveState s, string code, ReceivingLineVm line)
    {
        var add = await ReceivingRepeatQtyDialog.AskAsync(this, line, 1).ConfigureAwait(true);
        if (_receive != s)
            return;
        s.RepeatGuard.Counted(code); // после окна (любой исход) этот код по-прежнему «последний»: ещё один скан подряд — снова вопрос
        if (add is { } n && n > 0 && s.Lines.Contains(line))
        {
            ReceivingRepeatScan.AddTo(line, n);
            ReceiveShowTop(s, line, false);
        }
        else
            ReceiveFocusScan(s);
    }

    /// <summary>2026-10-24 (Б2): при выбранном поставщике его товары идут в подсказках первыми.
    /// 2026-11-06 (Б-12): список памяти, затем локальная база кассы (без связи память может быть пуста). Умное сопоставление накладных (сокращения, единицы) — только по Enter, см. ReceiveAddByCodeAsync (не на каждый символ).</summary>
    private static IEnumerable<CatalogProductTileVm> ReceiveMatches(string text, HashSet<string>? firstIds = null)
    {
        // Подсказки на каждый символ: базу читаем только если её кэш уже готов (иначе UI-поток ждал бы его построения) — готовность запускается в фоне.
        var repo = LocalProductRepository.Instance;
        return ReceivingNameSearch.Find(text, CatalogCacheService.Products.ToArray(),
            () => { if (repo.IsCacheReady) return repo.LoadAllTiles(); _ = repo.EnsureCacheReadyAsync(); return []; }, firstIds).Tiles;
    }

    /// <summary>Подсказки под полем: до 8 товаров по названию/штрихкоду (нажатие — в приём).</summary>
    private void ReceiveSuggest(IReadOnlyList<CatalogProductTileVm>? forced = null)
    {
        if (_receive is not { } s)
            return;
        s.Suggest.Children.Clear();
        var text = (s.Scan.Text ?? "").Trim();
        var digitsOnly = text.Length > 0 && text.All(char.IsDigit);
        var matches = forced?.ToList() ?? (digitsOnly && text.Length >= 8 ? new List<CatalogProductTileVm>() : ReceiveMatches(text, s.SupplierIds).ToList()); // 2026-11-06 (Б-12): forced — «похожие» из умного сопоставления
        foreach (var p in matches)
        {
            var name = new TextBlock { Text = p.Title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            Use(name, TextBlock.ForegroundProperty, "BrushText");
            var meta = new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(p.Barcode) ? "" : p.Barcode + " · ")}{T("остаток", "калдык", "stock", "stok", "qoldiq")} {Q(p.Quantity)}", FontSize = 12 };
            Use(meta, TextBlock.ForegroundProperty, "BrushTextSoft");
            var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            content.Children.Add(new StackPanel { Spacing = 1, Children = { name, meta } });
            // 2026-10-24 (Б2): значок «поставщик» — товар выбранного поставщика.
            if (s.SupplierIds.Contains(p.Id))
            {
                var badge = SectionCards.Pill(this, T("поставщик", "жеткирүүчү", "supplier", "tedarikçi", "yetkazib beruvchi"), "BrushSuccessSoft", "BrushSuccess");
                badge.VerticalAlignment = VerticalAlignment.Center;
                badge.Margin = new Thickness(8, 0, 0, 0);
                Grid.SetColumn(badge, 1);
                content.Children.Add(badge);
            }
            var b = new Button
            {
                Content = content, MinHeight = 44, Padding = new Thickness(10, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, CornerRadius = new CornerRadius(8),
            };
            Avalonia.Automation.AutomationProperties.SetName(b, p.Title);
            b.Click += (_, _) =>
            {
                s.Scan.Text = "";
                ReceiveAddTile(p, 1);
            };
            s.Suggest.Children.Add(b);
        }
        s.SuggestBox.IsVisible = matches.Count > 0;
    }

    private void ReceiveAddTile(CatalogProductTileVm tile, double qty, bool fromPanel = false, bool byScan = false)
    {
        if (_receive is not { } s)
            return;
        s.RepeatGuard.Reset(); // 2026-11-09: товар добавлен не сканом кода (название, подсказка, панель поставщика) — отсчёт «тот же код подряд» заново; скан кода после этого вызывает Counted
        var line = s.Lines.FirstOrDefault(x => string.Equals(x.ProductId, tile.Id, StringComparison.OrdinalIgnoreCase));
        if (line is not null && byScan)
            ReceivingRepeatScan.AddTo(line, qty); // 2026-11-11 (Б-4, как «Склад → Приёмка»): скан кода к строке в режиме упаковок — +1 упаковка (в единицах колонки «Принимаем»), а не +1 штука
        else if (line is not null)
            line.Quantity += qty;
        else
            line = PurchaseReceivingService.FromTile(tile, qty);
        ReceiveShowTop(s, line, fromPanel);
    }

    // ------------------------------------------------------------------ 2026-10-24, ТЗ «Склад» (Б2): строка сверху, курсор по ячейкам, подсветка

    /// <summary>Строка (новая или найденная повторным сканом) встаёт первой в списке: курсор — в «Количество» с выделенным числом (у нового товара без
    /// названия — в «Название»), строка 1,5 с подсвечена. Флажок «Сразу к скану» оставляет курсор в поле скана.</summary>
    private void ReceiveShowTop(ReceiveState s, ReceivingLineVm line, bool fromPanel)
    {
        s.AddedFromPanel = fromPanel;
        s.SuggestBox.IsVisible = false;
        var shown = s.RowMap.TryGetValue(line, out var parts);
        var index = s.Lines.IndexOf(line);
        if (index < 0)
            ReceiveInitLine(s, line); // 2026-11-06: закупка по умолчанию (поставщик → карточка), продажа — из каталога; общая наценка только для товаров без известной цены (было EnableAutoSale: 2026-10-28)
        // 2026-11-06 (жалоба владельца «при приёмке очередь и порядок сбиваются»): НОВАЯ строка встаёт наверх (как задумано), а повторный скан товара, который уже в списке,
        // только прибавляет количество В ТОЙ ЖЕ строке — она не перескакивает наверх (раньше каждый повторный скан вырывал строку и порядок списка, а с ним и порядок
        // строк в отправляемом приходе, менялся). Строка подсвечивается и прокручивается в поле зрения.
        ReceivingListOrder.Place(s.Lines, line);
        if (shown)
        {
            if (index < 0)
            {
                var at = s.Rows.Children.IndexOf(parts!.Box);
                if (at > 0)
                {
                    s.Rows.Children.RemoveAt(at);
                    s.Rows.Children.Insert(0, parts.Box);
                }
                s.Scroll.ScrollToHome();
            }
            else
                parts!.Box.BringIntoView();
            ReceiveRefresh();
        }
        else if (index >= 0 || s.RowMap.Count == 0 || s.RowMap.Count != s.Lines.Count - 1)
            ReceiveRender(); // первая строка заменяет заглушку «Отсканируйте товары…» (или список разошёлся с таблицей — пересобираем)
        else
        {
            s.Rows.Children.Insert(0, ReceiveRow(line));
            s.Scroll.ScrollToHome();
            ReceiveRefresh();
        }
        ReceiveHighlight(s, line);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_receive != s || !s.Lines.Contains(line))
                return;
            if (s.FastNext.IsChecked == true)
                ReceiveFocusScan(s);
            else
                ReceiveFocusCell(s, line);
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>2026-11-06: цены новой строки «Приём товара». Закупка — последняя у выбранного поставщика (история закупок), иначе из карточки, иначе 0;
    /// продажа — текущая из каталога (FromTile); наценка считается из двух цен. Общая «Наценка для всех» применяется только к товару без известной
    /// цены продажи (а если цены нет в карточке, но есть в прошлой закупке — берётся она).</summary>
    private void ReceiveInitLine(ReceiveState s, ReceivingLineVm line)
    {
        var history = ReceiveLocalHistory(line);
        ReceivingDefaults.ApplyPurchase(line, ReceiveSupplierPrice(s, line, history));
        line.EnableReceivingPricing(ReceivingDefaults.LastSalePrice(history));
    }

    private static List<PurchaseReceivingService.HistoryEntry> ReceiveLocalHistory(ReceivingLineVm line)
    {
        if (line.IsNew || string.IsNullOrWhiteSpace(line.ProductId))
            return new List<PurchaseReceivingService.HistoryEntry>();
        try
        {
            return PurchaseReceivingService.Instance.LoadLocalHistory(line.ProductId!);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: история закупок товара {line.ProductId} не прочитана ({ex.Message}).", "WARNING");
            return new List<PurchaseReceivingService.HistoryEntry>();
        }
    }

    private static ReceivingDefaults.SupplierPrice? ReceiveSupplierPrice(ReceiveState s, ReceivingLineVm line, IEnumerable<PurchaseReceivingService.HistoryEntry> history)
    {
        if (s.Picker.Selected is not { } supplier)
            return null;
        ReceivingDefaults.SupplierPrice? server = line.ProductId is { Length: > 0 } id && s.SupplierPrices.TryGetValue(id, out var sp) ? sp : null;
        return ReceivingDefaults.Newest(ReceivingDefaults.FromHistory(history, supplier.Name), server);
    }

    /// <summary>2026-11-06: поставщика сменили (или его закупки дозагрузились) — пока цену закупки не правили руками, она берётся из истории этого поставщика.</summary>
    private void ReceiveReapplyPurchases(ReceiveState s)
    {
        if (_receive != s)
            return;
        var changed = false;
        foreach (var line in s.Lines.ToList())
            if (!line.IsNew && !line.PurchaseIsManual)
                changed |= ReceivingDefaults.ApplyPurchase(line, ReceiveSupplierPrice(s, line, ReceiveLocalHistory(line)));
        if (changed)
            ReceiveRefresh();
    }

    /// <summary>2026-11-06: «Применить ко всем» — явное применение общей наценки ко всем строкам (кроме тех, где цену правили руками).</summary>
    private void ReceiveApplyMarkupToAll(ReceiveState s)
    {
        if (_receive != s)
            return;
        var settings = ReceivingMarkupSettingsStore.Current;
        if (!settings.AutoEnabled)
        {
            // 2026-11-06: общая наценка включается числом в поле «Наценка для всех, %».
            ReceiveMessage(T("Общая наценка выключена — введите число в поле «Наценка для всех, %».", "Жалпы кошумча өчүк — «Баарына кошумча, %» талаасына санды киргизиңиз.", "The common markup is off — enter a number in the “Markup for all, %” field.",
                "Ortak kâr kapalı — «Tümü için kâr, %» alanına bir sayı girin.", "Umumiy ustama o'chiq — «Hammasi uchun ustama, %» maydoniga son kiriting."), warning: true);
            return;
        }
        var changed = ReceivingPricing.ApplyCommonMarkupToAll(s.Lines, out var skipped);
        var pct = settings.DefaultPercent.ToString("0.##", Ru);
        var text = T($"Общая наценка {pct} % применена: цена продажи изменилась у {changed} {Tr.Plural(changed, "товара", "товаров", "товаров")}.",
            $"Жалпы кошумча {pct} % колдонулду: {changed} товардын сатуу баасы өзгөрдү.",
            $"The common markup {pct}% was applied: the sale price changed for {changed} {Tr.PluralEn(changed, "product", "products")}.",
            $"Ortak kâr oranı {pct} % uygulandı: {changed} ürünün satış fiyatı değişti.",
            $"Umumiy ustama {pct} % qo'llandi: {changed} ta mahsulotning sotuv narxi o'zgardi.");
        if (skipped > 0)
            text += " " + T($"Строк с ценой вручную пропущено: {skipped}.", $"Баасы кол менен коюлган саптар өткөрүлдү: {skipped}.", $"Lines with a hand-set price skipped: {skipped}.",
                $"Fiyatı elle girilen satırlar atlandı: {skipped}.", $"Narxi qo'lda kiritilgan qatorlar o'tkazib yuborildi: {skipped}.");
        ReceiveMessage(text, notice: true);
        ReceiveRefresh();
    }

}
