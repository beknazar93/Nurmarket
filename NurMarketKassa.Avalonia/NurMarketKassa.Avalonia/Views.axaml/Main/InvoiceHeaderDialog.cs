using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>Изменённые реквизиты накладной (null — не менять).</summary>
internal sealed record HeaderEdit(string? SupplierId, string? SupplierName, string? DocNumber, DateTime? DocDate, string? Comment, string? PayType, double? Paid, double? Discount, double? DiscountPercent);

/// <summary>2026-11-01 (ответ Умара: PATCH прихода): «Реквизиты накладной» — поставщик, номер и дата накладной поставщика, комментарий, оплата и (если сервер сообщает receipt_edit_discount) скидка.
/// Отправляется вместе с остальной правкой одним PATCH после «Проверить →», с причиной и записью в журнал. Без признака скидки поле выключено: «Требует серверной поддержки».</summary>
internal sealed class InvoiceHeaderDialog : Window
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    public HeaderEdit? Result { get; private set; }
    public bool Saved { get; private set; }

    private readonly SupplierReceipt _r;
    private readonly PurchasesSupplierPicker _supplier;
    private readonly TextBox _number = new() { MinHeight = 44 };
    private readonly CalendarDatePicker _date = new() { MinHeight = 44, SelectedDateFormat = CalendarDatePickerFormat.Short, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _comment = new() { MinHeight = 44, MaxLength = 500 };
    private readonly ComboBox _pay = new() { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _paid = new() { MinHeight = 44 };
    private readonly TextBox _discount = new() { MinHeight = 44 };
    private readonly TextBox _discountPercent = new() { MinHeight = 44 };
    private readonly string[] _payKeys = { "", "cash", "debt", "partial" };

    public InvoiceHeaderDialog(SupplierReceipt r, SupplierApiService svc, ReceiptEditCaps caps, HeaderEdit? current)
    {
        _r = r;
        Title = T("Реквизиты накладной", "Накладнойдун реквизиттери", "Invoice details", "Fatura bilgileri", "Yuk xati rekvizitlari");
        SectionCards.AsModal(this, 640);
        _supplier = new PurchasesSupplierPicker(svc) { AllowAll = false, ShowAdd = false };
        var panel = new StackPanel { Spacing = 8 };
        TextBlock Label(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold };
            t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
            return t;
        }
        var note = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Text = T("Меняйте только нужное: всё, что вы оставите как есть, не отправляется. Правка уйдёт одним запросом вместе с количеством и ценами, с причиной и записью в журнал.",
            "Керектүүсүн гана өзгөртүңүз: калтырганыңыз жөнөтүлбөйт. Оңдоо санды жана бааны кошо бир суроо менен кетет, себеби жана журналга жазуу менен.",
            "Change only what you need: anything left as is is not sent. The edit goes in one request together with quantities and prices, with a reason and a journal entry.",
            "Yalnızca gerekeni değiştirin: olduğu gibi bıraktıklarınız gönderilmez. Düzenleme miktar ve fiyatlarla birlikte tek istekle, nedenle ve günlük kaydıyla gider.",
            "Faqat kerakligini o'zgartiring: o'zgarishsiz qoldirilgani yuborilmaydi. Tahrir miqdor va narxlar bilan birga bitta so'rov bilan, sabab va jurnal yozuvi bilan ketadi.") };
        note.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(note);
        panel.Children.Add(Label(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi")));
        panel.Children.Add(_supplier);
        panel.Children.Add(Label(T("№ накладной поставщика", "Жеткирүүчүнүн накладной №", "Supplier invoice no.", "Tedarikçi fatura no.", "Yetkazuvchi yuk xati №")));
        _number.Text = current?.DocNumber ?? r.SupplierDocNumber;
        panel.Children.Add(_number);
        panel.Children.Add(Label(T("Дата накладной поставщика", "Жеткирүүчүнүн накладной күнү", "Supplier invoice date", "Tedarikçi fatura tarihi", "Yetkazuvchi yuk xati sanasi")));
        _date.SelectedDate = current?.DocDate ?? r.SupplierDocDate;
        panel.Children.Add(_date);
        panel.Children.Add(Label(T("Комментарий", "Комментарий", "Comment", "Yorum", "Izoh")));
        // 2026-11-01 (приёмка 1.17.72, Н-2): служебный префикс комментария («№ ПР-…; накладная поставщика: …») не показываем и не стираем — на нём держится номер документа
        _comment.Text = current?.Comment is { } cur ? SplitComment(cur).User : SplitComment(r.Comment).User;
        panel.Children.Add(_comment);
        panel.Children.Add(Label(T("Оплата", "Төлөм", "Payment", "Ödeme", "To'lov")));
        _pay.ItemsSource = new[]
        {
            T("Не менять", "Өзгөртпөө", "Leave as is", "Değiştirme", "O'zgartirmaslik"), T("Наличные (оплачено)", "Накталай (төлөндү)", "Cash (paid)", "Nakit (ödendi)", "Naqd (to'langan)"),
            T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"), T("Частично", "Жарым-жартылай", "Partly", "Kısmen", "Qisman"),
        };
        _pay.SelectedIndex = Math.Max(0, Array.IndexOf(_payKeys, current?.PayType ?? ""));
        panel.Children.Add(_pay);
        _paid.Watermark = T("Сколько оплачено (для «Частично»)", "Канча төлөндү («Жарым-жартылай» үчүн)", "Amount paid (for “Partly”)", "Ödenen tutar («Kısmen» için)", "To'langan summa («Qisman» uchun)");
        _paid.Text = current?.Paid?.ToString("0.##", CultureInfo.InvariantCulture);
        panel.Children.Add(_paid);
        panel.Children.Add(Label(T("Скидка накладной (сумма / процент)", "Накладнойдун арзандатуусу (сумма / пайыз)", "Invoice discount (amount / percent)", "Fatura indirimi (tutar / yüzde)", "Yuk xati chegirmasi (summa / foiz)")));
        _discount.Watermark = T("Сумма скидки", "Арзандатуу суммасы", "Discount amount", "İndirim tutarı", "Chegirma summasi");
        _discountPercent.Watermark = T("Процент скидки", "Арзандатуу пайызы", "Discount percent", "İndirim yüzdesi", "Chegirma foizi");
        _discount.Text = current?.Discount?.ToString("0.##", CultureInfo.InvariantCulture) ?? (r.Discount > 0 ? r.Discount.ToString("0.##", CultureInfo.InvariantCulture) : "");
        _discountPercent.Text = current?.DiscountPercent?.ToString("0.##", CultureInfo.InvariantCulture) ?? (r.DiscountPercent > 0 ? r.DiscountPercent.ToString("0.##", CultureInfo.InvariantCulture) : "");
        if (!caps.Discount)
        {
            _discount.IsEnabled = _discountPercent.IsEnabled = false;
            ToolTip.SetTip(_discount, ReceiptEditService.NeedsServerText);
            ToolTip.SetTip(_discountPercent, ReceiptEditService.NeedsServerText);
            _discount.Watermark = ReceiptEditService.NeedsServerText;
        }
        panel.Children.Add(_discount);
        panel.Children.Add(_discountPercent);

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        var ok = SectionCards.Primary(T("Готово", "Даяр", "Done", "Tamam", "Tayyor"));
        ok.Click += (_, _) => Accept();
        Content = SectionCards.Shell(Title, $"{r.SupplierName}", new ScrollViewer { Content = panel, MaxHeight = 520 }, SectionCards.Buttons(cancel, ok));
        Opened += async (_, _) =>
        {
            await _supplier.LoadAsync().ConfigureAwait(true);
            _supplier.Select(current?.SupplierId ?? r.SupplierId, raise: false);
        };
    }

    private static readonly System.Text.RegularExpressions.Regex ServicePrefix = new(@"^\s*(№\s*\S+\s*;?\s*)?(накладная поставщика:[^;]*;?\s*)?", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Комментарий прихода = служебный префикс («№ ПР-2026-000123; накладная поставщика: 45 от 29.10») + текст пользователя.</summary>
    private static (string Prefix, string User) SplitComment(string? comment)
    {
        var text = comment ?? "";
        var m = ServicePrefix.Match(text);
        return (m.Value, text[m.Length..].Trim());
    }

    private static double? ParseNum(string? text) =>
        double.TryParse((text ?? "").Replace(" ", "").Replace(',', '.').Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    /// <summary>null — текст пользователя не менялся (префикс не трогаем, лишнего не шлём); иначе префикс + новый текст.</summary>
    private string? ChangedComment(string typed)
    {
        var (prefix, user) = SplitComment(_r.Comment);
        if (string.Equals(typed.Trim(), user, StringComparison.Ordinal))
            return null;
        return prefix.Length == 0 ? typed.Trim() : (prefix.TrimEnd().EndsWith(';') ? prefix.TrimEnd() + " " : prefix.TrimEnd() + "; ") + typed.Trim();
    }

    private void Accept()
    {
        string? supplierId = null, supplierName = null;
        if (_supplier.Selected is { } s && !string.Equals(s.Id, _r.SupplierId, StringComparison.OrdinalIgnoreCase))
        {
            supplierId = s.Id;
            supplierName = s.Name;
        }
        var number = (_number.Text ?? "").Trim();
        var comment = (_comment.Text ?? "").Trim();
        DateTime? date = _date.SelectedDate is { } d && d.Date != _r.SupplierDocDate?.Date ? d.Date : null;
        var payType = _pay.SelectedIndex > 0 ? _payKeys[_pay.SelectedIndex] : null;
        double? paid = payType is "partial" ? ParseNum(_paid.Text) : null;
        var discount = _discount.IsEnabled ? ParseNum(_discount.Text) : null;
        var percent = _discountPercent.IsEnabled ? ParseNum(_discountPercent.Text) : null;
        if (discount is { } dv && Math.Abs(dv - _r.Discount) < 0.005)
            discount = null;
        if (percent is { } pv && Math.Abs(pv - _r.DiscountPercent) < 0.005)
            percent = null;
        var edit = new HeaderEdit(supplierId, supplierName, number != _r.SupplierDocNumber ? number : null, date, ChangedComment(comment), payType, paid, discount, percent);
        Saved = true;
        Result = edit == new HeaderEdit(null, null, null, null, null, null, null, null, null) ? null : edit;
        Close();
    }
}
