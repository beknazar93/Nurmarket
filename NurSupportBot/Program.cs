using System.Text;
using NurSupportBot;
using NurSupportBot.Bot;
using NurSupportBot.Data;
using NurSupportBot.Services;
using NurSupportBot.Telegram;

Console.OutputEncoding = Encoding.UTF8;
var baseDir = AppContext.BaseDirectory;
var config = BotConfig.Load(baseDir);
Directory.CreateDirectory(config.DataDir);
using var db = new Db(Path.Combine(config.DataDir, "bot.db"));

// Первый запуск: база инструкций пуста — берём черновик из seed/kb.json.
var seed = Path.Combine(baseDir, "seed", "kb.json");
if (db.NodeCount() == 0 && File.Exists(seed))
{
    var added = KnowledgeBase.Import(db, File.ReadAllText(seed), replace: false);
    SupportBot.Log($"База инструкций загружена из seed/kb.json: {added} разделов и инструкций.");
}

// Служебные режимы (без Telegram):
//   --import файл.json  — заменить базу инструкций файлом;
//   --export файл.json  — выгрузить базу;
//   --search "вопрос"   — проверить, что найдёт поиск.
if (args.Length >= 2 && args[0] == "--import")
{
    Console.WriteLine($"Загружено: {KnowledgeBase.Import(db, File.ReadAllText(args[1]), replace: true)}");
    return 0;
}
if (args.Length >= 2 && args[0] == "--export")
{
    File.WriteAllText(args[1], KnowledgeBase.Export(db), new UTF8Encoding(false));
    Console.WriteLine($"Выгружено в {args[1]}");
    return 0;
}
if (args.Length >= 2 && args[0] == "--search")
{
    foreach (var query in args.Skip(1))
    {
        var hits = Search.Find(db.AllArticles(), query);
        Console.WriteLine($"«{query}» → " + (hits.Count == 0 ? "ничего" : string.Join(" | ", hits.Select(h => $"{h.Node.Title} ({h.Score:0.0})"))));
    }
    return 0;
}

//   --crm-test e-mail   — вход в NurCRM (пароль из переменной NURBOT_TEST_PASSWORD) и отчёт за сегодня и месяц.
if (args.Length >= 2 && args[0] == "--crm-test")
{
    var crm = new NurCrmClient(config.NurCrmBaseUrl);
    var login = await crm.LoginAsync(args[1], Environment.GetEnvironmentVariable("NURBOT_TEST_PASSWORD") ?? "", CancellationToken.None);
    Console.WriteLine($"Вход: {login.Name} · {login.Company} · {login.Tariff} · {login.Sector} · {login.Role}");
    var access = await crm.RefreshAsync(login.Refresh, CancellationToken.None) ?? throw new Exception("refresh не сработал");
    var today = Ui.Today;
    foreach (var (from, name) in new[] { (today, "сегодня"), (new DateTime(today.Year, today.Month, 1), "месяц") })
    {
        var r = await crm.SalesReportAsync(access, from, today, CancellationToken.None);
        Console.WriteLine($"{name}: выручка {r.Revenue}, чеков {r.Receipts}, средний {r.AvgReceipt}, нал {r.Cash}, безнал {r.NonCash}, "
                          + $"возвраты {r.Returns} ({r.ReturnsCount}), прибыль {r.Profit}, маржа {r.Margin}");
    }
    return 0;
}

if (string.IsNullOrWhiteSpace(config.BotToken) || config.BotToken.Contains("..."))
{
    Console.WriteLine("Не задан токен бота: заполните BotToken в appsettings.json (образец — appsettings.example.json) или переменную NURBOT_TOKEN.");
    return 1;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

var bot = new SupportBot(config, new TgClient(config.BotToken, config.TelegramApiUrl), db, new NurCrmClient(config.NurCrmBaseUrl), new TokenVault(config.DataDir));
try
{
    await bot.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
}
SupportBot.Log("Бот остановлен.");
return 0;
