using System.Globalization;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services.Billing;

/// <summary>Срок подписки для шапки окна и напоминаний: Normal — больше 7 дней, Soon — 7 дней и меньше, Expired — просрочена, Unknown — даты нет.</summary>
public enum BillingExpiry { Unknown, Normal, Soon, Expired }

/// <summary>2026-11-19 (оплата подписки ELQR): кто и когда видит экран «Подписка», учебная оплата и напоминания о конце срока. Чистая логика без окон — проверяется юнит-тестами.</summary>
public static class BillingAccess
{
    /// <summary>Признак сервера <c>billing_elqr</c> в GET main/capabilities/ (в том числе вложенный features). Без него экран не показывается нигде.</summary>
    public static bool FeatureOn() => ServerCapabilities.Has(ServerCapabilities.BillingElqr);

    /// <summary>Показывать ли пункт «Подписка / Оплатить»: признак есть, роль владелец/админ. Кассир видит только «Подписка до …» (Настройки → Аккаунт).</summary>
    public static bool ShouldOffer(bool featureOn, bool isOwnerOrAdmin) => featureOn && isOwnerOrAdmin;

    /// <summary>Оплата возможна: признак есть и config.available == true. Иначе окно говорит «ещё не подключена для вашей компании».</summary>
    public static bool CanPay(bool featureOn, BillingConfig? config) => featureOn && config is { Available: true };

    /// <summary>«Учебная оплата (тест)»: ТОЛЬКО когда config.mode == "test" и пользователь владелец/админ. В боевом режиме кнопки нет вообще.</summary>
    public static bool CanTestPay(BillingConfig? config, bool isOwnerOrAdmin) => config is { IsTest: true } && isOwnerOrAdmin;

    /// <summary>Дней до конца подписки по календарю (сегодня — 0, вчера — −1). null — даты нет.</summary>
    public static int? DaysLeft(DateTimeOffset? validUntil, DateTimeOffset now) =>
        validUntil is { } v ? (int)(v.ToLocalTime().Date - now.ToLocalTime().Date).TotalDays : null;

    public static BillingExpiry ExpiryState(DateTimeOffset? validUntil, DateTimeOffset now) =>
        DaysLeft(validUntil, now) switch
        {
            null => BillingExpiry.Unknown,
            < 0 => BillingExpiry.Expired,
            <= 7 => BillingExpiry.Soon,
            _ => BillingExpiry.Normal,
        };

    /// <summary>Ступень напоминания: 7 (от 7 до 4 дней), 3 (3–2 дня), 1 (последний день и сегодня), 0 — напоминать рано, −1 — подписка просрочена, null — даты нет.</summary>
    public static int? ReminderStep(DateTimeOffset? validUntil, DateTimeOffset now) =>
        DaysLeft(validUntil, now) switch
        {
            null => null,
            < 0 => -1,
            <= 1 => 1,
            <= 3 => 3,
            <= 7 => 7,
            _ => 0,
        };

    /// <summary>«Подписка до 19.12.2026».</summary>
    public static string Day(DateTimeOffset value) => value.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    /// <summary>Текст напоминания: «Подписка заканчивается через 3 дн.» / «…завтра» / «…сегодня» / «Подписка истекла». null — напоминать не нужно.</summary>
    public static string? ReminderText(DateTimeOffset? validUntil, DateTimeOffset now)
    {
        var days = DaysLeft(validUntil, now);
        if (days is null or > 7)
            return null;
        if (days < 0)
            return Tr.T("Подписка истекла", "Жазылуу мөөнөтү бүттү", "Subscription expired", "Abonelik sona erdi", "Obuna muddati tugadi");
        if (days == 0)
            return Tr.T("Подписка заканчивается сегодня", "Жазылуу бүгүн бүтөт", "Subscription ends today", "Abonelik bugün bitiyor", "Obuna bugun tugaydi");
        if (days == 1)
            return Tr.T("Подписка заканчивается завтра", "Жазылуу эртең бүтөт", "Subscription ends tomorrow", "Abonelik yarın bitiyor", "Obuna ertaga tugaydi");
        return Tr.T($"Подписка заканчивается через {days} дн.", $"Жазылуу {days} күндөн кийин бүтөт", $"Subscription ends in {days} days",
            $"Abonelik {days} gün içinde bitiyor", $"Obuna {days} kundan keyin tugaydi");
    }
}
