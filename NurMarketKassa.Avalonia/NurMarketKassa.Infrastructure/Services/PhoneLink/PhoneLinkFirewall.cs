using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NurMarketKassa.Services.PhoneLink;

/// <summary>2026-11-27, жалоба владельца «не работает подключение сканера с телефона»: проверка, пустит ли брандмауэр Windows телефон
/// к кассе. Только ЧТЕНИЕ (правил касса по-прежнему не создаёт). Частый случай: в окне Windows «Разрешить доступ» по умолчанию
/// отмечена только частная сеть — Windows создаёт «разрешить» для частных сетей и «блокировать» для общедоступных, а новая сеть
/// Wi-Fi в Windows 11 по умолчанию общедоступная: телефон в той же сети, а соединение молча отбрасывается (у телефона — таймаут).
/// Чистая логика — <see cref="Evaluate"/> (проверяется тестом), чтение Windows — <see cref="Check"/>.</summary>
public static class PhoneLinkFirewall
{
    public enum Verdict
    {
        /// <summary>Не удалось проверить (не Windows, нет доступа) — молчим.</summary>
        Unknown,
        /// <summary>Брандмауэр выключен или есть разрешающее правило для этой сети.</summary>
        Allowed,
        /// <summary>Есть запрещающее правило для кассы в этой сети.</summary>
        Blocked,
        /// <summary>Разрешающего правила нет — входящие соединения отбрасываются.</summary>
        NoRule,
    }

    /// <summary>Профили брандмауэра (NET_FW_PROFILE_TYPE2).</summary>
    public const int ProfileDomain = 1, ProfilePrivate = 2, ProfilePublic = 4;

    /// <summary>Правило брандмауэра (только нужные поля).</summary>
    public sealed record Rule(string? Application, bool Inbound, bool Enabled, bool Allow, int Profiles, int Protocol, string? LocalPorts);

    public sealed record Result(Verdict Verdict, int Profile);

    /// <summary>Решение по правилам: запрет сильнее разрешения (как в самом брандмауэре).</summary>
    /// <param name="profile">Профиль сети, в которой касса ждёт телефон (1/2/4).</param>
    /// <param name="firewallOn">Брандмауэр включён для этого профиля.</param>
    public static Verdict Evaluate(int profile, bool firewallOn, IEnumerable<Rule> rules, string exePath, int port)
    {
        if (!firewallOn)
            return Verdict.Allowed;
        var allowed = false;
        foreach (var r in rules)
        {
            if (!r.Inbound || !r.Enabled || (r.Profiles & profile) == 0)
                continue;
            if (r.Protocol is not (6 or 256)) // TCP или «любой»
                continue;
            var app = r.Application?.Trim();
            bool matches;
            if (!string.IsNullOrEmpty(app) && app != "*")
                matches = SamePath(app, exePath) && PortMatches(r.LocalPorts, port);
            else
                matches = PortListed(r.LocalPorts, port); // правило «по порту» без программы — только если порт назван явно
            if (!matches)
                continue;
            if (!r.Allow)
                return Verdict.Blocked;
            allowed = true;
        }
        return allowed ? Verdict.Allowed : Verdict.NoRule;
    }

