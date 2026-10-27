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

    /// <summary>2026-10-27 (В-10): сервер ответил 401 — вход истёк, отозван или сменён пароль. Приходит один раз; App закрывает окна и открывает вход.</summary>
    public static event Action? SessionExpired;
    private int _expiredFired;

    public void NotifyExpired()
    {
        if (Interlocked.Exchange(ref _expiredFired, 1) == 0)
            SessionExpired?.Invoke();
    }

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
        {
            NotifyExpired();
            throw new ApiException(L.T("Сеанс входа истёк — войдите снова.", "Кирүү мөөнөтү бүттү — кайра кириңиз.", "Sign-in expired — sign in again.",
                "Oturum süresi doldu — yeniden giriş yapın.", "Kirish muddati tugadi — qayta kiring."));
        }
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
        if (r.StatusCode == HttpStatusCode.Unauthorized)
            NotifyExpired();
        if (r.StatusCode == HttpStatusCode.TooManyRequests)
            throw new ApiException(L.T("Слишком много заметок — подождите минуту.", "Өтө көп эскертме — бир мүнөт күтүңүз.", "Too many notes — wait a minute.", "Çok fazla not — bir dakika bekleyin.", "Juda ko'p eslatma — bir daqiqa kuting."));
        r.EnsureSuccessStatusCode();
    }

    /// <summary>Выход: токен отзывается на сервере. Старый сервер без /api/logout (404) — не ошибка.</summary>
    public async Task LogoutAsync()
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/logout");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var r = await _http.SendAsync(req);
        }
        catch
        {
            // нет связи или старый сервер — токен и так истечёт, выходим локально
        }
    }

    /// <summary>Смена пароля (POST /api/password). Старый сервер (404/405) — понятное сообщение; успех — новый токен.</summary>
    public async Task ChangePasswordAsync(string oldPassword, string newPassword)
    {
        HttpResponseMessage r;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/password") { Content = JsonContent.Create(new { old = oldPassword, @new = newPassword }) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            r = await _http.SendAsync(req);
        }
        catch (Exception ex)
        {
            throw new ApiException(L.T("Нет связи с сервером поддержки: ", "Колдоо сервери менен байланыш жок: ", "No connection to the support server: ", "Destek sunucusuna bağlantı yok: ", "Server bilan aloqa yo'q: ") + ex.Message);
        }
        using (r)
        {
            switch (r.StatusCode)
            {
                case HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed:
                    throw new ApiException(L.T("Сервер поддержки ещё не умеет менять пароль. Попросите администратора сервера сбросить пароль командой add-operator.", "Колдоо сервери сырсөздү алмаштыра элек. Сервер администраторунан add-operator менен сырсөздү калыбына келтирүүнү сураңыз.", "The support server cannot change passwords yet. Ask the server administrator to reset it with add-operator.", "Destek sunucusu henüz şifre değiştiremiyor. Sunucu yöneticisinden add-operator ile sıfırlamasını isteyin.", "Yordam serveri hali parolni almashtira olmaydi. Server administratoridan add-operator bilan tiklashni so'rang."));
                case HttpStatusCode.Unauthorized:
                    NotifyExpired();
                    throw new ApiException(L.T("Сеанс входа истёк — войдите снова.", "Кирүү мөөнөтү бүттү — кайра кириңиз.", "Sign-in expired — sign in again.", "Oturum süresi doldu — yeniden giriş yapın.", "Kirish muddati tugadi — qayta kiring."));
                case HttpStatusCode.Forbidden:
                    throw new ApiException(L.T("Текущий пароль неверный.", "Азыркы сырсөз туура эмес.", "The current password is wrong.", "Mevcut şifre yanlış.", "Joriy parol noto'g'ri."));
                case HttpStatusCode.TooManyRequests:
                    throw new ApiException(L.T("Слишком много попыток — подождите.", "Өтө көп аракет — күтүңүз.", "Too many attempts — wait.", "Çok fazla deneme — bekleyin.", "Juda ko'p urinish — kuting."));
                case HttpStatusCode.BadRequest:
                {
                    var j = await r.Content.ReadFromJsonAsync<JsonElement>();
                    var err = j.TryGetProperty("error", out var e) ? e.GetString() : "";
                    if (err == "same-password")
                        throw new ApiException(L.T("Новый пароль совпадает со старым.", "Жаңы сырсөз эскиси менен бирдей.", "The new password is the same as the old one.", "Yeni şifre eskisiyle aynı.", "Yangi parol eskisi bilan bir xil."));
                    var min = j.TryGetProperty("min", out var m) && m.TryGetInt32(out var mv) ? mv : 10;
                    throw new ApiException(L.T($"Новый пароль слишком короткий (не меньше {min} символов).", $"Жаңы сырсөз өтө кыска (кеминде {min} белги).", $"The new password is too short (at least {min} characters).", $"Yeni şifre çok kısa (en az {min} karakter).", $"Yangi parol juda qisqa (kamida {min} belgi)."));
                }
            }
            if (!r.IsSuccessStatusCode)
                throw new ApiException(L.T("Сервер ответил ошибкой ", "Сервер ката менен жооп берди ", "Server error ", "Sunucu hatası ", "Server xatosi ") + (int)r.StatusCode);
            var ok = await r.Content.ReadFromJsonAsync<JsonElement>();
            Token = ok.GetProperty("token").GetString() ?? Token;
        }
    }
}
