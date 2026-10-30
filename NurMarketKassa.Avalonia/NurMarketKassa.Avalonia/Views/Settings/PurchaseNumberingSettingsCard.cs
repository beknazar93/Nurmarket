using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-30, просьба владельца «нумерация накладных в закупках»: карточка «Нумерация закупок» во вкладке «Операции» — префиксы серий
/// (приход ПР, возврат ВП, заказ ЗП), сброс нумерации по годам, число цифр, «Показывать наценку» в печатной накладной и во внутренней копии PDF.
/// Настройки хранятся на этом ПК (рядом с журналом закупок): префиксы и ширину на всех кассах компании лучше задать одинаково — иначе один и тот же
/// номер на разных ПК выглядит по-разному (порядковый номер у всех совпадает). Номера у уже выписанных документов не меняются на сервере.</summary>
public sealed class PurchaseNumberingSettingsCard : Border
{
    private readonly TextBox _receipt = Box(6);
    private readonly TextBox _return = Box(6);
    private readonly TextBox _order = Box(6);
    private readonly TextBox _width = Box(1);
    private readonly CheckBox _byYear;
    private readonly CheckBox _printMarkup;
    private readonly CheckBox _pdfMarkup;
    private readonly TextBlock _sample = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private bool _suppress;

    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    protected override Type StyleKeyOverride => typeof(Border);

    private static TextBox Box(int max) => new() { MinHeight = 40, Width = max == 1 ? 60 : 90, MaxLength = max, VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };

