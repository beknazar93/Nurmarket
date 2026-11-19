using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using NurMarketKassa.AvaloniaHost.Views.Dialogs;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Views.Settings;

public partial class AccountView
{
    /// <summary>2026-11-19 (оплата подписки через ELQR): карточка «Оплата подписки» с кнопкой «Оплатить подписку…» — только владельцу/админу и только при признаке сервера billing_elqr
    /// (признаки могут прийти позже открытия раздела — карточка появляется по событию ServerCapabilities.Changed). Кассир видит только «Подписка до …» в карточке «Срок действия».</summary>
    private Border BuildSubscriptionCard()
    {
        var button = new Button { Content = Tr.T("Оплатить подписку…", "Жазылууну төлөө…", "Pay for the subscription…", "Aboneliği öde…", "Obunani to'lash…"), HorizontalAlignment = HorizontalAlignment.Left };
        button.Classes.Add("btn-primary");
        button.Click += (_, _) =>
        {
            try
            {
                SubscriptionWindow.ShowOrActivate(TopLevel.GetTopLevel(this) as Window);
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Окно «Подписка» не открылось: {ex.GetType().Name}", "WARNING");
            }
        };

        var card = Card(
            Title(Tr.T("Оплата подписки", "Жазылууну төлөө", "Subscription payment", "Abonelik ödemesi", "Obuna to'lovi")),
            Soft(Tr.T("Продлите подписку NurCRM через ELQR — оплатите QR в мобильном приложении любого банка. Комиссию банка платит клиент; тариф и срок видны в окне оплаты.",
                "NurCRM жазылуусун ELQR аркылуу узартыңыз — QR кодду каалаган банктын мобилдик тиркемесинде төлөңүз. Банктын комиссиясын кардар төлөйт; тариф жана мөөнөт төлөө терезесинде көрүнөт.",
                "Renew the NurCRM subscription through ELQR — pay the QR in any bank's mobile app. The bank fee is paid by the customer; the plan and term are shown in the payment window.",
                "NurCRM aboneliğini ELQR ile yenileyin — QR'ı herhangi bir bankanın mobil uygulamasında ödeyin. Banka komisyonunu müşteri öder; tarife ve süre ödeme penceresinde görünür.",
                "NurCRM obunasini ELQR orqali uzaytiring — QR ni istalgan bankning mobil ilovasida to'lang. Bank komissiyasini mijoz to'laydi; tarif va muddat to'lov oynasida ko'rinadi.")),
            button);

        card.IsVisible = SubscriptionWindow.CanOffer();
        void Refresh() => Dispatcher.UIThread.Post(() => card.IsVisible = SubscriptionWindow.CanOffer());
        card.AttachedToVisualTree += (_, _) =>
        {
            ServerCapabilities.Changed += Refresh;
            card.IsVisible = SubscriptionWindow.CanOffer();
        };
        card.DetachedFromVisualTree += (_, _) => ServerCapabilities.Changed -= Refresh;
        return card;
    }
}
