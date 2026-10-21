using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NurRemoteSupport.Operator;

internal sealed class ApiException(string message) : Exception(message);

/// <summary>HTTP-часть ретранслятора (PROTOCOL.md: /api/login, /api/sessions, /api/notes). Токен живёт 12 ч, хранится только в памяти.</summary>
internal sealed class Api
{
    private readonly HttpClient _http;

    public Uri Base { get; }
    public string Token { get; private set; } = "";
    public string Name { get; private set; } = "";
    public string Login { get; private set; } = "";

    private Api(Uri baseUri)
    {
        Base = baseUri;
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>Адрес сервера: без схемы — https://. Без шифрования (http) — только сервер на этом же компьютере (проверка).</summary>
    public static Api For(string server)
    {
        var s = (server ?? "").Trim().TrimEnd('/');
        if (!s.Contains("://", StringComparison.Ordinal))
            s = "https://" + s;
        if (!Uri.TryCreate(s + "/", UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ApiException(L.T("Адрес сервера должен начинаться с https://", "Сервердин дареги https:// менен башталышы керек",
                "The server address must start with https://", "Sunucu adresi https:// ile başlamalı", "Server manzili https:// bilan boshlanishi kerak"));
        return new Api(uri);
    }

    // 2026-10-19 (проверка безопасности): токен — не в адресе (адреса попадают в журналы прокси), а заголовком
    // Authorization при подключении (SessionClient). В адресе он остаётся только у браузерного оператора (PROTOCOL.md).
    public Uri OperatorWs => new((Base.Scheme == "https" ? "wss" : "ws") + "://" + Base.Authority + "/ws/operator");

    public async Task LoginAsync(string login, string password)
    {
        HttpResponseMessage r;
        try
        {
            r = await _http.PostAsJsonAsync("api/login", new { login, password });
        }
        catch (Exception ex)
        {
            throw new ApiException(L.T("Нет связи с сервером поддержки: ", "Колдоо сервери менен байланыш жок: ", "No connection to the support server: ",
                "Destek sunucusuna bağlantı yok: ", "Server bilan aloqa yo'q: ") + ex.Message);
        }
        if (r.StatusCode == HttpStatusCode.Unauthorized)
            throw new ApiException(L.T("Неверный логин или пароль.", "Логин же сырсөз туура эмес.", "Wrong login or password.", "Kullanıcı adı veya şifre yanlış.", "Login yoki parol noto'g'ri."));
        if (r.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ApiException(L.T("Слишком много попыток — подождите минуту.", "Өтө көп аракет — бир мүнөт күтүңүз.", "Too many attempts — wait a minute.",
                "Çok fazla deneme — bir dakika bekleyin.", "Juda ko'p urinish — bir daqiqa kuting."));
        if (!r.IsSuccessStatusCode)
            throw new ApiException(L.T("Сервер ответил ошибкой ", "Сервер ката менен жооп берди ", "Server error ", "Sunucu hatası ", "Server xatosi ") + (int)r.StatusCode);
        var j = await r.Content.ReadFromJsonAsync<JsonElement>();
        Token = j.GetProperty("token").GetString() ?? "";
        Name = j.TryGetProperty("name", out var n) ? n.GetString() ?? login : login;
        Login = login;
    }

    private async Task<JsonElement> GetAsync(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var r = await _http.SendAsync(req);
        if (r.StatusCode == HttpStatusCode.Unauthorized)
            throw new ApiException(L.T("Сеанс входа истёк — войдите снова.", "Кирүү мөөнөтү бүттү — кайра кириңиз.", "Sign-in expired — sign in again.",
                "Oturum süresi doldu — yeniden giriş yapın.", "Kirish muddati tugadi — qayta kiring."));
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>{"active":[…],"recent":[…]} — кассы, ждущие помощи и идущие сеансы; журнал сеансов (новые сверху).</summary>
    public Task<JsonElement> SessionsAsync(int limit = 300) => GetAsync($"api/sessions?limit={limit}");

    /// <summary>Заметки операторов по кассе (компания + компьютер), без компании — последние по всем.</summary>
    public async Task<List<JsonElement>> NotesAsync(string? company, string? machine, int limit = 100)
    {
        var q = $"api/notes?limit={limit}";
        if (!string.IsNullOrWhiteSpace(company))
            q += "&company=" + Uri.EscapeDataString(company);
        if (!string.IsNullOrWhiteSpace(machine))
            q += "&machine=" + Uri.EscapeDataString(machine);
        var j = await GetAsync(q);
        return j.GetProperty("notes").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public async Task AddNoteAsync(string company, string machine, string? sessionId, string text)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/notes") { Content = JsonContent.Create(new { company, machine, sessionId, text }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        using var r = await _http.SendAsync(req);
        r.EnsureSuccessStatusCode();
    }
}
