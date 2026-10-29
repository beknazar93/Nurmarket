using System.Text.Json;

namespace NurMarketKassa.Services;

/// <summary>2026-10-28: понятные сообщения об ошибках операций с сотрудниками (создание, правка, удаление, доступы). Раньше на экран шёл
/// сырой ответ сервера («email: user with this email already exists»). Здесь разбор по кодам 400/401/403/404/409/429/5xx и по
/// ключевым полям ответа; текст сервера добавляется в конце, если он говорит больше. Секреты (пароли, PIN) сюда не попадают.</summary>
public static class EmployeeApiMessages
{
    /// <summary>2026-10-28: сообщения операций с ролями (создать, переименовать, удалить). Роль, назначенную сотрудникам, сервер не
    /// удаляет (400/409, а то и 500 из-за защиты связей) - объясняем, что делать.</summary>
    public static string DescribeRole(Exception ex, bool deleting = false)
    {
        if (ex is ApiException api && api.StatusCode is 400 or 409 or 500 && deleting)
            return Tr.T("Роль нельзя удалить: она назначена сотрудникам. Сначала переназначьте им другую роль (Настройки → Сотрудники → «Данные» → Роль), потом удалите эту. ",
                "Ролду өчүрүүгө болбойт: ал кызматкерлерге дайындалган. Адегенде аларга башка ролду дайындаңыз (Жөндөөлөр → Кызматкерлер → «Маалымат» → Ролу), анан бул ролду өчүрүңүз. ",
                "The role cannot be deleted: it is assigned to employees. Reassign them to another role first (Settings → Employees → “Details” → Role), then delete this one. ",
                "Rol silinemez: personele atanmış. Önce onlara başka bir rol atayın (Ayarlar → Personel → «Bilgiler» → Rol), sonra bunu silin. ",
                "Rolni o'chirib bo'lmaydi: u xodimlarga biriktirilgan. Avval ularga boshqa rol biriktiring (Sozlamalar → Xodimlar → «Ma'lumot» → Roli), keyin bu rolni o'chiring. ")
                + (api.StatusCode == 500 ? "" : api.Message);
        if (ex is ApiException a2 && a2.StatusCode == 400)
            return Tr.T("Сервер не принял название роли: ", "Сервер ролдун атын кабыл албады: ", "The server did not accept the role name: ",
                "Sunucu rol adını kabul etmedi: ", "Server rol nomini qabul qilmadi: ") + a2.Message;
        if (ex is ApiException a3 && a3.StatusCode == 404 && a3.Message.StartsWith("Не найден рабочий адрес", StringComparison.Ordinal))
            return Tr.T("На сервере нет нужного адреса для работы с ролями. Создавайте и меняйте роли на сайте app.nurcrm.kg (Сотрудники → Роли) и сообщите в поддержку.",
                "Серверде ролдор менен иштөө үчүн керектүү дарек жок. Ролдорду app.nurcrm.kg сайтында түзүңүз жана өзгөртүңүз (Кызматкерлер → Ролдор), колдоо кызматына билдириңиз.",
                "The server has no endpoint for working with roles. Create and edit roles on app.nurcrm.kg (Employees → Roles) and tell support.",
                "Sunucuda rollerle çalışmak için uygun adres yok. Rolleri app.nurcrm.kg sitesinde oluşturun ve düzenleyin (Personel → Roller), destek ekibine bildirin.",
                "Serverda rollar bilan ishlash uchun kerakli manzil yo'q. Rollarni app.nurcrm.kg saytida yarating va o'zgartiring (Xodimlar → Rollar), qo'llab-quvvatlashga xabar bering.");
        return Describe(ex);
    }

    public static string Describe(Exception ex)
    {
        if (ex is not ApiException api)
            return ex.Message;

        var server = (api.Message ?? "").Trim();
        var low = server.ToLowerInvariant();
        string? Field(string name)
        {
            if (api.Payload is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v))
                return v.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", v.EnumerateArray().Select(x => x.ToString()))
                    : v.ToString();
            return null;
        }
        var code = Field("code") ?? "";

        // Лимит сотрудников по тарифу — сервер отвечает 400/403 с текстом про лимит/тариф.
        if (low.Contains("лимит") || low.Contains("limit") || low.Contains("тариф") || code.Contains("limit", StringComparison.OrdinalIgnoreCase))
            return Tr.T("Достигнут лимит сотрудников по тарифу. Удалите ненужного сотрудника или расширьте тариф на app.nurcrm.kg. ",
                "Тариф боюнча кызматкерлер лимити бүттү. Кереги жок кызматкерди өчүрүңүз же тарифти app.nurcrm.kg сайтында кеңейтиңиз. ",
                "The employee limit of your plan is reached. Delete an unneeded employee or upgrade the plan on app.nurcrm.kg. ",
                "Tarifenizin personel sınırına ulaşıldı. Gereksiz personeli silin veya tarifeyi app.nurcrm.kg sitesinde yükseltin. ",
                "Tarif bo'yicha xodimlar limiti tugadi. Keraksiz xodimni o'chiring yoki tarifni app.nurcrm.kg saytida kengaytiring. ") + server;

