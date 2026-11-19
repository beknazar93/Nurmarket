using System.Globalization;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Hardware;

namespace NurMarketKassa.AvaloniaHost.Views.Dialogs;

// 2026-11-13: весы Масса-К (серии R/SL) — проверка связи и тексты, общие для «Настройки → Весы», окна «Весы» и окна «Настройки весов Масса-К».
internal static partial class ScaleUi
{
    /// <summary>«Проверить связь» с Масса-К: только чтение (серийный номер, состояние файлов, масса на платформе). Весы ничего не меняют.</summary>
    public static async Task<(bool Ok, string Message)> CheckMassaKAsync(string ip, int port, CancellationToken ct)
    {
        var scale = MassaKScaleService.TryCreate(ip, port);
        if (scale is null)
            return (false, NobodyAnswers(ip));

        var result = await scale.CheckAsync(ct).ConfigureAwait(true);
        PosLogger.Log($"Масса-К: проверка связи {ip}:{port}: ошибка {result.Error}, серийный {result.Serial?.ToString() ?? "-"}, маска файлов {result.FileMask?.ToString() ?? "-"}, масса {result.WeightKg?.ToString(CultureInfo.InvariantCulture) ?? "-"} ({result.Detail})", "SCALES");
        return (result.Ok, MassaKCheckText(result, ip, port, await MassaKPingHintAsync(result, ip).ConfigureAwait(true)));
    }

    /// <summary>Если подключиться не удалось — отличаем «устройство есть, порт закрыт» от «по адресу никого нет».</summary>
    private static async Task<(bool PingOk, long Rtt)> MassaKPingHintAsync(MassaKCheckResult result, string ip) =>
        result.Error == MassaKError.ConnectFailed ? await RongtaScaleSettingsWindow.PingAsync(ip).ConfigureAwait(true) : (false, 0);

