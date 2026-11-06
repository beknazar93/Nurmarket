using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class AccountView
{
    /// <summary>2026-11-06 (Л4-19, решение владельца): карточка «Просить PIN владельца при запуске на ПК с кассой» — только в программе владельца. По умолчанию выключено
    /// (у большинства PIN не задан, и без него на таком ПК приходилось входить паролем при каждом запуске). Выбор сохраняется сразу, как переключили.</summary>
    private void AddOwnerPinCard()
    {
        if (!AppMode.IsOwner || LogoutButton.Parent?.Parent?.Parent is not Panel list)
            return;

        var check = new CheckBox
        {
            IsChecked = UserPreferences.Instance.AskOwnerPinOnKassaPc,
            Content = Tr.T("Просить PIN владельца при запуске на ПК с кассой", "Кассасы бар ПКда ишке киргизгенде ээсинин PIN-кодун сурайт",
                "Ask for the owner PIN at start-up on a PC with a till", "Kasası olan bilgisayarda açılışta sahip PIN'ini iste", "Kassali kompyuterda ishga tushganda egasining PIN-kodini so'rash"),
            FontSize = 14,
        };
        Avalonia.Automation.AutomationProperties.SetName(check, Tr.T("PIN владельца при запуске", "Ишке киргизгенде ээсинин PIN-коду", "Owner PIN at start-up", "Açılışta sahip PIN'i", "Ishga tushganda egasining PIN-kodi"));
        check.IsCheckedChanged += (_, _) =>
        {
            UserPreferences.Instance.AskOwnerPinOnKassaPc = check.IsChecked == true;
            try { UserPreferences.Instance.SaveToDisk(); }
            catch (Exception ex) { PosLogger.Log($"Настройка PIN владельца при запуске не сохранена: {ex.Message}", "WARNING"); }
        };

        var hint = new TextBlock
        {
            Text = Tr.T("Если на этом ПК стоит и касса, кассир мог бы открыть программу владельца ярлыком. Включите — и после автовхода программа попросит PIN владельца (он должен быть задан в NurCRM); без PIN войти можно только паролем. Выключено — программа открывается сразу.",
                "Бул ПКда касса да турса, кассир ээсинин программасын жарлык менен ачып алышы мүмкүн. Күйгүзсөңүз — авто кирүүдөн кийин программа ээсинин PIN-кодун сурайт (ал NurCRM'де коюлган болушу керек); PIN жок болсо сырсөз менен гана кирүүгө болот. Өчүк — программа дароо ачылат.",
                "If a till is installed on this PC too, a cashier could open the owner program by its shortcut. Turn this on and after auto-login the program asks for the owner PIN (it must be set in NurCRM); without it you can only sign in with the password. Off — the program opens at once.",
                "Bu bilgisayarda kasa da varsa, kasiyer sahip programını kısayolla açabilir. Açarsanız otomatik girişten sonra program sahip PIN'ini ister (NurCRM'de ayarlı olmalı); PIN olmadan yalnızca şifreyle girilir. Kapalı — program hemen açılır.",
                "Bu kompyuterda kassa ham bo'lsa, kassir egasi dasturini yorliq orqali ochib qo'yishi mumkin. Yoqsangiz — avto-kirishdan keyin dastur egasining PIN-kodini so'raydi (u NurCRM'da o'rnatilgan bo'lishi kerak); PIN bo'lmasa faqat parol bilan kiriladi. O'chiq — dastur darrov ochiladi."),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));

        var card = new Border { Padding = new Thickness(18), Child = new StackPanel { Spacing = 6, Children = { check, hint } } };
        card.Classes.Add("SettingsCard");

        // Карточка встаёт перед «Выход» (последней карточкой списка).
        var logoutCard = LogoutButton.Parent?.Parent;
        var index = logoutCard is Control c ? list.Children.IndexOf(c) : -1;
        list.Children.Insert(index >= 0 ? index : list.Children.Count, card);
    }
}
