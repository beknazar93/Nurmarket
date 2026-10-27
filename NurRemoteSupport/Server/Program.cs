// Ретранслятор удалённой поддержки NurMarket (2026-09-29).
// Запуск сервера:        dotnet Server.dll [--urls http://0.0.0.0:8080]
// Добавить/сбросить:     dotnet Server.dll add-operator <login> "<Имя>"   (пароль — со stdin)
// Удалить / список:      dotnet Server.dll remove-operator <login> | list-operators
// Протокол: ../PROTOCOL.md
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using NurRemoteSupport.Server;

if (args.Length > 0 && !args[0].StartsWith('-'))
    return Cli.Run(args);

// Корень — папка программы (там wwwroot), а не текущая папка: сервер можно запускать откуда угодно.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
// Необязательный файл настроек рядом с данными (в Docker — смонтированный), переменные окружения важнее.
builder.Configuration.AddJsonFile(Environment.GetEnvironmentVariable("RELAY_CONFIG") ?? "relay.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

// Только консоль (в Docker её собирает docker logs; журнал событий Windows не нужен и без прав падает).
// Формат JSON — переменной Logging__Console__FormatterName=json.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
// Адреса с ?token= не должны попадать в лог — запросы ASP.NET пишем только предупреждения.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

var relay = (builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions()).ResolvePaths();
Directory.CreateDirectory(relay.DataDir);
builder.Services.AddSingleton(relay);
builder.Services.AddSingleton(new OperatorStore(relay.OperatorsPath));
builder.Services.AddSingleton<Journal>();
builder.Services.AddSingleton<NotesStore>(); // 2026-10-16, v1.1: заметки операторов по кассам
builder.Services.AddSingleton<SessionRegistry>();
builder.Services.AddSingleton<AttemptLimiter>();
builder.Services.AddSingleton<ConnectionGate>();
builder.Services.AddSingleton(sp => new TokenService(
    TokenService.LoadOrCreateKey(relay, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Relay.Tokens")),
    TimeSpan.FromHours(relay.TokenLifetimeHours),
    sp.GetRequiredService<OperatorStore>(),
    Path.Combine(relay.DataDir, "revoked-tokens.txt")));
builder.Services.AddHostedService<SweepService>();

builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 64 * 1024;
});

// 2026-10-27 (В-11): X-Forwarded-For/Proto принимаются только от доверенных прокси. По умолчанию — только loopback
// (nginx/Caddy на этой же машине). Relay:TrustedProxies — адреса и сети через запятую ("172.16.0.0/12,10.0.0.5") для Docker.
// Relay:TrustForwardedHeaders=true (устарело) — верить любому: допустимо, только если порт сервера недоступен снаружи.
builder.Services.Configure<ForwardedHeadersOptions>(f =>
{
    f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    f.ForwardLimit = 1;
    if (relay.TrustForwardedHeaders)
    {
        f.KnownNetworks.Clear();
        f.KnownProxies.Clear();
    }
    else if (!string.IsNullOrWhiteSpace(relay.TrustedProxies))
    {
        f.KnownNetworks.Clear();
        f.KnownProxies.Clear();
        foreach (var part in relay.TrustedProxies.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = part.IndexOf('/');
            if (slash > 0 && System.Net.IPAddress.TryParse(part[..slash], out var net) && int.TryParse(part[(slash + 1)..], out var bits))
                f.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(net, bits));
            else if (System.Net.IPAddress.TryParse(part, out var one))
                f.KnownProxies.Add(one);
            else
                throw new InvalidOperationException($"Relay:TrustedProxies: непонятный адрес или сеть «{part}».");
        }
    }
});

builder.Services.AddRateLimiter(r =>
{
    r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    r.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        RelayEndpoints.IpKey(RelayEndpoints.ClientIp(ctx)),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = relay.LoginPerMinutePerIp, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Relay");
_ = app.Services.GetRequiredService<TokenService>(); // создать/прочитать ключ сразу, а не при первом входе

if (relay.TrustForwardedHeaders)
    startupLog.LogWarning("Relay:TrustForwardedHeaders=true: заголовкам X-Forwarded-* верим от любого. Лучше Relay:TrustedProxies.");
app.UseForwardedHeaders();

// Заголовки безопасности для страницы оператора
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; img-src 'self' blob: data:; connect-src 'self'; style-src 'self'; script-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    // 2026-10-27 (С-4): ответы API с токенами и данными не кладутся в кэш браузера и прокси.
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        h["Cache-Control"] = "no-store";
        h["Pragma"] = "no-cache";
    }
    // 2026-10-27: HSTS и от самого сервера, когда запрос пришёл по HTTPS (в том числе по X-Forwarded-Proto от доверенного прокси).
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000";
    await next();
});

