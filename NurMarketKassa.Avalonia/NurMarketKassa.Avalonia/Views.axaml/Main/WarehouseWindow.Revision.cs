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
    internal static bool RevisionShowPurchase => ReceivingMarkupDialog.CanEditSettings(); // 2026-11-05 (Л4-10): --owner сам по себе закупку не открывает

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
        pillsRow.Children.Add(BuildCycleCountButton()); // 2026-11-05 (функция 9): «Пересчёт дня…»

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
        CycleCountReset(); // 2026-11-05 (функция 9)
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
        // 2026-11-11 (Д-7): на NBS при ревизии частная память доходила до 2,1 ГБ, в Headless на 15 000 строк (с обёрткой масштаба владельца) — +57 МБ и 16 строк на экране.
        // Чтобы следующая живая проверка показала причину: память «до / через 8 с» и сколько строк списка создано (должно быть ≈ видимым, а не тысячи).
        MemoryDiagnostics.LogAround("ревизия начата");
        var probe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        probe.Tick += (_, _) =>
        {
            probe.Stop();
            try
            {
                var realized = _revList.GetRealizedContainers().Count();
                PosLogger.Log($"Склад: ревизия — строк списка создано {realized} из {_revView.Count}, список {_revList.Bounds.Width:0}×{_revList.Bounds.Height:0}.",
                    realized > 300 ? "WARNING" : "STOCK");
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Склад: ревизия — замер списка не удался: {ex.Message}", "DEBUG");
            }
        };
        probe.Start();
    }

    /// <summary>Закрыть рабочий экран (ревизия проведена или брошена) и вернуться на стартовый.</summary>
    private void RevisionDetach()
    {
        var s = _rev;
        _rev = null;
        if (s is not null && _revChangedHandler is not null)
            s.Changed -= _revChangedHandler;
        _revChangedHandler = null;
        CycleCountReset(); // 2026-11-05 (функция 9)
        _revCurrent = null;
        _revScanQueue.Clear();
        _revView = new ObservableCollection<RevisionRow>();
        _revList.ItemsSource = _revView;
        RevisionDraftStore.ReleaseLock();
        RevisionShowScreen(false);
        RevisionRefreshStart();
        // 2026-11-11 (Д-7, живая проверка «Склада»: на «Ревизии» частная память владельца до 2,1 ГБ, после «Бросить ревизию» сборщик держал 1,9 ГБ при живых 0,4 ГБ).
        // Ревизия на 15 тыс. строк закончена/брошена — через 20 с одна уплотняющая сборка (OwnerMemoryTrim: не чаще раза в 2 мин), память возвращается системе.
        if (AppMode.IsOwner)
        {
            var trim = new DispatcherTimer { Interval = OwnerMemoryTrim.SettleDelay };
            trim.Tick += (_, _) =>
            {
                trim.Stop();
                _ = Task.Run(() => OwnerMemoryTrim.TrimNow(DateTime.MinValue));
            };
            trim.Start();
        }
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
        // 2026-11-06 (В12, ЖП Н-3): в «Пересчёте дня» счётчики — по выбранным позициям (25), а не по всей ревизии (15 459). Деньги (ниже) остаются по всей ревизии.
        (int Total, int Counted, int NotCounted, int WithDifference) shown = _cycleIds is { Count: > 0 } cycle ? s.GetSubsetCounts(cycle) : (t.Total, t.Counted, t.NotCounted, t.WithDifference);
        var som = RevT("сом", "сом", "som", "TL", "so'm");
        var pcs = RevT("шт", "дана", "pcs", "adet", "dona");
        var text = RevT(
            $"Посчитано {ReportFormat.Integer(shown.Counted)} из {ReportFormat.Integer(shown.Total)}", $"Саналды {ReportFormat.Integer(shown.Counted)} / {ReportFormat.Integer(shown.Total)}",
            $"Counted {ReportFormat.Integer(shown.Counted)} of {ReportFormat.Integer(shown.Total)}", $"Sayılan {ReportFormat.Integer(shown.Counted)} / {ReportFormat.Integer(shown.Total)}", $"Sanaldi {ReportFormat.Integer(shown.Counted)} / {ReportFormat.Integer(shown.Total)}");
        text += "  ·  " + RevT($"с расхождением {ReportFormat.Integer(shown.WithDifference)}", $"айырмасы бар {ReportFormat.Integer(shown.WithDifference)}",
            $"with differences {ReportFormat.Integer(shown.WithDifference)}", $"farklı {ReportFormat.Integer(shown.WithDifference)}", $"farqli {ReportFormat.Integer(shown.WithDifference)}");
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

        _revPills[0].Content = RevT("Все", "Баары", "All", "Tümü", "Hammasi") + " " + ReportFormat.Integer(shown.Total);
        _revPills[1].Content = RevT("Посчитанные", "Саналгандар", "Counted", "Sayılanlar", "Sanalganlar") + " " + ReportFormat.Integer(shown.Counted);
        _revPills[2].Content = RevT("Не посчитанные", "Саналбагандар", "Not counted", "Sayılmayanlar", "Sanalmaganlar") + " " + ReportFormat.Integer(shown.NotCounted);
        _revPills[3].Content = RevT("С расхождением", "Айырмасы бар", "With differences", "Farklı olanlar", "Farqlilar") + " " + ReportFormat.Integer(shown.WithDifference);
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
        var key = ((int)_revFilter).ToString(CultureInfo.InvariantCulture) + "|" + search + "|" + _revCategory + "|" + (_cycleIds?.Count.ToString(CultureInfo.InvariantCulture) ?? "");
        if (!force && key == _revViewKey)
            return;
        _revViewKey = key;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var list = s.Filter(_revFilter, search, _revCategory);
        if (_cycleIds is not null) // 2026-11-05 (функция 9): «Пересчёт дня» — в списке только выбранные позиции
            list = list.Where(r => _cycleIds.Contains(r.ProductId)).ToList();
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

}
