namespace NurMarketKassa.Services;

/// <summary>2026-11-06: В3 Л5-18 — чистые правила следящего за несохранённым вводом (UnsavedInputTracker в приложении): какое слово на кнопке значит «сохранено»
/// и какие сочетания клавиш меняют текст поля без печати. Вынесено из окна, чтобы проверять тестом без Avalonia.</summary>
public static class UnsavedInputRules
{
    private static readonly string[] SaveWords =
    {
        "сохран", "сакта", "save", "kaydet", "saqla",
        "опубликов", "жарыяла", "publish", "yayınla", "e'lon",
        "примен", "колдонуу", "apply", "uygula", "qo'lla",
        // 2026-11-05 (аудит №4, Л4-11): ещё завершающие действия — после них ввод ушёл на сервер, «несохранённого» нет.
        // 2026-11-05 (ревью волн 0–3, С1): «Создать резервную копию», «Отправить в WhatsApp», «Записать» стирали признак несохранённого у всех полей окна —
        // слова "созда/отправ/запис/send/create…" убраны. Завершающую кнопку с другим словом можно отметить явно: UnsavedInputTracker.SetCommits(button, true).
        "провест", "приня", "оприход", "submit",
    };

    /// <summary>Слова, которые содержат слово из <see cref="SaveWords"/>, но «сохранением» не являются.
    /// 2026-11-06 (Л5-18): «колдонуу» (кырг. «применить») есть внутри «колдонуучу» («пользователь») — кнопка «Колдонуучу кошуу» (добавить пользователя) не должна снимать признак.</summary>
    private static readonly string[] NotSaveWords = { "колдонуучу" };

    /// <summary>Текст кнопки означает «сохранить / опубликовать / применить / провести» (на пяти языках).</summary>
    public static bool IsSaveText(string? text)
    {
        var lower = (text ?? "").Trim().ToLowerInvariant();
        return lower.Length > 0 && !NotSaveWords.Any(lower.Contains) && SaveWords.Any(lower.Contains);
    }
}