app.UseRateLimiter();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/health", () => Results.Text("ok"));

app.MapPost("/api/login", async (LoginRequest req, OperatorStore store, TokenService tokens, AttemptLimiter limiter, HttpContext http) =>
{
    var login = OperatorStore.NormalizeLogin(req.Login ?? "");
    var password = req.Password ?? "";
    if (login.Length == 0 || login.Length > 64 || password.Length == 0 || password.Length > 256)
        return Results.BadRequest(new { error = "bad-request" });
    var ip = RelayEndpoints.ClientIp(http);
    // 2026-10-27 (С-3): блокировка по «логин + сеть». Чужие ошибки с других адресов верный пароль не блокируют;
    // если ошибок со всех адресов очень много (подбор) — ответ только замедляется, а не запирается.
    var failKey = "loginfail:" + login + "|" + RelayEndpoints.IpKey(ip);
    var allKey = "loginfail-all:" + login;
    var window = TimeSpan.FromMinutes(15);
    if (limiter.Count(failKey, window) >= relay.LoginFailuresPer15MinPerLoginIp)
    {
        startupLog.LogWarning("Вход {Login} временно заблокирован (много ошибок с этого адреса), IP {Ip}", Msg.LogSafe(login), ip);
        return Results.Json(new { error = "rate-limited" }, statusCode: 429);
    }
    var allFails = limiter.Count(allKey, window);
    if (allFails >= relay.LoginGlobalFailureDelayAfter)
        await Task.Delay(Math.Min(5000, (allFails - relay.LoginGlobalFailureDelayAfter + 1) * 100));
    var op = store.Verify(login, password);
    if (op is null)
    {
        limiter.Hit(failKey);
        limiter.Hit(allKey);
        startupLog.LogWarning("Неудачный вход {Login} с {Ip}", Msg.LogSafe(login), ip);
        return Results.Json(new { error = "bad-credentials" }, statusCode: 401);
    }
    var (token, expires) = tokens.Issue(op);
    startupLog.LogInformation("Оператор {Login} вошёл с {Ip}", op.Login, ip);
    return Results.Ok(new { token, name = op.Name, login = op.Login, expiresAt = expires });
}).RequireRateLimiting("login");

// 2026-10-27 (С-5): смена пароля самим оператором. Старый пароль проверяется, неверный считается как ошибка входа;
// после смены все прежние токены и открытые соединения этого оператора перестают действовать (в ответе — свежий токен).
app.MapPost("/api/password", (PasswordRequest req, HttpContext http, TokenService tokens, OperatorStore store, AttemptLimiter limiter) =>
{
    if (Auth(http, tokens) is not { } me) return Results.Unauthorized();
    var ip = RelayEndpoints.ClientIp(http);
    var failKey = "loginfail:" + me.Login + "|" + RelayEndpoints.IpKey(ip);
    var allKey = "loginfail-all:" + me.Login;
    var window = TimeSpan.FromMinutes(15);
    if (limiter.Count(failKey, window) >= relay.LoginFailuresPer15MinPerLoginIp)
        return Results.Json(new { error = "rate-limited" }, statusCode: 429);
    var oldPw = req.Old ?? "";
    var newPw = req.New ?? "";
    if (oldPw.Length == 0 || oldPw.Length > 256 || newPw.Length > 256)
        return Results.BadRequest(new { error = "bad-request" });
    if (store.Verify(me.Login, oldPw) is null)
    {
        limiter.Hit(failKey);
        limiter.Hit(allKey);
        startupLog.LogWarning("Смена пароля {Login}: неверный текущий пароль, IP {Ip}", me.Login, ip);
        return Results.Json(new { error = "bad-credentials" }, statusCode: 403);
    }
    if (newPw.Length < relay.MinPasswordLength)
        return Results.Json(new { error = "weak-password", min = relay.MinPasswordLength }, statusCode: 400);
    if (newPw == oldPw)
        return Results.Json(new { error = "same-password" }, statusCode: 400);
    var (op, _) = store.Upsert(me.Login, "", newPw);
    var (token, expires) = tokens.Issue(op);
    startupLog.LogInformation("Оператор {Login} сменил пароль (IP {Ip}); прежние входы недействительны", op.Login, ip);
    return Results.Ok(new { token, name = op.Name, login = op.Login, expiresAt = expires });
}).RequireRateLimiting("login");

