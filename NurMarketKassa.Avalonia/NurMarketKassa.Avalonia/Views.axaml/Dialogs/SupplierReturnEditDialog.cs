using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;
using Tile = NurMarketKassa.Models.Pos.CatalogProductTileVm;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-10 (ТЗ «правка и аннулирование возвратных накладных 24 ч»): правка проведённого возврата поставщику — существующие строки
/// (количество, цена закупки, скидка, примечание; количество 0 = убрать строку), комментарий и обязательная причина правки; сумма пересчитывается сразу
/// (расчёт — SupplierReturnEdit/ReturnLineMath в Infrastructure). «Сохранить»: проверка → свежая карточка с сервера (версия и срок по времени сервера) →
/// PIN руководителя (токен на эту правку и версию) → PATCH suppliers/returns/{id}/ (If-Match, Idempotency-Key). «Удалить возврат» — общий шаг ReturnEditFlows.VoidAsync.
/// Склад, долг поставщику и деньги пересчитывает сервер. Появляется только при признаке supplier_return_edit_24h и can_edit документа.
/// 2026-11-11 (реальный контракт Умара): скидку и примечание строки сервер НЕ хранит — полей для них в окне больше нет, в PATCH уходят только id, qty, purchase_price
/// (+ причина и комментарий документа); сумма строки = кол-во × цена.
/// Результат ShowDialog — true: документ изменён/аннулирован или выяснилось, что его изменили, — список надо перечитать.</summary>
public sealed class SupplierReturnEditDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    // 2026-11-12 (стресс-тест: 2000 строк — открытие 31 с и ~950 МБ памяти, 500 строк — 3,9 с и 240 МБ): значения строк живут в модели (QtyText/PriceText),
    // а поля ввода строятся только для показанной страницы (PageSize) — остальные строки по «Показать ещё» или через поиск по товару. Изменённые строки
    // сохраняются и уходят в PATCH, даже если сейчас скрыты.
    private sealed class Row
    {
        public SupplierReturnEditLine Source = null!;
        public string QtyText = "";
        public string PriceText = "";
        // 2026-11-12 (аудит В-4): исходные тексты полей — строка считается изменённой, только если текст поля отличается от исходного; иначе уходят исходные значения до последнего знака
        public string OrigQtyText = "";
        public string OrigPriceText = "";
        public TextBox? Qty;
        public TextBox? Price;
        public TextBlock? Sum;
        // 2026-11-12 (UX правки накладной): название (зачёркивается у убранной строки) и кнопка «убрать / вернуть строку»
        public TextBlock? Name;
        public Button? Remove;
    }

    private const int PageSize = 50;
    // 2026-11-12 (UX правки накладной): строка — одна линия таблицы «товар | кол-во | цена | сумма | ✕» (раньше название и два высоких поля с подписями в каждой строке:
    // на 1024×600 было видно 2–3 товара, а обязательная «Причина правки» уходила за прокрутку); шапка столбцов — один раз над списком
    private const string Cols = "*,12,112,8,124,8,108,8,44";

    private readonly SupplierReturnInfo _info;
    private readonly string _number;
    private readonly List<SupplierReturnEditLine> _original;
    private readonly List<Row> _rows = new();
    private readonly StackPanel _lines = new() { Spacing = 8 };
    private readonly Button _more;
    private readonly TextBox? _find;
    private readonly Avalonia.Threading.DispatcherTimer? _findTimer;
    private int _shown = PageSize;
    private string _appliedFilter = "";
    private readonly TextBox _comment;
    private readonly TextBox _reason;
    // 2026-11-12 (UX правки накладной): итог в подвале — подпись, крупная сумма и строка «было … · меньше/больше на …» (раньше одна строка 15 px «Сумма: … (было …)», в ky ломалась на две)
    private readonly TextBlock _total = new() { FontSize = 20, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _delta = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private string _totalText = "";
    private readonly TextBlock _preview = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private TextBlock _previewTitle = null!;
    private StackPanel _previewBox = null!;
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Button _save;
    private readonly Button _delete;
    private readonly ModalShell _shell;
    // 2026-11-12 (стресс-тест «обрыв связи на сохранении»): ключ повтора — по id возврата, а не по окну. Связь оборвалась после отправки, окно закрыли и открыли
    // снова — тот же состав той же версии уйдёт с тем же Idempotency-Key (сервер вернёт прежний итог, не проведёт второй раз). Другая версия/состав — новый ключ.
    private static readonly Dictionary<string, PendingIdempotencyKey> Keys = new(StringComparer.Ordinal);
    private ReturnEditApi? _api;
    private bool _changed;
    private bool _closed;
    private bool _asking;
    // 2026-11-21 (ответ Умара 21.11, п. 5): что сервер разрешает менять (editable_fields карточки; не прислал — по контракту), правка владельцем после 24 ч, причина и вид компенсации САМОГО возврата
    private readonly bool _ownerOverride;
    private readonly bool _canItems, _canReason, _canComp, _canComment;
    private string _retReason;
    private string _retComp;
    private readonly WrapPanel _reasonChips = new();
    private readonly StackPanel _compChips = new() { Spacing = 6 };
    private TextBox? _add;
    private readonly StackPanel _addResults = new() { Spacing = 6 };
    private readonly TextBlock _addNote = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public SupplierReturnEditDialog(SupplierReturnInfo info, string number, ReturnEditApi? api = null)
    {
        _info = info;
        _number = number;
        _api = api;
        _original = SupplierReturnEdit.FromInfo(info);
        _ownerOverride = ReturnEditGate.IsOwnerOverride(ReturnDocKind.Supplier, info.EditState, ReturnEditFlows.IsManager);
        _canItems = info.EditState.CanEditField("items");
        _canReason = info.EditState.CanEditField("reason");
        _canComp = info.EditState.CanEditField("compensation");
        _canComment = info.EditState.CanEditField("comment");
        _retReason = (info.Reason ?? "").Trim().ToLowerInvariant();
        _retComp = (info.Compensation ?? "").Trim().ToLowerInvariant();
        // 2026-11-12: заголовок как у кнопки «Править накладную» (владелец искал правку именно накладной)
        Title = T("Правка накладной возврата поставщику", "Жеткирүүчүгө кайтаруу накладнойун оңдоо", "Edit supplier return waybill", "Tedarikçi iade irsaliyesini düzenle", "Yetkazib beruvchiga qaytarish yuk xatini tahrirlash");
        SectionCards.AsModal(this, 800); // 2026-11-12 (UX): было 720 — строка-таблица с названием товара в одну-две линии

        var panel = new StackPanel { Spacing = 8 };
        if (_ownerOverride)
        {
            // 2026-11-21: правка владельцем после 24 ч — честная пометка, PIN и журнал
            var ownerNote = Soft("ⓘ " + ReturnEditFlows.OwnerOverrideText());
            ownerNote.Name = "SupplierReturnOwnerNote";
            panel.Children.Add(ownerNote);
        }
        foreach (var line in _original)
        {
            // 2026-11-12 (аудит В-4): цена показывается до 4 знаков (раньше «0.##»: 33,3333 → 33,33 и уходило округлённым), количество — до 4
            var qtyText = line.Qty.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
            var priceText = line.Price.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
            _rows.Add(new Row { Source = line, QtyText = qtyText, PriceText = priceText, OrigQtyText = qtyText, OrigPriceText = priceText });
        }
        _more = SectionCards.Secondary("…");
        _more.HorizontalAlignment = HorizontalAlignment.Left;
        _more.Click += (_, _) => ShowMore();
        // 2026-11-21 (владелец: «редактор возвраты поставщикам нету!»): поиск/скан строки — с 6 строк (раньше только с 51-й): штрихкод со сканера + Enter ставит курсор в количество этой строки
        if (_rows.Count > 5)
        {
            // 2026-11-12: длинная накладная — подсказка и поиск товара по названию (поиск с задержкой 300 мс, чтобы ввод не перестраивал строки на каждую букву)
            var n = _rows.Count;
            if (_rows.Count > PageSize)
            panel.Children.Add(Soft(T($"В накладной {n} строк — показаны по {PageSize}. Найдите товар поиском или нажмите «Показать ещё»; изменённые строки сохраняются, даже если скрыты.",
                $"Накладнойдо {n} сап — {PageSize} боюнча көрсөтүлөт. Товарды издөө менен табыңыз же «Дагы көрсөтүү» басыңыз; өзгөртүлгөн саптар жашырылса да сакталат.",
                $"The waybill has {n} lines — shown {PageSize} at a time. Find a product with search or press “Show more”; edited lines are kept even when hidden.",
                $"İrsaliyede {n} satır var — {PageSize}'er gösterilir. Ürünü aramayla bulun veya «Daha fazla göster»e basın; değiştirilen satırlar gizlense de korunur.",
                $"Yuk xatida {n} qator — {PageSize} tadan ko'rsatiladi. Mahsulotni qidiruv bilan toping yoki «Yana ko'rsatish»ni bosing; o'zgartirilgan qatorlar yashirilsa ham saqlanadi.")));
            _find = SectionCards.Field(new TextBox { Watermark = T("Найти или сканировать товар: название, штрихкод, артикул", "Товар издөө же сканерлөө: аталышы, штрих-код, артикул", "Find or scan a product: name, barcode, article", "Ürün ara veya okut: ad, barkod, stok kodu", "Mahsulotni qidirish yoki skanerlash: nomi, shtrixkod, artikul"), MaxLength = 100 });
            Avalonia.Automation.AutomationProperties.SetName(_find, _find.Watermark);
            _find.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter)
                    return;
                e.Handled = true;
                OnFindEnter();
            };
            var findTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            findTimer.Tick += (_, _) =>
            {
                findTimer.Stop();
                if ((_find?.Text ?? "").Trim() == _appliedFilter)
                    return; // поиск не изменился — страницу («Показать ещё») не сбрасываем
                _shown = PageSize;
                RenderRows();
            };
            _find.TextChanged += (_, _) =>
            {
                findTimer.Stop();
                findTimer.Start();
            };
            Closed += (_, _) => findTimer.Stop();
            _findTimer = findTimer;
            panel.Children.Add(_find);
        }
        RenderRows();
        // 2026-11-12 (UX правки накладной): шапка столбцов один раз над строками
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols) };
        var headTexts = new[]
        {
            T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
            T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Сумма", "Суммасы", "Total", "Tutar", "Summa"),
        };
        for (var i = 0; i < headTexts.Length; i++)
        {
            var h = Soft(headTexts[i]);
            h.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            Grid.SetColumn(h, i * 2);
            head.Children.Add(h);
        }
        panel.Children.Add(SectionCards.Group(this, new StackPanel { Spacing = 8, Children = { head, _lines, _more } }));
        var afterLines = panel.Children.Count; // 2026-11-21: сюда встанет строка «Причина правки / Комментарий»

        // 2026-11-21 (ответ Умара 21.11, п. 5): в возврат можно ДОБАВИТЬ строку — товар из каталога: поиск по названию/штрихкоду/артикулу или скан + Enter ({product, qty, purchase_price?}; без цены сервер берёт цену из прихода или закупочную товара)
        if (_canItems)
        {
            _add = SectionCards.Field(new TextBox { Watermark = T("Добавить товар: название, штрихкод или артикул — или сканируйте", "Товар кошуу: аталышы, штрих-код же артикул — же сканерлеңиз", "Add a product: name, barcode or article — or scan", "Ürün ekle: ad, barkod veya stok kodu — veya okutun", "Mahsulot qo'shish: nomi, shtrixkod yoki artikul — yoki skanerlang"), MaxLength = 100 });
            _add.Name = "SupplierReturnAddProduct";
            Avalonia.Automation.AutomationProperties.SetName(_add, _add.Watermark);
            _add.TextChanged += (_, _) => RenderAddResults();
            _add.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter)
                    return;
                e.Handled = true;
                OnAddEnter();
            };
            _addNote.Bind(TextBlock.ForegroundProperty, _addNote.GetResourceObservable("BrushTextSoft"));
            panel.Children.Add(new StackPanel { Spacing = 6, Children = { Label(T("Добавить товар в возврат", "Кайтарууга товар кошуу", "Add a product to the return", "İadeye ürün ekle", "Qaytarishga mahsulot qo'shish")), _add, _addResults, _addNote } });
        }

        // 2026-11-21: причина возврата и вид компенсации (сервер откатывает старую и проводит новую — это видно в «Что изменится»)
        if (_canReason)
        {
            panel.Children.Add(Label(T("Причина возврата", "Кайтаруунун себеби", "Return reason", "İade nedeni", "Qaytarish sababi")));
            panel.Children.Add(_reasonChips);
        }
        if (_canComp)
        {
            panel.Children.Add(Label(T("Что с деньгами", "Акча менен эмне", "What happens with the money", "Para ile ne olacak", "Pul bilan nima bo'ladi")));
            panel.Children.Add(_compChips);
        }
        BuildChips();

        // 2026-11-21 (ответ Умара 21.11): в этом окне не меняются только поставщик и дата возврата (дата — 400 field_not_editable; поля, которых нет в editable_fields карточки, тоже перечислены).
        // Нужно другое — «Удалить возврат» (если сервер разрешает) и оформить новый.
        var fixedInfo = Soft(T("Не меняется здесь: ", "Бул жерде өзгөрбөйт: ", "Not changeable here: ", "Burada değişmez: ", "Bu yerda o'zgarmaydi: ")
            + string.Join(" · ", new[]
            {
                info.SupplierName,
                info.CreatedAt is { } at ? at.ToLocalTime().ToString("dd.MM.yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "",
                _canReason ? "" : T("причина: ", "себеби: ", "reason: ", "neden: ", "sabab: ") + SupplierReturnsWindow.ReasonName(info.Reason),
                _canComp ? "" : SupplierReturnsWindow.CompensationName(info.Compensation),
            }.Where(x => x.Length > 0)) + ". "
            + T("Поставщика и дату возврата сервер менять не даёт. Нужно другое — «Удалить возврат» и оформите новый.",
                "Жеткирүүчүнү жана кайтаруу датасын сервер өзгөртүүгө жол бербейт. Башкасы керек болсо — «Кайтарууну өчүрүү» жана жаңысын тариздеңиз.",
                "The server does not allow changing the supplier or the return date. For anything else, press “Delete return” and make a new one.",
                "Sunucu tedarikçinin ve iade tarihinin değiştirilmesine izin vermez. Başka bir şey için «İadeyi sil» ve yenisini kaydedin.",
                "Server yetkazib beruvchini va qaytarish sanasini o'zgartirishga ruxsat bermaydi. Boshqasi kerak bo'lsa — «Qaytarishni o'chirish» va yangisini rasmiylashtiring."));
        fixedInfo.Name = "SupplierReturnFixedInfo";
        panel.Children.Add(fixedInfo);

        // 2026-11-12 (UX): «Причина правки» и «Комментарий» — в одну строку двумя столбцами (обязательная причина — первой, видна без прокрутки на 1024×600); подсказка в поле короче — подпись над полем уже есть
        _comment = SectionCards.Field(new TextBox { Text = info.Comment, Watermark = T("Комментарий документа", "Документтин комментарийи", "Document comment", "Belge yorumu", "Hujjat izohi"), MaxLength = 500, VerticalContentAlignment = VerticalAlignment.Center });
        _reason = SectionCards.Field(new TextBox { Watermark = T("Обязательно, от 5 знаков", "Милдеттүү, 5 белгиден", "Required, at least 5 characters", "Zorunlu, en az 5 karakter", "Majburiy, kamida 5 belgi"), MaxLength = 500, VerticalContentAlignment = VerticalAlignment.Center });
        Avalonia.Automation.AutomationProperties.SetName(_comment, _comment.Watermark);
        _comment.IsEnabled = _canComment; // 2026-11-21: editable_fields карточки
        Avalonia.Automation.AutomationProperties.SetName(_reason, T("Причина правки (обязательно, от 5 знаков)", "Оңдоонун себеби (милдеттүү, 5 белгиден)", "Reason for the change (required, at least 5 characters)",
            "Düzenleme nedeni (zorunlu, en az 5 karakter)", "Tahrirlash sababi (majburiy, kamida 5 belgi)"));
        var reasonCell = new StackPanel { Spacing = 4, Children = { Label(T("Причина правки", "Оңдоонун себеби", "Reason for the change", "Düzenleme nedeni", "Tahrirlash sababi") + " *"), _reason } };
        var commentCell = new StackPanel { Spacing = 4, Children = { Label(T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh")), _comment } };
        Grid.SetColumn(commentCell, 2);
        panel.Children.Insert(afterLines, new Grid { // 2026-11-21: обязательная причина правки — сразу под строками (до добавления товара, причины возврата и компенсации), видна без прокрутки
             ColumnDefinitions = new ColumnDefinitions("*,12,*"), Children = { reasonCell, commentCell } });
        // 2026-11-21: «что изменится» — склад, деньги/долг поставщику (ожидание; окончательно пересчитывает сервер); виден, только если есть изменения
        _previewTitle = Label(T("Что изменится после сохранения", "Сактагандан кийин эмне өзгөрөт", "What will change after saving", "Kaydettikten sonra ne değişecek", "Saqlagandan keyin nima o'zgaradi"));
        _preview.Bind(TextBlock.ForegroundProperty, _preview.GetResourceObservable("BrushText"));
        _previewBox = new StackPanel { Spacing = 2, IsVisible = false, Name = "SupplierReturnPreview", Children = { _previewTitle, _preview } };
        panel.Children.Add(_previewBox);
        ReturnEditFlows.AttachTimeLeft(panel, this, info.EditState); // 2026-11-12 (аудит Н-2): подсказка срока пересчитывается раз в минуту
        panel.Children.Add(SectionCards.ErrorBox(this, _error));
        _total.Classes.Add("num");
        _total.Bind(TextBlock.ForegroundProperty, _total.GetResourceObservable("BrushText"));
        _delta.Bind(TextBlock.ForegroundProperty, _delta.GetResourceObservable("BrushTextSoft"));
        var totalBox = new StackPanel { Spacing = 0, MinWidth = 140, Children = { Soft(T("Сумма возврата", "Кайтаруу суммасы", "Return total", "İade toplamı", "Qaytarish summasi")), _total, _delta } };

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += async (_, _) => await CancelAsync().ConfigureAwait(true); // 2026-11-12 (аудит С-1): Отмена, ✕ и Esc с несохранёнными правками — сначала вопрос
        _delete = SectionCards.Secondary(T("Удалить возврат", "Кайтарууну өчүрүү", "Delete return", "İadeyi sil", "Qaytarishni o'chirish"));
        _delete.Classes.Add("danger"); // 2026-11-12 (UX): опасное действие — красной обводкой (как «Удалить» в списке возвратов), первым слева, подальше от «Сохранить»
        _delete.IsVisible = ReturnEditGate.ShowVoid(ReturnDocKind.Supplier, info.EditState);
        _delete.Click += async (_, _) => await DeleteAsync().ConfigureAwait(true);
        _save = SectionCards.Primary(T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"));
        _save.Click += async (_, _) => await SaveAsync().ConfigureAwait(true);
        // 2026-11-12 (UX): номер накладной — в начале подзаголовка (в конце он переносился посередине: «ВП-» / «2026-000123»)
        Content = _shell = SectionCards.Shell(Title,
            (number.Length > 0 ? number + " · " : "") +
            T("Склад, деньги и долг поставщику пересчитает сервер. Нужен PIN руководителя.", "Кампаны, акчаны жана жеткирүүчүгө карызды сервер кайра эсептейт. Жетекчинин PIN коду керек.",
                "The server recalculates stock, money and the supplier debt. A manager PIN is required.", "Stok, para ve tedarikçi borcunu sunucu yeniden hesaplar. Yönetici PIN'i gerekir.",
                "Ombor, pul va yetkazib beruvchiga qarzni server qayta hisoblaydi. Rahbar PIN kodi kerak."),
            panel, SectionCards.Buttons(_delete, cancel, _save), totalBox);
        UpdateTotal();
        Closed += (_, _) => _closed = true; // 2026-11-12: окно закрыли во время запроса — дальше PIN и PATCH не идут (окно PIN без владельца бросило бы исключение)
    }

    private ReturnEditApi Api() => _api ??= ReturnEditFlows.Api();

    // 2026-11-21: причина и вид компенсации возврата изменены относительно карточки (и сервер это разрешает)
    private bool ReasonChanged => _canReason && !string.Equals(_retReason, (_info.Reason ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
    private bool CompChanged => _canComp && !string.Equals(_retComp, (_info.Compensation ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private PendingIdempotencyKey Key
    {
        get
        {
            lock (Keys)
                return Keys.TryGetValue(_info.Id, out var k) ? k : Keys[_info.Id] = new PendingIdempotencyKey();
        }
    }

    private void ForgetKey()
    {
        lock (Keys)
            Keys.Remove(_info.Id);
    }

    /// <summary>2026-11-12: забыть ключи повтора между тестами.</summary>
    public static void ResetKeysForTests()
    {
        lock (Keys)
            Keys.Clear();
    }

    private static string UnknownOutcomeText() => T("Нет связи с сервером — неизвестно, дошла ли правка. Нажмите «Сохранить» ещё раз, когда связь появится: повтор не проведёт её второй раз.",
        "Сервер менен байланыш жок — оңдоо жеткени белгисиз. Байланыш пайда болгондо «Сактоо» дагы басыңыз: кайталоо аны экинчи жолу өткөрбөйт.",
        "No connection to the server — it is unknown whether the change arrived. Press “Save” again when the connection is back: a retry will not apply it twice.",
        "Sunucuyla bağlantı yok — düzenlemenin ulaşıp ulaşmadığı bilinmiyor. Bağlantı gelince yine «Kaydet»e basın: tekrar, onu ikinci kez işlemez.",
        "Server bilan aloqa yo'q — tahrir yetib borgani noma'lum. Aloqa paydo bo'lganda yana «Saqlash»ni bosing: takror uni ikkinchi marta o'tkazmaydi.");

    /// <summary>2026-11-12 (аудит С-1): есть ли несохранённые правки (строки, комментарий или причина).</summary>
    private bool IsDirty()
    {
        if (_changed)
            return false;
        if (!string.IsNullOrWhiteSpace(_reason.Text))
            return true;
        var lines = ReadLines(out _);
        return lines is null || SupplierReturnEdit.HasChanges(_original, lines, _info.Comment, (_comment.Text ?? "").Trim(), _info.Reason, _canReason ? _retReason : null, _info.Compensation, _canComp ? _retComp : null);
    }

    private async Task CancelAsync()
    {
        if (IsDirty()
            && await PosMessageBox.ShowModalAsync(this,
                T("Закрыть без сохранения? Введённые изменения накладной и причина пропадут.", "Сактабай жабасызбы? Киргизилген өзгөртүүлөр жана себеп жоголот.",
                    "Close without saving? The changes you entered and the reason will be lost.", "Kaydetmeden kapatılsın mı? Girdiğiniz değişiklikler ve neden kaybolacak.", "Saqlamasdan yopilsinmi? Kiritilgan o'zgarishlar va sabab yo'qoladi."),
                Title ?? "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question,
                confirmText: T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"), cancelText: T("Остаться", "Калуу", "Stay", "Kal", "Qolish")).ConfigureAwait(true) != System.Windows.MessageBoxResult.Yes)
            return;
        Close(_changed);
    }

    /// <summary>Название товара по id товара — для текстов 409 (negative_stock, exceeds_returnable).</summary>
    private string? ProductNameById(string id) => _rows.FirstOrDefault(r => string.Equals(r.Source.ProductId, id, StringComparison.OrdinalIgnoreCase))?.Source.Name;

    /// <summary>2026-11-12 (аудит С-9): подозрительная правка — цена 0 у изменённой строки или сумма возврата выросла в разы (опечатка «1000» вместо «10»); null — вопросов нет.</summary>
    private string? SuspiciousText(IReadOnlyList<SupplierReturnEditLine> lines)
    {
        var parts = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.IsNew) // 2026-11-21: добавленная строка — цену ввели руками и она 0
            {
                if (l.Qty > 0 && l.PriceSet && l.Price <= 0)
                    parts.Add(T($"В строке «{l.Name}» цена закупки 0.", $"«{l.Name}» сабында сатып алуу баасы 0.", $"The purchase price of “{l.Name}” is 0.", $"«{l.Name}» satırında alış fiyatı 0.", $"«{l.Name}» qatorida xarid narxi 0."));
                continue;
            }
            if (i >= _original.Count)
                break;
            if (l.Qty > 0 && l.Price <= 0 && Math.Abs(_original[i].Price - l.Price) > 0.00005)
                parts.Add(T($"В строке «{l.Name}» цена закупки 0.", $"«{l.Name}» сабында сатып алуу баасы 0.", $"The purchase price of “{l.Name}” is 0.", $"«{l.Name}» satırında alış fiyatı 0.", $"«{l.Name}» qatorida xarid narxi 0."));
        }
        var oldTotal = SupplierReturnEdit.Total(_original);
        var newTotal = SupplierReturnEdit.Total(lines);
        if (oldTotal > 0.004 && newTotal > oldTotal * 3)
            parts.Add(T($"Сумма возврата вырастет с {MoneyText.Som(oldTotal)} до {MoneyText.Som(newTotal)} (больше чем в 3 раза).", $"Кайтаруу суммасы {MoneyText.Som(oldTotal)} ден {MoneyText.Som(newTotal)} га чейин өсөт (3 эседен көп).",
                $"The return total will grow from {MoneyText.Som(oldTotal)} to {MoneyText.Som(newTotal)} (more than 3 times).", $"İade toplamı {MoneyText.Som(oldTotal)} değerinden {MoneyText.Som(newTotal)} değerine çıkacak (3 kattan fazla).",
                $"Qaytarish summasi {MoneyText.Som(oldTotal)} dan {MoneyText.Som(newTotal)} gacha oshadi (3 baravardan ko'p)."));
        return parts.Count == 0 ? null : string.Join("\n", parts) + "\n\n" + T("Сохранить так?", "Ушундай сактайсызбы?", "Save it like this?", "Böyle kaydedilsin mi?", "Shunday saqlansinmi?");
    }

    private static string LikelySavedText() => T("Связь оборвалась после отправки, а на сервере документ уже новой версии с вашим составом — скорее всего, изменение сохранено. Закройте окно: список обновится, проверьте накладную. Второй раз ничего не отправлено.",
        "Жөнөтүлгөндөн кийин байланыш үзүлдү, ал эми серверде документ сиздин курам менен жаңы версияда — оңдоо сакталды. Терезени жабыңыз: тизме жаңырат, накладнойду текшериңиз. Экинчи жолу эч нерсе жөнөтүлгөн жок.",
        "The connection dropped after sending, and the server already has the document in a newer version with your lines — the change was saved. Close the window: the list will refresh, check the waybill. Nothing was sent a second time.",
        "Gönderimden sonra bağlantı koptu ve sunucuda belge sizin satırlarınızla yeni sürümde — düzenleme kaydedildi. Pencereyi kapatın: liste yenilenecek, irsaliyeyi kontrol edin. İkinci kez bir şey gönderilmedi.",
        "Yuborilgandan keyin aloqa uzildi, serverda esa hujjat sizning qatorlaringiz bilan yangi versiyada — tahrir saqlandi. Oynani yoping: ro'yxat yangilanadi, yuk xatini tekshiring. Ikkinchi marta hech narsa yuborilmadi.");

    private List<Row> Matches()
    {
        var filter = (_find?.Text ?? "").Trim();
        // 2026-11-12 (аудит Н-3): поиск — по названию, штрихкоду и артикулу
        return filter.Length == 0 ? _rows : _rows.Where(r => r.Source.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                                                             || r.Source.Barcode.Contains(filter, StringComparison.OrdinalIgnoreCase)
                                                             || r.Source.Article.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void UpdateMoreButton(int matchCount)
    {
        var shown = Math.Min(_shown, matchCount);
        _more.IsVisible = matchCount > shown;
        _more.Content = T($"Показать ещё (показано {shown} из {matchCount})", $"Дагы көрсөтүү ({matchCount} ичинен {shown} көрсөтүлдү)", $"Show more ({shown} of {matchCount} shown)",
            $"Daha fazla göster ({matchCount} içinden {shown} gösterildi)", $"Yana ko'rsatish ({matchCount} tadan {shown} tasi ko'rsatildi)");
        Avalonia.Automation.AutomationProperties.SetName(_more, _more.Content as string);
    }

    /// <summary>2026-11-12: поля ввода — только для показанной страницы строк (с учётом поиска); у скрытых строк значения остаются в модели.</summary>
    private void RenderRows()
    {
        foreach (var r in _rows)
            (r.Qty, r.Price, r.Sum, r.Name, r.Remove) = (null, null, null, null, null);
        _lines.Children.Clear();
        _appliedFilter = (_find?.Text ?? "").Trim();
        var matches = Matches();
        foreach (var r in matches.Take(_shown))
            _lines.Children.Add(BuildRow(r));
        if (matches.Count == 0)
            _lines.Children.Add(Soft(T("Ничего не найдено", "Эч нерсе табылган жок", "Nothing found", "Hiçbir şey bulunamadı", "Hech narsa topilmadi")));
        UpdateMoreButton(matches.Count);
        UpdateTotal();
    }

    /// <summary>2026-11-12 (аудит С-7): «Показать ещё» добавляет только новую страницу — уже показанные строки не пересоздаются (раньше все перестраивались: терялся фокус, а сорок нажатий
    /// на 2000 строк возвращали секунды и сотни МБ). Поиск менялся — страница строится заново.</summary>
    private void ShowMore()
    {
        var matches = Matches();
        var from = Math.Min(_shown, matches.Count);
        _shown += PageSize;
        if (!string.Equals((_find?.Text ?? "").Trim(), _appliedFilter, StringComparison.Ordinal))
        {
            RenderRows();
            return;
        }
        foreach (var r in matches.Skip(from).Take(_shown - from))
            _lines.Children.Add(BuildRow(r));
        UpdateMoreButton(matches.Count);
        UpdateTotal();
    }

    /// <summary>2026-11-21: Enter в поле поиска/скана — курсор в количество найденной строки. 2026-11-21 (ответ Умара 21.11): такого товара в накладной нет — ищем в каталоге и добавляем строку (сервер новые строки принимает).</summary>
    private void OnFindEnter()
    {
        var matches = Matches();
        if (matches.Count == 0)
        {
            var text = (_find?.Text ?? "").Trim();
            if (_canItems && FindProducts(text).FirstOrDefault() is { } product)
            {
                if (_find is not null)
                    _find.Text = "";
                AddProduct(product);
                return;
            }
            ShowError(T("Такого товара нет ни в накладной, ни в каталоге.", "Мындай товар накладнойдо да, каталогто да жок.", "This product is neither in the waybill nor in the catalog.",
                "Bu ürün ne irsaliyede ne de katalogda var.", "Bunday mahsulot yuk xatida ham, katalogda ham yo'q."));
            return;
        }
        ShowError(null);
        RevealRow(matches[0]);
    }

    private void BuildChips()
    {
        _reasonChips.Children.Clear();
        foreach (var reason in new[] { "defect", "surplus", "wrong_item", "expired", "other" })
        {
            var chip = UiKit.Chip(this, SupplierReturnsWindow.ReasonName(reason), reason == _retReason);
            chip.MinHeight = 44;
            chip.Margin = new Thickness(0, 0, 8, 8);
            chip.Click += (_, _) =>
            {
                _retReason = reason;
                BuildChips();
                UpdateTotal();
            };
            _reasonChips.Children.Add(chip);
        }
        _compChips.Children.Clear();
        foreach (var comp in new[] { "cash", "debt_offset", "none" })
        {
            var chip = UiKit.Chip(this, SupplierReturnsWindow.CompensationName(comp), comp == _retComp);
            chip.MinHeight = 44;
            chip.HorizontalAlignment = HorizontalAlignment.Stretch;
            chip.HorizontalContentAlignment = HorizontalAlignment.Left;
            chip.CornerRadius = new CornerRadius(10);
            chip.Click += (_, _) =>
            {
                _retComp = comp;
                BuildChips();
                UpdateTotal();
            };
            _compChips.Children.Add(chip);
        }
    }

    /// <summary>2026-11-21: товары каталога по тексту — точный код (основной/доп. штрихкод, артикул) или совпадение по названию/штрихкоду/артикулу.</summary>
    private static List<Tile> FindProducts(string text)
    {
        text = (text ?? "").Trim();
        var list = new List<Tile>();
        if (text.Length < 2)
            return list;
        try
        {
            if (text.Length >= 4 && NurMarketKassa.Services.ProductCodeLookup.FindLocal(text, allowWeightLabel: false)?.Product is { } exact)
                list.Add(exact);
            foreach (var p in NurMarketKassa.Services.CatalogCacheService.Products
                         .Where(p => !string.IsNullOrWhiteSpace(p.Title) && NurMarketKassa.Core.Application.SearchMatch.Matches(text, p.Title, p.Barcode, p.Article))
                         .OrderBy(p => p.Title).Take(8))
                if (!list.Any(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)))
                    list.Add(p);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Возврат поставщику: поиск товара для добавления не удался ({ex.GetType().Name}).", "WARNING");
        }
        return list;
    }

    private void RenderAddResults()
    {
        _addResults.Children.Clear();
        var text = (_add?.Text ?? "").Trim();
        foreach (var p in FindProducts(text).Take(6))
        {
            var chip = UiKit.Chip(this, p.Title, false);
            chip.MinHeight = 44;
            chip.HorizontalAlignment = HorizontalAlignment.Stretch;
            chip.HorizontalContentAlignment = HorizontalAlignment.Left;
            chip.CornerRadius = new CornerRadius(10);
            chip.Click += (_, _) => AddProduct(p);
            _addResults.Children.Add(chip);
        }
    }

    private void OnAddEnter()
    {
        var text = (_add?.Text ?? "").Trim();
        if (FindProducts(text).FirstOrDefault() is { } product)
            AddProduct(product);
        else if (text.Length > 0)
            ShowError(T("Такого товара нет в каталоге.", "Мындай товар каталогто жок.", "This product is not in the catalog.", "Bu ürün katalogda yok.", "Bunday mahsulot katalogda yo'q."));
    }

    /// <summary>2026-11-21: добавить товар в возврат строкой (количество 1, цена — закупочная из карточки, пока не введена вручную). Такой товар уже есть среди строк — не дублируем, показываем её.</summary>
    private void AddProduct(Tile product)
    {
        ShowError(null);
        if (_add is not null)
            _add.Text = "";
        _addResults.Children.Clear();
        var same = _rows.FirstOrDefault(r => string.Equals(r.Source.ProductId, product.Id, StringComparison.OrdinalIgnoreCase));
        if (same is not null)
        {
            if (same.Source.IsNew)
            {
                var q = (SupplierReturnEdit.ParseNumber(same.QtyText) ?? 0) + 1;
                same.QtyText = q.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
                if (same.Qty is { } box)
                    box.Text = same.QtyText;
                UpdateTotal();
            }
            _addNote.Text = T($"«{product.Title}» уже есть в возврате — измените количество в строке.", $"«{product.Title}» кайтарууда бар — саптагы санын өзгөртүңүз.", $"“{product.Title}” is already in the return — change the quantity in its line.",
                $"«{product.Title}» zaten iadede var — satırdaki miktarı değiştirin.", $"«{product.Title}» qaytarishda allaqachon bor — qatordagi miqdorni o'zgartiring.");
            _addNote.IsVisible = true;
            RevealRow(same);
            return;
        }
        _addNote.IsVisible = false;
        var unit = product.Unit ?? "";
        var price = product.PurchasePrice > 0 ? product.PurchasePrice : 0;
        var line = new SupplierReturnEditLine("", product.Id, product.Title, unit, 1, price, 0, "", product.Article ?? "", product.Barcode ?? "", IsNew: true, PriceSet: false);
        var priceText = price.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
        var row = new Row { Source = line, QtyText = "1", PriceText = priceText, OrigQtyText = "1", OrigPriceText = priceText };
        _rows.Add(row);
        RevealRow(row);
    }

    /// <summary>2026-11-12 (аудит С-6): ошибка относится к строке, скрытой страницей или поиском, — сбрасываем поиск, раскрываем страницу до строки и ставим фокус в её поле.</summary>
    private void RevealRow(Row? row, bool price = false)
    {
        if (row is null)
            return;
        if (_find is not null)
        {
            _findTimer?.Stop();
            if (!string.IsNullOrEmpty(_find.Text))
                _find.Text = "";
            _findTimer?.Stop();
        }
        var index = _rows.IndexOf(row);
        if (row.Qty is null || (_find is not null && _appliedFilter.Length > 0))
        {
            _shown = Math.Max(_shown, (index / PageSize + 1) * PageSize);
            RenderRows();
        }
        var box = price ? row.Price : row.Qty;
        if (box is null)
            return;
        box.BringIntoView();
        box.Focus();
        box.SelectAll();
    }

    private Control BuildRow(Row row)
    {
        var line = row.Source;
        // 2026-11-12 (UX правки накладной): строка — одна линия таблицы (столбцы Cols, подписи — в шапке над списком); «✕» убирает строку (кол-во 0), «↩» возвращает исходное количество
        var name = new TextBlock { Text = (line.Name.Length > 0 ? line.Name : "—") + (line.Unit.Length > 0 ? $" ({line.Unit})" : ""), FontSize = 13.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("BrushText"));
        row.Name = name;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(Cols) }; // 2026-11-11: без скидки и примечания строки
        row.Qty = Num(row.QtyText, T("Кол-во", "Саны", "Qty", "Adet", "Soni"), line.Name);
        row.Price = Num(row.PriceText, T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), line.Name);
        row.Sum = new TextBlock { FontSize = 13.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        row.Sum.Classes.Add("num");
        row.Sum.Bind(TextBlock.ForegroundProperty, row.Sum.GetResourceObservable("BrushText"));
        var remove = new Button
        {
            Content = "✕", Width = 44, Height = 44, MinHeight = 44, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = false, // Tab идёт по полям «кол-во → цена → следующая строка», не застревая на кнопке
        };
        remove.Classes.Add("sec-outline");
        row.Remove = remove;
        remove.Click += (_, _) =>
        {
            if (row.Source.IsNew)
            {
                // 2026-11-21: добавленную при правке строку убираем совсем (в документе её ещё нет — «вернуть» нечего)
                _rows.Remove(row);
                RenderRows();
                return;
            }
            var removed = SupplierReturnEdit.ParseNumber(row.QtyText) is { } q0 && q0 == 0;
            var text = removed ? row.OrigQtyText : "0";
            row.QtyText = text;
            if (row.Qty is { } qb)
                qb.Text = text;
            UpdateTotal();
        };
        Grid.SetColumn(row.Qty, 2);
        Grid.SetColumn(row.Price, 4);
        Grid.SetColumn(row.Sum, 6);
        Grid.SetColumn(remove, 8);
        grid.Children.Add(name);
        grid.Children.Add(row.Qty);
        grid.Children.Add(row.Price);
        grid.Children.Add(row.Sum);
        grid.Children.Add(remove);
        Control box = grid;
        var qty = row.Qty;
        var price = row.Price;
        if (!_canItems)
            qty.IsEnabled = price.IsEnabled = remove.IsEnabled = false; // 2026-11-21: сервер не разрешил менять строки (editable_fields)
        qty.TextChanged += (_, _) =>
        {
            row.QtyText = qty.Text ?? "";
            UpdateTotal();
        };
        price.TextChanged += (_, _) =>
        {
            row.PriceText = price.Text ?? "";
            UpdateTotal();
        };
        return box;
    }

    private static TextBox Num(string text, string label, string product)
    {
        // 2026-11-12 (UX): число — по центру высоты поля (было прижато к верхнему краю 44-пиксельного поля)
        var t = SectionCards.Field(new TextBox { Text = text, HorizontalContentAlignment = HorizontalAlignment.Right, VerticalContentAlignment = VerticalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Watermark = "0", MaxLength = 14 });
        Avalonia.Automation.AutomationProperties.SetName(t, label + ": " + (product.Length > 0 ? product : "—"));
        return t;
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    /// <summary>Строки из полей; null — в каком-то поле не число (текст ошибки — в <paramref name="error"/>, строка и поле — в <paramref name="bad"/>).
    /// 2026-11-12 (аудит В-4): поле, текст которого не менялся, не разбирается заново — берётся исходное значение до последнего знака.</summary>
    private List<SupplierReturnEditLine>? ReadLines(out string? error, out (Row Row, bool Price)? bad)
    {
        error = null;
        bad = null;
        var list = new List<SupplierReturnEditLine>();
        foreach (var r in _rows)
        {
            var qty = string.Equals(r.QtyText, r.OrigQtyText, StringComparison.Ordinal) ? r.Source.Qty : SupplierReturnEdit.ParseNumber(r.QtyText);
            var price = string.Equals(r.PriceText, r.OrigPriceText, StringComparison.Ordinal) ? r.Source.Price : SupplierReturnEdit.ParseNumber(r.PriceText);
            if (qty is null || price is null)
            {
                error = $"«{(r.Source.Name.Length > 0 ? r.Source.Name : "—")}»: " + T("введите число", "сан жазыңыз", "enter a number", "bir sayı girin", "son kiriting") + ".";
                bad = (r, qty is not null);
                return null;
            }
            // 2026-11-21: у добавленной строки цена «введена», только если текст поля изменили (иначе purchase_price не уходит — сервер берёт цену из прихода или закупочную товара)
            list.Add(r.Source with { Qty = qty.Value, Price = price.Value, PriceSet = !r.Source.IsNew || !string.Equals(r.PriceText, r.OrigPriceText, StringComparison.Ordinal) });
        }
        return list;
    }

    private List<SupplierReturnEditLine>? ReadLines(out string? error) => ReadLines(out error, out _);

    private void UpdateTotal()
    {
        var lines = ReadLines(out _);
        foreach (var r in _rows)
        {
            if (r.Sum is null) // 2026-11-12: строка не показана — сумма строки не рисуется (в итог входит)
                continue;
            var q = SupplierReturnEdit.ParseNumber(r.QtyText) ?? 0;
            var p = SupplierReturnEdit.ParseNumber(r.PriceText) ?? 0;
            r.Sum.Text = MoneyText.Som(ReturnLineMath.LineTotal(q, p, 0));
            // 2026-11-12 (UX): убранная строка (кол-во 0) — название зачёркнуто и бледнее, кнопка «✕» становится «↩ вернуть строку»
            var removed = SupplierReturnEdit.ParseNumber(r.QtyText) is { } rq && rq == 0;
            if (r.Name is { } nameText)
            {
                nameText.TextDecorations = removed ? TextDecorations.Strikethrough : null;
                nameText.Opacity = removed ? 0.55 : 1;
            }
            r.Sum.Opacity = removed ? 0.55 : 1;
            if (r.Remove is { } rb)
            {
                rb.Content = removed ? "↩" : "✕";
                var tip = (removed ? T("Вернуть строку", "Сапты кайтаруу", "Restore the line", "Satırı geri al", "Qatorni tiklash") : T("Убрать строку", "Сапты алып салуу", "Remove the line", "Satırı kaldır", "Qatorni olib tashlash"))
                          + ": " + (r.Source.Name.Length > 0 ? r.Source.Name : "—");
                Avalonia.Automation.AutomationProperties.SetName(rb, tip);
                ToolTip.SetTip(rb, tip);
            }
        }
        var previewLines = lines is null ? new List<string>() : SupplierReturnEdit.Preview(_original, lines, _info.Compensation, CompChanged ? _retComp : null).ToList();
        if (ReasonChanged) // 2026-11-21: причина возврата тоже меняется — в журнале сервера; на склад и деньги не влияет
            previewLines.Add(T("Причина возврата: ", "Кайтаруунун себеби: ", "Return reason: ", "İade nedeni: ", "Qaytarish sababi: ") + SupplierReturnsWindow.ReasonName(_info.Reason) + " → " + SupplierReturnsWindow.ReasonName(_retReason));
        _preview.Text = string.Join("\n", previewLines);
        if (_previewBox is not null) // RenderRows() в конструкторе вызывает UpdateTotal раньше, чем создан блок
            _previewBox.IsVisible = previewLines.Count > 0;
        var newTotal = lines is null ? (double?)null : SupplierReturnEdit.Total(lines);
        var oldTotal = SupplierReturnEdit.Total(_original);
        _totalText = T("Сумма: ", "Суммасы: ", "Total: ", "Toplam: ", "Jami: ") + (newTotal is { } nt ? MoneyText.Som(nt) : "—")
                      + (newTotal is { } n && Math.Abs(n - oldTotal) > 0.004 ? T($" (было {MoneyText.Som(oldTotal)})", $" ({MoneyText.Som(oldTotal)} болчу)", $" (was {MoneyText.Som(oldTotal)})", $" (önce {MoneyText.Som(oldTotal)})", $" (edi {MoneyText.Som(oldTotal)})") : "");
        // 2026-11-12 (UX правки накладной): в подвале — крупная новая сумма и строкой ниже, насколько она меньше/больше прежней (сколько поставщик вернёт или зачтёт в долг иначе)
        _total.Text = newTotal is { } shown ? MoneyText.Som(shown) : "—";
        if (newTotal is not { } now || Math.Abs(now - oldTotal) <= 0.004)
            _delta.Text = newTotal is null ? "" : T("без изменений", "өзгөрүүсүз", "no changes", "değişiklik yok", "o'zgarishsiz");
        else
        {
            var o = MoneyText.Som(oldTotal);
            var d = MoneyText.Som(Math.Abs(now - oldTotal));
            _delta.Text = now < oldTotal
                ? T($"было {o} · меньше на {d}", $"{o} болчу · {d} азайды", $"was {o} · {d} less", $"önce {o} · {d} daha az", $"edi {o} · {d} kamaydi")
                : T($"было {o} · больше на {d}", $"{o} болчу · {d} көбөйдү", $"was {o} · {d} more", $"önce {o} · {d} daha fazla", $"edi {o} · {d} ko'paydi");
        }
    }

    private void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.IsVisible = !string.IsNullOrWhiteSpace(text);
        // 2026-11-12 (UX): плашка ошибки — внизу прокручиваемой части; при длинной накладной она оставалась за краем и «Сохранить» как будто не работала
        if (_error.IsVisible)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _error.BringIntoView(), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void Lock()
    {
        _save.IsEnabled = false;
        _delete.IsEnabled = false;
        _changed = true;
    }

    private async Task SaveAsync()
    {
        ShowError(null);
        var lines = ReadLines(out var parseError, out var badField);
        if (lines is null)
        {
            ShowError(parseError);
            RevealRow(badField?.Row, badField?.Price ?? false); // 2026-11-12 (аудит С-6): строка может быть скрыта страницей или поиском
            return;
        }
        var reason = (_reason.Text ?? "").Trim();
        var comment = (_comment.Text ?? "").Trim();
        if (SupplierReturnEdit.Validate(lines, reason, _original, out var badIndex) is { } problem)
        {
            ShowError(problem);
            if (badIndex >= 0 && badIndex < _rows.Count)
                RevealRow(_rows[badIndex]);
            else if (reason.Length < 5)
                _reason.Focus(); // 2026-11-12 (UX): не заполнена причина — курсор сразу в её поле
            return;
        }
        if (!SupplierReturnEdit.HasChanges(_original, lines, _info.Comment, comment, _info.Reason, _canReason ? _retReason : null, _info.Compensation, _canComp ? _retComp : null))
        {
            ShowError(T("Изменений нет — сохранять нечего.", "Өзгөртүү жок — сактай турган эч нерсе жок.", "No changes — nothing to save.", "Değişiklik yok — kaydedilecek bir şey yok.", "O'zgarish yo'q — saqlanadigan narsa yo'q."));
            return;
        }
        // 2026-11-12 (аудит С-9): цена 0 и рост суммы в разы — вопрос до PIN (опечатка «1000» вместо «10» иначе уходит на сервер молча)
        if (SuspiciousText(lines) is { } doubt)
        {
            if (_asking)
                return;
            _asking = true;
            try
            {
                if (await PosMessageBox.ShowModalAsync(this, doubt, Title ?? "", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                        confirmText: T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), cancelText: T("Проверить", "Текшерүү", "Check", "Kontrol et", "Tekshirish")).ConfigureAwait(true) != System.Windows.MessageBoxResult.Yes)
                    return;
            }
            finally
            {
                _asking = false;
            }
        }
        if (_closed || !_shell.TryBeginBusy())
            return;
        _save.IsEnabled = false;
        try
        {
            // Свежая проверка на сервере: версия (не правил ли кто-то другой) и срок по времени сервера.
            var card = await Api().GetDocumentAsync(ReturnDocKind.Supplier, _info.Id).ConfigureAwait(true);
            if (_closed)
                return;
            var fresh = ReturnEditState.Parse(card, DateTime.UtcNow);
            if (_info.EditState.Version > 0 && fresh.Version != _info.EditState.Version)
            {
                // 2026-11-12 (аудит В-3): «скорее всего, сохранено» — только если прошлая отправка осталась без ответа (обрыв/5xx) И состав накладной на сервере совпал с отправленным;
                // после отказа 409 и чужой правки признака «без ответа» нет (ведётся по ReturnEditErrors.IsOutcomeUnknown) — говорим «изменён другим сотрудником»
                var ours = Key.HasUnansweredAttempt && SupplierReturnEdit.SameComposition(SupplierReturnEdit.FromInfo(SupplierApiService.ParseReturn(card)), lines);
                ShowError(ours ? LikelySavedText() : ReturnEditErrors.VersionConflictText());
                ForgetKey();
                Lock();
                return;
            }
            if (!ReturnEditGate.ShowEdit(ReturnDocKind.Supplier, fresh, ReturnEditFlows.IsManager)) // 2026-11-21: владелец/админ — и после 24 ч
            {
                ShowError(ReturnEditErrors.NotEditableText(fresh)); // 2026-11-11: причина сервера (edit_block_reason)
                Lock();
                return;
            }
            _shell.EndBusy();
            var doc = new ReturnDocRef(ReturnDocKind.Supplier, _info.Id, _number, _info.Total, fresh);
            var (token, final) = await ReturnEditFlows.AskPinAsync(this, doc, ReturnEditAction.Edit, fresh.Version, Api()).ConfigureAwait(true);
            if (token is null)
            {
                if (final is not null)
                {
                    ShowError(ReturnEditErrors.Text(final));
                    Lock();
                }
                return;
            }
            if (_closed || !_shell.TryBeginBusy())
                return;
            var newReason = ReasonChanged ? _retReason : null; // 2026-11-21: причина/вид компенсации возврата — только если изменили
            var newComp = CompChanged ? _retComp : null;
            var body = SupplierReturnEdit.BuildPatchBody(lines, reason, comment, _original, newReason, newComp);
            var key = Key;
            var keyValue = key.Get(SupplierReturnEdit.Signature(_info.Id, fresh.Version, lines, reason, comment, newReason, newComp));
            await Api().PatchAsync(ReturnDocKind.Supplier, _info.Id, fresh.Version, body, token, keyValue).ConfigureAwait(true);
            key.OnSuccess();
            ForgetKey();
            PosLogger.Log($"Возврат поставщику {_number} ({_info.Id}) изменён{(_ownerOverride ? " владельцем после 24 ч (owner_override)" : "")}: v{fresh.Version} → новая версия, строк {lines.Count(l => l.Qty > 0)}, добавлено {lines.Count(l => l.IsNew && l.Qty > 0)}{(newReason is not null ? ", причина возврата изменена" : "")}{(newComp is not null ? ", вид компенсации изменён" : "")}.", "INFO");
            _changed = true;
            _shell.EndBusy();
            Close(true);
        }
        catch (ReturnEditException e)
        {
            // 2026-11-12 (аудит В-3): признак «без ответа» — только обрыв/5xx; отказ по существу (409 negative_stock и др.) — ключ забывается, ложного «скорее всего, сохранено» потом не будет
            var unknown = ReturnEditErrors.IsOutcomeUnknown(e);
            if (unknown)
                Key.OnError(e.InnerException ?? e);
            else
                ForgetKey();
            // 2026-11-12 (стресс-тест): правка ушла, а ответа нет — «ничего не изменено» было бы неправдой; говорим, что повтор безопасен
            ShowError(unknown ? UnknownOutcomeText() : T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + ReturnEditErrors.Text(e, ProductNameById));
            if (e.IsFinal)
                Lock();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 2026-11-12: неожиданная ошибка (например, окно закрыли посреди шага) не должна ронять кассу из обработчика кнопки
            PosLogger.Log($"Возврат поставщику {_info.Id}: правка прервана ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowError(T("Не сохранено: ", "Сакталган жок: ", "Not saved: ", "Kaydedilmedi: ", "Saqlanmadi: ") + NurMarketKassa.Services.ServerOutageMonitor.UserText(ex));
        }
        finally
        {
            _shell.EndBusy();
            if (!_changed)
                _save.IsEnabled = true;
        }
    }

    private async Task DeleteAsync()
    {
        var doc = new ReturnDocRef(ReturnDocKind.Supplier, _info.Id, _number, _info.Total, _info.EditState);
        try
        {
            if (await ReturnEditFlows.VoidAsync(this, doc, Api()).ConfigureAwait(true))
            {
                _changed = true;
                Close(true);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 2026-11-12 (аудит С-3): «Удалить возврат» — обработчик без try ронял программу при неожиданной ошибке
            PosLogger.Log($"Возврат поставщику {_info.Id}: удаление прервано ({ex.GetType().Name}: {ex.Message}).", "WARNING");
            ShowError(T("Не удалено: ", "Өчүрүлгөн жок: ", "Not deleted: ", "Silinmedi: ", "O'chirilmadi: ") + NurMarketKassa.Services.ServerOutageMonitor.UserText(ex));
        }
    }

    /// <summary>Для тестов окна: ввести количество в строку <paramref name="index"/>.</summary>
    public void SetQtyForTests(int index, string text)
    {
        var r = _rows[index];
        r.QtyText = text;
        if (r.Qty is { } box)
            box.Text = text;
        UpdateTotal();
    }

    public string TotalText => _totalText;

    /// <summary>2026-11-21: id товаров, добавленных в возврат при правке (после сохранения их остаток перечитывается).</summary>
    public IReadOnlyList<string> AddedProductIds => _rows.Where(r => r.Source.IsNew && (SupplierReturnEdit.ParseNumber(r.QtyText) ?? 0) > 0).Select(r => r.Source.ProductId).ToList();

    /// <summary>2026-11-21: добавить товар каталога в возврат (для тестов окна).</summary>
    public void AddProductForTests(Tile product) => AddProduct(product);

    /// <summary>2026-11-21: выбрать причину / вид компенсации возврата (для тестов окна).</summary>
    public void SetReturnReasonForTests(string reason)
    {
        _retReason = reason;
        BuildChips();
        UpdateTotal();
    }

    public void SetCompensationForTests(string compensation)
    {
        _retComp = compensation;
        BuildChips();
        UpdateTotal();
    }

    public bool OwnerOverrideForTests => _ownerOverride;
    public bool AddFieldVisibleForTests => _add is not null;
    public bool ReasonChipsVisibleForTests => _canReason;

    /// <summary>2026-11-21: текст предпросмотра «что изменится» (пусто — блок скрыт).</summary>
    public string PreviewTextForTests => _previewBox is { IsVisible: true } ? _preview.Text ?? "" : "";

    /// <summary>2026-11-21: ввести в поле поиска/скана текст и нажать Enter (false — поля поиска нет).</summary>
    public bool ScanForTests(string text)
    {
        if (_find is null)
            return false;
        _find.Text = text;
        OnFindEnter();
        return true;
    }

    /// <summary>2026-11-12 (UX): строка под суммой в подвале — «было … · меньше/больше на …».</summary>
    public string DeltaTextForTests => _delta.Text ?? "";

    // 2026-11-12 (стресс-тест правки накладной возврата поставщику): входы для тестов окна
    public void SetPriceForTests(int index, string text)
    {
        var r = _rows[index];
        r.PriceText = text;
        if (r.Price is { } box)
            box.Text = text;
        UpdateTotal();
    }

    /// <summary>Сколько строк сейчас с полями ввода (страница + поиск).</summary>
    public int ShownRowsForTests => _rows.Count(r => r.Qty is not null);

    /// <summary>Поиск товара в длинной накладной — сразу, без задержки.</summary>
    public void FindForTests(string text)
    {
        if (_find is null)
            return;
        _find.Text = text;
        _findTimer?.Stop();
        _shown = PageSize;
        RenderRows();
    }

    public void ShowMoreForTests()
    {
        _findTimer?.Stop();
        ShowMore();
    }

    /// <summary>Номер строки накладной, у которой сейчас фокус ввода (−1 — нет) — проверка, что «Показать ещё» не сбрасывает фокус.</summary>
    public int FocusedRowForTests => _rows.FindIndex(r => (r.Qty?.IsFocused ?? false) || (r.Price?.IsFocused ?? false));

    /// <summary>Сами поля строки (null — строка сейчас не показана) — проверка, что «Показать ещё» не пересоздаёт уже показанные.</summary>
    public (TextBox? Qty, TextBox? Price) RowBoxesForTests(int index) => (_rows[index].Qty, _rows[index].Price);

    public void FocusRowForTests(int index, bool price = false) => (price ? _rows[index].Price : _rows[index].Qty)?.Focus();

    public void SetReasonForTests(string text) => _reason.Text = text;

    public Task SaveForTestsAsync() => SaveAsync();

    public string ErrorText => _error.IsVisible ? _error.Text ?? "" : "";

    public int RowCountForTests => _rows.Count;

    public bool SaveEnabledForTests => _save.IsEnabled;
}
