using NurMarketKassa.Tests.Infrastructure;

namespace NurMarketKassa.UiTests.Harness;

/// <summary>2026-11-06 (В14): один поддельный NurCRM (WireMock, FakeNurCrmServer из проекта обычных тестов — по ссылке на файл) на весь прогон окон:
/// хост служб кассы (TestHost) создаётся один раз и смотрит на его адрес. Перед каждым сценарием — <see cref="Reset"/> (чистый журнал запросов и стандартные заглушки).</summary>
public static class UiFake
{
    private static readonly Lazy<FakeNurCrmServer> Lazy = new(() => new FakeNurCrmServer());

    public static FakeNurCrmServer Server => Lazy.Value;

    public static string Url => Server.Url;

    /// <summary>Снимает все заглушки и журнал запросов, возвращает стандартные (вход, профиль, товары, оплата, возврат, смена).</summary>
    public static FakeNurCrmServer Reset()
    {
        var s = Server;
        s.Server.Reset();
        s.StubLogin();
        s.StubProfile();
        s.StubProducts();
        s.StubCheckout();
        s.StubReturn();
        s.StubShiftOpen();
        return s;
    }
}
