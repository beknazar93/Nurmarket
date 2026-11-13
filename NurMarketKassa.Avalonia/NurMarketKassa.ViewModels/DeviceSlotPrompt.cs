using System.Globalization;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;

namespace NurMarketKassa.ViewModels;

/// <summary>2026-11-13 (ответ Умара 13.11, п. 5): тексты окон про слоты устройств — вопрос при входе (409 device_slot_taken) и сообщение старой кассе (401 session_revoked).
/// Тексты в одном месте, чтобы окно входа (Windows/Android/Linux), программа владельца и тесты брали одни и те же строки.</summary>
public static class DeviceSlotPrompt
{
    public static string Title => Tr.T("Другая касса уже на этом логине", "Бул логинде башка касса бар", "Another till is on this login", "Bu kullanıcı adında başka bir kasa var", "Bu loginda boshqa kassa bor");
    public static string ConfirmText => Tr.T("Войти и отключить другую", "Кирүү жана экинчисин өчүрүү", "Sign in and disconnect the other", "Giriş yap ve diğerini kapat", "Kirish va boshqasini o'chirish");
    public static string CancelText => Tr.T("Отмена", "Жокко чыгаруу", "Cancel", "İptal", "Bekor qilish");

    /// <summary>Человеческое название типа устройства по слоту сервера (desktop_pos, android_pos, desktop_owner, mobile); неизвестное — «касса».</summary>
    public static string TypeLabel(string? slot) => (slot ?? "").Trim().ToLowerInvariant() switch
    {
        "desktop_pos" => Tr.T("касса на компьютере", "компьютердеги касса", "desktop till", "bilgisayardaki kasa", "kompyuterdagi kassa"),
        "android_pos" => Tr.T("касса на Android", "Android'деги касса", "Android till", "Android kasa", "Android'dagi kassa"),
        "desktop_owner" => Tr.T("программа владельца", "ээсинин программасы", "owner program", "sahip programı", "ega dasturi"),
        "mobile" => Tr.T("мобильное приложение", "мобилдик колдонмо", "mobile app", "mobil uygulama", "mobil ilova"),
        _ => Tr.T("касса", "касса", "till", "kasa", "kassa"),
    };

