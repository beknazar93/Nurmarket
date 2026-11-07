using System.Globalization;
using System.Text;

namespace NurMarketKassa.Services.Hardware;

public enum ShtrikhPcScenarioStatus
{
    /// <summary>Файл сценария записан (при первой замене — с резервной копией).</summary>
    Written,
    /// <summary>Такой же файл уже лежит — не переписывался.</summary>
    Unchanged,
    /// <summary>Нечего записывать (товаров 0) — сценарий не тронут.</summary>
    Skipped,
    Failed,
}

public sealed record ShtrikhPcScenarioResult(ShtrikhPcScenarioStatus Status, int Products, int Screens, string Path, string? BackupPath = null, string? Detail = null);

/// <summary>2026-11-06 (изучена копия папки весов владельца: Config.txt, Shared\SelfScenario\Semya.ssc): главный экран РС-200 строится не из всей базы товаров, а из СЦЕНАРИЯ
/// самообслуживания — файла <c>Shared\SelfScenario\&lt;имя&gt;.ssc</c> (параметры конфигурации 951 — папка, 952 — файл). Сценарий — список кнопок экрана, каждая привязана к КОНКРЕТНОМУ
/// коду товара. После «$$$CLR» и загрузки товаров с другими кодами кнопки старых кодов пропадают, наших товаров в сценарии нет — экран пуст. Касса пересобирает сценарий из
/// выгруженных товаров. Формат — по настоящему Semya.ssc (ASCII, перевод строки LF, разделитель «; »): строки шрифтов «&lt;F», экран «&lt;W», на каждый товар 4 строки с
/// последовательными номерами — кнопка «&lt;B», название «&lt;T», картинка «&lt;P», номер плитки «&lt;T»; сетка 6 × 5, кнопки перехода между экранами. Весы читают сценарий,
/// выбранный перед выходом из режима конфигурации (руководство, п. 7.10) — после выгрузки нужно зайти в «Конфигурация» и нажать «Продолжение работы».</summary>
public static class ShtrikhPcScenarioWriter
{
    public const string DefaultFolder = "SelfScenario";
    public const string DefaultFileName = "Semya.ssc";

    private static readonly int[] Xs = { 4, 174, 344, 514, 684, 854 };
    private static readonly int[] Ys = { 4, 120, 236, 352, 468 };

    private const int Columns = 6;
    private const int Slots = 30;
    private const int SlotForward = 29;   // (854, 468)
    private const int SlotBackMiddle = 28; // (684, 468)
    private const int SlotBackLast = 24;  // (4, 468) — как в настоящем сценарии

