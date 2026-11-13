using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-11 (владелец: «массовая печать ценников/штрихкодов — выведи в Склад сверху! Как в вебе!»): окно «Печать штрихкодов и ценников» — тонкая оболочка
/// (ModalShell: заголовок, «Закрыть», страницы и «Виды ценников…» в подвале) вокруг <see cref="BarcodePrintView"/>. Вид, размеры, точки входа
/// (Open / OpenWith) и стендовые подмены — прежние; всё содержимое и логика печати — в <see cref="BarcodePrintView"/> (BarcodePrintWindow.*.cs).</summary>
public sealed class BarcodePrintWindow : Window
{
    // ---- Подмены для проверочного стенда (в программе всегда null) — те же поля элемента, прежние имена сохранены.
    public static Func<LabelPrintRequest, LabelPrintResult>? ThermalPrintOverride
    {
        get => BarcodePrintView.ThermalPrintOverride;
        set => BarcodePrintView.ThermalPrintOverride = value;
    }

    public static Func<IReadOnlyList<DiscoveredPrinter>>? PrinterListOverride
    {
        get => BarcodePrintView.PrinterListOverride;
        set => BarcodePrintView.PrinterListOverride = value;
    }

    public static Func<string, string, Task<string?>>? BarcodeSaveOverride
    {
        get => BarcodePrintView.BarcodeSaveOverride;
        set => BarcodePrintView.BarcodeSaveOverride = value;
    }

    public static Func<IReadOnlyList<A4SheetItem>, A4SheetOptions, string, LabelPrintResult>? SheetPrintOverride
    {
        get => BarcodePrintView.SheetPrintOverride;
        set => BarcodePrintView.SheetPrintOverride = value;
    }

    /// <summary>Стенд: подтверждение создания штрихкода без окна вопроса.</summary>
    public static Func<string, Task<bool>>? ConfirmOverride
    {
        get => BarcodePrintView.ConfirmOverride;
        set => BarcodePrintView.ConfirmOverride = value;
    }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly BarcodePrintView _view;

    /// <summary>Содержимое окна (поиск, карточки, печать) — тот же элемент, что стоит вкладкой «Штрихкоды» в «Складе».</summary>
    public BarcodePrintView View => _view;

