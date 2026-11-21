using System.Net.Http;
using System.Text.Json;

namespace NurMarketKassa.Services;

internal static class ApiErrorParser
{
    /// <summary>Понятный текст по коду ошибки сервера; null — кода нет в словаре (остаётся текст сервера).</summary>
    internal static string? Friendly(string? code) => (code ?? "").Trim() switch
    {
        "shift_not_owned" => Tr.T("Это смена другого кассира — откройте свою смену.", "Бул башка кассирдин сменасы — өз сменаңызды ачыңыз.", "This is another cashier's shift — open your own shift.", "Bu başka bir kasiyerin vardiyası — kendi vardiyanızı açın.", "Bu boshqa kassirning smenasi — o'z smenangizni oching."),
        "product_has_history" => Tr.T("Товар нельзя удалить — по нему есть документы. Отправьте его в архив.", "Товарды өчүрүүгө болбойт — ал боюнча документтер бар. Архивге салыңыз.", "The product cannot be deleted — it has documents. Archive it instead.", "Ürün silinemez — belgeleri var. Arşive alın.", "Tovarni o'chirib bo'lmaydi — unda hujjatlar bor. Arxivga o'tkazing."),
        "offline_too_old" => Tr.T("Продажа старше 7 суток — её проводит владелец с PIN-кодом.", "Сатуу 7 суткадан эски — аны ээси PIN-код менен өткөрөт.", "The sale is older than 7 days — the owner posts it with a PIN.", "Satış 7 günden eski — sahibi PIN ile işler.", "Sotuv 7 sutkadan eski — uni egasi PIN bilan o'tkazadi."),
        // 2026-11-19 (сверка): тексты fiscal_locked и fiscal_mode_server — в одном месте, FiscalErrors (путь запроса различает чек и возврат там же); эти две строки оставлены для ответов не 409.
        "fiscal_locked" => Fiscal.FiscalErrors.LockedReturnText,
        "fiscal_pending" => Tr.T("Подождите — возврат ещё отправляется в налоговую.", "Күтө туруңуз — кайтаруу салыкка жөнөтүлүп жатат.", "Please wait — the return is still being sent to the tax service.", "Bekleyin — iade vergi dairesine gönderiliyor.", "Kuting — qaytarish soliqqa yuborilmoqda."),
        "fiscal_mode_server" => Fiscal.FiscalErrors.ModeServerText,
        "method_not_allowed" => Tr.T("Этот способ оплаты не разрешён — выберите другой.", "Бул төлөм ыкмасына уруксат жок — башкасын тандаңыз.", "This payment method is not allowed — choose another.", "Bu ödeme yöntemine izin yok — başkasını seçin.", "Bu to'lov usuliga ruxsat yo'q — boshqasini tanlang."),
        "edit_window_expired" => Tr.T("Время правки документа вышло.", "Документти оңдоо убактысы бүттү.", "The editing time for this document has expired.", "Belgeyi düzenleme süresi doldu.", "Hujjatni tahrirlash vaqti tugadi."),
        "has_returns" => Tr.T("По документу уже был возврат — правка и удаление невозможны.", "Документ боюнча кайтаруу болгон — оңдоого жана өчүрүүгө болбойт.", "The document already has a return — it cannot be edited or deleted.", "Belgede iade var — düzenlenemez ve silinemez.", "Hujjat bo'yicha qaytarish bo'lgan — tahrirlash va o'chirib bo'lmaydi."),
        "debt_already_paid" => Tr.T("Долг по документу уже оплачен — менять нельзя.", "Документ боюнча карыз төлөнгөн — өзгөртүүгө болбойт.", "The debt for this document is already paid — it cannot be changed.", "Belgenin borcu ödenmiş — değiştirilemez.", "Hujjat bo'yicha qarz to'langan — o'zgartirib bo'lmaydi."),
        "period_closed" => Tr.T("Период закрыт — изменить документ нельзя.", "Мезгил жабык — документти өзгөртүүгө болбойт.", "The period is closed — the document cannot be changed.", "Dönem kapalı — belge değiştirilemez.", "Davr yopiq — hujjatni o'zgartirib bo'lmaydi."),
        "permission_denied" => Tr.T("Нет прав на это действие — обратитесь к владельцу.", "Бул аракетке укук жок — ээсине кайрылыңыз.", "You do not have permission for this action — ask the owner.", "Bu işlem için yetkiniz yok — sahibine başvurun.", "Bu amalga ruxsat yo'q — egasiga murojaat qiling."),
        // 2026-11-20 (ответ Умара 20.11, А.4): 400 invalid_attributes — характеристики запчасти (attributes) не прошли проверку сервера.
        "invalid_attributes" => Tr.T("Характеристики товара (каталожный номер, OEM, гарантия и другие) не приняты сервером: название — латиницей до 32 знаков, значение — текст до 64 знаков или число, не больше 30 характеристик. Проверьте поля и сохраните ещё раз.",
            "Товардын мүнөздөмөлөрү (каталог номери, OEM, кепилдик ж.б.) сервер тарабынан кабыл алынган жок: аталышы — латынча 32 белгиге чейин, мааниси — 64 белгиге чейинки текст же сан, 30дан ашык эмес. Талааларды текшерип, кайра сактаңыз.",
            "The product characteristics (part number, OEM, warranty and others) were not accepted by the server: the name is Latin letters up to 32 characters, the value is text up to 64 characters or a number, at most 30 characteristics. Check the fields and save again.",
            "Ürün özellikleri (parça numarası, OEM, garanti vb.) sunucu tarafından kabul edilmedi: ad en fazla 32 karakterlik Latin harfleri, değer en fazla 64 karakterlik metin veya sayı, en çok 30 özellik. Alanları kontrol edip tekrar kaydedin.",
            "Mahsulot xususiyatlari (katalog raqami, OEM, kafolat va boshqalar) server tomonidan qabul qilinmadi: nomi — lotin harflarida 32 belgigacha, qiymati — 64 belgigacha matn yoki son, 30 tadan oshmasin. Maydonlarni tekshirib, qayta saqlang."),
        "has_sales_after" => Tr.T("После этого прихода уже были продажи — остаток уйдёт в минус, удалить нельзя.", "Бул кирүүдөн кийин сатуулар болгон — калдык минуска кетет, өчүрүүгө болбойт.", "There were sales after this delivery — the stock would go negative, so it cannot be deleted.", "Bu girişten sonra satış yapılmış — stok eksiye düşer, silinemez.", "Bu kirimdan keyin sotuvlar bo'lgan — qoldiq minusga ketadi, o'chirib bo'lmaydi."),
        _ => null,
    };

