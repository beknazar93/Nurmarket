using Avalonia.Controls;
using Avalonia.Interactivity;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.Services;

// 2026-11-13 (Л-5, «возврат без чека»): сервер NurCRM принимает возврат только по проданному чеку (pos/sales/{id}/return/ — строки sale_item_id); возврата «без исходной продажи» (отдельного
// документа со своим номером) у него нет — ТЗ Умару, п. «Возврат без исходной продажи». Денежного пути мимо сервера касса НЕ создаёт: кнопка объясняет, как найти чек и что делать, если чека нет.
// Когда сервер научится — здесь появится вход «Возврат без чека» с PIN старшего и записью в «Подозрительные операции» (правило FraudMonitor «возврат без чека» уже есть).
namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

public partial class ReturnSaleDialog
{
    private void InitNoReceiptButton()
    {
        var text = Tr.T("Нет чека?", "Чек жокпу?", "No receipt?", "Fiş yok mu?", "Chek yo'qmi?");
        NoReceiptButton.Content = text;
        Avalonia.Automation.AutomationProperties.SetName(NoReceiptButton, text);
    }

    internal static string NoReceiptText() => Tr.T(
        "Возврат без чека сервер NurCRM пока не принимает: каждый возврат должен опираться на проданный чек, иначе деньги и остаток товара не сойдутся.\n\n"
        + "Что сделать:\n1. Найдите чек в поле поиска: по товару, дате, сумме, имени или телефону клиента, последним 4 цифрам карты.\n"
        + "2. Чек старый — нажмите «Ещё» или выберите период в «Фильтрах».\n"
        + "3. Чека нет совсем (потерян, пробит на другой кассе) — позовите владельца: он решит вопрос и оформит корректировку на сайте NurCRM.",
        "Чексиз кайтарууну NurCRM сервери азырынча кабыл албайт: ар бир кайтаруу сатылган чекке таянышы керек, болбосо акча менен товардын калдыгы дал келбейт.\n\n"
        + "Эмне кылуу керек:\n1. Чекти издөө талаасынан табыңыз: товар, дата, сумма, кардардын аты же телефону, картанын акыркы 4 цифрасы боюнча.\n"
        + "2. Чек эски болсо — «Дагы» баскычын басыңыз же «Чыпкалардан» мезгилди тандаңыз.\n"
        + "3. Чек такыр жок болсо (жоголгон, башка кассада чыккан) — ээсин чакырыңыз: ал маселени чечип, NurCRM сайтында оңдоону жасайт.",
        "The NurCRM server does not accept a return without a receipt yet: every return must rest on a sold receipt, otherwise money and stock will not match.\n\n"
        + "What to do:\n1. Find the receipt with the search field: by product, date, amount, client name or phone, last 4 digits of the card.\n"
        + "2. Old receipt — press “More” or pick a period in “Filters”.\n"
        + "3. No receipt at all (lost, rung up on another till) — call the owner: they will decide and post a correction on the NurCRM website.",
        "NurCRM sunucusu henüz fişsiz iadeyi kabul etmiyor: her iade satılmış bir fişe dayanmalıdır, aksi halde para ve stok tutmaz.\n\n"
        + "Ne yapmalı:\n1. Fişi arama alanıyla bulun: ürün, tarih, tutar, müşteri adı veya telefonu, kartın son 4 hanesi ile.\n"
        + "2. Fiş eskiyse — «Daha fazla»ya basın veya «Filtreler»den dönem seçin.\n"
        + "3. Fiş hiç yoksa (kayıp, başka kasada kesilmiş) — sahibini çağırın: o karar verir ve NurCRM sitesinde düzeltme yapar.",
        "NurCRM serveri hozircha cheksiz qaytarishni qabul qilmaydi: har bir qaytarish sotilgan chekka tayanishi kerak, aks holda pul va tovar qoldig'i mos kelmaydi.\n\n"
        + "Nima qilish kerak:\n1. Chekni qidiruv maydonidan toping: mahsulot, sana, summa, mijoz ismi yoki telefoni, kartaning oxirgi 4 raqami bo'yicha.\n"
        + "2. Chek eski bo'lsa — «Yana»ni bosing yoki «Filtrlar»dan davrni tanlang.\n"
        + "3. Chek umuman yo'q bo'lsa (yo'qolgan, boshqa kassada chiqarilgan) — egasini chaqiring: u masalani hal qilib, NurCRM saytida tuzatish kiritadi.");

    private void NoReceipt_Click(object? sender, RoutedEventArgs e)
    {
        PosLogger.Log("Возврат: нажато «Нет чека?» — показано объяснение (возврат без исходной продажи сервер не принимает).", "INFO");
        PosMessageBox.Show(this, NoReceiptText(), Tr.T("Возврат без чека", "Чексиз кайтаруу", "Return without a receipt", "Fişsiz iade", "Cheksiz qaytarish"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
