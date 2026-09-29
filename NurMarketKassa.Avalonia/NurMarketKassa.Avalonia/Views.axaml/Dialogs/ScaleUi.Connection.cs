using System.Globalization;
using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NurMarketKassa.AvaloniaHost.Views.Settings;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

/// <summary>Уровень подсказки к адресу весов: нет адреса / всё хорошо / похоже на ошибку / точно ошибка.</summary>
internal enum ScaleIpLevel
{
    None,
    Ok,
    Warning,
    Error,
}

/// <summary>Последняя проверка связи с весами марки (живёт до закрытия кассы).</summary>
internal sealed record ScaleCheckState(DateTime At, bool Ok, string Ip, string Message);

// 2026-09-28, редизайн «Весы с печатью этикеток» (просьба владельца «сделай нормальными настройки
// отправки на весы»). На скриншоте владелец ввёл адрес TM-30F «192.169.0.150» (опечатка 192.168)
// в поля «Сетевые весы», которые на самом деле принадлежали Штрих-ПРИНТ, — интерфейс путал. Здесь
// общее для Настроек → Весы и окна «Весы»: название марки, адрес и способ отправки одной строкой,
// проверка адреса (опечатка, не локальная сеть, не та подсеть) и «Проверить связь» по марке.
internal static partial class ScaleUi
{
    /// <summary>Марка из настроек, приведённая к одному из четырёх значений.</summary>
    public static string NormalizeBrand(string? brand) => brand switch
    {
        BrandRongta => BrandRongta,
        BrandAi => BrandAi,
        BrandTm => BrandTm,
        _ => BrandShtrikh,
    };

    /// <summary>Полное название марки для карточек и шапки окна «Весы». Штрих-М назван по модели
    /// весов (ШТРИХ-ПРИНТ) — так написано на самих весах, владелец искал именно это слово.</summary>
    public static string LabelBrandTitle(string brand) => NormalizeBrand(brand) switch
    {
        BrandRongta => "Rongta",
        BrandTm => "TM-30F (Dahua)",
        BrandAi => L("AI весы", "AI тараза", "AI scales", "AI tartı", "AI tarozi"),
        _ => L("Штрих-ПРИНТ (Штрих-М)", "Штрих-ПРИНТ (Штрих-М)", "Shtrih-PRINT (Shtrih-M)", "Shtrih-PRINT (Shtrih-M)", "Shtrix-PRINT (Shtrix-M)"),
    };

    /// <summary>Порт весов марки из настроек (у AI-весов порта нет).</summary>
    public static int PortOf(string brand)
    {
        var prefs = UserPreferences.Instance;
        return NormalizeBrand(brand) switch
        {
            BrandRongta => prefs.RongtaScalePort,
            BrandTm => prefs.TmScalePort,
            BrandAi => 0,
            _ => prefs.ScaleLanPort,
        };
    }

    /// <summary>Как касса отправляет товары на весы этой марки — одной фразой.</summary>
    public static string RouteText(string brand)
    {
        var prefs = UserPreferences.Instance;
        return NormalizeBrand(brand) switch
        {
            BrandRongta => string.Equals(prefs.RongtaDataSource, "server", StringComparison.OrdinalIgnoreCase)
                ? L($"свой сервер кассы, порт {prefs.RongtaServerPort}", $"кассанын өз сервери, {prefs.RongtaServerPort} порт", $"till's own server, port {prefs.RongtaServerPort}", $"kasanın kendi sunucusu, port {prefs.RongtaServerPort}", $"kassaning o‘z serveri, {prefs.RongtaServerPort} port")
                : L("через сайт и программу RLS1000", "сайт жана RLS1000 программасы аркылуу", "via the website and RLS1000", "site ve RLS1000 programı üzerinden", "sayt va RLS1000 dasturi orqali"),
            BrandTm => L("напрямую по сети", "тармак аркылуу түз", "directly over the network", "doğrudan ağ üzerinden", "to‘g‘ridan-to‘g‘ri tarmoq orqali"),
            BrandAi => L("файл для программы весов", "тараза программасы үчүн файл", "file for the scale's software", "tartı programı için dosya", "tarozi dasturi uchun fayl"),
            _ => prefs.ShtrikhDirectLan
                ? L("напрямую по сети", "тармак аркылуу түз", "directly over the network", "doğrudan ağ üzerinden", "to‘g‘ridan-to‘g‘ri tarmoq orqali")
                : L("через сервер NurCRM", "NurCRM сервери аркылуу", "via the NurCRM server", "NurCRM sunucusu üzerinden", "NurCRM serveri orqali"),
        };
    }

