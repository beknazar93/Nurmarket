using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>2026-10-17, редизайн (этап 4, макет MCancelled): «Убрать из чека?» — причина удаления строки для журнала
/// «Удалено из чека». Показывается только при включённой настройке (Настройки → Операции → «Контроль удалений»)
/// и сумме строки от порога. «Оставить» (Esc, ✕) — строка остаётся; «Убрать» (Enter) — <see cref="Answer"/>.</summary>
public partial class CartRemovalReasonDialog : Window
{
    /// <summary>Коды причин в журнале (CartRemovalRecord.Reason) — названия переводит <see cref="ReasonTitle"/>.</summary>
    public const string ReasonChangedMind = "changed_mind";
    public const string ReasonCashierError = "cashier_error";
    public const string ReasonDefect = "defect";
    public const string ReasonOther = "other";

    private static readonly string[] ReasonCodes = { ReasonChangedMind, ReasonCashierError, ReasonDefect, ReasonOther };

    public CartRemovalReasonDialog() : this(new CartRemovalReasonRequest("—", 1, "", 0, false)) { }

    public CartRemovalReasonDialog(CartRemovalReasonRequest request)
    {
        InitializeComponent();

        var title = Tr.T("Убрать из чека?", "Чектен алып салуу керекпи?", "Remove from the receipt?", "Fişten çıkarılsın mı?", "Chekdan olib tashlansinmi?");
        Title = title;
        Shell.Title = title;
        Shell.Subtitle = $"{request.Title} · {FormatQuantity(request.Quantity)} {request.Unit} · {FormatMoney(request.LineTotal)}".Replace(" ·  ·", " ·");
        // Неразрывные пробелы: строка переносится только у «·», а не «Esc —» / «оставить».
        Shell.Hint = Tr.T("Enter — убрать · Esc — оставить", "Enter — алып салуу · Esc — калтыруу",
            "Enter — remove · Esc — keep", "Enter — çıkar · Esc — bırak", "Enter — olib tashlash · Esc — qoldirish");

        ReasonLabel.Text = Tr.T("ПРИЧИНА (КЛАВИШИ 1–4) — ПОПАДЁТ В ЖУРНАЛ «УДАЛЕНО ИЗ ЧЕКА»",
            "СЕБЕБИ (1–4 БАСКЫЧТАРЫ) — «ЧЕКТЕН АЛЫНДЫ» ЖУРНАЛЫНА ЖАЗЫЛАТ",
            "REASON (KEYS 1–4) — GOES TO THE “REMOVED FROM RECEIPT” LOG",
            "NEDEN (1–4 TUŞLARI) — «FİŞTEN ÇIKARILANLAR» GÜNLÜĞÜNE YAZILIR",
            "SABAB (1–4 TUGMALARI) — «CHEKDAN OLIB TASHLANGAN» JURNALIGA YOZILADI");
        Reason1.Content = ReasonTitle(ReasonChangedMind);
        Reason2.Content = ReasonTitle(ReasonCashierError);
        Reason3.Content = ReasonTitle(ReasonDefect);
        Reason4.Content = ReasonTitle(ReasonOther);
        NoteBox.Watermark = Tr.T("Комментарий (для «Другое» — обязательно)", "Комментарий («Башка» үчүн — милдеттүү)",
            "Comment (required for “Other”)", "Yorum («Diğer» için zorunlu)", "Izoh («Boshqa» uchun — majburiy)");

        if (request.CanRemoveOne)
        {
            AmountPanel.IsVisible = true;
            AmountLabel.Text = Tr.T("СКОЛЬКО УБРАТЬ", "КАНЧАНЫ АЛЫП САЛУУ", "HOW MUCH TO REMOVE", "NE KADAR ÇIKARILSIN", "QANCHASINI OLIB TASHLASH");
            var qty = FormatQuantity(request.Quantity);
            WholeLineRadio.Content = Tr.T($"Всю строку · {qty} {request.Unit}", $"Бүт сап · {qty} {request.Unit}",
                $"Whole line · {qty} {request.Unit}", $"Tüm satır · {qty} {request.Unit}", $"Butun qator · {qty} {request.Unit}");
            OnlyOneRadio.Content = Tr.T($"Только 1 {request.Unit}", $"1 {request.Unit} гана", $"Only 1 {request.Unit}",
                $"Yalnızca 1 {request.Unit}", $"Faqat 1 {request.Unit}");
        }

        KeepButton.Content = Tr.T("Оставить", "Калтыруу", "Keep", "Bırak", "Qoldirish");
        RemoveButton.Content = Tr.T("Убрать", "Алып салуу", "Remove", "Çıkar", "Olib tashlash");

        // Цифры 1–4 выбирают причину, пока курсор не в поле комментария (туда цифры пишутся как текст).
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    /// <summary>Ответ после «Убрать».</summary>
    public CartRemovalReasonAnswer? Answer { get; private set; }

    /// <summary>Название причины по коду журнала — на языке интерфейса (окно журнала тоже им пользуется).</summary>
    public static string ReasonTitle(string? code) => code switch
    {
        ReasonChangedMind => Tr.T("Покупатель передумал", "Сатып алуучу оюн өзгөрттү", "Customer changed mind", "Müşteri vazgeçti", "Xaridor fikridan qaytdi"),
        ReasonCashierError => Tr.T("Ошибка кассира / скана", "Кассирдин / скандын катасы", "Cashier / scan error", "Kasiyer / okutma hatası", "Kassir / skaner xatosi"),
        ReasonDefect => Tr.T("Брак или нет в наличии", "Бузук же жок", "Defective or out of stock", "Kusurlu veya stokta yok", "Nuqsonli yoki mavjud emas"),
        ReasonOther => Tr.T("Другое", "Башка", "Other", "Diğer", "Boshqa"),
        _ => "",
    };

    private string? SelectedReason()
    {
        RadioButton[] radios = { Reason1, Reason2, Reason3, Reason4 };
        for (var i = 0; i < radios.Length; i++)
            if (radios[i].IsChecked == true)
                return ReasonCodes[i];
        return null;
    }

    // 2026-10-17, ревью этапа 4: сканер «печатает» в открытое окно (штрихкод 470…/46…/2…, затем Enter) — цифры
    // выбирали причину/«Другое», а Enter сканера сам нажимал «Убрать». Клавиши чаще 40 мс подряд — это сканер:
    // такой Enter глотаем и возвращаем причину и комментарий, какими они были до начала пачки.
    private const long ScannerKeyGapMs = 40;
    private long _lastKeyAt;
    private int _fastKeys;
    private int _burstReason = -1;
    private string? _burstNote;

    private int SelectedReasonIndex()
    {
        RadioButton[] radios = { Reason1, Reason2, Reason3, Reason4 };
        return Array.FindIndex(radios, r => r.IsChecked == true);
    }

    private bool TrackScannerBurst(KeyEventArgs e)
    {
        var now = Environment.TickCount64;
        var fast = _lastKeyAt != 0 && now - _lastKeyAt < ScannerKeyGapMs;
        _lastKeyAt = now;
        if (!fast)
        {
            _fastKeys = 0;
            _burstReason = SelectedReasonIndex();
            _burstNote = NoteBox.Text;
            return false;
        }
        _fastKeys++;
        if (e.Key is not (Key.Enter or Key.Return) || _fastKeys < 3)
            return false;
        RadioButton[] radios = { Reason1, Reason2, Reason3, Reason4 };
        for (var i = 0; i < radios.Length; i++)
            radios[i].IsChecked = i == _burstReason;
        NoteBox.Text = _burstNote;
        RemoveButton.Focus();
        _fastKeys = 0;
        return true;
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (TrackScannerBurst(e))
        {
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None || NoteBox.IsFocused)
            return;
        RadioButton? target = e.Key switch
        {
            Key.D1 or Key.NumPad1 => Reason1,
            Key.D2 or Key.NumPad2 => Reason2,
            Key.D3 or Key.NumPad3 => Reason3,
            Key.D4 or Key.NumPad4 => Reason4,
            _ => null,
        };
        if (target is null)
            return;
        target.IsChecked = true;
        e.Handled = true;
    }

    private void Reason_Changed(object? sender, RoutedEventArgs e)
    {
        ErrorText.IsVisible = false;
        // «Другое» — сразу в поле комментария: причину пишут словами.
        if (sender == Reason4 && Reason4.IsChecked == true)
            NoteBox.Focus();
    }

    private void Keep_Click(object? sender, RoutedEventArgs e) => Close(false);

    private void Remove_Click(object? sender, RoutedEventArgs e)
    {
        var reason = SelectedReason();
        var note = (NoteBox.Text ?? "").Trim();
        if (reason is null && note.Length == 0)
        {
            ErrorText.Text = Tr.T("Выберите причину или напишите комментарий.", "Себепти тандаңыз же комментарий жазыңыз.",
                "Choose a reason or write a comment.", "Bir neden seçin veya yorum yazın.", "Sababni tanlang yoki izoh yozing.");
            ErrorText.IsVisible = true;
            return;
        }
        if (reason == ReasonOther && note.Length == 0)
        {
            ErrorText.Text = Tr.T("Для «Другое» напишите причину в комментарии.", "«Башка» үчүн себебин комментарийге жазыңыз.",
                "For “Other”, write the reason in the comment.", "«Diğer» için nedeni yoruma yazın.", "«Boshqa» uchun sababni izohga yozing.");
            ErrorText.IsVisible = true;
            NoteBox.Focus();
            return;
        }

        Answer = new CartRemovalReasonAnswer(reason ?? ReasonOther, note.Length == 0 ? null : note,
            AmountPanel.IsVisible && OnlyOneRadio.IsChecked == true);
        Close(true);
    }

    private static string FormatQuantity(double quantity) =>
        quantity.ToString(Math.Abs(quantity - Math.Round(quantity)) < 1e-9 ? "0" : "0.###", CultureInfo.InvariantCulture);

    // 2026-10-20 (живой аудит кассы, К18): «243 615,05 сом» (MoneyText) вместо «243 615.05 сом» с точкой.
    internal static string FormatMoney(double amount) => MoneyText.Som(amount);
}
