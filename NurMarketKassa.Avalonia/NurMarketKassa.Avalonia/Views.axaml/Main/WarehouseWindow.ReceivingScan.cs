using NurMarketKassa.Core.Application;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-24, ТЗ «Склад — ревизия, приёмка, поставщик, Telegram-группа», пакеты Б1 и В. Склад → Приёмка: «скан → строка».
/// • Скан (или выбор товара) ставит строку СВЕРХУ списка, курсор — в «Принято» с выделенным числом; Enter (и Tab) ведёт по ячейкам:
///   «Принято» → «Цена закупки» → «Цена продажи» → снова поле скана. Флажок «Сразу к следующему скану» оставляет курсор в поле скана.
/// • «Товары поставщика (N)» — панель с поиском и виртуализированным списком вместо 60 кнопок; подсказки поля скана показывают товары
///   выбранного поставщика первыми.
/// • «＋» рядом с выбором поставщика — создать поставщика (SupplierQuickCreate), он сразу выбран; в списке поставщиков есть поиск.
/// Деньги и остатки здесь не считаются — всё проводит PurchaseReceivingService (через WarehouseViewModel.CommitReceivingAsync).</summary>
public partial class WarehouseWindow
{
    private static string TR(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // ------------------------------------------------------------------ значок «поставщик» в подсказках скана

    /// <summary>Подпись значка у подсказки, если товар приходил от выбранного поставщика (x:Static в разметке).</summary>
    public static string SupplierBadgeText => TR("поставщик", "жеткирүүчү", "supplier", "tedarikçi", "yetkazib beruvchi");

    // Идентификаторы товаров выбранного поставщика. Статическая ссылка нужна конвертеру разметки; окно склада одно, при двух —
    // значок покажет товары того окна, где поставщика выбрали последним (на логику приёмки не влияет).
    private static HashSet<string> s_supplierProductIds = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _supplierProductIds = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IValueConverter IsSupplierProductConverter =
        new FuncValueConverter<string?, bool>(id => !string.IsNullOrEmpty(id) && s_supplierProductIds.Contains(id));

    private bool IsSupplierProduct(string? productId) => !string.IsNullOrEmpty(productId) && _supplierProductIds.Contains(productId);

    // ------------------------------------------------------------------ колонки таблицы приёмки

    // 2026-10-29, спека Fable: порядок — Название · Штрихкод · Ед. · На складе · Принимаем · Закупка · Наценка % · Цена продажи · Закупка изменилась · Как посчитана цена ·
    // Прибыль с шт. · Сумма закупки · Размеры · ✕ (колонки «Источник» нет: источник — подстрока под названием).
    private DataGridColumn RcvColName => ReceivingGrid.Columns[0];
    private DataGridColumn RcvColBarcode => ReceivingGrid.Columns[1];
    private DataGridColumn RcvColUnit => ReceivingGrid.Columns[2];
    private DataGridColumn RcvColWas => ReceivingGrid.Columns[3];
    private DataGridColumn RcvColQty => ReceivingGrid.Columns[4];
    private DataGridColumn RcvColPurchase => ReceivingGrid.Columns[5];
    private DataGridColumn RcvColMarkup => ReceivingGrid.Columns[6];
    private DataGridColumn RcvColSale => ReceivingGrid.Columns[7];
    private DataGridColumn RcvColChange => ReceivingGrid.Columns[8];
    private DataGridColumn RcvColAuto => ReceivingGrid.Columns[9];
    private DataGridColumn RcvColProfit => ReceivingGrid.Columns[10];
    private DataGridColumn RcvColTotal => ReceivingGrid.Columns[11];
    private DataGridColumn RcvColSizes => ReceivingGrid.Columns[12];
    private DataGridColumn RcvColRemove => ReceivingGrid.Columns[13];

    // ------------------------------------------------------------------ запуск

    private void InitReceivingScan()
    {
        ReceivingHintText.Text = TR(
            "Сканируйте товары по накладной. Поставщика можно выбрать в любой момент до «Оприходовать». После скана курсор встаёт в «Принято»: число → Enter → цена закупки → Enter → цена продажи → Enter — и снова скан. Незнакомый штрихкод — новая строка: впишите название.",
            "Накладная боюнча товарларды сканерлеңиз. Жеткирүүчүнү «Кирешеге алуу» басылганга чейин каалаган убакта тандаса болот. Сканерден кийин курсор «Кабыл алынды» уячасына тургандыктан: сан → Enter → сатып алуу баасы → Enter → сатуу баасы → Enter — жана кайра скан. Белгисиз штрихкод — жаңы сап: атын жазыңыз.",
            "Scan the items of the delivery note. You can pick the supplier at any moment before “Receive”. After a scan the cursor lands in “Received”: number → Enter → purchase price → Enter → sale price → Enter — and scan again. An unknown barcode is a new line: type its name.",
            "İrsaliyedeki ürünleri okutun. Tedarikçiyi «Stoğa al»dan önce istediğiniz an seçebilirsiniz. Okutmadan sonra imleç «Kabul edilen» hücresinde olur: sayı → Enter → alış fiyatı → Enter → satış fiyatı → Enter — ve yine okutma. Bilinmeyen barkod — yeni satır: adını yazın.",
            "Nakladnoy bo'yicha mahsulotlarni skanerlang. Yetkazib beruvchini «Kirim qilish»dan oldin istalgan paytda tanlash mumkin. Skanerdan keyin kursor «Qabul qilindi» katakchasida turadi: son → Enter → xarid narxi → Enter → sotuv narxi → Enter — va yana skaner. Notanish shtrix-kod — yangi qator: nomini yozing.");
        ReceivingFastNextCheck.Content = TR("Сразу к скану", "Дароо сканерге", "Straight to scan", "Doğrudan okutmaya", "Darhol skanerga");
        // Подсказка вкладки (на низком окне она скрыта) — всплывающей подсказкой поля скана (полный текст); над таблицей — одна короткая строка.
        ToolTip.SetTip(ReceivingScanBox, ReceivingHintText.Text);
        ReceivingHintText.Text = TR("Как работает: скан → «Принимаем» → Enter → «Закупка» → Enter → снова скан. Цена продажи считается сама: закупка + наценка.",
            "Кандай иштейт: скан → «Кабыл алабыз» → Enter → «Сатып алуу» → Enter → кайра скан. Сатуу баасы өзү эсептелет: сатып алуу + кошумча.",
            "How it works: scan → “Receiving” → Enter → “Purchase” → Enter → scan again. The sale price is calculated for you: purchase + markup.",
            "Nasıl çalışır: okutma → «Alınıyor» → Enter → «Alış» → Enter → yine okutma. Satış fiyatı kendiliğinden hesaplanır: alış + kâr.",
            "Qanday ishlaydi: skan → «Qabul qilinadi» → Enter → «Xarid» → Enter → yana skan. Sotuv narxi o'zi hisoblanadi: xarid + ustama.");
        ReceivingHintText.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
        ReceivingHintText.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        ToolTip.SetTip(ReceivingFastNextCheck, TR("Курсор остаётся в поле скана, каждый скан прибавляет количество из поля рядом.",
            "Курсор сканер талаасында калат, ар бир скан жанындагы талаадагы санды кошот.",
            "The cursor stays in the scan field; every scan adds the quantity from the field next to it.",
            "İmleç okutma alanında kalır, her okutma yanındaki alandaki miktarı ekler.",
            "Kursor skaner maydonida qoladi, har bir skan yonidagi maydondagi sonni qo'shadi."));
        // 2026-10-29, спека Fable (Specs-1029/01) + жалоба «что означает Динамика»: заголовки простыми словами, подзаголовки, тултипы; числа и их заголовки — вправо.
        RcvNameHeader.Text = TR("Название", "Аталышы", "Name", "Ad", "Nomi");
        RcvBarHeader.Text = TR("Штрихкод", "Штрихкод", "Barcode", "Barkod", "Shtrix-kod");
        RcvUnitHeader.Text = TR("Ед.", "Бир.", "Unit", "Birim", "Birlik");
        RcvWasHeader.Text = TR("На складе", "Кампада", "In stock", "Depoda", "Omborda");
        RcvTakenHeader.Text = TR("Прини­маем", "Кабыл ала­быз", "Receiving", "Alınıyor", "Qabul qili­nadi");
        RcvPurchaseHeader.Text = TR("Закупка", "Сатып алуу", "Purchase", "Alış", "Xarid");
        RcvPurchaseSub.Text = TR("сом за шт.", "бирдик үчүн сом", "som per unit", "birim başına som", "dona uchun so'm");
        RcvMarkupHeader.Text = TR("Наценка %", "Кошумча %", "Markup %", "Kâr %", "Ustama %");
        RcvSaleHeader.Text = TR("Цена продажи", "Сатуу баасы", "Sale price", "Satış fiyatı", "Sotuv narxi");
        RcvChangeHeader.Text = TR("Закупка изменилась", "Сатып алуу өзгөрдү", "Purchase changed", "Alış değişti", "Xarid o'zgardi");
        RcvChangeSub.IsVisible = false; // «против прошлой» — в тултипе заголовка и в подстроке «было …»
        RcvAutoHeader.Text = TR("Как посчитана цена", "Баа кантип эсептелген", "How the price is set", "Fiyat nasıl hesaplandı", "Narx qanday hisoblangan");
        RcvProfitHeader.Text = TR("Прибыль с шт.", "Бирдиктен пайда", "Profit per unit", "Birim kârı", "Dona foydasi");
        RcvTotalHeader.Text = TR("Сумма закупки", "Сатып алуу суммасы", "Purchase total", "Alış tutarı", "Xarid summasi");
        ToolTip.SetTip(RcvWasHeader, TR("Сколько этого товара сейчас на складе (до приёмки).", "Бул товардан азыр кампада канча бар (кабыл алганга чейин).", "How much of this product is in stock now (before receiving).", "Bu üründen şu an depoda ne kadar var (kabulden önce).", "Bu mahsulotdan hozir omborda qancha bor (qabuldan oldin)."));
        ToolTip.SetTip(RcvTakenHeader, TR("Сколько привезли — впишите число.", "Канча алып келишти — санды жазыңыз.", "How many arrived — enter the number.", "Kaç adet geldi — sayıyı yazın.", "Nechta keldi — sonni yozing."));
        ToolTip.SetTip(RcvChangeHeader, TR("Сравнение с прошлой закупкой этого товара: дороже, дешевле или без изменений.", "Бул товардын мурунку сатып алуу баасы менен салыштыруу: кымбат, арзан же өзгөрүүсүз.",
            "Comparison with the previous purchase price of this product: higher, lower or unchanged.", "Bu ürünün önceki alış fiyatıyla karşılaştırma: pahalı, ucuz veya değişmemiş.", "Shu mahsulotning oldingi xarid narxi bilan taqqoslash: qimmat, arzon yoki o'zgarmagan."));
        ToolTip.SetTip(RcvAutoHeader, TR("Как посчитана цена продажи: «авто» — по общей наценке, «вручную» — вы поменяли сами; предупреждения о малой марже и цене ниже закупки. «Как у всех» возвращает общую наценку.",
            "Сатуу баасы кантип эсептелген: «авто» — жалпы кошумча боюнча, «кол менен» — өзүңүз өзгөрттүңүз; аз маржа жана сатып алуудан төмөн баа эскертүүлөрү. «Баарындай» жалпы кошумчаны кайтарат.",
            "How the sale price was set: “auto” — by the common markup, “manual” — you changed it; warnings for low margin and price below cost. “Like all” brings the common markup back.",
            "Satış fiyatı nasıl hesaplandı: «oto» — ortak kâr oranıyla, «elle» — siz değiştirdiniz; düşük marj ve alışın altı uyarıları. «Hepsi gibi» ortak oranı geri getirir.",
            "Sotuv narxi qanday hisoblangan: «avto» — umumiy ustama bo'yicha, «qo'lda» — o'zingiz o'zgartirgansiz; kam marja va xariddan past narx ogohlantirishlari. «Hammasi kabi» umumiy ustamani qaytaradi."));
        foreach (var h in new[] { RcvWasHeader, RcvTakenHeader, RcvPurchaseHeader, RcvPurchaseSub, RcvMarkupHeader, RcvSaleHeader, RcvProfitHeader, RcvTotalHeader })
        {
            h.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            h.TextAlignment = Avalonia.Media.TextAlignment.Right;
        }
        // Строка «цена ниже закупки» — красная, «маржа мало» — жёлтая (стили DataGridRow.rcv-below / rcv-low в разметке).
        var rowHooks = new Dictionary<DataGridRow, (ReceivingLineVm Line, System.ComponentModel.PropertyChangedEventHandler Handler)>();
        static void SetRowClass(DataGridRow row, string name, bool on)
        {
            if (on && !row.Classes.Contains(name))
                row.Classes.Add(name);
            else if (!on)
                row.Classes.Remove(name);
        }
        static void ApplyRowClasses(DataGridRow row, ReceivingLineVm l)
        {
            SetRowClass(row, "rcv-below", l.IsBelowCost);
            SetRowClass(row, "rcv-low", !l.IsBelowCost && l.IsLowMargin);
        }
        void UnhookRow(DataGridRow row)
        {
            if (rowHooks.Remove(row, out var old))
                old.Line.PropertyChanged -= old.Handler;
        }
        ReceivingGrid.LoadingRow += (_, e) =>
        {
            UnhookRow(e.Row);
            if (e.Row.DataContext is not ReceivingLineVm line)
                return;
            System.ComponentModel.PropertyChangedEventHandler handler = (_, _) => ApplyRowClasses(e.Row, line);
            line.PropertyChanged += handler;
            rowHooks[e.Row] = (line, handler);
            ApplyRowClasses(e.Row, line);
        };
        ReceivingGrid.UnloadingRow += (_, e) => UnhookRow(e.Row);
        ReceivingMarkupButton.Content = TR("Наценка…", "Кошумча %…", "Markup…", "Kâr oranı…", "Ustama…");
        ToolTip.SetTip(ReceivingMarkupButton, TR("Автоматическая наценка: цена продажи считается по закупке; наценка по умолчанию, по категориям, округление.",
            "Автоматтык кошумча: сатуу баасы сатып алуу баасы боюнча эсептелет; демейки жана категория боюнча кошумча, тегеректөө.",
            "Automatic markup: the sale price is calculated from the purchase price; default and per-category markup, rounding.",
            "Otomatik kâr oranı: satış fiyatı alış fiyatından hesaplanır; varsayılan ve kategori kârı, yuvarlama.",
            "Avtomatik ustama: sotuv narxi xarid narxidan hisoblanadi; standart va kategoriya ustamasi, yaxlitlash."));
        // 2026-10-28: «Наценка для всех, %» над списком: ввод пересчитывает все строки в режиме «авто» на лету, запоминается при выходе из поля (Enter/уход).
        ReceivingMarkupLabel.Text = TR("Наценка для всех, %", "Баарына кошумча, %", "Markup for all, %", "Tümü için kâr, %", "Hammasi uchun ustama, %");
        ReceivingMarkupBox.Text = ReceivingMarkupSettingsStore.Current.DefaultPercent.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        Avalonia.Automation.AutomationProperties.SetName(ReceivingMarkupBox, ReceivingMarkupLabel.Text);
        var markupTyping = false;
        void MarkupFieldCommit(bool persist)
        {
            if (CheckoutValidation.TryParseAmount(ReceivingMarkupBox.Text, out var percent) == CheckoutValidation.AmountParse.Ok && percent <= 1000)
            {
                markupTyping = true;
                try { ReceivingMarkupSettingsStore.SetDefaultPercent(percent, persist); }
                finally { markupTyping = false; }
            }
            else if (persist)
                ReceivingMarkupBox.Text = ReceivingMarkupSettingsStore.Current.DefaultPercent.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
        }
        ReceivingMarkupBox.TextChanged += (_, _) => { if (!markupTyping && ReceivingMarkupBox.IsFocused) MarkupFieldCommit(false); };
        ReceivingMarkupBox.LostFocus += (_, _) => MarkupFieldCommit(true);
        ReceivingMarkupBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; MarkupFieldCommit(true); ReceivingScanBox.Focus(); } };
        Action markupChanged = () => Dispatcher.UIThread.Post(() =>
        {
            var auto = ReceivingMarkupSettingsStore.Current.AutoEnabled;
            ReceivingMarkupBox.IsEnabled = auto;
            ToolTip.SetTip(ReceivingMarkupBox, auto ? null : TR("Автонаценка выключена в окне «Наценка…».", "Автоматтык кошумча «Кошумча %…» терезесинде өчүрүлгөн.",
                "Automatic markup is turned off in the “Markup…” window.", "Otomatik kâr oranı «Kâr oranı…» penceresinde kapalı.", "Avtomatik ustama «Ustama…» oynasida o'chirilgan."));
            if (!ReceivingMarkupBox.IsFocused)
                ReceivingMarkupBox.Text = ReceivingMarkupSettingsStore.Current.DefaultPercent.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            if (auto)
                ReceivingPricing.ApplyAutoToAll(_viewModel.ReceivingLines);
            RefreshReceivingSummary();
        });
        ReceivingMarkupSettingsStore.Changed += markupChanged;
        Closed += (_, _) => ReceivingMarkupSettingsStore.Changed -= markupChanged;
        ToolTip.SetTip(ReceivingNewSupplierButton, TR("Новый поставщик", "Жаңы жеткирүүчү", "New supplier", "Yeni tedarikçi", "Yangi yetkazib beruvchi"));
        if (OfflineModeHelper.UseLocalOperations)
        {
            ReceivingNewSupplierButton.IsEnabled = false;
            ToolTip.SetTip(ReceivingNewSupplierButton, TR("Поставщика можно создать только при связи с сервером.", "Жеткирүүчүнү сервер менен байланышта гана түзсө болот.",
                "A supplier can only be created while connected to the server.", "Tedarikçi yalnızca sunucuya bağlıyken oluşturulabilir.",
                "Yetkazib beruvchini faqat server bilan aloqada yaratish mumkin."));
        }
        ReceivingSupplierSearch.Watermark = TR("Поиск по названию или телефону", "Аталышы же телефону боюнча издөө", "Search by name or phone",
            "Ad veya telefona göre ara", "Nomi yoki telefon bo'yicha qidirish");
        ReceivingSupplierProductsSearch.Watermark = TR("Найти товар поставщика: название или штрихкод — Enter добавит первый",
            "Жеткирүүчүнүн товарын табуу: аталышы же штрихкод — Enter биринчисин кошот", "Find a supplier product: name or barcode — Enter adds the first",
            "Tedarikçi ürününü bul: ad veya barkod — Enter ilkini ekler", "Yetkazib beruvchi mahsulotini topish: nomi yoki shtrix-kod — Enter birinchisini qo'shadi");

