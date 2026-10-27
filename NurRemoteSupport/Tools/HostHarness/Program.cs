// Стенд настоящей кассы-хоста (2026-10-16): HostHarness --url ws://127.0.0.1:5099/ws/host
// Касса подключается к ретранслятору, печатает CODE=… PIN=…, на запрос оператора сама отвечает «Разрешить».
// 2026-10-26 (приоритет кассира): по умолчанию ввод оператора НЕ исполняется на этом ПК, а печатается строкой
// «INPUT mouse …» / «INPUT key …» / «INPUT text …» (настоящие мышь и клавиатура не трогаются); хуки ввода не ставятся.
// «Ввод кассира» подаётся строкой «client» на стандартный ввод — стенд зовёт RemoteSupportHost.NoteClientInput();
// «stop» завершает сеанс. Смена состояния печатается: «CLIENT-ACTIVE true|false». Экран передаётся настоящий (основной
// монитор). Параметры: --consent allow|view|deny, --grace-ms N (по умолчанию 3000), --real-input (настоящий SendInput + хуки —
// только если за ПК никто не работает), --seconds N. Журнал кассы идёт в консоль строками «LOG [категория] текст».
using System.Text;
using NurMarketKassa.AvaloniaHost.Services.RemoteSupport;
using NurMarketKassa.Services;

Console.OutputEncoding = Encoding.UTF8;
if (args.Contains("--hook-selftest"))
    return HookSelfTest();
string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
var url = Arg("--url") ?? "ws://127.0.0.1:5099/ws/host";
var seconds = int.TryParse(Arg("--seconds"), out var s) ? s : 300;
var graceMs = int.TryParse(Arg("--grace-ms"), out var g) ? g : 3000;
var realInput = args.Contains("--real-input");
var consent = Arg("--consent") switch
{
    "view" => RemoteSupportHost.Consent.ViewOnly,
    "deny" => RemoteSupportHost.Consent.Deny,
    _ => RemoteSupportHost.Consent.Allow,
};

PosLogger.Observer = (_, category, message) => Console.WriteLine($"LOG [{category}] {message}"); // журнал кассы в консоль стенда
Environment.SetEnvironmentVariable("NURREMOTE_RELAY", url);

var host = new RemoteSupportHost(simulateInput: !realInput) { ClientPriorityMs = graceMs };
host.CodeReceived += (code, pin, _) => Console.WriteLine($"CODE={code} PIN={pin}");
host.ConsentRequested += op =>
{
    Console.WriteLine($"согласие: оператор «{op}» — {consent} стендом");
    return Task.FromResult(consent);
};
host.SessionStarted += op => Console.WriteLine($"сеанс начат: {op}");
host.InputExecuted += trace => Console.WriteLine("INPUT " + trace);
host.ClientActiveChanged += active => Console.WriteLine("CLIENT-ACTIVE " + (active ? "true" : "false"));
host.Ended += reason => Console.WriteLine("конец: " + reason);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
stop.Token.Register(() => _ = host.StopAsync());
_ = Task.Run(async () =>
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        switch (line.Trim())
        {
            case "client":
                host.NoteClientInput();
                Console.WriteLine("кассир коснулся мыши/клавиатуры (стенд)");
                break;
            case "stop":
                await host.StopAsync();
                break;
        }
    }
});
await host.RunAsync("Магазин Стенд", "1.17.99");
return 0;

// 2026-10-26: самопроверка хуков ввода БЕЗ настоящей мыши: шлём ТОЛЬКО клавишу VK 0xE8 (не назначена, символов не печатает).
// Своя клавиша (с меткой кассы) не должна считаться вводом кассира; чужая (без метки) — должна. Эти события видят хуки и
// запасной способ (GetLastInputInfo). Кода выхода 0 — всё верно.
static int HookSelfTest()
{
    var ok = true;
    void Check(bool cond, string what)
    {
        Console.WriteLine((cond ? "OK   " : "FAIL ") + what);
        ok &= cond;
    }
    using var w = new ClientInputWatcher();
    w.Start();
    Check(w.HooksInstalled, "хуки мыши и клавиатуры поставлены");
    Thread.Sleep(300);
    Check(!w.IsActive(1000), "тишина: кассир не работает");
    Key(0xE8, ClientInputWatcher.OwnMarker, w);
    Thread.Sleep(400);
    Check(!w.IsActive(1000), "свой ввод (с меткой кассы) не считается вводом кассира (хук и GetLastInputInfo)");
    Wheel0(ClientInputWatcher.OwnMarker, w);
    Thread.Sleep(400);
    Check(!w.IsActive(1000), "своё колесо мыши (с меткой кассы) не считается вводом кассира (смещение dwExtraInfo в хуке мыши верное)");
    Key(0xE8, IntPtr.Zero, null);
    Thread.Sleep(400);
    Check(w.IsActive(1000), "чужой ввод (без метки) — кассир работает");
    Thread.Sleep(1300);
    Check(!w.IsActive(1000), "через паузу — кассир снова не работает");
    w.Stop();
    Check(!w.HooksInstalled, "после Stop хуки сняты");
    Key(0xE8, IntPtr.Zero, null);
    Thread.Sleep(300);
    Check(!w.IsActive(1000), "после Stop ввод не наблюдается");
    return ok ? 0 : 1;
}

static void Key(ushort vk, IntPtr extra, ClientInputWatcher? own)
{
    own?.NoteOwnInput();
    var down = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwExtraInfo = extra } };
    var up = new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = 2, dwExtraInfo = extra } };
    SendInput(2, new[] { down, up }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
}

[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
static extern uint SendInput(uint count, INPUT[] inputs, int size);


// INPUT на x64: type(4)+выравнивание(4)+union(32: MOUSEINPUT — самый большой) — как в RemoteSupportHost.

// Колесо мыши с нулевым сдвигом (ничего не прокручивает): проверка смещения dwExtraInfo в MSLLHOOKSTRUCT без движения настоящей мыши.
static void Wheel0(IntPtr extra, ClientInputWatcher? own)
{
    own?.NoteOwnInput();
    var w = new INPUT_M { type = 0, mi = new MOUSEINPUT { dwFlags = 0x0800, mouseData = 0, dwExtraInfo = extra } };
    SendInputM(1, new[] { w }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT_M>());
}

[System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
static extern uint SendInputM(uint count, INPUT_M[] inputs, int size);



// Структуры Win32 (типы — после всех операторов верхнего уровня)
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; public long pad; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct INPUT { public uint type; public KEYBDINPUT ki; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct INPUT_M { public uint type; public MOUSEINPUT mi; }