    /// <summary>«192.168.0.50:1111 · напрямую по сети» — адрес весов и способ отправки.</summary>
    public static string AddressLine(string brand)
    {
        brand = NormalizeBrand(brand);
        if (brand == BrandAi)
            return RouteText(brand);
        var ip = IpOf(brand).Trim();
        var address = ip.Length == 0
            ? L("адрес не задан", "дарек коюлган эмес", "address not set", "adres girilmemiş", "manzil kiritilmagan")
            : ip + ":" + PortOf(brand).ToString(CultureInfo.InvariantCulture);
        return address + " · " + RouteText(brand);
    }

    // ------------------------------------------------------------------ проверка адреса

    private static IReadOnlyList<LocalSubnet>? _subnetsCache;
    private static DateTime _subnetsAt;

    /// <summary>Сети компьютера. Кэш на 30 секунд: подсказка к адресу пересчитывается на
    /// каждое нажатие клавиши, а опрос адаптеров Windows — не бесплатный.</summary>
    private static IReadOnlyList<LocalSubnet> LocalSubnets()
    {
        if (_subnetsCache is null || DateTime.UtcNow - _subnetsAt > TimeSpan.FromSeconds(30))
        {
            try
            {
                _subnetsCache = ScaleNetworkScanner.GetLocalSubnets();
            }
            catch (Exception ex)
            {
                PosLogger.Log($"Весы: список сетей компьютера не получен: {ex.Message}", "SCALES");
                _subnetsCache = Array.Empty<LocalSubnet>();
            }
            _subnetsAt = DateTime.UtcNow;
        }
        return _subnetsCache;
    }

    /// <summary>Разбирает строго «a.b.c.d» (IPAddress.TryParse пропускает и «10.1» — весам
    /// такое не годится, а владелец увидел бы «адрес правильный»).</summary>
    private static bool TryParseDotted(string text, out byte[] octets)
    {
        octets = new byte[4];
        var parts = text.Split('.');
        if (parts.Length != 4)
            return false;
        for (var i = 0; i < 4; i++)
        {
            var p = parts[i];
            if (p.Length is 0 or > 3 || !p.All(char.IsDigit) || !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n > 255)
                return false;
            octets[i] = (byte)n;
        }
        return true;
    }

