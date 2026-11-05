using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NurMarketKassa.AvaloniaHost.Views;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-05 (волна 2-Б, функция 8): экранные кусочки «Чеки и бонусы в Telegram» — окно с QR для покупателя (из панели чека и из окна настроек)
/// и маленький блок QR для экрана покупателя. Данные и отправка — Infrastructure/TelegramReceipts; здесь только вид.</summary>
internal static class TelegramReceiptUi
{
    public static string CallToAction => Tr.T("Получайте чеки и бонусы в Telegram", "Чектерди жана бонустарды Telegram'да алыңыз", "Get receipts and bonuses in Telegram",
        "Fişleri ve bonusları Telegram'dan alın", "Chek va bonuslarni Telegram'da oling");

    /// <summary>Окно «QR: чеки в Telegram»: покупатель наводит камеру → бот → «Старт» → «Подтвердить номер».</summary>
    public static void ShowQr(Window? owner)
    {
        var link = TelegramReceiptLinks.ShopLink();
        var body = new StackPanel { Spacing = 12 };
        if (link is null)
        {
            body.Children.Add(new TextBlock
            {
                Text = Tr.T("Имя Telegram-бота магазина не сохранено: откройте «Настройки → Telegram-бот» и подключите бота.", "Дүкөндүн Telegram-ботунун аты сакталган эмес: «Жөндөөлөр → Telegram-бот» бөлүмүндө ботту туташтырыңыз.",
                    "The shop's Telegram bot name isn't saved: open “Settings → Telegram bot” and connect the bot.", "Mağaza Telegram botunun adı kayıtlı değil: «Ayarlar → Telegram botu» bölümünden botu bağlayın.",
                    "Do'konning Telegram-bot nomi saqlanmagan: «Sozlamalar → Telegram-bot» bo'limida botni ulang."),
                TextWrapping = TextWrapping.Wrap, FontSize = 14,
            });
        }
        else
        {
            var qr = new ReceiptQrView { Data = link, Size = 260, Dark = new SolidColorBrush(Color.Parse("#0B2A3C")), Light = Brushes.White };
            body.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(16), Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Center, Child = qr });
            var hint = new TextBlock
            {
                Text = Tr.T("Покупатель наводит камеру → открывается бот → «Старт» → «Подтвердить номер». Дальше чеки покупок по этому номеру приходят ему в Telegram сами. Отписаться — /stop.",
                    "Сатып алуучу камерасын багыттайт → бот ачылат → «Старт» → «Номерди ырастоо». Андан ары ушул номер боюнча чектер Telegram'га өзү келет. Баш тартуу — /stop.",
                    "The customer points the camera → the bot opens → “Start” → “Confirm my number”. After that receipts of purchases with this number come to Telegram by themselves. Opt out — /stop.",
                    "Müşteri kamerayı tutar → bot açılır → «Başlat» → «Numaramı doğrula». Sonra bu numarayla yapılan alışverişlerin fişleri Telegram'a kendiliğinden gelir. Vazgeçmek için — /stop.",
                    "Xaridor kamerani yo'naltiradi → bot ochiladi → «Start» → «Raqamni tasdiqlash». Keyin shu raqam bilan xaridlar cheklari Telegram'ga o'zi keladi. Voz kechish — /stop."),
                FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            };
            hint.Bind(TextBlock.ForegroundProperty, hint.GetResourceObservable("BrushTextSoft"));
            body.Children.Add(hint);
            var linkText = new TextBlock { Text = link, FontSize = 12, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, IsHitTestVisible = true };
            linkText.Bind(TextBlock.ForegroundProperty, linkText.GetResourceObservable("BrushTextSoft"));
            body.Children.Add(linkText);
        }
        var close = SectionCards.Cancel(Tr.T("Закрыть", "Жабуу", "Close", "Kapat", "Yopish"));
        var w = new Window();
        SectionCards.AsModal(w, 460);
        w.Title = CallToAction;
        w.Content = SectionCards.Shell(w.Title, null, body, SectionCards.Buttons(close));
        close.Click += (_, _) => w.Close();
        _ = PosDialogHost.ShowAsync(w, owner);
    }

    /// <summary>Маленький блок для экрана покупателя: QR + подпись. null — выключено в настройках или нет имени бота (экран остаётся как раньше).</summary>
    public static Control? BuildCustomerBlock(double qrSize = 96)
    {
        try
        {
            var r = AntiFraudSettingsStore.Current.Receipts;
            if (!r.Enabled || !r.QrOnCustomerDisplay || TelegramReceiptLinks.ShopLink() is not { } link)
                return null;
            var qr = new ReceiptQrView { Data = link, Size = qrSize, Dark = new SolidColorBrush(Color.Parse("#0B2A3C")), Light = Brushes.White };
            var caption = new TextBlock { Text = CallToAction, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 170 };
            caption.Bind(TextBlock.ForegroundProperty, caption.GetResourceObservable("BrushTextSoft"));
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0),
                Children = { new Border { Background = Brushes.White, CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = qr }, caption },
            };
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Чеки в Telegram: QR для экрана покупателя не построен ({ex.GetType().Name}).", "WARNING");
            return null;
        }
    }

    /// <summary>Строка результата ручной отправки чека кнопкой «Telegram-бот магазина».</summary>
    public static string ResultText(TelegramReceiptSendResult result) => result switch
    {
        TelegramReceiptSendResult.Sent => Tr.T("Чек отправлен покупателю в Telegram.", "Чек сатып алуучуга Telegram аркылуу жөнөтүлдү.", "The receipt was sent to the customer in Telegram.", "Fiş müşteriye Telegram'dan gönderildi.", "Chek xaridorga Telegram orqali yuborildi."),
        TelegramReceiptSendResult.Off => Tr.T("Чеки в Telegram выключены: включите их в «Подозрительные операции → Чеки в Telegram» и подключите бота.", "Telegram'да чектер өчүк: «Шектүү операциялар → Telegram'да чектер» бөлүмүндө күйгүзүп, ботту туташтырыңыз.",
            "Receipts in Telegram are off: turn them on in “Suspicious operations → Receipts in Telegram” and connect the bot.", "Telegram'da fiş kapalı: «Şüpheli işlemler → Telegram'da fişler» bölümünden açıp botu bağlayın.", "Telegram'da cheklar o'chiq: «Shubhali amallar → Telegram'da cheklar» bo'limida yoqing va botni ulang."),
        TelegramReceiptSendResult.NoPhone => Tr.T("У покупателя нет телефона в чеке.", "Чекте сатып алуучунун телефону жок.", "The customer has no phone on the receipt.", "Fişte müşteri telefonu yok.", "Chekda xaridorning telefoni yo'q."),
        TelegramReceiptSendResult.NotLinked => Tr.T("Этот номер не привязан к боту: покажите покупателю QR «Чеки в Telegram» — он нажмёт «Старт» и подтвердит номер.", "Бул номер ботко байланган эмес: сатып алуучуга «Telegram'да чектер» QR көрсөтүңүз — ал «Старт» басып, номерин ырастайт.",
            "This number isn't linked to the bot: show the customer the “Receipts in Telegram” QR — they press Start and confirm the number.", "Bu numara bota bağlı değil: müşteriye «Telegram'da fişler» QR'ını gösterin — Başlat'a basıp numarayı doğrulasın.", "Bu raqam botga bog'lanmagan: xaridorga «Telegram'da cheklar» QR ini ko'rsating — u Start bosib raqamni tasdiqlaydi."),
        _ => Tr.T("Не удалось отправить чек в Telegram.", "Чекти Telegram'га жөнөтүү мүмкүн болгон жок.", "Could not send the receipt in Telegram.", "Fiş Telegram'a gönderilemedi.", "Chekni Telegram'ga yuborib bo'lmadi."),
    };
}
