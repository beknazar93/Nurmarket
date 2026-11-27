using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-11-27: мастер «Новая акция / Изменить акцию» — полоса шагов, «Назад / Далее», шаги 1–5 (Название, Вид, На что, Когда, Условия). Шаг 6 и сохранение —
/// <c>PromotionsWindow.Preview.cs</c>. Раскладка по чек-листу окон 20.11: подпись над полем, ячейки в WrapPanel, высота полей 44, перенос на узком окне, прокрутка на 1024×600.</summary>
public sealed partial class PromotionsWindow
{
    private const int StepCount = 6;
    private const double FieldHeight = 44;

    private readonly Grid _wizardView = new() { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto") };
    private readonly TextBlock _wizardTitle = new() { FontSize = 18, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _stepBar = new() { Orientation = Orientation.Horizontal };
    private readonly List<Button> _stepButtons = new();
    private readonly ScrollViewer _stepScroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBlock _wizardStatus = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly WrapPanel _wizardRight = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private Button _back = null!, _next = null!, _saveDraft = null!, _startButton = null!, _saveChanges = null!;
    private readonly Control[] _stepPanels = new Control[StepCount];

    private Promotion _draft = new();
    private string _idemKey = "";
    private int _step = 1;
    private bool _saving;
    private readonly Dictionary<string, PromotionProduct> _productNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PromotionRef> _categories = Array.Empty<PromotionRef>();
    private IReadOnlyList<PromotionRef> _brands = Array.Empty<PromotionRef>();
    private IReadOnlyList<PromotionRef> _branches = Array.Empty<PromotionRef>();
    private readonly Dictionary<Control, IDisposable> _errorMarks = new();

    // Шаг 1
    private TextBox _titleBox = null!, _descriptionBox = null!;
    // Шаг 2
    private readonly Dictionary<string, Button> _kindCards = new();
    private readonly TextBlock _kindLockNote = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel _paramsHost = new() { Spacing = 8, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _example = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private TextBox _buyBox = null!, _getBox = null!, _getPctBox = null!, _percentBox = null!, _minAmountBox = null!, _maxQtyBox = null!, _amountBox = null!, _priceBox = null!;
    private Button _perUnit = null!, _perLine = null!;
    // Шаг 3
    private Button _scopeAllChip = null!, _scopeSomeChip = null!;
    private readonly StackPanel _scopeSomeHost = new() { Spacing = 10 };
    private ProductPicker _includePicker = null!, _excludePicker = null!;
    private RefChecklist _categoryList = null!, _brandList = null!, _branchList = null!;
    private Border _scopeBox = null!;
    // Шаг 4
    private TextBox _startBox = null!, _endBox = null!, _timeFromBox = null!, _timeToBox = null!;
    private string _startOriginal = "", _endOriginal = "";
    private readonly Dictionary<int, Button> _dayChips = new();
    // Шаг 5
    private Button _audienceAll = null!, _audienceClients = null!;
    private TextBox _priorityBox = null!, _limitBox = null!;
    private TextBlock _limitCaption = null!;
    private CheckBox _stackManual = null!, _stackPromos = null!, _belowCost = null!;
    private Control _branchesBlock = null!;

    private static string StepTitle(int step) => step switch
    {
        6 => T("Предпросмотр", "Алдын ала көрүү", "Preview", "Önizleme", "Oldindan ko'rish"),
        _ => PromotionErrors.StepName(step),
    };

    private void BuildWizardFrame()
    {
        Use(_wizardTitle, TextBlock.ForegroundProperty, "BrushText");
        var toList = Small(UiKit.Ghost(this, T("← К списку", "← Тизмеге", "← To the list", "← Listeye", "← Ro'yxatga")));
        AutomationProperties.SetName(toList, T("К списку акций", "Акциялар тизмесине", "To the promotion list", "Kampanya listesine", "Aksiyalar ro'yxatiga"));
        toList.Click += async (_, _) => await LeaveWizardAsync().ConfigureAwait(true);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(_wizardTitle);
        toList.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(toList, 1);
        head.Children.Add(toList);

        for (var i = 1; i <= StepCount; i++)
        {
            var step = i;
            var b = UiKit.Seg(i + ". " + StepTitle(i), i == 1);
            b.Margin = new Thickness(0, 0, 0, 2);
            b.Click += (_, _) => GoToStep(step, validate: false);
            _stepButtons.Add(b);
            _stepBar.Children.Add(b);
        }
        var bar = UiKit.Segment(_stepBar);
        bar.HorizontalAlignment = HorizontalAlignment.Left;
        bar.Margin = new Thickness(0, 0, 0, 10);

        _back = Small(UiKit.Ghost(this, T("Назад", "Артка", "Back", "Geri", "Orqaga")));
        _back.Click += (_, _) => GoToStep(_step - 1, validate: false);
        _next = Small(UiKit.Primary(this, T("Далее", "Кийинки", "Next", "İleri", "Keyingi")));
        _next.Click += (_, _) => GoToStep(_step + 1, validate: true);
        _saveDraft = Small(UiKit.Ghost(this, T("Сохранить черновиком", "Каралама катары сактоо", "Save as draft", "Taslak olarak kaydet", "Qoralama sifatida saqlash")));
        _saveDraft.Click += async (_, _) => await SaveAsync(start: false).ConfigureAwait(true);
        _startButton = Small(UiKit.Primary(this, T("Запустить", "Иштетүү", "Start", "Başlat", "Boshlash")));
        _startButton.Click += async (_, _) => await SaveAsync(start: true).ConfigureAwait(true);
        _saveChanges = Small(UiKit.Primary(this, T("Сохранить изменения", "Өзгөртүүлөрдү сактоо", "Save changes", "Değişiklikleri kaydet", "O'zgarishlarni saqlash")));
        _saveChanges.Click += async (_, _) => await SaveAsync(start: false).ConfigureAwait(true);
        foreach (var b in new[] { _back, _next, _saveDraft, _startButton, _saveChanges })
            AutomationProperties.SetName(b, (string)b.Content!);
        foreach (var b in new[] { _saveDraft, _saveChanges, _startButton, _next })
        {
            b.Margin = new Thickness(8, 0, 0, 6);
            _wizardRight.Children.Add(b);
        }
        _back.Margin = new Thickness(0, 0, 8, 6);
        var bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(_back);
        Grid.SetColumn(_wizardRight, 1);
        bottom.Children.Add(_wizardRight);

        AddRow(_wizardView, head, 0);
        AddRow(_wizardView, bar, 1);
        AddRow(_wizardView, _stepScroll, 2);
        AddRow(_wizardView, _wizardStatus, 3);
        AddRow(_wizardView, bottom, 4);
    }

    // ── Открыть / закрыть мастер ──────────────────────────────────────────────────

    private void OpenWizard(Promotion? existing)
    {
        _draft = existing?.Clone() ?? NewDraft();
        _idemKey = Guid.NewGuid().ToString("N"); // 2026-11-27: один ключ на открытие мастера; повтор того же сохранения уходит с ним же
        _wizardTitle.Text = existing == null
            ? T("Новая акция", "Жаңы акция", "New promotion", "Yeni kampanya", "Yangi aksiya")
            : T("Изменить акцию", "Акцияны өзгөртүү", "Edit promotion", "Kampanyayı düzenle", "Aksiyani o'zgartirish") + " · " + PromotionText.StatusLabel(_draft, Now);
        _errorMarks.Clear();
        _previewResult.Children.Clear();
        _sample.Clear();
        BuildSteps();
        _listView.IsVisible = false;
        _wizardView.IsVisible = true;
        _step = 1;
        ShowStep();
        SetWizardStatus("", "BrushTextSoft");
        _ = LoadReferencesAsync();
        _ = LoadProductNamesAsync(_draft.Products.Concat(_draft.ExcludeProducts).ToList());
    }

    private static Promotion NewDraft()
    {
        var today = Now.ToOffset(PromotionsApi.CompanyOffset);
        return new Promotion
        {
            Kind = Promotion.KindBuyNGetM,
            StartsAt = new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, PromotionsApi.CompanyOffset),
            Priority = 10,
        };
    }

    private async Task LeaveWizardAsync()
    {
        if (!_saving && !await ConfirmAsync(T("Выйти из мастера? Несохранённые изменения пропадут.", "Мастерден чыгасызбы? Сакталбаган өзгөртүүлөр жоголот.",
                "Leave the wizard? Unsaved changes will be lost.", "Sihirbazdan çıkılsın mı? Kaydedilmemiş değişiklikler kaybolur.", "Ustadan chiqilsinmi? Saqlanmagan o'zgarishlar yo'qoladi."),
                T("Выйти", "Чыгуу", "Leave", "Çık", "Chiqish"), danger: false).ConfigureAwait(true))
            return;
        CloseWizard();
    }

    private void CloseWizard()
    {
        _wizardView.IsVisible = false;
        _listView.IsVisible = true;
    }

    private async Task LoadReferencesAsync()
    {
        try
        {
            var cats = _api.CategoriesAsync(_cts.Token);
            var brands = _api.BrandsAsync(_cts.Token);
            _categories = await cats.ConfigureAwait(true);
            _brands = await brands.ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Акции: категории/бренды не загружены ({ex.GetType().Name}: {ex.Message}).", "WARNING");
        }
        _categoryList.SetItems(_categories);
        _brandList.SetItems(_brands);
        try
        {
            _branches = await _api.BranchesAsync(_cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PosLogger.Log($"Акции: филиалы не загружены ({ex.GetType().Name}: {ex.Message}) — выбор филиалов скрыт.", "WARNING");
            _branches = Array.Empty<PromotionRef>();
        }
        _branchList.SetItems(_branches);
        _branchesBlock.IsVisible = _branches.Count > 1 || _draft.Branches.Count > 0; // один филиал — выбирать нечего
    }

    private async Task LoadProductNamesAsync(IReadOnlyList<string> ids)
    {
        foreach (var id in ids.Where(i => !_productNames.ContainsKey(i)).Take(40))
        {
            try
            {
                if (await _api.ProductAsync(id, _cts.Token).ConfigureAwait(true) is { } p)
                    _productNames[id] = p;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Акции: товар {id} не загружен ({ex.GetType().Name}).", "WARNING");
            }
        }
        _includePicker.RenderChosen();
        _excludePicker.RenderChosen();
    }

    internal string ProductName(string id) =>
        _productNames.TryGetValue(id, out var p) && p.Name.Length > 0 ? p.Name : T("Товар ", "Товар ", "Product ", "Ürün ", "Mahsulot ") + "…" + (id.Length > 6 ? id[^6..] : id);

    // ── Шаги ──────────────────────────────────────────────────────────────────────

    private void GoToStep(int step, bool validate)
    {
        if (step < 1 || step > StepCount || _saving)
            return;
        ClearErrorMarks();
        if (validate && step > _step && !ReadStep(_step, out var error, out var field))
        {
            SetWizardStatus(error, "BrushDanger");
            Mark(field);
            return;
        }
        ReadStep(_step, out _, out _); // перенести введённое в _draft (без проверки)
        _step = step;
        SetWizardStatus("", "BrushTextSoft");
        ShowStep();
    }

    private void ShowStep()
    {
        for (var i = 0; i < _stepButtons.Count; i++)
        {
            UiKit.SetActive(_stepButtons[i], i + 1 == _step);
            _stepButtons[i].Classes.Set("active", i + 1 == _step);
        }
        if (_step == 2)
            RefreshKindUi();
        if (_step == 5)
            RefreshConditionsUi();
        if (_step == 6)
            EnterPreviewStep();
        _stepScroll.Content = _stepPanels[_step - 1];
        _stepScroll.Offset = default;
        UpdateWizardButtons();
    }

    private void UpdateWizardButtons()
    {
        var editing = _draft.Id.Length > 0;
        _back.IsVisible = _step > 1;
        _next.IsVisible = _step < StepCount;
        _saveDraft.IsVisible = (_step == StepCount || editing) && _draft.IsDraft;
        _startButton.IsVisible = _step == StepCount && _draft.IsDraft;
        _saveChanges.IsVisible = editing && !_draft.IsDraft;
    }

    private void BuildSteps()
    {
        // 2026-11-27: мастер открывается много раз — общие (readonly) элементы снимаются с прежних шагов, иначе «already has a visual parent».
        foreach (var c in new Control[] { _kindLockNote, _paramsHost, _example, _scopeSomeHost, _sampleHost, _previewResult, _summary })
            Detach(c);
        _scopeSomeHost.Children.Clear();
        _paramsHost.Children.Clear();
        _kindCards.Clear();
        _stepPanels[0] = BuildTitleStep();
        _stepPanels[1] = BuildKindStep();
        _stepPanels[2] = BuildScopeStep();
        _stepPanels[3] = BuildWhenStep();
        _stepPanels[4] = BuildConditionsStep();
        _stepPanels[5] = BuildPreviewStep();
    }

    private StackPanel StepPanel(string lead)
    {
        var p = new StackPanel { Spacing = 4, MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 12, 12) };
        var t = new TextBlock { Text = lead, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        p.Children.Add(t);
        return p;
    }

    // 1. Название
    private Control BuildTitleStep()
    {
        var p = StepPanel(T("Название видят кассир (метка в строке чека) и покупатель (в чеке). Коротко и понятно: «2+1 на воду», «Счастливые часы −15 %».",
            "Аталышты кассир (чектин сабындагы белги) жана сатып алуучу (чекте) көрөт. Кыска жана түшүнүктүү: «Сууга 2+1», «Бактылуу сааттар −15 %».",
            "The cashier (label on the receipt line) and the customer (on the receipt) see the name. Short and clear: “2+1 on water”, “Happy hours −15 %”.",
            "Adı kasiyer (fiş satırındaki etiket) ve müşteri (fişte) görür. Kısa ve anlaşılır: «Suya 2+1», «Mutlu saatler −%15».",
            "Nomni kassir (chek qatoridagi belgi) va xaridor (chekda) ko'radi. Qisqa va tushunarli: «Suvga 2+1», «Baxtli soatlar −15 %»."));
        _titleBox = Box(T("Например: 2+1 на воду", "Мисалы: Сууга 2+1", "E.g. 2+1 on water", "Örn. Suya 2+1", "Masalan: Suvga 2+1"), _draft.Title);
        _titleBox.MaxLength = 120;
        AutomationProperties.SetName(_titleBox, T("Название акции", "Акциянын аталышы", "Promotion name", "Kampanya adı", "Aksiya nomi"));
        _descriptionBox = Box(T("Для себя: зачем акция, до чего договорились с поставщиком…", "Өзүңүз үчүн: акция эмне үчүн, жеткирүүчү менен эмнеге макулдаштыңыз…",
            "For yourself: why the promotion, what was agreed with the supplier…", "Kendiniz için: kampanya neden, tedarikçiyle ne kararlaştırıldı…",
            "O'zingiz uchun: aksiya nima uchun, yetkazib beruvchi bilan nimaga kelishildi…"), _draft.Description);
        _descriptionBox.Height = 88;
        _descriptionBox.AcceptsReturn = true;
        _descriptionBox.TextWrapping = TextWrapping.Wrap;
        _descriptionBox.VerticalContentAlignment = VerticalAlignment.Top;
        _descriptionBox.Padding = new Thickness(12, 8);
        _descriptionBox.MaxLength = 500;
        AutomationProperties.SetName(_descriptionBox, T("Описание акции", "Акциянын сүрөттөмөсү", "Promotion description", "Kampanya açıklaması", "Aksiya tavsifi"));
        p.Children.Add(Labeled(T("Название акции", "Акциянын аталышы", "Promotion name", "Kampanya adı", "Aksiya nomi"), _titleBox));
        p.Children.Add(Labeled(T("Описание (необязательно)", "Сүрөттөмө (милдеттүү эмес)", "Description (optional)", "Açıklama (isteğe bağlı)", "Tavsif (ixtiyoriy)"), _descriptionBox));
        return p;
    }

    // 2. Вид
    private Control BuildKindStep()
    {
        var p = StepPanel(T("Выберите, какую скидку даёт акция. Ниже — пример расчёта, он меняется вместе с числами.", "Акция кандай арзандатуу берерин тандаңыз. Төмөндө — эсептөөнүн мисалы, ал сандар менен бирге өзгөрөт.",
            "Choose what discount the promotion gives. Below is a calculation example that changes with the numbers.", "Kampanyanın hangi indirimi vereceğini seçin. Aşağıda sayılarla birlikte değişen bir hesap örneği var.",
            "Aksiya qanday chegirma berishini tanlang. Pastda — hisob-kitob misoli, u sonlar bilan birga o'zgaradi."));
        Use(_kindLockNote, TextBlock.ForegroundProperty, "BrushWarning");
        _kindLockNote.Text = T("Акция уже запускалась — вид менять нельзя (так касса и отчёт не перепутают старые чеки). Можно менять числа. Нужен другой вид — завершите акцию и создайте новую.",
            "Акция мурда иштетилген — түрүн өзгөртүүгө болбойт (касса жана отчёт эски чектерди чаташтырбашы үчүн). Сандарды өзгөртүүгө болот. Башка түрү керек болсо — акцияны аяктап, жаңысын түзүңүз.",
            "The promotion has been started — its type can't be changed (so the till and the report don't mix up old receipts). Numbers can be changed. Need another type — finish it and create a new one.",
            "Kampanya başlatıldı — türü değiştirilemez (kasa ve rapor eski fişleri karıştırmasın diye). Sayılar değiştirilebilir. Başka tür gerekiyorsa — bitirip yenisini oluşturun.",
            "Aksiya boshlangan — turini o'zgartirib bo'lmaydi (kassa va hisobot eski cheklarni adashtirmasligi uchun). Sonlarni o'zgartirish mumkin. Boshqa tur kerak bo'lsa — yakunlab, yangisini yarating.");
        p.Children.Add(_kindLockNote);

        var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var kinds = new[]
        {
            (Promotion.KindBuyNGetM, T("Купи N — получи M", "N сатып ал — M ал", "Buy N, get M", "N al, M kazan", "N ta ol — M ta ol"),
                T("«2+1»: каждая третья штука бесплатно или со скидкой. Бесплатной считается самая дешёвая.", "«2+1»: ар бир үчүнчүсү бекер же арзан. Эң арзаны бекер саналат.",
                    "“2+1”: every third item free or discounted. The cheapest one counts as free.", "«2+1»: her üçüncü ürün ücretsiz veya indirimli. En ucuzu ücretsiz sayılır.",
                    "«2+1»: har uchinchisi bepul yoki chegirmali. Eng arzoni bepul hisoblanadi.")),
            (Promotion.KindPercent, T("Скидка в процентах", "Пайыздык арзандатуу", "Percent discount", "Yüzde indirim", "Foizli chegirma"),
                T("Например, −15 % на молочку. Можно с порогом суммы и пределом штук.", "Мисалы, сүт азыктарына −15 %. Сумманын босогосу жана даана чеги менен болот.",
                    "E.g. −15 % on dairy. Optionally with an amount threshold and a quantity limit.", "Örn. süt ürünlerine −%15. Tutar eşiği ve adet sınırıyla olabilir.",
                    "Masalan, sut mahsulotlariga −15 %. Summa chegarasi va dona cheklovi bilan bo'lishi mumkin.")),
            (Promotion.KindAmount, T("Скидка суммой", "Сумма менен арзандатуу", "Amount discount", "Tutar indirimi", "Summali chegirma"),
                T("Например, −20 сом за каждую штуку или на всю строку.", "Мисалы, ар бир даанага же бүт сапка −20 сом.",
                    "E.g. −20 som per item or per whole line.", "Örn. her adet veya tüm satır için −20 som.", "Masalan, har bir donaga yoki butun qatorga −20 so'm.")),
            (Promotion.KindFixedPrice, T("Цена по акции", "Акциялык баа", "Promo price", "Kampanya fiyatı", "Aksiya narxi"),
                T("Например, «Хлеб за 79». Цена штуки на время акции.", "Мисалы, «Нан 79га». Акция убагында даананын баасы.",
                    "E.g. “Bread for 79”. The item price during the promotion.", "Örn. «Ekmek 79'a». Kampanya süresince adet fiyatı.", "Masalan, «Non 79 ga». Aksiya vaqtida dona narxi.")),
        };
        for (var i = 0; i < kinds.Length; i++)
        {
            var (kind, title, text) = kinds[i];
            var k = kind;
            var t1 = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
            var t2 = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.Normal, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }; // выбранная карточка жирная — описание остаётся обычным
            var card = new Button
            {
                Content = new StackPanel { Children = { t1, t2 } },
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                Padding = new Thickness(14, 10), MinHeight = 72, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Focusable = false,
                Margin = new Thickness(i % 2 == 0 ? 0 : 6, i < 2 ? 0 : 6, i % 2 == 0 ? 6 : 0, 0),
            };
            card.Classes.Add("sec-chip");
            AutomationProperties.SetName(card, title);
            card.Click += (_, _) =>
            {
                if (_draft.KindLocked)
                    return;
                ReadParams(out _, out _);
                _draft.Kind = k;
                RefreshKindUi();
            };
            Grid.SetColumn(card, i % 2);
            Grid.SetRow(card, i / 2);
            cards.Children.Add(card);
            _kindCards[kind] = card;
        }
        p.Children.Add(cards);

        _buyBox = NumBox(_draft.Buy.ToString(CultureInfo.InvariantCulture));
        _getBox = NumBox(_draft.Get.ToString(CultureInfo.InvariantCulture));
        _getPctBox = NumBox(PromotionText.Num(_draft.GetDiscountPercent));
        _percentBox = NumBox(PromotionText.Num(_draft.Percent));
        _minAmountBox = NumBox(_draft.MinAmount is > 0 ? PromotionText.Num(_draft.MinAmount.Value) : "", T("необязательно", "милдеттүү эмес", "optional", "isteğe bağlı", "ixtiyoriy"));
        _maxQtyBox = NumBox(_draft.MaxQty is > 0 ? PromotionText.Num(_draft.MaxQty.Value) : "", T("необязательно", "милдеттүү эмес", "optional", "isteğe bağlı", "ixtiyoriy"));
        _amountBox = NumBox(PromotionText.Num(_draft.Amount));
        _priceBox = NumBox(_draft.Price > 0 ? PromotionText.Num(_draft.Price) : "");
        foreach (var box in new[] { _buyBox, _getBox, _getPctBox, _percentBox, _minAmountBox, _maxQtyBox, _amountBox, _priceBox })
            box.TextChanged += (_, _) => UpdateExample();
        _perUnit = UiKit.Chip(this, T("За каждую штуку", "Ар бир даанага", "Per item", "Adet başına", "Har bir donaga"), _draft.Per != "line");
        _perLine = UiKit.Chip(this, T("На всю строку", "Бүт сапка", "Per whole line", "Tüm satıra", "Butun qatorga"), _draft.Per == "line");
        _perUnit.Click += (_, _) => SetPer("unit");
        _perLine.Click += (_, _) => SetPer("line");
        p.Children.Add(_paramsHost);

        var exampleCaption = Caption(T("Пример расчёта", "Эсептөөнүн мисалы", "Calculation example", "Hesap örneği", "Hisob-kitob misoli"));
        var exampleBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10), BorderThickness = new Thickness(1), Child = _example };
        Use(exampleBox, Border.BackgroundProperty, "BrushAccentSoft");
        Use(exampleBox, Border.BorderBrushProperty, "BrushBorder");
        Use(_example, TextBlock.ForegroundProperty, "BrushText");
        exampleCaption.Margin = new Thickness(0, 12, 0, 4);
        p.Children.Add(exampleCaption);
        p.Children.Add(exampleBox);
        return p;
    }

