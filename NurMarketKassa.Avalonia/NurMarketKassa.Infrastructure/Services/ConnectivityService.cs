using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

public sealed class ConnectivityService : IConnectivityService
{
    private readonly IAuthApiService _authApi;

    public ConnectivityService(IAuthApiService authApi) => _authApi = authApi;

    /// <summary>2026-09-29: в аварии сервера (ServerOutageMonitor) — «нет связи» без сетевого
    /// запроса: проверку ведёт монитор с нарастающей паузой, а опрос шапки (20 с) и каталог не
    /// должны каждый раз ждать таймаута. Неудачу обычной проверки монитор засчитывает как сбой.</summary>
    public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default)
    {
        if (ServerOutageMonitor.IsOutage)
            return false;

        var online = await _authApi.CanReachApiAsync(cancellationToken).ConfigureAwait(false);
        ServerOutageMonitor.ReportProbeResult(online, Tr.T("проверка связи", "байланышты текшерүү", "connection check", "bağlantı kontrolü", "aloqa tekshiruvi"));
        return online && !ServerOutageMonitor.IsOutage;
    }
}
