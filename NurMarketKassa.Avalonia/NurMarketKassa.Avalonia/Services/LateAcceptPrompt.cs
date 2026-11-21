using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-11-19 (ответ Умара 19.11, п. 3): запрос PIN владельца/администратора для «Подтвердить отправку…» старых чеков (старше 7 суток) в «Неотправленном».
/// Тот же диалог кода (IUserPrompts.ConfirmWithCodeAsync) и та же проверка на сервере (SyncService.VerifyOwnerPinForLateAsync), что у «Провести по PIN владельца» и просрочки:
/// кассир без своего PIN вводит PIN владельца. Результат — токен для X-Confirm-Token (живёт 5 минут) и срок в секундах. Токен и PIN держатся только в памяти, в журнал и файлы не пишутся.</summary>
internal static class LateAcceptPrompt
{
    /// <summary>Спросить PIN и получить токен. null — отмена или отказ (текст причины уже показан через <paramref name="show"/>).</summary>
    public static async Task<LatePinResult?> AskConfirmTokenAsync(SyncService sync, Action<string> show, string? infoText = null)
    {
        var prompts = App.GetRequiredService<IUserPrompts>();
        var info = infoText ?? InfoText(); // 2026-11-21: «Очистить неотправленные чеки» просит тот же PIN со своим пояснением (токен ей не нужен — важен сам факт проверки на сервере)
        while (true)
        {
            var wait = SyncService.LatePinLockSecondsLeft;
            if (wait > 0)
            {
                show(Tr.T($"Слишком много неверных PIN. Подождите {wait} с и повторите.", $"PIN өтө көп жолу туура эмес киргизилди. {wait} с күтүп, кайталаңыз.", $"Too many incorrect PINs. Wait {wait} s and try again.", $"Çok fazla yanlış PIN. {wait} sn bekleyip tekrar deneyin.", $"PIN juda ko'p marta noto'g'ri kiritildi. {wait} s kutib, qayta urining."));
                return null;
            }
            string? entered = null;
            var granted = await prompts.ConfirmWithCodeAsync(
                Tr.T("PIN владельца или администратора", "Ээсинин же администратордун PIN коду", "Owner or administrator PIN", "Sahip veya yönetici PIN'i", "Egasi yoki administrator PIN kodi"),
                info,
                code => { entered = code?.Trim(); return !string.IsNullOrEmpty(entered); }).ConfigureAwait(true);
            if (!granted || string.IsNullOrEmpty(entered))
                return null;
            var verified = await sync.VerifyOwnerPinForLateAsync(entered).ConfigureAwait(true);
            entered = null;
            switch (verified.Status)
            {
                case LatePinStatus.Ok:
                    return verified;
                case LatePinStatus.Wrong:
                    info = verified.Message; // «PIN неверный. Осталось попыток: N.» — и вводим заново
                    break;
                default:
                    show(verified.Message); // pin_not_set / блок 15 минут / нет связи / нет прав
                    return null;
            }
        }
    }

    public static string InfoText() => Tr.T(
        "Введите PIN владельца или администратора (не пароль от входа). Одного ввода хватит на все выбранные чеки в течение 5 минут.",
        "Ээсинин же администратордун PIN кодун киргизиңиз (кирүү сырсөзү эмес). Тандалган бардык чектерге 5 мүнөт ичинде бир жолу киргизүү жетет.",
        "Enter the owner's or administrator's PIN (not the login password). One entry covers all selected receipts for 5 minutes.",
        "Sahibin veya yöneticinin PIN'ini girin (giriş şifresi değil). Tek giriş 5 dakika boyunca seçilen tüm fişler için yeter.",
        "Egasi yoki administrator PIN kodini kiriting (kirish paroli emas). Bir marta kiritish tanlangan barcha cheklar uchun 5 daqiqa yetadi.");
}