    public static string Parse(HttpResponseMessage response, string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText))
            return $"HTTP {(int)response.StatusCode}";

        try
        {
            using var doc = JsonDocument.Parse(bodyText);
            var root = doc.RootElement;

            // Django/DRF иногда отдаёт тело ошибки не объектом ({"detail": ...} и т.п.),
            // а голым JSON-массивом (например ValidationError(["текст"])) или примитивом —
            // TryGetProperty на не-объекте не возвращает false, а бросает исключение
            // ("target element has type 'Array'"), поэтому форму тела проверяем заранее.
            if (root.ValueKind == JsonValueKind.Array)
            {
                var items = root.EnumerateArray().Select(e => e.ToString()).ToArray();
                return items.Length > 0 ? string.Join("; ", items) : bodyText;
            }

            if (root.ValueKind != JsonValueKind.Object)
                return root.ToString();

            // 2026-11-21 (ответ Умара): 409 barcode_duplicate — «код уже у товара «…»» из data сервера (одно место для карточки, мастера загрузки, приёмки); 409 version_conflict — карточку успели изменить.
            if (ServerBarcodeErrors.TryDuplicate(root) is { } duplicate)
                return ServerBarcodeErrors.DuplicateText(duplicate);
            if (ServerErrorCodes.Code(root) == ServerBarcodeErrors.VersionConflict)
                return ServerBarcodeErrors.VersionConflictText;

            // 2026-11-19 (план «удобство 10 из 10», Л-11): коды ошибок сервера — человеческим языком и что делать (5 языков).
            if (root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String && Friendly(codeEl.GetString()) is { } friendly)
                return friendly;

            if (root.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? bodyText;
                if (detail.ValueKind == JsonValueKind.Array)
                {
                    var parts = detail.EnumerateArray().Select(e => e.ToString()).ToArray();
                    return string.Join("; ", parts);
                }
            }

            var pairs = new List<string>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    pairs.Add($"{prop.Name}: {string.Join(", ", prop.Value.EnumerateArray().Select(x => x.ToString()))}");
                else
                    pairs.Add($"{prop.Name}: {prop.Value}");
            }

            return pairs.Count > 0 ? string.Join("; ", pairs) : bodyText;
        }
        catch (JsonException)
        {
            var raw = bodyText.Trim();
            var low = raw.Length > 500 ? raw.AsSpan(0, 500).ToString().ToLowerInvariant() : raw.ToLowerInvariant();
            if (raw.StartsWith("<!", StringComparison.Ordinal) || low.Contains("<html", StringComparison.Ordinal))
                return $"HTTP {(int)response.StatusCode}: адрес API не найден или неверный путь (ожидался JSON).";
            return string.IsNullOrEmpty(raw) ? $"HTTP {(int)response.StatusCode}" : raw;
        }
    }
}