    /// <summary>Подсказка к адресу весов: формат, опечатка «192.169» (частые сети — 192.168),
    /// адрес не из локальной сети, адрес не из сетей этого компьютера (тогда связи не будет, и
    /// надо «Найти в сети» или поменять адрес на весах).</summary>
    public static (ScaleIpLevel Level, string Text) CheckScaleIp(string? text)
    {
        var ip = (text ?? "").Trim();
        if (ip.Length == 0)
            return (ScaleIpLevel.None, "");

        if (!TryParseDotted(ip, out var o))
        {
            return (ScaleIpLevel.Error, L("Адрес записан неверно: нужно 4 числа от 0 до 255 через точку, например 192.168.1.50.",
                "Дарек туура эмес жазылган: чекит менен бөлүнгөн 0дөн 255ке чейинки 4 сан керек, мисалы 192.168.1.50.",
                "The address is malformed: it needs 4 numbers 0–255 separated by dots, e.g. 192.168.1.50.",
                "Adres hatalı: noktayla ayrılmış 0–255 arası 4 sayı gerekir, ör. 192.168.1.50.",
                "Manzil noto‘g‘ri yozilgan: nuqta bilan ajratilgan 0 dan 255 gacha 4 ta son kerak, masalan 192.168.1.50."));
        }

        if (o[0] == 127)
        {
            return (ScaleIpLevel.Error, L("Это адрес самого компьютера, а не весов.", "Бул компьютердин өзүнүн дареги, тараза эмес.", "This is the computer's own address, not the scale.", "Bu bilgisayarın kendi adresi, tartının değil.", "Bu kompyuterning o‘z manzili, tarozi emas."));
        }
        if (o[0] == 0 || o[0] >= 224)
        {
            return (ScaleIpLevel.Error, L("Такой адрес не может быть у весов.", "Мындай дарек тараза үчүн болбойт.", "A scale cannot have this address.", "Bir tartının böyle bir adresi olamaz.", "Tarozida bunday manzil bo‘lmaydi."));
        }

        // Главная опечатка: 192.169… вместо 192.168… (так было у владельца, 28.09).
        if (o[0] == 192 && o[1] != 168)
        {
            var guess = $"192.168.{o[2]}.{o[3]}";
            return (ScaleIpLevel.Warning, L($"Похоже на опечатку: адреса локальной сети начинаются с 192.168… Вероятно, нужно {guess}.",
                $"Ката жазылганга окшойт: жергиликтүү тармактын даректери 192.168… менен башталат. Балким, {guess} керек.",
                $"Looks like a typo: local network addresses start with 192.168… You probably meant {guess}.",
                $"Yazım hatası gibi görünüyor: yerel ağ adresleri 192.168… ile başlar. Muhtemelen {guess} olmalı.",
                $"Xato yozilganga o‘xshaydi: mahalliy tarmoq manzillari 192.168… bilan boshlanadi. Ehtimol, {guess} kerak."));
        }

        if (o[0] == 169 && o[1] == 254)
        {
            return (ScaleIpLevel.Warning, L("Это автоматический адрес (169.254…): устройство не получило адрес в сети. Задайте весам постоянный адрес.",
                "Бул автоматтык дарек (169.254…): түзмөк тармактан дарек алган жок. Таразага туруктуу дарек коюңуз.",
                "This is an automatic address (169.254…): the device did not get a network address. Give the scale a fixed address.",
                "Bu otomatik bir adres (169.254…): cihaz ağdan adres alamadı. Tartıya sabit bir adres verin.",
                "Bu avtomatik manzil (169.254…): qurilma tarmoqdan manzil olmadi. Taroziga doimiy manzil bering."));
        }

        var isPrivate = o[0] == 10 || (o[0] == 172 && o[1] is >= 16 and <= 31) || (o[0] == 192 && o[1] == 168);
        if (!isPrivate)
        {
            return (ScaleIpLevel.Warning, L("Это не адрес локальной сети. У весов в магазине адрес обычно вида 192.168.x.x или 10.x.x.x.",
                "Бул жергиликтүү тармактын дареги эмес. Дүкөндөгү тараза адатта 192.168.x.x же 10.x.x.x түрүндөгү дарек менен болот.",
                "This is not a local network address. A shop scale usually has an address like 192.168.x.x or 10.x.x.x.",
                "Bu bir yerel ağ adresi değil. Mağazadaki tartının adresi genellikle 192.168.x.x veya 10.x.x.x biçimindedir.",
                "Bu mahalliy tarmoq manzili emas. Do‘kondagi tarozi manzili odatda 192.168.x.x yoki 10.x.x.x ko‘rinishida bo‘ladi."));
        }

        if (o[3] is 0 or 255)
        {
            return (ScaleIpLevel.Warning, L($"Адрес, оканчивающийся на .{o[3]}, — это адрес всей сети, а не устройства. Проверьте последнее число.",
                $".{o[3]} менен бүткөн дарек — бүт тармактын дареги, түзмөктүкү эмес. Акыркы санды текшериңиз.",
                $"An address ending in .{o[3]} is the whole network, not a device. Check the last number.",
                $".{o[3]} ile biten adres bir cihazın değil, tüm ağın adresidir. Son sayıyı kontrol edin.",
                $".{o[3]} bilan tugagan manzil — butun tarmoq manzili, qurilmaniki emas. Oxirgi sonni tekshiring."));
        }

        var subnets = LocalSubnets();
        if (subnets.Count == 0)
        {
            return (ScaleIpLevel.Warning, L("У компьютера сейчас нет сетевых подключений — весы по сети не будут видны.",
                "Компьютерде азыр тармак туташуулары жок — тараза тармактан көрүнбөйт.",
                "The computer has no network connections right now — the scale will not be reachable.",
                "Bilgisayarın şu an ağ bağlantısı yok — tartıya ağdan ulaşılamaz.",
                "Kompyuterda hozir tarmoq ulanishlari yo‘q — tarozi tarmoqda ko‘rinmaydi."));
        }

        var number = ScaleNetworkScanner.ToNumber(new IPAddress(o));
        foreach (var s in subnets)
        {
            var prefix = Math.Clamp(s.PrefixLength, 0, 32);
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            if ((number & mask) == (ScaleNetworkScanner.ToNumber(s.LocalAddress) & mask))
            {
                if (s.LocalAddress.Equals(new IPAddress(o)))
                {
                    return (ScaleIpLevel.Error, L("Это адрес самого компьютера, а не весов.", "Бул компьютердин өзүнүн дареги, тараза эмес.", "This is the computer's own address, not the scale.", "Bu bilgisayarın kendi adresi, tartının değil.", "Bu kompyuterning o‘z manzili, tarozi emas."));
                }
                return (ScaleIpLevel.Ok, L($"Та же сеть, что у подключения «{s.AdapterName}» ({s.LocalAddress}/{prefix}).",
                    $"«{s.AdapterName}» туташуусу менен бир тармак ({s.LocalAddress}/{prefix}).",
                    $"Same network as the “{s.AdapterName}” connection ({s.LocalAddress}/{prefix}).",
                    $"“{s.AdapterName}” bağlantısıyla aynı ağ ({s.LocalAddress}/{prefix}).",
                    $"«{s.AdapterName}» ulanishi bilan bir tarmoq ({s.LocalAddress}/{prefix})."));
            }
        }

        var own = string.Join(", ", subnets.Select(s => $"{s.LocalAddress}/{Math.Clamp(s.PrefixLength, 0, 32)}"));
        return (ScaleIpLevel.Warning, L($"Компьютер не в этой сети (у компьютера: {own}). Связи с такими весами не будет — нажмите «Найти в сети» или поменяйте адрес на весах.",
            $"Компьютер бул тармакта эмес (компьютерде: {own}). Мындай тараза менен байланыш болбойт — «Тармактан табуу» басыңыз же таразадагы даректи өзгөртүңүз.",
            $"The computer is not on this network (computer: {own}). There will be no link to this scale — press “Find on network” or change the address on the scale.",
            $"Bilgisayar bu ağda değil (bilgisayar: {own}). Bu tartıyla bağlantı olmaz — «Ağda bul»a basın veya tartıdaki adresi değiştirin.",
            $"Kompyuter bu tarmoqda emas (kompyuterda: {own}). Bunday tarozi bilan aloqa bo‘lmaydi — «Tarmoqda topish»ni bosing yoki tarozidagi manzilni o‘zgartiring."));
    }

