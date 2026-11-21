namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (владелец: «Убрать ИИ и Сотрудники из настроек КАССЫ! Телеграм-бот тоже, что касается владельца, удалить!»): единая проверка
/// для всех входов — вкладки «Сотрудники» и «ИИ» окна «Настройки», карточка «Телеграм-бот владельца» в «Операциях» (мастер, токен, получатель, сводки,
/// команды, напоминания о долгах), ключи ИИ и тумблеры Нур Советника. Всё это — только в программе владельца (<see cref="AppMode.IsOwner"/>, ключ --owner);
/// в кассе (Windows/Android/Linux) вкладок, карточек и пунктов нет — вместо них короткое пояснение <see cref="KassaNote"/>.
/// Вкладка «Операции» остаётся в кассе (QR-оплата, смена, удаления из чека, вид магазина). Бот, уже настроенный на сервере NurCRM, работает как работал.</summary>
public static class OwnerOnlySettings
{
    /// <summary>true — программа владельца: «ИИ» и карточка Телеграм-бота доступны (дальше действуют обычные права и тариф).</summary>
    public static bool Allowed => AppMode.IsOwner;

    /// <summary>«Сотрудники»: как <see cref="Allowed"/>, но в автономном режиме (OwnerSectionsInKassa: учётки без NurCRM, программа владельца там не работает)
    /// вкладка остаётся в кассе — это единственное место, где задаются локальные коды доступа сотрудников.</summary>
    public static bool EmployeesAllowed => AppMode.IsOwner || AppMode.OwnerSectionsInKassa;

    /// <summary>Пояснение вместо вкладки/кнопки в кассе.</summary>
    public static string KassaNote => Tr.T("Это — в программе владельца", "Бул — ээсинин программасында", "This is in the owner program",
        "Bu, sahip programında", "Bu — egasi dasturida");

    private static readonly System.Text.RegularExpressions.Regex EmployeesPathRx = new(
        "(Настройки|Жөндөөлөр|Settings|Ayarlar|Sozlamalar) → [«“]?(Сотрудники|Кызматкерлер|Employees|Staff|Personel|Çalışanlar|Xodimlar)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Подсказки кассиру вида «Настройки → Сотрудники»: в кассе этой вкладки нет — путь получает приставку «в программе владельца: …».</summary>
    public static string EmployeesPath(string text) => EmployeesAllowed || string.IsNullOrEmpty(text)
        ? text
        : EmployeesPathRx.Replace(text, m => Tr.T("в программе владельца: ", "ээсинин программасында: ", "in the owner program: ", "sahip programında: ", "egasi dasturida: ") + m.Value);
}
