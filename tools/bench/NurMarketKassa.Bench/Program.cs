using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

// 2026-11-05: точка входа замеров. Папка данных кассы — ВРЕМЕННАЯ (до любого обращения к сервисам), сеть — заглушка:
// настоящие %APPDATA%\NurMarketKassa и боевой сервер не трогаются. Замеры идут в этом же процессе (без пересборки проекта на каждый прогон —
// ПК слабый), короткий прогон; для точных цифр запускать без других тяжёлых программ.
var root = Path.Combine(Path.GetTempPath(), "nurmarket-bench", Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("NURMARKET_DATA_ROOT", root);
Environment.SetEnvironmentVariable("DESKTOP_MARKET_API_URL", "http://127.0.0.1:9");
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { Directory.Delete(root, recursive: true); } catch { /* файл мог быть занят — это временная папка */ }
};

var config = DefaultConfig.Instance
    .AddJob(Job.ShortRun.WithToolchain(InProcessNoEmitToolchain.Instance))
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
