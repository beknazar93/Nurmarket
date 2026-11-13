namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (весы «Штрих РС-200», ТЗ Умара): тексты про штрихкод весов на 5 языках — один источник для чека, сканера и Android.</summary>
public static class ScanMessages
{
    /// <summary>Весовая этикетка с PLU, которого нет в каталоге (вместо «Товар с кодом … не найден»).</summary>
    public static string WeightPluNotFound(string plu) => Tr.T(
        $"Весовой товар с PLU {plu} не найден. Проверьте PLU товара в NurCRM.",
        $"PLU {plu} болгон салмактуу товар табылган жок. NurCRM'де товардын PLU кодун текшериңиз.",
        $"Weighed product with PLU {plu} was not found. Check the product's PLU in NurCRM.",
        $"PLU {plu} olan tartılı ürün bulunamadı. Ürünün PLU kodunu NurCRM'de kontrol edin.",
        $"PLU {plu} bo'lgan tarozi mahsuloti topilmadi. Mahsulotning PLU kodini NurCRM'da tekshiring.");

    /// <summary>Этикетка весов прочитана с ошибкой (контрольная цифра EAN-13 не сходится) — отсканировать ещё раз, на сервер не спрашивать.</summary>
    public static string BadScan => Tr.T(
        "Штрихкод прочитан неверно, отсканируйте ещё раз.",
        "Штрихкод туура эмес окулду, кайра сканерлеңиз.",
        "The barcode was read incorrectly, please scan again.",
        "Barkod yanlış okundu, lütfen tekrar tarayın.",
        "Shtrix-kod noto'g'ri o'qildi, qayta skanerlang.");

    // ------------------------------------------------------------------ 2026-11-11: мягкая подсказка о контрольной цифре обычного кода

    /// <summary>2026-11-11 (разбор «Кыргызстанские штрихкоды», п. 3/9): обычный (не весовой, не на «2») код EAN-13/EAN-8, у которого не сходится контрольная цифра —
    /// скорее всего сканер прочитал одну цифру неверно. Код НЕ отвергаем (у поставщиков бывают законные коды с «неверной» цифрой), только подсказываем после
    /// неудачного поиска, перед «Добавить товар». Коды на «2» (внутренние и этикетки весов) не трогаем — для них свои правила.</summary>
    public static bool LooksMisread(string? code)
    {
        var key = ProductCodeLookup.Normalize(code);
        if (key.Length is not (8 or 13) || !key.All(char.IsDigit) || key[0] == '2')
            return false;
        // EAN-13: веса 1,3,1,3… слева; EAN-8: 3,1,3,1… слева (обе схемы — «3 у цифры перед контрольной»)
        var sum = 0;
        for (var i = 0; i < key.Length - 1; i++)
            sum += (key[i] - '0') * ((key.Length - 1 - i) % 2 == 1 ? 3 : 1);
        return (10 - sum % 10) % 10 != key[^1] - '0';
    }

    public static string CheckDigitHint => Tr.T(
        "Проверьте штрихкод: контрольная цифра не сходится — отсканируйте ещё раз.",
        "Штрихкодду текшериңиз: текшерүү цифрасы дал келбейт — кайра сканерлеңиз.",
        "Check the barcode: the check digit does not match — please scan again.",
        "Barkodu kontrol edin: kontrol basamağı uyuşmuyor — tekrar tarayın.",
        "Shtrix-kodni tekshiring: nazorat raqami mos kelmayapti — qayta skanerlang.");

    public static string AddAnyway => Tr.T("Всё равно добавить", "Баары бир кошуу", "Add anyway", "Yine de ekle", "Baribir qo'shish");

    public static string ScanAgain => Tr.T("Отсканирую ещё раз", "Кайра сканерлейм", "I'll scan again", "Tekrar tarayacağım", "Qayta skanerlayman");

    /// <summary>2026-11-11: общий шаг кассы и приёмки после неудачного поиска. Контрольная цифра сходится (или код не EAN) — true сразу; не сходится — вопрос
    /// «Проверьте штрихкод…» с кнопками «Всё равно добавить» (true) / «Отсканирую ещё раз» (false).</summary>
    public static async Task<bool> ConfirmUnknownCodeAsync(NurMarketKassa.Core.Contracts.IUserPrompts? prompts, string? code)
    {
        if (prompts is null || !LooksMisread(code))
            return true;
        PosLogger.Log($"Скан {PurchaseReceivingService.MaskCode(code)}: товар не найден, контрольная цифра не сходится — предложено отсканировать ещё раз.", "CART");
        var addAnyway = await prompts.ConfirmAsync(CheckDigitHint, AddAnyway, ScanAgain).ConfigureAwait(true);
        PosLogger.Log(addAnyway ? "Скан с неверной контрольной цифрой: выбрано «Всё равно добавить»." : "Скан с неверной контрольной цифрой: выбрано «Отсканирую ещё раз».", "CART");
        return addAnyway;
    }
}
