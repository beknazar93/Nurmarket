using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NurMarketKassa.Services;

/// <summary>2026-11-06 (В18, АЛ-5): печать готового файла (PDF) на обычный принтер Linux/macOS через CUPS — команда <c>lp</c>. Для листов A4 с этикетками и
/// ценниками, документов и чеков на офисный принтер. Сырые данные (команды ESC/POS и TSPL) по-прежнему идут через <c>PosixPrinterTransport</c> (<c>lp -o raw</c>).
/// Безопасность: оболочка не используется (аргументы — списком), имя очереди проверяется по списку <c>lpstat -e</c> и по шаблону, ожидание ограничено.
/// В Windows и Android не вызывается (там своя печать). НЕ ПРОВЕРЕНО на живом Linux с принтером — только на разборе вывода и сборке.</summary>
public static class CupsPrintService
{
    /// <summary>Префикс порта очереди CUPS в списке принтеров кассы: «CUPS:ИмяОчереди» (так их отдаёт PosixPrinterTransport.Discover).</summary>
    public const string PortPrefix = "CUPS:";

    // Имя назначения в CUPS: буквы, цифры, «_ . - @ + :»; без пробелов, слэшей и «#». Начинаться с «-» не может (иначе его приняли бы за ключ lp).
    private static readonly Regex QueueNameRx = new(@"^[A-Za-z0-9_@+:][A-Za-z0-9_.@+:\-]{0,126}$", RegexOptions.Compiled);