        switch (api.StatusCode)
        {
            case 400:
                if (Field("email") is { } email)
                    return Tr.T($"Почта не подходит: {email}. Возможно, сотрудник с такой почтой уже есть (почта должна быть уникальной).",
                        $"Почта ылайыксыз: {email}. Мындай почталуу кызматкер мурунтан болушу мүмкүн (почта уникалдуу болушу керек).",
                        $"The email is not accepted: {email}. An employee with this email may already exist (email must be unique).",
                        $"E-posta kabul edilmedi: {email}. Bu e-postayla bir personel zaten olabilir (e-posta benzersiz olmalı).",
                        $"Email qabul qilinmadi: {email}. Bunday email bilan xodim allaqachon bo'lishi mumkin (email noyob bo'lishi kerak).");
                if (Field("phone_number") is { } phone)
                    return Tr.T($"Телефон не подходит: {phone}.", $"Телефон ылайыксыз: {phone}.", $"The phone number is not accepted: {phone}.",
                        $"Telefon kabul edilmedi: {phone}.", $"Telefon qabul qilinmadi: {phone}.");
                if (Field("track_number") is { } track)
                    return Tr.T($"Номер не подходит: {track}.", $"Номер ылайыксыз: {track}.", $"The number is not accepted: {track}.",
                        $"Numara kabul edilmedi: {track}.", $"Raqam qabul qilinmadi: {track}.");
                return Tr.T("Сервер не принял данные сотрудника: ", "Сервер кызматкердин маалыматын кабыл албады: ", "The server did not accept the employee data: ",
                    "Sunucu personel verisini kabul etmedi: ", "Server xodim ma'lumotlarini qabul qilmadi: ") + server;
            case 401:
                return Tr.T("Сессия истекла — войдите заново.", "Сессия бүттү — кайра кириңиз.", "The session expired — sign in again.",
                    "Oturum süresi doldu — yeniden giriş yapın.", "Sessiya tugadi — qayta kiring.");
            case 403:
                var need = Field("required");
                return Tr.T("Нет права на эту операцию с сотрудниками" + (need != null ? $" (нужно: {need})" : "") + ". Попросите владельца включить «Сотрудники: редактирование» на app.nurcrm.kg.",
                    "Кызматкерлер менен бул операцияга укук жок" + (need != null ? $" (керек: {need})" : "") + ". Ээсинен app.nurcrm.kg сайтында «Кызматкерлер: түзөтүү» күйгүзүүнү сураныңыз.",
                    "No permission for this employee operation" + (need != null ? $" (required: {need})" : "") + ". Ask the owner to enable “Employees: edit” on app.nurcrm.kg.",
                    "Bu personel işlemi için yetki yok" + (need != null ? $" (gereken: {need})" : "") + ". Sahibinden app.nurcrm.kg sitesinde «Personel: düzenleme» iznini açmasını isteyin.",
                    "Xodimlar bilan bu amal uchun huquq yo'q" + (need != null ? $" (kerak: {need})" : "") + ". Egasidan app.nurcrm.kg saytida «Xodimlar: tahrirlash»ni yoqishni so'rang.");
            case 404 when server.StartsWith("Не найден рабочий адрес", StringComparison.Ordinal):
                return server;
            case 404:
                return Tr.T("Сотрудник не найден — возможно, его уже удалили на сайте. Обновите список («Загрузить с сайта»).",
                    "Кызматкер табылган жок — сайттан өчүрүлгөн болушу мүмкүн. Тизмени жаңыртыңыз («Сайттан жүктөө»).",
                    "Employee not found — it may have been deleted on the website. Refresh the list (“Load from website”).",
                    "Personel bulunamadı — sitede silinmiş olabilir. Listeyi yenileyin («Siteden yükle»).",
                    "Xodim topilmadi — saytda o'chirilgan bo'lishi mumkin. Ro'yxatni yangilang («Saytdan yuklash»).");
            case 409:
                return Tr.T("Конфликт: ", "Кагылышуу: ", "Conflict: ", "Çakışma: ", "Ziddiyat: ") + server;
            case 429:
                return Tr.T("Слишком много запросов. Подождите минуту и повторите.", "Өтүнүчтөр өтө көп. Бир мүнөт күтүп, кайталаңыз.",
                    "Too many requests. Wait a minute and try again.", "Çok fazla istek. Bir dakika bekleyip tekrar deneyin.",
                    "So'rovlar juda ko'p. Bir daqiqa kutib, qayta urinib ko'ring.");
            case >= 500:
                return Tr.T("Сервер NurCRM сейчас не отвечает (ошибка на стороне сервера). Попробуйте позже.", "NurCRM сервери азыр жооп бербейт (сервер тарабындагы ката). Кийинчерээк аракет кылыңыз.",
                    "The NurCRM server is not responding (server-side error). Try again later.", "NurCRM sunucusu yanıt vermiyor (sunucu hatası). Daha sonra deneyin.",
                    "NurCRM serveri javob bermayapti (server xatosi). Keyinroq urinib ko'ring.");
            default:
                return server;
        }
    }
}
