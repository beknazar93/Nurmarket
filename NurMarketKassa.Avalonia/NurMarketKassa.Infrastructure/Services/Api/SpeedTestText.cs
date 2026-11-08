using System.Globalization;

namespace NurMarketKassa.Services.Api;

/// <summary>2026-11-08 (ТЗ п. 34): человеческий итог замера скорости на 5 языках — кнопка «Проверить скорость связи» показывает его как есть.</summary>
public static class SpeedTestText
{
    /// <summary>Число с одним знаком: «23,4» (en — «23.4»).</summary>
    internal static string Num(double value)
    {
        var s = value.ToString("0.#", CultureInfo.InvariantCulture);
        return UserPreferences.Instance.Language == AppLanguage.English ? s : s.Replace('.', ',');
    }

    public static string StepLabel(string step) => step switch
    {
        "ping" => Tr.T("Проверяю задержку…", "Кармоо убактысын текшерип жатам…", "Checking latency…", "Gecikme kontrol ediliyor…", "Kechikish tekshirilmoqda…"),
        "ip" => Tr.T("Узнаю внешний адрес…", "Тышкы даректи аныктап жатам…", "Getting the external address…", "Dış adres öğreniliyor…", "Tashqi manzil aniqlanmoqda…"),
        "down" => Tr.T("Проверяю скачивание…", "Жүктөп алууну текшерип жатам…", "Checking download…", "İndirme kontrol ediliyor…", "Yuklab olish tekshirilmoqda…"),
        "up" => Tr.T("Проверяю отдачу…", "Жөнөтүүнү текшерип жатам…", "Checking upload…", "Yükleme kontrol ediliyor…", "Yuborish tekshirilmoqda…"),
        _ => "…",
    };

    public static string Format(SpeedTestResult r)
    {
        var lines = new List<string>();
        if (r.LatencyMs is { } ms)
            lines.Add(Tr.T($"Задержка: {Num(ms)} мс", $"Кармоо убактысы: {Num(ms)} мс", $"Latency: {Num(ms)} ms", $"Gecikme: {Num(ms)} ms", $"Kechikish: {Num(ms)} ms"));
        if (r.DownMbps is { } d)
            lines.Add(Tr.T($"Скачивание: {Num(d)} Мбит/с", $"Жүктөп алуу: {Num(d)} Мбит/с", $"Download: {Num(d)} Mbit/s", $"İndirme: {Num(d)} Mbit/sn", $"Yuklab olish: {Num(d)} Mbit/s"));
        if (r.UpMbps is { } u)
            lines.Add(Tr.T($"Отдача: {Num(u)} Мбит/с", $"Жөнөтүү: {Num(u)} Мбит/с", $"Upload: {Num(u)} Mbit/s", $"Yükleme: {Num(u)} Mbit/sn", $"Yuborish: {Num(u)} Mbit/s"));
        if (!string.IsNullOrWhiteSpace(r.Ip))
            lines.Add(Tr.T($"Внешний IP: {r.Ip}", $"Тышкы IP: {r.Ip}", $"External IP: {r.Ip}", $"Dış IP: {r.Ip}", $"Tashqi IP: {r.Ip}"));

        var tail = r.Status switch
        {
            SpeedTestStatus.Ok => Verdict(r.Verdict),
            SpeedTestStatus.RateLimited => RateLimited(r.RetryAfterSeconds),
            SpeedTestStatus.NotSignedIn => Tr.T("Войдите в NurCRM заново — замер доступен только после входа.", "NurCRM'ге кайра кириңиз — өлчөө киргенден кийин гана жеткиликтүү.",
                "Sign in to NurCRM again — the test is available only when signed in.", "NurCRM'e yeniden giriş yapın — ölçüm yalnızca girişten sonra kullanılabilir.", "NurCRM'ga qaytadan kiring — o'lchash faqat kirgandan keyin mavjud."),
            _ => Failed(r),
        };
        // Частичный замер (429 на скачивании) вывода «хватит» не даёт, но «слабо» по уже снятым цифрам сказать можно.
        if (r.Status == SpeedTestStatus.RateLimited && r.Verdict == SpeedVerdict.Weak)
            lines.Add(Verdict(SpeedVerdict.Weak));
        return lines.Count > 0 ? string.Join("\n", lines) + "\n\n" + tail : tail;
    }

