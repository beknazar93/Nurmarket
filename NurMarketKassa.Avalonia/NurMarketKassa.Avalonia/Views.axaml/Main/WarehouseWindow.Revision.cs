using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.ViewModels;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Models.Pos;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-24, ТЗ «Склад — ревизия, приёмка, поставщик, Telegram-группа», пакет Р2: вкладка «Ревизия» по схеме «скан → факт».
/// Весь склад списком с учётным остатком; пикнул штрихкод — строка найдена, прокручена и выделена, курсор в поле «Факт» КАРТОЧКИ над списком
/// (а не в ячейке таблицы — пять ошибок старой ревизии, ТЗ 1.2, пропали вместе с правкой ячеек); число и Enter — факт записан, курсор снова в
/// поле скана. Остатки и акты ведёт движок <see cref="RevisionSession"/> (пакет Р1) — здесь нет ни своей логики остатков, ни денег:
/// окно только показывает и передаёт ввод. «Завершить ревизию…» — окно итога с предпросмотром и «Применить» (RevisionFinishDialog),
/// «Выгрузить» — пакет Р3 (RevisionExportUi), «Прошлые ревизии» — RevisionActStore.
/// Вкладка собрана кодом (как «Массовое добавление»), без Windows-only API: файл собирается и для Linux/Android.
/// Скан: вне полей ввода его читает общий сервис (OnBarcodeScanned → RevisionOnScanned), в поле «Факт» — перехват быстрого ввода
/// (Window_KeyDown → HandleScanInsideField, пороги не менялись), поле «Скан / поиск» ловит скан само по Enter.</summary>
public partial class WarehouseWindow
{
    private RevisionSession? _rev;
    private Control _revStartView = null!;
    private Control _revWorkView = null!;

    // стартовый экран
    private TextBlock _revStartStatus = null!;
    private TextBlock _revLockText = null!;
    private Border _revDraftBox = null!;
    private TextBlock _revDraftText = null!;
    private Button _revStartButton = null!;
    private Button _revResumeButton = null!;
    private Button _revRestartButton = null!;
    private Button _revPastButton = null!;
    private bool _revStarting;

    // рабочий экран
    private TextBox _revSearchBox = null!;
    private ComboBox _revCategoryBox = null!;
    private CheckBox _revPlusOne = null!;
    private TextBlock _revStockLabel = null!;
    private readonly RadioButton[] _revPills = new RadioButton[4];
    private TextBlock _revCardName = null!;
    private TextBlock _revCardMeta = null!;
    private TextBlock _revCardNote = null!;
    private TextBlock _revCardBook = null!;
    private TextBlock _revCardDiff = null!;
    private TextBox _revFactBox = null!;
    private Button _revEqualButton = null!;
    private Button _revResetButton = null!;
    private ListBox _revList = null!;
    private TextBlock _revEmptyList = null!;
    private TextBlock _revTotalsText = null!;
    private WrapPanel _revMoneyPanel = null!;
    private ContentControl _revHeaderHost = null!;
    private TextBlock _revCardCost = null!;
    private TextBlock _revCardMoney = null!;
    private Control _revCardCostBox = null!;
    private Control _revCardMoneyBox = null!;
    private TextBlock _revMessage = null!;
    private Button _revFinishButton = null!;
    private Button _revDiscardButton = null!;
    private Button _revExportButton = null!;
    private ObservableCollection<RevisionRow> _revView = new();
    private RevisionRow? _revCurrent;
    private string _revFactInitial = "";
    private RevisionFilter _revFilter = RevisionFilter.All;
    private string? _revCategory;
    private string _revViewKey = "";
    private bool _revUiSetup;
    private bool _revSuppressSel;
    private DispatcherTimer? _revTotalsTimer;
    private DispatcherTimer? _revFilterTimer;
    private readonly Queue<string> _revScanQueue = new();
    private bool _revScanRunning;
    private Action? _revChangedHandler;

    /// <summary>Только для стенда проверки: вместо настоящего обновления каталога с сервера (его в стенде нет).</summary>
    internal static Func<CancellationToken, Task<CatalogSyncResult>>? RevisionSyncOverride { get; set; }

    private static string RevT(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>Идёт ревизия, которую нельзя терять при уходе из раздела владельца (подсчёт и открытое окно).</summary>
    private bool RevisionKeepAlive => _rev is { IsCompleted: false, CountedCount: > 0 } || _rev is { IsApplying: true };

    /// <summary>Закупочные цены в выгрузке — только в программе владельца (вопрос В-7 ТЗ: кассиру закупку не показываем).
    /// 2026-10-29 (владелец: «где приходные цены?»): то же видит владелец/администратор магазина и автономный режим в обычной кассе —
    /// как доступ к ключам ИИ в настройках; кассир (роль не owner/admin) закупку по-прежнему не видит. Права «видеть закупку» в проекте нет.</summary>
    internal static bool RevisionShowPurchase => AppMode.IsOwner || ReceivingMarkupDialog.CanEditSettings();

    // ------------------------------------------------------------------ сборка вкладки

    private void InitRevision()
    {
        _revStartView = BuildRevisionStart();
        _revWorkView = BuildRevisionWork();
        var host = new Grid();
        host.Children.Add(_revStartView);
        host.Children.Add(_revWorkView);
        RevisionPanel.Child = host;
        _revWorkView.IsVisible = false;

        _revTotalsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _revTotalsTimer.Tick += (_, _) =>
        {
            _revTotalsTimer.Stop();
            RevisionRefreshTotals();
        };
        _revFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _revFilterTimer.Tick += (_, _) =>
        {
            _revFilterTimer.Stop();
            RevisionRebuildView(false);
        };
        WarehouseTabs.SelectionChanged += (_, e) =>
        {
            // SelectionChanged всплывает от списка и выпадающих списков внутри вкладки — реагируем только на смену самой вкладки
            // (иначе выбор строки в списке ревизии каждый раз возвращал бы курсор в поле скана).
            if (ReferenceEquals(e.Source, WarehouseTabs) && ReferenceEquals(WarehouseTabs.SelectedItem, RevisionTabItem))
                RevisionOnTabOpened();
        };
    }

    private static TextBlock RevText(string text, double size, bool soft = false, bool bold = false, bool center = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        if (bold)
            t.FontWeight = FontWeight.Bold;
        if (center)
            t.TextAlignment = TextAlignment.Center;
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable(soft ? "BrushTextSoft" : "BrushText"));
        return t;
    }

