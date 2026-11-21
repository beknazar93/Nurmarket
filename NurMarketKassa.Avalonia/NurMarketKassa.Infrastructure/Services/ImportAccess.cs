namespace NurMarketKassa.Services;

/// <summary>2026-11-21 (владелец: «перенос данных из другой программы убери у программы кассы, он только у владельца должен быть!»):
/// единая проверка для всех входов импорта — мастер «Перенос данных / Массовая загрузка», «Клиенты и долги», старая загрузка CSV,
/// «Загрузить накладную» из файла. Работают только в программе владельца (<see cref="AppMode.IsOwner"/>, ключ --owner); в кассе
/// (Windows/Android/Linux) кнопок, пунктов меню, подсказок и горячих клавиш нет — вместо них короткое пояснение <see cref="KassaNote"/>.</summary>
public static class ImportAccess
{
    /// <summary>true — программа владельца: импорт доступен (дальше действуют обычные права «Склад: редактирование»).</summary>
    public static bool Allowed => AppMode.IsOwner;

    /// <summary>Пояснение вместо кнопки импорта в кассе (пустой склад и т. п.).</summary>
    public static string KassaNote => Tr.T("Импорт — в программе владельца", "Импорт — ээсинин программасында", "Import is in the owner program",
        "İçe aktarma — sahip programında", "Import — egasi dasturida");
}