    /// <summary>2026-11-06: имя флаг-файла перезагрузки сценария (параметр 954): только имя файла без путей и без «.ssc»; иначе — flag.</summary>
    public static string SafeFlagName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n.Length > 64 || n.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || n.StartsWith('.') || n.EndsWith('.'))
            return ShtrikhPcSettings.DefaultReloadFlag;
        return n;
    }

    /// <summary>2026-11-06: пустой флаг перезагрузки сценария — в папке обмена (<c>&lt;папка весов&gt;\flag</c>) и копией рядом со сценарием (<c>&lt;папка весов&gt;\SelfScenario\flag</c>): куда именно
    /// смотрит параметр 954, из руководства не следует, лишний пустой файл безопасен (весы удаляют использованный). Возвращает созданные пути; выключено — пусто.</summary>
    public static async Task<IReadOnlyList<string>> WriteReloadFlagsAsync(ShtrikhPcSettings settings, CancellationToken ct = default)
    {
        settings.Normalize();
        if (!settings.ScenarioReloadFlagEnabled)
            return Array.Empty<string>();
        var name = SafeFlagName(settings.ScenarioReloadFlag);
        var folder = string.IsNullOrWhiteSpace(settings.ScenarioFolder) ? DefaultFolder : settings.ScenarioFolder;
        var paths = new[]
        {
            ShtrikhPcImportWriter.ResolvePath(settings.Folder, name),
            ShtrikhPcImportWriter.ResolvePath(settings.Folder, folder + "\\" + name),
        };
        await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var _ = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                ShtrikhPcImportWriter.TraceFile(path);
            }
        }, ct).ConfigureAwait(false);
        return paths;
    }

    /// <summary>Имя файла сценария: только имя с «.ssc», без путей; иначе — Semya.ssc.</summary>
    public static string SafeFileName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0 || !n.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase) || n.Length > 64)
            return DefaultFileName;
        return n;
    }

    /// <summary>Сколько товаров на каждом экране: один экран — до 30 без кнопок перехода; иначе первый — 29, средние — 28, последний — до 29 (кнопки перехода занимают места).</summary>
    public static List<int> ScreenSizes(int products)
    {
        var sizes = new List<int>();
        if (products <= 0)
            return sizes;
        if (products <= Slots)
        {
            sizes.Add(products);
            return sizes;
        }
        var left = products;
        sizes.Add(29);
        left -= 29;
        while (left > 29)
        {
            sizes.Add(28);
            left -= 28;
        }
        sizes.Add(left);
        return sizes;
    }

    /// <summary>Текст сценария для товаров с данными кодами (в порядке списка; плитки нумеруются подряд с 1). Перевод строки — LF, как в файле весов.</summary>
    public static string Build(IReadOnlyList<long> codes)
    {
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');
        Line("<F 100000; 19; 2; Verdana");
        Line("<F 100001; 12; 2; Verdana");

        var sizes = ScreenSizes(codes.Count);
        var id = 1;
        var index = 0;
        var number = 1;
        var previousScreen = 0;
        for (var screen = 0; screen < sizes.Count; screen++)
        {
            var screenId = id;
            Line($"<W {id}; 0; 0; 0; 1024; 584; 0; 0; 0; 0;");
            id++;
            for (var slot = 0; slot < sizes[screen]; slot++)
            {
                var x = Xs[slot % Columns];
                var y = Ys[slot / Columns];
                var code = codes[index++].ToString(CultureInfo.InvariantCulture);
                var button = id;
                Line($"<B {button}; {screenId}; {x}; {y}; 166; 112; 1; {code}; 0; 0;");
                Line($"<T {button + 1}; {button}; {x + 3}; {y + 13}; 160; 96; 0; 0; 4; {code}; ; 0; 0; 0; 0; 0; 100001");
                Line($"<P {button + 2}; {button}; {x + 3}; {y + 3}; 160; 106; 0; 0; 4; {code}; ; 1; 0; 0");
                Line($"<T {button + 3}; {button}; {x + 3}; {y + 3}; 160; 16; 0; 0; 3; {number.ToString(CultureInfo.InvariantCulture)}; ; 16711935; 16711935; 2; 1; 1; 100000");
                id += 4;
                number++;
            }

            var isFirst = screen == 0;
            var isLast = screen == sizes.Count - 1;
            if (!isFirst)
            {
                // «Назад»: на последнем экране — в первый слот нижнего ряда, как в настоящем сценарии (если он не занят товарами), на среднем — рядом с «Вперёд».
                var slot = isLast ? (sizes[screen] <= SlotBackLast ? SlotBackLast : SlotForward) : SlotBackMiddle;
                Line($"<B {id}; {screenId}; {Xs[slot % Columns]}; {Ys[slot / Columns]}; 166; 112; 2; {previousScreen}; 2; 1;");
                id++;
            }
            if (!isLast)
            {
                // «Вперёд»: следующий экран получает номер сразу за этой кнопкой.
                Line($"<B {id}; {screenId}; {Xs[SlotForward % Columns]}; {Ys[SlotForward / Columns]}; 166; 112; 2; {id + 1}; 2; 0;");
                id++;
            }
            previousScreen = screenId;
        }
        return sb.ToString();
    }

    /// <summary>Записывает сценарий в <c>&lt;папка весов&gt;\SelfScenario\&lt;файл&gt;</c>. Нет товаров — не трогает. Тот же набор — не переписывает. Прежний файл при ПЕРВОЙ замене сохраняется
    /// рядом как <c>&lt;файл&gt;.bak-ГГГГММДД-ЧЧММСС</c> (дальше резервные копии не плодятся). Запись атомарная. Вызывать, пока подключена папка весов, и только после удачной записи файла импорта.</summary>
    public static async Task<ShtrikhPcScenarioResult> WriteAsync(IReadOnlyList<long> codes, ShtrikhPcSettings settings, DateTime? now = null, CancellationToken ct = default)
    {
        settings.Normalize();
        var folder = string.IsNullOrWhiteSpace(settings.ScenarioFolder) ? DefaultFolder : settings.ScenarioFolder;
        var path = ShtrikhPcImportWriter.ResolvePath(settings.Folder, folder + "\\" + SafeFileName(settings.ScenarioFile));
        var limited = codes.Take(settings.ScenarioMaxProducts).ToList();
        if (limited.Count == 0)
            return new ShtrikhPcScenarioResult(ShtrikhPcScenarioStatus.Skipped, 0, 0, path);
        var screens = ScreenSizes(limited.Count).Count;
        var bytes = Encoding.ASCII.GetBytes(Build(limited));
        try
        {
            return await Task.Run(() =>
            {
                string? backup = null;
                if (File.Exists(path))
                {
                    if (File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                        return new ShtrikhPcScenarioResult(ShtrikhPcScenarioStatus.Unchanged, limited.Count, screens, path);
                    var dir = Path.GetDirectoryName(path)!;
                    var name = Path.GetFileName(path);
                    if (Directory.GetFiles(dir, name + ".bak-*").Length == 0)
                    {
                        backup = path + ".bak-" + (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                        File.Copy(path, backup, overwrite: false);
                    }
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                ShtrikhPcImportWriter.TraceFile(path);
                return new ShtrikhPcScenarioResult(ShtrikhPcScenarioStatus.Written, limited.Count, screens, path, backup);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ShtrikhPcScenarioResult(ShtrikhPcScenarioStatus.Failed, limited.Count, screens, path, null, ex.Message);
        }
    }
}