    // ------------------------------------------------------------------ проверка связи

    private static readonly Dictionary<string, ScaleCheckState> LastChecks = new(StringComparer.Ordinal);

    /// <summary>Последняя проверка связи с весами марки — если адрес с тех пор не меняли.</summary>
    public static ScaleCheckState? LastCheckOf(string brand)
    {
        brand = NormalizeBrand(brand);
        return LastChecks.TryGetValue(brand, out var state) && string.Equals(state.Ip, IpOf(brand).Trim(), StringComparison.Ordinal)
            ? state
            : null;
    }

    /// <summary>«✓ Проверено в 14:32: …» / «Связь не проверялась».</summary>
    public static string LastCheckText(string brand)
    {
        var state = LastCheckOf(brand);
        if (state is null)
        {
            return L("Связь ещё не проверялась.", "Байланыш азырынча текшерилген жок.", "Connection not checked yet.", "Bağlantı henüz kontrol edilmedi.", "Aloqa hali tekshirilmagan.");
        }
        var time = state.At.ToString("HH:mm", CultureInfo.InvariantCulture);
        return (state.Ok ? "✓ " : "✗ ")
               + L($"Проверено в {time}: ", $"{time} текшерилди: ", $"Checked at {time}: ", $"{time} kontrol edildi: ", $"{time} da tekshirildi: ")
               + state.Message;
    }