    /// <summary>Открыть окно из Склада и Маркетплейса (прежняя точка входа BulkPriceTagPrintDialog.Open).</summary>
    public static async Task Open(Window? owner)
    {
        if (!BarcodePrintAccess.Ensure(owner)) // 2026-11-01 (П-10): право «Печать штрих-кодов»
            return;
        // 2026-10-27 (аудит редакторов, Б-2): массовая печать — платная; все входы (Склад, Маркетплейс, редактор этикеток, ценники) идут сюда.
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.BulkPriceTag, owner))
            return;
        var window = new BarcodePrintWindow();
        if (owner is null)
        {
            window.Show();
            return;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    /// <summary>2026-11-01 (П-4): открыть окно с отмеченными товарами и готовыми копиями (например, «Этикетки на приход»).
    /// Платная проверка та же, что у Open. Запомненный поиск не применяется. Товары, которых нет в каталоге кассы, пропускаются с сообщением.</summary>
    public static async Task OpenWith(Window? owner, IReadOnlyList<(string ProductId, string? Barcode, int Copies)> items)
    {
        if (!BarcodePrintAccess.Ensure(owner)) // 2026-11-01 (П-10): право «Печать штрих-кодов»
            return;
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.BulkPriceTag, owner))
            return;
        var window = new BarcodePrintWindow(false);
        window.ApplyPreset(items);
        if (owner is null)
        {
            window.Show();
            return;
        }
        await window.ShowDialog(owner).ConfigureAwait(true);
    }

    public BarcodePrintWindow() : this(true)
    {
    }

    internal BarcodePrintWindow(bool restoreLast)
    {
        _view = new BarcodePrintView(restoreLast, false);
        Title = T("Печать штрихкодов и ценников", "Штрих-коддорду жана баа белгилерин басып чыгаруу", "Print barcodes and price tags",
            "Barkod ve fiyat etiketi yazdırma", "Shtrix-kod va narx yorliqlarini chop etish");
        Width = 1264;
        Height = 860;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        FontFamily = new FontFamily("Segoe UI, Inter, Noto Sans");
        this.Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        this.Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var close = new Button { IsCancel = true, MinWidth = 110, Content = T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish") };
        close.Classes.Add("modal-cancel");
        close.Click += (_, _) => Close();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _view.LegacyButton, close } };

        var shell = new ModalShell
        {
            Margin = new Thickness(16),
            Title = T("Печать штрихкодов и ценников", "Штрих-коддорду жана баа белгилерин басып чыгаруу", "Print barcodes and price tags", "Barkod ve fiyat etiketi yazdırma", "Shtrix-kod va narx yorliqlarini chop etish"),
            Subtitle = T("Отметьте товары, настройте этикетку и печатайте на термопринтер или на лист A4.", "Товарларды белгилеп, этикетканы тууралап, термопринтерге же A4 барагына басыңыз.",
                "Tick the products, set up the label and print on a thermal printer or an A4 sheet.", "Ürünleri işaretleyin, etiketi ayarlayın ve termal yazıcıya veya A4 sayfaya yazdırın.", "Mahsulotlarni belgilang, yorliqni sozlang va termoprinterda yoki A4 varaqqa chop eting."),
            ContentScroll = ScrollBarVisibility.Disabled,
            ScreenHeightShare = 1.0,
            InitialFocus = _view.SearchBox,
            Footer = footer,
            Hint = _view.HintBar,
            Content = _view,
        };
        _view.Shell = shell;
        Content = shell;
        Closing += (_, _) => _view.SaveState();
        Closed += (_, _) => _view.Shutdown();
    }

    internal void ApplyPreset(IReadOnlyList<(string ProductId, string? Barcode, int Copies)> items) => _view.ApplyPreset(items);
}

