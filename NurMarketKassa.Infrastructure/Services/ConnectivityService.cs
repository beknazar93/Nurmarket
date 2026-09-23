using NurMarketKassa.Core.Contracts;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.Services;

public sealed class ConnectivityService : IConnectivityService
{
    private readonly IAuthApiService _authApi;

    public ConnectivityService(IAuthApiService authApi) => _authApi = authApi;

    public Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default) =>
        _authApi.CanReachApiAsync(cancellationToken);
}
