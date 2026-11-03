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
    private readonly SupplierApiService _svc;
    private readonly TextBlock _error = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
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
        _svc = svc;
        Title = T("Реквизиты накладной", "Накладнойдун реквизиттери", "Invoice details", "Fatura bilgileri", "Yuk xati rekvizitlari");
        SectionCards.AsModal(this, 640);
        _supplier = new PurchasesSupplierPicker(svc) { AllowAll = false, ShowAdd = false };
        var panel = new StackPanel { Spacing = 8 };
        TextBlock Label(string text)
        {
            var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold };
            t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
            return t;
        }
        var note = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Text = T("Меняйте только нужное: всё, что вы оставите как есть, не отправляется. Правка уйдёт одним запросом вместе с количеством и ценами, с причиной и записью в журнал.",
            "Керектүүсүн гана өзгөртүңүз: калтырганыңыз жөнөтүлбөйт. Оңдоо санды жана бааны кошо бир суроо менен кетет, себеби жана журналга жазуу менен.",
            "Change only what you need: anything left as is is not sent. The edit goes in one request together with quantities and prices, with a reason and a journal entry.",
            "Yalnızca gerekeni değiştirin: olduğu gibi bıraktıklarınız gönderilmez. Düzenleme miktar ve fiyatlarla birlikte tek istekle, nedenle ve günlük kaydıyla gider.",
            "Faqat kerakligini o'zgartiring: o'zgarishsiz qoldirilgani yuborilmaydi. Tahrir miqdor va narxlar bilan birga bitta so'rov bilan, sabab va jurnal yozuvi bilan ketadi.") };
        note.Bind(TextBlock.ForegroundProperty, note.GetResourceObservable("BrushTextSoft"));
        panel.Children.Add(note);
        panel.Children.Add(Label(T("Поставщик", "Жеткирүүчү", "Supplier", "Tedarikçi", "Yetkazib beruvchi")));
        panel.Children.Add(_supplier);
        panel.Children.Add(Label(T("№ накладной поставщика", "Жеткирүүчүнүн накладной №", "Supplier invoice no.", "Tedarikçi fatura no.", "Yetkazuvchi yuk xati №")));
        _number.Text = current?.DocNumber ?? r.SupplierDocNumber;
        panel.Children.Add(_number);
        panel.Children.Add(Label(T("Дата накладной поставщика", "Жеткирүүчүнүн накладной күнү", "Supplier invoice date", "Tedarikçi fatura tarihi", "Yetkazuvchi yuk xati sanasi")));
        _date.SelectedDate = current?.DocDate ?? r.SupplierDocDate;
        // 2026-11-02 (живая проверка круга 3): дата, набранная неверно (32.13.2026), больше не теряется молча — поле краснеет, сохранение реквизитов блокируется с понятным текстом
        _date.DateValidationError += (_, e) =>
        {
            _badDateText = e.Text;
            MarkDate(true);
        };
        _date.SelectedDateChanged += (_, _) =>
        {
            _badDateText = null;
            MarkDate(false);
        };
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

        panel.Children.Add(SectionCards.ErrorBox(this, _error));

        var cancel = SectionCards.Cancel(T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"));
        cancel.Click += (_, _) => Close();
        var ok = SectionCards.Primary(T("Готово", "Даяр", "Done", "Tamam", "Tayyor"));
        ok.Click += async (_, _) => await AcceptAsync().ConfigureAwait(true);
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

    private string? _badDateText;

    private void MarkDate(bool bad)
    {
        if (bad)
        {
            _date.BorderThickness = new Thickness(2);
            _date.Bind(BorderBrushProperty, _date.GetResourceObservable("BrushDanger"));
        }
        else
        {
            _date.ClearValue(BorderBrushProperty);
            _date.ClearValue(BorderThicknessProperty);
        }
    }

    private void Fail(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }

    private static string PayName(string? key) => key switch
    {
        "cash" => T("Наличные (оплачено)", "Накталай (төлөндү)", "Cash (paid)", "Nakit (ödendi)", "Naqd (to'langan)"),
        "debt" => T("В долг", "Карызга", "On credit", "Veresiye", "Qarzga"),
        "partial" => T("Частично", "Жарым-жартылай", "Partly", "Kısmen", "Qisman"),
        _ => "—",
    };

    /// <summary>2026-11-02 (аудит №2, С2-23): проверки полей до отправки. Дата накладной поставщика — не в будущем и не раньше 2000 года; номер нельзя очистить, а повтор номера у того же поставщика
    /// — вопрос; скидка — сумма ИЛИ процент (не оба), процент 0–100, сумма не больше итога; оплата — «Частично» только с суммой больше нуля и меньше итога, оплачено не больше итога;
    /// выбранный способ оплаты, совпадающий с текущим, не отправляется (повторное «В долг» не создаёт второй долг); смена оплаты — с подтверждением.</summary>
    private async Task AcceptAsync()
    {
        _error.IsVisible = false;
        string? supplierId = null, supplierName = null;
        if (_supplier.Selected is { } s && !string.Equals(s.Id, _r.SupplierId, StringComparison.OrdinalIgnoreCase))
        {
            supplierId = s.Id;
            supplierName = s.Name;
        }
        // набранный вручную текст даты не разобрался (SelectedDate = null при непустом тексте) — блокируем, а не теряем
        var badDate = _badDateText ?? (!string.IsNullOrWhiteSpace(_date.Text) && _date.SelectedDate is null ? _date.Text : null);
        if (!string.IsNullOrWhiteSpace(badDate))
        {
            MarkDate(true);
            Fail(T($"Дата «{badDate}» неверная — впишите существующую дату (например, 28.10.2026) или выберите её в календаре.", $"«{badDate}» күнү туура эмес — бар күндү жазыңыз (мисалы, 28.10.2026) же жылнаамадан тандаңыз.",
                $"The date “{badDate}” is not valid — enter an existing date (for example 28.10.2026) or pick it in the calendar.", $"«{badDate}» tarihi geçersiz — var olan bir tarih yazın (örneğin 28.10.2026) veya takvimden seçin.", $"«{badDate}» sanasi noto'g'ri — mavjud sanani yozing (masalan, 28.10.2026) yoki kalendardan tanlang."));
            return;
        }
        var originalNumber = _r.SupplierDocNumber ?? "";
        var number = (_number.Text ?? "").Trim();
        var comment = (_comment.Text ?? "").Trim();
        DateTime? date = _date.SelectedDate is { } d && d.Date != _r.SupplierDocDate?.Date ? d.Date : null;
        if (date is { } dt && (dt > DateTime.Today || dt < new DateTime(2000, 1, 1)))
        {
            Fail(T("Дата накладной поставщика не может быть в будущем или раньше 2000 года.", "Жеткирүүчүнүн накладной күнү келечекте же 2000-жылдан мурун болушу мүмкүн эмес.",
                "The supplier invoice date cannot be in the future or before the year 2000.", "Tedarikçi fatura tarihi gelecekte veya 2000 yılından önce olamaz.", "Yetkazuvchi yuk xati sanasi kelajakda yoki 2000-yildan oldin bo'lishi mumkin emas."));
            return;
        }
        if (number != originalNumber && number.Length == 0 && originalNumber.Length > 0)
        {
            Fail(T("Номер накладной поставщика нельзя очистить: впишите новый номер или оставьте прежний.", "Жеткирүүчүнүн накладной номерин тазалоого болбойт: жаңы номерди жазыңыз же мурункусун калтырыңыз.",
                "The supplier invoice number cannot be cleared: enter a new number or keep the old one.", "Tedarikçi fatura numarası silinemez: yeni numara yazın veya eskisini bırakın.", "Yetkazuvchi yuk xati raqamini o'chirib bo'lmaydi: yangisini yozing yoki eskisini qoldiring."));
            return;
        }
        var payType = _pay.SelectedIndex > 0 ? _payKeys[_pay.SelectedIndex] : null;
        double? paid = payType is "partial" ? ParseNum(_paid.Text) : null;
        var currentPay = (_r.PaymentType ?? "").Trim().ToLowerInvariant();
        if (payType is not null && payType == currentPay && !(payType == "partial" && paid is { } pp && Math.Abs(pp - _r.PaidAmount) > 0.005))
            payType = null; // тот же способ оплаты — ничего не отправляем (повторное «В долг» не создаёт второй долг)
        if (payType == "partial" && (paid is null || paid <= 0 || (_r.Total > 0 && paid >= _r.Total)))
        {
            Fail(T("Для «Частично» укажите, сколько оплачено: больше нуля и меньше суммы накладной.", "«Жарым-жартылай» үчүн канча төлөнгөнүн көрсөтүңүз: нөлдөн көп жана накладнойдун суммасынан аз.",
                "For “Partly” enter how much was paid: above zero and below the invoice total.", "«Kısmen» için ne kadar ödendiğini yazın: sıfırdan büyük ve fatura tutarından az.", "«Qisman» uchun qancha to'langanini yozing: noldan katta va yuk xati summasidan kam."));
            return;
        }
        var discount = _discount.IsEnabled ? ParseNum(_discount.Text) : null;
        var percent = _discountPercent.IsEnabled ? ParseNum(_discountPercent.Text) : null;
        if (discount is { } dv && Math.Abs(dv - _r.Discount) < 0.005)
            discount = null;
        if (percent is { } pv && Math.Abs(pv - _r.DiscountPercent) < 0.005)
            percent = null;
        if (discount is not null && percent is not null)
        {
            Fail(T("Скидка: укажите либо сумму, либо процент — не оба сразу.", "Арзандатуу: сумманы же пайызды көрсөтүңүз — экөөнү тең эмес.", "Discount: enter either an amount or a percent — not both.",
                "İndirim: tutar veya yüzde yazın — ikisini birden değil.", "Chegirma: summa yoki foizdan birini yozing — ikkalasini emas."));
            return;
        }
        if (percent is > 100)
        {
            Fail(T("Процент скидки — от 0 до 100.", "Арзандатуу пайызы — 0дөн 100гө чейин.", "The discount percent is from 0 to 100.", "İndirim yüzdesi 0 ile 100 arasındadır.", "Chegirma foizi — 0 dan 100 gacha."));
            return;
        }
        if (discount is { } da && _r.Total > 0 && da > _r.Total + 0.005)
        {
            Fail(T("Скидка не может быть больше суммы накладной.", "Арзандатуу накладнойдун суммасынан чоң болушу мүмкүн эмес.", "The discount cannot exceed the invoice total.",
                "İndirim fatura tutarından büyük olamaz.", "Chegirma yuk xati summasidan katta bo'lishi mumkin emas."));
            return;
        }
        var owner = this;
        if (number != originalNumber && number.Length > 0)
        {
            // номер уже есть у этого поставщика — вопрос (не запрет: поставщик мог выдать повторный номер); не удалось проверить — идём дальше
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var (list, _) = await _svc.GetReceiptsAsync(supplierId ?? _r.SupplierId, 200, cts.Token).ConfigureAwait(true);
                if (list.FirstOrDefault(x => !string.Equals(x.Id, _r.Id, StringComparison.OrdinalIgnoreCase) && string.Equals(x.SupplierDocNumber, number, StringComparison.OrdinalIgnoreCase)) is { } dup
                    && !await NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmDialog.ShowModalAsync(owner,
                        T("Такой номер уже есть", "Мындай номер бар", "This number already exists", "Bu numara zaten var", "Bunday raqam bor"),
                        T($"У этого поставщика уже есть накладная с номером «{number}» ({dup.CreatedAt:dd.MM.yyyy}). Всё равно сохранить такой номер?", $"Бул жеткирүүчүдө «{number}» номерлүү накладной бар ({dup.CreatedAt:dd.MM.yyyy}). Ошол номерди сактайбызбы?",
                            $"This supplier already has an invoice numbered “{number}” ({dup.CreatedAt:dd.MM.yyyy}). Save this number anyway?", $"Bu tedarikçide «{number}» numaralı bir fatura zaten var ({dup.CreatedAt:dd.MM.yyyy}). Yine de kaydedilsin mi?", $"Bu yetkazib beruvchida «{number}» raqamli yuk xati allaqachon bor ({dup.CreatedAt:dd.MM.yyyy}). Baribir saqlansinmi?"),
                        T("Сохранить", "Сактоо", "Save", "Kaydet", "Saqlash"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                        NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmAccent.Primary).ConfigureAwait(true))
                    return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                PosLogger.Log($"Накладная: повтор номера у поставщика не проверен ({ex.Message}).", "WARNING");
            }
        }
        if (payType is not null
            && !await NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmDialog.ShowModalAsync(owner,
                T("Изменить оплату накладной?", "Накладнойдун төлөмүн өзгөртөбүзбү?", "Change the invoice payment?", "Fatura ödemesi değiştirilsin mi?", "Yuk xati to'lovi o'zgartirilsinmi?"),
                T($"Оплата изменится: {PayName(currentPay)} → {PayName(payType)}. Сервер пересчитает долг поставщику и деньги в кассе. Продолжить?", $"Төлөм өзгөрөт: {PayName(currentPay)} → {PayName(payType)}. Сервер жеткирүүчүгө карызды жана кассадагы акчаны кайра эсептейт. Улантабызбы?",
                    $"The payment changes: {PayName(currentPay)} → {PayName(payType)}. The server recalculates the supplier debt and the money. Continue?", $"Ödeme değişiyor: {PayName(currentPay)} → {PayName(payType)}. Sunucu tedarikçi borcunu ve parayı yeniden hesaplar. Devam edilsin mi?", $"To'lov o'zgaradi: {PayName(currentPay)} → {PayName(payType)}. Server yetkazib beruvchi qarzi va pulni qayta hisoblaydi. Davom etamizmi?"),
                T("Изменить", "Өзгөртүү", "Change", "Değiştir", "O'zgartirish"), T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish"),
                NurMarketKassa.AvaloniaHost.Views.Dialogs.PosConfirmAccent.Primary).ConfigureAwait(true))
            return;
        var edit = new HeaderEdit(supplierId, supplierName, number != originalNumber ? number : null, date, ChangedComment(comment), payType, paid, discount, percent);
        Saved = true;
        Result = edit == new HeaderEdit(null, null, null, null, null, null, null, null, null) ? null : edit;
        Close();
    }
}