    /// <summary>«Проверить связь» с весами выбранной марки по адресу из настроек. Ничего на весах
    /// не меняет: Штрих-ПРИНТ — опознание и гудок (без пароля), TM-30F — чтение PLU №1, Rongta —
    /// ping, TCP-порт и таблица ARP (своего протокола у Rongta нет).</summary>
    public static async Task<(bool Ok, string Message)> CheckConnectionAsync(string brand, CancellationToken ct = default)
    {
        brand = NormalizeBrand(brand);
        var prefs = UserPreferences.Instance;
        var ip = IpOf(brand).Trim();
        (bool Ok, string Message) result;

        if (brand == BrandAi)
        {
            return (false, L("У AI-весов нет связи с кассой: касса только готовит файл для их программы.",
                "AI таразанын кассага байланышы жок: касса алардын программасы үчүн файл гана даярдайт.",
                "AI scales have no link to the till: the till only prepares a file for their software.",
                "AI tartıların kasayla bağlantısı yok: kasa yalnızca onların programı için dosya hazırlar.",
                "AI tarozilarning kassa bilan aloqasi yo‘q: kassa faqat ularning dasturi uchun fayl tayyorlaydi."));
        }

        if (!IPAddress.TryParse(ip, out _) || ip.Count(c => c == '.') != 3)
        {
            return (false, L("Введите IP-адрес весов, например 192.168.1.50.", "Таразанын IP-дарегин киргизиңиз, мисалы 192.168.1.50.", "Enter the scale IP address, e.g. 192.168.1.50.", "Tartı IP adresini girin, ör. 192.168.1.50.", "Tarozi IP manzilini kiriting, masalan 192.168.1.50."));
        }

        try
        {
            result = brand switch
            {
                BrandTm => await CheckTmAsync(ip, prefs.TmScalePort, ct).ConfigureAwait(true),
                BrandRongta => await CheckRongtaAsync(ip, prefs.RongtaScalePort).ConfigureAwait(true),
                _ => await CheckShtrikhAsync(ip, prefs.ScaleLanPort, prefs.ScaleLanPassword, ct).ConfigureAwait(true),
            };
        }
        catch (Exception ex)
        {
            PosLogger.Log($"Весы ({brand}): проверка связи {ip}: {ex}", "SCALES");
            result = (false, ex.Message);
        }

        LastChecks[brand] = new ScaleCheckState(DateTime.Now, result.Ok, ip, result.Message);
        return result;
    }

    private static string NobodyAnswers(string ip) => L(
        $"По адресу {ip} никто не отвечает: проверьте адрес на весах, кабель и что весы включены.",
        $"{ip} дареги боюнча эч ким жооп бербейт: таразадагы даректи, кабелди жана тараза күйүк экенин текшериңиз.",
        $"Nobody answers at {ip}: check the address on the scale, the cable and that the scale is on.",
        $"{ip} adresinde yanıt veren yok: tartıdaki adresi, kabloyu ve tartının açık olduğunu kontrol edin.",
        $"{ip} manzilida hech kim javob bermayapti: tarozidagi manzilni, kabelni va tarozi yoqilganini tekshiring.");

