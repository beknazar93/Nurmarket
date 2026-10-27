// Проверка ретранслятора NurMarket (2026-09-29). Поднимает СВОЙ сервер на 127.0.0.1 с временной папкой.
//   dotnet run -c Release -- e2e
//   dotnet run -c Release -- auth
//   dotnet run -c Release -- load [--sessions 50] [--seconds 30] [--fps 15]
//   --server-dll <путь к Server.dll>  (по умолчанию ../../Server/bin/Release/net8.0/Server.dll)
using System.Text;
using NurRemoteSupport.RelayTests;

Console.OutputEncoding = Encoding.UTF8;
var mode = args.FirstOrDefault() ?? "e2e";
string? dllArg = null;
int sessions = 50, seconds = 30, fps = 15;
for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server-dll": dllArg = args[++i]; break;
        case "--sessions": sessions = int.Parse(args[++i]); break;
        case "--seconds": seconds = int.Parse(args[++i]); break;
        case "--fps": fps = int.Parse(args[++i]); break;
    }
}
var dll = ServerProcess.ServerDll(dllArg);
return mode switch
{
    "e2e" => await new E2E(dll).RunAsync(),
    "auth" => await new E2EAuth(dll).RunAsync(), // 2026-10-27: вход, смена пароля, билеты, прокси, ротация (пакет С2)
    "load" => await new Load(dll, sessions, seconds, fps).RunAsync(),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("RelayTests e2e | auth | load [--sessions 50 --seconds 30 --fps 15] [--server-dll путь]");
    return 2;
}
