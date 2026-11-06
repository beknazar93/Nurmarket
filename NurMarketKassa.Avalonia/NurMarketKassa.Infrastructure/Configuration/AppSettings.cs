using System.IO;
using System.Text.Json;

namespace NurMarketKassa.Configuration;

/// <summary>
/// ������� ���������. ����������: ���������� ��������� DESKTOP_MARKET_API_URL (��� � Python config.py).
/// </summary>
public sealed class AppSettings
{
    public string ApiBaseUrl { get; init; } = "https://app.nurcrm.kg";

    public ReceiptPrinterSettings ReceiptPrinter { get; init; } = new();

    public ScaleSettings Scale { get; init; } = new();

    public CatalogUiSettings Catalog { get; init; } = new();

    public UpdateSettings Updates { get; init; } = new();

    public MySqlSettings MySql { get; init; } = new();

    public PostgreSqlSettings PostgreSql { get; init; } = new();

    public HardwareSettings Hardware { get; init; } = new();

    public static AppSettings Load()
    {
        var env = Environment.GetEnvironmentVariable("DESKTOP_MARKET_API_URL")?.Trim();
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        AppSettings? fromFile = null;
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                fromFile = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                NurMarketKassa.Services.PosLogger.Log($"Settings load failed: {ex.GetType().Name}", "WARNING");
            }
        }

        var merged = fromFile ?? new AppSettings();
        merged = CoalesceSections(merged);

        if (!string.IsNullOrWhiteSpace(env))
        {
            merged = new AppSettings
            {
                ApiBaseUrl = env.TrimEnd('/'),
                ReceiptPrinter = merged.ReceiptPrinter,
                Scale = merged.Scale,
                Catalog = merged.Catalog,
                Updates = merged.Updates,
                MySql = merged.MySql,
                PostgreSql = merged.PostgreSql ?? new PostgreSqlSettings(),
                Hardware = merged.Hardware ?? new HardwareSettings(),
            };
        }

        var manifestEnv = Environment.GetEnvironmentVariable("DESKTOP_MARKET_UPDATE_MANIFEST_URL")?.Trim();
        if (!string.IsNullOrWhiteSpace(manifestEnv))
            manifestEnv = RequireHttps(manifestEnv, "DESKTOP_MARKET_UPDATE_MANIFEST_URL"); // 2026-11-06: В3 Л4-21 — адрес обновлений только по https (внутренний адрес стенда — как есть)
        if (!string.IsNullOrWhiteSpace(manifestEnv))
        {
            merged = new AppSettings
            {
                ApiBaseUrl = merged.ApiBaseUrl,
                ReceiptPrinter = merged.ReceiptPrinter,
                Scale = merged.Scale,
                Catalog = merged.Catalog,
                Updates = new UpdateSettings
                {
                    ManifestUrl = manifestEnv,
                    CheckOnStartup = merged.Updates.CheckOnStartup,
                    MinHoursBetweenChecks = merged.Updates.MinHoursBetweenChecks,
                },
                MySql = merged.MySql,
                PostgreSql = merged.PostgreSql,
                Hardware = merged.Hardware,
            };
        }

        // 2026-11-06: В3 Л4-21/Л5-16 — адрес API принимается только по https; http оставлен для стенда на этом же ПК или в локальной сети (127.0.0.1, 192.168.x.x…).
        // Пароль и токены не должны уходить по открытому каналу на внешний адрес («http://app.nurcrm.kg» из переменной или файла поднимается до https).
        var safeUrl = NormalizeApiUrl(merged.ApiBaseUrl);
        if (!string.Equals(safeUrl, merged.ApiBaseUrl, StringComparison.Ordinal))
            merged = new AppSettings
            {
                ApiBaseUrl = safeUrl,
                ReceiptPrinter = merged.ReceiptPrinter,
                Scale = merged.Scale,
                Catalog = merged.Catalog,
                Updates = merged.Updates,
                MySql = merged.MySql,
                PostgreSql = merged.PostgreSql,
                Hardware = merged.Hardware,
            };

        return merged;
    }

    /// <summary>2026-11-06: В3 Л4-21 — адрес API с обязательным https для внешних серверов. Чистая функция (проверяется тестом, окружение не читает).
    /// Разрешён http только для петли (localhost, 127.x, ::1) и частных адресов (10.x, 172.16–31.x, 192.168.x, 169.254.x) — стенды и заглушки;
    /// любой другой http-адрес меняется на https. Пустая строка и адрес без схемы возвращаются как есть.</summary>
    public static string NormalizeApiUrl(string? url)
    {
        var raw = (url ?? "").Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            return raw;
        if (IsLocalHost(uri.Host))
            return raw;
        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port };
        var upgraded = builder.Uri.ToString().TrimEnd('/');
        NurMarketKassa.Services.PosLogger.Log($"Адрес API «{raw}» без шифрования заменён на https.", "WARNING");
        return upgraded;
    }

    private static string RequireHttps(string url, string source)
    {
        var safe = NormalizeApiUrl(url);
        if (!string.Equals(safe, url, StringComparison.Ordinal))
            NurMarketKassa.Services.PosLogger.Log($"{source}: адрес обновлений поднят до https.", "WARNING");
        return safe;
    }

    /// <summary>Петля или частная сеть — туда http допустим (стенд). Имя «localhost» и IP-адреса; прочие имена — внешние.</summary>
    internal static bool IsLocalHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            return false;
        if (System.Net.IPAddress.IsLoopback(ip))
            return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    private static AppSettings CoalesceSections(AppSettings s) =>
        new()
        {
            ApiBaseUrl = s.ApiBaseUrl,
            ReceiptPrinter = s.ReceiptPrinter ?? new ReceiptPrinterSettings(),
            Scale = s.Scale ?? new ScaleSettings(),
            Catalog = s.Catalog ?? new CatalogUiSettings(),
            Updates = s.Updates ?? new UpdateSettings(),
            MySql = s.MySql ?? new MySqlSettings(),
            PostgreSql = s.PostgreSql ?? new PostgreSqlSettings(),
            Hardware = s.Hardware ?? new HardwareSettings(),
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
