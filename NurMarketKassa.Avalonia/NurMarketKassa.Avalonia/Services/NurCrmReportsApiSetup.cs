using System.Runtime.CompilerServices;
using NurMarketKassa.Services;
using NurMarketKassa.Services.Api;

namespace NurMarketKassa.AvaloniaHost.Services;

/// <summary>2026-09-28: даёт новым адресам NurCRM (<see cref="NurCrmReportsApi"/> в
/// Infrastructure) тот же клиент API, что у всей кассы, — из DI, в момент запроса. Задаётся при
/// загрузке сборки, чтобы не править общий запуск (App.axaml.cs); пока хост не поднят или вход не
/// выполнен, клиента нет, и методы NurCrmReportsApi просто возвращают null.</summary>
internal static class NurCrmReportsApiSetup
{
#pragma warning disable CA2255 // инициализатор модуля здесь намеренный: только присваивает делегат
    [ModuleInitializer]
    internal static void Initialize() =>
        NurCrmReportsApi.ClientResolver = () => App.GetRequiredService<NurMarketApiClient>();
#pragma warning restore CA2255
}
