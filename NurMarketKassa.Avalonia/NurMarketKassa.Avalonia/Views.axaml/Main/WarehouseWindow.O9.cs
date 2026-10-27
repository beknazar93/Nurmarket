using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-19, редизайн программы владельца, этап О9 («Товары и склад», аудит 19.10: 6,0 / 6,2). Только вид:
/// пустые состояния приёмки, ревизии, истории списаний и перемещений с подсказкой «что делать» (были пустые рамки таблиц),
/// узкие таблицы на 1024 — прячутся второстепенные колонки («Источник» и «Было» в приёмке, «Кассир» в истории списаний)
/// вместо прокрутки вбок, плитки товаров — по ширине списка. Колонки приёмки в коде — по именам (RcvCol*, 2026-10-24).</summary>
public partial class WarehouseWindow
{
    // Ширина таблицы, меньше которой второстепенная колонка прячется (1366 — таблица ~1180, 1024 — ~840).
    private const double ReceivingNarrowWidth = 1250; // 2026-10-27: было 1000 — между 1000 и 1206 широкий вид уже не помещался (прокрутка вбок, срезанные ячейки)
    private const double WriteOffHistoryNarrowWidth = 600;

    private void InitO9Look()
    {
        ReceivingEmptyTitle.Text = T("Накладная пока пустая", "Накладной азырынча бош", "The delivery note is empty", "İrsaliye henüz boş",
            "Nakladnoy hozircha bo'sh");
        ReceivingEmptyHint.Text = T("Отсканируйте первый товар накладной или нажмите «Выбрать из списка». Нового товара нет в базе — «+ Новый товар».",
            "Накладнойдун биринчи товарын сканерлеңиз же «Тизмеден тандоо» баскычын басыңыз. Жаңы товар базада жок болсо — «+ Жаңы товар».",
            "Scan the first item of the delivery note or press «Pick from list». Not in the base yet — «+ New product».",
            "İrsaliyenin ilk ürününü okutun veya «Listeden seç»e basın. Ürün yeni ise — «+ Yeni ürün».",
            "Nakladnoyning birinchi mahsulotini skanerlang yoki «Ro'yxatdan tanlash»ni bosing. Yangi mahsulot bo'lsa — «+ Yangi mahsulot».");
        // 2026-10-24, пакет Р2: пустое состояние ревизии убрано — вкладка собрана кодом (WarehouseWindow.Revision.cs).
        WriteOffEmptyTitle.Text = T("Списаний пока нет", "Эсептен чыгаруулар азырынча жок", "No write-offs yet", "Henüz düşüm yok",
            "Hozircha hisobdan chiqarishlar yo'q");
        WriteOffEmptyHint.Text = T("Здесь появятся акты списания с этой кассы: что, сколько, почему и кто списал.",
            "Бул жерде ушул кассадан эсептен чыгаруу актылары чыгат: эмне, канча, эмне үчүн жана ким.",
            "Write-off acts from this till will appear here: what, how many, why and who.",
            "Bu kasadaki düşüm belgeleri burada görünecek: ne, ne kadar, neden ve kim.",
            "Bu kassadagi hisobdan chiqarish aktlari shu yerda chiqadi: nima, qancha, nega va kim.");
        // 2026-10-27, аудит владельца В-2: у владельца история списаний — акты всех касс компании (сервер, GET inventory/sessions/)
        // плюс записи этого компьютера; раньше был только этот ПК, и подсказка так и говорила.
        if (AppMode.IsOwner)
            WriteOffEmptyHint.Text = T("Здесь появятся списания со всех касс магазина и из программы владельца: что, сколько, почему и кто списал. Если сервер недоступен, показаны только списания этого компьютера.",
                "Бул жерде дүкөндүн бардык кассаларынан жана ээсинин программасынан эсептен чыгаруулар чыгат: эмне, канча, эмне үчүн жана ким. Сервер жеткиликсиз болсо, ушул компьютердин эсептен чыгаруулары гана көрсөтүлөт.",
                "Write-offs from all tills of the shop and from the owner app will appear here: what, how many, why and who. If the server is unavailable, only this computer's write-offs are shown.",
                "Mağazanın tüm kasalarından ve sahip programından yapılan düşümler burada görünür: ne, ne kadar, neden ve kim. Sunucuya ulaşılamazsa yalnızca bu bilgisayardaki düşümler gösterilir.",
                "Do'konning barcha kassalaridan va egasining dasturidan hisobdan chiqarishlar shu yerda chiqadi: nima, qancha, nega va kim. Server mavjud bo'lmasa, faqat shu kompyuterdagi hisobdan chiqarishlar ko'rsatiladi.");

        void Sync()
        {
            ReceivingEmptyState.IsVisible = _viewModel.ReceivingLines.Count == 0;
            WriteOffEmptyState.IsVisible = _viewModel.WriteOffHistory.Count == 0;
        }
        _viewModel.ReceivingLines.CollectionChanged += (_, _) => Sync();
        _viewModel.WriteOffHistory.CollectionChanged += (_, _) => Sync();
        Sync();

        // Узкая таблица — второстепенная колонка прячется (по ширине самой таблицы, а не окна: в программе владельца
        // таблица уже окна на ширину меню).
        ReceivingGrid.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width <= 0)
                return;
            var wide = e.NewSize.Width >= ReceivingNarrowWidth;
            // Узкая таблица: прячутся «Источник», «Ед.» и «Было» (остаток до приёмки виден в карточке товара), числа плотнее,
            // штрихкод целиком (13 цифр), название не уже 170.
            // 2026-10-24, ТЗ «Склад» (Б1): колонки — по именам (RcvCol*), не по номерам; добавились «Наценка %» и «Изменение» — на 1024 прячется и «Ед.».
            RcvColSource.IsVisible = wide;
            RcvColWas.IsVisible = wide;
            RcvColUnit.IsVisible = wide;
            RcvColName.MinWidth = wide ? 200 : 170;
            // 2026-10-19: «Было» 72 → 84 — кыргызское «Болгон» переносилось посреди слова.
            // 2026-10-21 (пакет 1, п. 71): узкая таблица — числа чуть плотнее, название на 2 строки (стиль rcv-name), строка выше.
            var widths = wide
                ? new (DataGridColumn Column, double Width)[] { (RcvColBarcode, 160), (RcvColUnit, 56), (RcvColWas, 84), (RcvColQty, 92), (RcvColPurchase, 96), (RcvColMarkup, 100), (RcvColSale, 96), (RcvColChange, 120), (RcvColTotal, 104) }
                : new (DataGridColumn Column, double Width)[] { (RcvColBarcode, 158), (RcvColUnit, 56), (RcvColWas, 72), (RcvColQty, 88), (RcvColPurchase, 92), (RcvColMarkup, 100), (RcvColSale, 92), (RcvColChange, 120), (RcvColTotal, 84) };
            // 2026-10-27 (приёмка 26.10, Д-6): на окне ~1080 заголовки рвались по слогам («Принят о», «Нацен ка %»), «Динамика» обрезалась, штрихкод — до 12 цифр:
            // колонки узкого вида не уже, чем нужно слову заголовка, «Динамика» 120 (чтобы «маржа −97 %» не резалась), штрихкод 158 (13 цифр), «Наценка %» 100 (кыргызское «Кошумча» не рвётся). Узкий вид занимает 950 точек (с названием 170); уже 950 — «Сумма» прячется (итог виден внизу).
            RcvColTotal.IsVisible = wide || e.NewSize.Width >= 950;
            ReceivingGrid.RowHeight = wide ? 44 : 52;
            foreach (var (column, width) in widths)
                if (column.Width.Value != width)
                    column.Width = new DataGridLength(width);
        };
        // Перемещение: пустой журнал документов или движения — заголовок и подсказка по выбранному разделу.
        void SyncMovements()
        {
            var docs = TransfersGrid.IsVisible;
            var grid = docs ? TransfersGrid : MovementsGrid;
            var empty = grid.ItemsSource is not System.Collections.IEnumerable items || !items.GetEnumerator().MoveNext();
            MovementsEmptyState.IsVisible = empty;
            if (!empty)
                return;
            MovementsEmptyTitle.Text = docs
                ? T("Документов перемещения нет", "Жылдыруу документтери жок", "No transfer documents", "Transfer belgesi yok", "Ko'chirish hujjatlari yo'q")
                : T("За этот период со склада ничего не уходило", "Бул мезгилде кампадан эч нерсе чыккан жок", "Nothing left the warehouse in this period",
                    "Bu dönemde depodan çıkış olmadı", "Bu davrda ombordan hech narsa chiqmadi");
            MovementsEmptyHint.Text = docs
                ? T("Нажмите «+ Создать перемещение» и укажите, откуда и куда переносите товар.",
                    "«+ Жылдыруу түзүү» баскычын басып, товарды кайдан кайда жылдырып жатканыңызды көрсөтүңүз.",
                    "Press «+ New transfer» and set where the goods go from and to.",
                    "«+ Yeni transfer»e basın ve malın nereden nereye gittiğini belirtin.",
                    "«+ Yangi ko'chirish»ni bosing va mahsulot qayerdan qayerga ketishini ko'rsating.")
                : T("Здесь будут продажи и списания: что, когда и сколько ушло. Выберите период побольше.",
                    "Бул жерде сатуулар жана эсептен чыгаруулар болот: эмне, качан жана канча. Узунураак мезгилди тандаңыз.",
                    "Sales and write-offs will show here: what, when and how many. Try a longer period.",
                    "Satışlar ve düşümler burada görünür: ne, ne zaman, ne kadar. Daha uzun bir dönem seçin.",
                    "Bu yerda sotuvlar va hisobdan chiqarishlar bo'ladi: nima, qachon va qancha. Uzunroq davrni tanlang.");
        }
        foreach (var grid in new[] { TransfersGrid, MovementsGrid })
            grid.PropertyChanged += (_, e) =>
            {
                if (e.Property == DataGrid.ItemsSourceProperty || e.Property == IsVisibleProperty)
                    SyncMovements();
            };
        SyncMovements();

        // Плитки товаров: столько в ряд, сколько помещается плиток не уже 208, и ширина делится поровну.
        ProductsTilesScroll.SizeChanged += (_, e) =>
        {
            const double min = 208, gap = 12;
            var available = e.NewSize.Width - 4 - 14; // поле справа у списка и полоса прокрутки
            if (available <= min)
                return;
            var columns = Math.Max(1, (int)Math.Floor((available + gap) / (min + gap)));
            Resources["WarehouseTileWidth"] = Math.Floor((available + gap) / columns - gap);
        };

        WriteOffHistoryGrid.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && WriteOffHistoryGrid.Columns.Count > 4)
                WriteOffHistoryGrid.Columns[4].IsVisible = e.NewSize.Width >= WriteOffHistoryNarrowWidth;
        };

        InitPackage1();
    }

    // ------------------------------------------------------------------ 2026-10-21, пакет 1 «Товары»

    private void InitPackage1()
    {
        // П. 71: название в приёмке переносится на 2-ю строку (стиль DataGridCell.rcv-name в разметке) — на 1024 и
        // по-кыргызски было «Печенье «Юбилейное» трад».
        RcvColName.CellStyleClasses.Add("rcv-name"); // 2026-10-24: по имени колонки, не по номеру

        // П. 109: «Штучные» и «Поштучно» рядом не различались — второе «Поштучно из упаковки», у обоих подсказка.
        SaleUnitPiecePackageRadio.Content = T("Поштучно из упаковки", "Таңгактан даана менен", "Single pieces from a pack",
            "Paketten tek tek", "Qadoqdan donalab");
        ToolTip.SetTip(SaleUnitPieceRadio, T("Обычные штучные товары: продаются целиком, как есть.",
            "Кадимки даана товарлар: бүтүн бойдон сатылат.", "Regular piece goods sold as a whole.",
            "Normal adet ürünler: bütün olarak satılır.", "Oddiy donali mahsulotlar: butunligicha sotiladi."));
        ToolTip.SetTip(SaleUnitPiecePackageRadio, T("Товары, которые продаются и упаковкой, и по одной штуке из неё (сигареты блоком и пачкой и т. п.).",
            "Таңгагы менен да, андан бирден да сатылган товарлар (тамеки блогу жана пачкасы ж. б.).",
            "Goods sold both as a pack and one by one from it (cigarettes by the carton and by the pack, etc.).",
            "Hem paket hem de içinden tek tek satılan ürünler (sigara karton ve paket vb.).",
            "Ham qadoq bilan, ham undan donalab sotiladigan mahsulotlar (sigaret bloki va qutisi va h.k.)."));

        // П. 109: «Страница 1 из 7» — какие товары на странице и сколько всего.
        void SyncPage()
        {
            var total = _viewModel.FilteredProducts.Count;
            var shown = _viewModel.PagedProducts.Count;
            var first = shown == 0 ? 0 : _viewModel.FilteredProducts.IndexOf(_viewModel.PagedProducts[0]) + 1;
            var last = shown == 0 ? 0 : first + shown - 1;
            var page = _viewModel.CurrentProductPage;
            var pages = _viewModel.TotalProductPages;
            ProductPageStatus.Text = T($"Товары {first}–{last} из {total} · страница {page} из {pages}",
                $"Товарлар {first}–{last} / {total} · {page}/{pages}-бет",
                $"Products {first}–{last} of {total} · page {page} of {pages}",
                $"Ürünler {first}–{last} / {total} · sayfa {page}/{pages}",
                $"Mahsulotlar {first}–{last} / {total} · sahifa {page}/{pages}");
        }
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(WarehouseViewModel.ProductPageStatusText) or nameof(WarehouseViewModel.PagedProducts))
                SyncPage();
        };
        SyncPage();

        // П. 110: у редактора этикетки — цена и где купить, кнопка по состоянию (подключён — «Открыть», нет — «Ввести серийный номер»).
        RefreshLabelEditorTab();
        WarehouseTabs.SelectionChanged += (_, _) =>
        {
            if (ReferenceEquals(WarehouseTabs.SelectedItem, LabelEditorTabItem))
                RefreshLabelEditorTab();
        };
        LabelEditorOpenButton.Click += (_, _) => Dispatcher.UIThread.Post(RefreshLabelEditorTab);

        // П. 113: «Списать» из «Сроков годности» — этот склад открывает «Списание» с товаром (см. RequestWriteOff).
        WriteOffRequested += OnWriteOffRequested;
        Closed += (_, _) => WriteOffRequested -= OnWriteOffRequested;
    }

    private void RefreshLabelEditorTab()
    {
        var unlocked = UserPreferences.Instance.LabelEditorUnlocked;
        var fee = TariffGate.PackActivationFee;
        LabelEditorPaidText.Text = unlocked
            ? T("Подключено на этом компьютере.", "Бул компьютерде туташтырылган.", "Activated on this computer.",
                "Bu bilgisayarda etkin.", "Shu kompyuterda ulangan.")
            : T($"Платная функция — подключение {fee} сом. Купить: «Маркетплейс → Доп. функции» или у поддержки NurMarket; полученный серийный номер введите кнопкой ниже.",
                $"Акылуу функция — туташтыруу {fee} сом. Сатып алуу: «Маркетплейс → Кошумча функциялар» же NurMarket колдоо кызматы; алынган сериялык номерди төмөнкү баскыч менен киргизиңиз.",
                $"Paid feature — activation {fee} som. Buy it in «Marketplace → Extras» or from NurMarket support, then enter the serial number with the button below.",
                $"Ücretli özellik — etkinleştirme {fee} som. «Pazar yeri → Ek özellikler» bölümünden veya NurMarket destekten satın alın; aldığınız seri numarasını aşağıdaki düğmeyle girin.",
                $"Pullik funksiya — ulash {fee} so'm. Sotib olish: «Marketpleys → Qo'shimcha funksiyalar» yoki NurMarket qo'llab-quvvatlash xizmati; olingan seriya raqamini quyidagi tugma bilan kiriting.");
        LabelEditorOpenButton.Content = unlocked
            ? T("Открыть редактор", "Редакторду ачуу", "Open editor", "Düzenleyiciyi aç", "Muharrirni ochish")
            : T("Ввести серийный номер", "Сериялык номерди киргизүү", "Enter serial number", "Seri numarası gir", "Seriya raqamini kiritish");
    }

    /// <summary>П. 113: просьба «Списать товар» из «Пополнения и сроков» — открытым складам (в программе владельца это раздел «Склад»).</summary>
    private static event Action<string, string>? WriteOffRequested;

    /// <summary>2026-10-21 (пакет 1, п. 113): «Списать» в «Сроках годности» — склад на вкладке «Списание» с этим товаром и причиной
    /// «Просрочка». Программа владельца открывает раздел «Склад» (как пункт меню, с проверкой прав), касса — окно склада.</summary>
    public static void RequestWriteOff(Window from, string productId)
    {
        var reason = WarehouseViewModel.WriteOffReasons[1];
        if (AppMode.IsOwner && ProductActionPlan.OpenSection is { } openSection)
        {
            openSection("warehouse");
            // Раздел создаётся в той же очереди — просьба уходит после него.
            Dispatcher.UIThread.Post(() => WriteOffRequested?.Invoke(productId, reason));
            return;
        }

        if (!App.GetRequiredService<IPermissionService>().HasPermission(PosPermissions.ViewProducts))
        {
            PosLogger.Log($"Permission denied: {PosPermissions.ViewProducts}", "WARNING");
            PosDialogs.Error(from, T("Недостаточно прав для выполнения этой операции.", "Бул операцияны аткарууга укук жетишсиз.",
                    "Insufficient permissions to perform this operation.", "Bu işlemi gerçekleştirmek için yetkiniz yok.", "Bu amalni bajarish uchun huquq yetarli emas."),
                T("Склад", "Кампа", "Warehouse", "Depo", "Ombor"));
            return;
        }
        var warehouse = App.GetRequiredService<WarehouseWindow>();
        warehouse.Show(from.Owner as Window ?? from);
        warehouse.ShowWriteOff(productId, reason);
    }

    private void OnWriteOffRequested(string productId, string reason) => ShowWriteOff(productId, reason);

    /// <summary>Вкладка «Списание» с выбранным товаром и причиной (количество — как обычно, вписывает человек).</summary>
    public void ShowWriteOff(string productId, string reason)
    {
        if (!WriteOffTabItem.IsVisible)
            return;
        WarehouseTabs.SelectedItem = WriteOffTabItem;
        if (CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, productId, StringComparison.OrdinalIgnoreCase)) is { } product
            && _viewModel.WriteOffPickCommand.CanExecute(product))
            _viewModel.WriteOffPickCommand.Execute(product);
        SetWriteOffReason(reason);
        PosLogger.Log($"Склад: «Списать» из сроков годности — товар {productId}.", "STOCK");
        Activate();
        Dispatcher.UIThread.Post(() => WriteOffQuantityBox?.Focus());
    }

    /// <summary>2026-10-21 (пакет 1, п. 107): цены и количество в строках приёмки — с запятой («38,5»), как во всей программе
    /// (было «38.5» — строка приёмки пишет число в инвариантной культуре). Обратно — как ввели: разбор понимает и запятую, и точку.</summary>
    public static readonly IValueConverter CommaDecimal = new CommaDecimalConverter();

    private sealed class CommaDecimalConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is string s ? s.Replace('.', ',') : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
    }
}
