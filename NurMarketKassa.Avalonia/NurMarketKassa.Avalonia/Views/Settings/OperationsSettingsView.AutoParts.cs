using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-20 (сфера «Автозапчасти», часть Б, ТЗ 19.11.2026 п. 4.6): карточка «Обмен и возврат» владельца — «Срок возврата, дней» и «Не принимать
/// установленные детали и электрику». Общие настройки сфер (SphereSettings, раздел autoparts); касса читает их в окне возврата и в печати чека.</summary>
public partial class OperationsSettingsView
{
    private void AddAutoPartsReturnSettings()
    {
        var title = new TextBlock { Classes = { "SettingsCardTitle" }, FontSize = 14,
            Text = Tr.T("Гарантия и возврат", "Кепилдик жана кайтаруу", "Warranty and returns", "Garanti ve iade", "Kafolat va qaytarish") };
        ExchangeRulesPanel.Children.Add(title);
        ExchangeRulesPanel.Children.Add(Soft(Tr.T(
            "Эти условия печатаются внизу чека. После срока и для электрики касса предупреждает и пускает дальше только по PIN руководителя; брак и гарантию оформляйте причиной возврата.",
            "Бул шарттар чектин ылдый жагына басылат. Мөөнөттөн кийин жана электрика үчүн касса эскертет жана жетекчинин PIN коду менен гана өткөрөт; бузук жана кепилдикти кайтаруу себеби менен тариздеңиз.",
            "These terms are printed at the bottom of the receipt. After the period and for electrical parts the till warns and continues only with the manager PIN; handle defects and warranty with a return reason.",
            "Bu koşullar fişin altına basılır. Süreden sonra ve elektrik aksamı için kasa uyarır ve yalnızca yönetici PIN'i ile devam eder; kusur ve garantiyi iade nedeniyle işleyin.",
            "Bu shartlar chek pastiga bosiladi. Muddatdan keyin va elektrika uchun kassa ogohlantiradi va faqat rahbar PIN kodi bilan davom etadi; nuqson va kafolatni qaytarish sababi bilan rasmiylashtiring.")));

        var daysRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var daysLabel = new TextBlock { Text = Tr.T("Срок возврата, дней:", "Кайтаруу мөөнөтү, күн:", "Return period, days:", "İade süresi, gün:", "Qaytarish muddati, kun:"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        daysLabel.Bind(TextBlock.ForegroundProperty, daysLabel.GetResourceObservable("BrushText"));
        var daysBox = new TextBox { Width = 90, Text = AutoPartsRules.GetReturnDays().ToString(CultureInfo.InvariantCulture), MaxLength = 3 };
        var noInstalled = new CheckBox { Content = Tr.T("Не принимать установленные детали и электрику", "Орнотулган тетиктерди жана электриканы кабыл албоо", "Do not accept installed parts and electrical parts", "Takılmış parçaları ve elektrik aksamını kabul etme", "O'rnatilgan detallar va elektrikani qabul qilmaslik"),
            IsChecked = AutoPartsRules.GetNoInstalled(), FontSize = 13, Margin = new Thickness(0, 4, 0, 0) };
        noInstalled.Bind(CheckBox.ForegroundProperty, noInstalled.GetResourceObservable("BrushText"));
        void Save()
        {
            if (!int.TryParse((daysBox.Text ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days > 365)
                return;
            if (days == AutoPartsRules.GetReturnDays() && (noInstalled.IsChecked == true) == AutoPartsRules.GetNoInstalled())
                return;
            AutoPartsRules.SetReturnTerms(days, noInstalled.IsChecked == true);
            PosLogger.Log($"Настройки: автозапчасти — срок возврата {days} дн., не принимать установленные: {noInstalled.IsChecked == true}.", "SETTINGS");
        }
        daysBox.TextChanged += (_, _) => Save();
        noInstalled.IsCheckedChanged += (_, _) => Save();
        daysRow.Children.Add(daysLabel);
        daysRow.Children.Add(daysBox);
        ExchangeRulesPanel.Children.Add(daysRow);
        ExchangeRulesPanel.Children.Add(noInstalled);
    }
}
