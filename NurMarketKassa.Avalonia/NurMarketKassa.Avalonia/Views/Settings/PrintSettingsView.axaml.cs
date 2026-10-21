using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class PrintSettingsView : UserControl
{
    public event EventHandler? SaveRequested;

    public PrintSettingsView()
    {
        InitializeComponent();
        // 2026-10-20 (живой аудит кассы, К7): «Печатать чек после оплаты» включён, а в окне оплаты «Напечатать чек»
        // выключен — два переключателя путали. Подпись: этот — главный; окно оплаты помнит последний выбор кассира.
        PrintSwitchHint.Text = NurMarketKassa.Services.Tr.T(
            "Главный выключатель печати: без него чеки не печатаются. В окне оплаты «Напечатать чек» запоминает последний выбор кассира; включение здесь снова включает его. Чеки возврата и внесения/изъятия печатаются по этому выключателю.",
            "Басып чыгаруунун негизги которгучу: ансыз чек басылбайт. Төлөм терезесиндеги «Чекти басып чыгаруу» кассирдин акыркы тандоосун эстейт; бул жерден күйгүзүү аны кайра күйгүзөт. Кайтаруу жана акча салуу/алуу чектери ушул которгуч боюнча басылат.",
            "Main printing switch: without it no receipts are printed. “Print receipt” in the payment window remembers the cashier's last choice; turning this on turns it back on. Return and cash in/out receipts follow this switch.",
            "Ana yazdırma anahtarı: kapalıyken fiş yazdırılmaz. Ödeme penceresindeki «Fiş yazdır» kasiyerin son seçimini hatırlar; burada açmak onu da yeniden açar. İade ve para giriş/çıkış fişleri bu anahtara göre yazdırılır.",
            "Chop etishning asosiy kaliti: usiz chek chop etilmaydi. To'lov oynasidagi «Chekni chop etish» kassirning oxirgi tanlovini eslab qoladi; bu yerda yoqish uni qayta yoqadi. Qaytarish va kirim/chiqim cheklari shu kalit bo'yicha chop etiladi.");
    }

    private void Save_Click(object? sender, RoutedEventArgs e) =>
        SaveRequested?.Invoke(this, EventArgs.Empty);
}