    private Control BuildRevisionStart()
    {
        var title = RevText(RevT("Ревизия склада", "Кампаны ревизиялоо", "Stocktake", "Stok sayımı", "Ombor inventarizatsiyasi"), 22, bold: true, center: true);
        var hint = RevText(RevT(
            "Весь склад списком с учётным остатком. Пикните товар — впишите, сколько его на самом деле, и нажмите Enter. Окно можно закрыть в любой момент: подсчёт сохраняется.",
            "Бүт кампа эсептик калдык менен тизме түрүндө. Товарды сканерлеңиз — иш жүзүндө канча экенин жазып, Enter басыңыз. Терезени каалаган убакта жапсаңыз болот: саноо сакталат.",
            "The whole warehouse as a list with the book quantity. Scan a product, type how many there really are and press Enter. You can close the window at any time: the count is saved.",
            "Tüm depo, sistem miktarıyla bir liste halinde. Ürünü okutun, gerçekte kaç tane olduğunu yazıp Enter'a basın. Pencereyi istediğiniz zaman kapatabilirsiniz: sayım kaydedilir.",
            "Butun ombor hisob qoldig'i bilan ro'yxat holida. Mahsulotni skanerlang, aslida qanchaligini yozing va Enter bosing. Oynani istalgan vaqt yopishingiz mumkin: hisob saqlanadi."),
            13.5, soft: true, center: true);

        _revDraftText = RevText("", 14, bold: true, center: true);
        _revDraftText.VerticalAlignment = VerticalAlignment.Center;
        _revResumeButton = BulkButton(RevT("Продолжить", "Улантуу", "Continue", "Devam et", "Davom etish"), primary: true);
        _revResumeButton.Click += async (_, _) => await RevisionBeginAsync(resume: true).ConfigureAwait(true);
        _revRestartButton = BulkButton(RevT("Начать заново", "Кайра баштоо", "Start over", "Yeniden başla", "Qaytadan boshlash"), primary: false);
        _revRestartButton.Click += async (_, _) => await RevisionRestartAsync().ConfigureAwait(true);
        var draftButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        draftButtons.Children.Add(_revResumeButton);
        draftButtons.Children.Add(_revRestartButton);
        var draftStack = new StackPanel { Spacing = 10 };
        draftStack.Children.Add(_revDraftText);
        draftStack.Children.Add(draftButtons);
        _revDraftBox = SoftBox(draftStack);
        _revDraftBox.IsVisible = false;

        _revStartButton = BulkButton(RevT("Начать ревизию", "Ревизияны баштоо", "Start stocktake", "Sayımı başlat", "Inventarizatsiyani boshlash"), primary: true, minWidth: 240);
        _revStartButton.HorizontalAlignment = HorizontalAlignment.Center;
        _revStartButton.Click += async (_, _) => await RevisionBeginAsync(resume: false).ConfigureAwait(true);

        _revLockText = RevText("", 13, bold: true, center: true);
        _revLockText.Bind(TextBlock.ForegroundProperty, _revLockText.GetResourceObservable("BrushDanger"));
        _revLockText.IsVisible = false;
        _revStartStatus = RevText("", 13, soft: true, center: true);

        _revPastButton = BulkButton(RevT("Прошлые ревизии ▾", "Мурунку ревизиялар ▾", "Past stocktakes ▾", "Geçmiş sayımlar ▾", "Oldingi inventarizatsiyalar ▾"), primary: false);
        _revPastButton.HorizontalAlignment = HorizontalAlignment.Center;
        _revPastButton.Click += async (_, _) => await RevisionShowPastAsync().ConfigureAwait(true);

        var stack = new StackPanel { Spacing = 14, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 16) };
        stack.Children.Add(title);
        stack.Children.Add(hint);
        stack.Children.Add(_revDraftBox);
        stack.Children.Add(_revStartButton);
        stack.Children.Add(_revLockText);
        stack.Children.Add(_revStartStatus);
        stack.Children.Add(_revPastButton);
        return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
    }

    private Control BuildRevisionWork()
    {
        // --- строка скана/поиска, категория, «каждый скан = +1»
        _revSearchBox = new TextBox
        {
            Watermark = RevT("Скан или поиск: штрихкод, артикул, название — Enter", "Скан же издөө: штрихкод, артикул, аталышы — Enter",
                "Scan or search: barcode, article, name — Enter", "Okut veya ara: barkod, kod, ad — Enter", "Skan yoki qidiruv: shtrix-kod, artikul, nomi — Enter"),
            MinHeight = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _revSearchBox.TextChanged += (_, _) =>
        {
            if (!_revUiSetup)
            {
                _revFilterTimer?.Stop();
                _revFilterTimer?.Start();
            }
        };
        _revSearchBox.KeyDown += RevisionSearchKeyDown;

        _revCategoryBox = new ComboBox { MinHeight = 40, MinWidth = 170, MaxWidth = 240, VerticalContentAlignment = VerticalAlignment.Center };
        _revCategoryBox.SelectionChanged += (_, _) =>
        {
            if (_revUiSetup)
                return;
            _revCategory = _revCategoryBox.SelectedIndex > 0 ? _revCategoryBox.SelectedItem as string : null;
            RevisionRebuildView(false);
        };

        _revPlusOne = new CheckBox
        {
            Content = RevT("Каждый скан = +1", "Ар бир скан = +1", "Each scan = +1", "Her okutma = +1", "Har bir skan = +1"),
            VerticalAlignment = VerticalAlignment.Center,
            IsChecked = false,
        };
        ToolTip.SetTip(_revPlusOne, RevT("Для штучного пересчёта: каждый скан прибавляет 1 (у упаковки — её количество), число вводить не нужно.",
            "Даана саноо үчүн: ар бир скан 1 кошот (таңгакта — анын саны), сан киргизүү керек эмес.",
            "For piece-by-piece counting: every scan adds 1 (a pack adds its quantity), no need to type a number.",
            "Tek tek sayım için: her okutma 1 ekler (koli için kendi adedi), sayı yazmaya gerek yok.",
            "Donalab sanash uchun: har bir skan 1 qo'shadi (qadoqda — uning soni), son kiritish shart emas."));

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        _revCategoryBox.Margin = new Thickness(8, 0, 0, 0);
        _revPlusOne.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_revCategoryBox, 1);
        Grid.SetColumn(_revPlusOne, 2);
        top.Children.Add(_revSearchBox);
        top.Children.Add(_revCategoryBox);
        top.Children.Add(_revPlusOne);

        // --- фильтры: Все / Посчитанные / Не посчитанные / С расхождением
        var pillsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        var segment = new Border { Classes = { "sec-segment" } };
        var segPanel = new StackPanel { Orientation = Orientation.Horizontal };
        segment.Child = segPanel;
        var filters = new[] { RevisionFilter.All, RevisionFilter.Counted, RevisionFilter.NotCounted, RevisionFilter.WithDifference };
        for (var i = 0; i < filters.Length; i++)
        {
            var f = filters[i];
            var rb = new RadioButton { GroupName = "RevFilter", IsChecked = f == RevisionFilter.All, Focusable = false };
            rb.Classes.Add("sec-seg");
            rb.IsCheckedChanged += (_, _) =>
            {
                if (rb.IsChecked == true && !_revUiSetup && _revFilter != f)
                {
                    _revFilter = f;
                    RevisionRebuildView(false);
                }
            };
            _revPills[i] = rb;
            segPanel.Children.Add(rb);
        }

        _revStockLabel = RevText("", 12.5, soft: true);
        _revStockLabel.VerticalAlignment = VerticalAlignment.Center;
        pillsRow.Children.Add(segment);
        pillsRow.Children.Add(_revStockLabel);

        // --- карточка текущего товара
        var card = BuildRevisionCard();

        // --- список
        // 2026-10-29: шапку пересобираем при открытии рабочего экрана (колонки «Закупка» и «Сумма разницы» зависят от роли пользователя).
        _revHeaderHost = new ContentControl { Content = RevisionRowView.BuildHeader(false) };
        var header = _revHeaderHost;
        _revList = new ListBox
        {
            SelectionMode = SelectionMode.Single,
            Background = Brushes.Transparent,
            ItemsSource = _revView,
            ItemTemplate = new FuncDataTemplate<RevisionRow>((row, _) => row is null ? new Control() : new RevisionRowView(row)),
        };
        _revList.Styles.Add(new Avalonia.Styling.Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Avalonia.Styling.Setter(PaddingProperty, new Thickness(0)),
                new Avalonia.Styling.Setter(MinHeightProperty, 0.0),
            },
        });
        _revList.SelectionChanged += (_, _) =>
        {
            // Стрелками по списку: строка становится текущей без перевода курсора (одно нажатие мышью обрабатывает PointerPressed ниже).
            if (_revSuppressSel || _rev is null)
                return;
            if (_revList.SelectedItem is RevisionRow r && !ReferenceEquals(r, _revCurrent))
                RevisionSetCurrent(r, focusFact: false);
        };
        _revList.AddHandler(PointerPressedEvent, RevisionListPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        _revList.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter && _revList.SelectedItem is RevisionRow r)
            {
                e.Handled = true;
                RevisionSetCurrent(r, focusFact: true);
            }
        };
        _revEmptyList = RevText(RevT("Ничего не найдено.", "Эчтеке табылган жок.", "Nothing found.", "Hiçbir şey bulunamadı.", "Hech narsa topilmadi."), 14, soft: true, center: true);
        _revEmptyList.HorizontalAlignment = HorizontalAlignment.Center;
        _revEmptyList.VerticalAlignment = VerticalAlignment.Center;
        _revEmptyList.IsVisible = false;
        _revEmptyList.IsHitTestVisible = false;
        var listArea = new Grid();
        listArea.Children.Add(_revList);
        listArea.Children.Add(_revEmptyList);

        // --- низ: сообщение, итоги, кнопки
        _revMessage = RevText("", 13, bold: true);
        _revTotalsText = RevText("", 13);
        _revExportButton = BulkButton(RevT("Выгрузить ▾", "Чыгаруу ▾", "Export ▾", "Dışa aktar ▾", "Yuklab olish ▾"), primary: false);
        _revExportButton.Click += (_, _) => RevisionShowExportMenu(this, _revExportButton, null, isDraft: true, say: RevisionSay, ok: RevisionSayOk);
        _revDiscardButton = BulkButton(RevT("Бросить ревизию", "Ревизияны таштоо", "Discard", "Sayımı bırak", "Inventarizatsiyani tashlash"), primary: false);
        _revDiscardButton.Click += async (_, _) => await RevisionDiscardAsync().ConfigureAwait(true);
        _revFinishButton = BulkButton(RevT("Завершить ревизию…", "Ревизияны аякташ…", "Finish stocktake…", "Sayımı bitir…", "Inventarizatsiyani yakunlash…"), primary: true);
        _revFinishButton.Click += async (_, _) => await RevisionFinishAsync().ConfigureAwait(true);
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in new Button[] { _revExportButton, _revDiscardButton, _revFinishButton })
        {
            b.MinHeight = 40; // на низком экране (1024×600) списку нужно место
            b.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(b);
        }

        _revMoneyPanel = new WrapPanel { Orientation = Orientation.Horizontal, IsVisible = false };
        var footerText = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        footerText.Children.Add(_revMessage);
        footerText.Children.Add(_revTotalsText);
        // 2026-10-29: денежные итоги — отдельной строкой под текстом и кнопками на всю ширину (на 1024×600 они не заезжают под кнопки).
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 8, 0, 0) };
        footerText.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(buttons, 1);
        Grid.SetRow(_revMoneyPanel, 1);
        Grid.SetColumnSpan(_revMoneyPanel, 2);
        _revMoneyPanel.Margin = new Thickness(0, 4, 0, 0);
        footer.Children.Add(footerText);
        footer.Children.Add(buttons);
        footer.Children.Add(_revMoneyPanel);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto") };
        void Place(Control c, int row)
        {
            Grid.SetRow(c, row);
            root.Children.Add(c);
        }

        Place(top, 0);
        Place(pillsRow, 1);
        Place(card, 2);
        header.Margin = new Thickness(0, 8, 0, 0);
        Place(header, 3);
        Place(listArea, 4);
        Place(footer, 5);
        return root;
    }

    private Control BuildRevisionCard()
    {
        _revCardName = RevText("", 17, bold: true);
        _revCardName.TextWrapping = TextWrapping.NoWrap;
        _revCardName.TextTrimming = TextTrimming.CharacterEllipsis;
        _revCardMeta = RevText("", 12.5, soft: true);
        _revCardMeta.TextWrapping = TextWrapping.NoWrap;
        _revCardMeta.TextTrimming = TextTrimming.CharacterEllipsis;
        _revCardNote = RevText("", 12.5, bold: true);
        _revCardNote.TextWrapping = TextWrapping.NoWrap;
        _revCardNote.TextTrimming = TextTrimming.CharacterEllipsis;
        _revCardNote.Bind(TextBlock.ForegroundProperty, _revCardNote.GetResourceObservable("BrushAccent"));
        var left = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(_revCardName);
        left.Children.Add(_revCardMeta);
        left.Children.Add(_revCardNote);

        _revCardBook = RevText("", 20, bold: true);
        _revCardBook.TextWrapping = TextWrapping.NoWrap;
        var bookBox = RevLabeled(RevT("Учёт", "Эсеп", "Book", "Sistem", "Hisob"), _revCardBook, 90);

        _revFactBox = new TextBox
        {
            Width = 120,
            MinHeight = 40,
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            VerticalContentAlignment = VerticalAlignment.Center,
            Watermark = RevT("Факт", "Факт", "Count", "Sayım", "Haqiqiy"),
            IsEnabled = false,
        };
        // Только цифры, разделитель и знаки «+» / «−»: штрихкод со сканера сюда не печатается (его перехватывает Window_KeyDown).
        _revFactBox.AddHandler(TextInputEvent, (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text) && e.Text.Any(c => !(char.IsDigit(c) || c is '.' or ',' or '+' or '-')))
                e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _revFactBox.TextChanged += (_, _) => RevisionUpdateLiveDiff();
        _revFactBox.KeyDown += RevisionFactKeyDown;
        var factBox = RevLabeled(RevT("Факт", "Иш жүзүндө", "Counted", "Sayılan", "Haqiqiy"), _revFactBox, 120);

        _revCardDiff = RevText("", 20, bold: true);
        _revCardDiff.TextWrapping = TextWrapping.NoWrap;
        var diffBox = RevLabeled(RevT("Разница", "Айырма", "Difference", "Fark", "Farq"), _revCardDiff, 90);

        _revEqualButton = BulkButton(RevT("Сошлось", "Дал келди", "Matches", "Tutuyor", "Mos keldi"), primary: false);
        _revEqualButton.Click += (_, _) => RevisionMarkEqual();
        ToolTip.SetTip(_revEqualButton, RevT("Факт равен учёту", "Факт эсепке барабар", "Counted equals the book quantity", "Sayım sistem miktarına eşit", "Haqiqiy hisobga teng"));
        _revResetButton = BulkButton(RevT("Сбросить", "Тазалоо", "Clear", "Temizle", "Tozalash"), primary: false);
        _revResetButton.Click += (_, _) => RevisionClearCurrent();
        ToolTip.SetTip(_revResetButton, RevT("Снять отметку «посчитано» у этого товара", "Бул товардан «саналды» белгисин алуу", "Remove the “counted” mark from this product", "Bu üründen «sayıldı» işaretini kaldır", "Bu mahsulotdan «sanaldi» belgisini olib tashlash"));
        _revEqualButton.MinHeight = 40;
        _revResetButton.MinHeight = 40;
        var cardButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Bottom };
        cardButtons.Children.Add(_revEqualButton);
        cardButtons.Children.Add(_revResetButton);

        // 2026-10-29: «Закупка» (цена закупки за единицу) и «Сумма» (разница × закупка) — только если пользователю можно видеть закупку.
        _revCardCost = RevText("", 20, bold: true);
        _revCardCost.TextWrapping = TextWrapping.NoWrap;
        _revCardCostBox = RevLabeled(RevT("Закупка", "Сатып алуу", "Cost", "Alış", "Xarid"), _revCardCost, 90);
        _revCardMoney = RevText("", 20, bold: true);
        _revCardMoney.TextWrapping = TextWrapping.NoWrap;
        _revCardMoneyBox = RevLabeled(RevT("Сумма разницы", "Айырма суммасы", "Diff. amount", "Fark tutarı", "Farq summasi"), _revCardMoney, 120);
        _revCardCostBox.IsVisible = false;
        _revCardMoneyBox.IsVisible = false;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto,Auto") };
        void Col(Control c, int col, double left0 = 14)
        {
            Grid.SetColumn(c, col);
            if (col > 0)
                c.Margin = new Thickness(left0, 0, 0, 0);
            grid.Children.Add(c);
        }

        Col(left, 0);
        Col(bookBox, 1);
        Col(factBox, 2);
        Col(diffBox, 3);
        Col(_revCardCostBox, 4);
        Col(_revCardMoneyBox, 5);
        Col(cardButtons, 6);
        return SoftBox(grid);
    }

    private static Control RevLabeled(string label, Control value, double width)
    {
        var l = RevText(label, 11.5, soft: true);
        l.TextWrapping = TextWrapping.NoWrap;
        var s = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, MinWidth = width };
        s.Children.Add(l);
        s.Children.Add(value);
        return s;
    }

    // ------------------------------------------------------------------ экраны

    private void RevisionOnTabOpened()
    {
        if (_rev is null)
        {
            RevisionRefreshStart();
            return;
        }

        Dispatcher.UIThread.Post(() => RevisionFocusScan());
    }

    private void RevisionShowScreen(bool work)
    {
        _revStartView.IsVisible = !work;
        _revWorkView.IsVisible = work;
    }

    private void RevisionFocusScan()
    {
        if (_rev is not null && _revWorkView.IsVisible)
            _revSearchBox.Focus();
    }

    /// <summary>Стартовый экран: «Начать ревизию» или «Продолжить ревизию от …» (черновик), замок «открыта в другой программе».</summary>
    private void RevisionRefreshStart()
    {
        if (_revStarting)
            return;
        RevisionDraftInfo? info = null;
        try
        {
            info = RevisionDraftStore.Peek();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: черновик не прочитан ({ex.Message}).", "WARNING");
        }

        _revDraftBox.IsVisible = info is not null;
        _revStartButton.IsVisible = info is null;
        if (info is not null)
        {
            _revDraftText.Text = RevT(
                $"Продолжить ревизию от {info.StartedAt:dd.MM HH:mm}, посчитано {info.Counted}",
                $"Ревизияны улантуу: {info.StartedAt:dd.MM HH:mm}, саналды {info.Counted}",
                $"Continue the stocktake from {info.StartedAt:dd.MM HH:mm}, counted {info.Counted}",
                $"{info.StartedAt:dd.MM HH:mm} tarihli sayıma devam et, sayılan {info.Counted}",
                $"{info.StartedAt:dd.MM HH:mm} dagi inventarizatsiyani davom ettirish, sanaldi {info.Counted}");
            // Часть актов уже создана на сервере — «заново» нельзя (были бы потеряны номера актов); продолжение довершит дело.
            _revRestartButton.IsVisible = !info.PostingStarted;
        }

        _revLockText.IsVisible = false;
        _revStartStatus.Text = "";
    }

    private void RevisionStartBusy(bool busy, string text = "")
    {
        _revStarting = busy;
        _revStartButton.IsEnabled = !busy;
        _revResumeButton.IsEnabled = !busy;
        _revRestartButton.IsEnabled = !busy;
        _revPastButton.IsEnabled = !busy;
        _revStartStatus.Text = text;
    }

    private async Task RevisionRestartAsync()
    {
        if (_revStarting)
            return;
        var info = RevisionDraftStore.Peek();
        var ok = PosConfirmDialog.Show(this,
            RevT("Начать ревизию заново?", "Ревизияны кайра баштайсызбы?", "Start the stocktake over?", "Sayım yeniden başlatılsın mı?", "Inventarizatsiya qaytadan boshlansinmi?"),
            RevT($"Сохранённый подсчёт ({info?.Counted ?? 0} позиций) будет удалён.", $"Сакталган саноо ({info?.Counted ?? 0} позиция) өчүрүлөт.",
                $"The saved count ({info?.Counted ?? 0} items) will be deleted.", $"Kayıtlı sayım ({info?.Counted ?? 0} kalem) silinecek.", $"Saqlangan hisob ({info?.Counted ?? 0} pozitsiya) o'chiriladi."),
            confirmText: RevT("Начать заново", "Кайра баштоо", "Start over", "Yeniden başla", "Qaytadan boshlash"),
            cancelText: RevT("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        if (!ok)
            return;
        RevisionDraftStore.Delete();
        await RevisionBeginAsync(resume: false).ConfigureAwait(true);
    }

    private static RevisionOptions RevisionMakeOptions() => new()
    {
        SizedProductIds = MarketSpheres.IsClothing ? RevisionSession.SizedProductIdsFromIndex() : null,
        // Штрихкод размера, весовая этикетка, 12/13 цифр — всё, что касса находит, а индекс ревизии (точное совпадение) нет.
        ExternalResolver = code => ProductCodeLookup.FindLocal(code)?.Product?.Id,
    };

    /// <summary>Обновить остатки с сервера (ТЗ 2.1, п. 1). (успех, текст ошибки).</summary>
    private static async Task<(bool Ok, string Error)> RevisionSyncCatalogAsync(CancellationToken ct)
    {
        try
        {
            var r = RevisionSyncOverride is { } over
                ? await over(ct).ConfigureAwait(true)
                : await CatalogCacheService.SyncCatalogFullAsync(ct).ConfigureAwait(true);
            return (r.Success, r.ErrorMessage ?? "");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task RevisionBeginAsync(bool resume)
    {
        if (_revStarting || _rev is not null)
            return;
        RevisionStartBusy(true, RevT("Обновляю остатки с сервера…", "Калдыктарды серверден жаңыртып жатам…", "Refreshing stock from the server…", "Stoklar sunucudan güncelleniyor…", "Qoldiqlar serverdan yangilanmoqda…"));
        var started = false;
        try
        {
            if (!RevisionDraftStore.TryAcquireLock(out var holder))
            {
                _revLockText.Text = RevT($"Ревизия открыта в другой программе этого компьютера (процесс {holder}). Закройте её там и повторите.",
                    $"Ревизия бул компьютердеги башка программада ачык (процесс {holder}). Ал жерде жаап, кайра аракет кылыңыз.",
                    $"The stocktake is open in another program on this computer (process {holder}). Close it there and try again.",
                    $"Sayım bu bilgisayardaki başka bir programda açık (işlem {holder}). Orada kapatıp tekrar deneyin.",
                    $"Inventarizatsiya shu kompyuterdagi boshqa dasturda ochiq (jarayon {holder}). U yerda yopib, qayta urinib ko'ring.");
                _revLockText.IsVisible = true;
                return;
            }

            _revLockText.IsVisible = false;
            try
            {
                var sync = await RevisionSyncCatalogAsync(CancellationToken.None).ConfigureAwait(true);
                if (!sync.Ok)
                {
                    PosLogger.Log($"Ревизия: остатки не обновлены ({sync.Error}).", "WARNING");
                    App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(RevT(
                        "Сервер не ответил — остатки взяты с этого компьютера.", "Сервер жооп берген жок — калдыктар бул компьютерден алынды.",
                        "The server did not respond — stock is taken from this computer.", "Sunucu yanıt vermedi — stoklar bu bilgisayardan alındı.", "Server javob bermadi — qoldiqlar shu kompyuterdan olindi."), isWarning: true);
                }

                var catalog = CatalogCacheService.Products.ToList();
                if (catalog.Count == 0)
                {
                    _revStartStatus.Text = RevT("Каталог пуст — ревизию начать нельзя.", "Каталог бош — ревизияны баштоо мүмкүн эмес.", "The catalog is empty — cannot start.", "Katalog boş — sayım başlatılamaz.", "Katalog bo'sh — boshlab bo'lmaydi.");
                    return;
                }

                var stockAt = sync.Ok ? DateTime.Now : (CatalogCacheService.LastSyncTime?.ToLocalTime() ?? DateTime.Now);
                RevisionSession? s;
                if (resume)
                {
                    var report = new RevisionRestoreReport();
                    s = RevisionSession.Restore(catalog, RevisionMakeOptions(), report);
                    if (s is null)
                    {
                        _revStartStatus.Text = RevT("Сохранённый подсчёт не найден.", "Сакталган саноо табылган жок.", "The saved count was not found.", "Kayıtlı sayım bulunamadı.", "Saqlangan hisob topilmadi.");
                        return;
                    }

                    if (sync.Ok)
                        s.RefreshExpected(catalog, stockAt);
                    if (report.MissingProducts > 0)
                        App.AppHost?.Services.GetService<IUserPrompts>()?.ShowToast(RevT(
                            $"Товаров из подсчёта больше нет в каталоге: {report.MissingProducts}.", $"Саноодогу товарлар каталогто жок: {report.MissingProducts}.",
                            $"Products from the count that are no longer in the catalog: {report.MissingProducts}.", $"Sayımdaki ürünlerden katalogda artık olmayan: {report.MissingProducts}.", $"Hisobdagi mahsulotlardan katalogda endi yo'qlari: {report.MissingProducts}."), isWarning: true);
                }
                else
                {
                    s = RevisionSession.Start(catalog, RevisionMakeOptions(), stockAt);
                }

                started = true;
                RevisionAttach(s);
            }
            finally
            {
                if (!started)
                    RevisionDraftStore.ReleaseLock();
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: не удалось начать: {ex}", "WARNING");
            _revStartStatus.Text = RevT("Не удалось начать ревизию: ", "Ревизияны баштоо мүмкүн болгон жок: ", "Could not start the stocktake: ", "Sayım başlatılamadı: ", "Inventarizatsiyani boshlab bo'lmadi: ") + ex.Message;
        }
        finally
        {
            RevisionStartBusy(false, started ? "" : _revStartStatus.Text ?? "");
            if (!started)
                RevisionRefreshStartKeepStatus();
        }
    }

    private void RevisionRefreshStartKeepStatus()
    {
        var status = _revStartStatus.Text;
        var lockText = _revLockText.Text;
        var lockVisible = _revLockText.IsVisible;
        RevisionRefreshStart();
        _revStartStatus.Text = status;
        _revLockText.Text = lockText;
        _revLockText.IsVisible = lockVisible;
    }

    /// <summary>Открыть рабочий экран для готовой сессии.</summary>
    private void RevisionAttach(RevisionSession s)
    {
        _rev = s;
        _revChangedHandler = () => Dispatcher.UIThread.Post(RevisionScheduleTotals);
        s.Changed += _revChangedHandler;
        s.StockApplied = RevisionSession.ApplyToLocalCatalog;

        // 2026-10-29: колонки с деньгами — по роли пользователя (шапка, строки, карточка, итоги).
        RevisionRowView.ShowMoney = RevisionShowPurchase;
        _revHeaderHost.Content = RevisionRowView.BuildHeader(false);
        _revCardCostBox.IsVisible = RevisionRowView.ShowMoney;
        _revCardMoneyBox.IsVisible = RevisionRowView.ShowMoney;

        _revUiSetup = true;
        _revFilter = RevisionFilter.All;
        _revCategory = null;
        _revViewKey = "";
        _revCurrent = null;
        _revSearchBox.Text = "";
        _revPills[0].IsChecked = true;
        var cats = new List<string> { RevT("Все категории", "Бардык категориялар", "All categories", "Tüm kategoriler", "Barcha toifalar") };
        cats.AddRange(s.Categories());
        _revCategoryBox.ItemsSource = cats;
        _revCategoryBox.SelectedIndex = 0;
        _revUiSetup = false;

        RevisionRebuildView(true);
        RevisionUpdateCard(null);
        RevisionRefreshTotals();
        _revMessage.Text = "";
        var at = s.StockAt;
        _revStockLabel.Text = RevT($"Остатки на {(at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("dd.MM HH:mm"))}", $"Калдыктар: {(at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("dd.MM HH:mm"))}",
            $"Stock as of {(at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("dd.MM HH:mm"))}", $"Stok: {(at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("dd.MM HH:mm"))}", $"Qoldiq: {(at.Date == DateTime.Today ? at.ToString("HH:mm") : at.ToString("dd.MM HH:mm"))}");
        _revDiscardButton.IsEnabled = !s.IsPostingStarted;
        RevisionShowScreen(true);
        PosLogger.Log($"Склад: ревизия {(s.CountedCount > 0 ? "продолжена" : "начата")}: строк {s.Rows.Count}, посчитано {s.CountedCount}.", "STOCK");
        Dispatcher.UIThread.Post(() => RevisionFocusScan());
    }

    /// <summary>Закрыть рабочий экран (ревизия проведена или брошена) и вернуться на стартовый.</summary>
    private void RevisionDetach()
    {
        var s = _rev;
        _rev = null;
        if (s is not null && _revChangedHandler is not null)
            s.Changed -= _revChangedHandler;
        _revChangedHandler = null;
        _revCurrent = null;
        _revScanQueue.Clear();
        _revView = new ObservableCollection<RevisionRow>();
        _revList.ItemsSource = _revView;
        RevisionDraftStore.ReleaseLock();
        RevisionShowScreen(false);
        RevisionRefreshStart();
    }

    private void RevisionOnWindowClosed()
    {
        _revTotalsTimer?.Stop();
        _revFilterTimer?.Stop();
        var s = _rev;
        if (s is null)
            return;
        try
        {
            RevisionCommitPending();
            if (!s.IsCompleted)
                s.SaveDraftNow();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Ревизия: подсчёт не сохранён при закрытии ({ex.Message}).", "WARNING");
        }

        if (_revChangedHandler is not null)
            s.Changed -= _revChangedHandler;
        _revChangedHandler = null;
        RevisionDraftStore.ReleaseLock();
    }

    // ------------------------------------------------------------------ список, фильтры, итоги

    private void RevisionScheduleTotals()
    {
        if (_revTotalsTimer is null)
            return;
        _revTotalsTimer.Stop();
        _revTotalsTimer.Start();
    }

    private void RevisionRefreshTotals()
    {
        var s = _rev;
        if (s is null)
            return;
        var t = s.GetTotals();
        var som = RevT("сом", "сом", "som", "TL", "so'm");
        var pcs = RevT("шт", "дана", "pcs", "adet", "dona");
        var text = RevT(
            $"Посчитано {ReportFormat.Integer(t.Counted)} из {ReportFormat.Integer(t.Total)}", $"Саналды {ReportFormat.Integer(t.Counted)} / {ReportFormat.Integer(t.Total)}",
            $"Counted {ReportFormat.Integer(t.Counted)} of {ReportFormat.Integer(t.Total)}", $"Sayılan {ReportFormat.Integer(t.Counted)} / {ReportFormat.Integer(t.Total)}", $"Sanaldi {ReportFormat.Integer(t.Counted)} / {ReportFormat.Integer(t.Total)}");
        text += "  ·  " + RevT($"с расхождением {ReportFormat.Integer(t.WithDifference)}", $"айырмасы бар {ReportFormat.Integer(t.WithDifference)}",
            $"with differences {ReportFormat.Integer(t.WithDifference)}", $"farklı {ReportFormat.Integer(t.WithDifference)}", $"farqli {ReportFormat.Integer(t.WithDifference)}");
        // 2026-10-29: владельцу/администратору деньги по расхождению — отдельными строками ниже (по закупке и по продаже), кассиру — как раньше, по продаже.
        var money = RevisionRowView.ShowMoney;
        _revMoneyPanel.Children.Clear();
        if (money)
        {
            foreach (var (lineText, brush, tip) in RevisionRowView.MoneyLines(t))
            {
                var tb = new TextBlock { Text = lineText, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 18, 0), TextWrapping = TextWrapping.NoWrap };
                tb.Bind(TextBlock.ForegroundProperty, tb.GetResourceObservable(brush));
                if (tip is not null)
                    ToolTip.SetTip(tb, tip);
                _revMoneyPanel.Children.Add(tb);
            }
        }

        _revMoneyPanel.IsVisible = _revMoneyPanel.Children.Count > 0;
        if (t.WithDifference > 0 && !money)
        {
            text += "  ·  " + RevT($"недостача −{ReportFormat.Quantity(t.ShortageQty)} {pcs} (−{ReportFormat.Money(t.ShortageAtSale)} {som})", $"жетишсиздик −{ReportFormat.Quantity(t.ShortageQty)} {pcs} (−{ReportFormat.Money(t.ShortageAtSale)} {som})",
                $"shortage −{ReportFormat.Quantity(t.ShortageQty)} {pcs} (−{ReportFormat.Money(t.ShortageAtSale)} {som})", $"eksik −{ReportFormat.Quantity(t.ShortageQty)} {pcs} (−{ReportFormat.Money(t.ShortageAtSale)} {som})", $"kamomad −{ReportFormat.Quantity(t.ShortageQty)} {pcs} (−{ReportFormat.Money(t.ShortageAtSale)} {som})");
            text += "  ·  " + RevT($"излишек +{ReportFormat.Quantity(t.SurplusQty)} {pcs} (+{ReportFormat.Money(t.SurplusAtSale)} {som})", $"ашыкча +{ReportFormat.Quantity(t.SurplusQty)} {pcs} (+{ReportFormat.Money(t.SurplusAtSale)} {som})",
                $"surplus +{ReportFormat.Quantity(t.SurplusQty)} {pcs} (+{ReportFormat.Money(t.SurplusAtSale)} {som})", $"fazla +{ReportFormat.Quantity(t.SurplusQty)} {pcs} (+{ReportFormat.Money(t.SurplusAtSale)} {som})", $"ortiqcha +{ReportFormat.Quantity(t.SurplusQty)} {pcs} (+{ReportFormat.Money(t.SurplusAtSale)} {som})");
        }

        if (t.ReadOnly > 0)
            text += "  ·  " + RevT($"по размерам (не входят): {t.ReadOnly}", $"өлчөмдөр боюнча (кирбейт): {t.ReadOnly}", $"by sizes (not included): {t.ReadOnly}", $"bedenlere göre (dahil değil): {t.ReadOnly}", $"o'lchamlar bo'yicha (kirmaydi): {t.ReadOnly}");
        _revTotalsText.Text = text;

        _revPills[0].Content = RevT("Все", "Баары", "All", "Tümü", "Hammasi") + " " + ReportFormat.Integer(t.Total);
        _revPills[1].Content = RevT("Посчитанные", "Саналгандар", "Counted", "Sayılanlar", "Sanalganlar") + " " + ReportFormat.Integer(t.Counted);
        _revPills[2].Content = RevT("Не посчитанные", "Саналбагандар", "Not counted", "Sayılmayanlar", "Sanalmaganlar") + " " + ReportFormat.Integer(t.NotCounted);
        _revPills[3].Content = RevT("С расхождением", "Айырмасы бар", "With differences", "Farklı olanlar", "Farqlilar") + " " + ReportFormat.Integer(t.WithDifference);
        _revFinishButton.IsEnabled = t.Counted > 0 || s.IsPostingStarted;
        _revDiscardButton.IsEnabled = !s.IsPostingStarted;
    }

    /// <summary>Пересобрать список по фильтру, поиску и категории — одним присвоением ItemsSource (ТЗ 2.5: не 15 000 Add).
    /// Если параметры не менялись с прошлой сборки — ничего не делает (скан со стиранием поля не сбрасывает прокрутку).</summary>
    private void RevisionRebuildView(bool force)
    {
        var s = _rev;
        if (s is null)
            return;
        var search = _revSearchBox.Text?.Trim() ?? "";
        var key = ((int)_revFilter).ToString(CultureInfo.InvariantCulture) + "|" + search + "|" + _revCategory;
        if (!force && key == _revViewKey)
            return;
        _revViewKey = key;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var list = s.Filter(_revFilter, search, _revCategory);
        _revView = new ObservableCollection<RevisionRow>(list);
        _revSuppressSel = true;
        _revList.ItemsSource = _revView;
        if (_revCurrent is not null && _revView.Contains(_revCurrent))
            _revList.SelectedItem = _revCurrent;
        _revSuppressSel = false;
        _revEmptyList.IsVisible = _revView.Count == 0;
        if (watch.ElapsedMilliseconds > 150)
            PosLogger.Log($"Склад: ревизия — список пересобран за {watch.ElapsedMilliseconds} мс ({_revView.Count} строк).", "STOCK");
    }

    /// <summary>Строки нет в текущем списке (фильтр/поиск) — показываем «Все» без поиска и категории, чтобы скан не «терялся».</summary>
    private void RevisionEnsureVisible(RevisionRow row)
    {
        if (_revView.Contains(row))
            return;
        _revUiSetup = true;
        _revFilter = RevisionFilter.All;
        _revPills[0].IsChecked = true;
        _revSearchBox.Text = "";
        _revCategory = null;
        _revCategoryBox.SelectedIndex = 0;
        _revUiSetup = false;
        RevisionRebuildView(true);
    }

    // ------------------------------------------------------------------ текущая строка и карточка

    private void RevisionListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_rev is null || !e.GetCurrentPoint(_revList).Properties.IsLeftButtonPressed)
            return;
        var view = (e.Source as Visual)?.FindAncestorOfType<RevisionRowView>(true);
        if (view is null)
            return;
        var row = view.Row;
        // После нажатия список сам забирает фокус на строку — курсор в «Факт» ставим следующим тактом.
        Dispatcher.UIThread.Post(() =>
        {
            if (_rev is not null)
                RevisionSetCurrent(row, focusFact: true);
        }, DispatcherPriority.Input);
    }

    private void RevisionSetCurrent(RevisionRow? row, bool focusFact, string? note = null)
    {
        if (!ReferenceEquals(row, _revCurrent))
            RevisionCommitPending();
        _revCurrent = row;
        if (row is not null)
        {
            RevisionEnsureVisible(row);
            _revSuppressSel = true;
            _revList.SelectedItem = row;
            _revSuppressSel = false;
            // Строка не у самого края: сначала подводим строку чуть ниже неё, потом её саму (виден «контекст», а не последняя строка у края).
            var at = _revView.IndexOf(row);
            var visibleRows = (int)(_revList.Bounds.Height / RevisionRowView.RowHeight);
            var ahead = Math.Min(3, visibleRows - 2);
            if (at >= 0 && ahead > 0 && at + ahead < _revView.Count)
                _revList.ScrollIntoView(at + ahead);
            _revList.ScrollIntoView(at >= 0 ? at : 0);
        }

        RevisionUpdateCard(note);
        if (focusFact && row is { IsReadOnly: false })
        {
            _revFactBox.Focus();
            _revFactBox.SelectAll();
        }
    }

    private static string RevisionNumberText(double v) => v.ToString("0.###", CultureInfo.CurrentCulture);

    private void RevisionUpdateCard(string? note)
    {
        var r = _revCurrent;
        if (r is null)
        {
            _revCardName.Text = RevT("Пикните товар или выберите строку в списке", "Товарды сканерлеңиз же тизмеден саптны тандаңыз", "Scan a product or pick a line in the list", "Ürünü okutun veya listeden bir satır seçin", "Mahsulotni skanerlang yoki ro'yxatdan qatorni tanlang");
            _revCardMeta.Text = "";
            _revCardNote.Text = "";
            _revCardBook.Text = "";
            _revCardDiff.Text = "";
            _revCardCost.Text = "";
            _revCardMoney.Text = "";
            _revFactBox.Text = "";
            _revFactInitial = "";
            _revFactBox.IsEnabled = false;
            _revEqualButton.IsEnabled = false;
            _revResetButton.IsEnabled = false;
            return;
        }

        var pcs = string.IsNullOrWhiteSpace(r.Unit) ? RevT("шт", "дана", "pcs", "adet", "dona") : r.Unit;
        _revCardName.Text = r.Name;
        _revCardMeta.Text = string.Join("  ·  ", new[] { r.Barcode, r.Article, r.Category }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var book = r.IsCounted ? r.ExpectedAtCount : r.Expected;
        _revCardBook.Text = ReportFormat.Quantity(book) + " " + pcs;
        _revFactBox.IsEnabled = !r.IsReadOnly && !(_rev?.IsLocked ?? true);
        _revFactBox.Text = r.Fact is { } f ? RevisionNumberText(f) : "";
        _revFactInitial = _revFactBox.Text ?? "";
        _revEqualButton.IsEnabled = _revFactBox.IsEnabled;
        _revResetButton.IsEnabled = _revFactBox.IsEnabled && r.IsCounted;
        if (note is null)
        {
            if (r.IsReadOnly)
                note = RevT("Товар с размерами — пересчитайте по размерам в карточке товара.", "Өлчөмдүү товар — товардын карточкасында өлчөмдөр боюнча кайра санаңыз.",
                    "Product with sizes — count by size in the product card.", "Bedenli ürün — ürün kartında bedenlere göre sayın.", "O'lchamli mahsulot — mahsulot kartochkasida o'lchamlar bo'yicha sanang.");
            else if (r.IsCounted)
                note = RevT($"Уже посчитано: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})", $"Мурун саналган: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})",
                    $"Already counted: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})", $"Zaten sayıldı: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})", $"Allaqachon sanalgan: {ReportFormat.Quantity(r.Fact ?? 0)} ({r.CountedAt:HH:mm})");
            else
                note = "";
        }

        _revCardNote.Text = note;
        RevisionUpdateLiveDiff();
    }

    /// <summary>«Разница» в карточке считается на лету по тому, что набрано в «Факт» (число заменяет, «+3» прибавляет).</summary>
    private void RevisionUpdateLiveDiff()
    {
        var r = _revCurrent;
        if (r is null)
            return;
        double? fact = r.Fact;
        var text = _revFactBox.Text?.Trim() ?? "";
        if (text.Length > 0 && text != _revFactInitial.Trim() && RevisionSession.TryParseEntry(text, out var v, out var add))
            fact = add ? (r.Fact ?? 0) + v : v;
        // 2026-10-29: закупка за единицу и сумма разницы (разница × закупка; закупки нет — по цене продажи, пометка «*»).
        _revCardCost.Text = r.HasPurchase ? ReportFormat.Money(r.PurchasePrice) : "—";
        _revCardCost.Bind(TextBlock.ForegroundProperty, _revCardCost.GetResourceObservable(r.HasPurchase ? "BrushText" : "BrushTextSoft"));
        if (fact is not { } fv)
        {
            _revCardDiff.Text = "—";
            _revCardDiff.Foreground = null;
            _revCardMoney.Text = "—";
            _revCardMoney.Foreground = null;
            ToolTip.SetTip(_revCardMoney, null);
            return;
        }

        var d = Math.Round(fv - (r.IsCounted ? r.ExpectedAtCount : r.Expected), 3);
        _revCardDiff.Text = d > 0 ? "+" + ReportFormat.Quantity(d) : ReportFormat.Quantity(d);
        var key = d < -0.0005 ? "BrushDanger" : d > 0.0005 ? "BrushSuccess" : "BrushText";
        _revCardDiff.Bind(TextBlock.ForegroundProperty, _revCardDiff.GetResourceObservable(key));
        if (RevisionRowView.DiffMoney(r, d) is { } m)
        {
            var star = m.Fallback && Math.Abs(m.Amount) > 0.004;
            _revCardMoney.Text = RevisionRowView.SignedMoney(m.Amount) + (star ? "*" : "");
            _revCardMoney.Bind(TextBlock.ForegroundProperty, _revCardMoney.GetResourceObservable(key));
            ToolTip.SetTip(_revCardMoney, star
                ? RevT("Закупки у товара нет — сумма посчитана по цене продажи.", "Товардын сатып алуу баасы жок — сумма сатуу баасы боюнча эсептелген.",
                    "No purchase price for this product — the amount uses the selling price.", "Ürünün alış fiyatı yok — tutar satış fiyatıyla hesaplandı.", "Mahsulotda xarid narxi yo'q — summa sotuv narxida hisoblangan.")
                : null);
        }
        else
        {
            _revCardMoney.Text = "—";
            _revCardMoney.Foreground = null;
            ToolTip.SetTip(_revCardMoney, null);
        }
    }

    // ------------------------------------------------------------------ ввод факта

    private void RevisionSay(string? text) => RevisionSayCore(text, "BrushText");

    private void RevisionSayOk() => RevisionSayCore(_revMessage.Text, "BrushSuccess");

    private void RevisionSayCore(string? text, string brush)
    {
        _revMessage.Text = text ?? "";
        _revMessage.Bind(TextBlock.ForegroundProperty, _revMessage.GetResourceObservable(brush));
    }

    private void RevisionWarn(string text) => RevisionSayCore(text, "BrushDanger");

    private void RevisionFactKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                RevisionCommitFromBox();
                break;
            case Key.Escape:
                e.Handled = true;
                _revFactBox.Text = _revFactInitial;
                RevisionFocusScan();
                break;
            case Key.Down or Key.Up when _revCurrent is not null:
                e.Handled = true;
                RevisionMove(e.Key == Key.Down ? 1 : -1);
                break;
        }
    }

    private void RevisionMove(int delta)
    {
        if (_revCurrent is null)
            return;
        var i = _revView.IndexOf(_revCurrent);
        var next = i + delta;
        if (i < 0 || next < 0 || next >= _revView.Count)
            return;
        RevisionSetCurrent(_revView[next], focusFact: true);
    }

    /// <summary>Enter в «Факт»: записать число (заменяет, «+3» прибавляет) и вернуть курсор в поле скана.</summary>
    private void RevisionCommitFromBox()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null)
        {
            RevisionFocusScan();
            return;
        }

        var text = (_revFactBox.Text ?? "").Trim();
        if (text.Length == 0 || text == _revFactInitial.Trim())
        {
            RevisionFocusScan(); // ничего не набрано — просто дальше
            return;
        }

        if (!RevisionSession.TryParseEntry(text, out _, out _))
        {
            RevisionWarn(RevT("Введите число: 5, 5,5 или +3 (прибавить).", "Сан киргизиңиз: 5, 5,5 же +3 (кошуу).", "Enter a number: 5, 5.5 or +3 (add).", "Bir sayı yazın: 5, 5,5 veya +3 (ekle).", "Son kiriting: 5, 5,5 yoki +3 (qo'shish)."));
            _revFactBox.SelectAll();
            return;
        }

        if (!s.CommitEntry(row, text))
        {
            RevisionWarn(RevT("Число не принято: итог не может быть меньше нуля.", "Сан кабыл алынган жок: жыйынтык нөлдөн аз болбошу керек.", "The number was not accepted: the result cannot be below zero.", "Sayı kabul edilmedi: sonuç sıfırdan küçük olamaz.", "Son qabul qilinmadi: natija noldan kam bo'lmasligi kerak."));
            _revFactBox.SelectAll();
            return;
        }

        RevisionAfterCommit(row, focusScan: true);
    }

    /// <summary>Набрали число, но не нажали Enter, и пикнули следующий (или щёлкнули другую строку) — число сохраняется (ТЗ 2.1, п. 3).
    /// Нетронутое поле (текст тот же, что показали) не записывается: иначе перезапись сдвинула бы учёт на момент пересчёта.</summary>
    private void RevisionCommitPending()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null || !_revFactBox.IsEnabled)
            return;
        var text = (_revFactBox.Text ?? "").Trim();
        if (text.Length == 0 || text == _revFactInitial.Trim())
            return;
        _revFactInitial = _revFactBox.Text ?? "";
        if (RevisionSession.TryParseEntry(text, out _, out _) && s.CommitEntry(row, text))
            RevisionAfterCommit(row, focusScan: false);
        else
            RevisionWarn(RevT($"«{row.Name}»: набранное «{text}» не принято.", $"«{row.Name}»: терилген «{text}» кабыл алынган жок.", $"“{row.Name}”: the typed “{text}” was not accepted.", $"“{row.Name}”: yazılan “{text}” kabul edilmedi.", $"«{row.Name}»: kiritilgan «{text}» qabul qilinmadi."));
    }

    private void RevisionAfterCommit(RevisionRow row, bool focusScan)
    {
        // Фильтр «не посчитанные» / «с расхождением»: строка, которая перестала подходить, уходит из списка.
        var leaves = (_revFilter == RevisionFilter.NotCounted && row.IsCounted) || (_revFilter == RevisionFilter.WithDifference && !row.HasDifference);
        if (leaves && _revView.Contains(row))
        {
            _revSuppressSel = true;
            _revView.Remove(row);
            _revSuppressSel = false;
            _revEmptyList.IsVisible = _revView.Count == 0;
        }

        if (ReferenceEquals(row, _revCurrent))
            RevisionUpdateCard(null);
        var d = row.Difference;
        RevisionSayCore(RevT($"✓ {row.Name}: факт {ReportFormat.Quantity(row.Fact ?? 0)}, разница {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: факт {ReportFormat.Quantity(row.Fact ?? 0)}, айырма {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: counted {ReportFormat.Quantity(row.Fact ?? 0)}, difference {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: sayım {ReportFormat.Quantity(row.Fact ?? 0)}, fark {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}",
            $"✓ {row.Name}: haqiqiy {ReportFormat.Quantity(row.Fact ?? 0)}, farq {(d > 0 ? "+" : "")}{ReportFormat.Quantity(d)}"),
            d < -0.0005 ? "BrushDanger" : d > 0.0005 ? "BrushSuccess" : "BrushText");
        RevisionScheduleTotals();
        if (focusScan)
            RevisionFocusScan();
    }

    private void RevisionMarkEqual()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null || row.IsReadOnly)
            return;
        if (s.SetFact(row, row.Expected))
            RevisionAfterCommit(row, focusScan: true);
    }

    private void RevisionClearCurrent()
    {
        var s = _rev;
        var row = _revCurrent;
        if (s is null || row is null)
            return;
        if (s.ClearFact(row))
        {
            RevisionAfterCommit(row, focusScan: true);
            RevisionSay(RevT($"Отметка снята: {row.Name}", $"Белги алынды: {row.Name}", $"Mark removed: {row.Name}", $"İşaret kaldırıldı: {row.Name}", $"Belgi olib tashlandi: {row.Name}"));
        }
    }

    // ------------------------------------------------------------------ поле «Скан / поиск»

    private void RevisionSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_revSearchBox.Text))
            {
                e.Handled = true;
                _revSearchBox.Text = "";
                RevisionRebuildView(false);
            }

            return;
        }

        if (e.Key == Key.Down && _revView.Count > 0)
        {
            e.Handled = true;
            RevisionSetCurrent(_revCurrent is not null && _revView.Contains(_revCurrent) ? _revCurrent : _revView[0], focusFact: false);
            _revList.Focus();
            return;
        }

        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        var s = _rev;
        var text = _revSearchBox.Text?.Trim() ?? "";
        if (s is null || text.Length == 0)
            return;
        if (s.FindByCode(text) is not null || WarehouseViewModel.LooksLikeCode(text))
        {
            // Штрихкод (со сканера или набранный руками) — это скан.
            _revSearchBox.Text = "";
            RevisionRebuildView(false);
            RevisionOnScanned(text);
            return;
        }

        // Название: список уже отфильтрован — Enter берёт первую найденную строку.
        RevisionRebuildView(false);
        if (_revView.Count > 0)
            RevisionSetCurrent(_revView[0], focusFact: true);
        else
            RevisionWarn(RevT($"«{text}» не найдено.", $"«{text}» табылган жок.", $"“{text}” not found.", $"“{text}” bulunamadı.", $"«{text}» topilmadi."));
    }

    // ------------------------------------------------------------------ скан

    /// <summary>Нужен ли перехват быстрого ввода внутри этого поля (HandleScanInsideField): везде, кроме «Скан / поиск» — оно ловит скан само.</summary>
    private bool RevisionNeedsFieldScanIntercept(TextBox box) => !ReferenceEquals(box, _revSearchBox);

    /// <summary>Код извне (камера, переход из другого окна): в открытую ревизию; без ревизии — игнорируется.</summary>
    private void RevisionExternalScan(string barcode)
    {
        if (_rev is not null)
            RevisionOnScanned(barcode);
        else
            PosLogger.Log($"Склад: код '{barcode}' пришёл, а ревизия не начата — пропущен.", "STOCK");
    }

    /// <summary>Скан со сканера (вне полей) или из поля «Факт»: ставится в очередь и обрабатывается по одному, по порядку.</summary>
    private void RevisionOnScanned(string barcode)
    {
        if (_rev is null || string.IsNullOrWhiteSpace(barcode))
            return;
        _revScanQueue.Enqueue(barcode);
        if (!_revScanRunning)
            _ = RevisionDrainScansAsync();
    }

    private async Task RevisionDrainScansAsync()
    {
        _revScanRunning = true;
        try
        {
            while (_revScanQueue.Count > 0 && _rev is not null)
            {
                var code = _revScanQueue.Dequeue();
                try
                {
                    await RevisionHandleScanAsync(code).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    PosLogger.Log($"Склад: скан '{code}' в ревизии не обработан: {ex.Message}", "WARNING");
                }
            }
        }
        finally
        {
            _revScanRunning = false;
        }
    }

    private async Task RevisionHandleScanAsync(string raw)
    {
        var s = _rev;
        if (s is null)
            return;
        // Число, набранное и не подтверждённое Enter, сохраняется раньше, чем обработается новый скан.
        RevisionCommitPending();
        var plusOne = _revPlusOne.IsChecked == true;
        var res = s.Scan(raw, plusOne ? RevisionScanMode.PlusOne : RevisionScanMode.EnterNumber);
        switch (res.Status)
        {
            case RevisionScanStatus.Ignored:
                return;
            case RevisionScanStatus.Locked when res.Match is { } lockedMatch:
                RevisionSetCurrent(lockedMatch.Row, focusFact: false);
                RevisionFocusScan();
                if (s.IsLocked && !lockedMatch.Row.IsReadOnly)
                    RevisionWarn(RevT("Ревизия отправляется на сервер — менять подсчёт нельзя.", "Ревизия серверге жөнөтүлүп жатат — саноону өзгөртүүгө болбойт.", "The stocktake is being sent to the server — the count cannot be changed.", "Sayım sunucuya gönderiliyor — sayım değiştirilemez.", "Inventarizatsiya serverga yuborilmoqda — hisobni o'zgartirib bo'lmaydi."));
                return;
            case RevisionScanStatus.NotFound:
            {
                var row = await RevisionResolveUnknownAsync(res.Code).ConfigureAwait(true);
                if (row is null || _rev is null)
                {
                    RevisionFocusScan();
                    return;
                }

                if (plusOne)
                {
                    _rev.AddFact(row, 1);
                    RevisionSetCurrent(row, focusFact: false, RevT("Новый товар: +1", "Жаңы товар: +1", "New product: +1", "Yeni ürün: +1", "Yangi mahsulot: +1"));
                    RevisionFocusScan();
                }
                else
                {
                    RevisionSetCurrent(row, focusFact: true, RevT("Новый товар: учёт 0", "Жаңы товар: эсеп 0", "New product: book 0", "Yeni ürün: sistem 0", "Yangi mahsulot: hisob 0"));
                }

                return;
            }
        }

        var match = res.Match!;
        if (plusOne)
        {
            RevisionSetCurrent(match.Row, focusFact: false, RevT($"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}", $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}",
                $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}", $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}", $"+{ReportFormat.Quantity(res.Added)} → {ReportFormat.Quantity(match.Row.Fact ?? 0)}"));
            RevisionAfterCommit(match.Row, focusScan: true);
            return;
        }

        string? note = null;
        if (match.PackageQuantity > 1)
            note = RevT($"Упаковка ×{ReportFormat.Quantity(match.PackageQuantity)} шт", $"Таңгак ×{ReportFormat.Quantity(match.PackageQuantity)} дана", $"Pack ×{ReportFormat.Quantity(match.PackageQuantity)} pcs", $"Koli ×{ReportFormat.Quantity(match.PackageQuantity)} adet", $"Qadoq ×{ReportFormat.Quantity(match.PackageQuantity)} dona");
        RevisionSetCurrent(match.Row, focusFact: true, note);
    }

    /// <summary>Код не из списка ревизии: товар есть в каталоге (добавлен позже) → строка; нет — сервер, затем «Создать товар» (как раньше).</summary>
    private async Task<RevisionRow?> RevisionResolveUnknownAsync(string code)
    {
        var key = ProductCodeLookup.Normalize(code);
        CatalogProductTileVm? tile = null;
        var local = ProductCodeLookup.FindLocal(key)?.Product;
        if (local is not null)
        {
            tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, local.Id, StringComparison.OrdinalIgnoreCase)) ?? local;
        }
        else
        {
            if (!WarehouseViewModel.LooksLikeCode(key))
            {
                RevisionWarn(RevT($"«{key}» не найдено.", $"«{key}» табылган жок.", $"“{key}” not found.", $"“{key}” bulunamadı.", $"«{key}» topilmadi."));
                return null;
            }

            ProductCodeLookup.Result server;
            try
            {
                server = await ProductCodeLookup.FindOnServerAsync(key).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Склад: ревизия — поиск '{key}' на сервере упал: {ex.Message}", "STOCK");
                server = new ProductCodeLookup.Result(null, true);
            }

            if (server.Hit?.Product is { } hit)
            {
                tile = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, hit.Id, StringComparison.OrdinalIgnoreCase)) ?? hit;
            }
            else if (server.Unavailable)
            {
                RevisionWarn(RevT("Не удалось проверить штрихкод на сервере. Проверьте связь и пикните ещё раз.", "Штрихкодду серверден текшерүү мүмкүн болгон жок. Байланышты текшерип, кайра сканерлеңиз.",
                    "The barcode could not be verified with the server. Check the connection and scan again.", "Barkod sunucuda doğrulanamadı. Bağlantıyı kontrol edip tekrar okutun.", "Shtrix-kodni serverda tekshirib bo'lmadi. Ulanishni tekshirib, qayta skanerlang."));
                return null;
            }
            else
            {
                tile = await OfferCreateProductAsync(key).ConfigureAwait(true);
            }
        }

        return tile is null ? null : _rev?.AddCatalogProduct(tile);
    }

    // ------------------------------------------------------------------ завершить, бросить

    private async Task RevisionDiscardAsync()
    {
        var s = _rev;
        if (s is null)
            return;
        RevisionCommitPending();
        var ok = PosConfirmDialog.Show(this,
            RevT("Бросить ревизию?", "Ревизияны таштайсызбы?", "Discard the stocktake?", "Sayım bırakılsın mı?", "Inventarizatsiya tashlansinmi?"),
            RevT($"Подсчёт ({s.CountedCount} позиций) будет удалён, остатки не изменятся.", $"Саноо ({s.CountedCount} позиция) өчүрүлөт, калдыктар өзгөрбөйт.",
                $"The count ({s.CountedCount} items) will be deleted; stock will not change.", $"Sayım ({s.CountedCount} kalem) silinecek, stoklar değişmeyecek.", $"Hisob ({s.CountedCount} pozitsiya) o'chiriladi, qoldiqlar o'zgarmaydi."),
            confirmText: RevT("Бросить", "Таштоо", "Discard", "Bırak", "Tashlash"),
            cancelText: RevT("Остаться", "Калуу", "Stay", "Vazgeç", "Qolish"),
            accent: PosConfirmAccent.Danger);
        if (!ok)
            return;
        if (!s.Discard())
        {
            RevisionWarn(RevT("Часть актов уже создана на сервере — бросить нельзя, завершите ревизию.", "Актылардын бир бөлүгү серверде түзүлгөн — таштоого болбойт, ревизияны аякташ керек.",
                "Some documents are already created on the server — cannot discard, finish the stocktake.", "Bazı tutanaklar sunucuda oluşturuldu — bırakılamaz, sayımı bitirin.", "Aktlarning bir qismi serverda yaratilgan — tashlab bo'lmaydi, inventarizatsiyani yakunlang."));
            return;
        }

        RevisionDetach();
        await Task.CompletedTask;
    }

    private async Task RevisionFinishAsync()
    {
        var s = _rev;
        if (s is null)
            return;
        RevisionCommitPending();
        if (s.CountedCount == 0 && !s.IsPostingStarted)
        {
            RevisionWarn(RevT("Ничего не посчитано.", "Эч нерсе саналган жок.", "Nothing counted yet.", "Henüz hiçbir şey sayılmadı.", "Hali hech narsa sanalmagan."));
            return;
        }

        var api = App.AppHost?.Services.GetService<IInventoryApiService>();
        if (api is null)
        {
            RevisionWarn(RevT("Сервис ревизии недоступен в этом режиме.", "Бул режимде ревизия кызматы жеткиликсиз.", "The stocktake service isn't available in this mode.", "Sayım hizmeti bu modda kullanılamıyor.", "Bu rejimda inventarizatsiya xizmati mavjud emas."));
            return;
        }

        var dialog = new RevisionFinishDialog(s, api, RevisionRefreshForApplyAsync, RevisionShowExportMenu);
        await PosDialogHost.ShowAsync(dialog, this).ConfigureAwait(true);
        if (dialog.Result is { Status: RevisionApplyStatus.Done } done)
        {
            RevisionDetach();
            _viewModel.ReapplyProductFilter();
            RefreshWarehouseTotals();
            SuccessToast.Show(RevT(
                $"Ревизия проведена: позиций {done.SentRows}, актов {done.SessionIds.Count}.", $"Ревизия өткөрүлдү: позиция {done.SentRows}, акт {done.SessionIds.Count}.",
                $"Stocktake posted: {done.SentRows} items, {done.SessionIds.Count} documents.", $"Sayım tamamlandı: {done.SentRows} kalem, {done.SessionIds.Count} tutanak.", $"Inventarizatsiya o'tkazildi: {done.SentRows} pozitsiya, {done.SessionIds.Count} akt."));
            return;
        }

        RevisionRefreshTotals();
        RevisionUpdateCard(null);
        RevisionFocusScan();
    }

    /// <summary>Свежий каталог для «Применить» (поправка на продажи, ТЗ 2.3). Сбой обновления останавливает «Применить»: со старым учётом поправка была бы неверной.</summary>
    private async Task<IReadOnlyList<CatalogProductTileVm>> RevisionRefreshForApplyAsync(CancellationToken ct)
    {
        var r = await RevisionSyncCatalogAsync(ct).ConfigureAwait(true);
        if (!r.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(r.Error) ? RevT("нет связи с сервером", "сервер менен байланыш жок", "no connection to the server", "sunucuyla bağlantı yok", "server bilan aloqa yo'q") : r.Error);
        return CatalogCacheService.Products.ToList();
    }

    // ------------------------------------------------------------------ выгрузка и прошлые ревизии

    private static List<RevisionExportRow> RevisionBuildExportRows(RevisionSession s, IReadOnlyDictionary<string, double>? recorded)
    {
        var buy = RevisionShowPurchase;
        var list = new List<RevisionExportRow>(s.Rows.Count);
        foreach (var r in s.Rows)
        {
            double? written = recorded is not null && recorded.TryGetValue(r.ProductId, out var w) ? w : null;
            list.Add(new RevisionExportRow(r.Name, r.Barcode, r.IsCounted ? r.ExpectedAtCount : r.Expected, r.Fact, r.SalePrice, buy ? r.PurchasePrice : null, r.CountedAt, written));
        }

        return list;
    }

    private static string RevisionScopeName(RevisionExportScope scope) => scope switch
    {
        RevisionExportScope.Scanned => RevT("Отсканированные", "Сканерленгендер", "Scanned only", "Okutulanlar", "Skanerlanganlar"),
        RevisionExportScope.Discrepancy => RevT("С расхождением", "Айырмасы барлар", "With differences", "Farklı olanlar", "Farqlilar"),
        _ => RevT("Весь склад", "Бүт кампа", "Whole warehouse", "Tüm depo", "Butun ombor"),
    };

    private static string RevisionFormatName(RevisionExportFormat f) => f switch
    {
        RevisionExportFormat.Csv => "CSV",
        RevisionExportFormat.Pdf => "PDF",
        _ => "Excel",
    };

    /// <summary>Пункты «Excel / CSV / PDF» с объёмами «весь склад / отсканированные / с расхождением».</summary>
    private static void RevisionFillFormatItems(ItemCollection items, Action<RevisionExportFormat, RevisionExportScope> run)
    {
        foreach (var format in new[] { RevisionExportFormat.Excel, RevisionExportFormat.Csv, RevisionExportFormat.Pdf })
        {
            var formatItem = new MenuItem { Header = RevisionFormatName(format) };
            foreach (var scope in new[] { RevisionExportScope.All, RevisionExportScope.Scanned, RevisionExportScope.Discrepancy })
            {
                var item = new MenuItem { Header = RevisionScopeName(scope) };
                var f = format;
                var sc = scope;
                item.Click += (_, _) => run(f, sc);
                formatItem.Items.Add(item);
            }

            items.Add(formatItem);
        }
    }

    /// <summary>Меню «Выгрузить ▾» (черновик или окно итога): <paramref name="recorded"/> — что уйдёт в акт (колонка «Записано в акт»).</summary>
    internal void RevisionShowExportMenu(Window owner, Control anchor, IReadOnlyDictionary<string, double>? recorded, bool isDraft, Action<string?> say, Action ok)
    {
        var s = _rev;
        if (s is null)
            return;
        RevisionCommitPending();
        var menu = new ContextMenu();
        RevisionFillFormatItems(menu.Items, (format, scope) =>
        {
            var current = _rev;
            if (current is null)
                return;
            var rows = RevisionBuildExportRows(current, recorded);
            var meta = new RevisionExportMeta
            {
                ShopName = UserPreferences.Instance.StoreName,
                IsDraft = isDraft,
                StartedAt = current.StartedAt,
                ConductedBy = current.UserName,
                StockAsOf = current.StockAt,
            };
            _ = RevisionExportUi.RunAsync(owner, format, rows, meta, new RevisionExportOptions { Scope = scope, IncludePurchasePrices = RevisionShowPurchase }, say, ok);
        });
        menu.Open(anchor);
    }

    private async Task RevisionShowPastAsync()
    {
        _revPastButton.IsEnabled = false;
        List<RevisionActSummary> acts;
        // 2026-10-27, аудит владельца В-2: у владельца к местным ревизиям добавляются ревизии с других касс и ПК (акты сервера).
        List<(string Key, DateTime WhenUtc, string By, List<InventoryAct> Parts)> remote = new();
        try
        {
            acts = await Task.Run(RevisionActStore.List).ConfigureAwait(true);
            if (AppMode.IsOwner && App.AppHost?.Services.GetService<IInventoryApiService>() is { } invApi)
            {
                var known = new HashSet<string>(acts.SelectMany(a => a.SessionIds ?? Array.Empty<string>()), StringComparer.OrdinalIgnoreCase);
                var all = await invApi.ListActsAsync(CancellationToken.None).ConfigureAwait(true);
                // Части одной ревизии («… (1/3)», «… (2/3)») — один пункт списка.
                foreach (var g in (all ?? Array.Empty<InventoryAct>())
                             .Where(a => a.IsApplied && a.Note.TrimStart().StartsWith("Ревизия", StringComparison.OrdinalIgnoreCase) && !known.Contains(a.Id))
                             .GroupBy(a => System.Text.RegularExpressions.Regex.Replace(a.Note.Trim(), @"\s*\(\d+/\d+\)\s*$", "")))
                    remote.Add((g.Key, g.Max(a => a.CreatedUtc), g.First().CreatedByName, g.ToList()));
                remote = remote.OrderByDescending(r => r.WhenUtc).Take(30).ToList();
            }
        }
        finally
        {
            _revPastButton.IsEnabled = true;
        }

        var menu = new ContextMenu();
        if (acts.Count == 0 && remote.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = RevT("Проведённых ревизий пока нет", "Өткөрүлгөн ревизиялар азырынча жок", "No completed stocktakes yet", "Henüz tamamlanmış sayım yok", "Hali o'tkazilgan inventarizatsiyalar yo'q"), IsEnabled = false });
        }

        foreach (var act in acts)
        {
            var item = new MenuItem { Header = $"{act.FinishedAt:dd.MM.yyyy HH:mm}  ·  " + RevT($"посчитано {act.Counted} из {act.Total}", $"саналды {act.Counted} / {act.Total}", $"counted {act.Counted} of {act.Total}", $"sayılan {act.Counted} / {act.Total}", $"sanaldi {act.Counted} / {act.Total}") };
            var file = act.FileName;
            RevisionFillFormatItems(item.Items, (format, scope) => _ = RevisionExportActAsync(file, format, scope));
            menu.Items.Add(item);
        }

        foreach (var r in remote)
        {
            var parts = r.Parts;
            var lineCount = parts.Sum(a => a.Lines.Count);
            var by = string.IsNullOrWhiteSpace(r.By) ? "" : "  ·  " + r.By;
            var item = new MenuItem
            {
                Header = $"{r.WhenUtc.ToLocalTime():dd.MM.yyyy HH:mm}  ·  " + RevT($"на другой кассе: изменено {lineCount}", $"башка кассада: өзгөргөн {lineCount}", $"on another till: {lineCount} changed",
                    $"başka kasada: {lineCount} değişti", $"boshqa kassada: {lineCount} ta o'zgardi") + by,
            };
            RevisionFillFormatItems(item.Items, (format, scope) => _ = RevisionExportRemoteAsync(parts, r.WhenUtc, format, scope));
            menu.Items.Add(item);
        }

        menu.Open(_revPastButton);
    }

    /// <summary>2026-10-27, В-2: выгрузка ревизии, проведённой на другой кассе, по актам сервера (было / стало; цены и штрихкод — из каталога, если товар найден).</summary>
    private async Task RevisionExportRemoteAsync(List<InventoryAct> parts, DateTime whenUtc, RevisionExportFormat format, RevisionExportScope scope)
    {
        var buy = RevisionShowPurchase;
        var rows = new List<RevisionExportRow>();
        foreach (var line in parts.SelectMany(a => a.Lines))
        {
            var card = CatalogCacheService.Products.FirstOrDefault(p => string.Equals(p.Id, line.ProductId, StringComparison.OrdinalIgnoreCase));
            rows.Add(new RevisionExportRow(line.Name, card?.Barcode, line.Before, line.Fact,
                card is null ? 0 : LocalCartService.ParsePrice(card.PriceLine), buy ? card?.PurchasePrice : null, whenUtc.ToLocalTime(), line.Fact));
        }

        var meta = new RevisionExportMeta
        {
            ShopName = UserPreferences.Instance.StoreName,
            IsDraft = false,
            StartedAt = whenUtc.ToLocalTime(),
            FinishedAt = whenUtc.ToLocalTime(),
            ConductedBy = parts.FirstOrDefault()?.CreatedByName,
            ActNumbers = parts.Select(a => a.Id).ToList(),
        };
        var success = false;
        await RevisionExportUi.RunAsync(this, format, rows, meta, new RevisionExportOptions { Scope = scope, IncludePurchasePrices = buy },
            text => _revStartStatus.Text = text ?? "", () => success = true).ConfigureAwait(true);
        if (success)
            _revStartStatus.Bind(TextBlock.ForegroundProperty, _revStartStatus.GetResourceObservable("BrushSuccess"));
    }

    private async Task RevisionExportActAsync(string fileName, RevisionExportFormat format, RevisionExportScope scope)
    {
        var act = await Task.Run(() => RevisionActStore.Load(fileName)).ConfigureAwait(true);
        if (act is null)
        {
            _revStartStatus.Text = RevT("Не удалось прочитать сохранённый акт.", "Сакталган актты окуу мүмкүн болгон жок.", "Could not read the saved document.", "Kayıtlı tutanak okunamadı.", "Saqlangan aktni o'qib bo'lmadi.");
            return;
        }

        var buy = RevisionShowPurchase;
        var rows = act.Rows.Select(r => new RevisionExportRow(r.Name, r.Barcode, r.Expected, r.Fact, r.SalePrice, buy ? r.PurchasePrice : null, r.CountedAt, r.Written)).ToList();
        var meta = new RevisionExportMeta
        {
            ShopName = UserPreferences.Instance.StoreName,
            IsDraft = false,
            StartedAt = act.StartedAt,
            FinishedAt = act.FinishedAt,
            ConductedBy = act.UserName,
            ActNumbers = act.SessionIds,
            StockAsOf = act.StockAt,
        };
        var success = false;
        await RevisionExportUi.RunAsync(this, format, rows, meta, new RevisionExportOptions { Scope = scope, IncludePurchasePrices = buy },
            text => _revStartStatus.Text = text ?? "", () => success = true).ConfigureAwait(true);
        if (success)
            _revStartStatus.Bind(TextBlock.ForegroundProperty, _revStartStatus.GetResourceObservable("BrushSuccess"));
    }
}
