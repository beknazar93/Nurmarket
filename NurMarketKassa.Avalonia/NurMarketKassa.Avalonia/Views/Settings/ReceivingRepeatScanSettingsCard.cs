using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

/// <summary>2026-11-09 (владелец: «при повторном скане того же товара — спросить количество, и на закупках в десктопе»): карточка «Повторный скан в приёмке» в «Операциях».
/// Один тумблер «Спрашивать количество при повторном скане» (по умолчанию ВКЛ) — общий для «Склад → Приёмка» и «Закупки → Приём товара»; хранится в UserPreferences.AskQtyOnRepeatScan
/// на этом компьютере. Выключить стоит тем, кто набирает количество серией сканов «пик-пик».</summary>
public sealed class ReceivingRepeatScanSettingsCard : Border
{
    private static string T(string ru, string ky, string en, string tr, string uz) => Tr.T(ru, ky, en, tr, uz);

    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>Тумблер (для проверки).</summary>
    public CheckBox Toggle { get; }

    public ReceivingRepeatScanSettingsCard()
    {
        Classes.Add("SettingsCard");
        Padding = new Thickness(14);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = T("Повторный скан в приёмке", "Кабыл алууда кайра сканерлөө", "Repeat scan in receiving", "Kabulde tekrar okutma", "Qabulda qayta skanerlash"), Classes = { "SettingsCardTitle" }, FontSize = 14 });

        var label = new TextBlock
        {
            Text = T("Спрашивать количество при повторном скане", "Кайра сканерлегенде санын сурап туруу", "Ask for the quantity on a repeat scan", "Tekrar okutmada miktarı sor", "Qayta skanerlashda miqdorni so'rash"),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
        };
        label.Bind(TextBlock.ForegroundProperty, label.GetResourceObservable("BrushText"));
        Toggle = new CheckBox { Name = "AskQtyOnRepeatScanToggle", Classes = { "ToggleSwitch" }, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsChecked = UserPreferences.Instance.AskQtyOnRepeatScan };
        Avalonia.Automation.AutomationProperties.SetName(Toggle, label.Text);
        Grid.SetColumn(Toggle, 1);
        stack.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { label, Toggle } });

        var sec = (int)ReceivingRepeatScanGuard.DefaultResetAfter.TotalSeconds;
        var hint = new TextBlock
        {
            Text = T($"«Склад → Приёмка» и «Закупки → Приём товара»: если тот же штрихкод отсканирован второй раз подряд, программа останавливается и спрашивает «Сколько штук?» — вместо молчаливого +1. Скан другого товара, пауза больше {sec} секунд и закрытие приёмки начинают счёт заново. Выключите, если количество набираете серией сканов подряд — тогда каждый скан прибавляет единицу, как раньше.",
                $"«Кампа → Кабыл алуу» жана «Сатып алуулар → Товар кабыл алуу»: ошол эле штрихкод удаа эки жолу сканерленсе, программа токтоп «Канча даана?» деп сурайт — унчукпай +1 кошпойт. Башка товарды сканерлөө, {sec} секунддан узун тыныгуу жана кабыл алууну жабуу эсепти кайра баштайт. Санды удаа сканерлеп терсеңиз — өчүрүңүз, анда ар бир скан мурдагыдай бирди кошот.",
                $"“Warehouse → Receiving” and “Purchases → Receive goods”: if the same barcode is scanned a second time in a row, the program stops and asks “How many?” instead of silently adding +1. Scanning another product, a pause longer than {sec} seconds or closing the receiving starts the count anew. Turn it off if you build up the quantity with a series of scans — then every scan adds one, as before.",
                $"«Depo → Kabul» ve «Satın alma → Mal kabul»: aynı barkod art arda ikinci kez okutulursa program durur ve sessizce +1 eklemek yerine «Kaç adet?» diye sorar. Başka ürün okutma, {sec} saniyeden uzun ara ve kabulü kapatma sayımı yeniden başlatır. Miktarı art arda okutarak biriktiriyorsanız kapatın — o zaman her okutma eskisi gibi bir ekler.",
                $"«Ombor → Qabul» va «Xaridlar → Tovar qabul qilish»: bir xil shtrix-kod ketma-ket ikkinchi marta skanerlansa, dastur to'xtab, jimgina +1 qo'shmasdan «Nechta dona?» deb so'raydi. Boshqa mahsulotni skanerlash, {sec} soniyadan uzoq pauza va qabulni yopish hisobni qaytadan boshlaydi. Miqdorni ketma-ket skanerlab yig'sangiz — o'chiring, u holda har bir skan avvalgidek bittadan qo'shadi."),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
        stack.Children.Add(hint);
        Child = stack;

        // Выбор сохраняется сразу, как переключили (облачной синхронизации у настройки нет — только этот компьютер).
        Toggle.IsCheckedChanged += (_, _) =>
        {
            UserPreferences.Instance.AskQtyOnRepeatScan = Toggle.IsChecked == true;
            try { UserPreferences.Instance.SaveToDisk(); }
            catch (Exception ex) { PosLogger.Log($"Настройка «Спрашивать количество при повторном скане» не сохранена: {ex.Message}", "WARNING"); }
        };
    }
}
