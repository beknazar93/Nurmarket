using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views;

/// <summary>2026-10-03, владелец: «индикатор ИИ на сервере добавь у клиентов» и «почему у меня ИИ работает,
/// а у других нет». Метки состояния бота на сервере NurCRM — цветная точка и подпись: где работает бот,
/// работает ли ИИ (и почему нет — «нет ключа ИИ»), связь с Telegram, получатель. Общие для мастера бота
/// и раздела «Телеграм-бот» программы владельца.</summary>
internal static class TelegramBotStatusChips
{
    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#22A06B"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F5A524"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#2AABEE"));

    public static void Fill(Panel host, ServerBotSettings? s)
    {
        host.Children.Clear();
        if (s is null)
            return;
        var on = s.IsServerMode;
        host.Children.Add(Chip(on ? Blue : Warn, on
            ? Tr.T("Бот на сервере · отвечает всегда", "Бот серверде · дайыма жооп берет", "Bot on the server · always on", "Bot sunucuda · her zaman açık", "Bot serverda · doim javob beradi")
            : Tr.T("Бот на этом компьютере", "Бот бул компьютерде", "Bot on this computer", "Bot bu bilgisayarda", "Bot shu kompyuterda")));
        if (!on)
            return;
        var aiOn = s.AiEnabled && s.AiKeySet;
        host.Children.Add(Chip(aiOn ? Ok : Bad, aiOn
            ? Tr.T("ИИ работает", "ЖИ иштейт", "AI is working", "Yapay zekâ çalışıyor", "SI ishlayapti")
            : !s.AiKeySet
                ? Tr.T("ИИ не работает: нет ключа ИИ на сервере", "ЖИ иштебейт: серверде ЖИ ачкычы жок", "AI is off: no AI key on the server", "Yapay zekâ kapalı: sunucuda anahtar yok", "SI ishlamaydi: serverda SI kaliti yo'q")
                : Tr.T("ИИ выключен", "ЖИ өчүк", "AI is turned off", "Yapay zekâ kapalı", "SI o'chirilgan")));
        host.Children.Add(Chip(s.WebhookOk ? Ok : Bad, s.WebhookOk
            ? Tr.T("Связь с Telegram", "Telegram менен байланыш", "Telegram connected", "Telegram bağlı", "Telegram bilan aloqa")
            : Tr.T("Нет связи с Telegram", "Telegram менен байланыш жок", "No Telegram connection", "Telegram bağlantısı yok", "Telegram bilan aloqa yo'q")));
        var hasOwner = !string.IsNullOrWhiteSpace(s.OwnerChatId);
        host.Children.Add(Chip(hasOwner ? Ok : Warn, hasOwner
            ? Tr.T("Получатель сводок задан", "Сводка алуучу коюлган", "Report recipient set", "Rapor alıcısı ayarlı", "Hisobot qabul qiluvchisi belgilangan")
            : Tr.T("Нет получателя: напишите боту /start", "Алуучу жок: ботко /start жазыңыз", "No recipient: send /start to the bot", "Alıcı yok: bota /start yazın", "Qabul qiluvchi yo'q: botga /start yozing")));
        if (s.IsStuck)
            host.Children.Add(Chip(Bad, Tr.T("Не отвечает больше 2 минут", "2 мүнөттөн ашык жооп бербейт", "No replies for over 2 minutes", "2 dakikadan uzun süredir yanıt yok", "2 daqiqadan ko'proq javob yo'q")));
    }

    private static Control Chip(IBrush dot, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        row.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(5), Background = dot, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(label);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(11, 5),
            Margin = new Thickness(0, 0, 8, 8),
            BorderThickness = new Thickness(1),
            BorderBrush = dot,
            Background = new SolidColorBrush(((ISolidColorBrush)dot).Color, 0.10),
            Child = row,
        };
        label.Bind(TextBlock.ForegroundProperty, chip.GetResourceObservable("BrushText"));
        return chip;
    }
}
