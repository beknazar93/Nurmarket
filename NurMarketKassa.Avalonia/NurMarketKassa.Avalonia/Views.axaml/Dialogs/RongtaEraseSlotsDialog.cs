using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-11-21 (просьба владельца «добавь возможность старые ячейки стирать!»): что стереть в ячейках PLU весов Rongta — только старые (переехавшие товары и товары, которых нет в
/// каталоге), номера вручную («5, 10-20, 33») или все ячейки. Перед стиранием окно показывает, ЧТО будет стёрто и что пропущено как защищённое; кнопка «Стереть» — не «по умолчанию»
/// (Enter не стирает), «Все ячейки» требуют впечатать число ячеек. Ничего не отправляет — только возвращает план (<see cref="Plan"/>), стирает окно «Весы».</summary>
internal sealed class RongtaEraseSlotsDialog : Window
{
    private readonly RongtaEraseContext _ctx;
    private readonly RadioButton _staleRadio;
    private readonly RadioButton _rangeRadio;
    private readonly RadioButton _allRadio;
    private readonly TextBox _rangeBox;
    private readonly CheckBox _keepSentCheck;
    private readonly TextBlock _previewText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _confirmLabel = new() { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _confirmBox = new() { Width = 120, Height = 36, MaxLength = 5 };
    private readonly StackPanel _confirmRow = new() { Orientation = Orientation.Vertical, Spacing = 4, IsVisible = false };
    private readonly Button _ok;

    /// <summary>Выбранный план (после «Стереть»); null — окно закрыто отменой.</summary>
    public RongtaErasePlan? Plan { get; private set; }

    public RongtaEraseMode Mode { get; private set; }

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public RongtaEraseSlotsDialog(RongtaEraseContext ctx)
    {
        _ctx = ctx;
        var title = T("Стереть старые ячейки", "Эски уячаларды өчүрүү", "Erase old slots", "Eski hücreleri sil", "Eski kataklarni o‘chirish");
        Title = title;
        Width = 600;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        Bind(BackgroundProperty, this.GetResourceObservable("DialogWindowBackground"));
        Bind(TransparencyLevelHintProperty, this.GetResourceObservable("DialogTransparencyHint"));

        var intro = new TextBlock
        {
            Text = T($"Весы Rongta {ctx.ScaleHost}. Ячейка очищается записью пустого товара (без названия, код 0, цена 0): сам номер в памяти весов остаётся. Ничего не стирается без вашего подтверждения; ячейки отмеченных в окне товаров касса не трогает.",
                     $"Rongta таразасы {ctx.ScaleHost}. Уяча бош товар жазуу менен тазаланат (аты жок, коду 0, баасы 0): номердин өзү таразанын эс тутумунда калат. Сиздин ырастооңузсуз эч нерсе өчпөйт; терезеде белгиленген товарлардын уячаларына касса тийбейт.",
                     $"Rongta scale {ctx.ScaleHost}. A slot is cleared by writing an empty item (no name, code 0, price 0): the number itself stays in the scale memory. Nothing is erased without your confirmation; the slots of the products ticked in the window are never touched.",
                     $"Rongta tartı {ctx.ScaleHost}. Hücre boş bir ürün yazılarak temizlenir (adsız, kod 0, fiyat 0): numaranın kendisi tartının belleğinde kalır. Onayınız olmadan hiçbir şey silinmez; penceredeki işaretli ürünlerin hücrelerine kasa dokunmaz.",
                     $"Rongta tarozi {ctx.ScaleHost}. Katak bo‘sh tovar yozish bilan tozalanadi (nomsiz, kod 0, narx 0): raqamning o‘zi tarozi xotirasida qoladi. Tasdig‘ingizsiz hech narsa o‘chmaydi; oynada belgilangan tovarlarning kataklariga kassa tegmaydi."),
            FontSize = 13, TextWrapping = TextWrapping.Wrap,
        };
        intro.Bind(TextBlock.ForegroundProperty, intro.GetResourceObservable("BrushText"));

        _staleRadio = new RadioButton
        {
            GroupName = "rongta-erase-mode",
            IsChecked = ctx.Stale.Count > 0,
            IsEnabled = ctx.Stale.Count > 0,
            Content = ctx.Stale.Count > 0
                ? T($"Только старые ячейки: {ctx.Stale.Count} (товар сменил номер или его нет в каталоге)",
                    $"Эски уячалар гана: {ctx.Stale.Count} (товар номерин алмаштырды же каталогто жок)",
                    $"Only old slots: {ctx.Stale.Count} (the product changed its number or is not in the catalog)",
                    $"Yalnızca eski hücreler: {ctx.Stale.Count} (ürün numara değiştirdi veya katalogda yok)",
                    $"Faqat eski kataklar: {ctx.Stale.Count} (tovar raqamini almashtirgan yoki katalogda yo‘q)")
                : T("Только старые ячейки: таких нет", "Эски уячалар гана: андайлар жок", "Only old slots: there are none", "Yalnızca eski hücreler: yok", "Faqat eski kataklar: yo‘q"),
        };
        _rangeBox = new TextBox
        {
            Height = 36, MaxLength = 200, Margin = new Thickness(26, 0, 0, 0),
            Watermark = T("например: 5, 10-20, 33", "мисалы: 5, 10-20, 33", "for example: 5, 10-20, 33", "örneğin: 5, 10-20, 33", "masalan: 5, 10-20, 33"),
        };
        _rangeRadio = new RadioButton
        {
            GroupName = "rongta-erase-mode",
            IsChecked = ctx.Stale.Count == 0,
            Content = T($"Номера PLU вручную (1–{RongtaSlotEraser.MaxSlot})", $"PLU номерлерин колу менен (1–{RongtaSlotEraser.MaxSlot})",
                $"PLU numbers by hand (1–{RongtaSlotEraser.MaxSlot})", $"PLU numaralarını elle (1–{RongtaSlotEraser.MaxSlot})", $"PLU raqamlarini qo‘lda (1–{RongtaSlotEraser.MaxSlot})"),
        };
        _allRadio = new RadioButton
        {
            GroupName = "rongta-erase-mode",
            Content = T($"Все ячейки (1–{RongtaSlotEraser.MaxSlot})", $"Бардык уячалар (1–{RongtaSlotEraser.MaxSlot})", $"All slots (1–{RongtaSlotEraser.MaxSlot})", $"Tüm hücreler (1–{RongtaSlotEraser.MaxSlot})", $"Barcha kataklar (1–{RongtaSlotEraser.MaxSlot})"),
        };
        _keepSentCheck = new CheckBox
        {
            IsChecked = true,
            Margin = new Thickness(26, 0, 0, 0),
            Content = T("Не трогать ячейки товаров, которые касса отправила и они есть в каталоге (рекомендуется)",
                        "Касса жөнөткөн жана каталогдо бар товарлардын уячаларына тийбөө (сунушталат)",
                        "Do not touch the slots of goods the till sent that are still in the catalog (recommended)",
                        "Kasanın gönderdiği ve katalogda bulunan ürünlerin hücrelerine dokunma (önerilir)",
                        "Kassa yuborgan va katalogda bor tovarlar kataklariga tegmaslik (tavsiya etiladi)"),
        };
        foreach (var rb in new[] { _staleRadio, _rangeRadio, _allRadio })
        {
            rb.Bind(ForegroundProperty, rb.GetResourceObservable("BrushText"));
            rb.IsCheckedChanged += (_, _) => Refresh();
        }
        _keepSentCheck.Bind(ForegroundProperty, _keepSentCheck.GetResourceObservable("BrushText"));
        _keepSentCheck.IsCheckedChanged += (_, _) => Refresh();
        _rangeBox.TextChanged += (_, _) => Refresh();
        _confirmBox.TextChanged += (_, _) => Refresh();

        _previewText.Bind(TextBlock.ForegroundProperty, _previewText.GetResourceObservable("BrushText"));
        var previewScroll = new ScrollViewer
        {
            MaxHeight = 150,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _previewText,
        };
        var previewBox = new Border { Padding = new Thickness(10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = previewScroll };
        previewBox.Bind(Border.BackgroundProperty, previewBox.GetResourceObservable("BrushPanelSoft"));
        previewBox.Bind(Border.BorderBrushProperty, previewBox.GetResourceObservable("BrushBorder"));

        _confirmLabel.Bind(TextBlock.ForegroundProperty, _confirmLabel.GetResourceObservable("BrushDanger"));
        _confirmRow.Children.Add(_confirmLabel);
        _confirmRow.Children.Add(_confirmBox);

        var cancel = new Button { Content = T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"), IsCancel = true };
        cancel.Classes.Add("modal-cancel");
        cancel.Click += (_, _) => Close(false);
        // Enter не стирает: кнопка «Стереть» не IsDefault.
        _ok = new Button { Content = T("Стереть", "Өчүрүү", "Erase", "Sil", "O‘chirish"), IsEnabled = false };
        _ok.Classes.Add("modal-primary");
        _ok.Classes.Add("danger");
        _ok.Click += OnOk;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, _ok } };

        var body = new StackPanel
        {
            Spacing = 8,
            Children = { intro, _staleRadio, _rangeRadio, _rangeBox, _allRadio, _keepSentCheck, previewBox, _confirmRow },
        };
        Content = new ModalShell
        {
            Margin = new Thickness(16), Title = title, Footer = buttons,
            Content = new ScrollViewer { MaxHeight = 470, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = body },
        };
        Refresh();
        Opened += (_, _) =>
        {
            if (_rangeRadio.IsChecked == true)
                _rangeBox.Focus();
        };
    }

    private RongtaEraseMode CurrentMode => _allRadio.IsChecked == true ? RongtaEraseMode.All : _rangeRadio.IsChecked == true ? RongtaEraseMode.Range : RongtaEraseMode.Stale;

    /// <summary>Что выбрано сейчас: план, пустой/ошибочный список → null и текст в <paramref name="problem"/>.</summary>
    private RongtaErasePlan? BuildPlan(out string? problem)
    {
        problem = null;
        var mode = CurrentMode;
        IEnumerable<int> requested;
        switch (mode)
        {
            case RongtaEraseMode.Stale:
                requested = _ctx.Stale.Keys;
                break;
            case RongtaEraseMode.All:
                requested = RongtaSlotEraser.AllSlots();
                break;
            default:
                switch (RongtaSlotEraser.TryParseSlots(_rangeBox.Text, out var list))
                {
                    case RongtaSlotListProblem.Empty:
                        problem = T("Впишите номера PLU, например: 5, 10-20, 33.", "PLU номерлерин жазыңыз, мисалы: 5, 10-20, 33.", "Type the PLU numbers, for example: 5, 10-20, 33.", "PLU numaralarını yazın, örneğin: 5, 10-20, 33.", "PLU raqamlarini yozing, masalan: 5, 10-20, 33.");
                        return null;
                    case RongtaSlotListProblem.BadToken:
                        problem = T("Не понял список. Нужны номера и диапазоны через запятую: 5, 10-20, 33.", "Тизме түшүнүксүз. Номерлер жана диапазондор үтүр менен керек: 5, 10-20, 33.", "Could not read the list. Use numbers and ranges separated by commas: 5, 10-20, 33.", "Liste anlaşılamadı. Virgülle ayrılmış numaralar ve aralıklar gerekir: 5, 10-20, 33.", "Ro‘yxat tushunarsiz. Vergul bilan raqamlar va oraliqlar kerak: 5, 10-20, 33.");
                        return null;
                    case RongtaSlotListProblem.OutOfRange:
                        problem = T($"Номера PLU — от 1 до {RongtaSlotEraser.MaxSlot}.", $"PLU номерлери — 1ден {RongtaSlotEraser.MaxSlot}гө чейин.", $"PLU numbers are 1 to {RongtaSlotEraser.MaxSlot}.", $"PLU numaraları 1 ile {RongtaSlotEraser.MaxSlot} arasındadır.", $"PLU raqamlari — 1 dan {RongtaSlotEraser.MaxSlot} gacha.");
                        return null;
                }
                requested = list;
                break;
        }

        var protectedSlots = new HashSet<int>(_ctx.ProtectedTicked);
        // В «старых» ячейках и так нет живых товаров; в ручном списке и «всех» защита товаров, отправленных кассой, включается галочкой.
        if (mode == RongtaEraseMode.Stale || _keepSentCheck.IsChecked == true)
            protectedSlots.UnionWith(_ctx.ProtectedSent);
        return RongtaSlotEraser.Plan(requested, protectedSlots);
    }

    private void Refresh()
    {
        if (_ok is null || _previewText is null)
            return;
        var mode = CurrentMode;
        _rangeBox.IsEnabled = mode == RongtaEraseMode.Range;
        _keepSentCheck.IsVisible = mode != RongtaEraseMode.Stale;

        var plan = BuildPlan(out var problem);
        if (plan is null)
        {
            _previewText.Text = problem ?? "";
            _confirmRow.IsVisible = false;
            _ok.IsEnabled = false;
            _ok.Content = T("Стереть", "Өчүрүү", "Erase", "Sil", "O‘chirish");
            return;
        }

        var count = plan.Slots.Count;
        var lines = new List<string>();
        lines.Add(count == 0
            ? T("Стирать нечего.", "Өчүрүүгө эч нерсе жок.", "Nothing to erase.", "Silinecek bir şey yok.", "O‘chirishga hech narsa yo‘q.")
            : T($"Будет стёрто ячеек: {count} — PLU {RongtaSlotEraser.FormatSlots(plan.Slots)}.", $"Өчүрүлөт: {count} уяча — PLU {RongtaSlotEraser.FormatSlots(plan.Slots)}.",
                $"Slots to be erased: {count} — PLU {RongtaSlotEraser.FormatSlots(plan.Slots)}.", $"Silinecek hücre: {count} — PLU {RongtaSlotEraser.FormatSlots(plan.Slots)}.", $"O‘chiriladigan kataklar: {count} — PLU {RongtaSlotEraser.FormatSlots(plan.Slots)}."));
        var named = plan.Slots.Where(s => _ctx.Names.TryGetValue(s, out var n) && !string.IsNullOrWhiteSpace(n)).Take(15).ToList();
        foreach (var slot in named)
            lines.Add($"   PLU {slot} — «{_ctx.Names[slot]}»");
        var namedTotal = plan.Slots.Count(s => _ctx.Names.TryGetValue(s, out var n) && !string.IsNullOrWhiteSpace(n));
        if (namedTotal > named.Count)
            lines.Add($"   … +{namedTotal - named.Count}");
        if (plan.SkippedProtected.Count > 0)
            lines.Add(T($"Пропущено (товары в работе — отмеченные или отправленные кассой): {plan.SkippedProtected.Count} — PLU {RongtaSlotEraser.FormatSlots(plan.SkippedProtected, 15)}.",
                        $"Өткөрүлдү (иштеп жаткан товарлар — белгиленген же касса жөнөткөн): {plan.SkippedProtected.Count} — PLU {RongtaSlotEraser.FormatSlots(plan.SkippedProtected, 15)}.",
                        $"Skipped (goods in use — ticked or sent by the till): {plan.SkippedProtected.Count} — PLU {RongtaSlotEraser.FormatSlots(plan.SkippedProtected, 15)}.",
                        $"Atlandı (kullanımdaki ürünler — işaretli veya kasanın gönderdiği): {plan.SkippedProtected.Count} — PLU {RongtaSlotEraser.FormatSlots(plan.SkippedProtected, 15)}.",
                        $"O‘tkazib yuborildi (ishlatilayotgan tovarlar — belgilangan yoki kassa yuborgan): {plan.SkippedProtected.Count} — PLU {RongtaSlotEraser.FormatSlots(plan.SkippedProtected, 15)}."));
        _previewText.Text = string.Join(Environment.NewLine, lines);

        // «Все ячейки» (и любой большой список — от 100 ячеек): впечатать число ячеек.
        var hard = mode == RongtaEraseMode.All || count >= 100;
        _confirmRow.IsVisible = hard && count > 0;
        _confirmLabel.Text = T($"Для подтверждения впишите число ячеек, которые будут стёрты: {count}", $"Ырастоо үчүн өчүрүлө турган уячалардын санын жазыңыз: {count}",
            $"To confirm, type the number of slots that will be erased: {count}", $"Onaylamak için silinecek hücre sayısını yazın: {count}", $"Tasdiqlash uchun o‘chiriladigan kataklar sonini yozing: {count}");
        var confirmed = !hard || (_confirmBox.Text ?? "").Trim() == count.ToString(CultureInfo.InvariantCulture);
        _ok.IsEnabled = count > 0 && confirmed;
        _ok.Content = count > 0
            ? T($"Стереть ячейки: {count}", $"Уячаларды өчүрүү: {count}", $"Erase slots: {count}", $"Hücreleri sil: {count}", $"Kataklarni o‘chirish: {count}")
            : T("Стереть", "Өчүрүү", "Erase", "Sil", "O‘chirish");
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var plan = BuildPlan(out _);
        if (plan is null || plan.Slots.Count == 0 || !_ok.IsEnabled)
            return;
        Plan = plan;
        Mode = CurrentMode;
        Close(true);
    }
}

/// <summary>Исходные данные окна «Стереть старые ячейки»: что считать старым, чьи ячейки защищены, как называются товары в ячейках.</summary>
internal sealed record RongtaEraseContext(
    string ScaleHost,
    IReadOnlyDictionary<int, string> Stale,
    IReadOnlySet<int> ProtectedTicked,
    IReadOnlySet<int> ProtectedSent,
    IReadOnlyDictionary<int, string> Names);