    private static bool SamePath(string a, string b)
    {
        static string Norm(string p)
        {
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(p)).TrimEnd('\\', '/'); }
            catch { return p.Trim(); }
        }
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);
    }

    private static bool PortMatches(string? ports, int port) =>
        string.IsNullOrWhiteSpace(ports) || ports.Trim() == "*" || PortListed(ports, port);

    private static bool PortListed(string? ports, int port)
    {
        if (string.IsNullOrWhiteSpace(ports))
            return false;
        foreach (var raw in ports.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            if (dash > 0 && int.TryParse(raw[..dash], out var lo) && int.TryParse(raw[(dash + 1)..], out var hi))
            {
                if (port >= lo && port <= hi)
                    return true;
            }
            else if (int.TryParse(raw, out var single) && single == port)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Проверить брандмауэр Windows для сети, к которой относится адрес <paramref name="ipv4"/>. Медленно (сотни правил,
    /// ~0,2 с) — вызывать не на UI-потоке. Любая ошибка → Unknown.</summary>
    public static Result Check(string ipv4, int port)
    {
        if (!OperatingSystem.IsWindows() || port <= 0)
            return new Result(Verdict.Unknown, 0);
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return new Result(Verdict.Unknown, 0);
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType is null)
                return new Result(Verdict.Unknown, 0);
            var policy = Activator.CreateInstance(policyType)!;
            var profile = ProfileOf(ipv4) ?? WorstActiveProfile(policy);
            var on = Convert.ToBoolean(Get(policy, "FirewallEnabled", profile));
            var rules = new List<Rule>();
            if (on && Get(policy, "Rules") is IEnumerable all)
            {
                foreach (var r in all)
                {
                    if (r is null)
                        continue;
                    rules.Add(new Rule(
                        Get(r, "ApplicationName") as string,
                        Convert.ToInt32(Get(r, "Direction")) == 1,
                        Convert.ToBoolean(Get(r, "Enabled")),
                        Convert.ToInt32(Get(r, "Action")) == 1,
                        Convert.ToInt32(Get(r, "Profiles")),
                        Convert.ToInt32(Get(r, "Protocol")),
                        Get(r, "LocalPorts") as string));
                }
            }
            return new Result(Evaluate(profile, on, rules, exe, port), profile);
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PHONELINK: брандмауэр не проверен ({ex.GetType().Name}).", "INFO");
            return new Result(Verdict.Unknown, 0);
        }
    }

    private static object? Get(object com, string name, params object[] args) =>
        com.GetType().InvokeMember(name, BindingFlags.GetProperty, null, com, args.Length == 0 ? null : args);

    /// <summary>Сейчас подключены сети нескольких типов — берём «худший» (общедоступная строже частной).</summary>
    private static int WorstActiveProfile(object policy)
    {
        var active = Convert.ToInt32(Get(policy, "CurrentProfileTypes"));
        if ((active & ProfilePublic) != 0) return ProfilePublic;
        if ((active & ProfilePrivate) != 0) return ProfilePrivate;
        return (active & ProfileDomain) != 0 ? ProfileDomain : ProfilePublic;
    }

    /// <summary>Профиль сети адаптера с этим адресом (NetworkListManager: категория 0 — общедоступная, 1 — частная, 2 — домен).</summary>
    private static int? ProfileOf(string ipv4)
    {
        if (!OperatingSystem.IsWindows() || !IPAddress.TryParse(ipv4, out var ip))
            return null;
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(ip)));
            if (nic is null || !Guid.TryParse(nic.Id, out var adapter))
                return null;
            var nlmType = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (nlmType is null)
                return null;
            var nlm = Activator.CreateInstance(nlmType)!;
            if (nlm.GetType().InvokeMember("GetNetworkConnections", BindingFlags.InvokeMethod, null, nlm, null) is not IEnumerable connections)
                return null;
            foreach (var c in connections)
            {
                if (c is null)
                    continue;
                // GUID через IDispatch не передаётся («Value does not fall within the expected range») — читаем через vtable (проверено на ПК 27.11).
                if (c is not INetworkConnection connection || connection.GetAdapterId() != adapter)
                    continue;
                if (connection.GetNetwork() is not INetwork network)
                    return null;
                return network.GetCategory() switch { 1 => ProfilePrivate, 2 => ProfileDomain, _ => ProfilePublic };
            }
        }
        catch
        {
            // нет NetworkListManager — возьмём худший из активных профилей
        }
        return null;
    }

    // NetworkListManager (netlistmgr.h): порядок методов как в vtable — менять нельзя.
    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
        int GetDomainType();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        int GetCategory();
        void SetCategory(int category);
    }
}
