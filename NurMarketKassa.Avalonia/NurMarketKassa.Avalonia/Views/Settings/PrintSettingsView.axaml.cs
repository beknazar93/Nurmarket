using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class PrintSettingsView : UserControl
{
    public event EventHandler? SaveRequested;

    /// <summary>2026-11-01: блоки «Сеть (LAN)» принтера чеков и принтера этикеток.</summary>
    public NetworkPrinterPanel ReceiptNetPanel { get; } = new(showUseButton: false);
    public NetworkPrinterPanel LabelNetPanel { get; } = new(showUseButton: true);

    public PrintSettingsView()
    {
        InitializeComponent();
        // 2026-10-27, К-П3: карточка «Телефон как сканер» (только касса Windows/Linux, не программа владельца).
        if (NurMarketKassa.AvaloniaHost.Services.PhoneLinkHost.Supported)
            PhoneLinkHost.Content = new PhoneLinkSettingsView();
        // 2026-10-20 (живой аудит кассы, К7): «Печатать чек после оплаты» включён, а в окне оплаты «Напечатать чек»
        // выключен — два переключателя путали. Подпись: этот — главный; окно оплаты помнит последний выбор кассира.
        PrintSwitchHint.Text = NurMarketKassa.Services.Tr.T(
            "Главный выключатель печати: без него чеки не печатаются. В окне оплаты «Напечатать чек» запоминает последний выбор кассира; включение здесь снова включает его. Чеки возврата и внесения/изъятия печатаются по этому выключателю.",
            "Басып чыгаруунун негизги которгучу: ансыз чек басылбайт. Төлөм терезесиндеги «Чекти басып чыгаруу» кассирдин акыркы тандоосун эстейт; бул жерден күйгүзүү аны кайра күйгүзөт. Кайтаруу жана акча салуу/алуу чектери ушул которгуч боюнча басылат.",
            "Main printing switch: without it no receipts are printed. “Print receipt” in the payment window remembers the cashier's last choice; turning this on turns it back on. Return and cash in/out receipts follow this switch.",
            "Ana yazdırma anahtarı: kapalıyken fiş yazdırılmaz. Ödeme penceresindeki «Fiş yazdır» kasiyerin son seçimini hatırlar; burada açmak onu da yeniden açar. İade ve para giriş/çıkış fişleri bu anahtara göre yazdırılır.",
            "Chop etishning asosiy kaliti: usiz chek chop etilmaydi. To'lov oynasidagi «Chekni chop etish» kassirning oxirgi tanlovini eslab qoladi; bu yerda yoqish uni qayta yoqadi. Qaytarish va kirim/chiqim cheklari shu kalit bo'yicha chop etiladi.");
        ReceiptNetHost.Content = ReceiptNetPanel;
        LabelNetHost.Content = LabelNetPanel;
        // 2026-11-01 (просьба владельца: два принтера разной модели одновременно): подписи карточки «Принтер этикеток и штрихкодов».
        LabelPrinterTitleText.Text = NurMarketKassa.Services.Tr.T(
            "Принтер этикеток и штрихкодов",
            "Этикетка жана штрих-код принтери",
            "Label and barcode printer",
            "Etiket ve barkod yazıcısı",
            "Yorliq va shtrix-kod printeri");
        LabelPrinterHintText.Text = NurMarketKassa.Services.Tr.T(
            "Штрихкоды, ценники и этикетки печатаются сразу на этот принтер — отдельно от принтера чеков. Оба принтера работают одновременно и не мешают друг другу.",
            "Штрих-коддор, баа белгилери жана этикеткалар түз ушул принтерге басылат — чек принтеринен өзүнчө. Эки принтер тең бир убакта иштеп, бири-бирине тоскоол болбойт.",
            "Barcodes, price tags and labels are printed straight to this printer, separately from the receipt printer. Both printers work at the same time and do not interfere.",
            "Barkodlar, fiyat etiketleri ve etiketler doğrudan bu yazıcıya basılır — fiş yazıcısından ayrı. İki yazıcı aynı anda çalışır ve birbirini engellemez.",
            "Shtrix-kodlar, narx yorliqlari va yorliqlar to'g'ridan-to'g'ri shu printerga chop etiladi — chek printeridan alohida. Ikkala printer bir vaqtda ishlaydi va bir-biriga xalaqit bermaydi.");
        BtnForgetLabelPrinter.Content = NurMarketKassa.Services.Tr.T("Забыть принтер", "Принтерди унутуу", "Forget printer", "Yazıcıyı unut", "Printerni unutish");
        LabelPrinterCombo.PlaceholderText = NurMarketKassa.Services.Tr.T(
            "Принтер этикеток не выбран", "Этикетка принтери тандалган эмес", "No label printer selected",
            "Etiket yazıcısı seçilmedi", "Yorliq printeri tanlanmagan");
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);
}