    /// <summary>Время «был в сети» из ISO-строки сервера в местном времени («13.11 14:32»); не разобралось — пусто.</summary>
    public static string FormatLastSeen(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
            : "";

    /// <summary>«(тип, «имя», была в сети …)» — только то, что сервер прислал.</summary>
    public static string DescribeOther(DeviceSlotInfo? info)
    {
        var parts = new List<string> { TypeLabel(info?.Slot) };
        if (!string.IsNullOrWhiteSpace(info?.DeviceName))
            parts.Add("«" + info!.DeviceName + "»");
        var seen = FormatLastSeen(info?.LastSeenAt);
        if (seen.Length > 0)
            parts.Add(Tr.T("в сети " + seen, seen + " болгон", "last seen " + seen, "son görülme " + seen, seen + " da bo'lgan"));
        return string.Join(", ", parts);
    }

    public static string Message(DeviceSlotInfo? info)
    {
        var other = DescribeOther(info);
        return Tr.T(
            $"На этом логине уже работает другая касса ({other}). Войти здесь и отключить ту?\n\nТа касса сразу потеряет вход. Её неотправленные чеки сохранятся и уйдут на сервер после повторного входа.\n\nНесколько касс на одном логине включает поддержка NurCRM.",
            $"Бул логинде башка касса иштеп жатат ({other}). Бул жерден кирип, аны өчүрөлүбү?\n\nАл касса кирүүсүн дароо жоготот. Жөнөтүлө элек чектери сакталып, кайра киргенден кийин серверге кетет.\n\nБир логинде бир нече кассаны NurCRM колдоосу күйгүзөт.",
            $"Another till is already working on this login ({other}). Sign in here and disconnect that one?\n\nThat till will lose its sign-in immediately. Its unsent receipts are kept and go to the server after it signs in again.\n\nSeveral tills on one login are enabled by NurCRM support.",
            $"Bu kullanıcı adında başka bir kasa zaten çalışıyor ({other}). Burada giriş yapıp onu kapatalım mı?\n\nO kasa girişini hemen kaybeder. Gönderilmemiş fişleri saklanır ve yeniden girişten sonra sunucuya gider.\n\nBir kullanıcı adında birden fazla kasayı NurCRM desteği açar.",
            $"Bu loginda boshqa kassa allaqachon ishlayapti ({other}). Shu yerda kirib, uni o'chiraylikmi?\n\nU kassa kirishini darhol yo'qotadi. Yuborilmagan cheklari saqlanadi va qayta kirgandan keyin serverga ketadi.\n\nBir loginda bir nechta kassani NurCRM qo'llab-quvvatlashi yoqadi.");
    }

    /// <summary>Сообщение, если окно подтверждения недоступно (нет подписчика): без вопроса, только причина.</summary>
    public static string DeclinedMessage => Tr.T(
        "Вход не выполнен: на этом логине уже работает другая касса. Несколько касс на одном логине включает поддержка NurCRM.",
        "Кирүү аткарылган жок: бул логинде башка касса иштеп жатат. Бир логинде бир нече кассаны NurCRM колдоосу күйгүзөт.",
        "Not signed in: another till is already working on this login. Several tills on one login are enabled by NurCRM support.",
        "Giriş yapılmadı: bu kullanıcı adında başka bir kasa çalışıyor. Bir kullanıcı adında birden fazla kasayı NurCRM desteği açar.",
        "Kirilmadi: bu loginda boshqa kassa ishlayapti. Bir loginda bir nechta kassani NurCRM qo'llab-quvvatlashi yoqadi.");

    // ---- 401 session_revoked: сообщение для ОТКЛЮЧЁННОЙ кассы ----

    public static string RevokedTitle => Tr.T("Вход выполнен на другом устройстве", "Башка түзмөктө кирүү аткарылды", "Signed in on another device", "Başka bir cihazda giriş yapıldı", "Boshqa qurilmada kirilgan");
    public static string RevokedSignIn => Tr.T("Войти снова", "Кайра кирүү", "Sign in again", "Yeniden giriş yap", "Qayta kirish");
    public static string RevokedLater => Tr.T("Позже", "Кийинчерээк", "Later", "Daha sonra", "Keyinroq");

    public static string RevokedMessage(string? byDeviceName, string? at)
    {
        var who = string.IsNullOrWhiteSpace(byDeviceName) ? "" : " («" + byDeviceName!.Trim() + "»" + (FormatLastSeen(at) is { Length: > 0 } t ? ", " + t : "") + ")";
        return Tr.T(
            $"Ваш вход закрыт: этот логин открыт на другом устройстве{who}.\n\nПродажи, смена и корзины сохранены — после повторного входа всё отправится на сервер само. Если войти здесь снова, другое устройство будет отключено.",
            $"Кирүүңүз жабылды: бул логин башка түзмөктө ачылды{who}.\n\nСатуулар, смена жана себеттер сакталды — кайра киргенден кийин баары серверге өзү жөнөтүлөт. Бул жерден кайра кирсеңиз, башка түзмөк өчүрүлөт.",
            $"Your sign-in was closed: this login was opened on another device{who}.\n\nSales, the shift and carts are saved — after you sign in again everything is sent to the server automatically. If you sign in here again, the other device will be disconnected.",
            $"Girişiniz kapatıldı: bu kullanıcı adı başka bir cihazda açıldı{who}.\n\nSatışlar, vardiya ve sepetler kayıtlı — yeniden girişten sonra her şey sunucuya otomatik gönderilir. Burada yeniden giriş yaparsanız diğer cihaz kapatılır.",
            $"Kirishingiz yopildi: bu login boshqa qurilmada ochildi{who}.\n\nSotuvlar, smena va savatlar saqlangan — qayta kirgandan keyin hammasi serverga o'zi yuboriladi. Shu yerda qayta kirsangiz, boshqa qurilma o'chiriladi.");
    }
}
