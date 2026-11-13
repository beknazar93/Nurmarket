using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-12 (ТЗ «Сфера Электроника», ЭЛ6): в карточке «Обмен и возврат» сферы «Электроника» — «Серийные номера и гарантия»:
/// какие категории товаров требуют IMEI / серийный номер при продаже и срок гарантии (месяцев) по умолчанию и по категориям.
/// Хранится в <see cref="ElectronicsSettings"/> (файл этой кассы; общий ключ компании — пакет ХМ15). В других сферах не показывается.</summary>
public partial class OperationsSettingsView
{
    private void AddElectronicsSettings()
    {
        var current = ElectronicsSettings.Current;
        var title = new TextBlock { Classes = { "SettingsCardTitle" }, FontSize = 14, Margin = new Thickness(0, 10, 0, 0),
            Text = Tr.T("Серийные номера и гарантия", "Сериялык номерлер жана кепилдик", "Serial numbers and warranty", "Seri numaraları ve garanti", "Seriya raqamlari va kafolat") };
        ExchangeRulesPanel.Children.Add(title);
        ExchangeRulesPanel.Children.Add(Soft(Tr.T(
            "Отметьте категории, где при продаже спрашивается IMEI или серийный номер. Справа — срок гарантии в месяцах (пусто — по умолчанию). Номера и гарантия печатаются на чеке и в гарантийном талоне; пока номера хранятся только на этой кассе.",
            "Сатууда IMEI же сериялык номер сураган категорияларды белгилеңиз. Оң жакта — кепилдик мөөнөтү айлар менен (бош — демейки). Номерлер жана кепилдик чекте жана кепилдик талонунда басылат; азырынча номерлер ушул кассада гана сакталат.",
            "Tick the categories where the IMEI or serial number is asked at sale. On the right — the warranty period in months (empty — default). Numbers and warranty are printed on the receipt and the warranty card; for now numbers are stored only on this till.",
            "Satışta IMEI veya seri numarasının sorulacağı kategorileri işaretleyin. Sağda — garanti süresi (ay; boşsa varsayılan). Numaralar ve garanti fişe ve garanti belgesine yazdırılır; şimdilik numaralar yalnızca bu kasada saklanır.",
            "Sotuvda IMEI yoki seriya raqami so'raladigan toifalarni belgilang. O'ngda — kafolat muddati oylarda (bo'sh — odatiy). Raqamlar va kafolat chekda va kafolat talonida chop etiladi; hozircha raqamlar faqat shu kassada saqlanadi.")));

        var defaultRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var defaultLabel = new TextBlock { Text = Tr.T("Гарантия по умолчанию, мес.:", "Демейки кепилдик, ай:", "Default warranty, months:", "Varsayılan garanti, ay:", "Odatiy kafolat, oy:"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        defaultLabel.Bind(TextBlock.ForegroundProperty, defaultLabel.GetResourceObservable("BrushText"));
        var defaultBox = new TextBox { Width = 90, MaxLength = 3, Name = "ElectronicsDefaultWarranty", Text = current.DefaultWarrantyMonths.ToString(CultureInfo.InvariantCulture) };
        defaultRow.Children.Add(defaultLabel);
        defaultRow.Children.Add(defaultBox);
        ExchangeRulesPanel.Children.Add(defaultRow);

        var categories = NonExchangeableRules.AllCategories();
        if (categories.Count == 0)
        {
            ExchangeRulesPanel.Children.Add(Soft(Tr.T("В каталоге пока нет категорий — задайте их товарам на складе.", "Каталогдо азырынча категориялар жок — аларды кампадагы товарларга коюңуз.",
                "The catalog has no categories yet — set them on products in the warehouse.", "Katalogda henüz kategori yok — depodaki ürünlere atayın.",
                "Katalogda hozircha toifalar yo'q — ularni ombordagi mahsulotlarga belgilang.")));
        }

        var rows = new List<(string Category, CheckBox Serial, TextBox Months)>();
        void Save()
        {
            var settings = new ElectronicsSettings
            {
                SerialCategories = rows.Where(r => r.Serial.IsChecked == true).Select(r => r.Category).ToList(),
                DefaultWarrantyMonths = int.TryParse((defaultBox.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var d) && d <= 120 ? d : current.DefaultWarrantyMonths,
            };
            foreach (var r in rows)
                if (int.TryParse((r.Months.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m <= 120)
                    settings.WarrantyByCategory[r.Category] = m;
            ElectronicsSettings.Save(settings);
        }

        foreach (var category in categories)
        {
            var serial = new CheckBox { Content = category, IsChecked = ElectronicsProfile.NeedsSerialIn(category, current.SerialCategories), FontSize = 13, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center };
            serial.Bind(CheckBox.ForegroundProperty, serial.GetResourceObservable("BrushText"));
            var months = new TextBox { Width = 70, MaxLength = 3, VerticalAlignment = VerticalAlignment.Center,
                Watermark = Tr.T("мес.", "ай", "mo.", "ay", "oy"),
                Text = current.WarrantyByCategory.FirstOrDefault(kv => string.Equals(kv.Key, category, StringComparison.CurrentCultureIgnoreCase)) is { Key: not null } kv2 && kv2.Key.Length > 0
                    ? kv2.Value.ToString(CultureInfo.InvariantCulture) : "" };
            serial.IsCheckedChanged += (_, _) => Save();
            months.LostFocus += (_, _) => Save();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(serial);
            row.Children.Add(months);
            rows.Add((category, serial, months));
            ExchangeRulesPanel.Children.Add(row);
        }
        defaultBox.LostFocus += (_, _) => Save();
    }
}
