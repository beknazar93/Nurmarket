using NurMarketKassa.Services;
using System;
using System.ComponentModel;
using System.Linq;

namespace NurMarketKassa.ViewModels
{
    // 2026-10-30: редизайн окна «Оплата покупки» (docs/Дизайн/Окно оплаты — редизайн (30.10.2026).md).
    // Здесь только свойства ДЛЯ ПОКАЗА (подписи, строки итога, плашка «Сдача / Не хватает / В долг уйдёт», чип клиента).
    // Расчёт денег (CanExecutePay, CashReceivedForApi, бонусы, итог) не затронут — всё читается из тех же полей.
    public partial class CheckoutViewModel
    {
        // ------------------------------------------------------------------ подписи (5 языков, как TotalDueLabel)
        public string SubtotalLabel => Tr.T("Сумма позиций", "Саптардын суммасы", "Items total", "Kalem toplamı", "Pozitsiyalar summasi");
        public string LineDiscountsLabel => Tr.T("Скидки по строкам", "Саптар боюнча арзандатуулар", "Line discounts", "Satır indirimleri", "Qatorlar bo'yicha chegirmalar");
        public string OrderDiscountLabel => Tr.T("Скидка на чек", "Чекке арзандатуу", "Receipt discount", "Fiş indirimi", "Chek chegirmasi");
        public string BonusesLabel => Tr.T("Бонусами", "Бонус менен", "With bonuses", "Puanla", "Bonus bilan");
        public string MoneyUnitText => Tr.T("сом", "сом", "som", "som", "so'm");
        public string ExactCashLabel => $"{Tr.T("Ровно", "Так", "Exact", "Tam", "Aniq")} {MoneyText.Amount(_effectiveTotalDue)}";
        public string QrCardTitle => Tr.T("QR для оплаты", "Төлөм үчүн QR", "QR for payment", "Ödeme için QR", "To'lov uchun QR");
        public string QrManualHint => Tr.T("Enter подтверждает вручную — QR не обязателен", "Enter кол менен ырастайт — QR милдеттүү эмес",
            "Enter confirms manually — QR is optional", "Enter elle onaylar — QR zorunlu değil", "Enter qo'lda tasdiqlaydi — QR shart emas");
        public string OfflineBadgeText => Tr.T("Без связи — чек уйдёт в очередь", "Байланыш жок — чек кезекке түшөт", "Offline — the receipt will be queued",
            "Bağlantı yok — fiş sıraya alınır", "Aloqa yo'q — chek navbatga tushadi");
        public string CurrentDebtLabel => Tr.T("Текущий долг", "Азыркы карыз", "Current debt", "Mevcut borç", "Joriy qarz");
        public string DebtAfterSaleLabel => Tr.T("После продажи", "Сатуудан кийин", "After this sale", "Satıştan sonra", "Sotuvdan keyin");

        /// <summary>Без связи (касса в офлайн-режиме или сервер не отвечает) — бейдж в шапке окна.</summary>
        public bool IsOfflineMode => OfflineModeHelper.SellLocally;

        /// <summary>2026-10-30 (приёмка 1.17.67, мелочи): связь могла пропасть/вернуться, пока окно оплаты открыто, а свойство только читается —
        /// окно раз в пару секунд просит перечитать бейдж «Без связи».</summary>
        public void RefreshOfflineMode() => OnPropertyChanged(nameof(IsOfflineMode));

        // ------------------------------------------------------------------ строки под итогом
        public bool HasLineDiscounts => _lineDiscounts > 0.005;
        public string LineDiscountsDisplay => "−" + MoneyText.Som(_lineDiscounts);
        public string OrderDiscountDisplay => "−" + MoneyText.Som(_orderDiscountAmount);
        public bool HasPointsRedeemed => _pointsRedeemed > 0.005;
        public string PointsRedeemedDisplay => "−" + MoneyText.Som(_pointsRedeemed);

        // ------------------------------------------------------------------ плашка результата (наличные: сдача / не хватает; в долг: сколько уйдёт в долг)
        public bool ShowResultPlate => IsCashMode || IsDebtMode;

        private (string Title, double Amount, string Formula, string Kind) ResultPlate()
        {
            var total = Math.Max(0, _effectiveTotalDue);
            if (IsDebtMode)
            {
                var paid = Math.Clamp(ParseNonNegative(_debtCashReceived) ?? 0, 0, total);
                return (Tr.T("В долг уйдёт", "Карызга кетет", "Goes on credit", "Veresiyeye yazılır", "Qarzga yoziladi"), Math.Max(0, total - paid), "", "warn");
            }
            var cash = ParseCash();
            if (cash is not { } c)
                return (Tr.T("Не хватает", "Жетишпейт", "Short by", "Eksik", "Yetishmaydi"), total, "", "short");
            var formula = $"{MoneyText.Amount(c)} − {MoneyText.Amount(total)}";
            return c + 1e-9 < total
                ? (Tr.T("Не хватает", "Жетишпейт", "Short by", "Eksik", "Yetishmaydi"), total - c, formula, "short")
                : (Tr.T("Сдача", "Кайтарым", "Change", "Para üstü", "Qaytim"), c - total, formula, "ok");
        }

