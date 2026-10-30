using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

// 2026-10-22 (закупки как в вебе): окна «Закупок» — «Подтверждение оплаты», «Оприходование завершено», «Позиции прихода», заказ поставщику.
// Все — в общей оболочке ModalShell (заголовок и ✕ сверху, кнопки снизу не прокручиваются), одна жёлтая кнопка, зоны нажатия ≥ 44 px.

/// <summary>«Подтверждение оплаты» сайта: поставщик, позиций, сумма накладной; «Наличные (полная оплата)» / «Предоплата» / «В долг поставщику»;
/// галочка «Я заплатил из кассы (ящика) смены» (касса с открытой сменой); срок долга; «К списанию из кассы: …» → PurchasePayment.</summary>
public sealed class PurchasePaymentDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    private readonly double _total;
    private readonly StackPanel _kinds = new() { Spacing = 6 };
    private readonly StackPanel _partialPanel = new() { Spacing = 6 };
    private readonly StackPanel _termPanel = new() { Spacing = 6 };
    private readonly StackPanel _termSegs = new() { Orientation = Orientation.Horizontal };
    private readonly TextBox _paid;
    private readonly TextBox _days;
    private readonly TextBlock _due = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _drawer = new() { MinHeight = 44, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _cashHint = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _cashbox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 44 };
    private readonly StackPanel _cashboxPanel = new() { Spacing = 4, IsVisible = false };
    private readonly TextBlock _summary = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private List<SalaryPayoutService.PayoutCashbox> _cashboxes = new();
    private PurchasePayKind _kind = PurchasePayKind.Cash;
    private int _termDays = 30;

    public PurchasePayment? Result { get; private set; }

    public PurchasePaymentDialog(string supplier, int positions, double total)
    {
        _total = Math.Round(total, 2);
        Title = T("Подтверждение оплаты", "Төлөмдү ырастоо", "Payment confirmation", "Ödeme onayı", "To'lovni tasdiqlash");
        SectionCards.AsModal(this, 600);
        var panel = new StackPanel { Spacing = 8 };

        // Поставщик — строкой (в плитке длинное название уменьшалось до нечитаемого), позиции и сумма — плитками.
        var who = new TextBlock { Text = supplier, FontSize = 16, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        Use(who, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(who);
        var tiles = new NurMarketKassa.AvaloniaHost.Views.Controls.KpiTileGrid { MinTileWidth = 150, Spacing = 8 };
        tiles.Children.Add(UiKit.Kpi(T("Позиций", "Позиция", "Items", "Kalem", "Pozitsiya"), positions.ToString(CultureInfo.InvariantCulture)));
        tiles.Children.Add(UiKit.Kpi(T("Сумма накладной", "Накладнойдун суммасы", "Invoice total", "Fatura toplamı", "Nakladnoy summasi"), MoneyText.Som(_total)));
        panel.Children.Add(tiles);

        panel.Children.Add(Label(T("Тип оплаты", "Төлөм түрү", "Payment type", "Ödeme türü", "To'lov turi")));
        panel.Children.Add(_kinds);

        // Ящик смены — только касса с открытой сменой, как галочка сайта.
        _drawer.Content = new TextBlock
        {
            Text = T($"Я заплатил из кассы (ящика) смены — спишется с кассы «{PosApp.PosCashboxDisplayName ?? T("касса", "касса", "till", "kasa", "kassa")}»",
                $"Мен сменанын кассасынан төлөдүм — «{PosApp.PosCashboxDisplayName ?? "касса"}» кассасынан чыгат",
                $"I paid from the shift drawer — it will be taken from the “{PosApp.PosCashboxDisplayName ?? "till"}” till",
                $"Vardiya kasasından ödedim — «{PosApp.PosCashboxDisplayName ?? "kasa"}» kasasından düşülecek",
                $"Smena kassasidan to'ladim — «{PosApp.PosCashboxDisplayName ?? "kassa"}» kassasidan chiqariladi"),
            TextWrapping = TextWrapping.Wrap, FontSize = 13.5,
        };
        Avalonia.Automation.AutomationProperties.SetName(_drawer, T("Я заплатил из ящика смены", "Мен сменанын кассасынан төлөдүм", "I paid from the shift drawer", "Vardiya kasasından ödedim", "Smena kassasidan to'ladim"));
        _drawer.IsVisible = PurchasePaymentService.DrawerAvailable;
        _drawer.IsCheckedChanged += (_, _) => Update();
        panel.Children.Add(_drawer);
        Use(_cashHint, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(_cashHint);

        _paid = Field(T("Сумма предоплаты", "Алдын ала төлөм суммасы", "Prepayment amount", "Ön ödeme tutarı", "Oldindan to'lov summasi"));
        _paid.TextChanged += (_, _) => Update();
        _partialPanel.Children.Add(Label(T("Сколько заплатили сейчас", "Азыр канча төлөндү", "Paid now", "Şimdi ödenen", "Hozir to'langan")));
        _partialPanel.Children.Add(_paid);
        panel.Children.Add(_partialPanel);
        _cashboxPanel.Children.Add(Label(T("Из какой кассы", "Кайсы кассадан", "From which till", "Hangi kasadan", "Qaysi kassadan")));
        _cashboxPanel.Children.Add(_cashbox);
        Avalonia.Automation.AutomationProperties.SetName(_cashbox, T("Касса", "Касса", "Till", "Kasa", "Kassa"));
        panel.Children.Add(_cashboxPanel);

        _days = Field(T("дней", "күн", "days", "gün", "kun"));
        _days.Width = 90;
        _days.Text = _termDays.ToString(CultureInfo.InvariantCulture);
        _days.TextChanged += (_, _) =>
        {
            if (int.TryParse((_days.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d is >= 1 and <= 3650)
                _termDays = d;
            BuildTerm();
            Update();
        };
        _termPanel.Children.Add(Label(T("Срок оплаты долга", "Карызды төлөө мөөнөтү", "Debt due in", "Borç vadesi", "Qarz to'lov muddati")));
        var termRow = new WrapPanel { Orientation = Orientation.Horizontal };
        var segBox = UiKit.Segment(_termSegs);
        segBox.Margin = new Thickness(0, 0, 10, 6);
        termRow.Children.Add(segBox);
        _days.Margin = new Thickness(0, 0, 8, 6);
        termRow.Children.Add(_days);
        _due.Margin = new Thickness(0, 0, 0, 6);
        Use(_due, TextBlock.ForegroundProperty, "BrushTextSoft");
        termRow.Children.Add(_due);
        _termPanel.Children.Add(termRow);
        panel.Children.Add(_termPanel);

        var errorBox = SectionCards.ErrorBox(this, _error);
        errorBox.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(errorBox);

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        var ok = SectionCards.Primary(T("Подтвердить и оприходовать", "Ырастап, кирешке алуу", "Confirm and post", "Onayla ve kaydet", "Tasdiqlash va kirim qilish"));
        ok.Click += (_, _) => Confirm();
        Use(_summary, TextBlock.ForegroundProperty, "BrushText");
        Content = SectionCards.Shell(Title, T("Как оплачен этот приход поставщику — так и запишутся деньги и долг.", "Бул кириш жеткирүүчүгө кантип төлөнгөн — акча жана карыз ошондой жазылат.",
                "How this delivery was paid — money and debt are recorded accordingly.", "Bu giriş nasıl ödendiyse para ve borç öyle kaydedilir.",
                "Bu kirim qanday to'langan bo'lsa — pul va qarz shunday yoziladi."),
            panel, SectionCards.Buttons(cancel, ok), _summary);
        BuildKinds();
        BuildTerm();
        Update();
        Opened += async (_, _) => await LoadCashboxesAsync().ConfigureAwait(true);
    }

    private TextBox Field(string watermark)
    {
        var t = new TextBox { Watermark = watermark, Height = 44, MinHeight = 44, FontSize = 15, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(10),
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Right };
        t.Classes.Add("num");
        Avalonia.Automation.AutomationProperties.SetName(t, watermark);
        return t;
    }

    private TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) };
        Use(t, TextBlock.ForegroundProperty, "BrushTextSoft");
        return t;
    }

    private void BuildKinds()
    {
        _kinds.Children.Clear();
        foreach (var (kind, text) in new[]
                 {
                     (PurchasePayKind.Cash, T("Наличные — полная оплата", "Накталай — толук төлөм", "Cash — paid in full", "Nakit — tam ödeme", "Naqd — to'liq to'lov")),
                     (PurchasePayKind.Partial, T("Предоплата — часть сейчас, остальное в долг", "Алдын ала төлөм — бир бөлүгү азыр, калганы карызга", "Prepayment — part now, the rest on credit", "Ön ödeme — bir kısmı şimdi, kalanı veresiye", "Oldindan to'lov — bir qismi hozir, qolgani qarzga")),
                     (PurchasePayKind.Debt, T("В долг поставщику", "Жеткирүүчүгө карызга", "On credit from the supplier", "Tedarikçiye veresiye", "Yetkazib beruvchiga qarzga")),
                 })
        {
            var chip = UiKit.Chip(this, text, kind == _kind || (kind == PurchasePayKind.Cash && _kind == PurchasePayKind.Drawer));
            chip.MinHeight = 44;
            chip.HorizontalAlignment = HorizontalAlignment.Stretch;
            chip.HorizontalContentAlignment = HorizontalAlignment.Left;
            chip.CornerRadius = new CornerRadius(10);
            Avalonia.Automation.AutomationProperties.SetName(chip, text);
            chip.Click += (_, _) =>
            {
                _kind = kind;
                BuildKinds();
                Update();
            };
            _kinds.Children.Add(chip);
        }
    }

    private void BuildTerm()
    {
        _termSegs.Children.Clear();
        foreach (var d in new[] { 7, 14, 30, 60 })
        {
            var seg = UiKit.Seg(T($"{d} дн.", $"{d} күн", $"{d} d", $"{d} gün", $"{d} kun"), d == _termDays);
            seg.MinHeight = 44;
            seg.Click += (_, _) =>
            {
                _termDays = d;
                _days.Text = d.ToString(CultureInfo.InvariantCulture);
            };
            _termSegs.Children.Add(seg);
        }
        var due = DateTime.Today.AddDays(_termDays);
        _due.Text = T($"оплатить до {due:dd.MM.yyyy}", $"{due:dd.MM.yyyy} чейин төлөө", $"pay by {due:dd.MM.yyyy}", $"son ödeme {due:dd.MM.yyyy}", $"{due:dd.MM.yyyy} gacha to'lash");
    }

    private bool FromDrawer => _drawer.IsVisible && _drawer.IsChecked == true;

    private double? PaidValue =>
        double.TryParse((_paid.Text ?? "").Trim().Replace(" ", "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Round(v, 2) : null;

    private void Update()
    {
        var cash = _kind is PurchasePayKind.Cash or PurchasePayKind.Drawer;
        var partial = _kind == PurchasePayKind.Partial;
        _partialPanel.IsVisible = partial;
        _termPanel.IsVisible = _kind is PurchasePayKind.Partial or PurchasePayKind.Debt;
        _drawer.IsVisible = PurchasePaymentService.DrawerAvailable && (cash || partial);
        _cashboxPanel.IsVisible = partial && !FromDrawer && _cashboxes.Count > 0;
        _cashHint.IsVisible = true;
        _cashHint.Text = _kind switch
        {
            PurchasePayKind.Debt => T("Деньги сейчас не списываются. Долг поставщику запишется в NurCRM (как на сайте) — он виден в «Поставщиках», оплатить можно там же.",
                "Акча азыр чыкпайт. Жеткирүүчүгө карыз NurCRM'ге жазылат (сайттагыдай) — «Жеткирүүчүлөрдө» көрүнөт, ошол жерден төлөсө болот.",
                "No money is taken now. The debt to the supplier is recorded in NurCRM (as on the website) — it shows in “Suppliers” and can be paid there.",
                "Şimdi para düşülmez. Tedarikçi borcu NurCRM'e kaydedilir (sitedeki gibi) — «Tedarikçiler»de görünür, oradan ödenebilir.",
                "Hozir pul chiqarilmaydi. Yetkazib beruvchiga qarz NurCRM'ga yoziladi (saytdagidek) — «Yetkazib beruvchilar»da ko'rinadi."),
            PurchasePayKind.Partial when FromDrawer => T("Оплаченная часть — изъятием из ящика смены (войдёт в отчёт смены), остаток — долгом поставщику.",
                "Төлөнгөн бөлүгү — сменанын кассасынан алуу менен (смена отчётуна кирет), калганы — жеткирүүчүгө карыз.",
                "The paid part is a cash-out from the shift drawer (it goes into the shift report), the rest is a debt to the supplier.",
                "Ödenen kısım vardiya kasasından çıkış olarak yazılır (vardiya raporuna girer), kalanı tedarikçi borcu olur.",
                "To'langan qismi smena kassasidan chiqim bo'ladi (smena hisobotiga kiradi), qolgani — yetkazib beruvchiga qarz."),
            PurchasePayKind.Partial => T("Оплаченная часть — расходом из выбранной кассы, остаток — долгом поставщику со сроком.",
                "Төлөнгөн бөлүгү — тандалган кассадан чыгым, калганы — мөөнөтү менен жеткирүүчүгө карыз.",
                "The paid part is an expense from the chosen till, the rest is a debt to the supplier with a due date.",
                "Ödenen kısım seçilen kasadan gider, kalanı vadeli tedarikçi borcu olur.",
                "To'langan qismi tanlangan kassadan xarajat, qolgani — muddatli qarz."),
            _ when FromDrawer => T("Сервер запишет расход на этот приход в кассу смены; после проведения проверим, что он попал в смену.",
                "Сервер бул кириштин чыгымын сменанын кассасына жазат; өткөргөндөн кийин сменага түшкөнүн текшеребиз.",
                "The server records the expense for this delivery in the shift till; after posting we check it landed in the shift.",
                "Sunucu bu girişin giderini vardiya kasasına yazar; kayıttan sonra vardiyaya düştüğünü kontrol ederiz.",
                "Server bu kirim xarajatini smena kassasiga yozadi; o'tkazgandan keyin smenaga tushganini tekshiramiz."),
            // 2026-10-23 (аудит Б-7): сервер пишет расход в «Основную кассу компании», а не в отдельную «кассу расходов» — текст по факту.
            _ => T("Деньги спишутся из «Основной кассы компании» — расход запишет сервер NurCRM, как при закупке на сайте.",
                "Акча «Компаниянын негизги кассасынан» чыгат — чыгымды NurCRM сервери жазат, сайттагы сатып алуудагыдай.",
                "The money is taken from the company's main till — the NurCRM server records the expense, as with a purchase on the website.",
                "Para şirketin ana kasasından düşülür — gideri NurCRM sunucusu kaydeder, sitedeki alım gibi.",
                "Pul kompaniyaning asosiy kassasidan chiqariladi — xarajatni NurCRM serveri yozadi, saytdagi xariddagidek."),
        };
        var paid = _kind switch
        {
            PurchasePayKind.Debt => 0,
            PurchasePayKind.Partial => Math.Clamp(PaidValue ?? 0, 0, _total),
            _ => _total,
        };
        var debt = Math.Round(_total - paid, 2);
        _summary.Text = _kind == PurchasePayKind.Debt
            ? T($"Долг поставщику: {MoneyText.Som(_total)}", $"Жеткирүүчүгө карыз: {MoneyText.Som(_total)}", $"Debt to supplier: {MoneyText.Som(_total)}", $"Tedarikçi borcu: {MoneyText.Som(_total)}", $"Qarz: {MoneyText.Som(_total)}")
            : T($"К списанию из кассы: {MoneyText.Som(paid)}", $"Кассадан чыгат: {MoneyText.Som(paid)}", $"Taken from the till: {MoneyText.Som(paid)}", $"Kasadan düşülecek: {MoneyText.Som(paid)}", $"Kassadan chiqadi: {MoneyText.Som(paid)}")
              + (debt > 0.004 ? T($" · остаток в долг: {MoneyText.Som(debt)}", $" · калганы карызга: {MoneyText.Som(debt)}", $" · rest on credit: {MoneyText.Som(debt)}", $" · kalan veresiye: {MoneyText.Som(debt)}", $" · qolgani qarzga: {MoneyText.Som(debt)}") : "");
        _error.IsVisible = false;
    }

    private async Task LoadCashboxesAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _cashboxes = await SalaryPayoutService.LoadCashboxesAsync(cts.Token).ConfigureAwait(true);
            _cashbox.ItemsSource = _cashboxes.Select(c => c.Name).ToList();
            _cashbox.SelectedIndex = _cashboxes.Count == 0 ? -1 : Math.Max(0, SalaryPayoutService.DefaultCashboxIndex(_cashboxes));
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Закупки: кассы компании не получены ({ex.Message}).", "WARNING");
        }
        Update();
    }

    private void Confirm()
    {
        string? problem = null;
        var paid = 0d;
        if (_kind == PurchasePayKind.Partial)
        {
            paid = PaidValue ?? 0;
            if (paid <= 0)
                problem = T("Укажите сумму предоплаты.", "Алдын ала төлөм суммасын жазыңыз.", "Enter the prepayment amount.", "Ön ödeme tutarını girin.", "Oldindan to'lov summasini kiriting.");
            else if (paid >= _total)
                problem = T("Предоплата не может быть больше или равна сумме накладной — выберите «Наличные — полная оплата».",
                    "Алдын ала төлөм накладнойдун суммасынан чоң же барабар боло албайт — «Накталай — толук төлөмдү» тандаңыз.",
                    "The prepayment can't be equal to or above the invoice total — choose “Cash — paid in full”.",
                    "Ön ödeme fatura toplamına eşit veya büyük olamaz — «Nakit — tam ödeme»yi seçin.",
                    "Oldindan to'lov nakladnoy summasidan katta yoki teng bo'lolmaydi — «Naqd — to'liq to'lov»ni tanlang.");
            else if (FromDrawer && CashWithdrawalGuard.Refusal((decimal)paid) is { } refusal)
                problem = refusal;
            else if (!FromDrawer && (_cashbox.SelectedIndex < 0 || _cashbox.SelectedIndex >= _cashboxes.Count))
                problem = T("Выберите кассу, из которой заплатили.", "Кайсы кассадан төлөнгөнүн тандаңыз.", "Choose the till you paid from.", "Ödediğiniz kasayı seçin.", "Qaysi kassadan to'langanini tanlang.");
        }
        if (problem is null && _kind is PurchasePayKind.Partial or PurchasePayKind.Debt && _termDays is < 1 or > 3650)
            problem = T("Срок оплаты — от 1 до 3650 дней.", "Төлөө мөөнөтү — 1ден 3650 күнгө чейин.", "The due period is 1 to 3650 days.", "Vade 1–3650 gün.", "Muddat — 1 dan 3650 kungacha.");
        if (problem is not null)
        {
            _error.Text = problem;
            _error.IsVisible = true;
            return;
        }
        var kind = _kind == PurchasePayKind.Cash && FromDrawer ? PurchasePayKind.Drawer : _kind;
        var box = _kind == PurchasePayKind.Partial && !FromDrawer && _cashbox.SelectedIndex >= 0 && _cashbox.SelectedIndex < _cashboxes.Count ? _cashboxes[_cashbox.SelectedIndex] : null;
        Result = new PurchasePayment(kind, paid, _termDays, FromDrawer, box?.Id, box?.Name);
        Close();
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>«Оприходование завершено» сайта: что принято, как оплачено (долг/касса), предупреждения; «Скачать накладную» (PDF, жёлтая),
/// «Печать», «Закрыть».</summary>
public sealed class PurchaseDoneDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public PurchaseDoneDialog(SupplierInfo supplier, PurchaseOutcome outcome, IReadOnlyList<PurchaseDocs.DocLine> lines)
    {
        Title = outcome.Warning
            ? T("Оприходовано, но есть замечания", "Кирешке алынды, бирок эскертүүлөр бар", "Posted, with remarks", "Kaydedildi, notlar var", "Kirim qilindi, eslatmalar bor")
            : T("Оприходование завершено", "Кирешке алуу аяктады", "Posting completed", "Kayıt tamamlandı", "Kirim yakunlandi");
        SectionCards.AsModal(this, 600);
        var total = outcome.Result.ReceiptTotal is > 0 and var t ? t : outcome.Result.TotalAmount;
        var panel = new StackPanel { Spacing = 10 };
        var head = new TextBlock
        {
            Text = (outcome.Warning ? "⚠ " : "✓ ") + T($"Товары оприходованы у поставщика «{supplier.Name}». Сумма накладной: {MoneyText.Som(total)}.",
                $"Товарлар «{supplier.Name}» жеткирүүчүдөн кирешке алынды. Накладнойдун суммасы: {MoneyText.Som(total)}.",
                $"Goods posted from supplier “{supplier.Name}”. Invoice total: {MoneyText.Som(total)}.",
                $"Ürünler «{supplier.Name}» tedarikçisinden kaydedildi. Fatura toplamı: {MoneyText.Som(total)}.",
                $"Tovarlar «{supplier.Name}» dan kirim qilindi. Nakladnoy summasi: {MoneyText.Som(total)}."),
            FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        Use(head, TextBlock.ForegroundProperty, "BrushText");
        var headBox = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10), Child = head };
        Use(headBox, Border.BackgroundProperty, outcome.Warning ? "BrushWarningSoft" : "BrushSuccessSoft");
        panel.Children.Add(headBox);
        var summary = new TextBlock { Text = outcome.Summary, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
        Use(summary, TextBlock.ForegroundProperty, "BrushText");
        panel.Children.Add(summary);
        if (outcome.Result.Errors.Count > 0)
        {
            var errors = new TextBlock { Text = string.Join("\n", outcome.Result.Errors.Take(8)), FontSize = 13, TextWrapping = TextWrapping.Wrap };
            panel.Children.Add(SectionCards.ErrorBox(this, errors));
        }
        var message = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(message, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(message);

        // 2026-10-30 (нумерация закупок): номер документа, присвоенный при отправке прихода (ПР-2026-000123 или временный ПР-2026-ч5 без связи).
        var reserved = outcome.Result.ReceiptKey is { Length: > 0 } rk ? DocumentNumbering.Store.FindReservation(DocSeries.Receipt, rk) : null;
        var number = reserved is not null ? DocumentNumbering.Store.Format(reserved)
            : outcome.Result.ReceiptId is { Length: >= 8 } id ? DocumentNumbering.ReceiptText(id) : DateTime.Now.ToString("yyMMdd-HHmm", CultureInfo.InvariantCulture);
        if (reserved is not null)
        {
            var numberText = new TextBlock
            {
                FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap,
                Text = T("Номер прихода: ", "Кирүүнүн номери: ", "Delivery number: ", "Giriş numarası: ", "Kirim raqami: ") + number
                       + (reserved.Draft ? T(" (временный — станет постоянным после сверки с сервером)", " (убактылуу — сервер менен тактоодон кийин туруктуу болот)", " (temporary — becomes permanent after syncing with the server)",
                           " (geçici — sunucuyla eşitlenince kalıcı olur)", " (vaqtincha — server bilan solishtirilgach doimiy bo'ladi)") : ""),
            };
            Use(numberText, TextBlock.ForegroundProperty, "BrushText");
            panel.Children.Add(numberText);
        }
        var date = outcome.Result.ReceiptAt ?? DateTime.Now;
        var party = new PurchaseDocs.Party(supplier.Name, supplier.Phone, supplier.Inn, supplier.Address);
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var print = SectionCards.Cancel(T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"));
        print.IsCancel = false;
        print.Click += async (_, _) =>
        {
            print.IsEnabled = false;
            var error = await PurchaseDocs.PrintAsync(PurchaseDocs.Build(this, "receipt", party, number, date, lines, null), "Накладная " + number).ConfigureAwait(true);
            message.Text = error ?? T("Отправлено на принтер.", "Принтерге жөнөтүлдү.", "Sent to the printer.", "Yazıcıya gönderildi.", "Printerga yuborildi.");
            message.IsVisible = true;
            print.IsEnabled = true;
        };
        var pdf = SectionCards.Primary(T("Скачать накладную", "Накладнойду жүктөө", "Download invoice", "Faturayı indir", "Nakladnoyni yuklab olish"));
        pdf.Click += async (_, _) =>
        {
            pdf.IsEnabled = false;
            var (path, error) = await PurchaseDocs.SavePdfAsync(PurchaseDocs.Build(this, "receipt", party, number, date, lines, null),
                PurchaseDocs.FileName("receipt", supplier.Name, number, date)).ConfigureAwait(true);
            message.Text = path is not null
                ? T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: ") + path
                : T("PDF не сохранён: ", "PDF сакталган жок: ", "PDF not saved: ", "PDF kaydedilemedi: ", "PDF saqlanmadi: ") + error;
            message.IsVisible = true;
            pdf.IsEnabled = true;
        };
        Content = SectionCards.Shell(Title, null, panel, SectionCards.Buttons(close, print, pdf));
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>2026-10-30: «Позиции прихода» открыты из карточки товара («Поступления»): выделить строку товара, показать «Открыть файл» накладной (или честно —
/// что файл не сохранён), «Вернуть»/«Повторить» не показывать (они принадлежат окну «Закупки»).</summary>
public sealed record PurchaseReceiptContext(string HighlightProductId, string? InvoiceFilePath);

/// <summary>«Позиции прихода» сайта + наше: оплата прихода, возвращено, «Вернуть поставщику», «Повторить закупку», накладная PDF/печать.</summary>
public sealed class PurchaseReceiptDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>return — открыть возврат по приходу, repeat — новый приём с теми же товарами.</summary>
    public string? Action { get; private set; }

    public PurchaseReceiptDialog(SupplierReceipt r, PurchaseReceiptNote? note, bool debtRecorded, SupplierInfo? supplier, PurchaseReceiptContext? context = null)
    {
        // 2026-10-30 (нумерация закупок): в заголовке — номер документа (ПР-2026-000123; пока история не сверена — короткий код).
        var docNumber = DocumentNumbering.ReceiptText(r.Id);
        Title = T($"Приход {docNumber} · Позиции", $"Кирүү {docNumber} · Позициялар", $"Delivery {docNumber} · Items", $"Giriş {docNumber} · Kalemler", $"Kirim {docNumber} · Pozitsiyalar");
        SectionCards.AsModal(this, 760);
        var panel = new StackPanel { Spacing = 8 };
        void Line(string label, string value)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
            var a = new TextBlock { Text = label, FontSize = 13 };
            Use(a, TextBlock.ForegroundProperty, "BrushTextSoft");
            var b = new TextBlock { Text = value, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
            Use(b, TextBlock.ForegroundProperty, "BrushText");
            Grid.SetColumn(b, 1);
            row.Children.Add(a);
            row.Children.Add(b);
            panel.Children.Add(row);
        }
        // Номер документа: нажатие копирует его; рядом — номер накладной поставщика (отдельно).
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*") };
            var a = new TextBlock { Text = T("Номер", "Номер", "Number", "Numara", "Raqam"), FontSize = 13 };
            Use(a, TextBlock.ForegroundProperty, "BrushTextSoft");
            var b = new TextBlock { Text = docNumber, FontSize = 14, FontWeight = FontWeight.SemiBold, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            b.Classes.Add("num");
            Use(b, TextBlock.ForegroundProperty, "BrushText");
            ToolTip.SetTip(b, PurchaseNumberUi.CopyHint);
            b.PointerPressed += async (_, _) =>
            {
                await PurchaseNumberUi.CopyAsync(this, docNumber).ConfigureAwait(true);
                ToolTip.SetTip(b, PurchaseNumberUi.CopiedText);
            };
            Grid.SetColumn(b, 1);
            row.Children.Add(a);
            row.Children.Add(b);
            panel.Children.Add(row);
        }
        var supplierDocText = PurchaseNumberUi.SupplierDocText(r.SupplierDocNumber, r.SupplierDocDate);
        if (supplierDocText.Length > 0)
            Line(T("Накладная поставщика", "Жеткирүүчүнүн накладнойу", "Supplier invoice", "Tedarikçi faturası", "Yetkazib beruvchi yuk xati"), r.SupplierDocNumber + (r.SupplierDocDate is { } sd ? " · " + sd.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : ""));
        Line(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi"), r.SupplierName);
        Line(T("Принял", "Кабыл алды", "Received by", "Kabul eden", "Qabul qildi"), r.CreatedBy.Length > 0 ? r.CreatedBy : "—");
        Line(T("Дата", "Күнү", "Date", "Tarih", "Sana"), r.CreatedAt?.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—");
        var pay = note?.PayKind switch
        {
            "cash" => T("оплачено из основной кассы компании", "компаниянын негизги кассасынан төлөндү", "paid from the company main till", "şirket ana kasasından ödendi", "kompaniyaning asosiy kassasidan to'landi"),
            "drawer" => T("оплачено из ящика смены", "сменанын кассасынан төлөндү", "paid from the shift drawer", "vardiya kasasından ödendi", "smena kassasidan to'landi"),
            "partial" => T($"предоплата {MoneyText.Som(note.Paid)}, долг {MoneyText.Som(note.Debt)}", $"алдын ала {MoneyText.Som(note.Paid)}, карыз {MoneyText.Som(note.Debt)}",
                $"prepaid {MoneyText.Som(note.Paid)}, debt {MoneyText.Som(note.Debt)}", $"ön ödeme {MoneyText.Som(note.Paid)}, borç {MoneyText.Som(note.Debt)}", $"oldindan {MoneyText.Som(note.Paid)}, qarz {MoneyText.Som(note.Debt)}"),
            "debt" => T("в долг поставщику", "жеткирүүчүгө карызга", "on credit", "veresiye", "qarzga"),
            _ => debtRecorded ? T("в долг поставщику", "жеткирүүчүгө карызга", "on credit", "veresiye", "qarzga")
                : T("не известно (сервер способ оплаты не хранит)", "белгисиз (сервер төлөм ыкмасын сактабайт)", "unknown (the server doesn't store it)", "bilinmiyor (sunucu saklamaz)", "noma'lum (server saqlamaydi)"),
        };
        if (note?.DueDate is { } due && note.Debt > 0.005)
            pay += T($", оплатить до {due:dd.MM.yyyy}", $", {due:dd.MM.yyyy} чейин төлөө", $", pay by {due:dd.MM.yyyy}", $", son ödeme {due:dd.MM.yyyy}", $", {due:dd.MM.yyyy} gacha");
        Line(T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov"), pay);
        if (note?.MoneyNote is { Length: > 0 } mn)
            Line("", mn);
        if (r.ReturnedAmount > 0.005)
            Line(T("Возвращено", "Кайтарылды", "Returned", "İade edilen", "Qaytarildi"), MoneyText.Som(r.ReturnedAmount));
        var rows = r.Lines.Select(l => (l.ProductName + (l.ReturnedQty > 0 ? T($" (возвр. {PurchasesWindow.Q(l.ReturnedQty)})", $" (кайт. {PurchasesWindow.Q(l.ReturnedQty)})", $" (ret. {PurchasesWindow.Q(l.ReturnedQty)})", $" (iade {PurchasesWindow.Q(l.ReturnedQty)})", $" (qayt. {PurchasesWindow.Q(l.ReturnedQty)})") : ""),
            PurchasesWindow.Q(l.Qty) + " " + (l.Unit.Length > 0 ? l.Unit : "шт"), MoneyText.Som(l.PurchasePrice), MoneyText.Som(l.Qty * l.PurchasePrice))).ToList();
        // 2026-10-30 (наценка в накладной): ролям с правом на закупочные цены — таблица с колонками «Наценка %» и «Цена продажи» и итогом по документу.
        var markupRows = WarehouseWindow.RevisionShowPurchase && rows.Count > 0 ? PurchaseMarkupTable.Rows(r) : null;
        if (markupRows is not null)
            panel.Children.Add(PurchaseMarkupTable.Build(this, markupRows,
                context is null ? null : r.Lines.Select((l, i) => (l, i)).Where(x => string.Equals(x.l.ProductId, context.HighlightProductId, StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToHashSet()));
        else if (rows.Count > 0)
            panel.Children.Add(SectionCards.LinesTable(this, rows, T("Наименование", "Аталышы", "Name", "Ad", "Nomi"), T("Кол-во", "Саны", "Qty", "Adet", "Soni"),
                T("Цена закупки", "Сатып алуу баасы", "Purchase price", "Alış fiyatı", "Xarid narxi"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa"),
                context is null ? null : r.Lines.Select((l, i) => (l, i)).Where(x => string.Equals(x.l.ProductId, context.HighlightProductId, StringComparison.OrdinalIgnoreCase)).Select(x => x.i).ToHashSet()));
        else
            panel.Children.Add(SectionCards.Empty(this, T("Позиции не загрузились — проверьте связь.", "Позициялар жүктөлгөн жок — байланышты текшериңиз.", "Items didn't load — check the connection.", "Kalemler yüklenmedi — bağlantıyı kontrol edin.", "Pozitsiyalar yuklanmadi — aloqani tekshiring.")));
        var message = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(message, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(message);
        // 2026-10-30: исходный файл накладной (копия, которую касса оставила при загрузке накладной) — открыть; нет — честно сказать.
        if (context is not null)
        {
            var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var fileNote = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480 };
            Use(fileNote, TextBlock.ForegroundProperty, "BrushTextSoft");
            if (context.InvoiceFilePath is { Length: > 0 } filePath)
            {
                fileNote.Text = T("Исходный файл накладной: ", "Накладнойдун баштапкы файлы: ", "Original invoice file: ", "Orijinal fatura dosyası: ", "Yuk xatining asl fayli: ") + System.IO.Path.GetFileName(filePath);
                fileRow.Children.Add(fileNote);
                if (!OperatingSystem.IsAndroid())
                {
                    var open = SectionCards.Secondary(T("Открыть файл", "Файлды ачуу", "Open file", "Dosyayı aç", "Faylni ochish"));
                    open.Click += (_, _) => PurchaseDocs.OpenFile(filePath);
                    fileRow.Children.Add(open);
                }
            }
            else
            {
                fileNote.Text = T("Исходный файл накладной не сохранён: сервер NurCRM файл прихода не хранит, а эта касса не загружала накладную файлом. Ниже можно скачать PDF прихода.",
                    "Накладнойдун баштапкы файлы сакталган эмес: NurCRM сервери кирүү файлын сактабайт, ал эми бул касса накладнойду файл менен жүктөгөн эмес. Төмөндөн кирүүнүн PDF жүктөп алса болот.",
                    "The original invoice file is not saved: the NurCRM server does not keep a file for a receipt, and this till did not load the invoice from a file. You can download the receipt PDF below.",
                    "Orijinal fatura dosyası kaydedilmedi: NurCRM sunucusu giriş için dosya saklamaz, bu kasa da faturayı dosyadan yüklemedi. Aşağıdan giriş PDF'ini indirebilirsiniz.",
                    "Yuk xatining asl fayli saqlanmagan: NurCRM serveri kirim uchun fayl saqlamaydi, bu kassa esa yuk xatini fayldan yuklamagan. Quyidan kirim PDF'ini yuklab olish mumkin.");
                fileRow.Children.Add(fileNote);
            }
            panel.Children.Add(fileRow);
        }

        // 2026-10-30: печатная накладная поставщику — без наценки (настройка «Показывать наценку в печатной накладной», по умолчанию выкл.); «Скачать PDF» —
        // внутренняя копия с наценкой (по умолчанию вкл.), и только у ролей с правом на закупочные цены.
        List<PurchaseDocs.DocLine> LinesFor(bool withMarkup) => r.Lines.Select((l, i) => new PurchaseDocs.DocLine(l.ProductName, l.Unit, l.Qty, l.PurchasePrice,
            withMarkup && markupRows is not null ? PurchaseDocs.MarkupSuffix(markupRows[i].View) : null)).ToList();
        string? NoteFor(bool withMarkup) => PurchaseDocs.ReceiptNote(supplierDocText.Length > 0 ? char.ToUpperInvariant(supplierDocText[0]) + supplierDocText[1..] : null,
            withMarkup && markupRows is not null ? ReceiptMarkupLog.Totals(markupRows.Select(x => (x.Line.Qty, x.Line.PurchasePrice, x.View))) : null);
        var lines = LinesFor(false);
        var number = docNumber;
        var date = r.CreatedAt ?? DateTime.Now;
        var party = new PurchaseDocs.Party(r.SupplierName, supplier?.Phone ?? "", supplier?.Inn ?? "", supplier?.Address ?? "");
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        Button Outline(string text, Action click)
        {
            var b = SectionCards.Cancel(text);
            b.IsCancel = false;
            b.Click += (_, _) => click();
            return b;
        }
        var giveBack = Outline(T("Вернуть", "Кайтаруу", "Return", "İade", "Qaytarish"), () =>
        {
            Action = "return";
            Close();
        });
        giveBack.IsEnabled = r.ReturnedAmount < r.Total - 0.005;
        var repeat = Outline(T("Повторить", "Кайталоо", "Repeat", "Tekrarla", "Takrorlash"), () =>
        {
            Action = "repeat";
            Close();
        });
        ToolTip.SetTip(repeat, T("Новый приём с этими же товарами и ценами", "Ушул эле товарлар жана баалар менен жаңы кабыл алуу", "New receiving with the same products and prices",
            "Aynı ürün ve fiyatlarla yeni kabul", "Shu mahsulotlar va narxlar bilan yangi qabul"));
        var print = Outline(T("Печать", "Басып чыгаруу", "Print", "Yazdır", "Chop etish"), async () =>
        {
            var error = await PurchaseDocs.PrintAsync(PurchaseDocs.Build(this, "receipt", party, number, date, LinesFor(DocumentNumbering.Settings.PrintMarkup), NoteFor(DocumentNumbering.Settings.PrintMarkup)), "Накладная " + number).ConfigureAwait(true);
            message.Text = error ?? T("Отправлено на принтер.", "Принтерге жөнөтүлдү.", "Sent to the printer.", "Yazıcıya gönderildi.", "Printerga yuborildi.");
            message.IsVisible = true;
        });
        var pdf = SectionCards.Primary(T("Скачать PDF", "PDF жүктөө", "Download PDF", "PDF indir", "PDF yuklab olish"));
        pdf.IsEnabled = lines.Count > 0;
        pdf.Click += async (_, _) =>
        {
            var (path, error) = await PurchaseDocs.SavePdfAsync(PurchaseDocs.Build(this, "receipt", party, number, date, LinesFor(DocumentNumbering.Settings.PdfMarkup), NoteFor(DocumentNumbering.Settings.PdfMarkup)),
                PurchaseDocs.FileName("receipt", r.SupplierName, number, date)).ConfigureAwait(true);
            message.Text = path is not null ? T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: ") + path
                : T("PDF не сохранён: ", "PDF сакталган жок: ", "PDF not saved: ", "PDF kaydedilemedi: ", "PDF saqlanmadi: ") + error;
            message.IsVisible = true;
        };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        // 2026-10-30: из карточки товара «Вернуть»/«Повторить» не нужны (они запускают возврат и приём в окне «Закупки»).
        foreach (var b in context is null ? new[] { close, giveBack, repeat, print, pdf } : new[] { close, print, pdf })
        {
            b.Margin = new Thickness(8, 4, 0, 4);
            buttons.Children.Add(b);
        }
        Content = SectionCards.Shell(Title, $"{r.SupplierName} · {MoneyText.Som(r.Total)}", panel, buttons);
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, this.GetResourceObservable(key));
}

/// <summary>Заказ поставщику: строки (заказано / принято / осталось), «Принять товар», «WhatsApp», «PDF», «Изменить», «Отменить»/«Удалить».</summary>
public sealed class PurchaseOrderDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    /// <summary>receive, edit, cancel, delete.</summary>
    public string? Action { get; private set; }

    public PurchaseOrderDialog(PurchaseOrder o, SupplierInfo? supplier)
    {
        Title = T($"Заказ {o.Number}", $"Буйрутма {o.Number}", $"Order {o.Number}", $"Sipariş {o.Number}", $"Buyurtma {o.Number}");
        SectionCards.AsModal(this, 780);
        var panel = new StackPanel { Spacing = 8 };
        var (state, bg, fg) = PurchasesWindow.OrderState(o);
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateRow.Children.Add(SectionCards.Pill(this, state, bg, fg));
        var when = new TextBlock
        {
            Text = T($"создан {o.CreatedAt:dd.MM.yyyy HH:mm}", $"түзүлдү {o.CreatedAt:dd.MM.yyyy HH:mm}", $"created {o.CreatedAt:dd.MM.yyyy HH:mm}", $"oluşturuldu {o.CreatedAt:dd.MM.yyyy HH:mm}", $"yaratildi {o.CreatedAt:dd.MM.yyyy HH:mm}")
                   + (o.SentAt is { } sent ? T($" · отправлен {sent:dd.MM.yyyy}", $" · жөнөтүлдү {sent:dd.MM.yyyy}", $" · sent {sent:dd.MM.yyyy}", $" · gönderildi {sent:dd.MM.yyyy}", $" · yuborildi {sent:dd.MM.yyyy}") : "")
                   + (o.Author.Length > 0 ? " · " + o.Author : ""),
            FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
        };
        Use(when, TextBlock.ForegroundProperty, "BrushTextSoft");
        stateRow.Children.Add(when);
        panel.Children.Add(stateRow);
        var rows = o.Lines.Where(l => l.Qty > 0).Select(l => (l.Name,
            PurchasesWindow.Q(l.Qty) + " " + (l.Unit.Length > 0 ? l.Unit : "шт") + (l.ReceivedQty > 0 ? T($" · принято {PurchasesWindow.Q(l.ReceivedQty)}", $" · кабыл {PurchasesWindow.Q(l.ReceivedQty)}", $" · got {PurchasesWindow.Q(l.ReceivedQty)}", $" · gelen {PurchasesWindow.Q(l.ReceivedQty)}", $" · qabul {PurchasesWindow.Q(l.ReceivedQty)}") : ""),
            l.Price > 0 ? MoneyText.Som(l.Price) : "—", MoneyText.Som(l.Qty * l.Price))).ToList();
        panel.Children.Add(SectionCards.LinesTable(this, rows, T("Товар", "Товар", "Product", "Ürün", "Mahsulot"), T("Заказано", "Буйрутмаланды", "Ordered", "Sipariş", "Buyurtma"),
            T("Цена", "Баасы", "Price", "Fiyat", "Narx"), T("Сумма", "Сумма", "Amount", "Tutar", "Summa")));
        if (o.Note.Length > 0)
        {
            var note = new TextBlock { Text = o.Note, FontSize = 13, TextWrapping = TextWrapping.Wrap };
            Use(note, TextBlock.ForegroundProperty, "BrushTextSoft");
            panel.Children.Add(note);
        }
        var message = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        Use(message, TextBlock.ForegroundProperty, "BrushTextSoft");
        panel.Children.Add(message);

        var lines = o.Lines.Select(l => new PurchaseDocs.DocLine(l.Name, l.Unit, l.Qty, l.Price)).ToList();
        var party = new PurchaseDocs.Party(o.SupplierName, supplier?.Phone ?? o.SupplierPhone, supplier?.Inn ?? "", supplier?.Address ?? "");
        Button Outline(string text, Action click)
        {
            var b = SectionCards.Cancel(text);
            b.IsCancel = false;
            b.Click += (_, _) => click();
            return b;
        }
        var close = SectionCards.Cancel(T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        close.Click += (_, _) => Close();
        var whatsapp = Outline("WhatsApp", () =>
        {
            PurchaseDocs.OpenWhatsApp(party.Phone, PurchaseDocs.OrderText(o.SupplierName, o.Number, o.CreatedAt, lines, o.Note));
            if (o.Status == "draft")
                PurchasesStore.SetOrderStatus(o.Id, "sent");
            message.Text = T("Открыт WhatsApp с текстом заказа. Заказ отмечен «Отправлен».", "WhatsApp буйрутманын тексти менен ачылды. Буйрутма «Жөнөтүлдү» деп белгиленди.",
                "WhatsApp opened with the order text. The order is marked “Sent”.", "WhatsApp sipariş metniyle açıldı. Sipariş «Gönderildi» olarak işaretlendi.",
                "WhatsApp buyurtma matni bilan ochildi. Buyurtma «Yuborildi» deb belgilandi.");
            message.IsVisible = true;
        });
        var pdf = Outline("PDF", async () =>
        {
            var (path, error) = await PurchaseDocs.SavePdfAsync(PurchaseDocs.Build(this, "order", party, o.Number, o.CreatedAt, lines, o.Note),
                PurchaseDocs.FileName("order", o.SupplierName, o.Number, o.CreatedAt)).ConfigureAwait(true);
            message.Text = path is not null ? T("PDF сохранён: ", "PDF сакталды: ", "PDF saved: ", "PDF kaydedildi: ", "PDF saqlandi: ") + path
                : T("PDF не сохранён: ", "PDF сакталган жок: ", "PDF not saved: ", "PDF kaydedilemedi: ", "PDF saqlanmadi: ") + error;
            message.IsVisible = true;
        });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var list = new List<Button> { close };
        if (o.IsOpen)
        {
            if (o.State == "draft")
                list.Add(Outline(T("Изменить", "Өзгөртүү", "Edit", "Düzenle", "O'zgartirish"), () =>
                {
                    Action = "edit";
                    Close();
                }));
            list.Add(Outline(o.State == "draft" ? T("Удалить", "Өчүрүү", "Delete", "Sil", "O'chirish") : T("Отменить", "Жокко чыгаруу", "Cancel order", "İptal et", "Bekor qilish"), () =>
            {
                Action = o.State == "draft" ? "delete" : "cancel";
                Close();
            }));
            list.Add(whatsapp);
            list.Add(pdf);
            var receive = SectionCards.Primary(T("Принять товар", "Товар кабыл алуу", "Receive goods", "Mal kabul et", "Tovar qabul qilish"));
            receive.Click += (_, _) =>
            {
                Action = "receive";
                Close();
            };
            list.Add(receive);
        }
        else
        {
            list.Add(whatsapp);
            list.Add(pdf);
        }
        foreach (var b in list)
        {
            b.Margin = new Thickness(8, 4, 0, 4);
            buttons.Children.Add(b);
        }
        Content = SectionCards.Shell(Title, $"{o.SupplierName} · {MoneyText.Som(o.Total)}", panel, buttons);
    }

    private void Use(AvaloniaObject target, AvaloniaProperty property, string key) => target.Bind(property, this.GetResourceObservable(key));
}