/// <summary>2026-11-11 (владелец: «массовая печать — в Склад сверху, как в вебе»): содержимое вкладки «Штрихкоды» в «Складе» и раздела «Штрихкоды и ценники» программы
/// владельца. Права и платность — прежние (право «Печать штрих-кодов», <see cref="PaidFeature.BulkPriceTag"/>): вкладка видна всегда, а если печать не куплена или нет права —
/// вместо печати подсказка с кнопкой «Ввести серийный номер» (та же проверка, что у окна). Печать (<see cref="BarcodePrintView"/>) создаётся при первом показе.</summary>
public sealed class BarcodePrintTabHost : UserControl
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private BarcodePrintView? _view;
    private readonly Border _gate;
    private readonly TextBlock _gateTitle;
    private readonly TextBlock _gateDescription;
    private readonly TextBlock _gateInfo;
    private readonly Button _gateButton;

    /// <summary>Печать куплена или активирована серийным номером (после этого кнопки «Печать ценников» в Складе становятся постоянными).</summary>
    public event Action? Unlocked;

    /// <summary>Сама печать; null — ещё не показывалась или закрыта проверкой прав/покупки.</summary>
    public BarcodePrintView? View => _view;

    public BarcodePrintTabHost()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch; // 2026-11-11: ширину задаёт родитель (вкладка/раздел), а не содержимое
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ClipToBounds = true;
        _gateTitle = new TextBlock { FontSize = 18, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _gateDescription = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        _gateInfo = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        _gateButton = new Button { HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 44, Padding = new Thickness(18, 0), CornerRadius = new CornerRadius(10), FontWeight = FontWeight.SemiBold };
        _gateButton.Classes.Add("sec-primary");
        _gateButton.Click += (_, _) => Unlock(TopLevel.GetTopLevel(this) as Window);
        var icon = MarketplaceIcons.Icon("barcode", 40);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.Bind(TemplatedControl.ForegroundProperty, icon.GetResourceObservable("BrushTextSoft"));
        _gateTitle.Bind(TextBlock.ForegroundProperty, _gateTitle.GetResourceObservable("BrushText"));
        _gateDescription.Bind(TextBlock.ForegroundProperty, _gateDescription.GetResourceObservable("BrushTextSoft"));
        _gateInfo.Bind(TextBlock.ForegroundProperty, _gateInfo.GetResourceObservable("BrushText"));
        _gate = new Border
        {
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Child = new StackPanel
            {
                Spacing = 14, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, _gateTitle, _gateDescription, _gateInfo, _gateButton },
            },
        };
        _gate.Bind(Border.BackgroundProperty, _gate.GetResourceObservable("BrushPanel"));
        AttachedToVisualTree += (_, _) => Refresh();
    }

    /// <summary>Право есть и печать куплена.</summary>
    public static bool CanPrint => BarcodePrintAccess.IsAllowed() && PaidFeatureGate.IsUnlocked(PaidFeature.BulkPriceTag);

    /// <summary>Показать печать (если можно) или подсказку; вызывается при каждом показе вкладки.</summary>
    public void Refresh()
    {
        if (CanPrint)
        {
            if (_view is null)
            {
                // Каталог в памяти пуст (раздел открыт раньше «Склада») — берём из базы этого компьютера, как делает «Склад» (WarehouseWindow.LoadCatalogIfEmpty).
                if (CatalogCacheService.Products.Count == 0)
                    CatalogCacheService.LoadFromDatabase();
                _view = new BarcodePrintView(false, true); // 2026-11-11: без «запомненного поиска» прошлого раза
            }
            if (!ReferenceEquals(Content, _view))
                Content = _view; // 2026-11-12: без вложенного ScrollViewer/MinWidthHost (давали пустые рамки кнопок и растянутую полосу настроек)
            return;
        }
        _gateTitle.Text = T("Печать штрихкодов и ценников", "Штрих-коддорду жана баа белгилерин басып чыгаруу", "Print barcodes and price tags",
            "Barkod ve fiyat etiketi yazdırma", "Shtrix-kod va narx yorliqlarini chop etish");
        if (!BarcodePrintAccess.IsAllowed())
        {
            _gateDescription.Text = BarcodePrintAccess.DeniedText;
            _gateInfo.Text = "";
            _gateButton.IsVisible = false;
        }
        else
        {
            var fee = TariffGate.PackActivationFee;
            _gateDescription.Text = T("Отметьте товары, настройте размер этикетки и напечатайте штрихкоды и ценники сразу для всех — на термопринтер или на лист A4.",
                "Товарларды белгилеп, этикетканын өлчөмүн тууралап, штрих-коддорду жана баа белгилерин бирдей баарына термопринтерге же A4 барагына басыңыз.",
                "Tick the products, set the label size and print barcodes and price tags for all of them at once — on a thermal printer or an A4 sheet.",
                "Ürünleri işaretleyin, etiket boyutunu ayarlayın ve barkod ile fiyat etiketlerini hepsi için tek seferde termal yazıcıya veya A4 sayfaya yazdırın.",
                "Mahsulotlarni belgilang, yorliq o'lchamini sozlang va shtrix-kod hamda narx yorliqlarini barchasi uchun birdaniga termoprinterda yoki A4 varaqqa chop eting.");
            _gateInfo.Text = T($"Платная функция — подключение {fee} сом. Купить: «Маркетплейс → Доп. функции» или у поддержки NurCRM; полученный серийный номер введите кнопкой ниже.",
                $"Акылуу функция — туташтыруу {fee} сом. Сатып алуу: «Маркетплейс → Кошумча функциялар» же NurCRM колдоо кызматы; алынган сериялык номерди төмөнкү баскыч менен киргизиңиз.",
                $"Paid feature — activation {fee} som. Buy it in «Marketplace → Extras» or from NurCRM support, then enter the serial number with the button below.",
                $"Ücretli özellik — etkinleştirme {fee} som. «Pazar yeri → Ek özellikler» bölümünden veya NurCRM destekten satın alın; aldığınız seri numarasını aşağıdaki düğmeyle girin.",
                $"Pullik funksiya — ulash {fee} so'm. Sotib olish: «Marketpleys → Qo'shimcha funksiyalar» yoki NurCRM qo'llab-quvvatlash xizmati; olingan seriya raqamini quyidagi tugma bilan kiriting.");
            _gateButton.Content = T("Ввести серийный номер", "Сериялык номерди киргизүү", "Enter serial number", "Seri numarası gir", "Seriya raqamini kiritish");
            _gateButton.IsVisible = true;
        }
        if (!ReferenceEquals(Content, _gate))
            Content = _gate;
    }

    /// <summary>Та же проверка, что у окна: право, затем покупка (при необходимости — ввод серийного номера). true — печать доступна, вкладка показывает её.</summary>
    public bool Unlock(Window? owner)
    {
        if (!BarcodePrintAccess.Ensure(owner))
            return false;
        var wasUnlocked = PaidFeatureGate.IsUnlocked(PaidFeature.BulkPriceTag);
        if (!PaidFeatureGate.EnsureUnlocked(PaidFeature.BulkPriceTag, owner))
            return false;
        Refresh();
        if (!wasUnlocked)
            Unlocked?.Invoke();
        return _view != null;
    }

    /// <summary>Выбранные товары (кнопка «Печать ценников (N)» на вкладке «Товары»): отмечены и показаны первыми, копии — по умолчанию. false — нет права или покупки.</summary>
    public bool ApplySelection(IReadOnlyList<string> productIds, Window? owner)
    {
        if (!Unlock(owner))
            return false;
        // copies <= 0 — «по умолчанию»: число копий из настроек печати, а не жёстко одна.
        _view!.ApplyPreset(productIds.Select(id => (id, (string?)null, 0)).ToList());
        return true;
    }

    /// <summary>Остановить таймер состояния принтера (раздел закрывается).</summary>
    public void Shutdown() => _view?.Shutdown();
}