        // Enter / Tab в правке ячейки приёмки — по цепочке «Принято» → «Закупка» → «Продажа» → поле скана (туннельно: раньше, чем DataGrid уведёт курсор вниз).
        ReceivingGrid.AddHandler(KeyDownEvent, ReceivingGrid_PreviewKeyDown, RoutingStrategies.Tunnel);

        // 2026-10-26, приёмка (Д-2): в программе владельца в пустой приёмке видели «строку-призрак» («Новый · шт · 0 · 1 …») поверх «Накладная пока
        // пустая», хотя в списке 0 позиций. Причину в стенде воспроизвести не удалось, поэтому — защита от симптома: после каждого изменения
        // списка, если таблица показывает строк больше, чем в списке, она получает источник заново (лишние строки пропадают).
        _viewModel.ReceivingLines.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(SweepStaleReceivingRows, DispatcherPriority.Background);

        // Открыли вкладку — курсор в поле скана. e.Source: SelectionChanged всплывает от вложенных списков (замечание к Р2).
        WarehouseTabs.SelectionChanged += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, WarehouseTabs) || !ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem))
                return;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(WarehouseTabs.SelectedItem, ReceivingTabItem))
                    ReceivingScanBox.Focus();
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>2026-10-26 (Д-2): таблица приёмки показывает больше строк, чем в списке, — переустановить источник (см. InitReceivingScan).
    /// В норме не делает ничего: число видимых строк не больше числа позиций (прокрутка показывает меньше, лишних не бывает).</summary>
    private void SweepStaleReceivingRows()
    {
        try
        {
            if (!ReceivingGrid.IsEffectivelyVisible)
                return;
            var shown = ReceivingGrid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsVisible && r.Bounds.Height > 0);
            if (shown <= _viewModel.ReceivingLines.Count)
                return;
            PosLogger.Log($"Приёмка: таблица показывала лишние строки ({shown} при {_viewModel.ReceivingLines.Count} в списке) — таблица обновлена.", "WARNING");
            var source = ReceivingGrid.ItemsSource;
            ReceivingGrid.ItemsSource = null;
            ReceivingGrid.ItemsSource = source;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: проверка лишних строк таблицы не удалась: {ex.Message}", "DEBUG");
        }
    }

    // ------------------------------------------------------------------ строка добавлена: наверх, курсор в «Принято»

    /// <summary>Скан / выбор товара добавил (или нашёл) строку: она уже первая в списке. Курсор — в «Принято» (у нового товара без названия — в «Название»);
    /// с флажком «Сразу к следующему скану» остаётся в поле скана.</summary>
    private void OnReceivingLineAdded(ReceivingLineVm line)
    {
        RefreshReceivingSummary();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_viewModel.ReceivingLines.Contains(line))
                return;
            ReceivingGrid.SelectedItem = line;
            ReceivingGrid.ScrollIntoView(line, null);
            if (ReceivingFastNextCheck.IsChecked == true)
            {
                ReceivingScanBox.Focus();
                return;
            }
            BeginReceivingEdit(line, string.IsNullOrWhiteSpace(line.ProductName) ? RcvColName : RcvColQty);
        }, DispatcherPriority.Background);
    }

    /// <summary>Правка ячейки строки с выделенным текстом (число сразу заменяется набором). Предыдущая правка дописывается.</summary>
    private void BeginReceivingEdit(ReceivingLineVm line, DataGridColumn column)
    {
        if (!_viewModel.ReceivingLines.Contains(line) || !column.IsVisible)
            return;
        ReceivingGrid.CommitEdit();
        ReceivingGrid.SelectedItem = line;
        ReceivingGrid.ScrollIntoView(line, column);
        ReceivingGrid.CurrentColumn = column;
        if (!ReceivingGrid.BeginEdit())
            return;
        // Редактор появляется после разметки ячейки; фокус и выделение ставим сами — не зависим от версии DataGrid.
        Dispatcher.UIThread.Post(() =>
        {
            var editor = ReceivingGrid.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => t.IsVisible && t.GetVisualAncestors().OfType<DataGridCell>().Any());
            if (editor is null)
                return;
            editor.Focus();
            editor.SelectAll();
        }, DispatcherPriority.Input);
    }

    /// <summary>Enter и Tab в правке ячейки: «Название» → «Принято» → «Закупка» → «Продажа» → поле скана. Остальные ячейки — как в DataGrid.</summary>
    private void ReceivingGrid_PreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Tab) || e.KeyModifiers != KeyModifiers.None)
            return;
        if (e.Source is not TextBox editor || !editor.GetVisualAncestors().OfType<DataGridCell>().Any())
            return;
        if (ReceivingGrid.SelectedItem is not ReceivingLineVm line)
            return;

        var current = ReceivingGrid.CurrentColumn;
        DataGridColumn? next;
        if (ReferenceEquals(current, RcvColName))
            next = RcvColQty;
        else if (ReferenceEquals(current, RcvColQty))
            next = RcvColPurchase;
        else if (ReferenceEquals(current, RcvColPurchase))
            next = RcvColSale;
        else if (ReferenceEquals(current, RcvColSale))
            next = null;
        else
            return;

        e.Handled = true;
        if (!ReceivingGrid.CommitEdit())
            return;
        RefreshReceivingSummary();
        Dispatcher.UIThread.Post(() =>
        {
            if (next is null)
            {
                ReceivingGrid.SelectedItem = line;
                ReceivingScanBox.Focus();
                ReceivingScanBox.SelectAll();
            }
            else
                BeginReceivingEdit(line, next);
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ поставщик: список с поиском, «＋», панель товаров

    private bool _supplierListSync;
    private List<SupplierProductRow> _supplierProductRows = new();
    private CancellationTokenSource? _supplierProductsCts;
    private bool _supplierProductsOpen;

    /// <summary>Строка панели «Товары поставщика».</summary>
    private sealed class SupplierProductRow
    {
        public string Title { get; init; } = "";
        public string Info { get; init; } = "";
        public string SearchKey { get; init; } = "";
        public CatalogProductTileVm Tile { get; init; } = null!;
        public PurchaseReceivingService.SupplierProduct Item { get; init; } = null!;
    }

    private static string DigitsOnly(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Поиск в списке поставщиков — по названию и по цифрам телефона. «Без поставщика» остаётся всегда.</summary>
    private void ReceivingSupplierSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!ReceivingSupplierPanel.IsVisible)
            return;
        var query = (ReceivingSupplierSearch.Text ?? "").Trim();
        var digits = DigitsOnly(query);
        object source = query.Length == 0
            ? _viewModel.ReceivingSuppliers
            : _viewModel.ReceivingSuppliers.Where(s => string.IsNullOrEmpty(s.Id)
                || SearchMatch.Matches(query, s.Name) // 2026-10-29: единый поиск
                || (digits.Length >= 3 && DigitsOnly(s.Phone).Contains(digits, StringComparison.Ordinal))).ToList();
        _supplierListSync = true;
        try
        {
            ReceivingSupplierList.ItemsSource = (System.Collections.IEnumerable)source;
        }
        finally
        {
            _supplierListSync = false;
        }
    }

    /// <summary>«＋»: окно «Новый поставщик» (название и телефон). Такое же название — выбирается существующий, второй не создаётся.</summary>
    private async void ReceivingNewSupplier_Click(object? sender, RoutedEventArgs e)
    {
        if (OfflineModeHelper.UseLocalOperations || !ReceivingNewSupplierButton.IsEnabled)
            return;
        ReceivingNewSupplierButton.IsEnabled = false; // от двойного нажатия
        try
        {
            await _viewModel.LoadReceivingSuppliersAsync().ConfigureAwait(true);
            CloseReceivingPopups();
            var known = _viewModel.ReceivingSuppliers.Where(s => !string.IsNullOrEmpty(s.Id)).ToList();
            var result = await SupplierQuickCreate.ShowAsync(this, known).ConfigureAwait(true);
            if (result is null)
                return;
            _viewModel.SelectOrAddReceivingSupplier(result.Supplier);
            // 2026-10-27 (приёмка 26.10, Д-9): плашка под кнопкой «Оприходовать» вместо ShowToast — в программе владельца он открывался модальным окном.
            _viewModel.ShowReceivingInfo(result.Existed
                ? TR("Такой поставщик уже есть — выбран.", "Мындай жеткирүүчү бар — тандалды.", "This supplier already exists — selected.",
                    "Bu tedarikçi zaten var — seçildi.", "Bunday yetkazib beruvchi allaqachon bor — tanlandi.")
                : TR("Поставщик добавлен и выбран.", "Жеткирүүчү кошулду жана тандалды.", "Supplier added and selected.",
                    "Tedarikçi eklendi ve seçildi.", "Yetkazib beruvchi qo'shildi va tanlandi."));
            ReceivingScanBox.Focus();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Приёмка: окно «Новый поставщик» не открылось: {ex}", "WARNING");
        }
        finally
        {
            ReceivingNewSupplierButton.IsEnabled = !OfflineModeHelper.UseLocalOperations;
        }
    }

    /// <summary>Товары выбранного поставщика (приходили раньше): кнопка «Товары поставщика (N)» открывает панель с поиском. Список — все товары
    /// (до 600 и больше), виртуализированный; свежие приходы сверху.</summary>
    private async Task ShowSupplierProductsAsync()
    {
        _supplierProductsCts?.Cancel();
        _supplierProductsOpen = false;
        _supplierProductRows = new List<SupplierProductRow>();
        _supplierProductIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        s_supplierProductIds = _supplierProductIds;
        ReceivingSupplierProductsList.ItemsSource = null;
        ReceivingSupplierProductsSearch.Text = "";
        if (_viewModel?.ReceivingSupplier is not { Id.Length: > 0 } supplier)
        {
            ReceivingSupplierProductsBox.IsVisible = false;
            ReceivingSupplierProductsButton.IsVisible = false;
            return;
        }
        var cts = _supplierProductsCts = new CancellationTokenSource();
        ReceivingSupplierProductsBox.IsVisible = false;
        ReceivingSupplierProductsButton.IsVisible = true;
        ReceivingSupplierProductsButton.IsEnabled = false;
        ReceivingSupplierProductsButton.Content = TR("Товары поставщика…", "Жеткирүүчүнүн товарлары…", "Supplier products…", "Tedarikçi ürünleri…", "Yetkazib beruvchi mahsulotlari…");
        List<PurchaseReceivingService.SupplierProduct> products;
        try
        {
            products = await PurchaseReceivingService.Instance.LoadSupplierProductsAsync(supplier, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
                return;
            ReceivingSupplierProductsButton.IsVisible = false;
            ReceivingSupplierProductsBox.IsVisible = true;
            ReceivingSupplierProductsSearch.IsVisible = false;
            ReceivingSupplierProductsList.IsVisible = false;
            ReceivingSupplierProductsTitle.Text = TR("Товары поставщика не загрузились: ", "Жеткирүүчүнүн товарлары жүктөлгөн жок: ", "Supplier products didn't load: ",
                "Tedarikçi ürünleri yüklenemedi: ", "Yetkazib beruvchi mahsulotlari yuklanmadi: ") + ex.Message;
            return;
        }
        if (cts.IsCancellationRequested)
            return;

        var catalog = new Dictionary<string, CatalogProductTileVm>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in CatalogCacheService.Products)
            catalog.TryAdd(p.Id, p);
        var rows = new List<SupplierProductRow>();
        foreach (var item in products)
        {
            if (!catalog.TryGetValue(item.ProductId, out var tile))
                continue;
            var at = item.LastAt.ToLocalTime().ToString("dd.MM.yyyy");
            var price = item.LastPurchasePrice > 0 ? $" × {item.LastPurchasePrice:0.##}" : "";
            rows.Add(new SupplierProductRow
            {
                Title = tile.Title,
                Info = $"{at} · {item.LastQuantity:0.###}{price}",
                SearchKey = $"{tile.Title} {tile.Barcode} {tile.Article}",
                Tile = tile,
                Item = item,
            });
        }
        _supplierProductRows = rows;
        foreach (var row in rows)
            _supplierProductIds.Add(row.Tile.Id);

        ReceivingSupplierProductsSearch.IsVisible = rows.Count > 0;
        ReceivingSupplierProductsList.IsVisible = rows.Count > 0;
        if (rows.Count == 0)
        {
            ReceivingSupplierProductsButton.IsVisible = false;
            ReceivingSupplierProductsBox.IsVisible = true;
            ReceivingSupplierProductsTitle.Text = TR($"От «{supplier.Name}» раньше ничего не приходовали.", $"«{supplier.Name}» жеткирүүчүдөн мурда эч нерсе кириштелген эмес.",
                $"Nothing has been received from “{supplier.Name}” before.", $"«{supplier.Name}» tedarikçisinden daha önce bir şey alınmadı.",
                $"«{supplier.Name}» dan avval hech narsa kirim qilinmagan.");
            return;
        }
        ReceivingSupplierProductsButton.IsEnabled = true;
        ReceivingSupplierProductsButton.Content = TR($"Товары поставщика ({rows.Count})", $"Жеткирүүчүнүн товарлары ({rows.Count})",
            $"Supplier products ({rows.Count})", $"Tedarikçi ürünleri ({rows.Count})", $"Yetkazib beruvchi mahsulotlari ({rows.Count})");
        ReceivingSupplierProductsTitle.Text = TR($"Товары поставщика «{supplier.Name}» ({rows.Count}) — дата последнего прихода · количество × цена закупки. Найдите нужный и нажмите:",
            $"«{supplier.Name}» жеткирүүчүнүн товарлары ({rows.Count}) — акыркы кирешенин күнү · саны × сатып алуу баасы. Керектүүсүн тапып басыңыз:",
            $"Products of “{supplier.Name}” ({rows.Count}) — date of the last receipt · quantity × purchase price. Find the one you need and press it:",
            $"«{supplier.Name}» ürünleri ({rows.Count}) — son giriş tarihi · miktar × alış fiyatı. İhtiyacınız olanı bulup basın:",
            $"«{supplier.Name}» mahsulotlari ({rows.Count}) — oxirgi kirim sanasi · soni × xarid narxi. Keraklisini topib bosing:");
        ApplySupplierProductsFilter();
    }

    private void ApplySupplierProductsFilter()
    {
        var query = (ReceivingSupplierProductsSearch.Text ?? "").Trim();
        IEnumerable<SupplierProductRow> rows = _supplierProductRows;
        if (query.Length > 0)
            rows = rows.Where(r => SearchMatch.Matches(query, r.SearchKey)); // 2026-10-29: единый поиск
        ReceivingSupplierProductsList.ItemsSource = rows.ToList();
    }

    private void ReceivingSupplierProducts_Click(object? sender, RoutedEventArgs e)
    {
        _supplierProductsOpen = !_supplierProductsOpen;
        ReceivingSupplierProductsBox.IsVisible = _supplierProductsOpen;
        if (_supplierProductsOpen)
            ReceivingSupplierProductsSearch.Focus();
    }

    private void ReceivingSupplierProductsClose_Click(object? sender, RoutedEventArgs e)
    {
        _supplierProductsOpen = false;
        ReceivingSupplierProductsBox.IsVisible = false;
    }

    private void ReceivingSupplierProductsSearch_TextChanged(object? sender, TextChangedEventArgs e) => ApplySupplierProductsFilter();

    /// <summary>Enter в поиске панели — добавить первый найденный товар.</summary>
    private void ReceivingSupplierProductsSearch_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        if (ReceivingSupplierProductsList.ItemsSource is IEnumerable<SupplierProductRow> found && found.FirstOrDefault() is { } first)
            AddSupplierProductRow(first);
    }

    private void ReceivingSupplierProduct_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SupplierProductRow row })
            AddSupplierProductRow(row);
    }

    /// <summary>Нажатие «＋» у товара поставщика: строка сверху с количеством из поля рядом и последней ценой закупки у этого поставщика
    /// (если в строке цены закупки ещё нет); курсор — в «Принято».</summary>
    private void AddSupplierProductRow(SupplierProductRow row)
    {
        if (!double.TryParse(ReceivingQuantityBox.Text?.Replace(',', '.'), System.Globalization.NumberStyles.AllowDecimalPoint | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite | System.Globalization.NumberStyles.AllowTrailingWhite,
                System.Globalization.CultureInfo.InvariantCulture, out var quantity) || quantity <= 0)
            quantity = 1;
        ReceivingGrid.CommitEdit();
        var line = _viewModel.AddReceivingProduct(row.Tile, quantity);
        if (row.Item.LastPurchasePrice > 0 && line.PurchasePrice <= 0)
        {
            line.PurchasePrice = row.Item.LastPurchasePrice;
            line.ApplyAutoSale(); // 2026-10-28: последняя закупка у поставщика подставлена — цена по наценке (если строка в режиме «авто»)
        }
        ReceivingQuantityBox.Text = "1";
        ReceivingSupplierProductsSearch.Text = "";
        OnReceivingLineAdded(line);
    }

    // ------------------------------------------------------------------ 2026-10-28: автонаценка — «Авто» в строке и окно «Наценка…»

    private void ReceivingAutoSale_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ReceivingLineVm line })
        {
            ReceivingGrid.CommitEdit();
            line.ReturnToAuto();
            RefreshReceivingSummary();
        }
    }

    private async void ReceivingMarkup_Click(object? sender, RoutedEventArgs e)
    {
        ReceivingGrid.CommitEdit();
        await new ReceivingMarkupDialog().ShowDialog(this).ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ подорожание: «Обновить цены продажи (N)» и подтверждение перед «Оприходовать»

    /// <summary>Кнопка «Обновить цены продажи (N)» видна, пока есть строки с подорожанием и непоправленной продажей (ReceivingPricing.CountRaisable).</summary>
    private void RefreshReceivingRaiseButton()
    {
        var n = ReceivingPricing.CountRaisable(_viewModel.ReceivingLines);
        ReceivingRaiseButton.IsVisible = n > 0;
        if (n > 0)
            ReceivingRaiseButton.Content = TR($"Обновить цены продажи ({n})", $"Сатуу баасын жаңыртуу ({n})", $"Update sale prices ({n})",
                $"Satış fiyatlarını güncelle ({n})", $"Sotuv narxlarini yangilash ({n})");
    }

    private async void ReceivingRaise_Click(object? sender, RoutedEventArgs e)
    {
        ReceivingGrid.CommitEdit();
        var dialog = new ReceivingRaiseDialog(_viewModel.ReceivingLines.ToList());
        await dialog.ShowDialog(this).ConfigureAwait(true);
        if (dialog.AppliedCount > 0)
            PosLogger.Log($"Приёмка: «Обновить цены продажи» — {dialog.AppliedCount} {Tr.Plural(dialog.AppliedCount, "строка", "строки", "строк")}.", "STOCK");
        RefreshReceivingSummary();
    }

    /// <summary>Перед «Оприходовать»: «Цена продажи изменится у K товаров» со списком (прежняя → новая). false — кассир передумал.</summary>
    /// <summary>2026-10-29 (спека Fable): «Цена ниже закупки у N товаров» со списком (закупка → продажа, убыток с единицы). false — кассир передумал.</summary>
    private Task<bool> ConfirmBelowCostAsync(IReadOnlyList<ReceivingLineVm> below)
    {
        var shown = below.Take(12).Select(l =>
            $"• {(string.IsNullOrWhiteSpace(l.ProductName) ? l.Barcode : l.ProductName)}: {ReceivingLineVm.FormatPrice(l.PurchasePrice).Replace('.', ',')} → {ReceivingLineVm.FormatPrice(l.SalePrice).Replace('.', ',')}").ToList();
        if (below.Count > shown.Count)
            shown.Add(TR($"…и ещё {below.Count - shown.Count}", $"…жана дагы {below.Count - shown.Count}", $"…and {below.Count - shown.Count} more", $"…ve {below.Count - shown.Count} tane daha", $"…va yana {below.Count - shown.Count}"));
        var message = TR($"Цена продажи ниже закупки у {below.Count} {Tr.Plural(below.Count, "товара", "товаров", "товаров")} — каждая продажа будет в убыток:", $"{below.Count} товардын сатуу баасы сатып алуудан төмөн — ар бир сатуу чыгым менен болот:",
                $"The sale price is below the purchase price for {below.Count} {Tr.PluralEn(below.Count, "product", "products")} — every sale will lose money:", $"{below.Count} ürünün satış fiyatı alışın altında — her satış zararla olur:",
                $"{below.Count} ta mahsulotning sotuv narxi xariddan past — har bir sotuv zarar bilan bo'ladi:")
            + "\n\n" + string.Join("\n", shown);
        return PosConfirmDialog.ShowModalAsync(this,
            TR("Цена ниже закупки", "Баа сатып алуудан төмөн", "Price below cost", "Fiyat alışın altında", "Narx xariddan past"), message,
            TR("Всё равно провести", "Баары бир өткөрүү", "Post anyway", "Yine de kaydet", "Baribir o'tkazish"),
            TR("Вернуться", "Артка", "Go back", "Geri dön", "Orqaga"), PosConfirmAccent.Danger);
    }

    private Task<bool> ConfirmSalePriceChangesAsync(IReadOnlyList<ReceivingLineVm> changed)
    {
        var shown = changed.Take(12).Select(l =>
            $"• {(string.IsNullOrWhiteSpace(l.ProductName) ? l.Barcode : l.ProductName)}: {l.OriginalSalePrice:0.##} → {l.SalePrice:0.##}").ToList();
        if (changed.Count > shown.Count)
            shown.Add(TR($"…и ещё {changed.Count - shown.Count}", $"…жана дагы {changed.Count - shown.Count}", $"…and {changed.Count - shown.Count} more",
                $"…ve {changed.Count - shown.Count} tane daha", $"…va yana {changed.Count - shown.Count}"));
        // 2026-10-27 (приёмка 26.10, Д-8): склонение «у 1 товара / у 2 товаров», «for 1 product».
        var message = TR($"Цена продажи изменится у {changed.Count} {Tr.Plural(changed.Count, "товара", "товаров", "товаров")}:", $"{changed.Count} товардын сатуу баасы өзгөрөт:",
                $"The sale price will change for {changed.Count} {Tr.PluralEn(changed.Count, "product", "products")}:", $"{changed.Count} ürünün satış fiyatı değişecek:",
                $"{changed.Count} ta mahsulotning sotuv narxi o'zgaradi:")
            + "\n\n" + string.Join("\n", shown);
        return PosConfirmDialog.ShowModalAsync(this,
            TR("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"), message,
            TR("Оприходовать", "Кирешеге алуу", "Receive", "Stoğa al", "Kirim qilish"),
            TR("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), PosConfirmAccent.Primary);
    }
}
