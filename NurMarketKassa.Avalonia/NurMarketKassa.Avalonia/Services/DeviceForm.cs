namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-04, владелец: «раздели каталог и корзину для всех мобильных устройств». Какой аппарат под
/// кассой: телефон или планшет (экран до 11") — или стационарный кассовый терминал. Задаёт Android-часть при
/// запуске (AndroidBootstrap); в Windows всегда false.</summary>
public static class DeviceForm
{
    /// <summary>Телефон или планшет: каталог и чек — два экрана с переключателем «Товары / Чек»
    /// в любом положении экрана (MainWindow.ApplyPortraitLayout).</summary>
    public static bool IsHandheld { get; set; }
}