    public PurchaseNumberingSettingsCard()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(14);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = T("Нумерация закупок", "Сатып алуулардын номерлери", "Purchase numbering", "Alım numaralandırma", "Xaridlar raqamlanishi"), Classes = { "SettingsCardTitle" }, FontSize = 14 });
        stack.Children.Add(Hint(T("Номер документа: тип-год-порядок, например ПР-2026-000123. Порядок считается по истории сервера (по дате создания), поэтому на всех кассах номер один и тот же. Временная нумерация — пока NurCRM не присвоит номера сам.",
            "Документтин номери: түрү-жыл-тартиби, мисалы ПР-2026-000123. Тартиби сервердин тарыхы боюнча (түзүлгөн күнү) эсептелет, ошондуктан бардык кассада номер бирдей. Убактылуу номерлөө — NurCRM өзү номер бергенге чейин.",
            "Document number: type-year-sequence, e.g. ПР-2026-000123. The sequence is counted from the server history (by creation date), so every till shows the same number. Temporary numbering until NurCRM assigns numbers itself.",
            "Belge numarası: tür-yıl-sıra, örn. ПР-2026-000123. Sıra sunucu geçmişinden (oluşturma tarihine göre) hesaplanır, bu yüzden tüm kasalarda aynıdır. NurCRM kendi numarasını verene kadar geçici numaralandırma.",
            "Hujjat raqami: tur-yil-tartib, masalan ПР-2026-000123. Tartib server tarixi bo'yicha (yaratilgan sana) hisoblanadi, shuning uchun barcha kassalarda bir xil. NurCRM o'zi raqam berguncha vaqtincha raqamlash.")));
        stack.Children.Add(Row(T("Префикс прихода", "Кирүүнүн префикси", "Delivery prefix", "Giriş öneki", "Kirim prefiksi"), _receipt));
        stack.Children.Add(Row(T("Префикс возврата поставщику", "Жеткирүүчүгө кайтаруунун префикси", "Supplier return prefix", "Tedarikçi iade öneki", "Yetkazib beruvchiga qaytarish prefiksi"), _return));
        stack.Children.Add(Row(T("Префикс заказа поставщику", "Жеткирүүчүгө буйрутманын префикси", "Supplier order prefix", "Tedarikçi sipariş öneki", "Yetkazib beruvchiga buyurtma prefiksi"), _order));
        stack.Children.Add(Row(T("Цифр в порядковом номере (4–9)", "Тартиптик номердеги цифралар (4–9)", "Digits in the sequence (4–9)", "Sıra numarasındaki hane (4–9)", "Tartib raqamidagi raqamlar (4–9)"), _width));
        _byYear = Toggle(stack, T("Начинать нумерацию заново каждый год", "Номерлөөнү жыл сайын кайра баштоо", "Restart numbering every year", "Numaralandırmayı her yıl yeniden başlat", "Raqamlashni har yili qaytadan boshlash"));
        _printMarkup = Toggle(stack, T("Показывать наценку в печатной накладной (поставщику лучше не показывать)", "Басма накладнойдо кошумчаны көрсөтүү (жеткирүүчүгө көрсөтпөгөн жакшы)",
            "Show markup on the printed invoice (better not to show it to the supplier)", "Basılı faturada kârı göster (tedarikçiye göstermemek daha iyi)", "Bosma yuk xatida ustamani ko'rsatish (yetkazib beruvchiga ko'rsatmagan ma'qul)"));
        _pdfMarkup = Toggle(stack, T("Наценка во внутренней копии накладной (PDF «Скачать»)", "Накладнойдун ички көчүрмөсүндө кошумча (PDF «Жүктөө»)",
            "Markup in the internal invoice copy (PDF “Download”)", "Faturanın iç kopyasında kâr (PDF «İndir»)", "Yuk xatining ichki nusxasida ustama (PDF «Yuklab olish»)"));
        stack.Children.Add(Hint(T("Наценку в накладных видят только роли с правом на закупочные цены.", "Накладнойлордогу кошумчаны сатып алуу баасына укугу бар ролдор гана көрөт.",
            "Only roles allowed to see purchase prices can see markup on invoices.", "Faturalardaki kârı yalnızca alış fiyatlarını görme yetkisi olan roller görür.", "Yuk xatlaridagi ustamani faqat xarid narxlarini ko'rish huquqi bor rollar ko'radi.")));
        _sample.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(_sample);
        Child = stack;

        Show(DocumentNumbering.Settings);
        foreach (var box in new[] { _receipt, _return, _order, _width })
        {
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                    Commit();
            };
        }
        foreach (var check in new[] { _byYear, _printMarkup, _pdfMarkup })
            check.IsCheckedChanged += (_, _) => Commit();
    }

    private TextBlock Hint(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private Grid Row(string label, Control box)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        Grid.SetColumn(box, 1);
        grid.Children.Add(text);
        grid.Children.Add(box);
        return grid;
    }

    private CheckBox Toggle(StackPanel stack, string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var label = new TextBlock { Text = text, FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var check = new CheckBox { Classes = { "ToggleSwitch" }, VerticalAlignment = VerticalAlignment.Center };
        Avalonia.Automation.AutomationProperties.SetName(check, text);
        Grid.SetColumn(check, 1);
        grid.Children.Add(label);
        grid.Children.Add(check);
        stack.Children.Add(grid);
        return check;
    }

    private void Show(DocNumberingSettings s)
    {
        _suppress = true;
        try
        {
            _receipt.Text = s.PrefixReceipt;
            _return.Text = s.PrefixReturn;
            _order.Text = s.PrefixOrder;
            _width.Text = s.Width.ToString();
            _byYear.IsChecked = s.ResetByYear;
            _printMarkup.IsChecked = s.PrintMarkup;
            _pdfMarkup.IsChecked = s.PdfMarkup;
            _sample.Text = T("Пример: ", "Мисал: ", "Example: ", "Örnek: ", "Misol: ") + DocumentNumberingCore.Format(s, new DocNumber(DocSeries.Receipt, DateTime.Now.Year, 123)) + ", "
                           + DocumentNumberingCore.Format(s, new DocNumber(DocSeries.Return, DateTime.Now.Year, 7)) + ", "
                           + DocumentNumberingCore.Format(s, new DocNumber(DocSeries.Order, DateTime.Now.Year, 45)) + ", "
                           + T("временный: ", "убактылуу: ", "temporary: ", "geçici: ", "vaqtincha: ") + DocumentNumberingCore.Format(s, new DocNumber(DocSeries.Receipt, DateTime.Now.Year, 5, true));
        }
        finally
        {
            _suppress = false;
        }
    }

    private void Commit()
    {
        if (_suppress)
            return;
        var width = int.TryParse((_width.Text ?? "").Trim(), out var w) ? w : 6;
        var next = new DocNumberingSettings
        {
            PrefixReceipt = _receipt.Text ?? "", PrefixReturn = _return.Text ?? "", PrefixOrder = _order.Text ?? "", Width = width,
            ResetByYear = _byYear.IsChecked == true, PrintMarkup = _printMarkup.IsChecked == true, PdfMarkup = _pdfMarkup.IsChecked == true,
        }.Normalized();
        DocumentNumbering.Store.SaveSettings(next);
        PosLogger.Log($"Закупки: настройки нумерации — {next.PrefixReceipt}/{next.PrefixReturn}/{next.PrefixOrder}, цифр {next.Width}, сброс по годам {(next.ResetByYear ? "да" : "нет")}.", "INFO");
        Show(next);
    }
}
