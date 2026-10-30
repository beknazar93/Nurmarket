using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Lan;
using NurMarketKassa.Services.PhoneLink;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-10-27, пакеты К-П2/К-П3/К-П5 (ТЗ «Мобилка и касса», путь B): связь кассы с телефонами — одно место, где живут
/// список телефонов (<see cref="PhoneLinkPairing"/>), сервер (<see cref="PhoneLinkServer"/>) и обработчик сканов
/// (<see cref="PhoneScanHandler"/>). Главное окно кассы один раз вызывает <see cref="Attach"/> (MainWindow.PhoneLink.cs);
/// окно настроек и окно «Подключить телефон» читают состояние и включают/выключают приём. Только касса: в программе владельца
/// (--owner) и на Android ничего не запускается. По умолчанию приём выключен, порт закрыт.
/// Брандмауэр Windows: правил автоматически не создаём — при первом включении Windows сама спросит «Разрешить».</summary>
public static class PhoneLinkHost
{
    private static readonly object Gate = new();
    private static PhoneLinkPairing? _pairing;
    private static PhoneLinkServer? _server;
    private static PhoneScanHandler? _scan;
    // 2026-10-27, ревью Ф-1: окно, которое сейчас обслуживает приём, и его делегаты. Сервер и обработчик создаются один раз, а к окну
    // привязываются через эти поля: вышли из кассы / сменили кассира — окно закрылось (Detach), новое окно встаёт на его место (Attach).
    private static object? _owner;
    private static Func<CancellationToken, Task<bool>>? _isTillReady;
    private static Func<string, CancellationToken, Task<PhoneScanHandler.EnqueueResult>>? _enqueue;
    private static bool _companyHooked;

    /// <summary>Причина, по которой приём не запустился (нет свободного порта и т. п.); null — всё в порядке.</summary>
    public static string? LastError { get; private set; }

    /// <summary>Изменилось состояние (включили/выключили, порт открыт/закрыт, телефоны).</summary>
    public static event Action? StateChanged;

    /// <summary>Связь с телефонами есть только в кассе Windows/Linux (не в программе владельца и не на Android).</summary>
    public static bool Supported => !AppMode.IsOwner && !OperatingSystem.IsAndroid();