    public static bool IsAvailable => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static bool IsCupsPort(string? port) => !string.IsNullOrWhiteSpace(port) && port.TrimStart().StartsWith(PortPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Имя очереди из порта «CUPS:имя» (также «LP:имя»); пусто — порт не CUPS.</summary>
    public static string QueueName(string? port)
    {
        if (string.IsNullOrWhiteSpace(port))
            return "";
        var p = port.Trim();
        var i = p.IndexOf(':');
        if (i <= 0)
            return "";
        var prefix = p[..i];
        return prefix.Equals("CUPS", StringComparison.OrdinalIgnoreCase) || prefix.Equals("LP", StringComparison.OrdinalIgnoreCase) ? p[(i + 1)..].Trim() : "";
    }

    public static bool IsValidQueueName(string? name) => !string.IsNullOrEmpty(name) && QueueNameRx.IsMatch(name);

    /// <summary>Очереди из вывода <c>lpstat -e</c> (по одной в строке).</summary>
    public static IReadOnlyList<string> ParseQueues(string? lpstatOutput) =>
        (lpstatOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsValidQueueName).ToList();

    /// <summary>Очереди CUPS (имена) для выбора принтера A4: <c>lpstat -e</c>. Нет CUPS или таймаут — пустой список.</summary>
    public static IReadOnlyList<string> Queues()
    {
        if (!IsAvailable)
            return [];
        var (code, output, _) = Run("lpstat", ["-e"], TimeSpan.FromSeconds(5));
        return code == 0 ? ParseQueues(output) : [];
    }

    /// <summary>Принтер по умолчанию в CUPS: из строки «system default destination: имя» вывода <c>lpstat -d</c>; null — не задан.</summary>
    public static string? ParseDefaultQueue(string? lpstatDOutput)
    {
        var line = (lpstatDOutput ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Contains(':'));
        if (line is null)
            return null;
        var name = line[(line.LastIndexOf(':') + 1)..].Trim();
        return IsValidQueueName(name) ? name : null;
    }

    public static string? DefaultQueue()
    {
        if (!IsAvailable)
            return null;
        var (code, output, _) = Run("lpstat", ["-d"], TimeSpan.FromSeconds(5));
        return code == 0 ? ParseDefaultQueue(output) : null;
    }

    /// <summary>Аргументы <c>lp</c> для печати PDF: очередь, число копий, заголовок задания, A4 без масштабирования (сетка этикеток не должна «поплыть»).</summary>
    public static IReadOnlyList<string> BuildLpArguments(string queue, string file, int copies, string title)
    {
        var args = new List<string> { "-d", queue, "-n", Math.Clamp(copies, 1, 99).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (!string.IsNullOrWhiteSpace(title))
        {
            args.Add("-t");
            args.Add(new string(title.Where(c => !char.IsControl(c)).Take(80).ToArray()));
        }
        args.Add("-o");
        args.Add("media=A4");
        args.Add("-o");
        args.Add("print-scaling=none");
        args.Add("--"); // дальше только имя файла
        args.Add(file);
        return args;
    }

    /// <summary>Печатает PDF на очередь CUPS. Success — задание принято CUPS (это не значит «бумага вышла»); PrinterNotFound — нет очереди с таким именем;
    /// Failed — lp не установлен, отказал или не ответил за <paramref name="timeout"/> (подробности — в журнале).</summary>
    public static LabelPrintResult PrintPdf(string? port, byte[] pdf, int copies, string title, TimeSpan? timeout = null)
    {
        var queue = QueueName(port);
        if (!IsValidQueueName(queue))
        {
            PosLogger.Log("CUPS: имя очереди пустое или недопустимое.", "WARNING");
            return LabelPrintResult.PrinterNotFound;
        }
        if (!IsAvailable)
        {
            PosLogger.Log("CUPS: на этой системе нет lp (только Linux и macOS).", "WARNING");
            return LabelPrintResult.Failed;
        }
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var (listCode, listOut, _) = Run("lpstat", ["-e"], TimeSpan.FromSeconds(5));
        if (listCode < 0)
        {
            PosLogger.Log("CUPS: lpstat не запустился — CUPS не установлен?", "WARNING");
            return LabelPrintResult.Failed;
        }
        if (!ParseQueues(listOut).Contains(queue, StringComparer.Ordinal))
        {
            PosLogger.Log($"CUPS: очередь «{queue}» не найдена в lpstat -e.", "WARNING");
            return LabelPrintResult.PrinterNotFound;
        }

        var file = Path.Combine(Path.GetTempPath(), "nurcrm-print-" + Guid.NewGuid().ToString("N")[..12] + ".pdf");
        try
        {
            File.WriteAllBytes(file, pdf);
            var (code, _, err) = Run("lp", BuildLpArguments(queue, file, copies, title), limit);
            if (code == 0)
                return LabelPrintResult.Success;
            PosLogger.Log($"CUPS: lp для «{queue}» завершился с кодом {code}: {err.Trim()}", "ERROR");
            return LabelPrintResult.Failed;
        }
        catch (Exception ex)
        {
            PosLogger.Log($"CUPS: печать на «{queue}» не удалась: {ex.GetType().Name}: {ex.Message}", "ERROR");
            return LabelPrintResult.Failed;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    private static (int Code, string Output, string Error) Run(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return (-1, "", "");
            var errTask = proc.StandardError.ReadToEndAsync();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try
                {
                    proc.Kill(true);
                }
                catch (InvalidOperationException)
                {
                }
                return (-2, "", Tr.T($"{exe}: нет ответа за {timeout.TotalSeconds:0} с", $"{exe}: {timeout.TotalSeconds:0} сек ичинде жооп жок",
                    $"{exe}: no answer within {timeout.TotalSeconds:0} s", $"{exe}: {timeout.TotalSeconds:0} sn içinde yanıt yok", $"{exe}: {timeout.TotalSeconds:0} soniyada javob yo'q"));
            }
            return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "", "");
        }
    }

    /// <summary>Понятный текст для окна печати, когда задание не принято (5 языков): куда смотреть на Linux.</summary>
    public static string FailedText => Tr.T(
        "Не удалось отправить лист в CUPS. Проверьте, что принтер включён и виден в «lpstat -p», а служба cups запущена. Подробности — в журнале кассы.",
        "Баракты CUPS'ка жөнөтүү мүмкүн болгон жок. Принтер күйгүзүлгөнүн, «lpstat -p» ичинде көрүнгөнүн жана cups кызматы иштеп жатканын текшериңиз. Толук маалымат — касса журналында.",
        "Could not send the sheet to CUPS. Check that the printer is on and listed by “lpstat -p”, and that the cups service is running. Details are in the till log.",
        "Sayfa CUPS'a gönderilemedi. Yazıcının açık olduğunu, “lpstat -p” çıktısında göründüğünü ve cups hizmetinin çalıştığını kontrol edin. Ayrıntılar kasa günlüğünde.",
        "Varaqni CUPS'ga yuborib bo'lmadi. Printer yoqilganini, «lpstat -p» ro'yxatida ko'rinishini va cups xizmati ishlayotganini tekshiring. Batafsil — kassa jurnalida.");
}
