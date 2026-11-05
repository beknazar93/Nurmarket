using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace NurMarketKassa.Tests.Infrastructure;

/// <summary>2026-11-05 (аудит №4, волна 0-A): поддельный app.nurcrm.kg на localhost (WireMock.Net). Касса направляется на него через
/// <c>AppSettings.ApiBaseUrl</c> (в приложении — переменная DESKTOP_MARKET_API_URL). Настоящий сервер и настоящая база не нужны.
/// Заглушки: вход, профиль, товары, оплата (pos/checkout), возврат, смена, ошибки 403/500 и медленный ответ.
/// Формы ответов — минимальные, по коду кассы; точные образцы с NBS можно подложить через <see cref="Json"/>.</summary>
public sealed class FakeNurCrmServer : IDisposable
{
    public WireMockServer Server { get; }

    /// <summary>Адрес вида http://localhost:порт — для AppSettings.ApiBaseUrl.</summary>
    public string Url => Server.Url!;

    public FakeNurCrmServer()
    {
        Server = WireMockServer.Start();
        StubLogin();
        StubProfile();
        StubProducts();
        StubCheckout();
        StubReturn();
        StubShiftOpen();
    }

    /// <summary>Произвольная заглушка: метод, путь (можно с «*»), статус, тело JSON, задержка ответа.</summary>
    public void Json(string method, string path, int status, string body, TimeSpan? delay = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body);
        if (delay is { } d)
            response = response.WithDelay(d);
        Server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(response);
    }

    /// <summary>Любой запрос к пути отвечает ошибкой (403 — «нет прав», 500 — «внутренняя ошибка»).</summary>
    public void Error(string method, string path, int status, string detail = "error") =>
        Json(method, path, status, "{\"detail\":\"" + detail + "\"}");

    /// <summary>Любой запрос к пути отвечает медленно (проверка таймаутов и «касса не ждёт медленный сервер»).</summary>
    public void Slow(string method, string path, TimeSpan delay, string body = "{}") => Json(method, path, 200, body, delay);

    public void StubLogin() => Json("POST", "/api/users/auth/login/", 200,
        "{\"access\":\"test-access\",\"refresh\":\"test-refresh\",\"user\":{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\"}}");

    public void StubProfile() => Json("GET", "/api/users/profile/", 200,
        "{\"id\":\"u1\",\"email\":\"cashier@test.kg\",\"full_name\":\"Тест Кассир\",\"role\":\"owner\",\"company\":{\"id\":\"fake-company-1\",\"name\":\"Тест\"}}");

    public void StubProducts() => Json("GET", "/api/main/products/list/", 200,
        "{\"count\":2,\"next\":null,\"previous\":null,\"results\":[" +
        "{\"id\":\"p1\",\"name\":\"Хлеб\",\"barcode\":\"4600000000001\",\"price\":\"45.00\",\"quantity\":\"10\",\"is_weight\":false}," +
        "{\"id\":\"p2\",\"name\":\"Яблоки\",\"barcode\":\"4600000000002\",\"price\":\"120.00\",\"quantity\":\"5.5\",\"is_weight\":true}]}");

    public void StubCheckout() => Json("POST", "/api/main/pos/checkout/", 200,
        "{\"id\":\"sale-1\",\"number\":1001,\"status\":\"paid\",\"total\":\"45.00\"}");

    public void StubReturn() => Json("POST", "/api/main/pos/sales/*/return/", 200,
        "{\"id\":\"ret-1\",\"sale\":\"sale-1\",\"amount\":\"45.00\"}");

    public void StubShiftOpen() => Json("POST", "/api/construction/shifts/*", 200,
        "{\"id\":\"shift-1\",\"status\":\"open\",\"opening_cash\":\"0.00\"}");

    public void Dispose() => Server.Stop();
}