/// <summary>2026-11-11 (видео «у.mp4»: при прокрутке списка карточек вся область «Склад → Штрихкоды» сужалась ступенями к центру и рывком раздувалась): содержимое занимает всю
/// выданную ширину, но своей желаемой ширины родителю НЕ сообщает. Список карточек считает колонки и ширину карточек от ширины, которую ему дали; если ширина ряда карточек
/// (колонки × карточка) становилась желаемой шириной вкладки, родитель (обёртка «Масштаба интерфейса» LayoutTransformControl измеряет ребёнка по его прошлому размеру) сужал
/// следующий проход, колонки пересчитывались — петля. Здесь петля разорвана: желаемая ширина 0 (при неограниченной ширине — как у ребёнка), высота — как у ребёнка.</summary>
public sealed class WidthFreeHost : Decorator
{
    /// <summary>Сколько раз выполнялось измерение (проверка: в покое раскладка не крутится).</summary>
    public int MeasureCalls { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureCalls++;
        if (Child is null)
            return default;
        Child.Measure(availableSize);
        return new Size(double.IsInfinity(availableSize.Width) ? Child.DesiredSize.Width : 0, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is null)
            return finalSize;
        // 2026-11-11 (снимок владельца 22:14, «срез» справа): ребёнок был измерен по ширине, которую родитель потом не выдал (желаемая ширина 0), и раскладывался уже
        // по меньшей — список считал колонки и карточки по большей. Перед раскладкой ребёнок измеряется заново по ширине, которую ему на самом деле дают (если она та же — Avalonia пропускает).
        Child.Measure(finalSize);
        Child.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