// 2026-10-27 (С-4): выход — токен отзывается сразу (до конца срока), открытые с ним соединения закрываются.
app.MapPost("/api/logout", (HttpContext http, TokenService tokens, SessionRegistry registry) =>
{
    if (AuthCred(http, tokens) is not { } cred) return Results.Unauthorized();
    tokens.Revoke(cred);
    registry.KickBySignature(cred.Sig);
    startupLog.LogInformation("Оператор {Login} вышел", cred.Login);
    return Results.Ok(new { ok = true });
});

// 2026-10-27 (С-4): одноразовый билет на 30 с для WebSocket страницы: токен не попадает в адрес и журналы прокси.
app.MapPost("/api/ws-ticket", (HttpContext http, TokenService tokens, AttemptLimiter limiter) =>
{
    if (AuthCred(http, tokens) is not { } cred) return Results.Unauthorized();
    if (!limiter.TryHit("ticket:" + cred.Login, 30, TimeSpan.FromMinutes(1)))
        return Results.Json(new { error = "rate-limited" }, statusCode: 429);
    return Results.Ok(new { ticket = tokens.IssueTicket(cred), expiresIn = TokenService.TicketSeconds });
});

app.MapGet("/api/me", (HttpContext http, TokenService tokens) =>
    Auth(http, tokens) is { } me ? Results.Ok(new { login = me.Login, name = me.Name }) : Results.Unauthorized());

app.MapGet("/api/sessions", async (HttpContext http, TokenService tokens, SessionRegistry registry, Journal journal, int? limit) =>
{
    if (Auth(http, tokens) is null) return Results.Unauthorized();
    var active = registry.Snapshot()
        .Where(s => s.State != SessionState.Ended)
        .OrderByDescending(s => s.Created)
        .Select(s => new
        {
            sessionId = s.Id,
            state = s.State switch
            {
                SessionState.Pending => "pending",
                SessionState.AwaitingConsent => "awaiting-consent",
                SessionState.Active => "active",
                _ => "ended",
            },
            company = s.Host.Company,
            machine = s.Host.Machine,
            app = s.Host.App,
            version = s.Host.Version,
            hostIp = s.HostConn.Ip,
            user = s.Host.User, // 2026-10-16, v1.1: кассир и что случилось — для очереди программы оператора
            note = s.Host.Note,
            caps = s.Host.Caps ?? Array.Empty<string>(),
            created = s.Created,
            expiresAt = s.ExpiresAt,
            @operator = s.Operator?.Identity.Login,
            operatorName = s.Operator?.Identity.Name,
            start = s.Started,
            bytesToOperator = Interlocked.Read(ref s.BytesToOperator),
        })
        .ToList();
    var recent = await journal.TailAsync(Math.Clamp(limit ?? relay.JournalPageSize, 1, 1000));
    return Results.Ok(new { active, recent });
});

// 2026-10-16, v1.1: история обращений — заметки операторов по кассам (NotesStore).
app.MapGet("/api/notes", async (HttpContext http, TokenService tokens, NotesStore notes, string? company, string? machine, int? limit) =>
{
    if (Auth(http, tokens) is null) return Results.Unauthorized();
    var list = await notes.ListAsync(company, machine, Math.Clamp(limit ?? 50, 1, 500));
    return Results.Ok(new { notes = list });
});

app.MapPost("/api/notes", async (HttpContext http, TokenService tokens, NotesStore notes, AttemptLimiter limiter, NoteRequest req) =>
{
    if (Auth(http, tokens) is not { } me) return Results.Unauthorized();
    // 2026-10-27 (С-7): не больше NotesPerMinute заметок в минуту от одного оператора — файл не раздувается.
    if (!limiter.TryHit("note:" + me.Login, relay.NotesPerMinute, TimeSpan.FromMinutes(1)))
        return Results.Json(new { error = "rate-limited" }, statusCode: 429);
    var text = (req.Text ?? "").Trim();
    if (text.Length == 0 || text.Length > 4000) return Results.BadRequest(new { error = "text" });
    static string Cut(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : t;
    var entry = new NoteEntry
    {
        Operator = me.Login,
        OperatorName = me.Name,
        Company = Cut(req.Company, 100),
        Machine = Cut(req.Machine, 100),
        SessionId = string.IsNullOrWhiteSpace(req.SessionId) ? null : Cut(req.SessionId, 64),
        Text = text,
    };
    await notes.AddAsync(entry);
    return Results.Ok(entry);
});

app.Map("/ws/host", RelayEndpoints.HandleHostAsync);
app.Map("/ws/operator", RelayEndpoints.HandleOperatorAsync);

var registryForShutdown = app.Services.GetRequiredService<SessionRegistry>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    try { registryForShutdown.ShutdownAsync().Wait(TimeSpan.FromSeconds(5)); } catch { }
});

