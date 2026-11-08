using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Services;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class AccountView
{
    /// <summary>2026-11-08 (ТЗ п. 32 и 34): две карточки перед «Выход» — «Скорость связи» (замер для любого вошедшего сотрудника) и «Тариф «Своё»» (только владелец, в программе владельца).</summary>
    private void AddSpeedAndTariffCards()
    {
        if (LogoutButton.Parent?.Parent?.Parent is not Panel list)
            return;
        var logoutCard = LogoutButton.Parent?.Parent;
        void Put(Control card)
        {
            var index = logoutCard is Control c ? list.Children.IndexOf(c) : -1;
            list.Children.Insert(index >= 0 ? index : list.Children.Count, card);
        }

        Put(BuildSpeedCard());
        if (AppMode.IsOwner && IsOwnerRole())
            Put(BuildTariffCard());
    }

    private static TextBlock Soft(string text, double size = 12)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushTextSoft"));
        return t;
    }

    private static Border Card(params Control[] children)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in children)
            panel.Children.Add(c);
        var card = new Border { Padding = new Thickness(18), Child = panel };
        card.Classes.Add("SettingsCard");
        return card;
    }

    private static TextBlock Title(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold };
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("BrushText"));
        return t;
    }

    /// <summary>Роль «владелец» из профиля (админу тариф «Своё» сервер не отдаёт — 403).</summary>
    private static bool IsOwnerRole()
    {
        try
        {
            var user = App.GetRequiredService<NurMarketApiClient>().UserPayload;
            return user.ValueKind == System.Text.Json.JsonValueKind.Object
                   && user.TryGetProperty("role", out var role) && role.ValueKind == System.Text.Json.JsonValueKind.String
                   && string.Equals(role.GetString(), "owner", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Карточка «Своё»: роль не определена: {ex.Message}", "WARNING");
            return false;
        }
    }

    private Border BuildSpeedCard()
    {
        var button = new Button { Content = Tr.T("Проверить скорость связи", "Байланыштын ылдамдыгын текшерүү", "Check connection speed", "Bağlantı hızını kontrol et", "Aloqa tezligini tekshirish"), HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.Add("btn-secondary");
        var result = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        result.Bind(TextBlock.ForegroundProperty, result.GetResourceObservable("BrushText"));

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            result.IsVisible = true;
            result.Text = SpeedTestText.StepLabel("ping");
            try
            {
                var test = App.GetRequiredService<ConnectionSpeedTest>();
                var r = await test.RunAsync(step: s => Dispatcher.UIThread.Post(() => result.Text = SpeedTestText.StepLabel(s))).ConfigureAwait(true);
                result.Text = SpeedTestText.Format(r);
                PosLogger.Log($"Замер скорости: {r.Status}, задержка {r.LatencyMs} мс, вниз {r.DownMbps} Мбит/с, вверх {r.UpMbps} Мбит/с", "INFO");
            }
            catch (Exception ex)
            {
                result.Text = Tr.T("Замер не удался: ", "Өлчөө ишке ашкан жок: ", "The test failed: ", "Ölçüm başarısız: ", "O'lchash amalga oshmadi: ") + ServerOutageMonitor.FriendlyError(ex);
                PosLogger.Log($"Замер скорости не удался: {ex.Message}", "WARNING");
            }
            finally
            {
                button.IsEnabled = true;
            }
        };

        return Card(
            Title(Tr.T("Скорость связи", "Байланыштын ылдамдыгы", "Connection speed", "Bağlantı hızı", "Aloqa tezligi")),
            Soft(Tr.T("Замер задержки, скачивания и отдачи до сервера NurCRM (около 7 МБ трафика). Сервер пускает его не чаще раза в минуту.",
                "NurCRM серверине чейин кармоо убактысын, жүктөп алууну жана жөнөтүүнү өлчөө (болжол менен 7 МБ трафик). Сервер аны мүнөтүнө бир жолудан көп жасатпайт.",
                "Measures latency, download and upload to the NurCRM server (about 7 MB of traffic). The server allows it no more than once a minute.",
                "NurCRM sunucusuna gecikme, indirme ve yükleme ölçümü (yaklaşık 7 MB trafik). Sunucu bunu dakikada en fazla bir kez yapmaya izin verir.",
                "NurCRM serveriga kechikish, yuklab olish va yuborishni o'lchash (taxminan 7 MB trafik). Server buni daqiqasiga bir martadan ko'p bermaydi.")),
            button, result);
    }

    private Border BuildTariffCard()
    {
        var button = new Button { Content = Tr.T("Собрать тариф «Своё»…", "«Өзүмдүкү» тарифин түзүү…", "Build the “Custom” plan…", "«Özel» tarifeyi oluştur…", "«O'zimniki» tarifini yig'ish…"), HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.Add("btn-secondary");
        button.Click += async (_, _) =>
        {
            var owner = TopLevel.GetTopLevel(this) as Window;
            try
            {
                await PosDialogHost.ShowAsync(new TariffCustomWindow(App.GetRequiredService<TariffApi>()), owner).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Окно «Своё» не открылось: {ex}", "WARNING");
            }
        };

        return Card(
            Title(Tr.T("Тариф «Своё»", "«Өзүмдүкү» тарифи", "“Custom” plan", "«Özel» tarife", "«O'zimniki» tarifi")),
            Soft(Tr.T("Выберите нужные функции поверх «Старта»: цены считает сервер. Заявку обработает менеджер и свяжется с вами.",
                "«Старттын» үстүнөн керектүү функцияларды тандаңыз: баасын сервер эсептейт. Өтүнүчтү менеджер иштетип, сиз менен байланышат.",
                "Pick the features you need on top of “Start”: the server calculates the prices. A manager will process the request and contact you.",
                "«Start» üzerine ihtiyacınız olan özellikleri seçin: fiyatları sunucu hesaplar. Talebi bir yönetici işleyip sizinle iletişime geçer.",
                "«Start» ustiga kerakli funksiyalarni tanlang: narxni server hisoblaydi. Arizani menejer ko'rib chiqib, siz bilan bog'lanadi.")),
            button);
    }
}
