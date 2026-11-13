using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class ScreenSettingsView : UserControl
{
    /// <summary>Id == null represents "Авто (первая активная касса)".</summary>
    public sealed record CashboxOption(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    // Слайдер с Minimum="50" force-коэрсит значение по умолчанию (0) до 50 синхронно во время
    // InitializeComponent() — до того, как тело конструктора успело бы выставить флаг, поэтому
    // он выставлен по умолчанию инициализатором поля (см. тот же приём и объяснение в
    // ThemeSettingsDialog.axaml.cs для FontSizeSlider).
    private bool _suppressUiScaleChange = true;

    /// <summary>2026-11-05 (аудит №3, В4-10): подпись пункта «Авто» собирается при каждом обращении, а список касс пересобирается при смене языка
    /// (раньше после переключения на узбекский в списке оставался турецкий «Otomatik (ilk aktif kasa)»).</summary>
    private static string AutoCashboxText =>
        Tr.T("Авто (первая активная касса)", "Авто (биринчи активдүү касса)", "Auto (first active till)", "Otomatik (ilk aktif kasa)", "Avto (birinchi faol kassa)");

    private void OnLanguageChangedCashboxes()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CashboxCombo.ItemsSource is not IEnumerable<CashboxOption> current)
                return;
            var selectedId = (CashboxCombo.SelectedItem as CashboxOption)?.Id;
            var list = current.Select(o => o.Id is null ? new CashboxOption(null, AutoCashboxText) : o).ToList();
            CashboxCombo.ItemsSource = list;
            CashboxCombo.SelectedItem = list.FirstOrDefault(o => o.Id == selectedId) ?? list[0];
        });
    }

    public event EventHandler? SaveRequested;

    /// <summary>Раздельно от SaveRequested — масштаб применяется мгновенно при перетаскивании
    /// ползунка (не только по кнопке "Сохранить"), см. UiScaleSlider_ValueChanged. Хозяин этого
    /// UserControl (PosSettingsWindow) подписывается на это, чтобы масштабировать и себя тоже,
    /// не только MainWindow за собой.</summary>
    public event EventHandler? UiScaleChanged;

    /// <summary>2026-10-05, владелец: «в настройках сделай скрытие вкладок, чтобы клиент мог скрыть ненужные функции».
    /// Карточка «Разделы меню»: переключатель у каждого раздела (SectionVisibility); выключил — раздел пропал из меню.</summary>
    private void BuildSectionsCard()
    {
        // 2026-11-11 (Д-5): переключатели разделов действуют сразу — в проверку «несохранённых» не входят.
        var card = new Border { Classes = { "SettingsCard", NurMarketKassa.AvaloniaHost.Views.PosSettingsWindow.ImmediateSettingClass }, Padding = new Avalonia.Thickness(14) };
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = Tr.T("Разделы меню", "Менюнун бөлүмдөрү", "Menu sections", "Menü bölümleri", "Menyu bo'limlari"),
            Classes = { "SettingsCardTitle" }, FontSize = 14,
        });
        var hint = new TextBlock
        {
            Text = Tr.T("Выключите разделы, которыми не пользуетесь, — они пропадут из меню. Включить обратно можно здесь же. Доступ по тарифу и правам сотрудников не меняется.",
                "Колдонбогон бөлүмдөрдү өчүрүңүз — алар менюдан жоголот. Кайра ушул жерден күйгүзсө болот. Тариф жана кызматкерлердин укуктары боюнча мүмкүнчүлүк өзгөрбөйт.",
                "Turn off the sections you don't use — they disappear from the menu. You can turn them back on here. Access by plan and staff rights doesn't change.",
                "Kullanmadığınız bölümleri kapatın — menüden kaybolurlar. Buradan geri açabilirsiniz. Tarife ve personel yetkilerine göre erişim değişmez.",
                "Foydalanmaydigan bo'limlarni o'chiring — ular menyudan yo'qoladi. Shu yerdan qayta yoqish mumkin. Tarif va xodimlar huquqlari bo'yicha kirish o'zgarmaydi."),
            FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(hint);
        var wrap = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var (key, title) in SectionVisibility.Catalog)
        {
            // 2026-10-19: длинная подпись («Продажи и финансы — часть «Продажи»», ky/uz) наезжала на соседний переключатель —
            // строка в своей ширине 250, подпись переносится (было StackPanel: текст выходил за 250).
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Width = 250, Margin = new Avalonia.Thickness(0, 0, 12, 6) };
            var check = new CheckBox { Classes = { "ToggleSwitch" }, IsChecked = !SectionVisibility.IsHidden(key), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            check.IsCheckedChanged += (_, _) => SectionVisibility.Set(key, check.IsChecked != true);
            row.Children.Add(check);
            var label = new TextBlock
            {
                Text = title, FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(label, 1);
            Avalonia.Automation.AutomationProperties.SetName(check, title);
            label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("BrushText"));
            row.Children.Add(label);
            wrap.Children.Add(row);
        }
        stack.Children.Add(wrap);
        card.Child = stack;
        CardList.Children.Add(card);
    }

    /// <summary>2026-10-18: «Вид меню» программы владельца (новое / бургер ☰ / старое) — те же карточки, что в окне кнопки «Вид»
    /// внизу меню (OwnerMenuStylePicker); нажатие меняет меню сразу.</summary>
    /// 2026-10-19: та же карточка — в кассе для меню ☰ (catalog = KassaMenuStyles.Catalog, «Старое» / «Новое»).
    private void BuildOwnerMenuStyleCard(MenuStyleCatalog? catalog = null)
    {
        var kassa = catalog is not null;
        var card = new Border { Classes = { "SettingsCard", NurMarketKassa.AvaloniaHost.Views.PosSettingsWindow.ImmediateSettingClass }, Padding = new Avalonia.Thickness(14) }; // 2026-11-11 (Д-5): меняется сразу
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = Tr.T("Вид меню", "Менюнун көрүнүшү", "Menu layout", "Menü görünümü", "Menyu ko'rinishi"),
            Classes = { "SettingsCardTitle" }, FontSize = 14,
        });
        var hint = new TextBlock
        {
            Text = kassa
                ? Tr.T("Как выглядит меню ☰ кассы. Меняется сразу; пункты и права — те же.",
                    "Кассанын ☰ менюсу кандай көрүнөт. Дароо өзгөрөт; пункттар жана укуктар ошол эле.",
                    "How the till's ☰ menu looks. Changes right away; items and rights stay the same.",
                    "Kasanın ☰ menüsünün görünümü. Hemen değişir; öğeler ve yetkiler aynı kalır.",
                    "Kassaning ☰ menyusi qanday ko'rinadi. Darhol o'zgaradi; bandlar va huquqlar o'sha-o'sha.")
                : Tr.T("Как показывать разделы слева. Меняется сразу, открытый раздел остаётся.",
                "Бөлүмдөрдү сол жакта кантип көрсөтүү. Дароо өзгөрөт, ачык бөлүм калат.",
                "How the sections are shown on the left. Changes right away; the open section stays.",
                "Bölümlerin solda nasıl gösterileceği. Hemen değişir, açık bölüm kalır.",
                "Bo'limlarni chapda qanday ko'rsatish. Darhol o'zgaradi, ochiq bo'lim qoladi."),
            FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(hint);
        stack.Children.Add(new OwnerMenuStylePicker { Margin = new Avalonia.Thickness(0, 8, 0, 0), Catalog = catalog ?? OwnerMenuStyles.Catalog });
        card.Child = stack;
        CardList.Children.Add(card);
    }

    public ScreenSettingsView()
    {
        InitializeComponent();
        // 2026-11-11 (Д-5, живой показ 1.17.85): переключатели и ползунки, которые применяются и сохраняются сразу (без «Сохранить»), — не повод
        // спрашивать при закрытии «Есть несохранённые изменения» (PosSettingsWindow.SnapshotEditable их пропускает).
        foreach (var immediate in new Control[] { QuickProductsCheck, UpsellCheck, PayDockBottomCheck, UiScaleSlider, TileSizeSlider, CompactAutoRadio, CompactOnRadio, CompactOffRadio })
            immediate.Classes.Add(NurMarketKassa.AvaloniaHost.Views.PosSettingsWindow.ImmediateSettingClass);
        // 2026-11-05 (В4-10): пункт «Авто» списка касс следует за языком программы.
        AttachedToVisualTree += (_, _) =>
        {
            Tr.LanguageChanged -= OnLanguageChangedCashboxes;
            Tr.LanguageChanged += OnLanguageChangedCashboxes;
        };
        DetachedFromVisualTree += (_, _) => Tr.LanguageChanged -= OnLanguageChangedCashboxes;
        // 2026-10-05, снимок владельца с телефона: на Android видны пункты только для Windows — «Полноэкранный
        // режим», «Настоящий полноэкранный режим (панель задач Windows)», «Запускать вместе с Windows».
        // На Android касса и так во весь экран, а автозапуска у неё нет — строки скрыты.
        if (OperatingSystem.IsAndroid())
        {
            foreach (var check in new Control[] { FullscreenCheck, TrueFullscreenCheck, AutostartCheck })
            {
                if (check.Parent is Control row)
                    row.IsVisible = false;
            }
        }
        // 2026-10-18, владелец: «вид меню на выбор» — в программе владельца карточки трёх видов меню перед «Разделами меню».
        if (AppMode.IsOwner)
            BuildOwnerMenuStyleCard();
        else
            BuildOwnerMenuStyleCard(KassaMenuStyles.Catalog); // 2026-10-19: вид меню ☰ кассы (старое / новое)
        BuildSectionsCard();
        BuildGraphicsRows(); // 2026-11-11: «Размытие фона под окнами» и «Режим отрисовки» в группе «Производительность»
        // 2026-10-22 (аудит 1.17.59, П-Г): карточка «Оплата долга» и «Предоплата при продаже в долг» переехали во вкладку «Операции» (OperationsSettingsView).
        CashboxCombo.ItemsSource =new[] { new CashboxOption(null, AutoCashboxText) };
        CashboxCombo.SelectedIndex = 0;

        _suppressUiScaleChange = true;
        UiScaleSlider.Value = UserPreferences.Instance.UiScalePercent;
        UpdateUiScaleValueText(UserPreferences.Instance.UiScalePercent);
        TileSizeSlider.Value = UserPreferences.Instance.CatalogTileScalePercent;
        TileSizeValueText.Text = $"{UserPreferences.Instance.CatalogTileScalePercent:F0}%";
        // 2026-10-04: компактный вид для маленьких экранов — текущий выбор (по умолчанию «Авто»).
        var compactMode = UserPreferences.Instance.CompactLayoutMode;
        CompactOnRadio.IsChecked = compactMode == "on";
        CompactOffRadio.IsChecked = compactMode == "off";
        CompactAutoRadio.IsChecked = compactMode is not ("on" or "off");
        _suppressUiScaleChange = false;
        QuickProductsCheck.IsChecked = UserPreferences.Instance.ShowQuickProducts;
        // 2026-10-27, аудит владельца Б-1: «Быстрые товары в пустом чеке» — личная привычка кассира, она хранится у каждого пользователя в
        // его программе и в программу владельца не доходит. В программе владельца чека нет — переключатель только вводил в заблуждение, скрыт.
        if (AppMode.IsOwner && QuickProductsCheck.Parent is Control quickRow)
            quickRow.IsVisible = false;
        UpsellCheck.IsChecked = UserPreferences.Instance.UpsellEnabled;
        // 2026-10-27: «Окно оплаты прижимать к низу» — свой файл payment_dialog_position.json; на Android и в программе владельца окна оплаты-панели нет.
        PayDockText.Text = Tr.T("Окно оплаты прижимать к низу", "Төлөм терезесин ылдыйга жакындатуу", "Snap the payment window to the bottom",
            "Ödeme penceresini alta yapıştır", "To'lov oynasini pastga yopishtirish");
        PayDockDesc.Text = Tr.T("Выключено — окно оплаты стоит по центру кассы, слева от чека.",
            "Өчүк — төлөм терезеси кассанын ортосунда, чектин сол жагында турат.",
            "Off — the payment window is centered over the register, left of the receipt.",
            "Kapalı — ödeme penceresi kasanın ortasında, fişin solunda durur.",
            "O'chiq — to'lov oynasi kassaning o'rtasida, chekning chap tomonida turadi.");
        if (OperatingSystem.IsAndroid() || AppMode.IsOwner)
            PayDockRow.IsVisible = false;
        else
        {
            _suppressPayDock = true;
            PayDockBottomCheck.IsChecked = PaymentDockPlacement.ReadBottomMode();
            _suppressPayDock = false;
            // 2026-10-30: «модалка не двигается!!!» — окно оплаты двигается за шапку и помнит место; кнопка возвращает обычное.
            PayDockResetButton.Content = Tr.T("Сбросить положение окна оплаты", "Төлөм терезесинин ордун кайтаруу", "Reset payment window position",
                "Ödeme penceresi konumunu sıfırla", "To'lov oynasi joylashuvini tiklash");
            PayDockResetButton.IsEnabled = PaymentDockPlacement.ReadOffset() != (0, 0);
        }

        // Выбор вида кассы (карточки с миниатюрами) — общий элемент KassaLayoutPicker
        // (2026-09-28, он же в Маркетплейсе → «Виды кассы»); язык и тему он отслеживает сам.
        AttachedToVisualTree += (_, _) =>
        {
            // Окно настроек подгоняется под экран уже после конструктора — подпись с реальным
            // масштабом обновляем, когда страница оказалась на экране.
            UpdateUiScaleValueText(UiScaleSlider.Value);
        };
    }

    // ---- 2026-11-11 (решение владельца): размытие фона под окнами и режим отрисовки Windows ----

    private CheckBox? _blurCheck;
    private TextBlock? _blurDesc;
    private TextBlock? _renderNote;
    private Button? _renderResetButton;
    private RadioButton? _renderAutoRadio;
    private bool _suppressGfx;

    /// <summary>Переключатель «Размытие фона под окнами» (для тестов и окна настроек).</summary>
    public CheckBox? BlurCheck => _blurCheck;

    /// <summary>Пункт «Режим отрисовки» показан (только Windows).</summary>
    public Control? RenderModeBlock { get; private set; }

    private static TextBlock GfxText(string name, string text, double size, string brush, bool wrap = true)
    {
        var tb = new TextBlock
        {
            Name = name, Text = text, FontSize = size, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            TextWrapping = wrap ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap,
        };
        tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(brush));
        return tb;
    }

    /// <summary>Строки в группе «Производительность» сразу после «Режима слабого устройства»: размытие (сразу, без «Сохранить») и режим
    /// отрисовки Windows (сохраняется сразу, действует после перезапуска).</summary>
    private void BuildGraphicsRows()
    {
        if (LowPerformanceModeCheck.Parent is not Control lowRow || lowRow.Parent is not Panel perfStack)
            return;
        var index = perfStack.Children.IndexOf(lowRow) + 1;
        var prefs = UserPreferences.Instance;

        // Размытие фона под окнами.
        var blurRow = new Grid { Name = "GfxBlurRow", ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        // 2026-11-11 (Д-5): применяется сразу — окно настроек не спрашивает про «несохранённые изменения» после этого переключателя.
        blurRow.Classes.Add(NurMarketKassa.AvaloniaHost.Views.PosSettingsWindow.ImmediateSettingClass);
        var blurText = new StackPanel();
        var blurTitle = GfxText("GfxBlurTitle", Tr.T("Размытие фона под окнами (для мощных ПК)", "Терезелердин астындагы фонду бүдөмүктөтүү (күчтүү ПК үчүн)",
            "Blur the background under windows (for powerful PCs)", "Pencerelerin altındaki arka planı bulanıklaştır (güçlü PC'ler için)",
            "Oynalar ostidagi fonni xiralashtirish (kuchli kompyuterlar uchun)"), 13, "BrushText");
        _blurDesc = GfxText("GfxBlurDesc", "", 12, "BrushTextSoft");
        _blurDesc.MaxWidth = 360;
        blurText.Children.Add(blurTitle);
        blurText.Children.Add(_blurDesc);
        blurRow.Children.Add(blurText);
        _blurCheck = new CheckBox { Name = "GfxBlurCheck", Classes = { "ToggleSwitch" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        Avalonia.Automation.AutomationProperties.SetName(_blurCheck, blurTitle.Text ?? "");
        Grid.SetColumn(_blurCheck, 1);
        blurRow.Children.Add(_blurCheck);
        _blurCheck.IsCheckedChanged += (_, _) =>
        {
            if (!_suppressGfx && _blurCheck.IsEnabled)
                UserPreferences.SetModalBackgroundBlur(_blurCheck.IsChecked == true);
        };
        perfStack.Children.Insert(index++, blurRow);

        // Режим отрисовки — только Windows.
        if (RenderModeSettings.IsAvailableHere)
        {
            var block = new StackPanel { Name = "GfxRenderBlock", Spacing = 4 };
            block.Classes.Add(NurMarketKassa.AvaloniaHost.Views.PosSettingsWindow.ImmediateSettingClass); // 2026-11-11 (Д-5): сохраняется сразу
            block.Children.Add(GfxText("GfxRenderTitle", Tr.T("Режим отрисовки", "Сүрөт тартуу режими", "Rendering mode", "Çizim modu", "Chizish rejimi"), 13, "BrushText"));
            var desc = GfxText("GfxRenderDesc", Tr.T(
                "Меняйте, только если после сворачивания окно показывается наполовину или кнопки кривые. «Совместимый» — без DirectComposition, «Программный» — без видеокарты (медленнее).",
                "Терезени кичирейтип кайра ачканда ал жарымы гана көрүнсө же баскычтар кыйшык болсо гана өзгөртүңүз. «Шайкеш» — DirectComposition'сыз, «Программалык» — видеокартасыз (жайыраак).",
                "Change it only if, after minimizing, the window shows only half or the buttons look broken. “Compatible” works without DirectComposition, “Software” without the graphics card (slower).",
                "Yalnızca simge durumuna küçülttükten sonra pencere yarım görünüyorsa veya düğmeler bozuksa değiştirin. “Uyumlu” DirectComposition olmadan, “Yazılımsal” ekran kartı olmadan çalışır (daha yavaş).",
                "Faqat oyna yig'ib qo'yilgandan keyin yarmi ko'rinsa yoki tugmalar qiyshiq bo'lsa o'zgartiring. «Mos» — DirectComposition'siz, «Dasturiy» — videokartasiz (sekinroq)."), 12, "BrushTextSoft");
            desc.MaxWidth = 480;
            block.Children.Add(desc);

            var current = RenderModeSettings.Normalize(prefs.WindowsRenderMode);
            var wrap = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Margin = new Avalonia.Thickness(0, 4, 0, 0) };
            RadioButton Radio(string name, string mode, string text)
            {
                var r = new RadioButton
                {
                    Name = name, GroupName = "GfxRenderMode", Classes = { "ClickPill" }, Content = text, Tag = mode,
                    Margin = new Avalonia.Thickness(0, 0, 8, 8), IsChecked = current == mode,
                };
                r.IsCheckedChanged += (_, _) =>
                {
                    if (_suppressGfx || r.IsChecked != true)
                        return;
                    SetRenderMode(mode);
                };
                wrap.Children.Add(r);
                return r;
            }
            _renderAutoRadio = Radio("GfxRenderAuto", RenderModeSettings.Auto,
                Tr.T("Авто (как сейчас)", "Авто (азыркыдай)", "Auto (as now)", "Otomatik (şimdiki gibi)", "Avto (hozirgidek)"));
            Radio("GfxRenderCompat", RenderModeSettings.Compatible, RenderModeSettings.Title(RenderModeSettings.Compatible));
            Radio("GfxRenderSoftware", RenderModeSettings.Software,
                Tr.T("Программный (без видеокарты)", "Программалык (видеокартасыз)", "Software (no graphics card)", "Yazılımsal (ekran kartı olmadan)", "Dasturiy (videokartasiz)"));
            block.Children.Add(wrap);

            _renderNote = GfxText("GfxRenderNote", "", 12, "BrushTextSoft");
            _renderNote.MaxWidth = 480;
            block.Children.Add(_renderNote);
            _renderResetButton = new Button
            {
                Name = "GfxRenderReset", Classes = { "SecondaryButton" }, Padding = new Avalonia.Thickness(10, 4), FontSize = 12,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Content = Tr.T("Вернуть как было", "Мурункудай кайтаруу", "Restore the default", "Eskisine döndür", "Avvalgidek qaytarish"),
            };
            _renderResetButton.Click += (_, _) =>
            {
                if (_renderAutoRadio is not null)
                    _renderAutoRadio.IsChecked = true; // → SetRenderMode(Auto)
            };
            block.Children.Add(_renderResetButton);
            RenderModeBlock = block;
            perfStack.Children.Insert(index, block);
        }

        LowPerformanceModeCheck.IsCheckedChanged += (_, _) => UpdateGraphicsRows();
        UpdateGraphicsRows();
    }

    private void SetRenderMode(string mode)
    {
        var prefs = UserPreferences.Instance;
        mode = RenderModeSettings.Normalize(mode);
        if (RenderModeSettings.Normalize(prefs.WindowsRenderMode) != mode)
        {
            prefs.WindowsRenderMode = mode;
            prefs.SaveToDisk();
            PosLogger.Log($"Режим отрисовки выбран: {mode} (применится после перезапуска).", "SETTINGS");
        }
        UpdateGraphicsRows();
    }

    /// <summary>«Режим слабого устройства» выключает размытие (переключатель неактивен с пояснением); подпись режима отрисовки — что будет после перезапуска.</summary>
    private void UpdateGraphicsRows()
    {
        var prefs = UserPreferences.Instance;
        var low = LowPerformanceModeCheck.IsChecked == true;
        if (_blurCheck is not null && _blurDesc is not null)
        {
            _suppressGfx = true;
            _blurCheck.IsEnabled = !low;
            _blurCheck.IsChecked = !low && prefs.ModalBackgroundBlur;
            _suppressGfx = false;
            _blurDesc.Text = low
                ? Tr.T("В «режиме слабого устройства» размытие выключено.", "«Алсыз түзмөк режиминде» бүдөмүктөтүү өчүк.",
                    "Blur is off in “Low-power device mode”.", "“Zayıf cihaz modu”nda bulanıklaştırma kapalı.", "«Zaif qurilma rejimi»da xiralashtirish o'chiq.")
                : Tr.T("Красиво, но на слабой видеокарте окна открываются медленнее. Включается сразу.",
                    "Кооз, бирок алсыз видеокартада терезелер жайыраак ачылат. Дароо күйөт.",
                    "Looks nice, but windows open slower on a weak graphics card. Takes effect right away.",
                    "Güzel görünür ama zayıf ekran kartında pencereler daha yavaş açılır. Hemen etkili olur.",
                    "Chiroyli, lekin zaif videokartada oynalar sekinroq ochiladi. Darhol yoqiladi.");
        }
        if (_renderNote is not null)
        {
            var chosen = RenderModeSettings.Normalize(prefs.WindowsRenderMode);
            var text = Tr.T("Применится после перезапуска программы.", "Программа кайра ачылгандан кийин күчүнө кирет.", "Takes effect after the program restarts.",
                "Program yeniden başlatıldıktan sonra geçerli olur.", "Dastur qayta ishga tushgandan keyin kuchga kiradi.");
            if (low)
                text += " " + Tr.T("Сейчас включён «режим слабого устройства» — отрисовка программная, пока он включён.",
                    "Азыр «алсыз түзмөк режими» күйүк — ал күйүк турганда сүрөт программалык түрдө тартылат.",
                    "“Low-power device mode” is on — rendering stays software while it is on.",
                    "“Zayıf cihaz modu” açık — açık kaldıkça çizim yazılımsal olur.",
                    "Hozir «zaif qurilma rejimi» yoqilgan — u yoqilgan ekan, chizish dasturiy bo'ladi.");
            _renderNote.Text = text;
            if (_renderResetButton is not null)
                _renderResetButton.IsEnabled = chosen != RenderModeSettings.Auto;
        }
    }

    private void QuickProductsCheck_Changed(object? sender, RoutedEventArgs e) =>
        UserPreferences.SetShowQuickProducts(QuickProductsCheck.IsChecked == true);

    /// <summary>2026-10-01: подсказки допродажи — сразу, без «Сохранить».</summary>
    private void UpsellCheck_Changed(object? sender, RoutedEventArgs e) =>
        UserPreferences.SetUpsellEnabled(UpsellCheck.IsChecked == true);

    private bool _suppressPayDock;

    /// <summary>2026-10-27: положение окна оплаты — сразу, без «Сохранить»; действует с ближайшего открытия окна оплаты.</summary>
    private void PayDockBottomCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressPayDock)
            return;
        PaymentDockPlacement.WriteBottomMode(PayDockBottomCheck.IsChecked == true);
        PayDockResetButton.IsEnabled = false; // 2026-10-30: смена режима стирает и смещение от перетаскивания
    }

    private void PayDockResetButton_Click(object? sender, RoutedEventArgs e)
    {
        PaymentDockPlacement.ResetOffset();
        PayDockResetButton.IsEnabled = false;
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);

    public double UiScalePercent => UiScaleSlider.Value;

    /// <summary>Ползунок — это выбор кассира, а на экране может стоять меньше: масштаб никогда
    /// не выходит за то, что помещается на экран (см. UiScaleHelper.ComputeScale). Раньше здесь
    /// показывалось только значение ползунка — «100%» при реальных 78% на 1024×768, и было не
    /// понять, почему касса вдруг выросла или почему ползунок «не работает».</summary>
    private void UpdateUiScaleValueText(double percent)
    {
        var text = $"{percent:F0}%";
        var actual = TryGetAppliedUiScalePercent();
        if (actual is { } applied && Math.Abs(applied - percent) >= 0.5)
        {
            text = Tr.T(
                $"{percent:F0}% (на этом экране {applied:F0}%)",
                $"{percent:F0}% (бул экранда {applied:F0}%)",
                $"{percent:F0}% ({applied:F0}% on this screen)",
                $"{percent:F0}% (bu ekranda {applied:F0}%)",
                $"{percent:F0}% (bu ekranda {applied:F0}%)");
        }

        UiScaleValueText.Text = text;
    }

    /// <summary>Реально применённый масштаб: у кассы, если она открыта, иначе (программа
    /// владельца) — у самого окна настроек.</summary>
    private double? TryGetAppliedUiScalePercent()
    {
        try
        {
            // 2026-11-01: настройки встроены в программу владельца — масштаб стоит на её окне (Owner), а не на самих настройках.
            return UiScaleHelper.GetAppliedPercent(App.GetRequiredService<MainWindowHostBridge>().Window)
                   ?? UiScaleHelper.GetAppliedPercent((TopLevel.GetTopLevel(this) as Window)?.Owner as Control)
                   ?? UiScaleHelper.GetAppliedPercent(TopLevel.GetTopLevel(this) as Control);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private CancellationTokenSource? _uiScaleSaveDebounceCts;

    private void UiScaleSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressUiScaleChange)
        {
            UpdateUiScaleValueText(e.NewValue);
            return;
        }

        // Живое применение сразу при перетаскивании ползунка (та же схема, что уже
        // используется для GlassOpacitySlider/LiquidGlassToggle в MarketplaceView) —
        // не нужно ждать "Сохранить", чтобы увидеть эффект. Само масштабирование —
        // дешёвая операция (просто ScaleTransform), но запись на диск на каждый
        // промежуточный тик перетаскивания ползунка (десятки раз в секунду) грузила
        // диск/UI-поток на слабых устройствах — SaveToDisk откладываем до остановки.
        UserPreferences.Instance.UiScalePercent = e.NewValue;
        App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshUiScale();
        UiScaleHelper.NotifyPreferenceChanged(); // 2026-11-01: и программа владельца (у неё нет MainWindow)
        UiScaleChanged?.Invoke(this, EventArgs.Empty);
        // После применения — чтобы подпись показала, что реально получилось на этом экране.
        UpdateUiScaleValueText(e.NewValue);
        ScheduleUiScaleSaveDebounce();
    }

    /// <summary>2026-10-04: компактный вид «Классики» (Авто / Всегда / Выключен) — сразу, без
    /// «Сохранить», как масштаб: касса перестраивается и пересчитывает масштаб (MainWindow.RefreshUiScale).
    /// Пишется только выбор владельца; у кого его нет, остаётся «Авто».</summary>
    private void CompactLayoutRadio_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressUiScaleChange || sender is not RadioButton { IsChecked: true } radio)
            return;

        var mode = ReferenceEquals(radio, CompactOnRadio) ? "on"
            : ReferenceEquals(radio, CompactOffRadio) ? "off"
            : "auto";
        if (UserPreferences.Instance.CompactLayoutMode == mode)
            return;

        UserPreferences.Instance.CompactLayoutMode = mode;
        App.GetRequiredService<MainWindowHostBridge>().Window?.RefreshUiScale();
        UiScaleHelper.NotifyPreferenceChanged(); // 2026-11-01: и программа владельца
        UiScaleChanged?.Invoke(this, EventArgs.Empty);
        UpdateUiScaleValueText(UiScaleSlider.Value);
        ScheduleUiScaleSaveDebounce();
    }

    /// <summary>Размер карточек применяется сразу, пока тянут ползунок, — как и масштаб.</summary>
    private void TileSizeSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        TileSizeValueText.Text = $"{e.NewValue:F0}%";
        if (_suppressUiScaleChange)
            return;

        UserPreferences.Instance.CatalogTileScalePercent = e.NewValue;
        AccentThemeService.ApplyCatalogTileSize();
        ScheduleUiScaleSaveDebounce();
    }

    private void ScheduleUiScaleSaveDebounce()
    {
        var cts = new CancellationTokenSource();
        _uiScaleSaveDebounceCts?.Cancel();
        _uiScaleSaveDebounceCts = cts;
        _ = RunDebouncedUiScaleSaveAsync(cts);
    }

    private async Task RunDebouncedUiScaleSaveAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!cts.IsCancellationRequested)
            UserPreferences.Instance.SaveToDisk();
    }

    /// <summary>Выбранная касса: null означает "Авто".</summary>
    public CashboxOption SelectedCashbox =>
        CashboxCombo.SelectedItem as CashboxOption ?? new CashboxOption(null, AutoCashboxText);

    /// <summary>Ставит текущее сохранённое значение как выбранное (после первой загрузки списка
    /// список содержит только "Авто" — выбор синхронизируется повторно после RefreshCashboxes_Click).</summary>
    public void PreselectCashbox(string? id, string? name)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            CashboxCombo.SelectedIndex = 0;
            return;
        }

        if (CashboxCombo.ItemsSource is IEnumerable<CashboxOption> options)
        {
            var match = options.FirstOrDefault(o => o.Id == id);
            if (match is not null)
            {
                CashboxCombo.SelectedItem = match;
                return;
            }
        }

        // Список ещё не загружен с сервера — показываем сохранённое имя как временный пункт,
        // чтобы не выглядело как "сброшено на Авто" пока кассир не нажмёт "Обновить".
        var placeholder = new CashboxOption(id, name ?? id);
        CashboxCombo.ItemsSource = new[] { new CashboxOption(null, AutoCashboxText), placeholder };
        CashboxCombo.SelectedItem = placeholder;
    }

    private async void RefreshCashboxes_Click(object? sender, RoutedEventArgs e)
    {
        RefreshCashboxesButton.IsEnabled = false;
        try
        {
            var previousId = SelectedCashbox.Id;
            var raw = await App.ShiftApi.ConstructionCashboxesListAsync().ConfigureAwait(true);
            var boxes = CartDisplayHelper.ListCashboxes(raw);

            var options = new List<CashboxOption> { new(null, AutoCashboxText) };
            options.AddRange(boxes.Select(b =>
                new CashboxOption(b.Id, b.IsActive ? b.DisplayName : $"{b.DisplayName} " + Tr.T("(неактивна)", "(активдүү эмес)", "(inactive)", "(etkin değil)", "(faol emas)"))));

            CashboxCombo.ItemsSource = options;
            CashboxCombo.SelectedItem = options.FirstOrDefault(o => o.Id == previousId) ?? options[0];
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Cashbox list refresh failed: {ex.Message}", "SETTINGS");
        }
        finally
        {
            RefreshCashboxesButton.IsEnabled = true;
        }
    }
}