    private static async Task<(bool, string)> CheckShtrikhAsync(string ip, int port, string? password, CancellationToken ct)
    {
        try
        {
            using var scale = new ShtrikhPrintLanScaleService(ip, port, password);
            var info = await scale.TestConnectionAsync(beep: true, ct).ConfigureAwait(true);
            return (true, L("весы на связи: ", "тараза байланышта: ", "scale connected: ", "tartı bağlı: ", "tarozi aloqada: ") + info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            PosLogger.Log($"Штрих-ПРИНТ: проверка связи {ip}:{port}: {ex.Message}", "SCALES");
            // Весы молчат — выясняем, есть ли вообще устройство по адресу: это разные советы.
            var (pingOk, rtt) = await RongtaScaleSettingsWindow.PingAsync(ip).ConfigureAwait(true);
            return (false, pingOk
                ? L($"устройство {ip} отвечает на ping ({rtt} мс), но весы не ответили по UDP-порту {port}. Проверьте порт в системном меню весов (обычно 1111) и что это именно Штрих-ПРИНТ.",
                    $"{ip} түзмөгү ping'ге жооп берет ({rtt} мс), бирок тараза UDP {port} порту боюнча жооп берген жок. Таразанын системалык менюсундагы портту (адатта 1111) жана бул Штрих-ПРИНТ экенин текшериңиз.",
                    $"device {ip} answers ping ({rtt} ms) but the scale did not answer on UDP port {port}. Check the port in the scale's system menu (usually 1111) and that it is a Shtrih-PRINT.",
                    $"{ip} cihazı ping'e yanıt veriyor ({rtt} ms) ama tartı UDP {port} portundan yanıt vermedi. Tartının sistem menüsündeki portu (genellikle 1111) ve bunun Shtrih-PRINT olduğunu kontrol edin.",
                    $"{ip} qurilmasi ping'ga javob beradi ({rtt} ms), lekin tarozi UDP {port} porti orqali javob bermadi. Tarozining tizim menyusidagi portni (odatda 1111) va bu Shtrix-PRINT ekanini tekshiring.")
                : NobodyAnswers(ip));
        }
    }

    private static async Task<(bool, string)> CheckTmAsync(string ip, int port, CancellationToken ct)
    {
        var scale = DahuaTmScaleService.TryCreate(ip, port);
        if (scale is null)
            return (false, NobodyAnswers(ip));

        var result = await scale.TestConnectionAsync(ct).ConfigureAwait(true);
        PosLogger.Log($"TM-30F (Dahua): проверка связи {ip}:{port}: connected={result.Connected}, replied={result.Replied}, error={result.Error} {result.Detail}", "SCALES");
        if (result.Replied)
        {
            return (true, L($"весы на связи ({result.ConnectMs} мс) и ответили на чтение PLU №1.",
                $"тараза байланышта ({result.ConnectMs} мс) жана PLU №1 окууга жооп берди.",
                $"the scale is connected ({result.ConnectMs} ms) and answered the PLU No. 1 read.",
                $"tartı bağlı ({result.ConnectMs} ms) ve PLU No. 1 okumasına yanıt verdi.",
                $"tarozi aloqada ({result.ConnectMs} ms) va PLU №1 o‘qishga javob berdi."));
        }
        if (result.Connected)
        {
            return (false, L($"подключение к {ip}:{port} есть, но весы не ответили на чтение за 2,5 с — возможно, они заняты программой «Русский масштаб». Отправку можно пробовать, ответы видны в журнале обмена.",
                $"{ip}:{port} менен туташуу бар, бирок тараза 2,5 с ичинде окууга жооп берген жок — балким, «Русский масштаб» программасы менен бош эмес. Жөнөтүүнү сынаса болот, жооптор алмашуу журналында көрүнөт.",
                $"connected to {ip}:{port}, but the scale did not answer the read within 2.5 s — it may be busy with “Russian Scale”. You can still try sending; replies are in the exchange log.",
                $"{ip}:{port} bağlantısı var ama tartı okumaya 2,5 sn içinde yanıt vermedi — «Русский масштаб» ile meşgul olabilir. Göndermeyi deneyebilirsiniz; yanıtlar iletişim günlüğünde.",
                $"{ip}:{port} bilan ulanish bor, lekin tarozi 2,5 s ichida o‘qishga javob bermadi — ehtimol «Русский масштаб» bilan band. Yuborishni sinash mumkin, javoblar almashuv jurnalida."));
        }

        var (pingOk, rtt) = await RongtaScaleSettingsWindow.PingAsync(ip).ConfigureAwait(true);
        return (false, pingOk
            ? L($"устройство {ip} отвечает на ping ({rtt} мс), но порт {port} закрыт: это не весы, у весов другой порт или их занял «Русский масштаб» — закройте его и повторите.",
                $"{ip} түзмөгү ping'ге жооп берет ({rtt} мс), бирок {port} порт жабык: бул тараза эмес, таразанын порту башка же аны «Русский масштаб» ээлеп турат — аны жаап, кайталаңыз.",
                $"device {ip} answers ping ({rtt} ms) but port {port} is closed: it is not the scale, the scale uses another port, or “Russian Scale” holds it — close it and retry.",
                $"{ip} cihazı ping'e yanıt veriyor ({rtt} ms) ama {port} portu kapalı: bu tartı değil, tartı başka port kullanıyor ya da «Русский масштаб» tutuyor — kapatıp tekrar deneyin.",
                $"{ip} qurilmasi ping'ga javob beradi ({rtt} ms), lekin {port} port yopiq: bu tarozi emas, tarozi porti boshqa yoki uni «Русский масштаб» band qilgan — uni yopib, qayta urinib ko‘ring.")
            : NobodyAnswers(ip));
    }

    private static async Task<(bool, string)> CheckRongtaAsync(string ip, int port)
    {
        var (pingOk, rtt) = await RongtaScaleSettingsWindow.PingAsync(ip).ConfigureAwait(true);
        var tcpOk = await ScaleNetworkScanner.TcpPortOpenAsync(ip, port, 1500).ConfigureAwait(true);
        var mac = ScaleNetworkScanner.ReadArpTable()
            .FirstOrDefault(a => a.Address == ScaleNetworkScanner.ToNumber(IPAddress.Parse(ip))).Mac;
        PosLogger.Log($"Rongta: проверка связи {ip}: ping={pingOk}, tcp{port}={tcpOk}, mac={mac ?? "-"}", "SCALES");

        if (!pingOk && !tcpOk && mac is null)
            return (false, NobodyAnswers(ip));

        var parts = new List<string>();
        if (pingOk)
            parts.Add(L($"ping {rtt} мс", $"ping {rtt} мс", $"ping {rtt} ms", $"ping {rtt} ms", $"ping {rtt} ms"));
        parts.Add(tcpOk
            ? L($"TCP {port} открыт", $"TCP {port} ачык", $"TCP {port} open", $"TCP {port} açık", $"TCP {port} ochiq")
            : L($"TCP {port} закрыт", $"TCP {port} жабык", $"TCP {port} closed", $"TCP {port} kapalı", $"TCP {port} yopiq"));
        if (mac is not null)
            parts.Add("MAC " + mac);
        return (true, L("устройство по этому адресу в сети (", "бул даректеги түзмөк тармакта (", "a device at this address is on the network (", "bu adresteki cihaz ağda (", "bu manzildagi qurilma tarmoqda (")
                      + string.Join(", ", parts) + ").");
    }

    // ------------------------------------------------------------------ окно настроек выбранных весов

    /// <summary>Окно настроек весов из списка (LabelScaleEditWindow): название, марка, адрес,
    /// способ отправки, категории. Без профиля — выбранные сейчас весы. Открывается кнопкой
    /// «Изменить…» из окна «Весы» и «Настроить…» в Настройки → Весы.</summary>
    public static async Task<bool> OpenLabelScaleSetupAsync(Window owner, NurMarketKassa.AvaloniaHost.Services.LabelScaleProfile? profile = null)
    {
        var window = new LabelScaleEditWindow(profile ?? NurMarketKassa.AvaloniaHost.Services.LabelScaleStore.Active);
        await window.ShowDialog(owner).ConfigureAwait(true);
        return window.Saved;
    }
}
