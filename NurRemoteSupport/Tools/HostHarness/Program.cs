// Стенд настоящей кассы-хоста (2026-10-16): HostHarness --url ws://127.0.0.1:5099/ws/host
// Касса подключается к ретранслятору, печатает CODE=… PIN=…, на запрос оператора сама отвечает «Разрешить»,
// на сообщения чата отвечает. Экран передаётся настоящий (основной монитор), ввод оператора выполняется — поэтому
// оператор при проверке ничего не нажимает на экране. Данные кассы — во временной папке NurRemoteHarness.
using System.Text;
using NurMarketKassa.AvaloniaHost.Services.RemoteSupport;
using NurMarketKassa.Services;

Console.OutputEncoding = Encoding.UTF8;
var url = args.SkipWhile(a => a != "--url").Skip(1).FirstOrDefault() ?? "ws://127.0.0.1:5099/ws/host";
var seconds = int.TryParse(args.SkipWhile(a => a != "--seconds").Skip(1).FirstOrDefault(), out var s) ? s : 300;

AppMode.DataFolderOverride = "NurRemoteHarness";
var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NurRemoteHarness", "Logs");
Directory.CreateDirectory(logs);
await File.WriteAllLinesAsync(Path.Combine(logs, "nurmarket-kassa.log"),
    Enumerable.Range(1, 40).Select(i => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [INFO] стенд: строка журнала {i}")
        .Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [ERROR] стенд: пример ошибки принтера (проверка вкладки «Журнал»)"));
Environment.SetEnvironmentVariable("NURREMOTE_RELAY", url);

var host = new RemoteSupportHost();
host.CodeReceived += (code, pin, _) => Console.WriteLine($"CODE={code} PIN={pin}");
host.ConsentRequested += op =>
{
    Console.WriteLine($"согласие: оператор «{op}» — разрешено стендом");
    return Task.FromResult(true);
};
host.SessionStarted += op => Console.WriteLine($"сеанс начат: {op}");
host.ChatReceived += text =>
{
    Console.WriteLine("чат от оператора: " + text);
    _ = host.SendChatAsync("Кассир (стенд): получил «" + text + "»");
};
host.FileReceived += path => Console.WriteLine("файл от оператора: " + path);
host.Ended += reason => Console.WriteLine("конец: " + reason);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
stop.Token.Register(() => _ = host.StopAsync());
await host.RunAsync("Магазин Стенд", "1.17.99", "Кассир Стенд", "Проверка настоящего кода кассы");
