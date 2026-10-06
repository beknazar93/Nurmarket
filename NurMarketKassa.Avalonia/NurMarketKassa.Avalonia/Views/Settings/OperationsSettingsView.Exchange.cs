using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-10-06, исследование «Кассы для одежды» (О-31, О-32), владелец: «делай всё по этапно». Карточка
/// «Обмен и возврат» в сфере «Одежда»: срок (закон КР — 14 дней, не считая дня покупки) и категории, которые без брака
/// не обменивают и не возвращают. Касса по ним предупреждает при возврате и обмене (ReturnSaleDialog), но не запрещает.</summary>
public partial class OperationsSettingsView
{
    private void RefreshExchangeRulesCard()
    {
        ExchangeRulesCard.IsVisible = MarketSpheres.IsClothing;
        ExchangeRulesPanel.Children.Clear();
        if (!ExchangeRulesCard.IsVisible)
            return;

        var title = new TextBlock { Classes = { "SettingsCardTitle" }, FontSize = 14,
            Text = Tr.T("Обмен и возврат", "Алмаштыруу жана кайтаруу", "Exchange and returns", "Değişim ve iade", "Almashtirish va qaytarish") };
        ExchangeRulesPanel.Children.Add(title);
        ExchangeRulesPanel.Children.Add(Soft(Tr.T(
            "По закону товар без брака обменивают и возвращают в течение 14 дней, не считая дня покупки. Позже касса предупредит и попросит подтвердить: брак принимают дольше. 0 — срок не проверять.",
            "Мыйзам боюнча бузук эмес товар сатып алган күндү эсептебегенде 14 күндүн ичинде алмаштырылат жана кайтарылат. Кийин касса эскертип, ырастоону сурайт: бузук товар кечирээк да кабыл алынат. 0 — мөөнөт текшерилбейт.",
            "By law, items without defects can be exchanged or returned within 14 days, not counting the day of purchase. After that the till warns and asks to confirm: defective items are accepted longer. 0 — don't check.",
            "Kanuna göre kusursuz ürün, satın alma günü sayılmadan 14 gün içinde değiştirilir ve iade edilir. Sonrasında kasa uyarır ve onay ister: kusurlu ürün daha uzun süre kabul edilir. 0 — süre kontrol edilmez.",
            "Qonun bo'yicha nuqsonsiz mahsulot xarid kunini hisoblamaganda 14 kun ichida almashtiriladi va qaytariladi. Keyin kassa ogohlantiradi va tasdiqlashni so'raydi: nuqsonli mahsulot uzoqroq qabul qilinadi. 0 — muddat tekshirilmaydi.")));

        var daysRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var daysLabel = new TextBlock { Text = Tr.T("Срок, дней:", "Мөөнөт, күн:", "Period, days:", "Süre, gün:", "Muddat, kun:"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        daysLabel.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        var daysBox = new TextBox { Width = 90, Text = UserPreferences.Instance.ExchangeDaysLimit.ToString(CultureInfo.InvariantCulture), MaxLength = 3 };
        daysBox.TextChanged += (_, _) =>
        {
            if (!int.TryParse((daysBox.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days > 365)
                return;
            var prefs = UserPreferences.Instance;
            if (prefs.ExchangeDaysLimit == days)
                return;
            prefs.ExchangeDaysLimit = days;
            prefs.SaveToDisk();
        };
        daysRow.Children.Add(daysLabel);
        daysRow.Children.Add(daysBox);
        ExchangeRulesPanel.Children.Add(daysRow);

        var catTitle = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Text = Tr.T("Категории, которые без брака не обменивают и не возвращают", "Бузук болбосо алмаштырылбаган жана кайтарылбаган категориялар",
                "Categories not exchanged or returned unless defective", "Kusurlu değilse değiştirilmeyen ve iade edilmeyen kategoriler",
                "Nuqsonsiz bo'lsa almashtirilmaydigan va qaytarilmaydigan toifalar") };
        catTitle.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushText"));
        ExchangeRulesPanel.Children.Add(catTitle);
        ExchangeRulesPanel.Children.Add(Soft(Tr.T(
            "Перечень по закону: нижнее бельё, чулки и носки, парфюмерия и косметика, ювелирные изделия, лекарства, книги. Пока вы не меняли список, касса отмечает такие категории сама — поправьте под свой магазин.",
            "Мыйзам боюнча тизме: ич кийим, байпак жана колготки, атыр жана косметика, зер буюмдар, дары-дармек, китептер. Тизмени өзгөртө элек болсоңуз, касса мындай категорияларды өзү белгилейт — дүкөнүңүзгө ылайыкташтырыңыз.",
            "The legal list: underwear, hosiery and socks, perfume and cosmetics, jewellery, medicines, books. Until you change the list, the till marks such categories itself — adjust it to your store.",
            "Yasal liste: iç çamaşırı, çorap, parfüm ve kozmetik, mücevher, ilaç, kitap. Listeyi değiştirene kadar kasa bu kategorileri kendisi işaretler — mağazanıza göre düzeltin.",
            "Qonun bo'yicha ro'yxat: ichki kiyim, paypoq, atir va kosmetika, zargarlik buyumlari, dorilar, kitoblar. Ro'yxatni o'zgartirmaguningizcha kassa bunday toifalarni o'zi belgilaydi — do'koningizga moslang.")));

        var categories = NonExchangeableRules.AllCategories();
        if (categories.Count == 0)
        {
            ExchangeRulesPanel.Children.Add(Soft(Tr.T("В каталоге пока нет категорий — задайте их товарам на складе.", "Каталогдо азырынча категориялар жок — аларды кампадагы товарларга коюңуз.",
                "The catalog has no categories yet — set them on products in the warehouse.", "Katalogda henüz kategori yok — depodaki ürünlere atayın.",
                "Katalogda hozircha toifalar yo'q — ularni ombordagi mahsulotlarga belgilang.")));
            return;
        }
        var selected = NonExchangeableRules.SelectedCategories();
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        var boxes = new List<CheckBox>();
        foreach (var category in categories)
        {
            var box = new CheckBox { Content = category, IsChecked = selected.Contains(category), Margin = new Thickness(0, 0, 14, 4), FontSize = 13 };
            box.Bind(CheckBox.ForegroundProperty, this.GetResourceObservable("BrushText"));
            box.IsCheckedChanged += (_, _) =>
            {
                NonExchangeableRules.SaveSelected(boxes.Where(b => b.IsChecked == true).Select(b => b.Content?.ToString() ?? ""));
                PosLogger.Log("Настройки: категории «без обмена» изменены.", "SETTINGS");
            };
            boxes.Add(box);
            wrap.Children.Add(box);
        }
        ExchangeRulesPanel.Children.Add(wrap);
    }

    private TextBlock Soft(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("BrushTextSoft"));
        return t;
    }
}