        public string ResultTitle => ResultPlate().Title;
        public string ResultAmountDisplay => MoneyText.Amount(ResultPlate().Amount);
        public string ResultFormula => ResultPlate().Formula;
        public bool HasResultFormula => ResultPlate().Formula.Length > 0;
        public bool ResultIsOk => ResultPlate().Kind == "ok";
        public bool ResultIsShort => ResultPlate().Kind == "short";
        public bool ResultIsWarn => ResultPlate().Kind == "warn";

        // ------------------------------------------------------------------ чип клиента
        public string SelectedClientInitials => _selectedClient?.Initials ?? "";
        public string SelectedClientShortName => _selectedClient?.Name ?? "";

        /// <summary>«бонусы 20 · Долг: 1 250,00 сом» под именем клиента (только то, что уже известно кассе).</summary>
        public string SelectedClientSubline
        {
            get
            {
                if (_selectedClient is not { } client)
                    return "";
                var parts = new System.Collections.Generic.List<string>();
                if (LoyaltyEnabled && _clientLoyaltyBalance > 0.005)
                    parts.Add($"{Tr.T("бонусы", "бонус", "points", "puan", "bonus")} {MoneyText.Amount(_clientLoyaltyBalance)}");
                if (!string.IsNullOrWhiteSpace(client.Phone))
                    parts.Add(client.Phone);
                if (client.HasDebt)
                    parts.Add(client.DebtText);
                return string.Join(" · ", parts);
            }
        }

        public bool HasSelectedClientSubline => SelectedClientSubline.Length > 0;

        // ------------------------------------------------------------------ бонусы: «Списать N (F6)» → «✓ Списано N»
        private double RedeemableNow => Math.Min(_clientLoyaltyBalance, Math.Max(0, _subtotal - _lineDiscounts - _orderDiscountAmount));

        public bool CanRedeemPoints => HasPointsRedeemed || RedeemableNow > 0.005;

        public string RedeemButtonText => HasPointsRedeemed
            ? $"✓ {Tr.T("Списано", "Чыгарылды", "Redeemed", "Düşüldü", "Yechildi")} {MoneyText.Amount(_pointsRedeemed)}"
            : $"{Tr.T("Списать", "Чыгаруу", "Redeem", "Düş", "Yechish")} {MoneyText.Amount(RedeemableNow)}";

        /// <summary>F6 / кнопка: списать столько бонусов, сколько можно, а если уже списаны — снять. Пишет в то же поле, что и ввод руками;
        /// сколько спишется на самом деле, считает ComputePointsRedeemed (не больше баланса и суммы к оплате).</summary>
        public void ToggleRedeemAllPoints()
        {
            if (!LoyaltyEnabled || !HasSelectedClient)
                return;
            if (HasPointsRedeemed)
                PointsToRedeemInput = "0";
            else if (RedeemableNow > 0.005)
                PointsToRedeemInput = ShowDec(Math.Floor(RedeemableNow * 100 + 1e-6) / 100);
        }

        // ------------------------------------------------------------------ карточка долга (данные клиента, которые уже есть в модели)
        public bool ShowDebtCard => IsDebtMode && _selectedClient is not null;
        public string CurrentDebtDisplay => MoneyText.Som(_selectedClient?.Debt ?? 0);

        public string DebtAfterSaleDisplay
        {
            get
            {
                var total = Math.Max(0, _effectiveTotalDue);
                var paid = Math.Clamp(ParseNonNegative(_debtCashReceived) ?? 0, 0, total);
                return MoneyText.Som((_selectedClient?.Debt ?? 0) + Math.Max(0, total - paid));
            }
        }

        // ------------------------------------------------------------------ уведомления об изменении (подписка в конструкторе)
        private static readonly string[] RedesignDependencies =
        {
            nameof(BigTotalDisplay), nameof(ChangeAmountDisplay), nameof(DebtRemainingText), nameof(MixedRemainingText),
            nameof(PointsRedeemed), nameof(SelectedClientName), nameof(PaymentMethod), nameof(ClientLoyaltyBalanceDisplay),
        };

        private static readonly string[] RedesignProperties =
        {
            nameof(ExactCashLabel), nameof(HasLineDiscounts), nameof(LineDiscountsDisplay), nameof(OrderDiscountDisplay),
            nameof(HasPointsRedeemed), nameof(PointsRedeemedDisplay), nameof(ShowResultPlate), nameof(ResultTitle),
            nameof(ResultAmountDisplay), nameof(ResultFormula), nameof(HasResultFormula), nameof(ResultIsOk), nameof(ResultIsShort),
            nameof(ResultIsWarn), nameof(SelectedClientInitials), nameof(SelectedClientShortName), nameof(SelectedClientSubline),
            nameof(HasSelectedClientSubline), nameof(RedeemButtonText), nameof(CanRedeemPoints), nameof(ShowDebtCard), nameof(CurrentDebtDisplay),
            nameof(DebtAfterSaleDisplay),
        };

        private void RaiseRedesignDependents(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null || !RedesignDependencies.Contains(e.PropertyName))
                return;
            foreach (var name in RedesignProperties)
                OnPropertyChanged(name);
        }
    }
}