    private void SetPer(string per)
    {
        _draft.Per = per;
        UiKit.SetActive(_perUnit, per != "line");
        UiKit.SetActive(_perLine, per == "line");
        UpdateExample();
    }

    private void RefreshKindUi()
    {
        foreach (var (kind, card) in _kindCards)
        {
            UiKit.SetActive(card, kind == _draft.Kind);
            card.IsEnabled = !_draft.KindLocked || kind == _draft.Kind;
        }
        _kindLockNote.IsVisible = _draft.KindLocked;
        _paramsHost.Children.Clear();
        var cells = new WrapPanel { Orientation = Orientation.Horizontal };
        switch (_draft.Kind)
        {
            case Promotion.KindBuyNGetM:
                cells.Children.Add(Cell(T("Купить, шт. (N)", "Сатып алуу, даана (N)", "Buy, pcs (N)", "Al, adet (N)", "Olish, dona (N)"), _buyBox));
                cells.Children.Add(Cell(T("Получить, шт. (M)", "Алуу, даана (M)", "Get, pcs (M)", "Kazan, adet (M)", "Olish, dona (M)"), _getBox));
                cells.Children.Add(Cell(T("Скидка на M, %", "M үчүн арзандатуу, %", "Discount on M, %", "M için indirim, %", "M uchun chegirma, %"), _getPctBox));
                _paramsHost.Children.Add(cells);
                _paramsHost.Children.Add(Hint(T("100 % — бесплатно. Бесплатными считаются самые дешёвые штуки из подходящих товаров.", "100 % — бекер. Ылайыктуу товарлардын эң арзандары бекер саналат.",
                    "100 % means free. The cheapest of the matching items count as free.", "%100 — ücretsiz. Uygun ürünlerin en ucuzları ücretsiz sayılır.", "100 % — bepul. Mos mahsulotlarning eng arzonlari bepul hisoblanadi.")));
                break;
            case Promotion.KindPercent:
                cells.Children.Add(Cell(T("Скидка, %", "Арзандатуу, %", "Discount, %", "İndirim, %", "Chegirma, %"), _percentBox));
                cells.Children.Add(Cell(T("Если строка от, сом", "Сап ушундан, сом", "If the line is from, som", "Satır şundan itibaren, som", "Agar qator dan, so'm"), _minAmountBox));
                cells.Children.Add(Cell(T("Не больше, шт.", "Ашпаган, даана", "At most, pcs", "En fazla, adet", "Ko'pi bilan, dona"), _maxQtyBox));
                _paramsHost.Children.Add(cells);
                _paramsHost.Children.Add(Hint(T("Порог и предел необязательны: пусто — скидка на любую сумму и любое количество.", "Босого жана чек милдеттүү эмес: бош — каалаган суммага жана санга арзандатуу.",
                    "Threshold and limit are optional: empty means any amount and any quantity.", "Eşik ve sınır isteğe bağlı: boş — her tutara ve her adede indirim.", "Chegara va cheklov ixtiyoriy: bo'sh — istalgan summa va miqdorga chegirma.")));
                break;
            case Promotion.KindAmount:
                cells.Children.Add(Cell(T("Скидка, сом", "Арзандатуу, сом", "Discount, som", "İndirim, som", "Chegirma, so'm"), _amountBox));
                _paramsHost.Children.Add(cells);
                _paramsHost.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { WithMargin(_perUnit), WithMargin(_perLine) } });
                break;
            case Promotion.KindFixedPrice:
                cells.Children.Add(Cell(T("Цена по акции, сом", "Акциялык баа, сом", "Promo price, som", "Kampanya fiyatı, som", "Aksiya narxi, so'm"), _priceBox));
                _paramsHost.Children.Add(cells);
                _paramsHost.Children.Add(Hint(T("Если обычная цена товара ниже — касса оставит обычную.", "Товардын кадимки баасы төмөн болсо — касса кадимкисин калтырат.",
                    "If the regular price is lower, the till keeps the regular one.", "Ürünün normal fiyatı daha düşükse kasa normal fiyatı bırakır.", "Agar mahsulotning oddiy narxi past bo'lsa — kassa oddiysini qoldiradi.")));
                break;
        }
        UpdateExample();
    }

    private static void Detach(Control c)
    {
        switch (c.Parent)
        {
            case Panel panel:
                panel.Children.Remove(c);
                break;
            case Decorator decorator:
                decorator.Child = null;
                break;
            case ContentControl content:
                content.Content = null;
                break;
        }
    }

    private static Button WithMargin(Button b)
    {
        b.Margin = new Thickness(0, 0, 8, 6);
        return b;
    }

    private void UpdateExample()
    {
        var copy = _draft.Clone();
        TryNum(_buyBox?.Text, out var buy);
        TryNum(_getBox?.Text, out var get);
        copy.Buy = (int)buy;
        copy.Get = (int)get;
        if (TryNum(_getPctBox?.Text, out var gp)) copy.GetDiscountPercent = gp;
        if (TryNum(_percentBox?.Text, out var pc)) copy.Percent = pc;
        copy.MinAmount = TryNum(_minAmountBox?.Text, out var mn) ? mn : null;
        copy.MaxQty = TryNum(_maxQtyBox?.Text, out var mq) ? mq : null;
        if (TryNum(_amountBox?.Text, out var am)) copy.Amount = am;
        if (TryNum(_priceBox?.Text, out var pr)) copy.Price = pr;
        _example.Text = PromotionText.Example(copy);
    }

    // 3. На что действует
    private Control BuildScopeStep()
    {
        var p = StepPanel(T("Выберите товары, на которые действует акция. Категория включает все свои подкатегории. Исключения сильнее: исключённый товар скидку не получит.",
            "Акция таасир эткен товарларды тандаңыз. Категория өзүнүн бардык подкатегорияларын камтыйт. Чыгарылгандар күчтүүрөөк: чыгарылган товар арзандатуу албайт.",
            "Choose the products the promotion applies to. A category includes all its subcategories. Exclusions win: an excluded product gets no discount.",
            "Kampanyanın uygulanacağı ürünleri seçin. Kategori tüm alt kategorilerini içerir. Hariç tutulanlar önceliklidir: hariç ürün indirim almaz.",
            "Aksiya amal qiladigan mahsulotlarni tanlang. Toifa barcha ichki toifalarini o'z ichiga oladi. Istisnolar kuchliroq: istisno qilingan mahsulot chegirma olmaydi."));
        _scopeAllChip = UiKit.Chip(this, T("Все товары", "Бардык товарлар", "All products", "Tüm ürünler", "Barcha mahsulotlar"), _draft.ScopeAll);
        _scopeSomeChip = UiKit.Chip(this, T("Выбранные товары, категории, бренды", "Тандалган товарлар, категориялар, бренддер", "Selected products, categories, brands",
            "Seçili ürünler, kategoriler, markalar", "Tanlangan mahsulotlar, toifalar, brendlar"), !_draft.ScopeAll);
        _scopeAllChip.Click += (_, _) => SetScopeAll(true);
        _scopeSomeChip.Click += (_, _) => SetScopeAll(false);
        p.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { WithMargin(_scopeAllChip), WithMargin(_scopeSomeChip) } });

        _includePicker = new ProductPicker(this, _draft.Products, T("Найти товар по названию или штрихкоду", "Товарды аталышы же штрихкоду боюнча табуу", "Find a product by name or barcode",
            "Ürünü ada veya barkoda göre bul", "Mahsulotni nomi yoki shtrix-kodi bo'yicha topish"), null);
        _excludePicker = new ProductPicker(this, _draft.ExcludeProducts, T("Найти товар, который исключить", "Чыгарыла турган товарды табуу", "Find a product to exclude",
            "Hariç tutulacak ürünü bul", "Istisno qilinadigan mahsulotni topish"), null);
        _categoryList = new RefChecklist(this, _draft.Categories, tree: true);
        _brandList = new RefChecklist(this, _draft.Brands, tree: false);

        _scopeSomeHost.Children.Add(Section(T("Товары", "Товарлар", "Products", "Ürünler", "Mahsulotlar"), _includePicker.View));
        _scopeSomeHost.Children.Add(Section(T("Категории (с подкатегориями)", "Категориялар (подкатегориялары менен)", "Categories (with subcategories)", "Kategoriler (alt kategorileriyle)", "Toifalar (ichki toifalari bilan)"), _categoryList.View));
        _scopeSomeHost.Children.Add(Section(T("Бренды", "Бренддер", "Brands", "Markalar", "Brendlar"), _brandList.View));
        _scopeBox = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(0), Child = _scopeSomeHost };
        p.Children.Add(_scopeBox);
        p.Children.Add(Section(T("Исключить товары (необязательно)", "Товарларды чыгаруу (милдеттүү эмес)", "Exclude products (optional)", "Ürünleri hariç tut (isteğe bağlı)", "Mahsulotlarni istisno qilish (ixtiyoriy)"), _excludePicker.View));
        SetScopeAll(_draft.ScopeAll);
        return p;
    }

    private void SetScopeAll(bool all)
    {
        _draft.ScopeAll = all;
        UiKit.SetActive(_scopeAllChip, all);
        UiKit.SetActive(_scopeSomeChip, !all);
        _scopeBox.IsVisible = !all;
    }

    private Control Section(string title, Control body)
    {
        var caption = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        Use(caption, TextBlock.ForegroundProperty, "BrushText");
        var card = Card(new StackPanel { Children = { caption, body } });
        card.Margin = new Thickness(0, 6, 0, 0);
        return card;
    }

    // 4. Когда
    private Control BuildWhenStep()
    {
        var p = StepPanel(T("Период, дни и часы — по времени магазина (Бишкек). Пустая дата окончания — акция бессрочная. Ни одного дня не выбрано — каждый день.",
            "Мезгил, күндөр жана сааттар — дүкөндүн убакыты боюнча (Бишкек). Аяктоо датасы бош — акция мөөнөтсүз. Бир да күн тандалбаса — күн сайын.",
            "Period, days and hours — store time (Bishkek). Empty end date — the promotion has no end. No day selected — every day.",
            "Dönem, günler ve saatler — mağaza saatine göre (Bişkek). Bitiş tarihi boş — kampanya süresiz. Gün seçilmezse — her gün.",
            "Davr, kunlar va soatlar — do'kon vaqti bo'yicha (Bishkek). Tugash sanasi bo'sh — aksiya muddatsiz. Birorta kun tanlanmasa — har kuni."));
        _startOriginal = _draft.StartsAt is { } s ? PromotionsApi.Local(s).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
        _endOriginal = _draft.EndsAt is { } e ? PromotionsApi.Local(e).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
        _startBox = Box(T("дд.мм.гггг", "кк.аа.жжжж", "dd.mm.yyyy", "gg.aa.yyyy", "kk.oo.yyyy"), _startOriginal);
        _endBox = Box(T("пусто — бессрочно", "бош — мөөнөтсүз", "empty — no end", "boş — süresiz", "bo'sh — muddatsiz"), _endOriginal);
        _startBox.MaxLength = _endBox.MaxLength = 10;
        var dates = new WrapPanel { Orientation = Orientation.Horizontal };
        dates.Children.Add(Cell(T("Начало (дата)", "Башталышы (дата)", "Start (date)", "Başlangıç (tarih)", "Boshlanishi (sana)"), _startBox));
        dates.Children.Add(Cell(T("Окончание (дата)", "Аякташы (дата)", "End (date)", "Bitiş (tarih)", "Tugashi (sana)"), _endBox));
        p.Children.Add(dates);

        p.Children.Add(Caption(T("Дни недели", "Жуманын күндөрү", "Days of the week", "Haftanın günleri", "Hafta kunlari")));
        var days = new WrapPanel { Orientation = Orientation.Horizontal };
        _dayChips.Clear();
        for (var d = 1; d <= 7; d++)
        {
            var day = d;
            var chip = UiKit.Chip(this, PromotionText.DayShort(d), _draft.Weekdays.Contains(d));
            chip.MinWidth = 56;
            chip.Margin = new Thickness(0, 0, 6, 6);
            chip.Click += (_, _) =>
            {
                if (!_draft.Weekdays.Remove(day))
                    _draft.Weekdays.Add(day);
                UiKit.SetActive(chip, _draft.Weekdays.Contains(day));
            };
            _dayChips[d] = chip;
            days.Children.Add(chip);
        }
        p.Children.Add(days);
        p.Children.Add(Hint(T("Не выбрано ни одного — акция действует каждый день.", "Бир да күн тандалган жок — акция күн сайын иштейт.", "None selected — the promotion works every day.",
            "Hiçbiri seçilmedi — kampanya her gün geçerli.", "Birortasi tanlanmagan — aksiya har kuni amal qiladi.")));

        _timeFromBox = Box(T("ЧЧ:ММ", "СС:ММ", "HH:MM", "SS:DD", "SS:DD"), _draft.TimeFrom ?? "");
        _timeToBox = Box(T("ЧЧ:ММ", "СС:ММ", "HH:MM", "SS:DD", "SS:DD"), _draft.TimeTo ?? "");
        _timeFromBox.MaxLength = _timeToBox.MaxLength = 5;
        var hours = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        hours.Children.Add(Cell(T("Часы: с", "Сааттар: башталышы", "Hours: from", "Saatler: başlangıç", "Soatlar: dan"), _timeFromBox));
        hours.Children.Add(Cell(T("Часы: до", "Сааттар: аягы", "Hours: until", "Saatler: bitiş", "Soatlar: gacha"), _timeToBox));
        p.Children.Add(hours);
        p.Children.Add(Hint(T("Пусто — весь день. «До» не включается: 10:00–14:00 — чек в 13:59 со скидкой, в 14:00 уже без. Можно через полночь: 22:00–02:00.",
            "Бош — күнү бою. «Аягы» кирбейт: 10:00–14:00 — 13:59 чеги арзандатуу менен, 14:00 — арзандатуусуз. Түн ортосу аркылуу болот: 22:00–02:00.",
            "Empty — all day. “Until” is not included: 10:00–14:00 — a receipt at 13:59 gets the discount, at 14:00 it doesn't. Past midnight works: 22:00–02:00.",
            "Boş — tüm gün. «Bitiş» dahil değil: 10:00–14:00 — 13:59 fişi indirimli, 14:00 indirimsiz. Gece yarısını geçebilir: 22:00–02:00.",
            "Bo'sh — kun bo'yi. «Gacha» kirmaydi: 10:00–14:00 — 13:59 dagi chek chegirma bilan, 14:00 da — chegirmasiz. Yarim tun orqali mumkin: 22:00–02:00.")));
        return p;
    }

    // 5. Условия
    private Control BuildConditionsStep()
    {
        var p = StepPanel(T("Кому и как сочетается скидка. Если не уверены — оставьте как есть.", "Арзандатуу кимге жана кантип айкалышат. Ишенимиңиз жок болсо — ушундай калтырыңыз.",
            "Who gets the discount and how it combines. If unsure, leave as is.", "İndirim kime ve nasıl birleşir. Emin değilseniz olduğu gibi bırakın.", "Chegirma kimga va qanday qo'shiladi. Ishonchingiz komil bo'lmasa — shundayligicha qoldiring."));
        p.Children.Add(Caption(T("Кому", "Кимге", "Who", "Kime", "Kimga")));
        _audienceAll = UiKit.Chip(this, T("Всем покупателям", "Бардык сатып алуучуларга", "All customers", "Tüm müşterilere", "Barcha xaridorlarga"), _draft.Audience != "clients");
        _audienceClients = UiKit.Chip(this, T("Только клиентам (в чеке выбран клиент)", "Кардарларга гана (чекте кардар тандалган)", "Customers only (a customer is chosen on the receipt)",
            "Yalnızca müşterilere (fişte müşteri seçili)", "Faqat mijozlarga (chekda mijoz tanlangan)"), _draft.Audience == "clients");
        _audienceAll.Click += (_, _) => SetAudience("all");
        _audienceClients.Click += (_, _) => SetAudience("clients");
        p.Children.Add(new WrapPanel { Orientation = Orientation.Horizontal, Children = { WithMargin(_audienceAll), WithMargin(_audienceClients) } });

        _priorityBox = NumBox(_draft.Priority.ToString(CultureInfo.InvariantCulture));
        _limitBox = NumBox(_draft.LimitPerReceipt is > 0 ? _draft.LimitPerReceipt.Value.ToString(CultureInfo.InvariantCulture) : "", T("без лимита", "чексиз", "no limit", "sınırsız", "cheklovsiz"));
        var priorityCell = Cell(T("Приоритет", "Артыкчылык", "Priority", "Öncelik", "Ustuvorlik"), _priorityBox);
        var limitCell = Cell("", _limitBox);
        _limitCaption = (TextBlock)((StackPanel)limitCell).Children[0];
        var numbers = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        numbers.Children.Add(priorityCell);
        numbers.Children.Add(limitCell);
        p.Children.Add(numbers);
        p.Children.Add(Hint(T("Приоритет: больше число — важнее. Если на товар подходят две акции, сработает та, у которой приоритет больше (при равном — выгоднее покупателю).",
            "Артыкчылык: сан чоң болсо — маанилүүрөөк. Товарга эки акция туура келсе, артыкчылыгы чоңу иштейт (бирдей болсо — сатып алуучуга пайдалуусу).",
            "Priority: a bigger number wins. If two promotions match a product, the one with higher priority applies (if equal — the better one for the customer).",
            "Öncelik: büyük sayı önemlidir. Bir ürüne iki kampanya uyarsa önceliği yüksek olan uygulanır (eşitse müşteriye daha avantajlı olan).",
            "Ustuvorlik: katta son — muhimroq. Mahsulotga ikkita aksiya mos kelsa, ustuvorligi kattasi ishlaydi (teng bo'lsa — xaridorga foydalirog'i).")));

        _stackManual = Check(T("Сочетается с ручной скидкой кассира", "Кассирдин кол менен арзандатуусу менен айкалышат", "Combines with the cashier's manual discount",
            "Kasiyerin manuel indirimiyle birleşir", "Kassirning qo'lda chegirmasi bilan qo'shiladi"), _draft.StackableWithManual);
        _stackPromos = Check(T("Сочетается с другими акциями", "Башка акциялар менен айкалышат", "Combines with other promotions", "Diğer kampanyalarla birleşir", "Boshqa aksiyalar bilan qo'shiladi"), _draft.StackableWithPromos);
        p.Children.Add(_stackManual);
        p.Children.Add(Hint(T("Без галочки кассир не сможет дать ручную скидку на строку с этой акцией.", "Белгисиз кассир бул акциясы бар сапка кол менен арзандатуу бере албайт.",
            "Without the tick the cashier can't give a manual discount on a line with this promotion.", "İşaretsiz kasiyer bu kampanyalı satıra manuel indirim veremez.",
            "Belgisiz kassir bu aksiyali qatorga qo'lda chegirma bera olmaydi.")));
        p.Children.Add(_stackPromos);
        p.Children.Add(Hint(T("Две акции суммируются, только если галочка стоит у обеих.", "Эки акция экөөндө тең белги болгондо гана кошулат.", "Two promotions add up only if both have this tick.",
            "İki kampanya yalnızca ikisinde de işaret varsa toplanır.", "Ikkita aksiya faqat ikkalasida ham belgi bo'lsa qo'shiladi.")));

        _branchList = new RefChecklist(this, _draft.Branches, tree: false);
        _branchesBlock = Section(T("Филиалы (ничего не выбрано — все)", "Филиалдар (эч нерсе тандалбаса — баары)", "Branches (none selected — all)", "Şubeler (seçilmezse — tümü)", "Filiallar (tanlanmasa — hammasi)"), _branchList.View);
        _branchesBlock.IsVisible = false;
        p.Children.Add(_branchesBlock);

        _belowCost = Check(T("Разрешить продажу ниже закупки", "Сатып алуу баасынан төмөн сатууга уруксат", "Allow selling below cost", "Alış fiyatının altında satışa izin ver", "Xarid narxidan past sotishga ruxsat"), _draft.AllowBelowCost);
        var warn = new TextBlock
        {
            Text = T("Осторожно: магазин будет продавать в убыток. Без галочки касса сама урежет скидку так, чтобы цена не упала ниже закупки. Включает только владелец.",
                "Абайлаңыз: дүкөн зыян менен сатат. Белгисиз касса баа сатып алуудан төмөн түшпөшү үчүн арзандатууну өзү кыскартат. Ээси гана күйгүзөт.",
                "Careful: the store will sell at a loss. Without the tick the till itself caps the discount so the price doesn't fall below cost. Only the owner can enable it.",
                "Dikkat: mağaza zararına satar. İşaretsiz kasa, fiyat alışın altına düşmesin diye indirimi kendisi kısar. Yalnızca sahip açabilir.",
                "Ehtiyot bo'ling: do'kon zarariga sotadi. Belgisiz kassa narx xariddan pastga tushmasligi uchun chegirmani o'zi qisqartiradi. Faqat egasi yoqadi."),
            FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(28, 0, 0, 0),
        };
        Use(warn, TextBlock.ForegroundProperty, "BrushDanger");
        var belowBlock = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 0), Children = { _belowCost, warn } };
        belowBlock.IsVisible = _isOwner || _draft.AllowBelowCost;
        _belowCost.IsEnabled = _isOwner;
        p.Children.Add(belowBlock);
        RefreshConditionsUi();
        return p;
    }

    private void SetAudience(string audience)
    {
        _draft.Audience = audience;
        UiKit.SetActive(_audienceAll, audience != "clients");
        UiKit.SetActive(_audienceClients, audience == "clients");
    }

    private void RefreshConditionsUi()
    {
        _limitCaption.Text = _draft.Kind == Promotion.KindBuyNGetM
            ? T("Лимит на чек, раз", "Бир чекте эң көп, жолу", "Limit per receipt, times", "Fiş başına limit, kez", "Bir chekda ko'pi bilan, marta")
            : T("Лимит на чек, шт.", "Бир чекте эң көп, даана", "Limit per receipt, pcs", "Fiş başına limit, adet", "Bir chekda ko'pi bilan, dona");
    }

    // ── Чтение и проверка шагов ───────────────────────────────────────────────────

    /// <summary>Перенести поля шага в _draft и проверить. false — error и поле, которое подсветить.</summary>
    private bool ReadStep(int step, out string error, out Control? field)
    {
        error = "";
        field = null;
        switch (step)
        {
            case 1:
                _draft.Title = (_titleBox.Text ?? "").Trim();
                _draft.Description = (_descriptionBox.Text ?? "").Trim();
                if (_draft.Title.Length == 0)
                {
                    error = T("Укажите название акции — его увидят кассир и покупатель в чеке.", "Акциянын аталышын жазыңыз — аны кассир жана чектеги сатып алуучу көрөт.",
                        "Enter the promotion name — the cashier and the customer see it on the receipt.", "Kampanya adını girin — kasiyer ve fişte müşteri görür.",
                        "Aksiya nomini kiriting — uni kassir va chekdagi xaridor ko'radi.");
                    field = _titleBox;
                    return false;
                }
                return true;
            case 2:
                return ReadParams(out error, out field);
            case 3:
                _draft.Products = _includePicker.Ids;
                _draft.ExcludeProducts = _excludePicker.Ids;
                _draft.Categories = _categoryList.Ids;
                _draft.Brands = _brandList.Ids;
                if (!_draft.ScopeAll && _draft.Products.Count + _draft.Categories.Count + _draft.Brands.Count == 0)
                {
                    error = T("Выберите хотя бы один товар, категорию или бренд — или нажмите «Все товары».", "Жок дегенде бир товар, категория же бренд тандаңыз — же «Бардык товарлар» басыңыз.",
                        "Choose at least one product, category or brand — or press “All products”.", "En az bir ürün, kategori veya marka seçin — ya da «Tüm ürünler»e basın.",
                        "Kamida bitta mahsulot, toifa yoki brend tanlang — yoki «Barcha mahsulotlar»ni bosing.");
                    field = _scopeBox;
                    return false;
                }
                return true;
            case 4:
                return ReadWhen(out error, out field);
            case 5:
                _draft.StackableWithManual = _stackManual.IsChecked == true;
                _draft.StackableWithPromos = _stackPromos.IsChecked == true;
                _draft.AllowBelowCost = _belowCost.IsChecked == true;
                _draft.Branches = _branchList.Ids;
                if (!int.TryParse((_priorityBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var prio) || prio < 0 || prio > 1000)
                {
                    error = T("Приоритет — целое число от 0 до 1000 (больше — важнее).", "Артыкчылык — 0дөн 1000ге чейинки бүтүн сан (чоңу — маанилүүрөөк).",
                        "Priority is a whole number from 0 to 1000 (bigger wins).", "Öncelik 0 ile 1000 arasında tam sayıdır (büyük olan önemlidir).",
                        "Ustuvorlik — 0 dan 1000 gacha butun son (kattasi muhimroq).");
                    field = _priorityBox;
                    return false;
                }
                _draft.Priority = prio;
                var lt = (_limitBox.Text ?? "").Trim();
                if (lt.Length == 0)
                    _draft.LimitPerReceipt = null;
                else if (int.TryParse(lt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lim) && lim > 0)
                    _draft.LimitPerReceipt = lim;
                else
                {
                    error = T("Лимит на чек — целое число больше нуля или пусто (без лимита).", "Бир чекке чек — нөлдөн чоң бүтүн сан же бош (чексиз).",
                        "Limit per receipt is a whole number above zero or empty (no limit).", "Fiş başına limit sıfırdan büyük tam sayı veya boş (sınırsız) olmalı.",
                        "Chekka cheklov — noldan katta butun son yoki bo'sh (cheklovsiz).");
                    field = _limitBox;
                    return false;
                }
                return true;
        }
        return true;
    }

    private bool ReadParams(out string error, out Control? field)
    {
        error = "";
        field = null;
        switch (_draft.Kind)
        {
            case Promotion.KindBuyNGetM:
                if (!TryInt(_buyBox.Text, out var buy) || buy < 1 || buy > 100)
                    return Fail(T("«Купить, шт.» — целое число от 1 до 100.", "«Сатып алуу, даана» — 1ден 100гө чейинки бүтүн сан.", "“Buy, pcs” is a whole number from 1 to 100.",
                        "«Al, adet» 1 ile 100 arasında tam sayı olmalı.", "«Olish, dona» — 1 dan 100 gacha butun son."), _buyBox, out error, out field);
                if (!TryInt(_getBox.Text, out var get) || get < 1 || get > 100)
                    return Fail(T("«Получить, шт.» — целое число от 1 до 100.", "«Алуу, даана» — 1ден 100гө чейинки бүтүн сан.", "“Get, pcs” is a whole number from 1 to 100.",
                        "«Kazan, adet» 1 ile 100 arasında tam sayı olmalı.", "«Olish, dona» — 1 dan 100 gacha butun son."), _getBox, out error, out field);
                if (!TryNum(_getPctBox.Text, out var gp) || gp <= 0 || gp > 100)
                    return Fail(T("Скидка на M — от 1 до 100 % (100 — бесплатно).", "M үчүн арзандатуу — 1ден 100 %га чейин (100 — бекер).", "Discount on M is from 1 to 100 % (100 = free).",
                        "M için indirim %1 ile %100 arasında (100 = ücretsiz).", "M uchun chegirma — 1 dan 100 % gacha (100 — bepul)."), _getPctBox, out error, out field);
                _draft.Buy = buy;
                _draft.Get = get;
                _draft.GetDiscountPercent = gp;
                _draft.Cheapest = true;
                return true;
            case Promotion.KindPercent:
                if (!TryNum(_percentBox.Text, out var pc) || pc <= 0 || pc > 100)
                    return Fail(T("Скидка — от 0,01 до 100 %.", "Арзандатуу — 0,01ден 100 %га чейин.", "The discount is from 0.01 to 100 %.", "İndirim %0,01 ile %100 arasında.",
                        "Chegirma — 0,01 dan 100 % gacha."), _percentBox, out error, out field);
                decimal? min = null, max = null;
                if ((_minAmountBox.Text ?? "").Trim().Length > 0)
                {
                    if (!TryNum(_minAmountBox.Text, out var m) || m < 0)
                        return Fail(T("Порог суммы — число сом или пусто.", "Сумманын босогосу — сом саны же бош.", "The amount threshold is a number of som or empty.",
                            "Tutar eşiği som cinsinden sayı veya boş olmalı.", "Summa chegarasi — so'm soni yoki bo'sh."), _minAmountBox, out error, out field);
                    min = m > 0 ? m : null;
                }
                if ((_maxQtyBox.Text ?? "").Trim().Length > 0)
                {
                    if (!TryNum(_maxQtyBox.Text, out var q) || q <= 0)
                        return Fail(T("«Не больше, шт.» — число больше нуля или пусто.", "«Ашпаган, даана» — нөлдөн чоң сан же бош.", "“At most, pcs” is a number above zero or empty.",
                            "«En fazla, adet» sıfırdan büyük sayı veya boş olmalı.", "«Ko'pi bilan, dona» — noldan katta son yoki bo'sh."), _maxQtyBox, out error, out field);
                    max = q;
                }
                _draft.Percent = pc;
                _draft.MinAmount = min;
                _draft.MaxQty = max;
                return true;
            case Promotion.KindAmount:
                if (!TryNum(_amountBox.Text, out var am) || am <= 0)
                    return Fail(T("Укажите сумму скидки в сомах (больше нуля).", "Арзандатуунун суммасын сом менен жазыңыз (нөлдөн чоң).", "Enter the discount amount in som (above zero).",
                        "İndirim tutarını som cinsinden girin (sıfırdan büyük).", "Chegirma summasini so'mda kiriting (noldan katta)."), _amountBox, out error, out field);
                _draft.Amount = am;
                return true;
            case Promotion.KindFixedPrice:
                if (!TryNum(_priceBox.Text, out var pr) || pr <= 0)
                    return Fail(T("Укажите цену по акции в сомах (больше нуля).", "Акциялык бааны сом менен жазыңыз (нөлдөн чоң).", "Enter the promo price in som (above zero).",
                        "Kampanya fiyatını som cinsinden girin (sıfırdan büyük).", "Aksiya narxini so'mda kiriting (noldan katta)."), _priceBox, out error, out field);
                _draft.Price = pr;
                return true;
        }
        return true;
    }

    private static readonly Regex TimeRx = new(@"^([01]?\d|2[0-3])[:.]([0-5]\d)$", RegexOptions.Compiled);

    private bool ReadWhen(out string error, out Control? field)
    {
        error = "";
        field = null;
        var startText = (_startBox.Text ?? "").Trim();
        var endText = (_endBox.Text ?? "").Trim();
        DateTimeOffset? start = _draft.StartsAt, end = _draft.EndsAt;
        if (startText != _startOriginal)
        {
            if (startText.Length == 0)
                start = null;
            else if (TryDate(startText, out var d))
                start = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, PromotionsApi.CompanyOffset);
            else
                return Fail(DateError(), _startBox, out error, out field);
        }
        if (endText != _endOriginal)
        {
            if (endText.Length == 0)
                end = null;
            else if (TryDate(endText, out var d))
                end = new DateTimeOffset(d.Year, d.Month, d.Day, 23, 59, 59, PromotionsApi.CompanyOffset);
            else
                return Fail(DateError(), _endBox, out error, out field);
        }
        if (start is { } s && end is { } e && e < s)
            return Fail(T("Дата окончания раньше даты начала — исправьте одну из них.", "Аяктоо датасы башталышынан эрте — бирөөнү оңдоңуз.", "The end date is before the start date — fix one of them.",
                "Bitiş tarihi başlangıçtan önce — birini düzeltin.", "Tugash sanasi boshlanishdan oldin — bittasini tuzating."), _endBox, out error, out field);

        string? from = null, to = null;
        var ft = (_timeFromBox.Text ?? "").Trim();
        var tt = (_timeToBox.Text ?? "").Trim();
        if (ft.Length > 0 || tt.Length > 0)
        {
            if (!TryTime(ft, out from))
                return Fail(TimeError(), _timeFromBox, out error, out field);
            if (!TryTime(tt, out to))
                return Fail(TimeError(), _timeToBox, out error, out field);
            if (from == to)
                return Fail(T("Часы «с» и «до» совпадают — укажите разные или оставьте оба пустыми (весь день).", "«Башталышы» жана «аягы» бирдей — башка жазыңыз же экөөнү тең бош калтырыңыз (күнү бою).",
                    "“From” and “until” are the same — set different ones or leave both empty (all day).", "«Başlangıç» ve «bitiş» aynı — farklı girin veya ikisini de boş bırakın (tüm gün).",
                    "«Dan» va «gacha» bir xil — boshqasini kiriting yoki ikkalasini bo'sh qoldiring (kun bo'yi)."), _timeToBox, out error, out field);
        }
        _draft.StartsAt = start;
        _draft.EndsAt = end;
        _draft.TimeFrom = from;
        _draft.TimeTo = to;
        _startOriginal = start is { } s2 ? PromotionsApi.Local(s2).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
        _endOriginal = end is { } e2 ? PromotionsApi.Local(e2).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "";
        _startBox.Text = _startOriginal;
        _endBox.Text = _endOriginal;
        return true;
    }

    private static string DateError() => T("Дата — в виде дд.мм.гггг, например 01.12.2026.", "Дата — кк.аа.жжжж түрүндө, мисалы 01.12.2026.", "Date as dd.mm.yyyy, e.g. 01.12.2026.",
        "Tarih gg.aa.yyyy biçiminde, örn. 01.12.2026.", "Sana — kk.oo.yyyy ko'rinishida, masalan 01.12.2026.");

    private static string TimeError() => T("Часы — в виде ЧЧ:ММ, например 10:00; заполните оба поля или оставьте оба пустыми.", "Сааттар — СС:ММ түрүндө, мисалы 10:00; эки талааны тең толтуруңуз же экөөнү тең бош калтырыңыз.",
        "Hours as HH:MM, e.g. 10:00; fill both fields or leave both empty.", "Saatler SS:DD biçiminde, örn. 10:00; iki alanı da doldurun veya ikisini de boş bırakın.",
        "Soatlar — SS:DD ko'rinishida, masalan 10:00; ikkala maydonni to'ldiring yoki ikkalasini bo'sh qoldiring.");

    private static bool TryDate(string text, out DateTime d) =>
        DateTime.TryParseExact(text.Replace('/', '.').Replace('-', '.'), new[] { "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out d);

    internal static bool TryTime(string text, out string? normalized)
    {
        normalized = null;
        var m = TimeRx.Match((text ?? "").Trim());
        if (!m.Success)
            return false;
        normalized = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture).ToString("00", CultureInfo.InvariantCulture) + ":" + m.Groups[2].Value;
        return true;
    }

    private static bool Fail(string text, Control box, out string error, out Control? field)
    {
        error = text;
        field = box;
        return false;
    }

    internal static bool TryNum(string? text, out decimal value) =>
        decimal.TryParse((text ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    private static bool TryInt(string? text, out int value) => int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    // ── Подсветка ошибок ──────────────────────────────────────────────────────────

    private void Mark(Control? field)
    {
        if (field == null)
            return;
        var prop = field is Border ? Border.BorderBrushProperty : Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty;
        _errorMarks[field] = field.Bind(prop, field.GetResourceObservable("BrushDanger"));
        if (field is TextBox box)
            box.Focus();
        field.BringIntoView();
    }

    private void ClearErrorMarks()
    {
        foreach (var d in _errorMarks.Values)
            d.Dispose();
        _errorMarks.Clear();
    }

    /// <summary>Поле мастера по имени поля сервера (promotion_invalid{field}).</summary>
    private Control? FieldControl(string? field) => (field ?? "").Trim() switch
    {
        "title" => _titleBox,
        "description" => _descriptionBox,
        "buy" => _buyBox,
        "get" => _getBox,
        "get_discount_percent" => _getPctBox,
        "percent" => _percentBox,
        "min_amount" => _minAmountBox,
        "max_qty" => _maxQtyBox,
        "amount" => _amountBox,
        "price" => _priceBox,
        "params" => _draft.Kind switch
        {
            Promotion.KindBuyNGetM => _buyBox,
            Promotion.KindPercent => _percentBox,
            Promotion.KindAmount => _amountBox,
            _ => _priceBox,
        },
        "scope" or "products" or "categories" or "brands" => _scopeBox,
        "starts_at" => _startBox,
        "ends_at" => _endBox,
        "time_from" => _timeFromBox,
        "time_to" => _timeToBox,
        "priority" => _priorityBox,
        "limit_per_receipt" => _limitBox,
        "allow_below_cost" => _belowCost,
        _ => null,
    };

    // ── Поля ──────────────────────────────────────────────────────────────────────

    private TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    /// <summary>Подпись над полем на всю ширину шага.</summary>
    private Control Labeled(string label, Control field)
    {
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        return new StackPanel { Margin = new Thickness(0, 0, 0, 10), Children = { Caption(label), field } };
    }

    /// <summary>Ячейка «подпись над полем» для WrapPanel: ширина ячейки фиксирована, поле растянуто по ней (чек-лист 20.11, п. 1).</summary>
    private Control Cell(string label, Control field, double width = 210)
    {
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        (field.Parent as Panel)?.Children.Remove(field);
        return new StackPanel { Width = width, Margin = new Thickness(0, 0, 12, 10), Children = { Caption(label), field } };
    }

    private TextBox Box(string watermark, string text)
    {
        var box = UiKit.Input(this, watermark, FieldHeight);
        box.Text = text;
        box.TextChanged += (_, _) =>
        {
            if (_errorMarks.Remove(box, out var d))
                d.Dispose();
        };
        return box;
    }

    private TextBox NumBox(string text, string watermark = "") => Box(watermark, text);

    private CheckBox Check(string text, bool value)
    {
        var c = new CheckBox { IsChecked = value, MinHeight = 44, Content = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center } };
        AutomationProperties.SetName(c, text);
        return c;
    }

    private void SetWizardStatus(string text, string brush)
    {
        _wizardStatus.Text = text;
        _wizardStatus.IsVisible = text.Length > 0;
        Use(_wizardStatus, TextBlock.ForegroundProperty, brush);
    }
}