    public static PhoneLinkPairing Pairing
    {
        get
        {
            lock (Gate)
            {
                if (_pairing is null)
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppMode.DataFolderName, "data");
                    _pairing = new PhoneLinkPairing(dir, () => CompanyInfoService.LastCompany?.Id);
                    _pairing.Changed += () => StateChanged?.Invoke();
                }
                return _pairing;
            }
        }
    }

    /// <summary>Порт открыт и касса принимает запросы.</summary>
    public static bool IsListening
    {
        get
        {
            lock (Gate)
                return _server?.IsRunning == true;
        }
    }

    /// <summary>Порт, на котором слушает касса (0 — закрыт).</summary>
    public static int Port
    {
        get
        {
            lock (Gate)
                return _server?.Port ?? 0;
        }
    }

    /// <summary>Сколько сканов принято с телефонов с запуска кассы.</summary>
    public static long ScansAccepted => _scan?.AddedCount ?? 0;

    /// <summary>Включать приём и сопрягать телефоны могут владелец и администратор (право «настройки»); кассир только смотрит состояние.</summary>
    public static bool CanManage()
    {
        try
        {
            var permissions = App.AppHost?.Services.GetService<IPermissionService>();
            return permissions?.HasPermission(PosPermissions.ViewSettings) ?? false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Главное окно кассы готово: создаём сервер (один раз) и привязываем к нему это окно; если приём был включён раньше —
    /// открываем порт. 2026-10-27, ревью Ф-1: вызывается при КАЖДОМ новом главном окне (после выхода или смены кассира прежнее окно
    /// закрыто, <see cref="Detach"/> его отвязал) — делегаты заменяются на делегаты нового окна, порт открывается по настройке.</summary>
    /// <param name="owner">Окно-владелец (this): только оно может отвязаться через <see cref="Detach"/>.</param>
    /// <param name="isTillReady">Окно активно и готово к приёму (быстрая проверка до поиска товара).</param>
    /// <param name="enqueue">Поставить штрихкод в очередь сканов чека на UI-потоке и сказать, принят ли (окно активно, оплата не идёт).</param>
    public static void Attach(object owner, Func<CancellationToken, Task<bool>> isTillReady,
        Func<string, CancellationToken, Task<PhoneScanHandler.EnqueueResult>> enqueue)
    {
        if (!Supported)
            return;
        lock (Gate)
        {
            _owner = owner;
            _isTillReady = isTillReady;
            _enqueue = enqueue;
            if (_server is null)
            {
                _scan = new PhoneScanHandler(
                    ct => _isTillReady is { } ready ? ready(ct) : Task.FromResult(false),
                    LookupAsync,
                    (code, ct) => _enqueue is { } post ? post(code, ct) : Task.FromResult(PhoneScanHandler.EnqueueResult.TillInactive),
                    Pairing);
                _server = new PhoneLinkServer(Pairing, BuildHostInfo);
                _server.Register("scan", _scan.HandleAsync);
                // 2026-10-29 (скорость синхронизации): касса — центр событий магазина; телефон держит «events» и шлёт «hint» после своей правки.
                _server.Register(PhoneLinkEvents.OpEvents, PhoneLinkEvents.HandleEventsAsync);
                _server.Register(PhoneLinkEvents.OpHint, PhoneLinkEvents.HandleHintAsync);
                // Точка расширения: print / cart / uploaded подключаются так же — _server.Register("print", …).
            }
            if (!_companyHooked)
            {
                _companyHooked = true;
                CompanyInfoService.CompanyChanged += OnCompanyChanged;
            }
        }
        // Список телефонов — с диска (после выхода/входа другой компании в памяти мог остаться чужой).
        Pairing.Reload();
        if (Pairing.Enabled)
            StartListening();
        else
            StopListening();
    }

    /// <summary>2026-10-27, ревью Ф-1: окно закрывается — отвязать его делегаты и закрыть порт. Если за это время уже привязалось
    /// другое (новое) окно — ничего не трогаем.</summary>
    public static void Detach(object owner)
    {
        PhoneLinkServer? server;
        lock (Gate)
        {
            if (!ReferenceEquals(_owner, owner))
                return;
            _owner = null;
            _isTillReady = null;
            _enqueue = null;
            server = _server;
        }
        server?.Stop();
        StateChanged?.Invoke();
    }

    /// <summary>2026-10-27, ревью Ф-1: компания сменилась — ключи и список телефонов прежней компании сбрасываются и читаются заново
    /// (по новой компании); порт открывается или закрывается по настройке новой компании.</summary>
    private static void OnCompanyChanged(string? companyId)
    {
        try
        {
            if (!Supported)
                return;
            Pairing.Reload();
            bool attached;
            lock (Gate)
                attached = _owner is not null;
            if (!attached)
                return;
            if (Pairing.Enabled)
                StartListening();
            else
                StopListening();
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PHONELINK: смена компании не обработана: {ex.Message}", "WARNING");
        }
    }

    /// <summary>«Отключить телефон» из окон кассы: только владелец и администратор.</summary>
    public static bool Revoke(string phoneId) => CanManage() && Pairing.Revoke(phoneId);

    /// <summary>Включить/выключить приём. Включение открывает порт (false — не удалось, причина в <paramref name="error"/>).</summary>
    public static bool SetEnabled(bool on, out string? error)
    {
        error = null;
        if (!Supported)
        {
            error = Tr.T("Связь с телефонами есть только в кассе.", "Телефон менен байланыш кассада гана бар.", "Phone link is available only in the till.",
                "Telefon bağlantısı yalnızca kasada vardır.", "Telefon aloqasi faqat kassada mavjud.");
            return false;
        }
        if (on && !CanManage())
        {
            error = Tr.T("Включать приём с телефонов могут только владелец и администратор.",
                "Телефондон кабыл алууну ээси жана администратор гана күйгүзө алат.",
                "Only the owner and administrators can turn on phone input.",
                "Telefondan girişi yalnızca sahip ve yönetici açabilir.",
                "Telefondan qabul qilishni faqat egasi va administrator yoqa oladi.");
            return false;
        }
        if (!on && !CanManage())
        {
            error = Tr.T("Выключать приём с телефонов могут только владелец и администратор.",
                "Телефондон кабыл алууну ээси жана администратор гана өчүрө алат.",
                "Only the owner and administrators can turn off phone input.",
                "Telefondan girişi yalnızca sahip ve yönetici kapatabilir.",
                "Telefondan qabul qilishni faqat egasi va administrator o'chira oladi.");
            return false;
        }

        if (!Pairing.SetEnabled(on))
        {
            error = Tr.T("Не удалось сохранить настройку.", "Жөндөөнү сактоо мүмкүн болгон жок.", "Could not save the setting.", "Ayar kaydedilemedi.", "Sozlamani saqlab bo'lmadi.");
            return false;
        }

        if (on)
        {
            if (!StartListening())
            {
                error = LastError;
                return false;
            }
        }
        else
        {
            StopListening();
        }
        return true;
    }

    private static bool StartListening()
    {
        PhoneLinkServer? server;
        bool attached;
        lock (Gate)
        {
            server = _server;
            attached = _owner is not null;
        }
        if (server is null || !attached)
        {
            // Окно кассы ещё не прикреплено (или уже закрыто) — порт откроется при Attach.
            return true;
        }
        var ok = server.Start(out var error);
        LastError = ok ? null : Tr.T($"Не удалось открыть порт для телефонов: {error}.", $"Телефондор үчүн портту ачуу мүмкүн болгон жок: {error}.",
            $"Could not open the port for phones: {error}.", $"Telefonlar için bağlantı noktası açılamadı: {error}.", $"Telefonlar uchun portni ochib bo'lmadi: {error}.");
        StateChanged?.Invoke();
        return ok;
    }

    private static void StopListening()
    {
        PhoneLinkServer? server;
        lock (Gate)
            server = _server;
        server?.Stop();
        LastError = null;
        StateChanged?.Invoke();
    }

    /// <summary>Касса закрывается — порт закрываем и окно отвязываем (любое).</summary>
    public static void Shutdown()
    {
        PhoneLinkServer? server;
        lock (Gate)
        {
            _owner = null;
            _isTillReady = null;
            _enqueue = null;
            server = _server;
        }
        server?.Stop();
    }

    // ------------------------------------------------------------------ сведения о кассе

    private static PhoneLinkHostInfo BuildHostInfo() => new(KassaName(), CompanyHash8(), !string.IsNullOrWhiteSpace(PosApp.ActiveShiftId));

    public static string KassaName() =>
        !string.IsNullOrWhiteSpace(PosApp.PosCashboxDisplayName) ? PosApp.PosCashboxDisplayName!.Trim() : Environment.MachineName;

    public static string CompanyHash8() => PhoneLinkProtocol.CompanyHash8(CompanyInfoService.LastCompany?.Id);

    /// <summary>Новое ожидание сопряжения: свежий случайный ключ (действует 5 минут, прежний отменяется).</summary>
    public static string BeginPairingKey() => Pairing.BeginPairing();

    /// <summary>Строка QR: nurmarket-link:v1?d=…&amp;h=…&amp;p=…&amp;k=…&amp;n=…&amp;c=… . null — порт закрыт или нет адреса.</summary>
    public static string? BuildQr(string key, string ipv4)
    {
        var port = Port;
        if (port == 0 || string.IsNullOrWhiteSpace(ipv4))
            return null;
        return PhoneLinkProtocol.BuildQr(LanSyncService.DeviceId, ipv4, port, key, KassaName(), CompanyHash8());
    }

    /// <summary>IPv4-адреса кассы в локальной сети: сначала подключения со шлюзом (настоящий Wi-Fi/кабель), виртуальные адаптеры
    /// (VMware, Hyper-V, Docker, VPN) — в самом конце.</summary>
    public static IReadOnlyList<string> LocalIPv4()
    {
        var ranked = new List<(int Rank, string Ip)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                var label = (nic.Name + " " + nic.Description).ToLowerInvariant();
                var virtualAdapter = new[] { "virtual", "vmware", "vbox", "hyper-v", "vethernet", "docker", "wsl", "tap-", "tailscale", "zerotier", "vpn", "loopback" }
                    .Any(label.Contains);
                foreach (var unicast in props.UnicastAddresses)
                {
                    var ip = unicast.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip))
                        continue;
                    var bytes = ip.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254)
                        continue;
                    var isPrivate = bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
                    var rank = (hasGateway ? 0 : 4) + (virtualAdapter ? 2 : 0) + (isPrivate ? 0 : 1);
                    ranked.Add((rank, ip.ToString()));
                }
            }
        }
        catch (Exception ex)
        {
            PosLogger.Log($"PHONELINK: адреса сети не прочитаны ({ex.Message}).", "WARNING");
        }
        return ranked.OrderBy(x => x.Rank).Select(x => x.Ip).Distinct().ToList();
    }

    // ------------------------------------------------------------------ каталог

    /// <summary>Есть ли такой товар в каталоге кассы (для ответа телефону not_found до того, как код попадёт в чек).
    /// Сначала этот компьютер, потом сервер — тот же поиск, что у скана на кассе (ProductCodeLookup).</summary>
    private static async Task<PhoneScanHandler.LookupResult> LookupAsync(string code, CancellationToken ct)
    {
        var normalized = ProductCodeLookup.Normalize(code);
        if (normalized.Length == 0)
            return new PhoneScanHandler.LookupResult(PhoneScanHandler.LookupKind.NotFound);
        // QR клиента NurCRM: его разбирает сама касса (привязка клиента к чеку).
        if (ClientQrCode.TryParse(normalized, out _))
            return new PhoneScanHandler.LookupResult(PhoneScanHandler.LookupKind.Found, null, normalized);

        var result = await ProductCodeLookup.FindAsync(normalized, true, ct).ConfigureAwait(false);
        if (result.Hit is { } hit)
            return new PhoneScanHandler.LookupResult(PhoneScanHandler.LookupKind.Found, hit.Product.Title, normalized);
        return new PhoneScanHandler.LookupResult(result.Unavailable ? PhoneScanHandler.LookupKind.Unavailable : PhoneScanHandler.LookupKind.NotFound);
    }
}
