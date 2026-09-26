namespace NurMarketKassa.Services;

/// <summary>Касса или программа владельца (2026-09-26, разделение программ по решению владельца).
///
/// Код один, программ две. Касса — продажа, смена, внесение/изъятие, возвраты, оплата долга,
/// тема и оборудование. Программа владельца («NurMarket Владелец») — склад, продажи, финансы,
/// зарплата, клиенты и остальное, ставится на другой компьютер. Обе работают с одним сервером
/// NurCRM, поэтому продажа на кассе видна у владельца без отдельной синхронизации.
///
/// Режим определяется при запуске: файл <see cref="OwnerMarkerFile"/> рядом с exe (его кладёт
/// установщик программы владельца), ключ командной строки <c>--owner</c> или переменная
/// окружения NURMARKET_MODE=owner.</summary>
public static class AppMode
{
    public const string OwnerMarkerFile = "owner.mode";

    public static bool IsOwner { get; private set; }

    /// <summary>Старый отдельный пакет NurMarketOwner (файл owner.mode рядом с exe). С 1.17.16
    /// программа владельца ставится вместе с кассой и запускается ярлыком с ключом --owner:
    /// пакет и канал обновлений у неё те же, что у кассы.</summary>
    public static bool IsSeparateOwnerPackage { get; private set; }

    /// <summary>Канал обновлений Velopack: свой («owner») только у отдельного пакета владельца.</summary>
    public static string? UpdateChannel => IsSeparateOwnerPackage ? "owner" : null;

    /// <summary>Папка данных в %AppData% / %LocalAppData% / %Temp%. У программы владельца своя:
    /// на одном компьютере с кассой они иначе перезаписывали бы друг другу файл настроек (кто
    /// сохранил последним, тот и прав — касса теряла бы, например, настройки принтера), делили
    /// бы одну базу SQLite и один журнал.</summary>
    public static string DataFolderName => IsOwner ? "NurMarketOwner" : "NurMarketKassa";

    /// <summary>Добавка к именам общесистемных объектов (защита от второго запуска и сигнал
    /// «покажись»): касса и программа владельца на одном компьютере не должны мешать друг другу.</summary>
    public static string InstanceSuffix => IsOwner ? "-Owner" : "";

    /// <summary>Показывать ли в КАССЕ разделы владельца (склад, продажи, финансы, зарплата, ABC,
    /// клиенты, CRM, пополнение и сроки). При входе через NurCRM — нет: они в программе владельца.
    /// В автономном режиме (локальные учётные записи, без NurCRM) — да: программа владельца без
    /// NurCRM не работает, и кроме кассы вести склад там негде. Выставляет MainWindow при входе.</summary>
    public static bool OwnerSectionsInKassa { get; set; }

    /// <summary>Итог для меню кассы: разделы владельца в кассе остаются в автономном режиме и на
    /// тарифе «Старт» — решение владельца 2026-09-26: кассу урезаем только со «Стандарта»
    /// (на «Старте» программа владельца тоже ставится, но касса остаётся прежней).</summary>
    public static bool ShowOwnerSectionsInKassa => OwnerSectionsInKassa || TariffGate.IsStartTariff;

    /// <summary>Вызывается первой строкой запуска, до любых путей к данным.</summary>
    public static void Initialize(string[] args)
    {
        IsSeparateOwnerPackage = File.Exists(Path.Combine(AppContext.BaseDirectory, OwnerMarkerFile));
        IsOwner = args.Any(a => string.Equals(a, "--owner", StringComparison.OrdinalIgnoreCase))
                  || IsSeparateOwnerPackage
                  || string.Equals(Environment.GetEnvironmentVariable("NURMARKET_MODE"), "owner", StringComparison.OrdinalIgnoreCase);
    }
}