    private static string Verdict(SpeedVerdict v) => v switch
    {
        SpeedVerdict.Good => Tr.T("Вывод: связи хватит для кассы.", "Жыйынтык: байланыш кассага жетиштүү.", "Verdict: the connection is good enough for the till.",
            "Sonuç: bağlantı kasa için yeterli.", "Xulosa: aloqa kassa uchun yetarli."),
        SpeedVerdict.Weak => Tr.T("Вывод: связь слабая — касса будет работать медленно. Проверьте Wi‑Fi или кабель, попробуйте позже.",
            "Жыйынтык: байланыш алсыз — касса жай иштейт. Wi‑Fi же кабелди текшерип, кийинчерээк аракет кылыңыз.",
            "Verdict: weak connection — the till will be slow. Check Wi‑Fi or the cable, try again later.",
            "Sonuç: bağlantı zayıf — kasa yavaş çalışır. Wi‑Fi'yi veya kabloyu kontrol edin, daha sonra tekrar deneyin.",
            "Xulosa: aloqa zaif — kassa sekin ishlaydi. Wi‑Fi yoki kabelni tekshiring, keyinroq urinib ko'ring."),
        _ => "",
    };

    private static string RateLimited(int seconds) =>
        Tr.T($"Слишком часто: сервер пускает замер раз в минуту (не больше 30 в сутки). Повторите через {seconds} с.",
            $"Өтө тез-тез: сервер өлчөөнү мүнөтүнө бир жолу гана жасатат (суткасына 30дан ашык эмес). {seconds} секундадан кийин кайталаңыз.",
            $"Too often: the server allows one test a minute (30 a day at most). Try again in {seconds} s.",
            $"Çok sık: sunucu dakikada bir ölçüme izin verir (günde en fazla 30). {seconds} sn sonra tekrar deneyin.",
            $"Juda tez-tez: server daqiqasiga bir marta o'lchashga ruxsat beradi (kuniga 30 tadan ko'p emas). {seconds} soniyadan keyin qaytaring.");

    private static string Failed(SpeedTestResult r)
    {
        if (r.FailedStep == "busy")
            return Tr.T("Замер уже идёт.", "Өлчөө жүрүп жатат.", "A test is already running.", "Ölçüm zaten sürüyor.", "O'lchash allaqachon ketmoqda.");
        if (r.HttpStatus == 404)
            return Tr.T("Этот сервер ещё не умеет замерять скорость.", "Бул сервер ылдамдыкты өлчөй элек.", "This server cannot measure speed yet.", "Bu sunucu henüz hızı ölçemiyor.", "Bu server tezlikni hali o'lchay olmaydi.");
        if (r.HttpStatus == 413)
            return Tr.T("Сервер отказался принять тестовый файл (слишком большой).", "Сервер сынак файлын кабыл алган жок (өтө чоң).", "The server refused the test file (too large).",
                "Sunucu test dosyasını kabul etmedi (çok büyük).", "Server sinov faylini qabul qilmadi (juda katta).");
        if (r.HttpStatus is { } code)
            return Tr.T($"Сервер ответил ошибкой {code}. Попробуйте позже.", $"Сервер {code} катасы менен жооп берди. Кийинчерээк аракет кылыңыз.", $"The server answered with error {code}. Try again later.",
                $"Sunucu {code} hatasıyla yanıt verdi. Daha sonra deneyin.", $"Server {code} xatosi bilan javob berdi. Keyinroq urinib ko'ring.");
        return Tr.T("Нет связи с сервером — замер не удался.", "Сервер менен байланыш жок — өлчөө ишке ашкан жок.", "No connection to the server — the test failed.",
            "Sunucuyla bağlantı yok — ölçüm başarısız.", "Server bilan aloqa yo'q — o'lchash amalga oshmadi.");
    }
}
