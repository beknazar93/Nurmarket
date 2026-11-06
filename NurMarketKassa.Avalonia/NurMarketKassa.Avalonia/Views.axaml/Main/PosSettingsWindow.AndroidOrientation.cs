#if NURANDROID
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NurMarketKassa.Droid;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views
{
    /// <summary>2026-11-06: В4 А4-20 — на Android-кассе в «Экран → Производительность» добавлена строка «Ориентация экрана» (авто / только альбомная / только
    /// книжная). Планшет на подставке, повёрнутый при оплате, перестраивал главное окно и окно оплаты. Выбор применяется сразу и хранится на этом
    /// аппарате (SharedPreferences Android, не в настройках кассы и не в облаке). На Windows и Linux этого кода нет.</summary>
    public partial class PosSettingsWindow
    {
        private bool _orientationRowAdded;

        private void AddAndroidOrientationRow()
        {
            if (_orientationRowAdded || LowPerformanceModeCheck.Parent?.Parent is not Panel container)
                return;
            _orientationRowAdded = true;

            var combo = new ComboBox { MinWidth = 200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            var items = new[]
            {
                (AndroidOrientation.Auto, Tr.T("Авто (по датчику)", "Авто (сенсор боюнча)", "Auto (by sensor)", "Otomatik (sensöre göre)", "Avto (datchik bo'yicha)")),
                (AndroidOrientation.Landscape, Tr.T("Только альбомная", "Жатуу гана", "Landscape only", "Yalnızca yatay", "Faqat albom")),
                (AndroidOrientation.Portrait, Tr.T("Только книжная", "Тик гана", "Portrait only", "Yalnızca dikey", "Faqat kitob")),
            };
            foreach (var (tag, text) in items)
                combo.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            var current = AndroidOrientation.Current;
            combo.SelectedIndex = Math.Max(0, Array.FindIndex(items, i => i.Item1 == current));
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is ComboBoxItem { Tag: string tag })
                    AndroidOrientation.Set(tag);
            };
            Avalonia.Automation.AutomationProperties.SetName(combo, Tr.T("Ориентация экрана", "Экрандын багыты", "Screen orientation", "Ekran yönü", "Ekran yo'nalishi"));

            var label = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = Tr.T("Ориентация экрана", "Экрандын багыты", "Screen orientation", "Ekran yönü", "Ekran yo'nalishi"), FontSize = 13, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center },
                    new TextBlock
                    {
                        Text = Tr.T("Закрепите, если планшет стоит на подставке: оплата не перестроится при случайном повороте.",
                            "Планшет подставкада турса бекитиңиз: кокус бурулганда төлөм терезеси кайра курулбайт.",
                            "Lock it if the tablet sits on a stand: the payment window will not rebuild on an accidental turn.",
                            "Tablet bir standdaysa sabitleyin: yanlışlıkla döndürmede ödeme penceresi yeniden kurulmaz.",
                            "Planshet tagligda tursa mahkamlang: tasodifiy burilganda to'lov oynasi qayta qurilmaydi."),
                        FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                    },
                },
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(label);
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);

            var index = container.Children.IndexOf((Control)LowPerformanceModeCheck.Parent!) + 1;
            container.Children.Insert(Math.Min(index, container.Children.Count), row);
        }
    }
}
#endif
