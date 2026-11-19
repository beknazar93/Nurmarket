using System.Globalization;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (В7, «Касса — вид главной»): чистая логика вида главного экрана без Avalonia — чтобы проверить тестом.
/// Пилюля смены (Д4-01), плашка «Вернуть» (Д4-40), автосписок каталога (Д4-36),
/// признак «одна строка Скан или поиск» (Д4-03).</summary>
internal static class KassaMainViewRules
{
    // ── Д4-01: пилюля «Смена 09:04 ▾» ───────────────────────────────────────────────────────────────────

    /// <summary>Текст пилюли: «Смена 09:04»; время открытия неизвестно (нет связи, смену открыли на другом устройстве) — просто «Смена».</summary>
    public static string ShiftPillLabel(DateTime? openedAt)
    {
        var word = Tr.T("Смена", "Смена", "Shift", "Vardiya", "Smena");
        return openedAt is { } t ? word + " " + t.ToString("HH:mm", CultureInfo.InvariantCulture) : word;
    }

    /// <summary>Подсказка и имя для диктора: что внутри меню пилюли.</summary>
    public static string ShiftPillTooltip(DateTime? openedAt)
    {
        var menu = Tr.T("X-отчёт, внесение / изъятие, закрыть смену", "X-отчёт, акча салуу / алуу, сменаны жабуу",
            "X-report, cash in / out, close shift", "X raporu, para girişi / çıkışı, vardiyayı kapat", "X-hisobot, kirim / chiqim, smenani yopish");
        return openedAt is { } t
            ? Tr.T($"Смена открыта в {t:HH:mm}. ", $"Смена саат {t:HH:mm} ачылган. ", $"Shift opened at {t:HH:mm}. ", $"Vardiya {t:HH:mm} saatinde açıldı. ", $"Smena {t:HH:mm} da ochilgan. ") + menu
            : menu;
    }

    // ── Д4-40: «Чек очищен · Вернуть (5)» ───────────────────────────────────────────────────────────────

    /// <summary>Сколько целых секунд осталось до исчезновения плашки (округление вверх: 5, 4, 3, 2, 1; потом 0).</summary>
    public static int UndoSecondsLeft(DateTime shownAtUtc, TimeSpan window, DateTime nowUtc) =>
        Math.Max(0, (int)Math.Ceiling((window - (nowUtc - shownAtUtc)).TotalSeconds));

    /// <summary>Надпись кнопки: «Вернуть (5)»; 0 секунд — без числа.</summary>
    public static string UndoButtonLabel(string undoText, int secondsLeft) =>
        secondsLeft > 0 ? $"{undoText} ({secondsLeft})" : undoText;

    // ── Д4-36: автосписок каталога на узком окне ────────────────────────────────────────────────────────

    /// <summary>Ширина окна кассы (в логических точках, после масштаба), ниже которой каталог сам становится списком.</summary>
    public const double AutoListBelowWidth = 1280;

    /// <summary>Запас на границе: список включается ниже 1280, а обратно на плитки — только от 1304, чтобы вид не мигал при изменении размера окна на несколько точек.</summary>
    public const double AutoListHysteresis = 24;

    /// <summary>Нужно ли показывать каталог списком вместо плиток. Кассир сам выбрал «Плитки» — выбор уважаем (userChoseCards); выбор «Таблица» — всегда список.</summary>
    public static bool UseAutoList(double logicalWidth, bool currentlyAutoList, bool userChoseCards)
    {
        if (userChoseCards || logicalWidth <= 1)
            return false;
        return currentlyAutoList
            ? logicalWidth < AutoListBelowWidth + AutoListHysteresis
            : logicalWidth < AutoListBelowWidth;
    }

    // ── Д4-03: признак «одна строка Скан или поиск» ─────────────────────────────────────────────────────

    /// <summary>Имя файла-признака в %LOCALAPPDATA%\NurMarketKassa. Есть файл — нижняя строка ввода чека («Код или название», «− 1 +», «Добавить») скрыта,
    /// Enter в поиске каталога добавляет код/название в чек, как «Добавить». Без файла — всё как раньше. Включать после живой проверки сканером.</summary>
    public const string SingleInputFlagFile = "single-input-row.on";

    /// <summary>2026-11-13 (Н-2, решение владельца): одно поле ввода в кассе — по умолчанию (прежний файл-признак «.on» больше не нужен). Откат без пересборки:
    /// файл с этим именем в папке данных кассы «Local» возвращает прежние два поля (нижняя строка «Код или название», «− 1 +», «Добавить»).</summary>
    public const string SingleInputOffFlagFile = "single-input-row.off";

    /// <param name="kassaLocalFolder">Папка данных кассы в «Local» (в тесте — временная); null — настоящая (AppPaths.KassaLocal).</param>
    public static bool SingleInputRowEnabled(string? kassaLocalFolder = null)
    {
        try
        {
            return !File.Exists(Path.Combine(kassaLocalFolder ?? AppPaths.KassaLocal, SingleInputOffFlagFile));
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Надпись поля поиска каталога: при «одной строке» поле принимает и скан, и код, и название.</summary>
    public static string SearchWatermark(bool singleInput) => singleInput
        ? Tr.T("Скан, код или название", "Скан, код же аты", "Scan, code or name", "Tara, kod veya ad", "Skan, kod yoki nom")
        : "";

    /// <summary>2026-11-13 (Н-2): подсказка единственного поля ввода — что в него можно вводить.</summary>
    public static string SearchTooltip() => Tr.T(
        "Сканируйте товар или введите код / название и нажмите Enter. Количество: «3*код» или «3*» и скан. Ctrl+F — курсор в это поле.",
        "Товарды сканерлеңиз же кодун / атын жазып, Enter басыңыз. Саны: «3*код» же «3*» жана скан. Ctrl+F — курсор ушул талаага.",
        "Scan a product or type a code / name and press Enter. Quantity: “3*code” or “3*” and scan. Ctrl+F puts the cursor here.",
        "Ürünü okutun veya kod / ad yazıp Enter'a basın. Miktar: «3*kod» veya «3*» ve okutma. Ctrl+F — imleç bu alana.",
        "Mahsulotni skanerlang yoki kod / nomini yozib Enter bosing. Miqdor: «3*kod» yoki «3*» va skan. Ctrl+F — kursor shu maydonga.");
}