var opCount = app.Services.GetRequiredService<OperatorStore>().All().Count;
startupLog.LogInformation("Ретранслятор NurMarket запущен. Данные: {Dir}, операторов: {Count}", Path.GetFullPath(relay.DataDir), opCount);
if (opCount == 0)
    startupLog.LogWarning("Нет ни одного оператора. Добавьте: dotnet Server.dll add-operator <login> \"<Имя>\"");

app.Run();
return 0;

static OperatorIdentity? Auth(HttpContext http, TokenService tokens) => AuthCred(http, tokens)?.Identity;

static OperatorCredential? AuthCred(HttpContext http, TokenService tokens)
{
    var h = http.Request.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? tokens.Parse(h[7..].Trim()) : null;
}

public sealed record LoginRequest(string? Login, string? Password);

public sealed record PasswordRequest(string? Old, string? New);

/// <summary>Раз в секунду: истёкшие коды, неотвеченные запросы согласия, пределы сеанса. Раз в минуту (2026-10-27, В-5): закрыть соединения операторов с недействительным входом.</summary>
public sealed class SweepService(SessionRegistry registry, TokenService tokens, RelayOptions o, ILogger<SweepService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        var every = Math.Max(1, o.OperatorRecheckSeconds);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { registry.Sweep(); }
            catch (Exception ex) { log.LogError(ex, "Ошибка обслуживания реестра"); }
            if (++tick < every) continue;
            tick = 0;
            try
            {
                registry.RecheckOperators(tokens);
                tokens.Sweep();
            }
            catch (Exception ex) { log.LogError(ex, "Ошибка проверки входов операторов"); }
        }
    }
}

/// <summary>Команды обслуживания операторов (без запуска сервера).</summary>
public static class Cli
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try { Console.InputEncoding = Encoding.UTF8; } catch { }
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile(Environment.GetEnvironmentVariable("RELAY_CONFIG") ?? "relay.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var o = (config.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions()).ResolvePaths();
        var store = new OperatorStore(o.OperatorsPath);

        switch (args[0])
        {
            case "add-operator":
            {
                if (args.Length < 2)
                {
                    Console.Error.WriteLine("Использование: add-operator <login> \"<Имя>\"   (пароль читается со stdin)");
                    return 2;
                }
                var login = OperatorStore.NormalizeLogin(args[1]);
                if (!OperatorStore.IsValidLogin(login))
                {
                    Console.Error.WriteLine("Логин: 2–32 символа, латиница, цифры, точка, _ или -.");
                    return 2;
                }
                var name = args.Length > 2 ? string.Join(' ', args.Skip(2)) : "";
                var password = ReadPassword();
                if (password is null) return 2;
                if (password.Length < o.MinPasswordLength)
                {
                    Console.Error.WriteLine($"Пароль слишком короткий (нужно не меньше {o.MinPasswordLength} символов).");
                    return 2;
                }
                var (op, created) = store.Upsert(login, name, password);
                Console.WriteLine(created
                    ? $"Оператор добавлен: {op.Login} ({op.Name}). Файл: {Path.GetFullPath(store.FilePath)}"
                    : $"Пароль оператора {op.Login} ({op.Name}) сброшен. Старые входы недействительны.");
                return 0;
            }
            case "remove-operator":
            {
                if (args.Length < 2) { Console.Error.WriteLine("Использование: remove-operator <login>"); return 2; }
                var ok = store.Remove(args[1]);
                Console.WriteLine(ok ? $"Оператор {args[1]} удалён." : $"Оператора {args[1]} нет.");
                return ok ? 0 : 1;
            }
            case "list-operators":
                foreach (var op in store.All())
                    Console.WriteLine($"{op.Login,-20} {op.Name,-30} {(op.Disabled ? "отключён" : "")} изменён {op.Updated:yyyy-MM-dd HH:mm}");
                return 0;
            default:
                Console.Error.WriteLine("Команды: add-operator <login> \"<Имя>\" | remove-operator <login> | list-operators");
                return 2;
        }
    }

    private static string? ReadPassword()
    {
        if (Console.IsInputRedirected)
            // Windows PowerShell 5.1 ставит в начало переданного по трубе текста BOM (EF BB BF) — убираем (2026-09-29)
            return Console.In.ReadLine()?.TrimStart('﻿').TrimEnd('\r', '\n');
        var first = ReadMasked("Пароль: ");
        var second = ReadMasked("Ещё раз: ");
        if (first != second)
        {
            Console.Error.WriteLine("Пароли не совпали.");
            return null;
        }
        return first;
    }

    private static string ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