    /// <summary>Текст итога проверки Масса-К на 5 языках (чистая функция — для окна и тестов).</summary>
    public static string MassaKCheckText(MassaKCheckResult result, string ip, int port, (bool PingOk, long Rtt) ping)
    {
        switch (result.Error)
        {
            case MassaKError.None:
                var parts = new List<string>();
                if (result.Serial is { } serial)
                    parts.Add(L($"серийный № {serial}", $"сериялык № {serial}", $"serial No. {serial}", $"seri no {serial}", $"seriya № {serial}"));
                if (result.FileMask is { } mask)
                    parts.Add(MassaKFilesText(mask));
                if (result.WeightKg is { } kg)
                {
                    var weight = kg.ToString("0.000", CultureInfo.CurrentCulture);
                    parts.Add(L($"на платформе {weight} кг", $"платформада {weight} кг", $"{weight} kg on the platform", $"platformda {weight} kg", $"platformada {weight} kg"));
                }
                return L("весы Масса-К на связи", "Масса-К таразасы байланышта", "the Massa-K scale is connected", "Massa-K tartısı bağlı", "Massa-K tarozisi aloqada")
                       + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "") + ".";
            case MassaKError.Nack:
                return L($"устройство {ip}:{port} ответило «неизвестная команда» — это не весы Масса-К серий R/SL (ВП/ВПМ — другой протокол, пока не поддерживается).",
                    $"{ip}:{port} түзмөгү «белгисиз буйрук» деп жооп берди — бул R/SL сериясындагы Масса-К таразасы эмес (ВП/ВПМ — башка протокол, азырынча колдоого алынбайт).",
                    $"the device at {ip}:{port} answered “unknown command” — this is not a Massa-K R/SL scale (VP/VPM use another protocol, not supported yet).",
                    $"{ip}:{port} cihazı «bilinmeyen komut» yanıtı verdi — bu R/SL serisi Massa-K tartısı değil (VP/VPM başka protokol, henüz desteklenmiyor).",
                    $"{ip}:{port} qurilmasi «noma’lum buyruq» deb javob berdi — bu R/SL seriyasidagi Massa-K tarozisi emas (VP/VPM boshqa protokol, hozircha qo‘llab-quvvatlanmaydi).");
            case MassaKError.NoReply:
                return L($"подключение к {ip}:{port} есть, но весы не ответили. Проверьте, что это весы Масса-К серий R/SL и порт указан верно (в меню весов обычно 5001).",
                    $"{ip}:{port} менен туташуу бар, бирок тараза жооп берген жок. Бул R/SL сериясындагы Масса-К таразасы экенин жана порт туура экенин текшериңиз (тараза менюсунда адатта 5001).",
                    $"connected to {ip}:{port}, but the scale did not answer. Check that it is a Massa-K R/SL scale and the port is right (usually 5001 in the scale menu).",
                    $"{ip}:{port} bağlantısı var ama tartı yanıt vermedi. Bunun R/SL serisi Massa-K tartısı olduğunu ve portun doğru olduğunu kontrol edin (tartı menüsünde genellikle 5001).",
                    $"{ip}:{port} bilan ulanish bor, lekin tarozi javob bermadi. Bu R/SL seriyasidagi Massa-K tarozisi ekanini va port to‘g‘riligini tekshiring (tarozi menyusida odatda 5001).");
            case MassaKError.Cancelled:
                return L("проверка отменена.", "текшерүү токтотулду.", "the check was cancelled.", "kontrol iptal edildi.", "tekshirish bekor qilindi.");
            default:
                return ping.PingOk
                    ? L($"устройство {ip} отвечает на ping ({ping.Rtt} мс), но порт {port} закрыт: проверьте порт в меню весов (обычно 5001) и что это Масса-К серий R/SL.",
                        $"{ip} түзмөгү ping'ге жооп берет ({ping.Rtt} мс), бирок {port} порт жабык: тараза менюсундагы портту (адатта 5001) жана бул R/SL сериясындагы Масса-К экенин текшериңиз.",
                        $"device {ip} answers ping ({ping.Rtt} ms) but port {port} is closed: check the port in the scale menu (usually 5001) and that it is a Massa-K R/SL.",
                        $"{ip} cihazı ping'e yanıt veriyor ({ping.Rtt} ms) ama {port} portu kapalı: tartı menüsündeki portu (genellikle 5001) ve bunun R/SL serisi Massa-K olduğunu kontrol edin.",
                        $"{ip} qurilmasi ping'ga javob beradi ({ping.Rtt} ms), lekin {port} port yopiq: tarozi menyusidagi portni (odatda 5001) va bu R/SL seriyasidagi Massa-K ekanini tekshiring.")
                    : NobodyAnswers(ip);
        }
    }

    /// <summary>Маска состояния файлов (бит = 1 — файла нет или он с ошибкой): бит 0 — товары, 4 — PLU/штрихкоды, 5 — шаблоны печати.</summary>
    private static string MassaKFilesText(uint mask)
    {
        var missing = new List<string>();
        if ((mask & (1u << 0)) != 0)
            missing.Add(L("товары", "товарлар", "goods", "ürünler", "tovarlar"));
        if ((mask & (1u << 4)) != 0)
            missing.Add(L("PLU", "PLU", "PLU", "PLU", "PLU"));
        if ((mask & (1u << 5)) != 0)
            missing.Add(L("шаблоны печати", "басма үлгүлөрү", "print templates", "baskı şablonları", "chop etish shablonlari"));
        return missing.Count == 0
            ? L("файлы на весах в порядке", "тараздагы файлдар тартипте", "files on the scale are fine", "tartıdaki dosyalar yerinde", "tarozidagi fayllar joyida")
            : L("на весах нет или с ошибкой: ", "таразада жок же ката менен: ", "missing or broken on the scale: ", "tartıda yok veya hatalı: ", "tarozida yo‘q yoki xato: ") + string.Join(", ", missing);
    }
}
